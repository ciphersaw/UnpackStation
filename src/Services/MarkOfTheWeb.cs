using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UnpackStation.Services;

/// <summary>
/// Writes the Mark of the Web tag to extracted executables.
/// It is implemented by writing ZoneId=3 to the NTFS alternate data stream file.exe:Zone.Identifier,
/// so Windows shows a security prompt when the user double-clicks. It does not modify file content or touch the user's existing files.
/// On non-NTFS volumes or with insufficient privileges it only records a diagnostic and does not fail the extraction.
/// </summary>
internal static class MarkOfTheWeb
{
    internal const string StreamSuffix = ":Zone.Identifier";

    private static readonly byte[] ZoneTransfer = Encoding.ASCII.GetBytes("[ZoneTransfer]\r\nZoneId=3\r\n");

    // The requirement only names exe/dll; this also covers common types that execute or get loaded on a double-click
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".ocx", ".sys", ".drv", ".scr", ".cpl", ".msi", ".msp",
        ".bat", ".cmd", ".ps1", ".vbs", ".js", ".jse", ".wsf", ".hta", ".jar"
    };

    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWrite = 0x00000003;
    private const uint CreateAlways = 2;
    private const uint FileAttributeNormal = 0x00000080;

    internal static bool NeedsMark(string path)
    {
        try
        {
            return ExecutableExtensions.Contains(Path.GetExtension(path));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Only processes the "newly written this run" file list passed by the caller; returns the marked and failed counts.</summary>
    internal static (int Marked, int Failed) Apply(IEnumerable<string> files, StringBuilder diag)
    {
        var marked = 0;
        var failed = 0;

        foreach (var file in files)
        {
            if (!NeedsMark(file))
            {
                continue;
            }

            if (WriteZoneIdentifier(file))
            {
                marked++;
            }
            else
            {
                failed++;
                if (failed <= 5)
                {
                    diag.AppendLine($"Failed to write the Mark of the Web tag (volume may not be NTFS): {file}");
                }
            }
        }

        if (marked > 0 || failed > 0)
        {
            diag.AppendLine($"Wrote the Mark of the Web tag to {marked} extracted executable files" +
                            (failed > 0 ? $", {failed} failed" : string.Empty));
        }

        return (marked, failed);
    }

    internal static bool WriteZoneIdentifier(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            // The \\?\ prefix avoids MAX_PATH; the ADS name must be appended after the full file path
            var full = Path.GetFullPath(path);
            var devicePath = full.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + full[2..] + StreamSuffix
                : @"\\?\" + full + StreamSuffix;

            using var handle = CreateFileW(
                devicePath, GenericWrite, FileShareReadWrite, IntPtr.Zero,
                CreateAlways, FileAttributeNormal, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                return false;
            }

            using var stream = new FileStream(handle, FileAccess.Write);
            stream.Write(ZoneTransfer, 0, ZoneTransfer.Length);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode,
        ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
}
