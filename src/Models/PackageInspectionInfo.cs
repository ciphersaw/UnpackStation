namespace UnpackStation.Models;

/// <summary>
/// Structured result of reading a package's layout. Displayed in the package-info card
/// so the user does not have to dig through log lines. Fields that do not apply to the
/// selected package type stay null and are rendered as "—" by the view.
/// </summary>
public sealed record PackageInspectionInfo
{
    public string Format { get; init; } = "—";
    public string Engine { get; init; } = "—";
    public string? EntryCount { get; init; }
    public string? TopLevelFolder { get; init; }
    public string? InstallPath { get; init; }
    public string? InstallOrigin { get; init; }
    public string Decision { get; init; } = "—";
    public string? Warning { get; init; }
}
