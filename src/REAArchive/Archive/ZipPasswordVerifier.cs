// ZipPasswordVerifier.cs — 进程内 ZIP 密码校验（纯 BCL，零子进程、零 NuGet）
//
// 为什么要有它：7z.exe 每个候选一个进程 ≈ 50ms（20 次/秒），而纯内存计算是微秒级。
// 对 zip 来说完全没必要为每个候选付进程启动成本。
//
// 两级判定（先便宜后精确）：
//   1) 快速筛：
//      · ZipCrypto  ：解 12 字节加密头，比对最后一个字节（CRC32 高字节 / 有数据描述符时用修改时间高字节）→ 1/256 假阳性
//      · WinZip AES ：PBKDF2-HMAC-SHA1(1000 轮) 派生 2*keyLen+2 字节，比对 2 字节 password verifier → 1/65536 假阳性
//   2) 精确复核（只在快速筛命中时做，代价可以忽略）：
//      · ZipCrypto  ：用同一组密钥把整个条目解密，按压缩方法解压后比对 CRC32 与原始大小
//      · WinZip AES ：用 authKey 对密文算 HMAC-SHA1，比对末尾 10 字节认证码
// 所以本校验器不会报假阳性；`MatchUnverified` 只出现在「压缩方法不受支持（如 Deflate64/LZMA）」时，
// 那种情况由编排层再用 `7z t` 定论。
//
// 密码字节编码：ZIP 传统加密的密码是**裸字节**，没有编码声明（同一串字符可能被写成 UTF-8 / GB18030 / Latin-1）。
// 这里对纯 ASCII 候选只算一套（快路径），非 ASCII 依次试三套，任一命中即算命中。
//
// 只读：以 FileShare.ReadWrite 打开、只 seek 读，从不写入，用户同时解压也不会冲突。

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace REAArchive.Archive;

internal enum ZipProbeStatus
{
    /// <summary>可用：已定位到加密条目，能进程内校验。</summary>
    Ok = 0,
    /// <summary>不是 ZIP（结构读不出来）。</summary>
    NotZip,
    /// <summary>是 ZIP，但没有加密条目。</summary>
    NotEncrypted,
    /// <summary>是加密 ZIP，但加密方式本校验器不支持（如 PKWARE 强加密）。</summary>
    Unsupported,
}

internal enum ZipEncryptionKind
{
    None = 0,
    ZipCrypto,
    Aes,
}

internal enum ZipVerifyOutcome
{
    /// <summary>快速筛就没过：不是这个密码。</summary>
    NoMatch = 0,
    /// <summary>快速筛过了，且精确复核也通过：确定是这个密码。</summary>
    Match,
    /// <summary>快速筛过了，但压缩方法不支持精确复核（需要外部 7z 定论）。</summary>
    MatchUnverified,
}

internal sealed class ZipPasswordVerifier : IDisposable
{
    private const uint SigLocalHeader = 0x04034B50;
    private const uint SigCentralHeader = 0x02014B50;
    private const uint SigEocd = 0x06054B50;
    private const uint SigZip64Eocd = 0x06064B50;
    private const uint SigZip64Locator = 0x07064B50;

    private const ushort FlagEncrypted = 0x0001;
    private const ushort FlagDataDescriptor = 0x0008;
    private const ushort FlagStrongEncryption = 0x0040;

    private const ushort MethodStored = 0;
    private const ushort MethodDeflate = 8;
    private const ushort MethodAes = 99;

    private const ushort ExtraZip64 = 0x0001;
    private const ushort ExtraAes = 0x9901;

    private const int LocalHeaderLength = 30;
    private const int CentralHeaderLength = 46;
    private const int ZipCryptoHeaderLength = 12;
    private const int AesIterations = 1000;
    private const int AesAuthCodeLength = 10;

    /// <summary>扫描中央目录的条目上限（防止畸形归档让破译准备阶段卡死）。</summary>
    private const int MaxEntriesToScan = 500_000;

    private const long MaxExtraFieldLength = 0xFFFF;

    private static readonly uint[] CrcTable = BuildCrcTable();

    private readonly FileStream _stream;
    private readonly ZipEncryptionKind _kind;
    private readonly long _dataOffset;
    private readonly ushort _method;
    private readonly uint _crc;
    private readonly long _compressedSize;
    private readonly long _uncompressedSize;
    private readonly byte _zipCryptoCheckByte;
    private readonly ushort _flags;
    private readonly byte[]? _aesSalt;
    private readonly byte[]? _aesVerifier;
    private readonly int _aesKeyLen;
    private readonly byte[] _cryptoHeader = new byte[ZipCryptoHeaderLength];

    private ZipPasswordVerifier(FileStream stream, in EntryPlan plan)
    {
        _stream = stream;
        _kind = plan.Kind;
        _dataOffset = plan.DataOffset;
        _method = plan.Method;
        _crc = plan.Crc;
        _compressedSize = plan.CompressedSize;
        _uncompressedSize = plan.UncompressedSize;
        _zipCryptoCheckByte = plan.CheckByte;
        _flags = plan.Flags;
        _aesSalt = plan.AesSalt;
        _aesVerifier = plan.AesVerifier;
        _aesKeyLen = plan.AesKeyLen;
    }

    public ZipEncryptionKind Kind => _kind;

    // ── 打开 / 解析 ───────────────────────────────────────────────────────────

    /// <summary>尝试打开并定位到第一个加密文件条目。任何异常都收敛成状态码，不抛给调用方。</summary>
    public static ZipProbeStatus TryOpen(string archivePath, out ZipPasswordVerifier? verifier, out string detail)
    {
        verifier = null;
        detail = "";

        if (string.IsNullOrWhiteSpace(archivePath))
        {
            detail = "未指定归档路径。";
            return ZipProbeStatus.NotZip;
        }

        FileStream? stream = null;
        try
        {
            stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                16 * 1024, FileOptions.RandomAccess);

            if (stream.Length < 22)
            {
                detail = "文件不足 22 字节，不是 ZIP。";
                return ZipProbeStatus.NotZip;
            }

            if (!TryReadEocd(stream, out long cdOffset, out long cdSize, out long entryCount, out detail))
                return ZipProbeStatus.NotZip;

            if (!TryFindEncryptedEntry(stream, cdOffset, cdSize, entryCount,
                    out CentralEntry entry, out bool sawAnyEntry, out detail))
            {
                detail = sawAnyEntry ? "归档里没有加密条目。" : detail;
                return sawAnyEntry ? ZipProbeStatus.NotEncrypted : ZipProbeStatus.NotZip;
            }

            if (!TryPlanEntry(stream, entry, out EntryPlan plan, out detail))
                return ZipProbeStatus.Unsupported;

            verifier = new ZipPasswordVerifier(stream, plan);
            stream = null; // 所有权转移给 verifier
            return ZipProbeStatus.Ok;
        }
        catch (IOException ex)
        {
            detail = "读取归档失败：" + ex.Message;
        }
        catch (UnauthorizedAccessException ex)
        {
            detail = "无权读取归档：" + ex.Message;
        }
        catch (NotSupportedException ex)
        {
            detail = "ZIP 结构不受支持：" + ex.Message;
        }
        catch (ArgumentException ex)
        {
            detail = "归档路径无效：" + ex.Message;
        }
        finally
        {
            stream?.Dispose();
        }

        return ZipProbeStatus.NotZip;
    }

    /// <summary>只问「有没有可用的进程内校验器」，不保留状态。</summary>
    public static bool CanVerify(string archivePath)
    {
        ZipProbeStatus status = TryOpen(archivePath, out ZipPasswordVerifier? probe, out _);
        probe?.Dispose();
        return status == ZipProbeStatus.Ok;
    }

    // ── 校验 ──────────────────────────────────────────────────────────────────

    /// <summary>校验一个候选密码（密码字节编码策略见文件头注释）。</summary>
    public ZipVerifyOutcome Verify(string candidate)
    {
        if (candidate is null) return ZipVerifyOutcome.NoMatch;

        byte[] utf8 = Encoding.UTF8.GetBytes(candidate);
        ZipVerifyOutcome outcome = VerifyBytes(utf8);
        if (outcome != ZipVerifyOutcome.NoMatch) return outcome;

        // 纯 ASCII：UTF-8 / GB18030 / Latin-1 三套字节完全相同，没必要再算。
        if (IsAscii(candidate)) return ZipVerifyOutcome.NoMatch;

        try
        {
            byte[]? gb = LegacyText.TryEncodeGb18030(candidate);
            if (gb is not null && !ByteEquals(gb, utf8))
            {
                outcome = VerifyBytes(gb);
                if (outcome != ZipVerifyOutcome.NoMatch) return outcome;
            }

            byte[] ansi = Encoding.Latin1.GetBytes(candidate);
            if (!ByteEquals(ansi, utf8))
            {
                outcome = VerifyBytes(ansi);
                if (outcome != ZipVerifyOutcome.NoMatch) return outcome;
            }
        }
        catch (ArgumentException)
        {
            // 编码失败只影响这一套尝试；上面的 UTF-8 已经试过了。
        }

        return ZipVerifyOutcome.NoMatch;
    }

    private ZipVerifyOutcome VerifyBytes(byte[] passwordBytes)
        => _kind == ZipEncryptionKind.Aes ? VerifyAes(passwordBytes) : VerifyZipCrypto(passwordBytes);

    // ── ZipCrypto（传统加密） ─────────────────────────────────────────────────

    private ZipVerifyOutcome VerifyZipCrypto(byte[] passwordBytes)
    {
        uint k0 = 0x12345678, k1 = 0x23456789, k2 = 0x34567890;
        foreach (byte b in passwordBytes) UpdateKeys(ref k0, ref k1, ref k2, b);

        if (!TryReadAt(_stream, _dataOffset, _cryptoHeader, ZipCryptoHeaderLength))
            return ZipVerifyOutcome.NoMatch;

        byte lastPlain = 0;
        for (int i = 0; i < ZipCryptoHeaderLength; i++)
        {
            lastPlain = (byte)(_cryptoHeader[i] ^ DecryptByte(k2));
            UpdateKeys(ref k0, ref k1, ref k2, lastPlain);
        }

        if (lastPlain != _zipCryptoCheckByte) return ZipVerifyOutcome.NoMatch;

        switch (ConfirmZipCrypto(k0, k1, k2))
        {
            case ExactOutcome.Confirmed:
                return ZipVerifyOutcome.Match;

            case ExactOutcome.Unverifiable:
                return ZipVerifyOutcome.MatchUnverified;

            default:
                return ZipVerifyOutcome.NoMatch; // 1/256 假阳性，继续试下一个
        }
    }

    /// <summary>
    /// 精确复核：用命中的密钥把条目正文解出来，按压缩方法还原后比对 CRC32 与原始大小。
    /// 流式处理（64KB 缓冲），不会把大条目读进内存。
    /// </summary>
    private ExactOutcome ConfirmZipCrypto(uint k0, uint k1, uint k2)
    {
        if (_compressedSize <= ZipCryptoHeaderLength) return ExactOutcome.Unverifiable;
        if (_method != MethodStored && _method != MethodDeflate) return ExactOutcome.Unverifiable;

        // 置了数据描述符（bit 3）的条目：压缩流末尾可能跟着加密后的描述符，长度不好切，
        // 而且 CRC 有可能落在描述符里。这种情况不做进程内精确复核，直接让 7z 定论，避免假阴性。
        if ((_flags & FlagDataDescriptor) != 0) return ExactOutcome.Unverifiable;

        long payloadLength = _compressedSize - ZipCryptoHeaderLength;

        try
        {
            using var payload = new ZipCryptoDecryptStream(
                _stream, _dataOffset + ZipCryptoHeaderLength, payloadLength, k0, k1, k2);

            Stream content = _method == MethodDeflate
                ? new DeflateStream(payload, CompressionMode.Decompress)
                : payload;

            try
            {
                uint crc = 0xFFFFFFFF;
                long total = 0;
                var buffer = new byte[64 * 1024];

                int n;
                while ((n = content.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += n;
                    for (int i = 0; i < n; i++)
                    {
                        crc = (crc >> 8) ^ CrcTable[(crc ^ buffer[i]) & 0xFF];
                    }
                }

                crc ^= 0xFFFFFFFF;

                if (_uncompressedSize >= 0 && total != _uncompressedSize) return ExactOutcome.Rejected;
                return crc == _crc ? ExactOutcome.Confirmed : ExactOutcome.Rejected;
            }
            finally
            {
                if (!ReferenceEquals(content, payload)) content.Dispose();
            }
        }
        catch (InvalidDataException)
        {
            return ExactOutcome.Rejected;   // 解压流损坏 = 密码不对
        }
        catch (IOException)
        {
            return ExactOutcome.Unverifiable;
        }
    }

    // ── WinZip AES ────────────────────────────────────────────────────────────

    private ZipVerifyOutcome VerifyAes(byte[] passwordBytes)
    {
        int keyLen = _aesKeyLen;
        if (keyLen <= 0 || _aesSalt is null || _aesVerifier is null) return ZipVerifyOutcome.NoMatch;

        byte[] derived = Rfc2898DeriveBytes.Pbkdf2(
            passwordBytes, _aesSalt, AesIterations, HashAlgorithmName.SHA1, keyLen * 2 + 2);

        if (derived[keyLen * 2] != _aesVerifier[0] || derived[keyLen * 2 + 1] != _aesVerifier[1])
            return ZipVerifyOutcome.NoMatch;

        return ConfirmAes(derived, keyLen) ? ZipVerifyOutcome.Match : ZipVerifyOutcome.NoMatch;
    }

    /// <summary>精确复核：WinZip AES 的 HMAC-SHA1 认证码（末尾 10 字节）。</summary>
    private bool ConfirmAes(byte[] derived, int keyLen)
    {
        int saltLen = _aesSalt!.Length;
        long cipherLength = _compressedSize - (saltLen + 2);
        if (cipherLength < AesAuthCodeLength) return false;

        long macLength = cipherLength - AesAuthCodeLength;
        long macOffset = _dataOffset + saltLen + 2 + macLength;

        var expected = new byte[AesAuthCodeLength];
        if (!TryReadAt(_stream, macOffset, expected, AesAuthCodeLength)) return false;

        var authKey = new byte[keyLen];
        Array.Copy(derived, keyLen, authKey, 0, keyLen);

        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, authKey);
        using var cipher = new BoundedReadStream(_stream, _dataOffset + saltLen + 2, macLength);
        var buffer = new byte[64 * 1024];
        int n;
        while ((n = cipher.Read(buffer, 0, buffer.Length)) > 0) hmac.AppendData(buffer, 0, n);

        byte[] mac = hmac.GetHashAndReset();
        return mac.Length >= AesAuthCodeLength && mac.AsSpan(0, AesAuthCodeLength).SequenceEqual(expected);
    }

    // ── 密码字节 / 密钥算法 ───────────────────────────────────────────────────

    private static bool IsAscii(string value)
    {
        foreach (char c in value)
        {
            if (c >= 0x80) return false;
        }
        return true;
    }

    private static bool ByteEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    private static byte DecryptByte(uint k2)
    {
        uint temp = (k2 & 0xFFFF) | 2;
        return (byte)((temp * (temp ^ 1)) >> 8);
    }

    private static void UpdateKeys(ref uint k0, ref uint k1, ref uint k2, byte plain)
    {
        k0 = (k0 >> 8) ^ CrcTable[(k0 ^ plain) & 0xFF];
        k1 = (k1 + (k0 & 0xFF)) * 134775813u + 1u;
        k2 = (k2 >> 8) ^ CrcTable[(k2 ^ (byte)(k1 >> 24)) & 0xFF];
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? (0xEDB88320u ^ (value >> 1)) : (value >> 1);
            }
            table[i] = value;
        }
        return table;
    }

    // ── 结构解析 ──────────────────────────────────────────────────────────────

    private enum ExactOutcome { Rejected = 0, Confirmed, Unverifiable }

    private readonly record struct CentralEntry(
        ushort Flags, ushort Method, ushort ModTime, uint Crc,
        long CompressedSize, long UncompressedSize, long LocalOffset,
        bool HasAesExtra, byte AesStrength);

    private readonly record struct EntryPlan(
        ZipEncryptionKind Kind, long DataOffset, ushort Method, uint Crc,
        long CompressedSize, long UncompressedSize, byte CheckByte,
        byte[]? AesSalt, byte[]? AesVerifier, int AesKeyLen, ushort Flags);

    /// <summary>定位 EOCD（含 ZIP64 locator/record）。</summary>
    private static bool TryReadEocd(FileStream fs, out long cdOffset, out long cdSize, out long entryCount, out string detail)
    {
        cdOffset = 0;
        cdSize = 0;
        entryCount = 0;
        detail = "";

        long length = fs.Length;
        int tailLength = (int)Math.Min(length, 22 + 65_535);
        var tail = new byte[tailLength];
        if (!TryReadAt(fs, length - tailLength, tail, tailLength))
        {
            detail = "读取文件尾部失败。";
            return false;
        }

        int eocd = -1;
        for (int i = tailLength - 22; i >= 0; i--)
        {
            if (tail[i] == 0x50 && tail[i + 1] == 0x4B && tail[i + 2] == 0x05 && tail[i + 3] == 0x06)
            {
                eocd = i;
                break;
            }
        }

        if (eocd < 0)
        {
            detail = "找不到 ZIP 结尾记录（EOCD）。";
            return false;
        }

        long eocdAbsolute = length - tailLength + eocd;
        ushort commentLength = ReadU16(tail, eocd + 20);
        if (eocdAbsolute + 22 + commentLength > length)
        {
            detail = "EOCD 越出文件末尾。";
            return false;
        }

        ulong entries = ReadU16(tail, eocd + 10);
        ulong size = ReadU32(tail, eocd + 12);
        ulong offset = ReadU32(tail, eocd + 16);

        if (entries == 0xFFFF || size == 0xFFFFFFFF || offset == 0xFFFFFFFF)
        {
            // ZIP64：EOCD 前面 20 字节是 locator，再由它指向 ZIP64 EOCD record。
            if (eocdAbsolute >= 20)
            {
                var locator = new byte[20];
                if (TryReadAt(fs, eocdAbsolute - 20, locator, 20) && ReadU32(locator, 0) == SigZip64Locator)
                {
                    ulong zip64Offset = ReadU64(locator, 8);
                    var zip64 = new byte[56];
                    if (zip64Offset + 56 <= (ulong)length
                        && TryReadAt(fs, (long)zip64Offset, zip64, 56)
                        && ReadU32(zip64, 0) == SigZip64Eocd)
                    {
                        entries = ReadU64(zip64, 32);
                        size = ReadU64(zip64, 40);
                        offset = ReadU64(zip64, 48);
                    }
                }
            }
        }

        if (offset > (ulong)length || size > (ulong)length || offset + size > (ulong)length)
        {
            detail = "中央目录偏移越界。";
            return false;
        }

        cdOffset = (long)offset;
        cdSize = (long)size;
        entryCount = entries > (ulong)int.MaxValue ? int.MaxValue : (long)entries;
        return true;
    }

    /// <summary>顺序扫中央目录，找第一个「加密的文件条目」（目录条目跳过）。</summary>
    private static bool TryFindEncryptedEntry(
        FileStream fs, long cdOffset, long cdSize, long entryCount,
        out CentralEntry entry, out bool sawAnyEntry, out string detail)
    {
        entry = default;
        sawAnyEntry = false;
        detail = "";

        long end = cdOffset + cdSize;
        long position = cdOffset;
        var header = new byte[CentralHeaderLength];

        for (long i = 0; i < Math.Min(entryCount, MaxEntriesToScan) && position + CentralHeaderLength <= end; i++)
        {
            if (!TryReadAt(fs, position, header, CentralHeaderLength)) break;
            if (ReadU32(header, 0) != SigCentralHeader) break;

            sawAnyEntry = true;

            ushort flags = ReadU16(header, 8);
            ushort method = ReadU16(header, 10);
            ushort modTime = ReadU16(header, 12);
            uint crc = ReadU32(header, 16);
            ulong compressedSize = ReadU32(header, 20);
            ulong uncompressedSize = ReadU32(header, 24);
            ushort nameLength = ReadU16(header, 28);
            ushort extraLength = ReadU16(header, 30);
            ushort commentLength = ReadU16(header, 32);
            ulong localOffset = ReadU32(header, 42);

            long variableLength = (long)nameLength + extraLength + commentLength;
            byte[]? variable = null;
            if (variableLength > 0)
            {
                if (variableLength > 1 << 20) break; // 畸形条目：放弃
                variable = new byte[variableLength];
                if (!TryReadAt(fs, position + CentralHeaderLength, variable, (int)variableLength)) break;
            }

            bool hasAesExtra = false;
            byte aesStrength = 0;

            if (extraLength > 0 && variable is not null)
            {
                ReadOnlySpan<byte> extra = variable.AsSpan(nameLength, extraLength);

                if (compressedSize == 0xFFFFFFFF || uncompressedSize == 0xFFFFFFFF || localOffset == 0xFFFFFFFF)
                {
                    ApplyZip64Extra(extra, ref uncompressedSize, ref compressedSize, ref localOffset);
                }

                hasAesExtra = TryReadAesExtra(extra, out aesStrength, out _);
            }

            // 下一个条目的位置
            position += CentralHeaderLength + variableLength;

            if ((flags & FlagEncrypted) == 0) continue;             // 没加密
            if (nameLength > 0 && variable is not null && variable[nameLength - 1] == (byte)'/') continue; // 目录

            if (localOffset > (ulong)fs.Length) continue;

            entry = new CentralEntry(
                flags, method, modTime, crc,
                compressedSize > long.MaxValue ? -1 : (long)compressedSize,
                uncompressedSize > long.MaxValue ? -1 : (long)uncompressedSize,
                (long)localOffset, hasAesExtra, aesStrength);
            return true;
        }

        detail = "中央目录里没有可用的加密条目。";
        return false;
    }

    private static void ApplyZip64Extra(
        ReadOnlySpan<byte> extra, ref ulong uncompressedSize, ref ulong compressedSize, ref ulong localOffset)
    {
        int i = 0;
        while (i + 4 <= extra.Length)
        {
            ushort id = (ushort)(extra[i] | (extra[i + 1] << 8));
            ushort length = (ushort)(extra[i + 2] | (extra[i + 3] << 8));
            int dataStart = i + 4;
            if (dataStart + length > extra.Length) return;

            if (id == ExtraZip64)
            {
                int p = dataStart;
                int stop = dataStart + length;
                if (uncompressedSize == 0xFFFFFFFF && p + 8 <= stop) { uncompressedSize = ReadU64(extra, p); p += 8; }
                if (compressedSize == 0xFFFFFFFF && p + 8 <= stop) { compressedSize = ReadU64(extra, p); p += 8; }
                if (localOffset == 0xFFFFFFFF && p + 8 <= stop) { localOffset = ReadU64(extra, p); }
                return;
            }

            i = dataStart + length;
        }
    }

    /// <summary>读 0x9901（WinZip AES）扩展：version u16 / "AE" / strength u8 / 真实压缩方法 u16。</summary>
    private static bool TryReadAesExtra(ReadOnlySpan<byte> extra, out byte strength, out ushort actualMethod)
    {
        strength = 0;
        actualMethod = 0;

        int i = 0;
        while (i + 4 <= extra.Length)
        {
            ushort id = (ushort)(extra[i] | (extra[i + 1] << 8));
            ushort length = (ushort)(extra[i + 2] | (extra[i + 3] << 8));
            int dataStart = i + 4;
            if (dataStart + length > extra.Length) return false;

            if (id == ExtraAes)
            {
                if (length < 7) return false;
                strength = extra[dataStart + 4];
                actualMethod = (ushort)(extra[dataStart + 5] | (extra[dataStart + 6] << 8));
                return true;
            }

            i = dataStart + length;
        }

        return false;
    }

    /// <summary>读本地头，得出「加密数据从哪里开始」以及 AES 参数 / ZipCrypto 检查字节。</summary>
    private static bool TryPlanEntry(FileStream fs, in CentralEntry entry, out EntryPlan plan, out string detail)
    {
        plan = default;
        detail = "";

        var localHeader = new byte[LocalHeaderLength];
        if (!TryReadAt(fs, entry.LocalOffset, localHeader, LocalHeaderLength))
        {
            detail = "本地头读取失败。";
            return false;
        }
        if (ReadU32(localHeader, 0) != SigLocalHeader)
        {
            detail = "本地头签名不匹配。";
            return false;
        }

        ushort nameLength = ReadU16(localHeader, 26);
        ushort extraLength = ReadU16(localHeader, 28);
        long dataOffset = entry.LocalOffset + LocalHeaderLength + nameLength + extraLength;
        if (dataOffset > fs.Length)
        {
            detail = "加密数据偏移越界。";
            return false;
        }

        bool hasLocalAes = false;
        byte localStrength = 0;
        if (extraLength > 0 && extraLength <= MaxExtraFieldLength)
        {
            var extra = new byte[extraLength];
            if (TryReadAt(fs, entry.LocalOffset + LocalHeaderLength + nameLength, extra, extraLength)
                && TryReadAesExtra(extra, out byte strength, out _))
            {
                hasLocalAes = true;
                localStrength = strength;
            }
        }

        bool hasAes = entry.HasAesExtra || hasLocalAes;
        if (entry.Method == MethodAes || hasAes)
        {
            if (!hasAes)
            {
                detail = "AES 条目缺少 0x9901 扩展，加密参数不明。";
                return false;
            }

            byte strength = entry.HasAesExtra ? entry.AesStrength : localStrength;
            int keyLen = strength switch { 1 => 16, 2 => 24, 3 => 32, _ => 0 };
            if (keyLen == 0)
            {
                detail = "AES 强度字段非法。";
                return false;
            }

            int saltLength = keyLen / 2; // 128/192/256 位 → 8/12/16 字节 salt
            var head = new byte[saltLength + 2];
            if (!TryReadAt(fs, dataOffset, head, head.Length))
            {
                detail = "AES 头（salt + verifier）读取失败。";
                return false;
            }

            plan = new EntryPlan(
                ZipEncryptionKind.Aes, dataOffset, entry.Method, entry.Crc,
                entry.CompressedSize, entry.UncompressedSize, 0,
                head.AsSpan(0, saltLength).ToArray(), head.AsSpan(saltLength, 2).ToArray(), keyLen, entry.Flags);
            return true;
        }

        if ((entry.Flags & FlagStrongEncryption) != 0)
        {
            detail = "ZIP 强加密（PKWARE SES）本校验器不支持。";
            return false;
        }

        // 传统加密：检查字节来自 CRC32 高字节；写了数据描述符（bit 3）时 CRC 位置不可靠，改用修改时间高字节。
        byte checkByte = (entry.Flags & FlagDataDescriptor) != 0
            ? (byte)(entry.ModTime >> 8)
            : (byte)(entry.Crc >> 24);

        plan = new EntryPlan(
            ZipEncryptionKind.ZipCrypto, dataOffset, entry.Method, entry.Crc,
            entry.CompressedSize, entry.UncompressedSize, checkByte, null, null, 0, entry.Flags);
        return true;
    }

    // ── 小工具 ────────────────────────────────────────────────────────────────

    private static bool TryReadAt(FileStream fs, long offset, byte[] buffer, int count)
    {
        if (offset < 0 || count < 0 || offset + count > fs.Length) return false;

        fs.Position = offset;
        int read = 0;
        while (read < count)
        {
            int n = fs.Read(buffer, read, count - read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data, int index)
        => (ushort)(data[index] | (data[index + 1] << 8));

    private static uint ReadU32(ReadOnlySpan<byte> data, int index)
        => (uint)(data[index] | (data[index + 1] << 8) | (data[index + 2] << 16) | (data[index + 3] << 24));

    private static ulong ReadU64(ReadOnlySpan<byte> data, int index)
        => ReadU32(data, index) | ((ulong)ReadU32(data, index + 4) << 32);

    public void Dispose() => _stream.Dispose();

    /// <summary>只读、有界、流式的「解 ZipCrypto」流（每次 Read 就地解密）。</summary>
    private sealed class ZipCryptoDecryptStream : Stream
    {
        private readonly FileStream _inner;
        private readonly long _length;
        private long _position;
        private uint _k0;
        private uint _k1;
        private uint _k2;

        public ZipCryptoDecryptStream(FileStream inner, long start, long length, uint k0, uint k1, uint k2)
        {
            _inner = inner;
            _length = length;
            _k0 = k0;
            _k1 = k1;
            _k2 = k2;
            _inner.Position = start;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            long remaining = _length - _position;
            if (remaining <= 0) return 0;

            int want = (int)Math.Min(count, remaining);
            int n = _inner.Read(buffer, offset, want);
            if (n <= 0) return 0;

            for (int i = 0; i < n; i++)
            {
                int at = offset + i;
                byte plain = (byte)(buffer[at] ^ DecryptByte(_k2));
                UpdateKeys(ref _k0, ref _k1, ref _k2, plain);
                buffer[at] = plain;
            }

            _position += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>只读、有界、流式的「只读一段」包装（AES 认证码计算用）。不关闭底层流。</summary>
    private sealed class BoundedReadStream : Stream
    {
        private readonly FileStream _inner;
        private readonly long _length;
        private long _position;

        public BoundedReadStream(FileStream inner, long start, long length)
        {
            _inner = inner;
            _length = length;
            _inner.Position = start;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            long remaining = _length - _position;
            if (remaining <= 0) return 0;

            int want = (int)Math.Min(count, remaining);
            int n = _inner.Read(buffer, offset, want);
            if (n <= 0) return 0;

            _position += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
