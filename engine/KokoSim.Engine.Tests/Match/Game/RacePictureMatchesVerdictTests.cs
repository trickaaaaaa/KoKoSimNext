using System;
using System.Linq;
using System.Threading.Tasks;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 塁上の時間の勝負（本塁・三塁・タッチアップ・進塁打）で、アウトの判定が送球の到達より先に出ないこと（issue #801）。
/// 判定は運を秒として扱う＝勝負の抽選はクランプしない logistic で、アウトなら走者が引き受けた出遅れ秒が絵に乗る。
/// </summary>
public sealed class RacePictureMatchesVerdictTests
{
    private static readonly BaserunningCoefficients C = new();
    private static readonly FieldGeometry Field = new();

    private static HomePlaySituation Situation(double distanceM, double fieldedAt)
        => new(new Vector3D(0, 0, distanceM), fieldedAt, new Player { ArmStrength = 50 }.ToFielder().ThrowSpeedMps);

    /// <summary>NextDouble を固定値で返し、呼ばれた回数を数える。</summary>
    private sealed class FixedRandom(double u) : IRandomSource
    {
        public int Draws { get; private set; }
        public double NextDouble() { Draws++; return u; }
        public ulong NextUInt64() => 0UL;
        public int NextInt(int minInclusive, int maxExclusive) => minInclusive;
        public double NextGaussian(double mean = 0.0, double stdDev = 1.0) => mean;
        public IRandomSource Fork(ulong streamId) => this;
    }

    [Fact]
    public void RunnerFarAheadOfTheThrow_IsNotOutOnTheOnePercentFloor()
    {
        // 深いセンター前（処理点95m・処理4.5s）で三塁走者が還る＝走者は送球より数秒早い。
        // 以前は確率が99%で頭打ちだったので、u=0.995 を引くとアウトになっていた。
        var s = Situation(95, 4.5);
        var runner = new Player { Speed = 50, Baserunning = 50 };
        var p = HomePlayResolver.HomeParams(C);
        Assert.True(HomePlayResolver.Margin(runner, 3, s, Field, C, p) > 2.0);

        var race = HomePlayResolver.ResolveRace(runner, 3, s, Field, C, p, new FixedRandom(0.995));
        Assert.True(race.Safe);
    }

    [Fact]
    public void Race_DrawsExactlyOneRandom()
    {
        var rng = new FixedRandom(0.5);
        HomePlayResolver.ResolveRace(new Player(), 2, Situation(70, 3.0), Field, C, HomePlayResolver.HomeParams(C), rng);
        Assert.Equal(1, rng.Draws);
    }

    [Fact]
    public void Out_RunnerIsDelayedUntilTheThrowCanArrive()
    {
        // アウトなら、走者の到達（＋出遅れ）は「持ち替えを最速で済ませた送球＋タッチ」より前にならない。
        var p = HomePlayResolver.HomeParams(C);
        var outs = 0;
        foreach (var dist in new[] { 45.0, 60.0, 75.0, 90.0 })
        foreach (var fromBase in new[] { 1, 2, 3 })
        foreach (var u in new[] { 0.9, 0.99, 0.999, 0.99999 })
        {
            var s = Situation(dist, 2.0 + dist / 30.0);
            var runner = new Player { Speed = 50, Baserunning = 50 };
            var race = HomePlayResolver.ResolveRace(runner, fromBase, s, Field, C, p, new FixedRandom(u));
            if (race.Safe) { Assert.Equal(0.0, race.RunnerDelaySeconds); continue; }
            outs++;
            var defense = HomePlayResolver.DefenseTimeSeconds(s, p.TargetPoint, p.TagSeconds, C);
            var quick = HomePlayResolver.QuickHandlingSeconds(s, p.TargetPoint, C);
            var runnerArrive = HomePlayResolver.RunnerTimeSeconds(runner, fromBase, Field, C) + race.RunnerDelaySeconds;
            Assert.True(runnerArrive >= defense - quick - 1e-9,
                $"走者が送球より先に着いてアウト: dist={dist} from={fromBase} u={u} 走者={runnerArrive:F2} 守備={defense:F2}");
        }
        Assert.True(outs > 0);
    }

    [Fact]
    public void RealGames_NoRunnerIsCalledOutBeforeTheBallReachesTheBase()
    {
        const int games = 300;
        var early = new int[games];
        var counted = new int[games];
        Parallel.For(0, games, i =>
        {
            var seed = (ulong)(i + 1);
            var away = StrengthTeamFactory.Create(55, "先攻", new Xoshiro256Random(seed * 2));
            var home = StrengthTeamFactory.Create(55, "後攻", new Xoshiro256Random(seed * 2 + 1));
            var r = GameEngine.Play(away, home, new GameContext { CaptureTimelines = true }, new Xoshiro256Random(seed));
            foreach (var e in r.Log)
            {
                if (e.Timeline is not { } tl) continue;
                foreach (var leg in tl.Runners.Where(l => l.OutAtEnd && l.Label != "打"))
                {
                    counted[i]++;
                    var judged = tl.Judgements.FirstOrDefault(j => j.Label == leg.Label && j.Kind == JudgementKind.Out)?.T ?? leg.T1;
                    // その塁へ球を届けた送球（自分で踏んだ運びを含む）のうち最も早い到達
                    var arrivals = tl.Ball
                        .Where(b => b.Kind is BallSegmentKind.Throw or BallSegmentKind.Carry
                                    && Math.Abs(b.To.X - leg.To.X) < 3 && Math.Abs(b.To.Z - leg.To.Z) < 3)
                        .Select(b => b.T1).ToList();
                    if (arrivals.Count == 0 || arrivals.Min() > judged + 0.05) early[i]++;
                }
            }
        });
        Assert.True(counted.Sum() > 1000, $"走者のアウトが少なすぎる（{counted.Sum()}件）");
        Assert.Equal(0, early.Sum());
    }
}
