using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Tactics;

/// <summary>
/// 敵AI（設計書11）。三層＝①能力値ミス率 ②ティア引き出し ③校風の重み を検証する。
/// プレイヤーと同じ采配システム（ITacticsBrain）を実装し、AI専用の裏ルールがないことを前提に、
/// AI vs AI 相当の大量試行で各層が意図通り効くかを集計する（§6）。
/// </summary>
public sealed class EnemyAiTests
{
    private static readonly EnemyAiCoefficients Ai = new();

    private static Player P(int contact = 50, int power = 50, int bunt = 50, int speed = 50,
        int steal = 50, int arm = 50, FieldPosition pos = FieldPosition.CenterField)
        => new() { Position = pos, Contact = contact, Power = power, Bunt = bunt, Speed = speed, Steal = steal, ArmStrength = arm };

    private static Player Pitcher(int control = 50) => new()
    { Position = FieldPosition.Pitcher, Pitching = new PitcherAttributes { Control = control } };

    private static TacticsSituation Sit(
        int inning = 8, int outs = 0, int diff = 0,
        Player? first = null, Player? second = null, Player? third = null,
        Player? batter = null)
        => new(inning, 9, outs, diff, first, second, third,
            batter ?? P(power: 40, bunt: 60), Pitcher(), P(pos: FieldPosition.Catcher),
            PressureIndex: 2, PitcherRattled: false, OffenseTimeoutsLeft: 3, DefenseTimeoutsLeft: 3, TieBreak: false);

    private static List<OffensiveSign> RollOffense(ITacticsBrain brain, TacticsSituation s, int n = 400)
    {
        var list = new List<OffensiveSign>(n);
        for (ulong i = 0; i < (ulong)n; i++) list.Add(brain.CallOffense(s, new Xoshiro256Random(1000 + i)));
        return list;
    }

    /// <summary>
    /// 盗塁の「試みるか」判定は設計書15 Phase D-2d で CallOffense（打席頭一度）から
    /// IPitchTacticsBrain.CallPitchAction（毎球）へ移った。1球分の判定を n 回振って企図数を数える。
    /// </summary>
    private static int CountStealAttempts(AiTacticsBrain brain, TacticsSituation s, int n, ulong seedBase = 1000)
    {
        var c = 0;
        for (ulong i = 0; i < (ulong)n; i++)
        {
            var d = brain.CallPitchAction(new PitchTacticsSituation(s, 0, 0, 0, null), new Xoshiro256Random(seedBase + i));
            if (d?.StealAttempt is not null) c++;
        }
        return c;
    }

    // ===== ② ティア層: 引き出しにない高度戦術は素の手へ落ちる =====

    [Fact]
    public void Tier_LowTier_NeverUsesAdvancedSigns()
    {
        // G(0): スクイズ・エンドランは引き出しにない → 意図的にも偶発的にも出ない。
        // （盗塁は①能力値ミスの「無謀な盗塁」として稀に出得るので、ここでは検証しない＝下の頻度テストで扱う）
        var low = new AiTacticsBrain(new AiProfile(90, TierRank: 0, SchoolStyle.SmallBall), aiCoeff: Ai);
        var s = Sit(inning: 9, outs: 1, first: P(steal: 90), third: P(), batter: P(power: 40, bunt: 70));
        var signs = RollOffense(low, s, 500);
        Assert.DoesNotContain(OffensiveSign.Squeeze, signs);
        Assert.DoesNotContain(OffensiveSign.HitAndRun, signs);
    }

    [Fact]
    public void Tier_IntendedStealsScaleWithTier()
    {
        // 盗塁好機で、上級(引き出しにある＝意図的に走る)は初級(偶発の無謀な盗塁のみ)より遥かに多く走る。
        int Steals(int tier)
        {
            var brain = new AiTacticsBrain(new AiProfile(90, tier, SchoolStyle.SmallBall), aiCoeff: Ai);
            var s = Sit(inning: 8, outs: 0, first: P(speed: 90, steal: 90), batter: P(power: 72));
            return CountStealAttempts(brain, s, 600);
        }
        Assert.True(Steals(7) > Steals(0) * 3, "上級は初級より意図的な盗塁が遥かに多いはず");
    }

    [Fact]
    public void Tier_HighTier_CanUseSqueezeAndSteal()
    {
        // S(7): 全戦術が引き出しにある。
        var high = new AiTacticsBrain(new AiProfile(90, TierRank: 7, SchoolStyle.SmallBall), aiCoeff: Ai);
        var squeezeSit = Sit(inning: 9, outs: 1, third: P(), batter: P(power: 40, bunt: 80));
        Assert.Contains(OffensiveSign.Squeeze, RollOffense(high, squeezeSit, 500));
        var stealSit = Sit(inning: 8, outs: 0, first: P(speed: 90, steal: 90),
            batter: P(power: 70)); // 強打者で送りバント条件を外し盗塁を促す
        Assert.True(CountStealAttempts(high, stealSit, 500) > 0, "上級ティアで盗塁企図が一度も出ない");
    }

    [Fact]
    public void Tier_LowTier_DefenseHasNoShiftOrGear()
    {
        var low = new AiTacticsBrain(new AiProfile(90, TierRank: 1, SchoolStyle.DefensiveMinded), aiCoeff: Ai);
        var s = Sit(inning: 9, outs: 0, first: P(), diff: -6, batter: P(power: 40, bunt: 60));
        for (ulong i = 0; i < 200; i++)
        {
            var d = low.CallDefense(s, new Xoshiro256Random(i));
            Assert.False(d.BuntShift);                 // シフトは上級のみ
            Assert.Equal(PitcherGear.Normal, d.Gear);  // ギア先読みは C 以上
        }
    }

    // ===== 盗塁の始動種別（ギャンブル始動, 設計書12 §5, G3b） =====

    // 際どい見込みの盗塁（好機だがギャンブル検討域）。Standard/SmallBall どちらの StealMinSuccess も
    // 超える帯（設計書15 Phase D-2d で「試みるか」と「始動種別」が1メソッドに統合されたため、
    // どちらの校風でも試み自体は発生する見込みを選ぶ）。
    private static TacticsSituation MarginalStealSit()
        => Sit(inning: 8, outs: 0, first: P(speed: 80, steal: 80), batter: P(power: 72));

    private static int CountGambles(SchoolStyle style, int tier, int n = 600)
    {
        var brain = new AiTacticsBrain(new AiProfile(90, tier, style), aiCoeff: Ai);
        var s = MarginalStealSit();
        var g = 0;
        for (ulong i = 0; i < (ulong)n; i++)
        {
            var d = brain.CallPitchAction(new PitchTacticsSituation(s, 0, 0, 0, null), new Xoshiro256Random(2000 + i));
            if (d?.StealAttempt == StartType.Gamble) g++;
        }
        return g;
    }

    [Fact]
    public void GambleStart_LowTier_NeverGambles()
    {
        // ②引き出し: ギャンブル始動は上級（C以上）。弱小校は好機でも通常始動に落ちる。
        Assert.Equal(0, CountGambles(SchoolStyle.SmallBall, tier: Ai.GambleStartMinTier - 1));
    }

    [Fact]
    public void GambleStart_HighTier_CanGamble()
    {
        Assert.True(CountGambles(SchoolStyle.SmallBall, tier: 7) > 0,
            "上級の機動力校は際どい盗塁でギャンブル始動を使うはず");
    }

    [Fact]
    public void GambleStart_SmallBallGamblesMoreThanStandard()
    {
        // ③校風: 機動力校はギャンブル始動を多用（同ティアの標準校より多い）。
        Assert.True(CountGambles(SchoolStyle.SmallBall, tier: 7) > CountGambles(SchoolStyle.Standard, tier: 7),
            "機動力校は標準校よりギャンブル始動が多いはず");
    }

    // ===== ③ 校風層: 同じ局面でも手が変わる =====

    private static int CountSteals(SchoolStyle style, int tier = 7)
    {
        var brain = new AiTacticsBrain(new AiProfile(90, tier, style), aiCoeff: Ai);
        var s = Sit(inning: 6, outs: 0, first: P(speed: 78, steal: 78), batter: P(power: 72)); // 強打者=バント条件外
        return CountStealAttempts(brain, s, 600);
    }

    [Fact]
    public void Style_SmallBall_StealsMoreThanStandard()
    {
        Assert.True(CountSteals(SchoolStyle.SmallBall) > CountSteals(SchoolStyle.Standard),
            "機動力野球は標準より盗塁企図が多いはず");
    }

    [Fact]
    public void Style_PowerHitting_BuntsLessThanStandard()
    {
        int Bunts(SchoolStyle style)
        {
            var brain = new AiTacticsBrain(new AiProfile(90, 7, style), aiCoeff: Ai);
            var s = Sit(inning: 8, outs: 0, first: P(), batter: P(power: 45, bunt: 60));
            return RollOffense(brain, s, 600).Count(x => x == OffensiveSign.SacrificeBunt);
        }
        Assert.True(Bunts(SchoolStyle.PowerHitting) < Bunts(SchoolStyle.Standard),
            "強打・待球はバントが少ないはず");
    }

    [Fact]
    public void Style_DefensiveMinded_ShiftsMoreThanStandard()
    {
        int Shifts(SchoolStyle style)
        {
            var brain = new AiTacticsBrain(new AiProfile(90, 7, style), aiCoeff: Ai);
            var s = Sit(inning: 8, outs: 0, first: P(), batter: P(power: 40, bunt: 60));
            var n = 0;
            for (ulong i = 0; i < 600; i++)
                if (brain.CallDefense(s, new Xoshiro256Random(i)).BuntShift) n++;
            return n;
        }
        Assert.True(Shifts(SchoolStyle.DefensiveMinded) > Shifts(SchoolStyle.Standard),
            "守り勝つ野球はバントシフトが多いはず");
    }

    // ===== ① 能力値層: 低采配能力ほど正着を外す（弱くても時々good play） =====

    [Fact]
    public void Ability_LowSense_ExecutesFewerCorrectBunts()
    {
        int Bunts(int sense)
        {
            var brain = new AiTacticsBrain(new AiProfile(sense, 7, SchoolStyle.Standard), aiCoeff: Ai);
            var s = Sit(inning: 8, outs: 0, first: P(), batter: P(power: 40, bunt: 60));
            return RollOffense(brain, s, 800).Count(x => x == OffensiveSign.SacrificeBunt);
        }
        var wise = Bunts(95);
        var foolish = Bunts(10);
        Assert.True(foolish < wise, $"凡将は正着の送りバントを取りこぼすはず: {foolish} vs {wise}");
        Assert.True(foolish > 0, "弱くても時々は正しく決める（完全ランダムではない）");
    }

    [Fact]
    public void Ability_OptimalProbability_RisesWithSense()
    {
        var dull = new AiTacticsBrain(new AiProfile(10, 7, SchoolStyle.Standard), aiCoeff: Ai);
        var sharp = new AiTacticsBrain(new AiProfile(95, 7, SchoolStyle.Standard), aiCoeff: Ai);
        Assert.True(sharp.OptimalProbability > dull.OptimalProbability);
        Assert.InRange(sharp.OptimalProbability, 0.5, 0.98);
    }

    // ===== 伝令: ティアで運用可否、豪腕依存は守備伝令を絞る =====

    [Fact]
    public void Timeout_LowTier_NeverUses()
    {
        var low = new AiTacticsBrain(new AiProfile(90, TierRank: 0, SchoolStyle.Standard), aiCoeff: Ai);
        var s = Sit() with { PitcherRattled = true, PressureIndex = 6 };
        for (ulong i = 0; i < 100; i++)
            Assert.False(low.CallDefenseTimeout(s, new Xoshiro256Random(i)));
    }

    [Fact]
    public void Timeout_AceDependent_VisitsMoundLessThanStandard()
    {
        int Visits(SchoolStyle style)
        {
            var brain = new AiTacticsBrain(new AiProfile(90, 7, style), aiCoeff: Ai);
            var s = Sit() with { PitcherRattled = true, PressureIndex = 6 };
            var n = 0;
            for (ulong i = 0; i < 600; i++)
                if (brain.CallDefenseTimeout(s, new Xoshiro256Random(i))) n++;
            return n;
        }
        Assert.True(Visits(SchoolStyle.AceDependent) < Visits(SchoolStyle.Standard),
            "豪腕依存はマウンドへ行く回数が少ないはず");
    }

    // ===== 同じ采配システム: 決定論・試合統合 =====

    [Fact]
    public void Game_WithAiBrains_IsDeterministic()
    {
        Team Build(string n, SchoolStyle style, int sense, int tier)
        {
            var order = new List<Player>
            {
                P(pos: FieldPosition.Catcher), P(pos: FieldPosition.FirstBase), P(pos: FieldPosition.SecondBase),
                P(pos: FieldPosition.ThirdBase), P(pos: FieldPosition.Shortstop), P(pos: FieldPosition.LeftField),
                P(pos: FieldPosition.CenterField), P(pos: FieldPosition.RightField),
                new Player { Position = FieldPosition.Pitcher, Name = n + "P", Pitching = PitcherAttributes.LeagueAverage },
            };
            return new Team
            {
                Name = n, BattingOrder = order, PitcherSlot = 8,
                Tactics = new AiTacticsBrain(new AiProfile(sense, tier, style), aiCoeff: Ai),
            };
        }
        var ctx = new GameContext();
        for (ulong s = 0; s < 5; s++)
        {
            var a = GameEngine.Play(Build("A", SchoolStyle.SmallBall, 70, 6), Build("H", SchoolStyle.DefensiveMinded, 40, 4), ctx, new Xoshiro256Random(s));
            var b = GameEngine.Play(Build("A", SchoolStyle.SmallBall, 70, 6), Build("H", SchoolStyle.DefensiveMinded, 40, 4), ctx, new Xoshiro256Random(s));
            Assert.Equal(a.AwayRuns, b.AwayRuns);
            Assert.Equal(a.HomeRuns, b.HomeRuns);
            Assert.Equal(a.TotalPitches, b.TotalPitches);
        }
    }

    // ===== 委任采配（§7）: プレイヤー自身の采配能力＋Standard校風で同じブレインを流用 =====

    [Fact]
    public void Delegated_UsesStandardStyle_AndPlayerSense()
    {
        var profile = AiProfile.Delegated(tacticalSense: 60, tierRank: 5);
        Assert.Equal(SchoolStyle.Standard, profile.Style);
        Assert.Equal(60, profile.TacticalSense);
        var brain = new AiTacticsBrain(profile, aiCoeff: Ai);
        // 委任でも共通の采配システムがそのまま動く（決定的に手を返す）。
        var s = Sit(inning: 8, outs: 0, first: P(), batter: P(power: 40, bunt: 60));
        var sign = brain.CallOffense(s, new Xoshiro256Random(3));
        Assert.True(sign is OffensiveSign.SacrificeBunt or OffensiveSign.Swing);
    }
}
