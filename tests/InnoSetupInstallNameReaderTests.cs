using UnpackStation.Services;
using Xunit;

namespace UnpackStation.Tests;

public class InnoSetupInstallNameReaderTests
{
    [Fact]
    public void Parse_ReadsApplicationNameFromInfoOutput()
    {
        const string output = """
            Inspecting "My App" - setup data version 6.7.0 (unicode)

            Languages:
             - chinesesimplified: 简体中文

            No GOG.com game ID found!

            Setup is not passworded!

            Done.
            """;

        Assert.Equal("My App", InnoSetupInstallNameReader.Parse(output));
    }

    [Fact]
    public void Parse_StripsSetupSuffixFromApplicationName() =>
        Assert.Equal("My App", InnoSetupInstallNameReader.Parse(
            "Inspecting \"My App Setup\" - setup data version 6.7.0 (unicode)"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Languages:\n - english: English\n")]
    public void Parse_NoInspectingLine_ReturnsNull(string? output) =>
        Assert.Null(InnoSetupInstallNameReader.Parse(output));
}
