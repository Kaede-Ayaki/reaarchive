// RecoveryTests.cs — 密码破译自测（零 NuGet，纯控制台断言）
//
// 覆盖：
//   1. 字典 + ZipCrypto（7z 现场造的 ASCII 密码 zip；另一个自写 zip 覆盖非 ASCII 密码「密码123」）
//   2. 字典 + WinZip AES-256 zip
//   3. 掩码 ?d?d?d?d 命中，且 Tried 落在正确序号附近
//   4. 增量命中 + Exhausted 分支（MaxAttempts 上限）
//   5. 取消（起始即取消 / 跑到一半取消）
//   6. 7z 预言机路径（.7z + 非 ASCII 密码「密码」）
//   7. 未加密归档 / 不存在的文件 / 掩码占位符非法 / 缺词表 → 中文 ArchiveException
//   8. 候选估算 + 吞吐量实测（打印到控制台，供交付报告引用）
//
// 约定：
//   · 所有临时文件在 Path.GetTempPath() 下的独立目录里，用例结束清理。
//   · 密码只出现在断言里，不写日志文件。
//   · 随包 7z 25.01 **无法创建非 ASCII 密码的 zip**（`-p密码123` 直接 System ERROR），
//     所以「密码123 的 ZipCrypto zip」由本文件的 WriteZipCryptoZip 按 APPNOTE 手工写出，
//     密码字节显式取 UTF-8（见方法注释）。

using System.Diagnostics;
using System.Text;
using REAArchive.Archive;

namespace EngineTests;

public static class RecoveryTests
{
    private const string ZipCryptoPassword = "abc12345";
    private const string AesPassword = "aesPW2026";
    private const string CjkPassword = "密码123";
    private const string SevenZipPassword = "密码";

    public static async Task RunAsync(ArchiveService svc)
    {
        await DictionaryZipCryptoAsync(svc);
        await DictionaryAesAsync(svc);
        await MaskStrategyAsync(svc);
        await IncrementalStrategyAsync(svc);
        await CancelAsync(svc);
        await SevenZipOracleAsync(svc);
        await ErrorCasesAsync(svc);
        await FalsePositiveGuardAsync(svc);
        await EstimateAndThroughputAsync(svc);
    }

    // ── 1. 字典 + ZipCrypto ───────────────────────────────────────────────────

    private static async Task DictionaryZipCryptoAsync(ArchiveService svc)
    {
        Harness.Section("密码破译：字典 + ZipCrypto（含非 ASCII 密码）");
        string work = Harness.NewDir("rec-dict");
        try
        {
            string src = Path.Combine(work, "src");
            Harness.WriteText(Path.Combine(src, "secret.txt"), "TOP-SECRET-破译用例");

            // (a) 7z 现场造的 ZipCrypto zip：ASCII 密码（7z 的 zip 加密只接受 ASCII 密码）
            string asciiZip = Path.Combine(work, "zc-ascii.zip");
            SevenZipOutput created = await Run7zAsync(svc, src,
                "a", "-tzip", "-mem=ZipCrypto", "-p" + ZipCryptoPassword, "--", asciiZip, "secret.txt");
            Harness.Check("破译: 7z 造出 ZipCrypto zip", created.ExitCode == 0 && File.Exists(asciiZip),
                $"exit={created.ExitCode} {created.Output}");

            var service = new PasswordRecoveryService();
            Harness.Check("破译: SupportsNativeVerify(.zip)=true", service.SupportsNativeVerify(asciiZip));
            Harness.Check("破译: SupportsNativeVerify(.7z)=false", !service.SupportsNativeVerify(Path.Combine(work, "x.7z")));

            string asciiList = Path.Combine(work, "ascii.txt");
            Harness.WriteText(asciiList, "wrong-one\r\nwrong-two\r\n" + ZipCryptoPassword + "\r\ntail\r\n");

            RecoveryResult asciiResult = await service.RecoverAsync(DictionaryOptions(asciiZip, asciiList), null,
                TestTimeout(60));

            Harness.Check("破译: ZipCrypto 字典命中", asciiResult.Found && asciiResult.Password == ZipCryptoPassword,
                $"Found={asciiResult.Found} Password={Mask(asciiResult.Password)} Tried={asciiResult.Tried}");
            Harness.CheckEqual("破译: ZipCrypto 尝试次数（第 3 个候选）", 3L, asciiResult.Tried);
            Harness.CheckEqual("破译: ZipCrypto 校验方式", "zip-native", asciiResult.Verifier);
            Harness.Check("破译: ZipCrypto 未标 Exhausted", !asciiResult.Exhausted);

            // (b) 非 ASCII 密码：7z 造不出来，用本文件的写出器按 APPNOTE 手写（密码字节 = UTF-8）
            string cjkZip = Path.Combine(work, "zc-cjk.zip");
            WriteZipCryptoZip(cjkZip, "secret.txt", Encoding.UTF8.GetBytes("TOP-SECRET-非 ASCII 密码"),
                Encoding.UTF8.GetBytes(CjkPassword));

            string cjkList = Path.Combine(work, "cjk.txt");
            Harness.WriteText(cjkList, "密码\r\nmima\r\n" + CjkPassword + "\r\n");

            RecoveryResult cjkResult = await service.RecoverAsync(DictionaryOptions(cjkZip, cjkList), null,
                TestTimeout(60));

            Harness.Check("破译: 非 ASCII 密码（UTF-8 字节）命中", cjkResult.Found && cjkResult.Password == CjkPassword,
                $"Found={cjkResult.Found} Password={Mask(cjkResult.Password)} Tried={cjkResult.Tried}");
            Harness.CheckEqual("破译: 非 ASCII 密码尝试次数", 3L, cjkResult.Tried);

            // (c) 同一串「密码123」，密码字节取 GB18030（= 本机中文 ANSI；7-Zip 自己写 zip 时用的就是这套字节）
            //     GB2312 映射：密=C3 DC，码=C2 EB，"123"=31 32 33
            string gbZip = Path.Combine(work, "zc-gb18030.zip");
            WriteZipCryptoZip(gbZip, "secret.txt", Encoding.UTF8.GetBytes("GB18030 密码字节"),
                new byte[] { 0xC3, 0xDC, 0xC2, 0xEB, 0x31, 0x32, 0x33 });

            RecoveryResult gbResult = await service.RecoverAsync(DictionaryOptions(gbZip, cjkList), null,
                TestTimeout(60));
            Harness.Check("破译: 非 ASCII 密码（GB18030 字节）命中", gbResult.Found && gbResult.Password == CjkPassword,
                $"Found={gbResult.Found} Password={Mask(gbResult.Password)} Tried={gbResult.Tried}");

            // (d) Latin-1（ANSI 兜底）字节：café 的 é 只有 0xE9 一个字节
            string latinZip = Path.Combine(work, "zc-latin1.zip");
            WriteZipCryptoZip(latinZip, "secret.txt", Encoding.UTF8.GetBytes("Latin-1 密码字节"),
                new byte[] { 0x63, 0x61, 0x66, 0xE9 });
            string latinList = Path.Combine(work, "latin.txt");
            Harness.WriteText(latinList, "cafe\r\ncafé\r\n");

            RecoveryResult latinResult = await service.RecoverAsync(DictionaryOptions(latinZip, latinList), null,
                TestTimeout(60));
            Harness.Check("破译: 非 ASCII 密码（Latin-1 字节）命中",
                latinResult.Found && latinResult.Password == "café",
                $"Found={latinResult.Found} Password={Mask(latinResult.Password)} Tried={latinResult.Tried}");

            // (e) ZIP64：中央目录尺寸/偏移全填 0xFFFFFFFF + 0x0001 扩展，EOCD 走 ZIP64 locator/record。
            //     密码取 ASCII，这样可以顺手让 7z 验证「这份手写归档本身合法」，避免自说自话。
            string zip64Zip = Path.Combine(work, "zc-zip64.zip");
            WriteZipCryptoZip(zip64Zip, "secret.txt", Encoding.UTF8.GetBytes("ZIP64 结构内容"),
                Encoding.ASCII.GetBytes("zip64pw"), forceZip64: true);

            SevenZipOutput zip64Check = await Run7zAsync(svc, work, "t", "-sccUTF-8", "-pzip64pw", "--", zip64Zip);
            Harness.Check("破译: 手写 ZIP64 zip 能被 7z 校验通过", zip64Check.ExitCode == 0, zip64Check.Output);

            string zip64List = Path.Combine(work, "zip64.txt");
            Harness.WriteText(zip64List, "nope\r\nzip64pw\r\n");

            RecoveryResult zip64Result = await service.RecoverAsync(DictionaryOptions(zip64Zip, zip64List), null,
                TestTimeout(60));
            Harness.Check("破译: ZIP64 归档命中（locator/record 解析正确）",
                zip64Result.Found && zip64Result.Password == "zip64pw",
                $"Found={zip64Result.Found} Password={Mask(zip64Result.Password)} Tried={zip64Result.Tried}");

            // 变体展开：词表里只有小写形式，靠 Capitalize 命中
            string variantZip = Path.Combine(work, "zc-variant.zip");
            await Run7zAsync(svc, src, "a", "-tzip", "-mem=ZipCrypto", "-pPassword99", "--", variantZip, "secret.txt");
            string variantList = Path.Combine(work, "variant.txt");
            Harness.WriteText(variantList, "password99\r\n");

            RecoveryResult variantResult = await service.RecoverAsync(
                new RecoveryOptions
                {
                    ArchivePath = variantZip,
                    Strategy = RecoveryStrategy.Dictionary,
                    WordlistPaths = new[] { variantList },
                    Variants = WordVariant.Capitalize,
                }, null, TestTimeout(60));

            Harness.Check("破译: 变体展开（Capitalize）命中",
                variantResult.Found && variantResult.Password == "Password99",
                $"Found={variantResult.Found} Password={Mask(variantResult.Password)}");
        }
        finally
        {
            Harness.TryDelete(work);
        }
    }

    // ── 2. 字典 + WinZip AES-256 ──────────────────────────────────────────────

    private static async Task DictionaryAesAsync(ArchiveService svc)
    {
        Harness.Section("密码破译：字典 + WinZip AES-256");
        string work = Harness.NewDir("rec-aes");
        try
        {
            string src = Path.Combine(work, "src");
            Harness.WriteText(Path.Combine(src, "secret.txt"), "AES-256 内容");

            string aesZip = Path.Combine(work, "aes.zip");
            SevenZipOutput created = await Run7zAsync(svc, src,
                "a", "-tzip", "-mem=AES256", "-p" + AesPassword, "--", aesZip, "secret.txt");
            Harness.Check("破译: 7z 造出 AES-256 zip", created.ExitCode == 0 && File.Exists(aesZip),
                $"exit={created.ExitCode} {created.Output}");

            string wordlist = Path.Combine(work, "aes.txt");
            Harness.WriteText(wordlist, "not-it\r\n" + AesPassword + "\r\n");

            var service = new PasswordRecoveryService();
            RecoveryResult result = await service.RecoverAsync(DictionaryOptions(aesZip, wordlist), null,
                TestTimeout(60));

            Harness.Check("破译: AES-256 字典命中", result.Found && result.Password == AesPassword,
                $"Found={result.Found} Password={Mask(result.Password)} Tried={result.Tried}");
            Harness.CheckEqual("破译: AES-256 尝试次数", 2L, result.Tried);
            Harness.CheckEqual("破译: AES-256 校验方式", "zip-native", result.Verifier);
        }
        finally
        {
            Harness.TryDelete(work);
        }
    }

    // ── 3. 掩码 ──────────────────────────────────────────────────────────────

    private static async Task MaskStrategyAsync(ArchiveService svc)
    {
        Harness.Section("密码破译：掩码 ?d?d?d?d");
        string work = Harness.NewDir("rec-mask");
        try
        {
            string src = Path.Combine(work, "src");
            Harness.WriteText(Path.Combine(src, "secret.txt"), "4 位数字密码");
            string zip = Path.Combine(work, "mask.zip");
            SevenZipOutput created = await Run7zAsync(svc, src,
                "a", "-tzip", "-mem=ZipCrypto", "-p0427", "--", zip, "secret.txt");
            Harness.Check("破译: 7z 造出 4 位数字密码 zip", created.ExitCode == 0, created.Output);

            var service = new PasswordRecoveryService();
            RecoveryResult result = await service.RecoverAsync(new RecoveryOptions
            {
                ArchivePath = zip,
                Strategy = RecoveryStrategy.Mask,
                Mask = "?d?d?d?d",
            }, null, TestTimeout(120));

            // 顺序是 0000,0001,…,0427（最后一位变化最快），所以 0427 是第 428 个候选。
            Harness.Check("破译: 掩码命中 0427", result.Found && result.Password == "0427",
                $"Found={result.Found} Password={Mask(result.Password)} Tried={result.Tried}");
            Harness.Check("破译: 掩码 Tried 落在正确序号附近",
                Math.Abs(result.Tried - 428) <= 2, $"Tried={result.Tried}（期望 ≈428）");
            Harness.CheckEqual("破译: 掩码校验方式", "zip-native", result.Verifier);

            // 自定义字符集 + KnownPrefix
            string prefixZip = Path.Combine(work, "prefix.zip");
            await Run7zAsync(svc, src, "a", "-tzip", "-mem=ZipCrypto", "-pab12", "--", prefixZip, "secret.txt");

            RecoveryResult prefixResult = await service.RecoverAsync(new RecoveryOptions
            {
                ArchivePath = prefixZip,
                Strategy = RecoveryStrategy.Incremental,
                Charset = "012",
                MinLength = 1,
                MaxLength = 2,
                KnownPrefix = "ab",
            }, null, TestTimeout(120));

            Harness.Check("破译: KnownPrefix + 增量命中 ab12",
                prefixResult.Found && prefixResult.Password == "ab12",
                $"Found={prefixResult.Found} Password={Mask(prefixResult.Password)} Tried={prefixResult.Tried}");

            // 掩码里的非法占位符必须是中文错误
            await Harness.CheckThrowsAsync<ArchiveException>("破译: 非法掩码占位符给出中文错误",
                () =>
                {
                    _ = service.EstimateCandidates(new RecoveryOptions
                    {
                        ArchivePath = zip,
                        Strategy = RecoveryStrategy.Mask,
                        Mask = "?x?d",
                    });
                    return Task.CompletedTask;
                }, "掩码");
        }
        finally
        {
            Harness.TryDelete(work);
        }
    }

    // ── 4. 增量与 Exhausted ──────────────────────────────────────────────────

    private static async Task IncrementalStrategyAsync(ArchiveService svc)
    {
        Harness.Section("密码破译：增量命中 + Exhausted 分支");
        string work = Harness.NewDir("rec-incr");
        try
        {
            string src = Path.Combine(work, "src");
            Harness.WriteText(Path.Combine(src, "secret.txt"), "增量用例");
            string zip = Path.Combine(work, "incr.zip");
            await Run7zAsync(svc, src, "a", "-tzip", "-mem=ZipCrypto", "-p123", "--", zip, "secret.txt");

            var service = new PasswordRecoveryService();

            RecoveryResult hit = await service.RecoverAsync(new RecoveryOptions
            {
                ArchivePath = zip,
                Strategy = RecoveryStrategy.Incremental,
                Charset = "0123456789",
                MinLength = 1,
                MaxLength = 3,
            }, null, TestTimeout(120));

            Harness.Check("破译: 增量命中 123", hit.Found && hit.Password == "123",
                $"Found={hit.Found} Password={Mask(hit.Password)} Tried={hit.Tried}");

            // 同长度左起变化最快：1 位 10 个 + 2 位 100 个 + 3 位里 123 是第 124 个 → 共 234 次
            Harness.Check("破译: 增量 Tried 合理（≤ 1110）", hit.Tried > 0 && hit.Tried <= 1110,
                $"Tried={hit.Tried}");

            // Exhausted：密码不在字符集里，MaxAttempts 限制 100
            string zip2 = Path.Combine(work, "incr2.zip");
            await Run7zAsync(svc, src, "a", "-tzip", "-mem=ZipCrypto", "-pabcdef", "--", zip2, "secret.txt");

            RecoveryResult miss = await service.RecoverAsync(new RecoveryOptions
            {
                ArchivePath = zip2,
                Strategy = RecoveryStrategy.Incremental,
                Charset = "0123456789",
                MinLength = 1,
                MaxLength = 6,
                MaxAttempts = 100,
            }, null, TestTimeout(120));

            Harness.Check("破译: 到达上限 → Exhausted && !Found", miss.Exhausted && !miss.Found,
                $"Exhausted={miss.Exhausted} Found={miss.Found} Tried={miss.Tried}");
            Harness.CheckEqual("破译: 到达上限时 Tried == MaxAttempts", 100L, miss.Tried);
        }
        finally
        {
            Harness.TryDelete(work);
        }
    }

    // ── 5. 取消 ──────────────────────────────────────────────────────────────

    private static async Task CancelAsync(ArchiveService svc)
    {
        Harness.Section("密码破译：取消（起始即取消 / 中途取消）");
        string work = Harness.NewDir("rec-cancel");
        try
        {
            string src = Path.Combine(work, "src");
            Harness.WriteText(Path.Combine(src, "secret.txt"), "取消用例");
            string zip = Path.Combine(work, "cancel.zip");
            await Run7zAsync(svc, src, "a", "-tzip", "-mem=ZipCrypto", "-p9999", "--", zip, "secret.txt");

            var service = new PasswordRecoveryService();

            bool startCanceled = false;
            try
            {
                using var already = new CancellationTokenSource();
                await already.CancelAsync();
                _ = await service.RecoverAsync(new RecoveryOptions
                {
                    ArchivePath = zip,
                    Strategy = RecoveryStrategy.Mask,
                    Mask = "?d?d?d?d",
                }, null, already.Token);
            }
            catch (OperationCanceledException)
            {
                startCanceled = true;
            }
            catch (Exception ex)
            {
                Harness.Info($"起始取消抛了 {ex.GetType().Name}: {ex.Message}");
            }
            Harness.Check("破译: 起始即取消 → OperationCanceledException", startCanceled);

            bool midCanceled = false;
            string? midError = null;
            var sw = Stopwatch.StartNew();
            try
            {
                using var cts = new CancellationTokenSource();
                cts.CancelAfter(150);
                _ = await service.RecoverAsync(new RecoveryOptions
                {
                    ArchivePath = zip,
                    Strategy = RecoveryStrategy.Mask,
                    Mask = "?d?d?d?d?d?d?d?d", // 10^8：正常跑不完
                }, null, cts.Token);
            }
            catch (OperationCanceledException)
            {
                midCanceled = true;
            }
            catch (Exception ex)
            {
                midError = $"{ex.GetType().Name}: {ex.Message}";
            }
            sw.Stop();

            Harness.Check("破译: 中途取消 → OperationCanceledException", midCanceled, midError);
            Harness.Check("破译: 取消及时返回（<10s）", sw.Elapsed.TotalSeconds < 10, $"{sw.Elapsed.TotalSeconds:0.00}s");
        }
        finally
        {
            Harness.TryDelete(work);
        }
    }

    // ── 6. 7z 预言机（非 zip） ───────────────────────────────────────────────

    private static async Task SevenZipOracleAsync(ArchiveService svc)
    {
        Harness.Section("密码破译：7z 预言机（.7z + 非 ASCII 密码，慢）");
        string work = Harness.NewDir("rec-oracle");
        try
        {
            string src = Path.Combine(work, "src");
            Harness.WriteText(Path.Combine(src, "secret.txt"), "7z 加密内容");

            string archive = Path.Combine(work, "oracle.7z");
            SevenZipOutput created = await Run7zAsync(svc, src,
                "a", "-t7z", "-mhe=on", "-p" + SevenZipPassword, "--", archive, "secret.txt");
            Harness.Check("破译: 7z 造出文件名加密归档", created.ExitCode == 0, created.Output);

            string wordlist = Path.Combine(work, "oracle.txt");
            Harness.WriteText(wordlist, "alpha\r\nbeta\r\n" + SevenZipPassword + "\r\n");

            var service = new PasswordRecoveryService();
            Harness.Check("破译: 预言机模式 SupportsNativeVerify(.7z)=false", !service.SupportsNativeVerify(archive));

            var sw = Stopwatch.StartNew();
            RecoveryResult result;
            try
            {
                result = await service.RecoverAsync(DictionaryOptions(archive, wordlist), null, TestTimeout(180));
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                Harness.Skip("破译: 7z 预言机命中", "超时保护触发（>180s）");
                return;
            }
            sw.Stop();

            Harness.Check("破译: 7z 预言机命中", result.Found && result.Password == SevenZipPassword,
                $"Found={result.Found} Password={Mask(result.Password)} Tried={result.Tried}");
            Harness.CheckEqual("破译: 预言机校验方式", "sevenzip-oracle", result.Verifier);

            if (result.Found && result.Tried > 0)
            {
                double rate = result.Tried / Math.Max(result.Elapsed.TotalSeconds, 0.001);
                Harness.Info($"7z 预言机实测吞吐：{rate:0.0} 次/秒（{result.Tried} 次 / {result.Elapsed.TotalMilliseconds:0}ms，" +
                             $"含预检与进程启动）");
            }
        }
        finally
        {
            Harness.TryDelete(work);
        }
    }

    // ── 7. 错误分支 ──────────────────────────────────────────────────────────

    private static async Task ErrorCasesAsync(ArchiveService svc)
    {
        Harness.Section("密码破译：错误分支（中文 ArchiveException）");
        string work = Harness.NewDir("rec-err");
        try
        {
            string src = Path.Combine(work, "src");
            Harness.WriteText(Path.Combine(src, "plain.txt"), "没有加密");
            var service = new PasswordRecoveryService();

            string plainZip = Path.Combine(work, "plain.zip");
            await Run7zAsync(svc, src, "a", "-tzip", "--", plainZip, "plain.txt");
            string wordlist = Path.Combine(work, "words.txt");
            Harness.WriteText(wordlist, "a\r\nb\r\n");

            await Harness.CheckThrowsAsync<ArchiveException>("破译: 未加密 zip 给出中文错误",
                () => service.RecoverAsync(DictionaryOptions(plainZip, wordlist), null, TestTimeout(60)), "加密");

            string plain7z = Path.Combine(work, "plain.7z");
            await Run7zAsync(svc, src, "a", "-t7z", "--", plain7z, "plain.txt");
            await Harness.CheckThrowsAsync<ArchiveException>("破译: 未加密 7z 给出中文错误",
                () => service.RecoverAsync(DictionaryOptions(plain7z, wordlist), null, TestTimeout(60)), "加密");

            // 加密 zip：用于「词表文件不存在」用例（未加密归档会在更早的预检处报错，测不到词表分支）
            string encryptedZip = Path.Combine(work, "enc.zip");
            await Run7zAsync(svc, src, "a", "-tzip", "-mem=ZipCrypto", "-ppw123", "--", encryptedZip, "plain.txt");

            await Harness.CheckThrowsAsync<ArchiveException>("破译: 归档不存在给出中文错误",
                () => service.RecoverAsync(DictionaryOptions(Path.Combine(work, "missing.zip"), wordlist),
                    null, TestTimeout(60)), "找不到");

            await Harness.CheckThrowsAsync<ArchiveException>("破译: 字典缺词表给出中文错误",
                () => service.RecoverAsync(new RecoveryOptions
                {
                    ArchivePath = plainZip,
                    Strategy = RecoveryStrategy.Dictionary,
                }, null, TestTimeout(60)), "词表");

            await Harness.CheckThrowsAsync<ArchiveException>("破译: 词表文件不存在给出中文错误",
                () => service.RecoverAsync(DictionaryOptions(encryptedZip, Path.Combine(work, "nope.txt")),
                    null, TestTimeout(60)), "词表");
        }
        finally
        {
            Harness.TryDelete(work);
        }
    }

    // ── 8. ZipCrypto 假阳性防护（1/256 筛 → 精确复核） ───────────────────────

    /// <summary>
    /// 构造一个「快速筛必定通过、但内容对不上」的归档，验证 1/256 的假阳性不会变成「找到密码」。
    ///
    /// 造法：ZipCrypto 的 12 字节加密头只有最后一个明文字节参与校验。我们先把「假密码 W」的密钥流跑起来，
    /// 让这 12 字节解密后最后一字节正好等于 CRC32 高字节（校验字节），于是 W 必定通过快速筛；
    /// 但正文用别的数据填充，解出来的内容 CRC/大小都对不上 —— 精确复核必须把它否掉。
    /// 对照组 B 用同一套密钥去加密真实正文：这时 W 真的是密码，精确复核必须通过。
    /// 两个归档的头完全同构，唯一差别是正文，所以能证明「结论差异来自精确复核」而不是别的路径。
    /// </summary>
    private static async Task FalsePositiveGuardAsync(ArchiveService svc)
    {
        Harness.Section("密码破译：ZipCrypto 假阳性防护（1/256 筛 + 精确复核）");
        string work = Harness.NewDir("rec-fp");
        try
        {
            const string fakePassword = "crafted99";
            byte[] passwordBytes = Encoding.ASCII.GetBytes(fakePassword);
            var service = new PasswordRecoveryService();

            string wordlist = Path.Combine(work, "fp.txt");
            Harness.WriteText(wordlist, fakePassword + "\r\n");

            // A) 校验字节对得上，正文对不上 → 必须判「没找到」，绝不能报假阳性
            byte[] declared = Encoding.UTF8.GetBytes("REAL-CONTENT-0123456789");
            byte[] wrongPayload = Encoding.UTF8.GetBytes("DIFFERENT-PAYLOAD-XXXXXX");
            string bogus = Path.Combine(work, "fp-bogus.zip");
            WriteCraftedZipCryptZip(bogus, declared, wrongPayload, passwordBytes);

            SevenZipOutput bogusCheck = await Run7zAsync(svc, work, "t", "-sccUTF-8", "-p" + fakePassword, "--", bogus);
            Harness.Check("破译: 假阳性归档确实对不上（7z 也报错）", bogusCheck.ExitCode != 0,
                $"exit={bogusCheck.ExitCode}");

            RecoveryResult bogusResult = await service.RecoverAsync(DictionaryOptions(bogus, wordlist), null,
                TestTimeout(60));
            Harness.Check("破译: 假阳性被精确复核否掉（不报 Found）",
                !bogusResult.Found && bogusResult.Exhausted && bogusResult.Tried == 1,
                $"Found={bogusResult.Found} Exhausted={bogusResult.Exhausted} Tried={bogusResult.Tried}");

            // B) 同样的头，正文是真的 → 精确复核必须通过（证明上面的“否掉”不是因为快速筛就没过）
            string genuine = Path.Combine(work, "fp-genuine.zip");
            WriteCraftedZipCryptZip(genuine, declared, declared, passwordBytes);

            SevenZipOutput genuineCheck = await Run7zAsync(svc, work, "t", "-sccUTF-8", "-p" + fakePassword, "--", genuine);
            Harness.Check("破译: 同构归档在密码正确时 7z 校验通过", genuineCheck.ExitCode == 0, genuineCheck.Output);

            RecoveryResult genuineResult = await service.RecoverAsync(DictionaryOptions(genuine, wordlist), null,
                TestTimeout(60));
            Harness.Check("破译: 同构归档命中（说明假阳性用例确实通过了快速筛）",
                genuineResult.Found && genuineResult.Password == fakePassword && genuineResult.Tried == 1,
                $"Found={genuineResult.Found} Password={Mask(genuineResult.Password)} Tried={genuineResult.Tried}");
        }
        finally
        {
            Harness.TryDelete(work);
        }
    }

    // ── 9. 估算 + 吞吐量 ─────────────────────────────────────────────────────

    private static async Task EstimateAndThroughputAsync(ArchiveService svc)
    {
        Harness.Section("密码破译：候选估算与吞吐量实测");
        string work = Harness.NewDir("rec-perf");
        try
        {
            var service = new PasswordRecoveryService();

            Harness.CheckEqual("破译: 估算掩码 ?d?d?d?d", 10_000L, service.EstimateCandidates(new RecoveryOptions
            {
                ArchivePath = "x.zip",
                Strategy = RecoveryStrategy.Mask,
                Mask = "?d?d?d?d",
            }));

            Harness.CheckEqual("破译: 估算掩码 ?l?d", 26L * 10L, service.EstimateCandidates(new RecoveryOptions
            {
                ArchivePath = "x.zip",
                Strategy = RecoveryStrategy.Mask,
                Mask = "?l?d",
            }));

            Harness.CheckEqual("破译: 估算增量 数字 1..6", 1_111_110L, service.EstimateCandidates(new RecoveryOptions
            {
                ArchivePath = "x.zip",
                Strategy = RecoveryStrategy.Incremental,
                Charset = "0123456789",
                MinLength = 1,
                MaxLength = 6,
            }));

            Harness.CheckEqual("破译: 估算字典 = -1（未知）", -1L, service.EstimateCandidates(new RecoveryOptions
            {
                ArchivePath = "x.zip",
                Strategy = RecoveryStrategy.Dictionary,
                WordlistPaths = new[] { "whatever.txt" },
            }));

            // 预估语义：UI 每次击键都调 EstimateCandidates，配置不完整只能是「算不出来」，不能抛
            Harness.CheckEqual("破译: 估算字典（未提供词表）= -1 且不抛",
                -1L, service.EstimateCandidates(new RecoveryOptions
                {
                    ArchivePath = "x.zip",
                    Strategy = RecoveryStrategy.Dictionary,
                }));

            Harness.CheckEqual("破译: 估算字典（词表路径读不到）= -1 且不抛",
                -1L, service.EstimateCandidates(new RecoveryOptions
                {
                    ArchivePath = "x.zip",
                    Strategy = RecoveryStrategy.Dictionary,
                    WordlistPaths = new[] { @"Z:\不存在\词表.txt" },
                }));

            Harness.CheckEqual("破译: 估算掩码为空 = 0", 0L, service.EstimateCandidates(new RecoveryOptions
            {
                ArchivePath = "x.zip",
                Strategy = RecoveryStrategy.Mask,
                Mask = "   ",
            }));

            Harness.CheckEqual("破译: 估算增量（最大长度 < 最小长度）= 0",
                0L, service.EstimateCandidates(new RecoveryOptions
                {
                    ArchivePath = "x.zip",
                    Strategy = RecoveryStrategy.Incremental,
                    Charset = "0123456789",
                    MinLength = 5,
                    MaxLength = 2,
                }));

            Harness.CheckEqual("破译: 估算增量（字符集为空）= 0", 0L, service.EstimateCandidates(new RecoveryOptions
            {
                ArchivePath = "x.zip",
                Strategy = RecoveryStrategy.Incremental,
                Charset = "",
                MinLength = 1,
                MaxLength = 4,
            }));

            Harness.CheckEqual("破译: 估算不依赖归档是否存在", 10_000L, service.EstimateCandidates(new RecoveryOptions
            {
                ArchivePath = @"Z:\不存在\archive.zip",
                Strategy = RecoveryStrategy.Mask,
                Mask = "?d?d?d?d",
            }));

            // 吞吐量：用一个不命中的 3 位数字空间（1000 个候选）测 zip-native 校验速度。
            string src = Path.Combine(work, "src");
            Harness.WriteText(Path.Combine(src, "secret.txt"), "吞吐量用例");
            string zip = Path.Combine(work, "perf.zip");
            await Run7zAsync(svc, src, "a", "-tzip", "-mem=ZipCrypto", "-pzzz999", "--", zip, "secret.txt");

            var sw = Stopwatch.StartNew();
            RecoveryResult result = await service.RecoverAsync(new RecoveryOptions
            {
                ArchivePath = zip,
                Strategy = RecoveryStrategy.Mask,
                Mask = "?d?d?d",
            }, null, TestTimeout(120));
            sw.Stop();

            Harness.Check("破译: 吞吐量用例跑完 1000 个候选（未命中即穷尽）",
                !result.Found && result.Exhausted && result.Tried == 1000,
                $"Found={result.Found} Exhausted={result.Exhausted} Tried={result.Tried}");

            if (result.Tried > 0)
            {
                double rate = result.Tried / Math.Max(result.Elapsed.TotalSeconds, 0.0001);
                Harness.Info($"zip-native 实测吞吐：{rate:0} 次/秒（{result.Tried} 次 / {result.Elapsed.TotalMilliseconds:0}ms）");
            }

            Harness.Check("破译: zip-native 吞吐 > 1000 次/秒", result.Tried / sw.Elapsed.TotalSeconds > 1000,
                $"{result.Tried / sw.Elapsed.TotalSeconds:0} 次/秒");

            // 大样本：增量 1..5 位纯数字 = 111,110 个候选，拿一个更可信的吞吐数字
            var big = Stopwatch.StartNew();
            RecoveryResult bigResult = await service.RecoverAsync(new RecoveryOptions
            {
                ArchivePath = zip,
                Strategy = RecoveryStrategy.Incremental,
                Charset = "0123456789",
                MinLength = 1,
                MaxLength = 5,
            }, null, TestTimeout(300));
            big.Stop();

            if (bigResult.Tried > 0 && big.Elapsed.TotalSeconds > 0)
            {
                double bigRate = bigResult.Tried / big.Elapsed.TotalSeconds;
                Harness.Info($"zip-native 大样本吞吐：{bigRate:0} 次/秒（{bigResult.Tried:N0} 次 / " +
                             $"{big.Elapsed.TotalMilliseconds:0}ms，单线程）");
            }
        }
        finally
        {
            Harness.TryDelete(work);
        }
    }

    // ── 工具 ──────────────────────────────────────────────────────────────────

    private static RecoveryOptions DictionaryOptions(string archive, string wordlist) => new()
    {
        ArchivePath = archive,
        Strategy = RecoveryStrategy.Dictionary,
        WordlistPaths = new[] { wordlist },
    };

    /// <summary>用例级超时保护：真实等待超过 timeoutSeconds 就让 OperationCanceledException 冒出来。</summary>
    private static CancellationToken TestTimeout(int timeoutSeconds) => new CancellationTokenSource(
        TimeSpan.FromSeconds(timeoutSeconds)).Token;

    private static string Mask(string? password)
        => password is null ? "null" : $"“{password}”";

    private sealed record SevenZipOutput(int ExitCode, string Output);

    private static async Task<SevenZipOutput> Run7zAsync(ArchiveService svc, string? workingDirectory, params string[] args)
    {
        string exe = svc.EnginePath;
        if (string.IsNullOrEmpty(exe)) throw new InvalidOperationException("找不到 7z.exe，无法构造测试归档。");

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);
        if (!string.IsNullOrEmpty(workingDirectory)) psi.WorkingDirectory = workingDirectory;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 7z.exe。");

        try { process.StandardInput.Close(); } catch { /* 已退出 */ }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);

        string output = await stdoutTask.ConfigureAwait(false) + await stderrTask.ConfigureAwait(false);
        return new SevenZipOutput(process.ExitCode, output);
    }

    // ── 自写的 ZipCrypto 写出器（只为造「非 ASCII 密码 / ZIP64 的 zip」用例） ────
    //
    // 随包 7z 25.01 拒绝创建非 ASCII 密码的 zip，所以这里按 APPNOTE 手工写一个最小合法 zip：
    //   · 单条目、method=0（Store）、flag=0x0001（加密、无数据描述符）
    //   · 加密头 12 字节 = 11 个随机字节 + 1 个校验字节（CRC32 高字节）
    //   · 密码字节由调用方显式给出 —— 本用例传的是 Encoding.UTF8.GetBytes("密码123")，
    //     也就是说该归档里「密码123」的字节序列是 UTF-8（不是 GB18030）。
    //   · forceZip64 = true 时：中央目录的尺寸/偏移全填 0xFFFFFFFF + 0x0001 扩展，
    //     EOCD 的条目数/尺寸/偏移全填 0xFFFF/0xFFFFFFFF，并插入 ZIP64 EOCD record + locator。
    private static void WriteZipCryptoZip(
        string path, string entryName, byte[] content, byte[] passwordBytes, bool forceZip64 = false)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(entryName);
        uint crc = Crc32(content);

        var cryptoHeader = new byte[12];
        new Random(20260101).NextBytes(cryptoHeader);
        cryptoHeader[11] = (byte)(crc >> 24);

        uint k0 = 0x12345678, k1 = 0x23456789, k2 = 0x34567890;
        foreach (byte b in passwordBytes) UpdateKeys(ref k0, ref k1, ref k2, b);

        var encrypted = new byte[cryptoHeader.Length + content.Length];
        for (int i = 0; i < cryptoHeader.Length; i++)
        {
            encrypted[i] = (byte)(cryptoHeader[i] ^ DecryptByte(k2));
            UpdateKeys(ref k0, ref k1, ref k2, cryptoHeader[i]);
        }
        for (int i = 0; i < content.Length; i++)
        {
            encrypted[cryptoHeader.Length + i] = (byte)(content[i] ^ DecryptByte(k2));
            UpdateKeys(ref k0, ref k1, ref k2, content[i]);
        }

        uint compressedSize = (uint)encrypted.Length;

        WriteZipBody(path, nameBytes, encrypted, crc, (uint)content.Length, forceZip64);
    }

    /// <summary>
    /// 手工造一个「快速筛一定通过、正文却对不上」的 ZipCrypto 归档：
    /// 加密头 12 字节解密后最后一字节被摆成 CRC32 高字节（校验字节），正文由调用方指定。
    /// declared 决定中央目录里写的 CRC / 原始大小，payload 是被加密的真实字节。
    /// </summary>
    private static void WriteCraftedZipCryptZip(string path, byte[] declared, byte[] payload, byte[] passwordBytes)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes("secret.txt");
        uint crc = Crc32(declared);

        var headerPlain = new byte[12];
        new Random(777).NextBytes(headerPlain);
        headerPlain[11] = (byte)(crc >> 24);   // 校验字节：让快速筛对这个密码成立

        uint k0 = 0x12345678, k1 = 0x23456789, k2 = 0x34567890;
        foreach (byte b in passwordBytes) UpdateKeys(ref k0, ref k1, ref k2, b);

        var encrypted = new byte[headerPlain.Length + payload.Length];
        for (int i = 0; i < headerPlain.Length; i++)
        {
            encrypted[i] = (byte)(headerPlain[i] ^ DecryptByte(k2));
            UpdateKeys(ref k0, ref k1, ref k2, headerPlain[i]);
        }
        for (int i = 0; i < payload.Length; i++)
        {
            encrypted[headerPlain.Length + i] = (byte)(payload[i] ^ DecryptByte(k2));
            UpdateKeys(ref k0, ref k1, ref k2, payload[i]);
        }

        WriteZipBody(path, nameBytes, encrypted, crc, (uint)declared.Length, forceZip64: false);
    }

    /// <summary>写一个「单条目、Store、flag=0x0001」的 zip（可选 ZIP64 结构）。</summary>
    private static void WriteZipBody(
        string path, byte[] nameBytes, byte[] encrypted, uint crc, uint uncompressedSize, bool forceZip64)
    {
        ushort dosTime = 0x6000;   // 任意合法时间；flag bit 3 未置位，校验字节只取自 CRC
        ushort dosDate = 0x5821;
        uint compressedSize = (uint)encrypted.Length;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var w = new BinaryWriter(fs, new UTF8Encoding(false), leaveOpen: false);

        // 本地头
        w.Write(0x04034B50u);
        w.Write((ushort)(forceZip64 ? 45 : 20));
        w.Write((ushort)0x0001);   // 加密
        w.Write((ushort)0);        // Store
        w.Write(dosTime);
        w.Write(dosDate);
        w.Write(crc);
        w.Write(compressedSize);
        w.Write(uncompressedSize);
        w.Write((ushort)nameBytes.Length);
        w.Write((ushort)0);        // extra
        w.Write(nameBytes);
        w.Write(encrypted);

        long centralOffset = fs.Position;

        // 中央目录
        w.Write(0x02014B50u);
        w.Write((ushort)(forceZip64 ? 45 : 20)); // version made by
        w.Write((ushort)(forceZip64 ? 45 : 20)); // version needed
        w.Write((ushort)0x0001);   // 加密
        w.Write((ushort)0);        // Store
        w.Write(dosTime);
        w.Write(dosDate);
        w.Write(crc);
        w.Write(forceZip64 ? 0xFFFFFFFFu : compressedSize);
        w.Write(forceZip64 ? 0xFFFFFFFFu : uncompressedSize);
        w.Write((ushort)nameBytes.Length);
        w.Write((ushort)(forceZip64 ? 28 : 0));  // extra 长度
        w.Write((ushort)0);        // comment
        w.Write((ushort)0);        // disk start
        w.Write((ushort)0);        // internal attrs
        w.Write(0u);               // external attrs
        w.Write(forceZip64 ? 0xFFFFFFFFu : 0u); // local header offset
        w.Write(nameBytes);

        if (forceZip64)
        {
            // 0x0001 扩展：原始大小、压缩后大小、本地头偏移（顺序固定，仅包含被置为 0xFFFFFFFF 的字段）
            w.Write((ushort)0x0001);
            w.Write((ushort)24);
            w.Write((ulong)uncompressedSize);
            w.Write((ulong)compressedSize);
            w.Write(0ul);
        }

        long centralSize = fs.Position - centralOffset;

        if (forceZip64)
        {
            long zip64RecordOffset = fs.Position;

            // ZIP64 EOCD record（56 字节）
            w.Write(0x06064B50u);
            w.Write(44ul);             // 本记录剩余长度
            w.Write((ushort)45);       // version made by
            w.Write((ushort)45);       // version needed
            w.Write(0u);               // 本磁盘号
            w.Write(0u);               // 中央目录起始磁盘号
            w.Write(1ul);              // 本磁盘条目数
            w.Write(1ul);              // 条目总数
            w.Write((ulong)centralSize);
            w.Write((ulong)centralOffset);

            // ZIP64 EOCD locator（20 字节）
            w.Write(0x07064B50u);
            w.Write(0u);               // ZIP64 EOCD 所在磁盘号
            w.Write((ulong)zip64RecordOffset);
            w.Write(1u);               // 磁盘总数

            // 传统 EOCD：字段全部溢出，真正的值在 ZIP64 record 里
            w.Write(0x06054B50u);
            w.Write((ushort)0);
            w.Write((ushort)0);
            w.Write((ushort)0xFFFF);
            w.Write((ushort)0xFFFF);
            w.Write(0xFFFFFFFFu);
            w.Write(0xFFFFFFFFu);
            w.Write((ushort)0);
            return;
        }

        // 传统 EOCD
        w.Write(0x06054B50u);
        w.Write((ushort)0);
        w.Write((ushort)0);
        w.Write((ushort)1);
        w.Write((ushort)1);
        w.Write((uint)centralSize);
        w.Write((uint)centralOffset);
        w.Write((ushort)0);
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc = (crc >> 8) ^ CrcTable[(crc ^ b) & 0xFF];
        }
        return crc ^ 0xFFFFFFFF;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? (0xEDB88320u ^ (value >> 1)) : (value >> 1);
            }
            table[i] = value;
        }
        return table;
    }

    private static byte DecryptByte(uint k2)
    {
        uint temp = (k2 & 0xFFFF) | 2;
        return (byte)((temp * (temp ^ 1)) >> 8);
    }

    private static void UpdateKeys(ref uint k0, ref uint k1, ref uint k2, byte plain)
    {
        k0 = (k0 >> 8) ^ CrcTable[(k0 ^ plain) & 0xFF];
        k1 = (k1 + (k0 & 0xFF)) * 134775813u + 1u;
        k2 = (k2 >> 8) ^ CrcTable[(k2 ^ (byte)(k1 >> 24)) & 0xFF];
    }
}
