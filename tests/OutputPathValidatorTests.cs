using System.IO;
using UnpackStation.Services;
using Xunit;

namespace UnpackStation.Tests;

/// <summary>Path traversal validation is the core security baseline of an extract tool, covered separately.</summary>
public class OutputPathValidatorTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "UnpackStationPath_" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("readme.txt")]
    [InlineData("sub/app.exe")]
    [InlineData("sub\\app.exe")]
    [InlineData("中文 目录/文件.dll")]
    [InlineData("./readme.txt")]
    public void ValidEntryPath_PassesAndStaysInsideOutputDirectory(string entryPath)
    {
        Assert.True(OutputPathValidator.TryResolve(_root, entryPath, out var fullPath, out var reason), reason);
        Assert.StartsWith(Path.GetFullPath(_root), fullPath, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../evil.exe")]
    [InlineData("..\\evil.exe")]
    [InlineData("sub/../../evil.exe")]
    [InlineData("C:\\Windows\\evil.exe")]
    [InlineData("D:evil.exe")]
    [InlineData("/absolute/evil.exe")]
    [InlineData("\\\\server\\share\\evil.exe")]
    [InlineData("file.exe:evil")]
    [InlineData("")]
    public void DangerousEntryPath_AlwaysRejected(string entryPath)
    {
        Assert.False(OutputPathValidator.TryResolve(_root, entryPath, out _, out var reason));
        Assert.False(string.IsNullOrEmpty(reason));
    }
}
