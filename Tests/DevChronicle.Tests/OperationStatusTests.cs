using DevChronicle.ViewModels;
using Xunit;

namespace DevChronicle.Tests;

public class OperationStatusTests
{
    [Fact]
    public void FormatProgress_WithTotal_IncludesCounter()
    {
        var result = OperationStatusFormatter.FormatProgress("Processing", 2, 5);
        Assert.Equal("Processing (2/5)", result);
    }

    [Fact]
    public void FormatProgress_WithoutTotal_UsesEllipsis()
    {
        var result = OperationStatusFormatter.FormatProgress("Processing", 0, 0);
        Assert.Equal("Processing...", result);
    }
}
