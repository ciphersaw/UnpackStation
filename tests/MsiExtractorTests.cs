using System.Diagnostics;
using System.IO;
using UnpackStation.Services;
using Xunit;

namespace UnpackStation.Tests;

/// <summary>
/// P3 Services layer tests: exit codes, command lines, and cleanup strategies are all verified with replaceable short-lived processes;
/// One case also calls the real msiexec to verify the process chain itself.
/// </summary>
public class MsiExtractorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "UnpackStationP3_" + Guid.NewGuid().ToString("N"));

    public MsiExtractorTests()
    {
        Directory.CreateDirectory(_root);
        // Impersonate the source file; the fake process does not actually read it, but ExtractAsync checks whether the file exists
        File.WriteAllBytes(Path.Combine(_root, "fake.msi"), OpaHeader());
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

    private string Output(string tag) => Path.Combine(_root, tag + "_extracted");

    private static byte[] OpaHeader() => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>Use cmd.exe to impersonate msiexec, exit with the specified code, and first write two files to the output directory.</summary>
    private static MsiExtractor FakeExtractor(int exitCode, string? script = null, TimeSpan? timeout = null)
    {
        return new MsiExtractor(timeout ?? TimeSpan.FromSeconds(30), (_, target, _) =>
        {
            var command = script ??
                          $"mkdir \"{target}\\sub\" 2>nul & echo hello> \"{target}\\readme.txt\" " +
                          $"& echo dll> \"{target}\\sub\\app.dll\" & exit /b {exitCode}";
            return new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c " + command,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
        });
    }

    /// <summary>Directory layout for a custom fake process; target is the engine's internal staging directory.</summary>
    private static MsiExtractor LayoutExtractor(Func<string, string, string> scriptFactory)
    {
        return new MsiExtractor(TimeSpan.FromSeconds(30), (source, target, _) => new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c " + scriptFactory(source, target),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        });
    }

    private bool HasStagingLeftover() =>
        Directory.GetDirectories(_root, ".unpackstation_*").Length > 0;

    [Fact]
    public async Task Extract_RemovesMsiCopyButKeepsPackageProductDirectory()
    {
        var output = Output("go");
        var extractor = LayoutExtractor((source, target) =>
            $"mkdir \"{target}\\Go\\bin\" 2>nul " +
            $"& echo msi> \"{target}\\{Path.GetFileName(source)}\" " +
            $"& echo go> \"{target}\\Go\\bin\\go.exe\" & exit /b 0");

        var result = await extractor.ExtractAsync(
            Path.Combine(_root, "fake.msi"), output, new CollectingProgress(), CancellationToken.None);

        Assert.True(result.Success, result.Diagnostic);
        Assert.True(File.Exists(Path.Combine(output, "Go", "bin", "go.exe")));
        Assert.True(Directory.Exists(Path.Combine(output, "Go")));
        Assert.False(File.Exists(Path.Combine(output, "bin", "go.exe")));
        Assert.False(File.Exists(Path.Combine(output, "fake.msi")));
        Assert.Equal(1, result.FileCount);
        Assert.Contains("Removed the administrative install copy", result.Diagnostic);
        Assert.Contains("Keeping the directory structure shipped with the installer package", result.Diagnostic);
        Assert.False(HasStagingLeftover());
    }

    [Fact]
    public async Task Extract_MultipleTopLevelDirectories_KeepsStructureButRemovesMsiCopy()
    {
        var output = Output("multi");
        var extractor = LayoutExtractor((source, target) =>
            $"mkdir \"{target}\\Go\\bin\" 2>nul & mkdir \"{target}\\Tools\" 2>nul " +
            $"& echo msi> \"{target}\\{Path.GetFileName(source)}\" " +
            $"& echo go> \"{target}\\Go\\bin\\go.exe\" " +
            $"& echo tool> \"{target}\\Tools\\t.exe\" & exit /b 0");

        var result = await extractor.ExtractAsync(
            Path.Combine(_root, "fake.msi"), output, new CollectingProgress(), CancellationToken.None);

        Assert.True(result.Success, result.Diagnostic);
        Assert.True(File.Exists(Path.Combine(output, "Go", "bin", "go.exe")));
        Assert.True(File.Exists(Path.Combine(output, "Tools", "t.exe")));
        Assert.False(File.Exists(Path.Combine(output, "fake.msi")));
        Assert.Contains("Keeping the directory structure shipped with the installer package", result.Diagnostic);
    }

    [Fact]
    public async Task Extract_TopLevelFiles_KeepsOriginalStructure()
    {
        var output = Output("withfile");
        var extractor = LayoutExtractor((source, target) =>
            $"mkdir \"{target}\\Go\\bin\" 2>nul " +
            $"& echo msi> \"{target}\\{Path.GetFileName(source)}\" " +
            $"& echo readme> \"{target}\\readme.txt\" " +
            $"& echo go> \"{target}\\Go\\bin\\go.exe\" & exit /b 0");

        var result = await extractor.ExtractAsync(
            Path.Combine(_root, "fake.msi"), output, new CollectingProgress(), CancellationToken.None);

        Assert.True(result.Success, result.Diagnostic);
        Assert.True(File.Exists(Path.Combine(output, "Go", "bin", "go.exe")));
        Assert.True(File.Exists(Path.Combine(output, "readme.txt")));
        Assert.False(File.Exists(Path.Combine(output, "fake.msi")));
    }

    [Fact]
    public async Task Extract_OutputDirSameNameAsPackageTopLevel_PlacesDirectlyWithoutDuplicateLayer()
    {
        // The default output directory is now Tool\Go; the package's own Go\ layer must be absorbed
        var output = Path.Combine(_root, "Go");
        var extractor = LayoutExtractor((source, target) =>
            $"mkdir \"{target}\\Go\\bin\" 2>nul " +
            $"& echo msi> \"{target}\\{Path.GetFileName(source)}\" " +
            $"& echo go> \"{target}\\Go\\bin\\go.exe\" & exit /b 0");

        var result = await extractor.ExtractAsync(
            Path.Combine(_root, "fake.msi"), output, new CollectingProgress(), CancellationToken.None);

        Assert.True(result.Success, result.Diagnostic);
        Assert.True(File.Exists(Path.Combine(output, "bin", "go.exe")));
        Assert.False(File.Exists(Path.Combine(output, "Go", "bin", "go.exe")), "Should not produce a duplicate Go\\Go layer");
        Assert.False(File.Exists(Path.Combine(output, "fake.msi")));
        Assert.Contains("avoid a duplicate level", result.Diagnostic);
        Assert.False(HasStagingLeftover());
    }

    [Fact]
    public async Task Extract_SameNameTopLevelDirAndOutputExists_MergesRecursivelyWithoutOverwrite()
    {
        var output = Path.Combine(_root, "Go");
        Directory.CreateDirectory(output);
        var keep = Path.Combine(output, "VERSION");
        File.WriteAllText(keep, "Old version, do not overwrite");
        var extractor = LayoutExtractor((source, target) =>
            $"mkdir \"{target}\\Go\\bin\" 2>nul " +
            $"& echo msi> \"{target}\\{Path.GetFileName(source)}\" " +
            $"& echo new> \"{target}\\Go\\VERSION\" " +
            $"& echo go> \"{target}\\Go\\bin\\go.exe\" & exit /b 0");

        var result = await extractor.ExtractAsync(
            Path.Combine(_root, "fake.msi"), output, new CollectingProgress(), CancellationToken.None);

        Assert.True(result.Success, result.Diagnostic);
        Assert.Equal("Old version, do not overwrite", File.ReadAllText(keep));
        Assert.True(File.Exists(Path.Combine(output, "bin", "go.exe")));
        Assert.False(File.Exists(Path.Combine(output, "Go", "bin", "go.exe")));
        Assert.Contains("same name", result.Diagnostic);
        Assert.False(HasStagingLeftover());
    }

    [Fact]
    public async Task Extract_MultipleTopLevelDirsInPackage_NotAbsorbedEvenIfSameName()
    {
        var output = Path.Combine(_root, "Go");
        var extractor = LayoutExtractor((source, target) =>
            $"mkdir \"{target}\\Go\\bin\" 2>nul & mkdir \"{target}\\Tools\" 2>nul " +
            $"& echo go> \"{target}\\Go\\bin\\go.exe\" " +
            $"& echo tool> \"{target}\\Tools\\t.exe\" & exit /b 0");

        var result = await extractor.ExtractAsync(
            Path.Combine(_root, "fake.msi"), output, new CollectingProgress(), CancellationToken.None);

        Assert.True(result.Success, result.Diagnostic);
        Assert.True(File.Exists(Path.Combine(output, "Go", "bin", "go.exe")));
        Assert.True(File.Exists(Path.Combine(output, "Tools", "t.exe")));
        Assert.DoesNotContain("avoid a duplicate level", result.Diagnostic);
    }

    [Fact]
    public async Task Extract_ExtraPFilesLayer_FindsSameNameLayerAlongSingleDirectoryChain()
    {
        // MSIs like PuTTY / Node: ProgramFilesFolder's DefaultDir is not ., so an administrative install adds an extra layer
        var output = Path.Combine(_root, "PuTTY");
        var extractor = LayoutExtractor((source, target) =>
            $"mkdir \"{target}\\PFiles\\PuTTY\" 2>nul " +
            $"& echo msi> \"{target}\\{Path.GetFileName(source)}\" " +
            $"& echo putty> \"{target}\\PFiles\\PuTTY\\putty.exe\" & exit /b 0");

        var result = await extractor.ExtractAsync(
            Path.Combine(_root, "fake.msi"), output, new CollectingProgress(), CancellationToken.None);

        Assert.True(result.Success, result.Diagnostic);
        Assert.True(File.Exists(Path.Combine(output, "putty.exe")));
        Assert.False(File.Exists(Path.Combine(output, "PFiles", "PuTTY", "putty.exe")));
        Assert.Contains("avoid a duplicate level", result.Diagnostic);
        Assert.False(HasStagingLeftover());
    }

    [Fact]
    public async Task Extract_NoSameNameLayerOnSingleDirectoryChain_KeepsOriginalStructure()
    {
        var output = Path.Combine(_root, "MyOut");
        var extractor = LayoutExtractor((_, target) =>
            $"mkdir \"{target}\\Vendor\\Product\" 2>nul " +
            $"& echo app> \"{target}\\Vendor\\Product\\app.exe\" & exit /b 0");

        var result = await extractor.ExtractAsync(
            Path.Combine(_root, "fake.msi"), output, new CollectingProgress(), CancellationToken.None);

        Assert.True(result.Success, result.Diagnostic);
        Assert.True(File.Exists(Path.Combine(output, "Vendor", "Product", "app.exe")));
        Assert.DoesNotContain("avoid a duplicate level", result.Diagnostic);
    }

    [Fact]
    public async Task Extract_ExistingSameNameEntryInOutput_SkipsAndDoesNotOverwrite()
    {
        var output = Output("existing");
        Directory.CreateDirectory(output);
        var keepFile = Path.Combine(output, "readme.txt");
        File.WriteAllText(keepFile, "Don't overwrite me");
        var extractor = LayoutExtractor((_, target) =>
            $"mkdir \"{target}\\sub\" 2>nul " +
            $"& echo new> \"{target}\\readme.txt\" " +
            $"& echo dll> \"{target}\\sub\\app.dll\" & exit /b 0");

        var result = await extractor.ExtractAsync(
            Path.Combine(_root, "fake.msi"), output, new CollectingProgress(), CancellationToken.None);

        Assert.True(result.Success, result.Diagnostic);
        Assert.Equal("Don't overwrite me", File.ReadAllText(keepFile));
        Assert.True(File.Exists(Path.Combine(output, "sub", "app.dll")));
        Assert.Contains("skipped", result.Diagnostic);
        Assert.False(HasStagingLeftover());
    }

    [Fact]
    public async Task Extract_ExistingSameNameProductDirInTarget_MergesRecursivelyWithoutOverwrite()
    {
        var output = Output("merge");
        Directory.CreateDirectory(Path.Combine(output, "Go"));
        var keep = Path.Combine(output, "Go", "VERSION");
        File.WriteAllText(keep, "Old version, do not overwrite");
        var extractor = LayoutExtractor((_, target) =>
            $"mkdir \"{target}\\Go\\bin\" 2>nul " +
            $"& echo new> \"{target}\\Go\\VERSION\" " +
            $"& echo go> \"{target}\\Go\\bin\\go.exe\" & exit /b 0");

        var result = await extractor.ExtractAsync(
            Path.Combine(_root, "fake.msi"), output, new CollectingProgress(), CancellationToken.None);

        Assert.True(result.Success, result.Diagnostic);
        Assert.Equal("Old version, do not overwrite", File.ReadAllText(keep));
        Assert.True(File.Exists(Path.Combine(output, "Go", "bin", "go.exe")));
        Assert.Equal(1, result.FileCount);
        Assert.Contains("same name", result.Diagnostic);
        Assert.False(HasStagingLeftover());
    }

    [Fact]
    public void CanHandle_OnlyMsiExtensionPlusOleCompoundHeader()
    {
        var msi = Path.Combine(_root, "setup.msi");
        File.WriteAllBytes(msi, OpaHeader());
        var txt = Path.Combine(_root, "setup.txt");
        File.WriteAllBytes(txt, OpaHeader());
        var broken = Path.Combine(_root, "broken.msi");
        File.WriteAllBytes(broken, [1, 2, 3, 4, 5, 6, 7, 8]);

        var extractor = new MsiExtractor();
        Assert.True(extractor.CanHandle(msi));
        Assert.False(extractor.CanHandle(txt));
        Assert.False(extractor.CanHandle(broken));
        Assert.False(extractor.CanHandle(Path.Combine(_root, "不存在.msi")));
    }

    [Fact]
    public void BuildArguments_BuildsAdminInstallCommandPerDocsAndQuotesPaths()
    {
        var arguments = MsiExtractor.BuildArguments(
            @"D:\下载\中文 空格.msi", @"D:\输出 目录\setup_extracted", @"C:\Temp\msi.log");

        Assert.Equal(
            "/a \"D:\\下载\\中文 空格.msi\" " +
            "TARGETDIR=\"D:\\输出 目录\\setup_extracted\" /qn /norestart /L*v \"C:\\Temp\\msi.log\"",
            arguments);
    }

    [Fact]
    public void NormalizeOutputDir_StripsTrailingBackslashAndRejectsDriveRoot()
    {
        Assert.True(MsiExtractor.TryNormalizeOutputDir(@"D:\out\folder\", out var root, out var reason));
        Assert.Equal(@"D:\out\folder", root);
        Assert.Null(reason);

        Assert.False(MsiExtractor.TryNormalizeOutputDir(@"D:\", out _, out reason));
        Assert.Contains("drive root", reason);

        Assert.False(MsiExtractor.TryNormalizeOutputDir(string.Empty, out _, out reason));
        Assert.Contains("path is invalid", reason);
    }

    [Fact]
    public void TranslateExitCode_Translates0And3010And1603PerDocs_OthersUnchanged()
    {
        Assert.Null(MsiExtractor.TranslateExitCode(0));
        Assert.Null(MsiExtractor.TranslateExitCode(3010));
        Assert.Contains("1603", MsiExtractor.TranslateExitCode(1603));
        Assert.Contains("Insufficient privileges", MsiExtractor.TranslateExitCode(1603));
        Assert.Equal("msiexec returned exit code 1619", MsiExtractor.TranslateExitCode(1619));
    }

    [Fact]
    public async Task Extract_ExitCode0_ReturnsFileCountAndDiagnostics()
    {
        var output = Output("ok");
        var progress = new CollectingProgress();

        var result = await FakeExtractor(0).ExtractAsync(
            Path.Combine(_root, "fake.msi"), output, progress, CancellationToken.None);

        Assert.True(result.Success, result.Diagnostic);
        Assert.Equal(2, result.FileCount);
        Assert.True(File.Exists(Path.Combine(output, "readme.txt")));
        Assert.True(File.Exists(Path.Combine(output, "sub", "app.dll")));
        Assert.Contains("Exit code: 0", result.Diagnostic);
        Assert.Contains("Command line", result.Diagnostic);
        Assert.Contains(progress.Reports, r => r.percent == 100);
        // When real progress is unavailable, only Marquee and 100% are allowed; do not fabricate intermediate percentages
        Assert.DoesNotContain(progress.Reports, r => r.percent is > 0 and < 100);
    }

    [Fact]
    public async Task Extract_ExitCode3010_SuccessButWarnsRebootRequired()
    {
        var output = Output("reboot");
        var progress = new CollectingProgress();

        var result = await FakeExtractor(3010).ExtractAsync(
            Path.Combine(_root, "fake.msi"), output, progress, CancellationToken.None);

        Assert.True(result.Success, result.Diagnostic);
        Assert.Contains("3010", result.Diagnostic);
        Assert.Contains(progress.Reports, r => r.message.Contains("a restart is required"));
    }

    [Fact]
    public async Task Extract_ExitCode1603_GivesPermissionHintAndDeletesExtractedFiles()
    {
        var output = Output("denied");

        var result = await FakeExtractor(1603).ExtractAsync(
            Path.Combine(_root, "fake.msi"), output, new CollectingProgress(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("1603", result.ErrorMessage);
        Assert.Contains("Exit code: 1603", result.Diagnostic);
        Assert.False(Directory.Exists(output));
        Assert.False(HasStagingLeftover());
    }

    [Fact]
    public async Task Extract_UnknownExitCode_ShownAsIs()
    {
        var output = Output("unknown");

        var result = await FakeExtractor(1619).ExtractAsync(
            Path.Combine(_root, "fake.msi"), output, new CollectingProgress(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("msiexec returned exit code 1619", result.ErrorMessage);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task Extract_OnFailureKeepsUserFilesInOutputDirectory()
    {
        var output = Output("keep");
        Directory.CreateDirectory(output);
        var keepFile = Path.Combine(output, "我的文件.txt");
        File.WriteAllText(keepFile, "Don't delete me");

        var result = await FakeExtractor(1603).ExtractAsync(
            Path.Combine(_root, "fake.msi"), output, new CollectingProgress(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(Directory.Exists(output));
        Assert.Equal("Don't delete me", File.ReadAllText(keepFile));
        Assert.False(File.Exists(Path.Combine(output, "readme.txt")));
        Assert.False(Directory.Exists(Path.Combine(output, "sub")));
    }

    [Fact]
    public async Task Extract_CancelledBeforeStart_NoProcessAndNoOutputDirectory()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var output = Output("cancel");

        var result = await FakeExtractor(0).ExtractAsync(
            Path.Combine(_root, "fake.msi"), output, new CollectingProgress(), cts.Token);

        Assert.False(result.Success);
        Assert.Equal("Extraction canceled", result.ErrorMessage);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task Extract_Timeout_KillsProcessAndCleansOutputDirectory()
    {
        var output = Output("timeout");
        var script = "mkdir \"" + output + "\\sub\" 2>nul & echo x> \"" + output + "\\partial.txt\" " +
                     "& ping -n 20 127.0.0.1 >nul 2>nul & exit /b 0";
        var extractor = FakeExtractor(0, script, TimeSpan.FromSeconds(1));

        var result = await extractor.ExtractAsync(
            Path.Combine(_root, "fake.msi"), output, new CollectingProgress(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Extraction timed out", result.ErrorMessage);
        Assert.Contains("force-killing the process tree", result.Diagnostic);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task Extract_RealMsiexecWithInvalidPackage_GivesExitCodeDiagnosticAndNoOutputDirectory()
    {
        var source = Path.Combine(_root, "invalid.msi");
        File.WriteAllText(source, "this is not an msi");
        var output = Output("realmsiexec");

        var result = await new MsiExtractor().ExtractAsync(
            source, output, new CollectingProgress(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.StartsWith("msiexec returned exit code", result.ErrorMessage);
        Assert.Contains("Command line", result.Diagnostic);
        Assert.Contains("Exit code:", result.Diagnostic);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task Extract_FileCountExceedsLimit_AbortsAndDoesNotWriteToOutputDirectory()
    {
        var output = Output("bombcount");
        var extractor = new MsiExtractor(TimeSpan.FromSeconds(30), (_, target, _) => new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c " + $"echo a> \"{target}\\a.dll\" & echo b> \"{target}\\b.dll\" & exit /b 0",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }, maxFileCount: 1);

        var result = await extractor.ExtractAsync(
            Path.Combine(_root, "fake.msi"), output, new CollectingProgress(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("exceeds the limit", result.ErrorMessage);
        Assert.Contains("Staging directory stats", result.Diagnostic);
        Assert.False(Directory.Exists(output));
        Assert.False(HasStagingLeftover());
    }

    [Fact]
    public async Task Extract_TotalBytesExceedLimit_AbortsAndDoesNotWriteToOutputDirectory()
    {
        var output = Output("bombsize");
        var extractor = new MsiExtractor(TimeSpan.FromSeconds(30), (_, target, _) => new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c " + $"echo hello> \"{target}\\a.dll\" & exit /b 0",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }, maxTotalBytes: 1);

        var result = await extractor.ExtractAsync(
            Path.Combine(_root, "fake.msi"), output, new CollectingProgress(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("exceeds the limit", result.ErrorMessage);
        Assert.False(Directory.Exists(output));
        Assert.False(HasStagingLeftover());
    }

    [Fact]
    public async Task Extract_ExtractedDllGetsMarkOfTheWeb()
    {
        var output = Output("motw");
        var extractor = LayoutExtractor((_, target) =>
            $"mkdir \"{target}\\bin\" 2>nul & echo dll> \"{target}\\bin\\app.dll\" & exit /b 0");

        var result = await extractor.ExtractAsync(
            Path.Combine(_root, "fake.msi"), output, new CollectingProgress(), CancellationToken.None);

        Assert.True(result.Success, result.Diagnostic);
        var dll = Path.Combine(output, "bin", "app.dll");
        Assert.True(File.Exists(dll));
        Assert.Contains("ZoneId=3", File.ReadAllText(dll + MarkOfTheWeb.StreamSuffix));
        Assert.Contains("Mark of the Web", result.Diagnostic);
    }

    [Fact]
    public async Task Extract_ExistingUserDllOfSameName_NotOverwrittenAndNotMarked()
    {
        var output = Output("motw_existing");
        Directory.CreateDirectory(output);
        var existing = Path.Combine(output, "app.dll");
        File.WriteAllText(existing, "old");
        var extractor = LayoutExtractor((_, target) =>
            $"echo dll> \"{target}\\app.dll\" & exit /b 0");

        var result = await extractor.ExtractAsync(
            Path.Combine(_root, "fake.msi"), output, new CollectingProgress(), CancellationToken.None);

        Assert.True(result.Success, result.Diagnostic);
        Assert.Equal("old", File.ReadAllText(existing));
        Assert.False(File.Exists(existing + MarkOfTheWeb.StreamSuffix), "The user's existing file should not be marked");
    }

    [Theory]
    [InlineData(0, 10, 0)]
    [InlineData(5, 10, 49)]
    [InlineData(10, 10, 99)]
    [InlineData(20, 10, 99)]
    [InlineData(5, 0, 0)]
    public void ComputePercent_ConvertsWrittenFileCountAndCapsAt99(int count, int expected, int percent)
    {
        Assert.Equal(percent, MsiExtractor.ComputePercent(count, expected));
    }

    [Fact]
    public async Task Extract_WhenFileTableCountAvailable_ReportsRealIntermediateProgress()
    {
        var output = Output("progress");
        var extractor = new MsiExtractor(
            TimeSpan.FromSeconds(30),
            (_, target, _) => new ProcessStartInfo
            {
                FileName = "cmd.exe",
                // Stay for about 1 second after writing the file so the 250ms polling can capture the intermediate state
                Arguments = "/c " + $"echo a> \"{target}\\a.txt\" & ping -n 2 127.0.0.1 >nul & exit /b 0",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            fileCountProbe: _ => 2);

        var progress = new CollectingProgress();
        var result = await extractor.ExtractAsync(
            Path.Combine(_root, "fake.msi"), output, progress, CancellationToken.None);

        Assert.True(result.Success, result.Diagnostic);
        Assert.Contains(progress.Reports, p => p.percent > 0 && p.percent < 100);
        Assert.Contains(progress.Reports, p => p.percent == 100);
        Assert.Contains("Progress denominator", result.Diagnostic);
    }

    [Fact]
    public async Task Extract_WhenFileTableCountUnavailable_KeepsIndeterminateProgressWithoutFabricatedPercent()
    {
        var output = Output("noprogress");
        var extractor = new MsiExtractor(
            TimeSpan.FromSeconds(30),
            (_, target, _) => new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c " + $"echo a> \"{target}\\a.txt\" & exit /b 0",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            fileCountProbe: _ => null);

        var progress = new CollectingProgress();
        var result = await extractor.ExtractAsync(
            Path.Combine(_root, "fake.msi"), output, progress, CancellationToken.None);

        Assert.True(result.Success, result.Diagnostic);
        Assert.DoesNotContain(progress.Reports, p => p.percent > 0 && p.percent < 100);
        Assert.Contains("progress stays indeterminate", result.Diagnostic);
    }

    private sealed class CollectingProgress : IProgress<(int percent, string message)>
    {
        internal List<(int percent, string message)> Reports { get; } = new();

        public void Report((int percent, string message) value)
        {
            lock (Reports)
            {
                Reports.Add(value);
            }
        }
    }
}
