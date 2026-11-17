using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnpackStation.Models;
using UnpackStation.Services.SevenZip;

namespace UnpackStation.Services;

/// <summary>
/// Extraction of exe installer packages: dynamically loads 7z.dll and lets 7-Zip detect the format by file content (NSIS / CAB SFX / plain SFX / 7z / Zip, etc.).
/// Does not run the installer package, register COM, or depend on a system-installed 7-Zip.
/// </summary>
public sealed class SevenZipExtractor : IPackageExtractor
{
    internal const string EngineName = "7z.dll (7-Zip 26.01 x64)";
    private const string EncryptedMessage = "This installer package is encrypted and requires a password";
    private const string UnrecognizedMessage =
        "Could not recognize the packaging format of this exe " +
        "(possibly InstallShield or another unsupported format)";

    // An SFX PE header is usually only a few hundred KB; limit the signature scan range to avoid a full scan of GB-sized files
    private const long MaxSignatureScan = 16L * 1024 * 1024;

    // 7z.dll can open more than 60 formats, but the payload of an exe installer package can only be the archive / SFX formats below.
    // 7-Zip can also open formats like PE, COFF, TE, ELF, disk images, and single-stream compression, but what it extracts is
    // executable sections / file system data (the "pile of gibberish" the user sees), which must never be the extraction result.
    // Ordered by "most like an installer package"; still relies on 7-Zip content signatures to Open each one, not guessing by extension.
    private static readonly string[] InstallerFormats =
    [
        "Nsis", "Cab", "7z", "zip", "Rar5", "Rar", "wim",
        "tar", "Cpio", "Ar", "Xar", "Rpm", "Lzh", "Arj",
        "Iso", "Udf", "Chm", "Hxs", "Dmg", "SquashFS", "CramFS"
    ];

    /// <summary>Whether this format can carry the file list of an exe installer package.</summary>
    internal static bool IsInstallerFormat(string name) =>
        InstallerFormats.Any(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Installer package formats come first so NSIS / CAB SFX are tried before image formats like PE.</summary>
    internal static List<SevenZipFormat> OrderForProbing(IEnumerable<SevenZipFormat> formats) =>
        formats.OrderBy(format =>
        {
            var rank = Array.FindIndex(
                InstallerFormats,
                name => string.Equals(name, format.Name, StringComparison.OrdinalIgnoreCase));
            return rank < 0 ? int.MaxValue : rank;
        }).ToList();

    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) ||
            !string.Equals(Path.GetExtension(filePath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 2);
            Span<byte> head = stackalloc byte[2];
            return stream.Read(head) == 2 && head[0] == (byte)'M' && head[1] == (byte)'Z';
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
        // All 7z.dll calls are blocking COM calls; they must run on a background thread so the UI does not freeze
        return Task.Run(() => ExtractCore(sourcePath, outputDir, progress, ct));
    }

    private static ExtractResult ExtractCore(
        string sourcePath,
        string outputDir,
        IProgress<ExtractionProgress> progress,
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

        var rootCreated = false;
        var createdDirs = new List<string>();
        var createdFiles = new List<string>();
        var cleaned = false;
        IInArchive? archive = null;

        void CleanupOnce()
        {
            if (cleaned)
            {
                return;
            }

            cleaned = true;
            Cleanup(root, rootCreated, createdFiles, createdDirs, diag);
        }

        ExtractResult Fail(string message, int hr = 0)
        {
            if (hr != 0)
            {
                diag.AppendLine($"Exit code / HRESULT: 0x{hr:X8}");
            }

            CleanupOnce();
            return new ExtractResult(false, root, 0, message, diag.ToString());
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(new ExtractionProgress(-1, ExtractionStage.Preparing, "Loading the 7-Zip engine …"));
            SevenZipNative.EnsureLoaded();
            diag.AppendLine($"Engine path: {SevenZipNative.LibraryPath}");

            if (!File.Exists(sourcePath))
            {
                return Fail("The source file could not be found; please select an installer package again.");
            }

            using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            var inStream = new InStreamWrapper(input);

            rootCreated = !Directory.Exists(root);
            CreateDirectoryTracked(root, root, createdDirs);

            progress.Report(new ExtractionProgress(-1, ExtractionStage.Preparing, "Detecting the packaging format …"));
            var formats = OrderForProbing(SevenZipNative.EnumerateFormats());
            diag.AppendLine($"7-Zip has {formats.Count} available formats, {formats.Count(f => IsInstallerFormat(f.Name))} installer/archive formats");
            string? matchedFormat = null;
            var imageOnlyMatches = new List<string>();
            var lastHr = 0;

            foreach (var format in formats)
            {
                ct.ThrowIfCancellationRequested();

                var isInstaller = IsInstallerFormat(format.Name);
                var candidate = SevenZipNative.CreateInArchive(format.ClassId);
                if (candidate is null)
                {
                    continue;
                }

                var openCallback = new OpenCallback();
                var hr = HResults.EFail;
                var opened = false;
                try
                {
                    input.Position = 0;
                    ulong maxCheck = (ulong)Math.Min(input.Length, MaxSignatureScan);
                    hr = candidate.Open(inStream, ref maxCheck, openCallback);
                    opened = hr == HResults.SOk;
                }
                catch (Exception ex)
                {
                    diag.AppendLine($"Format {format.Name} open exception: {ex.GetType().Name}: {ex.Message}");
                }

                if (openCallback.PasswordRequested)
                {
                    diag.AppendLine($"7-Zip requested a password when opening as {format.Name}");
                    CloseAndRelease(candidate, opened);
                    if (isInstaller)
                    {
                        return Fail(EncryptedMessage, hr);
                    }

                    continue;
                }

                if (opened && isInstaller)
                {
                    archive = candidate;
                    matchedFormat = format.Name;
                    break;
                }

                if (opened)
                {
                    // Record but do not use: image / disk / single-stream formats can be opened, but the product is not install content
                    imageOnlyMatches.Add(format.Name);
                }

                lastHr = hr;
                CloseAndRelease(candidate, opened);
            }

            if (archive is null)
            {
                diag.AppendLine($"Tried {formats.Count} formats; no usable installer/archive format was found");
                if (imageOnlyMatches.Count > 0)
                {
                    diag.AppendLine(
                        $"7-Zip could only open as {string.Join(", ", imageOnlyMatches)} (executable image / disk image / single-stream compression); skipped, image sections are not extracted.");
                    diag.AppendLine("Such exe files are usually InstallShield or a setup.exe wrapping an MSI.");
                }

                return Fail(UnrecognizedMessage, lastHr);
            }

            progress.Report(new ExtractionProgress(-1, ExtractionStage.Preparing, $"Identified as a {matchedFormat} installer package"));
            diag.AppendLine($"Identified format: {matchedFormat}");

            // Read entries one by one and validate target paths before allowing 7-Zip to write any file
            var itemCount = archive.GetNumberOfItems();
            var entryLimit = ExtractionLimits.Check(itemCount, 0);
            if (entryLimit is not null)
            {
                diag.AppendLine(entryLimit);
                return Fail(entryLimit);
            }

            var isNsis = string.Equals(matchedFormat, "Nsis", StringComparison.OrdinalIgnoreCase);
            var warnings = new List<string>();

            // First normalize all entry paths so the "single top-level directory" decision and the write plan share the same rules.
            // null means a directory entry / empty path / NSIS installer runtime file; these do not take part in writing to disk.
            var entryPaths = new string?[itemCount];
            var runtimeSkipped = 0;
            var runtimeFallback = new List<(uint Index, string Path)>();
            for (uint index = 0; index < itemCount; index++)
            {
                ct.ThrowIfCancellationRequested();
                if (ReadBool(archive, index, ItemPropId.IsFolder) ||
                    ReadBool(archive, index, ItemPropId.IsAnti))
                {
                    // Directories are created automatically from the parent of the file path; anti entries are 7z deletion markers and are ignored in the current version
                    continue;
                }

                var rawPath = ReadString(archive, index, ItemPropId.Path);
                if (string.IsNullOrWhiteSpace(rawPath))
                {
                    warnings.Add($"Entry #{index} has an empty path; skipped");
                    continue;
                }

                var decision = InstallerEntryPath.Normalize(rawPath, isNsis);
                if (decision.Skip)
                {
                    runtimeSkipped++;
                    if (!string.IsNullOrEmpty(decision.RelativePath))
                    {
                        runtimeFallback.Add((index, decision.RelativePath));
                    }

                    continue;
                }

                entryPaths[index] = decision.RelativePath;
            }

            // Conservative fallback: if the package has only installer runtime entries, keep the paths after stripping pseudo-directories rather than returning empty-handed
            if (isNsis && runtimeSkipped > 0 &&
                entryPaths.All(path => path is null) &&
                runtimeFallback.Count > 0)
            {
                foreach (var (index, path) in runtimeFallback)
                {
                    entryPaths[index] = path;
                }

                diag.AppendLine("The package has only installer runtime entries; the paths after stripping pseudo-directories are kept");
            }

            if (runtimeSkipped > 0)
            {
                diag.AppendLine($"Skipped {runtimeSkipped} NSIS installer runtime entries (plugins / wizard resources / temp files)");
            }

            var plan = new Dictionary<uint, string>();
            var plannedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ulong totalBytes = 0;
            var encrypted = false;
            var checkedParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var blockedParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // The output directory points to "the directory where the extracted result actually lives". When all files in the package hang under a single top-level directory
            // and that directory name is exactly the output directory name, extraction drops this segment to avoid App\App\readme.txt.
            var topLevelToStrip = FindMatchingTopLevel(entryPaths, root);
            if (topLevelToStrip is not null)
            {
                diag.AppendLine($"The output directory already points to the package's single top-level directory {topLevelToStrip}\\; extracted files are placed directly into the output directory");
            }

            for (uint index = 0; index < itemCount; index++)
            {
                ct.ThrowIfCancellationRequested();

                if (ReadBool(archive, index, ItemPropId.Encrypted))
                {
                    encrypted = true;
                }

                var entryPath = entryPaths[index];
                if (entryPath is null)
                {
                    continue;
                }

                if (topLevelToStrip is not null)
                {
                    entryPath = StripTopLevelSegment(entryPath, topLevelToStrip);
                    if (string.IsNullOrWhiteSpace(entryPath))
                    {
                        warnings.Add($"Entry #{index} has only the top-level directory name left; skipped: {topLevelToStrip}");
                        continue;
                    }
                }

                if (!OutputPathValidator.TryResolve(root, entryPath, out var target, out var reason))
                {
                    warnings.Add($"Entry #{index} skipped ({reason}): {entryPath}");
                    continue;
                }

                // A valid string does not mean a safe destination: existing symbolic links / junctions in the output directory would send the file elsewhere
                var parentDir = Path.GetDirectoryName(target);
                if (parentDir is not null && !checkedParents.Contains(parentDir))
                {
                    checkedParents.Add(parentDir);
                    if (OutputPathValidator.HasReparsePointInParents(root, target, out var linkReason))
                    {
                        blockedParents.Add(parentDir);
                        diag.AppendLine($"Directory {parentDir} was blocked: {linkReason}");
                    }
                }

                if (parentDir is not null && blockedParents.Contains(parentDir))
                {
                    warnings.Add($"Entry #{index} target directory is a symbolic link / junction; skipped: {entryPath}");
                    continue;
                }

                // The default output directory is the source file's directory, which already has user files: never overwrite
                if (File.Exists(target))
                {
                    warnings.Add($"Entry #{index} target already exists; skipped without overwriting: {entryPath}");
                    continue;
                }

                if (!plannedTargets.Add(target))
                {
                    warnings.Add($"Entry #{index} has the same name as another entry in the package; skipped: {entryPath}");
                    continue;
                }

                var size = ReadUInt64(archive, index, ItemPropId.Size);
                var nextTotal = size > ulong.MaxValue - totalBytes ? ulong.MaxValue : totalBytes + size;
                // Declared-value check: block obvious bombs before writing to disk; actual bytes are checked again in the callback
                var limit = ExtractionLimits.Check(plan.Count + 1, nextTotal);
                if (limit is not null)
                {
                    diag.AppendLine($"Entry #{index} triggered the limit: {entryPath} ({size} bytes)");
                    return Fail(limit);
                }

                plan[index] = target;
                createdFiles.Add(target);
                totalBytes = nextTotal;
            }

            foreach (var warning in warnings)
            {
                progress.Report(new ExtractionProgress(-1, ExtractionStage.Preparing, "Warning: " + warning));
                diag.AppendLine("Warning: " + warning);
            }

            if (encrypted)
            {
                diag.AppendLine("The archive contains encrypted entries");
                return Fail(EncryptedMessage);
            }

            if (plan.Count == 0)
            {
                return Fail("No files in the package can be safely extracted (see the warnings).");
            }

            foreach (var parent in plan.Values
                         .Select(path => Path.GetDirectoryName(path))
                         .Where(path => !string.IsNullOrEmpty(path))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(path => path!.Length))
            {
                CreateDirectoryTracked(parent!, root, createdDirs);
            }

            diag.AppendLine($"Planned to extract {plan.Count} files, {totalBytes} bytes total ({ExtractionLimits.FormatBytes(totalBytes)})");

            var indices = plan.Keys.OrderBy(index => index).ToArray();

            var budget = new ExtractionBudget();
            ExtractCallback callback;
            int extractHr;
            using (callback = new ExtractCallback(plan, progress, ct, totalBytes, budget))
            {
                extractHr = archive.Extract(indices, (uint)indices.Length, 0, callback);
            }

            diag.AppendLine($"Extract HRESULT: 0x{extractHr:X8}");
            diag.AppendLine($"Actually wrote {callback.WrittenFileCount} files, actually extracted {ExtractionLimits.FormatBytes(budget.Bytes)}");
            foreach (var error in callback.Errors)
            {
                diag.AppendLine("Error: " + error);
            }

            if (budget.Exceeded)
            {
                diag.AppendLine(budget.Reason ?? "Extraction budget exceeded");
                return Fail(budget.Reason ?? "Extraction aborted: the safety limit was exceeded.", extractHr);
            }

            if (callback.WrongPassword)
            {
                return Fail(EncryptedMessage, extractHr);
            }

            if (ct.IsCancellationRequested || extractHr == HResults.EAbort)
            {
                return Fail("Extraction canceled", extractHr);
            }

            if (extractHr != HResults.SOk || callback.Errors.Count > 0 || callback.WrittenFileCount == 0)
            {
                return Fail("Extraction failed; see the diagnostic information.", extractHr);
            }

            // Only tag executables actually written this run with Mark of the Web; never touch the user's existing files
            progress.Report(new ExtractionProgress(-1, ExtractionStage.Organizing, "Finalizing extracted files …"));
            MarkOfTheWeb.Apply(callback.WrittenFiles, diag);

            progress.Report(new ExtractionProgress(100, ExtractionStage.Completed, $"{callback.WrittenFileCount} files"));
            return new ExtractResult(true, root, callback.WrittenFileCount, null, diag.ToString());
        }
        catch (OperationCanceledException)
        {
            diag.AppendLine("Operation canceled");
            return Fail("Extraction canceled");
        }
        catch (Exception ex)
        {
            diag.AppendLine($"Exception: {ex}");
            return Fail("Extraction failed; see the diagnostic information.");
        }
        finally
        {
            if (archive is not null)
            {
                try
                {
                    archive.Close();
                }
                catch
                {
                    // A close failure does not affect the result already obtained
                }

                ReleaseArchive(archive);
            }
        }
    }

    /// <summary>Unused candidate archives are Closed before releasing the COM reference so 7-Zip's internal cache does not hold the source file.</summary>
    private static void CloseAndRelease(IInArchive candidate, bool opened)
    {
        if (opened)
        {
            try
            {
                candidate.Close();
            }
            catch
            {
                // A close failure does not affect probing other formats
            }
        }

        ReleaseArchive(candidate);
    }

    private static void ReleaseArchive(object? archive)
    {
        if (archive is null)
        {
            return;
        }

        try
        {
            Marshal.ReleaseComObject(archive);
        }
        catch (Exception ex)
        {
            // Do not throw again when the RCW is already invalid; leave it to the GC
            System.Diagnostics.Debug.WriteLine($"Failed to release the RCW: {ex.Message}");
        }
    }

    private static void CreateDirectoryTracked(string dir, string root, List<string> created)
    {
        if (Directory.Exists(dir))
        {
            return;
        }

        // Record every directory level created this run from the top down; on cancel delete only what was created here
        var missing = new Stack<string>();
        var current = dir;
        while (!Directory.Exists(current))
        {
            missing.Push(current);
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current ||
                !parent.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }

        while (missing.Count > 0)
        {
            var path = missing.Pop();
            Directory.CreateDirectory(path);
            created.Add(path);
        }
    }

    /// <summary>On failure/cancel, delete files and directories written this run; never delete content of directories the user already had.</summary>
    private static void Cleanup(
        string root,
        bool rootCreated,
        List<string> createdFiles,
        List<string> createdDirs,
        StringBuilder diag)
    {
        foreach (var file in createdFiles)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex)
            {
                diag.AppendLine($"Failed to clean up file {file}: {ex.Message}");
            }
        }

        foreach (var dir in createdDirs
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(path => path.Length))
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                }
            }
            catch (Exception ex)
            {
                diag.AppendLine($"Failed to clean up directory {dir}: {ex.Message}");
            }
        }

        if (rootCreated)
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (Exception ex)
            {
                diag.AppendLine($"Failed to clean the output directory {root}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Finds the package's single top-level directory name, returned only when it matches the output directory name.
    /// Returns null if there are root-level files, several different top-level directories, or the name does not match; keep the package's original hierarchy.
    /// </summary>
    private static string? FindMatchingTopLevel(string?[] entryPaths, string root)
    {
        var expected = Path.GetFileName(
            Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(expected))
        {
            return null;
        }

        string? common = null;
        foreach (var path in entryPaths)
        {
            if (path is null)
            {
                continue;
            }

            var normalized = path.Replace('/', '\\').TrimStart('\\');
            var separator = normalized.IndexOf('\\');
            if (separator < 0)
            {
                // Files directly at the root do not count as a single top-level directory
                return null;
            }

            var first = normalized[..separator];
            if (common is null)
            {
                common = first;
            }
            else if (!string.Equals(common, first, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return string.Equals(common, expected, StringComparison.OrdinalIgnoreCase) ? common : null;
    }

    /// <summary>Removes the leading segment of the entry path that matches the output directory name; returns the path unchanged when there is no match.</summary>
    internal static string StripTopLevelSegment(string entryPath, string topLevel)
    {
        var normalized = entryPath.Replace('/', '\\').TrimStart('\\');
        return normalized.StartsWith(topLevel + "\\", StringComparison.OrdinalIgnoreCase)
            ? normalized[(topLevel.Length + 1)..]
            : entryPath;
    }

    internal static string? ReadString(IInArchive archive, uint index, ItemPropId propId)
    {
        var value = new PropVariant();
        try
        {
            archive.GetProperty(index, propId, ref value);
            return value.Vt == PropVariant.VtBstr && value.Pointer != IntPtr.Zero
                ? Marshal.PtrToStringUni(value.Pointer)
                : null;
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            value.Clear();
        }
    }

    internal static bool ReadBool(IInArchive archive, uint index, ItemPropId propId)
    {
        var value = new PropVariant();
        try
        {
            archive.GetProperty(index, propId, ref value);
            return value.AsBool();
        }
        catch (COMException)
        {
            return false;
        }
        finally
        {
            value.Clear();
        }
    }

    private static ulong ReadUInt64(IInArchive archive, uint index, ItemPropId propId)
    {
        var value = new PropVariant();
        try
        {
            archive.GetProperty(index, propId, ref value);
            return value.AsUInt64();
        }
        catch (COMException)
        {
            return 0;
        }
        finally
        {
            value.Clear();
        }
    }
}
