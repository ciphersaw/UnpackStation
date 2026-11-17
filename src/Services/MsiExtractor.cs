using System.Diagnostics;
using System.IO;
using System.Text;
using UnpackStation.Models;

namespace UnpackStation.Services;

/// <summary>
/// Extraction of msi installer packages: uses the system's own msiexec /a to perform an administrative install.
/// Deliberately does not use 7z — extracting split packages and external CABs with 7z silently drops files, which is why msiexec is used here.
/// msiexec /a copies the msi itself into TARGETDIR; extract first to a staging directory on the same drive, delete that copy,
/// then keep the directory structure shipped with the MSI (e.g. Go\) and move it as-is into the user's output directory.
/// The target directory is usually the source file's directory, which already contains user files, so same-name items are always skipped and never overwritten.
/// Does not request administrator privileges and does not run any extracted program.
/// </summary>
public sealed class MsiExtractor : IPackageExtractor
{
    internal const string EngineName = "msiexec /a (Windows Installer)";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);
    private static readonly byte[] CompoundFileHeader = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private readonly TimeSpan _timeout;
    private readonly long _maxFileCount;
    private readonly ulong _maxTotalBytes;

    // Production code always uses CreateMsiexecStartInfo; tests can swap in a short-lived process to verify timeout, exit codes, and cleanup
    private readonly Func<string, string, string, ProcessStartInfo> _createStartInfo;

    // Reads the MSI File table entry count as the progress denominator; tests can inject a fixed value, production uses read-only P/Invoke
    private readonly Func<string, int?> _fileCountProbe;

    public MsiExtractor() : this(DefaultTimeout, null)
    {
    }

    internal MsiExtractor(
        TimeSpan timeout,
        Func<string, string, string, ProcessStartInfo>? createStartInfo,
        long maxFileCount = ExtractionLimits.MaxFileCount,
        ulong maxTotalBytes = ExtractionLimits.MaxTotalBytes,
        Func<string, int?>? fileCountProbe = null)
    {
        _timeout = timeout;
        _createStartInfo = createStartInfo ?? CreateMsiexecStartInfo;
        _maxFileCount = maxFileCount;
        _maxTotalBytes = maxTotalBytes;
        _fileCountProbe = fileCountProbe ?? MsiInstallDirectoryReader.TryReadFileCount;
    }

    /// <summary>An MSI is always an OLE compound document; check the extension first, then validate the 8-byte file header.</summary>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) ||
            !string.Equals(Path.GetExtension(filePath), ".msi", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 8);
            Span<byte> head = stackalloc byte[CompoundFileHeader.Length];
            return stream.Read(head) == head.Length && head.SequenceEqual(CompoundFileHeader);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public Task<ExtractResult> ExtractAsync(
        string sourcePath,
        string outputDir,
        IProgress<ExtractionProgress> progress,
        CancellationToken ct)
    {
        // Process waiting is asynchronous, but directory snapshots, log reading, and cleanup deletion are synchronous IO; run everything on a background thread to avoid freezing the UI
        return Task.Run(() => ExtractCoreAsync(sourcePath, outputDir, progress, ct));
    }

    /// <summary>Builds arguments according to msiexec command-line rules; targetDir must already have its trailing backslash removed.</summary>
    internal static string BuildArguments(string sourcePath, string targetDir, string logPath) =>
        $"/a \"{sourcePath}\" TARGETDIR=\"{targetDir}\" /qn /norestart /L*v \"{logPath}\"";

    /// <summary>msiexec stdout is UTF-16LE; without specifying an encoding it would be read as garbled text.</summary>
    internal static ProcessStartInfo CreateMsiexecStartInfo(string sourcePath, string targetDir, string logPath)
    {
        // Use an absolute path in the system directory so a same-named program on PATH is not invoked by mistake
        var msiexec = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe");

        return new ProcessStartInfo
        {
            FileName = msiexec,
            Arguments = BuildArguments(sourcePath, targetDir, logPath),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.Unicode,
            StandardErrorEncoding = Encoding.Unicode,
            CreateNoWindow = true
        };
    }

    /// <summary>Returns null on success (0 / 3010), otherwise a human-readable message shown to the user.</summary>
    internal static string? TranslateExitCode(int exitCode) => exitCode switch
    {
        0 => null,
        3010 => null,
        1603 => "Insufficient privileges or a problem with the output path (msiexec exit code 1603); please choose another output directory and try again.",
        _ => $"msiexec returned exit code {exitCode}"
    };

    private async Task<ExtractResult> ExtractCoreAsync(
        string sourcePath,
        string outputDir,
        IProgress<ExtractionProgress> progress,
        CancellationToken ct)
    {
        var diag = new StringBuilder();
        diag.AppendLine($"Source file: {sourcePath}");
        diag.AppendLine($"Engine: {EngineName}");
        diag.AppendLine($"Output directory: {outputDir}");
        diag.AppendLine($"Timeout: {_timeout.TotalMinutes:0} minutes");

        if (!TryNormalizeOutputDir(outputDir, out var root, out var pathError))
        {
            diag.AppendLine($"Invalid output directory: {pathError}");
            return new ExtractResult(false, outputDir, 0, pathError, diag.ToString());
        }

        // Snapshot the output directory before the call: on failure/cancel only delete newly added content and never touch the user's existing files
        var rootCreated = !Directory.Exists(root);
        var before = Snapshot(root, diag);
        var cleaned = false;

        // The staging directory must be at the same level as the target directory (same disk) so the last step can use a "rename" instead of copying tens of thousands of files
        var parentDir = Path.GetDirectoryName(root) ?? string.Empty;
        var stageDir = Path.Combine(parentDir, ".unpackstation_" + Guid.NewGuid().ToString("N")[..8]);
        var parentCreated = false;

        void CleanupOnce()
        {
            if (cleaned)
            {
                return;
            }

            cleaned = true;
            Cleanup(root, before, rootCreated, diag);
        }

        ExtractResult Fail(string message)
        {
            CleanupOnce();
            return new ExtractResult(false, root, 0, message, diag.ToString());
        }

        if (ct.IsCancellationRequested)
        {
            return Fail("Extraction canceled");
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
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            diag.AppendLine($"Failed to create the staging directory: {ex}");
            return Fail("Cannot create the temporary staging directory; please check write permissions at the output location.");
        }

        diag.AppendLine($"Staging directory: {stageDir}");
        var logPath = Path.Combine(
            Path.GetTempPath(), "UnpackStation_msi_" + Guid.NewGuid().ToString("N") + ".log");

        // Progress polling shares the msiexec process lifetime; every exit must stop it first so progress is not written back after extraction ends
        using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var progressTask = Task.CompletedTask;

        try
        {
            ProcessStartInfo startInfo;
            try
            {
                startInfo = _createStartInfo(Path.GetFullPath(sourcePath), stageDir, logPath);
            }
            catch (Exception ex)
            {
                diag.AppendLine($"Failed to build the startup arguments: {ex}");
                return Fail("Cannot start Windows Installer; see the diagnostic information.");
            }

            diag.AppendLine($"Command line: {startInfo.FileName} {startInfo.Arguments}");
            progress.Report(new ExtractionProgress(-1, ExtractionStage.Extracting, "System Windows Installer administrative install …"));

            // msiexec /a is a separate process, so the internal INSTALLMESSAGE_PROGRESS callback of Windows Installer cannot be obtained;
            // but it writes files into the staging directory one by one, so "files written to disk / File table entry count" is used as the real progress.
            // If the denominator cannot be read, do not start polling; the progress bar stays Marquee and no percentage is fabricated.
            int? expectedFileCount = null;
            try
            {
                expectedFileCount = _fileCountProbe(Path.GetFullPath(sourcePath));
            }
            catch (Exception ex)
            {
                diag.AppendLine($"Failed to read the MSI file count: {ex.Message}");
            }

            if (expectedFileCount is > 0)
            {
                diag.AppendLine($"Progress denominator: {expectedFileCount} entries in the MSI File table");
                var msiFileName = Path.GetFileName(Path.GetFullPath(sourcePath));
                progressTask = Task.Run(() => PollStageProgressAsync(
                    stageDir, expectedFileCount.Value, msiFileName, progress, progressCts.Token));
            }
            else
            {
                diag.AppendLine("Could not read the MSI File table entry count; progress stays indeterminate (Marquee)");
            }

            using var process = new Process { StartInfo = startInfo };
            var stdoutTask = Task.FromResult(string.Empty);
            var stderrTask = Task.FromResult(string.Empty);
            int exitCode;

            try
            {
                if (!process.Start())
                {
                    return Fail("Cannot start the msiexec process.");
                }

                if (startInfo.RedirectStandardOutput)
                {
                    stdoutTask = ReadAllAsync(process.StandardOutput);
                }

                if (startInfo.RedirectStandardError)
                {
                    stderrTask = ReadAllAsync(process.StandardError);
                }

                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                waitCts.CancelAfter(_timeout);

                try
                {
                    await process.WaitForExitAsync(waitCts.Token);
                }
                catch (OperationCanceledException)
                {
                    var canceledByUser = ct.IsCancellationRequested;
                    diag.AppendLine(canceledByUser
                        ? "Cancellation requested; force-killing the msiexec process tree"
                        : "Timed out waiting for msiexec; force-killing the process tree");
                    KillTree(process, diag);
                    await WaitForExitBounded(process);
                    return Fail(canceledByUser
                        ? "Extraction canceled"
                        : $"Extraction timed out (over {_timeout.TotalMinutes:0} minutes); it has been force-stopped.");
                }

                exitCode = process.ExitCode;
            }
            catch (Exception ex)
            {
                diag.AppendLine($"Exception: {ex}");
                KillTree(process, diag);
                return Fail("Extraction failed; see the diagnostic information.");
            }

            diag.AppendLine($"Exit code: {exitCode}");

            // The pipe may be held by a child process; wait at most 5 seconds and never let reading output stall the UI
            await Task.WhenAny(Task.WhenAll(stdoutTask, stderrTask), Task.Delay(TimeSpan.FromSeconds(5)));
            if (stdoutTask.IsCompletedSuccessfully && !string.IsNullOrWhiteSpace(stdoutTask.Result))
            {
                diag.AppendLine("msiexec output: " + stdoutTask.Result.Trim());
            }

            if (stderrTask.IsCompletedSuccessfully && !string.IsNullOrWhiteSpace(stderrTask.Result))
            {
                diag.AppendLine("msiexec error output: " + stderrTask.Result.Trim());
            }

            var errorMessage = TranslateExitCode(exitCode);
            if (errorMessage is not null)
            {
                // Successful MSI logs often contain unrelated DEBUG: Error lines; only excerpt on failure so as not to alarm the user
                AppendLogTail(diag, logPath);
                return Fail(errorMessage);
            }

            if (exitCode == 3010)
            {
                progress.Report(new ExtractionProgress(-1, ExtractionStage.Organizing, "Warning: the system indicates a restart is required to finish configuration, but the files have been extracted."));
                diag.AppendLine("msiexec returned 3010: success but a restart is required");
            }

            progress.Report(new ExtractionProgress(-1, ExtractionStage.Organizing, "Organizing the output directory …"));
            RemoveAdminMsiCopy(stageDir, Path.GetFileName(Path.GetFullPath(sourcePath)), diag);
            diag.AppendLine("Keeping the directory structure shipped with the installer package (no extra flattening of levels)");

            // msiexec writes all files at once and cannot be intercepted while writing; this can only be done before moving into the user's directory
            // check the staging directory and discard everything if the limit is exceeded, never letting a bomb land in the shared output directory
            var bomb = CheckStagedContent(stageDir, diag);
            if (bomb is not null)
            {
                return Fail(bomb);
            }

            try
            {
                MoveToOutput(stageDir, root, diag);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diag.AppendLine($"Failed to organize the output directory: {ex}");
                return Fail("Files were extracted, but organizing the output directory failed; see the diagnostic information.");
            }

            var newFiles = Entries(root, diag)
                .Where(entry => !entry.IsDirectory && !before.Contains(entry.RelativePath))
                .Select(entry => Path.Combine(root, entry.RelativePath))
                .ToList();
            diag.AppendLine($"Newly extracted {newFiles.Count} files");

            // Only tag executables newly extracted this run with Mark of the Web; none of the user's existing files are touched
            MarkOfTheWeb.Apply(newFiles, diag);

            progressCts.Cancel();
            await StopProgressPollingAsync(progressTask, diag);

            progress.Report(new ExtractionProgress(100, ExtractionStage.Completed, $"{newFiles.Count} files"));
            return new ExtractResult(true, root, newFiles.Count, null, diag.ToString());
        }
        finally
        {
            progressCts.Cancel();
            await StopProgressPollingAsync(progressTask, diag);
            DeleteStaging(stageDir, parentDir, parentCreated, diag);
            TryDeleteLog(logPath, diag);
        }
    }

    /// <summary>
    /// Converts "files written to disk" into a 0..99 percentage; 99 is the cap,
    /// and 100% is reserved until organizing the output directory and MoTW tagging are all done, so the bar does not reach the end early.
    /// </summary>
    internal static int ComputePercent(int fileCount, int expected)
    {
        if (expected <= 0 || fileCount <= 0)
        {
            return 0;
        }

        return (int)Math.Clamp((long)fileCount * 99 / expected, 0, 99);
    }

    /// <summary>
    /// Every 250 ms, counts the files already written to disk in the staging directory and reports real progress to the UI.
    /// An administrative install copies the source msi itself into the staging directory root; exclude it by file name so it is not counted as install content.
    /// Enumeration may throw mid-way while msiexec is rewriting the directory; just skip this round and wait for the next, without affecting extraction.
    /// The structured report carries an Extracting stage; MainViewModel throttles progress logs to one line every 5%.
    /// </summary>
    private static async Task PollStageProgressAsync(
        string stageDir,
        int expected,
        string msiFileName,
        IProgress<ExtractionProgress> progress,
        CancellationToken ct)
    {
        const int intervalMs = 250;
        var lastPercent = -1;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int count;
                try
                {
                    count = Directory.Exists(stageDir)
                        ? Directory.EnumerateFiles(stageDir, "*", SearchOption.AllDirectories)
                            .Count(path => !string.Equals(
                                Path.GetFileName(path), msiFileName, StringComparison.OrdinalIgnoreCase))
                        : 0;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    count = -1;
                }

                if (count >= 0)
                {
                    var percent = ComputePercent(count, expected);
                    if (percent > lastPercent)
                    {
                        lastPercent = percent;
                        progress.Report(new ExtractionProgress(percent, ExtractionStage.Extracting,
                            $"{Math.Min(count, expected)} / {expected} files"));
                    }
                }

                await Task.Delay(intervalMs, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Extraction finished, normal wrap-up
        }
    }

    private static async Task StopProgressPollingAsync(Task task, StringBuilder diag)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Polling being canceled is the expected path
        }
        catch (Exception ex)
        {
            diag.AppendLine($"Progress polling exception (does not affect the extraction result): {ex.Message}");
        }
    }

    /// <summary>
    /// Counts files and total bytes in the staging directory and returns a failure reason if the safe limit is exceeded.
    /// msiexec /a is a black box and can only be checked afterwards; the benefit is that the check happens before moving into the user's directory,
    /// so on failure the staging directory is simply deleted and not a single byte in the user's existing directory is changed.
    /// </summary>
    private string? CheckStagedContent(string stageDir, StringBuilder diag)
    {
        long count = 0;
        ulong total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(stageDir, "*", SearchOption.AllDirectories))
            {
                count++;
                total += (ulong)new FileInfo(file).Length;

                // Exit early to avoid enumerating the whole directory on a genuinely huge package
                if (ExtractionLimits.Check(count, total, _maxFileCount, _maxTotalBytes) is { } early)
                {
                    diag.AppendLine($"Staging directory stats: {count} files, {ExtractionLimits.FormatBytes(total)}");
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
        return null;
    }

    /// <summary>An administrative install copies the source msi into the TARGETDIR root; this file is useless to the user, so delete it.</summary>
    private static void RemoveAdminMsiCopy(string stageDir, string sourceFileName, StringBuilder diag)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(stageDir))
            {
                if (!string.Equals(Path.GetFileName(file), sourceFileName, StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(Path.GetExtension(file), ".msi", StringComparison.OrdinalIgnoreCase))
                    {
                        diag.AppendLine($"Keeping the .msi entry with a different name to avoid deleting install content by mistake: {Path.GetFileName(file)}");
                    }

                    continue;
                }

                File.Delete(file);
                diag.AppendLine($"Removed the administrative install copy {Path.GetFileName(file)}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diag.AppendLine($"Failed to remove the msi copy: {ex.Message}");
        }
    }

    /// <summary>
    /// Puts the staged content together with the directory hierarchy shipped with the MSI (e.g. Go\) into the output directory.
    /// The target directory is usually the source file's directory, which already has user files: same-name files are always skipped, same-name directories are merged recursively.
    /// </summary>
    private static void MoveToOutput(string stageDir, string root, StringBuilder diag)
    {
        // The output directory points to "the directory where the extracted result actually lives". If the package ships a top-level directory with the same name (e.g. Go\,
        // and the default output directory happens to be ...\Go), put that directory's contents directly into the output directory,
        // otherwise a duplicate level like Go\Go\bin\go.exe would appear.
        var source = FindMatchingWrapperDirectory(stageDir, root, diag) ?? stageDir;

        if (!Directory.Exists(root))
        {
            try
            {
                // A same-disk rename is a single directory-entry operation; even tens of thousands of files need no copying
                Directory.Move(source, root);
                diag.AppendLine("Extracted content has been placed into the output directory");
                return;
            }
            catch (IOException ex)
            {
                diag.AppendLine($"Renaming the whole staging directory failed; falling back to moving items one by one: {ex.Message}");
                Directory.CreateDirectory(root);
            }
        }

        var skipped = 0;
        MergeInto(source, root, root, diag, ref skipped);
        if (skipped > 0)
        {
            diag.AppendLine($"The output directory already had {skipped} files with the same name; they were skipped and not overwritten");
        }
    }

    /// <summary>
    /// Finds the layer with the same name as the output directory in the "single directory chain" and puts its contents directly into the output directory.
    /// Go is staging\Go\..., but PuTTY / Node are staging\PFiles\PuTTY\... (in the Directory table,
    /// the DefaultDir of ProgramFilesFolder is not ., so an administrative install adds one more level), so follow the single directory chain downward.
    /// Returns null if any level contains files or sibling directories, the chain is too deep, or no same-name layer is ever found; keep the package's original hierarchy and never flatten by mistake.
    /// </summary>
    private static string? FindMatchingWrapperDirectory(string stageDir, string root, StringBuilder diag)
    {
        const int maxDepth = 4;
        try
        {
            var expected = Path.GetFileName(
                Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(expected))
            {
                return null;
            }

            var current = stageDir;
            for (var depth = 0; depth < maxDepth; depth++)
            {
                if (Directory.EnumerateFiles(current).Any())
                {
                    return null;
                }

                var directories = Directory.EnumerateDirectories(current).ToArray();
                if (directories.Length != 1)
                {
                    return null;
                }

                var candidate = directories[0];
                if (string.Equals(Path.GetFileName(candidate), expected, StringComparison.OrdinalIgnoreCase))
                {
                    diag.AppendLine(
                        $"The output directory already points to the package's top-level directory {expected}\\; putting its contents directly into the output directory (to avoid a duplicate level)");
                    return candidate;
                }

                current = candidate;
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static void MergeInto(
        string source,
        string target,
        string root,
        StringBuilder diag,
        ref int skipped)
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
                    diag.AppendLine($"The output directory already has a file with the same name; skipped: {destination}");
                }

                continue;
            }

            File.Move(file, destination);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var destination = Path.Combine(target, Path.GetFileName(directory));
            if (!OutputPathValidator.IsInsideRoot(root, destination))
            {
                diag.AppendLine($"Target path escapes the output directory; skipped the whole directory: {destination}");
                continue;
            }

            if (File.Exists(destination))
            {
                diag.AppendLine($"The item with the same name in the output directory is a file; skipped the whole directory: {destination}");
                continue;
            }

            // An existing link in the output directory cannot be a destination: content would be written outside the output directory
            if (Directory.Exists(destination) && IsReparsePoint(destination, diag))
            {
                continue;
            }

            if (!Directory.Exists(destination))
            {
                try
                {
                    // When the target has no such directory, rename the whole tree; much faster than moving files one by one
                    Directory.Move(directory, destination);
                }
                catch (IOException ex)
                {
                    // A directory with tens of thousands of files is occasionally locked briefly by security software, causing the whole rename to fail;
                    // fall back to moving items one by one so the whole extraction is not wasted (observed with the Go MSI)
                    diag.AppendLine($"Moving the whole directory failed; falling back to a per-item merge of {Path.GetFileName(directory)}: {ex.Message}");
                    Directory.CreateDirectory(destination);
                    MergeInto(directory, destination, root, diag, ref skipped);
                }

                continue;
            }

            MergeInto(directory, destination, root, diag, ref skipped);
        }
    }

    private static bool IsReparsePoint(string directory, StringBuilder diag)
    {
        try
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
            {
                return false;
            }

            diag.AppendLine($"The item with the same name in the output directory is a symbolic link / junction; skipped to prevent writing outside: {directory}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            diag.AppendLine($"Cannot confirm the directory attributes; skipped {directory} out of caution: {ex.Message}");
            return true;
        }
    }

    private static void DeleteStaging(
        string stageDir,
        string parentDir,
        bool parentCreated,
        StringBuilder diag)
    {
        try
        {
            if (Directory.Exists(stageDir))
            {
                Directory.Delete(stageDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diag.AppendLine($"Failed to clean the staging directory {stageDir}: {ex.Message}");
        }

        if (!parentCreated || string.IsNullOrEmpty(parentDir))
        {
            return;
        }

        try
        {
            if (Directory.Exists(parentDir) && !Directory.EnumerateFileSystemEntries(parentDir).Any())
            {
                Directory.Delete(parentDir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diag.AppendLine($"Failed to clean the parent directory {parentDir}: {ex.Message}");
        }
    }

    /// <summary>
    /// Normalizes the output directory and removes the trailing backslash: the trailing \" in TARGETDIR="D:\out\" would be treated as an escaped quote by the command line.
    /// A drive root is rejected outright — it cannot be safely quoted, and extracting to a drive root is itself unsafe.
    /// </summary>
    internal static bool TryNormalizeOutputDir(string outputDir, out string root, out string? reason)
    {
        reason = null;
        try
        {
            root = Path.GetFullPath(outputDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            root = string.Empty;
            reason = "The output directory path is invalid; please choose again.";
            return false;
        }

        if (root.EndsWith(':'))
        {
            reason = "The output directory cannot be a drive root; please choose a subdirectory.";
            return false;
        }

        return true;
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

    private static void KillTree(Process process, StringBuilder diag)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            diag.AppendLine($"Failed to kill the msiexec process: {ex.Message}");
        }
    }

    private static async Task WaitForExitBounded(Process process)
    {
        try
        {
            // Wait a little after killing the process so file handles are released before cleaning the output directory
            await Task.WhenAny(process.WaitForExitAsync(CancellationToken.None), Task.Delay(TimeSpan.FromSeconds(5)));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to wait for the process to exit: {ex.Message}");
        }
    }

    private static void AppendLogTail(StringBuilder diag, string logPath)
    {
        if (!File.Exists(logPath))
        {
            return;
        }

        try
        {
            // The msiexec /L*v log is UTF-16LE; excerpt only error lines to avoid dumping the whole log into diagnostics
            var lines = File.ReadLines(logPath, Encoding.Unicode).ToList();
            var interesting = lines
                .Where(line => line.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                               line.Contains("failed", StringComparison.OrdinalIgnoreCase))
                .TakeLast(10)
                .ToList();
            if (interesting.Count == 0)
            {
                interesting = lines.TakeLast(3).ToList();
            }

            if (interesting.Count > 0)
            {
                diag.AppendLine("msiexec log excerpt:");
                foreach (var line in interesting)
                {
                    diag.AppendLine("  " + line.Trim());
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            diag.AppendLine($"Failed to read the msiexec log: {ex.Message}");
        }
    }

    private static void TryDeleteLog(string logPath, StringBuilder diag)
    {
        try
        {
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diag.AppendLine($"Failed to clean up the temporary log: {ex.Message}");
        }
    }

    private static HashSet<string> Snapshot(string root, StringBuilder diag)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root))
        {
            return result;
        }

        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            {
                result.Add(Path.GetRelativePath(root, path));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diag.AppendLine($"Failed to read the output directory snapshot: {ex.Message}");
        }

        return result;
    }

    private static List<(string RelativePath, bool IsDirectory)> Entries(string root, StringBuilder diag)
    {
        var result = new List<(string, bool)>();
        if (!Directory.Exists(root))
        {
            return result;
        }

        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            {
                result.Add((Path.GetRelativePath(root, path), Directory.Exists(path)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diag.AppendLine($"Failed to enumerate the output directory: {ex.Message}");
        }

        return result;
    }

    /// <summary>On failure/cancel, delete only files and directories added this run; existing directories themselves are kept.</summary>
    private static void Cleanup(
        string root,
        HashSet<string> before,
        bool rootCreated,
        StringBuilder diag)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        var entries = Entries(root, diag);

        foreach (var entry in entries)
        {
            if (entry.IsDirectory || before.Contains(entry.RelativePath))
            {
                continue;
            }

            var full = Path.Combine(root, entry.RelativePath);
            try
            {
                if (File.Exists(full))
                {
                    File.Delete(full);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diag.AppendLine($"Failed to clean up file {full}: {ex.Message}");
            }
        }

        foreach (var entry in entries
                     .Where(item => item.IsDirectory && !before.Contains(item.RelativePath))
                     .OrderByDescending(item => item.RelativePath.Length))
        {
            var full = Path.Combine(root, entry.RelativePath);
            try
            {
                if (Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any())
                {
                    Directory.Delete(full);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diag.AppendLine($"Failed to clean up directory {full}: {ex.Message}");
            }
        }

        if (rootCreated)
        {
            try
            {
                if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
                {
                    Directory.Delete(root);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diag.AppendLine($"Failed to clean the output directory {root}: {ex.Message}");
            }
        }
    }
}
