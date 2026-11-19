// ErrorTests.cs — 引擎信息、损坏归档、错误密码、文件不存在、取消（真实杀进程）
//
// 重点：所有失败都必须是中文 ArchiveException；取消必须是 OperationCanceledException 且不留僵尸 7z.exe。

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using REAArchive.Archive;

namespace EngineTests;

internal static class ErrorTests
{
    public static void EngineInfo(ArchiveService svc)
    {
        Harness.Section("引擎信息");
        Harness.Check("引擎: EngineAvailable", svc.EngineAvailable, svc.EnginePath);
        Harness.Check("引擎: EngineVersion 形如 7-Zip 25.01 (x64)",
            svc.EngineVersion.StartsWith("7-Zip", StringComparison.Ordinal) && svc.EngineVersion.Contains("25.01"),
            svc.EngineVersion);
        Harness.Check("引擎: 7z.exe 存在", File.Exists(svc.EnginePath), svc.EnginePath);
        Harness.CheckEqual("引擎: CreatableFormats 六种", 6, svc.CreatableFormats.Count);
        Harness.Check("引擎: CreatableFormats 含 7z/zip/tar/gz/bz2/xz",
            new[] { ArchiveFormat.SevenZip, ArchiveFormat.Zip, ArchiveFormat.Tar, ArchiveFormat.GZip, ArchiveFormat.BZip2, ArchiveFormat.Xz }
                .All(f => svc.CreatableFormats.Contains(f)));
    }

    public static async Task MissingAndCorruptAsync(ArchiveService svc)
    {
        Harness.Section("不存在 / 损坏 / 截断的归档 → 中文异常");
        string work = Harness.NewDir("bad");
        string missing = Path.Combine(work, "nope.7z");

        await Harness.CheckThrowsAsync<ArchiveException>("错误: 列表不存在文件", () => svc.ListAsync(missing, null), "找不到");
        await Harness.CheckThrowsAsync<ArchiveException>("错误: 解压不存在文件",
            () => svc.ExtractAsync(missing, new ExtractOptions { Destination = Path.Combine(work, "out") }), "找不到");
        await Harness.CheckThrowsAsync<ArchiveException>("错误: 测试不存在文件", () => svc.TestAsync(missing, null), "找不到");
        await Harness.CheckThrowsAsync<ArchiveException>("错误: 读取不存在文件",
            () => svc.ReadEntryAsync(missing, "a.txt", null), "找不到");

        // 随机垃圾
        string garbage = Path.Combine(work, "garbage.7z");
        Harness.WriteBytes(garbage, Harness.PatternBytes(4096, 5));
        await Harness.CheckThrowsAsync<ArchiveException>("错误: 垃圾文件当 7z 列表", () => svc.ListAsync(garbage, null), "损坏");
        await Harness.CheckThrowsAsync<ArchiveException>("错误: 垃圾文件测试", () => svc.TestAsync(garbage, null), "损坏");

        // 截断的 7z（头在文件尾部，截断后无法打开）
        string src = Path.Combine(work, "src");
        Harness.WriteText(Path.Combine(src, "a.txt"), new string('A', 200_000));
        string good = Path.Combine(work, "good.7z");
        await svc.CreateAsync(new CreateOptions
        {
            ArchivePath = good,
            Format = ArchiveFormat.SevenZip,
            Level = 1,
            Sources = new[] { Path.Combine(src, "a.txt") },
            BaseDirectory = src,
        });

        byte[] bytes = File.ReadAllBytes(good);
        string truncated = Path.Combine(work, "truncated.7z");
        Harness.WriteBytes(truncated, bytes[..(int)(bytes.Length * 0.55)]);

        await Harness.CheckThrowsAsync<ArchiveException>("错误: 截断归档测试", () => svc.TestAsync(truncated, null), "损坏");
        await Harness.CheckThrowsAsync<ArchiveException>("错误: 截断归档列表", () => svc.ListAsync(truncated, null), "损坏");

        // 目录当归档
        await Harness.CheckThrowsAsync<ArchiveException>("错误: 文件夹当归档",
            () => svc.ListAsync(work, null), "文件夹");

        Harness.TryDelete(work);
    }

    public static async Task PasswordErrorsAsync(ArchiveService svc)
    {
        Harness.Section("密码：数据加密 / 文件名加密 / 错误密码");
        const string password = "pa ss #1"; // 注意：ZIP 加密不支持非 ASCII 密码（7-Zip 限制），单独测
        string work = Harness.NewDir("pw");
        string src = Path.Combine(work, "src");
        Harness.WriteText(Path.Combine(src, "secret.txt"), "TOP-SECRET-内容");

        // 1) 7z 数据加密（文件名不加密）
        string dataEnc = Path.Combine(work, "data.7z");
        await svc.CreateAsync(new CreateOptions
        {
            ArchivePath = dataEnc,
            Format = ArchiveFormat.SevenZip,
            Level = 1,
            Password = password,
            EncryptHeaders = false,
            Sources = new[] { Path.Combine(src, "secret.txt") },
            BaseDirectory = src,
        });

        ListResult plainList = await svc.ListAsync(dataEnc, null);
        Harness.Check("密码: 未加密文件名时可无密码列表", plainList.FileCount == 1 && plainList.IsEncrypted,
            $"FileCount={plainList.FileCount}, IsEncrypted={plainList.IsEncrypted}");

        await Harness.CheckThrowsAsync<ArchiveException>("密码: 无密码解压加密 7z",
            () => svc.ExtractAsync(dataEnc, new ExtractOptions { Destination = Path.Combine(work, "o1") }), "密码");
        await Harness.CheckThrowsAsync<ArchiveException>("密码: 错误密码解压 7z",
            () => svc.ExtractAsync(dataEnc, new ExtractOptions { Destination = Path.Combine(work, "o2"), Password = "bad" }),
            "密码错误");
        await Harness.CheckThrowsAsync<ArchiveException>("密码: 错误密码测试 7z",
            () => svc.TestAsync(dataEnc, "bad"), "密码");

        string dest1 = Path.Combine(work, "ok1");
        await svc.ExtractAsync(dataEnc, new ExtractOptions { Destination = dest1, Password = password });
        Harness.Check("密码: 正确密码解压 7z",
            File.Exists(Path.Combine(dest1, "secret.txt")) && File.ReadAllText(Path.Combine(dest1, "secret.txt")) == "TOP-SECRET-内容");

        // 2) 文件名加密：无密码连列表都不行
        string hdrEnc = Path.Combine(work, "hdr.7z");
        await svc.CreateAsync(new CreateOptions
        {
            ArchivePath = hdrEnc,
            Format = ArchiveFormat.SevenZip,
            Level = 1,
            Password = password,
            EncryptHeaders = true,
            Sources = new[] { Path.Combine(src, "secret.txt") },
            BaseDirectory = src,
        });
        await Harness.CheckThrowsAsync<ArchiveException>("密码: 文件名加密无密码列表",
            () => svc.ListAsync(hdrEnc, null), "密码");
        await Harness.CheckThrowsAsync<ArchiveException>("密码: 文件名加密错误密码列表",
            () => svc.ListAsync(hdrEnc, "bad"), "密码错误");
        Harness.CheckEqual("密码: 文件名加密正确密码列表", 1, (await svc.ListAsync(hdrEnc, password)).FileCount);

        // 3) ZIP 加密
        string zipEnc = Path.Combine(work, "data.zip");
        await svc.CreateAsync(new CreateOptions
        {
            ArchivePath = zipEnc,
            Format = ArchiveFormat.Zip,
            Level = 1,
            Password = password,
            Sources = new[] { Path.Combine(src, "secret.txt") },
            BaseDirectory = src,
        });
        await Harness.CheckThrowsAsync<ArchiveException>("密码: 无密码解压加密 zip",
            () => svc.ExtractAsync(zipEnc, new ExtractOptions { Destination = Path.Combine(work, "z1") }), "密码");
        await Harness.CheckThrowsAsync<ArchiveException>("密码: 错误密码解压 zip",
            () => svc.ExtractAsync(zipEnc, new ExtractOptions { Destination = Path.Combine(work, "z2"), Password = "bad" }), "密码");

        string destZ = Path.Combine(work, "zok");
        await svc.ExtractAsync(zipEnc, new ExtractOptions { Destination = destZ, Password = password });
        Harness.Check("密码: 正确密码解压 zip",
            File.Exists(Path.Combine(destZ, "secret.txt")) && File.ReadAllText(Path.Combine(destZ, "secret.txt")) == "TOP-SECRET-内容");

        // 4) 7-Zip 的 ZIP 加密不支持非 ASCII 密码 → 必须提前给中文错误，而不是暴露 7z 的乱码断言
        await Harness.CheckThrowsAsync<ArchiveException>("密码: ZIP 非 ASCII 密码给出可读错误",
            () => svc.CreateAsync(new CreateOptions
            {
                ArchivePath = Path.Combine(work, "unicode.zip"),
                Format = ArchiveFormat.Zip,
                Level = 1,
                Password = "中文密码",
                Sources = new[] { Path.Combine(src, "secret.txt") },
                BaseDirectory = src,
            }), "ASCII");

        Harness.TryDelete(work);
    }

    public static async Task CancelAsync(ArchiveService svc)
    {
        Harness.Section("取消：2 秒后 Cancel → OperationCanceledException，无僵尸 7z，旧归档不被毁");
        string work = Harness.NewDir("cancel");

        // 96MB 不可压缩数据 + 极限压缩，保证 2 秒时仍在压缩。
        string src = Path.Combine(work, "random.bin");
        using (var fs = new FileStream(src, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            byte[] buf = new byte[1024 * 1024];
            for (int i = 0; i < 96; i++)
            {
                RandomNumberGenerator.Fill(buf);
                fs.Write(buf, 0, buf.Length);
            }
        }

        string target = Path.Combine(work, "cancel.7z");
        byte[] sentinel = Encoding.ASCII.GetBytes("ORIGINAL-ARCHIVE-MUST-SURVIVE");
        File.WriteAllBytes(target, sentinel);

        // 记录取消前已存在的 7z PID，只检查“本次新产生且 5 秒内不退出”的进程。
        var beforePids = Process.GetProcessesByName("7z").Select(p => p.Id).ToHashSet();
        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        bool canceled = false;
        string? error = null;
        try
        {
            await svc.CreateAsync(new CreateOptions
            {
                ArchivePath = target,
                Format = ArchiveFormat.SevenZip,
                Level = 9,
                Sources = new[] { src },
                BaseDirectory = work,
            }, null, cts.Token);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
        }
        sw.Stop();

        Harness.Check("取消: 抛 OperationCanceledException", canceled, error ?? "操作正常完成（太快，未触发取消）");
        Harness.Check("取消: 及时返回 (<15s)", sw.Elapsed.TotalSeconds < 15, $"{sw.Elapsed.TotalSeconds:0.0}s");
        Harness.Check("取消: 原归档内容未被破坏", File.ReadAllBytes(target).SequenceEqual(sentinel));
        Harness.Check("取消: 没有留下临时目录",
            Directory.GetDirectories(work, ".rea-tmp-*").Length == 0,
            string.Join(", ", Directory.GetDirectories(work)));

        // Kill 是异步生效的：最多等 5 秒，直到没有“本次新产生”的 7z 进程。
        var zombieWatch = Stopwatch.StartNew();
        int leftover = 0;
        while (zombieWatch.ElapsedMilliseconds < 5000)
        {
            leftover = Process.GetProcessesByName("7z").Count(p => !beforePids.Contains(p.Id));
            if (leftover == 0) break;
            await Task.Delay(100);
        }
        Harness.Check("取消: 没有僵尸 7z 进程", leftover == 0,
            $"取消后仍有 {leftover} 个新的 7z 进程（已等待 {zombieWatch.ElapsedMilliseconds}ms）");

        Harness.Info($"取消耗时 {sw.Elapsed.TotalMilliseconds:0}ms；进程收尾等待 {zombieWatch.ElapsedMilliseconds}ms，" +
                     $"取消前 7z 进程 {beforePids.Count} 个 → 新残留 {leftover} 个");
        Harness.TryDelete(work);
    }

    public static async Task ReadEntryErrorsAsync(ArchiveService svc)
    {
        Harness.Section("单文件读取：正确内容 / 超限 / 不存在 / 目录");
        string work = Harness.NewDir("read");
        string src = Path.Combine(work, "src");
        byte[] content = Encoding.UTF8.GetBytes("read me 内容 1234567890");
        Harness.WriteBytes(Path.Combine(src, "a.txt"), content);
        Harness.WriteText(Path.Combine(src, "sub", "inner.txt"), "inner");

        string archive = Path.Combine(work, "r.7z");
        await svc.CreateAsync(new CreateOptions
        {
            ArchivePath = archive,
            Format = ArchiveFormat.SevenZip,
            Level = 1,
            Sources = Directory.GetFileSystemEntries(src),
            BaseDirectory = src,
        });

        byte[] data = await svc.ReadEntryAsync(archive, "a.txt", null);
        Harness.Check("读取: 内容逐字节一致", data.SequenceEqual(content),
            $"len={data.Length} vs {content.Length}");

        await Harness.CheckThrowsAsync<ArchiveException>("读取: 超过 maxBytes",
            () => svc.ReadEntryAsync(archive, "a.txt", null, maxBytes: 4), "超过");
        await Harness.CheckThrowsAsync<ArchiveException>("读取: 条目不存在",
            () => svc.ReadEntryAsync(archive, "missing.txt", null), "找不到");
        await Harness.CheckThrowsAsync<ArchiveException>("读取: 目录不能预览",
            () => svc.ReadEntryAsync(archive, "sub", null), "文件夹");

        Harness.TryDelete(work);
    }
}
