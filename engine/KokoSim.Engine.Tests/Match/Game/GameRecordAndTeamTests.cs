using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 試合の速報記録（イニング別得点・安打・失策・プレーログ）と、
/// 育成ロスター→Team 編成の検証（試合「高速モード」UIの土台）。
/// </summary>
public sealed class GameRecordAndTeamTests
{
    private static Team StrengthTeam(double s, string name, ulong seed)
        => StrengthTeamFactory.Create(s, name, new Xoshiro256Random(seed));

    // ===== ロスター→Team 編成 =====

    [Fact]
    public void RosterTeamBuilder_BuildsValidNineManTeam_WithNamesAndPitcher()
    {
        var roster = ProspectGenerator.Intake(1, new RosterCoefficients(), new Xoshiro256Random(42));
        var team = RosterTeamBuilder.Build(roster, "桜丘");

        Assert.Equal("桜丘", team.Name);
        Assert.Equal(9, team.BattingOrder.Count);
        Assert.Equal(8, team.PitcherSlot);

        var pitcher = team.BattingOrder[team.PitcherSlot];
        Assert.Equal(FieldPosition.Pitcher, pitcher.Position);
        Assert.NotNull(pitcher.Pitching);
        Assert.InRange(pitcher.Pitching!.MaxVelocityKmh, 118, 155);

        // 8守備位置が打順に揃う。
        var positions = team.BattingOrder.Select(p => p.Position).ToHashSet();
        foreach (var pos in new[] { FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase,
                     FieldPosition.ThirdBase, FieldPosition.Shortstop, FieldPosition.LeftField,
                     FieldPosition.CenterField, FieldPosition.RightField, FieldPosition.Pitcher })
            Assert.Contains(pos, positions);

        // 氏名が育成選手から引き継がれる（既定"選手"のままでない）。
        Assert.Contains(team.BattingOrder, p => p.Name != "選手");
    }

    // ===== 速報記録 =====

    [Fact]
    public void Play_RecordsLineScore_SummingToFinalRuns()
    {
        var away = StrengthTeam(55, "遠征校", 1);
        var home = StrengthTeam(55, "地元校", 2);
        var result = GameEngine.Play(away, home, new GameContext(), new Xoshiro256Random(100));

        Assert.Equal(result.AwayRuns, result.AwayLineScore.Sum());
        Assert.Equal(result.HomeRuns, result.HomeLineScore.Sum());
        // 先攻は毎回攻撃 → イニング別得点の要素数は実施イニング数と一致。
        Assert.Equal(result.InningsPlayed, result.AwayLineScore.Count);
        // 後攻は最終回に攻撃しない場合がある → 実施回数か1つ少ない。
        Assert.InRange(result.HomeLineScore.Count, result.InningsPlayed - 1, result.InningsPlayed);
    }

    [Fact]
    public void Play_RecordsPlayLog_AndHitsErrorsNonNegative()
    {
        var away = StrengthTeam(60, "遠征校", 3);
        var home = StrengthTeam(50, "地元校", 4);
        var result = GameEngine.Play(away, home, new GameContext(), new Xoshiro256Random(200));

        Assert.NotEmpty(result.Log);
        Assert.All(result.Log, e => Assert.InRange(e.Inning, 1, result.InningsPlayed));
        Assert.True(result.AwayHits >= 0 && result.HomeHits >= 0);
        Assert.True(result.AwayErrors >= 0 && result.HomeErrors >= 0);
        // ログの得点合計＝両チーム総得点。
        Assert.Equal(result.TotalRuns, result.Log.Sum(e => e.RunsScored));
    }

    [Fact]
    public void Play_RecordsBoxScore_ConsistentWithTotals()
    {
        var away = StrengthTeam(55, "遠征校", 11);
        var home = StrengthTeam(50, "地元校", 12);
        var result = GameEngine.Play(away, home, new GameContext(), new Xoshiro256Random(321));

        // スタメン9人＋途中出場。
        Assert.True(result.AwayBatting.Count >= 9);
        Assert.True(result.HomeBatting.Count >= 9);
        // チーム安打合計＝ボックススコアの安打合計（途中出場も含め一致）。
        Assert.Equal(result.AwayHits, result.AwayBatting.Sum(b => b.Hits));
        Assert.Equal(result.HomeHits, result.HomeBatting.Sum(b => b.Hits));
        // 投手が失点合計＝相手の得点（先発＋救援）。
        Assert.Equal(result.HomeRuns, result.AwayPitching.Sum(p => p.Runs));
        Assert.Equal(result.AwayRuns, result.HomePitching.Sum(p => p.Runs));
        // 投手の記録アウト合計＝攻撃回数×3 近辺（最低でも正）。
        Assert.True(result.AwayPitching.Sum(p => p.Outs) > 0);
        Assert.NotEmpty(result.AwayPitching);
        // 打数・安打・打率の整合。
        Assert.All(result.HomeBatting, b => Assert.True(b.Hits <= b.AtBats));
    }

    [Fact]
    public void Play_IsDeterministic_IncludingLog()
    {
        var a1 = StrengthTeam(55, "A", 5);
        var h1 = StrengthTeam(55, "H", 6);
        var r1 = GameEngine.Play(a1, h1, new GameContext(), new Xoshiro256Random(777));

        var a2 = StrengthTeam(55, "A", 5);
        var h2 = StrengthTeam(55, "H", 6);
        var r2 = GameEngine.Play(a2, h2, new GameContext(), new Xoshiro256Random(777));

        Assert.Equal(r1.AwayRuns, r2.AwayRuns);
        Assert.Equal(r1.HomeRuns, r2.HomeRuns);
        Assert.Equal(r1.Log.Count, r2.Log.Count);
        Assert.Equal(r1.AwayHits, r2.AwayHits);
    }
}
