using System.Collections.Generic;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 大会台帳→実効目安投球数の一括変換（<see cref="PitchRecoveryLookup"/>・issue #41 本流配線）。
/// 純関数の値は <c>PitchRecoveryTests</c> で確認済みなので、ここではロスター全体の走査・
/// キー選択（自校/相手校, OPEN-QUESTIONS Q26）・null/未登板の扱いを固定する。
/// </summary>
public sealed class PitchRecoveryLookupTests
{
    private static readonly PitchRecoveryCoefficients Coeff = new();

    private static Player MakePitcher(int uniformNumber, int? sourceId, double staminaPitches) => new()
    {
        Position = FieldPosition.Pitcher,
        UniformNumber = uniformNumber,
        SourceId = sourceId,
        Pitching = PitcherAttributes.LeagueAverage with { StaminaPitches = staminaPitches },
    };

    [Fact]
    public void BuildFor_NullLedger_ReturnsEmptyMap()
    {
        var roster = new[] { MakePitcher(1, null, 110.0) };
        var map = PitchRecoveryLookup.BuildFor(roster, p => PitcherLedgerKey.ForOpponent(1, p.UniformNumber), ledger: null, matchDay: 5, Coeff);
        Assert.Empty(map);
    }

    [Fact]
    public void BuildFor_FreshPitcher_MapsToRawStamina()
    {
        var ledger = new TournamentPitchLedger();
        var roster = new[] { MakePitcher(1, null, 110.0) };
        var map = PitchRecoveryLookup.BuildFor(
            roster, p => PitcherLedgerKey.ForOpponent(7, p.UniformNumber), ledger, matchDay: 1, Coeff);
        Assert.Equal(110.0, map[roster[0]], 6);
    }

    [Fact]
    public void BuildFor_HeavyRecentOuting_ReducesEffectiveStamina()
    {
        var ledger = new TournamentPitchLedger();
        var ace = MakePitcher(1, null, 110.0);
        var key = PitcherLedgerKey.ForOpponent(7, ace.UniformNumber);
        ledger.Record(key, 120, matchDay: 1);   // 前日に120球の完投級

        var map = PitchRecoveryLookup.BuildFor(new[] { ace }, p => PitcherLedgerKey.ForOpponent(7, p.UniformNumber), ledger, matchDay: 2, Coeff);
        Assert.True(map[ace] < 110.0, $"連戦の直後なのに実効が落ちていない: {map[ace]}");

        // 中6日空ければ（day8）フル回復。
        var restedMap = PitchRecoveryLookup.BuildFor(new[] { ace }, p => PitcherLedgerKey.ForOpponent(7, p.UniformNumber), ledger, matchDay: 8, Coeff);
        Assert.Equal(110.0, restedMap[ace], 6);
    }

    [Fact]
    public void ForMatch_NullContext_ReturnsEmptyMapsForBothTeams()
    {
        var away = new[] { MakePitcher(1, null, 110.0) };
        var home = new[] { MakePitcher(1, null, 105.0) };
        var (awayMap, homeMap) = PitchRecoveryLookup.ForMatch(
            context: null, Coeff,
            away, p => PitcherLedgerKey.ForOpponent(1, p.UniformNumber),
            home, p => PitcherLedgerKey.ForOpponent(2, p.UniformNumber));
        Assert.Empty(awayMap);
        Assert.Empty(homeMap);
    }

    [Fact]
    public void ForMatch_ReturnsSeparateDictionaries_EvenWhenBothTeamsHaveStructurallyIdenticalPlayers()
    {
        // Player は値等価の record。1つの辞書へ merge していた旧実装だと、両校の選手がたまたま
        // 全属性一致（このテストのように同じ MakePitcher 引数）した瞬間に同一キーへ潰れて片方の
        // 実効値が消えていた。チームごとに別辞書を返す今の実装ではこれが構造的に起きない。
        var ledger = new TournamentPitchLedger();
        // 両校とも背番号1のエースが前日に登板＝校IDで分けないとキーが衝突する（Q26と同型の罠）。
        ledger.Record(PitcherLedgerKey.ForOpponent(1, 1), 120, matchDay: 1);
        ledger.Record(PitcherLedgerKey.ForOpponent(2, 1), 20, matchDay: 1);

        var awayAce = MakePitcher(1, null, 110.0);
        var homeAce = MakePitcher(1, null, 110.0);   // 全属性一致（自校をまたいだ最悪ケース）
        var context = new TournamentMatchContext(RoundsRemaining: 2, MatchDay: 2, ledger);

        var (awayMap, homeMap) = PitchRecoveryLookup.ForMatch(
            context, Coeff,
            new[] { awayAce }, p => PitcherLedgerKey.ForOpponent(1, p.UniformNumber),
            new[] { homeAce }, p => PitcherLedgerKey.ForOpponent(2, p.UniformNumber));

        Assert.Single(awayMap);
        Assert.Single(homeMap);
        Assert.True(awayMap[awayAce] < homeMap[homeAce], "重い連投の校が軽い連投の校より実効が高い＝キーが混線している");
    }

    [Fact]
    public void PitcherLedgerKeyFor_ManagerSchool_UsesSourceId_OpponentSchool_UsesUniformNumber()
    {
        // OPEN-QUESTIONS Q26: 自校かどうかで分ける。SourceId の有無だけでは分けない。
        var own = PitcherLedgerKey.For(isManagerSchool: true, schoolId: 900, sourceId: 5, uniformNumber: 1);
        Assert.Equal(PitcherLedgerKey.ForPlayer(5), own);

        // 自校でも SourceId が無ければ (校ID, 背番号) へフォールバック。
        var ownFallback = PitcherLedgerKey.For(isManagerSchool: true, schoolId: 900, sourceId: null, uniformNumber: 3);
        Assert.Equal(PitcherLedgerKey.ForOpponent(900, 3), ownFallback);

        // 相手校は SourceId を持っていても (校ID, 背番号) を使う（自校IDと衝突させない）。
        var opponent = PitcherLedgerKey.For(isManagerSchool: false, schoolId: 901, sourceId: 5, uniformNumber: 1);
        Assert.Equal(PitcherLedgerKey.ForOpponent(901, 1), opponent);
        Assert.NotEqual(own, opponent);
    }

    [Fact]
    public void AllPlayers_EnumeratesBattingOrderReservesAndStartingPitcher_WithoutDuplicates()
    {
        var p1 = MakePitcher(1, null, 100.0);
        var p2 = MakePitcher(2, null, 100.0);
        var dh = MakePitcher(9, null, 100.0);
        var team = new Team
        {
            Name = "T",
            BattingOrder = new[] { p1 },
            Reserves = new[] { p2 },
            DhSlot = 0,
            StartingPitcher = dh,
        };
        var all = new List<Player>(PitchRecoveryLookup.AllPlayers(team));
        Assert.Equal(3, all.Count);
        Assert.Contains(p1, all);
        Assert.Contains(p2, all);
        Assert.Contains(dh, all);
    }
}
