// Program.cs — EngineTests 入口（控制台 Exe，零 NuGet，FAIL > 0 时退出码非 0）
//
// 用法：
//   dotnet run --project tests/EngineTests -c Release            # 全部
//   dotnet run --project tests/EngineTests -c Release -- 格式     # 只跑名称含“格式”的组
//   dotnet run --project tests/EngineTests -c Release -- --keep  # 保留临时目录
//   dotnet run --project tests/EngineTests -c Release -- --bench # 额外跑与外部对照 CLI 的性能对照（REA_BENCH_REF_CLI）

using System.Text;
using REAArchive.Archive;

namespace EngineTests;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        bool keep = args.Contains("--keep", StringComparer.OrdinalIgnoreCase);
        bool bench = args.Contains("--bench", StringComparer.OrdinalIgnoreCase);
        string? filter = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));

        Harness.InitRoot(keep);
        var svc = new ArchiveService();

        Console.WriteLine("REAArchive 引擎自测（纯控制台断言，零 NuGet）");
        Console.WriteLine($"引擎版本：{svc.EngineVersion}");
        Console.WriteLine($"引擎路径：{svc.EnginePath}");
        Console.WriteLine($"临时目录：{Harness.Root}");

        if (!svc.EngineAvailable)
        {
            Console.WriteLine("[FATAL] 找不到 7z.exe，无法继续。");
            return 2;
        }

        var groups = new List<(string Name, Func<Task> Run)>
        {
            ("引擎信息", () => { ErrorTests.EngineInfo(svc); return Task.CompletedTask; }),
            ("六格式矩阵", () => FormatTests.RunAsync(svc)),
            ("深目录与长路径", () => ContentTests.DeepAndLongPathAsync(svc)),
            ("大文件 200MB", () => ContentTests.BigFileAsync(svc)),
            ("10k 小文件", () => ContentTests.ManyFilesAsync(svc)),
            ("分卷", () => ContentTests.VolumesAsync(svc)),
            ("选择解压与冲突策略", () => ContentTests.SelectedAndOverwriteAsync(svc)),
            ("进度信息", () => ContentTests.ProgressAsync(svc)),
            ("进度单调性（20k）", () => ContentTests.ProgressMonotonicAsync(svc)),
            ("追加与删除", () => MutationTests.AddDeleteAsync(svc)),
            ("加密归档追加删除", () => MutationTests.EncryptedAddDeleteAsync(svc)),
            ("覆盖重建", () => MutationTests.CreateOverwritesAsync(svc)),
            ("非法参数", () => MutationTests.InvalidOptionsAsync(svc)),
            ("不存在与损坏", () => ErrorTests.MissingAndCorruptAsync(svc)),
            ("密码错误", () => ErrorTests.PasswordErrorsAsync(svc)),
            ("单文件读取", () => ErrorTests.ReadEntryErrorsAsync(svc)),
            ("取消", () => ErrorTests.CancelAsync(svc)),
            ("目录穿越", () => SecurityTests.TraversalAsync(svc)),
            ("非法字符消毒", () => SecurityTests.SanitizeAsync(svc)),
            ("格式识别", () => SecurityTests.DetectFormatAsync(svc)),
        };

        if (bench) groups.Add(("性能对照", () => Benchmark.RunAsync(svc)));

        foreach ((string name, Func<Task> run) in groups)
        {
            if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                await run().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Harness.Check($"{name}: 用例未处理异常", false, ex.ToString());
            }
        }

        Harness.PrintSummary();
        if (!keep) Harness.TryDelete(Harness.Root);
        return Harness.Fail == 0 ? 0 : 1;
    }
}
