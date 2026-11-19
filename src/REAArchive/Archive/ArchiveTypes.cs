// ArchiveTypes.cs — 引擎与 UI 的共享契约（Lead 维护，双方都不得单方面改语义）
//
// 设计原则：
//  1) 所有归档操作都通过 IArchiveService；UI 不直接碰 7z.exe / 文件格式细节。
//  2) 全部 async + CancellationToken + IProgress，长任务可取消、可显示进度。
//  3) 本程序**不联网、不检查更新、不收集遥测**——契约里刻意不提供任何网络相关成员。
//  4) 路径统一用 Windows 本地路径（string），归档内部路径用 '/' 分隔（PosixStyle）。

namespace REAArchive.Archive;

public enum ArchiveFormat
{
    Unknown = 0,
    Zip,
    SevenZip,
    Tar,
    GZip,
    BZip2,
    Xz,
    Rar,
    Iso,
    Wim,
}

public enum OverwritePolicy
{
    Ask = 0,      // 由 UI 在遇到冲突时询问（引擎以 Ask 时报 Conflict 事件更复杂，本期退化为 Skip/Overwrite 二选一）
    Overwrite,
    Skip,
    Rename,       // 自动改名 xxx (1).ext
}

public sealed record ArchiveEntryInfo
{
    /// <summary>归档内部路径，'/' 分隔，不含前导分隔符。</summary>
    public required string Path { get; init; }
    public bool IsDirectory { get; init; }
    /// <summary>解压后大小（字节）。目录为 0。</summary>
    public long Size { get; init; }
    /// <summary>压缩后大小（字节），未知为 -1。</summary>
    public long CompressedSize { get; init; } = -1;
    public DateTime? Modified { get; init; }
    public bool Encrypted { get; init; }
    /// <summary>压缩方法（7z 的 Method 字段，如 LZMA2 / Deflate / Store）。</summary>
    public string Method { get; init; } = "";
    /// <summary>CRC32（十六进制字符串，未知为 null）。</summary>
    public string? Crc { get; init; }
}

public sealed record ListResult
{
    public required string ArchivePath { get; init; }
    public required ArchiveFormat Format { get; init; }
    public required IReadOnlyList<ArchiveEntryInfo> Entries { get; init; }
    public bool IsEncrypted { get; init; }
    /// <summary>归档自身的注释（若有）。</summary>
    public string? Comment { get; init; }
    public TimeSpan Elapsed { get; init; }
    public string EngineVersion { get; init; } = "";
    public long TotalSize => Entries.Where(e => !e.IsDirectory).Sum(e => e.Size);
    public int FileCount => Entries.Count(e => !e.IsDirectory);
    public int DirCount => Entries.Count(e => e.IsDirectory);
}

public sealed record ExtractOptions
{
    public required string Destination { get; init; }
    /// <summary>只解压这些内部路径；null/空 = 全部。</summary>
    public IReadOnlyList<string>? SelectedPaths { get; init; }
    public string? Password { get; init; }
    public OverwritePolicy Overwrite { get; init; } = OverwritePolicy.Overwrite;
    /// <summary>是否还原目录结构（false = 所有文件平铺到目标目录）。</summary>
    public bool PreservePaths { get; init; } = true;
    /// <summary>目标目录不存在时自动创建。</summary>
    public bool CreateDestination { get; init; } = true;
}

public sealed record ExtractResult
{
    public int Files { get; init; }
    public long Bytes { get; init; }
    public TimeSpan Elapsed { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public double BytesPerSecond => Elapsed.TotalSeconds > 0 ? Bytes / Elapsed.TotalSeconds : 0;
}

public sealed record CreateOptions
{
    public required string ArchivePath { get; init; }
    public ArchiveFormat Format { get; init; } = ArchiveFormat.SevenZip;
    /// <summary>0=仅存储 1=最快 5=标准 7=最大 9=极限。</summary>
    public int Level { get; init; } = 5;
    public string? Password { get; init; }
    /// <summary>7z 专用：连文件名一起加密。</summary>
    public bool EncryptHeaders { get; init; }
    /// <summary>分卷大小（字节），null = 不分卷。仅 7z/zip 支持。</summary>
    public long? VolumeSize { get; init; }
    /// <summary>要加入的磁盘路径（文件或目录）。</summary>
    public required IReadOnlyList<string> Sources { get; init; }
    /// <summary>相对根目录；null = 自动取 Sources 的公共父目录。</summary>
    public string? BaseDirectory { get; init; }
    public string? Comment { get; init; }
}

/// <summary>向**已存在**归档追加内容（7z a）。与 CreateAsync 的区别：CreateAsync 是覆盖重建。</summary>
public sealed record AddOptions
{
    public required string ArchivePath { get; init; }
    public required IReadOnlyList<string> Sources { get; init; }
    public string? Password { get; init; }
    public int Level { get; init; } = 5;
    public string? BaseDirectory { get; init; }
}

public sealed record DeleteResult
{
    public int Removed { get; init; }
    public TimeSpan Elapsed { get; init; }
}

public sealed record ProgressInfo
{
    /// <summary>0~100；无法估算时为 -1。</summary>
    public double Percent { get; init; } = -1;
    public string? CurrentItem { get; init; }
    public long BytesDone { get; init; }
    public long BytesTotal { get; init; }
    public double SpeedBytesPerSecond { get; init; }
    /// <summary>扫描 / 解压 / 压缩 / 校验 / 收尾。</summary>
    public string Phase { get; init; } = "";
}

/// <summary>归档操作失败（密码错误/文件损坏/不支持等）统一抛这个，UI 直接显示 Message。</summary>
public sealed class ArchiveException : Exception
{
    public ArchiveException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// 归档服务：本程序唯一的归档入口。
/// 实现要求：
///  - 不启动任何网络连接；不检查更新；不写注册表（注册右键菜单由 UI 的独立模块负责，且必须由用户显式确认）。
///  - 所有方法对损坏/加密/缺 7z 引擎等情况抛 ArchiveException（信息面向用户，中文）。
///  - 取消时抛 OperationCanceledException（由 CancellationToken 自然抛出）。
/// </summary>
public interface IArchiveService
{
    /// <summary>引擎自述，如 "7-Zip 25.01 (x64)"。</summary>
    string EngineVersion { get; }

    /// <summary>7z 引擎是否可用（缺失时应返回 false，UI 给出提示而不是崩溃）。</summary>
    bool EngineAvailable { get; }

    /// <summary>可用格式（用于新建对话框）。</summary>
    IReadOnlyList<ArchiveFormat> CreatableFormats { get; }

    /// <summary>按扩展名 + 文件头判断格式。</summary>
    ArchiveFormat DetectFormat(string path);

    Task<ListResult> ListAsync(string archivePath, string? password, CancellationToken ct = default);

    Task<ExtractResult> ExtractAsync(string archivePath, ExtractOptions options,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// 新建归档。**目标已存在时必须覆盖重建（不得追加）**：实现应写临时文件成功后原子替换，
    /// 避免中途失败把用户原归档毁掉。追加请用 <see cref="AddAsync"/>。
    /// </summary>
    Task<ExtractResult> CreateAsync(CreateOptions options,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default);

    /// <summary>向已存在归档追加文件/目录（7z a）。归档不存在时抛 ArchiveException（请走 CreateAsync）。</summary>
    Task<ExtractResult> AddAsync(AddOptions options,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default);

    /// <summary>从归档中删除条目（7z d）。返回删掉的条目数；条目不存在时静默跳过（Removed 不含它）。</summary>
    Task<DeleteResult> DeleteAsync(string archivePath, IReadOnlyList<string> entryPaths,
        string? password = null, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default);

    /// <summary>完整性测试（7z t）。失败抛 ArchiveException。</summary>
    Task<bool> TestAsync(string archivePath, string? password,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default);

    /// <summary>直接读取归档内某个小文件的字节（用于"双击预览"）；超过 maxBytes 抛 ArchiveException。</summary>
    Task<byte[]> ReadEntryAsync(string archivePath, string entryPath, string? password,
        int maxBytes = 8 * 1024 * 1024, CancellationToken ct = default);
}

/// <summary>格式 / 大小的友好显示（UI 与日志共用）。</summary>
public static class FormatUtil
{
    public static string Size(long bytes)
    {
        if (bytes < 0) return "—";
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.##} {u[i]}";
    }

    public static string Speed(double bytesPerSec)
        => bytesPerSec <= 0 ? "—" : Size((long)bytesPerSec) + "/s";

    public static string FormatName(ArchiveFormat f) => f switch
    {
        ArchiveFormat.Zip => "ZIP",
        ArchiveFormat.SevenZip => "7Z",
        ArchiveFormat.Tar => "TAR",
        ArchiveFormat.GZip => "GZ",
        ArchiveFormat.BZip2 => "BZ2",
        ArchiveFormat.Xz => "XZ",
        ArchiveFormat.Rar => "RAR",
        ArchiveFormat.Iso => "ISO",
        ArchiveFormat.Wim => "WIM",
        _ => "未知",
    };

    public static string ExtensionOf(ArchiveFormat f) => f switch
    {
        ArchiveFormat.Zip => ".zip",
        ArchiveFormat.SevenZip => ".7z",
        ArchiveFormat.Tar => ".tar",
        ArchiveFormat.GZip => ".gz",
        ArchiveFormat.BZip2 => ".bz2",
        ArchiveFormat.Xz => ".xz",
        _ => ".archive",
    };
}
