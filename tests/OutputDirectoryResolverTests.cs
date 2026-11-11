using UnpackStation.Services;
using Xunit;

namespace UnpackStation.Tests;

/// <summary>Tests only the Services layer: the rules for computing the default output directory.</summary>
public class OutputDirectoryResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "UnpackStationTests_" + Guid.NewGuid().ToString("N"));

    public OutputDirectoryResolverTests()
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
            // Same as above
        }
    }

    [Fact]
    public void Resolve_DefaultOutputDirIsSourceFileDirectory()
    {
        var source = CreateSource("setup.exe");

        Assert.Equal(_root, OutputDirectoryResolver.Resolve(source));
    }

    [Fact]
    public void Resolve_NoLongerAppendsExtractedSuffix()
    {
        var source = CreateSource("go1.26.4.windows-amd64.msi");
        Directory.CreateDirectory(Path.Combine(_root, "go1.26.4.windows-amd64_extracted"));
        Directory.CreateDirectory(Path.Combine(_root, "go1.26.4.windows-amd64_extracted_1"));

        Assert.Equal(_root, OutputDirectoryResolver.Resolve(source));
    }

    [Fact]
    public void Resolve_ChineseAndSpacePathWorks()
    {
        var source = CreateSource("我的 安装包.msi");

        Assert.Equal(_root, OutputDirectoryResolver.Resolve(source));
    }

    [Fact]
    public void Resolve_RelativeInputPathAlsoReturnsAbsoluteDirectory()
    {
        var source = CreateSource("setup.exe");

        Assert.Equal(_root, OutputDirectoryResolver.Resolve(source));
        Assert.True(Path.IsPathRooted(OutputDirectoryResolver.Resolve(source)));
    }

    [Fact]
    public void Resolve_SourceFileAtDriveRoot_FallsBackToSeparateSubdirectory()
    {
        // Only computes the path, does not actually create a file at the drive root
        var rootPath = Path.GetPathRoot(_root)!;
        var source = Path.Combine(rootPath, "setup.msi");

        Assert.Equal(Path.Combine(rootPath, "setup_extracted"), OutputDirectoryResolver.Resolve(source));
    }

    [Fact]
    public void Resolve_EmptyPathThrowsArgumentException()
    {
        Assert.ThrowsAny<ArgumentException>(() => OutputDirectoryResolver.Resolve("   "));
    }

    private string CreateSource(string fileName)
    {
        var path = Path.Combine(_root, fileName);
        File.WriteAllText(path, "placeholder content");
        return path;
    }
}
