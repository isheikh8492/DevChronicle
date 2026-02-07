using DevChronicle.Services;
using Xunit;

namespace DevChronicle.Tests;

public class GitAuthorFilteringTests
{
    [Fact]
    public void EscapeLiteral_EscapesRegexMetacharacters()
    {
        var input = @"john.doe+work@company.com";
        var escaped = GitAuthorRegex.EscapeLiteral(input);

        Assert.Equal(@"john\.doe\+work@company\.com", escaped);
    }
}
