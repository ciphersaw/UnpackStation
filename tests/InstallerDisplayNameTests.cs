using UnpackStation.Services;
using Xunit;

namespace UnpackStation.Tests;

public class InstallerDisplayNameTests
{
    [Theory]
    [InlineData("MyApp", "MyApp")]
    [InlineData("MyApp Setup", "MyApp")]
    [InlineData("  My App Installer  ", "My App")]
    [InlineData("Foo setup program", "Foo")]
    [InlineData("Web Browser", "Web Browser")]
    [InlineData("Setup", null)]
    [InlineData("Installer", null)]
    [InlineData("  ", null)]
    [InlineData("", null)]
    [InlineData("C:\\Program Files\\MyApp", null)]
    [InlineData("[UNRESOLVED]", null)]
    [InlineData("ThisNameIsWayTooLongToBeUsedAsADirectoryName_12345678901234567890", null)]
    public void Normalize_ReturnsSafeApplicationNameOrNull(string? raw, string? expected) =>
        Assert.Equal(expected, InstallerDisplayName.Normalize(raw));
}
