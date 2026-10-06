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

    /// <summary>
    /// <see cref="PlateAppearanceResultExtensions.IsLookingStrikeout"/>（見逃し三振率の調査用に導入）は
    /// 三振の最終球が CalledStrike のときだけ true になり、空振り三振（最終球が SwingingStrike）は false になる。
    /// 三振以外の結果は常に false。両方のケースが実際に現れることも固定する。
    /// </summary>
    [Fact]
    public void IsLookingStrikeout_MatchesFinalPitchKind_AndBothKindsOccur()
    {
        var looking = 0;
        var swinging = 0;
        var checkedNonStrikeouts = 0;

        foreach (var (_, batter, pitcher) in Matchups())
        {
            var ctx = new AtBatContext { Fielders = Field.StandardAlignment() };
            for (ulong seed = 1; seed <= 200; seed++)
            {
                var session = AtBatSession.Begin(batter, pitcher, ctx);
                var rng = new Xoshiro256Random(seed);
                while (!session.IsComplete) session.ThrowNextPitch(rng);
                var result = session.Result;

                if (result.Result != PlateAppearanceResult.Strikeout)
                {
                    Assert.False(result.Result.IsLookingStrikeout(result.PitchLog));
                    checkedNonStrikeouts++;
                    continue;
                }

                var last = result.PitchLog![^1];
                Assert.Equal(last.Kind == PitchKind.CalledStrike, result.Result.IsLookingStrikeout(result.PitchLog));
                if (result.Result.IsLookingStrikeout(result.PitchLog)) looking++;
                else swinging++;
            }
        }

        Assert.True(checkedNonStrikeouts > 0, "三振以外の結果が1件も観測されなかった（テスト条件を見直す）");
        Assert.True(looking > 0, "見逃し三振が1件も観測されなかった");
        Assert.True(swinging > 0, "空振り三振が1件も観測されなかった");
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
            Reserves = new[] { Pos(FieldPosition.Pitcher) with { Name = name + "R", Pitching = PitcherAttributes.LeagueAverage } },
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

    // --- BattedBall 観測（Issue #313: ファウルの打球データ） ---

    /// <summary>
    /// <see cref="PitchRecord.BattedBall"/> は「コンタクトが実際に物理層（初速・角度・方向）を生成し、
    /// かつ FieldingResolver が方位角±45°超えでファウルと裁いた」経路だけが非null になる。
    /// 確率だけで決まる「当たったが物理を生成しない」ファウル（<see cref="BattingCoefficients.FoulShare"/> 経路）
    /// は解決済みの値そのものが存在しないため null のまま（設計書15 §4「解決済みの値の写しのみ」）。
    /// 特定の乱数列を手で組む代わりに多数の打席を回し、両方の経路が実際に現れること・非nullのときは
    /// 常に FieldingResolver と同じファウル判定基準（|BearingDeg| &gt; 45°）を満たすことを固定する。
    /// </summary>
    [Fact]
    public void FoulBattedBall_PopulatedOnlyWhenPhysicsWasGenerated_AndMatchesFoulBearing()
    {
        var ctx = new AtBatContext { Fielders = Field.StandardAlignment() };
        var foulsWithBall = new List<BattedBall>();
        var foulsWithoutBall = 0;

        foreach (var (_, batter, pitcher) in Matchups())
        {
            for (ulong seed = 1; seed <= 150; seed++)
            {
                var session = AtBatSession.Begin(batter, pitcher, ctx);
                var rng = new Xoshiro256Random(seed);
                while (!session.IsComplete) session.ThrowNextPitch(rng);

                foreach (var rec in session.Result.PitchLog!)
                {
                    if (rec.Kind != PitchKind.Foul) continue;
                    if (rec.BattedBall is { } ball) foulsWithBall.Add(ball);
                    else foulsWithoutBall++;
                }
            }
        }

        Assert.True(foulsWithBall.Count > 0, "物理層を持つファウル（方位角超え経路）が1件も観測されなかった");
        Assert.True(foulsWithoutBall > 0, "物理層を持たないファウル（FoulShare経路）が1件も観測されなかった");
        Assert.All(foulsWithBall, b => Assert.True(System.Math.Abs(b.BearingDeg) > 45.0,
            $"BattedBall はあるのに方位角がファウル域外 (BearingDeg={b.BearingDeg})"));
    }

    // --- 狙い位置（AimX/AimY）観測（Issue #396: 対決ビューの捕手構え） ---

    /// <summary>
    /// <see cref="PitchRecord.AimX"/>/<see cref="AimY"/> は <see cref="Pitching.PitchPlan.AimX"/>/<see cref="AimY"/>
    /// の写しで、<see cref="PitchRecord.LocationX"/>/<see cref="LocationY"/>（コントロール散布込みの実着弾位置）とは
    /// 別物。散布が実際に効いている（狙いと実着弾が食い違う球が現れる）ことを固定し、AimX/AimY が誤って
    /// LocationX/LocationY のコピーになっていないことを検出する。
    /// </summary>
    [Fact]
    public void AimXY_ObservationData_DiffersFromActualLocation_WhenControlScatterApplies()
    {
        var ctx = new AtBatContext { Fielders = Field.StandardAlignment() };
        var checkedRecords = 0;
        var scatteredRecords = 0;

        foreach (var (_, batter, pitcher) in Matchups())
        {
            for (ulong seed = 1; seed <= 80; seed++)
            {
                var session = AtBatSession.Begin(batter, pitcher, ctx);
                var rng = new Xoshiro256Random(seed);
                while (!session.IsComplete) session.ThrowNextPitch(rng);

                foreach (var rec in session.Result.PitchLog!)
                {
                    Assert.True(double.IsFinite(rec.AimX), "AimX が有限値でない");
                    Assert.True(double.IsFinite(rec.AimY), "AimY が有限値でない");
                    checkedRecords++;
                    if (System.Math.Abs(rec.AimX - rec.LocationX) > 0.001
                        || System.Math.Abs(rec.AimY - rec.LocationY) > 0.001)
                        scatteredRecords++;
                }
            }
        }

        Assert.True(checkedRecords > 300, $"検証件数が少ない（{checkedRecords}）");
        Assert.True(scatteredRecords > 0, "AimX/AimY が常に実着弾位置と一致している（散布が効いていない）");
    }

    /// <summary>
    /// <see cref="Timeline.Playback.PitchTokenFactory.From"/> は AimX/AimY をそのまま写す（新規抽選なし）。
    /// </summary>
    [Fact]
    public void PitchTokenFactory_CopiesAimXY_FromPitchRecord()
    {
        var rec = new PitchRecord(
            PitchKind.Ball, 1, 0, KokoSim.Engine.Players.PitchType.Fastball,
            LocationX: 0.20, LocationY: 0.90, VelocityKmh: 145.0,
            AimX: 0.05, AimY: 0.80);

        var token = KokoSim.Engine.Match.Timeline.Playback.PitchTokenFactory.From(
            rec, new KokoSim.Engine.Match.Game.GameContext(),
            runnerOnFirst: false, runnerOnSecond: false, runnerOnThird: false);

        Assert.Equal(rec.AimX, token.AimX);
        Assert.Equal(rec.AimY, token.AimY);
    }

    /// <summary>
    /// 合成列（<see cref="Timeline.Playback.PitchSequenceSynthesizer"/>）は実データを持たないため、
    /// AimX/AimY も LocationX/LocationY と同様に null のまま（架空の狙いを主張しない）。
    /// </summary>
    [Fact]
    public void SynthesizedPitchToken_HasNullAimXY()
    {
        var seq = KokoSim.Engine.Match.Timeline.Playback.PitchSequenceSynthesizer.Synthesize(
            PlateAppearanceResult.Strikeout, pitchCount: 4, seed: 12345);

        Assert.NotEmpty(seq.Pitches);
        Assert.All(seq.Pitches, t =>
        {
            Assert.Null(t.AimX);
            Assert.Null(t.AimY);
        });
    }

    /// <summary>バント試行のファウルは打球データを持たない（BuntResolver は物理層を生成しないため）。</summary>
    [Fact]
    public void BuntFoul_NeverHasBattedBall()
    {
        var ctx = new AtBatContext { Fielders = Field.StandardAlignment() };
        var batter = new BatterAttributes { Contact = 55 };
        var bunter = new Player { Bunt = 55, Speed = 60 };
        var pitcher = PitcherAttributes.LeagueAverage;
        var buntFouls = 0;

        for (ulong seed = 1; seed <= 200; seed++)
        {
            var session = AtBatSession.Begin(batter, pitcher, ctx, batterPlayer: bunter);
            var rng = new Xoshiro256Random(seed);
            while (!session.IsComplete)
                session.ThrowNextPitch(rng, KokoSim.Engine.Match.Tactics.PitchBattingOverride.Bunt);

            foreach (var rec in session.Result.PitchLog!)
            {
                if (rec.Kind != PitchKind.Foul) continue;
                buntFouls++;
                Assert.Null(rec.BattedBall);
            }
        }

        Assert.True(buntFouls > 0, "バントのファウルが1件も観測されなかった（テスト条件を見直す）");
    }
}
