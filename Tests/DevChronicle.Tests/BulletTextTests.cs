using DevChronicle.Services;
using Xunit;

namespace DevChronicle.Tests;

public class BulletTextTests
{
    [Fact]
    public void NormalizeToDashBullets_ConvertsDotBullet()
    {
        var bullets = BulletText.NormalizeToDashBullets("• hello");
        Assert.Equal(new[] { "- hello" }, bullets);
    }

    [Fact]
    public void NormalizeToDashBullets_ConvertsPlainLine()
    {
        var bullets = BulletText.NormalizeToDashBullets("hello");
        Assert.Equal(new[] { "- hello" }, bullets);
    }

    [Fact]
    public void NormalizeToDashBullets_KeepsDashBullet()
    {
        var bullets = BulletText.NormalizeToDashBullets("- hello");
        Assert.Equal(new[] { "- hello" }, bullets);
    }

    [Fact]
    public void NormalizeToDashBullets_DropsEmptyLines()
    {
        var bullets = BulletText.NormalizeToDashBullets("\n\n- a\n\n \n• b\n");
        Assert.Equal(new[] { "- a", "- b" }, bullets);
    }
}

