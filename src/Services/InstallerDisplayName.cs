using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace UnpackStation.Services;

/// <summary>
/// Turns installer metadata into a safe directory-name leaf. Setup file names such as
/// "myapp-setup.exe" are poor isolation names when the installer itself reports
/// the real application name ("My App").
/// </summary>
internal static partial class InstallerDisplayName
{
    private static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Setup", "Installer", "Install", "Setup Program", "Application", "App", "Package"
    };

    [GeneratedRegex(@"\s+(?:setup|installer|install|setup program)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingSetupSuffix();

    /// <summary>Reads ProductName (or FileDescription) from the PE version resource; never runs the exe.</summary>
    internal static string? FromPeVersionInfo(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return Normalize(info.ProductName) ?? Normalize(info.FileDescription);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                                       NotSupportedException or Win32Exception)
        {
            return null;
        }
    }

    internal static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var name = TrailingSetupSuffix().Replace(raw.Trim(), string.Empty).Trim();
        if (name.Length == 0 || GenericNames.Contains(name))
        {
            return null;
        }

        return MsiInstallDirectoryLogic.IsSafeLeafName(name) ? name : null;
    }
}
