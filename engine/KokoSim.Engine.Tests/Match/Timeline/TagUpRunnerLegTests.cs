using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// タッチアップの走者は捕球を待ってから走る（issue score-display-before-home-touch）。
///
/// <para>判定（<see cref="HomePlayResolver.TagUpRunnerTimeSeconds"/>）は「捕球起点・二次リードなし・
/// 静止スタート」で走者の時計を回すのに、絵は塁上の走者を一律「接触＋反応遅れ」で走らせていた。
/// 結果、犠飛の走者は<b>打った瞬間に三塁を離れ</b>（実測 208/208 件・中央値 4.04 秒早い）、
/// 得点はちょうど捕球のころに入っていた（本塁到達−捕球 の中央値 +0.03 秒／47.6% は捕球以前）。</para>
///
/// <para>得点の加算は各走者の本塁到達レッグ（<see cref="PlaybackDisplaySync.RunScoreTimes"/>）で
/// 駆動している（issue #438）ので、レッグの時刻を判定へ揃えれば表示も自動的に揃う。</para>
/// </summary>
public sealed class TagUpRunnerLegTests
{
    private const int Games = 80;

    // ---- 時計が判定と一致すること（表示側は判定の式を呼ばず、文脈だけを共有する） ----

    /// <summary>
    /// <see cref="HomePlayResolver.TagUpContextOf"/> ＋ <see cref="RunnerClock"/> が、判定の
    /// <see cref="HomePlayResolver.TagUpRunnerTimeSeconds"/> とまったく同じ秒を返す。
    ///
    /// <para>判定側の式は<b>1文字も動かさない</b>（帯・決定論ダイジェストを守るため）。同じ値を返すことは
    /// 式の共有ではなくこのテストで固定する＝式を触ったらここが落ちる。</para>
    /// </summary>
    [Theory]
    [InlineData(20, 20, 2.10)]
    [InlineData(50, 50, 3.70)]
    [InlineData(80, 95, 5.25)]
    [InlineData(99, 1, 0.00)]
    public void TagUpContext_ReproducesTheVerdictClock(int speed, int baserunning, double fieldedAt)
    {
        var c = new BaserunningCoefficients();
        var field = new FieldGeometry();
        var runner = Runner(speed, baserunning);
        var s = new HomePlaySituation(new Vector3D(0, 0, 92.0), fieldedAt, 33.0);

        var verdict = HomePlayResolver.TagUpRunnerTimeSeconds(runner, 3, 4, s, field, c);
        var display = s.BallFieldedAtSeconds
                      + RunnerClock.SegmentSeconds(runner, c.AbilityEffect, 3, 4, HomePlayResolver.TagUpContextOf(runner, c), field);

        Assert.Equal(verdict, display, 9);
    }

    /// <summary>
    /// 通常の塁上走者（打球を見て走る）の文脈は変えていない＝二次リードぶん前に出ており、
    /// 走り出しの速度倍率も乗る。タッチアップの文脈と取り違えると全走者の到達が遅くなるので、
    /// 「2つの文脈が別物であること」も固定しておく。
    /// </summary>
    [Fact]
    public void RunnerContext_AndTagUpContext_AreDifferent()
    {
        var c = new BaserunningCoefficients();
        var runner = Runner(50, 50);
        var normal = HomePlayResolver.RunnerContextOf(runner, c);
        var tagUp = HomePlayResolver.TagUpContextOf(runner, c);

        Assert.Equal(normal.StartDelaySeconds, tagUp.StartDelaySeconds, 12);   // 反応遅れは同じ
        Assert.Equal(0.0, tagUp.LeadDistanceM);                                // 二次リードは取れない
        Assert.True(normal.LeadDistanceM > 0.0);
        Assert.Equal(1.0, tagUp.TopSpeedFactor);                               // 静止スタート
        Assert.True(normal.TopSpeedFactor > 1.0);
    }

    // ---- 母集団（実試合の犠飛） ----

    /// <summary>
    /// 実試合の犠飛で、走者が<b>捕球より前に三塁を離れない</b>／得点の加算が捕球より前に出ない。
    /// 現状（修正前）は 208/208 件で離塁が早く、47.6% は加算が捕球と同時かそれ以前だった。
    /// </summary>
    [Fact]
    public void SacFlyRunners_DoNotLeaveBeforeTheCatch()
    {
        var early = new List<string>();
        var earlyScore = new List<string>();
        var sacFlies = 0;

        foreach (var (entry, play) in SacFlyPlays())
        {
            sacFlies++;
            var catchAt = CatchTime(entry.Timeline!);
            if (catchAt is not { } tc) continue;

            foreach (var leg in MovingLegs(entry.Timeline!))
            {
                if (leg.T0 < tc - 1e-6)
                    early.Add($"{leg.Label} が捕球({tc:F2}s)の {tc - leg.T0:F2}s 前に離塁");
            }

            var scores = PlaybackDisplaySync.RunScoreTimes(play, entry.RunsScored, 0.5);
            foreach (var t in scores)
            {
                if (t < tc - 1e-6) earlyScore.Add($"得点が捕球({tc:F2}s)の {tc - t:F2}s 前に入る");
            }
        }

        Assert.True(sacFlies >= 30, $"犠飛が {sacFlies} 件しか出ておらずテストが空回りしている");
        Assert.True(early.Count == 0,
            $"捕球前に離塁した走者が {early.Count} 人（犠飛 {sacFlies} 件）: {string.Join(" / ", early.Take(5))}");
        Assert.True(earlyScore.Count == 0,
            $"捕球前に入った得点が {earlyScore.Count} 件: {string.Join(" / ", earlyScore.Take(5))}");
    }

    /// <summary>
    /// 犠飛の本塁到達が判定の到達時刻と一致する（±0.05秒）。走者ごとに
    /// 「到達 − 捕球」＝タッチアップの時計（<see cref="RunnerClock"/>）になっているかを見る。
    /// </summary>
    [Fact]
    public void SacFlyArrival_MatchesTheVerdictClock()
    {
        var c = new BaserunningCoefficients();
        var field = new FieldGeometry();
        var worst = 0.0;
        var worstWhere = "";
        var checkedLegs = 0;

        foreach (var (entry, _) in SacFlyPlays())
        {
            var catchAt = CatchTime(entry.Timeline!);
            if (catchAt is not { } tc) continue;

            foreach (var leg in MovingLegs(entry.Timeline!))
            {
                if (leg.Runner is not { } runner) continue;
                if (!IsHome(leg.To)) continue;
                var expected = RunnerClock.SegmentSeconds(
                    runner, c.AbilityEffect, 3, 4, HomePlayResolver.TagUpContextOf(runner, c), field);
                var actual = leg.T1 - tc;
                checkedLegs++;
                if (Math.Abs(actual - expected) > worst)
                {
                    worst = Math.Abs(actual - expected);
                    worstWhere = $"{leg.Label} 到達−捕球={actual:F3}s 期待={expected:F3}s";
                }
            }
        }

        Assert.True(checkedLegs >= 30, $"検査できた本塁到達レッグが {checkedLegs} 本しかない");
        Assert.True(worst <= 0.05, $"判定の到達時刻と {worst:F3}s ずれている: {worstWhere}");
    }

    // ---- 補助 ----

    /// <summary>犠飛（フライ捕球＋生還）のプレーを実試合から拾う。</summary>
    private static IEnumerable<(PlayLogEntry Entry, PlaybackPlay Play)> SacFlyPlays()
    {
        for (ulong seed = 1; seed <= Games; seed++)
        {
            var r = GameEngine.Play(BuildTeam("A"), BuildTeam("H"),
                new GameContext { CaptureTimelines = true }, new Xoshiro256Random(seed));
            foreach (var e in r.Log)
            {
                if (!e.IsSacFly || e.Timeline is null || e.RunsScored <= 0) continue;
                yield return (e, PlayTimelineAdapter.ToPlaybackPlay(e.Timeline));
            }
        }
    }

    /// <summary>捕球時刻[s]＝打球の滞空セグメントの終端（犠飛なので必ず空中で捕っている）。</summary>
    private static double? CatchTime(PlayTimeline t)
    {
        double? last = null;
        foreach (var b in t.Ball)
        {
            if (b.Kind == BallSegmentKind.Flight) last = b.T1;
        }
        return last;
    }

    /// <summary>
    /// 実際に位置が動く<b>塁上の</b>走者レッグ（待機・定点レッグは From==To なので除く）。
    /// 打者走者（"打"）は打った瞬間に走り出すのが正しい＝タッチアップの対象外。
    /// </summary>
    private static IEnumerable<RunnerLeg> MovingLegs(PlayTimeline t)
        => t.Runners.Where(l => l.Label != "打" && Dist(l.From, l.To) > 0.5 && l.T1 > l.T0 + 1e-9);

    private static bool IsHome(TimelinePoint p) => p.X * p.X + p.Z * p.Z < 1.0;

    private static double Dist(TimelinePoint a, TimelinePoint b)
        => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z));

    private static Player Runner(int speed, int baserunning) => new()
    {
        Position = FieldPosition.LeftField,
        Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = speed, Baserunning = baserunning, ArmStrength = 50, Fielding = 50, Catching = 50,
    };

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
