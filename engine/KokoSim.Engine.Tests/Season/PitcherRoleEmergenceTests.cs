using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 投手役の創発と単一生成器（Issue #366a / #377 / #1792）。生成に役割ゲートが無く打撃が連続量であること、
/// 在籍中の「投手寄りか」は実際の投手能力（物差しの変更・issue #1792）から導出し、選手が覚えている属性ではないことを検証する。
/// 生成時の能力配分（w/wRole）だけは旧来どおり適性ベースのまま（オーナー決定・分布不変）。
/// </summary>
public sealed class PitcherRoleEmergenceTests
{
    private static readonly RosterCoefficients C = new();

    private static List<DevelopingPlayer> Cohorts(int seeds)
    {
        var all = new List<DevelopingPlayer>();
        for (var s = 0; s < seeds; s++)
            all.AddRange(ProspectGenerator.Intake(1, C, new Xoshiro256Random((ulong)(60000 + s))));
        return all;
    }

    private static double BattingLevel(DevelopingPlayer p)
        => (p.Level(AbilityKind.Contact) + p.Level(AbilityKind.Power)) / 2.0;

    // --- 判定が1箇所にしかない（#377 の本丸・#1792 で物差しが能力ベースへ） ---

    /// <summary>
    /// 生成時の能力配分（w/wRole, <see cref="PitcherRoleResolver.IsNaturalPitcher(int,int,RosterCoefficients)"/>）は
    /// 旧来どおり適性ベースのまま（オーナー決定・分布不変）。これ自体は #1792 で変えていない。
    /// </summary>
    [Fact]
    public void IsNaturalPitcher_StillAptitudeBased_ForGenerationWeights()
    {
        var rng = new Xoshiro256Random(377);
        var biasMattered = 0;

        for (var i = 0; i < 20000; i++)
        {
            var apt = AptitudeRoller.Roll(C, rng.Fork((ulong)i));

            // 供給バイアスが効いた個体（素の比較なら野手・バイアス込みなら投手）を数える。
            var pitcherApt = apt[(int)FieldPosition.Pitcher];
            var bestField = AptitudeRoller.BestField(apt);
            if (pitcherApt <= bestField && PitcherRoleResolver.IsNaturalPitcher(apt, C)) biasMattered++;
        }

        // バイアス無しの判定に戻っていたら 0 になる（実測では全体の15%強がこの帯にいる）。
        Assert.True(biasMattered > 1000,
            $"供給バイアスが判定に効いていない（境界帯の投手が {biasMattered} 人しかいない）");
    }

    /// <summary>
    /// 在籍中の投手寄り判定（issue #1792）は実際の能力（投手総合 vs 打撃・守備総合）からの導出であって、
    /// 適性を書き換えても（遊びの帯を超えない限り）切り替わらない＝旧来の適性ベースの契約からは変わった。
    /// </summary>
    [Fact]
    public void LeansPitcher_FollowsAbility_NotAptitude()
    {
        var p = new DevelopingPlayer();
        PitcherRoleResolver.SeedAptitudeForRole(p, true);
        Assert.True(PitcherRoleResolver.LeansPitcher(p));

        // 適性だけ書き換えても（能力は変えていない）判定は変わらない＝適性は在籍中の判定に効かない。
        p.SetAptitude(FieldPosition.Pitcher, 1);
        p.SetAptitude(FieldPosition.Shortstop, 99);
        Assert.True(PitcherRoleResolver.LeansPitcher(p));
    }

    /// <summary>投手寄りか野手寄りかは、投手能力と打撃・守備能力のどちらが高いかで決まる。</summary>
    [Fact]
    public void LeansPitcher_FollowsAbility_HighPitchingBeatsHighBatting()
    {
        var pitcher = new DevelopingPlayer();
        foreach (var k in new[] { AbilityKind.Velocity, AbilityKind.Control, AbilityKind.Stamina, AbilityKind.PitchRank })
            pitcher.SetLevel(k, 80);
        foreach (var k in new[] { AbilityKind.Contact, AbilityKind.Power, AbilityKind.Fielding, AbilityKind.Catching, AbilityKind.ArmStrength })
            pitcher.SetLevel(k, 30);
        Assert.True(PitcherRoleResolver.LeansPitcher(pitcher));

        var fielder = new DevelopingPlayer();
        foreach (var k in new[] { AbilityKind.Velocity, AbilityKind.Control, AbilityKind.Stamina, AbilityKind.PitchRank })
            fielder.SetLevel(k, 30);
        foreach (var k in new[] { AbilityKind.Contact, AbilityKind.Power, AbilityKind.Fielding, AbilityKind.Catching, AbilityKind.ArmStrength })
            fielder.SetLevel(k, 80);
        Assert.False(PitcherRoleResolver.LeansPitcher(fielder));
    }

    /// <summary>
    /// 遊び（ヒステリシス）: 投手寄りと判定された後、能力が1つ上下しただけ（遊びの帯の中）では判定が入れ替わらない。
    /// 帯を超える大きな変化では切り替わる。
    /// </summary>
    [Fact]
    public void LeansPitcher_Hysteresis_SmallChangeDoesNotFlip_LargeChangeDoes()
    {
        var p = new DevelopingPlayer();
        foreach (var k in new[] { AbilityKind.Velocity, AbilityKind.Control, AbilityKind.Stamina, AbilityKind.PitchRank })
            p.SetLevel(k, 60);
        foreach (var k in new[] { AbilityKind.Contact, AbilityKind.Power, AbilityKind.Fielding, AbilityKind.Catching, AbilityKind.ArmStrength })
            p.SetLevel(k, 30);
        Assert.True(PitcherRoleResolver.LeansPitcher(p)); // 前回判定=投手寄りが確定

        // 制球が1だけ下がる（能力が1つ下がる程度）→ 遊びの帯の中なので投手寄りのまま。
        p.SetLevel(AbilityKind.Control, 59);
        Assert.True(PitcherRoleResolver.LeansPitcher(p));

        // 打撃・守備側を大きく伸ばす（帯を超える）→ 野手寄りへ切り替わる。
        foreach (var k in new[] { AbilityKind.Contact, AbilityKind.Power, AbilityKind.Fielding, AbilityKind.Catching, AbilityKind.ArmStrength })
            p.SetLevel(k, 90);
        Assert.False(PitcherRoleResolver.LeansPitcher(p));
    }

    // --- 投手供給 ---

    /// <summary>投手適性者0人のコホートが出ない（自校側の下限保証, #377）。</summary>
    [Fact]
    public void EveryCohort_HasAtLeastOnePitcher()
    {
        for (var s = 0; s < 4000; s++)
        {
            var cohort = ProspectGenerator.Intake(1, C, new Xoshiro256Random((ulong)(200000 + s)));
            Assert.True(cohort.Any(p => PitcherRoleResolver.LeansPitcher(p)), $"seed {s} のコホートに投手が居ない");
        }
    }

    /// <summary>供給率が実プレイに必要な水準に留まる（バイアスを落とすと半減して落ちる）。</summary>
    [Fact]
    public void PitcherSupply_StaysInBand()
    {
        var all = Cohorts(2000);
        var share = all.Count(p => PitcherRoleResolver.LeansPitcher(p)) / (double)all.Count;
        Assert.InRange(share, 0.25, 0.40);
    }

    // --- 生成の単一化（打撃の崖が無い） ---

    /// <summary>
    /// 投手の打撃が「野手 − 固定値」の崖でなく連続分布になる（#360 の根治）。
    /// 崖があると投手の打撃はごく狭い帯に固まるので、分布の広がりと野手との重なりで見る。
    /// </summary>
    [Fact]
    public void PitcherBatting_IsContinuous_NotACliff()
    {
        var all = Cohorts(1500);
        var pitchers = all.Where(p => PitcherRoleResolver.LeansPitcher(p)).Select(BattingLevel).ToList();
        var fielders = all.Where(p => !PitcherRoleResolver.LeansPitcher(p)).Select(BattingLevel).ToList();

        var pitcherSd = Math.Sqrt(pitchers.Sum(v => Math.Pow(v - pitchers.Average(), 2)) / pitchers.Count);
        Assert.True(pitcherSd > 5.0, $"投手の打撃分布が狭すぎる（崖の疑い）: sd={pitcherSd:F2}");

        // 野手の分布と十分に重なる（完全に分離＝崖）。
        var fielderMedian = fielders.OrderBy(v => v).ElementAt(fielders.Count / 2);
        var overlap = pitchers.Count(v => v >= fielderMedian) / (double)pitchers.Count;
        Assert.True(overlap > 0.05, $"投手の打撃が野手中央値を超える割合が低すぎる: {overlap:P1}");
    }

    /// <summary>打撃が野手平均を超える投手が非ゼロで出る（#360 の完了条件）。</summary>
    [Fact]
    public void SomePitchers_OutHit_TheAverageFielder()
    {
        var all = Cohorts(1500);
        var fielderAvg = all.Where(p => !PitcherRoleResolver.LeansPitcher(p)).Average(BattingLevel);
        var share = all.Where(p => PitcherRoleResolver.LeansPitcher(p)).Count(p => BattingLevel(p) > fielderAvg)
                    / (double)all.Count(p => PitcherRoleResolver.LeansPitcher(p));
        Assert.True(share > 0.02, $"野手平均を超える投手が出ていない: {share:P1}");
    }

    /// <summary>二刀流（投手能力・打撃とも上位）が構造的に存在する。</summary>
    [Fact]
    public void TwoWayPlayers_ExistStructurally()
    {
        var all = Cohorts(1500);
        var pitchers = all.Where(p => PitcherRoleResolver.LeansPitcher(p)).ToList();

        double Pitching(DevelopingPlayer p)
            => (p.Level(AbilityKind.Velocity) + p.Level(AbilityKind.Control) + p.Level(AbilityKind.PitchRank)) / 3.0;

        // 上位2割の基準線（母集団全体に対する相対順位）。
        var batBar = all.Select(BattingLevel).OrderByDescending(v => v).ElementAt(all.Count / 5);
        var pitchBar = pitchers.Select(Pitching).OrderByDescending(v => v).ElementAt(pitchers.Count / 5);

        var twoWay = pitchers.Count(p => BattingLevel(p) >= batBar && Pitching(p) >= pitchBar);
        Assert.True(twoWay > 0, "投打とも上位の二刀流個体が1人も出ていない");
    }

    // --- AI校（StrengthTeamFactory）側の単一生成器 ---

    /// <summary>
    /// AI校の投手にも打撃−20の崖が無い（#360 の実測は県内211校で「野手平均超えの投手0件」だった）。
    /// </summary>
    [Fact]
    public void AiSchoolPitchers_HaveNoBattingCliff()
    {
        var pc = new PersonalityCoefficients();
        var teams = Enumerable.Range(1, 300)
            .Select(i => StrengthTeamFactory.Create(55.0, "校", new Xoshiro256Random((ulong)i), pc, rosterCoeff: C))
            .ToList();

        double Bat(Player p) => (p.Contact + p.Power) / 2.0;
        // 投手か否かは配置で測る（#381: 投手能力は全選手が持つので有無では分けられない）。
        var pitchers = teams.SelectMany(t => t.BattingOrder.Concat(t.Reserves))
            .Where(p => p.Position == FieldPosition.Pitcher).Select(Bat).ToList();
        var fielderAvg = teams.SelectMany(t => t.BattingOrder)
            .Where(p => p.Position != FieldPosition.Pitcher).Average(Bat);

        var share = pitchers.Count(v => v > fielderAvg) / (double)pitchers.Count;
        Assert.True(share > 0.01, $"AI校で野手平均を超える投手が出ていない: {share:P1}");

        // 固定オフセットなら投手の打撃は狭い帯に固まる。
        var sd = Math.Sqrt(pitchers.Sum(v => Math.Pow(v - pitchers.Average(), 2)) / pitchers.Count);
        Assert.True(sd > 5.0, $"AI校の投手打撃が狭すぎる（崖の疑い）: sd={sd:F2}");
    }

    /// <summary>投手枠でない選手は単一生成器を w=0 で通る＝打撃中心が strength のまま（従来と一致）。</summary>
    [Fact]
    public void FieldersAreUnaffected_ByTheSingleGeneratorSwitch()
    {
        var pc = new PersonalityCoefficients();
        var teams = Enumerable.Range(1, 200)
            .Select(i => StrengthTeamFactory.Create(55.0, "校", new Xoshiro256Random((ulong)i), pc, rosterCoeff: C))
            .ToList();
        var fielders = teams.SelectMany(t => t.BattingOrder)
            .Where(p => p.Position != FieldPosition.Pitcher).ToList();

        // 中心・σ=6 の Ability ロールなので、平均は「その学校の能力中心」の近傍に収まる。
        // 中心は strength そのものではなく**ティア圧縮を通した値**（issue #578）＝ここで literal 55 を
        // 期待すると、圧縮の有無ではなく写像の定義を二重に書くことになる。見たいのは
        // 「野手に隠れたオフセットが乗っていないこと」なので、中心は写像から引く。
        var center = C.CompressStrength(55.0);
        // 表示Lv（issue #1080: 今の物差し −Δ）。中心は今の物差しの値。
        Assert.InRange(fielders.Average(p => (p.Contact + p.Power) / 2.0) + KokoSim.Engine.Players.AbilityScale.Delta, center - 2.0, center + 2.0);
    }
}
