using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using KokoSim.Engine.Stats;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// issue #68: 犠飛（SF）を打数（AB）から除外し、出塁率の分母へ算入する集計の検証。
/// PlateAppearanceResult には新しい列挙値を追加せず（switch網羅への波及を避けるため、既存の犠打/スクイズと
/// 同じ「集計層カウンタ」方式を採用）、GameEngine 側でフライ捕球＋タッチアップ生還（runs>0）を犠飛と判定する。
/// </summary>
public sealed class SacrificeFlyStatsTests
{
    private static Player Pos(FieldPosition pos) => new() { Position = pos, Contact = 50, Power = 50 };

    private static Team TeamOf(string name)
    {
        var order = new List<Player>
        {
            Pos(FieldPosition.Catcher), Pos(FieldPosition.FirstBase), Pos(FieldPosition.SecondBase),
            Pos(FieldPosition.ThirdBase), Pos(FieldPosition.Shortstop), Pos(FieldPosition.LeftField),
            Pos(FieldPosition.CenterField), Pos(FieldPosition.RightField),
            new() { Name = name + "P", Position = FieldPosition.Pitcher, Pitching = PitcherAttributes.LeagueAverage },
        };
        return new Team { Name = name, BattingOrder = order, PitcherSlot = 8 };
    }

    // ===== TeamState 単体 =====

    [Fact]
    public void RecordBatting_SacFly_ExcludedFromAtBats_CountsAsSF()
    {
        var team = TeamOf("A");
        var batter = team.BattingOrder[0];
        var state = new TeamState(team);

        state.RecordBatting(batter, PlateAppearanceResult.InPlayOut, rbi: 1, isSacFly: true);

        var line = state.BuildBattingLines().Single(l => l.Order == 1);
        Assert.Equal(1, line.PlateAppearances);
        Assert.Equal(0, line.AtBats); // 犠飛は打数に含めない
        Assert.Equal(1, line.SacrificeFlies);
        Assert.Equal(1, line.Rbi);
    }

    [Fact]
    public void RecordBatting_OrdinaryInPlayOut_StillCountsAsAtBat()
    {
        // 回帰: 通常の凡打（犠飛でない）は従来どおり打数に算入される。
        var team = TeamOf("A");
        var batter = team.BattingOrder[0];
        var state = new TeamState(team);

        state.RecordBatting(batter, PlateAppearanceResult.InPlayOut, rbi: 0, isSacFly: false);

        var line = state.BuildBattingLines().Single(l => l.Order == 1);
        Assert.Equal(1, line.AtBats);
        Assert.Equal(0, line.SacrificeFlies);
    }

    // ===== BattingStatLine（出塁率の分母） =====

    [Fact]
    public void Obp_IncludesSacFlyInDenominator_ButNotInAtBatsOrAverage()
    {
        var stat = new BattingStatLine();
        // 4打数1安打＋犠飛1（打点のみで打数不算入）。
        stat.Add(new BattingLine(1, FieldPosition.CenterField, "X",
            PlateAppearances: 5, AtBats: 4, Hits: 1, Doubles: 0, Triples: 0, HomeRuns: 0,
            Rbi: 1, Walks: 0, StrikeOuts: 0, SacrificeFlies: 1));

        Assert.Equal(4, stat.AtBats);
        Assert.Equal(0.25, stat.Average, 3); // 犠飛は打率の分母に含まれない
        Assert.Equal((double)1 / (4 + 0 + 0 + 1), stat.Obp, 6); // OBP=(H+BB+HBP)/(AB+BB+HBP+SF)
    }

    // ===== GameEngine 統合（フライ＋三塁走者生還→SF計上・AB非算入） =====

    [Fact]
    public void FullGames_SacFlyOccurs_AndPlateAppearanceInvariantHolds()
    {
        // 十分な試合数を回せば実際にタッチアップ犠飛が発生し、かつ全打者について
        // PA = AB + BB + HBP + SF（犠飛が打数からもPAからも欠落・二重計上されない）という不変条件が保たれること。
        var ctx = new GameContext();
        var totalSf = 0;
        var lines = new List<BattingLine>();
        for (ulong seed = 0; seed < 60; seed++)
        {
            var rng = new Xoshiro256Random(1000 + seed);
            var away = StrengthTeamFactory.Create(60, "A", rng);
            var home = StrengthTeamFactory.Create(60, "B", rng);
            var r = GameEngine.Play(away, home, ctx, rng);
            lines.AddRange(r.AwayBatting);
            lines.AddRange(r.HomeBatting);
        }

        foreach (var line in lines)
        {
            totalSf += line.SacrificeFlies;
            Assert.Equal(line.PlateAppearances, line.AtBats + line.Walks + line.HitByPitches + line.SacrificeFlies);
        }

        Assert.True(totalSf > 0, "60試合回して犠飛が一度も記録されなかった（配線未達の疑い）");
    }
}
