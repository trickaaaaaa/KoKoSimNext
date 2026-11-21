using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Config;

/// <summary>
/// 表示能力の物差しの下げ幅 <see cref="AbilityEffectCoefficients.Delta"/>（issue #1080・PR-A）の配管を固定する。
///
/// <para>PR-A は配線のみ（既定 Delta=0・生成側はまだ表示Lvを下げていない）なので、ここで固定するのは2つ:
/// (1) Delta=0 は現状と1ビット一致（配線しても何も動かない）。
/// (2) <b>漏れ検出</b>: 表示Lvを −Delta した上で Delta 付きの係数を通した結果は、−Delta も Delta も無しで
/// 素の Lv を通した結果と一致する（＝入口を通らず生の Lv を読む場所があれば、ここのどちらかの等式が崩れて赤くなる）。
/// 物理層の4入口（<see cref="Player.ToBatter(AbilityEffectCoefficients)"/>／<see cref="Player.ToFielder(AbilityEffectCoefficients)"/>／
/// <see cref="AbilityEffectCoefficients.Pitcher(PitcherAttributes)"/>／<see cref="AbilityEffectCoefficients.Pitching(int)"/> 等の
/// <see cref="AbilityEffectCoefficients.Scale"/> 系ヘルパ）だけを対象にする（design-21・PR-A の範囲）。</para>
/// </summary>
public sealed class AbilityScaleDeltaTests
{
    private static readonly int[] Levels = { 1, 10, 30, 49, 50, 51, 70, 93, 100 };
    private static readonly double[] Deltas = { 0.0, 3.0, 5.0, 7.0, 7.4 };
    private static readonly double[] Scales = { 1.0, 0.41, 0.5 };

    [Fact]
    public void Delta_DefaultsToZero_AndIsANoOp()
    {
        var e = new AbilityEffectCoefficients();
        Assert.Equal(0.0, e.Delta);
        var zero = e with { Delta = 0.0 };
        foreach (var lv in Levels)
        {
            Assert.Equal(e.Batting(lv), zero.Batting(lv));
            Assert.Equal(e.Fielding(lv), zero.Fielding(lv));
            Assert.Equal(e.OutfieldFielding(lv), zero.OutfieldFielding(lv));
            Assert.Equal(e.Arm(lv), zero.Arm(lv));
            Assert.Equal(e.Running(lv), zero.Running(lv));
            Assert.Equal(e.Pitching(lv), zero.Pitching(lv));
        }
    }

    /// <summary>
    /// 核心のテスト: 表示Lvを −Delta した上で Delta 付き係数を通す（＝PR-B が生成でやること）結果は、
    /// 下げも戻しも無しで素の Lv を通した結果と一致する（＝物理・帯・勝率は Delta の値によらず不変）。
    /// 天井・下限（1〜100）に当たると丸めで崩れるので、クランプしない範囲（11〜89 かつ −Delta後も1〜100内）だけを見る。
    /// </summary>
    [Fact]
    public void LoweringDisplayByDelta_ThenAddingItBackAtTheEntry_MatchesTheUnshiftedResult()
    {
        foreach (var scale in Scales)
        foreach (var delta in Deltas)
        {
            var baseline = new AbilityEffectCoefficients { BattingScale = scale, FieldingScale = scale, ArmScale = scale, RunningScale = scale, PitchingScale = scale };
            var withDelta = baseline with { Delta = delta };
            foreach (var lv in Levels)
            {
                var lowered = (int)System.Math.Round(lv - delta);
                if (lowered < 11 || lowered > 89) continue; // 頭打ち・下限の丸めは対象外（design-21 の既知差）

                Assert.Equal(baseline.Batting(lv), withDelta.Batting(lowered));
                Assert.Equal(baseline.Fielding(lv), withDelta.Fielding(lowered));
                Assert.Equal(baseline.Arm(lv), withDelta.Arm(lowered));
                Assert.Equal(baseline.Running(lv), withDelta.Running(lowered));
                Assert.Equal(baseline.Pitching(lv), withDelta.Pitching(lowered));
            }
        }
    }

    [Fact]
    public void LoweringDisplayByDelta_ThenAddingItBack_MatchesForOutfieldKnee()
    {
        var baseline = new AbilityEffectCoefficients();
        foreach (var delta in Deltas)
        {
            var withDelta = baseline with { Delta = delta };
            foreach (var lv in Levels)
            {
                var lowered = (int)System.Math.Round(lv - delta);
                if (lowered < 11 || lowered > 89) continue;
                Assert.Equal(baseline.OutfieldFielding(lv), withDelta.OutfieldFielding(lowered));
            }
        }
    }

    [Fact]
    public void Pitcher_HonorsDelta_ControlAndPitchRankAndRepertoire()
    {
        var p = new PitcherAttributes
        {
            MaxVelocityKmh = 146.0, Control = 65, PitchRank = 60, StaminaPitches = 130.0,
            Repertoire = new[] { PitchSlot.FastballOf(65) },
        };
        var baseline = new AbilityEffectCoefficients { PitchingScale = 0.5 };
        var delta = 7.0;
        var shifted = p with { Control = 58, PitchRank = 53, Repertoire = new[] { PitchSlot.FastballOf(58) } };
        var withDelta = baseline with { Delta = delta };

        var expected = baseline.Pitcher(p);
        var actual = withDelta.Pitcher(shifted);
        Assert.Equal(expected.Control, actual.Control);
        Assert.Equal(expected.PitchRank, actual.PitchRank);
        Assert.Equal(expected.Repertoire![0].Power, actual.Repertoire![0].Power);
        Assert.Equal(expected.MaxVelocityKmh, actual.MaxVelocityKmh, 9);       // 球速は Delta の対象外（画面に出る単位）
        Assert.Equal(expected.StaminaPitches, actual.StaminaPitches, 9);       // 目安球数も対象外
    }

    [Fact]
    public void ToBatter_ToFielder_HonorDelta()
    {
        Player Make(int contact, int power, int discipline, int speed, int arm, int throwAcc, int fielding, int catching) => new()
        {
            Name = "x", Position = FieldPosition.Shortstop,
            Contact = contact, Power = power, Discipline = discipline, Speed = speed,
            ArmStrength = arm, ThrowAccuracy = throwAcc, Fielding = fielding, Catching = catching,
        };

        var baseline = new AbilityEffectCoefficients { BattingScale = 0.5, FieldingScale = 0.5, RunningScale = 0.5 };
        var p = Make(70, 60, 30, 65, 40, 55, 80, 45);
        var delta = 5.0;
        var shifted = Make(65, 55, 25, 60, 35, 50, 75, 40);
        var withDelta = baseline with { Delta = delta };

        var batterExpected = p.ToBatter(baseline);
        var batterActual = shifted.ToBatter(withDelta);
        Assert.Equal(batterExpected.Contact, batterActual.Contact);
        Assert.Equal(batterExpected.Power, batterActual.Power);
        Assert.Equal(batterExpected.Discipline, batterActual.Discipline);
        Assert.Equal(batterExpected.Speed, batterActual.Speed);

        var fielderExpected = p.ToFielder(baseline);
        var fielderActual = shifted.ToFielder(withDelta);
        Assert.Equal(fielderExpected.ArmStrength, fielderActual.ArmStrength);
        Assert.Equal(fielderExpected.ThrowAccuracy, fielderActual.ThrowAccuracy);
        Assert.Equal(fielderExpected.Fielding, fielderActual.Fielding);
        Assert.Equal(fielderExpected.Catching, fielderActual.Catching);
        Assert.Equal(fielderExpected.Speed, fielderActual.Speed);
    }
}
