using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// AI校の左投げが捕手・二塁手・三塁手・遊撃手に就かない（issue ai-lefty-not-at-c-2b-3b-ss）。
///
/// <para>現実の野球ではまず存在しない配置で、実測では<b>2,000チーム中 829（41.5%）</b>の先発に
/// 「左投げの捕/二/三/遊」が居た（＝対戦相手のおよそ2校に1校で目に入る）。</para>
///
/// <para>⚠ <b>統計帯はロスター生成をほとんど測っていない</b>（帯4本は StrengthTeamFactory /
/// ProspectGenerator を通らない）＝この issue のテストが唯一の安全網になる。</para>
///
/// <para>⚠ 自校（<c>ProspectGenerator</c> の新入生）は対象外。ポジションはプレイヤーが決める
/// （issue #93・監督決定 2026-08-13）。</para>
/// </summary>
public sealed class AiLeftThrowPositionTests
{
    private const int Teams = 2000;

    // ---- ① 試合用のAI校（ティア格子） ----

    [Fact]
    public void StrengthTeamFactory_NeverPutsALeftyAtCatcherSecondThirdOrShort()
    {
        var starters = 0;
        var reserves = 0;

        for (var i = 0; i < Teams; i++)
        {
            var team = StrengthTeamFactory.Create(55.0, "T" + i, new Xoshiro256Random(0x515E_0000UL + (ulong)i));
            foreach (var p in team.BattingOrder)
            {
                if (Bad(p)) starters++;
            }
            foreach (var p in team.Reserves)
            {
                if (Bad(p)) reserves++;
            }
        }

        Assert.Equal(0, starters);
        Assert.Equal(0, reserves);
    }

    /// <summary>
    /// 左投手・左投げ一塁手・左投げ外野手は従来どおり出る（左投げを消したのではなく<b>置き場所を
    /// 直した</b>）。位置ごとの左投げ率が係数 <c>ThrowLeftProb</c> 近傍にあることで固定する。
    ///
    /// <para>⚠ <b>チーム全体</b>の左投げ率は下がる（先発9人のうち4枠が右投げ固定になるため
    /// 12% → 約7%）。「引いてから捨てる」流儀＝Fork の消費列を1ビットも変えないことを優先した
    /// 帰結で、母集団の左投げ率を保ちたければ「就ける位置の確率を上げる」という別の判断が要る
    /// （<c>ThrowLeftProb</c> をいじる＝モデルの変更なので、この issue では触らない）。</para>
    /// </summary>
    [Fact]
    public void LeftiesStillAppear_AtPitcherFirstBaseAndOutfield()
    {
        var counts = new Dictionary<FieldPosition, (int Left, int Total)>();
        for (var i = 0; i < Teams; i++)
        {
            var team = StrengthTeamFactory.Create(55.0, "T" + i, new Xoshiro256Random(0x515E_0000UL + (ulong)i));
            foreach (var p in team.BattingOrder)
            {
                var c = counts.TryGetValue(p.Position, out var v) ? v : (0, 0);
                counts[p.Position] = (c.Item1 + (p.Throws == Handedness.Left ? 1 : 0), c.Item2 + 1);
            }
        }

        var prob = new KokoSim.Engine.Season.RosterCoefficients().ThrowLeftProb;
        foreach (var pos in new[]
                 {
                     FieldPosition.Pitcher, FieldPosition.FirstBase,
                     FieldPosition.LeftField, FieldPosition.CenterField, FieldPosition.RightField,
                 })
        {
            var (left, total) = counts[pos];
            Assert.True(total > 0, $"{pos} が1人も出ていない");
            var rate = (double)left / total;
            Assert.InRange(rate, prob - 0.03, prob + 0.03);
        }
    }

    // ---- ② 永続AIロスターからの配置（重み罰・妥協順） ----

    /// <summary>
    /// 右投げが足りているチームでは左投げが右投げ限定の枠に入らない
    /// （実効守備力で勝っていても入らない＝罰が能力差より大きい）。
    /// </summary>
    [Fact]
    public void AiTeamBuilder_PrefersRightThrowers_AtRightOnlyPositions()
    {
        var team = BuildAiTeam(leftThrowers: 4, leftiesAreBetterFielders: true);
        foreach (var p in team.BattingOrder)
        {
            Assert.False(Bad(p), $"{p.Position} に左投げが座っている");
        }
    }

    /// <summary>
    /// 右投げが4人に満たないチームでも<b>割当は成立する</b>（ハード除外にしていない）。
    /// 妥協は 三塁 → 二塁 → 捕手 → 遊撃 の順（監督決定 2026-08-13。遊撃がいちばん譲れない）。
    /// </summary>
    [Theory]
    [InlineData(6, new[] { FieldPosition.ThirdBase })]                                        // 野手9人中 右投げ3人＝1枠だけ譲る
    [InlineData(7, new[] { FieldPosition.ThirdBase, FieldPosition.SecondBase })]              // 右投げ2人
    [InlineData(8, new[] { FieldPosition.ThirdBase, FieldPosition.SecondBase, FieldPosition.Catcher })]
    public void AiTeamBuilder_ConcedesInTheDecidedOrder_WhenRightThrowersRunOut(
        int leftThrowers, FieldPosition[] expectedConceded)
    {
        var team = BuildAiTeam(leftThrowers, leftiesAreBetterFielders: false);
        Assert.Equal(9, team.BattingOrder.Count);   // 割当が成立している（ソルバが解を返した）

        var conceded = team.BattingOrder
            .Where(Bad).Select(p => p.Position).OrderBy(p => (int)p).ToList();
        Assert.Equal(expectedConceded.OrderBy(p => (int)p).ToList(), conceded);
    }

    // ---- ③ 試合中の守備固め（交代先は退く選手の位置を引き継ぐ） ----

    [Fact]
    public void DefensiveSub_DoesNotPutALeftyAtARightOnlyPosition()
    {
        var brain = new KokoSim.Engine.Match.Tactics.StandardTacticsBrain();
        // 遊撃手（守備30）を替えたい場面。控えは「守備の良い左投げ」と「そこそこの右投げ」。
        var weak = Pos(FieldPosition.Shortstop) with { Fielding = 30 };
        var lineup = new List<Player>
        {
            Pos(FieldPosition.Catcher), Pos(FieldPosition.FirstBase), Pos(FieldPosition.SecondBase),
            Pos(FieldPosition.ThirdBase), weak, Pos(FieldPosition.LeftField),
            Pos(FieldPosition.CenterField), Pos(FieldPosition.RightField),
            Pos(FieldPosition.Pitcher) with { Pitching = PitcherAttributes.LeagueAverage },
        };
        var lefty = Pos(FieldPosition.RightField) with { Name = "左", Fielding = 90, Throws = Handedness.Left };
        var righty = Pos(FieldPosition.SecondBase) with { Name = "右", Fielding = 70, Throws = Handedness.Right };

        var sit = new KokoSim.Engine.Match.Tactics.SubstitutionSituation(
            Inning: 9, RegulationInnings: 9, Outs: 0, ScoreDiff: 3,
            OnFirst: null, OnSecond: null, OnThird: null,
            UpcomingBatter: lineup[0], UpcomingBatterIsPitcher: false,
            Lineup: lineup, Reserves: new[] { lefty, righty });

        var pick = brain.CallDefensiveSub(sit, new Xoshiro256Random(1));
        Assert.NotNull(pick);
        Assert.Equal("右", pick!.Value.Sub.Name);   // 守備で勝る左投げでなく右投げが入る
    }

    // ---- 補助 ----

    private static bool Bad(Player p)
        => p.Throws == Handedness.Left && FieldPositions.IsRightThrowOnly(p.Position);

    /// <summary>
    /// 永続AIロスターから Team を組む。<paramref name="leftThrowers"/> 人だけ左投げにする
    /// （守備適性は全員同じ＝配置は重み罰だけで決まる）。
    /// </summary>
    private static Team BuildAiTeam(int leftThrowers, bool leftiesAreBetterFielders)
    {
        var deps = new KokoSim.Engine.Nation.Roster.AiRosterDeps();
        var players = new List<KokoSim.Engine.Nation.Roster.AiPlayer>();
        // 野手9人＋投手2人。守備適性は一律にして、罰と（必要なら）守備力だけが効くようにする。
        for (var i = 0; i < 11; i++)
        {
            var left = i < leftThrowers;
            var snap = Pos(FieldPosition.Shortstop) with
            {
                Name = "P" + i,
                Throws = left ? Handedness.Left : Handedness.Right,
                Fielding = left && leftiesAreBetterFielders ? 95 : 60,
                // ⚠ 全員に投手能力を持たせる（AiTeamBuilder.PitcherScore が Pitching を読む）。
                // マウンド組は「投手適性が高い2人」＝下の apt で決まるよう、能力側でも差をつける。
                Pitching = i >= 9
                    ? PitcherAttributes.LeagueAverage
                    : PitcherAttributes.LeagueAverage with { Control = 1, PitchRank = 1 },
            };
            var apt = new int[Enum.GetValues(typeof(FieldPosition)).Length];
            for (var k = 0; k < apt.Length; k++) apt[k] = 60;
            apt[(int)FieldPosition.Pitcher] = i >= 9 ? 95 : 10;   // 投手2人だけマウンドへ
            players.Add(new KokoSim.Engine.Nation.Roster.AiPlayer(i + 1, 1, apt, KokoSim.Engine.Players.PhenomType.None, 2, snap));
        }

        var school = new School { Id = 7, Name = "テスト校", PrefectureId = 0, Strength = 55.0 };
        return KokoSim.Engine.Nation.Roster.AiTeamBuilder.BuildFrom(players, school, 1, deps);
    }

    private static Player Pos(FieldPosition pos) => new()
    {
        Position = pos,
        Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = 50, ArmStrength = 50, Fielding = 50, Catching = 50,
    };
}
