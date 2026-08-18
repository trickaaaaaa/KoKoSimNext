using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// AI校の上位集中＝スター型（issue prefecture-dominance-star・OPEN-QUESTIONS Q24-4）。
/// ティア圧縮で削った分（raw−圧縮後）をエース＋主軸へ戻し、脇役は圧縮後の中心のまま＝「強豪はエースが速く
/// 主軸が打つが打線は薄い」。使い捨て生成（<see cref="StrengthTeamFactory.Create"/>）と永続ロスター
/// （<see cref="AiRosterFactory"/>）の両経路で構造が出ていること、弱小校には出ないこと、チーム総合が圧縮後の
/// 尺度に留まることを固定する。
/// </summary>
public sealed class AiStarStructureTests
{
    private static IEnumerable<Player> All(Team t)
    {
        foreach (var p in t.BattingOrder) yield return p;
        foreach (var p in t.Reserves) yield return p;
        if (t.StartingPitcher is { } sp) yield return sp;
    }

    private static double Bat(Player p) => (p.Contact + p.Power + p.Discipline) / 3.0;

    [Fact]
    public void StarLift_IsZeroBelowPivot_AndEqualsRawMinusCompressedAtShareOne()
    {
        var c = new RosterCoefficients();
        Assert.Equal(0.0, c.StarLift(30.0), 9);
        Assert.Equal(0.0, c.StarLift(40.0), 9);
        Assert.Equal(80.0 - c.CompressStrength(80.0), c.StarLift(80.0), 9);
        Assert.Equal(0.0, (c with { AiStarShare = 0.0 }).StarLift(80.0), 9);
    }

    /// <summary>使い捨て生成: 強度80の先発野手8人のうち主軸3人の打撃中心が脇役より明確に高く、エースの投手能力が控え投手より高い。</summary>
    [Fact]
    public void Create_StrongSchool_HasStarHittersAndAce()
    {
        var starCount = new RosterCoefficients().AiStarHitters;
        var gaps = new List<double>();
        var aceGaps = new List<double>();
        for (var seed = 1UL; seed <= 20; seed++)
        {
            var t = StrengthTeamFactory.Create(80.0, "強豪", new Xoshiro256Random(seed));
            var bats = t.BattingOrder.Where(p => p.Position != FieldPosition.Pitcher).Select(Bat).OrderByDescending(x => x).ToList();
            gaps.Add(bats.Take(starCount).Average() - bats.Skip(starCount).Average());
            var pitchers = All(t).Where(p => p.Position == FieldPosition.Pitcher).ToList();
            var ace = pitchers.Single(p => p.UniformNumber == 1);
            aceGaps.Add(ace.Pitching!.MaxVelocityKmh - pitchers.Where(p => p != ace).Average(p => p.Pitching!.MaxVelocityKmh));
        }
        // 主軸の中心 = raw 80、脇役 = 62 − 0.3×18 ≈ 56.6 → 差 ≈ 23（観測は個体差で縮む）。一様圧縮（差 ≈ 8）と分離できる下限。
        Assert.True(gaps.Average() > 14.0, $"主軸と脇役の打撃差が小さい: {gaps.Average():F1}");
        Assert.True(aceGaps.Average() > 4.0, $"エースと控え投手の球速差が小さい: {aceGaps.Average():F1} km/h");
    }

    /// <summary>ピボット（40）以下の学校にはスターが出ない（圧縮で持ち上がる側＝lift 0）＝主軸/脇役の差は個体差だけ。</summary>
    [Fact]
    public void Create_WeakSchool_HasNoStars()
    {
        var starCount = new RosterCoefficients().AiStarHitters;
        var gaps = new List<double>();
        for (var seed = 1UL; seed <= 20; seed++)
        {
            var t = StrengthTeamFactory.Create(35.0, "弱小", new Xoshiro256Random(seed));
            var bats = t.BattingOrder.Where(p => p.Position != FieldPosition.Pitcher).Select(Bat).OrderByDescending(x => x).ToList();
            gaps.Add(bats.Take(starCount).Average() - bats.Skip(starCount).Average());
        }
        Assert.True(gaps.Average() < 12.0, $"弱小校に主軸構造が出ている: {gaps.Average():F1}");
    }

    /// <summary>share=0 は一様圧縮＝スター無し（比較用の固定。旧挙動へ戻せる）。</summary>
    [Fact]
    public void ShareZero_IsUniformCompression()
    {
        var uniform = new RosterCoefficients { AiStarShare = 0.0 };
        var gaps = new List<double>();
        for (var seed = 1UL; seed <= 20; seed++)
        {
            var t = StrengthTeamFactory.Create(80.0, "強豪", new Xoshiro256Random(seed), rosterCoeff: uniform);
            var bats = t.BattingOrder.Where(p => p.Position != FieldPosition.Pitcher).Select(Bat).OrderByDescending(x => x).ToList();
            gaps.Add(bats.Take(3).Average() - bats.Skip(3).Average());
        }
        Assert.True(gaps.Average() < 12.0, $"share=0 でも主軸構造が出ている: {gaps.Average():F1}");
    }

    /// <summary>永続ロスター: 学年ごとにエース格1人（投手に寄った者の重み順）と主軸1人（野手から）を選ぶ。決定論。</summary>
    [Fact]
    public void PickStars_ChoosesTopWeightPitcher_AndOneHitter_Deterministically()
    {
        var leans = new[] { false, true, false, true, false, false };
        var weights = new[] { 0.1, 0.6, 0.2, 0.9, 0.0, 0.3 };
        var rc = new RosterCoefficients();
        var (p1, h1) = AiRosterFactory.PickStars(leans, weights, rc, new Xoshiro256Random(9));
        var (p2, h2) = AiRosterFactory.PickStars(leans, weights, rc, new Xoshiro256Random(9));
        Assert.Equal(new HashSet<int> { 3 }, p1);
        Assert.Single(h1);
        Assert.DoesNotContain(1, h1);
        Assert.DoesNotContain(3, h1);
        Assert.Equal(p1, p2);
        Assert.Equal(h1, h2);

        // 投手に寄った者がいない学年はエース格なし。
        var (none, _) = AiRosterFactory.PickStars(new[] { false, false, false }, new[] { 0.1, 0.2, 0.3 }, rc, new Xoshiro256Random(9));
        Assert.Empty(none);
    }

    /// <summary>
    /// 永続ロスター（実ゲーム経路）: 強度80校の年度3チームは、エースの最速が脇の投手より速く、主軸3人が脇6人より打ち、
    /// それでもチーム総合（6指標）は圧縮後の目標（62）近傍＝逆算配分が脇役で吸収する。
    /// </summary>
    [Fact]
    public void Persistent_StrongSchool_TopHeavy_ButOverallStaysOnCompressedScale()
    {
        var deps = new AiRosterDeps
        {
            Persistent = new PersistentRosterCoefficients
            {
                Phenom = new PhenomCoefficients { SpikeRatePerSchoolYear = 0.0, AllRoundRatePerSchoolYear = 0.0 },
            },
        };
        var overalls = new List<double>(); var gaps = new List<double>(); var aceKmh = new List<double>();
        for (var id = 1; id <= 12; id++)
        {
            var school = new School { Id = 700 + id, Name = $"強豪{id}", PrefectureId = 13, Strength = 80, Fame = 50 };
            var rosters = new NationRosters(deps);
            var t = rosters.TeamFor(school, 3);
            overalls.Add(ScoutedTeamProfile.Compute(t, deps.TeamStrength).Overall);
            var bats = t.BattingOrder.Where(p => p.Position != FieldPosition.Pitcher).Select(Bat).OrderByDescending(x => x).ToList();
            gaps.Add(bats.Take(3).Average() - bats.Skip(3).Average());
            aceKmh.Add(All(t).Where(p => p.Position == FieldPosition.Pitcher).Max(p => p.Pitching!.MaxVelocityKmh));
        }
        var target = deps.Roster.CompressStrength(80.0);
        Assert.InRange(overalls.Average(), target - 3.0, target + 3.0);
        Assert.True(gaps.Average() > 10.0, $"永続ロスターに主軸構造が出ていない: {gaps.Average():F1}");
        Assert.True(aceKmh.Average() >= 142.0, $"強度80校のエース最速が遅い: {aceKmh.Average():F1} km/h（一様圧縮 ≈139）");
    }
}
