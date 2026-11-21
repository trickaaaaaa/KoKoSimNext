using KokoSim.Config;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Balance;
using Xunit;

namespace KokoSim.Engine.Tests.Config;

/// <summary>
/// 表示能力→物理層の効き（設計書21・<see cref="AbilityEffectCoefficients"/>）の配管を固定する。
/// 正典は YAML の <c>ability_effect:</c> 1ブロックで、ローダが batting/fielding/baserunning の3ブロックへ同じ値を配る＝
/// 3つが食い違うと「走者は寝ているのに野手は寝ていない」世界になるので、YAML 読込・C#既定・GameContext 既定の3経路で一致を確認する。
/// </summary>
public sealed class AbilityEffectConsistencyTests
{
    [Fact]
    public void LoadedBundle_DistributesOneBlockToThreeDomains()
    {
        var b = CoefficientsLoader.LoadFromFile(BalanceRegressionTests.FindDataFile("coefficients.yaml"));
        Assert.Equal(b.Batting.AbilityEffect, b.Fielding.AbilityEffect);
        Assert.Equal(b.Batting.AbilityEffect, b.Baserunning.AbilityEffect);
    }

    [Fact]
    public void Defaults_AreConsistentAcrossDomains_AndGameContext()
    {
        var ctx = new GameContext();
        Assert.Equal(new AbilityEffectCoefficients(), ctx.Batting.AbilityEffect);
        Assert.Equal(ctx.Batting.AbilityEffect, ctx.Fielding.AbilityEffect);
        Assert.Equal(ctx.Batting.AbilityEffect, ctx.Baserunning.AbilityEffect);
    }

    /// <summary>
    /// 外野手の守備の上位の頭打ち（issue #976 追補・オーナー決定 ③）: 膝より下は内野手（頭打ちなし）と1ビット一致、上は伸びが
    /// 逓減するが単調増加（守備が上がれば物理の値も下がらない＝能力の向きは保つ）。内野手・肩は頭打ちしない。
    /// </summary>
    [Fact]
    public void OutfieldDefenseTopKnee_IsIdentityBelowTheKnee_MonotoneAbove_AndOutfieldOnly()
    {
        var e = new AbilityEffectCoefficients();
        for (var lv = 1; lv <= (int)e.OutfieldDefenseTopKnee; lv++) Assert.Equal(e.Fielding(lv), e.OutfieldFielding(lv));
        for (var lv = 2; lv <= 100; lv++) Assert.True(e.OutfieldFielding(lv) >= e.OutfieldFielding(lv - 1), $"守備 {lv}");
        Assert.True(e.OutfieldFielding(90) < e.Fielding(90));
        Assert.True(e.OutfieldFielding(90) > e.OutfieldFielding(70));

        Player Star(FieldPosition pos) => new() { Position = pos, Fielding = 90, Catching = 90, ThrowAccuracy = 90, ArmStrength = 90 };
        var of = Star(FieldPosition.CenterField).ToFielder(e);
        var inf = Star(FieldPosition.Shortstop).ToFielder(e);
        Assert.Equal(e.OutfieldFielding(90), of.Fielding);
        Assert.Equal(e.Fielding(90), inf.Fielding);
        Assert.Equal(inf.ArmStrength, of.ArmStrength);   // 肩は頭打ちしない（#764）
    }

    /// <summary>
    /// 効き0.50は Pivot からの差が奇数のとき必ず.5になる。既定の<c>Math.Round</c>は偶数丸めで、
    /// 「3段同じ・1段上がる」の不均一な刻みができ、育成が丸ごと効かない区間が出る（issue #1211）。
    /// 期待は Lv 2段ごとに均一に+1（<c>MidpointRounding.AwayFromZero</c>）。
    /// </summary>
    [Fact]
    public void Scale_HalfIntegerOffsetFromPivot_RoundsAwayFromZero_NotBankersRounding()
    {
        var half = new AbilityEffectCoefficients { BattingScale = 0.5 };
        // 奇数lvと次の偶数lvが必ず同値（2段ペア）。3段同値になる偏りが無い＝育成が必ず動く。
        for (var lv = 49; lv <= 59; lv += 2)
            Assert.Equal(half.Batting(lv), half.Batting(lv + 1));
        Assert.NotEqual(half.Batting(53), half.Batting(55)); // 旧コードは52,52で同値になっていた
        Assert.Equal(52, half.Batting(53));
        Assert.Equal(53, half.Batting(55));
    }

    [Fact]
    public void Identity_LeavesAbilitiesUntouched_AndPivotIsFixedPoint()
    {
        var id = AbilityEffectCoefficients.Identity;
        for (var lv = 1; lv <= 100; lv++)
        {
            Assert.Equal(lv, id.Batting(lv));
            Assert.Equal(lv, id.Fielding(lv));
            Assert.Equal(lv, id.Arm(lv));
            Assert.Equal(lv, id.Running(lv));
            Assert.Equal(lv, id.Pitching(lv));
        }
        Assert.Equal(146.4, id.PitchingKmh(146.4), 12);
        var half = new AbilityEffectCoefficients { BattingScale = 0.5, FieldingScale = 0.5, RunningScale = 0.5, PitchingScale = 0.5 };
        Assert.Equal(50, half.Batting(50));
        Assert.Equal(60, half.Batting(70));
        Assert.Equal(40, half.Fielding(30));
        Assert.Equal(75, half.Running(100));
        Assert.Equal(70, half.Pitching(90));
        // 球速の反応は Lv50 の最速（132km/h）を軸に km/h で寝る。
        Assert.Equal(132.0, half.PitchingKmh(132.0), 9);
        Assert.Equal(139.0, half.PitchingKmh(146.0), 9);
        Assert.Equal(127.0, half.PitchingKmh(122.0), 9);
    }

    [Fact]
    public void Pitcher_ScalesControlAndRanks_BakesReactionOffset_KeepsKmhAndStamina()
    {
        var p = new PitcherAttributes
        {
            MaxVelocityKmh = 146.0, Control = 80, PitchRank = 70, StaminaPitches = 130.0,
            Repertoire = new[] { PitchSlot.FastballOf(80), new PitchSlot { Type = PitchType.Slider, Power = 40, Sharpness = 90 } },
        };
        var half = new AbilityEffectCoefficients { PitchingScale = 0.5 };
        var e = half.Pitcher(p);
        Assert.Equal(146.0, e.MaxVelocityKmh, 9);            // 実球速（画面に出る）は据え置き
        Assert.Equal(130.0, e.StaminaPitches, 9);            // 目安球数（画面に出る）も据え置き
        Assert.Equal(65, e.Control);
        Assert.Equal(60, e.PitchRank);
        Assert.Equal(65, e.Repertoire![0].Power);
        Assert.Equal(45, e.Repertoire[1].Power);
        Assert.Equal(70, e.Repertoire[1].Sharpness);
        Assert.Equal(139.0 - 146.0, e.ReactionVelocityOffsetKmh, 9);   // 反応は 139km/h 相当（差分で持つ）
        // 恒等は同一インスタンス（従来と1ビット一致）
        Assert.Same(p, AbilityEffectCoefficients.Identity.Pitcher(p));
        // 平均投手（Lv50 相当）は既定の効きでもオフセット0＝帯の平均は動かない
        Assert.Equal(0.0, new AbilityEffectCoefficients().Pitcher(PitcherAttributes.LeagueAverage).ReactionVelocityOffsetKmh, 9);
    }

    [Fact]
    public void ToBatter_ToFielder_ApplyScales_ButNotLaunch()
    {
        var p = new Player
        {
            Name = "x", Position = KokoSim.Engine.Match.Field.FieldPosition.Shortstop,
            Contact = 70, Power = 70, Discipline = 30, LaunchTendency = 80, Speed = 70,
            ArmStrength = 30, ThrowAccuracy = 70, Fielding = 90, Catching = 10, Lead = 80,
        };
        var e = new AbilityEffectCoefficients { BattingScale = 0.5, FieldingScale = 0.5, RunningScale = 0.5, PitchingScale = 0.5 };
        var b = p.ToBatter(e);
        Assert.Equal(60, b.Contact); Assert.Equal(60, b.Power); Assert.Equal(40, b.Discipline);
        Assert.Equal(80, b.LaunchTendency);   // 弾道は「型」＝寝かせない
        Assert.Equal(60, b.Speed);
        var f = p.ToFielder(e);
        Assert.Equal(30, f.ArmStrength);      // 肩は ArmScale（既定1.0＝寝かせない・issue #764）
        Assert.Equal(60, f.ThrowAccuracy); Assert.Equal(70, f.Fielding); Assert.Equal(30, f.Catching);
        Assert.Equal(65, f.Lead);             // 捕手リードは投手側の効き（PitchingScale）で寝る
        Assert.Equal(60, f.Speed);
        // 恒等はビット一致
        Assert.Equal(p.ToBatter(), p.ToBatter(AbilityEffectCoefficients.Identity));
        Assert.Equal(p.ToFielder(), p.ToFielder(AbilityEffectCoefficients.Identity));
    }
}
