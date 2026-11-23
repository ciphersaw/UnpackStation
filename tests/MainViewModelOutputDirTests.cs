using UnpackStation.ViewModels;
using Xunit;

namespace UnpackStation.Tests;

/// <summary>Regression tests for keeping the detected folder name when the user picks a different parent directory.</summary>
public class MainViewModelOutputDirTests : IDisposable
{
    private const string DetectedName = "SampleApp";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "UnpackStationOutputDir_" + Guid.NewGuid().ToString("N"));

    public MainViewModelOutputDirTests() => Directory.CreateDirectory(_root);

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

    private string CreateSource()
    {
        var source = Path.Combine(_root, DetectedName + ".exe");
        File.Copy(TestFixtures.Sample("plain.7z"), source);
        return source;
    }

    private static async Task WaitForProbeAsync(MainViewModel vm)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!string.Equals(vm.StatusText, "Ready to start extraction", StringComparison.Ordinal)
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Equal("Ready to start extraction", vm.StatusText);
    }

    [Fact]
    public async Task SetOutputDir_AfterProbe_AppendsDetectedNameToSelectedParent()
    {
        var vm = new MainViewModel();
        vm.SetSourceFile(CreateSource());
        await WaitForProbeAsync(vm);

        Assert.Equal(Path.Combine(_root, DetectedName), vm.OutputDir);

        var parent = Path.Combine(_root, "Program Files");
        Directory.CreateDirectory(parent);
        vm.SetOutputDir(parent);

        Assert.Equal(Path.Combine(parent, DetectedName), vm.OutputDir);
    }

    [Fact]
    public async Task SetOutputDir_ParentAlreadyEndsWithDetectedName_DoesNotAppendTwice()
    {
        var vm = new MainViewModel();
        vm.SetSourceFile(CreateSource());
        await WaitForProbeAsync(vm);

        var target = Path.Combine(_root, DetectedName);
        vm.SetOutputDir(target);

        Assert.Equal(target, vm.OutputDir);
    }

    [Fact]
    public async Task SetOutputDir_BeforeProbeFinishes_ProbeUsesSelectedParentForDetectedName()
    {
        var vm = new MainViewModel();
        vm.SetSourceFile(CreateSource());

        var parent = Path.Combine(_root, "Program Files");
        Directory.CreateDirectory(parent);
        vm.SetOutputDir(parent);

        await WaitForProbeAsync(vm);

        Assert.Equal(Path.Combine(parent, DetectedName), vm.OutputDir);
    }
}
