using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using KokoSim.Engine.Stats;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 試合中の能力ビューア（issue #923）の中身の組み立てと、DH の打者の守備位置（issue #931）。
/// 一覧に出してよい位置は「いま試合で就いている位置」だけ＝DH の枠に本来の守備位置を出さない。
/// </summary>
public sealed class MatchScoutViewTests
{
    private static Player P(FieldPosition pos, string name, int sid) => new()
    {
        Position = pos, Name = name, SourceId = sid, UniformNumber = sid,
    };

    private static readonly FieldPosition[] Fielders =
    {
        FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase, FieldPosition.ThirdBase,
        FieldPosition.Shortstop, FieldPosition.LeftField, FieldPosition.CenterField, FieldPosition.RightField,
    };

    // DH制の打順: 8人の野手＋DH（本来の守備位置は中堅＝ゲームが付けた位置）。投手は打順外。
    private static SubstitutionOptions DhOptions(bool isOffense = true)
    {
        var lineup = Fielders.Select((pos, i) => P(pos, "野" + (i + 1), i + 1)).ToList();
        lineup.Insert(3, P(FieldPosition.CenterField, "指名打者", 20));   // 4番＝DH（Player.Position は中堅のまま）
        var pitcher = P(FieldPosition.Pitcher, "先発", 1) with { SourceId = 11, UniformNumber = 1 };
        return Options(lineup, pitcher, pitcherSlot: -1, usesDh: true, dhSlot: 3, isOffense,
            reserves: new[] { P(FieldPosition.Shortstop, "控え", 12) },
            used: new[] { P(FieldPosition.LeftField, "使用済み", 13) });
    }

    private static SubstitutionOptions NonDhOptions(bool isOffense = true)
    {
        var lineup = Fielders.Select((pos, i) => P(pos, "野" + (i + 1), i + 1)).ToList();
        var pitcher = P(FieldPosition.Pitcher, "先発", 11);
        lineup.Add(pitcher);   // 9番＝投手
        return Options(lineup, pitcher, pitcherSlot: 8, usesDh: false, dhSlot: -1, isOffense,
            reserves: new[] { P(FieldPosition.Pitcher, "控え投手", 12) }, used: System.Array.Empty<Player>());
    }

    private static SubstitutionOptions Options(IReadOnlyList<Player> lineup, Player pitcher, int pitcherSlot,
        bool usesDh, int dhSlot, bool isOffense, IReadOnlyList<Player> reserves, IReadOnlyList<Player> used) => new()
    {
        TeamIsAway = true, IsOffense = isOffense, IsFinished = false,
        Lineup = lineup, UpcomingBatterSlot = 2, CurrentPitcher = pitcher,
        PitcherSlot = pitcherSlot, UsesDh = usesDh, DhSlot = dhSlot,
        Runners = System.Array.Empty<RunnerChoice>(), Reserves = reserves, UsedReserves = used,
        ReliefCandidates = reserves,
    };

    // ── 一覧の位置（issue #931） ──

    [Fact]
    public void Roster_DhBatter_ShowsDesignatedHitter_NotTheGameAssignedPosition()
    {
        var rows = MatchScoutView.Roster(DhOptions());
        var dh = rows.Single(r => r.Player.Name == "指名打者");
        Assert.Equal(FieldPosition.DesignatedHitter, dh.Position);
        Assert.Equal(4, dh.Order);
        // 本来の位置（中堅）は打順の中で本当に中堅を守る1人だけ。
        Assert.Single(rows, r => r.Position == FieldPosition.CenterField);
    }

    [Fact]
    public void Roster_Dh_AddsCurrentPitcherSection_AsPitcher()
    {
        var rows = MatchScoutView.Roster(DhOptions(isOffense: false));
        var pit = rows.Single(r => r.Section == ScoutSection.Pitcher);
        Assert.Equal("先発", pit.Player.Name);
        Assert.Equal(FieldPosition.Pitcher, pit.Position);
        Assert.True(pit.IsNow);   // 守備中は現投手を点ける
    }

    [Fact]
    public void Roster_NonDh_PitcherStaysInLineup_NoDuplicateRow()
    {
        var rows = MatchScoutView.Roster(NonDhOptions());
        Assert.DoesNotContain(rows, r => r.Section == ScoutSection.Pitcher);
        Assert.Single(rows, r => r.Player.Name == "先発");
        Assert.Equal(FieldPosition.Pitcher, rows.Single(r => r.Order == 9).Position);
        Assert.DoesNotContain(rows, r => r.Position == FieldPosition.DesignatedHitter);
    }

    [Fact]
    public void Roster_Reserves_CarryNoPosition()
    {
        var rows = MatchScoutView.Roster(DhOptions());
        var reserves = rows.Where(r => r.Section == ScoutSection.Reserves).ToList();
        Assert.Equal(2, reserves.Count);
        Assert.All(reserves, r => Assert.Null(r.Position));
        Assert.All(reserves, r => Assert.Equal(0, r.Order));
        Assert.True(reserves.Single(r => r.Player.Name == "使用済み").IsUsed);
        Assert.False(reserves.Single(r => r.Player.Name == "控え").IsUsed);
    }

    [Fact]
    public void Roster_Offense_LightsUpcomingBatterOnly()
    {
        var rows = MatchScoutView.Roster(DhOptions(isOffense: true));
        Assert.Equal(3, rows.Single(r => r.IsNow).Order);   // UpcomingBatterSlot=2 → 3番
    }

    [Fact]
    public void DefaultRow_IsLeadoffBatter()
    {
        var rows = MatchScoutView.Roster(DhOptions());
        Assert.Equal(1, MatchScoutView.DefaultRow(rows)!.Order);
        Assert.Null(MatchScoutView.DefaultRow(new List<ScoutRosterRow>()));
    }

    // ── 詳細欄の中身（issue #923） ──

    [Fact]
    public void Detail_Skills_OnlyForOwnSchool()
    {
        var row = MatchScoutView.Roster(DhOptions())[0];
        Assert.True(MatchScoutView.Detail(row, own: true, pitcherView: false, null, null, null, null).ShowSkills);
        Assert.False(MatchScoutView.Detail(row, own: false, pitcherView: false, null, null, null, null).ShowSkills);
    }

    [Fact]
    public void Detail_PitchChart_ShownForPitcherView_BothSchools()
    {
        var row = MatchScoutView.Roster(DhOptions())[0];
        foreach (var own in new[] { true, false })
        {
            Assert.True(MatchScoutView.Detail(row, own, pitcherView: true, null, null, null, null).ShowPitchChart);
            Assert.False(MatchScoutView.Detail(row, own, pitcherView: false, null, null, null, null).ShowPitchChart);
        }
    }

    [Fact]
    public void Detail_PositionIsTheRowsInGamePosition()
    {
        var rows = MatchScoutView.Roster(DhOptions());
        var dh = MatchScoutView.Detail(rows.Single(r => r.Player.Name == "指名打者"), true, false, null, null, null, null);
        Assert.Equal(FieldPosition.DesignatedHitter, dh.Position);
        var res = MatchScoutView.Detail(rows.Single(r => r.Player.Name == "控え"), true, false, null, null, null, null);
        Assert.Null(res.Position);
    }

    [Fact]
    public void Detail_StatLabels_FollowTheView()
    {
        var row = MatchScoutView.Roster(DhOptions())[0];
        var bat = MatchScoutView.Detail(row, true, pitcherView: false, null, null, null, null);
        Assert.Equal(new[] { "打数", "安打", "本塁打", "打点", "打率" }, bat.Stats.Select(s => s.Label));
        var pit = MatchScoutView.Detail(row, true, pitcherView: true, null, null, null, null);
        Assert.Equal(new[] { "投球回", "球数", "奪三振", "与四死球", "自責点", "防御率" }, pit.Stats.Select(s => s.Label));
        Assert.Equal(new[] { "今日", "通算" }, bat.StatScopes);
    }

    [Fact]
    public void BattingStats_TodayAndCareer_Formatted()
    {
        var today = new BattingLine(1, FieldPosition.CenterField, "x", PlateAppearances: 3, AtBats: 3, Hits: 2,
            Doubles: 0, Triples: 0, HomeRuns: 1, Rbi: 2, Walks: 0, StrikeOuts: 0);
        var career = new BattingStatLine();
        career.Add(new BattingLine(1, FieldPosition.CenterField, "x", 4, 3, 1, 0, 0, 0, 0, 1, 1));

        var rows = MatchScoutView.BattingStats(today, career).ToDictionary(r => r.Label);
        Assert.Equal(("3", "3"), (rows["打数"].Today, rows["打数"].Career));
        Assert.Equal(("2", "1"), (rows["安打"].Today, rows["安打"].Career));
        Assert.Equal(("1", "0"), (rows["本塁打"].Today, rows["本塁打"].Career));
        Assert.Equal((".667", ".333"), (rows["打率"].Today, rows["打率"].Career));
    }

    [Fact]
    public void BattingStats_NoRecord_IsDash()
    {
        // 打順に居るがまだ打席が来ていない（PA=0）／通算の試合数0。
        var today = new BattingLine(1, FieldPosition.CenterField, "x", 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var rows = MatchScoutView.BattingStats(today, new BattingStatLine());
        Assert.All(rows, r => Assert.Equal(("—", "—"), (r.Today, r.Career)));
    }

    [Fact]
    public void PitchingStats_TodayAndCareer_Formatted()
    {
        var today = new PitchingLine("p", Outs: 7, BattersFaced: 10, Hits: 2, Runs: 1, StrikeOuts: 4, Walks: 1,
            Pitches: 38, HitBatters: 1, EarnedRuns: 1);
        var rows = MatchScoutView.PitchingStats(today, null).ToDictionary(r => r.Label);
        Assert.Equal("2 1/3", rows["投球回"].Today);
        Assert.Equal("38", rows["球数"].Today);
        Assert.Equal("4", rows["奪三振"].Today);
        Assert.Equal("2", rows["与四死球"].Today);
        Assert.Equal("1", rows["自責点"].Today);
        Assert.Equal("3.86", rows["防御率"].Today);
        Assert.All(rows.Values, r => Assert.Equal("—", r.Career));
    }

    [Fact]
    public void Rate3_DropsLeadingZero()
    {
        Assert.Equal(".342", MatchScoutView.Rate3(0.342));
        Assert.Equal("1.000", MatchScoutView.Rate3(1.0));
    }

    // ── 実際の試合の観測データと突き合わせる ──

    private static Team DhTeam(string name, int sidBase)
    {
        var order = Fielders.Select((pos, i) => P(pos, name + (i + 1), sidBase + i + 1)).ToList();
        order.Insert(3, P(FieldPosition.CenterField, name + "DH", sidBase + 20));
        return new Team
        {
            Name = name, BattingOrder = order, PitcherSlot = 8, DhSlot = 3,
            StartingPitcher = P(FieldPosition.Pitcher, name + "P", sidBase + 30) with { Pitching = PitcherAttributes.LeagueAverage },
            Reserves = new[] { P(FieldPosition.Pitcher, name + "R", sidBase + 31) with { Pitching = PitcherAttributes.LeagueAverage } },
        };
    }

    private static MatchProgression NewDhProg() =>
        new(DhTeam("A", 100), DhTeam("H", 200), new GameContext { CaptureTimelines = true }, 7UL);

    [Fact]
    public void Roster_AgreesWithLiveLineupPositions_InARealDhGame()
    {
        var prog = NewDhProg();
        for (var i = 0; i < 15 && prog.Advance(); i++) { }

        var snap = prog.Snapshot();
        foreach (var (away, live) in new[] { (true, snap.Away), (false, snap.Home) })
        {
            var rows = MatchScoutView.Roster(prog.SubstitutionOptions(away))
                .Where(r => r.Section == ScoutSection.Lineup).ToList();
            Assert.Equal(live.Lineup.Select(s => s.Position), rows.Select(r => r.Position!.Value));
            Assert.Equal(FieldPosition.DesignatedHitter, rows[3].Position);
        }
    }

    [Fact]
    public void TodayLines_AgreeWithSnapshot()
    {
        var prog = NewDhProg();
        for (var i = 0; i < 30 && prog.Advance(); i++) { }

        var snap = prog.Snapshot();
        var opt = prog.SubstitutionOptions(true);
        for (var i = 0; i < 9; i++)
        {
            var line = prog.TodayBattingOf(true, opt.Lineup[i]);
            Assert.Equal(snap.Away.Lineup[i].AtBats, line?.AtBats ?? 0);
            Assert.Equal(snap.Away.Lineup[i].Hits, line?.Hits ?? 0);
        }
        var pit = prog.TodayPitchingOf(false, prog.SubstitutionOptions(false).CurrentPitcher);
        Assert.NotNull(pit);
        Assert.Equal(snap.Home.Pitcher.Pitches + snap.Home.Pitcher.PendingPitches, pit!.Pitches);
        Assert.Equal(snap.Home.Pitcher.StrikeOuts, pit.StrikeOuts);
        // 控え（未出場）は今日の成績を持たない。
        Assert.Null(prog.TodayBattingOf(true, opt.Reserves[0]));
        Assert.Null(prog.TodayPitchingOf(true, opt.Reserves[0]));
    }

    // 読むだけ＝ビューアを何度引いても試合の結果は変わらない（不変条件#2）。
    [Fact]
    public void Reading_DoesNotChangeTheGame()
    {
        static (int, int) Play(bool read)
        {
            var prog = NewDhProg();
            while (prog.Advance())
            {
                if (!read) continue;
                foreach (var away in new[] { true, false })
                {
                    var opt = prog.SubstitutionOptions(away);
                    foreach (var r in MatchScoutView.Roster(opt))
                    {
                        MatchScoutView.Detail(r, away, pitcherView: true, prog.TodayBattingOf(away, r.Player),
                            prog.TodayPitchingOf(away, r.Player), null, null);
                    }
                }
            }
            return (prog.AwayScore, prog.HomeScore);
        }

        Assert.Equal(Play(read: false), Play(read: true));
    }
}
