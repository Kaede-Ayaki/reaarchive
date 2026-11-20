// Program.cs — 入口：GUI 模式；无界面命令行 -l / -e / -t（供自动化/QA）。
// 本程序不联网、不检查更新、无广告、无遥测：代码中不包含任何网络客户端。
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using REAArchive.Archive;
using REAArchive.Ui;

namespace REAArchive;

internal static class Program
{
    private const string ProductName = "REAArchive";
    private const string ProductVersion = "1.1.0";

    /// <summary>进程内计时（仅诊断用，环境变量 REAARCHIVE_TIMING=1 时输出）。</summary>
    internal static readonly Stopwatch Clock = Stopwatch.StartNew();

    internal static void TimingLog(string message)
    {
        if (Environment.GetEnvironmentVariable("REAARCHIVE_TIMING") != "1") return;
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "reaarchive-timing.log"),
                $"{Clock.ElapsedMilliseconds,6}ms {message}\r\n");
        }
        catch { }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (IsCliInvocation(args)) return RunCli(args);
        return RunGui(args);
    }

    private static bool IsCliInvocation(string[] args)
    {
        if (args.Length == 0) return false;
        return args[0] switch
        {
            "-l" or "--list" or "-t" => true,
            // -e 一直是 CLI；--extract 只有带显式目标目录时才走 CLI。
            // 裸 `--extract <archive>` 是右键菜单入口，必须留给 GUI 弹解压对话框（B01）。
            "-e" => true,
            "--extract" => args.Length >= 3,
            "--install-shell" or "--uninstall-shell" or "--shell-status" => true,
            _ => false,
        };
    }

    // ————————————————————————————————————————————————
    // GUI
    // ————————————————————————————————————————————————
    private const string SingleInstanceMutex = @"Local\REAArchive.SingleInstance.v1";
    private const int WmCopyData = 0x004A;
    private const int CopyDataMagic = 0x52454131; // 'REA1'

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyDataStruct
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref CopyDataStruct lParam);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static int RunGui(string[] args)
    {
        ApplicationConfiguration.Initialize();
        InstallExceptionHandlers();

        using var mutex = new Mutex(false, SingleInstanceMutex);
        bool isFirst;
        try { isFirst = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { isFirst = true; }

        if (!isFirst)
        {
            // 已有实例：把参数转发过去，保证“不开第二个窗口”。
            if (ForwardToRunningInstance(args)) return 0;
            try
            {
                MsgBox.Warn(null, "REAArchive 已在运行，但无法把命令传给已有窗口。\n请先关闭已运行的 REAArchive 再试。", "单实例转发失败");
            }
            catch { }
            return 1;
        }

        try
        {
            using var form = new MainForm(args);
            Application.Run(form);
            return 0;
        }
        catch (Exception ex)
        {
            WriteErrorLog(ex);
            try
            {
                MsgBox.Error(null, "REAArchive 启动失败。\n\n" + ex.Message +
                                   "\n\n错误详情已写入本地日志（不含密码）：\n" + ErrorLogPath, "启动失败");
            }
            catch { }
            return 1;
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch { }
        }
    }

    private static bool ForwardToRunningInstance(string[] args)
    {
        var payload = string.Join('\u0001', args);
        var bytes = Encoding.Unicode.GetBytes(payload + "\0");
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            var data = new CopyDataStruct
            {
                dwData = new IntPtr(CopyDataMagic),
                cbData = bytes.Length,
                lpData = ptr,
            };

            var current = Process.GetCurrentProcess();
            var deadline = DateTime.UtcNow.AddSeconds(6);
            while (DateTime.UtcNow < deadline)
            {
                foreach (var process in Process.GetProcessesByName(current.ProcessName))
                {
                    if (process.Id == current.Id) continue;
                    IntPtr hwnd;
                    try { hwnd = process.MainWindowHandle; }
                    catch { hwnd = IntPtr.Zero; }
                    if (hwnd == IntPtr.Zero) continue;
                    try
                    {
                        ShowWindow(hwnd, 9 /* SW_RESTORE */);
                        SetForegroundWindow(hwnd);
                        var result = SendMessage(hwnd, WmCopyData, IntPtr.Zero, ref data);
                        return result != IntPtr.Zero;
                    }
                    catch { }
                }
                Thread.Sleep(150);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
        return false;
    }

    private static void InstallExceptionHandlers()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowFatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is not Exception ex) return;
            WriteErrorLog(ex);
            if (!e.IsTerminating) ShowFatal(ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteErrorLog(e.Exception);
            e.SetObserved();
        };
    }

    private static void ShowFatal(Exception exception)
    {
        WriteErrorLog(exception);
        try
        {
            MsgBox.Error(null,
                "程序遇到一个未处理的错误，但仍在运行。\n\n" + exception.Message +
                "\n\n错误详情已写入本地日志（不含密码）：\n" + ErrorLogPath,
                "REAArchive 错误");
        }
        catch
        {
            try { MessageBox.Show(exception.Message, "REAArchive 错误"); } catch { }
        }
    }

    internal static string ErrorLogPath => Path.Combine(SettingsStore.Folder, "errors.log");

    internal static void WriteErrorLog(Exception? exception)
    {
        if (exception == null) return;
        try
        {
            Directory.CreateDirectory(SettingsStore.Folder);
            File.AppendAllText(ErrorLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception.GetType().Name}: {exception.Message}\r\n{exception.StackTrace}\r\n\r\n");
        }
        catch { /* 日志失败绝不影响主流程 */ }
    }

    // ————————————————————————————————————————————————
    // CLI
    // ————————————————————————————————————————————————
    private static int RunCli(string[] args)
    {
        CliConsole.Init();
        try
        {
            return RunCliCore(args).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            CliConsole.WriteLine("# error=internal");
            CliConsole.WriteLine("# message=" + OneLine(ex.Message));
            WriteErrorLog(ex);
            CliConsole.CompleteFallback("extract");
            return 4;
        }
    }

    private static async Task<int> RunCliCore(string[] args)
    {
        string command = args[0];
        if (command is "--install-shell" or "--uninstall-shell" or "--shell-status")
            return RunShellCommand(command);

        string? archive = null;
        string? destination = null;
        string? password = null;

        for (int i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "-p" or "--password")
            {
                if (i + 1 < args.Length) password = args[++i];
                continue;
            }
            if (arg is "-h" or "--help")
            {
                PrintUsage();
                CliConsole.CompleteFallback("cli");
                return 0;
            }
            if (arg.StartsWith('-') && arg.Length > 1)
            {
                CliConsole.WriteLine("# error=usage");
                CliConsole.WriteLine("# message=未知参数：" + OneLine(arg));
                PrintUsage();
                CliConsole.CompleteFallback("cli");
                return 1;
            }
            if (archive == null) archive = arg;
            else if (destination == null) destination = arg;
            else
            {
                CliConsole.WriteLine("# error=usage");
                PrintUsage();
                CliConsole.CompleteFallback("cli");
                return 1;
            }
        }

        if (string.IsNullOrWhiteSpace(archive))
        {
            CliConsole.WriteLine("# error=usage");
            PrintUsage();
            CliConsole.CompleteFallback("cli");
            return 1;
        }
        if (!File.Exists(archive))
        {
            CliConsole.WriteLine("# error=not_found");
            CliConsole.WriteLine("# message=归档不存在：" + OneLine(archive));
            CliConsole.CompleteFallback("cli");
            return 2;
        }
        if (command is "-e" or "--extract" && string.IsNullOrWhiteSpace(destination))
        {
            CliConsole.WriteLine("# error=usage");
            CliConsole.WriteLine("# message=-e 需要指定目标文件夹，例如：REAArchive.exe -e \"a.7z\" \"D:\\out\"");
            CliConsole.CompleteFallback("cli");
            return 1;
        }

        var service = EngineBridge.Service;
        if (service == null || !service.EngineAvailable)
        {
            CliConsole.WriteLine("# error=engine");
            CliConsole.WriteLine("# message=" + OneLine(EngineBridge.Error));
            CliConsole.CompleteFallback("cli");
            return 3;
        }

        var mode = command is "-e" or "--extract" ? "extract" : command is "-t" or "--test" ? "test" : "list";
        try
        {
            switch (command)
            {
                case "-l":
                case "--list":
                    await ListToStdoutAsync(service, archive, password);
                    CliConsole.CompleteFallback("list");
                    return 0;

                case "-e":
                case "--extract":
                {
                    // B08：解压前检查目标盘空间，避免高压缩比归档把磁盘撑爆。
                    var listing = await service.ListAsync(archive, password);
                    long free = FreeSpaceOf(destination!);
                    const long margin = 16L * 1024 * 1024;
                    if (free > 0 && listing.TotalSize + margin > free)
                    {
                        CliConsole.WriteLine("# error=disk_space");
                        CliConsole.WriteLine(
                            $"# message=预计解压后占用 {FormatUtil.Size(listing.TotalSize)}，" +
                            $"目标磁盘剩余 {FormatUtil.Size(free)}，已停止。");
                        CliConsole.CompleteFallback(mode);
                        return 4;
                    }

                    var result = await service.ExtractAsync(archive, new ExtractOptions
                    {
                        Destination = destination!,
                        Password = password,
                        Overwrite = OverwritePolicy.Overwrite,
                        CreateDestination = true,
                        PreservePaths = true,
                    });
                    CliConsole.WriteLine($"# extracted {result.Files} files, {result.Bytes} bytes, {result.Elapsed.TotalSeconds:0.000}s");
                    CliConsole.WriteLine("# dest=" + Path.GetFullPath(destination!));
                    foreach (var warning in result.Warnings.Take(20))
                        CliConsole.WriteLine("# warning=" + OneLine(warning));
                    CliConsole.WriteLine("# ok");
                    CliConsole.CompleteFallback("extract");
                    return 0;
                }

                case "-t":
                case "--test":
                {
                    var stopwatch = Stopwatch.StartNew();
                    bool ok = await service.TestAsync(archive, password);
                    stopwatch.Stop();
                    CliConsole.WriteLine($"# test={(ok ? "ok" : "failed")} elapsed={stopwatch.Elapsed.TotalSeconds:0.000}s");
                    if (ok) CliConsole.WriteLine("# ok");
                    CliConsole.CompleteFallback(mode);
                    return ok ? 0 : 4;
                }

                default:
                    CliConsole.WriteLine("# error=usage");
                    PrintUsage();
                    CliConsole.CompleteFallback("cli");
                    return 1;
            }
        }
        catch (ArchiveException ex)
        {
            var code = IsPasswordIssue(ex.Message) ? "password" : "archive";
            CliConsole.WriteLine("# error=" + code);
            CliConsole.WriteLine("# message=" + OneLine(ex.Message));
            CliConsole.CompleteFallback(mode);
            return 4;
        }
        catch (OperationCanceledException)
        {
            CliConsole.WriteLine("# error=canceled");
            CliConsole.CompleteFallback(mode);
            return 5;
        }
    }

    private static int RunShellCommand(string command)
    {
        try
        {
            switch (command)
            {
                case "--shell-status":
                {
                    bool installed = ShellIntegration.IsInstalled();
                    bool progId = ShellIntegration.ProgIdInstalled();
                    CliConsole.WriteLine("# installed=" + (installed ? "true" : "false"));
                    CliConsole.WriteLine($"# progid={ShellIntegration.ProgId} installed={(progId ? "true" : "false")}");
                    CliConsole.WriteLine("# expected_keys=" + ShellIntegration.RootKeys.Count);
                    foreach (var key in ShellIntegration.InstalledKeys())
                        CliConsole.WriteLine("# key=" + key);
                    foreach (var ext in ShellIntegration.OpenWithInstalledExtensions())
                        CliConsole.WriteLine($"# openwith={ext},{ShellIntegration.ProgId}");
                    CliConsole.WriteLine("# ok");
                    CliConsole.CompleteFallback("shell");
                    return 0;
                }

                case "--install-shell":
                {
                    var exe = Environment.ProcessPath
                              ?? Path.Combine(AppContext.BaseDirectory, "REAArchive.exe");
                    if (!ShellIntegration.Install(exe, out var error))
                    {
                        CliConsole.WriteLine("# installed=false");
                        CliConsole.WriteLine("# error=install_failed");
                        CliConsole.WriteLine("# message=" + OneLine(error));
                        CliConsole.CompleteFallback("shell");
                        return 1;
                    }

                    bool installed = ShellIntegration.IsInstalled();
                    CliConsole.WriteLine("# installed=" + (installed ? "true" : "false"));
                    CliConsole.WriteLine($"# progid={ShellIntegration.ProgId} installed={(ShellIntegration.ProgIdInstalled() ? "true" : "false")}");
                    CliConsole.WriteLine("# exe=" + exe);
                    foreach (var key in ShellIntegration.RootKeys)
                        CliConsole.WriteLine("# key=" + key);
                    CliConsole.WriteLine("# key=" + ShellIntegration.ProgId);
                    foreach (var ext in ShellIntegration.OpenWithInstalledExtensions())
                        CliConsole.WriteLine($"# openwith={ext},{ShellIntegration.ProgId}");
                    CliConsole.WriteLine(installed ? "# ok" : "# error=partial");
                    CliConsole.CompleteFallback("shell");
                    return installed ? 0 : 1;
                }

                case "--uninstall-shell":
                {
                    if (!ShellIntegration.Uninstall(out var error))
                    {
                        CliConsole.WriteLine("# installed=false");
                        CliConsole.WriteLine("# error=uninstall_failed");
                        CliConsole.WriteLine("# message=" + OneLine(error));
                        CliConsole.CompleteFallback("shell");
                        return 1;
                    }
                    CliConsole.WriteLine("# installed=false");
                    CliConsole.WriteLine($"# progid={ShellIntegration.ProgId} installed=false");
                    CliConsole.WriteLine("# residue=0");
                    CliConsole.WriteLine("# ok");
                    CliConsole.CompleteFallback("shell");
                    return 0;
                }
            }
            return 1;
        }
        catch (Exception ex)
        {
            CliConsole.WriteLine("# error=shell_exception");
            CliConsole.WriteLine("# message=" + OneLine(ex.Message));
            CliConsole.CompleteFallback("shell");
            return 1;
        }
    }

    private static async Task ListToStdoutAsync(IArchiveService service, string archive, string? password)
    {
        var result = await service.ListAsync(archive, password);
        long compressed;
        try { compressed = new FileInfo(result.ArchivePath).Length; }
        catch { compressed = result.Entries.Where(e => !e.IsDirectory && e.CompressedSize >= 0).Sum(e => e.CompressedSize); }

        // 首行固定为机器可解析的格式+条目数（QA 直接切第一行）。
        CliConsole.WriteLine(
            $"# format={FormatUtil.FormatName(result.Format).ToLowerInvariant()} entries={result.Entries.Count} " +
            $"files={result.FileCount} dirs={result.DirCount} encrypted={(result.IsEncrypted ? "yes" : "no")}");
        CliConsole.WriteLine("# archive=" + result.ArchivePath);
        CliConsole.WriteLine("# engine=" + OneLine(result.EngineVersion));
        CliConsole.WriteLine($"# size={result.TotalSize} compressed={compressed} elapsed={result.Elapsed.TotalSeconds:0.000}s");
        CliConsole.WriteLine("# columns=path\tsize\tcompressed\tmodified");

        foreach (var entry in result.Entries)
        {
            var path = entry.IsDirectory ? entry.Path.TrimEnd('/') + "/" : entry.Path;
            var size = entry.IsDirectory ? "0" : entry.Size.ToString();
            var packed = entry.CompressedSize < 0 ? "-1" : entry.CompressedSize.ToString();
            var when = entry.Modified?.ToString("yyyy-MM-dd HH:mm") ?? "-";
            CliConsole.WriteLine($"{path}\t{size}\t{packed}\t{when}");
        }

        CliConsole.WriteLine("# ok");
    }

    private static bool IsPasswordIssue(string message)
        => message.Contains("密码", StringComparison.Ordinal) ||
           message.Contains("加密", StringComparison.Ordinal) ||
           message.Contains("password", StringComparison.OrdinalIgnoreCase);

    private static long FreeSpaceOf(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? 0 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return 0;
        }
    }

    private static string OneLine(string? text)
        => (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static void PrintUsage()
    {
        CliConsole.WriteLine("# usage: REAArchive.exe [archive]");
        CliConsole.WriteLine("#        REAArchive.exe -l <archive> [-p <password>]");
        CliConsole.WriteLine("#        REAArchive.exe -e <archive> <dir> [-p <password>]");
        CliConsole.WriteLine("#        REAArchive.exe -t <archive> [-p <password>]");
    }
}

/// <summary>
/// WinExe 的命令行输出：直接运行（父控制台）与重定向/管道两种场景都要能写出 UTF-8。
/// 父控制台：AttachConsole(-1) 后打开 CONOUT$；无控制台且无重定向：AllocConsole 兜底。
/// 每种模式同时把输出落一份到 %TEMP%\REAArchive-cli-*.txt，便于自动化保底取证。
/// </summary>
internal static class CliConsole
{
    private const int StdOutputHandle = -11;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const int CodePageUtf8 = 65001;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int stdHandle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr templateFile);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleOutputCP(int codePage);

    private static TextWriter? _writer;
    private static readonly StringBuilder Log = new();
    private static readonly object Sync = new();

    public static void Init()
    {
        try
        {
            if (!AttachConsole(-1) && GetStdHandle(StdOutputHandle) == IntPtr.Zero)
                AllocConsole();
        }
        catch { }

        try
        {
            var std = GetStdHandle(StdOutputHandle);
            if (std != IntPtr.Zero && std != new IntPtr(-1))
            {
                // 管道/文件重定向（PowerShell 捕获）或继承的控制台句柄。
                var stream = new FileStream(new SafeFileHandle(std, ownsHandle: false), FileAccess.Write, 65536);
                _writer = new StreamWriter(stream, new UTF8Encoding(false), 65536) { AutoFlush = false };
                if (GetFileType(std) == 2 /* FILE_TYPE_CHAR：控制台 */) SetConsoleOutputCP(CodePageUtf8);
            }
            else if (GetConsoleWindow() != IntPtr.Zero)
            {
                // 已附着到父控制台，但 GUI 进程没有 STD 句柄：直接打开 CONOUT$。
                var handle = CreateFileW("CONOUT$", GenericRead | GenericWrite, FileShareRead | FileShareWrite,
                    IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                if (handle != new IntPtr(-1) && handle != IntPtr.Zero)
                {
                    var stream = new FileStream(new SafeFileHandle(handle, ownsHandle: true), FileAccess.Write, 65536);
                    _writer = new StreamWriter(stream, new UTF8Encoding(false), 65536) { AutoFlush = false };
                    SetConsoleOutputCP(CodePageUtf8);
                }
            }
        }
        catch
        {
            _writer = null;
        }
    }

    public static void WriteLine(string text = "")
    {
        lock (Sync) Log.AppendLine(text);
        try { _writer?.WriteLine(text); } catch { }
    }

    /// <summary>写一份保底文件（避免 GUI 子系统 stdout 丢失时 QA 完全拿不到结果）。</summary>
    public static void CompleteFallback(string mode)
    {
        try { _writer?.Flush(); } catch { }
        try
        {
            string content;
            lock (Sync) content = Log.ToString();
            var path = Path.Combine(Path.GetTempPath(), "REAArchive-cli-" + mode + ".txt");
            File.WriteAllText(path, content, new UTF8Encoding(false));
            // 标准错误通常也被父进程重定向；写失败不影响退出码。
            try { Console.Error.WriteLine("# fallback=" + path); } catch { }
        }
        catch { }
    }
}
