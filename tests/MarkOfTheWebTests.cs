using System.IO;
using System.Text;
using UnpackStation.Services;
using Xunit;

namespace UnpackStation.Tests;

/// <summary>P4 MoTW: write Zone.Identifier only to executable files newly extracted in this run.</summary>
public class MarkOfTheWebTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "UnpackStationMoTW_" + Guid.NewGuid().ToString("N"));

    public MarkOfTheWebTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Theory]
    [InlineData("app.exe", true)]
    [InlineData("app.EXE", true)]
    [InlineData("lib.dll", true)]
    [InlineData("setup.msi", true)]
    [InlineData("run.cmd", true)]
    [InlineData("script.ps1", true)]
    [InlineData("readme.txt", false)]
    [InlineData("无扩展名", false)]
    public void NeedsMark_OnlyExecutableTypes(string name, bool expected)
    {
        Assert.Equal(expected, MarkOfTheWeb.NeedsMark(name));
    }

    [Fact]
    public void Apply_MarksNewlyExtractedExeAndDll_PlainTextUnaffected()
    {
        var exe = Path.Combine(_root, "app.exe");
        var dll = Path.Combine(_root, "lib.dll");
        var txt = Path.Combine(_root, "readme.txt");
        File.WriteAllText(exe, "MZ");
        File.WriteAllText(dll, "MZ");
        File.WriteAllText(txt, "hello");
        var diag = new StringBuilder();

        var (marked, failed) = MarkOfTheWeb.Apply([exe, dll, txt], diag);

        Assert.Equal(2, marked);
        Assert.Equal(0, failed);
        Assert.Contains("ZoneId=3", File.ReadAllText(exe + MarkOfTheWeb.StreamSuffix));
        Assert.Contains("ZoneId=3", File.ReadAllText(dll + MarkOfTheWeb.StreamSuffix));
        Assert.False(File.Exists(txt + MarkOfTheWeb.StreamSuffix));
        Assert.Contains("Mark of the Web", diag.ToString());
    }

    [Fact]
    public void Apply_DoesNotTouchFilesOutsideList()
    {
        var old = Path.Combine(_root, "old.dll");
        var fresh = Path.Combine(_root, "fresh.dll");
        File.WriteAllText(old, "old");
        File.WriteAllText(fresh, "new");

        MarkOfTheWeb.Apply([fresh], new StringBuilder());

        Assert.False(File.Exists(old + MarkOfTheWeb.StreamSuffix));
        Assert.True(File.Exists(fresh + MarkOfTheWeb.StreamSuffix));
    }

    [Fact]
    public void WriteZoneIdentifier_FileNotFound_ReturnsFalseSafely()
    {
        Assert.False(MarkOfTheWeb.WriteZoneIdentifier(Path.Combine(_root, "missing.exe")));
    }
}
