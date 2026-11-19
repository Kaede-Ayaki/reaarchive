// ArchiveService.cs — IArchiveService 的 7z.exe 实现（本程序唯一的归档入口）
//
// 设计约束（契约 docs/CONTRACT.md §2）：
//  - 唯一引擎：随包 7z.exe 子进程，不自己做格式解析（zip 也交给 7z，天然兼容 GBK）。
//  - 所有方法 async + CancellationToken + IProgress；取消时 Kill 整棵进程树并抛 OperationCanceledException。
//  - 列表用 `7z l -slt -sccUTF-8`，解压用 `7z x -y -bsp1 -bb1`，创建用 `7z a`，追加 `7z a`，删除 `7z d`。
//  - 创建/追加时把**进程 WorkingDirectory 设为基准目录**，源文件传相对路径——这样归档内路径干净。
//    （实测 7z 的 `-w` 只指定临时工作目录，不会改变源文件解析基准，所以不靠它。）
//  - 读取类命令（l/x/t/e）始终带 `-p…`（空密码给裸 `-p`）以防 7z 停下来等密码；
//    写入类命令（a/d）在无密码时**不能**给 `-p`，否则 7z 会交互式询问密码并因 stdin 关闭而中止。
//  - CreateAsync 对已存在目标 = 覆盖重建：先在同目录临时目录里生成，成功后原子替换，失败不毁原归档。
//  - 解压前用 PathGuard 对**每个条目**做目录穿越/绝对路径/ADS 校验，越界直接拒绝。
//  - 所有失败统一抛中文 ArchiveException；不联网、不写注册表、不写日志。
//
// 已知限制（会在 README 与最终汇报中注明）：
//  - 密码只能通过 -p 传给 7z.exe，因此**密码会短暂出现在本机进程命令行**（Windows 没有更安全的命令行传参方式；
//    7z 不支持从 stdin 读密码的非交互通道）。密码本身不写日志、不落盘、不出现在异常信息里。
//  - 7z 命令行无法设置归档注释（CreateOptions.Comment 仅保留语义，当前被忽略）。
//  - 分卷只支持 7z/zip；其他格式请求分卷会被忽略并在 Warnings 中说明。

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace REAArchive.Archive;

public sealed class ArchiveService : IArchiveService
{
    private static readonly ArchiveFormat[] Creatable =
    {
        ArchiveFormat.SevenZip,
        ArchiveFormat.Zip,
        ArchiveFormat.Tar,
        ArchiveFormat.GZip,
        ArchiveFormat.BZip2,
        ArchiveFormat.Xz,
    };

    private static readonly Regex PercentRx = new(@"(?<!\d)(\d{1,3})(?=\s*%)", RegexOptions.Compiled);
    // 7z 的 -bb1 条目行：解压 "- name"、测试 "T name"、压缩/追加 "+ name"、更新 "U name"。
    private static readonly Regex ItemRx = new(@"^[-+TU]\s+(?<name>.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex VolumeSuffixRx = new(@"^\.\d{3}$", RegexOptions.Compiled);

    private readonly SevenZipEngine _engine;

    public ArchiveService(string? engineDir = null) => _engine = new SevenZipEngine(engineDir);

    // ── IArchiveService ────────────────────────────────────────────────────────

    public string EngineVersion => _engine.Version;

    public bool EngineAvailable => _engine.Available;

    public IReadOnlyList<ArchiveFormat> CreatableFormats => Creatable;

    /// <summary>实际使用的 7z.exe 路径（找不到为空串），便于“关于/诊断”页展示。</summary>
    public string EnginePath => _engine.ExePath ?? "";

    public ArchiveFormat DetectFormat(string path) => FormatDetector.Detect(path);

    public async Task<ListResult> ListAsync(string archivePath, string? password, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        string exe = RequireEngine();
        string archive = RequireArchiveFile(archivePath);

        var parser = new SevenZipListParser();
        var args = new List<string>
        {
            "l", "-slt", "-sccUTF-8", PasswordArg(password), archive,
        };

        SevenZipRunResult res = await SevenZipRunner
            .RunAsync(exe, args, null, parser.Feed, ct)
            .ConfigureAwait(false);
        parser.Complete();

        if (res.ExitCode >= 2) throw MapError("读取归档", archive, password, res);
        if (res.ExitCode == 1 && parser.Entries.Count == 0 && LooksUnreadable(res.Diagnostic))
            throw MapError("读取归档", archive, password, res);

        ArchiveFormat format = FormatFromType(parser.ArchiveType) ?? DetectFormat(archive);
        IReadOnlyList<ArchiveEntryInfo> entries = parser.Entries;

        // xz / bzip2 是单文件流：-slt 不输出条目 Path（7z 只在普通列表里显示），按归档名合成一个条目。
        if (entries.Count == 0 && format is ArchiveFormat.GZip or ArchiveFormat.BZip2 or ArchiveFormat.Xz)
            entries = SynthesizeSingleStreamEntry(archive, parser);

        if (entries.Count == 0 && format == ArchiveFormat.Unknown)
            throw new ArchiveException("无法识别该文件的归档格式，或文件已损坏。");

        bool encrypted = parser.HeaderEncrypted || entries.Any(e => e.Encrypted);

        return new ListResult
        {
            ArchivePath = archive,
            Format = format,
            Entries = entries,
            IsEncrypted = encrypted,
            Comment = parser.Comment,
            Elapsed = sw.Elapsed,
            EngineVersion = EngineVersion,
        };
    }

    public async Task<ExtractResult> ExtractAsync(
        string archivePath,
        ExtractOptions options,
        IProgress<ProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var sw = Stopwatch.StartNew();
        string exe = RequireEngine();
        string archive = RequireArchiveFile(archivePath);

        if (string.IsNullOrWhiteSpace(options.Destination))
            throw new ArchiveException("未指定解压目标目录。");

        string dest = Path.GetFullPath(options.Destination.Trim());
        if (File.Exists(dest))
            throw new ArchiveException($"解压目标是一个文件，不是文件夹：{dest}");

        // 先把条目列出来：既要统计，又要在**解压前**做安全校验。
        ListResult listing = await ListAsync(archive, options.Password, ct).ConfigureAwait(false);

        var warnings = new ConcurrentBag<string>();
        bool preserve = options.PreservePaths;

        // 目录穿越 / 绝对路径 / ADS / 非法字符：逐条目校验（放在线程池，避免 10 万条目阻塞 UI 线程）。
        (ArchiveEntryInfo Entry, string Relative, string Target)[] allTargets = await Task.Run(() =>
        {
            var list = new (ArchiveEntryInfo, string, string)[listing.Entries.Count];
            for (int i = 0; i < listing.Entries.Count; i++)
            {
                ArchiveEntryInfo e = listing.Entries[i];
                var localWarnings = new List<string>();
                string rel = PathGuard.Sanitize(e.Path, localWarnings);
                foreach (string w in localWarnings) warnings.Add(w);
                string target = PathGuard.ResolveTarget(dest, preserve ? rel : Path.GetFileName(rel));
                list[i] = (e, rel, target);
            }
            return list;
        }, ct).ConfigureAwait(false);

        foreach (ArchiveEntryInfo e in listing.Entries)
        {
            if (e.Path.Contains('\uFFFD') || e.Path.Contains('?'))
            {
                warnings.Add($"条目名“{e.Path}”可能因编码问题无法完整显示，解压后的文件名可能不一致。");
                break;
            }
        }

        // 选中条目
        HashSet<string>? requested = null;
        if (options.SelectedPaths is { Count: > 0 })
        {
            requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string p in options.SelectedPaths)
            {
                if (!string.IsNullOrWhiteSpace(p)) requested.Add(NormalizeArchivePath(p));
            }
        }

        static bool IsUnder(ArchiveEntryInfo e, string sel)
            => e.Path.Equals(sel, StringComparison.OrdinalIgnoreCase)
               || e.Path.StartsWith(sel + "/", StringComparison.OrdinalIgnoreCase);

        ArchiveEntryInfo[] toExtract;
        if (requested is null)
        {
            toExtract = listing.Entries.ToArray();
        }
        else
        {
            var recognized = new List<string>();
            var missing = new List<string>();
            foreach (string sel in requested)
            {
                if (listing.Entries.Any(e => IsUnder(e, sel))) recognized.Add(sel);
                else missing.Add(sel);
            }
            if (recognized.Count == 0)
                throw new ArchiveException($"选中的条目在归档中不存在：{string.Join("、", missing)}");
            foreach (string m in missing) warnings.Add($"选中的条目不存在，已跳过：{m}");

            var set = new HashSet<string>(recognized, StringComparer.OrdinalIgnoreCase);
            toExtract = listing.Entries.Where(e => set.Any(s => IsUnder(e, s))).ToArray();
        }

        if (!Directory.Exists(dest))
        {
            if (!options.CreateDestination)
                throw new ArchiveException($"目标目录不存在：{dest}");
            Directory.CreateDirectory(PathGuard.Extended(dest));
        }

        var args = new List<string>
        {
            "x", "-y", "-bsp1", "-bb1", "-sccUTF-8",
            "-o" + dest,
            OverwriteArg(options.Overwrite, warnings),
            PasswordArg(options.Password),
            archive,
        };
        if (!preserve) args.Add("-e");
        if (requested is not null)
        {
            foreach (string sel in requested)
            {
                if (toExtract.Any(e => IsUnder(e, sel)))
                    args.Add(PathGuard.ToNative(sel));
            }
        }

        long totalBytes = toExtract.Where(e => !e.IsDirectory).Sum(e => e.Size);
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (ArchiveEntryInfo e in toExtract) sizes[e.Path] = e.Size;

        // 进度不再直接采信 7z 的 -bsp1 百分比：对小文件/大量条目它是按字节流估算的，
        // 会在真正写盘完成前就冲到 100%。这里优先用“已完成条目数 / 总条目数”。
        var tracker = new ProgressTracker(progress, sw, "解压", toExtract.Length, totalBytes, sizes,
            countDirectories: true);
        tracker.Start();

        void OnLine(string line)
        {
            TrackWarning(line, warnings);
            if (TryReadPercent(line, out double pct)) tracker.OnBytePercent(pct);
            else if (ItemRx.Match(line.Trim()) is { Success: true } m) tracker.OnItem(m.Groups["name"].Value.Trim());
            tracker.MaybeReport();
        }

        SevenZipRunResult res = await SevenZipRunner
            .RunAsync(exe, args, null, OnLine, ct)
            .ConfigureAwait(false);

        if (res.ExitCode >= 2) throw MapError("解压", archive, options.Password, res);
        if (res.ExitCode == 1 && toExtract.Length > 0 && res.Diagnostic.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
            throw MapError("解压", archive, options.Password, res);

        tracker.Complete();

        int files = toExtract.Count(e => !e.IsDirectory);
        long bytes = toExtract.Where(e => !e.IsDirectory).Sum(e => e.Size);

        // gz/bz2/xz 的条目大小在归档元数据里可能未知（bzip2 头没有原始大小），解压后按落盘文件补正。
        if (listing.Format is ArchiveFormat.GZip or ArchiveFormat.BZip2 or ArchiveFormat.Xz)
        {
            long actual = 0;
            foreach (ArchiveEntryInfo e in toExtract)
            {
                if (e.IsDirectory) continue;
                try
                {
                    string rel = PathGuard.Sanitize(e.Path, new List<string>());
                    string target = PathGuard.ResolveTarget(dest, preserve ? rel : Path.GetFileName(rel));
                    var fi = new FileInfo(target);
                    if (fi.Exists) actual += fi.Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArchiveException)
                {
                    // 统计失败不影响解压结果。
                }
            }
            bytes = actual;
        }

        return new ExtractResult
        {
            Files = files,
            Bytes = bytes,
            Elapsed = sw.Elapsed,
            Warnings = warnings.ToArray(),
        };
    }

    public async Task<ExtractResult> CreateAsync(
        CreateOptions options,
        IProgress<ProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var sw = Stopwatch.StartNew();
        string exe = RequireEngine();

        ArchiveFormat format = options.Format;
        if (!Creatable.Contains(format))
            throw new ArchiveException($"不支持创建 {FormatUtil.FormatName(format)} 格式。");

        ValidatePasswordForFormat(format, options.Password);

        var warnings = new ConcurrentBag<string>();
        List<string> sources = ValidateSources(options.Sources);

        if (format is ArchiveFormat.GZip or ArchiveFormat.BZip2 or ArchiveFormat.Xz)
        {
            if (sources.Count != 1 || Directory.Exists(sources[0]))
                throw new ArchiveException(
                    $"{FormatUtil.FormatName(format)} 只能压缩单个文件；要打包目录或多个文件，请选择 7Z、ZIP 或 TAR。");
        }

        if (string.IsNullOrWhiteSpace(options.ArchivePath))
            throw new ArchiveException("未指定新归档的保存路径。");

        if (!string.IsNullOrWhiteSpace(options.Comment))
            warnings.Add("7-Zip 命令行无法写入归档注释，Comment 已被忽略。");

        string target = Path.GetFullPath(options.ArchivePath.Trim());
        string targetDir = Path.GetDirectoryName(target) ?? Path.GetPathRoot(target)!;
        Directory.CreateDirectory(PathGuard.Extended(targetDir));

        if (Directory.Exists(target))
            throw new ArchiveException($"目标路径是一个文件夹：{target}");

        string baseDir = ResolveBaseDirectory(options.BaseDirectory, sources);
        List<string> rels = BuildRelativeSources(sources, baseDir, warnings);

        bool volumes = options.VolumeSize is > 0;
        if (volumes && format is not (ArchiveFormat.SevenZip or ArchiveFormat.Zip))
        {
            warnings.Add($"{FormatUtil.FormatName(format)} 不支持分卷，已忽略分卷设置。");
            volumes = false;
        }

        (int fileCount, long totalBytes, Dictionary<string, long> sizeMap) =
            await Task.Run(() => ScanSources(sources, baseDir, ct), ct).ConfigureAwait(false);

        string tempDir = Path.Combine(targetDir, ".rea-tmp-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(tempDir);
        string tempArchive = Path.Combine(tempDir, Path.GetFileName(target));

        try
        {
            var args = new List<string>
            {
                "a", "-bsp1", "-bb1",
                FormatSwitch(format),
                "-mx" + ClampLevel(options.Level),
                "-sccUTF-8",
            };
            AddPasswordForUpdate(args, options.Password);

            if (format == ArchiveFormat.SevenZip && options.EncryptHeaders)
                args.Add("-mhe=on");
            else if (options.EncryptHeaders)
                warnings.Add("只有 7Z 支持“连文件名一起加密”，该选项已忽略。");

            if (volumes)
                args.Add("-v" + options.VolumeSize!.Value.ToString(CultureInfo.InvariantCulture) + "b");

            string? listFile = MaybeWriteListFile(rels, tempDir, args);
            args.Add(tempArchive);

            var tracker = new ProgressTracker(progress, sw, "压缩", fileCount, totalBytes, sizeMap,
                countDirectories: false);
            tracker.Start();

            void OnLine(string line)
            {
                TrackWarning(line, warnings);
                if (TryReadPercent(line, out double pct)) tracker.OnBytePercent(pct);
                else if (ItemRx.Match(line.Trim()) is { Success: true } m) tracker.OnItem(m.Groups["name"].Value.Trim());
                tracker.MaybeReport();
            }

            if (listFile is null) args.AddRange(rels);

            SevenZipRunResult res = await SevenZipRunner
                .RunAsync(exe, args, baseDir, OnLine, ct)
                .ConfigureAwait(false);

            if (res.ExitCode >= 2) throw MapError("创建归档", target, options.Password, res);
            if (res.ExitCode == 1 && res.Diagnostic.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                throw MapError("创建归档", target, options.Password, res);

            // 创建成功后原子替换；此前 target 上的旧文件一直没被动过。
            try
            {
                InstallOutput(tempDir, tempArchive, target, volumes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ArchiveException(
                    $"归档已生成，但写入目标路径失败（文件可能被占用或没有权限）：{target}", ex);
            }
            tracker.Complete();

            return new ExtractResult
            {
                Files = fileCount,
                Bytes = totalBytes,
                Elapsed = sw.Elapsed,
                Warnings = warnings.ToArray(),
            };
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    public async Task<ExtractResult> AddAsync(
        AddOptions options,
        IProgress<ProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var sw = Stopwatch.StartNew();
        string exe = RequireEngine();

        if (string.IsNullOrWhiteSpace(options.ArchivePath))
            throw new ArchiveException("未指定要追加的归档文件。");

        string archive = Path.GetFullPath(options.ArchivePath.Trim());
        if (!File.Exists(archive))
            throw new ArchiveException($"归档不存在，请先新建：{archive}");

        ValidatePasswordForFormat(DetectFormat(archive), options.Password);

        var warnings = new ConcurrentBag<string>();
        List<string> sources = ValidateSources(options.Sources);

        string baseDir = ResolveBaseDirectory(options.BaseDirectory, sources);
        List<string> rels = BuildRelativeSources(sources, baseDir, warnings);

        (int fileCount, long totalBytes, Dictionary<string, long> sizeMap) =
            await Task.Run(() => ScanSources(sources, baseDir, ct), ct).ConfigureAwait(false);

        var args = new List<string>
        {
            "a", "-bsp1", "-bb1",
            "-mx" + ClampLevel(options.Level),
            "-sccUTF-8",
        };
        AddPasswordForUpdate(args, options.Password);

        string tempDir = Path.Combine(Path.GetTempPath(), "reaarchive-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(tempDir);
        try
        {
            string? listFile = MaybeWriteListFile(rels, tempDir, args);
            args.Add(archive);
            if (listFile is null) args.AddRange(rels);

            var tracker = new ProgressTracker(progress, sw, "追加", fileCount, totalBytes, sizeMap,
                countDirectories: false);
            tracker.Start();

            void OnLine(string line)
            {
                TrackWarning(line, warnings);
                if (TryReadPercent(line, out double pct)) tracker.OnBytePercent(pct);
                else if (ItemRx.Match(line.Trim()) is { Success: true } m) tracker.OnItem(m.Groups["name"].Value.Trim());
                tracker.MaybeReport();
            }

            SevenZipRunResult res = await SevenZipRunner
                .RunAsync(exe, args, baseDir, OnLine, ct)
                .ConfigureAwait(false);

            if (res.ExitCode >= 2) throw MapError("追加到归档", archive, options.Password, res);
            if (res.ExitCode == 1 && res.Diagnostic.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                throw MapError("追加到归档", archive, options.Password, res);

            tracker.Complete();
            return new ExtractResult
            {
                Files = fileCount,
                Bytes = totalBytes,
                Elapsed = sw.Elapsed,
                Warnings = warnings.ToArray(),
            };
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    public async Task<DeleteResult> DeleteAsync(
        string archivePath,
        IReadOnlyList<string> entryPaths,
        string? password = null,
        IProgress<ProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        string exe = RequireEngine();
        string archive = RequireArchiveFile(archivePath);

        ValidatePasswordForFormat(DetectFormat(archive), password);

        var requested = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (entryPaths is not null)
        {
            foreach (string p in entryPaths)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                string norm = NormalizeArchivePath(p);
                if (norm.Length > 0 && seen.Add(norm)) requested.Add(norm);
            }
        }

        if (requested.Count == 0)
            return new DeleteResult { Removed = 0, Elapsed = sw.Elapsed };

        ListResult listing = await ListAsync(archive, password, ct).ConfigureAwait(false);

        int matched = 0;
        foreach (ArchiveEntryInfo e in listing.Entries)
        {
            foreach (string req in requested)
            {
                if (e.Path.Equals(req, StringComparison.OrdinalIgnoreCase)
                    || e.Path.StartsWith(req + "/", StringComparison.OrdinalIgnoreCase))
                {
                    matched++;
                    break;
                }
            }
        }

        // 条目都不存在：静默跳过，不碰归档。
        if (matched == 0)
            return new DeleteResult { Removed = 0, Elapsed = sw.Elapsed };

        var args = new List<string> { "d", "-bsp1", "-bb1", "-sccUTF-8" };
        AddPasswordForUpdate(args, password);

        string tempDir = Path.Combine(Path.GetTempPath(), "reaarchive-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(tempDir);
        try
        {
            string? listFile = MaybeWriteListFile(requested.Select(PathGuard.ToNative).ToList(), tempDir, args);
            args.Add(archive);
            if (listFile is null) args.AddRange(requested.Select(PathGuard.ToNative));

            var tracker = new ProgressTracker(progress, sw, "删除", matched, 0, null, countDirectories: true);
            tracker.Start();

            void OnLine(string line)
            {
                if (TryReadPercent(line, out double pct)) tracker.OnBytePercent(pct);
                else if (ItemRx.Match(line.Trim()) is { Success: true } m) tracker.OnItem(m.Groups["name"].Value.Trim());
                tracker.MaybeReport();
            }

            SevenZipRunResult res = await SevenZipRunner
                .RunAsync(exe, args, null, OnLine, ct)
                .ConfigureAwait(false);

            if (res.ExitCode >= 2) throw MapError("删除条目", archive, password, res);
            if (res.ExitCode == 1 && res.Diagnostic.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                throw MapError("删除条目", archive, password, res);

            tracker.Complete();
            return new DeleteResult { Removed = matched, Elapsed = sw.Elapsed };
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    public async Task<bool> TestAsync(
        string archivePath,
        string? password,
        IProgress<ProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        string exe = RequireEngine();
        string archive = RequireArchiveFile(archivePath);

        var args = new List<string> { "t", "-bsp1", "-bb1", "-sccUTF-8", PasswordArg(password), archive };

        // 测试没有预先列表，无法可靠换算条目进度：用 7z 的字节百分比但**封顶 99%**，
        // 只有 7z 正常退出后才由 Complete() 给 100%。
        var tracker = new ProgressTracker(progress, sw, "校验", 0, 0, null, countDirectories: true);
        tracker.Start();

        void OnLine(string line)
        {
            if (TryReadPercent(line, out double pct)) tracker.OnBytePercent(pct);
            else if (ItemRx.Match(line.Trim()) is { Success: true } m) tracker.OnItem(m.Groups["name"].Value.Trim());
            tracker.MaybeReport();
        }

        SevenZipRunResult res = await SevenZipRunner
            .RunAsync(exe, args, null, OnLine, ct)
            .ConfigureAwait(false);

        if (res.ExitCode >= 2) throw MapError("测试归档", archive, password, res);
        if (res.ExitCode == 1 && res.Diagnostic.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
            throw MapError("测试归档", archive, password, res);

        tracker.Complete();
        return true;
    }

    public async Task<byte[]> ReadEntryAsync(
        string archivePath,
        string entryPath,
        string? password,
        int maxBytes = 8 * 1024 * 1024,
        CancellationToken ct = default)
    {
        string exe = RequireEngine();
        string archive = RequireArchiveFile(archivePath);

        if (string.IsNullOrWhiteSpace(entryPath))
            throw new ArchiveException("未指定要读取的归档内文件。");
        if (maxBytes <= 0)
            throw new ArchiveException("预览大小上限必须大于 0。");

        // 先精确定位条目：`7z e -so <dir>` 会把目录下所有子文件内容一起输出，
        // 必须提前判断“是目录 / 不存在 / 超过预览上限”，不能靠读到的字节数猜。
        string norm = NormalizeArchivePath(entryPath);
        var listArgs = new List<string>
        {
            "l", "-slt", "-sccUTF-8", PasswordArg(password), archive, PathGuard.ToNative(entryPath.Trim()),
        };
        var parser = new SevenZipListParser();
        SevenZipRunResult listRes = await SevenZipRunner
            .RunAsync(exe, listArgs, null, parser.Feed, ct)
            .ConfigureAwait(false);
        parser.Complete();

        if (listRes.ExitCode >= 2) throw MapError("读取文件", archive, password, listRes);

        ArchiveEntryInfo? hit = parser.Entries
            .FirstOrDefault(e => e.Path.Equals(norm, StringComparison.OrdinalIgnoreCase));
        if (hit is null)
            throw new ArchiveException($"归档中找不到该文件：{entryPath}");
        if (hit.IsDirectory)
            throw new ArchiveException($"“{entryPath}”是文件夹，不能预览。");
        if (hit.Size > maxBytes)
            throw new ArchiveException($"文件超过 {FormatUtil.Size(maxBytes)}，无法预览；请先解压到磁盘。");

        var args = new List<string>
        {
            "e", "-so", "-sccUTF-8", PasswordArg(password), archive, PathGuard.ToNative(entryPath.Trim()),
        };

        SevenZipBinaryResult res = await SevenZipRunner
            .RunBinaryAsync(exe, args, null, maxBytes, ct)
            .ConfigureAwait(false);

        if (res.Truncated)
            throw new ArchiveException($"文件超过 {FormatUtil.Size(maxBytes)}，无法预览；请先解压到磁盘。");
        if (res.ExitCode != 0)
            throw MapError("读取文件", archive, password, new SevenZipRunResult
            {
                ExitCode = res.ExitCode,
                StdErr = res.StdErr,
            });

        return res.Data;
    }

    // ── 参数构造 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 读取类命令（l/x/t/e）的密码参数：始终给 `-p…`（空密码为裸 `-p`），
    /// 这样 7z 永远不会停下来等密码输入。密码会短暂出现在进程命令行（Windows 限制）。
    /// </summary>
    private static string PasswordArg(string? password)
        => string.IsNullOrEmpty(password) ? "-p" : "-p" + password;

    /// <summary>
    /// 写入类命令（a/d）的密码参数：**没有密码时一个 -p 都不能给**——7z 会把裸 -p 当成
    /// “请交互输入密码”，在 stdin 关闭时直接 Break signaled（exit 255）。
    /// </summary>
    private static void AddPasswordForUpdate(List<string> args, string? password)
    {
        if (!string.IsNullOrEmpty(password)) args.Add("-p" + password);
    }

    /// <summary>
    /// 7-Zip 25.01 的 ZIP 加密不接受非 ASCII 密码（实测 exit 2 / System ERROR）。
    /// 提前给出可读中文错误，别让用户看到 7z 的乱码系统错误。
    /// </summary>
    private static void ValidatePasswordForFormat(ArchiveFormat format, string? password)
    {
        if (format != ArchiveFormat.Zip || string.IsNullOrEmpty(password)) return;
        foreach (char c in password)
        {
            if (c > 0x7F)
            {
                throw new ArchiveException(
                    "ZIP 格式不支持非 ASCII 密码（7-Zip 限制）；请改用 7Z 格式，或把密码改成纯英文/数字。");
            }
        }
    }

    private static string OverwriteArg(OverwritePolicy policy, ConcurrentBag<string> warnings) => policy switch
    {
        OverwritePolicy.Skip => "-aos",
        OverwritePolicy.Rename => "-aou",
        OverwritePolicy.Overwrite => "-aoa",
        _ => SkipForAsk(warnings),
    };

    private static string SkipForAsk(ConcurrentBag<string> warnings)
    {
        warnings.Add("冲突策略为“询问”，引擎无法交互，已退化为“跳过已存在文件”。");
        return "-aos";
    }

    private static string FormatSwitch(ArchiveFormat format) => format switch
    {
        ArchiveFormat.SevenZip => "-t7z",
        ArchiveFormat.Zip => "-tzip",
        ArchiveFormat.Tar => "-ttar",
        ArchiveFormat.GZip => "-tgzip",
        ArchiveFormat.BZip2 => "-tbzip2",
        ArchiveFormat.Xz => "-txz",
        _ => throw new ArchiveException($"不支持创建 {FormatUtil.FormatName(format)} 格式。"),
    };

    private static int ClampLevel(int level) => level switch
    {
        < 0 => 5,
        > 9 => 9,
        _ => level,
    };

    // ── 路径 / 源文件 ─────────────────────────────────────────────────────────

    private static List<string> ValidateSources(IReadOnlyList<string>? sources)
    {
        if (sources is null || sources.Count == 0)
            throw new ArchiveException("没有要处理的文件或文件夹。");

        var result = new List<string>(sources.Count);
        foreach (string raw in sources)
        {
            if (string.IsNullOrWhiteSpace(raw))
                throw new ArchiveException("源文件路径为空。");

            string full = Path.GetFullPath(raw.Trim());
            if (!File.Exists(full) && !Directory.Exists(full))
                throw new ArchiveException($"源文件不存在：{full}");

            result.Add(full);
        }
        return result;
    }

    private static string ResolveBaseDirectory(string? explicitBase, IReadOnlyList<string> sources)
    {
        if (!string.IsNullOrWhiteSpace(explicitBase))
        {
            string b = Path.GetFullPath(explicitBase.Trim());
            if (!Directory.Exists(b))
                throw new ArchiveException($"基准目录不存在：{b}");
            return b;
        }

        var dirs = new List<string>();
        foreach (string s in sources)
        {
            string? d = Directory.Exists(s) ? Path.GetDirectoryName(s.TrimEnd('\\', '/')) : Path.GetDirectoryName(s);
            if (string.IsNullOrEmpty(d)) d = Path.GetPathRoot(s);
            if (!string.IsNullOrEmpty(d)) dirs.Add(d);
        }
        if (dirs.Count == 0) return Path.GetPathRoot(sources[0])!;

        string common = dirs[0];
        for (int i = 1; i < dirs.Count; i++) common = CommonParent(common, dirs[i]);
        return common;
    }

    private static string CommonParent(string a, string b)
    {
        a = a.TrimEnd('\\', '/');
        b = b.TrimEnd('\\', '/');
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return a;

        string[] pa = a.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        string[] pb = b.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        int i = 0;
        while (i < pa.Length && i < pb.Length && string.Equals(pa[i], pb[i], StringComparison.OrdinalIgnoreCase)) i++;
        if (i == 0) return Path.GetPathRoot(a) ?? a;

        string joined = string.Join(Path.DirectorySeparatorChar, pa, 0, i);
        return joined.EndsWith(':') ? joined + Path.DirectorySeparatorChar : joined;
    }

    private static List<string> BuildRelativeSources(
        IReadOnlyList<string> sources, string baseDir, ConcurrentBag<string> warnings)
    {
        var rels = new List<string>(sources.Count);
        foreach (string src in sources)
        {
            string rel;
            try
            {
                rel = Path.GetRelativePath(baseDir, src);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                rel = src;
            }

            if (Path.IsPathRooted(rel) || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || rel == ".." || rel.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            {
                warnings.Add($"“{src}”不在基准目录内，归档中将保留完整路径。");
                rel = src;
            }
            else if (rel == "." || rel.Length == 0)
            {
                throw new ArchiveException("基准目录不能是源目录本身，请选择它的父目录。");
            }

            rels.Add(rel);
        }
        return rels;
    }

    /// <summary>大量文件时写 UTF-8 的 @listfile，避免命令行超长（Windows 上限 32767 字符）。</summary>
    private static string? MaybeWriteListFile(List<string> rels, string tempDir, List<string> args)
    {
        int chars = 0;
        foreach (string r in rels) chars += r.Length + 3;

        if (rels.Count <= 40 && chars <= 3000) return null;

        string path = Path.Combine(tempDir, "sources.txt");
        File.WriteAllLines(path, rels, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        args.Add("-scsUTF-8");
        args.Add("@" + path);
        return path;
    }

    /// <summary>
    /// 扫描源文件：统计文件数/字节，并建立「归档内相对路径 → 文件大小」映射，
    /// 供进度按“已完成条目”累计真实字节（7z -bb1 的条目名与这个键一致）。
    /// </summary>
    private static (int Files, long Bytes, Dictionary<string, long> Sizes) ScanSources(
        IReadOnlyList<string> sources, string baseDir, CancellationToken ct)
    {
        int files = 0;
        long bytes = 0;
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (string s in sources)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string rel = Path.GetRelativePath(baseDir, s);
                if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) rel = s;

                if (File.Exists(s))
                {
                    long len = 0;
                    try { len = new FileInfo(s).Length; } catch (IOException) { }
                    files++;
                    bytes += len;
                    sizes[ArchiveKey(rel)] = len;
                }
                else if (Directory.Exists(s))
                {
                    foreach (string f in Directory.EnumerateFiles(s, "*", SearchOption.AllDirectories))
                    {
                        ct.ThrowIfCancellationRequested();
                        long len = 0;
                        try { len = new FileInfo(f).Length; } catch (IOException) { }
                        files++;
                        bytes += len;
                        sizes[ArchiveKey(Path.Combine(rel, Path.GetRelativePath(s, f)))] = len;
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // 扫描只用于进度与统计；权限问题交给 7z 报错。
            }
            catch (IOException)
            {
                // 同上：个别目录不可枚举不影响压缩本身。
            }
        }
        return (files, bytes, sizes);
    }

    /// <summary>统一成归档内 '/' 分隔、无 "./" 前缀的键。</summary>
    private static string ArchiveKey(string path)
    {
        string key = path.Replace('\\', '/');
        while (key.StartsWith("./", StringComparison.Ordinal)) key = key[2..];
        return key;
    }

    // ── 输出安装 / 清理 ───────────────────────────────────────────────────────

    /// <summary>把临时目录里的产物原子替换到用户指定路径（失败时原归档保持不变）。</summary>
    private static void InstallOutput(string tempDir, string tempArchive, string target, bool volumes)
    {
        string targetDir = Path.GetDirectoryName(target) ?? Path.GetPathRoot(target)!;

        if (!volumes)
        {
            if (!File.Exists(tempArchive))
                throw new ArchiveException("7-Zip 未生成归档文件，压缩可能已失败。");
            File.Move(tempArchive, target, overwrite: true);
            return;
        }

        string tempBase = Path.GetFileName(tempArchive);
        string targetBase = Path.GetFileName(target);
        string[] produced = Directory.GetFiles(tempDir, tempBase + ".*");
        if (produced.Length == 0)
            throw new ArchiveException("7-Zip 未生成分卷文件，压缩可能已失败。");

        // 清掉同名的旧分卷与旧单文件（覆盖重建语义）。
        foreach (string old in Directory.GetFiles(targetDir, targetBase + ".*"))
        {
            string suffix = Path.GetFileName(old)[targetBase.Length..];
            if (VolumeSuffixRx.IsMatch(suffix)) File.Delete(old);
        }
        if (File.Exists(target)) File.Delete(target);

        foreach (string f in produced)
        {
            string suffix = Path.GetFileName(f)[tempBase.Length..];
            File.Move(f, Path.Combine(targetDir, targetBase + suffix), overwrite: true);
        }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, System.IO.FileAttributes.Normal); } catch (IOException) { }
            }
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ── 输出解析 / 错误映射 ──────────────────────────────────────────────────

    private static void TrackWarning(string line, ConcurrentBag<string> warnings)
    {
        if (line.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Scan WARNINGS", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(line);
        }
    }

    private static bool TryReadPercent(string line, out double percent)
    {
        percent = -1;
        bool any = false;
        foreach (Match m in PercentRx.Matches(line))
        {
            if (int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
                && v is >= 0 and <= 100)
            {
                percent = v;
                any = true;
            }
        }
        return any;
    }

    private static string NormalizeArchivePath(string path)
    {
        string p = path.Trim().Replace('\\', '/');
        while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        while (p.EndsWith("/", StringComparison.Ordinal) && p.Length > 1) p = p[..^1];
        return p;
    }

    private static ArchiveFormat? FormatFromType(string? type)
    {
        if (string.IsNullOrEmpty(type)) return null;
        return type.Trim().ToLowerInvariant() switch
        {
            "7z" => ArchiveFormat.SevenZip,
            "zip" => ArchiveFormat.Zip,
            "tar" => ArchiveFormat.Tar,
            "gzip" => ArchiveFormat.GZip,
            "bzip2" => ArchiveFormat.BZip2,
            "xz" => ArchiveFormat.Xz,
            "rar" or "rar5" => ArchiveFormat.Rar,
            "iso" => ArchiveFormat.Iso,
            "wim" or "esd" or "swm" => ArchiveFormat.Wim,
            _ => (ArchiveFormat?)null,
        };
    }

    /// <summary>
    /// 为 gz/bz2/xz 单文件流合成条目：7z 解压时用“归档名去掉压缩后缀”作为输出文件名。
    /// 例：a.txt.bz2 → a.txt。
    /// </summary>
    private static ArchiveEntryInfo[] SynthesizeSingleStreamEntry(string archive, SevenZipListParser parser)
    {
        string name = Path.GetFileNameWithoutExtension(archive);
        if (string.IsNullOrEmpty(name)) name = Path.GetFileName(archive);

        long size = 0;
        string? sizeText = parser.LastValue("Size");
        if (!string.IsNullOrEmpty(sizeText)
            && long.TryParse(sizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long s) && s >= 0)
        {
            size = s;
        }

        long packed = -1;
        string? packedText = parser.LastValue("Packed Size");
        if (!string.IsNullOrEmpty(packedText)
            && long.TryParse(packedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long p) && p >= 0)
        {
            packed = p;
        }
        if (packed < 0)
        {
            try { packed = new FileInfo(archive).Length; } catch (IOException) { }
        }

        string? crc = parser.LastValue("CRC");

        return new[]
        {
            new ArchiveEntryInfo
            {
                Path = name,
                IsDirectory = false,
                Size = size,
                CompressedSize = packed,
                Modified = null,
                Encrypted = false,
                Method = parser.LastValue("Method") ?? "",
                Crc = string.IsNullOrEmpty(crc) ? null : crc.ToUpperInvariant(),
            },
        };
    }

    private ArchiveException MapError(string operation, string archivePath, string? password, SevenZipRunResult res)
    {
        string diag = res.Diagnostic;
        string lower = diag.ToLowerInvariant();
        string name = Path.GetFileName(archivePath);

        if (res.ExitCode == 7)
            return new ArchiveException("7-Zip 命令行参数错误（内部错误）。请把这一步的操作反馈给开发者。");
        if (res.ExitCode == 8)
            return new ArchiveException("内存不足，操作已中止。");

        // 密码错误 / 需要密码（注意要与“数据错误”区分：先匹配密码相关文案）
        if (lower.Contains("wrong password") || lower.Contains("cannot open encrypted archive")
            || lower.Contains("data error in encrypted file"))
        {
            return string.IsNullOrEmpty(password)
                ? new ArchiveException($"“{name}”已加密，需要输入密码。")
                : new ArchiveException("密码错误，无法解密该归档。");
        }

        if (lower.Contains("unexpected end of archive") || lower.Contains("cannot open the file as")
            || lower.Contains("can not open the file as") || lower.Contains("can't open as archive")
            || lower.Contains("headers error") || lower.Contains("crc failed")
            || lower.Contains("data error") || lower.Contains("is not archive")
            || lower.Contains("unsupported method") || lower.Contains("is not supported archive"))
        {
            return new ArchiveException($"“{name}”已损坏，或不是受支持的归档格式。");
        }

        if (lower.Contains("access is denied") || diag.Contains("拒绝访问", StringComparison.Ordinal)
            || lower.Contains("being used by another process") || lower.Contains("cannot open file"))
        {
            return new ArchiveException("拒绝访问：文件正被其他程序占用，或没有写入权限。");
        }

        if (lower.Contains("not enough space") || diag.Contains("磁盘空间", StringComparison.Ordinal)
            || lower.Contains("disk full"))
        {
            return new ArchiveException("磁盘空间不足，操作已中止。");
        }

        if (lower.Contains("can't allocate") || lower.Contains("out of memory"))
            return new ArchiveException("内存不足，操作已中止。");

        if (res.ExitCode == 255)
            return new ArchiveException("操作被 7-Zip 中止（可能缺少密码或输入已中断）。");

        string? detail = FirstErrorLine(diag);
        string suffix = detail is null ? "" : $"：{detail}";
        return new ArchiveException($"{operation}失败（7-Zip 退出码 {res.ExitCode}）{suffix}。");
    }

    private static string? FirstErrorLine(string diagnostic)
    {
        string? found = null;
        foreach (string raw in diagnostic.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("System ERROR", StringComparison.OrdinalIgnoreCase))
            {
                found = line.Length > 240 ? line[..240] : line;
            }
        }
        return found;
    }

    private static bool LooksUnreadable(string diagnostic)
    {
        string lower = diagnostic.ToLowerInvariant();
        return lower.Contains("error") || lower.Contains("warning");
    }

    private string RequireEngine() => _engine.RequireExe();

    private static string RequireArchiveFile(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
            throw new ArchiveException("未指定归档文件路径。");

        string full = Path.GetFullPath(archivePath.Trim());
        if (Directory.Exists(full))
            throw new ArchiveException($"指定的是文件夹，不是归档文件：{full}");
        if (!File.Exists(full))
            throw new ArchiveException($"找不到归档文件：{full}");
        return full;
    }

    // ── 进度 ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 进度换算器（B04 修复）。
    ///
    /// 背景：7z `-bsp1` 的 `NN%` 是按**字节流**估算的。对「2 万个几十字节的小文件」，
    /// 7z 很快解压完压缩流并报 100%，但真正写 2 万个文件还要十几秒——UI 就会提前显示完成。
    ///
    /// 规则：
    ///  - 条目多/平均体积小 → 用「已完成条目数 / 总条目数」算 Percent；
    ///  - 单文件或平均体积大 → 用 7z 字节百分比（此时它足够准）；
    ///  - 两种都不可用 → Percent = -1（UI 走不确定态）；
    ///  - 操作成功结束前 **Percent 封顶 99**，只有 Complete()（7z 正常退出后）才给 100；
    ///  - Percent 单调不减；BytesTotal 来自列表/扫描，BytesDone 按已完成条目的真实大小累计。
    /// </summary>
    private sealed class ProgressTracker
    {
        private readonly object _gate = new();
        private readonly IProgress<ProgressInfo>? _progress;
        private readonly Stopwatch _sw;
        private readonly string _phase;
        private readonly int _itemsTotal;
        private readonly long _bytesTotal;
        private readonly Dictionary<string, long>? _sizes;
        private readonly bool _countDirectories;
        private readonly bool _itemMode;

        private int _itemsDone;
        private long _bytesDone;
        private string? _current;
        private string? _lastItem;
        private bool _currentCounts;
        private double _bytePercent = -1;
        private double _lastPercent = -1;
        private long _lastReportMs = long.MinValue / 2;
        private bool _sawItems;
        private bool _completed;

        public ProgressTracker(IProgress<ProgressInfo>? progress, Stopwatch sw, string phase,
            int itemsTotal, long bytesTotal, Dictionary<string, long>? sizes, bool countDirectories)
        {
            _progress = progress;
            _sw = sw;
            _phase = phase;
            _itemsTotal = itemsTotal;
            _bytesTotal = bytesTotal;
            _sizes = sizes;
            _countDirectories = countDirectories;
            _itemMode = UseItemCount(itemsTotal, bytesTotal);
        }

        /// <summary>条目数多（或平均体积小）时，条目计数比字节流百分比可信。</summary>
        private static bool UseItemCount(int itemsTotal, long bytesTotal)
        {
            if (itemsTotal <= 1) return false;
            if (itemsTotal >= 100) return true;
            if (bytesTotal <= 0) return true;
            long average = bytesTotal / itemsTotal;
            return average < 4L * 1024 * 1024;
        }

        public void Start() => ReportLocked(force: true);

        public void OnBytePercent(double percent)
        {
            if (percent is < 0 or > 100) return;
            lock (_gate)
            {
                _bytePercent = percent;
                ReportLocked(force: false);
            }
        }

        /// <summary>7z -bb1 输出一个条目名（开始处理该条目）→ 上一个条目视为已完成。</summary>
        public void OnItem(string rawName)
        {
            string name = rawName.Trim();
            if (name.Length == 0) return;
            bool isDir = name.EndsWith("\\", StringComparison.Ordinal) || name.EndsWith("/", StringComparison.Ordinal);
            if (isDir) name = name.TrimEnd('\\', '/');

            lock (_gate)
            {
                FinalizeCurrentLocked();
                _sawItems = true;
                _current = name;
                _lastItem = name;
                _currentCounts = !isDir || _countDirectories;
                // 不强制上报：2 万条目逐条 Report 会把 UI 消息队列打满，统一走 70ms 节流。
                ReportLocked(force: false);
            }
        }

        public void MaybeReport()
        {
            if (_progress is null) return;
            lock (_gate)
            {
                if (_sw.ElapsedMilliseconds - _lastReportMs < 70) return;
                ReportLocked(force: false);
            }
        }

        public void Complete()
        {
            lock (_gate)
            {
                FinalizeCurrentLocked();
                _completed = true;
                ReportLocked(force: true);
            }
        }

        private void FinalizeCurrentLocked()
        {
            if (_current is null) return;
            if (_currentCounts)
            {
                _itemsDone++;
                if (_sizes is not null && _sizes.TryGetValue(ArchiveKey(_current), out long size))
                    _bytesDone += size;
            }
            _current = null;
            _currentCounts = false;
        }

        private void ReportLocked(bool force)
        {
            if (_progress is null) return;
            long now = _sw.ElapsedMilliseconds;
            if (!force && now - _lastReportMs < 70) return;
            _lastReportMs = now;

            double percent = ComputePercentLocked();
            long done = ComputeBytesDoneLocked(percent);
            double seconds = _sw.Elapsed.TotalSeconds;
            double speed = seconds > 0.05 && done > 0 ? done / seconds : 0;

            _progress.Report(new ProgressInfo
            {
                Percent = percent,
                // 收尾时 _current 已清空，但最后一次上报仍应带当前项，避免 UI 闪成空白。
                CurrentItem = _current ?? _lastItem,
                BytesDone = done,
                BytesTotal = _bytesTotal,
                SpeedBytesPerSecond = speed,
                Phase = _phase,
            });
        }

        private double ComputePercentLocked()
        {
            if (_completed) return 100;

            double percent;
            if (_itemMode && _itemsTotal > 0)
            {
                // 条目总数已知：刚开始就是 0/总数，绝不返回 -1。
                percent = _itemsDone * 100.0 / _itemsTotal;
            }
            else if (_bytePercent >= 0)
            {
                percent = _bytePercent;
            }
            else if (_bytesTotal > 0)
            {
                // 已知总字节、还没收到 7z 的百分比 → 已处理 0 字节。
                percent = 0;
            }
            else
            {
                // 条目数与字节数都未知（如 7z t 前没有列表）→ 交给 UI 走不确定态。
                return -1;
            }

            percent = Math.Min(99.0, percent);
            if (_lastPercent >= 0 && percent < _lastPercent) percent = _lastPercent;
            _lastPercent = percent;
            return percent;
        }

        private long ComputeBytesDoneLocked(double percent)
        {
            if (_completed) return _bytesTotal;
            if (_itemMode && _sawItems)
                return Math.Min(_bytesDone, _bytesTotal > 0 ? _bytesTotal : _bytesDone);
            if (percent > 0 && _bytesTotal > 0)
                return (long)(_bytesTotal * percent / 100.0);
            return _bytesTotal > 0 ? Math.Min(_bytesDone, _bytesTotal) : _bytesDone;
        }
    }
}
