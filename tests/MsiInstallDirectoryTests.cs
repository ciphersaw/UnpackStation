using UnpackStation.Services;
using Xunit;

namespace UnpackStation.Tests;

/// <summary>Pure logic tests: do not depend on a real MSI on the machine; verify inference rules using constructed table data.</summary>
public class MsiInstallDirectoryTests
{
    [Theory]
    [InlineData("DLLS|DLLs", "DLLs")]
    [InlineData(".LIB|Lib", "Lib")]
    [InlineData("nodejs", "nodejs")]
    [InlineData(".", "")]
    [InlineData("SourceDir", "")]
    [InlineData("", "")]
    public void CleanDefaultDir_ShortAndLongNamesAndPlaceholders(string input, string expected) =>
        Assert.Equal(expected, MsiInstallDirectoryLogic.CleanDefaultDir(input));

    [Theory]
    [InlineData("[WindowsVolume]Python27", "Python27")]
    [InlineData("[ProgramFiles64Folder]Go", "Go")]
    [InlineData("[INSTALLDIR]", "")]
    [InlineData("[TARGETDIR]", "")]
    [InlineData("[TARGETDIR]Lib\\compileall.py", "")]
    [InlineData("", "")]
    public void ParseTemplateLeaf_TakesOnlySingleDirectorySegment(string template, string expected) =>
        Assert.Equal(expected, MsiInstallDirectoryLogic.ParseTemplateLeaf(template));

    [Theory]
    [InlineData("Python27", true)]
    [InlineData("Go Programming Language", true)]
    [InlineData("node-v24.14.0-x64", true)]
    [InlineData("", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("C:\\Program Files\\Go", false)]
    [InlineData("[Unresolved]", false)]
    [InlineData("%TEMP%", false)]
    [InlineData("CON", false)]
    public void IsSafeLeafName_RejectsAbsolutePathsAndVariables(string name, bool expected) =>
        Assert.Equal(expected, MsiInstallDirectoryLogic.IsSafeLeafName(name));

    [Fact]
    public void Resolve_WixUiStyle_ReadsGoAndPackageHasThisLayer()
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["WIXUI_INSTALLDIR"] = "INSTALLDIR",
            ["ProductName"] = "Go Programming Language amd64 go1.26.4",
        };
        var directories = new List<MsiDirectoryRow>
        {
            new("TARGETDIR", "", "SourceDir"),
            new("ProgramFiles64Folder", "TARGETDIR", "."),
            new("INSTALLDIR", "ProgramFiles64Folder", "Go"),
            new("dirBin", "INSTALLDIR", "bin"),
        };
        var actions = new List<MsiCustomActionRow>
        {
            // This kind of action sets ARPINSTALLLOCATION, not a directory property, so it cannot be used as the install path
            new("SetApplicationRootDirectory", 51, "ARPINSTALLLOCATION", "[INSTALLDIR]"),
        };

        var info = MsiInstallDirectoryLogic.Resolve(properties, directories, actions);

        Assert.NotNull(info);
        Assert.Equal("Go", info.LeafName);
        Assert.Equal("[ProgramFiles64Folder]Go", info.Template);
        Assert.True(info.PackageHasThisLayer, "The Go directory name appears in the Directory table, so this layer exists naturally after extraction");
    }

    [Fact]
    public void Resolve_LegacyMsi_ReadsPython27FromCustomActionAndPackageLacksThisLayer()
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ProductName"] = "Python 2.7.18 (64-bit)",
            ["ProductLine"] = "Python27",
        };
        var directories = new List<MsiDirectoryRow>
        {
            new("TARGETDIR", "", "SourceDir"),
            new("DLLDIR", "TARGETDIR", "."),
            new("DLLs", "TARGETDIR", "DLLS|DLLs"),
            new("Lib", "TARGETDIR", ".LIB|Lib"),
            new("include", "TARGETDIR", "INCLUDE|include"),
        };
        var actions = new List<MsiCustomActionRow>
        {
            new("InitialTargetDir", 307, "TARGETDIR", "[WindowsVolume]Python27"),
            new("SetDLLDirToTarget", 307, "DLLDIR", "[TARGETDIR]"),
            // src is not a directory property, and target is a command line, not a path
            new("SetCompilePycCommandLine", 51, "CompilePyc", "[#python.exe] -Wi \"[TARGETDIR]Lib\""),
        };

        var info = MsiInstallDirectoryLogic.Resolve(properties, directories, actions);

        Assert.NotNull(info);
        Assert.Equal("Python27", info.LeafName);
        Assert.Equal("[WindowsVolume]Python27", info.Template);
        Assert.Contains("CustomAction", info.Origin);
        Assert.False(info.PackageHasThisLayer, "The Python Directory table has no Python27 layer, so the tool must add it");
    }

    [Fact]
    public void Resolve_NodeStyle_ReadsNodejs()
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["WIXUI_INSTALLDIR"] = "INSTALLDIR",
        };
        var directories = new List<MsiDirectoryRow>
        {
            new("TARGETDIR", "", "SourceDir"),
            new("ProgramFiles64Folder", "TARGETDIR", "PFiles64"),
            new("INSTALLDIR", "ProgramFiles64Folder", "nodejs"),
        };

        var info = MsiInstallDirectoryLogic.Resolve(properties, directories, []);

        Assert.NotNull(info);
        Assert.Equal("nodejs", info.LeafName);
        Assert.True(info.PackageHasThisLayer);
    }

    [Fact]
    public void Resolve_PropertyFallback_ReadsSingleDirectorySegment()
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["INSTALLFOLDER"] = "[LocalAppDataFolder]MyTool",
        };
        var directories = new List<MsiDirectoryRow> { new("TARGETDIR", "", "SourceDir") };

        var info = MsiInstallDirectoryLogic.Resolve(properties, directories, []);

        Assert.NotNull(info);
        Assert.Equal("MyTool", info.LeafName);
        Assert.False(info.PackageHasThisLayer);
    }

    [Fact]
    public void Resolve_OnlyNonDirectoryPropertyCustomAction_ReturnsNull()
    {
        var actions = new List<MsiCustomActionRow>
        {
            new("SetApplicationRootDirectory", 51, "ARPINSTALLLOCATION", "[INSTALLDIR]"),
        };

        Assert.Null(MsiInstallDirectoryLogic.Resolve(
            new Dictionary<string, string>(), new List<MsiDirectoryRow>(), actions));
    }

    [Fact]
    public void Resolve_EmptyTables_ReturnsNull() =>
        Assert.Null(MsiInstallDirectoryLogic.Resolve(
            new Dictionary<string, string>(), new List<MsiDirectoryRow>(), new List<MsiCustomActionRow>()));

    [Fact]
    public void Resolve_DangerousDirectoryName_NotAdopted()
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["WIXUI_INSTALLDIR"] = "INSTALLDIR",
        };
        var directories = new List<MsiDirectoryRow>
        {
            new("INSTALLDIR", "TARGETDIR", "C:\\Windows"),
        };

        Assert.Null(MsiInstallDirectoryLogic.Resolve(properties, directories, []));
    }

    [Fact]
    public void TryRead_NonMsiFile_ReturnsNullWithoutThrowing()
    {
        var path = Path.Combine(Path.GetTempPath(), "UnpackStation_not_msi_" + Guid.NewGuid().ToString("N") + ".msi");
        File.WriteAllText(path, "This is not an MSI");
        try
        {
            Assert.Null(MsiInstallDirectoryReader.TryRead(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
