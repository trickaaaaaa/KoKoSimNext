using System;
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
        // 目安の onset（既定60%）までは無減衰。目安到達時は係数どおり（−2km/h・−5）＝FatigueCurveTests 参照
        // （issue fatigue-gradual-before-threshold で「目安まで完全ゼロ」の階段型は廃止）。
        var fresh = PitchingFatigue.Effective(p, 80.0 * new FatigueCoefficients().OnsetRatio, new FatigueCoefficients());
        Assert.Equal(140.0, fresh.MaxVelocityKmh);

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
        // issue #408: 切片63・傾き0.75。Level 50 → 100.5球（D帯＝既定値）/ Level 100 → 138球（変換は一箇所に集約）。
        Assert.Equal(100.5, PitcherAttributes.StaminaPitchesFromLevel(50), 6);
        Assert.Equal(138.0, PitcherAttributes.StaminaPitchesFromLevel(100), 6);
        // 既定の StaminaPitches は Lv50 の目安投球数（D帯）と一致する。
        Assert.Equal(PitcherAttributes.StaminaPitchesFromLevel(50), new PitcherAttributes().StaminaPitches, 6);
    }

    [Fact]
    public void StaminaConversion_SatisfiesIssue408Bands()
    {
        // issue #408 完了条件: クランプ下限(Lv10)≥70球 / 生成平均(Lv45)∈[95,100] / クランプ上限(Lv92)∈[120,140]。
        Assert.True(PitcherAttributes.StaminaPitchesFromLevel(10) >= 70.0,
            $"下限 Lv10 = {PitcherAttributes.StaminaPitchesFromLevel(10)}球 は 70球以上であること");
        var mean = PitcherAttributes.StaminaPitchesFromLevel(45);
        Assert.InRange(mean, 95.0, 100.0);
        var top = PitcherAttributes.StaminaPitchesFromLevel(92);
        Assert.InRange(top, 120.0, 140.0);
    }

    [Fact]
    public void StaminaConversion_RoundTrips_ForwardThenInverse()
    {
        // 順変換→逆変換が全等級で元のLevelに戻る（切片・傾きを別々に持つとずれる, issue #408）。
        for (var lv = 1; lv <= 100; lv++)
            Assert.Equal(lv, PitcherAttributes.LevelFromStaminaPitches(PitcherAttributes.StaminaPitchesFromLevel(lv)));
    }

    [Fact]
    public void LevelFromStaminaPitches_ExtrapolatesAbove100_NoUpperCap()
    {
        // issue #356/#408: スタミナは上限なし。Lv100=138球で頭打ちにならず外挿する（逆変換 (138-63)/0.75 = 100）。
        Assert.Equal(100, PitcherAttributes.LevelFromStaminaPitches(138.0));
        Assert.Equal(150, PitcherAttributes.LevelFromStaminaPitches(63.0 + 150 * 0.75));
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

    // --- 隅を狙う（issue walk-rate-floor-vs-elite-pitcher） ---

    [Fact]
    public void HigherControl_SpreadsTheAim_TowardTheCorners_And50IsIdentity()
    {
        // 狙い散布σは max(0.5, 1 + slope×(Control−50)) 倍。C50 は恒等・C90 は広く・C30 は狭い（真ん中を狙う）・乱数消費は同じ。
        double AimSpread(int control, ulong seed)
        {
            var pitcher = new PitcherAttributes { MaxVelocityKmh = 140.0, Control = control };
            var rng = new Xoshiro256Random(seed);
            var xs = Enumerable.Range(0, 3000).Select(_ => PitchSelection.Select(pitcher, Zone, Bat, Pit, rng).AimX).ToList();
            var mean = xs.Average();
            return Math.Sqrt(xs.Sum(x => (x - mean) * (x - mean)) / xs.Count);
        }
        var c30 = AimSpread(30, 9);
        var c50 = AimSpread(50, 9);
        var c90 = AimSpread(90, 9);
        Assert.InRange(c50, Bat.AimSigmaXMeters * 0.95, Bat.AimSigmaXMeters * 1.05);
        var expected90 = Bat.AimSigmaXMeters * (1.0 + Bat.AimSpreadControlSlopePerPoint * 40);
        var expected30 = Bat.AimSigmaXMeters * System.Math.Max(0.5, 1.0 - Bat.AimSpreadControlSlopePerPoint * 20);
        Assert.InRange(c90, expected90 * 0.95, expected90 * 1.05);
        Assert.InRange(c30, expected30 * 0.95, expected30 * 1.05);
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
            Reserves = System.Array.Empty<Player>(),
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

        var pitchers = all.Where(p => PitcherRoleResolver.LeansPitcher(p)).ToList();
        var fielders = all.Where(p => !PitcherRoleResolver.LeansPitcher(p)).ToList();

        // #377: 役割ゲートを撤去したので「投手は必ず1本以上」の断崖は無い。本数は投手適性の連続量で決まる。
        Assert.All(all, p => Assert.InRange(p.LearnedPitches.Count, 0, 3));

        // 投手として読める選手ほど多く持つ（境目で跳ねずに増える）。
        Assert.True(pitchers.Average(p => p.LearnedPitches.Count) >
                    fielders.Average(p => p.LearnedPitches.Count) + 0.5,
            "投手の変化球本数が野手を明確に上回っていない");
        // 断崖は無いが、変化球を1本も持たない投手は例外的（＝適性が判定境界すれすれの個体）に留まる。
        var noBreaking = pitchers.Count(p => p.LearnedPitches.Count == 0) / (double)pitchers.Count;
        Assert.True(noBreaking < 0.10, $"変化球ゼロの投手が多すぎる: {noBreaking:P1}");

        // 投手適性が高い野手ほど変化球を持ちやすい（旧「投手経歴」フラグの代わりに連続量が担う, #366b）。
        var apt = fielders.OrderByDescending(p => p.Aptitude(FieldPosition.Pitcher)).ToList();
        var topQuartile = apt.Take(apt.Count / 4).Average(p => p.LearnedPitches.Count);
        var bottomQuartile = apt.Skip(apt.Count * 3 / 4).Average(p => p.LearnedPitches.Count);
        Assert.True(topQuartile > bottomQuartile,
            $"投手適性の高い野手が変化球を持ちやすくなっていない（上位{topQuartile:F2} vs 下位{bottomQuartile:F2}）");
    }

    [Fact]
    public void Projection_BuildsRepertoire_FastballPlusLearned()
    {
        var dp = new DevelopingPlayer();
        PitcherRoleResolver.SeedAptitudeForRole(dp, true);
        dp.SetLevel(AbilityKind.PitchRank, 60);
        // Issue #274: ストレートの有効軸はPower←Stuff。Stuffを明示設定しないとLevel=0のまま。
        dp.SetLevel(AbilityKind.Stuff, 60);
        dp.SetCap(AbilityKind.Stuff, 99); // cap >= level が必要（SetWithCap の代わり）
        dp.LearnedPitches.Add(new LearnedPitch(PitchType.Slider, PowerOffset: 5, SharpnessOffset: -10));

        var player = RosterTeamBuilder.ToPlayer(dp, FieldPosition.Pitcher);
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

    // --- 捕手リード→配球選択（設計書01 §2①: リードが50から離れたぶん投手の強い/弱い軸へ寄る） ---

    [Fact]
    public void CatcherLead_Fifty_PitchSelection_IsUnchanged()
    {
        // 球威A(≈90)・キレF(≈15)相当の非対称レパートリー。ちょうどLead50では
        // 配球のRNG消費列も含め1ビットも変わらないことを固定する（帯不変の担保）。
        var pitcher = new PitcherAttributes
        {
            MaxVelocityKmh = 147.0,
            Repertoire = new[]
            {
                new PitchSlot { Type = PitchType.Fastball, Power = 90, Sharpness = 90 },
                new PitchSlot { Type = PitchType.Slider, Power = 50, Sharpness = 15 },
                new PitchSlot { Type = PitchType.Curve, Power = 50, Sharpness = 15 },
            },
        };

        var baseline = PitchSelection.Select(pitcher, Zone, Bat, Pit, new Xoshiro256Random(42));
        var withLead = PitchSelection.Select(pitcher, Zone, Bat, Pit, new Xoshiro256Random(42), catcherLead: 50);
        Assert.Equal(baseline.Type, withLead.Type);
        Assert.Equal(baseline.VelocityKmh, withLead.VelocityKmh, 9);
    }

    [Fact]
    public void CatcherLead_ShiftsStraightShare_TowardStrongerAxis_BothDirections()
    {
        static double FastballShare(PitcherAttributes p, int lead, ulong seed)
        {
            var rng = new Xoshiro256Random(seed);
            const int n = 20000;
            var fb = 0;
            for (var i = 0; i < n; i++)
            {
                if (PitchSelection.Select(p, Zone, Bat, Pit, rng, catcherLead: lead).Type == PitchType.Fastball) fb++;
            }
            return fb / (double)n;
        }

        // 球威A・キレF: ストレートが圧倒的に強い側。
        var strongFastball = new PitcherAttributes
        {
            MaxVelocityKmh = 147.0,
            Repertoire = new[]
            {
                new PitchSlot { Type = PitchType.Fastball, Power = 90, Sharpness = 90 },
                new PitchSlot { Type = PitchType.Slider, Power = 50, Sharpness = 15 },
            },
        };
        var fbShareAvg = FastballShare(strongFastball, 50, 1234);
        var fbShareHigh = FastballShare(strongFastball, 90, 1234);
        var fbShareLow = FastballShare(strongFastball, 10, 1234);
        // 高リードほど強い軸（ストレート）へ寄り、低リードほど逆に弱い軸（変化球）へ寄る（対称）。
        Assert.True(fbShareHigh > fbShareAvg + 0.02,
            $"高リードでストレート比率が上がっていない: avg={fbShareAvg:P1} high={fbShareHigh:P1}");
        Assert.True(fbShareLow < fbShareAvg - 0.02,
            $"低リードでストレート比率が下がっていない: avg={fbShareAvg:P1} low={fbShareLow:P1}");

        // 対称性: 変化球の方が強い投手では、高リードでストレート比率が下がる（＝良い変化球を活かす）。
        var strongBreaking = new PitcherAttributes
        {
            MaxVelocityKmh = 130.0,
            Repertoire = new[]
            {
                new PitchSlot { Type = PitchType.Fastball, Power = 30, Sharpness = 30 },
                new PitchSlot { Type = PitchType.Fork, Power = 50, Sharpness = 90 },
            },
        };
        var breakingShareAvg = FastballShare(strongBreaking, 50, 5678);
        var breakingShareHigh = FastballShare(strongBreaking, 90, 5678);
        Assert.True(breakingShareHigh < breakingShareAvg - 0.02,
            $"変化球優位の投手で高リードのストレート比率が下がっていない: avg={breakingShareAvg:P1} high={breakingShareHigh:P1}");
    }

    [Fact]
    public void CatcherLead_High_PrefersSharperBreakingBall_AmongMultiple()
    {
        var pitcher = new PitcherAttributes
        {
            MaxVelocityKmh = 140.0,
            Repertoire = new[]
            {
                PitchSlot.FastballOf(60),
                new PitchSlot { Type = PitchType.Slider, Power = 50, Sharpness = 20 },
                new PitchSlot { Type = PitchType.Curve, Power = 50, Sharpness = 80 },
            },
        };

        static double CurveShareAmongBreaking(PitcherAttributes p, int lead, ulong seed)
        {
            var rng = new Xoshiro256Random(seed);
            const int n = 20000;
            int curve = 0, slider = 0;
            for (var i = 0; i < n; i++)
            {
                var t = PitchSelection.Select(p, Zone, Bat, Pit, rng, catcherLead: lead).Type;
                if (t == PitchType.Curve) curve++;
                else if (t == PitchType.Slider) slider++;
            }
            return curve / (double)(curve + slider);
        }

        var atAverage = CurveShareAmongBreaking(pitcher, 50, 99);
        var atHigh = CurveShareAmongBreaking(pitcher, 95, 99);
        Assert.InRange(atAverage, 0.45, 0.55); // 50以下は従来どおり一様抽選（≈50/50）
        Assert.True(atHigh > 0.65, $"高リードでキレの高い変化球へ寄っていない: {atHigh:P1}");
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
