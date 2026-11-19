// SevenZipListParser.cs — 解析 `7z l -slt -sccUTF-8` 的输出
//
// 输出结构（7-Zip 25.01）：
//   版本横幅 / Scanning... / Listing archive: ...
//   -- 
//   Path / Type / Physical Size / Method / Solid / Blocks ...   ← 归档级信息
//   空行
//   ----------                                                  ← 条目区分隔线
//   Path = a.txt
//   Folder = -
//   Size = 11
//   ...
//   （空行分隔下一个条目）
//
// 注意：中文名必须靠 -sccUTF-8 才不会乱码；本类按行接收，避免 10 万条目时把整段输出留在内存里。

using System.Globalization;

namespace REAArchive.Archive;

internal sealed class SevenZipListParser
{
    private readonly List<ArchiveEntryInfo> _entries = new();
    private readonly Dictionary<string, string> _header = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _block = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lastBlock = new(StringComparer.OrdinalIgnoreCase);
    private bool _inBody;
    private bool _blockDirty;

    public IReadOnlyList<ArchiveEntryInfo> Entries => _entries;

    /// <summary>
    /// 最后一个条目的原始字段。xz/bzip2 单流格式的 -slt 输出没有 Path 字段（7z 只在普通列表里显示名字），
    /// 需要靠这里拿 Size / Method / CRC 再合成一个条目。
    /// </summary>
    public string? LastValue(string key)
        => _lastBlock.TryGetValue(key, out string? v) ? v.Trim() : null;

    public string? ArchiveType => GetHeader("Type");

    public string? Comment => GetHeader("Comment") is { Length: > 0 } c ? c : null;

    /// <summary>归档级 Encrypted 标记（zip 等格式会给出）。</summary>
    public bool HeaderEncrypted
        => string.Equals(GetHeader("Encrypted"), "+", StringComparison.Ordinal);

    /// <summary>7z 头部加密时，归档级 Method 会带 7zAES（数据加密也会带，故只作辅助判断）。</summary>
    public string HeaderMethod => GetHeader("Method") ?? "";

    public void Feed(string rawLine)
    {
        string line = rawLine.TrimEnd();
        if (!_inBody)
        {
            if (line.Trim() == "----------") { _inBody = true; return; }
            if (line.Trim() == "--") return;

            int i = line.IndexOf(" = ", StringComparison.Ordinal);
            if (i > 0)
            {
                string key = line[..i].Trim();
                string value = line[(i + 3)..];
                if (key.Length > 0) _header[key] = value.Trim();
            }
            return;
        }

        if (line.Length == 0)
        {
            FlushBlock();
            return;
        }

        int eq = line.IndexOf(" = ", StringComparison.Ordinal);
        if (eq <= 0)
        {
            // 7z 对某些格式可能输出无 " = " 的续行，忽略。
            return;
        }

        string k = line[..eq].Trim();
        string v = line[(eq + 3)..];
        if (k.Length == 0) return;
        _block[k] = v;
        _blockDirty = true;
    }

    /// <summary>列表结束（输出读完）时调用，确保最后一个条目被收下。</summary>
    public void Complete() => FlushBlock();

    private void FlushBlock()
    {
        if (!_blockDirty) { _block.Clear(); return; }
        _blockDirty = false;

        _lastBlock.Clear();
        foreach (var kv in _block) _lastBlock[kv.Key] = kv.Value;

        if (_block.TryGetValue("Path", out string? rawPath) && rawPath.Length > 0)
        {
            string path = NormalizePath(rawPath);
            if (path.Length > 0)
            {
                bool isDir = IsDirectory(_block);
                _entries.Add(new ArchiveEntryInfo
                {
                    Path = path,
                    IsDirectory = isDir,
                    Size = ParseLong(_block, "Size"),
                    CompressedSize = ParseLongOrUnknown(_block, "Packed Size"),
                    Modified = ParseDate(_block, "Modified"),
                    Encrypted = string.Equals(Get(_block, "Encrypted"), "+", StringComparison.Ordinal),
                    Method = Get(_block, "Method") ?? "",
                    Crc = Get(_block, "CRC") is { Length: > 0 } crc ? crc.ToUpperInvariant() : null,
                });
            }
        }
        _block.Clear();
    }

    private static bool IsDirectory(Dictionary<string, string> block)
    {
        string? folder = Get(block, "Folder");
        if (string.Equals(folder, "+", StringComparison.Ordinal)) return true;

        string? attrs = Get(block, "Attributes");
        if (!string.IsNullOrEmpty(attrs))
        {
            foreach (char c in attrs)
            {
                if (c == 'D' || c == 'd') return true;
            }
        }
        return false;
    }

    /// <summary>统一成 '/' 分隔；保留 '..' 与绝对路径原样，交给安全层判定（不能在这里"洗干净"）。</summary>
    private static string NormalizePath(string raw)
    {
        string p = raw.Replace('\\', '/').TrimEnd('\r');
        while (p.EndsWith("/", StringComparison.Ordinal) && p.Length > 1) p = p[..^1];
        return p;
    }

    private static long ParseLong(Dictionary<string, string> block, string key)
        => long.TryParse(Get(block, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) && v >= 0 ? v : 0;

    private static long ParseLongOrUnknown(Dictionary<string, string> block, string key)
    {
        string? s = Get(block, key);
        if (string.IsNullOrWhiteSpace(s)) return -1;
        return long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) && v >= 0 ? v : -1;
    }

    private static DateTime? ParseDate(Dictionary<string, string> block, string key)
    {
        string? s = Get(block, key);
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out DateTime dt))
            return dt;
        return null;
    }

    private static string? Get(Dictionary<string, string> block, string key)
        => block.TryGetValue(key, out string? v) ? v.Trim() : null;

    private string? GetHeader(string key)
        => _header.TryGetValue(key, out string? v) ? v.Trim() : null;
}
