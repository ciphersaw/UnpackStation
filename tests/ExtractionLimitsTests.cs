using UnpackStation.Services;
using Xunit;

namespace UnpackStation.Tests;

/// <summary>P4 pure logic tests for decompression bomb protection, independent of real archives.</summary>
public class ExtractionLimitsTests
{
    [Fact]
    public void Check_WithinThreshold_Allowed()
    {
        Assert.Null(ExtractionLimits.Check(1, 0));
        Assert.Null(ExtractionLimits.Check(ExtractionLimits.MaxFileCount, 0));
        Assert.Null(ExtractionLimits.Check(0, ExtractionLimits.MaxTotalBytes));
    }

    [Fact]
    public void Check_FileCountExceeded_GivesAbortReason()
    {
        var reason = ExtractionLimits.Check(ExtractionLimits.MaxFileCount + 1, 0);

        Assert.NotNull(reason);
        Assert.Contains("file count", reason);
        Assert.Contains("aborted", reason);
    }

    [Fact]
    public void Check_TotalBytesExceeded_GivesAbortReason()
    {
        var reason = ExtractionLimits.Check(1, ExtractionLimits.MaxTotalBytes + 1);

        Assert.NotNull(reason);
        Assert.Contains("20 GB", reason);
        Assert.Contains("aborted", reason);
    }

    [Fact]
    public void Check_CustomThresholdsAvailable_ForSmallSampleVerification()
    {
        Assert.Null(ExtractionLimits.Check(2, 10, 2, 10));
        Assert.NotNull(ExtractionLimits.Check(3, 10, 2, 10));
        Assert.NotNull(ExtractionLimits.Check(2, 11, 2, 10));
    }

    [Fact]
    public void Budget_AccumulatedBytes_RejectsFurtherWritesAfterLimit()
    {
        var budget = new ExtractionBudget(maxBytes: 10);

        Assert.True(budget.AddBytes(6));
        Assert.True(budget.AddBytes(4));
        Assert.Equal(10UL, budget.Bytes);
        Assert.False(budget.AddBytes(1));
        Assert.True(budget.Exceeded);
        Assert.NotNull(budget.Reason);
        Assert.False(budget.AddBytes(0), "Even 0 additional bytes must not be allowed after the limit is exceeded");
    }

    [Fact]
    public void Budget_HugeSingleWrite_NoOverflowAndBlocked()
    {
        var budget = new ExtractionBudget();

        Assert.False(budget.AddBytes(ulong.MaxValue));
        Assert.True(budget.Exceeded);
    }

    [Fact]
    public void Budget_ActualFileCountExceeded_Blocked()
    {
        var budget = new ExtractionBudget(maxBytes: 1024, maxFiles: 1);

        Assert.True(budget.AddFile());
        Assert.False(budget.AddFile());
        Assert.True(budget.Exceeded);
        Assert.Contains("the number of files", budget.Reason);
    }
}
