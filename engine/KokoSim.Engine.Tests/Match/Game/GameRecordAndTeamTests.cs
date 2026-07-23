using System.Collections.Generic;
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

    /// <summary>能力値のみで守備位置適性を持たせた育成選手（Fielding は全員同一水準）。</summary>
    private static DevelopingPlayer MakeFielderWithAptitude(string name, params (FieldPosition Pos, int Value)[] aptitudes)
    {
        var p = new DevelopingPlayer { Name = name };
        foreach (var k in AbilityKinds.All) { p.SetLevel(k, 60); p.SetCap(k, 99); }
        foreach (var (pos, value) in aptitudes) p.SetAptitude(pos, value);
        return p;
    }

    private static List<DevelopingPlayer> RosterWithCatcherAptitudeCases()
    {
        var pitcher = new DevelopingPlayer { Name = "投手", IsPitcher = true };
        foreach (var k in AbilityKinds.All) { pitcher.SetLevel(k, 60); pitcher.SetCap(k, 99); }

        return new List<DevelopingPlayer>
        {
            pitcher,
            // 捕手専門: 捕手適性だけ突出。他は中庸(50)のまま。
            MakeFielderWithAptitude("捕手専門", (FieldPosition.Catcher, 90)),
            // 捕手不向き: 捕手適性が著しく低い。他ポジション適性は中庸。
            MakeFielderWithAptitude("捕手不向き", (FieldPosition.Catcher, 10)),
            MakeFielderWithAptitude("内野汎用A"),
            MakeFielderWithAptitude("内野汎用B"),
            MakeFielderWithAptitude("内野汎用C"),
            MakeFielderWithAptitude("外野汎用A"),
            MakeFielderWithAptitude("外野汎用B"),
            MakeFielderWithAptitude("外野汎用C"),
        };
    }

    [Fact]
    public void RosterTeamBuilder_Build_AssignsCatcherSpecialist_ToCatcher_NotLowAptitudePlayer()
    {
        var team = RosterTeamBuilder.Build(RosterWithCatcherAptitudeCases(), "適性校");

        var catcher = team.BattingOrder.Single(p => p.Position == FieldPosition.Catcher);
        Assert.Equal("捕手専門", catcher.Name);
        Assert.DoesNotContain(team.BattingOrder, p => p.Name == "捕手不向き" && p.Position == FieldPosition.Catcher);
    }

    [Fact]
    public void RosterTeamBuilder_Build_PositionAssignment_IsDeterministic()
    {
        var roster = RosterWithCatcherAptitudeCases();
        var teamA = RosterTeamBuilder.Build(roster, "決定論校");
        var teamB = RosterTeamBuilder.Build(roster, "決定論校");

        var assignmentsA = teamA.BattingOrder.Select(p => (p.Name, p.Position)).ToList();
        var assignmentsB = teamB.BattingOrder.Select(p => (p.Name, p.Position)).ToList();
        Assert.Equal(assignmentsA, assignmentsB);
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
    public void Play_RecordsPersonalRuns_SummingToTeamRuns()
    {
        // 得点（個人の生還数, issue #77）: 打者別 Runs の合計＝チーム総得点。CaptureTimelines 未指定でも成立
        // （GameEngine は自校詳細試合専用で常に moves を収集する）。複数シードで多様な得点経路を通す。
        for (ulong seed = 1; seed <= 30; seed++)
        {
            var away = StrengthTeam(58, "遠征校", seed * 2);
            var home = StrengthTeam(52, "地元校", seed * 2 + 1);
            var result = GameEngine.Play(away, home, new GameContext(), new Xoshiro256Random(seed));

            Assert.Equal(result.AwayRuns, result.AwayBatting.Sum(b => b.Runs));
            Assert.Equal(result.HomeRuns, result.HomeBatting.Sum(b => b.Runs));
        }
    }

    [Fact]
    public void Play_RecordsHomeRunsAllowed_MatchingOpponentHomeRuns()
    {
        // 被本塁打（issue #77）: 投手陣の被本塁打合計＝相手打線の本塁打合計。
        var totalHra = 0;
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var away = StrengthTeam(62, "強打校", seed * 3);
            var home = StrengthTeam(48, "投手校", seed * 3 + 1);
            var result = GameEngine.Play(away, home, new GameContext(), new Xoshiro256Random(seed));

            Assert.Equal(result.AwayBatting.Sum(b => b.HomeRuns), result.HomePitching.Sum(p => p.HomeRunsAllowed));
            Assert.Equal(result.HomeBatting.Sum(b => b.HomeRuns), result.AwayPitching.Sum(p => p.HomeRunsAllowed));
            totalHra += result.HomePitching.Sum(p => p.HomeRunsAllowed) + result.AwayPitching.Sum(p => p.HomeRunsAllowed);
        }
        Assert.True(totalHra > 0, "40試合で被本塁打が一度も記録されない＝配線ミス");
    }

    [Fact]
    public void Play_RecordsBattingAgainstByPitch_ConsistentWithHitsAndHomeRuns()
    {
        // 球種別被打（issue #180）: 決め球（インプレーで終わった打席）が必ずタグ付けされるため、
        // 球種別内訳の合計は投手ラインの被安打・被本塁打・対戦打者数と厳密に整合する。
        var sawAnyPitchType = false;
        for (ulong seed = 1; seed <= 30; seed++)
        {
            var away = StrengthTeam(58, "強打校", seed * 5);
            var home = StrengthTeam(52, "投手校", seed * 5 + 1);
            var result = GameEngine.Play(away, home, new GameContext(), new Xoshiro256Random(seed));

            foreach (var line in result.AwayPitching.Concat(result.HomePitching))
            {
                var byPitch = line.BattingAgainstByPitch;
                if (byPitch is null) continue;
                sawAnyPitchType |= byPitch.Count > 0;

                Assert.Equal(line.Hits, byPitch.Values.Sum(v => v.Hits));
                Assert.Equal(line.HomeRunsAllowed, byPitch.Values.Sum(v => v.HomeRuns));
                Assert.True(byPitch.Values.Sum(v => v.AtBats) <= line.BattersFaced);
                Assert.All(byPitch.Values, v => Assert.True(v.Hits <= v.AtBats));
            }
        }
        Assert.True(sawAnyPitchType, "30試合で球種別被打が一度も記録されない＝配線ミス");
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
