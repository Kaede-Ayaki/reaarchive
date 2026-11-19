// PathGuard.cs — 解压路径安全：目录穿越拦截 + 非法字符消毒 + 长路径前缀
//
// 威胁模型（契约 §2.6/§2.7）：
//  - 归档内条目可能是 "..\\..\\Windows\\System32\\x.dll"、"/etc/passwd"、"C:\\x"、
//    "a.txt:hidden.exe"（NTFS 备选数据流）。这些在解压**之前**就必须拒绝，而不是依赖 7z 自己清洗。
//  - Windows 非法字符（< > " | ? * 与控制字符）无法落盘，按契约消毒成 '_' 并返回 warning。
//  - 目标绝对路径 > 240 字符时加 \\?\ 前缀，避免 MAX_PATH 限制。
//
// 注意：本类只做"判定/规划"，真正写文件的是 7z.exe。返回的 FullPath 供引擎做校验与清理。

using System.Text;

namespace REAArchive.Archive;

internal static class PathGuard
{
    private static readonly char[] InvalidNameChars = { '<', '>', '"', '|', '?', '*' };
    private static readonly string[] ReservedNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// 校验并消毒一个归档内条目路径。
    /// 目录穿越 / 绝对路径 / 盘符 / 冒号 ADS → 抛 ArchiveException（拒绝整个解压）。
    /// 非法字符 / 保留设备名 / 结尾点号 → 消毒并写入 warnings。
    /// </summary>
    public static string Sanitize(string entryPath, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(entryPath))
            throw new ArchiveException("归档中包含空的条目路径，已中止解压。");

        string raw = entryPath.Replace('/', '\\').Trim();

        // 绝对路径：\foo、/foo、\\server\share
        if (raw.StartsWith('\\'))
            throw new ArchiveException($"归档条目使用了绝对路径，已拒绝解压：{entryPath}");

        // 盘符（C:\... 或 C:foo），同时也是 ADS 的一种形态
        if (raw.Length >= 2 && char.IsAsciiLetter(raw[0]) && raw[1] == ':')
            throw new ArchiveException($"归档条目使用了盘符路径，已拒绝解压：{entryPath}");

        // 冒号 = NTFS 备选数据流（file.txt:evil.exe），按穿越类威胁处理
        if (raw.IndexOf(':') >= 0)
            throw new ArchiveException($"归档条目包含冒号（NTFS 数据流风险），已拒绝解压：{entryPath}");

        var segments = raw.Split('\\');
        var parts = new List<string>(segments.Length);
        bool changed = false;

        foreach (string segRaw in segments)
        {
            string seg = segRaw;

            if (seg.Length == 0 || seg == ".") continue;

            if (seg == "..")
                throw new ArchiveException($"归档条目包含目录穿越路径（..），已拒绝解压：{entryPath}");

            // 去掉 Windows 不允许的结尾点号/空格（会静默改名，先做掉并警告）
            string trimmed = seg.TrimEnd('.', ' ');
            if (trimmed.Length != seg.Length)
            {
                warnings.Add($"条目“{entryPath}”的名称含结尾点号/空格，已改为“{trimmed}”。");
                changed = true;
                seg = trimmed;
            }

            if (seg.Length == 0) { changed = true; continue; }

            var sb = new StringBuilder(seg.Length);
            foreach (char c in seg)
            {
                if (c < 32 || InvalidNameChars.Contains(c))
                {
                    sb.Append('_');
                    changed = true;
                }
                else
                {
                    sb.Append(c);
                }
            }
            string cleaned = sb.ToString();

            if (IsReservedName(cleaned))
            {
                cleaned = "_" + cleaned;
                warnings.Add($"条目“{entryPath}”使用了 Windows 保留设备名，已改为“{cleaned}”。");
                changed = true;
            }

            if (cleaned.Length == 0) { changed = true; continue; }
            parts.Add(cleaned);
        }

        if (parts.Count == 0)
            throw new ArchiveException($"归档条目路径无效，已拒绝解压：{entryPath}");

        string result = string.Join('\\', parts);
        if (changed)
            warnings.Add($"条目“{entryPath}”已消毒为“{result}”。");
        return result;
    }

    /// <summary>
    /// 把消毒后的相对路径解析为解压目标绝对路径，并做前缀越界校验。
    /// 越界（理论上不该发生，因为 Sanitize 已挡掉 ..）时抛 ArchiveException。
    /// </summary>
    public static string ResolveTarget(string destinationRoot, string sanitizedRelative)
    {
        string root = Path.GetFullPath(destinationRoot);
        string rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(rootWithSep, sanitizedRelative));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArchiveException($"条目路径无法解析为合法目标路径：{sanitizedRelative}", ex);
        }

        bool inside = full.Equals(root, StringComparison.OrdinalIgnoreCase)
                      || full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
        if (!inside)
            throw new ArchiveException($"归档条目会写到目标目录之外，已拒绝解压：{sanitizedRelative}");

        return full;
    }

    /// <summary>超过 240 字符时加 \\?\ 前缀，让后续 .NET 文件操作也支持长路径。</summary>
    public static string Extended(string fullPath)
    {
        if (fullPath.Length <= 240) return fullPath;
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal)) return fullPath;
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + fullPath[2..];
        return @"\\?\" + fullPath;
    }

    /// <summary>把归档内 '/' 分隔路径转成 7z 在 Windows 上识别的原生分隔符。</summary>
    public static string ToNative(string archivePath) => archivePath.Replace('/', '\\');

    private static bool IsReservedName(string name)
    {
        string stem = name;
        int dot = stem.IndexOf('.');
        if (dot >= 0) stem = stem[..dot];
        foreach (string r in ReservedNames)
        {
            if (stem.Equals(r, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
