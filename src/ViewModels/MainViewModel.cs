using System;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnpackStation.Models;
using UnpackStation.Services;

namespace UnpackStation.ViewModels;

/// <summary>Main window state and commands. Depends only on Services and references no UI types.</summary>
public partial class MainViewModel : ObservableObject
{
    private readonly StringBuilder _logBuilder = new();
    private CancellationTokenSource? _cts;
    private int _lastProgressBucket = -1;
    private string _lastStatusMessage = string.Empty;

    // After a file is selected, the package layout is read asynchronously; generation discards stale results from a previously selected file
    private int _sourceGeneration;
    private bool _outputDirManuallySet;
    private Task? _inspectTask;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string? sourcePath;

    [ObservableProperty] private string fileInfoText = "No file selected";
    [ObservableProperty] private string outputDir = string.Empty;
    [ObservableProperty] private int progressValue;
    [ObservableProperty] private bool isProgressIndeterminate;
    [ObservableProperty] private string progressText = "—";
    [ObservableProperty] private string stageText = "Ready";
    [ObservableProperty] private string statusText = "Ready";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditPaths), nameof(CanCancel))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(CancelCommand))]
    private bool isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenOutputCommand))]
    private bool canOpenOutput;

    /// <summary>Raised when the UI needs to show a notice, so the ViewModel does not reference MessageBox directly.</summary>
    public event Action<string>? NoticeRequested;

    /// <summary>Requests the UI to open the output directory after a successful extraction; the UI only calls Explorer and never runs any extracted program.</summary>
    public event Action<string>? OpenOutputRequested;

    /// <summary>Requests the UI to put diagnostic information on the clipboard; the ViewModel references no UI types.</summary>
    public event Action<string>? DiagnosticCopyRequested;

    /// <summary>Log text, accumulated with a StringBuilder to avoid memory churn from line-by-line concatenation.</summary>
    public string LogText => _logBuilder.ToString();

    /// <summary>The most recent diagnostic information, used by the "Copy diagnostic information" button.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyDiagnosticCommand))]
    private string lastDiagnostic = string.Empty;

    /// <summary>Copying is not allowed when there is no diagnostic information.</summary>
    public bool CanCopyDiagnostic => !string.IsNullOrWhiteSpace(LastDiagnostic);

    /// <summary>Paths cannot be changed during extraction.</summary>
    public bool CanEditPaths => !IsBusy;

    /// <summary>The "Cancel" button is unavailable when no task is running.</summary>
    public bool CanCancel => IsBusy;

    private bool CanStart => SourcePath is not null && !IsBusy;

    /// <summary>Sets the source file path passed in by the UI (drag-and-drop or file dialog).</summary>
    public void SetSourceFile(string path)
    {
        if (IsBusy)
        {
            AppendLog("Extraction in progress; the source file cannot be changed.");
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            AppendLog($"Invalid path: {ex.Message}");
            NoticeRequested?.Invoke("The file path is invalid; please choose another location and try again.");
            return;
        }

        var extension = Path.GetExtension(fullPath);
        if (!IsSupported(extension))
        {
            AppendLog($"Unsupported file type {extension}; only .exe / .msi are accepted.");
            NoticeRequested?.Invoke("Only .exe and .msi installer packages are supported.");
            return;
        }

        if (!File.Exists(fullPath))
        {
            AppendLog($"File not found: {fullPath}");
            NoticeRequested?.Invoke("The file could not be found; please select it again.");
            return;
        }

        SourcePath = fullPath;
        CanOpenOutput = false;
        ProgressValue = 0;
        IsProgressIndeterminate = false;
        LastDiagnostic = string.Empty;
        _outputDirManuallySet = false;
        var generation = ++_sourceGeneration;

        long fileSize;
        try
        {
            fileSize = new FileInfo(fullPath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppendLog($"Failed to read the file size: {ex.Message}");
            fileSize = -1;
        }

        FileInfoText = fileSize >= 0 ? $"{fullPath}  ({FormatSize(fileSize)})" : fullPath;
        AppendLog($"Selected file: {fullPath}");

        // Reset to the default directory when a new file is selected; a manually edited output directory is not kept, so behavior is predictable.
        // First provide an immediately usable fallback, then asynchronously read the package layout to decide whether to add a layer.
        try
        {
            OutputDir = OutputDirectoryResolver.Resolve(fullPath);
            AppendLog($"Initial output directory: {OutputDir} (source file's directory, reading package install information …)");
            StatusText = "Reading package install information …";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            OutputDir = string.Empty;
            _inspectTask = null;
            StatusText = "Output directory must be specified manually";
            AppendLog($"Cannot compute the default output directory: {ex.Message}");
            NoticeRequested?.Invoke("Cannot determine the output directory automatically; please use the Browse button to set it manually.");
            return;
        }

        _inspectTask = InspectDefaultOutputAsync(fullPath, generation);
    }

    /// <summary>
    /// Asynchronously reads the package's default install location / entry structure and adds a layer under the source file's directory when needed.
    /// If probing fails or the file has changed, always keep the current default directory; never block use because of metadata reading.
    /// </summary>
    private async Task InspectDefaultOutputAsync(string fullPath, int generation)
    {
        try
        {
            var plan = await DefaultOutputResolver.InspectAsync(fullPath, CancellationToken.None);
            if (generation != _sourceGeneration || _outputDirManuallySet)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(plan.OutputDir))
            {
                OutputDir = plan.OutputDir;
            }

            foreach (var note in plan.Notes)
            {
                AppendLog(note);
            }

            AppendLog($"Default output directory: {OutputDir}");
            StatusText = "Ready to start extraction";
        }
        catch (Exception ex)
        {
            if (generation != _sourceGeneration)
            {
                return;
            }

            AppendLog($"Failed to read package install information; keeping the current default directory: {ex.Message}");
            StatusText = "Ready to start extraction";
        }
    }

    /// <summary>Sets the output directory from the UI folder dialog; the default rule is no longer applied once the user chooses explicitly.</summary>
    public void SetOutputDir(string path)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            OutputDir = Path.GetFullPath(path);
            _outputDirManuallySet = true;
            AppendLog($"Output directory set to: {OutputDir}");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            AppendLog($"Invalid output directory: {ex.Message}");
            NoticeRequested?.Invoke("The output directory path is invalid; please choose again.");
        }
    }

    public void AppendLog(string message)
    {
        _logBuilder.Append('[').Append(DateTime.Now.ToString("HH:mm:ss")).Append("] ").AppendLine(message);
        OnPropertyChanged(nameof(LogText));
    }

    /// <summary>Starts extraction. The command is triggered by a button on the UI thread and returns to the UI thread after await.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        if (SourcePath is null || string.IsNullOrWhiteSpace(OutputDir))
        {
            NoticeRequested?.Invoke("Please select an installer package and confirm the output directory first.");
            return;
        }

        // If probing the package layout has not finished when Start is clicked, wait for the result first to avoid extracting into an unexpected hierarchy from the fallback directory
        if (_inspectTask is { IsCompleted: false })
        {
            AppendLog("Waiting for package install information to finish loading …");
            await _inspectTask;
        }

        AppendLog($"Starting extraction: {SourcePath}");
        AppendLog($"Output to: {OutputDir}");

        IsBusy = true;
        CanOpenOutput = false;
        ProgressValue = 0;
        IsProgressIndeterminate = true;
        ProgressText = "—";
        StageText = "Preparing";
        StatusText = string.Empty;
        _lastProgressBucket = -1;
        _lastStatusMessage = string.Empty;

        using var cts = new CancellationTokenSource();
        _cts = cts;
        // Progress<T> is constructed on the UI thread, so its callback is marshaled back to the UI thread; log/progress updates never touch controls across threads
        var progress = new Progress<ExtractionProgress>(OnProgress);

        try
        {
            var extractor = ExtractorFactory.Create(SourcePath);
            var result = await extractor.ExtractAsync(SourcePath, OutputDir, progress, cts.Token);

            LastDiagnostic = ComposeDiagnostic(result.ErrorMessage, result.Diagnostic ?? string.Empty);
            IsProgressIndeterminate = false;

            if (result.Success)
            {
                ProgressValue = 100;
                ProgressText = "100%";
                StageText = "Completed";
                StatusText = $"{result.FileCount} files";
                CanOpenOutput = true;
                AppendLog($"Done, {result.FileCount} files");
                AppendLog("Output directory: " + result.OutputDir);
                return;
            }

            ProgressValue = 0;
            ProgressText = "—";
            StageText = "Failed";
            StatusText = "Extraction failed";
            AppendLog("Failed: " + result.ErrorMessage);
            if (!string.IsNullOrWhiteSpace(result.Diagnostic))
            {
                AppendLog("Diagnostic information: " + result.Diagnostic.TrimEnd());
            }

            if (result.ErrorMessage != "Extraction canceled")
            {
                NoticeRequested?.Invoke(result.ErrorMessage ?? "Extraction failed.");
            }
        }
        catch (NotSupportedException ex)
        {
            ProgressText = "—";
            StageText = "Failed";
            StatusText = "Not supported";
            LastDiagnostic = ComposeDiagnostic(ex.Message, ex.ToString());
            AppendLog("Cannot extract: " + ex.Message);
            NoticeRequested?.Invoke(ex.Message);
        }
        catch (Exception ex)
        {
            ProgressText = "—";
            StageText = "Failed";
            StatusText = "Extraction failed";
            LastDiagnostic = ComposeDiagnostic("Unhandled exception", ex.ToString());
            AppendLog($"Unhandled exception: {ex.Message}");
            AppendLog("Diagnostic information: " + LastDiagnostic);
            NoticeRequested?.Invoke("An exception occurred during extraction; see the log for details.");
        }
        finally
        {
            _cts = null;
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (_cts is null)
        {
            AppendLog("There is no extraction task in progress.");
            return;
        }

        AppendLog("Canceling; incomplete output will be cleaned up …");
        StatusText = "Canceling";
        _cts.Cancel();
    }

    [RelayCommand(CanExecute = nameof(CanOpenOutput))]
    private void OpenOutput() => OpenOutputRequested?.Invoke(OutputDir);

    [RelayCommand(CanExecute = nameof(CanCopyDiagnostic))]
    private void CopyDiagnostic() => DiagnosticCopyRequested?.Invoke(LastDiagnostic);

    /// <summary>The copied diagnostic information should at least contain the file path, output directory, engine, and a hint about the exception / exit code.</summary>
    private string ComposeDiagnostic(string? errorMessage, string detail) =>
        $"""
        Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}
        Program: UnpackStation
        Source file: {SourcePath ?? "Not selected"}
        Output directory: {OutputDir}
        Result: {errorMessage ?? "Success"}
        Engine: Inno Setup uses innoextract 1.13.0, other exe files use 7z.dll (7-Zip 26.01 x64), msi uses msiexec /a

        {detail}
        """;

    private void OnProgress(ExtractionProgress report)
    {
        if (report.Percent < 0)
        {
            // When progress cannot be determined, the bar uses Marquee; do not fabricate a percentage
            IsProgressIndeterminate = true;
            ProgressText = "—";
        }
        else
        {
            IsProgressIndeterminate = false;
            ProgressValue = Math.Clamp(report.Percent, 0, 100);
            ProgressText = $"{ProgressValue}%";
        }

        StageText = report.Stage.ToString();
        var message = report.Detail;
        if (message.StartsWith("Warning:", StringComparison.Ordinal))
        {
            StatusText = message;
            AppendLog(message);
            return;
        }

        StatusText = message;
        if (report.Percent >= 0)
        {
            // Progress callbacks are dense; log one line every 5%, regardless of the engine's measurement unit
            var bucket = report.Percent / 5;
            if (bucket == _lastProgressBucket)
            {
                return;
            }

            _lastProgressBucket = bucket;
            AppendLog($"{report.Stage} · {message}");
            return;
        }

        if (string.Equals(message, _lastStatusMessage, StringComparison.Ordinal))
        {
            return;
        }

        _lastStatusMessage = message;
        AppendLog($"{report.Stage} · {message}");
    }

    private static bool IsSupported(string extension) =>
        string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(extension, ".msi", StringComparison.OrdinalIgnoreCase);

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024:0.##} GB",
        >= 1024L * 1024 => $"{bytes / 1024d / 1024:0.#} MB",
        >= 1024L => $"{bytes / 1024d:0.#} KB",
        _ => $"{bytes} B"
    };
}
