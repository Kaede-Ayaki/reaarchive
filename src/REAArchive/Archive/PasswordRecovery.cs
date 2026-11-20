// PasswordRecovery.cs — 压缩包密码破译（password recovery）编排层
//
// 分层：
//   PasswordCandidates       候选生成（惰性）
//   ZipPasswordVerifier      zip 的进程内校验器（ZipCrypto / WinZip AES，微秒级）
//   PasswordRecoveryService  编排：选校验器 → 遍历候选 → 节流进度 → 取消 / 上限 / 结果
//
// 校验器选择（性能是核心）：
//   · .zip（含 .zipx/.jar/.apk/.cbz/.epub）→ 进程内 ZipPasswordVerifier，不启子进程；
//     唯一例外是「加密方式不受支持」（如 PKWARE 强加密）或「压缩方法无法精确复核」时才回落到 7z 预言机。
//   · 其它格式（7z/rar/...）→ `7z t -p<candidate> -- <archive>` 预言机，一个候选一个进程，严格串行等待。
//
// 纪律：
//   · 密码只出现在 RecoveryResult.Password 里，绝不写日志 / 文件 / 进度对象（进度里的 CurrentCandidate
//     是正在尝试的候选，属于正常 UI 展示；超过 64 字符会被截断，避免 UI 持有超长字符串）。
//   · 面向用户的失败一律中文 ArchiveException；取消一律 OperationCanceledException。
//   · 构造函数必须廉价：不探测引擎、不起子进程、不读文件（7z 定位推迟到真正需要时，见 _engine 的 Lazy）。
//   · EstimateCandidates / SupportsNativeVerify 是纯计算（UI 每次击键都会调用），不读归档、不解析 zip。

using System.Diagnostics;

namespace REAArchive.Archive;

public enum RecoveryStrategy
{
    Dictionary = 0,
    Mask = 1,
    Incremental = 2,
}

/// <summary>字典变体规则位。None=原样。</summary>
[Flags]
public enum WordVariant
{
    None = 0,
    Capitalize = 1,      // password -> Password
    Upper = 2,           // PASSWORD
    AppendDigits = 4,    // password0 .. password9
    AppendYear = 8,      // password1990..password2030 常见年份
    PrependDigit = 16,   // 0password .. 9password
}

public sealed record RecoveryOptions
{
    public required string ArchivePath { get; init; }
    public RecoveryStrategy Strategy { get; init; } = RecoveryStrategy.Dictionary;
    /// <summary>字典策略：词表文件（UTF-8，失败回退 GB18030）。</summary>
    public IReadOnlyList<string>? WordlistPaths { get; init; }
    public WordVariant Variants { get; init; } = WordVariant.None;
    /// <summary>掩码策略：?l ?u ?d ?s ?a ?1..?4 ?b；其余字符按字面量。</summary>
    public string? Mask { get; init; }
    /// <summary>?1..?4 自定义字符集，索引 0..3。</summary>
    public IReadOnlyList<string>? CustomSets { get; init; }
    /// <summary>增量策略：字符集。</summary>
    public string Charset { get; init; } = "0123456789";
    public int MinLength { get; init; } = 1;
    public int MaxLength { get; init; } = 6;
    /// <summary>已确定的明文前缀（拼在每个候选之前）。</summary>
    public string? KnownPrefix { get; init; }
    /// <summary>最多尝试次数，0 = 不限制。</summary>
    public long MaxAttempts { get; init; }
}

public sealed record RecoveryProgress
{
    public long Tried { get; init; }
    public double AttemptsPerSecond { get; init; }
    public string? CurrentCandidate { get; init; }
    /// <summary>0~100；无法估算（字典/未知总数）时为 -1。</summary>
    public double Percent { get; init; } = -1;
    public TimeSpan? Eta { get; init; }
    public long TotalCandidates { get; init; } = -1;
    public string Phase { get; init; } = "";
}

public sealed record RecoveryResult
{
    public bool Found { get; init; }
    public string? Password { get; init; }
    public long Tried { get; init; }
    public TimeSpan Elapsed { get; init; }
    /// <summary>用来验证的方式：zip-native / sevenzip-oracle。</summary>
    public string Verifier { get; init; } = "";
    /// <summary>被中途取消或到达尝试上限而结束（Found=false）。</summary>
    public bool Exhausted { get; init; }
}

public interface IPasswordRecoveryService
{
    /// <summary>该归档能否用进程内快速校验器（zip）。</summary>
    bool SupportsNativeVerify(string archivePath);
    /// <summary>估算候选总数（-1 = 未知）。</summary>
    long EstimateCandidates(RecoveryOptions options);
    Task<RecoveryResult> RecoverAsync(RecoveryOptions options,
        IProgress<RecoveryProgress>? progress = null, CancellationToken ct = default);
}

public sealed class PasswordRecoveryService : IPasswordRecoveryService
{
    private const string VerifierNative = "zip-native";
    private const string VerifierOracle = "sevenzip-oracle";

    /// <summary>进度里 CurrentCandidate 的最大长度（UI 会高频刷新，别把长候选整串塞进去）。</summary>
    private const int MaxCandidateCharsInProgress = 64;

    /// <summary>单个 7z 子进程的超时（大归档 `t` 可能很久；超时给中文错误而不是永久卡住）。</summary>
    private const int OracleTimeoutMs = 180_000;

    /// <summary>扩展名 → 可能是 zip（只做字符串判断，不读文件：UI 每次击键都调 SupportsNativeVerify）。</summary>
    private static readonly HashSet<string> ZipExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".zipx", ".jar", ".apk", ".cbz", ".epub",
    };

    private readonly string? _sevenZipPath;

    /// <summary>
    /// 7z 引擎延迟解析：构造函数必须廉价（UI 会缓存实例反复用），
    /// 真正需要子进程时才去定位 Resources\7z\7z.exe。
    /// </summary>
    private readonly Lazy<SevenZipEngine> _engine;

    public PasswordRecoveryService()
        : this(null)
    {
    }

    /// <summary>可选：注入 7z.exe 路径；null = 用 EngineProvisioner 解析的随包引擎。</summary>
    public PasswordRecoveryService(string? sevenZipPath)
    {
        _sevenZipPath = string.IsNullOrWhiteSpace(sevenZipPath) ? null : sevenZipPath;
        _engine = new Lazy<SevenZipEngine>(() => new SevenZipEngine(_sevenZipPath), isThreadSafe: true);
    }

    private SevenZipEngine Engine => _engine.Value;

    // ── 公开查询（纯计算） ───────────────────────────────────────────────────

    /// <summary>
    /// 只看扩展名判断「可能是 zip」。真正的结构判断发生在 RecoverAsync 里（那时才读文件）；
    /// 这里读文件会让 UI 的每次击键都碰磁盘，得不偿失。
    /// </summary>
    public bool SupportsNativeVerify(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath)) return false;
        string extension = Path.GetExtension(archivePath.Trim());
        return extension.Length > 0 && ZipExtensions.Contains(extension);
    }

    /// <summary>估算候选总数；-1 = 未知（字典流式读取）。纯计算，不读词表。</summary>
    public long EstimateCandidates(RecoveryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return PasswordCandidates.Estimate(options);
    }

    // ── 破译主流程 ────────────────────────────────────────────────────────────

    public Task<RecoveryResult> RecoverAsync(
        RecoveryOptions options,
        IProgress<RecoveryProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        // 候选枚举 + 校验都是同步 IO/CPU：放到线程池，UI 线程不阻塞。
        return Task.Run(() => RecoverCoreAsync(options, progress, ct), CancellationToken.None);
    }

    private async Task<RecoveryResult> RecoverCoreAsync(
        RecoveryOptions options,
        IProgress<RecoveryProgress>? progress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string archive = RequireArchiveFile(options.ArchivePath);
        PasswordCandidates.Validate(options); // 参数错误尽早以中文抛出

        long total = PasswordCandidates.Estimate(options);
        var reporter = new ProgressReporter(progress, total);
        reporter.ReportStart();

        ZipPasswordVerifier? zip = null;
        string engineExe = "";
        bool useNative = false;

        try
        {
            if (SupportsNativeVerify(archive))
            {
                ZipProbeStatus status = ZipPasswordVerifier.TryOpen(archive, out zip, out _);
                if (status == ZipProbeStatus.Ok)
                {
                    useNative = true;
                }
                else if (status == ZipProbeStatus.NotEncrypted)
                {
                    throw new ArchiveException($"“{Path.GetFileName(archive)}”里没有加密条目，不需要破译密码。");
                }
                // NotZip / Unsupported：交给 7z 预言机，让它给出更准确的判断（损坏 / 强加密 / 真实格式）。
            }

            if (!useNative)
            {
                engineExe = RequireEngineExe();
                await EnsureEncryptedAsync(engineExe, archive, ct).ConfigureAwait(false);
            }

            string verifier = VerifierOracle; // 找到密码时回填成实际起作用的那条通道
            string? found = null;
            long tried = 0;

            foreach (string candidate in PasswordCandidates.Generate(options))
            {
                ct.ThrowIfCancellationRequested();

                if (options.MaxAttempts > 0 && tried >= options.MaxAttempts) break;

                tried++;

                if (useNative)
                {
                    ZipVerifyOutcome outcome = zip!.Verify(candidate);

                    if (outcome == ZipVerifyOutcome.Match)
                    {
                        // 进程内精确复核（CRC32 / HMAC）已经给出确定结论。
                        // 7z 交叉验证只对 ASCII 密码做：非 ASCII 时 7z 的 zip 密码只能按本机代码页表达，
                        // 它说“打不开”并不能证明密码错，做了只会产生假阴性。
                        await CrossCheckAsync(archive, candidate, ct).ConfigureAwait(false);
                        found = candidate;
                        verifier = VerifierNative;
                        break;
                    }

                    if (outcome == ZipVerifyOutcome.MatchUnverified)
                    {
                        // 快速筛过了但无法进程内精确复核（Deflate64/LZMA、数据描述符条目等）：让 7z 定论。
                        if (engineExe.Length == 0) engineExe = RequireEngineExe();
                        if (await TryOracleAsync(engineExe, archive, candidate, ct).ConfigureAwait(false))
                        {
                            found = candidate;
                            verifier = VerifierOracle;
                            break;
                        }
                    }

                    reporter.MaybeReport(tried, candidate);
                    continue;
                }

                if (await TryOracleAsync(engineExe, archive, candidate, ct).ConfigureAwait(false))
                {
                    found = candidate;
                    verifier = VerifierOracle;
                    break;
                }

                reporter.MaybeReport(tried, candidate);
            }

            TimeSpan elapsed = reporter.Elapsed;
            reporter.ReportFinal(tried, found is not null);

            return new RecoveryResult
            {
                Found = found is not null,
                Password = found,
                Tried = tried,
                Elapsed = elapsed,
                Verifier = found is not null ? verifier : "",
                // 找不到 = 候选耗尽或撞上 MaxAttempts（取消是抛 OperationCanceledException，不走这里）
                Exhausted = found is null,
            };
        }
        catch (IOException ex)
        {
            throw new ArchiveException($"读取归档或词表失败：{ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ArchiveException("无权读取归档文件或词表文件，请检查文件权限。", ex);
        }
        finally
        {
            zip?.Dispose();
        }
    }

    // ── 7z 预言机 ─────────────────────────────────────────────────────────────

    /// <summary>单个候选：`7z t -p<candidate> -- <archive>`。串行调用，一个候选一个进程。</summary>
    private static async Task<bool> TryOracleAsync(
        string exe, string archive, string candidate, CancellationToken ct)
    {
        var args = new List<string> { "t", "-sccUTF-8", "-bd", "-p" + candidate, "--", archive };

        SevenZipRunResult result = await SevenZipRunner
            .RunAsync(exe, args, null, null, ct, OracleTimeoutMs)
            .ConfigureAwait(false);

        if (result.TimedOut)
            throw new ArchiveException("7-Zip 引擎响应超时（单个候选超过 180 秒），破译已中止。");

        switch (result.ExitCode)
        {
            case 0:
            case 1:
                // 0 = 全部通过；1 = 密码正确但有个别条目告警。
                return true;

            case 2:
            {
                string diagnostic = result.Diagnostic.ToLowerInvariant();
                if (diagnostic.Contains("not enough space") || diagnostic.Contains("can't allocate")
                    || diagnostic.Contains("out of memory"))
                {
                    throw new ArchiveException("可用内存或磁盘空间不足，破译已中止。");
                }
                // 密码错误 / 认证失败 / 传统加密的假阳性（CRC failed、Data Error）——都算“不是这个密码”。
                return false;
            }

            case 7:
                throw new ArchiveException("7-Zip 命令行参数错误（内部错误），请把这一步反馈给开发者。");

            case 8:
                throw new ArchiveException("内存不足，破译已中止。");

            default:
                return false;
        }
    }

    /// <summary>已由进程内精确复核确认后的交叉验证：失败不影响结论，只做记录性检查。</summary>
    private async Task CrossCheckAsync(string archive, string candidate, CancellationToken ct)
    {
        if (!IsAscii(candidate)) return; // 7z 的 zip 密码走本机代码页，非 ASCII 复核没有意义

        string? exe = PeekEngineExe();
        if (exe is null) return;

        try
        {
            _ = await TryOracleAsync(exe, archive, candidate, ct).ConfigureAwait(false);
        }
        catch (ArchiveException)
        {
            // 交叉验证只是“再看一眼”，失败不改变已经精确证明的结论。
        }
    }

    /// <summary>破译前的预检：归档必须能列出，且确实有加密内容。</summary>
    private static async Task EnsureEncryptedAsync(string exe, string archive, CancellationToken ct)
    {
        var parser = new SevenZipListParser();
        var args = new List<string> { "l", "-slt", "-sccUTF-8", "-p", "--", archive };

        SevenZipRunResult result = await SevenZipRunner
            .RunAsync(exe, args, null, parser.Feed, ct, OracleTimeoutMs)
            .ConfigureAwait(false);

        parser.Complete();

        if (result.TimedOut)
            throw new ArchiveException("7-Zip 引擎响应超时，破译已中止。");

        if (result.ExitCode is 0 or 1)
        {
            bool encrypted = parser.HeaderEncrypted || parser.Entries.Any(e => e.Encrypted);
            if (!encrypted)
                throw new ArchiveException($"“{Path.GetFileName(archive)}”里没有加密条目，不需要破译密码。");
            return;
        }

        string diagnostic = result.Diagnostic.ToLowerInvariant();

        // 文件名都加密的 7z：不给密码本来就列不出来，7z 会用“密码错误”文案提示，说明它确实是加密归档。
        if (diagnostic.Contains("wrong password") || diagnostic.Contains("cannot open encrypted archive")
            || diagnostic.Contains("can not open encrypted archive"))
        {
            return;
        }

        throw new ArchiveException($"“{Path.GetFileName(archive)}”已损坏，或不是受支持的归档格式。");
    }

    // ── 小工具 ────────────────────────────────────────────────────────────────

    private string RequireEngineExe() => Engine.RequireExe();

    private string? PeekEngineExe()
    {
        try
        {
            return Engine.ExePath;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string RequireArchiveFile(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
            throw new ArchiveException("未指定要破译的归档文件。");

        string full;
        try
        {
            full = Path.GetFullPath(archivePath.Trim());
        }
        catch (Exception ex)
        {
            throw new ArchiveException($"归档路径无效：{ex.Message}", ex);
        }

        if (Directory.Exists(full))
            throw new ArchiveException($"指定的是文件夹，不是归档文件：{full}");
        if (!File.Exists(full))
            throw new ArchiveException($"找不到归档文件：{full}");

        return full;
    }

    private static bool IsAscii(string value)
    {
        foreach (char c in value)
        {
            if (c >= 0x80) return false;
        }
        return true;
    }

    // ── 进度节流 ──────────────────────────────────────────────────────────────

    /// <summary>进度节流：≥100ms 或每 500 次上报一次；速度用 2 秒滑动窗口估算。</summary>
    private sealed class ProgressReporter
    {
        private readonly IProgress<RecoveryProgress>? _progress;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly long _total;
        private readonly Queue<(long Milliseconds, long Tried)> _window = new();

        private long _lastReportMs = -1;
        private long _lastReportTried;

        public ProgressReporter(IProgress<RecoveryProgress>? progress, long total)
        {
            _progress = progress;
            _total = total;
        }

        public TimeSpan Elapsed => _clock.Elapsed;

        public void ReportStart()
        {
            if (_progress is null) return;

            _lastReportMs = _clock.ElapsedMilliseconds;
            _lastReportTried = 0;
            _progress.Report(new RecoveryProgress
            {
                Tried = 0,
                AttemptsPerSecond = 0,
                CurrentCandidate = null,
                Percent = _total > 0 ? 0 : -1,
                Eta = null,
                TotalCandidates = _total,
                Phase = "准备",
            });
        }

        public void MaybeReport(long tried, string? candidate)
        {
            if (_progress is null) return;

            long nowMs = _clock.ElapsedMilliseconds;
            if (tried - _lastReportTried < 500 && nowMs - _lastReportMs < 100) return;

            Report(tried, candidate, nowMs, "运行");
        }

        public void ReportFinal(long tried, bool found)
        {
            if (_progress is null) return;

            long nowMs = _clock.ElapsedMilliseconds;
            double speed = Speed(nowMs, tried);
            _progress.Report(new RecoveryProgress
            {
                Tried = tried,
                AttemptsPerSecond = speed,
                CurrentCandidate = null,
                Percent = _total > 0 || found ? 100 : -1,
                Eta = TimeSpan.Zero,
                TotalCandidates = _total,
                Phase = found ? "已找到" : "已完成",
            });

            _lastReportMs = nowMs;
            _lastReportTried = tried;
        }

        private void Report(long tried, string? candidate, long nowMs, string phase)
        {
            double speed = Speed(nowMs, tried);
            double percent = _total > 0 ? Math.Min(99.0, tried * 100.0 / _total) : -1;

            TimeSpan? eta = null;
            if (_total > 0 && speed > 0 && tried < _total)
            {
                double seconds = (_total - tried) / speed;
                if (seconds > 0 && seconds < TimeSpan.MaxValue.TotalSeconds)
                    eta = TimeSpan.FromSeconds(seconds);
            }

            _progress!.Report(new RecoveryProgress
            {
                Tried = tried,
                AttemptsPerSecond = speed,
                CurrentCandidate = TrimCandidate(candidate),
                Percent = percent,
                Eta = eta,
                TotalCandidates = _total,
                Phase = phase,
            });

            _lastReportMs = nowMs;
            _lastReportTried = tried;
        }

        /// <summary>2 秒滑动窗口估算速度（不累积历史，内存恒定）。</summary>
        private double Speed(long nowMs, long tried)
        {
            _window.Enqueue((nowMs, tried));
            while (_window.Count > 2 && nowMs - _window.Peek().Milliseconds > 2000) _window.Dequeue();

            (long Milliseconds, long Tried) oldest = _window.Peek();
            long deltaMs = nowMs - oldest.Milliseconds;
            long deltaTried = tried - oldest.Tried;
            if (deltaMs <= 0 || deltaTried <= 0) return 0;

            return deltaTried * 1000.0 / deltaMs;
        }

        private static string? TrimCandidate(string? candidate)
        {
            if (candidate is null) return null;
            if (candidate.Length <= MaxCandidateCharsInProgress) return candidate;
            return candidate[..(MaxCandidateCharsInProgress - 3)] + "...";
        }
    }
}
