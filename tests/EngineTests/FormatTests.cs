// FormatTests.cs — 六格式矩阵：创建 → 列表 → 解压 → 逐字节比对 → 完整性测试
//
// 覆盖 7z / zip / tar / gzip / bzip2 / xz，每个格式都包含中文、日文、emoji、空格、空文件、空目录
// （gzip/bzip2/xz 是单文件流格式，只能压一个文件，单独处理）。

using System.Collections.Concurrent;
using REAArchive.Archive;

namespace EngineTests;

internal static class FormatTests
{
    public static async Task RunAsync(ArchiveService svc)
    {
        await RunOneAsync(svc, ArchiveFormat.SevenZip, ".7z");
        await RunOneAsync(svc, ArchiveFormat.Zip, ".zip");
        await RunOneAsync(svc, ArchiveFormat.Tar, ".tar");
        await RunOneAsync(svc, ArchiveFormat.GZip, ".gz");
        await RunOneAsync(svc, ArchiveFormat.BZip2, ".bz2");
        await RunOneAsync(svc, ArchiveFormat.Xz, ".xz");
    }

    private static async Task RunOneAsync(ArchiveService svc, ArchiveFormat format, string ext)
    {
        string label = FormatUtil.FormatName(format);
        Harness.Section($"{label}：创建 → 列表 → 解压 → 逐字节比对 → 测试");

        string work = Harness.NewDir("fmt-" + ext.Trim('.'));
        bool single = format is ArchiveFormat.GZip or ArchiveFormat.BZip2 or ArchiveFormat.Xz;
        string src = Path.Combine(work, "src");
        BuildTree(src, single);

        string archive = Path.Combine(work, "sample" + ext);
        string[] sources = single
            ? new[] { Path.Combine(src, "a.txt") }
            : Directory.GetFileSystemEntries(src);

        var createProgress = new ConcurrentQueue<ProgressInfo>();
        ExtractResult create;
        try
        {
            create = await svc.CreateAsync(new CreateOptions
            {
                ArchivePath = archive,
                Format = format,
                Level = 1,
                Sources = sources,
                BaseDirectory = src,
            }, new Progress<ProgressInfo>(p => createProgress.Enqueue(p)));
        }
        catch (Exception ex)
        {
            Harness.Check($"{label}: 创建成功", false, $"{ex.GetType().Name}: {ex.Message}");
            return;
        }

        Harness.Check($"{label}: 创建成功", File.Exists(archive) && new FileInfo(archive).Length > 0,
            "归档文件不存在或为空");
        Harness.CheckEqual($"{label}: 创建统计文件数", single ? 1 : 6, create.Files);
        Harness.Check($"{label}: 创建进度事件", !createProgress.IsEmpty,
            "IProgress 未收到任何进度");
        if (createProgress.TryPeek(out _))
        {
            ProgressInfo last = createProgress.Last();
            Harness.Check($"{label}: 创建进度收尾 100%", Math.Abs(last.Percent - 100) < 0.01 && last.Phase == "压缩",
                $"Percent={last.Percent}, Phase={last.Phase}");
        }

        // ── 列表 ──
        ListResult list;
        try
        {
            list = await svc.ListAsync(archive, null);
        }
        catch (Exception ex)
        {
            Harness.Check($"{label}: 列表成功", false, $"{ex.GetType().Name}: {ex.Message}");
            return;
        }

        Harness.CheckEqual($"{label}: 列表格式", format, list.Format);
        Harness.CheckEqual($"{label}: 列表文件数", single ? 1 : 6, list.FileCount);
        Harness.Check($"{label}: 列表总大小 > 0", single && format == ArchiveFormat.BZip2
                ? list.Entries[0].CompressedSize > 0 // bzip2 头不含原始大小，列表只能给出压缩后大小
                : list.TotalSize > 0,
            $"TotalSize={list.TotalSize}");
        if (format != ArchiveFormat.BZip2)
        {
            Harness.CheckEqual($"{label}: 列表总大小 = 源总字节", create.Bytes, list.TotalSize);
        }
        Harness.Check($"{label}: 列表引擎版本", !string.IsNullOrWhiteSpace(list.EngineVersion), list.EngineVersion);

        if (!single)
        {
            var paths = list.Entries.Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Harness.Check($"{label}: 中文路径", paths.Contains("中文文件夹/文件名 含空格.txt"),
                string.Join(" | ", paths.Take(8)));
            Harness.Check($"{label}: 日文路径", paths.Contains("日本語/こんにちは.txt"));
            Harness.Check($"{label}: emoji 路径", paths.Contains("emoji/😀🎉🚀.txt"));
            Harness.Check($"{label}: 路径统一 '/' 且无前导分隔符",
                list.Entries.All(e => !e.Path.Contains('\\') && !e.Path.StartsWith('/') && e.Path.Length > 0));
            ArchiveEntryInfo? emptyFile = list.Entries.FirstOrDefault(e => e.Path == "empty.txt");
            Harness.Check($"{label}: 空文件条目", emptyFile is { IsDirectory: false, Size: 0 });
            ArchiveEntryInfo? emptyDir = list.Entries.FirstOrDefault(e => e.Path == "emptydir");
            Harness.Check($"{label}: 空目录条目", emptyDir is { IsDirectory: true },
                emptyDir is null ? "列表中没有 emptydir" : $"IsDirectory={emptyDir.IsDirectory}");
            if (format is ArchiveFormat.SevenZip or ArchiveFormat.Zip)
            {
                Harness.Check($"{label}: CRC 存在", list.Entries.Any(e => !string.IsNullOrEmpty(e.Crc)));
            }
        }

        // ── 解压 ──
        string dest = Path.Combine(work, "out");
        var extractProgress = new ConcurrentQueue<ProgressInfo>();
        ExtractResult extract;
        try
        {
            extract = await svc.ExtractAsync(archive,
                new ExtractOptions { Destination = dest },
                new Progress<ProgressInfo>(p => extractProgress.Enqueue(p)));
        }
        catch (Exception ex)
        {
            Harness.Check($"{label}: 解压成功", false, $"{ex.GetType().Name}: {ex.Message}");
            return;
        }

        Harness.CheckEqual($"{label}: 解压文件数", single ? 1 : 6, extract.Files);
        Harness.Check($"{label}: 解压进度事件", !extractProgress.IsEmpty);
        if (!extractProgress.IsEmpty)
        {
            ProgressInfo last = extractProgress.Last();
            Harness.Check($"{label}: 解压进度收尾 100%", Math.Abs(last.Percent - 100) < 0.01 && last.Phase == "解压",
                $"Percent={last.Percent}, Phase={last.Phase}");
        }

        if (single)
        {
            var extracted = Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories).ToList();
            Harness.Check($"{label}: 单文件解压产物", extracted.Count == 1, $"实际 {extracted.Count} 个文件");
            if (extracted.Count == 1)
            {
                Harness.Check($"{label}: 逐字节比对", Harness.Sha256(extracted[0]) == Harness.Sha256(Path.Combine(src, "a.txt")),
                    "SHA256 不一致");
            }
        }
        else
        {
            Harness.CheckTreesEqual($"{label}: 逐字节比对（含空文件/空目录）", src, dest);
        }

        // ── 测试 ──
        var testProgress = new ConcurrentQueue<ProgressInfo>();
        try
        {
            bool ok = await svc.TestAsync(archive, null, new Progress<ProgressInfo>(p => testProgress.Enqueue(p)));
            Harness.Check($"{label}: 完整性测试", ok);
            Harness.Check($"{label}: 测试进度事件", !testProgress.IsEmpty);
        }
        catch (Exception ex)
        {
            Harness.Check($"{label}: 完整性测试", false, $"{ex.GetType().Name}: {ex.Message}");
        }

        Harness.Info($"{label} 归档大小 {FormatUtil.Size(new FileInfo(archive).Length)}，" +
                     $"创建 {create.Elapsed.TotalMilliseconds:0}ms，解压 {extract.Elapsed.TotalMilliseconds:0}ms");
    }

    private static void BuildTree(string srcDir, bool singleOnly)
    {
        Harness.WriteText(Path.Combine(srcDir, "a.txt"), "hello 归档 — 7z/zip/tar 往返\n第二行\n");
        if (singleOnly) return;

        Harness.WriteBytes(Path.Combine(srcDir, "bytes.bin"), Harness.PatternBytes(64 * 1024, 1234));
        Harness.WriteText(Path.Combine(srcDir, "中文文件夹", "文件名 含空格.txt"), "中文内容：你好，世界。");
        Harness.WriteText(Path.Combine(srcDir, "日本語", "こんにちは.txt"), "こんにちは世界");
        Harness.WriteText(Path.Combine(srcDir, "emoji", "😀🎉🚀.txt"), "emoji 😀 内容");
        Harness.WriteBytes(Path.Combine(srcDir, "empty.txt"), Array.Empty<byte>());
        Directory.CreateDirectory(Path.Combine(srcDir, "emptydir"));
    }
}
