namespace UnpackStation.Models;

/// <summary>Coarse phase reported by every extraction engine; the UI derives a stable label from it.</summary>
public enum ExtractionStage
{
    Preparing,
    Extracting,
    Organizing,
    Completed
}

/// <summary>
/// Structured progress report shared by all engines.
/// Percent: -1 = indeterminate, 0..99 while working, 100 only after the whole extraction is complete.
/// Stage: coarse phase, independent of the engine's measurement unit (bytes for 7z, file count for Inno/MSI).
/// Detail: short human-readable text, e.g. "12 / 29 files" or "8.4 / 21.0 MB".
/// </summary>
public readonly record struct ExtractionProgress(int Percent, ExtractionStage Stage, string Detail);
