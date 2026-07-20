using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.AtBat;

/// <summary>
/// 設計書15 Phase A の合否テスト。<see cref="AtBatSession"/> を1球ずつ外部から回した打席が、従来一括の
/// <see cref="AtBatResolver.ResolveDetailed"/> と <b>打席結果・総球数・消費RNG数まで完全一致</b>することを固定する。
/// 走者/カウント初期値の代わりに、打者・投手能力のバリエーション×多シードで差分ゼロを機械的に検証する。
/// </summary>
public sealed class AtBatSessionTests
{
    /// <summary>消費した乱数の draw 回数を種別ごとに数える計数ラッパ（内部は同一 Xoshiro に委譲＝結果は不変）。</summary>
    private sealed class CountingRandom : IRandomSource
    {
        private readonly IRandomSource _inner;
        public long UInt64Calls;
        public long DoubleCalls;
        public long IntCalls;
        public long GaussianCalls;
        public long ForkCalls;

        public CountingRandom(IRandomSource inner) => _inner = inner;

        public ulong NextUInt64() { UInt64Calls++; return _inner.NextUInt64(); }
        public double NextDouble() { DoubleCalls++; return _inner.NextDouble(); }
        public int NextInt(int minInclusive, int maxExclusive) { IntCalls++; return _inner.NextInt(minInclusive, maxExclusive); }
        public double NextGaussian(double mean = 0.0, double stdDev = 1.0) { GaussianCalls++; return _inner.NextGaussian(mean, stdDev); }
        public IRandomSource Fork(ulong streamId) { ForkCalls++; return _inner.Fork(streamId); }

        public long Total => UInt64Calls + DoubleCalls + IntCalls + GaussianCalls + ForkCalls;

        public string Signature =>
            $"u{UInt64Calls}/d{DoubleCalls}/i{IntCalls}/g{GaussianCalls}/f{ForkCalls}";
    }

    private static readonly FieldGeometry Field = new();

    private static IEnumerable<(string Name, BatterAttributes Batter, PitcherAttributes Pitcher)> Matchups()
    {
        var avgP = PitcherAttributes.LeagueAverage;
        var wildP = new PitcherAttributes { MaxVelocityKmh = 128, Control = 25, PitchRank = 40 };
        var aceP = new PitcherAttributes { MaxVelocityKmh = 150, Control = 80, PitchRank = 80, StaminaPitches = 120 };

        yield return ("avg", BatterAttributes.LeagueAverage, avgP);
        yield return ("slugger", new BatterAttributes { Contact = 70, Power = 90, LaunchTendency = 70 }, avgP);
        yield return ("contact", new BatterAttributes { Contact = 90, Discipline = 75 }, avgP);
        yield return ("weak", new BatterAttributes { Contact = 25, Power = 20, Discipline = 30 }, aceP);
        yield return ("patient_vs_wild", new BatterAttributes { Discipline = 95, Contact = 55 }, wildP);
        yield return ("free_swinger", new BatterAttributes { Discipline = 20, Contact = 60 }, aceP);
    }

    /// <summary>
    /// 逐次進行（球間に何もしない＝Phase A の no-op 采配窓）で回した AtBatSession が、従来一括 ResolveDetailed と
    /// 結果 enum・総球数・消費RNG数まで一致する。球間 yield 相当の一時停止では乱数を消費しないことも同時に固定。
    /// </summary>
    [Fact]
    public void StepDrivenSession_MatchesResolveDetailed_ResultPitchesAndRngConsumption()
    {
        var mismatches = new List<string>();
        var chec0 = 0;

        foreach (var (name, batter, pitcher) in Matchups())
        {
            var ctx = new AtBatContext { Fielders = Field.StandardAlignment() };
            for (ulong seed = 1; seed <= 60; seed++)
            {
                // ① 従来一括: ResolveDetailed（内部でも AtBatSession を drain するが、外部からは一括呼び出し）。
                var batchRng = new CountingRandom(new Xoshiro256Random(seed));
                var batch = AtBatResolver.ResolveDetailed(batter, pitcher, ctx, batchRng);

                // ② 逐次進行: Begin → ThrowNextPitch を1球ずつ。各球の「前」に一時停止（乱数を引かない）を挟む。
                var stepRng = new CountingRandom(new Xoshiro256Random(seed));
                var session = AtBatSession.Begin(batter, pitcher, ctx);
                var pitches = 0;
                var beforeEachPitch = stepRng.Total; // 球間の一時停止では総 draw が動かないことを確認
                while (!session.IsComplete)
                {
                    Assert.Equal(beforeEachPitch, stepRng.Total); // yield（采配窓）は乱数を消費しない
                    var res = session.ThrowNextPitch(stepRng);
                    pitches++;
                    beforeEachPitch = stepRng.Total;
                    Assert.Equal(session.IsComplete, res.EndsPlateAppearance);
                }

                chec0++;

                if (batch.Result != session.Result.Result)
                    mismatches.Add($"{name}#{seed}: result {batch.Result} != {session.Result.Result}");
                else if (batch.Pitches != session.Result.Pitches || batch.Pitches != pitches)
                    mismatches.Add($"{name}#{seed}: pitches batch={batch.Pitches} session={session.Result.Pitches} loop={pitches}");
                else if (batchRng.Signature != stepRng.Signature)
                    mismatches.Add($"{name}#{seed}: rng {batchRng.Signature} != {stepRng.Signature}");
            }
        }

        Assert.True(chec0 >= 300, $"検証件数が少ない（{chec0}）");
        Assert.True(mismatches.Count == 0,
            $"Session(step) != ResolveDetailed（{mismatches.Count}件）:\n" + string.Join("\n", mismatches));
    }

    /// <summary>能力補正コピー（広角打法/粘り打ち）が効く経路でも逐次==一括を維持する（Begin の1回コピーの同一性）。</summary>
    [Fact]
    public void StepDrivenSession_MatchesResolveDetailed_WithSkillPlayMods()
    {
        var skills = new SkillPlayMods { BearingSigmaFactor = 1.2, FoulShareFactor = 1.3, StuffBonus = 5.0, FirstPitchSwingProb = 0.4 };
        var ctx = new AtBatContext { Fielders = Field.StandardAlignment(), Skills = skills };
        var batter = new BatterAttributes { Contact = 60, Power = 65 };
        var pitcher = PitcherAttributes.LeagueAverage;

        for (ulong seed = 1; seed <= 60; seed++)
        {
            var batchRng = new CountingRandom(new Xoshiro256Random(seed));
            var batch = AtBatResolver.ResolveDetailed(batter, pitcher, ctx, batchRng);

            var stepRng = new CountingRandom(new Xoshiro256Random(seed));
            var session = AtBatSession.Begin(batter, pitcher, ctx);
            while (!session.IsComplete) session.ThrowNextPitch(stepRng);

            Assert.Equal(batch.Result, session.Result.Result);
            Assert.Equal(batch.Pitches, session.Result.Pitches);
            Assert.Equal(batchRng.Signature, stepRng.Signature);
        }
    }

    /// <summary>確定後に ThrowNextPitch を呼ぶと例外（誤用検出）。</summary>
    [Fact]
    public void ThrowNextPitch_AfterComplete_Throws()
    {
        var ctx = new AtBatContext { Fielders = Field.StandardAlignment() };
        var session = AtBatSession.Begin(BatterAttributes.LeagueAverage, PitcherAttributes.LeagueAverage, ctx);
        var rng = new Xoshiro256Random(1);
        while (!session.IsComplete) session.ThrowNextPitch(rng);
        Assert.Throws<System.InvalidOperationException>(() => session.ThrowNextPitch(rng));
    }
}
