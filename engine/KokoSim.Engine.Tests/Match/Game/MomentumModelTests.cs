using System.Collections.Generic;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// チームの勢い（issue #863）。GameResult から1試合ぶんの増分を出す純関数（MomentumModel）と、
/// サヨナラ勝ちの印（GameResult.WalkoffWin）を検証する。大会の敗退・大会の終了で0に戻す処理は
/// 「大会の進行状態」を知っている Unity 側 GameSession の仕事なので、ここでは対象外
/// （NextLevel は敗戦・引き分けでは現状を維持するだけ＝呼び出し側が明示的に0へ戻す）。
/// </summary>
public sealed class MomentumModelTests
{
    private static readonly MomentumCoefficients C = new();

    private static GameResult Result(
        int awayRuns, int homeRuns, IReadOnlyList<int> awayLine, IReadOnlyList<int> homeLine, bool mercyEnded = false)
        => new()
        {
            AwayName = "A", HomeName = "H",
            AwayRuns = awayRuns, HomeRuns = homeRuns,
            InningsPlayed = awayLine.Count,
            TotalPitches = 0,
            PitcherChanges = 0,
            MercyEnded = mercyEnded,
            AwayLineScore = awayLine,
            HomeLineScore = homeLine,
        };

    // ===== GameResult.WalkoffWin =====

    [Fact]
    public void WalkoffWin_HomeWinsInBottomOfFinalInning_IsTrue()
    {
        // 0-0のまま9回裏まで進み、裏に1点を返してサヨナラ勝ち（両軍とも9イニングぶん記録が残る）。
        var r = Result(0, 1,
            awayLine: new[] { 0, 0, 0, 0, 0, 0, 0, 0, 0 },
            homeLine: new[] { 0, 0, 0, 0, 0, 0, 0, 0, 1 });
        Assert.True(r.WalkoffWin);
    }

    [Fact]
    public void WalkoffWin_HomeAlreadyLeadingSkipsBottomHalf_IsFalse()
    {
        // 後攻が表の時点で既にリードしており、裏の攻撃をせずに終わった「サヨナラでない後攻勝ち」。
        // 裏が1イニング少ない（HomeLineScore.Count < AwayLineScore.Count）のが目印。
        var r = Result(1, 5,
            awayLine: new[] { 1, 0, 0, 0, 0, 0, 0, 0, 0 },
            homeLine: new[] { 5, 0, 0, 0, 0, 0, 0, 0 });
        Assert.False(r.WalkoffWin);
    }

    [Fact]
    public void WalkoffWin_MercyEnded_IsNotCountedAsWalkoff()
    {
        // 規則上「サヨナラ」はコールドゲームと呼び分ける（MercyEnded=true は除外）。
        var r = Result(0, 10,
            awayLine: new[] { 0, 0, 0, 0, 0 },
            homeLine: new[] { 0, 0, 0, 0, 10 },
            mercyEnded: true);
        Assert.False(r.WalkoffWin);
    }

    [Fact]
    public void WalkoffWin_AwayWins_IsFalse()
    {
        var r = Result(3, 1,
            awayLine: new[] { 3, 0, 0, 0, 0, 0, 0, 0, 0 },
            homeLine: new[] { 1, 0, 0, 0, 0, 0, 0, 0, 0 });
        Assert.False(r.WalkoffWin);
    }

    // ===== MomentumModel.LateComeback =====

    [Fact]
    public void LateComeback_SelfBehindByThreeFromInning7_ThenWins_IsTrue()
    {
        // 自軍=先攻（managerIsAway=true）。7回に後攻が4点を返して自軍がビハインドになり、最終的に逆転勝ち。
        var away = new[] { 0, 0, 0, 0, 0, 0, 0, 0, 7 };
        var home = new[] { 0, 0, 0, 0, 0, 0, 4, 0, 0 };
        Assert.True(MomentumModel.LateComeback(away, home, managerIsAway: true, fromInning: 7, minDeficit: 3));
    }

    [Fact]
    public void LateComeback_DeficitBeforeInning7_ResolvedBeforeInning7_DoesNotCount()
    {
        // 1回に4点ビハインドでも、2回のうちに2点差まで詰めて以降動かない＝7回以降は常に3点差未満
        // （終盤に残っていないビハインドは「終盤の逆転」に数えない）。
        var away = new[] { 0, 2, 0, 0, 0, 0, 0, 0, 3 };
        var home = new[] { 4, 0, 0, 0, 0, 0, 0, 0, 0 };
        Assert.False(MomentumModel.LateComeback(away, home, managerIsAway: true, fromInning: 7, minDeficit: 3));
    }

    [Fact]
    public void LateComeback_DeficitUnderThreshold_DoesNotCount()
    {
        // 7回以降に2点差（閾値未満）止まりなら逆転扱いにしない。
        var away = new[] { 0, 0, 0, 0, 0, 0, 0, 0, 3 };
        var home = new[] { 0, 0, 0, 0, 0, 0, 2, 0, 0 };
        Assert.False(MomentumModel.LateComeback(away, home, managerIsAway: true, fromInning: 7, minDeficit: 3));
    }

    // ===== MomentumModel.WinBonus / NextLevel =====

    [Fact]
    public void WinBonus_PlainWin_NoTriggers_IsZero()
    {
        var r = Result(5, 1,
            awayLine: new[] { 1, 1, 1, 1, 1, 0, 0, 0, 0 },
            homeLine: new[] { 1, 0, 0, 0, 0, 0, 0, 0, 0 });
        Assert.Equal(0, MomentumModel.WinBonus(r, managerIsAway: true, selfStrength: 50, opponentStrength: 50, C));
    }

    [Fact]
    public void WinBonus_Loss_IsZero()
    {
        var r = Result(1, 5,
            awayLine: new[] { 1, 0, 0, 0, 0, 0, 0, 0, 0 },
            homeLine: new[] { 1, 1, 1, 1, 1, 0, 0, 0, 0 });
        Assert.Equal(0, MomentumModel.WinBonus(r, managerIsAway: true, selfStrength: 50, opponentStrength: 50, C));
    }

    [Theory]
    [InlineData(50, 60, 1)] // D vs C: ティア差1段 → +1
    [InlineData(50, 80, 2)] // D vs A: ティア差3段（2段以上）→ +2（1段ぶんとの合算ではない）
    [InlineData(60, 50, 0)] // 格下に勝利: 加点なし
    public void WinBonus_UpsetByTierGap(double selfStrength, double opponentStrength, int expected)
    {
        var r = Result(5, 1,
            awayLine: new[] { 1, 1, 1, 1, 1, 0, 0, 0, 0 },
            homeLine: new[] { 1, 0, 0, 0, 0, 0, 0, 0, 0 });
        Assert.Equal(expected, MomentumModel.WinBonus(r, managerIsAway: true, selfStrength, opponentStrength, C));
    }

    [Fact]
    public void WinBonus_WalkoffWin_AddsWalkoffBonus()
    {
        var r = Result(0, 1,
            awayLine: new[] { 0, 0, 0, 0, 0, 0, 0, 0, 0 },
            homeLine: new[] { 0, 0, 0, 0, 0, 0, 0, 0, 1 });
        // 自軍=後攻（managerIsAway=false）・同格（gap0）・逆転トリガー無しで分離。
        Assert.Equal(C.WalkoffBonus, MomentumModel.WinBonus(r, managerIsAway: false, selfStrength: 50, opponentStrength: 50, C));
    }

    [Fact]
    public void WinBonus_ComebackWin_AddsComebackBonus()
    {
        var r = Result(7, 4,
            awayLine: new[] { 0, 0, 0, 0, 0, 0, 0, 0, 7 },
            homeLine: new[] { 0, 0, 0, 0, 0, 0, 4, 0, 0 });
        Assert.Equal(C.ComebackBonus, MomentumModel.WinBonus(r, managerIsAway: true, selfStrength: 50, opponentStrength: 50, C));
    }

    [Fact]
    public void WinBonus_TriggersStack_UpsetAndWalkoff()
    {
        // 格上(ティア差1段)に、サヨナラで勝つ＝+1(格上)+1(サヨナラ)。
        var r = Result(0, 1,
            awayLine: new[] { 0, 0, 0, 0, 0, 0, 0, 0, 0 },
            homeLine: new[] { 0, 0, 0, 0, 0, 0, 0, 0, 1 });
        Assert.Equal(2, MomentumModel.WinBonus(r, managerIsAway: false, selfStrength: 50, opponentStrength: 60, C));
    }

    [Fact]
    public void NextLevel_PlainWin_MaintainsCurrentLevel_NoDecay()
    {
        var r = Result(5, 1,
            awayLine: new[] { 1, 1, 1, 1, 1, 0, 0, 0, 0 },
            homeLine: new[] { 1, 0, 0, 0, 0, 0, 0, 0, 0 });
        Assert.Equal(2, MomentumModel.NextLevel(2, r, managerIsAway: true, selfStrength: 50, opponentStrength: 50, C));
    }

    [Fact]
    public void NextLevel_Loss_MaintainsCurrentLevel()
    {
        var r = Result(1, 5,
            awayLine: new[] { 1, 0, 0, 0, 0, 0, 0, 0, 0 },
            homeLine: new[] { 1, 1, 1, 1, 1, 0, 0, 0, 0 });
        Assert.Equal(2, MomentumModel.NextLevel(2, r, managerIsAway: true, selfStrength: 50, opponentStrength: 50, C));
    }

    [Fact]
    public void NextLevel_ClampsAtMaxLevel()
    {
        // ティア差2段以上(+2)の勝利を現在2段で迎えても、上限(既定3)を超えない。
        var r = Result(5, 1,
            awayLine: new[] { 1, 1, 1, 1, 1, 0, 0, 0, 0 },
            homeLine: new[] { 1, 0, 0, 0, 0, 0, 0, 0, 0 });
        Assert.Equal(3, MomentumModel.NextLevel(2, r, managerIsAway: true, selfStrength: 50, opponentStrength: 80, C));
    }

    [Fact]
    public void NextLevel_FromZero_UpsetWalkoffCombo_Increases()
    {
        var r = Result(0, 1,
            awayLine: new[] { 0, 0, 0, 0, 0, 0, 0, 0, 0 },
            homeLine: new[] { 0, 0, 0, 0, 0, 0, 0, 0, 1 });
        Assert.Equal(2, MomentumModel.NextLevel(0, r, managerIsAway: false, selfStrength: 50, opponentStrength: 60, C));
    }

    // ===== MomentumModel.Multiplier / ApplyBatter / ApplyPitcher =====

    [Fact]
    public void Multiplier_LevelZero_IsIdentity() => Assert.Equal(1.0, MomentumModel.Multiplier(0, C));

    [Fact]
    public void Multiplier_IncreasesMonotonically_WithLevel()
    {
        var m0 = MomentumModel.Multiplier(0, C);
        var m1 = MomentumModel.Multiplier(1, C);
        var m2 = MomentumModel.Multiplier(2, C);
        var m3 = MomentumModel.Multiplier(3, C);
        Assert.True(m0 < m1 && m1 < m2 && m2 < m3);
        Assert.Equal(1.06, m3, 10);
    }

    [Fact]
    public void ApplyBatter_LevelZero_IsIdentity()
    {
        var b = new BatterAttributes { Contact = 55 };
        Assert.Same(b, MomentumModel.ApplyBatter(b, 0, C));
    }

    [Fact]
    public void ApplyPitcher_LevelZero_IsIdentity()
    {
        var p = new PitcherAttributes { Control = 55 };
        Assert.Same(p, MomentumModel.ApplyPitcher(p, 0, C));
    }

    [Fact]
    public void ApplyBatter_ContactRisesMonotonically_WithLevel()
    {
        var b = new BatterAttributes { Contact = 60 };
        var c1 = MomentumModel.ApplyBatter(b, 1, C).Contact;
        var c2 = MomentumModel.ApplyBatter(b, 2, C).Contact;
        var c3 = MomentumModel.ApplyBatter(b, 3, C).Contact;
        Assert.True(b.Contact < c1 && c1 <= c2 && c2 <= c3);
    }

    [Fact]
    public void ApplyPitcher_ControlRisesMonotonically_WithLevel()
    {
        var p = new PitcherAttributes { Control = 60 };
        var c1 = MomentumModel.ApplyPitcher(p, 1, C).Control;
        var c2 = MomentumModel.ApplyPitcher(p, 2, C).Control;
        var c3 = MomentumModel.ApplyPitcher(p, 3, C).Control;
        Assert.True(p.Control < c1 && c1 <= c2 && c2 <= c3);
    }

    [Fact]
    public void ApplyBatter_NeverExceedsAbilityCeiling()
    {
        var b = new BatterAttributes { Contact = 99 };
        Assert.Equal(100, MomentumModel.ApplyBatter(b, 3, C).Contact);
    }

    // ===== Team.MomentumLevel 既定 =====

    [Fact]
    public void Team_MomentumLevel_DefaultsToZero()
    {
        var team = new Team { BattingOrder = System.Array.Empty<Player>() };
        Assert.Equal(0, team.MomentumLevel);
    }
}
