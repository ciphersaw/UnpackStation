using System.IO;
using System.Runtime.InteropServices;
using UnpackStation.Services.SevenZip;

namespace UnpackStation.Services;

/// <summary>The exe package layout probed in read-only mode.</summary>
internal sealed record ArchiveLayoutInfo(
    string FormatName,
    bool HasSingleTopLevelDirectory,
    string? TopLevelName,
    int InspectedEntryCount);

/// <summary>
/// Read-only listing of entry paths in an exe archive to determine whether there is a "single top-level directory".
/// NSIS $INSTDIR and Inno DefaultDirName are compiled into the install script and are not exposed by 7-Zip,
/// so the EXE side cannot read the install path and can only rely on the entry structure to decide whether to add an isolation directory.
/// Nothing is extracted or written to disk; only IInArchive::Open + GetProperty are called.
/// </summary>
internal static class ArchiveLayoutProbe
{
    // Determining a single top-level directory does not require reading the whole package; for large packages (tens of thousands of files), reading the first few hundred entries is enough
    private const int MaxEntries = 512;

    // Keep consistent with SevenZipExtractor: an SFX PE header is usually only a few hundred KB, so limit the signature scan range
    private const long MaxSignatureScan = 16L * 1024 * 1024;

    /// <summary>Returns null on probe failure; the caller conservatively falls back to the source file's directory.</summary>
    internal static ArchiveLayoutInfo? TryProbe(string exePath, CancellationToken ct)
    {
        try
        {
            // Inno Setup installers are not readable by 7z.dll; let the bundled innoextract
            // engine list their entries read-only so the default output directory can still be derived.
            if (string.Equals(Path.GetExtension(exePath), ".exe", StringComparison.OrdinalIgnoreCase) &&
                InnoSetupExtractor.ContainsSignature(exePath))
            {
                return InnoSetupLayoutProbe.TryProbe(exePath, ct);
            }

            return ProbeCore(exePath, ct);
        }
        catch (Exception)
        {
            // A missing engine, unsupported format, encrypted package, or locked file should not affect file selection
            return null;
        }
    }

    private static ArchiveLayoutInfo? ProbeCore(string exePath, CancellationToken ct)
    {
        SevenZipNative.EnsureLoaded();
        using var input = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        using var inStream = new InStreamWrapper(input);

        var formats = SevenZipExtractor.OrderForProbing(SevenZipNative.EnumerateFormats());
        IInArchive? matched = null;
        string? matchedName = null;

        foreach (var format in formats)
        {
            ct.ThrowIfCancellationRequested();
            if (!SevenZipExtractor.IsInstallerFormat(format.Name))
            {
                continue;
            }

            var candidate = SevenZipNative.CreateInArchive(format.ClassId);
            if (candidate is null)
            {
                continue;
            }

            var openCallback = new OpenCallback();
            var opened = false;
            try
            {
                input.Position = 0;
                ulong maxCheck = (ulong)Math.Min(input.Length, MaxSignatureScan);
                opened = candidate.Open(inStream, ref maxCheck, openCallback) == HResults.SOk;
            }
            catch (COMException)
            {
                // This format cannot be opened; try the next one
            }

            if (opened && !openCallback.PasswordRequested)
            {
                matched = candidate;
                matchedName = format.Name;
                break;
            }

            CloseArchive(candidate, opened);
        }

        if (matched is null)
        {
            return null;
        }

        try
        {
            return ReadLayout(matched, matchedName!, ct);
        }
        finally
        {
            CloseArchive(matched, opened: true);
        }
    }

    private static ArchiveLayoutInfo? ReadLayout(IInArchive archive, string formatName, CancellationToken ct)
    {
        var isNsis = string.Equals(formatName, "Nsis", StringComparison.OrdinalIgnoreCase);
        var count = archive.GetNumberOfItems();
        var limit = (uint)Math.Min(count, MaxEntries);
        var firstSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rootLevelFiles = 0;
        var validEntries = 0;

        for (uint index = 0; index < limit; index++)
        {
            ct.ThrowIfCancellationRequested();

            if (SevenZipExtractor.ReadBool(archive, index, ItemPropId.IsFolder) ||
                SevenZipExtractor.ReadBool(archive, index, ItemPropId.IsAnti))
            {
                continue;
            }

            var entryPath = SevenZipExtractor.ReadString(archive, index, ItemPropId.Path);
            if (string.IsNullOrWhiteSpace(entryPath))
            {
                continue;
            }

            // NSIS uses the same rules as the extraction phase: skip plugins / temporary files and strip pseudo-directory segments from the rest before judging the hierarchy.
            // Otherwise plugins in $PLUGINSDIR\ would be treated as loose files at the root, and packages like Everything could never be detected as having a single top-level directory.
            string normalized;
            if (isNsis)
            {
                var decision = InstallerEntryPath.Normalize(entryPath, isNsis: true);
                if (decision.Skip)
                {
                    continue;
                }

                normalized = decision.RelativePath;
            }
            else
            {
                normalized = NormalizeEntryPath(entryPath);
            }

            if (string.IsNullOrEmpty(normalized))
            {
                continue;
            }

            validEntries++;
            var separator = normalized.IndexOf('\\');
            if (separator < 0)
            {
                rootLevelFiles++;
                continue;
            }

            firstSegments.Add(normalized[..separator]);
        }

        if (validEntries == 0)
        {
            return null;
        }

        var hasSingleTopLevel = rootLevelFiles == 0 && firstSegments.Count == 1;
        return new ArchiveLayoutInfo(
            formatName,
            hasSingleTopLevel,
            firstSegments.Count == 1 ? firstSegments.First() : null,
            validEntries);
    }

    /// <summary>
    /// Strip installer pseudo-directory segments before judging: NSIS puts all program files under $PLUGINSDIR\ first,
    /// and 7-Zip may add a [0]\ prefix for internal PE sections. These are not real levels in the user's directory,
    /// but the remaining files after stripping still take part in the "single top-level directory" decision, otherwise NSIS packages would be ignored entirely.
    /// An empty string means the whole entry is an internal installer item and should be ignored.
    /// </summary>
    internal static string NormalizeEntryPath(string rawPath)
    {
        var parts = rawPath
            .Replace('/', '\\')
            .Trim('\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        while (parts.Count > 0 && (parts[0].StartsWith('$') || parts[0].StartsWith('[')))
        {
            parts.RemoveAt(0);
        }

        return string.Join('\\', parts);
    }

    private static void CloseArchive(IInArchive candidate, bool opened)
    {
        if (opened)
        {
            try
            {
                candidate.Close();
            }
            catch
            {
                // A close failure does not affect subsequent probing
            }
        }

        try
        {
            Marshal.ReleaseComObject(candidate);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to release the RCW: {ex.Message}");
        }
    }
}
