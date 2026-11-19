// FormatDetector.cs — 归档格式识别：扩展名优先，文件头兜底
//
// 支持：7z / zip / tar / gz / bz2 / xz / rar / iso / wim
// 另外识别分卷后缀（.7z.001 / .zip.001），便于 UI 直接打开第一卷。

namespace REAArchive.Archive;

internal static class FormatDetector
{
    private static readonly byte[] Sig7z = { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C };   // "7z\xBC\xAF\x27\x1C"
    private static readonly byte[] SigGzip = { 0x1F, 0x8B };
    private static readonly byte[] SigBzip2 = { 0x42, 0x5A, 0x68 };                   // "BZh"
    private static readonly byte[] SigXz = { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 };    // "\xFD7zXZ\x00"
    private static readonly byte[] SigRar = { 0x52, 0x61, 0x72, 0x21 };               // "Rar!"
    private static readonly byte[] SigZipLocal = { 0x50, 0x4B, 0x03, 0x04 };          // "PK\x03\x04"
    private static readonly byte[] SigZipEmpty = { 0x50, 0x4B, 0x05, 0x06 };          // "PK\x05\x06"
    private static readonly byte[] SigWim = { 0x4D, 0x53, 0x57, 0x49, 0x4D, 0x00, 0x00, 0x00 }; // "MSWIM\0\0\0"

    public static ArchiveFormat Detect(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return ArchiveFormat.Unknown;

        string ext = Path.GetExtension(path).ToLowerInvariant();
        string name = Path.GetFileName(path).ToLowerInvariant();

        // 分卷：xxx.7z.001 → SevenZip；xxx.zip.002 → Zip
        if (ext.Length == 4 && ext[1..].All(char.IsAsciiDigit))
        {
            string inner = Path.GetExtension(Path.GetFileNameWithoutExtension(path)).ToLowerInvariant();
            ArchiveFormat vol = FromExtension(inner);
            if (vol is ArchiveFormat.SevenZip or ArchiveFormat.Zip) return vol;
        }

        // 双扩展名先判
        if (name.EndsWith(".tar.gz", StringComparison.Ordinal) || name.EndsWith(".tar.bz2", StringComparison.Ordinal)
            || name.EndsWith(".tar.xz", StringComparison.Ordinal))
        {
            // 7-Zip 把这些视为 gzip/bzip2/xz 单文件流，交给对应解压器即可。
        }

        ArchiveFormat byExt = FromExtension(ext);
        if (byExt != ArchiveFormat.Unknown) return byExt;

        ArchiveFormat byMagic = DetectByMagic(path);
        if (byMagic != ArchiveFormat.Unknown) return byMagic;

        return ArchiveFormat.Unknown;
    }

    private static ArchiveFormat FromExtension(string ext) => ext.ToLowerInvariant() switch
    {
        ".7z" => ArchiveFormat.SevenZip,
        ".zip" or ".jar" or ".apk" or ".docx" or ".xlsx" or ".pptx" or ".epub" or ".cbz"
            => ArchiveFormat.Zip,
        ".tar" => ArchiveFormat.Tar,
        ".gz" or ".tgz" or ".gzip" => ArchiveFormat.GZip,
        ".bz2" or ".tbz2" or ".tbz" => ArchiveFormat.BZip2,
        ".xz" or ".txz" => ArchiveFormat.Xz,
        ".rar" or ".r00" => ArchiveFormat.Rar,
        ".iso" => ArchiveFormat.Iso,
        ".wim" or ".esd" or ".swm" => ArchiveFormat.Wim,
        _ => ArchiveFormat.Unknown,
    };

    /// <summary>读文件头兜底。任何 IO 异常都返回 Unknown（识别格式不该让 UI 崩）。</summary>
    private static ArchiveFormat DetectByMagic(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096,
                FileOptions.SequentialScan);
            Span<byte> head = stackalloc byte[512];
            int read = fs.Read(head);
            ReadOnlySpan<byte> h = head[..read];

            if (StartsWith(h, Sig7z)) return ArchiveFormat.SevenZip;
            if (StartsWith(h, SigZipLocal) || StartsWith(h, SigZipEmpty)) return ArchiveFormat.Zip;
            if (StartsWith(h, SigRar)) return ArchiveFormat.Rar;
            if (StartsWith(h, SigGzip)) return ArchiveFormat.GZip;
            if (StartsWith(h, SigBzip2)) return ArchiveFormat.BZip2;
            if (StartsWith(h, SigXz)) return ArchiveFormat.Xz;
            if (StartsWith(h, SigWim)) return ArchiveFormat.Wim;

            // tar: "ustar" 位于偏移 257
            if (h.Length >= 262 && h.Slice(257, 5).SequenceEqual("ustar"u8)) return ArchiveFormat.Tar;

            // ISO 9660: "CD001" 位于偏移 32769（扇区 16）
            if (fs.Length >= 32774)
            {
                Span<byte> iso = stackalloc byte[5];
                fs.Position = 32769;
                if (fs.Read(iso) == 5 && iso.SequenceEqual("CD001"u8)) return ArchiveFormat.Iso;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }
        return ArchiveFormat.Unknown;
    }

    private static bool StartsWith(ReadOnlySpan<byte> data, ReadOnlySpan<byte> sig)
        => data.Length >= sig.Length && data[..sig.Length].SequenceEqual(sig);
}
