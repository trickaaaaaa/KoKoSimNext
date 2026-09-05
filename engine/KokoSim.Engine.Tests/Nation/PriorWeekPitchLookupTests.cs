using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 大会台帳→持ち越し球数の一括変換（<see cref="PriorWeekPitchLookup"/>・週間球数制限の本流配線）。
/// <see cref="PitchRecoveryLookupTests"/> と同型で、走査・窓・キー選択・null の扱いを固定する。
/// </summary>
public sealed class PriorWeekPitchLookupTests
{
    private static readonly int Window = new ModernRules().WeeklyPitchWindowDays;

    private static Player MakePitcher(int uniformNumber, int? sourceId = null) => new()
    {
        Position = FieldPosition.Pitcher,
        UniformNumber = uniformNumber,
        SourceId = sourceId,
        Pitching = PitcherAttributes.LeagueAverage,
    };

    [Fact]
    public void BuildFor_NullLedger_ReturnsEmptyMap()
    {
        var roster = new[] { MakePitcher(1) };
        var map = PriorWeekPitchLookup.BuildFor(
            roster, p => PitcherLedgerKey.ForOpponent(1, p.UniformNumber), ledger: null, matchDay: 5, Window);
        Assert.Empty(map);
    }

    [Fact]
    public void BuildFor_SumsOutingsInsideWindow_AndDropsOlderOnes()
    {
        var ledger = new TournamentPitchLedger();
        var ace = MakePitcher(1);
        var key = PitcherLedgerKey.ForOpponent(7, ace.UniformNumber);
        ledger.Record(key, 120, matchDay: 1);   // 窓の外（day9 から見て8日前）
        ledger.Record(key, 130, matchDay: 4);
        ledger.Record(key, 110, matchDay: 6);

        System.Func<Player, PitcherLedgerKey> keyOf = p => PitcherLedgerKey.ForOpponent(7, p.UniformNumber);
        var map = PriorWeekPitchLookup.BuildFor(new[] { ace }, keyOf, ledger, matchDay: 9, Window);
        Assert.Equal(240, map[ace]);   // day4 + day6 のみ（day1 は7日窓の外）

        // 当日（matchDay）ぶんは含まない＝この試合で投げる球は持ち越しではない。
        var sameDay = PriorWeekPitchLookup.BuildFor(new[] { ace }, keyOf, ledger, matchDay: 6, Window);
        Assert.Equal(250, sameDay[ace]);   // day1 + day4（day6 は当日なので除外）
    }

    [Fact]
    public void BuildFor_UnusedPitcher_IsAbsentFromMap()
    {
        // 未登板の投手はキーを持たない＝辞書に載せない（GameEngine 側は「持ち越し0」として扱う）。
        var ledger = new TournamentPitchLedger();
        var rookie = MakePitcher(11);
        var map = PriorWeekPitchLookup.BuildFor(
            new[] { rookie }, p => PitcherLedgerKey.ForOpponent(7, p.UniformNumber), ledger, matchDay: 3, Window);
        Assert.Empty(map);
    }

    [Fact]
    public void ForMatch_NullContext_ReturnsEmptyMapsForBothTeams()
    {
        var away = new[] { MakePitcher(1) };
        var home = new[] { MakePitcher(1) };
        var (awayMap, homeMap) = PriorWeekPitchLookup.ForMatch(
            context: null, Window,
            away, p => PitcherLedgerKey.ForOpponent(1, p.UniformNumber),
            home, p => PitcherLedgerKey.ForOpponent(2, p.UniformNumber));
        Assert.Empty(awayMap);
        Assert.Empty(homeMap);
    }

    /// <summary>
    /// 自校（SourceId）と相手校（校ID＋背番号）でキーの取り方が分かれること（OPEN-QUESTIONS Q26）。
    /// 記録側（TournamentRunner.RecordSide）と同じ <see cref="PitcherLedgerKey.For"/> を通す。
    /// </summary>
    [Fact]
    public void ForMatch_ManagerAndOpponent_UseTheirOwnKeyKinds()
    {
        var ledger = new TournamentPitchLedger();
        var mine = MakePitcher(1, sourceId: 42);
        var theirs = MakePitcher(1);   // 背番号は同じ＝キーが混ざれば必ず露見する
        ledger.Record(PitcherLedgerKey.For(isManagerSchool: true, 100, 42, 1), 200, matchDay: 2);
        ledger.Record(PitcherLedgerKey.For(isManagerSchool: false, 200, null, 1), 90, matchDay: 2);

        var (mgrMap, oppMap) = PriorWeekPitchLookup.ForMatch(
            new TournamentMatchContext(RoundsRemaining: 3, MatchDay: 4, ledger), Window,
            new[] { mine }, p => PitcherLedgerKey.For(true, 100, p.SourceId, p.UniformNumber),
            new[] { theirs }, p => PitcherLedgerKey.For(false, 200, p.SourceId, p.UniformNumber));

        Assert.Equal(200, mgrMap[mine]);
        Assert.Equal(90, oppMap[theirs]);
    }
}
