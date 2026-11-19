// SevenZipRunner.cs — 7z.exe 子进程封装（引擎层内部使用）
//
// 设计要点：
//  - 一律 CreateNoWindow + 重定向 stdout/stderr/stdin，stdout/stderr 用 UTF-8 解码。
//  - 不弹黑窗、不等待用户输入：调用方**始终**传 -p（空密码也传裸 -p），并且启动后立即关闭 stdin，
//    这样即使归档加密也不会出现 7z 阻塞在 "Enter password" 上。
//  - 取消：CancellationToken 注册 Process.Kill(entireProcessTree: true)，保证不留僵尸 7z。
//  - 输出按行流式回调（列表 10 万条目时不把整段输出堆在内存里），只保留末尾若干行用于错误诊断。

using System.Diagnostics;
using System.Text;

namespace REAArchive.Archive;

internal sealed class SevenZipRunResult
{
    public int ExitCode { get; init; }
    public bool Canceled { get; init; }
    public bool TimedOut { get; init; }
    public string StdErr { get; init; } = "";
    public IReadOnlyList<string> Tail { get; init; } = Array.Empty<string>();

    /// <summary>尾部输出 + stderr，供错误映射使用（不用于展示给用户）。</summary>
    public string Diagnostic => string.Join("\n", Tail) + (StdErr.Length > 0 ? "\n" + StdErr : "");
}

internal readonly record struct SevenZipBinaryResult(
    int ExitCode, byte[] Data, bool Truncated, bool Canceled, string StdErr);

internal static class SevenZipRunner
{
    private const int TailLimit = 400;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>启动 7z 并逐行回调 stdout；stderr 全文捕获。</summary>
    public static async Task<SevenZipRunResult> RunAsync(
        string exe,
        IReadOnlyList<string> args,
        string? workingDirectory,
        Action<string>? onLine,
        CancellationToken ct,
        int timeoutMs = 0)
    {
        ct.ThrowIfCancellationRequested();

        var psi = BuildStartInfo(exe, args, workingDirectory);
        using var proc = new Process { StartInfo = psi };

        bool canceled = false;
        bool timedOut = false;

        try
        {
            if (!proc.Start())
                throw new ArchiveException("无法启动 7-Zip 引擎（7z.exe）。");

            // 永远不等待密码输入：stdin 立刻 EOF。
            try { proc.StandardInput.Close(); } catch { /* 已退出 */ }

            using var timeoutCts = timeoutMs > 0 ? new CancellationTokenSource(timeoutMs) : null;
            using var reg = ct.Register(() =>
            {
                canceled = true;
                KillTree(proc);
            });
            using var regTimeout = timeoutCts?.Token.Register(() =>
            {
                timedOut = true;
                KillTree(proc);
            });

            var tail = new Queue<string>(TailLimit);
            var stderrTask = ReadAllAsync(proc.StandardError, CancellationToken.None);
            var stdoutTask = Task.Run(async () =>
            {
                string? line;
                while ((line = await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    string clean = CleanLine(line);

                    // 空行必须交给回调：7z -slt 用空行分隔条目块，吞掉它会导致只解析出最后一个条目。
                    try { onLine?.Invoke(clean); } catch { /* 回调异常不得影响进程收尾 */ }

                    if (clean.Length == 0) continue;
                    lock (tail)
                    {
                        if (tail.Count == TailLimit) tail.Dequeue();
                        tail.Enqueue(clean);
                    }
                }
            }, CancellationToken.None);

            await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

            // Kill(true) 之后主进程可能还没被 OS 完全回收；取消/超时场景再同步确认一次，
            // 避免调用方紧接着检查时看到“残留进程”（其实是收尾中的进程）。
            if (canceled || timedOut)
                await Task.Run(() => proc.WaitForExit(5000)).ConfigureAwait(false);

            string stderr = stderrTask.Result;
            string[] tailArr;
            lock (tail) tailArr = tail.ToArray();

            if (canceled) throw new OperationCanceledException(ct);
            if (timedOut) throw new ArchiveException("7-Zip 引擎响应超时。");

            return new SevenZipRunResult
            {
                ExitCode = proc.ExitCode,
                StdErr = stderr,
                Tail = tailArr,
            };
        }
        finally
        {
            KillTree(proc);
        }
    }

    /// <summary>读取 7z 的二进制 stdout（用于 7z e -so 单文件预览）。</summary>
    public static async Task<SevenZipBinaryResult> RunBinaryAsync(
        string exe,
        IReadOnlyList<string> args,
        string? workingDirectory,
        int maxBytes,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var psi = BuildStartInfo(exe, args, workingDirectory);
        psi.StandardOutputEncoding = null; // 二进制读取走 BaseStream
        using var proc = new Process { StartInfo = psi };

        bool canceled = false;
        bool truncated = false;
        try
        {
            if (!proc.Start())
                throw new ArchiveException("无法启动 7-Zip 引擎（7z.exe）。");
            try { proc.StandardInput.Close(); } catch { }

            using var reg = ct.Register(() => { canceled = true; KillTree(proc); });

            var stderrTask = ReadAllAsync(proc.StandardError, CancellationToken.None);
            using var ms = new MemoryStream();
            byte[] buf = new byte[81920];
            Stream stdout = proc.StandardOutput.BaseStream;
            while (true)
            {
                int n = await stdout.ReadAsync(buf, CancellationToken.None).ConfigureAwait(false);
                if (n <= 0) break;
                if (ms.Length + n > maxBytes)
                {
                    truncated = true;
                    KillTree(proc);
                    break;
                }
                ms.Write(buf, 0, n);
            }

            await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);

            if (canceled) throw new OperationCanceledException(ct);
            return new SevenZipBinaryResult(
                truncated ? -1 : proc.ExitCode,
                truncated ? Array.Empty<byte>() : ms.ToArray(),
                truncated, false, stderr);
        }
        finally
        {
            KillTree(proc);
        }
    }

    /// <summary>一次性运行并返回尾部输出（用于版本探测等短命令）。</summary>
    public static Task<SevenZipRunResult> RunCaptureAsync(
        string exe, IReadOnlyList<string> args, string? workingDirectory, CancellationToken ct, int timeoutMs = 0)
        => RunAsync(exe, args, workingDirectory, null, ct, timeoutMs);

    private static ProcessStartInfo BuildStartInfo(string exe, IReadOnlyList<string> args, string? workingDirectory)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        if (!string.IsNullOrEmpty(workingDirectory)) psi.WorkingDirectory = workingDirectory;
        return psi;
    }

    private static async Task<string> ReadAllAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return "";
        }
        catch (ObjectDisposedException)
        {
            return "";
        }
    }

    internal static void KillTree(Process proc)
    {
        // Kill(entireProcessTree) 只是“请求”终止；重试 + 有界等待，保证取消返回时不留下 7z 进程。
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (proc.HasExited) return;
                proc.Kill(entireProcessTree: true);
                if (proc.WaitForExit(1500)) return;
            }
            catch
            {
                return; // 已退出 / 无权限：不再重试
            }
        }
    }

    /// <summary>去掉 7z 进度输出里的退格与其它控制字符，便于按行解析。</summary>
    private static string CleanLine(string raw)
    {
        if (raw.IndexOf('\b') < 0)
        {
            bool hasControl = false;
            foreach (char c in raw)
            {
                if (char.IsControl(c) && c != '\t') { hasControl = true; break; }
            }
            if (!hasControl) return raw.TrimEnd();
        }

        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            if (c == '\b' || c == '\0') continue;
            if (c == '\t') { sb.Append(' '); continue; }
            if (char.IsControl(c)) continue;
            sb.Append(c);
        }
        return sb.ToString().TrimEnd();
    }
}
