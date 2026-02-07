using System.Reflection;
using DevChronicle.Services;
using Xunit;

namespace DevChronicle.Tests;

public class SummarizationContinuationTests
{
    [Fact]
    public void ExtractLastBulletLine_ReturnsLastBulletEvenIfTruncated()
    {
        var method = typeof(SummarizationService).GetMethod(
            "ExtractLastBulletLine",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var raw = string.Join("\n", new[]
        {
            "- First bullet is complete",
            "- Second bullet is also complete",
            "- Third bullet starts and then gets cut off mid"
        });

        var last = (string?)method!.Invoke(null, new object[] { raw });
        Assert.Equal("- Third bullet starts and then gets cut off mid", last);
    }
}

