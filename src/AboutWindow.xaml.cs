using System.Diagnostics;
using System.IO;
using System.Windows;

namespace UnpackStation;

/// <summary>About page: the LGPL notice is a hard requirement for shipping 7z.dll and must stay visible.</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
    }

    private void OpenLicense_Click(object sender, RoutedEventArgs e)
    {
        var license = Path.Combine(AppContext.BaseDirectory, "engines", "7z", "License.txt");
        if (!File.Exists(license))
        {
            MessageBox.Show(this, "The bundled License.txt could not be found.", "About UnpackStation",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            // Only open the license text shipped with the program; never run any extracted program
            Process.Start(new ProcessStartInfo { FileName = license, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to open the license file: {ex.Message}", "About UnpackStation",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
