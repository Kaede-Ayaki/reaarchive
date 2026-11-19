// TestHarness.cs — 零 NuGet 的断言/临时目录/哈希工具
//
// 约束：不使用任何测试框架包（离线可编译）。断言失败只计数并打印，全部跑完后由 Program 决定退出码。

using System.Security.Cryptography;
using System.Text;

namespace EngineTests;

internal static class Harness
{
    public static int Pass;
    public static int Fail;
    public static int SkipCount;
    public static readonly List<string> Failures = new();

    private static readonly object Gate = new();

    public static string Root { get; private set; } = "";

    public static void InitRoot(bool keep)
    {
        Root = Path.Combine(Path.GetTempPath(), "reaarchive-tests",
            DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Environment.ProcessId);
        Directory.CreateDirectory(Root);
        if (!keep) Console.WriteLine($"[INFO] 临时目录：{Root}");
    }

    public static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"── {title} ─────────────────────────────────────────────");
    }

    public static void Info(string message) => Console.WriteLine($"[INFO] {message}");

    public static void Check(string name, bool ok, string? detail = null)
    {
        lock (Gate)
        {
            if (ok)
            {
                Pass++;
                Console.WriteLine($"[PASS] {name}");
            }
            else
            {
                Fail++;
                string line = detail is null ? name : $"{name} — {detail}";
                Failures.Add(line);
                Console.WriteLine($"[FAIL] {line}");
            }
        }
    }

    public static void CheckEqual<T>(string name, T expected, T actual)
        => Check(name, EqualityComparer<T>.Default.Equals(expected, actual), $"期望 {expected}，实际 {actual}");

    public static void Skip(string name, string reason)
    {
        lock (Gate)
        {
            SkipCount++;
            Console.WriteLine($"[SKIP] {name} — {reason}");
        }
    }

    /// <summary>断言 action 抛 T（或其派生类），且 Message 含 contains（若提供）。</summary>
    public static async Task CheckThrowsAsync<T>(string name, Func<Task> action, string? contains = null)
        where T : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
            Check(name, false, $"没有抛异常（期望 {typeof(T).Name}）");
        }
        catch (T ex)
        {
            if (contains is not null && !ex.Message.Contains(contains, StringComparison.Ordinal))
            {
                Check(name, false, $"异常信息不含“{contains}”：{ex.Message}");
            }
            else
            {
                Check(name, true);
            }
        }
        catch (Exception ex)
        {
            Check(name, false, $"抛了 {ex.GetType().Name} 而不是 {typeof(T).Name}：{ex.Message}");
        }
    }

    // ── 临时目录 / 文件 ──────────────────────────────────────────────────────

    public static string NewDir(string label)
    {
        string dir = Path.Combine(Root, label + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string NewPath(string label, string fileName)
    {
        string dir = NewDir(label);
        return Path.Combine(dir, fileName);
    }

    public static void WriteText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    public static void WriteBytes(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
    }

    /// <summary>确定性伪随机数据（同一 seed 可复现），便于逐字节比对。</summary>
    public static byte[] PatternBytes(int length, int seed)
    {
        var data = new byte[length];
        var rng = new Random(seed);
        rng.NextBytes(data);
        return data;
    }

    /// <summary>大文件用：1MB 随机块重复填充，避免一次性分配几百 MB 随机数。</summary>
    public static void WriteLargePatternFile(string path, long totalBytes, int seed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] block = PatternBytes(1024 * 1024, seed);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        long remaining = totalBytes;
        while (remaining > 0)
        {
            int n = (int)Math.Min(block.Length, remaining);
            fs.Write(block, 0, n);
            remaining -= n;
        }
    }

    // ── 哈希 / 目录比对 ──────────────────────────────────────────────────────

    public static string Sha256(string path)
    {
        using var sha = SHA256.Create();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20,
            FileOptions.SequentialScan);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    public static SortedDictionary<string, string> HashTree(string root)
    {
        var map = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return map;

        foreach (string dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            map[Relative(root, dir) + "/"] = "dir";

        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            map[Relative(root, file)] = Sha256(file) + "|" + new FileInfo(file).Length;

        return map;
    }

    public static void CheckTreesEqual(string name, string expectedRoot, string actualRoot)
    {
        var expected = HashTree(expectedRoot);
        var actual = HashTree(actualRoot);

        var onlyExpected = expected.Keys.Except(actual.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        var onlyActual = actual.Keys.Except(expected.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        var different = expected.Where(kv => actual.TryGetValue(kv.Key, out string? v) && v != kv.Value)
                                .Select(kv => kv.Key).ToList();

        bool ok = onlyExpected.Count == 0 && onlyActual.Count == 0 && different.Count == 0;
        string detail = ok ? "" :
            $"缺少[{string.Join(", ", onlyExpected.Take(5))}] 多出[{string.Join(", ", onlyActual.Take(5))}] " +
            $"内容不同[{string.Join(", ", different.Take(5))}]";
        Check(name, ok, detail);
    }

    public static int CountFiles(string root)
        => Directory.Exists(root) ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count() : 0;

    public static string Relative(string root, string full)
        => Path.GetRelativePath(root, full).Replace('\\', '/');

    // ── 清理 ──────────────────────────────────────────────────────────────────

    public static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void PrintSummary()
    {
        Console.WriteLine();
        Console.WriteLine("══════════════════════════════════════════════════════════");
        Console.WriteLine($"PASS {Pass} / FAIL {Fail} / SKIP {SkipCount}");
        if (Failures.Count > 0)
        {
            Console.WriteLine("失败明细：");
            foreach (string f in Failures) Console.WriteLine("  - " + f);
        }
        Console.WriteLine("══════════════════════════════════════════════════════════");
    }
}
