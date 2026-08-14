using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Fielding;

/// <summary>
/// フェンス直撃の跳ね返り（issue fence-carom-and-over-the-head-cues）。
///
/// <para>エンジンは「転がりがフェンスに届いたか」を<b>以前から知っていた</b>（<c>RollPath.ReachedFence</c>）が、
/// record の宣言以外に参照が1つも無く、絵では球がフェンス際でそのまま静止して 0.40 秒の「無」があるだけだった。
/// 実測（400試合）: <b>二塁打の 92.9% ／ 三塁打の 100%</b> が該当＝長打のほぼ全部で出番がある。</para>
///
/// <para>⚠ <b>表示専用</b>。判定（回収野手・回収時刻・塁打数・送球到達）は1ビットも変えない
/// ＝跳ね返りは <see cref="ExtraBaseResolver.RollPath.PositionAt"/> を触らない別の純関数で解く。</para>
/// </summary>
public sealed class FenceCaromTests
{
    private static readonly FieldingCoefficients C = new();

    // ---- 跳ね返りの物理 ----

    [Fact]
    public void Carom_IsNull_WhenTheBallStopsShortOfTheFence()
    {
        var roll = ExtraBaseResolver.RollFrom(
            new Vector3D(0, 0, 60), new Vector3D(0, 0, 1), speedMps: 8.0, startSeconds: 2.0,
            fenceDistanceM: 118.0, C);
        Assert.False(roll.ReachedFence);
        Assert.Null(ExtraBaseResolver.ComputeFenceCarom(roll, C));
    }

    [Fact]
    public void Carom_ComesBackTowardTheInfield_WithPhysicalSpeedAndDistance()
    {
        var roll = ExtraBaseResolver.RollFrom(
            new Vector3D(0, 0, 80), new Vector3D(0, 0, 1), speedMps: 25.0, startSeconds: 3.0,
            fenceDistanceM: 118.0, C);
        Assert.True(roll.ReachedFence);

        var carom = ExtraBaseResolver.ComputeFenceCarom(roll, C);
        Assert.NotNull(carom);
        var k = carom!.Value;

        // 残速＝√(v0² − 2·a·d)、跳ね返り初速＝残速×反発係数、戻る距離＝v²/(2a)。
        var remaining = Math.Sqrt(25.0 * 25.0 - 2.0 * C.RollDecelMps2 * roll.RollDistanceM);
        Assert.Equal(remaining * C.FenceRestitution, k.SpeedMps, 6);
        Assert.Equal(k.SpeedMps * k.SpeedMps / (2.0 * C.RollDecelMps2), k.DistanceM, 6);

        // まっすぐ中堅へ転がった球はまっすぐ戻る（法線＝本塁向き）。
        Assert.Equal(-1.0, k.Direction.Z, 6);
        Assert.True(k.StopPosition.Z < k.HitPoint.Z, "内野側へ戻っていない");

        // 当たる時刻は往路の転がりが終わる瞬間（＝FenceCaromSeconds を足す前）。
        Assert.Equal(roll.SettleAtSeconds - C.FenceCaromSeconds, k.HitAtSeconds, 6);
    }

    /// <summary>斜めに当たった球はフェンス弧の法線で反射する（入射角＝反射角）。</summary>
    [Fact]
    public void Carom_ReflectsOffTheFenceArc()
    {
        var dir = new Vector3D(Math.Sin(0.5), 0, Math.Cos(0.5));   // 左中間へ斜めに
        var roll = ExtraBaseResolver.RollFrom(
            new Vector3D(0, 0, 40), dir, speedMps: 30.0, startSeconds: 2.5, fenceDistanceM: 110.0, C);
        var k = ExtraBaseResolver.ComputeFenceCarom(roll, C)!.Value;

        // 反射後は必ず内向き（本塁方向の成分が正）。
        var r = Math.Sqrt(k.HitPoint.X * k.HitPoint.X + k.HitPoint.Z * k.HitPoint.Z);
        var inwardX = -k.HitPoint.X / r;
        var inwardZ = -k.HitPoint.Z / r;
        Assert.True(k.Direction.X * inwardX + k.Direction.Z * inwardZ > 0.0, "跳ね返りが外を向いている");
        // 単位ベクトルであること。
        Assert.Equal(1.0, Math.Sqrt(k.Direction.X * k.Direction.X + k.Direction.Z * k.Direction.Z), 6);
    }

    [Fact]
    public void Carom_ScalesWithRestitution()
    {
        var roll = ExtraBaseResolver.RollFrom(
            new Vector3D(0, 0, 80), new Vector3D(0, 0, 1), 25.0, 3.0, 118.0, C);
        var soft = ExtraBaseResolver.ComputeFenceCarom(roll, C with { FenceRestitution = 0.30 })!.Value;
        var hard = ExtraBaseResolver.ComputeFenceCarom(roll, C with { FenceRestitution = 0.50 })!.Value;

        Assert.True(hard.DistanceM > soft.DistanceM);
        // e=0 なら跳ね返らない（＝従来どおりフェンス際で静止）。
        Assert.Null(ExtraBaseResolver.ComputeFenceCarom(roll, C with { FenceRestitution = 0.0 }));
    }

    // ---- 実試合: 絵に出ていること・判定が動いていないこと ----

    /// <summary>長打のほぼ全部に跳ね返りが付き、単打にはほぼ付かない（issue の実測と同じ形）。</summary>
    [Fact]
    public void RealGames_CaromAppearsOnExtraBaseHits_NotOnSingles()
    {
        var doubles = (Fence: 0, Total: 0);
        var singles = (Fence: 0, Total: 0);

        foreach (var e in Plays())
        {
            if (e.Timeline is null) continue;
            var reached = e.Timeline.Captions.Any(c => c.Text == "フェンス直撃！");
            switch (e.Result)
            {
                case KokoSim.Engine.Match.AtBat.PlateAppearanceResult.Double:
                case KokoSim.Engine.Match.AtBat.PlateAppearanceResult.Triple:
                    doubles = (doubles.Fence + (reached ? 1 : 0), doubles.Total + 1);
                    break;
                case KokoSim.Engine.Match.AtBat.PlateAppearanceResult.Single:
                    singles = (singles.Fence + (reached ? 1 : 0), singles.Total + 1);
                    break;
            }
        }

        Assert.True(doubles.Total >= 30, $"長打が {doubles.Total} 本しかない＝テストが空回り");
        // ⚠ issue 本文の「二塁打の92.9%」は `ReachedFence`（＝放っておけばフェンスまで転がる）の割合。
        // 実際に**当たる**のは外野手が手前で回収できなかったぶんだけで、実測 93/149＝62%。
        // 残りは球が壁に届く前に回収されている＝跳ね返りを描かないのが正しい。
        Assert.True((double)doubles.Fence / doubles.Total >= 0.55,
            $"長打の {(double)doubles.Fence / doubles.Total:P0} にしか跳ね返りが付いていない");
        Assert.True((double)singles.Fence / singles.Total <= 0.05,
            $"単打の {(double)singles.Fence / singles.Total:P0} に跳ね返りが付いている（フェンス手前で止まるはず）");
        // ⚠ issue 本文の「二塁打の92.9%」は `ReachedFence`（＝放っておけばフェンスまで転がる）の割合。
        // 実際に**当たる**のは外野手が手前で回収できなかったぶんだけで、実測 93/149＝62%。
        // 残りは球が壁に届く前に回収されている＝跳ね返りを描かないのが正しい。
        Assert.True((double)doubles.Fence / doubles.Total >= 0.55,
            $"長打の {(double)doubles.Fence / doubles.Total:P0} にしか跳ね返りが付いていない");
        Assert.True((double)singles.Fence / singles.Total <= 0.05,
            $"単打の {(double)singles.Fence / singles.Total:P0} に跳ね返りが付いている（フェンス手前で止まるはず）");
    }

    /// <summary>
    /// 跳ね返りの絵が「球が野手を通り過ぎる」形になっていない＝回収の瞬間に球と表示上の回収点が一致する。
    /// あわせて球の区間が時間で連続していること（当たる瞬間で区切って繋いでいる）を見る。
    /// </summary>
    [Fact]
    public void CaromSegments_AreContinuous_AndEndWhereTheBallIsRetrieved()
    {
        var checkedPlays = 0;
        foreach (var e in Plays())
        {
            if (e.Timeline is null) continue;
            if (!e.Timeline.Captions.Any(c => c.Text == "フェンス直撃！")) continue;

            var rolls = e.Timeline.Ball.Where(b => b.Kind == BallSegmentKind.Roll).ToList();
            if (rolls.Count < 2) continue;
            checkedPlays++;

            for (var i = 1; i < rolls.Count; i++)
            {
                Assert.Equal(rolls[i - 1].T1, rolls[i].T0, 6);                    // 時間が繋がっている
                Assert.True(Dist(rolls[i - 1].To, rolls[i].From) < 1e-6, "球が飛んでいる（位置が繋がっていない）");
            }

            // 最後の転がりの終端＝球が止まる/拾われる点。フェンスより内野側へ戻っていること。
            var hit = rolls[^1].From;
            var end = rolls[^1].To;
            Assert.True(Range(end) < Range(hit) + 1e-6, "跳ね返りが内野側へ戻っていない");
        }
        Assert.True(checkedPlays >= 20, $"跳ね返りのプレーが {checkedPlays} 件しか採れていない");
    }

    // ---- 補助 ----

    private static double Dist(TimelinePoint a, TimelinePoint b)
        => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z));

    private static double Range(TimelinePoint p) => Math.Sqrt(p.X * p.X + p.Z * p.Z);

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
