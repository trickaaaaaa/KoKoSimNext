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
/// Issue #40: 相手校への敵AI采配（AiTacticsBrain）配線の結合煙テスト。
/// <see cref="EnemyAiTests"/> がブレイン単体の判断ロジックを検証するのに対し、こちらは
/// 本流の配線（School → <see cref="EnemyAiFactory.BrainFor"/> → <see cref="Team.Tactics"/>
/// → <see cref="GameEngine.Play"/>）を通したときに、ティア・校風の差が試合結果の集計に現れることを確認する。
/// </summary>
public sealed class EnemyAiInjectionTests
{
    private static School Sch(int id, double strength, SchoolStyle style = SchoolStyle.Standard, int tacticalSense = 90)
        => new()
        {
            Id = id, Name = $"校{id}", PrefectureId = 0,
            Strength = strength, Style = style, TacticalSense = tacticalSense,
        };

    private static readonly FieldPosition[] Positions =
    {
        FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase,
        FieldPosition.ThirdBase, FieldPosition.Shortstop, FieldPosition.LeftField,
        FieldPosition.CenterField, FieldPosition.RightField,
    };

    /// <summary>
    /// 打者全員が弱打(Contact低)・控えが明確に上回る手持ち（設計書09 §6のPinchHit/DefensiveSub条件を満たしやすい）。
    /// ロースター自体は StrengthTeamFactory の乱数生成に委ねず固定するので、代打が「起きるかどうか」は
    /// 純粋にブレインのティアゲート（EnemyAiFactory.BrainFor(school) の TierRank）だけで決まる。
    /// </summary>
    private static Team WeakBattingTeamWithBench(ITacticsBrain tactics)
    {
        var order = Positions
            .Select(pos => new Player { Position = pos, Contact = 30, Power = 40, Speed = 35, Fielding = 35, Discipline = 40 })
            .Append(new Player { Position = FieldPosition.Pitcher, Pitching = PitcherAttributes.LeagueAverage })
            .ToList();
        var bench = Enumerable.Range(0, 6)
            .Select(_ => new Player { Position = FieldPosition.LeftField, Contact = 88, Power = 60, Speed = 85, Fielding = 85 })
            .ToList();
        return new Team { Name = "テスト校", BattingOrder = order, PitcherSlot = 8, Reserves = bench, Tactics = tactics };
    }

    [Fact]
    [Trait("Category", "Heavy")] // 数十〜数百試合のフルシム集計（数十秒）
    public void HighTierSchool_SubstitutesOverManyGames_LowTierSchool_Never()
    {
        // S(7): 代打・代走・守備固めすべて引き出しにある。G(0): どれも PinchHitMinTier(2) 未満で運用しない。
        // ロースターは両者同一（上のWeakBattingTeamWithBench）にして、差が純粋にティアゲートから来ることを保証する。
        var high = Sch(301, strength: 95);
        var low = Sch(302, strength: 20);
        var opponent = StrengthTeamFactory.Create(60, "対戦相手", new Xoshiro256Random(1234));
        var highTeam = WeakBattingTeamWithBench(EnemyAiFactory.BrainFor(high));
        var lowTeam = WeakBattingTeamWithBench(EnemyAiFactory.BrainFor(low));

        var highSubs = 0;
        var lowSubs = 0;
        const int games = 60;
        for (ulong i = 0; i < games; i++)
        {
            highSubs += GameEngine.Play(highTeam, opponent, new GameContext(), new Xoshiro256Random(5000 + i)).AwaySubstitutions;
            lowSubs += GameEngine.Play(lowTeam, opponent, new GameContext(), new Xoshiro256Random(6000 + i)).AwaySubstitutions;
        }

        Assert.Equal(0, lowSubs); // ②ティアの引き出しにない＝偶発でも起きない
        Assert.True(highSubs > 0, $"高ティア校が{games}試合で一度も選手交代を使わなかった");
    }

    /// <summary>
    /// 全打者が高出塁(Discipline/Contact高)＋高走力(Speed/Steal高)＝1試合あたりの盗塁機会（一塁に足の
    /// 速い走者が乗る場面）を稼働させ、校風差のシグナルをノイズに埋もれさせない（StrengthTeamFactory の
    /// 平均的なロースターだと出塁・盗塁機会が薄く、100試合でも小標本ノイズに負けてしまう）。
    /// </summary>
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

    // 校風比較の2テストは同一ロースター（SpeedyOnBaseTeam / StrengthTeamFactory の同一Id）で Tactics の
    // Style だけを変え、ロースターの個体差が結果を左右しないようにする。

    [Fact]
    [Trait("Category", "Heavy")] // 数十〜数百試合のフルシム集計（数十秒）
    public void SchoolStyle_SmallBall_StealsMoreThanStandard_OverManyGames()
    {
        var smallBall = Sch(310, strength: 65, style: SchoolStyle.SmallBall);
        var standard = Sch(310, strength: 65, style: SchoolStyle.Standard); // 同Id=同校風以外は同一プロファイル
        var opponent = StrengthTeamFactory.Create(60, "対戦相手", new Xoshiro256Random(4242));

        // 校風の配線を見るテストなので、俊足の探針チーム（走力88/盗塁88）を額面どおり効かせる（効き＝恒等）。
        // 既定の効き 0.55 では 88→71 に寝て成功見込みが下がる＝校風差の信号が小標本ノイズに埋もれやすくなる
        // （閾値そのものは issue steal-thresholds-unreachable で実測レンジに合わせて 0.72→0.50 へ再校正済み）。
        var ctx = new GameContext().WithAbilityEffect(AbilityEffectCoefficients.Identity);
        int Steals(School school)
        {
            var team = SpeedyOnBaseTeam(EnemyAiFactory.BrainFor(school, baserunning: ctx.Baserunning));
            var total = 0;
            const int games = 100;
            for (ulong i = 0; i < games; i++)
                total += GameEngine.Play(team, opponent, ctx, new Xoshiro256Random(7000 + i)).AwayTactics.StealAttempts;
            return total;
        }

        var sb = Steals(smallBall);
        var st = Steals(standard);
        Assert.True(sb > st, $"機動力校は標準校より盗塁企図の総数が多いはず（結合後も校風差が出る）: smallBall={sb} standard={st}");
    }

    /// <summary>
    /// issue steal-thresholds-unreachable（2026-08-27）の回帰: 旧閾値0.72は実ロスターでは到達不能で、
    /// 盗塁企図がほぼ「無謀（EnemyAiCoefficients.RecklessOnMissProb）」由来だけになっていた。無謀は
    /// 采配ミス時に発火するので、采配力(TacticalSense)が高いほど無謀は減り＝**盗塁も減る**逆転が起きていた
    /// （設計書11の「校風・采配力が高いほど戦術を使いこなす」意図と逆）。閾値を実測レンジへ再校正した後は
    /// 「狙って仕掛ける」経路が働くため、採配力が高いほど盗塁が増える向きに戻ることを固定する。
    /// 既定の効き(0.55)のまま測る（SmallBall比較と違い、この逆転自体が既定の効きの下でだけ起きていたため）。
    /// </summary>
    [Fact]
    [Trait("Category", "Heavy")] // 数百試合のフルシム集計（数十秒）
    public void HigherTacticalSense_AttemptsMoreSteals_NotFewer()
    {
        var highSense = Sch(410, strength: 65, style: SchoolStyle.Standard, tacticalSense: 95);
        var lowSense = Sch(411, strength: 65, style: SchoolStyle.Standard, tacticalSense: 10); // 同Id以外は同一
        var opponent = StrengthTeamFactory.Create(60, "対戦相手", new Xoshiro256Random(4242));
        var ctx = new GameContext(); // 既定の効き（実ゲームと同じ）

        int Steals(School school)
        {
            var team = SpeedyOnBaseTeam(EnemyAiFactory.BrainFor(school, baserunning: ctx.Baserunning));
            var total = 0;
            const int games = 300;
            for (ulong i = 0; i < games; i++)
                total += GameEngine.Play(team, opponent, ctx, new Xoshiro256Random(7000 + i)).AwayTactics.StealAttempts;
            return total;
        }

        var high = Steals(highSense);
        var low = Steals(lowSense);
        Assert.True(high > low,
            $"采配力が高い校の方が盗塁企図が多いはず（逆転回帰）: sense95={high} sense10={low}");
    }

    /// <summary>
    /// 送りバントは Power&lt;=58・Bunt&gt;=40・7回以降の接戦でしか候補にならない（StandardTacticsBrain）。
    /// StrengthTeamFactory の強度65ロースターは Power が65前後でこの条件に乗らずほぼ発生しないため、
    /// 弱打・好バント・高出塁の打線を手組みして機会を稼働させる。
    /// </summary>
    private static Team LowPowerOnBaseTeam(ITacticsBrain tactics)
    {
        var order = Positions
            .Select(pos => new Player
            {
                Position = pos, Contact = 70, Power = 45, Discipline = 85, Bunt = 70, Speed = 50, Fielding = 50,
            })
            .Append(new Player { Position = FieldPosition.Pitcher, Pitching = PitcherAttributes.LeagueAverage })
            .ToList();
        return new Team { Name = "テスト校", BattingOrder = order, PitcherSlot = 8, Tactics = tactics };
    }

    [Fact]
    [Trait("Category", "Heavy")] // 数十〜数百試合のフルシム集計（数十秒）
    public void SchoolStyle_PowerHitting_BuntsLessThanStandard_OverManyGames()
    {
        var power = Sch(320, strength: 65, style: SchoolStyle.PowerHitting);
        var standard = Sch(320, strength: 65, style: SchoolStyle.Standard); // 同Id=同校風以外は同一プロファイル
        var opponent = StrengthTeamFactory.Create(60, "対戦相手", new Xoshiro256Random(8484));

        int Bunts(School school)
        {
            var team = LowPowerOnBaseTeam(EnemyAiFactory.BrainFor(school));
            var total = 0;
            const int games = 300; // 送りバントは7回以降の接戦限定＝機会自体が薄いので標本を厚くする
            for (ulong i = 0; i < games; i++)
                total += GameEngine.Play(team, opponent, new GameContext(), new Xoshiro256Random(9000 + i)).AwayTactics.SacrificeBunts;
            return total;
        }

        var pw = Bunts(power);
        var st = Bunts(standard);
        Assert.True(pw < st, $"強打・待球校は標準校より犠打の総数が少ないはず（結合後も校風差が出る）: power={pw} standard={st}");
    }
}
