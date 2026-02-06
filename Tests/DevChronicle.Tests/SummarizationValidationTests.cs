using DevChronicle.Services;
using Xunit;

namespace DevChronicle.Tests;

public class SummarizationValidationTests
{
    [Fact]
    public void ValidateBullets_EnforcesPrefixAndLimit()
    {
        var lines = string.Join('\n', new[]
        {
            "- First bullet",
            "Not a bullet",
            "- Second bullet",
            "- Third bullet",
            "- Fourth bullet"
        });

        var method = typeof(SummarizationService).GetMethod(
            "ValidateBullets",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        var service = new SummarizationService(
            new DatabaseService(),
            new ClusteringService(),
            new SettingsService(new DatabaseService()));

        var result = (List<string>)method!.Invoke(service, new object[] { lines, 2 })!;

        Assert.Equal(2, result.Count);
        Assert.All(result, bullet => Assert.StartsWith("- ", bullet));
        Assert.Equal("- First bullet", result[0]);
        Assert.Equal("- Second bullet", result[1]);
    }
}
