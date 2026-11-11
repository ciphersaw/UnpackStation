using UnpackStation.Services;
using Xunit;

namespace UnpackStation.Tests;

/// <summary>NSIS installer runtime entry filtering: covers the path shapes seen in real installer packages on this machine.</summary>
public class InstallerEntryPathTests
{
    [Theory]
    // Clash.Verge: plugin and wizard resources that do not appear in the install directory after installation
    [InlineData("$PLUGINSDIR\\System.dll", "System.dll")]
    [InlineData("$PLUGINSDIR\\nsis_tauri_utils.dll", "nsis_tauri_utils.dll")]
    [InlineData("$PLUGINSDIR\\modern-wizard.bmp", "modern-wizard.bmp")]
    [InlineData("$PLUGINSDIR\\InstallOptions.ini", "InstallOptions.ini")]
    // Bootstrap installers in the temp directory
    [InlineData("$TEMP\\MicrosoftEdgeWebview2Setup.exe", "MicrosoftEdgeWebview2Setup.exe")]
    [InlineData("$TMP\\bootstrap.exe", "bootstrap.exe")]
    // Only the pseudo-directory segment itself / empty path
    [InlineData("$PLUGINSDIR", "")]
    [InlineData("", "")]
    public void Nsis_SkipsInstallerRuntimeEntries(string path, string fallbackRelative)
    {
        var decision = InstallerEntryPath.Normalize(path, isNsis: true);

        Assert.True(decision.Skip);
        Assert.Equal(fallbackRelative, decision.RelativePath);
        Assert.False(string.IsNullOrEmpty(decision.Reason));
    }

    [Theory]
    // Clash.Verge: real installation content kept as-is
    [InlineData("clash-verge.exe", "clash-verge.exe")]
    [InlineData("resources\\ASN.mmdb", "resources\\ASN.mmdb")]
    [InlineData("verge-mihomo.exe", "verge-mihomo.exe")]
    // Everything: the real program hides under the $PLUGINSDIR\Everything\ subdirectory, kept after stripping the pseudo-directory
    [InlineData("$PLUGINSDIR\\Everything\\Everything.exe", "Everything\\Everything.exe")]
    [InlineData("$PLUGINSDIR\\Everything\\License.txt", "Everything\\License.txt")]
    // Clash for Windows: the embedded program archive is not a plugin and must be kept
    [InlineData("$PLUGINSDIR\\app-64.7z", "app-64.7z")]
    // $INSTDIR is the real install directory; only strip the prefix, do not treat its dlls as plugins
    [InlineData("$INSTDIR\\app.dll", "app.dll")]
    [InlineData("$INSTDIR\\sub\\app.dll", "sub\\app.dll")]
    [InlineData("[0]\\payload.bin", "payload.bin")]
    public void Nsis_KeepsRealInstallContentAndStripsPseudoDirectories(string path, string expected)
    {
        var decision = InstallerEntryPath.Normalize(path, isNsis: true);

        Assert.False(decision.Skip);
        Assert.Equal(expected, decision.RelativePath);
        Assert.Null(decision.Reason);
    }

    [Theory]
    [InlineData("$PLUGINSDIR\\System.dll")]
    [InlineData("$TEMP\\setup.exe")]
    [InlineData("$foo\\bar.txt")]
    [InlineData("App\\readme.txt")]
    [InlineData("$PLUGINSDIR\\Everything\\Everything.exe")]
    public void NonNsisFormat_PathUnchanged(string path)
    {
        var decision = InstallerEntryPath.Normalize(path, isNsis: false);

        Assert.False(decision.Skip);
        Assert.Equal(path, decision.RelativePath);
    }
}
