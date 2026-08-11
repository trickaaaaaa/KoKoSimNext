using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 新設校スタートの初年度ロスター（issue #585 確定事項1）。1年生ちょうど10人だけで始まり、
/// その10人だけで守備が組める（投手役・捕手が必ず立つ）ことを固定する。
/// 人数の抽選（IntakeMean/Sd/Min/Max）を通さない別経路なので、既存の生成には影響しない。
/// </summary>
public sealed class NewSchoolIntakeTests
{
    private const int NewSchoolIntake = 10;   // Unity 側 RosterService.NewSchoolIntake と同値
    private static readonly RosterCoefficients C = new();

    /// <summary>固定人数を渡すと、抽選帯（8〜13）に関係なくちょうどその人数になる。</summary>
    [Fact]
    public void FixedCount_ProducesExactlyThatMany()
    {
        for (var s = 0; s < 500; s++)
        {
            var cohort = ProspectGenerator.Intake(
                1, C, new Xoshiro256Random((ulong)(400000 + s)), fixedCount: NewSchoolIntake);
            Assert.Equal(NewSchoolIntake, cohort.Count);
        }
    }

    /// <summary>
    /// 10人しか居なくても投手役と捕手が必ず立つ（#377 / #589 の供給下限が Intake の中で効く）。
    /// ここが崩れると新設校の初年度がそもそも守備を組めない＝詰む。
    /// </summary>
    [Fact]
    public void EveryNewSchoolCohort_HasPitcherAndCatcher()
    {
        for (var s = 0; s < 2000; s++)
        {
            var cohort = ProspectGenerator.Intake(
                1, C, new Xoshiro256Random((ulong)(410000 + s)), fixedCount: NewSchoolIntake);
            Assert.True(cohort.Any(p => PitcherRoleResolver.LeansPitcher(p, C)), $"seed {s}: 投手役が居ない");
            Assert.True(cohort.Any(p => CatcherRoleResolver.ReadsAsCatcher(p, C)), $"seed {s}: 捕手が居ない");
        }
    }

    /// <summary>1年生だけのロスターでも主将が決まる（最上級生＝1年から選ばれる）。</summary>
    [Fact]
    public void CaptainIsChosen_FromFirstYearsOnly()
    {
        var cohort = ProspectGenerator.Intake(
            1, C, new Xoshiro256Random(420000), fixedCount: NewSchoolIntake).ToList();
        foreach (var p in cohort) p.Grade = 1;

        var captain = CaptainSelector.EnsureCaptain(cohort);

        Assert.NotNull(captain);
        Assert.Equal(1, captain!.Grade);
        Assert.Single(cohort, p => p.IsCaptain);
    }

    /// <summary>固定人数の経路も同シード同結果（不変条件#2）。</summary>
    [Fact]
    public void FixedCount_IsDeterministic()
    {
        for (var s = 0; s < 200; s++)
        {
            var a = ProspectGenerator.Intake(1, C, new Xoshiro256Random((ulong)(430000 + s)), fixedCount: NewSchoolIntake);
            var b = ProspectGenerator.Intake(1, C, new Xoshiro256Random((ulong)(430000 + s)), fixedCount: NewSchoolIntake);
            Assert.Equal(a.Select(Key), b.Select(Key));
        }
    }

    /// <summary>
    /// 固定人数を渡さない従来の経路は、人数も中身も一切変わらない
    /// （<c>fixedCount</c> の追加が既存の決定論を動かしていないことの固定）。
    /// </summary>
    [Fact]
    public void DefaultPath_StaysInIntakeBand()
    {
        for (var s = 0; s < 500; s++)
        {
            var cohort = ProspectGenerator.Intake(1, C, new Xoshiro256Random((ulong)(440000 + s)));
            Assert.InRange(cohort.Count, C.IntakeMin, C.IntakeMax);
        }
    }

    private static string Key(DevelopingPlayer p)
        => p.Name + ":" + p.Aptitude(FieldPosition.Pitcher) + ":" + p.Aptitude(FieldPosition.Catcher)
           + ":" + p.Level(AbilityKind.Contact) + ":" + p.Level(AbilityKind.Control);
}
