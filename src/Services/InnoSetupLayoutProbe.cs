using System.Diagnostics;
using System.IO;
using System.Text;

namespace UnpackStation.Services;

/// <summary>
/// Read-only layout probe for Inno Setup installers. Uses innoextract --list (never runs the setup)
/// and applies the same app\ stripping rule as InnoSetupExtractor so the computed default output
/// directory matches what extraction actually produces.
/// </summary>
internal static class InnoSetupLayoutProbe
{
    private const int MaxEntries = 512;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    internal static ArchiveLayoutInfo? TryProbe(string exePath, CancellationToken ct) =>
        Parse(ListEntries(exePath, ct));

    /// <summary>Exact number of entries innoextract will extract; used as the real progress denominator.</summary>
    internal static int? CountEntries(string exePath, CancellationToken ct)
    {
        var output = ListEntries(exePath, ct);
        if (output is null)
        {
            return null;
        }

        var count = CountNonEmptyLines(output);
        return count > 0 ? count : null;
    }

    private static int CountNonEmptyLines(string output) => output
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Count(line => !string.IsNullOrWhiteSpace(line));

    private static string? ListEntries(string exePath, CancellationToken ct)
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
        startInfo.ArgumentList.Add("--list");
        startInfo.ArgumentList.Add("-m");       // --exclude-temp, same as extraction
        startInfo.ArgumentList.Add("-n");       // --no-extract-unknown
        startInfo.ArgumentList.Add("--silent");
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

    internal static ArchiveLayoutInfo? Parse(string? output)
    {
        if (output is null)
        {
            return null;
        }

        var paths = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Replace('/', '\\').TrimStart('\\'))
            .Where(line => line.Length > 0)
            .ToList();
        if (paths.Count == 0)
        {
            return null;
        }

        // InnoSetupExtractor strips app\ only when every payload file lives under it.
        // Apply the identical rule here, otherwise the suggested output directory would contain an extra app\ level.
        if (paths.All(IsUnderApp))
        {
            paths = paths.Select(StripApp).Where(path => path.Length > 0).ToList();
        }

        var firstSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rootLevelFiles = 0;
        var validEntries = 0;
        foreach (var path in paths.Take(MaxEntries))
        {
            var separator = path.IndexOf('\\');
            if (separator < 0)
            {
                rootLevelFiles++;
            }
            else
            {
                firstSegments.Add(path[..separator]);
            }

            validEntries++;
        }

        if (validEntries == 0)
        {
            return null;
        }

        var hasSingleTopLevel = rootLevelFiles == 0 && firstSegments.Count == 1;
        return new ArchiveLayoutInfo(
            "Inno Setup",
            hasSingleTopLevel,
            firstSegments.Count == 1 ? firstSegments.First() : null,
            validEntries);
    }

    private static bool IsUnderApp(string path)
    {
        var separator = path.IndexOf('\\');
        return separator >= 0 &&
               path[..separator].Equals("app", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripApp(string path)
    {
        var separator = path.IndexOf('\\');
        return separator >= 0 ? path[(separator + 1)..] : string.Empty;
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
            System.Diagnostics.Debug.WriteLine($"Failed to kill the innoextract probe: {ex.Message}");
        }
    }
}
