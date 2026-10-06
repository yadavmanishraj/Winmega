using Omega.Core.Upstream;
using Xunit;

namespace Omega.Core.Tests;

public class LyricsHelperTests
{
    [Fact]
    public void Clean_ConvertsBrToNewlines()
    {
        Assert.Equal(
            "Mujhko iraade de\nKasamein de, waade de",
            LyricsHelper.Clean("Mujhko iraade de<br>Kasamein de, waade de"));
    }

    [Fact]
    public void Clean_ToleratesBrVariants_AndDecodesEntities()
    {
        Assert.Equal(
            "one\ntwo\nthree & four",
            LyricsHelper.Clean("one<br/>two<br />three &amp; four"));
    }

    [Fact]
    public void Clean_Blank_ReturnsNull()
    {
        Assert.Null(LyricsHelper.Clean(null));
        Assert.Null(LyricsHelper.Clean(""));
        Assert.Null(LyricsHelper.Clean("   "));
    }
}
