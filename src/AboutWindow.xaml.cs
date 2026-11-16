using System.Diagnostics;
using System.IO;
using System.Windows;

namespace UnpackStation;

/// <summary>About page: engine/license notices for 7z.dll (LGPL) and innoextract (zlib/libpng) must stay visible.</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
    }

    private void Open7zLicense_Click(object sender, RoutedEventArgs e) =>
        OpenLicense(Path.Combine("engines", "7z", "License.txt"), "7-Zip");

    private void OpenInnoextractLicense_Click(object sender, RoutedEventArgs e) =>
        OpenLicense(Path.Combine("engines", "innoextract", "LICENSE.txt"), "innoextract");

    private void OpenLicense(string relativePath, string engineName)
    {
        var license = Path.Combine(AppContext.BaseDirectory, relativePath);
        if (!File.Exists(license))
        {
            MessageBox.Show(this, $"The bundled {engineName} license file could not be found.", "About UnpackStation",
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
            MessageBox.Show(this, $"Failed to open the {engineName} license file: {ex.Message}", "About UnpackStation",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
