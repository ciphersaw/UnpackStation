using UnpackStation.Models;

namespace UnpackStation.Services;

/// <summary>Unified entry point for extraction engines; adding Inno or MSIX later only requires a new implementation and one check in the factory.</summary>
public interface IPackageExtractor
{
    /// <summary>Whether this engine can handle the file (by extension plus the first few bytes of the header).</summary>
    bool CanHandle(string filePath);

    /// <summary>
    /// Extracts. Progress is reported as a structured <see cref="ExtractionProgress"/>:
    /// Percent is -1 when indeterminate, 0..99 while working, and 100 only on completion.
    /// </summary>
    Task<ExtractResult> ExtractAsync(
        string sourcePath,
        string outputDir,
        IProgress<ExtractionProgress> progress,
        CancellationToken ct);
}
