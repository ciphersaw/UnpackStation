using System.IO;
using System.Text;
using UnpackStation.Services;
using Xunit;

namespace UnpackStation.Tests;

/// <summary>Detection tests for the Inno Setup engine; the setup executable is never run.</summary>
public class InnoSetupExtractorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "UnpackStationInno_" + Guid.NewGuid().ToString("N"));

    public InnoSetupExtractorTests()
    {
        Directory.CreateDirectory(_root);
    }

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

    [Fact]
    public void CanHandle_DetectsInnoSetupSignatureInExe()
    {
        var source = Path.Combine(_root, "uniclash-setup.exe");
        var bytes = new List<byte> { (byte)'M', (byte)'Z' };
        bytes.AddRange(new byte[512]);
        bytes.AddRange(Encoding.ASCII.GetBytes("Inno Setup Setup Data (6.7.0)"));
        File.WriteAllBytes(source, bytes.ToArray());

        var extractor = new InnoSetupExtractor();
        Assert.True(extractor.CanHandle(source));
        Assert.IsType<InnoSetupExtractor>(ExtractorFactory.Create(source));
    }

    [Fact]
    public void CanHandle_OrdinaryPeOrOtherExtension_IsNotClaimedByInnoEngine()
    {
        var source = Path.Combine(_root, "plain.exe");
        File.WriteAllBytes(source, [(byte)'M', (byte)'Z', 0x00, 0x00]);
        var text = Path.Combine(_root, "setup.txt");
        File.WriteAllText(text, "Inno Setup Setup Data (6.7.0)");

        var extractor = new InnoSetupExtractor();
        Assert.False(extractor.CanHandle(source));
        Assert.False(extractor.CanHandle(text));
        Assert.IsType<SevenZipExtractor>(ExtractorFactory.Create(source));
    }

    [Fact]
    public void CanHandle_DetectsSignatureNearEndOfLargeFile()
    {
        var source = Path.Combine(_root, "large-setup.exe");
        using (var stream = File.Create(source))
        {
            stream.Write(new byte[17 * 1024 * 1024]);
            stream.Write(Encoding.ASCII.GetBytes("Inno Setup Setup Data (6.7.0)"));
        }

        Assert.True(new InnoSetupExtractor().CanHandle(source));
    }

    [Fact]
    public void LayoutProbe_StripsAppLayer_AndDetectsRootLevelPayload()
    {
        var layout = InnoSetupLayoutProbe.Parse("app\\uniclash.exe\r\napp\\UniClashCore.exe\r\napp\\app_links_plugin.dll");

        Assert.NotNull(layout);
        Assert.Equal("Inno Setup", layout.FormatName);
        Assert.False(layout.HasSingleTopLevelDirectory);
        Assert.Null(layout.TopLevelName);
        Assert.Equal(3, layout.InspectedEntryCount);
    }

    [Fact]
    public void LayoutProbe_KeepsRealTopLevelDirectoryUnderApp()
    {
        var layout = InnoSetupLayoutProbe.Parse("app\\MyTool\\a.exe\r\napp\\MyTool\\sub\\b.dll");

        Assert.NotNull(layout);
        Assert.True(layout.HasSingleTopLevelDirectory);
        Assert.Equal("MyTool", layout.TopLevelName);
    }

    [Fact]
    public void LayoutProbe_DoesNotStripApp_WhenOtherTopLevelDirectoriesExist()
    {
        var layout = InnoSetupLayoutProbe.Parse("app\\a.exe\r\ndata\\b.bin");

        Assert.NotNull(layout);
        Assert.False(layout.HasSingleTopLevelDirectory);
        Assert.Equal(2, layout.InspectedEntryCount);
    }

    [Fact]
    public void Engine_IsBundledWithTheApplication()
    {
        Assert.True(File.Exists(InnoSetupExtractor.EnginePath), InnoSetupExtractor.EnginePath);
    }
}
