using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 捕手供給の下限（#589・設計書04 §2.3「捕手のみ最低保証」）。捕手不在は「苦しい年」ではなく
/// 守備が組めない詰みなので、投手供給の下限（#377）と同型の保証を掛ける。
/// 判定の閾値は <see cref="CatcherRoleResolver"/> にしか存在しないこと、下限が
/// 「効くときだけ・最小限だけ」動くこと、乱数を消費しないことを固定する。
/// </summary>
public sealed class CatcherSupplyTests
{
    private static readonly RosterCoefficients C = new();

    private static List<DevelopingPlayer> Cohort(int seed)
        => ProspectGenerator.Intake(1, C, new Xoshiro256Random((ulong)seed)).ToList();

    /// <summary>捕手として読める選手が0人のコホートが出ない（下限保証の本体）。</summary>
    [Fact]
    public void EveryCohort_HasAtLeastOneCatcher()
    {
        for (var s = 0; s < 8000; s++)
        {
            var cohort = Cohort(300000 + s);
            Assert.True(cohort.Any(p => CatcherRoleResolver.ReadsAsCatcher(p, C)),
                $"seed {s} のコホートに捕手として読める選手が居ない");
        }
    }

    /// <summary>
    /// 下限が効くコホート（全員が閾値未満）で、最も惜しい1人だけが判定境界ちょうどまで上がる。
    /// 「能力は底上げしない」「他の適性は動かさない」も同時に固定する（投手版と同じ作法）。
    /// </summary>
    [Fact]
    public void Floor_LiftsExactlyOnePlayer_ToTheBoundary_AndNothingElse()
    {
        var cohort = Cohort(300001);
        // 全員を閾値未満へ落とす（1人だけ僅差で惜しい形にする）。
        for (var i = 0; i < cohort.Count; i++)
            cohort[i].SetAptitude(FieldPosition.Catcher, 20 + i); // 末尾ほど惜しい
        var expectedLift = cohort[cohort.Count - 1];
        var before = cohort.Select(Snapshot).ToList();

        ProspectGenerator.EnsureCatcherSupply(cohort, C);

        Assert.Equal(CatcherRoleResolver.MinimumAptitude(C), expectedLift.Aptitude(FieldPosition.Catcher));
        Assert.Single(cohort, p => CatcherRoleResolver.ReadsAsCatcher(p, C));
        for (var i = 0; i < cohort.Count; i++)
        {
            var now = Snapshot(cohort[i]);
            // 能力・他ポジ適性は1人も動かない。
            Assert.Equal(before[i] with { CatcherAptitude = 0 }, now with { CatcherAptitude = 0 });
            // 捕手適性が動くのは持ち上げた1人だけ。
            if (!ReferenceEquals(cohort[i], expectedLift))
                Assert.Equal(before[i].CatcherAptitude, now.CatcherAptitude);
        }
    }

    /// <summary>投手役は持ち上げない（持ち上げると最良守備適性が上がり投手供給の保証を割る）。</summary>
    [Fact]
    public void Floor_DoesNotLiftPitchers_SoPitcherSupplySurvives()
    {
        var cohort = Cohort(300002);
        var pitcher = cohort.First(p => PitcherRoleResolver.LeansPitcher(p, C));
        // 投手だけが「惜しい」形にして、下限の候補選びが投手を避けることを見る。
        for (var i = 0; i < cohort.Count; i++)
            cohort[i].SetAptitude(FieldPosition.Catcher, ReferenceEquals(cohort[i], pitcher) ? 49 : 10);
        // 捕手適性を下げた副作用で全員が投手役に倒れると前提が崩れる（候補が空＝全員から採る経路になる）。
        Assert.Contains(cohort, p => !PitcherRoleResolver.LeansPitcher(p, C));

        ProspectGenerator.EnsureCatcherSupply(cohort, C);

        Assert.Equal(49, pitcher.Aptitude(FieldPosition.Catcher));
        Assert.Contains(cohort, p => CatcherRoleResolver.ReadsAsCatcher(p, C));
        // 下限の引き上げで投手が居なくなっていない（投手を持ち上げると最良守備適性が上がり判定を割る）。
        Assert.Contains(cohort, p => PitcherRoleResolver.LeansPitcher(p, C));
    }

    /// <summary>既に捕手が居るコホートでは選手が一切変わらない（効かないときは触らない）。</summary>
    [Fact]
    public void Floor_IsNoOp_WhenCatcherAlreadyExists()
    {
        for (var s = 0; s < 200; s++)
        {
            var cohort = Cohort(310000 + s);
            if (!cohort.Any(p => CatcherRoleResolver.ReadsAsCatcher(p, C))) continue;
            var before = cohort.Select(Snapshot).ToList();
            ProspectGenerator.EnsureCatcherSupply(cohort, C);
            Assert.Equal(before, cohort.Select(Snapshot).ToList());
        }
    }

    /// <summary>
    /// 下限は乱数を消費しない＝同シードで生成物が完全一致する（不変条件#2）。
    /// 乱数を引く実装に変わると、下限が発動したコホートの以降の生成がずれてここが落ちる。
    /// </summary>
    [Fact]
    public void Intake_StaysDeterministic()
    {
        for (var s = 0; s < 500; s++)
        {
            var a = Cohort(320000 + s).Select(Snapshot).ToList();
            var b = Cohort(320000 + s).Select(Snapshot).ToList();
            Assert.Equal(a, b);
        }
    }

    /// <summary>閾値の判定がコピーされていない（<see cref="CatcherRoleResolver"/> が単一ソース）。</summary>
    [Fact]
    public void ReadsAsCatcher_MatchesTheFloor()
    {
        var floor = CatcherRoleResolver.MinimumAptitude(C);
        for (var apt = 1; apt <= 99; apt++)
            Assert.Equal(apt >= floor, CatcherRoleResolver.ReadsAsCatcher(apt, C));
    }

    // --- 比較用スナップショット（能力・適性の全体） ---

    private sealed record PlayerSnapshot(string Name, int CatcherAptitude, string OtherAptitudes, string Levels);

    private static PlayerSnapshot Snapshot(DevelopingPlayer p) => new(
        p.Name,
        p.Aptitude(FieldPosition.Catcher),
        string.Join(",", AptitudeRoller.AllPositions
            .Where(pos => pos != FieldPosition.Catcher).Select(pos => p.Aptitude(pos))),
        string.Join(",", System.Enum.GetValues(typeof(AbilityKind)).Cast<AbilityKind>().Select(k => p.Level(k))));
}
