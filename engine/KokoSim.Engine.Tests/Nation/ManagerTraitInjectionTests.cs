using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// Issue #55: 監督傾向がゲーム全体の采配活動量（TacticsTally・継投回数・エース球数）へ有意差として
/// 現れることの結合煙テスト。<see cref="ManagerTraitEffectsTests"/> が係数変換単体を検証するのに対し、
/// こちらは本流の配線（School.ManagerTraits → BrainFor/ForSchool → GameEngine.Play）を通す。
/// 校風テスト（<see cref="EnemyAiInjectionTests"/>）と同型で、傾向以外を同一に揃えて差を分離する。
/// </summary>
public sealed class ManagerTraitInjectionTests
{
    private static School Sch(int id, double strength, params ManagerTrait[] traits) => new()
    {
        Id = id, Name = $"校{id}", PrefectureId = 0,
        Strength = strength, TacticalSense = 90, ManagerTraits = traits,
    };

    private static readonly FieldPosition[] Positions =
    {
        FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase,
        FieldPosition.ThirdBase, FieldPosition.Shortstop, FieldPosition.LeftField,
        FieldPosition.CenterField, FieldPosition.RightField,
    };

    // --- 盗塁・バント・スクイズ機会を稼働させる打線（校風テストと同型） ---

    private static Team SpeedyOnBaseTeam(ITacticsBrain tactics)
    {
        var order = Positions
            .Select(pos => new Player
            {
                Position = pos, Contact = 75, Power = 50, Discipline = 80, Speed = 88, Steal = 88, Fielding = 50,
            })
            .Append(new Player { Position = FieldPosition.Pitcher, Pitching = PitcherAttributes.LeagueAverage })
            .ToList();
        return new Team { Name = "テスト校", BattingOrder = order, PitcherSlot = 8, Tactics = tactics };
    }

    private static Team LowPowerOnBaseTeam(ITacticsBrain tactics)
    {
        var order = Positions
            .Select(pos => new Player
            {
                Position = pos, Contact = 70, Power = 45, Discipline = 85, Bunt = 70, Speed = 55, Fielding = 50,
            })
            .Append(new Player { Position = FieldPosition.Pitcher, Pitching = PitcherAttributes.LeagueAverage })
            .ToList();
        return new Team { Name = "テスト校", BattingOrder = order, PitcherSlot = 8, Tactics = tactics };
    }

    // スクイズ機会（三塁走者・終盤・接戦・好バント打者）を稼働させる高出塁＋高走力＋好バント打線。
    private static Team BuntyOnBaseTeam(ITacticsBrain tactics)
    {
        var order = Positions
            .Select(pos => new Player
            {
                Position = pos, Contact = 80, Power = 45, Discipline = 82, Bunt = 80, Speed = 85, Steal = 80, Fielding = 50,
            })
            .Append(new Player { Position = FieldPosition.Pitcher, Pitching = PitcherAttributes.LeagueAverage })
            .ToList();
        return new Team { Name = "テスト校", BattingOrder = order, PitcherSlot = 8, Tactics = tactics };
    }

    // 弱打＋明確に上回る控え（代打が起きやすい）。校風テストの WeakBattingTeamWithBench と同型。
    private static Team WeakBattingTeamWithBench(ITacticsBrain tactics)
    {
        var order = Positions
            .Select(pos => new Player { Position = pos, Contact = 30, Power = 40, Speed = 35, Fielding = 35, Discipline = 40 })
            .Append(new Player { Position = FieldPosition.Pitcher, Pitching = PitcherAttributes.LeagueAverage })
            .ToList();
        var bench = Enumerable.Range(0, 6)
            .Select(_ => new Player { Position = FieldPosition.LeftField, Contact = 88, Power = 60, Speed = 85, Fielding = 85 })
            .ToList();
        return new Team { Name = "テスト校", BattingOrder = order, PitcherSlot = 8, Bench = bench, Tactics = tactics };
    }

    [Fact]
    [Trait("Category", "Heavy")]
    public void BuntHeavy_BuntsMoreThanPlain()
    {
        var bunty = Sch(410, 65, ManagerTrait.BuntHeavy);
        var plain = Sch(410, 65); // 同Id＝傾向以外は同一プロファイル
        var opponent = StrengthTeamFactory.Create(60, "対戦相手", new Xoshiro256Random(8484));

        int Bunts(School s)
        {
            var team = LowPowerOnBaseTeam(EnemyAiFactory.BrainFor(s));
            var total = 0;
            const int games = 300;
            for (ulong i = 0; i < games; i++)
                total += GameEngine.Play(team, opponent, new GameContext(), new Xoshiro256Random(9000 + i)).AwayTactics.SacrificeBunts;
            return total;
        }

        var bh = Bunts(bunty);
        var st = Bunts(plain);
        Assert.True(bh > st, $"バント多用監督は標準より犠打が多いはず: buntHeavy={bh} plain={st}");
    }

    [Fact]
    [Trait("Category", "Heavy")]
    public void RunAndGun_StealsMoreThanPlain()
    {
        var run = Sch(420, 65, ManagerTrait.RunAndGun);
        var plain = Sch(420, 65);
        var opponent = StrengthTeamFactory.Create(60, "対戦相手", new Xoshiro256Random(4242));

        int Steals(School s)
        {
            var team = SpeedyOnBaseTeam(EnemyAiFactory.BrainFor(s));
            var total = 0;
            const int games = 100;
            for (ulong i = 0; i < games; i++)
                total += GameEngine.Play(team, opponent, new GameContext(), new Xoshiro256Random(7000 + i)).AwayTactics.StealAttempts;
            return total;
        }

        var rg = Steals(run);
        var st = Steals(plain);
        Assert.True(rg > st, $"盗塁好き監督は標準より盗塁企図が多いはず: runAndGun={rg} plain={st}");
    }

    [Fact]
    [Trait("Category", "Heavy")]
    public void SqueezeLover_SqueezesMoreThanPlain()
    {
        // スクイズは SqueezeMinTier=5(B) 以上＝強さ75(=Tier B) で引き出しに入る。
        var squeeze = Sch(430, 75, ManagerTrait.SqueezeLover);
        var plain = Sch(430, 75);
        var opponent = StrengthTeamFactory.Create(60, "対戦相手", new Xoshiro256Random(3131));

        int Squeezes(School s)
        {
            var team = BuntyOnBaseTeam(EnemyAiFactory.BrainFor(s));
            var total = 0;
            const int games = 400;   // スクイズ機会は薄い（三塁走者・終盤・接戦）ので標本を厚くする
            for (ulong i = 0; i < games; i++)
                total += GameEngine.Play(team, opponent, new GameContext(), new Xoshiro256Random(13000 + i)).AwayTactics.Squeezes;
            return total;
        }

        var sq = Squeezes(squeeze);
        var st = Squeezes(plain);
        Assert.True(sq > st, $"スクイズ好き監督は標準よりスクイズが多いはず: squeezeLover={sq} plain={st}");
    }

    [Fact]
    [Trait("Category", "Heavy")]
    public void AggressivePinchHit_SubstitutesMoreThanPlain()
    {
        // 両者とも代打の引き出しがある高ティア(強さ75=B)にし、差を「発動の積極さ」だけに絞る。
        var pinch = Sch(440, 75, ManagerTrait.AggressivePinchHit);
        var plain = Sch(440, 75);
        var opponent = StrengthTeamFactory.Create(60, "対戦相手", new Xoshiro256Random(2727));

        int Subs(School s)
        {
            var team = WeakBattingTeamWithBench(EnemyAiFactory.BrainFor(s));
            var total = 0;
            const int games = 120;
            for (ulong i = 0; i < games; i++)
                total += GameEngine.Play(team, opponent, new GameContext(), new Xoshiro256Random(15000 + i)).AwaySubstitutions;
            return total;
        }

        var ph = Subs(pinch);
        var st = Subs(plain);
        Assert.True(ph > st, $"代打積極監督は標準より交代（代打）が多いはず: aggressive={ph} plain={st}");
    }

    [Fact]
    [Trait("Category", "Heavy")]
    public void AceOveruse_ThrowsAceLongerThanQuickHook()
    {
        // 同Id＝同一ロースター。差は継投しきい値（Team.Fatigue, 決定4: B-1）だけ。
        var overuse = Sch(500, 70, ManagerTrait.AceOveruse);
        var quick = Sch(500, 70, ManagerTrait.QuickHook);
        var oppSchool = Sch(501, 60);

        (int aceP, int changes) Run(School s)
        {
            var team = StrengthTeamFactory.ForSchool(s, yearIndex: 1);
            var opp = StrengthTeamFactory.ForSchool(oppSchool, yearIndex: 1);
            int aceP = 0, changes = 0;
            const int games = 60;
            for (ulong i = 0; i < games; i++)
            {
                var r = GameEngine.Play(team, opp, new GameContext(), new Xoshiro256Random(17000 + i));
                if (r.AwayPitching.Count > 0) aceP += r.AwayPitching[0].Pitches; // 先発＝エースの球数
                changes += r.AwayPitching.Count - 1;                              // away の継投回数
            }
            return (aceP, changes);
        }

        var (overuseAce, overuseChanges) = Run(overuse);
        var (quickAce, quickChanges) = Run(quick);
        Assert.True(overuseAce > quickAce,
            $"エース酷使はエースを引っ張る＝球数が多いはず: overuse={overuseAce} quick={quickAce}");
        Assert.True(overuseChanges < quickChanges,
            $"継投早めの方が継投回数が多いはず: overuse={overuseChanges} quick={quickChanges}");
    }
}
