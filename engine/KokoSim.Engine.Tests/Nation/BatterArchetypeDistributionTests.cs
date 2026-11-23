using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 打者の型（issue #1066）: 強豪校の選手が全員オールA・俊足になり個性が消える問題の実測固定。
/// issue 本文の実測手法（相手校を強さ別に60校ずつ作り、スタメン野手の6能力を集計）をそのまま単体テストにする。
///
/// 旧実装（型なし・BumpTo が全能力へ同じ量を足す）の実測（issue #1066 本文）:
///   強さ90: 6能力すべてA(80以上) 98%・走力70未満 0%・1人の中の最大−最小 2.2・99に張り付いた能力 92.4%
/// 目標（issue #1066・オーナー承認 2026-11-17）:
///   強さ80〜90でもスタメンの「オールA」は1〜2人まで／走力C以下のスラッガー等が一定数いる／
///   1人の中の最大−最小は強さによらず20〜30程度／99に張り付く能力は数%まで。
///
/// <b>強さ85以上は目標に届かない（既知・次の issue）</b>。この作品のチーム総合は、総合の高い学校ほど能力の平均も
/// 高くないと出ない（強さ85で6能力平均≈90）。学校の強さとチーム総合の対応（下の <see cref="StrengthToOverall_IsKept"/>）
/// を保つ限り、強さ85でオールA約2.7人/校・強さ90で約3人/校・99張り付き2〜3割が残る（main はそれぞれ 4.1人・7.9人・
/// 9割超）。オーナー判断（2026-11-17）は「個性を優先し、弱い学校も下げて差を保つ」＝AI校全体の物差しを下げる別作業。
/// 強さ90の学校（全国で数校）は全員ほぼ99でないと総合が届かず、型を入れると総合が目標を約4点下回る。
/// </summary>
public sealed class BatterArchetypeDistributionTests
{
    private readonly ITestOutputHelper _out;
    public BatterArchetypeDistributionTests(ITestOutputHelper o) { _out = o; }

    private static readonly AbilityKind[] CoreSix =
    {
        AbilityKind.Contact, AbilityKind.Power, AbilityKind.Speed,
        AbilityKind.ArmStrength, AbilityKind.Fielding, AbilityKind.Discipline,
    };

    private static int[] CoreSixValues(Player p) => new[]
        { p.Contact, p.Power, p.Speed, p.ArmStrength, p.Fielding, p.Discipline };

    /// <summary>強さ <paramref name="strength"/> の学校を <paramref name="count"/> 校ぶん年度3で実体化し、
    /// スタメン野手（投手を除く8人）を集める。</summary>
    private static List<Player> StarterFieldersAtStrength(double strength, int count, int idBase)
    {
        var deps = new AiRosterDeps();
        var starters = new List<Player>();
        for (var i = 1; i <= count; i++)
        {
            var school = new School { Id = idBase + i, Name = $"校{idBase}-{i}", PrefectureId = 0, Strength = strength, Fame = 50 };
            var rosters = new NationRosters(deps);
            var team = rosters.TeamFor(school, 3);
            starters.AddRange(team.BattingOrder.Where(p => p.Position != FieldPosition.Pitcher));
        }
        return starters;
    }

    [Theory]
    [InlineData(50.0, 30001)]
    [InlineData(60.0, 30101)]
    [InlineData(70.0, 30201)]
    [InlineData(80.0, 30301)]
    [InlineData(90.0, 30401)]
    public void StarterFielders_KeepIndividuality_AcrossStrength(double strength, int idBase)
    {
        const int schools = 60;
        var starters = StarterFieldersAtStrength(strength, schools, idBase);

        // ① 6能力すべてA(80以上)の人数（全校ぶんの総数を校数で割った1校あたりの平均）。
        var allACount = starters.Count(p => CoreSixValues(p).All(v => v >= 80));
        var allARate = 100.0 * allACount / starters.Count;
        var allAPerSchoolAvg = allACount / (double)schools;

        // ② 1人の中の最大−最小（スプレッド）。
        var spreads = starters.Select(p => CoreSixValues(p).Max() - CoreSixValues(p).Min()).ToList();
        var spreadAvg = spreads.Average();

        // ③ 99に張り付いた能力の割合（全(選手,能力)ペア）。
        var pinned = starters.Sum(p => CoreSixValues(p).Count(v => v >= 99));
        var pinnedRate = 100.0 * pinned / (starters.Count * CoreSix.Length);

        // ④ 長所↑短所↓の型が実際に居るか（パワーA以上で走力C以下＝長距離砲系／走力A以上でパワーD以下＝俊足巧打系）。
        var powerSluggerLowSpeed = starters.Count(p => p.Power >= 80 && p.Speed < 70);
        var speedsterLowPower = starters.Count(p => p.Speed >= 80 && p.Power < 60);

        _out.WriteLine($"強さ{strength}: n={starters.Count} オールA {allAPerSchoolAvg:F2}人/校（{allARate:F1}%） "
            + $"／ spread平均 {spreadAvg:F1} ／ 99張り付き率 {pinnedRate:F1}% "
            + $"／ 走力C未満の強打者 {powerSluggerLowSpeed} ／ パワーD未満の俊足 {speedsterLowPower}");

        // 能力は表示Lv（issue #1080: 今の物差し −Δ=7。A＝表示80以上＝今の物差しの87以上・99＝今の物差しの106）。
        // 実測（seed 固定・各60校）: 物差しを下げる前→下げた後で 強さ80 オールA 1.13→0.27人/校・張り付き 3.5→0.0%、
        // 強さ90 オールA 3.07→2.60人/校・張り付き 32→0.0%。張り付きは全強さで目標（数%）に入った。
        // 強さ90のオールAは目標（1校2人以下）に届いていない（OPEN-QUESTIONS Q58・issue #1080）＝悪化しない上限だけを締める。
        var allACap = strength >= 90 ? 3.0 : 2.0;
        var pinnedCap = 5.0;
        Assert.True(allAPerSchoolAvg <= allACap, $"オールAが多すぎる: {allAPerSchoolAvg:F2}人/校（強さ{strength}・上限{allACap}）");
        Assert.InRange(spreadAvg, 20.0, 36.0);
        Assert.True(pinnedRate <= pinnedCap, $"99への張り付き率が高すぎる: {pinnedRate:F1}%（強さ{strength}・上限{pinnedCap}）");
        if (strength >= 80)
        {
            Assert.True(powerSluggerLowSpeed > 0, "強豪校に「走力C未満の強打者」が居ない");
            Assert.True(speedsterLowPower > 0, "強豪校に「パワーD未満の俊足」が居ない");
        }
    }

    /// <summary>
    /// 学校の強さとチーム総合の対応を、型の導入前（main: 各強さで平均 −0.2 前後）から崩さない（issue #1066 の「守るもの」）。
    /// 型ありの成長はやわらかい天井で頭が寝るので、成長量を型なしで解いたままだと強豪校が弱くなる
    /// （最初の実装: 強さ80で −2.9・85で −4.0）。<c>AiRosterCycle.SolveGrowthG</c> の2段目（打者だけ型ありで解き直す）が守る。
    /// </summary>
    [Theory]
    [InlineData(60.0, 31001)]
    [InlineData(70.0, 31101)]
    [InlineData(80.0, 31201)]
    [InlineData(85.0, 31301)]
    public void StrengthToOverall_IsKept(double strength, int idBase)
    {
        var deps = new AiRosterDeps();
        var diffs = new List<double>();
        for (var i = 1; i <= 40; i++)
        {
            var school = new School { Id = idBase + i, Name = $"校{idBase}-{i}", PrefectureId = 0, Strength = strength, Fame = 50 };
            var team = new NationRosters(deps).TeamFor(school, 3);
            diffs.Add(ScoutedTeamProfile.Compute(team, deps.TeamStrength).Overall - deps.Roster.CompressStrength(strength));
        }
        _out.WriteLine($"強さ{strength}: 総合−目標 平均{diffs.Average():F2} 最小{diffs.Min():F2}");
        Assert.True(diffs.Average() >= -0.6, $"強さ{strength}の学校が目標より弱い: 平均 {diffs.Average():F2}");
    }

    /// <summary>
    /// 型の長所は短所より確かに大きい（オフセットの符号が狙いどおりに効いている）。
    /// 長距離砲型のパワーは、同じ強さの選手全体の平均パワーより高いことを確かめる。
    /// </summary>
    [Fact]
    public void PowerHitterArchetype_HasHigherPowerThanAverage()
    {
        var starters = StarterFieldersAtStrength(85.0, 60, 30501);
        var powerHitters = starters.Where(p => p.BatterArchetype == BatterArchetype.PowerHitter).ToList();
        Assert.True(powerHitters.Count > 0, "長距離砲型が1人も抽選されていない（60校・サンプル不足の可能性）");
        var avgAll = starters.Average(p => p.Power);
        Assert.True(powerHitters.Average(p => p.Power) > avgAll,
            $"長距離砲のパワーが平均以下: {powerHitters.Average(p => p.Power):F1} vs 全体平均 {avgAll:F1}");
    }
    /// <summary>中距離ヒッターの「低い弾道が長所」（issue #1304）: 弾道のオフセットは負で、他の型は0（弾道に触らない）。</summary>
    [Fact]
    public void GapHitter_HasNegativeLaunchOffset_OthersZero()
    {
        var c = new BatterArchetypeCoefficients();
        Assert.True(BatterArchetypes.LaunchOffset(BatterArchetype.GapHitter, c) < 0);
        foreach (var a in System.Enum.GetValues<BatterArchetype>().Where(a => a != BatterArchetype.GapHitter))
            Assert.Equal(0.0, BatterArchetypes.LaunchOffset(a, c));
    }

    /// <summary>
    /// 中距離ヒッターの弾道は、同じ強さの他の型の平均より低く、型なしの弾道（≈50）は動かさない（issue #1304）。
    /// </summary>
    [Fact]
    public void GapHitterArchetype_HasLowerLaunchThanOthers()
    {
        var starters = StarterFieldersAtStrength(80.0, 80, 30601);
        var gap = starters.Where(p => p.BatterArchetype == BatterArchetype.GapHitter).ToList();
        var others = starters.Where(p => p.BatterArchetype != BatterArchetype.GapHitter).ToList();
        Assert.True(gap.Count > 0, "中距離ヒッター型が1人も抽選されていない（サンプル不足の可能性）");
        _out.WriteLine($"中距離ヒッター n={gap.Count} 弾道平均 {gap.Average(p => p.LaunchTendency):F1} ／ 他 n={others.Count} 弾道平均 {others.Average(p => p.LaunchTendency):F1}");
        Assert.True(gap.Average(p => p.LaunchTendency) < others.Average(p => p.LaunchTendency) - 5,
            $"中距離ヒッターの弾道が低くない: {gap.Average(p => p.LaunchTendency):F1} vs {others.Average(p => p.LaunchTendency):F1}");
    }
}
