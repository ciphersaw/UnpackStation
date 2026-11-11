using System.IO;

namespace UnpackStation.Services;

/// <summary>Decision for an entry path during extraction: whether to skip, the relative path to write, and the skip reason.</summary>
internal sealed record InstallerEntryDecision(bool Skip, string RelativePath, string? Reason);

/// <summary>
/// When an NSIS installer package is opened by 7-Zip, the archive contains not only the program files to install but also files the installer uses at runtime:
///   $PLUGINSDIR\System.dll, modern-wizard.bmp, etc. — plugin / wizard resources released to a temp directory during installation and deleted at the end;
///   $TEMP\MicrosoftEdgeWebview2Setup.exe, etc. — runtime temp files that likewise do not stay in the install directory.
/// Extracting them as-is would add odd directories like $PLUGINSDIR and $TEMP, inconsistent with a direct installation.
///
/// The rules apply only when identified as Nsis; paths in ordinary 7z / zip / other SFX archives are not changed by a single character.
/// Conservative principle: only skip runtime files that can be identified with certainty; keep everything else after stripping pseudo-directory segments,
/// e.g. $PLUGINSDIR\Everything\Everything.exe → Everything\Everything.exe,
/// $PLUGINSDIR\app-64.7z → app-64.7z; never drop real content just because it "looks like a plugin".
/// $INSTDIR is the real install directory; only its prefix is stripped and it does not take part in plugin detection.
/// </summary>
internal static class InstallerEntryPath
{
    // Common extensions for plugin / wizard resources. Used for detection only for a "single-segment file name" directly under a pseudo-directory root,
    // files with the same name in subdirectories ($PLUGINSDIR\Everything\xxx.dll) are always kept.
    private static readonly HashSet<string> PluginExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".bmp", ".ini"
    };

    internal static InstallerEntryDecision Normalize(string entryPath, bool isNsis)
    {
        var parts = entryPath
            .Replace('/', '\\')
            .Trim('\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        if (parts.Count == 0)
        {
            return new InstallerEntryDecision(true, string.Empty, "Entry path is empty");
        }

        // In non-NSIS archives, directories starting with $ may be real user directories; do not interfere at all
        if (!isNsis)
        {
            return new InstallerEntryDecision(false, string.Join('\\', parts), null);
        }

        var cameFromPluginDir = false;
        while (parts.Count > 0 && IsInstallerVariableSegment(parts[0]))
        {
            var segment = parts[0];
            parts.RemoveAt(0);

            // Everything in $TEMP / $TMP is a runtime temp file deleted when installation ends; always skip
            if (string.Equals(segment, "$TEMP", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, "$TMP", StringComparison.OrdinalIgnoreCase))
            {
                return new InstallerEntryDecision(true, string.Join('\\', parts), "Installer temp directory ($TEMP)");
            }

            // $INSTDIR points to the real install directory; only strip the prefix and do not treat dlls under it as plugins
            if (!string.Equals(segment, "$INSTDIR", StringComparison.OrdinalIgnoreCase))
            {
                cameFromPluginDir = true;
            }
        }

        if (parts.Count == 0)
        {
            return new InstallerEntryDecision(true, string.Empty, "Installer internal directory entry");
        }

        var relative = string.Join('\\', parts);
        if (cameFromPluginDir &&
            parts.Count == 1 &&
            PluginExtensions.Contains(Path.GetExtension(parts[0])))
        {
            return new InstallerEntryDecision(true, relative, "Installer plugin/wizard resource");
        }

        return new InstallerEntryDecision(false, relative, null);
    }

    /// <summary>A leading $ marks an NSIS variable directory; [0] and the like are marker segments 7-Zip adds for internal sections.</summary>
    private static bool IsInstallerVariableSegment(string segment) =>
        segment.StartsWith('$') ||
        (segment.StartsWith('[') && segment.EndsWith(']'));
}
