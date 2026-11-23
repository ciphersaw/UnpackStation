using System.IO;
using UnpackStation.Models;

namespace UnpackStation.Services;

/// <summary>
/// Probe result: the final default output directory, notes for the log,
/// and structured information shown in the package-info card.
/// </summary>
internal sealed record DefaultOutputPlan(
    string OutputDir,
    IReadOnlyList<string> Notes,
    PackageInspectionInfo? Info = null,
    string? LeafName = null);

/// <summary>
/// Computes the "final" default output directory: first read the package's default install location / entry structure,
/// then decide whether to add an isolation directory under the source file's directory.
/// The added layer only affects the output directory path; the extraction logic needs no changes.
/// Any failure conservatively falls back to OutputDirectoryResolver.Resolve (the source file's directory).
/// </summary>
internal static class DefaultOutputResolver
{
    /// <summary>7z.dll / msi.dll are both blocking calls; run them on a background thread so selecting a file does not freeze the UI.</summary>
    internal static Task<DefaultOutputPlan> InspectAsync(string sourcePath, CancellationToken ct) =>
        Task.Run(() => InspectCore(sourcePath, ct), ct);

    private static DefaultOutputPlan InspectCore(string sourcePath, CancellationToken ct)
    {
        var notes = new List<string>();
        string fallback;
        try
        {
            fallback = OutputDirectoryResolver.Resolve(sourcePath);
        }
        catch (Exception ex)
        {
            // If the path itself is invalid, let the UI handle it; return an empty string here so later validation catches it
            return new DefaultOutputPlan(
                string.Empty,
                new[] { $"Cannot compute the default output directory: {ex.Message}" },
                new PackageInspectionInfo { Decision = "Unavailable", Warning = ex.Message });
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(sourcePath);
            var parentDir = Path.GetDirectoryName(fullPath);

            // When the source file is at a drive root, Resolve already provides a separate xxx_extracted directory, so no extra layer is needed
            if (!string.Equals(fallback, parentDir, StringComparison.OrdinalIgnoreCase))
            {
                notes.Add("The source file is at a drive root; using a separate output directory avoids scattering files at the root (conservative fallback, no extra layer added)");
                return new DefaultOutputPlan(
                    fallback,
                    notes,
                    new PackageInspectionInfo
                    {
                        Format = FormatFromExtension(fullPath),
                        Decision = "Kept source directory",
                        Warning = "Source file is at a drive root"
                    });
            }

            var extension = Path.GetExtension(fullPath);
            if (string.Equals(extension, ".msi", StringComparison.OrdinalIgnoreCase))
            {
                return InspectMsi(fullPath, fallback, notes);
            }

            if (string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
            {
                return InspectExe(fullPath, fallback, notes, ct);
            }

            notes.Add("Not .exe / .msi; keep extracting into the source file's directory");
            return new DefaultOutputPlan(
                fallback,
                notes,
                new PackageInspectionInfo
                {
                    Decision = "Kept source directory",
                    Warning = "Unsupported package type"
                });
        }
        catch (Exception ex)
        {
            notes.Add($"Failed to read the package layout ({ex.Message}); keep extracting into the source file's directory");
            return new DefaultOutputPlan(
                fallback,
                notes,
                new PackageInspectionInfo
                {
                    Decision = "Kept source directory",
                    Warning = ex.Message
                });
        }
    }

    private static DefaultOutputPlan InspectMsi(string sourcePath, string fallback, List<string> notes)
    {
        var info = MsiInstallDirectoryReader.TryRead(sourcePath);
        if (info is null)
        {
            notes.Add("Could not read the default install location from the MSI; keep extracting into the source file's directory");
            return new DefaultOutputPlan(
                fallback,
                notes,
                new PackageInspectionInfo
                {
                    Format = "MSI",
                    Engine = "msiexec /a",
                    Decision = "Kept source directory",
                    Warning = "Install location could not be read"
                });
        }

        // Absolute system paths (C:\Program Files\Go, C:\Python27) are for display only and never used as the output directory
        notes.Add($"Package default install location: {info.Template} (from {info.Origin})");

        // The output directory is always "the directory where the extracted content actually lives".
        // When the package already has a layer with the same name (Go\), the extraction phase absorbs it, so there will be no Go\Go\bin\go.exe.
        if (info.PackageHasThisLayer)
        {
            notes.Add($"The package already contains the top-level directory {info.LeafName}\\; its contents will be placed directly into the output directory during extraction");
        }
        else
        {
            notes.Add("Package files sit directly under the install root with no dedicated top-level directory to hold them");
        }

        var decision = DecideOutputDir(fallback, info.LeafName, out var reason);
        notes.Add(reason);
        var leafName = string.Equals(decision, fallback, StringComparison.OrdinalIgnoreCase) ? null : info.LeafName;
        return new DefaultOutputPlan(
            decision,
            notes,
            BuildInfo("MSI", "msiexec /a", null, info.LeafName, info.Template, info.Origin, fallback, decision, reason),
            leafName);
    }

    private static DefaultOutputPlan InspectExe(
        string sourcePath,
        string fallback,
        List<string> notes,
        CancellationToken ct)
    {
        var layout = ArchiveLayoutProbe.TryProbe(sourcePath, ct);
        if (layout is null)
        {
            notes.Add("Cannot probe the package structure in read-only mode (possibly an unsupported packaging format); keep extracting into the source file's directory");
            return new DefaultOutputPlan(
                fallback,
                notes,
                new PackageInspectionInfo
                {
                    Format = FormatFromExtension(sourcePath),
                    Engine = "7z.dll",
                    Decision = "Kept source directory",
                    Warning = "Layout probe failed (unsupported or unreadable package)"
                });
        }

        notes.Add($"Identified as {layout.FormatName} package; inspected {layout.InspectedEntryCount} entries");

        var format = NormalizeFormat(layout.FormatName);
        var engine = string.Equals(format, "Inno Setup", StringComparison.OrdinalIgnoreCase)
            ? "innoextract"
            : "7z.dll";

        // Prefer the package's own top-level directory; only when there is none do we look for
        // the application name reported by the installer itself before falling back to the file name.
        string? leaf;
        string? topLevel = null;
        string? detectedName = null;
        string? detectedOrigin = null;
        if (layout.HasSingleTopLevelDirectory && !string.IsNullOrWhiteSpace(layout.TopLevelName))
        {
            leaf = layout.TopLevelName;
            topLevel = leaf;
            notes.Add($"The package already has a single top-level directory {leaf}\\; its contents will be placed directly into the output directory during extraction");
        }
        else
        {
            if (string.Equals(format, "Inno Setup", StringComparison.OrdinalIgnoreCase))
            {
                detectedName = InnoSetupInstallNameReader.TryRead(sourcePath, ct);
                if (detectedName is not null)
                {
                    detectedOrigin = "innoextract --info";
                }
            }

            detectedName ??= InstallerDisplayName.FromPeVersionInfo(sourcePath);
            if (detectedName is not null)
            {
                detectedOrigin ??= "PE version resource";
                leaf = detectedName;
                notes.Add($"Installer reports the application name \"{detectedName}\" (from {detectedOrigin}); using it instead of the setup file name");
            }
            else
            {
                leaf = Path.GetFileNameWithoutExtension(sourcePath);
                notes.Add(string.Equals(format, "Inno Setup", StringComparison.OrdinalIgnoreCase)
                    ? "Inno Setup payload files sit directly under the install root and no application name could be read"
                    : "Package files are scattered at the root with no single top-level directory and no application name could be read");
            }
        }

        var decision = DecideOutputDir(fallback, leaf, out var reason);
        notes.Add(reason);
        var leafName = string.Equals(decision, fallback, StringComparison.OrdinalIgnoreCase) ? null : leaf;
        return new DefaultOutputPlan(
            decision,
            notes,
            BuildInfo(format, engine, layout.InspectedEntryCount.ToString("N0"), topLevel, detectedName, detectedOrigin, fallback, decision, reason),
            leafName);
    }

    private static PackageInspectionInfo BuildInfo(
        string format,
        string engine,
        string? entryCount,
        string? topLevelFolder,
        string? installPath,
        string? installOrigin,
        string fallback,
        string decision,
        string reason)
    {
        var addedLayer = !string.Equals(decision, fallback, StringComparison.OrdinalIgnoreCase);
        return new PackageInspectionInfo
        {
            Format = format,
            Engine = engine,
            EntryCount = entryCount,
            TopLevelFolder = topLevelFolder,
            InstallPath = installPath,
            InstallOrigin = installOrigin,
            Decision = addedLayer ? $"Added layer: {Path.GetFileName(decision)}" : "Kept source directory",
            Warning = addedLayer ? null : reason
        };
    }

    private static string FormatFromExtension(string path) =>
        string.Equals(Path.GetExtension(path), ".msi", StringComparison.OrdinalIgnoreCase) ? "MSI" : "EXE";

    private static string NormalizeFormat(string name) =>
        string.Equals(name, "Nsis", StringComparison.OrdinalIgnoreCase) ? "NSIS" : name;

    /// <summary>
    /// Pure decision: the output directory is always source directory\leaf, i.e. the directory where the extracted content actually lives.
    /// Any uncertainty (illegal name, path occupied by a file of the same name) falls back to the source directory; never blindly create directories.
    /// </summary>
    internal static string DecideOutputDir(
        string fallback,
        string? leaf,
        out string reason)
    {
        if (!MsiInstallDirectoryLogic.IsSafeLeafName(leaf))
        {
            reason = "The added layer's directory name is unusable; keep extracting into the source file's directory";
            return fallback;
        }

        string candidate;
        try
        {
            candidate = Path.Combine(fallback, leaf!);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = $"The added layer path is invalid ({ex.Message}); keep extracting into the source file's directory";
            return fallback;
        }

        if (File.Exists(candidate))
        {
            reason = $"The target location {candidate} is already occupied by a file of the same name; keep extracting into the source file's directory";
            return fallback;
        }

        reason = $"The output directory points to the {leaf}\\ directory where the extracted result actually lives";
        return candidate;
    }
}
