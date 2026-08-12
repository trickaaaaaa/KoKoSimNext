using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Nation.Tournaments;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 球数台帳（issue #41）のキーが AI校をまたいで衝突しないこと（OPEN-QUESTIONS Q26・2026-08-12 修正）。
///
/// <para><b>何が壊れていたか</b>: 旧実装は「SourceId を持つ＝自校」と決め打ちしてキーを選んでいたが、
/// AI校の選手も <c>AiRosterFactory.Bootstrap</c> が <c>nextId = 1</c> から振った<b>校内選手ID</b>を
/// SourceId に持つ。結果、全AI校の「選手ID 5」が同じキーへ潰れ（実測: 64校フルシム大会で79登板→19キー）、
/// さらに唯一の読み手 <see cref="AceRestSelector"/> は <c>ForOpponent(校ID, 背番号)</c> で引くので
/// <b>AI校のエース温存は台帳を一度も見ていなかった</b>（fatigueLoad が常に0）。</para>
///
/// <para>絵にもログにも出ない壊れ方（先発が誰でも試合は成立する）なので、キーの形をここで固定する。</para>
/// </summary>
public sealed class TournamentPitchLedgerKeyTests
{
    private const int ManagerId = 900;

    private static List<School> Schools(int count)
    {
        var list = new List<School>();
        for (var i = 0; i < count; i++)
            list.Add(new School { Id = ManagerId + i, Name = $"校{ManagerId + i}", PrefectureId = 13, Strength = 50 + i });
        return list;
    }

    /// <summary>大会を1つ回し切って台帳の中身（セーブ用の平坦な列）を得る。</summary>
    private static List<TournamentPitchLedger.SavedOuting> RunAndCaptureLedger(int schoolCount, ulong seed)
    {
        var schools = Schools(schoolCount);
        var rosters = new NationRosters(new AiRosterDeps());
        var bg = new BackgroundMatchResolver(rosters, new GameContext(), yearIndex: 1);
        var runner = new TournamentRunner(
            schools, schools[0], new NationCoefficients(), new Xoshiro256Random(seed),
            new TournamentSchedule(), "テスト大会", playerResolver: null, backgroundResolver: bg);

        while (!runner.Finished) runner.PlayNextPlayerMatch();
        runner.DrainBracketResolution();
        return runner.CaptureState().PitchLedger;
    }

    [Fact]
    public void EveryAiSchool_GetsItsOwnBucket_InsteadOfCollapsingIntoSharedKeys()
    {
        var ledger = RunAndCaptureLedger(schoolCount: 8, seed: 124);
        Assert.NotEmpty(ledger);

        var opponentSchools = new HashSet<int>();
        foreach (var o in ledger) if (!o.BySource) opponentSchools.Add(o.KeyA);

        // 8校の単純トーナメント＝自校以外の7校も必ず1試合は投げる。7校すべてが**自分の枠**を持つこと
        // ＝旧実装のように「全AI校の選手ID 5」が1つのキーへ潰れていない（実測79登板→19キー）。
        for (var i = 1; i < 8; i++)
            Assert.Contains(ManagerId + i, opponentSchools);

        // 自校だけが SourceId キー側＝(校ID, 背番号) の枠に自校IDは現れない。
        Assert.DoesNotContain(ManagerId, opponentSchools);
    }

    [Fact]
    public void EveryOuting_LandsOnTheKeyTheAceRestSelectorReads()
    {
        // 読み手と書き手が揃っていること＝エースの登板が ForOpponent(校ID, 1) で必ず引けること。
        // 旧実装ではここが 0 のままで、連投の疲労が先発選択に一切効いていなかった。
        var schools = Schools(8);
        var rosters = new NationRosters(new AiRosterDeps());
        var bg = new BackgroundMatchResolver(rosters, new GameContext(), yearIndex: 1);
        var runner = new TournamentRunner(
            schools, schools[0], new NationCoefficients(), new Xoshiro256Random(124),
            new TournamentSchedule(), "テスト大会", playerResolver: null, backgroundResolver: bg);

        while (!runner.Finished) runner.PlayNextPlayerMatch();
        runner.DrainBracketResolution();

        var ledger = new TournamentPitchLedger();
        ledger.RestoreState(runner.CaptureState().PitchLedger);

        // 自校以外の7校のうち、エース（背番号1）が投げた校を数える。旧実装ではここが必ず 0 だった
        // ＝AI校のエース温存は台帳の消耗を一度も見ていなかった。
        var aceLoaded = 0;
        for (var i = 1; i < 8; i++)
        {
            var key = PitcherLedgerKey.ForOpponent(ManagerId + i, AceRestSelector.AceUniformNumber);
            // 大会全期間を覆う窓で引く（日付の刻み方に依存しないため）。
            if (ledger.PitchesWithin(key, currentDay: 10_000, windowDays: 10_000) > 0) aceLoaded++;
        }

        // 温存は確率抽選なので全校とは限らないが、既定係数では大半の校がエースで入る。
        Assert.True(aceLoaded >= 4,
            $"AceRestSelector が引くキー（校ID, 背番号1）に球数が乗った校が {aceLoaded}/7 しかない"
            + "＝読み手と書き手が揃っていない");
    }

    [Fact]
    public void ManagerSchoolOutings_StillUseThePlayerId()
    {
        // 自校（詳細シム経路）は育成選手IDのまま＝セーブ内で一意で、年をまたいでも安定する。
        // ここは playerResolver を通さないと自校カードが背景フルシムへ落ちるので、キーの選択規則を直接確かめる。
        var ledger = new TournamentPitchLedger();
        var own = PitcherLedgerKey.ForPlayer(5);
        var aiA = PitcherLedgerKey.ForOpponent(901, 1);
        var aiB = PitcherLedgerKey.ForOpponent(902, 1);

        ledger.Record(own, 100, matchDay: 1);
        ledger.Record(aiA, 110, matchDay: 1);
        ledger.Record(aiB, 120, matchDay: 1);

        // 3つとも別枠＝背番号1が全校で同じキーに潰れない。
        Assert.Equal(100, ledger.PitchesWithin(own, 2, 7));
        Assert.Equal(110, ledger.PitchesWithin(aiA, 2, 7));
        Assert.Equal(120, ledger.PitchesWithin(aiB, 2, 7));
    }
}
