using UnpackStation.Models;

namespace UnpackStation.Services;

/// <summary>Unified entry point for extraction engines; adding Inno or MSIX later only requires a new implementation and one check in the factory.</summary>
public interface IPackageExtractor
{
    /// <summary>Whether this engine can handle the file (by extension plus the first few bytes of the header).</summary>
    bool CanHandle(string filePath);

    /// <summary>Extracts. The progress percent is 0-100; -1 means progress cannot be determined.</summary>
    Task<ExtractResult> ExtractAsync(
        string sourcePath,
        string outputDir,
        IProgress<(int percent, string message)> progress,
        CancellationToken ct);
}
