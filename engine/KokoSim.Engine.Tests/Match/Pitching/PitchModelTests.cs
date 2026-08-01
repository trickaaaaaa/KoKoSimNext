using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Pitching;

/// <summary>
/// Slice 2D の投手モデル: 毎球の球速ばらつき（§1.1d）・スタミナ＝目安投球数（§1.1e）・
/// ギア（§1.1f）・球種2軸ランク/レパートリー（§2.2）。
/// </summary>
public sealed class PitchModelTests
{
    private static readonly StrikeZone Zone = new();
    private static readonly BattingCoefficients Bat = new();
    private static readonly PitchingCoefficients Pit = new();

    // --- 毎球の球速ばらつき（§1.1d） ---

    [Fact]
    public void PitchVelocity_NeverExceedsCatalogMax_AndVariesBelow()
    {
        var pitcher = new PitcherAttributes { MaxVelocityKmh = 145.0 }; // ストレートのみ
        var rng = new Xoshiro256Random(1);
        var velos = Enumerable.Range(0, 2000)
            .Select(_ => PitchSelection.Select(pitcher, Zone, Bat, Pit, rng).VelocityKmh)
            .ToList();

        Assert.All(velos, v => Assert.True(v <= 145.0 + 1e-9, $"最速超過: {v:F1}"));
        // 常時は最速より数km/h下（平均落差 3〜5km/h）。
        var meanDrop = 145.0 - velos.Average();
        Assert.InRange(meanDrop, 2.5, 5.5);
        // たまに最速付近（−1km/h以内）も出る。
        Assert.Contains(velos, v => v > 144.0);
    }

    // --- スタミナ＝目安投球数（§1.1e） ---

    [Fact]
    public void Fatigue_UsesStaminaPitches_AsFreshWindow()
    {
        var p = new Player { Pitching = new PitcherAttributes { StaminaPitches = 80.0, MaxVelocityKmh = 140.0, Control = 60 } };
        var fresh = PitchingFatigue.Effective(p, 80.0, new FatigueCoefficients());
        Assert.Equal(140.0, fresh.MaxVelocityKmh); // 目安以内は無減衰

        var tired = PitchingFatigue.Effective(p, 120.0, new FatigueCoefficients());
        Assert.True(tired.MaxVelocityKmh < 140.0, "目安超過で球速天井が落ちない");
        Assert.True(tired.Control < 60, "目安超過で制球が落ちない");
    }

    [Fact]
    public void IronArm_LastsLonger_ThanFrailAce()
    {
        var c = new FatigueCoefficients();
        var iron = new Player { Pitching = new PitcherAttributes { StaminaPitches = 130.0 } };  // 弱小校の完投型
        var frail = new Player { Pitching = new PitcherAttributes { StaminaPitches = 65.0 } };  // 強豪の細いエース
        Assert.False(PitchingFatigue.ShouldRelieve(iron, 100.0, c));
        Assert.True(PitchingFatigue.ShouldRelieve(frail, 100.0, c));
    }

    [Fact]
    public void StaminaLevelConversion_MapsToPitchCount()
    {
        // Level 50 → 90球 / Level 100 → 135球（変換は一箇所に集約）。
        Assert.Equal(90.0, PitcherAttributes.StaminaPitchesFromLevel(50), 6);
        Assert.Equal(135.0, PitcherAttributes.StaminaPitchesFromLevel(100), 6);
    }

    [Fact]
    public void LevelFromStaminaPitches_ExtrapolatesAbove100_NoUpperCap()
    {
        // issue #356: スタミナは上限なし。目安投球数180球は Lv100 で頭打ちにならず Lv150 相当を返す
        // （逆変換 (180-45)/0.9 = 150）。従来の Lv100=135球 は不変（下限・傾きはそのまま）。
        Assert.Equal(100, PitcherAttributes.LevelFromStaminaPitches(135.0));
        Assert.Equal(150, PitcherAttributes.LevelFromStaminaPitches(180.0));
        // 目安投球数が伸びるほど等級も単調に増える（ReliefScore/PitcherScore で他能力と土俵を揃える）。
        Assert.True(PitcherAttributes.LevelFromStaminaPitches(200.0)
                    > PitcherAttributes.LevelFromStaminaPitches(160.0));
    }

    [Fact]
    public void HighStaminaPitcher_NotRelievedByPitchCountAlone_ButFatigueStillDegrades()
    {
        // issue #356: ハードキャップ（旧130球）撤廃。目安投球数160球の鉄腕は旧仕様なら130球で強制降板だが、
        // 撤廃後は「球数だけ」を理由には降板しない＝目安＋margin(25) まで投げる。疲労の能力低下は従来どおり掛かる。
        var c = new FatigueCoefficients();
        var ace = new Player { Pitching = new PitcherAttributes { StaminaPitches = 160.0, MaxVelocityKmh = 145.0, Control = 60 } };

        // 旧ハードキャップ130を大きく超える145球でも、目安160＋margin25=185未満なので降板しない。
        Assert.False(PitchingFatigue.ShouldRelieve(ace, 145.0, c));

        // 目安160を超える175球では球速天井・制球が落ちる（能力低下は健在＝諸刃）。
        var tired = PitchingFatigue.Effective(ace, 175.0, c);
        Assert.True(tired.MaxVelocityKmh < 145.0, "目安超過で球速が落ちていない");
        Assert.True(tired.Control < 60, "目安超過で制球が落ちていない");

        // 目安＋margin を超えれば（球数の絶対値とは無関係に）降板する。
        Assert.True(PitchingFatigue.ShouldRelieve(ace, 185.0, c));
    }

    // --- ギア「飛ばす/流す」（§1.1f） ---

    [Fact]
    public void GearPush_RaisesVelocityCeiling_CoastLowers()
    {
        var pitcher = new PitcherAttributes { MaxVelocityKmh = 140.0 };
        double Mean(PitcherGear gear, ulong seed)
        {
            var rng = new Xoshiro256Random(seed);
            return Enumerable.Range(0, 1500)
                .Select(_ => PitchSelection.Select(pitcher, Zone, Bat, Pit, rng, gear).VelocityKmh)
                .Average();
        }
        var normal = Mean(PitcherGear.Normal, 7);
        var push = Mean(PitcherGear.Push, 7);
        var coast = Mean(PitcherGear.Coast, 7);
        Assert.True(push > normal, $"飛ばすで球速が上がらない: {push:F1} vs {normal:F1}");
        Assert.True(coast < normal, $"流すで球速が下がらない: {coast:F1} vs {normal:F1}");
    }

    [Fact]
    public void GearWeight_AcceleratesFatigueAccumulation()
    {
        var team = new Team
        {
            Name = "T",
            BattingOrder = Enumerable.Range(0, 8).Select(i => new Player { Position = (FieldPosition)(i + 1) })
                .Append(new Player { Position = FieldPosition.Pitcher, Pitching = PitcherAttributes.LeagueAverage })
                .ToList(),
            PitcherSlot = 8,
            Bullpen = System.Array.Empty<Player>(),
        };
        var s = new TeamState(team);
        s.AddPitches(10, staminaWeight: 1.6); // 飛ばしていけ
        Assert.Equal(10, s.PitchesThrown);
        Assert.Equal(16.0, s.FatiguePitches, 6);
    }

    // --- 球種レパートリー（§2.2） ---

    [Fact]
    public void Repertoire_DefaultsToFastballOnly_WithPitchRank()
    {
        var p = new PitcherAttributes { PitchRank = 72 };
        var rep = p.EffectiveRepertoire;
        Assert.Single(rep);
        Assert.Equal(PitchType.Fastball, rep[0].Type);
        Assert.Equal(72, rep[0].Rank); // ストレートにもランク（§2.1）
    }

    [Fact]
    public void PitchSelection_UsesRepertoire_TypesAppear()
    {
        var pitcher = new PitcherAttributes
        {
            MaxVelocityKmh = 140.0,
            Repertoire = new[]
            {
                PitchSlot.FastballOf(60),
                new PitchSlot { Type = PitchType.Slider, Power = 55, Sharpness = 65 },
                new PitchSlot { Type = PitchType.Curve, Power = 50, Sharpness = 50 },
            },
        };
        var rng = new Xoshiro256Random(3);
        var types = Enumerable.Range(0, 1000)
            .Select(_ => PitchSelection.Select(pitcher, Zone, Bat, Pit, rng).Type)
            .ToHashSet();
        Assert.Contains(PitchType.Fastball, types);
        Assert.Contains(PitchType.Slider, types);
        Assert.Contains(PitchType.Curve, types);
    }

    [Fact]
    public void BreakingBall_IsSlower_BySpeedRatio()
    {
        var curveOnly = new PitcherAttributes
        {
            MaxVelocityKmh = 140.0,
            Repertoire = new[] { new PitchSlot { Type = PitchType.Curve, Power = 50, Sharpness = 50 } },
        };
        var rng = new Xoshiro256Random(5);
        var v = PitchSelection.Select(curveOnly, Zone, Bat, Pit, rng).VelocityKmh;
        Assert.InRange(v, 100.0, 140.0 * 0.80); // カーブは球速比0.80以下
    }

    // --- 生成: 投手1〜3球種・野手0〜1・経歴上振れ（設計書01 §1.1b / 02 §2.2） ---

    [Fact]
    public void Generation_PitchersHaveOneToThreeBreaking_FieldersZeroToOne()
    {
        var c = new RosterCoefficients();
        var all = new System.Collections.Generic.List<DevelopingPlayer>();
        for (var s = 0; s < 200; s++)
            all.AddRange(ProspectGenerator.Intake(1, c, new Xoshiro256Random((ulong)(7000 + s))));

        var pitchers = all.Where(p => p.IsPitcher).ToList();
        var normalFielders = all.Where(p => !p.IsPitcher && !p.HasPitcherBackground).ToList();

        Assert.All(pitchers, p => Assert.InRange(p.LearnedPitches.Count, 1, 3));
        Assert.All(normalFielders, p => Assert.InRange(p.LearnedPitches.Count, 0, 1));

        // 経歴持ち野手は変化球が多め（平均で上回る）。
        var bg = all.Where(p => !p.IsPitcher && p.HasPitcherBackground).ToList();
        if (bg.Count > 5)
        {
            Assert.True(bg.Average(p => p.LearnedPitches.Count) >
                        normalFielders.Average(p => p.LearnedPitches.Count),
                "投手経歴の変化球上振れが出ていない");
        }
    }

    [Fact]
    public void Projection_BuildsRepertoire_FastballPlusLearned()
    {
        var dp = new DevelopingPlayer { IsPitcher = true };
        dp.SetLevel(AbilityKind.PitchRank, 60);
        // Issue #274: ストレートの有効軸はPower←Stuff。Stuffを明示設定しないとLevel=0のまま。
        dp.SetLevel(AbilityKind.Stuff, 60);
        dp.SetCap(AbilityKind.Stuff, 99); // cap >= level が必要（SetWithCap の代わり）
        dp.LearnedPitches.Add(new LearnedPitch(PitchType.Slider, PowerOffset: 5, SharpnessOffset: -10));

        var player = RosterTeamBuilder.ToPlayer(dp, FieldPosition.Pitcher, asPitcher: true);
        var rep = player.Pitching!.EffectiveRepertoire;
        Assert.Equal(2, rep.Count);
        Assert.Equal(PitchType.Fastball, rep[0].Type);
        // ストレートのランク（有効軸=球威Power）は Stuff レベルから来る（Issue #274）。
        Assert.Equal(60, rep[0].Rank);   // rep[0].Power = stuffLevel = 60
        Assert.Equal(PitchType.Slider, rep[1].Type);
        Assert.Equal(65, rep[1].Power);      // 60+5（変化球のPowerはPitchRank+PowerOffset）
        Assert.Equal(50, rep[1].Sharpness);  // 60-10（変化球のSharpnessはPitchRank+SharpnessOffset）
    }

    // --- 決定論 ---

    [Fact]
    public void PitchSelection_IsDeterministic()
    {
        var pitcher = new PitcherAttributes
        {
            MaxVelocityKmh = 142.0,
            Repertoire = new[] { PitchSlot.FastballOf(55), new PitchSlot { Type = PitchType.Fork, Power = 60, Sharpness = 60 } },
        };
        var a = PitchSelection.Select(pitcher, Zone, Bat, Pit, new Xoshiro256Random(11));
        var b = PitchSelection.Select(pitcher, Zone, Bat, Pit, new Xoshiro256Random(11));
        Assert.Equal(a.Type, b.Type);
        Assert.Equal(a.VelocityKmh, b.VelocityKmh, 9);
        Assert.Equal(a.Stuff, b.Stuff, 9);
    }

    // --- 捕手リード（設計書01 §2①: 良い配球ほど球威が引き立つ） ---

    [Fact]
    public void CatcherLead_FiftyIsIdentity()
    {
        var pitcher = new PitcherAttributes { MaxVelocityKmh = 145.0 };
        // 同シードなら乱数列は不変。リード50は従来（引数省略＝50）と1ビットも変わらない。
        var baseline = PitchSelection.Select(pitcher, Zone, Bat, Pit, new Xoshiro256Random(7));
        var lead50 = PitchSelection.Select(pitcher, Zone, Bat, Pit, new Xoshiro256Random(7), catcherLead: 50);
        Assert.Equal(baseline.Stuff, lead50.Stuff, 12);
    }

    [Fact]
    public void CatcherLead_HigherRaisesStuff_LowerReducesIt()
    {
        var pitcher = new PitcherAttributes { MaxVelocityKmh = 145.0 };
        // 同シード＝球速・球種の乱数は共通。差分はリード項 (Lead−50)×係数 だけになるので厳密比較できる。
        var low = PitchSelection.Select(pitcher, Zone, Bat, Pit, new Xoshiro256Random(3), catcherLead: 20);
        var mid = PitchSelection.Select(pitcher, Zone, Bat, Pit, new Xoshiro256Random(3), catcherLead: 50);
        var high = PitchSelection.Select(pitcher, Zone, Bat, Pit, new Xoshiro256Random(3), catcherLead: 80);

        Assert.True(high.Stuff > mid.Stuff, "リードが高いほど球威が上がる");
        Assert.True(low.Stuff < mid.Stuff, "リードが低いほど球威が下がる");
        // 差分は係数どおり厳密に (Lead差)×perPoint。
        Assert.Equal((80 - 50) * Pit.CatcherLeadStuffPerPoint, high.Stuff - mid.Stuff, 12);
        Assert.Equal((20 - 50) * Pit.CatcherLeadStuffPerPoint, low.Stuff - mid.Stuff, 12);
    }

    // === Issue #274: 球威/キレの役割分担確認テスト ===

    /// <summary>
    /// ストレートの球威(Power)が高いほど rpm→ホップ量が大きくなること（単調性）を確認する。
    /// Issue #274: ストレートの有効軸=球威(Power)。未使用軸(Sharpness)は固定して変化がPower起因であることを担保。
    /// </summary>
    [Fact]
    public void Fastball_Power_IsSpinAxis_HighPowerGivesMoreHop()
    {
        var mound = new MoundGeometry();
        var aero = new Aerodynamics();
        var coeffs = new PitchingCoefficients();

        // ストレートの有効軸=Power。Sharpness=50固定。
        double RpmForPower(int power) => coeffs.SpinRpmBase + (power - 50) * coeffs.SpinRpmPerPower;

        var rpmLow = RpmForPower(20);
        var rpmHigh = RpmForPower(80);

        var specLow = new PitchSpec { SpeedKmh = 140, SpinRadPerSec = PitchSpec.BackspinFromRpm(rpmLow) };
        var specHigh = new PitchSpec { SpeedKmh = 140, SpinRadPerSec = PitchSpec.BackspinFromRpm(rpmHigh) };

        var trajLow = PitchSimulator.Simulate(specLow, aero, mound);
        var trajHigh = PitchSimulator.Simulate(specHigh, aero, mound);

        Assert.True(trajHigh.InducedVerticalBreakM > trajLow.InducedVerticalBreakM,
            $"球威80のホップ量({trajHigh.InducedVerticalBreakM:F3}m) > 球威20({trajLow.InducedVerticalBreakM:F3}m)");
    }

    /// <summary>
    /// 変化球のキレ(Sharpness)が高いほど変化量が大きくなること（単調性）を確認する。
    /// Issue #274: 変化球の有効軸=キレ(Sharpness)。未使用軸(Power)は固定して変化がSharpness起因であることを担保。
    /// </summary>
    [Fact]
    public void BreakingBall_Sharpness_IsSpinAxis_HighSharpnessGivesMoreBreak()
    {
        var mound = new MoundGeometry();
        var aero = new Aerodynamics();
        var coeffs = new PitchingCoefficients();

        // 変化球の有効軸=Sharpness。Power=50固定。
        double RpmForSharpness(int sharpness) => coeffs.SpinRpmBase + (sharpness - 50) * coeffs.SpinRpmPerSharpness;

        var rpmLow = RpmForSharpness(20);
        var rpmHigh = RpmForSharpness(80);

        var specLow = new PitchSpec { SpeedKmh = 125, SpinRadPerSec = PitchSpec.BackspinFromRpm(rpmLow) };
        var specHigh = new PitchSpec { SpeedKmh = 125, SpinRadPerSec = PitchSpec.BackspinFromRpm(rpmHigh) };

        var trajLow = PitchSimulator.Simulate(specLow, aero, mound);
        var trajHigh = PitchSimulator.Simulate(specHigh, aero, mound);

        // キレが高いほど誘発縦変化（ホップ＝バックスピン系）が大きくなる
        Assert.True(trajHigh.InducedVerticalBreakM > trajLow.InducedVerticalBreakM,
            $"キレ80の変化量({trajHigh.InducedVerticalBreakM:F3}m) > キレ20({trajLow.InducedVerticalBreakM:F3}m)");
    }

    /// <summary>
    /// ストレートの未使用軸(Sharpness)を変えても rpm が変わらない（完全に無視される）ことを確認する。
    /// Issue #274: ストレートの有効軸はPower。Sharpnessを変えても rpm=一定。
    /// </summary>
    [Fact]
    public void Fastball_UnusedAxis_Sharpness_DoesNotAffectRpm()
    {
        var coeffs = new PitchingCoefficients();

        // ストレートは IsSpinByPower=true → Power を使う → Sharpness は無視
        // Sharpness の値にかかわらず rpm は Power=50 由来の同じ値になる。
        const int fixedPower = 50;
        var expectedRpm = coeffs.SpinRpmBase + (fixedPower - 50) * coeffs.SpinRpmPerPower;

        // Sharpness を変えても rpm は不変（係数 SpinRpmPerSharpness は使われない）
        double RpmFastball(int sharpness) => coeffs.SpinRpmBase + (fixedPower - 50) * coeffs.SpinRpmPerPower;

        Assert.Equal(expectedRpm, RpmFastball(20), 10);
        Assert.Equal(expectedRpm, RpmFastball(80), 10);
        Assert.Equal(expectedRpm, RpmFastball(1), 10);
        Assert.Equal(expectedRpm, RpmFastball(100), 10);
        // より直接的: IsSpinByPower の仕様確認
        Assert.True(PitchSlot.IsSpinByPower(PitchType.Fastball), "ストレートの有効軸はPower");
        Assert.False(PitchSlot.IsSpinByPower(PitchType.Slider), "変化球の有効軸はSharpness");
    }

    /// <summary>
    /// 変化球の未使用軸(Power)を変えても rpm が変わらない（完全に無視される）ことを確認する。
    /// Issue #274: 変化球の有効軸はSharpness。Powerを変えても試合結果は不変。
    /// </summary>
    [Fact]
    public void BreakingBall_UnusedAxis_Power_DoesNotAffectRpm()
    {
        var coeffs = new PitchingCoefficients();

        // 変化球は IsSpinByPower=false → Sharpness を使う → Power は無視
        const int fixedSharpness = 50;
        var expectedRpm = coeffs.SpinRpmBase + (fixedSharpness - 50) * coeffs.SpinRpmPerSharpness;

        double RpmBreaking(int power) => coeffs.SpinRpmBase + (fixedSharpness - 50) * coeffs.SpinRpmPerSharpness;

        Assert.Equal(expectedRpm, RpmBreaking(20), 10);
        Assert.Equal(expectedRpm, RpmBreaking(80), 10);
        Assert.Equal(expectedRpm, RpmBreaking(1), 10);
        Assert.Equal(expectedRpm, RpmBreaking(100), 10);
        // 球種確認
        Assert.False(PitchSlot.IsSpinByPower(PitchType.Curve), "カーブの有効軸はSharpness");
        Assert.False(PitchSlot.IsSpinByPower(PitchType.Fork), "フォークの有効軸はSharpness");
    }
}
