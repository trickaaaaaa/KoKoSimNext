using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Tactics;

/// <summary>
/// 敵AI・委任采配のDH解除（issue #1492, 設計書09 §6・設計書11）。
/// 守りの上手いDHが負担の重い守備位置の下手な野手に代わり、その野手の打順へ投手が入る（DH消滅）。
/// リードを守る終盤限定・守備の改善が打力の損失を上回るときだけ。<see cref="StandardTacticsBrain"/> は恒等のまま
/// ＝統計シム（games_10k/tactics）の帯・決定論baseline は動かないことも併せて保証する。
/// </summary>
public sealed class DhReleaseTacticsTests
{
    private static Player At(FieldPosition pos, string name, int fielding = 50, int contact = 50, int power = 50)
        => new() { Position = pos, Name = name, Fielding = fielding, Contact = contact, Power = power };

    private const int DhSlot = 8;

    /// <summary>打順スロット順の8守備位置＋DH（スロット8）。遊撃だけ下手（30）、DHは守備が上手い（80）。</summary>
    private static (List<Player> Lineup, List<bool> Swappable) Lineup(
        int shortstopFielding = 30, int dhFielding = 80, int shortstopBat = 50)
    {
        var lineup = new List<Player>
        {
            At(FieldPosition.Catcher, "捕", 50),
            At(FieldPosition.FirstBase, "一", 50),
            At(FieldPosition.SecondBase, "二", 50),
            At(FieldPosition.ThirdBase, "三", 50),
            At(FieldPosition.Shortstop, "遊", shortstopFielding, shortstopBat, shortstopBat),
            At(FieldPosition.LeftField, "左", 50),
            At(FieldPosition.CenterField, "中", 50),
            At(FieldPosition.RightField, "右", 50),
            At(FieldPosition.Catcher, "DH", dhFielding),
        };
        var swappable = Enumerable.Range(0, 9).Select(i => i != DhSlot).ToList();
        return (lineup, swappable);
    }

    private static Player Pitcher(int contact = 20, int power = 20)
        => new() { Name = "投", Position = FieldPosition.Pitcher, Contact = contact, Power = power, Pitching = PitcherAttributes.LeagueAverage };

    private static DhReleaseSituation Sit(
        int inning = 8, int scoreDiff = 1, int shortstopFielding = 30, int dhFielding = 80,
        int shortstopBat = 50, Player? pitcher = null)
    {
        var (lineup, swappable) = Lineup(shortstopFielding, dhFielding, shortstopBat);
        return new DhReleaseSituation(inning, 9, scoreDiff, lineup, swappable, DhSlot, pitcher ?? Pitcher());
    }

    private static EnemyAiCoefficients Enabled(int minTier = 0) => new() { DhReleaseMinTier = minTier };

    private static int CountReleases(ITacticsBrain brain, DhReleaseSituation s, int n = 300)
    {
        var count = 0;
        for (ulong seed = 1; seed <= (ulong)n; seed++)
        {
            if (brain.CallReleaseDh(s, new Xoshiro256Random(seed)) is not null) count++;
        }
        return count;
    }

    // ===== StandardTacticsBrain は恒等（統計シムの帯・決定論baseline 不変の担保） =====

    [Fact]
    public void Standard_NeverReleasesDh_AndDrawsNoRng()
    {
        var brain = new StandardTacticsBrain();
        var rng = new Xoshiro256Random(11);
        var before = rng.CaptureState();

        Assert.Null(brain.CallReleaseDh(Sit(), rng));
        Assert.Equal(before, rng.CaptureState());
    }

    // ===== 選ぶケース／選ばないケース =====

    [Fact]
    public void Ai_ReleasesDh_AtWeakestHeavyPosition_WhenLeadingLate()
    {
        var ai = new AiTacticsBrain(new AiProfile(100, 7, SchoolStyle.Standard), aiCoeff: Enabled());
        var s = Sit(inning: 8, scoreDiff: 1);

        Assert.True(CountReleases(ai, s) > 240, "リードを守る終盤は高頻度で解除を選ぶはず");
        Assert.Equal(FieldPosition.Shortstop, ai.CallReleaseDh(s, new Xoshiro256Random(1)));
    }

    [Fact]
    public void Ai_DoesNotRelease_OutsideLeadProtectingLate()
    {
        var ai = new AiTacticsBrain(new AiProfile(100, 7, SchoolStyle.Standard), aiCoeff: Enabled());

        Assert.Equal(0, CountReleases(ai, Sit(inning: 7, scoreDiff: 1)));    // 終盤でない
        Assert.Equal(0, CountReleases(ai, Sit(inning: 9, scoreDiff: 0)));    // 同点
        Assert.Equal(0, CountReleases(ai, Sit(inning: 9, scoreDiff: -1)));   // ビハインド（打線を弱めない）
        Assert.Equal(0, CountReleases(ai, Sit(inning: 9, scoreDiff: 5)));    // 大差（動かす必要がない）
    }

    [Fact]
    public void Ai_DoesNotRelease_WhenDefensiveGainIsTooSmall()
    {
        var ai = new AiTacticsBrain(new AiProfile(100, 7, SchoolStyle.Standard), aiCoeff: Enabled());

        // DHの守備が野手と同程度（遊撃の下手さ＝30、DH＝35）＝改善が下限未満。
        Assert.Equal(0, CountReleases(ai, Sit(shortstopFielding: 30, dhFielding: 35)));
        // 野手のほうが上手い。
        Assert.Equal(0, CountReleases(ai, Sit(shortstopFielding: 80, dhFielding: 30)));
    }

    [Fact]
    public void Ai_DoesNotRelease_WhenBattingLossOutweighsDefensiveGain()
    {
        var ai = new AiTacticsBrain(new AiProfile(100, 7, SchoolStyle.Standard), aiCoeff: Enabled());

        // 守備の改善は 1.0×(80−30)=50。遊撃手の打力が極端に高く投手の打力が低いと、損失が改善を上回る。
        var heavyBat = Sit(shortstopBat: 200, pitcher: Pitcher(contact: 0, power: 0));
        Assert.Equal(0, CountReleases(ai, heavyBat));
        // 同じ守備差でも打てない遊撃手なら解除する。
        Assert.True(CountReleases(ai, Sit(shortstopBat: 20)) > 240);
    }

    [Fact]
    public void Ai_NeverMovesCatcher()
    {
        var ai = new AiTacticsBrain(new AiProfile(100, 7, SchoolStyle.Standard), aiCoeff: Enabled());
        var (lineup, swappable) = Lineup(shortstopFielding: 90, dhFielding: 95);
        lineup[0] = lineup[0] with { Fielding = 1 };   // 捕手が極端に下手でも動かさない（専門職）
        var s = new DhReleaseSituation(9, 9, 1, lineup, swappable, DhSlot, Pitcher());

        for (ulong seed = 1; seed <= 100; seed++)
            Assert.NotEqual(FieldPosition.Catcher, ai.CallReleaseDh(s, new Xoshiro256Random(seed)));
    }

    // ===== ② ティア関門 × ③ 校風 × ① 能力値 =====

    [Fact]
    public void Ai_DefaultCoefficients_GateAtTierC_AndDrawNoRngBelow()
    {
        var below = new AiTacticsBrain(new AiProfile(100, 3, SchoolStyle.Standard));
        var rng = new Xoshiro256Random(3);
        var before = rng.CaptureState();
        Assert.Null(below.CallReleaseDh(Sit(), rng));
        Assert.Equal(before, rng.CaptureState());

        Assert.True(CountReleases(new AiTacticsBrain(new AiProfile(100, 4, SchoolStyle.Standard)), Sit()) > 240);
    }

    [Fact]
    public void Ai_TierGateAndSchoolStyle()
    {
        var coeff = Enabled(minTier: 5);
        var s = Sit();

        Assert.True(CountReleases(new AiTacticsBrain(new AiProfile(100, 5, SchoolStyle.Standard), aiCoeff: coeff), s) > 240);
        Assert.Equal(0, CountReleases(new AiTacticsBrain(new AiProfile(100, 5, SchoolStyle.AceDependent), aiCoeff: coeff), s));
        Assert.True(CountReleases(new AiTacticsBrain(new AiProfile(100, 4, SchoolStyle.DefensiveMinded), aiCoeff: coeff), s) > 240);
        Assert.Equal(0, CountReleases(new AiTacticsBrain(new AiProfile(100, 4, SchoolStyle.Standard), aiCoeff: coeff), s));
    }

    [Fact]
    public void Ai_TacticalSense_ShiftsFireRate()
    {
        var s = Sit();
        var sharp = CountReleases(new AiTacticsBrain(new AiProfile(100, 7, SchoolStyle.Standard), aiCoeff: Enabled()), s);
        var dull = CountReleases(new AiTacticsBrain(new AiProfile(0, 7, SchoolStyle.Standard), aiCoeff: Enabled()), s);

        Assert.True(dull < sharp, $"采配能力が低い方が発火率が低いはず (dull={dull}, sharp={sharp})");
    }

    // ===== engine への結線 =====

    private static Team DhTeam(ITacticsBrain? brain)
    {
        var positions = new[]
        {
            FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase, FieldPosition.ThirdBase,
            FieldPosition.Shortstop, FieldPosition.LeftField, FieldPosition.CenterField, FieldPosition.RightField,
        };
        var order = positions.Select(p => At(p, $"{p}先発", fielding: p == FieldPosition.Shortstop ? 20 : 50)).ToList();
        order.Add(At(FieldPosition.Catcher, "DH打者", fielding: 90));
        return new Team
        {
            Name = "DH校",
            BattingOrder = order,
            DhSlot = 8,
            StartingPitcher = At(FieldPosition.Pitcher, "先発P") with { Pitching = PitcherAttributes.LeagueAverage },
            Tactics = brain,
        };
    }

    [Fact]
    public void Engine_AiBrain_ReleasesDh_ThroughGameLoop()
    {
        // 解除が実際に試合の流れで起きる（＝入口が engine に結線されている）。1回から・点差不問・下限なしに開けて確かめる。
        var coeff = new EnemyAiCoefficients
        {
            DhReleaseMinTier = 0, DhReleaseFromInning = 1, DhReleaseMaxLead = 99, DhReleaseMinGain = 0.5,
        };
        var ai = new AiTacticsBrain(new AiProfile(100, 7, SchoolStyle.Standard), aiCoeff: coeff);
        var released = 0;
        for (ulong seed = 1; seed <= 10; seed++)
        {
            var p = GameEngine.NewProgress(DhTeam(null), DhTeam(ai), new GameContext(), new Xoshiro256Random(seed));
            foreach (var _ in GameEngine.Steps(p)) { /* drain */ }
            // 守備に就かせる位置のどれかに DH打者 が居る＝解除されている（DHが残ると UsesDh は true のまま）。
            if (!p.Home.UsesDh) released++;
        }
        Assert.True(released > 0, "有効化したAIはどこかの試合でDHを解除するはず");
    }

    [Fact]
    public void Engine_StandardBrainOrNoBrain_NeverReleasesDh()
    {
        foreach (ITacticsBrain? brain in new ITacticsBrain?[] { null, new StandardTacticsBrain() })
        {
            var p = GameEngine.NewProgress(DhTeam(null), DhTeam(brain), new GameContext(), new Xoshiro256Random(1));
            foreach (var _ in GameEngine.Steps(p)) { /* drain */ }
            Assert.True(p.Home.UsesDh);
        }
    }
}
