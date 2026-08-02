using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Tests.Match.Fielding;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 打球の実況（接触直後のキャプション）が <see cref="BattedBallClass"/> の4分類を反映すること
/// （Issue #401③）。従来は <c>IsFly</c> の二値だけを見ており、「ライナー」という言葉が一度も出ず、
/// バウンド（チョッパー）もゴロと同じ文言になっていた。文言は engine の分類を写すだけで、
/// 新しい判定は作らない（判定・帯には無関係の表示専用）。
/// </summary>
public sealed class BattedBallCaptionTests
{
    private static string ContactCaption(FieldingPlay play)
    {
        var field = new FieldGeometry();
        var tl = TimelineBuilder.BuildBattedBall(
            play, field.StandardAlignment(), field, null, runnersOn: false, TimelineBuilder.DescribeResult(play));
        // captions[0] は投球開始の定型文。接触直後のキャプションは2番目。
        return tl.Captions[1].Text;
    }

    [Fact]
    public void Liner_GetsDistinctCaption_MentioningLiner()
    {
        var play = FieldingPlayFixtures.CaughtFly(FieldPosition.Shortstop) with { Class = BattedBallClass.Liner };
        Assert.Equal("痛烈なライナー！", ContactCaption(play));
    }

    [Fact]
    public void Fly_GetsCaption_DistinctFromLiner()
    {
        var play = FieldingPlayFixtures.CaughtFly(FieldPosition.CenterField);
        Assert.Equal("高々と上がった！", ContactCaption(play));
    }

    [Fact]
    public void Grounder_KeepsExistingCaption()
    {
        var play = FieldingPlayFixtures.InfieldGrounder(FieldPosition.Shortstop);
        Assert.Equal("打球が転がる！", ContactCaption(play));
    }

    [Fact]
    public void Bouncer_GetsDistinctCaption_FromGrounder()
    {
        var play = FieldingPlayFixtures.InfieldGrounder(
            FieldPosition.Shortstop, ballClass: BattedBallClass.Bouncer);
        Assert.Equal("大きく弾んだ！", ContactCaption(play));
    }

    [Fact]
    public void HomeRun_KeepsExistingCaption_RegardlessOfClass()
    {
        var play = FieldingPlayFixtures.HomeRun();
        Assert.Equal("打った——伸びていく！", ContactCaption(play));
    }

    [Fact]
    public void UnclassifiedPlay_FallsBackToGrounderCaption()
    {
        // ファウル等、分類前に返る経路（Class=null）は既定の Grounder 文言と同じにフォールバックする
        // （Issue #315 の「未判定は地を這う Roll 扱い」と同じ考え方）。
        var play = FieldingPlayFixtures.InfieldGrounder(FieldPosition.Shortstop) with { Class = null };
        Assert.Equal("打球が転がる！", ContactCaption(play));
    }
}
