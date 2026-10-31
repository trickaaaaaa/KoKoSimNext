using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 伝令役の人選（issue #716・設計書09 §3）。
/// ・伝令役の係数は 統率傾向／学年／同学年／性格の得意場面 で増減し、0.7〜1.4 に収まる。
/// ・伝令役なし＝係数1.0（#714 と同じ効き目）。
/// ・AI監督は采配能力が高ければ最適な控え、低ければベンチの先頭を走らせる（乱数不使用）。
/// </summary>
public sealed class TimeoutMessengerTests
{
    private static readonly TacticsCoefficients C = new();
    private static readonly PersonalityCoefficients Pc = new();

    private static Player P(string name, int grade = 2, int leadership = 50, Personality pers = Personality.Normal)
        => new() { Name = name, Grade = grade, Leadership = leadership, Personality = pers };

    private static readonly Player Receiver = P("受け手", grade: 2);

    private static double F(Player? m, bool toPitcher = true, Player? receiver = null)
        => TimeoutMessenger.Factor(m, receiver ?? Receiver, toPitcher, C, Pc);

    [Fact]
    public void NoMessenger_IsNeutral() => Assert.Equal(1.0, F(null));

    [Fact]
    public void Neutral2ndYear_DifferentGrade_IsNeutral()
        => Assert.Equal(1.0, F(P("m", grade: 2), receiver: P("r", grade: 3)), 10);

    [Fact]
    public void Leadership_RaisesFactor()
    {
        var other = P("r", grade: 3);
        Assert.True(F(P("lo", leadership: 30), receiver: other) < F(P("hi", leadership: 80), receiver: other));
        Assert.Equal(1.18, F(P("hi", leadership: 80), receiver: other), 10);
    }

    [Fact]
    public void Senior_Beats_Freshman()
    {
        var r = P("r", grade: 2);
        Assert.Equal(1.08, F(P("3年", grade: 3), receiver: r), 10);
        Assert.Equal(0.92, F(P("1年", grade: 1), receiver: r), 10);
    }

    [Fact]
    public void SameGrade_AddsBonus()
        => Assert.Equal(1.06, F(P("同期", grade: 2), receiver: P("r", grade: 2)), 10);

    [Fact]
    public void UnsetGrade_AddsNothing()
        => Assert.Equal(1.0, F(P("m", grade: 0), receiver: P("r", grade: 0)), 10);

    [Fact]
    public void Personality_HasFavoredScene()
    {
        var r = P("r", grade: 3);
        var hot = P("熱血", pers: Personality.HotBlood);
        var mood = P("ムード", pers: Personality.MoodMaker);
        Assert.True(F(hot, toPitcher: true, receiver: r) > F(mood, toPitcher: true, receiver: r), "熱血漢はマウンドへの伝令に強い");
        Assert.True(F(mood, toPitcher: false, receiver: r) > F(hot, toPitcher: false, receiver: r), "ムードメーカーは打者への伝令に強い");
        Assert.True(F(P("一匹狼", pers: Personality.LoneWolf), receiver: r) < 1.0, "一匹狼は伝令役に不向き");
    }

    [Fact]
    public void Factor_IsClamped()
    {
        var best = P("best", grade: 3, leadership: 99, pers: Personality.HotBlood);
        var worst = P("worst", grade: 1, leadership: 1, pers: Personality.Introvert);
        Assert.Equal(C.MessengerFactorMax, F(best, receiver: P("r", grade: 3)), 10);
        Assert.Equal(C.MessengerFactorMin, F(worst, receiver: P("r", grade: 3)), 10);
    }

    [Fact]
    public void Calm_ScalesWithMessengerFactor()
    {
        var plain = PressureModel.CalmMultiplier(5, 50, 1.0, C);
        Assert.Equal(plain, PressureModel.CalmMultiplier(5, 50, 1.0, C, 1.0));
        Assert.Equal(1.0 + 0.06 * 1.4, PressureModel.CalmMultiplier(5, 50, 1.0, C, 1.4), 10);
        Assert.Equal(1.0 + 0.06 * 0.7, PressureModel.CalmMultiplier(5, 50, 1.0, C, 0.7), 10);
    }

    [Fact]
    public void AiPick_HighSense_PicksBest_LowSense_PicksFirst()
    {
        var first = P("先頭", grade: 1, leadership: 30, pers: Personality.Introvert);
        var best = P("最適", grade: 3, leadership: 80, pers: Personality.HotBlood);
        var reserves = new[] { first, P("中", grade: 2), best };
        Assert.Same(best, TimeoutMessenger.AiPick(reserves, Receiver, true, 80, C, Pc));
        Assert.Same(first, TimeoutMessenger.AiPick(reserves, Receiver, true, 40, C, Pc));
        Assert.Null(TimeoutMessenger.AiPick(System.Array.Empty<Player>(), Receiver, true, 80, C, Pc));
    }

    [Fact]
    public void TeamState_RemembersMessenger_PerCall()
    {
        var order = new Player[9];
        for (var i = 0; i < 9; i++) order[i] = new Player { Name = "P" + i, Position = (KokoSim.Engine.Match.Field.FieldPosition)i };
        var ts = new TeamState(new Team { Name = "T", BattingOrder = order, PitcherSlot = 0 });
        var m = P("伝令");
        ts.StartDefenseCalm(3, m);
        Assert.Same(m, ts.DefenseMessenger);
        ts.StartDefenseCalm(3);
        Assert.Null(ts.DefenseMessenger);
    }
}
