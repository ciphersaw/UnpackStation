using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using UnpackStation.ViewModels;

namespace UnpackStation;

/// <summary>Main window: only translates UI events into ViewModel calls; no business logic here.</summary>
public partial class MainWindow : Window
{
    private static readonly SolidColorBrush HighlightBrush = new(Color.FromRgb(0x00, 0x78, 0xD4));
    private Brush? _normalBorderBrush;

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();

        var vm = new MainViewModel();
        vm.NoticeRequested += message =>
            MessageBox.Show(this, message, "UnpackStation", MessageBoxButton.OK, MessageBoxImage.Information);
        vm.OpenOutputRequested += path =>
        {
            try
            {
                // ShellExecute resolves extensionless paths via PATHEXT: "uniclash-setup" can be
                // mistaken for the neighbouring "uniclash-setup.exe". Open Explorer explicitly and
                // only when the directory really exists, so no extracted program is ever launched.
                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                {
                    vm.AppendLog($"Output directory does not exist; not opening: {path}");
                    return;
                }

                var fullPath = Path.GetFullPath(path);
                var startInfo = new ProcessStartInfo("explorer.exe")
                {
                    UseShellExecute = true
                };
                startInfo.ArgumentList.Add(fullPath);
                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                vm.AppendLog($"Failed to open the output directory: {ex.Message}");
            }
        };
        vm.DiagnosticCopyRequested += text =>
        {
            try
            {
                Clipboard.SetText(text);
                vm.AppendLog("Diagnostic information copied to the clipboard.");
            }
            catch (Exception ex)
            {
                vm.AppendLog($"Failed to copy diagnostic information: {ex.Message}");
            }
        };
        DataContext = vm;

        _normalBorderBrush = DropZone.BorderBrush;
        vm.AppendLog(
            "UnpackStation started. Inno Setup uses innoextract, other exe files use 7z.dll, msi uses msiexec /a for extraction;");
        vm.AppendLog("Path traversal checks, decompression bomb protection, and Mark of the Web tagging for executables are enabled.");
    }

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            DropZone.BorderBrush = HighlightBrush;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void DropZone_DragOver(object sender, DragEventArgs e) => DropZone_DragEnter(sender, e);

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        DropZone.BorderBrush = _normalBorderBrush;
        e.Handled = true;
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        DropZone.BorderBrush = _normalBorderBrush;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            return;
        }

        // Only one installer package at a time; when several are dropped, take the first and note it in the log
        if (paths.Length > 1)
        {
            ViewModel.AppendLog($"Only one installer package at a time; taking the first ({paths.Length} files were dropped).");
        }

        ViewModel.SetSourceFile(paths[0]);
        e.Handled = true;
    }

    private void DropZone_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.IsBusy)
        {
            ViewModel.AppendLog("Extraction in progress; the source file cannot be changed.");
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Select an installer package",
            Filter = "Installer packages (*.exe;*.msi)|*.exe;*.msi|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            ViewModel.SetSourceFile(dialog.FileName);
        }
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy)
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "Select an output directory",
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(ViewModel.OutputDir) && Directory.Exists(ViewModel.OutputDir))
        {
            dialog.InitialDirectory = ViewModel.OutputDir;
        }

        if (dialog.ShowDialog(this) == true)
        {
            ViewModel.SetOutputDir(dialog.FolderName);
        }
    }

    private void LogBox_TextChanged(object sender, TextChangedEventArgs e) => LogBox.ScrollToEnd();

    private void About_Click(object sender, RoutedEventArgs e) =>
        new AboutWindow { Owner = this }.ShowDialog();
}
