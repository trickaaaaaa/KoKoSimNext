using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.AtBat;

/// <summary>
/// 設計書15 Phase B-1 の合否テスト。<see cref="AtBatSession"/> が露出する
/// <see cref="AtBatResult.PitchLog"/> が、打席結果（enum・総球数）と矛盾なく再構成できることを固定する
/// （設計書15 §4「テスト1: PitchLog 整合」）。PitchLog は解決済みの値の写しのみで新たな抽選をしないため、
/// ここでの検証はすべて非乱数（rng は打席解決そのものにしか使わない）。
/// </summary>
public sealed class PitchRecordTests
{
    private static readonly FieldGeometry Field = new();

    private static IEnumerable<(string Name, BatterAttributes Batter, PitcherAttributes Pitcher)> Matchups()
    {
        var avgP = PitcherAttributes.LeagueAverage;
        var wildP = new PitcherAttributes { MaxVelocityKmh = 128, Control = 25, PitchRank = 40 };
        var aceP = new PitcherAttributes { MaxVelocityKmh = 150, Control = 80, PitchRank = 80, StaminaPitches = 120 };

        yield return ("avg", BatterAttributes.LeagueAverage, avgP);
        yield return ("slugger", new BatterAttributes { Contact = 70, Power = 90, LaunchTendency = 70 }, avgP);
        yield return ("patient_vs_wild", new BatterAttributes { Discipline = 95, Contact = 55 }, wildP);
        yield return ("free_swinger", new BatterAttributes { Discipline = 20, Contact = 60 }, aceP);
    }

    [Fact]
    public void PitchLog_ReconstructsFinalCountAndPitchCount_MatchingAtBatResult()
    {
        var mismatches = new List<string>();
        var chec0 = 0;

        foreach (var (name, batter, pitcher) in Matchups())
        {
            var ctx = new AtBatContext { Fielders = Field.StandardAlignment() };
            for (ulong seed = 1; seed <= 80; seed++)
            {
                var session = AtBatSession.Begin(batter, pitcher, ctx);
                var rng = new Xoshiro256Random(seed);
                while (!session.IsComplete) session.ThrowNextPitch(rng);
                var result = session.Result;
                chec0++;

                var log = result.PitchLog;
                if (log is null) { mismatches.Add($"{name}#{seed}: PitchLog が null"); continue; }
                if (log.Count != result.Pitches)
                {
                    mismatches.Add($"{name}#{seed}: PitchLog件数{log.Count} != Pitches{result.Pitches}");
                    continue;
                }

                // カウント再構成: Ball/CalledStrike/SwingingStrikeで増加、Foulは2ストライク未満のみ増加
                // （ファウルの2ストライク後不増加は実データで自然に成立するはず）。
                var balls = 0;
                var strikes = 0;
                var countMismatch = false;
                foreach (var rec in log)
                {
                    switch (rec.Kind)
                    {
                        case PitchKind.Ball: balls++; break;
                        case PitchKind.CalledStrike:
                        case PitchKind.SwingingStrike:
                            strikes++; break;
                        case PitchKind.Foul:
                            if (strikes < 2) strikes++;
                            break;
                        case PitchKind.InPlay: break;
                    }
                    if (rec.BallsAfter != balls || rec.StrikesAfter != strikes)
                    {
                        mismatches.Add(
                            $"{name}#{seed}: カウント不一致 記録={rec.BallsAfter}-{rec.StrikesAfter} 再構成={balls}-{strikes}");
                        countMismatch = true;
                        break;
                    }
                }
                if (countMismatch) continue;

                var last = log[^1];
                switch (result.Result)
                {
                    case PlateAppearanceResult.Strikeout:
                        if (last.Kind is not (PitchKind.CalledStrike or PitchKind.SwingingStrike) || last.StrikesAfter != 3)
                            mismatches.Add($"{name}#{seed}: 三振の最終球が不整合 {last.Kind} {last.StrikesAfter}");
                        break;
                    case PlateAppearanceResult.Walk:
                        if (last.Kind != PitchKind.Ball || last.BallsAfter != 4)
                            mismatches.Add($"{name}#{seed}: 四球の最終球が不整合 {last.Kind} {last.BallsAfter}");
                        break;
                    case PlateAppearanceResult.HitByPitch:
                        if (last.Kind != PitchKind.HitByPitch || last.BallsAfter >= 4 || last.StrikesAfter >= 3)
                            mismatches.Add($"{name}#{seed}: 死球の最終球が不整合 {last.Kind} {last.BallsAfter}-{last.StrikesAfter}");
                        break;
                    default:
                        if (last.Kind != PitchKind.InPlay)
                            mismatches.Add($"{name}#{seed}: インプレー結果の最終球が不整合 {last.Kind}");
                        break;
                }
            }
        }

        Assert.True(chec0 >= 300, $"検証件数が少ない（{chec0}）");
        Assert.True(mismatches.Count == 0, $"PitchLog不整合（{mismatches.Count}件）:\n" + string.Join("\n", mismatches));
    }

    // --- GameEngine 配線（PlayLogEntry.PitchLog）の結線確認 ---

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

    /// <summary>通常打席（AtBatSession 経由）は PlayLogEntry.PitchLog が球数と一致する件数で必ず載る。</summary>
    [Fact]
    public void GameEngine_PopulatesPitchLog_OnPlayLogEntries()
    {
        var r = GameEngine.Play(BuildTeam("A"), BuildTeam("H"), new GameContext(), new Xoshiro256Random(1));

        Assert.NotEmpty(r.Log);
        foreach (var e in r.Log)
        {
            Assert.NotNull(e.PitchLog);
            Assert.Equal(e.Pitches, e.PitchLog!.Count);
        }
    }

    // --- Trajectory 観測（設計書15 §4「テスト2/3」: 観測ゼロ影響・弾道無乱数） ---

    /// <summary>消費した乱数の draw 回数を数える計数ラッパ（内部は同一 Xoshiro に委譲＝結果は不変）。</summary>
    private sealed class CountingRandom : IRandomSource
    {
        private readonly IRandomSource _inner;
        public long Calls;
        public CountingRandom(IRandomSource inner) => _inner = inner;
        public ulong NextUInt64() { Calls++; return _inner.NextUInt64(); }
        public double NextDouble() { Calls++; return _inner.NextDouble(); }
        public int NextInt(int minInclusive, int maxExclusive) { Calls++; return _inner.NextInt(minInclusive, maxExclusive); }
        public double NextGaussian(double mean = 0.0, double stdDev = 1.0) { Calls++; return _inner.NextGaussian(mean, stdDev); }
        public IRandomSource Fork(ulong streamId) { Calls++; return _inner.Fork(streamId); }
    }

    /// <summary>
    /// Trajectory は CaptureTimeline のときだけ計算される（既定オフ＝統計シムはゼロコスト、設計書15 §4）。
    /// ON/OFF いずれでも消費RNG数・打席結果・球数は同一＝弾道積分はRNGを1発も引かない（観測専用）。
    /// </summary>
    [Fact]
    public void Trajectory_GatedByCaptureTimeline_AndConsumesNoRng()
    {
        var batter = BatterAttributes.LeagueAverage;
        var pitcher = PitcherAttributes.LeagueAverage;

        for (ulong seed = 1; seed <= 40; seed++)
        {
            var offCtx = new AtBatContext { Fielders = Field.StandardAlignment(), CaptureTimeline = false };
            var onCtx = new AtBatContext { Fielders = Field.StandardAlignment(), CaptureTimeline = true };

            var offRng = new CountingRandom(new Xoshiro256Random(seed));
            var off = AtBatSession.Begin(batter, pitcher, offCtx);
            while (!off.IsComplete) off.ThrowNextPitch(offRng);

            var onRng = new CountingRandom(new Xoshiro256Random(seed));
            var on = AtBatSession.Begin(batter, pitcher, onCtx);
            while (!on.IsComplete) on.ThrowNextPitch(onRng);

            Assert.Equal(off.Result.Result, on.Result.Result);
            Assert.Equal(off.Result.Pitches, on.Result.Pitches);
            Assert.Equal(offRng.Calls, onRng.Calls);

            Assert.All(off.Result.PitchLog!, r => Assert.Null(r.Trajectory));
            Assert.All(on.Result.PitchLog!, r => Assert.NotNull(r.Trajectory));
        }
    }

    /// <summary>CaptureTimeline時の弾道は物理的に妥当な範囲（到達時間・ホップ量が有限かつ常識的な幅）。</summary>
    [Fact]
    public void Trajectory_IsPhysicallyPlausible_WhenCaptureTimelineOn()
    {
        var ctx = new AtBatContext { Fielders = Field.StandardAlignment(), CaptureTimeline = true };
        var checkedRecords = 0;

        for (ulong seed = 1; seed <= 20; seed++)
        {
            var session = AtBatSession.Begin(BatterAttributes.LeagueAverage, PitcherAttributes.LeagueAverage, ctx);
            var rng = new Xoshiro256Random(seed);
            while (!session.IsComplete) session.ThrowNextPitch(rng);

            foreach (var rec in session.Result.PitchLog!)
            {
                var t = rec.Trajectory!;
                Assert.InRange(t.FlightTimeSeconds, 0.20, 0.80);
                Assert.True(double.IsFinite(t.InducedVerticalBreakM));
                Assert.InRange(System.Math.Abs(t.InducedVerticalBreakM), 0.0, 1.0);
                checkedRecords++;
            }
        }

        Assert.True(checkedRecords > 50, $"検証件数が少ない（{checkedRecords}）");
    }
}
