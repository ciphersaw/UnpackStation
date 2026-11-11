using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace UnpackStation;

/// <summary>
/// Report window for global unhandled exceptions: besides avoiding silent crashes and writing the exception to crash.log,
/// it lets the user copy the full diagnostic information with one click (the "Copy diagnostic information" feature in requirement §6).
/// The UI is built directly in code to avoid introducing XAML for a purely presentational dialog.
/// </summary>
public sealed class CrashReportWindow : Window
{
    private readonly string _diagnostic;

    public CrashReportWindow(string message, string diagnostic)
    {
        _diagnostic = diagnostic;
        Title = "UnpackStation - An unhandled exception occurred";
        Width = 660;
        Height = 440;
        MinWidth = 520;
        MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = SystemColors.ControlBrush;

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = message,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        };
        Grid.SetRow(title, 0);
        grid.Children.Add(title);

        var box = new TextBox
        {
            Text = diagnostic,
            IsReadOnly = true,
            AcceptsReturn = true,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = SystemColors.ControlLightLightBrush,
            Margin = new Thickness(0, 8, 0, 0)
        };
        Grid.SetRow(box, 1);
        grid.Children.Add(box);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };

        var copy = new Button { Content = "Copy diagnostic information", Width = 130, Height = 30 };
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(_diagnostic);
                MessageBox.Show(this, "Diagnostic information copied to the clipboard.", "UnpackStation",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Copy failed: {ex.Message}", "UnpackStation",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };

        var close = new Button { Content = "OK", Width = 80, Height = 30, Margin = new Thickness(12, 0, 0, 0) };
        close.Click += (_, _) => Close();

        buttons.Children.Add(copy);
        buttons.Children.Add(close);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);

        Content = grid;
    }

    /// <summary>Builds the full text for "Copy diagnostic information": time, version, system, exception, and log location.</summary>
    internal static string BuildDiagnostic(string source, Exception ex, string logPath)
    {
        return $"""
            Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}
            Program: UnpackStation (.NET 10 / WPF x64)
            System: {Environment.OSVersion.VersionString}
            Source: {source}
            Crash log: {logPath}
            Process path: {Environment.ProcessPath ?? "Unknown"}

            {ex}
            """;
    }

    internal static string CrashLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UnpackStation", "crash.log");
}
