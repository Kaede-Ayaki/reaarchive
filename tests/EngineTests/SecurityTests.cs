// SecurityTests.cs — 目录穿越/绝对路径/ADS 拒绝、非法字符消毒、格式识别
//
// 恶意归档用 .NET 自带 ZipArchive 构造（零 NuGet），条目名直接写成攻击载荷。

using System.IO.Compression;
using System.Text;
using REAArchive.Archive;

namespace EngineTests;

internal static class SecurityTests
{
    public static async Task TraversalAsync(ArchiveService svc)
    {
        Harness.Section("目录穿越 / 绝对路径 / ADS：解压前必须拒绝");
        string work = Harness.NewDir("sec");
        string dest = Path.Combine(work, "out");

        var cases = new (string Entry, string Label)[]
        {
            ("../evil.txt", "相对 .."),
            ("..\\..\\evil.txt", "连续 .."),
            ("/abs/evil.txt", "Unix 绝对路径"),
            ("C:\\evil.txt", "盘符绝对路径"),
            ("notes.txt:evil.exe", "NTFS ADS 冒号"),
            ("sub/../../evil.txt", "路径中间 .."),
        };

        foreach ((string entry, string label) in cases)
        {
            string zip = Path.Combine(work, "evil-" + Guid.NewGuid().ToString("N")[..6] + ".zip");
            BuildZip(zip, ("ok.txt", "ok"), (entry, "evil payload"));
            await Harness.CheckThrowsAsync<ArchiveException>($"穿越: 拒绝{label}",
                () => svc.ExtractAsync(zip, new ExtractOptions { Destination = dest }));
        }

        bool leaked = File.Exists(Path.Combine(work, "evil.txt"))
                      || File.Exists(Path.Combine(Path.GetDirectoryName(work)!, "evil.txt"))
                      || File.Exists(Path.Combine(work, "abs", "evil.txt"))
                      || File.Exists(Path.Combine(work, "C_", "evil.txt"));
        Harness.Check("穿越: 没有文件泄漏到目标目录之外", !leaked);
        Harness.Check("穿越: 被拒绝时没有写入任何文件", !Directory.Exists(dest) || Harness.CountFiles(dest) == 0,
            $"dest 下已有 {Harness.CountFiles(dest)} 个文件");

        Harness.TryDelete(work);
    }

    public static async Task SanitizeAsync(ArchiveService svc)
    {
        Harness.Section("非法字符消毒（warning，不崩溃）");
        string work = Harness.NewDir("sanitize");
        string zip = Path.Combine(work, "bad.zip");
        BuildZip(zip,
            ("bad|name?.txt", "1"),
            ("dir<1>/file||.txt", "2"),
            ("trail.", "3"),
            ("reserved/CON", "4"));

        string dest = Path.Combine(work, "out");
        ExtractResult result = await svc.ExtractAsync(zip, new ExtractOptions { Destination = dest });

        Harness.CheckEqual("消毒: 全部解压", 4, result.Files);
        Harness.CheckEqual("消毒: 落盘文件数", 4, Harness.CountFiles(dest));
        Harness.Check("消毒: 生成 warning", result.Warnings.Count > 0,
            $"warnings={result.Warnings.Count}");
        Harness.Check("消毒: 非法字符被替换为 _",
            File.Exists(Path.Combine(dest, "bad_name_.txt")),
            string.Join(" | ", Directory.GetFiles(dest, "*", SearchOption.AllDirectories).Select(Path.GetFileName)));
        Harness.Check("消毒: 保留设备名被改写（CON → _CON）",
            Directory.GetFiles(dest, "_CON", SearchOption.AllDirectories).Length == 1,
            string.Join(" | ", Directory.GetFiles(dest, "*", SearchOption.AllDirectories).Select(Path.GetFileName)));

        Harness.TryDelete(work);
    }

    public static async Task DetectFormatAsync(ArchiveService svc)
    {
        Harness.Section("DetectFormat：扩展名优先 + 文件头兜底");
        string work = Harness.NewDir("detect");
        string src = Path.Combine(work, "src");
        Harness.WriteText(Path.Combine(src, "s.txt"), "detect me " + new string('z', 1000));

        // 扩展名
        var extCases = new (string Name, ArchiveFormat Expected)[]
        {
            ("a.7z", ArchiveFormat.SevenZip),
            ("a.zip", ArchiveFormat.Zip),
            ("a.rar", ArchiveFormat.Rar),
            ("a.tar", ArchiveFormat.Tar),
            ("a.gz", ArchiveFormat.GZip),
            ("a.tgz", ArchiveFormat.GZip),
            ("a.bz2", ArchiveFormat.BZip2),
            ("a.xz", ArchiveFormat.Xz),
            ("a.iso", ArchiveFormat.Iso),
            ("a.wim", ArchiveFormat.Wim),
            ("a.7z.001", ArchiveFormat.SevenZip),
            ("a.zip.002", ArchiveFormat.Zip),
            ("a.txt", ArchiveFormat.Unknown),
            ("a.bin", ArchiveFormat.Unknown),
        };
        foreach ((string name, ArchiveFormat expected) in extCases)
        {
            Harness.CheckEqual($"识别: 扩展名 {name}", expected, svc.DetectFormat(Path.Combine(work, name)));
        }

        // 文件头（把真实归档改成 .dat，逼它读 magic）
        var magicCases = new (ArchiveFormat Format, string Ext)[]
        {
            (ArchiveFormat.SevenZip, ".7z"),
            (ArchiveFormat.Zip, ".zip"),
            (ArchiveFormat.Tar, ".tar"),
            (ArchiveFormat.GZip, ".gz"),
            (ArchiveFormat.BZip2, ".bz2"),
            (ArchiveFormat.Xz, ".xz"),
        };
        foreach ((ArchiveFormat format, string ext) in magicCases)
        {
            string arch = Path.Combine(work, "magic" + ext);
            await svc.CreateAsync(new CreateOptions
            {
                ArchivePath = arch,
                Format = format,
                Level = 1,
                Sources = new[] { Path.Combine(src, "s.txt") },
                BaseDirectory = src,
            });
            string disguised = Path.Combine(work, "blob-" + ext.Trim('.') + ".dat");
            File.Copy(arch, disguised, overwrite: true);
            Harness.CheckEqual($"识别: 文件头 {ext}", format, svc.DetectFormat(disguised));
        }

        // 随机数据 + 未知扩展 → Unknown
        string noise = Path.Combine(work, "noise.dat");
        Harness.WriteBytes(noise, Harness.PatternBytes(8192, 21));
        Harness.CheckEqual("识别: 随机数据 → Unknown", ArchiveFormat.Unknown, svc.DetectFormat(noise));

        // 不存在的文件按扩展名判断（UI 打开前也能显示类型）
        Harness.CheckEqual("识别: 不存在但扩展名已知", ArchiveFormat.SevenZip,
            svc.DetectFormat(Path.Combine(work, "ghost.7z")));

        Harness.TryDelete(work);
    }

    private static void BuildZip(string path, params (string Name, string Content)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach ((string name, string content) in entries)
        {
            ZipArchiveEntry entry = zip.CreateEntry(name);
            using Stream s = entry.Open();
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            s.Write(bytes, 0, bytes.Length);
        }
    }
}
