using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 入部予定者の2段化（3月確定 → 4月仮入部・issue #587）。従来は年度替わりの中で新入生を生成していたので、
/// <b>分けた結果が従来と1ビット一致する</b>ことがこの機能の土台になる（ずれると既存セーブの続きが
/// 別の新入生になる）。
/// </summary>
public sealed class ProspectSplitTests
{
    private static readonly RosterCoefficients C = new();

    /// <summary>3月に確定→4月に合流させたロスターが、従来の1段生成と完全に一致する。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void SplitConfirm_MatchesLegacySingleStage(int seedOffset)
    {
        var seed = (ulong)(510000 + seedOffset);

        // 従来: 年度替わりの中で生成する。
        var legacyRoster = BuildRoster(seed);
        var legacy = RosterLifecycle.AdvanceYear(
            legacyRoster, year: 2, C, new Xoshiro256Random(seed + 1), nextId: 100);

        // 2段: 3月に確定 → 4月は合流だけ（生成しない＝rng を消費しない）。
        var splitRoster = BuildRoster(seed);
        var confirmed = RosterLifecycle.ConfirmIncoming(
            splitRoster, year: 2, C, new Xoshiro256Random(seed + 1));
        var unusedRng = new Xoshiro256Random(seed + 999);   // 使われないはずの乱数源
        var split = RosterLifecycle.AdvanceYear(
            splitRoster, year: 2, C, unusedRng, nextId: 100, incoming: confirmed);

        Assert.Equal(legacy.Newcomers.Select(Key), split.Newcomers.Select(Key));
        Assert.Equal(legacy.Graduated.Select(Key), split.Graduated.Select(Key));
        Assert.Equal(legacy.NextId, split.NextId);
        Assert.Equal(legacyRoster.Select(Key), splitRoster.Select(Key));
        Assert.Equal(legacyRoster.Select(p => p.Id), splitRoster.Select(p => p.Id));
    }

    /// <summary>合流は乱数を消費しない（同じ乱数源を渡した前後で状態が動かない）。</summary>
    [Fact]
    public void Join_ConsumesNoRandomness()
    {
        var roster = BuildRoster(520000);
        var confirmed = RosterLifecycle.ConfirmIncoming(roster, 2, C, new Xoshiro256Random(520001));

        var rng = new Xoshiro256Random(777);
        rng.NextDouble();                                   // どこか途中の状態から試す
        var before = rng.CaptureState();

        RosterLifecycle.AdvanceYear(roster, 2, C, rng, nextId: 100, incoming: confirmed);

        Assert.Equal(before, rng.CaptureState());
    }

    /// <summary>確定した予定者がそのまま（同じインスタンス・同じ順で）合流する。</summary>
    [Fact]
    public void ConfirmedProspects_JoinAsFirstYears()
    {
        var roster = BuildRoster(530000);
        var confirmed = RosterLifecycle.ConfirmIncoming(roster, 2, C, new Xoshiro256Random(530001));

        var rollover = RosterLifecycle.AdvanceYear(
            roster, 2, C, new Xoshiro256Random(1), nextId: 100, incoming: confirmed);

        Assert.Equal(confirmed.Count, rollover.Newcomers.Count);
        for (var i = 0; i < confirmed.Count; i++)
        {
            Assert.Same(confirmed[i], rollover.Newcomers[i]);
            Assert.Equal(1, rollover.Newcomers[i].Grade);
            Assert.Contains(rollover.Newcomers[i], roster);
        }
    }

    /// <summary>予定者を渡さなければ従来どおりその場で生成する（旧セーブのフォールバック）。</summary>
    [Fact]
    public void WithoutIncoming_StillGeneratesInPlace()
    {
        var roster = BuildRoster(540000);
        var rollover = RosterLifecycle.AdvanceYear(roster, 2, C, new Xoshiro256Random(540001), nextId: 100);
        Assert.InRange(rollover.Newcomers.Count, C.IntakeMin, C.IntakeMax);
    }

    /// <summary>3月時点の3年生（引退済みを含む）は予定者の氏名重複回避に影響しない＝卒業後の並びで見る。</summary>
    [Fact]
    public void Confirm_IgnoresGraduatingThirdYears()
    {
        var a = BuildRoster(550000);
        var b = BuildRoster(550000);
        foreach (var p in b.Where(p => p.Grade == 3)) p.IsRetired = true;   // 夏で引退済みでも同じ結果

        var ca = RosterLifecycle.ConfirmIncoming(a, 2, C, new Xoshiro256Random(550001));
        var cb = RosterLifecycle.ConfirmIncoming(b, 2, C, new Xoshiro256Random(550001));

        Assert.Equal(ca.Select(Key), cb.Select(Key));
    }

    // 3学年ぶんのロスターを組む（RosterService.Build と同じ作法: 学年ごとに Intake → 安定ID連番）。
    private static List<DevelopingPlayer> BuildRoster(ulong seed)
    {
        var rng = new Xoshiro256Random(seed);
        var list = new List<DevelopingPlayer>();
        for (var grade = 1; grade <= 3; grade++)
        {
            foreach (var p in ProspectGenerator.Intake(grade, C, rng))
            {
                p.Grade = grade;
                list.Add(p);
            }
        }
        for (var i = 0; i < list.Count; i++) list[i].Id = i + 1;
        return list;
    }

    private static string Key(DevelopingPlayer p)
        => p.Name + ":" + p.Grade + ":" + p.Aptitude(FieldPosition.Pitcher)
           + ":" + p.Level(AbilityKind.Contact) + ":" + p.Level(AbilityKind.Control);
}
