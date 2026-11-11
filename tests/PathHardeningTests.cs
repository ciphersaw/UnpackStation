using System.Diagnostics;
using System.IO;
using UnpackStation.Services;
using Xunit;

namespace UnpackStation.Tests;

/// <summary>P4 path validation hardening: reserved device names, symbolic links/junctions, and out-of-boundary detection.</summary>
public class PathHardeningTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "UnpackStationHardening_" + Guid.NewGuid().ToString("N"));

    public PathHardeningTests() => Directory.CreateDirectory(_root);

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
    [InlineData("CON.txt")]
    [InlineData("NUL")]
    [InlineData("COM1.dll")]
    [InlineData("sub/LPT9.exe")]
    [InlineData("AUX ")]
    public void WindowsReservedDeviceNames_AlwaysRejected(string entryPath)
    {
        Assert.False(OutputPathValidator.TryResolve(_root, entryPath, out _, out var reason));
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Fact]
    public void HasReparsePointInParents_NormalSubdirectory_Allowed()
    {
        var sub = Path.Combine(_root, "sub", "nested");
        Directory.CreateDirectory(sub);

        Assert.False(OutputPathValidator.HasReparsePointInParents(
            _root, Path.Combine(sub, "app.dll"), out var reason));
        Assert.Empty(reason);
    }

    [Fact]
    public void HasReparsePointInParents_ParentIsJunction_Blocked()
    {
        var link = Path.Combine(_root, "link");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        if (!TryCreateJunction(link, outside))
        {
            // Some environments do not allow creating junctions; skip this case instead of failing
            return;
        }

        Assert.True(OutputPathValidator.HasReparsePointInParents(
            _root, Path.Combine(link, "evil.dll"), out var reason));
        Assert.Contains("symbolic link", reason);
    }

    [Fact]
    public void IsInsideRoot_OnlyInsideOutputDirectory()
    {
        Assert.True(OutputPathValidator.IsInsideRoot(_root, Path.Combine(_root, "a", "b.dll")));
        Assert.False(OutputPathValidator.IsInsideRoot(_root, _root));
        Assert.False(OutputPathValidator.IsInsideRoot(_root, Path.Combine(_root + "_other", "b.dll")));
    }

    /// <summary>A directory junction (mklink /J) needs no administrator privileges; on failure return false so the case is skipped.</summary>
    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(5000);
            return Directory.Exists(link) &&
                   (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }
}
