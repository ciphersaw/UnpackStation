using System.Text.RegularExpressions;

namespace UnpackStation.Services;

/// <summary>
/// Reads the application name that innoextract prints for an Inno Setup installer, e.g.
/// Inspecting "My App" - setup data version 6.7.0 (unicode)
/// This name comes from the installer's own AppName and is a much better default output
/// directory name than the setup file name. Only read-only metadata is queried.
/// </summary>
internal static partial class InnoSetupInstallNameReader
{
    [GeneratedRegex("^\\s*Inspecting\\s+\"(?<name>[^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex InspectingLine();

    internal static string? TryRead(string exePath, CancellationToken ct) =>
        Parse(InnoExtractRunner.Run(exePath, ct, "--info"));

    internal static string? Parse(string? infoOutput)
    {
        if (string.IsNullOrWhiteSpace(infoOutput))
        {
            return null;
        }

        var match = InspectingLine().Match(infoOutput);
        return match.Success ? InstallerDisplayName.Normalize(match.Groups["name"].Value) : null;
    }
}
