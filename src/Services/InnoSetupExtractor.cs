using System.Diagnostics;
using System.IO;
using System.Text;
using UnpackStation.Models;

namespace UnpackStation.Services;

/// <summary>
/// Extraction of Inno Setup installers through the bundled, zlib/libpng-licensed innoextract engine.
/// The setup executable is never run; innoextract reads its embedded data directly.
/// </summary>
public sealed class InnoSetupExtractor : IPackageExtractor
{
    internal const string EngineName = "innoextract 1.13.0 (zlib/libpng)";

    // Every Inno Setup installer carries this ASCII marker in its appended setup data,
    // e.g. "Inno Setup Setup Data (6.7.0)". It is a much stronger signal than the PE/MZ header.
    private static readonly byte[] Signature = Encoding.ASCII.GetBytes("Inno Setup Setup Data (");

    // The setup data header follows the small PE stub, so scanning the first and last 16 MB
    // is enough for real installers and avoids reading gigabyte-sized files completely.
    private const long ScanWindow = 16L * 1024 * 1024;
    private const int MaxDiagChars = 32 * 1024;
    private static readonly TimeSpan EngineTimeout = TimeSpan.FromMinutes(30);

    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) ||
            !string.Equals(Path.GetExtension(filePath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            return ContainsSignature(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public Task<ExtractResult> ExtractAsync(
        string sourcePath,
        string outputDir,
        IProgress<(int percent, string message)> progress,
        CancellationToken ct)
    {
        return Task.Run(() => ExtractCore(sourcePath, outputDir, progress, ct));
    }

    internal static string EnginePath => Path.Combine(
        AppContext.BaseDirectory, "engines", "innoextract", "x64", "innoextract.exe");

    /// <summary>True when the file carries an Inno Setup setup-data marker in the first or last scan window.</summary>
    internal static bool ContainsSignature(string filePath)
    {
        using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
        var length = stream.Length;
        if (ContainsInRange(stream, 0, Math.Min(length, ScanWindow)))
        {
            return true;
        }

        return length > ScanWindow &&
               ContainsInRange(stream, length - ScanWindow, ScanWindow);
    }

    private static bool ContainsInRange(FileStream stream, long start, long length)
    {
        const int chunk = 64 * 1024;
        var buffer = new byte[chunk + Signature.Length];
        var carry = 0;
        stream.Position = start;

        while (length > 0)
        {
            var read = stream.Read(buffer, carry, (int)Math.Min(chunk, length));
            if (read == 0)
            {
                break;
            }

            length -= read;
            var total = carry + read;
            for (var i = 0; i <= total - Signature.Length; i++)
            {
                var matched = true;
                for (var j = 0; j < Signature.Length; j++)
                {
                    if (buffer[i + j] != Signature[j])
                    {
                        matched = false;
                        break;
                    }
                }

                if (matched)
                {
                    return true;
                }
            }

            carry = Math.Min(Signature.Length - 1, total);
            if (carry > 0)
            {
                Buffer.BlockCopy(buffer, total - carry, buffer, 0, carry);
            }
        }

        return false;
    }

    private static async Task<ExtractResult> ExtractCore(
        string sourcePath,
        string outputDir,
        IProgress<(int percent, string message)> progress,
        CancellationToken ct)
    {
        var diag = new StringBuilder();
        diag.AppendLine($"Source file: {sourcePath}");
        diag.AppendLine($"Engine: {EngineName}");
        diag.AppendLine($"Output directory: {outputDir}");

        string root;
        try
        {
            root = Path.GetFullPath(outputDir);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            diag.AppendLine($"Invalid output directory: {ex}");
            return new ExtractResult(false, outputDir, 0, "The output directory path is invalid; please choose again.", diag.ToString());
        }

        var parentDir = Path.GetDirectoryName(root) ?? string.Empty;
        var stageDir = Path.Combine(parentDir, ".unpackstation_inno_" + Guid.NewGuid().ToString("N")[..8]);
        var parentCreated = false;
        var stageCreated = false;
        var movedFiles = new List<string>();

        void CleanupStage()
        {
            if (!stageCreated)
            {
                return;
            }

            stageCreated = false;
            try
            {
                if (Directory.Exists(stageDir))
                {
                    Directory.Delete(stageDir, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diag.AppendLine($"Failed to clean up the staging directory: {ex.Message}");
            }

            if (parentCreated && !string.IsNullOrEmpty(parentDir))
            {
                try
                {
                    if (Directory.Exists(parentDir) && !Directory.EnumerateFileSystemEntries(parentDir).Any())
                    {
                        Directory.Delete(parentDir);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    diag.AppendLine($"Failed to clean up the parent directory: {ex.Message}");
                }
            }
        }

        ExtractResult Fail(string message)
        {
            CleanupStage();
            return new ExtractResult(false, root, 0, message, diag.ToString());
        }

        if (ct.IsCancellationRequested)
        {
            return Fail("Extraction canceled");
        }

        var engine = EnginePath;
        if (!File.Exists(engine))
        {
            diag.AppendLine($"Engine not found: {engine}");
            return Fail("The Inno Setup engine is missing; please keep engines/innoextract/x64/innoextract.exe next to the program.");
        }

        if (!File.Exists(sourcePath))
        {
            return Fail("The source file could not be found; please select an installer package again.");
        }

        try
        {
            if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
            {
                Directory.CreateDirectory(parentDir);
                parentCreated = true;
            }

            Directory.CreateDirectory(stageDir);
            stageCreated = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            diag.AppendLine($"Failed to create the staging directory: {ex}");
            return Fail("Cannot create the temporary staging directory; please check write permissions at the output location.");
        }

        diag.AppendLine($"Staging directory: {stageDir}");
        diag.AppendLine($"Engine path: {engine}");

        // innoextract has no machine-readable progress stream, so count staged files against
        // the exact entry count from its read-only --list output (same rule as the MSI engine).
        int? expectedFileCount = null;
        try
        {
            expectedFileCount = InnoSetupLayoutProbe.CountEntries(Path.GetFullPath(sourcePath), ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            diag.AppendLine($"Failed to read the Inno Setup entry count: {ex.Message}");
        }

        if (expectedFileCount is > 0)
        {
            diag.AppendLine($"Progress denominator: {expectedFileCount} entries from innoextract --list");
        }
        else
        {
            diag.AppendLine("Could not read the Inno Setup entry count; progress stays indeterminate (Marquee)");
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
        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add(stageDir);
        startInfo.ArgumentList.Add("-m");       // --exclude-temp: never extract installer runtime files
        startInfo.ArgumentList.Add("-n");       // --no-extract-unknown: fail clearly on future unknown formats
        startInfo.ArgumentList.Add("--progress=0");
        startInfo.ArgumentList.Add("--silent");
        startInfo.ArgumentList.Add(Path.GetFullPath(sourcePath));

        diag.AppendLine($"Command line: {engine} -d \"{stageDir}\" -m -n --progress=0 --silent \"{Path.GetFullPath(sourcePath)}\"");
        progress.Report((-1, "Extracting Inno Setup package with innoextract …"));

        using var process = new Process { StartInfo = startInfo };
        var stdoutTask = Task.FromResult(string.Empty);
        var stderrTask = Task.FromResult(string.Empty);

        try
        {
            if (!process.Start())
            {
                return Fail("Cannot start the Inno Setup extraction engine.");
            }

            stdoutTask = ReadAllAsync(process.StandardOutput);
            stderrTask = ReadAllAsync(process.StandardError);

            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waitCts.CancelAfter(EngineTimeout);
            try
            {
                var waitTask = process.WaitForExitAsync(waitCts.Token);
                while (!waitTask.IsCompleted)
                {
                    await Task.WhenAny(waitTask, Task.Delay(300));
                    if (waitTask.IsCompleted || expectedFileCount is not > 0)
                    {
                        continue;
                    }

                    var written = CountStagedFiles(stageDir);
                    var percent = (int)Math.Min(99, written * 100L / expectedFileCount.Value);
                    progress.Report((percent, $"Extracted {written} of {expectedFileCount.Value} files"));
                }

                await waitTask;
            }
            catch (OperationCanceledException)
            {
                var canceledByUser = ct.IsCancellationRequested;
                diag.AppendLine(canceledByUser
                    ? "Cancellation requested; force-killing the innoextract process"
                    : $"Timed out after {EngineTimeout.TotalMinutes:0} minutes; force-killing the innoextract process");
                KillTree(process, diag);
                await WaitForExitBounded(process);
                return Fail(canceledByUser
                    ? "Extraction canceled"
                    : "Extraction timed out; it has been force-stopped.");
            }
        }
        catch (Exception ex)
        {
            diag.AppendLine($"Exception: {ex}");
            KillTree(process, diag);
            return Fail("Extraction failed; see the diagnostic information.");
        }

        await Task.WhenAny(Task.WhenAll(stdoutTask, stderrTask), Task.Delay(TimeSpan.FromSeconds(5)));
        AppendOutput(diag, "innoextract output", stdoutTask);
        AppendOutput(diag, "innoextract error output", stderrTask);
        diag.AppendLine($"Exit code: {process.ExitCode}");

        if (process.ExitCode != 0)
        {
            var combined = (stdoutTask.Result ?? string.Empty) + (stderrTask.Result ?? string.Empty);
            if (combined.Contains("password", StringComparison.OrdinalIgnoreCase))
            {
                return Fail("This installer package is encrypted and requires a password");
            }

            return Fail("Extraction failed; see the diagnostic information.");
        }

        progress.Report((-1, "Organizing the output directory …"));

        var bomb = CheckStagedContent(stageDir, diag);
        if (bomb is not null)
        {
            return Fail(bomb);
        }

        var payloadRoot = FindPayloadRoot(stageDir, diag);
        try
        {
            MoveToOutput(payloadRoot, root, diag, movedFiles);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diag.AppendLine($"Failed to organize the output directory: {ex}");
            RollbackMovedFiles(movedFiles, diag);
            return Fail("Files were extracted, but organizing the output directory failed; see the diagnostic information.");
        }

        MarkOfTheWeb.Apply(movedFiles, diag);
        CleanupStage();
        progress.Report((100, $"Done, {movedFiles.Count} files"));
        return new ExtractResult(true, root, movedFiles.Count, null, diag.ToString());
    }

    private static int CountStagedFiles(string stageDir)
    {
        try
        {
            return Directory.EnumerateFiles(stageDir, "*", SearchOption.AllDirectories).Count();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }
    }

    private static string? CheckStagedContent(string stageDir, StringBuilder diag)
    {
        long count = 0;
        ulong total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(stageDir, "*", SearchOption.AllDirectories))
            {
                count++;
                total += (ulong)new FileInfo(file).Length;
                if (ExtractionLimits.Check(count, total) is { } early)
                {
                    return early;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            diag.AppendLine($"Failed to tally the staging directory: {ex.Message}");
            return "Cannot confirm the size of the extracted content; extraction has been aborted for safety.";
        }

        diag.AppendLine($"Staging directory stats: {count} files, {ExtractionLimits.FormatBytes(total)}");
        return count == 0 ? "The installer package contains no files that can be extracted." : null;
    }

    /// <summary>innoextract puts installed files under app\; expose that as the output directory itself.</summary>
    private static string FindPayloadRoot(string stageDir, StringBuilder diag)
    {
        var appDir = Path.Combine(stageDir, "app");
        if (!Directory.Exists(appDir))
        {
            return stageDir;
        }

        var all = Directory.EnumerateFiles(stageDir, "*", SearchOption.AllDirectories).ToArray();
        if (all.Length > 0 && all.All(path => IsUnderApp(path, stageDir)))
        {
            diag.AppendLine("innoextract placed the installed files under app\\; extracting that directory into the output directory");
            return appDir;
        }

        return stageDir;
    }

    private static bool IsUnderApp(string path, string stageDir)
    {
        var relative = Path.GetRelativePath(stageDir, path).Replace('/', '\\');
        return relative.StartsWith("app\\", StringComparison.OrdinalIgnoreCase);
    }

    private static void MoveToOutput(string source, string root, StringBuilder diag, List<string> movedFiles)
    {
        if (!Directory.Exists(root))
        {
            try
            {
                Directory.Move(source, root);
                movedFiles.AddRange(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
                diag.AppendLine("Extracted content has been placed into the output directory");
                return;
            }
            catch (IOException ex)
            {
                diag.AppendLine($"Renaming the staged directory failed; falling back to moving items one by one: {ex.Message}");
                Directory.CreateDirectory(root);
            }
        }

        var skipped = 0;
        MergeInto(source, root, root, diag, ref skipped, movedFiles);
        if (skipped > 0)
        {
            diag.AppendLine($"The output directory already had {skipped} files with the same name; they were skipped and not overwritten");
        }
    }

    private static void MergeInto(
        string source,
        string target,
        string root,
        StringBuilder diag,
        ref int skipped,
        List<string> movedFiles)
    {
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var destination = Path.Combine(target, Path.GetFileName(file));
            if (!OutputPathValidator.IsInsideRoot(root, destination))
            {
                diag.AppendLine($"Target path escapes the output directory; skipped: {destination}");
                continue;
            }

            if (File.Exists(destination))
            {
                skipped++;
                if (skipped <= 10)
                {
                    diag.AppendLine($"The output directory already had a file with the same name; skipped: {destination}");
                }

                continue;
            }

            File.Move(file, destination);
            movedFiles.Add(destination);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var destination = Path.Combine(target, Path.GetFileName(directory));
            if (!OutputPathValidator.IsInsideRoot(root, destination))
            {
                diag.AppendLine($"Target path escapes the output directory; skipped the whole directory: {destination}");
                continue;
            }

            Directory.CreateDirectory(destination);
            MergeInto(directory, destination, root, diag, ref skipped, movedFiles);
        }
    }

    private static void RollbackMovedFiles(List<string> files, StringBuilder diag)
    {
        foreach (var file in files)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diag.AppendLine($"Failed to roll back {file}: {ex.Message}");
            }
        }
    }

    private static void AppendOutput(StringBuilder diag, string label, Task<string> task)
    {
        if (!task.IsCompletedSuccessfully || string.IsNullOrWhiteSpace(task.Result))
        {
            return;
        }

        var text = task.Result.Trim();
        if (text.Length > MaxDiagChars)
        {
            text = text[..MaxDiagChars] + " …";
        }

        diag.AppendLine($"{label}: {text}");
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

    private static async Task WaitForExitBounded(Process process)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void KillTree(Process process, StringBuilder diag)
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
            diag.AppendLine($"Failed to kill the innoextract process: {ex.Message}");
        }
    }
}
