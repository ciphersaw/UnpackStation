using UnpackStation.Services;
using Xunit;

namespace UnpackStation.Tests;

/// <summary>Final decision for the default output directory: keep the source directory when the package already has a top-level directory, otherwise add one level.</summary>
public class DefaultOutputResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "UnpackStationDefaultDir_" + Guid.NewGuid().ToString("N"));

    public DefaultOutputResolverTests() => Directory.CreateDirectory(_root);

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

    private static string Sample(string name) => TestFixtures.Sample(name);

    private string CopySample(string sample, string fileName)
    {
        var path = Path.Combine(_root, fileName);
        File.Copy(Sample(sample), path);
        return path;
    }

    [Fact]
    public void DecideOutputDir_PackageHasTopLevelDirectory_OutputIsThatDirectory()
    {
        // New semantics: Go's output directory is Tool\Go, consistent with Python's Tool\Python27
        var result = DefaultOutputResolver.DecideOutputDir(_root, "Go", out var reason);

        Assert.Equal(Path.Combine(_root, "Go"), result);
        Assert.Contains("extracted result actually lives", reason);
        Assert.False(Directory.Exists(result), "Only computes the path, does not create the directory");
    }

    [Fact]
    public void DecideOutputDir_NoTopLevelDirectory_UsesInstallDirNameAsResult()
    {
        var result = DefaultOutputResolver.DecideOutputDir(_root, "Python27", out var reason);

        Assert.Equal(Path.Combine(_root, "Python27"), result);
        Assert.Contains("Python27", reason);
        Assert.False(Directory.Exists(result), "Only computes the path, does not create the directory");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("C:\\Program Files\\Go")]
    [InlineData("[Unresolved]")]
    public void DecideOutputDir_NameUnavailable_FallsBackToSourceDirectory(string? leaf)
    {
        var result = DefaultOutputResolver.DecideOutputDir(_root, leaf, out var reason);

        Assert.Equal(_root, result);
        Assert.Contains("keep extracting into the source file's directory", reason);
    }

    [Fact]
    public void DecideOutputDir_TargetOccupiedByFileOfSameName_FallsBackToSourceDirectory()
    {
        File.WriteAllText(Path.Combine(_root, "Python27"), "User's existing file");

        var result = DefaultOutputResolver.DecideOutputDir(_root, "Python27", out var reason);

        Assert.Equal(_root, result);
        Assert.Contains("occupied by a file of the same name", reason);
    }

    [Fact]
    public async Task InspectAsync_ScatteredFilesExe_AddsPackageFileNameLevel()
    {
        var source = CopySample("plain.7z", "MyTool.exe");

        var plan = await DefaultOutputResolver.InspectAsync(source, CancellationToken.None);

        Assert.Equal(Path.Combine(_root, "MyTool"), plan.OutputDir);
        Assert.Contains(plan.Notes, note => note.Contains("MyTool"));
        Assert.Contains(plan.Notes, note => note.Contains("no single top-level directory"));
    }

    [Fact]
    public async Task InspectAsync_ExeWithSingleTopLevelDirectory_UsesPackageDirNameAsOutput()
    {
        var source = CopySample("wrapped.7z", "Wrapped.exe");

        var plan = await DefaultOutputResolver.InspectAsync(source, CancellationToken.None);

        Assert.Equal(Path.Combine(_root, "App"), plan.OutputDir);
        Assert.Contains(plan.Notes, note => note.Contains("App\\"));
    }

    [Fact]
    public async Task InspectAsync_InvalidMsi_ConservativelyFallsBackToSourceDirectory()
    {
        var source = Path.Combine(_root, "broken.msi");
        File.WriteAllText(source, "This is not an MSI");

        var plan = await DefaultOutputResolver.InspectAsync(source, CancellationToken.None);

        Assert.Equal(_root, plan.OutputDir);
        Assert.Contains(plan.Notes, note => note.Contains("Could not read the default install location from the MSI"));
    }

    [Fact]
    public async Task InspectAsync_UnsupportedExtension_FallsBackToSourceDirectory()
    {
        var source = Path.Combine(_root, "readme.txt");
        File.WriteAllText(source, "placeholder");

        var plan = await DefaultOutputResolver.InspectAsync(source, CancellationToken.None);

        Assert.Equal(_root, plan.OutputDir);
        Assert.Contains(plan.Notes, note => note.Contains("Not .exe / .msi"));
    }
}
