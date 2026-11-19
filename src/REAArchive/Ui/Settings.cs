// Settings.cs — 用户设置（只存本地 JSON，不含任何密码/网络配置）
using System.Text;
using System.Text.Json;
using REAArchive.Archive;

namespace REAArchive.Ui;

/// <summary>持久化设置：默认解压目录、冲突策略、解压后打开、临时目录、字号。</summary>
internal sealed class AppSettings
{
    public string DefaultExtractDir { get; set; } = "";
    /// <summary>Overwrite / Skip / Rename（序列化为字符串，便于人读与向前兼容）。</summary>
    public string ConflictPolicy { get; set; } = nameof(OverwritePolicy.Overwrite);
    public bool OpenFolderAfterExtract { get; set; }
    public string TempDir { get; set; } = "";
    public float FontSize { get; set; } = 9f;
    public bool CleanTempOnExit { get; set; } = true;
    /// <summary>快速压缩（右键/--compress-7z/zip）使用的默认级别：0/1/5/7/9。</summary>
    public int CompressionLevel { get; set; } = 5;

    public OverwritePolicy GetOverwritePolicy() => ConflictPolicy switch
    {
        nameof(OverwritePolicy.Skip) => OverwritePolicy.Skip,
        nameof(OverwritePolicy.Rename) => OverwritePolicy.Rename,
        _ => OverwritePolicy.Overwrite,
    };

    public void SetOverwritePolicy(OverwritePolicy policy) => ConflictPolicy = policy.ToString();

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    /// <summary>临时目录解析：空 = %TEMP%\REAArchive，支持 %ENV% 变量。</summary>
    public string ResolvedTempDir()
    {
        var raw = string.IsNullOrWhiteSpace(TempDir)
            ? Path.Combine(Path.GetTempPath(), "REAArchive")
            : Environment.ExpandEnvironmentVariables(TempDir.Trim());
        try { return Path.GetFullPath(raw); }
        catch { return Path.Combine(Path.GetTempPath(), "REAArchive"); }
    }

    public AppSettings Normalize()
    {
        FontSize = Math.Clamp(FontSize <= 0 ? 9f : FontSize, 8f, 14f);
        CompressionLevel = CompressionLevel is 0 or 1 or 5 or 7 or 9 ? CompressionLevel : 5;
        if (!Enum.TryParse<OverwritePolicy>(ConflictPolicy, ignoreCase: true, out var policy) ||
            policy == OverwritePolicy.Ask)
        {
            ConflictPolicy = nameof(OverwritePolicy.Overwrite);
        }
        return this;
    }
}

internal static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // 设置文件只给本程序读写，不涉及任何远程/遥测字段
    };

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "REAArchive");

    public static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            var json = File.ReadAllText(FilePath, Encoding.UTF8);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            return (loaded ?? new AppSettings()).Normalize();
        }
        catch
        {
            // 设置损坏时静默回退默认值，绝不因设置文件阻断启动
            return new AppSettings();
        }
    }

    public static bool Save(AppSettings settings, out string? error)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings.Normalize(), JsonOptions), new UTF8Encoding(false));
            File.Move(tmp, FilePath, overwrite: true);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
