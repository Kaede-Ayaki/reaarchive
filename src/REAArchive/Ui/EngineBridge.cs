// EngineBridge.cs — 定位引擎实现（IArchiveService），引擎未落地时给中文提示而不是让构建/运行崩溃。
//
// 为什么用反射：UI 与引擎并行开发，引擎实现由 archive-engineer 落地在 Archive/**。
// 这里按契约找 `REAArchive.Archive.ArchiveService`（兜底：任意实现 IArchiveService 的公开类），
// 引擎一编译进来即自动生效；同时把构造函数异常转成可读中文，缺引擎时 UI 只提示不崩。
using System.Reflection;
using REAArchive.Archive;

namespace REAArchive.Ui;

internal static class EngineBridge
{
    private static readonly object Gate = new();
    private static bool _probed;
    private static IArchiveService? _service;
    private static string _error = "";

    public static IArchiveService? Service
    {
        get { EnsureProbed(); return _service; }
    }

    public static string Error
    {
        get { EnsureProbed(); return _error; }
    }

    /// <summary>引擎是否真的可以干活（存在实现 + 7z.exe 可用）。</summary>
    public static bool IsUsable
    {
        get
        {
            var svc = Service;
            return svc is { EngineAvailable: true };
        }
    }

    public static string EngineText
    {
        get
        {
            var svc = Service;
            if (svc == null) return "引擎 未就绪";
            if (!svc.EngineAvailable) return "引擎 缺失 7z.exe";
            var v = svc.EngineVersion;
            return string.IsNullOrWhiteSpace(v) ? "引擎 7-Zip" : "引擎 " + v;
        }
    }

    private static void EnsureProbed()
    {
        if (_probed) return;
        lock (Gate)
        {
            if (_probed) return;
            _probed = true;
            try
            {
                var asm = typeof(IArchiveService).Assembly;
                var types = SafeGetTypes(asm)
                    .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IArchiveService).IsAssignableFrom(t))
                    .ToList();

                if (types.Count == 0)
                {
                    _error = "归档引擎尚未就绪：程序里还没有可用的 IArchiveService 实现。\n" +
                             "请使用完整构建（含 Archive 引擎）的版本，或等待引擎组件编译完成后重新构建。";
                    return;
                }

                var type = types.FirstOrDefault(t => t.Name == "ArchiveService") ?? types[0];
                var ctor = type.GetConstructor(new[] { typeof(string) })
                           ?? type.GetConstructor(Type.EmptyTypes);
                if (ctor == null)
                {
                    _error = $"归档引擎 {type.FullName} 没有可用的构造函数，UI 无法启动引擎。";
                    return;
                }

                var args = ctor.GetParameters().Length == 0 ? null : new object?[] { null };
                _service = (IArchiveService?)ctor.Invoke(args);
                if (_service == null)
                {
                    _error = "归档引擎初始化失败（构造函数返回 null）。";
                    return;
                }

                if (!_service.EngineAvailable)
                {
                    _error = "未找到 7-Zip 引擎文件（Resources\\7z\\7z.exe）。\n" +
                             "请确认程序目录完整，未被杀毒软件隔离；然后重新启动本程序。";
                }
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                _error = "归档引擎初始化失败：" + ex.InnerException.Message;
            }
            catch (Exception ex)
            {
                _error = "归档引擎初始化失败：" + ex.Message;
            }
        }
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null)!;
        }
        catch { return Array.Empty<Type>(); }
    }
}
