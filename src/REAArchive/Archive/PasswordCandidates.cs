// PasswordCandidates.cs — 候选密码生成（全部惰性 IEnumerable，内存恒定）
//
// 三种策略：
//   Dictionary  ：流式逐行读词表 → 按 WordVariant 惰性展开变体（不预生成数组）
//   Mask        ：?l ?u ?d ?s ?a ?1..?4 ?b，先解析成「每位一个字符集」，再按 odometer 进位
//   Incremental ：长度从 MinLength 到 MaxLength，每位 odometer 进位，**第一位变化最快**
//
// 硬性约束：
//  1) 绝不预生成候选数组：增量到 8 位纯数字就是 10^8 个候选，只能边算边试。
//  2) 词表流式读取（等价 File.ReadLines），UTF-8 优先；遇到非法字节序列再退 GB18030 / Latin-1。
//  3) KnownPrefix 统一在这里拼在最前面，三种策略都生效。
//
// 编码说明：零 NuGet 前提下 BCL 里没有 GB18030 编码器（System.Text.Encoding.CodePages 不在共享框架中），
// 所以 GB18030 走 Windows 的 MultiByteToWideChar / WideCharToMultiByte（见文件末尾 LegacyText）。
// 这在 net9.0-windows 上是系统自带能力，不引入任何依赖、不联网。

using System.Runtime.InteropServices;
using System.Text;

namespace REAArchive.Archive;

internal static class PasswordCandidates
{
    public const string Lowercase = "abcdefghijklmnopqrstuvwxyz";
    public const string Uppercase = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    public const string Digits = "0123456789";

    /// <summary>?s：可打印符号（与 hashcat 的 ?s 一致，含空格）。</summary>
    public const string Symbols = " !\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";

    private const int FirstYear = 1990;
    private const int LastYear = 2030;

    /// <summary>单行词表上限：超过这个长度的一行按“异常数据”处理，避免被二进制文件撑爆内存。</summary>
    private const int MaxLineBytes = 1024 * 1024;

    /// <summary>变体展开顺序固定，保证同一份词表每次跑出来的候选序列完全一致（可复现）。</summary>
    private static readonly WordVariant[] VariantOrder =
    {
        WordVariant.Capitalize,
        WordVariant.Upper,
        WordVariant.AppendDigits,
        WordVariant.AppendYear,
        WordVariant.PrependDigit,
    };

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // ── 对外入口 ──────────────────────────────────────────────────────────────

    /// <summary>按 options.Strategy 惰性产出候选；KnownPrefix 已拼好。</summary>
    public static IEnumerable<string> Generate(RecoveryOptions options)
    {
        Validate(options);

        string prefix = options.KnownPrefix ?? "";
        foreach (string candidate in GenerateCore(options))
        {
            yield return prefix.Length == 0 ? candidate : prefix + candidate;
        }
    }

    /// <summary>
    /// 估算候选总数（**预估语义：不校验"能不能跑"，只回答"大概多少个候选"**）。
    ///
    /// UI 每次击键都会调用，所以这里不能像 <see cref="Validate"/> 那样抛配置错误：
    ///   · 字典        ：词表行数未知（流式读取），无论是否已添加词表都返回 -1；
    ///   · 掩码        ：空掩码 → 0；非法占位符仍抛 ArchiveException（那是用户在打的字，必须即时纠正）；
    ///   · 增量        ：字符集为空 / 最大长度小于最小长度 → 0（UI 显示"当前设置有误"）；
    ///   · 数字溢出    ：返回 -1（未知）。
    /// 纯计算：不读词表、不碰归档、不看归档是否存在。
    /// </summary>
    public static long Estimate(RecoveryOptions options)
    {
        switch (options.Strategy)
        {
            case RecoveryStrategy.Mask:
            {
                if (string.IsNullOrWhiteSpace(options.Mask)) return 0;

                List<char[]> sets = ParseMask(options.Mask!, options.CustomSets);
                long total = 1;
                foreach (char[] set in sets)
                {
                    if (set.Length == 0) return 0;
                    if (total > long.MaxValue / set.Length) return -1;
                    total *= set.Length;
                }
                return total;
            }

            case RecoveryStrategy.Incremental:
            {
                int radix = options.Charset?.Length ?? 0;
                if (radix == 0) return 0;
                if (options.MaxLength < options.MinLength) return 0;

                long total = 0;
                long power = 1;
                for (int len = 1; len <= options.MaxLength; len++)
                {
                    if (power > long.MaxValue / radix) return -1;
                    power *= radix;
                    if (len < options.MinLength) continue;
                    if (total > long.MaxValue - power) return -1;
                    total += power;
                }
                return total;
            }

            default:
                return -1; // 字典（未知）以及未知策略
        }
    }

    /// <summary>参数校验；失败抛中文 ArchiveException（UI 会把 Message 直接显示出来）。</summary>
    public static void Validate(RecoveryOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ArchivePath))
            throw new ArchiveException("未指定要破译的归档文件。");

        switch (options.Strategy)
        {
            case RecoveryStrategy.Dictionary:
                if (options.WordlistPaths is null || options.WordlistPaths.Count == 0)
                    throw new ArchiveException("字典策略需要至少一个词表文件（每行一个候选密码）。");
                break;

            case RecoveryStrategy.Mask:
                if (string.IsNullOrWhiteSpace(options.Mask))
                    throw new ArchiveException("掩码策略需要填写掩码，例如 ?d?d?d?d 表示 4 位数字。");
                _ = ParseMask(options.Mask!, options.CustomSets); // 立刻校验占位符，错误尽早暴露
                break;

            case RecoveryStrategy.Incremental:
                if (string.IsNullOrEmpty(options.Charset))
                    throw new ArchiveException("增量策略需要填写字符集，例如 0123456789。");
                if (options.MinLength < 1)
                    throw new ArchiveException("最小长度必须 ≥ 1。");
                if (options.MaxLength < options.MinLength)
                    throw new ArchiveException($"最大长度（{options.MaxLength}）不能小于最小长度（{options.MinLength}）。");
                break;

            default:
                throw new ArchiveException($"未知的破译策略：{(int)options.Strategy}。");
        }
    }

    /// <summary>
    /// 解析掩码为「每位一个字符集」。支持 ?l ?u ?d ?s ?a ?1..?4 ?b，以及 ?? 表示字面量 '?'；
    /// 其它字符按字面量处理。未知占位符抛中文 ArchiveException。
    /// </summary>
    public static List<char[]> ParseMask(string mask, IReadOnlyList<string>? customSets)
    {
        var sets = new List<char[]>(mask.Length);
        int i = 0;
        while (i < mask.Length)
        {
            char c = mask[i];
            if (c != '?')
            {
                sets.Add(new[] { c });
                i++;
                continue;
            }

            if (i + 1 >= mask.Length)
            {
                throw new ArchiveException(
                    "掩码结尾的 “?” 不完整：请在 ? 后跟 l/u/d/s/a/b/1..4，字面量问号请写 “??”。");
            }

            char token = mask[i + 1];
            i += 2;

            switch (char.ToLowerInvariant(token))
            {
                case 'l':
                    sets.Add(Lowercase.ToCharArray());
                    break;
                case 'u':
                    sets.Add(Uppercase.ToCharArray());
                    break;
                case 'd':
                    sets.Add(Digits.ToCharArray());
                    break;
                case 's':
                    sets.Add(Symbols.ToCharArray());
                    break;
                case 'a':
                    sets.Add((Lowercase + Uppercase + Digits + Symbols).ToCharArray());
                    break;
                case 'b':
                    sets.Add(new[] { ' ' }); // ?b = 0x20 空格
                    break;
                case '?':
                    sets.Add(new[] { '?' }); // ?? = 字面量问号
                    break;
                case '1':
                case '2':
                case '3':
                case '4':
                {
                    int index = token - '1';
                    if (customSets is null || index >= customSets.Count)
                    {
                        throw new ArchiveException(
                            $"掩码用了 ?{token}，但没有提供对应的自定义字符集（?1..?4 依次对应自定义字符集 1..4）。");
                    }

                    string set = customSets[index] ?? "";
                    if (set.Length == 0)
                        throw new ArchiveException($"自定义字符集 {index + 1} 为空，无法生成候选。");

                    sets.Add(set.ToCharArray());
                    break;
                }
                default:
                    throw new ArchiveException(
                        $"掩码里有不支持的占位符 “?{token}”。支持：?l ?u ?d ?s ?a ?1..?4 ?b；字面量问号请写 “??”。");
            }
        }

        if (sets.Count == 0)
            throw new ArchiveException("掩码为空：请至少写一个占位符或字符。");

        return sets;
    }

    // ── 三种策略 ──────────────────────────────────────────────────────────────

    private static IEnumerable<string> GenerateCore(RecoveryOptions options)
    {
        switch (options.Strategy)
        {
            case RecoveryStrategy.Dictionary:
                foreach (string s in DictionaryCore(options)) yield return s;
                break;

            case RecoveryStrategy.Mask:
                foreach (string s in MaskCore(options)) yield return s;
                break;

            case RecoveryStrategy.Incremental:
                foreach (string s in IncrementalCore(options)) yield return s;
                break;
        }
    }

    private static IEnumerable<string> DictionaryCore(RecoveryOptions options)
    {
        IReadOnlyList<string> paths = options.WordlistPaths
            ?? throw new ArchiveException("字典策略需要至少一个词表文件（每行一个候选密码）。");

        foreach (string rawPath in paths)
        {
            string path = (rawPath ?? "").Trim();
            if (path.Length == 0) continue;

            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new ArchiveException($"词表文件路径无效：{path}", ex);
            }

            if (!File.Exists(full))
                throw new ArchiveException($"找不到词表文件：{full}");

            foreach (byte[] rawLine in ReadLinesRaw(full))
            {
                string word = DecodeLine(rawLine);
                if (word.Length == 0 || word.Trim().Length == 0) continue; // 跳过空行/纯空白行

                foreach (string variant in ExpandVariants(word, options.Variants))
                {
                    yield return variant;
                }
            }
        }
    }

    /// <summary>变体展开：对每个生效的规则做「用/不用」二叉展开，全部惰性（无中间数组）。</summary>
    private static IEnumerable<string> ExpandVariants(string word, WordVariant variants)
        => ExpandVariants(word, variants, 0);

    private static IEnumerable<string> ExpandVariants(string word, WordVariant variants, int stage)
    {
        int next = -1;
        for (int i = stage; i < VariantOrder.Length; i++)
        {
            if ((variants & VariantOrder[i]) != 0) { next = i; break; }
        }

        if (next < 0)
        {
            yield return word;
            yield break;
        }

        WordVariant flag = VariantOrder[next];
        foreach (string tail in ExpandVariants(word, variants, next + 1))
        {
            yield return tail; // 不套这条规则
            foreach (string applied in ApplyFlag(tail, flag))
            {
                yield return applied; // 套这条规则（AppendDigits/AppendYear/PrependDigit 会有多条）
            }
        }
    }

    private static IEnumerable<string> ApplyFlag(string word, WordVariant flag)
    {
        switch (flag)
        {
            case WordVariant.Capitalize:
            {
                if (word.Length == 0) yield break;
                string cap = char.ToUpperInvariant(word[0]) + word[1..];
                if (!string.Equals(cap, word, StringComparison.Ordinal)) yield return cap;
                break;
            }

            case WordVariant.Upper:
            {
                string upper = word.ToUpperInvariant();
                if (!string.Equals(upper, word, StringComparison.Ordinal)) yield return upper;
                break;
            }

            case WordVariant.AppendDigits:
                for (char d = '0'; d <= '9'; d++) yield return word + d;
                break;

            case WordVariant.AppendYear:
                for (int year = FirstYear; year <= LastYear; year++)
                    yield return word + year.ToString("0000", System.Globalization.CultureInfo.InvariantCulture);
                break;

            case WordVariant.PrependDigit:
                for (char d = '0'; d <= '9'; d++) yield return d + word;
                break;
        }
    }

    /// <summary>掩码：最后一位变化最快（?d?d → 00 01 … 09 10 …）。</summary>
    private static IEnumerable<string> MaskCore(RecoveryOptions options)
    {
        List<char[]> sets = ParseMask(options.Mask!, options.CustomSets);
        int positions = sets.Count;
        var indices = new int[positions];
        var buffer = new char[positions];

        while (true)
        {
            for (int k = 0; k < positions; k++) buffer[k] = sets[k][indices[k]];
            yield return new string(buffer);

            int carry = positions - 1;
            while (carry >= 0)
            {
                if (++indices[carry] < sets[carry].Length) break;
                indices[carry] = 0;
                carry--;
            }
            if (carry < 0) yield break;
        }
    }

    /// <summary>增量：长度从短到长，第一位变化最快（0,1,… 短密码优先）。</summary>
    private static IEnumerable<string> IncrementalCore(RecoveryOptions options)
    {
        char[] charset = options.Charset.ToCharArray();
        var buffer = new char[options.MaxLength];

        for (int length = options.MinLength; length <= options.MaxLength; length++)
        {
            var indices = new int[length];

            while (true)
            {
                for (int k = 0; k < length; k++) buffer[k] = charset[indices[k]];
                yield return new string(buffer, 0, length);

                int carry = 0; // 第一位变化最快
                while (carry < length)
                {
                    if (++indices[carry] < charset.Length) break;
                    indices[carry] = 0;
                    carry++;
                }
                if (carry >= length) break; // 这一位数的组合用完了，换更长的
            }
        }
    }

    // ── 词表读取（行 → 字节 → 字符串） ───────────────────────────────────────

    /// <summary>逐行产出原始字节（已去掉行尾 \r\n / \n，已去掉 UTF-8 BOM）。内存只用一个行缓冲。</summary>
    private static IEnumerable<byte[]> ReadLinesRaw(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            64 * 1024, FileOptions.SequentialScan);
        var chunk = new byte[64 * 1024];
        using var line = new MemoryStream(256);
        bool firstLine = true;

        int read;
        while ((read = fs.Read(chunk, 0, chunk.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                byte b = chunk[i];

                if (b == (byte)'\n')
                {
                    yield return FinishLine(line, ref firstLine);
                    continue;
                }

                line.WriteByte(b);

                // 异常长的行（可能是二进制文件被当成词表）：按一行截断，避免内存爆掉。
                if (line.Length >= MaxLineBytes)
                {
                    yield return FinishLine(line, ref firstLine);
                }
            }
        }

        if (line.Length > 0) yield return FinishLine(line, ref firstLine);
    }

    private static byte[] FinishLine(MemoryStream line, ref bool firstLine)
    {
        byte[] bytes = line.ToArray();
        line.SetLength(0);
        line.Position = 0;

        int length = bytes.Length;
        while (length > 0 && bytes[length - 1] == (byte)'\r') length--;

        int start = 0;
        if (firstLine)
        {
            firstLine = false;
            if (length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) start = 3;
        }

        if (start != 0 || length != bytes.Length)
        {
            var trimmed = new byte[length - start];
            Array.Copy(bytes, start, trimmed, 0, trimmed.Length);
            return trimmed;
        }
        return bytes;
    }

    /// <summary>UTF-8 严格解码；失败再退 GB18030（中文词表常见），最后 Latin-1 兜底（永不失败）。</summary>
    private static string DecodeLine(byte[] raw)
    {
        if (raw.Length == 0) return "";

        // 纯 ASCII 走最快的路径（绝大多数词表都是这种行）。
        bool ascii = true;
        foreach (byte b in raw)
        {
            if (b >= 0x80) { ascii = false; break; }
        }
        if (ascii) return Encoding.ASCII.GetString(raw);

        try
        {
            return StrictUtf8.GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            // 不是合法 UTF-8：按本机中文代码页解。
        }

        string? gb = LegacyText.TryDecodeGb18030(raw);
        return gb ?? Encoding.Latin1.GetString(raw);
    }
}

/// <summary>
/// 非 Unicode 代码页（GB18030）的兜底编解码。
///
/// 为什么不用 Encoding.GetEncoding(54936)：.NET 把非 Unicode 编码器放进了
/// System.Text.Encoding.CodePages 包，而本工程零 NuGet 依赖、共享框架里没有这个程序集。
/// 这里直接调 Windows 自带的 MultiByteToWideChar / WideCharToMultiByte（CP 54936），
/// 不引入依赖、不联网、不落盘。不可用时返回 null，调用方退化为 Latin-1。
/// </summary>
internal static class LegacyText
{
    private const uint CpGb18030 = 54936;
    private const uint CpAnsi = 0; // CP_ACP

    private static readonly bool Gb18030Usable = Probe();

    public static bool Gb18030Available => Gb18030Usable;

    /// <summary>把字符串编成 GB18030 字节（等价“本机中文 ANSI 代码页”）。不可用时返回 null。</summary>
    public static byte[]? TryEncodeGb18030(string text)
    {
        if (!Gb18030Usable || string.IsNullOrEmpty(text)) return null;

        try
        {
            char[] chars = text.ToCharArray();
            int need = WideCharToMultiByte(CpGb18030, 0, chars, chars.Length, null, 0, IntPtr.Zero, IntPtr.Zero);
            if (need <= 0) return null;

            var bytes = new byte[need];
            int written = WideCharToMultiByte(CpGb18030, 0, chars, chars.Length, bytes, need, IntPtr.Zero, IntPtr.Zero);
            if (written <= 0) return null;
            if (written != need) Array.Resize(ref bytes, written);
            return bytes;
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
        catch (MarshalDirectiveException) { return null; }
        catch (ArgumentException) { return null; }
    }

    /// <summary>把 GB18030 字节解成字符串。不可用时返回 null（调用方用 Latin-1 兜底）。</summary>
    public static string? TryDecodeGb18030(ReadOnlySpan<byte> raw)
    {
        if (!Gb18030Usable || raw.Length == 0) return null;

        try
        {
            byte[] bytes = raw.ToArray();
            int need = MultiByteToWideChar(CpGb18030, 0, bytes, bytes.Length, null, 0);
            if (need <= 0) return null;

            var chars = new char[need];
            int written = MultiByteToWideChar(CpGb18030, 0, bytes, bytes.Length, chars, need);
            if (written <= 0) return null;
            return new string(chars, 0, written);
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
        catch (MarshalDirectiveException) { return null; }
        catch (ArgumentException) { return null; }
    }

    /// <summary>本机是否真的能转 GB18030（一次性探测，失败就整段退化）。</summary>
    private static bool Probe()
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            char[] probe = { '中' };
            int need = WideCharToMultiByte(CpGb18030, 0, probe, probe.Length, null, 0, IntPtr.Zero, IntPtr.Zero);
            if (need <= 0) return false;

            var bytes = new byte[need];
            int written = WideCharToMultiByte(CpGb18030, 0, probe, probe.Length, bytes, need, IntPtr.Zero, IntPtr.Zero);
            return written == 2; // GB18030 下汉字是双字节
        }
        catch
        {
            return false;
        }
    }

#pragma warning disable SYSLIB1054 // 用经典 DllImport：LibraryImport 需要 unsafe/partial 生成代码，这里没必要
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MultiByteToWideChar(
        uint codePage, uint dwFlags, byte[] lpMultiByteStr, int cbMultiByte, char[]? lpWideCharStr, int cchWideChar);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WideCharToMultiByte(
        uint codePage, uint dwFlags, char[] lpWideCharStr, int cchWideChar,
        byte[]? lpMultiByteStr, int cbMultiByte, IntPtr lpDefaultChar, IntPtr lpUsedDefaultChar);
#pragma warning restore SYSLIB1054
}
