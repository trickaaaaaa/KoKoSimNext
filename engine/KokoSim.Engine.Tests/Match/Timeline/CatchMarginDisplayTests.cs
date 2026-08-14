using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Match.Fielding;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 捕球余裕・お手玉を絵まで通す（issue wire-unused-defender-motions ①）。
///
/// <para>engine は「その飛球にどれだけ余裕があったか」（<see cref="FieldingPlay.CatchMarginSeconds"/>）と
/// 「弾いたか」（<c>ErrorKind == Catch</c>）を**以前から知っていた**が、前者は判定オーバーレイ「フェア」の
/// 出し分けに使うだけ、後者はキャプションだけで、どちらも <see cref="PlayTimeline"/> に載っていなかった。
/// そのため3Dの守備は「ぎりぎり届いた」と「余裕で前に落ちた」を同じ絵で描いていた
/// （<c>FielderStarts</c> と同じ「計算済みだが絵へ渡っていない」構図）。</para>
///
/// <para>⚠ <b>表示専用</b>＝既に決まった値の写しで、新規計算も乱数消費もゼロ。</para>
/// </summary>
public sealed class CatchMarginDisplayTests
{
    private static readonly FieldGeometry Field = new();

    // ---- 写しであること（新規計算をしていない） ----

    [Theory]
    [InlineData(0.05)]
    [InlineData(-0.30)]
    public void CatchMargin_IsCopiedIntoTheTimeline_AndSurvivesTheAdapter(double margin)
    {
        var play = FieldingPlayFixtures.CaughtFly(
                FieldPosition.CenterField, landingZ: 90, hangTimeSeconds: 3.0, apexHeightM: 20)
            with { CatchMarginSeconds = margin };

        var tl = TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: false,
            TimelineBuilder.DescribeResult(play));

        Assert.Equal(margin, tl.CatchMarginSeconds);
        Assert.Equal(margin, PlayTimelineAdapter.ToPlaybackPlay(tl).CatchMargin);
    }

    [Fact]
    public void FumbledCatch_IsTrueForCatchErrors_AndFalseForWildThrows()
    {
        var grounder = FieldingPlayFixtures.InfieldGrounder(
            FieldPosition.Shortstop, result: BattedBallResult.Error, throwArriveSeconds: 4.0);

        var fumble = TimelineBuilder.BuildBattedBall(
            grounder with { ErrorKind = FieldingErrorKind.Catch }, Field.StandardAlignment(), Field, null,
            runnersOn: false, "E6");
        var wild = TimelineBuilder.BuildBattedBall(
            grounder with { ErrorKind = FieldingErrorKind.Throw }, Field.StandardAlignment(), Field, null,
            runnersOn: false, "E6");

        Assert.True(fumble.FumbledCatch);
        Assert.True(PlayTimelineAdapter.ToPlaybackPlay(fumble).Fumbled);
        Assert.False(wild.FumbledCatch);   // 悪送球は弾いていない＝お手玉の収録を流さない
        Assert.False(PlayTimelineAdapter.ToPlaybackPlay(wild).Fumbled);
    }

    [Fact]
    public void GroundersHaveNoCatchMargin()
    {
        var grounder = FieldingPlayFixtures.InfieldGrounder(
            FieldPosition.Shortstop, result: BattedBallResult.Out, throwArriveSeconds: 4.0);
        var tl = TimelineBuilder.BuildBattedBall(
            grounder, Field.StandardAlignment(), Field, null, runnersOn: false, "ゴロ");
        Assert.Null(tl.CatchMarginSeconds);   // 空中で捕っていない＝飛び込む余地もない
    }

    // ---- 母集団: しきい値 0.20 秒の妥当性 ----

    /// <summary>
    /// しきい値 0.20 秒（監督決定 2026-08-13）を母集団の数字で裏づける。
    ///
    /// <para>実測（40試合・空中捕球 1341 件＝捕った 851／落とした 490）。しきい値と発生率:</para>
    /// <code>
    ///   0.10s → 捕球  4.6% ／ 落球  8.2%
    ///   0.15s → 捕球  6.8% ／ 落球 12.4%
    ///   0.20s → 捕球  9.6% ／ 落球 16.7%   ← 採用（監督決定 2026-08-13）
    ///   0.30s → 捕球 14.9% ／ 落球 23.1%
    ///   0.50s → 捕球 26.1% ／ 落球 38.8%
    /// </code>
    /// <para>0.20 秒なら空中捕球の<b>約1割</b>＝10打席に1回強で飛び込む。0.50 秒まで開けると
    /// 4本に1本が飛び込みになって安っぽく、0.10 秒まで絞ると 4.6%＝死に演出になる。
    /// 落球側（余裕が負）の 16.7% が「飛び込んだが届かない」の出番。</para>
    /// </summary>
    [Fact]
    public void DivingThreshold_FiresOnAboutOneInTenAerialCatches()
    {
        const double dive = 0.20;
        int caught = 0, caughtTight = 0, dropped = 0, droppedTight = 0;

        foreach (var e in Plays())
        {
            if (e.Timeline?.CatchMarginSeconds is not { } m) continue;
            if (m >= 0) { caught++; if (m < dive) caughtTight++; }
            else { dropped++; if (m > -dive) droppedTight++; }
        }

        Assert.True(caught >= 150, $"空中捕球が {caught} 件しか採れていない＝テストが空回り");
        var rate = (double)caughtTight / caught;
        Assert.InRange(rate, 0.03, 0.30);   // 出過ぎ（安っぽい）でも出なさ過ぎ（死に演出）でもない
        Assert.True(dropped > 0 && droppedTight > 0,
            $"「飛び込んだが届かない」の母数が無い（落球 {dropped} 件／うち際どい {droppedTight} 件）");
    }

    /// <summary>実試合でも弾いた打球にだけお手玉の印が立つ（＝収録を流す条件が母集団で機能する）。</summary>
    [Fact]
    public void RealGames_FumbleFlag_OnlyOnErrors()
    {
        int fumbles = 0, otherErrors = 0;
        foreach (var e in Plays())
        {
            if (e.Timeline is null) continue;
            if (e.Timeline.FumbledCatch)
            {
                Assert.Equal(PlateAppearanceResult.ReachedOnError, e.Result);
                fumbles++;
            }
            else if (e.Result == PlateAppearanceResult.ReachedOnError) otherErrors++;
        }
        Assert.True(fumbles >= 5, $"お手玉が {fumbles} 件しか出ていない＝テストが空回り");
        Assert.True(otherErrors >= 1, $"悪送球が {otherErrors} 件＝区別の検証になっていない");
    }

    // ---- 補助 ----

    private static IEnumerable<PlayLogEntry> Plays()
    {
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var r = GameEngine.Play(BuildTeam("A"), BuildTeam("H"),
                new GameContext { CaptureTimelines = true }, new Xoshiro256Random(seed));
            foreach (var e in r.Log) yield return e;
        }
    }

    private static Player Pos(FieldPosition pos) => new()
    {
        Position = pos,
        Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = 50, ArmStrength = 50, Fielding = 50, Catching = 50,
    };

    private static Team BuildTeam(string name)
    {
        var order = new List<Player>
        {
            Pos(FieldPosition.Catcher), Pos(FieldPosition.FirstBase), Pos(FieldPosition.SecondBase),
            Pos(FieldPosition.ThirdBase), Pos(FieldPosition.Shortstop), Pos(FieldPosition.LeftField),
            Pos(FieldPosition.CenterField), Pos(FieldPosition.RightField),
            Pos(FieldPosition.Pitcher) with { Name = name + "P", Pitching = PitcherAttributes.LeagueAverage },
        };
        return new Team
        {
            Name = name, BattingOrder = order, PitcherSlot = 8,
            Reserves = new[]
            {
                Pos(FieldPosition.Pitcher) with { Name = name + "R", Pitching = PitcherAttributes.LeagueAverage },
            },
        };
    }
}
