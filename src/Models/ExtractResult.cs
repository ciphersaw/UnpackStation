namespace UnpackStation.Models;

/// <summary>The result of one extraction. ErrorMessage is shown to the user; Diagnostic is for "Copy diagnostic information".</summary>
public record ExtractResult(
    bool Success,
    string OutputDir,
    int FileCount,
    string? ErrorMessage,
    string? Diagnostic);
