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
/// 投手と野手が同じ試合で延々と入れ替わる往復の再発防止（issue #1742）。
/// 継投の人選が持ち回しの疲れを見ず、降板した元投手を疲れたままマウンドへ戻していた。
/// </summary>
public sealed class PitcherRoundTripTests
{
    private static PitcherAttributes Arm(int velLevel, double stamina)
        => new()
        {
            MaxVelocityKmh = PitcherAttributes.VelocityKmhFromLevel(velLevel),
            Control = 60,
            StaminaPitches = stamina,
            PitchRank = 55,
        };

    private static Player Field(string name, FieldPosition pos, PitcherAttributes? arm = null)
        => arm is null
            ? new Player { Name = name, Position = pos }
            : new Player { Name = name, Position = pos, Pitching = arm };

    /// <summary>控え投手ゼロ。先発「先発」の投手能力が最良、右翼「右」も投げられる＝issue の往復の組み合わせ。</summary>
    private static Team NoBullpenTeam(string name, double stamina = 60, EnemyAiCoefficients? ai = null)
    {
        var order = new List<Player>
        {
            Field("捕", FieldPosition.Catcher), Field("一", FieldPosition.FirstBase),
            Field("二", FieldPosition.SecondBase), Field("三", FieldPosition.ThirdBase),
            Field("遊", FieldPosition.Shortstop), Field("左", FieldPosition.LeftField),
            Field("中", FieldPosition.CenterField), Field("右", FieldPosition.RightField, Arm(80, stamina)),
            Field("先発", FieldPosition.Pitcher, Arm(90, stamina)),
        };
        return new Team
        {
            Name = name, BattingOrder = order, PitcherSlot = 8,
            Reserves = TeamReserves.Merge(System.Array.Empty<Player>(), new[] { Field("控", FieldPosition.FirstBase) }),
            Tactics = new AiTacticsBrain(new AiProfile(90, 6, SchoolStyle.DefensiveMinded), aiCoeff: ai),
        };
    }

    private static readonly FatigueCoefficients Fatigue = new();

    private static Player At(TeamState st, FieldPosition pos) => st.PlayerAtPosition(pos)!;

    /// <summary>先発が限界まで投げて右翼と入れ替わった状態（右翼から来た投手が今マウンド）。</summary>
    private static TeamState AfterOneSwap()
    {
        var st = new TeamState(NoBullpenTeam("T"));
        st.NoteInning(5);
        st.AddPitches(120);
        Assert.True(st.RelieveWith(st.CurrentLineup.First(p => p.Name == "右"), null, allowBenchBackfill: false));
        Assert.Equal("右", st.CurrentPitcher.Name);
        Assert.Equal("先発", At(st, FieldPosition.RightField).Name);
        return st;
    }

    [Fact]
    public void SpentFormerPitcher_IsNotOfferedAsReliever()
    {
        var st = AfterOneSwap();
        st.NoteInning(7);   // 回が変わっても、疲れは持ち回しで残る
        st.AddPitches(120);

        var names = st.ReliefCandidatesForAi(Fatigue, 7).Select(p => p.Name).ToList();

        Assert.DoesNotContain("先発", names);   // 疲れたまま戻して往復させない
        Assert.Contains("控", names);
    }

    [Fact]
    public void RestedFormerPitcher_CanStillBeOfferedInALaterInning()
    {
        var st = new TeamState(NoBullpenTeam("T"));
        st.NoteInning(2);
        st.AddPitches(10);   // 少し投げただけ
        Assert.True(st.RelieveWith(st.CurrentLineup.First(p => p.Name == "右"), null, allowBenchBackfill: false));

        Assert.Contains("先発", st.ReliefCandidatesForAi(Fatigue, 4).Select(p => p.Name));
    }

    [Fact]
    public void FormerPitcher_IsNotOfferedInTheInningHeWasMovedToField()
    {
        var st = new TeamState(NoBullpenTeam("T"));
        st.NoteInning(2);
        st.AddPitches(10);
        Assert.True(st.RelieveWith(st.CurrentLineup.First(p => p.Name == "右"), null, allowBenchBackfill: false));

        Assert.DoesNotContain("先発", st.ReliefCandidatesForAi(Fatigue, 2).Select(p => p.Name));
    }

    [Fact]
    public void PickReliever_NeverReturnsSpentAce_EvenWhenHeHasTheBestScore()
    {
        var st = AfterOneSwap();
        st.NoteInning(7);
        st.AddPitches(120);
        var s = new PitchingChangeSituation(
            7, 9, 0, 0, 0, false, true, false, 120, 60, 10, true, st.ReliefCandidatesForAi(Fatigue, 7));

        var pick = AiTacticsBrain.PickReliever(s, new SkillCoefficients());

        Assert.NotNull(pick);
        Assert.NotEqual("先発", pick!.Name);
    }

    [Fact]
    public void ReturningToTheMoundTwiceInOneInning_IsRejected_ButAllowedNextInning()
    {
        // 規則5.10: 投手が守備へ回った回に、投手へ戻れるのは1度まで。
        var st = new TeamState(NoBullpenTeam("T"));
        st.NoteInning(6);
        Assert.True(st.RelieveWith(st.CurrentLineup.First(p => p.Name == "右"), null, allowBenchBackfill: false)); // 先発→右翼
        Assert.True(st.RelieveWith(At(st, FieldPosition.RightField), null, allowBenchBackfill: false));           // 先発が戻る（1度目）
        Assert.True(st.RelieveWith(At(st, FieldPosition.RightField), null, allowBenchBackfill: false));           // 右が再登板＝先発はまた右翼へ
        Assert.False(st.RelieveWith(At(st, FieldPosition.RightField), null, allowBenchBackfill: false));          // 先発が戻る（2度目）は不可
        Assert.Equal("右", st.CurrentPitcher.Name);

        st.NoteInning(7);
        Assert.True(st.RelieveWith(At(st, FieldPosition.RightField), null, allowBenchBackfill: false));
        Assert.Equal("先発", st.CurrentPitcher.Name);
    }

    [Fact]
    public void AdvancedTriggers_DoNotChangeAgainInTheInningAPitcherWasAlreadyChanged()
    {
        // 崩れ（回の失点）は継いだ投手にも付いて回る。同じ回に替えた直後は高度トリガーで替え続けない。
        var coeff = new EnemyAiCoefficients { BlowupReliefMinTier = 0, BlowupRunsInInning = 1 };
        var brain = new AiTacticsBrain(new AiProfile(90, 6, SchoolStyle.DefensiveMinded), aiCoeff: coeff);
        var st = new TeamState(NoBullpenTeam("T"));
        PitchingChangeSituation Sit(bool changed) => new(
            3, 9, 1, 0, 3, false, false, false, 20, 100, 25, true, st.EligibleReliefCandidates,
            PitcherChangedThisInning: changed);

        var fresh = 0;
        for (ulong seed = 1; seed <= 30; seed++)
        {
            if (brain.CallPitchingChange(Sit(false), new Xoshiro256Random(seed)) is not null) fresh++;
            Assert.Null(brain.CallPitchingChange(Sit(true), new Xoshiro256Random(seed)));
        }
        Assert.True(fresh > 0, "回の最初の崩れでは従来どおり替える");
    }

    [Fact]
    public void FatigueTrigger_StillChangesInTheSameInning()
    {
        var brain = new AiTacticsBrain(new AiProfile(90, 6, SchoolStyle.DefensiveMinded));
        var st = new TeamState(NoBullpenTeam("T"));
        var s = new PitchingChangeSituation(
            3, 9, 1, 0, 0, false, true, false, 200, 100, 25, true, st.EligibleReliefCandidates,
            PitcherChangedThisInning: true);

        Assert.NotNull(brain.CallPitchingChange(s, new Xoshiro256Random(1)));
    }

    [Fact]
    public void Game_AiWithNoBullpen_DoesNotPingPongPitchersAndFielders()
    {
        // 控え投手ゼロの校どうし。往復していた頃は同じ2人が何度も入れ替わった。
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var r = GameEngine.Play(NoBullpenTeam("A", 100.5), NoBullpenTeam("H", 100.5),
                new GameContext(), new Xoshiro256Random(seed));
            foreach (var away in new[] { true, false })
            {
                var log = r.PitchingChangeLog.Where(e => e.DefenseIsAway == away).ToList();
                foreach (var g in log.GroupBy(e => (e.Inning, e.Change.InPitcherName)))
                    Assert.True(g.Count() <= 1, $"seed {seed}: {g.Key.InPitcherName} が{g.Key.Inning}回に{g.Count()}度登板");
                foreach (var g in log.GroupBy(e => e.Inning))
                    Assert.True(g.Count() <= 2, $"seed {seed}: {g.Key}回に{g.Count()}回も投手が替わった");
                // 一度マウンドを降りた投手が戻るのは1度まで（AIは2度目を選ばない）。
                var returns = new Dictionary<string, int>();
                var left = new HashSet<string>();
                foreach (var e in log)
                {
                    if (left.Contains(e.Change.InPitcherName))
                    {
                        returns[e.Change.InPitcherName] = returns.GetValueOrDefault(e.Change.InPitcherName) + 1;
                        Assert.True(returns[e.Change.InPitcherName] <= 1,
                            $"seed {seed}: {e.Change.InPitcherName} が{e.Inning}回に2度目のマウンド復帰");
                    }
                    left.Add(e.Change.OutPitcherName);
                }
            }
        }
    }
}
