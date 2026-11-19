// ContentTests.cs — 深目录/长路径、200MB 大文件、10,000 小文件、分卷、选择性解压、冲突策略、进度
//
// 这些用例偏“重”，但都是契约 §2 要求真实跑通的场景；每个用例自己清理临时文件。

using System.Collections.Concurrent;
using System.Text;
using REAArchive.Archive;

namespace EngineTests;

internal static class ContentTests
{
    public static async Task DeepAndLongPathAsync(ArchiveService svc)
    {
        Harness.Section("深目录 >20 层 + 解压目标路径 >240 字符");
        string work = Harness.NewDir("deep");
        string src = Path.Combine(work, "src");
        string cur = src;
        for (int i = 1; i <= 24; i++)
        {
            cur = Path.Combine(cur, "d" + i.ToString("00"));
            Directory.CreateDirectory(cur);
        }
        string deepFile = Path.Combine(cur, "深层文件.txt");
        Harness.WriteText(deepFile, "深目录内容 deep content");

        string archive = Path.Combine(work, "deep.7z");
        var create = await svc.CreateAsync(new CreateOptions
        {
            ArchivePath = archive,
            Format = ArchiveFormat.SevenZip,
            Level = 1,
            Sources = new[] { src },
            BaseDirectory = work,
        });
        Harness.Check("深目录: 创建成功", File.Exists(archive), string.Join(";", create.Warnings));

        ListResult list = await svc.ListAsync(archive, null);
        string expectedEntry = Harness.Relative(work, deepFile).Replace('\\', '/');
        ArchiveEntryInfo? entry = list.Entries.FirstOrDefault(e => e.Path == expectedEntry);
        Harness.Check("深目录: 列表包含 24 层条目", entry is not null,
            $"期望 {expectedEntry}；实际 {string.Join(" | ", list.Entries.Take(3).Select(e => e.Path))}");

        // 目标目录本身加 100 个中文字符（路径长度 +100），让最终落盘路径超过 240 字符。
        string longDest = Path.Combine(work, new string('长', 100), "out");
        ExtractResult extract = await svc.ExtractAsync(archive, new ExtractOptions { Destination = longDest });

        string relative = Harness.Relative(work, deepFile); // src/d01/.../深层文件.txt
        string extracted = Path.Combine(longDest, relative);
        Harness.Check("长路径: 目标超过 240 字符", extracted.Length > 240, $"实际 {extracted.Length} 字符");
        Harness.Check("长路径: 文件真实落盘", ExistsLong(extracted), extracted);
        Harness.Check("长路径: 内容一致",
            ExistsLong(extracted) && Harness.Sha256(extracted) == Harness.Sha256(deepFile));
        Harness.CheckEqual("长路径: 解压文件数", 1, extract.Files);

        Harness.TryDelete(work);
    }

    public static async Task BigFileAsync(ArchiveService svc)
    {
        Harness.Section("大文件 200MB：创建 → 列表 → 解压 → 逐字节比对");
        const long size = 200L * 1024 * 1024;
        string work = Harness.NewDir("big");
        var drive = new DriveInfo(Path.GetPathRoot(work)!);
        if (drive.AvailableFreeSpace < 1_600L * 1024 * 1024)
        {
            Harness.Skip("大文件 200MB", $"临时盘剩余空间不足（{FormatUtil.Size(drive.AvailableFreeSpace)}）");
            return;
        }

        string src = Path.Combine(work, "big.bin");
        Harness.WriteLargePatternFile(src, size, 424242);
        string archive = Path.Combine(work, "big.7z");

        ExtractResult create = await svc.CreateAsync(new CreateOptions
        {
            ArchivePath = archive,
            Format = ArchiveFormat.SevenZip,
            Level = 0, // 仅存储：验证大文件往返，不去测压缩率
            Sources = new[] { src },
            BaseDirectory = work,
        });
        Harness.CheckEqual("大文件: 创建文件数", 1, create.Files);
        Harness.CheckEqual("大文件: 创建字节数", size, create.Bytes);

        ListResult list = await svc.ListAsync(archive, null);
        Harness.CheckEqual("大文件: 列表大小", size, list.TotalSize);
        Harness.CheckEqual("大文件: 列表文件数", 1, list.FileCount);

        string dest = Path.Combine(work, "out");
        ExtractResult extract = await svc.ExtractAsync(archive, new ExtractOptions { Destination = dest });
        string extracted = Path.Combine(dest, "big.bin");
        Harness.Check("大文件: 解压产物存在", File.Exists(extracted),
            $"exists={File.Exists(extracted)}; warnings={string.Join(";", extract.Warnings)}");
        if (File.Exists(extracted))
        {
            Harness.CheckEqual("大文件: 解压字节数", size, new FileInfo(extracted).Length);
            Harness.Check("大文件: SHA256 一致", Harness.Sha256(extracted) == Harness.Sha256(src));
        }

        double createMbps = create.Elapsed.TotalSeconds > 0 ? size / 1024.0 / 1024.0 / create.Elapsed.TotalSeconds : 0;
        double extractMbps = extract.Elapsed.TotalSeconds > 0 ? size / 1024.0 / 1024.0 / extract.Elapsed.TotalSeconds : 0;
        Harness.Info($"200MB 存入 {create.Elapsed.TotalMilliseconds:0}ms ({createMbps:0.0} MB/s)，" +
                     $"解压 {extract.Elapsed.TotalMilliseconds:0}ms ({extractMbps:0.0} MB/s)，" +
                     $"归档 {FormatUtil.Size(new FileInfo(archive).Length)}");

        Harness.TryDelete(work);
    }

    public static async Task ManyFilesAsync(ArchiveService svc)
    {
        Harness.Section("10,000 小文件：创建 + 列表（记录耗时）");
        string work = Harness.NewDir("many");
        string src = Path.Combine(work, "src");
        Directory.CreateDirectory(src);

        var sample = new StringBuilder();
        for (int i = 0; i < 10_000; i++)
        {
            string path = Path.Combine(src, "file_" + i.ToString("D5") + ".txt");
            File.WriteAllText(path, $"index {i} 内容\n" + new string('x', 60), new UTF8Encoding(false));
            if (i % 2000 == 0) sample.Append(i).Append(',');
        }
        Harness.CheckEqual("10k: 源文件数", 10_000, Harness.CountFiles(src));

        string archive = Path.Combine(work, "many.7z");
        ExtractResult create = await svc.CreateAsync(new CreateOptions
        {
            ArchivePath = archive,
            Format = ArchiveFormat.SevenZip,
            Level = 1,
            Sources = new[] { src },
            BaseDirectory = work,
        });
        Harness.CheckEqual("10k: 创建统计文件数", 10_000, create.Files);
        Harness.Check("10k: 归档已生成", File.Exists(archive));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        ListResult list = await svc.ListAsync(archive, null);
        sw.Stop();
        long coldMs = sw.ElapsedMilliseconds;

        sw.Restart();
        await svc.ListAsync(archive, null);
        sw.Stop();
        long warmMs = sw.ElapsedMilliseconds;

        Harness.CheckEqual("10k: 列表文件数", 10_000, list.FileCount);
        Harness.Check("10k: 列表包含首尾文件",
            list.Entries.Any(e => e.Path == "src/file_00000.txt") && list.Entries.Any(e => e.Path == "src/file_09999.txt"));
        Harness.Check("10k: 列表耗时 < 5000ms", coldMs < 5000, $"冷 {coldMs}ms / 热 {warmMs}ms");
        Harness.Info($"10k 列表：冷 {coldMs}ms，热 {warmMs}ms（归档 {FormatUtil.Size(new FileInfo(archive).Length)}，" +
                     $"创建 {create.Elapsed.TotalMilliseconds:0}ms）");

        Harness.TryDelete(work);
    }

    public static async Task VolumesAsync(ArchiveService svc)
    {
        Harness.Section("分卷创建（7z -v100000b）→ 列表第一卷 → 解压比对");
        string work = Harness.NewDir("vol");
        string src = Path.Combine(work, "vol.bin");
        byte[] data = Harness.PatternBytes(300 * 1024, 99);
        Harness.WriteBytes(src, data);

        string archive = Path.Combine(work, "vol.7z");
        await svc.CreateAsync(new CreateOptions
        {
            ArchivePath = archive,
            Format = ArchiveFormat.SevenZip,
            Level = 0,
            VolumeSize = 100_000,
            Sources = new[] { src },
            BaseDirectory = work,
        });

        string[] volumes = Directory.GetFiles(work, "vol.7z.0*").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Harness.Check("分卷: 生成多个卷", volumes.Length >= 2, $"实际 {volumes.Length} 个：{string.Join(", ", volumes.Select(Path.GetFileName))}");
        Harness.Check("分卷: 不存在未分卷的单文件", !File.Exists(archive));

        if (volumes.Length >= 2)
        {
            ListResult list = await svc.ListAsync(volumes[0], null);
            Harness.CheckEqual("分卷: 第一卷可列表", 1, list.FileCount);
            Harness.CheckEqual("分卷: 第一卷条目大小", (long)data.Length, list.TotalSize);

            string dest = Path.Combine(work, "out");
            await svc.ExtractAsync(volumes[0], new ExtractOptions { Destination = dest });
            string extracted = Path.Combine(dest, "vol.bin");
            Harness.Check("分卷: 解压内容一致",
                File.Exists(extracted) && Harness.Sha256(extracted) == Harness.Sha256(src));
        }

        Harness.TryDelete(work);
    }

    public static async Task SelectedAndOverwriteAsync(ArchiveService svc)
    {
        Harness.Section("选择性解压 + 冲突策略（覆盖/跳过/重命名）");
        string work = Harness.NewDir("sel");
        string src = Path.Combine(work, "src");
        Harness.WriteText(Path.Combine(src, "a.txt"), "v1-original");
        Harness.WriteText(Path.Combine(src, "sub", "b.txt"), "b-content");
        Harness.WriteText(Path.Combine(src, "sub", "c.txt"), "c-content");

        string archive = Path.Combine(work, "sel.7z");
        await svc.CreateAsync(new CreateOptions
        {
            ArchivePath = archive,
            Format = ArchiveFormat.SevenZip,
            Level = 1,
            Sources = Directory.GetFileSystemEntries(src),
            BaseDirectory = src,
        });

        // 只解 sub/b.txt
        string selDest = Path.Combine(work, "selout");
        ExtractResult sel = await svc.ExtractAsync(archive, new ExtractOptions
        {
            Destination = selDest,
            SelectedPaths = new[] { "sub/b.txt" },
        });
        Harness.CheckEqual("选择解压: 文件数", 1, sel.Files);
        Harness.CheckEqual("选择解压: 只落一个文件", 1, Harness.CountFiles(selDest));
        Harness.Check("选择解压: 内容正确", File.Exists(Path.Combine(selDest, "sub", "b.txt")));

        // 解全量后测试冲突策略
        string dest = Path.Combine(work, "out");
        await svc.ExtractAsync(archive, new ExtractOptions { Destination = dest });
        string a = Path.Combine(dest, "a.txt");
        File.WriteAllText(a, "LOCAL-EDIT", new UTF8Encoding(false));

        ExtractResult skipped = await svc.ExtractAsync(archive, new ExtractOptions
        {
            Destination = dest,
            Overwrite = OverwritePolicy.Skip,
        });
        Harness.CheckEqual("冲突: 跳过时保留本地文件", "LOCAL-EDIT", File.ReadAllText(a));

        int before = Harness.CountFiles(dest);
        ExtractResult renamed = await svc.ExtractAsync(archive, new ExtractOptions
        {
            Destination = dest,
            Overwrite = OverwritePolicy.Rename,
        });
        Harness.CheckEqual("冲突: 重命名不覆盖本地文件", "LOCAL-EDIT", File.ReadAllText(a));
        Harness.Check("冲突: 重命名产生新文件名",
            Harness.CountFiles(dest) > before || Directory.GetFiles(dest, "a_*").Length > 0,
            $"before={before}, after={Harness.CountFiles(dest)}");

        ExtractResult overwritten = await svc.ExtractAsync(archive, new ExtractOptions
        {
            Destination = dest,
            Overwrite = OverwritePolicy.Overwrite,
        });
        Harness.CheckEqual("冲突: 覆盖策略写回归档内容", "v1-original", File.ReadAllText(a));

        Harness.Check("冲突: 跳过/重命名/覆盖都返回文件数", skipped.Files == 3 && renamed.Files == 3 && overwritten.Files == 3,
            $"{skipped.Files}/{renamed.Files}/{overwritten.Files}");

        Harness.TryDelete(work);
    }

    public static async Task ProgressAsync(ArchiveService svc)
    {
        Harness.Section("进度：百分比 / 当前项 / 速率 / 阶段");
        string work = Harness.NewDir("prog");
        string src = Path.Combine(work, "src");
        Harness.WriteBytes(Path.Combine(src, "medium.bin"), Harness.PatternBytes(6 * 1024 * 1024, 7));

        string archive = Path.Combine(work, "p.7z");
        var createEvents = new ConcurrentQueue<ProgressInfo>();
        await svc.CreateAsync(new CreateOptions
        {
            ArchivePath = archive,
            Format = ArchiveFormat.SevenZip,
            Level = 1,
            Sources = new[] { src },
            BaseDirectory = work,
        }, new Progress<ProgressInfo>(p => createEvents.Enqueue(p)));

        Harness.Check("进度: 创建阶段= 压缩", createEvents.Any(p => p.Phase == "压缩"));
        Harness.Check("进度: 创建有字节总数", createEvents.Any(p => p.BytesTotal > 0));
        Harness.Check("进度: 创建收尾 100%", createEvents.Any(p => Math.Abs(p.Percent - 100) < 0.01));

        var extractEvents = new ConcurrentQueue<ProgressInfo>();
        await svc.ExtractAsync(archive, new ExtractOptions { Destination = Path.Combine(work, "out") },
            new Progress<ProgressInfo>(p => extractEvents.Enqueue(p)));

        Harness.Check("进度: 解压阶段= 解压", extractEvents.Any(p => p.Phase == "解压"));
        Harness.Check("进度: 解压有当前项", extractEvents.Any(p => !string.IsNullOrEmpty(p.CurrentItem)),
            "没有收到任何 CurrentItem（-bb1 未生效）");
        Harness.Check("进度: 解压收尾 100%", extractEvents.Any(p => Math.Abs(p.Percent - 100) < 0.01));
        Harness.Check("进度: 速率非负", extractEvents.All(p => p.SpeedBytesPerSecond >= 0));

        Harness.TryDelete(work);
    }

    public static async Task ProgressMonotonicAsync(ArchiveService svc)
    {
        Harness.Section("进度单调性（20,000 小文件，B04 回归）");
        string work = Harness.NewDir("prog20k");
        string src = Path.Combine(work, "src");
        Directory.CreateDirectory(src);

        const int count = 20_000;
        const int payload = 64;
        for (int i = 0; i < count; i++)
        {
            var data = new byte[payload];
            data[0] = (byte)i;
            data[1] = (byte)(i >> 8);
            File.WriteAllBytes(Path.Combine(src, "f" + i.ToString("D5") + ".txt"), data);
        }

        string archive = Path.Combine(work, "many.7z");
        var createSamples = new List<(long Ms, ProgressInfo Info)>();
        var createSw = System.Diagnostics.Stopwatch.StartNew();
        await svc.CreateAsync(new CreateOptions
        {
            ArchivePath = archive,
            Format = ArchiveFormat.SevenZip,
            Level = 1,
            Sources = new[] { src },
            BaseDirectory = work,
        }, new InlineProgress(p => createSamples.Add((createSw.ElapsedMilliseconds, p))));
        createSw.Stop();

        ListResult list = await svc.ListAsync(archive, null);
        Harness.CheckEqual("B04: 列表文件数", count, list.FileCount);
        Harness.CheckEqual("B08: ListResult.TotalSize 等于源总字节", (long)count * payload, list.TotalSize);

        var samples = new List<(long Ms, ProgressInfo Info)>();
        var extractSw = System.Diagnostics.Stopwatch.StartNew();
        ExtractResult extract = await svc.ExtractAsync(
            archive,
            new ExtractOptions { Destination = Path.Combine(work, "out") },
            new InlineProgress(p => samples.Add((extractSw.ElapsedMilliseconds, p))));
        extractSw.Stop();
        Harness.CheckEqual("B04: 解压文件数", count, extract.Files);

        AssertMonotonic("B04 解压", samples, expectFinal100: true);
        AssertMonotonic("B04 压缩", createSamples, expectFinal100: true);

        // 采样输出：前 10 个点 + 每 10% 一个点
        Console.WriteLine();
        Console.WriteLine("  解压进度采样（20k 小文件，7z -mx1）：");
        Console.WriteLine("   idx      t(ms)   Percent      BytesDone / Total         当前项");
        int shown = Math.Min(10, samples.Count);
        for (int i = 0; i < shown; i++) PrintSample(i, samples[i]);
        for (int bucket = 10; bucket <= 90; bucket += 10)
        {
            int idx = samples.FindIndex(s => s.Info.Percent >= bucket);
            if (idx >= 0 && idx >= shown)
            {
                PrintSample(idx, samples[idx]);
            }
        }
        if (samples.Count > 0) PrintSample(samples.Count - 1, samples[^1]);

        Harness.Info($"B04: 解压 {extract.Elapsed.TotalMilliseconds:0}ms，进度事件 {samples.Count} 个，" +
                     $"最终 {samples[^1].Info.Percent:0.#}%");

        Harness.TryDelete(work);
    }

    private static void PrintSample(int index, (long Ms, ProgressInfo Info) sample)
    {
        ProgressInfo p = sample.Info;
        string item = p.CurrentItem is null ? "" : Path.GetFileName(p.CurrentItem);
        Console.WriteLine($"   {index,3}  {sample.Ms,8}   {p.Percent,6:0.0}%   " +
                          $"{FormatUtil.Size(p.BytesDone)} / {FormatUtil.Size(p.BytesTotal),9}   {item}");
    }

    private static void AssertMonotonic(string label, List<(long Ms, ProgressInfo Info)> samples, bool expectFinal100)
    {
        var percents = samples.Select(s => s.Info.Percent).ToList();
        Harness.Check($"{label}: 有进度事件", percents.Count >= 10, $"只有 {percents.Count} 个");

        bool monotonic = true;
        for (int i = 1; i < percents.Count; i++)
        {
            if (percents[i] < percents[i - 1])
            {
                monotonic = false;
                Harness.Check($"{label}: 单调不减", false,
                    $"第 {i} 个从 {percents[i - 1]:0.0}% 回退到 {percents[i]:0.0}%");
                break;
            }
        }
        if (monotonic) Harness.Check($"{label}: Percent 单调不减", true);

        bool anyUnknown = percents.Any(p => p < 0);
        Harness.Check($"{label}: 条目数已知时不返回 -1", !anyUnknown,
            string.Join(",", percents.Where(p => p < 0).Take(5)));

        int firstHundred = percents.FindIndex(p => p >= 99.999);
        bool earlyHundred = firstHundred >= 0 && firstHundred != percents.Count - 1;
        Harness.Check($"{label}: 不会提前 100%", !earlyHundred,
            firstHundred >= 0 ? $"首次 100% 出现在第 {firstHundred}/{percents.Count - 1} 个事件" : "从未到 100%");

        if (expectFinal100)
            Harness.Check($"{label}: 最后一个事件为 100%",
                percents.Count > 0 && Math.Abs(percents[^1] - 100) < 0.001,
                percents.Count > 0 ? $"{percents[^1]:0.0}%" : "无事件");

        bool hasMiddle = percents.Any(p => p > 0 && p < 99.999);
        Harness.Check($"{label}: 存在中间进度", hasMiddle);
    }

    private sealed class InlineProgress : IProgress<ProgressInfo>
    {
        private readonly Action<ProgressInfo> _report;
        public InlineProgress(Action<ProgressInfo> report) => _report = report;
        public void Report(ProgressInfo value) => _report(value);
    }

    private static bool ExistsLong(string path) => File.Exists(path) || File.Exists(@"\\?\" + path);
}
