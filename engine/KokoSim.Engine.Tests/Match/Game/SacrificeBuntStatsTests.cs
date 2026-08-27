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
/// issue sacrifice-bunt-counted-as-at-bat: 犠打（送りバント成功）を打数（AB）から除外する集計の検証。
/// <see cref="SacrificeFlyStatsTests"/>（issue #68・犠飛）と同じ「集計層カウンタ」方式の対称対応。
/// 犠飛と違い犠打は出塁率の分母にも入らない（打席が丸ごと除外される）。
/// </summary>
public sealed class SacrificeBuntStatsTests
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
    public void RecordBatting_SacBunt_ExcludedFromAtBats_CountsAsSH()
    {
        var team = TeamOf("A");
        var batter = team.BattingOrder[0];
        var state = new TeamState(team);

        state.RecordBatting(batter, PlateAppearanceResult.InPlayOut, rbi: 0, isSacBunt: true);

        var line = state.BuildBattingLines().Single(l => l.Order == 1);
        Assert.Equal(1, line.PlateAppearances);
        Assert.Equal(0, line.AtBats);      // 犠打は打数に含めない
        Assert.Equal(1, line.SacrificeHits);
    }

    [Fact]
    public void RecordBatting_OrdinaryInPlayOut_StillCountsAsAtBat_NotSH()
    {
        // 回帰: 通常の凡打（犠打でない）は従来どおり打数に算入され、犠打は増えない。
        var team = TeamOf("A");
        var batter = team.BattingOrder[0];
        var state = new TeamState(team);

        state.RecordBatting(batter, PlateAppearanceResult.InPlayOut, rbi: 0, isSacBunt: false);

        var line = state.BuildBattingLines().Single(l => l.Order == 1);
        Assert.Equal(1, line.AtBats);
        Assert.Equal(0, line.SacrificeHits);
    }

    // ===== BattingStatLine（打率・出塁率の分母から完全に除外） =====

    [Fact]
    public void Average_And_Obp_ExcludeSacBunt_FromBothDenominators()
    {
        var stat = new BattingStatLine();
        // 4打数1安打＋犠打1（打数にも出塁率の分母にも入らない＝打席自体が丸ごと除外）。
        stat.Add(new BattingLine(1, FieldPosition.CenterField, "X",
            PlateAppearances: 5, AtBats: 4, Hits: 1, Doubles: 0, Triples: 0, HomeRuns: 0,
            Rbi: 0, Walks: 0, StrikeOuts: 0, SacrificeHits: 1));

        Assert.Equal(4, stat.AtBats);
        Assert.Equal(1, stat.SacrificeHits);
        Assert.Equal(0.25, stat.Average, 3);
        Assert.Equal((double)1 / 4, stat.Obp, 6); // 犠飛と違い分母(AB+BB+HBP+SF)に犠打は入らない
    }

    // ===== GameEngine 統合（バント成功→SH計上・AB非算入、走者保存則と同型の不変条件） =====

    [Fact]
    public void FullGames_SacBuntOccurs_AndPlateAppearanceInvariantHolds()
    {
        // Team.Tactics が null（無指示）だとサイン分岐を一切通らずバントは発生しない（GameEngine.cs冒頭のコメント通り）。
        // StandardTacticsBrain を両チームへ与えて送りバント・スクイズのサインが実際に出る状態にする。
        var totalSh = 0;
        var lines = new List<BattingLine>();
        for (ulong seed = 0; seed < 200; seed++)
        {
            var ctx = new GameContext();
            var rng = new Xoshiro256Random(2000 + seed);
            var away = StrengthTeamFactory.Create(60, "A", rng) with { Tactics = new StandardTacticsBrain() };
            var home = StrengthTeamFactory.Create(60, "B", rng) with { Tactics = new StandardTacticsBrain() };
            var r = GameEngine.Play(away, home, ctx, rng);
            lines.AddRange(r.AwayBatting);
            lines.AddRange(r.HomeBatting);
        }

        foreach (var line in lines)
        {
            totalSh += line.SacrificeHits;
            // PA = AB + BB + HBP + SF + SH（犠打・犠飛はどちらも打数からもPAからも欠落・二重計上されない）。
            Assert.Equal(line.PlateAppearances,
                line.AtBats + line.Walks + line.HitByPitches + line.SacrificeFlies + line.SacrificeHits);
        }

        Assert.True(totalSh > 0, "200試合回して犠打が一度も記録されなかった（配線未達の疑い）");
    }
}
