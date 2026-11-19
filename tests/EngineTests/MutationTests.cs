// MutationTests.cs — 向已存在归档追加 / 删除条目，以及 CreateAsync 的“覆盖重建”语义
//
// 契约裁决（2026-11-19）：AddAsync = 7z a 追加；DeleteAsync = 7z d；CreateAsync 对已存在目标必须覆盖重建。

using REAArchive.Archive;

namespace EngineTests;

internal static class MutationTests
{
    public static async Task AddDeleteAsync(ArchiveService svc)
    {
        Harness.Section("追加 AddAsync / 删除 DeleteAsync（明文归档）");
        string work = Harness.NewDir("mut");
        string srcA = Path.Combine(work, "srcA");
        Harness.WriteText(Path.Combine(srcA, "a.txt"), "A-content");

        string archive = Path.Combine(work, "m.7z");
        await svc.CreateAsync(new CreateOptions
        {
            ArchivePath = archive,
            Format = ArchiveFormat.SevenZip,
            Level = 1,
            Sources = new[] { Path.Combine(srcA, "a.txt") },
            BaseDirectory = srcA,
        });

        string srcB = Path.Combine(work, "srcB");
        Harness.WriteText(Path.Combine(srcB, "extra.txt"), "EXTRA");
        Harness.WriteText(Path.Combine(srcB, "newdir", "新文件.txt"), "新内容");

        ExtractResult add = await svc.AddAsync(new AddOptions
        {
            ArchivePath = archive,
            Sources = new[] { Path.Combine(srcB, "extra.txt"), Path.Combine(srcB, "newdir") },
            BaseDirectory = srcB,
            Level = 1,
        });
        Harness.CheckEqual("追加: 返回文件数", 2, add.Files);

        ListResult afterAdd = await svc.ListAsync(archive, null);
        Harness.CheckEqual("追加: 归档内文件数", 3, afterAdd.FileCount);
        Harness.Check("追加: 条目已进入归档",
            afterAdd.Entries.Any(e => e.Path == "extra.txt") && afterAdd.Entries.Any(e => e.Path == "newdir/新文件.txt"),
            string.Join(" | ", afterAdd.Entries.Select(e => e.Path)));

        await Harness.CheckThrowsAsync<ArchiveException>("追加: 归档不存在时抛中文异常",
            () => svc.AddAsync(new AddOptions
            {
                ArchivePath = Path.Combine(work, "nope.7z"),
                Sources = new[] { Path.Combine(srcB, "extra.txt") },
                BaseDirectory = srcB,
            }), "不存在");

        DeleteResult del = await svc.DeleteAsync(archive, new[] { "extra.txt" });
        Harness.CheckEqual("删除: Removed 计数", 1, del.Removed);
        ListResult afterDelete = await svc.ListAsync(archive, null);
        Harness.Check("删除: 条目已消失", afterDelete.Entries.All(e => e.Path != "extra.txt"));
        Harness.CheckEqual("删除: 其余条目保留", 2, afterDelete.FileCount);

        DeleteResult missing = await svc.DeleteAsync(archive, new[] { "not-there.txt" });
        Harness.CheckEqual("删除: 不存在条目静默跳过", 0, missing.Removed);

        DeleteResult none = await svc.DeleteAsync(archive, Array.Empty<string>());
        Harness.CheckEqual("删除: 空列表返回 0", 0, none.Removed);

        // 删除目录前缀应连带删除其下条目
        DeleteResult delDir = await svc.DeleteAsync(archive, new[] { "newdir" });
        Harness.Check("删除: 目录前缀连带子项", delDir.Removed >= 2, $"Removed={delDir.Removed}");
        ListResult final = await svc.ListAsync(archive, null);
        Harness.Check("删除: 目录下条目全部消失", final.Entries.All(e => !e.Path.StartsWith("newdir")));
        Harness.Check("删除: a.txt 仍在", final.Entries.Any(e => e.Path == "a.txt"));

        Harness.TryDelete(work);
    }

    public static async Task EncryptedAddDeleteAsync(ArchiveService svc)
    {
        Harness.Section("加密归档：追加/删除透传密码，且不破坏文件名加密");
        const string password = "P@ss 中文 42";
        string work = Harness.NewDir("mutenc");
        string src = Path.Combine(work, "src");
        Harness.WriteText(Path.Combine(src, "a.txt"), "加密内容 A");

        string archive = Path.Combine(work, "enc.7z");
        await svc.CreateAsync(new CreateOptions
        {
            ArchivePath = archive,
            Format = ArchiveFormat.SevenZip,
            Level = 1,
            Password = password,
            EncryptHeaders = true,
            Sources = new[] { Path.Combine(src, "a.txt") },
            BaseDirectory = src,
        });

        await Harness.CheckThrowsAsync<ArchiveException>("加密: 无密码列表被拒绝",
            () => svc.ListAsync(archive, null), "密码");
        ListResult withPw = await svc.ListAsync(archive, password);
        Harness.CheckEqual("加密: 正确密码可列表", 1, withPw.FileCount);
        Harness.Check("加密: 列表标记为加密", withPw.IsEncrypted);

        Harness.WriteText(Path.Combine(src, "b.txt"), "加密内容 B");
        await Harness.CheckThrowsAsync<ArchiveException>("加密: 追加时密码错误被拒绝",
            () => svc.AddAsync(new AddOptions
            {
                ArchivePath = archive,
                Sources = new[] { Path.Combine(src, "b.txt") },
                BaseDirectory = src,
                Password = "wrong-password",
            }), "密码");

        ExtractResult add = await svc.AddAsync(new AddOptions
        {
            ArchivePath = archive,
            Sources = new[] { Path.Combine(src, "b.txt") },
            BaseDirectory = src,
            Password = password,
            Level = 1,
        });
        Harness.CheckEqual("加密: 追加成功", 1, add.Files);

        ListResult afterAdd = await svc.ListAsync(archive, password);
        Harness.CheckEqual("加密: 追加后文件数", 2, afterAdd.FileCount);

        // 文件名加密必须保留：无密码仍然打不开
        await Harness.CheckThrowsAsync<ArchiveException>("加密: 追加后仍保留文件名加密",
            () => svc.ListAsync(archive, null), "密码");

        // 无密码追加到文件名加密归档：必须快速失败（7z 不能停下等输入）
        Task noPwAdd = svc.AddAsync(new AddOptions
        {
            ArchivePath = archive,
            Sources = new[] { Path.Combine(src, "b.txt") },
            BaseDirectory = src,
        });
        try
        {
            await noPwAdd.WaitAsync(TimeSpan.FromSeconds(20));
            Harness.Check("加密: 无密码追加被拒绝（不挂起）", false, "没有抛异常");
        }
        catch (TimeoutException)
        {
            Harness.Check("加密: 无密码追加被拒绝（不挂起）", false, "超过 20 秒仍未返回（疑似停在密码输入）");
        }
        catch (ArchiveException)
        {
            Harness.Check("加密: 无密码追加被拒绝（不挂起）", true);
        }
        catch (Exception ex)
        {
            Harness.Check("加密: 无密码追加被拒绝（不挂起）", false, $"{ex.GetType().Name}: {ex.Message}");
        }

        await Harness.CheckThrowsAsync<ArchiveException>("加密: 删除时密码错误被拒绝",
            () => svc.DeleteAsync(archive, new[] { "b.txt" }, "wrong-password"), "密码");

        DeleteResult del = await svc.DeleteAsync(archive, new[] { "b.txt" }, password);
        Harness.CheckEqual("加密: 删除成功", 1, del.Removed);
        Harness.CheckEqual("加密: 删除后剩余文件数", 1, (await svc.ListAsync(archive, password)).FileCount);

        await Harness.CheckThrowsAsync<ArchiveException>("加密: 删除后仍保留文件名加密",
            () => svc.ListAsync(archive, null), "密码");

        // 正确密码解压，内容必须一致
        string dest = Path.Combine(work, "out");
        await svc.ExtractAsync(archive, new ExtractOptions { Destination = dest, Password = password });
        Harness.Check("加密: 正确密码解压内容一致",
            File.Exists(Path.Combine(dest, "a.txt")) &&
            File.ReadAllText(Path.Combine(dest, "a.txt")) == "加密内容 A");

        Harness.TryDelete(work);
    }

    public static async Task CreateOverwritesAsync(ArchiveService svc)
    {
        Harness.Section("CreateAsync 覆盖重建（不得变成追加）");
        string work = Harness.NewDir("over");
        string src = Path.Combine(work, "src");
        Harness.WriteText(Path.Combine(src, "a.txt"), "first");

        string archive = Path.Combine(work, "same.7z");
        await svc.CreateAsync(new CreateOptions
        {
            ArchivePath = archive,
            Format = ArchiveFormat.SevenZip,
            Level = 1,
            Sources = new[] { Path.Combine(src, "a.txt") },
            BaseDirectory = src,
        });
        Harness.CheckEqual("覆盖重建: 首次创建", 1, (await svc.ListAsync(archive, null)).FileCount);

        Harness.WriteText(Path.Combine(src, "b.txt"), "second");
        await svc.CreateAsync(new CreateOptions
        {
            ArchivePath = archive,
            Format = ArchiveFormat.SevenZip,
            Level = 1,
            Sources = new[] { Path.Combine(src, "b.txt") },
            BaseDirectory = src,
        });

        ListResult list = await svc.ListAsync(archive, null);
        Harness.CheckEqual("覆盖重建: 只有新内容", 1, list.FileCount);
        Harness.Check("覆盖重建: 旧条目 a.txt 不存在", list.Entries.All(e => e.Path != "a.txt"),
            string.Join(" | ", list.Entries.Select(e => e.Path)));
        Harness.Check("覆盖重建: 新条目 b.txt 存在", list.Entries.Any(e => e.Path == "b.txt"));

        // 不同格式覆盖同名目标（7z → zip）也应重建而不是追加
        string zipTarget = Path.Combine(work, "same.zip");
        await svc.CreateAsync(new CreateOptions
        {
            ArchivePath = zipTarget,
            Format = ArchiveFormat.Zip,
            Level = 1,
            Sources = new[] { Path.Combine(src, "a.txt") },
            BaseDirectory = src,
        });
        Harness.CheckEqual("覆盖重建: zip 生成", ArchiveFormat.Zip, svc.DetectFormat(zipTarget));
        Harness.CheckEqual("覆盖重建: zip 内容", 1, (await svc.ListAsync(zipTarget, null)).FileCount);

        Harness.TryDelete(work);
    }

    public static async Task InvalidOptionsAsync(ArchiveService svc)
    {
        Harness.Section("非法参数 → 中文 ArchiveException");
        string work = Harness.NewDir("invalid");
        string src = Path.Combine(work, "src");
        Harness.WriteText(Path.Combine(src, "a.txt"), "x");

        await Harness.CheckThrowsAsync<ArchiveException>("参数: 不允许创建 RAR",
            () => svc.CreateAsync(new CreateOptions
            {
                ArchivePath = Path.Combine(work, "x.rar"),
                Format = ArchiveFormat.Rar,
                Sources = new[] { Path.Combine(src, "a.txt") },
            }), "不支持");

        await Harness.CheckThrowsAsync<ArchiveException>("参数: 源文件不存在",
            () => svc.CreateAsync(new CreateOptions
            {
                ArchivePath = Path.Combine(work, "x.7z"),
                Format = ArchiveFormat.SevenZip,
                Sources = new[] { Path.Combine(work, "missing.bin") },
            }), "不存在");

        await Harness.CheckThrowsAsync<ArchiveException>("参数: gz 不能压缩目录",
            () => svc.CreateAsync(new CreateOptions
            {
                ArchivePath = Path.Combine(work, "x.gz"),
                Format = ArchiveFormat.GZip,
                Sources = new[] { src },
            }), "单个文件");

        await Harness.CheckThrowsAsync<ArchiveException>("参数: 没有源文件",
            () => svc.CreateAsync(new CreateOptions
            {
                ArchivePath = Path.Combine(work, "x.7z"),
                Format = ArchiveFormat.SevenZip,
                Sources = Array.Empty<string>(),
            }), "没有");

        Harness.TryDelete(work);
    }
}
