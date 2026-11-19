// EngineProvisioner.cs — 单文件发布时，把内嵌的 7-Zip 引擎释放到用户缓存目录
//
// 为什么需要：7z.exe 必须是磁盘上的真实文件才能被 Process 启动，而单文件发布只有一个 exe。
// 因此把 7z.exe / 7z.dll 作为 EmbeddedResource 编译进程序集，首次用到时释放到：
//     %LOCALAPPDATA%\REAArchive\engine\<7z.exe 的 SHA256 前 8 位>\
// 目录名带内容哈希：引擎升级后自动换目录，不会误用旧引擎；内容一致则跳过写入。
// 本类不联网、不写注册表；只写用户自己的 LocalAppData。
using System.Reflection;
using System.Security.Cryptography;

namespace REAArchive.Archive;

internal static class EngineProvisioner
{
    private const string ExeResource = "REAArchive.engine.7z.exe";
    private const string DllResource = "REAArchive.engine.7z.dll";

    private static readonly object Gate = new();
    private static string? _cachedDir;

    /// <summary>
    /// 返回内嵌引擎所在目录（必要时释放）；若当前构建没有内嵌引擎（例如开发期目录构建），返回 null。
    /// 失败会抛出带中文说明的 ArchiveException，交由上层转成用户可读错误。
    /// </summary>
    public static string? TryEnsure()
    {
        lock (Gate)
        {
            if (_cachedDir is not null)
            {
                return Directory.Exists(_cachedDir) ? _cachedDir : null;
            }

            Assembly asm = Assembly.GetExecutingAssembly();
            byte[]? exe = ReadResource(asm, ExeResource);
            byte[]? dll = ReadResource(asm, DllResource);
            if (exe is null || dll is null) return null;   // 目录构建：走本地 Resources\7z

            string tag = Convert.ToHexString(SHA256.HashData(exe)).ToLowerInvariant()[..8];
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "REAArchive", "engine", tag);

            try
            {
                Directory.CreateDirectory(root);
                WriteIfChanged(Path.Combine(root, "7z.exe"), exe);
                WriteIfChanged(Path.Combine(root, "7z.dll"), dll);
            }
            catch (Exception ex)
            {
                throw new ArchiveException(
                    "无法释放内置的 7-Zip 引擎到 " + root + "：" + ex.Message +
                    "。请确认当前用户对 %LOCALAPPDATA% 有写权限。", ex);
            }

            _cachedDir = root;
            return root;
        }
    }

    private static byte[]? ReadResource(Assembly asm, string name)
    {
        using Stream? s = asm.GetManifestResourceStream(name);
        if (s is null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static void WriteIfChanged(string path, byte[] data)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length == data.Length)
            {
                byte[] existing = File.ReadAllBytes(path);
                if (SHA256.HashData(existing).AsSpan().SequenceEqual(SHA256.HashData(data))) return;
            }
        }
        catch
        {
            // 读不到/读一半都按"需要重写"处理
        }

        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(tmp, data);
        try
        {
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* 清理失败不影响主错误 */ }
            throw;
        }
    }
}
