using KokoSim.Engine.Career;
using Xunit;

namespace KokoSim.Engine.Tests.Career;

/// <summary>転任判定の前の匂わせ（issue #1606・設計書04 §1.3）。会話を出す年と信頼度の段の出し分け。</summary>
public sealed class TransferHintTests
{
    private static readonly CareerCoefficients C = new();

    private static Manager Teacher(double trust, int yearsAtSchool = 2) => new()
    {
        Status = ManagerStatus.Teacher, Trust = trust, YearsAtSchool = yearsAtSchool,
        KoshienAppearances = 0, Fame = 10,
    };

    [Theory]
    [InlineData(0, false)]    // 赴任1年目の2月（3月の総括で1年目が終わる）
    [InlineData(1, false)]    // 2年目
    [InlineData(2, true)]     // 3年目＝この年度末が最初の抽選（総括で YearsAtSchool が3になる）
    [InlineData(5, true)]
    public void TierFor_OnlyInYearsWhereTheDrawHappens(int yearsAtSchool, bool expected)
    {
        Assert.Equal(expected, TransferHint.TierFor(Teacher(60, yearsAtSchool), C) != null);
    }

    [Fact]
    public void TierFor_AgreesWithEvaluate_AboutWhetherTheDrawHappens()
    {
        // 総括（YearsAtSchool++）の後の Evaluate が抽選に入る年と、匂わせを出す年が一致する。
        for (var years = 0; years < 6; years++)
        {
            var hint = TransferHint.TierFor(Teacher(50, years), C) != null;
            var atEvaluate = years + 1 >= C.MinTenureYears;
            Assert.Equal(atEvaluate, hint);
        }
    }

    [Fact]
    public void TierFor_FreeAgentNeverGetsTheTalk()
    {
        var m = Teacher(50);
        m.Status = ManagerStatus.Free;
        Assert.Null(TransferHint.TierFor(m, C));
    }

    [Fact]
    public void TierFor_TeacherWhoQualifiesForFreeAgency_HasNoDraw_SoNoTalk()
    {
        var byKoshien = Teacher(50);
        byKoshien.KoshienAppearances = C.FreeKoshienThreshold;
        Assert.Null(TransferHint.TierFor(byKoshien, C));

        var byFame = Teacher(50);
        byFame.Fame = C.FreeFameThreshold;
        Assert.Null(TransferHint.TierFor(byFame, C));
    }

    [Theory]
    [InlineData(20, TransferHintTier.Low)]
    [InlineData(49.9, TransferHintTier.Low)]
    [InlineData(50, TransferHintTier.Mid)]     // 中点−傾き
    [InlineData(60, TransferHintTier.Mid)]     // 中点
    [InlineData(69.9, TransferHintTier.Mid)]
    [InlineData(70, TransferHintTier.High)]    // 中点＋傾き
    [InlineData(95, TransferHintTier.High)]
    public void TierFor_SplitsTrustAroundTheRetainMidpoint(double trust, TransferHintTier expected)
    {
        Assert.Equal(expected, TransferHint.TierFor(Teacher(trust), C));
    }

    [Fact]
    public void TierFor_FollowsTheRetainCoefficients()
    {
        var c = new CareerCoefficients { RetainTrustThreshold = 40, RetainTrustSpread = 5 };
        Assert.Equal(TransferHintTier.High, TransferHint.TierFor(Teacher(45), c));
        Assert.Equal(TransferHintTier.Mid, TransferHint.TierFor(Teacher(40), c));
        Assert.Equal(TransferHintTier.Low, TransferHint.TierFor(Teacher(34), c));
    }

    [Fact]
    public void TierFor_DoesNotTouchTheManager()
    {
        var m = Teacher(63);
        TransferHint.TierFor(m, C);
        Assert.Equal(63, m.Trust);
        Assert.Equal(2, m.YearsAtSchool);
    }
}
