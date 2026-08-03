using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// スタミナ上限撤廃（issue #356）。スタミナ（AbilityKind.Stamina）だけ才能上限の天井（99）を外し、
/// 育成で Lv100 を超えて伸び、目安投球数（StaminaPitches）が135球を超えられることを検証する。
/// 他能力は従来どおり上限が効くことの回帰も併せて確認する（スタミナだけ例外＝作りの担保）。
/// </summary>
public sealed class StaminaUncapTests
{
    private static readonly RosterCoefficients C = new();
    private static readonly TrainingCoefficients Tc = new();

    /// <summary>複数シードで新入生を大量生成した母集団。</summary>
    private static List<DevelopingPlayer> Cohort(int seeds = 200)
    {
        var all = new List<DevelopingPlayer>();
        for (var s = 0; s < seeds; s++)
            all.AddRange(ProspectGenerator.Intake(1, C, new Xoshiro256Random((ulong)(4000 + s))));
        return all;
    }

    [Fact]
    public void Generation_StaminaCap_CanExceed99_OtherAbilitiesCannot()
    {
        var cohort = Cohort();

        // スタミナだけ天井（99）を外したので、素質の高い個体は Cap(Stamina)>99 を持てる（issue #356 ①④）。
        Assert.Contains(cohort, p => p.Cap(AbilityKind.Stamina) > 99);

        // 他能力（野手8＋投手のうちスタミナ以外）は従来どおり才能上限が99で頭打ち＝回帰。
        var capped = new[]
        {
            AbilityKind.Contact, AbilityKind.Power, AbilityKind.Discipline, AbilityKind.Speed,
            AbilityKind.ArmStrength, AbilityKind.Fielding, AbilityKind.Catching,
            AbilityKind.Velocity, AbilityKind.Control, AbilityKind.PitchRank, AbilityKind.Stuff,
        };
        foreach (var p in cohort)
            foreach (var k in capped)
                Assert.True(p.Cap(k) <= 99, $"{k} の才能上限が99を超えている（スタミナ以外は据え置きのはず）: {p.Cap(k)}");
    }

    [Fact]
    public void Training_StaminaGrowsAbove100_AndProjectsBeyond135Pitches()
    {
        // 天井を外したことで到達可能になった高い才能上限（120）を、育成で Lv100 超まで伸ばす。
        var p = new DevelopingPlayer { GrowthType = GrowthType.Standard };
        p.SetLevel(AbilityKind.Stamina, 95);
        p.SetCap(AbilityKind.Stamina, 120);

        // 十分な経験値を注いで上限（120）まで伸ばす（決定論・乱数不使用）。
        for (var i = 0; i < 500; i++)
            DevelopmentModel.ApplyExp(p, AbilityKind.Stamina, 5000.0, Tc);

        Assert.True(p.Level(AbilityKind.Stamina) > 100,
            $"スタミナが Lv100 を超えて伸びていない: Lv{p.Level(AbilityKind.Stamina)}");
        Assert.True(p.Level(AbilityKind.Stamina) <= 120, "才能上限（120）を超えて伸びてはならない");

        // 試合用 Player へ投影すると目安投球数が138球（Lv100相当・issue #408で切片63/傾き0.75へ更新）を超える（issue #356 ②）。
        var player = RosterTeamBuilder.ToPlayer(p, FieldPosition.Pitcher);
        Assert.NotNull(player.Pitching);
        Assert.True(player.Pitching!.StaminaPitches > 138.0,
            $"目安投球数が138球を超えていない: {player.Pitching.StaminaPitches:F1}球");
    }

    [Fact]
    public void Training_NonStaminaAbility_StaysCappedAt99()
    {
        // 回帰: スタミナ以外は SetCap で 99 以下しか入らない生成経路（Generation テストで担保）だが、
        // 育成そのものは Cap を尊重して止まる＝Cap を99にしたら Lv100 を超えない（スタミナだけが例外）。
        var p = new DevelopingPlayer { GrowthType = GrowthType.Standard };
        p.SetLevel(AbilityKind.Control, 95);
        p.SetCap(AbilityKind.Control, 99);
        for (var i = 0; i < 500; i++)
            DevelopmentModel.ApplyExp(p, AbilityKind.Control, 5000.0, Tc);
        Assert.Equal(99, p.Level(AbilityKind.Control));
    }
}
