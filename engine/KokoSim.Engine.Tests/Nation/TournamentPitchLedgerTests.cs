using KokoSim.Engine.Nation.Tournaments;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 大会中の投手球数台帳（issue #41・設計書05 §1.3）。記録/集計/リセットの単体検証。
/// 自校＝SourceId、相手校＝校ID＋背番号でキーが分離すること、直近ウィンドウ集計・休養日数が正しいこと。
/// </summary>
public sealed class TournamentPitchLedgerTests
{
    [Fact]
    public void RecordsAndSumsWithinWindow()
    {
        var ledger = new TournamentPitchLedger();
        var ace = PitcherLedgerKey.ForPlayer(1001);
        ledger.Record(ace, 120, matchDay: 1);
        ledger.Record(ace, 90, matchDay: 4);
        ledger.Record(ace, 30, matchDay: 7);

        // day 8 時点の直近7日（day1..7、当日8は含まない）＝ 120+90+30。
        Assert.Equal(240, ledger.PitchesWithin(ace, currentDay: 8, windowDays: 7));
        // day 8 の直近5日（day3..7）＝ 90+30（day1は窓外）。
        Assert.Equal(120, ledger.PitchesWithin(ace, currentDay: 8, windowDays: 5));
        // 当日（currentDay）自身は含まない。
        Assert.Equal(210, ledger.PitchesWithin(ace, currentDay: 7, windowDays: 7));
    }

    [Fact]
    public void SelfAndOpponentKeysAreDistinct()
    {
        var ledger = new TournamentPitchLedger();
        var self = PitcherLedgerKey.ForPlayer(7);
        var opp = PitcherLedgerKey.ForOpponent(schoolId: 7, uniformNumber: 1);
        ledger.Record(self, 100, matchDay: 1);
        ledger.Record(opp, 50, matchDay: 1);

        Assert.Equal(100, ledger.PitchesWithin(self, 2, 7));
        Assert.Equal(50, ledger.PitchesWithin(opp, 2, 7));
    }

    [Fact]
    public void OpponentKeyedBySchoolAndNumber()
    {
        var ledger = new TournamentPitchLedger();
        var aceOfA = PitcherLedgerKey.ForOpponent(schoolId: 100, uniformNumber: 1);
        var aceOfB = PitcherLedgerKey.ForOpponent(schoolId: 200, uniformNumber: 1);   // 別校の背番号1
        var subOfA = PitcherLedgerKey.ForOpponent(schoolId: 100, uniformNumber: 11);  // 同校の別投手
        ledger.Record(aceOfA, 130, matchDay: 1);
        ledger.Record(aceOfB, 40, matchDay: 1);
        ledger.Record(subOfA, 20, matchDay: 1);

        Assert.Equal(130, ledger.PitchesWithin(aceOfA, 2, 7));
        Assert.Equal(40, ledger.PitchesWithin(aceOfB, 2, 7));
        Assert.Equal(20, ledger.PitchesWithin(subOfA, 2, 7));
    }

    [Fact]
    public void SameDayOutingsAreCombined()
    {
        // 同一試合日の複数記録（継投を跨いだ再登板等）は合算される。
        var ledger = new TournamentPitchLedger();
        var p = PitcherLedgerKey.ForPlayer(1);
        ledger.Record(p, 60, matchDay: 3);
        ledger.Record(p, 25, matchDay: 3);
        Assert.Equal(85, ledger.PitchesWithin(p, 4, 7));
    }

    [Fact]
    public void RestDaysAndLastOuting()
    {
        var ledger = new TournamentPitchLedger();
        var p = PitcherLedgerKey.ForPlayer(1);
        ledger.Record(p, 100, matchDay: 1);
        ledger.Record(p, 80, matchDay: 4);

        Assert.Equal(4, ledger.LastOutingDay(p, currentDay: 7));
        Assert.Equal(3, ledger.RestDays(p, currentDay: 7));     // 7 - 4
        // 当日を含まないので、直近登板日と同日ならその前の登板を見る。
        Assert.Equal(1, ledger.LastOutingDay(p, currentDay: 4));
    }

    [Fact]
    public void UnseenPitcherIsFresh()
    {
        var ledger = new TournamentPitchLedger();
        var p = PitcherLedgerKey.ForPlayer(999);
        Assert.Equal(0, ledger.PitchesWithin(p, 5, 7));
        Assert.Null(ledger.LastOutingDay(p, 5));
        Assert.Null(ledger.RestDays(p, 5));
    }

    [Fact]
    public void NonPositivePitchesIgnored()
    {
        var ledger = new TournamentPitchLedger();
        var p = PitcherLedgerKey.ForPlayer(1);
        ledger.Record(p, 0, matchDay: 1);
        ledger.Record(p, -5, matchDay: 1);
        Assert.Equal(0, ledger.PitchesWithin(p, 2, 7));
        Assert.Null(ledger.LastOutingDay(p, 2));
    }

    [Fact]
    public void ResetClearsAllOutings()
    {
        var ledger = new TournamentPitchLedger();
        var p = PitcherLedgerKey.ForPlayer(1);
        ledger.Record(p, 100, matchDay: 1);
        ledger.Reset();
        Assert.Equal(0, ledger.PitchesWithin(p, 2, 7));
        Assert.Null(ledger.RestDays(p, 2));
    }
}
