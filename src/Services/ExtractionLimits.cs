using System.Globalization;

namespace UnpackStation.Services;

/// <summary>
/// Thresholds for decompression bomb protection (requirement §7): abort when cumulative extraction exceeds 20 GB or the file count exceeds 100,000.
/// The declared values are used to intercept "before writing to disk"; the actual values are checked again by <see cref="ExtractionBudget"/> while writing bytes,
/// to prevent forged Size attributes in the archive from bypassing the check.
/// </summary>
internal static class ExtractionLimits
{
    internal const long MaxFileCount = 100_000;
    internal const ulong MaxTotalBytes = 20UL * 1024 * 1024 * 1024;

    internal static string? Check(long fileCount, ulong totalBytes) =>
        Check(fileCount, totalBytes, MaxFileCount, MaxTotalBytes);

    /// <summary>Allows tests to verify the interception logic with smaller thresholds; production code uses the default overload above.</summary>
    internal static string? Check(long fileCount, ulong totalBytes, long maxFileCount, ulong maxTotalBytes)
    {
        if (fileCount > maxFileCount)
        {
            return $"Extraction aborted: file count {fileCount.ToString("N0", CultureInfo.CurrentCulture)} " +
                   $"exceeds the limit {maxFileCount.ToString("N0", CultureInfo.CurrentCulture)} (suspected decompression bomb).";
        }

        if (totalBytes > maxTotalBytes)
        {
            return $"Extraction aborted: cumulative extracted {FormatBytes(totalBytes)} " +
                   $"exceeds the limit {FormatBytes(maxTotalBytes)} (suspected decompression bomb).";
        }

        return null;
    }

    internal static string FormatBytes(ulong bytes)
    {
        if (bytes >= 1024UL * 1024 * 1024)
        {
            return $"{bytes / 1024d / 1024 / 1024:0.##} GB";
        }

        if (bytes >= 1024UL * 1024)
        {
            return $"{bytes / 1024d / 1024:0.#} MB";
        }

        return $"{bytes} B";
    }
}

/// <summary>
/// Runtime cumulative budget. 7-Zip's Extract uses a single-threaded callback, so no lock is needed here.
/// Bytes of skipped entries (still decompressed in solid archives) are also counted, to prevent a bomb from getting around the check another way.
/// </summary>
internal sealed class ExtractionBudget
{
    private readonly ulong _maxBytes;
    private readonly long _maxFiles;
    private ulong _bytes;
    private long _files;

    internal ExtractionBudget(
        ulong maxBytes = ExtractionLimits.MaxTotalBytes,
        long maxFiles = ExtractionLimits.MaxFileCount)
    {
        _maxBytes = maxBytes;
        _maxFiles = maxFiles;
    }

    internal ulong Bytes => _bytes;
    internal bool Exceeded { get; private set; }
    internal string? Reason { get; private set; }

    internal void Abort(string reason)
    {
        if (Exceeded)
        {
            return;
        }

        Exceeded = true;
        Reason = reason;
    }

    internal bool AddBytes(ulong size)
    {
        if (Exceeded)
        {
            return false;
        }

        // Subtract before adding to avoid overflow of _bytes + size
        if (size > _maxBytes - _bytes)
        {
            Abort($"Extraction aborted: actual extracted bytes exceed the limit {ExtractionLimits.FormatBytes(_maxBytes)} (suspected decompression bomb).");
            return false;
        }

        _bytes += size;
        return true;
    }

    internal bool AddFile()
    {
        if (Exceeded)
        {
            return false;
        }

        if (_files >= _maxFiles)
        {
            Abort($"Extraction aborted: the number of files actually written exceeds the limit {_maxFiles:N0} (suspected decompression bomb).");
            return false;
        }

        _files++;
        return true;
    }
}
