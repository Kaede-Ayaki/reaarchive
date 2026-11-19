// SevenZipEngine.cs — 定位随包 7z.exe 并解析版本号
//
// 查找顺序（找到即用）：
//   1) 构造参数 engineDir（可指向目录，也可直接指向 7z.exe）
//   2) <AppContext.BaseDirectory>\Resources\7z\7z.exe        ← 发布/构建产物标准位置
//   3) <AppContext.BaseDirectory>\7z.exe                     ← 单文件发布解包目录
//   4) 从 BaseDirectory 逐级向上，找 Resources\7z\7z.exe     ← dotnet run / 测试工程
//   5) 从 BaseDirectory 逐级向上，找 src\REAArchive\Resources\7z\7z.exe（源码树内直接跑测试）

using System.Text.RegularExpressions;

namespace REAArchive.Archive;

internal sealed class SevenZipEngine
{
    private static readonly Regex VersionRx = new(
        @"^\s*7-Zip\s+(?<ver>[0-9][0-9.]*)\s*(?<arch>\([^)]+\))?",
        RegexOptions.CultureInvariant);

    private readonly Lazy<string> _version;

    public SevenZipEngine(string? engineDir)
    {
        ExePath = ResolveExe(engineDir);
        _version = new Lazy<string>(ProbeVersion, isThreadSafe: true);
    }

    public string? ExePath { get; }

    public bool Available => ExePath is not null;

    public string Version => _version.Value;

    /// <summary>找不到引擎时抛中文 ArchiveException。</summary>
    public string RequireExe()
    {
        if (ExePath is null)
        {
            throw new ArchiveException(
                "未找到 7-Zip 引擎（Resources\\7z\\7z.exe）。请重新安装 REAArchive，或确认程序目录完整。");
        }
        return ExePath;
    }

    private string ProbeVersion()
    {
        if (ExePath is null) return "未找到 7-Zip 引擎";
        try
        {
            // 7z 无参数会打印版本横幅并返回 0。10 秒超时，避免异常环境卡住 UI。
            var res = SevenZipRunner
                .RunCaptureAsync(ExePath, Array.Empty<string>(), null, CancellationToken.None, timeoutMs: 10_000)
                .GetAwaiter().GetResult();

            foreach (string line in res.Tail)
            {
                Match m = VersionRx.Match(line);
                if (!m.Success) continue;
                string ver = m.Groups["ver"].Value;
                string arch = m.Groups["arch"].Success ? " " + m.Groups["arch"].Value : "";
                return $"7-Zip {ver}{arch}";
            }
        }
        catch
        {
            // 版本只是展示信息，失败不影响归档功能。
        }
        return "7-Zip（版本未知）";
    }

    private static string? ResolveExe(string? engineDir)
    {
        if (!string.IsNullOrWhiteSpace(engineDir))
        {
            string dir = engineDir.Trim();
            if (File.Exists(dir) && LooksLike7z(dir)) return Path.GetFullPath(dir);

            string direct = Path.Combine(dir, "7z.exe");
            if (File.Exists(direct)) return Path.GetFullPath(direct);

            string nested = Path.Combine(dir, "Resources", "7z", "7z.exe");
            if (File.Exists(nested)) return Path.GetFullPath(nested);
        }

        string baseDir = AppContext.BaseDirectory;
        string[] localCandidates =
        {
            Path.Combine(baseDir, "Resources", "7z", "7z.exe"),
            Path.Combine(baseDir, "7z", "7z.exe"),
            Path.Combine(baseDir, "7z.exe"),
        };
        foreach (string c in localCandidates)
        {
            if (File.Exists(c)) return Path.GetFullPath(c);
        }

        // 向上逐级查找（开发期 dotnet run / 测试工程直接跑的情况）。
        var dirInfo = new DirectoryInfo(baseDir);
        for (int depth = 0; dirInfo is not null && depth < 10; depth++, dirInfo = dirInfo.Parent)
        {
            string a = Path.Combine(dirInfo.FullName, "Resources", "7z", "7z.exe");
            if (File.Exists(a)) return Path.GetFullPath(a);

            string b = Path.Combine(dirInfo.FullName, "src", "REAArchive", "Resources", "7z", "7z.exe");
            if (File.Exists(b)) return Path.GetFullPath(b);
        }

        // 单文件发布：引擎内嵌在程序集里，首次使用时释放到 %LOCALAPPDATA% 再返回。
        try
        {
            string? provisioned = EngineProvisioner.TryEnsure();
            if (provisioned is not null)
            {
                string p = Path.Combine(provisioned, "7z.exe");
                if (File.Exists(p)) return Path.GetFullPath(p);
            }
        }
        catch
        {
            // 释放失败不在这里抛出：RequireExe() 会给出中文错误；版本探测等展示路径可降级。
        }

        return null;
    }

    private static bool LooksLike7z(string path)
        => string.Equals(Path.GetFileName(path), "7z.exe", StringComparison.OrdinalIgnoreCase);
}
