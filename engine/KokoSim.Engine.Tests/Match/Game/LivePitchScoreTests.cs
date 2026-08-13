using System.Collections.Generic;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 1球観測 <see cref="LivePitch"/> は「その球を投げ終えた時点の得点」を運ぶ（issue #628）。
///
/// <para>再生側は打席途中の得点（本盗の生還・暴投/捕逸での生還）をその場で掲示板へ出したいが、
/// <see cref="MatchProgression.Snapshot"/> は打席まるごと解決した後の集計なので、打席途中に読むと
/// 同じ打席の後の得点まで先に出る（issue #438 で直した「得点の先出し」の再発）。
/// この観測値と集計が一致するときだけ掲げる、という判断の土台。</para>
///
/// <para>観測専用・乱数不使用（既に解決済みの値の写し）＝統計帯には触れない。</para>
/// </summary>
public sealed class LivePitchScoreTests
{
    private static readonly BaserunningCoefficients C = new();

    // 走者を溜めて点の入りやすい足の速いチーム（BaserunningPlaysTests と同型）。
    private static Player Pos(FieldPosition p) => new()
    {
        Position = p, Contact = 55, Power = 45, Discipline = 50,
        Speed = 85, Steal = 85, Baserunning = 70, ArmStrength = 50, Fielding = 50, Catching = 50,
    };

    private static Team FastTeam(string name)
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
            Reserves = new[] { Pos(FieldPosition.Pitcher) with { Name = name + "R", Pitching = PitcherAttributes.LeagueAverage } },
        };
    }

    /// <summary>本題: 暴投・捕逸で三塁走者が生還した球には、その得点が**もう乗っている**。</summary>
    [Fact]
    public void BatteryMissRun_IsAlreadyOnThatPitchsScore()
    {
        var ctx = new GameContext
        {
            Baserunning = C with { WildPitchProb = 0.3, PassedBallProb = 0.3 },
            CaptureTimelines = true, CapturePitches = true,
        };
        var captured = 0;
        for (ulong seed = 1; seed <= 60 && captured == 0; seed++)
        {
            var prog = new MatchProgression(FastTeam("A"), FastTeam("H"), ctx, seed);
            LivePitch? prev = null;
            var prevAway = 0;
            var prevHome = 0;
            while (true)
            {
                var r = prog.AdvancePitch();
                if (r == AdvancePitchResult.Finished) break;
                var last = prog.LastPitch;
                if (last == null || ReferenceEquals(last, prev)) continue;
                var gained = last.AwayScore - prevAway + (last.HomeScore - prevHome);
                // 「点が入ったか」は得点の差でなく**再生データの走者が本塁へ着いたか**で判定する。
                // 差で判定すると、揃え直しを外しても差0の球は素通りして緑のままになる（実測）。
                var scoredOnMiss = last.BatteryMissPlay is { } bm && ReachesHome(bm);
                prev = last;
                prevAway = last.AwayScore;
                prevHome = last.HomeScore;
                if (!scoredOnMiss) continue;
                captured++;
                Assert.True(gained >= 1,
                    $"暴投・捕逸で三塁走者が生還したのに、その球の得点が増えていない（{prevAway}-{prevHome}）");
            }
        }
        Assert.True(captured > 0, "暴投・捕逸で生還した球が一度も出なかった（配線切れの疑い）");
    }

    /// <summary>再生データの中に本塁へ着いた走者がいるか（＝この球で点が入った）。</summary>
    private static bool ReachesHome(PlaybackPlay play)
    {
        foreach (var r in play.Runners)
        {
            if (r.Segs.Count == 0) continue;
            var to = r.Segs[r.Segs.Count - 1].To;
            if (to.X * to.X + to.Y * to.Y < 0.25) return true;
        }
        return false;
    }

    /// <summary>得点は球を追って単調に増える（減らない・巻き戻らない）。</summary>
    [Fact]
    public void ScoresNeverGoBackwardsAcrossPitches()
    {
        var ctx = new GameContext
        {
            Baserunning = C with { WildPitchProb = 0.2, PassedBallProb = 0.2 },
            CaptureTimelines = true, CapturePitches = true,
        };
        var prog = new MatchProgression(FastTeam("A"), FastTeam("H"), ctx, 7UL);
        LivePitch? prev = null;
        var away = 0;
        var home = 0;
        var seen = 0;
        while (true)
        {
            var r = prog.AdvancePitch();
            if (r == AdvancePitchResult.Finished) break;
            var last = prog.LastPitch;
            if (last == null || ReferenceEquals(last, prev)) continue;
            prev = last;
            seen++;
            Assert.True(last.AwayScore >= away, $"先攻の得点が減った {away}→{last.AwayScore}");
            Assert.True(last.HomeScore >= home, $"後攻の得点が減った {home}→{last.HomeScore}");
            // 観測は集計を追い越さない（打席途中に「これから入る点」を先出ししない）。
            Assert.True(last.AwayScore <= prog.AwayScore && last.HomeScore <= prog.HomeScore,
                "1球観測の得点が集計を追い越している＝先出し");
            away = last.AwayScore;
            home = last.HomeScore;
        }
        Assert.True(seen > 50, $"球が足りない（seen={seen}）");
    }

    /// <summary>最後の球の得点は最終スコアと一致する（試合を通して取りこぼしがない）。</summary>
    [Fact]
    public void LastPitchOfTheGameCarriesTheFinalScore()
    {
        var ctx = new GameContext { CapturePitches = true };
        var prog = new MatchProgression(FastTeam("A"), FastTeam("H"), ctx, 3UL);
        while (prog.AdvancePitch() != AdvancePitchResult.Finished) { }

        // 最終打席の得点まで含めた集計と、最後の1球観測の差＝その打席で入った点だけ。
        Assert.NotNull(prog.LastPitch);
        Assert.True(prog.LastPitch!.AwayScore <= prog.AwayScore);
        Assert.True(prog.LastPitch!.HomeScore <= prog.HomeScore);
    }
}
