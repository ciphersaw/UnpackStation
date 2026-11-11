using UnpackStation.Services;
using Xunit;

namespace UnpackStation.Tests;

/// <summary>The EXE side cannot read the install path, so a unified top-level directory is inferred solely from the entry structure.</summary>
public class ArchiveLayoutProbeTests
{
    private static string Sample(string name) => TestFixtures.Sample(name);

    [Theory]
    [InlineData("$PLUGINSDIR\\InstallOptions.dll", "InstallOptions.dll")]
    [InlineData("$INSTDIR\\app.exe", "app.exe")]
    [InlineData("$PLUGINSDIR\\Everything\\Everything.exe", "Everything\\Everything.exe")]
    [InlineData("[0]\\.text", ".text")]
    [InlineData("$PLUGINSDIR", "")]
    [InlineData("0\\payload", "0\\payload")]
    [InlineData("u0\\cab", "u0\\cab")]
    [InlineData("App\\readme.txt", "App\\readme.txt")]
    [InlineData("readme.txt", "readme.txt")]
    [InlineData("App\\sub\\app.dll", "App\\sub\\app.dll")]
    public void NormalizeEntryPath_StripsInstallerPseudoDirectorySegments(string path, string expected) =>
        Assert.Equal(expected, ArchiveLayoutProbe.NormalizeEntryPath(path));

    [Fact]
    public void TryProbe_ScatteredFilesInRoot_DetectsNoSingleTopLevelDirectory()
    {
        // plain.7z = readme.txt + sub\app.dll, with files directly in the root
        var layout = ArchiveLayoutProbe.TryProbe(Sample("plain.7z"), CancellationToken.None);

        Assert.NotNull(layout);
        Assert.False(layout.HasSingleTopLevelDirectory);
        Assert.True(layout.InspectedEntryCount >= 2);
    }

    [Fact]
    public void TryProbe_AllEntriesUnderOneDirectory_DetectsSingleTopLevelDirectory()
    {
        // wrapped.7z = App\readme.txt + App\sub\app.dll
        var layout = ArchiveLayoutProbe.TryProbe(Sample("wrapped.7z"), CancellationToken.None);

        Assert.NotNull(layout);
        Assert.True(layout.HasSingleTopLevelDirectory);
        Assert.Equal("App", layout.TopLevelName);
    }

    [Fact]
    public void TryProbe_SFXSample_AlsoReadsLayout()
    {
        var layout = ArchiveLayoutProbe.TryProbe(Sample("sfx_sample.exe"), CancellationToken.None);

        Assert.NotNull(layout);
        Assert.False(layout.HasSingleTopLevelDirectory, "The sample root has readme.txt, so there is no unified top-level directory");
    }

    [Fact]
    public void TryProbe_FileNotFound_ReturnsNull() =>
        Assert.Null(ArchiveLayoutProbe.TryProbe(
            Path.Combine(Path.GetTempPath(), "UnpackStation_missing_" + Guid.NewGuid().ToString("N") + ".exe"),
            CancellationToken.None));
}
