using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 再生（絵）が要求する脚力の上限（2026-08-01「野手の脚力」）。
///
/// <para>タイムラインは判定が使ったのと同じ距離・同じ時刻から組む。どこかで**判定が測った距離と
/// 絵が描く距離が食い違う**と、レッグが人の脚力を超えた速さになる。実際に3つ潜んでいた:
/// ①守備の定位置が2つの表に別々にあった（中堅で10m差） ②走者の二次リード3.5mを描いていなかった
/// ③守備範囲（飛びつき4.2m）ぶんを体が走ったことにしていた。どれも**絵を見ても「速いな」としか
/// 分からず**、原因は切り分けられない。だから数値で固定する。</para>
///
/// <para>上限の根拠: 野手は engine が「距離÷スプリント速度」で尺を決めるので平均＝スプリント速度、
/// ピークは速度プロファイル（台形）の 1/(1−ランプ割合) 倍。走者は塁上からのスタートだけ
/// <c>TopSpeedFactor</c> ぶん速い（既に走っている走者は速い＝物理として正しい）。</para>
/// </summary>
public sealed class LegSpeedTests
{
    private const int Games = 200;

    private static Player Pos(FieldPosition pos, int speed) => new()
    {
        Position = pos,
        Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = speed, ArmStrength = 50, Fielding = 50, Catching = 50,
    };

    private static Team BuildTeam(string name, int speed)
    {
        var order = new List<Player>
        {
            Pos(FieldPosition.Catcher, speed), Pos(FieldPosition.FirstBase, speed),
            Pos(FieldPosition.SecondBase, speed), Pos(FieldPosition.ThirdBase, speed),
            Pos(FieldPosition.Shortstop, speed), Pos(FieldPosition.LeftField, speed),
            Pos(FieldPosition.CenterField, speed), Pos(FieldPosition.RightField, speed),
            Pos(FieldPosition.Pitcher, speed) with { Name = name + "P", Pitching = PitcherAttributes.LeagueAverage },
        };
        return new Team
        {
            Name = name, BattingOrder = order, PitcherSlot = 8,
            Reserves = new[]
            {
                Pos(FieldPosition.Pitcher, speed) with { Name = name + "R", Pitching = PitcherAttributes.LeagueAverage },
            },
        };
    }

    [Fact]
    [Trait("Category", "Heavy")]
    public void NoLegRequiresMoreThanHumanLegs()
    {
        const int speedRating = 50;
        var sprint = Pos(FieldPosition.Shortstop, speedRating).ToFielder().SprintSpeedMps;
        var fielderCap = sprint / (1.0 - PlaybackEvaluator.MoveRampFraction);          // 台形のピーク
        var runnerCap = sprint * Math.Max(
            new BaserunningCoefficients().RunnerTopSpeedFactor,
            new FieldingCoefficients().RunningTopSpeedFactor);
        const double tol = 1e-3;

        var seeds = Enumerable.Range(1, Games).Select(i => (ulong)i).ToArray();
        var worstFielder = new (double Peak, string Where)[seeds.Length];
        var worstRunner = new (double Peak, string Where)[seeds.Length];

        System.Threading.Tasks.Parallel.For(0, seeds.Length, i =>
        {
            var r = GameEngine.Play(BuildTeam("A", speedRating), BuildTeam("H", speedRating),
                new GameContext { CaptureTimelines = true }, new Xoshiro256Random(seeds[i]));
            foreach (var e in r.Log)
            {
                if (e.Timeline is null) continue;
                var play = PlayTimelineAdapter.ToPlaybackPlay(e.Timeline);

                foreach (var role in PlaybackEvaluator.DefaultPositions.Keys)
                {
                    if (!play.Moves.TryGetValue(role, out var segs)) continue;
                    foreach (var m in segs)
                    {
                        if (m.T1 - m.T0 <= 1e-6) continue;
                        const double dt = 1.0 / 120.0;
                        for (var t = m.T0; t <= m.T1; t += dt)
                        {
                            var p0 = PlaybackEvaluator.FielderAt(play, role, t).Pos;
                            var p1 = PlaybackEvaluator.FielderAt(play, role, t + dt).Pos;
                            var v = Dist(p0, p1) / dt;
                            if (v > worstFielder[i].Peak)
                                worstFielder[i] = (v, string.Format(CultureInfo.InvariantCulture,
                                    "seed{0} {1} {2} t={3:F2}→{4:F2} {5}", seeds[i], role, e.Timeline.Result, m.T0, m.T1, m.To));
                        }
                    }
                }

                foreach (var runner in play.Runners)
                    foreach (var s in runner.Segs)
                    {
                        var dur = s.T1 - s.T0;
                        var d = Dist(s.From, s.To);
                        if (dur <= 1e-6 || d < 0.01) continue;
                        var v = d / dur;
                        if (v > worstRunner[i].Peak)
                            worstRunner[i] = (v, string.Format(CultureInfo.InvariantCulture,
                                "seed{0} {1} {2} {3:F2}m/{4:F2}s", seeds[i], runner.Label, e.Timeline.Result, d, dur));
                    }
            }
        });

        var f = worstFielder.OrderByDescending(x => x.Peak).First();
        var g = worstRunner.OrderByDescending(x => x.Peak).First();
        Assert.True(f.Peak <= fielderCap + tol,
            $"野手が {f.Peak:F2} m/s（上限 {fielderCap:F2}）で動いている: {f.Where}");
        Assert.True(g.Peak <= runnerCap + tol,
            $"走者が {g.Peak:F2} m/s（上限 {runnerCap:F2}）で動いている: {g.Where}");
    }

    /// <summary>
    /// 1人の野手に<b>同じ時刻の行き先を2つ</b>与えない（<see cref="NoLegRequiresMoreThanHumanLegs"/> の
    /// 原因側を直接固定する）。
    ///
    /// <para><c>FielderAt</c> は同じ役の移動を順に合成する＝時刻が重なった2本は「両方の行き先へ同時に
    /// 寄る」1つの動きになり、どちらのレッグの尺より速くなる。実測（2026-08-14）: 右前へのフライを
    /// 一塁手が捕った seed182 で、投手が「本塁バックアップ(0,-6)」と「一塁カバー(19.6,19.7)」を
    /// 同時刻に持ち 11.26 m/s（上限 8.53）。一塁へ球は来ておらずカバー自体が不要な場面だった。
    /// 脚力の上限だけを見ていると、重なりが短い/近い時は上限に収まって**素通りする**ので、
    /// 重なりの有無そのものをここで見る。</para>
    /// </summary>
    [Fact]
    [Trait("Category", "Heavy")]
    public void PictureNeverSplitsAFielder()
    {
        var seeds = Enumerable.Range(1, Games).Select(i => (ulong)i).ToArray();
        var worst = new string?[seeds.Length];

        System.Threading.Tasks.Parallel.For(0, seeds.Length, i =>
        {
            var r = GameEngine.Play(BuildTeam("A", 50), BuildTeam("H", 50),
                new GameContext { CaptureTimelines = true }, new Xoshiro256Random(seeds[i]));
            foreach (var e in r.Log)
            {
                if (e.Timeline is null) continue;
                foreach (var g in e.Timeline.Moves.GroupBy(m => m.Role))
                {
                    var segs = g.OrderBy(m => m.T0).ToList();
                    for (var k = 1; k < segs.Count; k++)
                    {
                        if (segs[k].T0 >= segs[k - 1].T1 - 1e-9) continue;
                        worst[i] ??= string.Format(CultureInfo.InvariantCulture,
                            "seed{0} {1} {2}: {3} t={4:F2}→{5:F2} と {6} t={7:F2}→{8:F2} が重なる",
                            seeds[i], g.Key, e.Timeline.Result,
                            segs[k - 1].Task, segs[k - 1].T0, segs[k - 1].T1,
                            segs[k].Task, segs[k].T0, segs[k].T1);
                    }
                }
            }
        });

        var found = worst.Where(w => w is not null).ToList();
        Assert.True(found.Count == 0,
            found.Count == 0 ? "" : $"同時刻に2つの行き先を持つ野手が {found.Count} 試合で出た: {found[0]}");
    }

    private static double Dist(PlaybackVec a, PlaybackVec b)
        => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
}
