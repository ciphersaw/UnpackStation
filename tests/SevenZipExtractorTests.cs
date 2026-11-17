using System.IO;
using UnpackStation.Models;
using UnpackStation.Services;
using UnpackStation.Services.SevenZip;
using Xunit;

// 7z.dll is a native library; run tests serially to avoid interference from concurrent calls
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace UnpackStation.Tests;

/// <summary>Services layer integration tests: exercise the full 7z.dll chain with real archive samples.</summary>
public class SevenZipExtractorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "UnpackStationP2_" + Guid.NewGuid().ToString("N"));

    public SevenZipExtractorTests()
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
            // Failure to clean up the temp directory does not affect the test result
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string Output(string tag) => Path.Combine(_root, tag + "_extracted");

    private static string Sample(string name) => TestFixtures.Sample(name);

    [Fact]
    public void Native_EnumeratesFormatsSupportedBy7z()
    {
        var formats = SevenZipNative.EnumerateFormats();

        Assert.True(formats.Count > 10, $"Only enumerated {formats.Count} formats: {string.Join(",", formats.Select(f => f.Name))}");
        Assert.Contains(formats, f => f.Name.Equals("7z", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Extract_Plain7z_DetectsFormatByContent()
    {
        var output = Output("plain");
        var progress = new CollectingProgress();

        var result = await new SevenZipExtractor().ExtractAsync(
            Sample("plain.7z"), output, progress, CancellationToken.None);

        Assert.True(result.Success, result.Diagnostic);
        Assert.Equal(2, result.FileCount);
        Assert.Equal("hello unpackstation", File.ReadAllText(Path.Combine(output, "readme.txt")).Trim());
        Assert.True(File.Exists(Path.Combine(output, "sub", "app.dll")));
        Assert.Contains(progress.Reports, r => r.Percent == 100);
        Assert.Contains("Identified format", result.Diagnostic);
    }

    [Fact]
    public async Task Extract_SfxSelfExtractingExe_SkipsPeHeaderToFindArchive()
    {
        var source = Sample("sfx_sample.exe");
        var extractor = ExtractorFactory.Create(source);

        Assert.True(extractor.CanHandle(source));
        var result = await extractor.ExtractAsync(source, Output("sfx"), new CollectingProgress(), CancellationToken.None);

        Assert.True(result.Success, result.Diagnostic);
        Assert.Equal(2, result.FileCount);
        Assert.True(File.Exists(Path.Combine(Output("sfx"), "readme.txt")));
    }

    [Fact]
    public async Task Extract_ZipContent_AlsoDetectedByContent()
    {
        var result = await new SevenZipExtractor().ExtractAsync(
            Sample("plain.zip"), Output("zip"), new CollectingProgress(), CancellationToken.None);

        Assert.True(result.Success, result.Diagnostic);
        Assert.Equal(2, result.FileCount);
    }

    [Fact]
    public async Task Extract_EncryptedPackage_GivesClearHintAndNoOutputDirectory()
    {
        var output = Output("enc");

        var result = await new SevenZipExtractor().ExtractAsync(
            Sample("encrypted.7z"), output, new CollectingProgress(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("This installer package is encrypted and requires a password", result.ErrorMessage);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task Extract_UnrecognizedFormat_GivesClearHintAndNoOutputDirectory()
    {
        var source = Path.Combine(_root, "garbage.exe");
        var bytes = new byte[4096];
        Random.Shared.NextBytes(bytes);
        File.WriteAllBytes(source, bytes);
        var output = Output("garbage");

        var result = await new SevenZipExtractor().ExtractAsync(
            source, output, new CollectingProgress(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Could not recognize the packaging format of this exe", result.ErrorMessage);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task Extract_Cancel_DeletesIncompleteOutputDirectory()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var output = Output("cancel");

        var result = await new SevenZipExtractor().ExtractAsync(
            Sample("plain.7z"), output, new CollectingProgress(), cts.Token);

        Assert.False(result.Success);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task Extract_PurePeExecutable_DoesNotExtractImageSectionsAndGivesClearHint()
    {
        // Regression: 7-Zip's PE handler can open any MZ file; early versions extracted .text/.rdata and other sections
        var source = Sample("plain_pe.exe");
        var output = Output("pe");

        var result = await new SevenZipExtractor().ExtractAsync(
            source, output, new CollectingProgress(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Could not recognize the packaging format of this exe", result.ErrorMessage);
        Assert.Contains("PE", result.Diagnostic);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task Extract_RealPeStubSfx_PrefersArchiveOverPeSections()
    {
        // Regression: the file itself can be opened by the PE handler, but the payload is 7z, so 7z must be selected
        var source = Sample("pe_sfx.exe");
        var output = Output("pe_sfx");

        var result = await new SevenZipExtractor().ExtractAsync(
            source, output, new CollectingProgress(), CancellationToken.None);

        Assert.True(result.Success, result.Diagnostic);
        Assert.Equal(2, result.FileCount);
        Assert.True(File.Exists(Path.Combine(output, "readme.txt")));
        Assert.True(File.Exists(Path.Combine(output, "sub", "app.dll")));
        Assert.Contains("Identified format: 7z", result.Diagnostic);
        Assert.False(File.Exists(Path.Combine(output, ".text")));
    }

    [Fact]
    public void FormatSelection_OnlyInstallerArchiveFormatsAndOrderedByPriority()
    {
        Assert.True(SevenZipExtractor.IsInstallerFormat("Nsis"));
        Assert.True(SevenZipExtractor.IsInstallerFormat("Cab"));
        Assert.True(SevenZipExtractor.IsInstallerFormat("7z"));
        Assert.True(SevenZipExtractor.IsInstallerFormat("zip"));
        Assert.False(SevenZipExtractor.IsInstallerFormat("PE"));
        Assert.False(SevenZipExtractor.IsInstallerFormat("COFF"));
        Assert.False(SevenZipExtractor.IsInstallerFormat("gzip"));
        Assert.False(SevenZipExtractor.IsInstallerFormat("Compound"));

        var formats = new[]
        {
            new SevenZipFormat("PE", Guid.NewGuid()),
            new SevenZipFormat("zip", Guid.NewGuid()),
            new SevenZipFormat("7z", Guid.NewGuid()),
            new SevenZipFormat("Nsis", Guid.NewGuid()),
            new SevenZipFormat("gzip", Guid.NewGuid())
        };

        var ordered = SevenZipExtractor.OrderForProbing(formats).Select(f => f.Name).ToArray();
        Assert.Equal(["Nsis", "7z", "zip", "PE", "gzip"], ordered);
    }

    [Fact]
    public async Task Extract_ExistingSameNameFileInOutput_SkipsWithoutOverwrite()
    {
        // The default output directory is the source file's directory, which already contains user files that must never be overwritten
        var output = Output("existing_exe");
        Directory.CreateDirectory(Path.Combine(output, "sub"));
        var keep = Path.Combine(output, "readme.txt");
        File.WriteAllText(keep, "Don't overwrite me");

        var result = await new SevenZipExtractor().ExtractAsync(
            Sample("plain.7z"), output, new CollectingProgress(), CancellationToken.None);

        Assert.True(result.Success, result.Diagnostic);
        Assert.Equal("Don't overwrite me", File.ReadAllText(keep));
        Assert.True(File.Exists(Path.Combine(output, "sub", "app.dll")));
        Assert.Equal(1, result.FileCount);
        Assert.Contains("Actually wrote 1 files", result.Diagnostic);
        Assert.Contains("skipped without overwriting", result.Diagnostic);
    }

    [Theory]
    [InlineData("App\\readme.txt", "App", "readme.txt")]
    [InlineData("App\\sub\\app.dll", "App", "sub\\app.dll")]
    [InlineData("sub\\app.dll", "App", "sub\\app.dll")]
    [InlineData("App", "App", "App")]
    [InlineData("app/sub/app.dll", "App", "sub\\app.dll")]
    public void StripTopLevelSegment_StripsOnlyFirstSegmentWithSameName(string path, string topLevel, string expected) =>
        Assert.Equal(expected, SevenZipExtractor.StripTopLevelSegment(path, topLevel));

    [Fact]
    public async Task Extract_OutputDirSameNameAsPackageSingleTopLevel_StripsThatLayer()
    {
        // The output directory is Tool\App; the package's own App\ layer must be absorbed
        var output = Path.Combine(_root, "App");

        var result = await new SevenZipExtractor().ExtractAsync(
            Sample("wrapped.7z"), output, new CollectingProgress(), CancellationToken.None);

        Assert.True(result.Success, result.Diagnostic);
        Assert.True(File.Exists(Path.Combine(output, "readme.txt")));
        Assert.True(File.Exists(Path.Combine(output, "sub", "app.dll")));
        Assert.False(File.Exists(Path.Combine(output, "App", "readme.txt")), "Should not produce a duplicate App\\App layer");
        Assert.Equal(2, result.FileCount);
        Assert.Contains("placed directly into the output directory", result.Diagnostic);
    }

    [Fact]
    public async Task Extract_OutputDirNameMismatch_KeepsPackageHierarchy()
    {
        var output = Output("wrapped_mismatch");

        var result = await new SevenZipExtractor().ExtractAsync(
            Sample("wrapped.7z"), output, new CollectingProgress(), CancellationToken.None);

        Assert.True(result.Success, result.Diagnostic);
        Assert.True(File.Exists(Path.Combine(output, "App", "readme.txt")));
        Assert.True(File.Exists(Path.Combine(output, "App", "sub", "app.dll")));
        Assert.DoesNotContain("placed directly into the output directory", result.Diagnostic);
    }

    [Fact]
    public void CanHandle_OnlyExeExtensionPlusMzHeader()
    {
        var exe = Path.Combine(_root, "setup.exe");
        File.WriteAllBytes(exe, [(byte)'M', (byte)'Z', 0x90, 0x00]);
        var txt = Path.Combine(_root, "notes.txt");
        File.WriteAllBytes(txt, [(byte)'M', (byte)'Z', 0x90, 0x00]);

        var extractor = new SevenZipExtractor();
        Assert.True(extractor.CanHandle(exe));
        Assert.False(extractor.CanHandle(txt));
        Assert.False(extractor.CanHandle(Path.Combine(_root, "不存在.exe")));
    }

    [Fact]
    public void Factory_Msi_UsesMsiExtractorNot7zEngine()
    {
        var source = Path.Combine(_root, "setup.msi");
        File.WriteAllBytes(source, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]);

        Assert.IsType<MsiExtractor>(ExtractorFactory.Create(source));
    }

    [Fact]
    public async Task Extract_ExtractedDllGetsMarkOfTheWeb_PlainTextUnaffected()
    {
        var output = Output("motw");

        var result = await new SevenZipExtractor().ExtractAsync(
            Sample("plain.7z"), output, new CollectingProgress(), CancellationToken.None);

        Assert.True(result.Success, result.Diagnostic);
        var dll = Path.Combine(output, "sub", "app.dll");
        var txt = Path.Combine(output, "readme.txt");
        Assert.True(File.Exists(dll));
        Assert.Contains("ZoneId=3", File.ReadAllText(dll + MarkOfTheWeb.StreamSuffix));
        Assert.False(File.Exists(txt + MarkOfTheWeb.StreamSuffix));
        Assert.Contains("Mark of the Web", result.Diagnostic);
    }

    private sealed class CollectingProgress : IProgress<ExtractionProgress>
    {
        internal List<ExtractionProgress> Reports { get; } = new();

        public void Report(ExtractionProgress value)
        {
            lock (Reports)
            {
                Reports.Add(value);
            }
        }
    }
}
