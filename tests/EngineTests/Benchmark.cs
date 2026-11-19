// Benchmark.cs — 性能对照（仅测试用）：本引擎 vs 外部对照 CLI
//
// 只在显式加 --bench 时运行；对照 CLI 由环境变量 REA_BENCH_REF_CLI 指定（不指定则 SKIP），程序本身不依赖它。
// 测试集对齐 QA 口径：500MB 单文件 + 20,000 个小文件。

using System.Diagnostics;
using REAArchive.Archive;

namespace EngineTests;

internal static class Benchmark
{
    private static readonly string? RefCliPath = Environment.GetEnvironmentVariable("REA_BENCH_REF_CLI");

    public static async Task RunAsync(ArchiveService svc)
    {
        Harness.Section("性能对照：REAArchive 引擎 vs 外部对照 CLI");
        bool hasBz = !string.IsNullOrWhiteSpace(RefCliPath) && File.Exists(RefCliPath);
        if (!hasBz) Harness.Skip("性能对照", "未设置 REA_BENCH_REF_CLI 或该 CLI 不存在（程序本身不依赖它）");

        string work = Harness.NewDir("bench");
        var drive = new DriveInfo(Path.GetPathRoot(work)!);
        if (drive.AvailableFreeSpace < 5L * 1024 * 1024 * 1024)
        {
            Harness.Skip("性能对照", $"临时盘剩余空间不足（{FormatUtil.Size(drive.AvailableFreeSpace)}）");
            Harness.TryDelete(work);
            return;
        }

        try
        {
            await BigFileAsync(svc, work, hasBz);
            await ManyFilesAsync(svc, work, hasBz);
            await HundredThousandListAsync(svc, work);
        }
        finally
        {
            Harness.TryDelete(work);
        }
    }

    private static async Task BigFileAsync(ArchiveService svc, string work, bool hasBz)
    {
        const long size = 500L * 1024 * 1024;
        string src = Path.Combine(work, "big.bin");
        Harness.WriteLargePatternFile(src, size, 777);

        string archive = Path.Combine(work, "big.7z");
        var createSw = Stopwatch.StartNew();
        await svc.CreateAsync(new CreateOptions
        {
            ArchivePath = archive,
            Format = ArchiveFormat.SevenZip,
            Level = 1,
            Sources = new[] { src },
            BaseDirectory = work,
        });
        createSw.Stop();

        double ourList = await TimeListAsync(svc, archive, rounds: 2);
        string destOurs = Path.Combine(work, "out-ours");
        double ourExtract = await TimeExtractAsync(svc, archive, destOurs, rounds: 2);
        long extractedSize = new FileInfo(Path.Combine(destOurs, "big.bin")).Length;

        double bzList = -1, bzExtract = -1;
        if (hasBz)
        {
            bzList = TimeBzList(archive, rounds: 2);
            string destBz = Path.Combine(work, "out-bz");
            bzExtract = TimeBzExtract(archive, destBz, rounds: 2);
            Harness.Check("性能: 对照 CLI 解压大小一致",
                new FileInfo(Path.Combine(destBz, "big.bin")).Length == size);
        }

        Harness.CheckEqual("性能: 500MB 解压大小", size, extractedSize);
        Harness.Info($"500MB 单文件（7z -mx1）：创建 {createSw.Elapsed.TotalMilliseconds:0}ms，" +
                     $"归档 {FormatUtil.Size(new FileInfo(archive).Length)}");
        Print("500MB 列表", ourList, bzList);
        Print("500MB 解压", ourExtract, bzExtract);

        if (hasBz && bzExtract > 0)
            Harness.Check("性能: 500MB 解压与对照 CLI 同量级（≤1.6×）", ourExtract <= bzExtract * 1.6,
                $"ours={ourExtract:0}ms, bz={bzExtract:0}ms");
    }

    private static async Task ManyFilesAsync(ArchiveService svc, string work, bool hasBz)
    {
        string src = Path.Combine(work, "many");
        Directory.CreateDirectory(src);
        for (int i = 0; i < 20_000; i++)
        {
            string body = "index=" + i + ";payload=" + new string((char)('a' + i % 26), 256);
            File.WriteAllText(Path.Combine(src, "f" + i.ToString("D5") + ".txt"), body);
        }

        string archive = Path.Combine(work, "many.7z");
        var createSw = Stopwatch.StartNew();
        await svc.CreateAsync(new CreateOptions
        {
            ArchivePath = archive,
            Format = ArchiveFormat.SevenZip,
            Level = 1,
            Sources = new[] { src },
            BaseDirectory = work,
        });
        createSw.Stop();

        double ourList = await TimeListAsync(svc, archive, rounds: 2);
        string destOurs = Path.Combine(work, "many-out-ours");
        double ourExtract = await TimeExtractAsync(svc, archive, destOurs, rounds: 1);
        int ourCount = Harness.CountFiles(destOurs);

        double bzList = -1, bzExtract = -1;
        if (hasBz)
        {
            bzList = TimeBzList(archive, rounds: 2);
            string destBz = Path.Combine(work, "many-out-bz");
            bzExtract = TimeBzExtract(archive, destBz, rounds: 1);
            Harness.CheckEqual("性能: 对照 CLI 解压文件数", 20_000, Harness.CountFiles(destBz));
        }

        Harness.CheckEqual("性能: 20k 解压文件数", 20_000, ourCount);
        Harness.Info($"20k 小文件（7z -mx1）：创建 {createSw.Elapsed.TotalMilliseconds:0}ms，" +
                     $"归档 {FormatUtil.Size(new FileInfo(archive).Length)}");
        Print("20k 列表", ourList, bzList);
        Print("20k 解压", ourExtract, bzExtract);

        if (hasBz && bzList > 0)
            Harness.Check("性能: 20k 列表与对照 CLI 同量级（≤2×）", ourList <= bzList * 2.0,
                $"ours={ourList:0}ms, bz={bzList:0}ms");
        if (hasBz && bzExtract > 0)
            Harness.Check("性能: 20k 解压与对照 CLI 同量级（≤1.6×）", ourExtract <= bzExtract * 1.6,
                $"ours={ourExtract:0}ms, bz={bzExtract:0}ms");
    }

    private static async Task HundredThousandListAsync(ArchiveService svc, string work)
    {
        Harness.Info("构造 100,000 条目归档（仅用于列表性能）…");
        string src = Path.Combine(work, "hundredk");
        Directory.CreateDirectory(src);
        for (int i = 0; i < 100_000; i++)
        {
            using var fs = File.Create(Path.Combine(src, "e" + i.ToString("D6") + ".txt"));
        }

        string archive = Path.Combine(work, "hundredk.7z");
        var createSw = Stopwatch.StartNew();
        ExtractResult create = await svc.CreateAsync(new CreateOptions
        {
            ArchivePath = archive,
            Format = ArchiveFormat.SevenZip,
            Level = 0,
            Sources = new[] { src },
            BaseDirectory = work,
        });
        createSw.Stop();
        Harness.CheckEqual("性能: 100k 创建条目数", 100_000, create.Files);

        double listMs = await TimeListAsync(svc, archive, rounds: 2);
        Harness.Info($"100k 列表：{listMs:0}ms（创建 {createSw.Elapsed.TotalMilliseconds:0}ms，" +
                     $"归档 {FormatUtil.Size(new FileInfo(archive).Length)}）");
        Harness.Check("性能: 100k 列表 ≤ 2000ms（契约 §0.4）", listMs <= 2000, $"实际 {listMs:0}ms");

        Harness.TryDelete(src);
        Harness.TryDelete(archive);
    }

    // ── 计时 ─────────────────────────────────────────────────────────────────
    private static async Task<double> TimeListAsync(ArchiveService svc, string archive, int rounds)
    {
        double best = double.MaxValue;
        for (int i = 0; i < rounds; i++)
        {
            var sw = Stopwatch.StartNew();
            ListResult result = await svc.ListAsync(archive, null);
            sw.Stop();
            if (result.Entries.Count == 0) Harness.Check("性能: 列表非空", false, "列表 0 条目");
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        return best;
    }

    private static async Task<double> TimeExtractAsync(ArchiveService svc, string archive, string dest, int rounds)
    {
        double best = double.MaxValue;
        for (int i = 0; i < rounds; i++)
        {
            Harness.TryDelete(dest);
            var sw = Stopwatch.StartNew();
            await svc.ExtractAsync(archive, new ExtractOptions { Destination = dest });
            sw.Stop();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        return best;
    }

    private static double TimeBzList(string archive, int rounds)
    {
        double best = double.MaxValue;
        for (int i = 0; i < rounds; i++)
        {
            best = Math.Min(best, RunBz("l", archive, null, null));
        }
        return best;
    }

    private static double TimeBzExtract(string archive, string dest, int rounds)
    {
        double best = double.MaxValue;
        for (int i = 0; i < rounds; i++)
        {
            Harness.TryDelete(dest);
            Directory.CreateDirectory(dest);
            best = Math.Min(best, RunBz("x", archive, "-y", "-o:" + dest));
        }
        return best;
    }

    private static double RunBz(string command, string archive, string? extra1, string? extra2)
    {
        var psi = new ProcessStartInfo(RefCliPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        psi.ArgumentList.Add(command);
        if (extra1 is not null) psi.ArgumentList.Add(extra1);
        if (extra2 is not null) psi.ArgumentList.Add(extra2);
        psi.ArgumentList.Add(archive);

        var sw = Stopwatch.StartNew();
        using var proc = Process.Start(psi)!;
        proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        sw.Stop();
        if (proc.ExitCode != 0)
            Harness.Check($"对照 CLI {command} 执行成功", false, $"exit={proc.ExitCode}");
        return sw.Elapsed.TotalMilliseconds;
    }

    private static void Print(string label, double ours, double bz)
    {
        if (bz <= 0)
        {
            Harness.Info($"{label}：REAArchive {ours:0}ms（无对照 CLI）");
            return;
        }
        Harness.Info($"{label}：REAArchive {ours:0}ms，对照 CLI {bz:0}ms，" +
                     $"比值 {ours / bz:0.00}×（{(ours <= bz ? "更快" : "较慢")}）");
    }
}
