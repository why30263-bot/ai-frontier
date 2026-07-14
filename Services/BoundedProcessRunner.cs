using System.Diagnostics;

namespace AIFrontier.Services;

public sealed record ProcessExecutionResult(
    int ExitCode,
    string Output,
    string Error,
    bool TimedOut,
    bool Started);

/// <summary>Runs a short-lived helper process with a hard deadline and process-tree cleanup.</summary>
public static class BoundedProcessRunner
{
    public static async Task<ProcessExecutionResult> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new(-1, string.Empty, "进程未能启动。", false, false);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new(-1, string.Empty, exception.Message, false, false);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(2)));
            return new(
                process.HasExited ? process.ExitCode : -2,
                await ReadCompletedAsync(outputTask),
                $"进程运行超过 {timeout.TotalSeconds:0} 秒，已终止。",
                true,
                true);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return new(process.ExitCode, await outputTask, await errorTask, false, true);
    }

    private static async Task<string> ReadCompletedAsync(Task<string> task)
    {
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromMilliseconds(500)));
        return completed == task ? await task : string.Empty;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process may have exited between the check and Kill.
        }
    }
}
