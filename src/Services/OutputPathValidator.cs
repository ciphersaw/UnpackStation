using System.IO;

namespace UnpackStation.Services;

/// <summary>
/// Validates entry paths in the archive before extraction to prevent path traversal.
/// Paths in the archive are always treated as untrusted input: those containing .., absolute paths, drive letters, or colons are rejected outright.
/// </summary>
internal static class OutputPathValidator
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    // Windows reserved device names: writing CON.txt, NUL.dll, etc. lands on a device rather than a file,
    // they are also a common path-confusion trick used by attackers, so reject them all
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    internal static bool TryResolve(
        string outputDir,
        string entryPath,
        out string fullPath,
        out string reason)
    {
        fullPath = string.Empty;
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(entryPath))
        {
            reason = "Path is empty";
            return false;
        }

        // Archives may use / or \; normalize to / first
        var normalized = entryPath.Replace('\\', '/');

        if (Path.IsPathRooted(entryPath) || normalized.StartsWith('/'))
        {
            reason = "Absolute path";
            return false;
        }

        if (normalized.Length >= 2 && normalized[1] == ':')
        {
            reason = "Contains a drive letter";
            return false;
        }

        // Also block NTFS alternate data streams (file.txt:stream) and colons in UNC paths
        if (entryPath.IndexOf(':') >= 0)
        {
            reason = "Contains a colon (drive letter or alternate data stream)";
            return false;
        }

        var segments = normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment != ".")
            .ToArray();

        if (segments.Length == 0)
        {
            reason = "No valid file name";
            return false;
        }

        if (segments.Any(segment => segment == ".."))
        {
            reason = "Contains .. parent traversal";
            return false;
        }

        foreach (var segment in segments)
        {
            if (segment.IndexOfAny(InvalidFileNameChars) >= 0 ||
                segment.EndsWith(' ') || segment.EndsWith('.'))
            {
                reason = "Contains invalid characters";
                return false;
            }

            var dot = segment.IndexOf('.');
            var stem = dot < 0 ? segment : segment[..dot];
            if (ReservedDeviceNames.Contains(stem))
            {
                reason = "Contains a Windows reserved device name";
                return false;
            }
        }

        try
        {
            var root = Path.GetFullPath(outputDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.Combine(new[] { root }.Concat(segments).ToArray()));

            // Final line of defense: the normalized full path must still be inside the output directory
            if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                reason = "Normalized path escapes the output directory";
                return false;
            }

            fullPath = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = $"Invalid path ({ex.GetType().Name})";
            return false;
        }
    }

    /// <summary>
    /// Second line of defense: a valid string path is not enough; if the output directory already contains a symbolic link / directory junction pointing elsewhere,
    /// writing to link\evil.dll would still land outside the output directory. Only parent directories "inside" the output directory are checked,
    /// and a link that is the output directory chosen by the user is not blocked (that is the user's choice).
    /// </summary>
    internal static bool HasReparsePointInParents(string root, string candidate, out string reason)
    {
        reason = string.Empty;
        var rootTrimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(candidate);

        while (!string.IsNullOrEmpty(parent) &&
               parent.Length > rootTrimmed.Length &&
               parent.StartsWith(rootTrimmed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(parent))
            {
                try
                {
                    if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                    {
                        reason = "Path passes through a symbolic link or directory junction";
                        return true;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                               ArgumentException or NotSupportedException)
                {
                    // If attributes cannot be read, take the most conservative approach: do not write into a directory that cannot be verified
                    reason = "Cannot confirm whether the directory is a link";
                    return true;
                }
            }

            parent = Path.GetDirectoryName(parent);
        }

        return false;
    }

    /// <summary>Used when moving from the MSI staging directory: the target full path must still be inside the output directory.</summary>
    internal static bool IsInsideRoot(string root, string candidate)
    {
        var rootTrimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return candidate.StartsWith(
            rootTrimmed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
