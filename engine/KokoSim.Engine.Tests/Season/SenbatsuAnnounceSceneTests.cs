using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>センバツ選考発表の場面の振り分け（issue #1732）。</summary>
public sealed class SenbatsuAnnounceSceneTests
{
    [Theory]
    [InlineData(true, true, SenbatsuAnnounceKind.Selected)]
    [InlineData(true, false, SenbatsuAnnounceKind.Selected)]
    [InlineData(false, true, SenbatsuAnnounceKind.NotSelected)]
    [InlineData(false, false, SenbatsuAnnounceKind.NotEntered)]
    public void KindOf_FollowsSelectedAndRegionalEntry(bool selected, bool entered, SenbatsuAnnounceKind expected)
        => Assert.Equal(expected, SenbatsuAnnounceScene.KindOf(selected, entered));

    [Fact]
    public void NotEnteredYear_ShowsTheShortScene_NotTheNotSelectedOne()
    {
        var id = SenbatsuAnnounceScene.EventIdOf(SenbatsuAnnounceScene.KindOf(selected: false, enteredRegional: false));
        Assert.Equal(SenbatsuAnnounceScene.NotEnteredEventId, id);
        Assert.NotEqual(SenbatsuAnnounceScene.NotSelectedEventId, id);
    }

    [Fact]
    public void EnteredYears_KeepTheirScenes()
    {
        Assert.Equal(SenbatsuAnnounceScene.NotSelectedEventId,
            SenbatsuAnnounceScene.EventIdOf(SenbatsuAnnounceScene.KindOf(false, true)));
        // 選ばれた年は激励金・抽選の別の場面を積むので、単発の場面IDは無い。
        Assert.Null(SenbatsuAnnounceScene.EventIdOf(SenbatsuAnnounceKind.Selected));
    }
}
