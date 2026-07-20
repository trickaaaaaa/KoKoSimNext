using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// PlayTimeline（エンジン実出力）→ PlaybackPlay（2D俯瞰ビュー再生モデル）アダプタのゴールデン/契約テスト。
///
/// 方針（設計者Claude指示）: 実エンジン出力の代表プレー（ヒット/フライ/ゴロ/盗塁/エラーを最低1つずつ）を
/// **固定シード**で採取し、変換後の PlaybackPlay が**再生可能**であることを検証する:
///   ・全ボールセグメントの T0 &lt; T1（時刻が前進する）
///   ・全座標が球場内（有限・妥当スケール：|x|,|y| ≤ 200m）
///   ・走者ラベルと野手キーが揃う（各野手キーに初期位置があり、各走者にラベルとレッグがある）
///   ・時刻 t を [0,Dur] で走らせて BallAt/FielderAt(9人)/RunnerAt が例外なく有限値を返す
/// </summary>
public sealed class PlayTimelineAdapterTests
{
    private const double FieldBoundM = 200.0; // 球場スケールの妥当上限（HR の飛距離も許容しつつ NaN/暴走を弾く）

    // ── チーム生成（TimelineTests と同じ最小構成） ──
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
            Bullpen = new[] { Pos(FieldPosition.Pitcher) with { Name = name + "R", Pitching = PitcherAttributes.LeagueAverage } },
        };
    }

    private enum Kind { Hit, FlyOut, GroundOut, Error }

    // 固定シードで試合を回し、代表プレーの PlayTimeline を1つずつ採取する。
    private static Dictionary<Kind, PlayTimeline> CollectRepresentativePlays()
    {
        var found = new Dictionary<Kind, PlayTimeline>();
        for (var seed = 1UL; seed <= 400 && found.Count < 4; seed++)
        {
            var r = GameEngine.Play(BuildTeam("A"), BuildTeam("H"),
                new GameContext { CaptureTimelines = true }, new Xoshiro256Random(seed));

            foreach (var e in r.Log)
            {
                if (e.Timeline is null) continue;
                var tl = e.Timeline;
                var kind = Classify(e.Result, tl);
                if (kind.HasValue && !found.ContainsKey(kind.Value)) found[kind.Value] = tl;
            }
        }
        return found;
    }

    private static Kind? Classify(PlateAppearanceResult result, PlayTimeline tl)
    {
        if (result.IsHit()) return Kind.Hit;
        if (result == PlateAppearanceResult.ReachedOnError) return Kind.Error;
        if (result == PlateAppearanceResult.InPlayOut)
        {
            if (tl.Ball.Any(b => b.Kind == BallSegmentKind.Flight)) return Kind.FlyOut;
            if (tl.Ball.Any(b => b.Kind == BallSegmentKind.Roll)) return Kind.GroundOut;
        }
        return null;
    }

    // 盗塁タイムライン（BuildSteal＝エンジンの実ビルダ）。成功/盗塁死の両方。
    private static PlayTimeline StealTimeline(StealResult result)
    {
        var runner = Pos(FieldPosition.SecondBase) with { Speed = 70 };
        var catcher = Pos(FieldPosition.Catcher) with { ArmStrength = 60 };
        return TimelineBuilder.BuildSteal(runner, catcher, result, new BaserunningCoefficients(), new FieldGeometry());
    }

    [Fact]
    public void RepresentativePlays_AllKindsFound()
    {
        var plays = CollectRepresentativePlays();
        Assert.True(plays.ContainsKey(Kind.Hit), "ヒットのタイムラインが採取できなかった");
        Assert.True(plays.ContainsKey(Kind.FlyOut), "フライアウトのタイムラインが採取できなかった");
        Assert.True(plays.ContainsKey(Kind.GroundOut), "ゴロアウトのタイムラインが採取できなかった");
        Assert.True(plays.ContainsKey(Kind.Error), "エラーのタイムラインが採取できなかった");
    }

    [Fact]
    public void Adapter_ProducesReplayablePlaybackPlays()
    {
        var sources = new List<PlayTimeline>();
        sources.AddRange(CollectRepresentativePlays().Values);
        sources.Add(StealTimeline(StealResult.Safe));
        sources.Add(StealTimeline(StealResult.CaughtStealing));

        Assert.True(sources.Count >= 6, "代表プレー（打球4種＋盗塁2種）が揃っていない");

        foreach (var tl in sources)
        {
            var play = PlayTimelineAdapter.ToPlaybackPlay(tl);
            AssertReplayable(play);
        }
    }

    [Fact]
    public void Adapter_MapsCoordinatesAndOutRunnerHides()
    {
        // 盗塁死: 走者は二塁レッグの終端(アウト)で hideAt により消える。座標も (X,Z)→(X,Y) で保たれる。
        var tl = StealTimeline(StealResult.CaughtStealing);
        var play = PlayTimelineAdapter.ToPlaybackPlay(tl);

        var runner = Assert.Single(play.Runners);
        Assert.False(string.IsNullOrEmpty(runner.Label));
        Assert.NotNull(runner.HideAt);                       // 盗塁死＝アウトで消える
        var lastLeg = runner.Segs[runner.Segs.Count - 1];
        Assert.Equal(runner.HideAt!.Value, lastLeg.T1, 6);   // 消えるのはアウトの瞬間

        // アウト直後は非表示、直前は表示。
        Assert.NotNull(PlaybackEvaluator.RunnerAt(runner, runner.HideAt.Value - 0.01));
        Assert.Null(PlaybackEvaluator.RunnerAt(runner, runner.HideAt.Value + 0.01));

        // 座標直写し: エンジンの二塁点(To.X,To.Z) が PlaybackVec(X,Y) に一致。
        var srcLeg = tl.Runners[0];
        Assert.Equal(srcLeg.To.X, lastLeg.To.X, 6);
        Assert.Equal(srcLeg.To.Z, lastLeg.To.Y, 6);
    }

    // ── 再生可能性の不変条件 ──
    private static void AssertReplayable(PlaybackPlay play)
    {
        Assert.True(play.Dur > 0, "Dur は正");
        Assert.InRange(play.ResAt, 0.0, play.Dur + 1e-6);

        // ボール: 各セグメント T0 < T1・T0 ≥ 0、端点座標が球場内。
        Assert.NotEmpty(play.Ball);
        foreach (var seg in play.Ball)
        {
            Assert.True(seg.T0 >= 0, "T0 ≥ 0");
            Assert.True(seg.T0 < seg.T1, $"T0<T1（T0={seg.T0} T1={seg.T1}）");
            AssertBallInField(seg.At(0));
            AssertBallInField(seg.At(seg.T1 - seg.T0));
        }

        // 野手キー: すべて初期守備位置を持つ。移動先も球場内。
        foreach (var kv in play.Moves)
        {
            Assert.True(PlaybackEvaluator.DefaultPositions.ContainsKey(kv.Key), $"野手キー {kv.Key} の初期位置が無い");
            foreach (var m in kv.Value)
            {
                Assert.True(m.T0 < m.T1, "移動の T0<T1");
                AssertVecInField(m.To);
            }
        }

        // 走者: ラベルとレッグがあり、座標が球場内。
        foreach (var runner in play.Runners)
        {
            Assert.False(string.IsNullOrEmpty(runner.Label), "走者ラベルが空でない");
            Assert.NotEmpty(runner.Segs);
            foreach (var s in runner.Segs)
            {
                Assert.True(s.T0 < s.T1, "レッグの T0<T1");
                AssertVecInField(s.From);
                AssertVecInField(s.To);
            }
        }

        // 時刻 t を [0,Dur] で走らせ、全アクターが例外なく有限値を返す。
        for (var t = 0.0; t <= play.Dur; t += 0.1)
        {
            var b = PlaybackEvaluator.BallAt(play, t);
            if (b.HasValue) AssertBallInField(b.Value);

            foreach (var pos in PlaybackEvaluator.DefaultPositions.Keys)
            {
                var (p, _) = PlaybackEvaluator.FielderAt(play, pos, t);
                AssertVecInField(p);
            }

            foreach (var runner in play.Runners)
            {
                var rp = PlaybackEvaluator.RunnerAt(runner, t);
                if (rp.HasValue) AssertVecInField(rp.Value);
            }
        }
    }

    private static void AssertBallInField(PlaybackBall b)
    {
        Assert.True(IsFinite(b.X) && IsFinite(b.Y) && IsFinite(b.H), "ボール座標が有限");
        Assert.InRange(b.X, -FieldBoundM, FieldBoundM);
        Assert.InRange(b.Y, -FieldBoundM, FieldBoundM);
        Assert.InRange(b.H, -1.0, 80.0); // 高さは負にならず、飛球でも常識的範囲
    }

    private static void AssertVecInField(PlaybackVec v)
    {
        Assert.True(IsFinite(v.X) && IsFinite(v.Y), "座標が有限");
        Assert.InRange(v.X, -FieldBoundM, FieldBoundM);
        Assert.InRange(v.Y, -FieldBoundM, FieldBoundM);
    }

    private static bool IsFinite(double d) => !double.IsNaN(d) && !double.IsInfinity(d);
}
