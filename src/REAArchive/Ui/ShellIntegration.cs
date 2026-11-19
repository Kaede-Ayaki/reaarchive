// ShellIntegration.cs — 可逆的 HKCU 右键菜单集成（扁平菜单 + ProgID + OpenWith）
//
// 实测：Win11 的经典右键菜单没有把 SubCommands / ExtendedSubCommandsKey 渲染成级联子菜单，
// 因此按 lead 允许的退路改为“前缀区分的平铺菜单项”，保证真能点：
//   REAArchive: 打开 / 解压到当前文件夹 / 解压到同名文件夹 / 解压到… / 测试压缩包
// 只写 HKCU，不改文件关联、不自启、不联网。
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace REAArchive.Ui;

internal static class ShellIntegration
{
    public static readonly string[] ArchiveExtensions =
    {
        ".7z", ".zip", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".iso", ".wim",
    };

    public const string ProgId = "REAArchive.Archive";

    private static readonly (string Id, string Title, string Arguments)[] ArchiveVerbs =
    {
        ("Open", "REAArchive: 打开(&O)", "\"%1\""),
        ("ExtractHere", "REAArchive: 解压到当前文件夹(&X)", "--extract-here \"%1\""),
        ("ExtractTo", "REAArchive: 解压到同名文件夹(&E)", "--extract-to \"%1\""),
        ("ExtractAsk", "REAArchive: 解压到…", "--extract-ask \"%1\""),
        ("Test", "REAArchive: 测试压缩包(&T)", "--test \"%1\""),
    };

    private static readonly (string Id, string Title, string Arguments)[] FileVerbs =
    {
        ("Compress7z", "REAArchive: 压缩为 7z(&7)", "--compress-7z \"%1\""),
        ("CompressZip", "REAArchive: 压缩为 ZIP(&Z)", "--compress-zip \"%1\""),
        ("CompressAsk", "REAArchive: 添加到压缩包…(&A)", "--compress \"%1\""),
    };

    private static readonly (string Id, string Title, string Arguments)[] BackgroundVerbs =
    {
        ("Compress7z", "REAArchive: 压缩此文件夹为 7z(&7)", "--compress-7z \"%V\""),
        ("CompressZip", "REAArchive: 压缩此文件夹为 ZIP(&Z)", "--compress-zip \"%V\""),
    };

    private static readonly string[] LegacyKeys =
    {
        @"*\shell\REAArchive.Open",
        @"*\shell\REAArchive.Extract",
        @"*\shell\REAArchive.Compress",
        @"Directory\shell\REAArchive.Compress",
    };

    /// <summary>旧版级联父键（v1 实验）与实验性 CommandStore 键，安装/卸载时一并清理。</summary>
    private static readonly List<string> LegacyCascadeKeys = BuildLegacyCascadeKeys();

    private static List<string> BuildLegacyCascadeKeys()
    {
        var keys = new List<string>();
        foreach (var ext in ArchiveExtensions)
            keys.Add(@"SystemFileAssociations\" + ext + @"\shell\REAArchive");
        keys.Add(@"*\shell\REAArchive");
        keys.Add(@"Directory\shell\REAArchive");
        keys.Add(@"Directory\Background\shell\REAArchive");
        keys.Add(@"REAArchive.Cascade.Archive");
        return keys;
    }

    public static IReadOnlyList<string> RootKeys { get; } = BuildRootKeys();

    private static string ProgIdPath => @"Software\Classes\" + ProgId;

    private static string OpenWithPath(string ext) =>
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + ext + @"\OpenWithProgids";

    private static List<string> BuildRootKeys()
    {
        var keys = new List<string>();
        foreach (var ext in ArchiveExtensions)
            foreach (var verb in ArchiveVerbs)
                keys.Add(@"SystemFileAssociations\" + ext + @"\shell\REAArchive." + verb.Id);
        foreach (var verb in FileVerbs)
        {
            keys.Add(@"*\shell\REAArchive." + verb.Id);
            keys.Add(@"Directory\shell\REAArchive." + verb.Id);
        }
        foreach (var verb in BackgroundVerbs)
            keys.Add(@"Directory\Background\shell\REAArchive." + verb.Id);
        return keys;
    }

    public static string RegistryPreview
    {
        get
        {
            var lines = new List<string> { "HKEY_CURRENT_USER\\Software\\Classes\\" };
            foreach (var ext in ArchiveExtensions)
                lines.Add(@"  SystemFileAssociations\" + ext + @"\shell\REAArchive.*   (5 项)");
            lines.Add(@"  *\shell\REAArchive.*");
            lines.Add(@"  Directory\shell\REAArchive.*");
            lines.Add(@"  Directory\Background\shell\REAArchive.*");
            lines.Add(@"  REAArchive.Archive   (默认打开方式 ProgID)");
            lines.Add("");
            lines.Add("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\FileExts\\<ext>\\OpenWithProgids");
            lines.Add("  REAArchive.Archive   （10 个扩展名的“打开方式”候选）");
            lines.Add("");
            lines.Add("（仅写当前用户 HKCU；不改文件关联、不自启、可一键卸载）");
            return string.Join("\n", lines);
        }
    }

    public static bool IsInstalled()
    {
        try { return RootKeys.All(KeyExists) && ProgIdInstalled(); }
        catch { return false; }
    }

    public static bool ProgIdInstalled()
        => KeyExistsPath(ProgIdPath) && OpenWithInstalled();

    public static IReadOnlyList<string> InstalledKeys()
    {
        var result = new List<string>();
        foreach (var key in RootKeys)
        {
            try { if (KeyExists(key)) result.Add(key); }
            catch { }
        }
        if (KeyExistsPath(ProgIdPath)) result.Add(ProgId);
        return result;
    }

    public static IReadOnlyList<string> OpenWithInstalledExtensions()
    {
        var installed = new List<string>();
        foreach (var ext in ArchiveExtensions)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(OpenWithPath(ext));
                if (key?.GetValue(ProgId) != null) installed.Add(ext);
            }
            catch { }
        }
        return installed;
    }

    private static bool OpenWithInstalled()
        => OpenWithInstalledExtensions().Count == ArchiveExtensions.Length;

    public static bool Install(string exePath, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            error = "程序路径不存在：" + exePath;
            return false;
        }

        try
        {
            var exe = "\"" + exePath + "\"";
            foreach (var ext in ArchiveExtensions)
            {
                foreach (var verb in ArchiveVerbs)
                    WriteVerb(@"SystemFileAssociations\" + ext + @"\shell", "REAArchive." + verb.Id, verb.Title, verb.Arguments, exePath);
            }
            foreach (var verb in FileVerbs)
            {
                WriteVerb(@"*\shell", "REAArchive." + verb.Id, verb.Title, verb.Arguments, exePath);
                WriteVerb(@"Directory\shell", "REAArchive." + verb.Id, verb.Title, verb.Arguments, exePath);
            }
            foreach (var verb in BackgroundVerbs)
                WriteVerb(@"Directory\Background\shell", "REAArchive." + verb.Id, verb.Title, verb.Arguments, exePath);

            // ProgID + 10 个扩展名的“打开方式”候选（不改 UserChoice——受系统保护）。
            using (var progId = Registry.CurrentUser.CreateSubKey(ProgIdPath, writable: true)
                               ?? throw new IOException("无法创建 ProgID：" + ProgId))
            {
                progId.SetValue(null, "REAArchive 压缩包");
                using (var icon = progId.CreateSubKey("DefaultIcon", writable: true)
                                  ?? throw new IOException("无法创建 DefaultIcon"))
                    icon.SetValue(null, exe + ",0");
                using var open = progId.CreateSubKey(@"shell\open\command", writable: true)
                                 ?? throw new IOException("无法创建 ProgID 打开命令");
                open.SetValue(null, exe + " \"%1\"");
            }

            foreach (var ext in ArchiveExtensions)
            {
                using var key = Registry.CurrentUser.CreateSubKey(OpenWithPath(ext), writable: true)
                                ?? throw new IOException("无法创建 OpenWithProgids：" + ext);
                try { key.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None); }
                catch { key.SetValue(ProgId, ""); }
            }

            // 清理旧版平铺键、实验级联父键与 CommandStore 实验项
            foreach (var legacy in LegacyKeys.Concat(LegacyCascadeKeys)) TryDeleteKey(legacy);
            foreach (var commandStore in ArchiveVerbs.Select(v => v.Id))
                TryDeleteKeyPath(@"Software\Microsoft\Windows\CurrentVersion\Explorer\CommandStore\shell\REA." + commandStore);

            NotifyShell();
            return true;
        }
        catch (Exception ex)
        {
            error = "写入右键菜单失败：" + ex.Message;
            return false;
        }
    }

    public static bool Uninstall(out string error)
    {
        error = "";
        try
        {
            foreach (var key in RootKeys) DeleteTree(key);
            foreach (var legacy in LegacyKeys.Concat(LegacyCascadeKeys)) DeleteTree(legacy);
            foreach (var commandStore in ArchiveVerbs.Select(v => v.Id))
                TryDeleteKeyPath(@"Software\Microsoft\Windows\CurrentVersion\Explorer\CommandStore\shell\REA." + commandStore);
            DeleteTreePath(ProgIdPath);

            foreach (var ext in ArchiveExtensions)
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(OpenWithPath(ext), writable: true);
                    key?.DeleteValue(ProgId, throwOnMissingValue: false);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
                {
                    throw new IOException("删除 OpenWithProgids 失败（权限不足）：" + ext, ex);
                }
            }

            NotifyShell();

            var residue = Residue();
            if (residue.Count > 0)
            {
                error = "仍有残留注册表项：" + string.Join("；", residue);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = "卸载右键菜单失败：" + ex.Message;
            return false;
        }
    }

    private static List<string> Residue()
    {
        var residue = new List<string>();
        foreach (var key in RootKeys.Concat(LegacyKeys).Concat(LegacyCascadeKeys))
            if (KeyExists(key)) residue.Add(key);
        if (KeyExistsPath(ProgIdPath)) residue.Add(ProgIdPath);
        foreach (var ext in ArchiveExtensions)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(OpenWithPath(ext));
                if (key?.GetValue(ProgId) != null) residue.Add("OpenWithProgids" + ext);
            }
            catch { }
        }
        return residue;
    }

    private static void WriteVerb(string shellKey, string id, string title, string arguments, string exePath)
    {
        var exe = "\"" + exePath + "\"";
        using var verb = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + shellKey + @"\" + id, writable: true)
                         ?? throw new IOException("无法创建菜单项：" + id);
        verb.SetValue("MUIVerb", title);
        verb.SetValue("Icon", exe);
        using var command = verb.CreateSubKey("command", writable: true)
                            ?? throw new IOException("无法创建命令：" + id);
        command.SetValue(null, exe + " " + arguments);
    }

    private static bool KeyExists(string relativeKey)
        => KeyExistsPath(@"Software\Classes\" + relativeKey);

    private static bool KeyExistsPath(string path)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            return key != null;
        }
        catch { return false; }
    }

    private static void DeleteTree(string relativeKey)
        => DeleteTreePath(@"Software\Classes\" + relativeKey);

    private static void DeleteTreePath(string path)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new IOException("删除注册表项失败（权限不足）：" + path, ex);
        }
    }

    private static void TryDeleteKey(string relativeKey)
    {
        try { DeleteTree(relativeKey); } catch { }
    }

    private static void TryDeleteKeyPath(string path)
    {
        try { DeleteTreePath(path); } catch { }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);

    private static void NotifyShell()
    {
        try { SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero); }
        catch { /* 仅刷新资源管理器缓存 */ }
    }
}
