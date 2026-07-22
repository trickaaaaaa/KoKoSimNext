using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation.Tournaments;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 試合間の回復モデル（issue #41・設計書02 §1.1e）。休養日数×直近球数の既知ケース突き合わせ。
/// 完了条件: 連戦で実効スタミナが落ち、休養十分ならフル回復。純関数・決定論。
/// </summary>
public sealed class PitchRecoveryTests
{
    private static readonly PitchRecoveryCoefficients C = new()
    {
        FullRecoveryDays = 7.0,
        ReferencePitches = 100.0,
        MaxReductionFraction = 0.5,
    };

    [Fact]
    public void FullRest_RecoversToBase()
    {
        // 中6日相当（restDays=7）＝完全回復＝目安投球数そのまま。序盤の一発トーナメントは実質不変。
        Assert.Equal(110.0, PitchRecoveryModel.EffectiveStaminaPitches(110.0, 7, 130, C), 6);
        Assert.Equal(110.0, PitchRecoveryModel.EffectiveStaminaPitches(110.0, 9, 130, C), 6);
    }

    [Fact]
    public void NoRecentPitches_NoReduction_RegardlessOfRest()
    {
        // 直近登板がゼロなら休養が短くても減衰しない（フレッシュ扱い）。
        Assert.Equal(110.0, PitchRecoveryModel.EffectiveStaminaPitches(110.0, 0, 0, C), 6);
    }

    [Fact]
    public void SameDayHeavyOuting_MaxReduction()
    {
        // restDays=0（未回復率1.0）＋直近が基準球数（負荷1.0）→ 最大減衰 = 目安×(1-0.5)。
        Assert.Equal(55.0, PitchRecoveryModel.EffectiveStaminaPitches(110.0, 0, 100, C), 6);
    }

    [Fact]
    public void HeavyLoadCapsAtReference()
    {
        // 直近球数が基準を超えても負荷は1.0で頭打ち（同日・最大減衰と同値）。
        Assert.Equal(55.0, PitchRecoveryModel.EffectiveStaminaPitches(110.0, 0, 260, C), 6);
    }

    [Fact]
    public void PartialRest_LinearBetween()
    {
        // restDays=3（未回復率 1-3/7=4/7）× 負荷1.0 × 最大0.5 → reduction=110*0.5*4/7。
        var expected = 110.0 - 110.0 * 0.5 * (4.0 / 7.0) * 1.0;
        Assert.Equal(expected, PitchRecoveryModel.EffectiveStaminaPitches(110.0, 3, 100, C), 6);
    }

    [Fact]
    public void PartialLoad_ScalesReduction()
    {
        // 直近50球（負荷0.5）× restDays=0（未回復1.0）× 最大0.5 → reduction=110*0.5*1.0*0.5。
        var expected = 110.0 - 110.0 * 0.5 * 1.0 * 0.5;
        Assert.Equal(expected, PitchRecoveryModel.EffectiveStaminaPitches(110.0, 0, 50, C), 6);
    }

    [Fact]
    public void MonotonicInRestAndLoad()
    {
        // 休養が増えるほど回復が進む（実効が単調非減少）。
        var d0 = PitchRecoveryModel.EffectiveStaminaPitches(110.0, 0, 100, C);
        var d2 = PitchRecoveryModel.EffectiveStaminaPitches(110.0, 2, 100, C);
        var d5 = PitchRecoveryModel.EffectiveStaminaPitches(110.0, 5, 100, C);
        Assert.True(d0 < d2 && d2 < d5, $"休養増で回復せず: {d0} {d2} {d5}");

        // 直近球数が多いほど実効が下がる（単調非増加）。
        var l30 = PitchRecoveryModel.EffectiveStaminaPitches(110.0, 1, 30, C);
        var l80 = PitchRecoveryModel.EffectiveStaminaPitches(110.0, 1, 80, C);
        Assert.True(l80 < l30, $"直近球数増で下がらず: {l30} {l80}");
    }

    [Fact]
    public void LedgerFeedsModel_ConsecutiveDaysDropStamina_RestRecovers()
    {
        // 台帳→回復モデルの結合。連戦（RoundGapDays=3, 中2日）でエースが完投を重ねる想定。
        var ledger = new TournamentPitchLedger();
        var ace = PitcherLedgerKey.ForPlayer(1);
        const double baseStamina = 110.0;

        // 初戦（day1）はフレッシュ＝完全回復（帯不変の一発トーナメント序盤）。
        var restR1 = ledger.RestDays(ace, currentDay: 1);        // null
        var loadR1 = ledger.PitchesWithin(ace, currentDay: 1, windowDays: 7);
        var effR1 = PitchRecoveryModel.EffectiveStaminaPitches(baseStamina, restR1 ?? 99, loadR1, C);
        Assert.Equal(baseStamina, effR1, 6);
        ledger.Record(ace, 120, matchDay: 1);

        // 2戦目（day4, 中2日）は前戦120球の連戦＝実効が落ちる。
        var effR2 = PitchRecoveryModel.EffectiveStaminaPitches(
            baseStamina, ledger.RestDays(ace, 4)!.Value, ledger.PitchesWithin(ace, 4, 7), C);
        Assert.True(effR2 < baseStamina, $"連戦で実効スタミナが落ちていない: {effR2}");
        ledger.Record(ace, 110, matchDay: 4);

        // もし中6日空けられれば（day11）完全回復に戻る（直近登板が窓外へ抜ける）。
        var effRested = PitchRecoveryModel.EffectiveStaminaPitches(
            baseStamina, ledger.RestDays(ace, 11)!.Value, ledger.PitchesWithin(ace, 11, 7), C);
        Assert.Equal(baseStamina, effRested, 6);
    }
}
