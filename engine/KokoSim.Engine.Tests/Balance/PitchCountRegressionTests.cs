using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using Xunit;

namespace KokoSim.Engine.Tests.Balance;

/// <summary>
/// 打席あたりの球数・初球で終わる割合・初球スイング率の統計回帰（issue #683）。
/// 1試合の球数（balance-targets の pitches_per_game）だけでは「全員が早打ち」を捕まえられない
/// （1打席3.26球でも1試合の帯には収まっていた）ので、打席単位の指標を別に固定する。
/// 目標: 打席あたり 3.6球・初球で終わる打席 約17%・初球スイング 約35%（実野球の目安 3.6〜3.9球／1〜2割強／約3割）。
/// 帯は目標の周りにゆるく置く（係数を動かしたら、ここと balance-targets の他の帯を一緒に確かめる）。
/// 強さの組は5通り×120試合（汎用の母集団）。
/// </summary>
[Trait("Category", "Heavy")]
public sealed class PitchCountRegressionTests
{
    [Fact]
    public void PitchesPerPlateAppearance_StayNearTarget()
    {
        var pas = new ConcurrentBag<IReadOnlyList<PitchRecord>>();
        var inputs = new List<(double A, double H, ulong Seed)>();
        foreach (var (a, h) in new[] { (40.0, 40.0), (50.0, 50.0), (60.0, 50.0), (45.0, 55.0), (70.0, 40.0) })
            for (ulong seed = 1; seed <= 120; seed++) inputs.Add((a, h, seed));

        Parallel.ForEach(inputs, t =>
        {
            var away = StrengthTeamFactory.Create(t.A, "A", new Xoshiro256Random(t.Seed * 7 + 1));
            var home = StrengthTeamFactory.Create(t.H, "H", new Xoshiro256Random(t.Seed * 13 + 5));
            var r = GameEngine.Play(away, home, new GameContext(), new Xoshiro256Random(t.Seed));
            foreach (var e in r.Log)
                if (e.PitchLog is { Count: > 0 } pl) pas.Add(pl);
        });

        var list = pas.ToList();
        var perPa = list.Sum(p => p.Count) / (double)list.Count;
        var firstEnds = list.Count(p => p.Count == 1) / (double)list.Count;
        var firstSwings = list.Count(p => p[0].Kind is PitchKind.SwingingStrike or PitchKind.Foul or PitchKind.InPlay) / (double)list.Count;

        Assert.InRange(perPa, 3.45, 3.85);
        Assert.InRange(firstEnds, 0.12, 0.20);
        Assert.InRange(firstSwings, 0.28, 0.40);
    }
}
