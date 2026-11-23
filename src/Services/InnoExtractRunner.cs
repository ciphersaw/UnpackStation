using System.Diagnostics;
using System.IO;
using System.Text;

namespace UnpackStation.Services;

/// <summary>
/// Runs the bundled innoextract in read-only mode. Shared by the entry-layout probe and the
/// installer-name probe so both use the same timeout, encoding and process-cleanup rules.
/// </summary>
internal static class InnoExtractRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Returns stdout on success, or null when the engine is missing, fails, or times out.</summary>
    internal static string? Run(string exePath, CancellationToken ct, params string[] arguments)
    {
        var engine = InnoSetupExtractor.EnginePath;
        if (!File.Exists(engine))
        {
            return null;
        }

        var startInfo = new ProcessStartInfo(engine)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.ArgumentList.Add(exePath);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return null;
            }

            var stdoutTask = ReadAllAsync(process.StandardOutput);
            var stderrTask = ReadAllAsync(process.StandardError);

            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waitCts.CancelAfter(Timeout);
            try
            {
                process.WaitForExitAsync(waitCts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                KillTree(process);
                process.WaitForExit(5000);
                return null;
            }

            Task.WhenAny(Task.WhenAll(stdoutTask, stderrTask), Task.Delay(2000))
                .GetAwaiter().GetResult();
            return process.ExitCode == 0 && stdoutTask.IsCompletedSuccessfully
                ? stdoutTask.Result
                : null;
        }
        catch (Exception)
        {
            KillTree(process);
            return null;
        }
    }

    private static async Task<string> ReadAllAsync(StreamReader reader)
    {
        try
        {
            return await reader.ReadToEndAsync();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return string.Empty;
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Failed to kill the innoextract probe: {ex.Message}");
        }
    }
}
