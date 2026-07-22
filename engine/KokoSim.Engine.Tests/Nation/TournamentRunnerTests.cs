using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 大会モードの進行体（設計書05 §1.2/§1.4）。自校の一戦停止・裏試合の自動消化・次戦相手/次戦日・
/// 優勝/敗退経路・不戦勝・決定論を検証する。
/// </summary>
public sealed class TournamentRunnerTests
{
    private static readonly NationCoefficients Coeff = new();
    private static readonly TournamentSchedule Schedule = new() { FirstRoundDay = 1, RoundGapDays = 3 };

    private static School Sch(int id, double strength) => new()
    {
        Id = id, Name = $"校{id}", PrefectureId = 0, Strength = strength,
    };

    /// <summary>自校(id=1)＋others 校の参加校リスト。</summary>
    private static List<School> Field(double managerStrength, int others, double othersStrength)
    {
        var list = new List<School> { Sch(1, managerStrength) };
        for (var i = 0; i < others; i++) list.Add(Sch(100 + i, othersStrength));
        return list;
    }

    private static TournamentRunner NewRunner(List<School> field, ulong seed = 42)
        => new(field, field[0], Coeff, new Xoshiro256Random(seed), Schedule, "テスト大会");

    private static void PlayToEnd(TournamentRunner r)
    {
        while (!r.Finished) r.PlayNextPlayerMatch();
    }

    [Fact]
    public void StrongManager_WinsChampionship()
    {
        var r = NewRunner(Field(99, 7, 30));
        PlayToEnd(r);

        Assert.True(r.IsChampion);
        Assert.True(r.PlayerActive);
        var view = r.BuildBracketView();
        Assert.Equal("校1", view.ChampionName);
        Assert.True(view.ManagerIsChampion);
        Assert.False(view.ManagerEliminated);
    }

    [Fact]
    public void WeakManager_EliminatedEarly_BracketStillCompletes()
    {
        var r = NewRunner(Field(8, 7, 95));
        PlayToEnd(r);

        Assert.False(r.IsChampion);
        Assert.False(r.PlayerActive);
        var view = r.BuildBracketView();
        Assert.True(view.ManagerEliminated);
        Assert.NotNull(view.ChampionName);            // 背景消化で優勝校まで確定する。
        Assert.NotEqual("校1", view.ChampionName);
    }

    [Fact]
    public void Determinism_SameSeedSameResult()
    {
        var a = NewRunner(Field(60, 15, 55), seed: 7);
        var b = NewRunner(Field(60, 15, 55), seed: 7);
        PlayToEnd(a);
        PlayToEnd(b);

        var va = a.BuildBracketView();
        var vb = b.BuildBracketView();
        Assert.Equal(va.ChampionName, vb.ChampionName);
        Assert.Equal(va.Matches.Count, vb.Matches.Count);
        Assert.Equal(a.IsChampion, b.IsChampion);
        for (var i = 0; i < va.Matches.Count; i++)
        {
            Assert.Equal(va.Matches[i].WinnerName, vb.Matches[i].WinnerName);
            Assert.Equal(va.Matches[i].WinnerScore, vb.Matches[i].WinnerScore);
            Assert.Equal(va.Matches[i].LoserScore, vb.Matches[i].LoserScore);
        }
    }

    [Fact]
    public void NextMatchDay_AdvancesByGap_WhenNoByes()
    {
        // 8校（2の冪, 不戦勝なし）。強い自校が確実に勝ち上がる。
        var r = NewRunner(Field(99, 7, 30));
        Assert.Equal(Schedule.MatchDay(0), r.NextMatchDay);      // 初戦。
        Assert.NotNull(r.NextOpponent);

        r.PlayNextPlayerMatch();                                // 1勝→2回戦へ。
        Assert.False(r.Finished);
        Assert.Equal(Schedule.MatchDay(1), r.NextMatchDay);     // 中3日ぶん進む。
    }

    [Fact]
    public void Bye_AdvancesManagerToNextRound()
    {
        // 3校＝ブラケットサイズ4。最強シードの自校は初戦不戦勝になる。
        var r = NewRunner(Field(99, 2, 40));

        Assert.True(r.PlayerActive);
        Assert.False(r.Finished);
        Assert.NotNull(r.NextOpponent);                         // 不戦勝を消化し実対戦相手が確定。
        Assert.Equal(Schedule.MatchDay(1), r.NextMatchDay);     // 初戦不戦勝で1ラウンド進んでいる。
    }

    [Fact]
    public void SoleEntrant_IsImmediateChampion()
    {
        var r = NewRunner(Field(50, 0, 0));
        Assert.True(r.IsChampion);
        Assert.True(r.Finished);
        Assert.Null(r.NextOpponent);
    }

    [Fact]
    public void Outcome_ReportsOpponentAndScore()
    {
        var r = NewRunner(Field(99, 7, 30));
        var opponent = r.NextOpponent!;
        var outcome = r.PlayNextPlayerMatch();

        Assert.True(outcome.ManagerWon);
        Assert.Equal(opponent.Name, outcome.OpponentName);
        Assert.Equal(opponent.Tier, outcome.OpponentTier);
        Assert.True(outcome.ManagerScore > outcome.OpponentScore);   // 勝者スコアが上。
    }

    // ===== 樹形図（BracketRound/BracketCard, 設計書05 §1.2） =====

    /// <summary>N=64 を最後まで回し、ラウンド構造・勝者の伝播・優勝校までの充填を検証する。</summary>
    [Fact]
    public void Bracket_Rounds_HaveHalvingSlots_AndWinnersPropagate()
    {
        var r = NewRunner(Field(60, 63, 55), seed: 11);
        PlayToEnd(r);
        var rounds = r.BuildBracketView().Rounds;

        Assert.Equal(new[] { 32, 16, 8, 4, 2, 1 }, rounds.Select(x => x.Cards.Count).ToArray());
        Assert.Equal(new[] { "1回戦", "2回戦", "3回戦", "準々決勝", "準決勝", "決勝" },
            rounds.Select(x => x.RoundName).ToArray());

        for (var i = 0; i < rounds.Count; i++)
        {
            Assert.Equal(i, rounds[i].Round);
            for (var s = 0; s < rounds[i].Cards.Count; s++) Assert.Equal(s, rounds[i].Cards[s].SlotIndex);
        }

        // 各カードの勝者が次ラウンドの SlotIndex/2（上下は SlotIndex%2）に現れる。
        for (var i = 0; i < rounds.Count - 1; i++)
        {
            foreach (var card in rounds[i].Cards)
            {
                var winner = WinnerNameOf(card);
                Assert.NotNull(winner);
                var nextCard = rounds[i + 1].Cards[card.SlotIndex / 2];
                var nextSlot = card.SlotIndex % 2 == 0 ? nextCard.Top : nextCard.Bottom;
                Assert.Equal(winner, nextSlot.TeamName);
            }
        }

        // 決勝スロットまで埋まり、優勝校＝決勝の勝者。
        var final = rounds[^1].Cards[0];
        Assert.True(final.IsPlayed);
        Assert.Equal(r.BuildBracketView().ChampionName, WinnerNameOf(final));
    }

    /// <summary>未消化ラウンドは空枠として返る（初戦だけ確定・以降は校名未確定）。</summary>
    [Fact]
    public void Bracket_UnplayedRounds_AreEmptyFrames()
    {
        var r = NewRunner(Field(60, 15, 55), seed: 3);
        var rounds = r.BuildBracketView().Rounds;

        Assert.Equal(4, rounds.Count);
        Assert.All(rounds[0].Cards, c => Assert.True(c.Top.IsDetermined && c.Bottom.IsDetermined));
        Assert.All(rounds[0].Cards, c => Assert.False(c.IsPlayed));      // 自校の初戦待ちで未消化。
        Assert.All(rounds[^1].Cards, c =>
        {
            Assert.False(c.Top.IsDetermined);
            Assert.False(c.Bottom.IsDetermined);
            Assert.Null(c.Top.Score);
        });

        // 自校の枠は初戦から自校としてマークされる（UIのアンバー強調の根拠）。
        Assert.Contains(rounds[0].Cards, c => c.Top.IsManager || c.Bottom.IsManager);
    }

    /// <summary>消化済みカードはスコアと勝者フラグを持ち、不戦勝は IsBye で表現される。</summary>
    [Fact]
    public void Bracket_PlayedCards_CarryScores_AndByesAreMarked()
    {
        var r = NewRunner(Field(99, 2, 40));                              // 3校＝サイズ4、自校は初戦不戦勝。
        var first = r.BuildBracketView().Rounds[0].Cards;
        Assert.Contains(first, c => c.IsBye);
        Assert.All(first.Where(c => c.IsBye), c => Assert.False(c.IsPlayed));

        PlayToEnd(r);
        foreach (var card in r.BuildBracketView().Rounds.SelectMany(x => x.Cards).Where(c => c.IsPlayed))
        {
            Assert.NotNull(card.Top.Score);
            Assert.NotNull(card.Bottom.Score);
            Assert.True(card.Top.IsWinner ^ card.Bottom.IsWinner);        // 勝者はどちらか一方だけ。
            var (win, lose) = card.Top.IsWinner
                ? (card.Top.Score!.Value, card.Bottom.Score!.Value)
                : (card.Bottom.Score!.Value, card.Top.Score!.Value);
            Assert.True(win > lose);
        }
    }

    /// <summary>同シードならスロット配置・スコアまで完全一致する（不変条件#2）。</summary>
    [Fact]
    public void Bracket_Determinism_SameSeedSameSlots()
    {
        var a = NewRunner(Field(60, 63, 55), seed: 5);
        var b = NewRunner(Field(60, 63, 55), seed: 5);
        PlayToEnd(a);
        PlayToEnd(b);

        var ra = a.BuildBracketView().Rounds;
        var rb = b.BuildBracketView().Rounds;
        Assert.Equal(ra.Count, rb.Count);
        for (var i = 0; i < ra.Count; i++) Assert.Equal(ra[i].Cards, rb[i].Cards);
    }

    private static string? WinnerNameOf(BracketCard card)
        => card.Top.IsWinner ? card.Top.TeamName
            : card.Bottom.IsWinner ? card.Bottom.TeamName
            : card.IsBye ? (card.Top.TeamName ?? card.Bottom.TeamName)
            : null;

    // ライブ観戦（Begin/Complete 制御反転）が自動消化（PlayNextPlayerMatch）と同じ大会展開になる＝
    // 「観戦してもしなくても結果は同じ」＋「本流RNGの消費順は不変」の核心保証（設計書の決定論ゲート相当）。
    [Theory]
    [InlineData(9UL)]
    [InlineData(21UL)]
    [InlineData(2026UL)]
    public void LiveBeginComplete_EqualsAutoPlayNextMatch(ulong seed)
    {
        var resolver = new LiveConsistentResolver();
        var auto = new TournamentRunner(Field(70, 15, 55), Sch(1, 70), Coeff,
            new Xoshiro256Random(seed), Schedule, "t", resolver);
        var live = new TournamentRunner(Field(70, 15, 55), Sch(1, 70), Coeff,
            new Xoshiro256Random(seed), Schedule, "t", resolver);

        while (!auto.Finished)
        {
            var oAuto = auto.PlayNextPlayerMatch();

            var lm = live.BeginNextPlayerMatch();
            Assert.Equal(oAuto.OpponentName, lm.OpponentName);   // 同じ相手を観戦している。
            while (lm.Progression.Advance()) { /* 采配なしで全打席 */ }
            var oLive = live.CompleteNextPlayerMatch(lm.Progression.BuildResult());

            Assert.Equal(oAuto.ManagerWon, oLive.ManagerWon);
            Assert.Equal(oAuto.ManagerScore, oLive.ManagerScore);
            Assert.Equal(oAuto.OpponentScore, oLive.OpponentScore);
            Assert.Equal(oAuto.OpponentName, oLive.OpponentName);
            Assert.Equal(oAuto.IsChampion, oLive.IsChampion);
        }

        Assert.Equal(auto.IsChampion, live.IsChampion);
        Assert.Equal(auto.PlayerActive, live.PlayerActive);

        // 観戦（BeginLive）と自動消化（Resolve）は同一ラウンド順で常に同じ mercyRuleEnabled を受け取る
        // （設計書05 §1.3, Q18の決定論契約＝観戦しても大会結果もルールも変わらない）。
        Assert.Equal(resolver.ResolveMercySeen, resolver.LiveMercySeen);
        Assert.NotEmpty(resolver.ResolveMercySeen);

        // 裏試合も含め大会概要（ブラケット全行）がバイト一致＝本流RNG消費順が完全に保たれている。
        var va = auto.BuildBracketView();
        var vb = live.BuildBracketView();
        Assert.Equal(va.ChampionName, vb.ChampionName);
        Assert.Equal(va.Matches.Count, vb.Matches.Count);
        for (var i = 0; i < va.Matches.Count; i++)
        {
            Assert.Equal(va.Matches[i].WinnerName, vb.Matches[i].WinnerName);
            Assert.Equal(va.Matches[i].LoserName, vb.Matches[i].LoserName);
            Assert.Equal(va.Matches[i].WinnerScore, vb.Matches[i].WinnerScore);
            Assert.Equal(va.Matches[i].LoserScore, vb.Matches[i].LoserScore);
        }
    }

    // Complete し損ねたライブ自校戦（観戦画面を離脱した等）から復帰できること。Begin を再度呼んでも例外で
    // 詰まず、同一の進行体が返り、そのまま Complete して大会が進む＝取り残しが行き止まりにならない保証。
    [Fact]
    public void PendingLiveMatch_IsResumable_AndDoesNotBlockTournament()
    {
        var resolver = new LiveConsistentResolver();
        var r = new TournamentRunner(Field(70, 15, 55), Sch(1, 70), Coeff,
            new Xoshiro256Random(2026), Schedule, "t", resolver);

        var first = r.BeginNextPlayerMatch();
        Assert.True(r.HasPendingLiveMatch);

        // 観戦画面を離脱した想定＝Complete せずに再度 Begin / Resume する。
        var again = r.BeginNextPlayerMatch();
        Assert.Same(first.Progression, again.Progression);
        Assert.Equal(first.OpponentName, again.OpponentName);
        Assert.Same(first.Progression, r.ResumePendingLiveMatch().Progression);

        while (again.Progression.Advance()) { /* 采配なしで全打席 */ }
        r.CompleteNextPlayerMatch(again.Progression.BuildResult());

        Assert.False(r.HasPendingLiveMatch);
        Assert.Equal(Schedule.MatchDay(1), r.NextMatchDay);   // ラウンドが進んだ（日程が据え置かれない）。
    }

    [Fact]
    public void ResumePendingLiveMatch_ThrowsWhenNothingPending()
    {
        var r = new TournamentRunner(Field(70, 15, 55), Sch(1, 70), Coeff,
            new Xoshiro256Random(3), Schedule, "t", new LiveConsistentResolver());
        Assert.False(r.HasPendingLiveMatch);
        Assert.Throws<System.InvalidOperationException>(() => r.ResumePendingLiveMatch());
    }

    /// <summary>
    /// Resolve と BeginLive を同一 teams＋同一Fork で作る整合リゾルバ（実 PlayerMatchResolver の契約と同形）。
    /// 自校＝後攻(home)。全打席を進めた BeginLive の結果は Resolve のボックススコアに一致する。
    /// </summary>
    private sealed class LiveConsistentResolver : IPlayerMatchResolver
    {
        /// <summary>Resolve/BeginLive が受け取った mercyRuleEnabled を呼び出し順に記録（決定論契約の検証用）。</summary>
        public List<bool> ResolveMercySeen { get; } = new();
        public List<bool> LiveMercySeen { get; } = new();

        public PlayerMatchDetail Resolve(School manager, School opponent, IRandomSource rng, bool mercyRuleEnabled)
        {
            ResolveMercySeen.Add(mercyRuleEnabled);
            return new(GameEngine.Play(MakeTeam(opponent.Name, opponent.Strength),
                MakeTeam(manager.Name, manager.Strength), new GameContext { MercyRuleEnabled = mercyRuleEnabled },
                rng.Fork(2)), ManagerIsAway: false);
        }

        public PlayerMatchLive BeginLive(School manager, School opponent, IRandomSource rng, bool mercyRuleEnabled)
        {
            LiveMercySeen.Add(mercyRuleEnabled);
            return new(new MatchProgression(MakeTeam(opponent.Name, opponent.Strength),
                MakeTeam(manager.Name, manager.Strength),
                new GameContext { CaptureTimelines = true, MercyRuleEnabled = mercyRuleEnabled }, rng.Fork(2)),
                ManagerIsAway: false);
        }

        private static Team MakeTeam(string name, double strength)
        {
            var ab = (int)System.Math.Clamp(strength, 20, 90);
            Player Pos(FieldPosition p) => new()
            {
                Position = p, Contact = ab, Power = ab, LaunchTendency = 50, Discipline = ab,
                Speed = ab, ArmStrength = ab, Fielding = ab, Catching = ab,
            };
            var order = new List<Player>
            {
                Pos(FieldPosition.Catcher), Pos(FieldPosition.FirstBase), Pos(FieldPosition.SecondBase),
                Pos(FieldPosition.ThirdBase), Pos(FieldPosition.Shortstop), Pos(FieldPosition.LeftField),
                Pos(FieldPosition.CenterField), Pos(FieldPosition.RightField),
                Pos(FieldPosition.Pitcher) with { Name = name + "P", Pitching = PitcherAttributes.LeagueAverage },
            };
            return new Team { Name = name, BattingOrder = order, PitcherSlot = 8 };
        }
    }

    // ===== コールドゲーム（マーシールール）の大会単位トグル（設計書05 §1.3, OPEN-QUESTIONS Q18） =====

    [Fact]
    public void MercyRule_OnForEarlyRounds_OffForFinal()
    {
        // 4校＝2ラウンド（準決勝→決勝）。自校は毎回勝ち上がるよう最強シードにする。
        var resolver = new LiveConsistentResolver();
        var r = new TournamentRunner(Field(99, 3, 20), Sch(1, 99), Coeff,
            new Xoshiro256Random(1), Schedule, "t", resolver);

        while (!r.Finished) r.PlayNextPlayerMatch();

        Assert.Equal(new[] { true, false }, resolver.ResolveMercySeen);   // 準決勝=ON, 決勝=OFF
    }

    [Fact]
    public void MercyRule_AlwaysOff_WhenNationalTournament()
    {
        var resolver = new LiveConsistentResolver();
        var r = new TournamentRunner(Field(99, 3, 20), Sch(1, 99), Coeff,
            new Xoshiro256Random(1), Schedule, "t", resolver, isNationalTournament: true);

        while (!r.Finished) r.PlayNextPlayerMatch();

        Assert.All(resolver.ResolveMercySeen, Assert.False);
        Assert.NotEmpty(resolver.ResolveMercySeen);
    }

    [Fact]
    public void MercyEnded_PropagatesToOutcome_AndBracketMatch()
    {
        // 自校=強豪・相手=弱小の準決勝で大差コールドが起きるはずの資源配分（GameEngineTests の弱小生成と同じ狙い）。
        var resolver = new MercyProneResolver();
        var r = new TournamentRunner(Field(99, 3, 20), Sch(1, 99), Coeff,
            new Xoshiro256Random(1), Schedule, "t", resolver);

        var outcome = r.PlayNextPlayerMatch();   // 準決勝（ラウンド残り2）＝コールドON。

        Assert.True(outcome.MercyEnded);
        var playedCard = r.BuildBracketView().Matches.Single(m => m.ManagerInvolved);
        Assert.True(playedCard.MercyEnded);
    }

    /// <summary>
    /// ブラケット全試合（自校戦＋裏試合）の勝敗が漏れなく <see cref="SchoolRecordBook"/> へ積まれる回帰テスト
    /// （issue #84）。1試合＝1勝1敗なので、全校の勝ち数の総和は試合数に一致するはず。
    /// </summary>
    [Fact]
    public void FoldTournament_SumOfWins_EqualsMatchCount()
    {
        var r = NewRunner(Field(55, 15, 50), seed: 3);
        PlayToEnd(r);
        var matches = r.BuildBracketView().Matches;

        var book = new SchoolRecordBook();
        book.FoldTournament(matches, TournamentKind.Summer, currentYear: 2028);

        var totalWins = 0;
        var totalLosses = 0;
        foreach (var rec in book.Records.Values)
        {
            totalWins += rec.OfficialWins;
            totalLosses += rec.OfficialLosses;
        }
        Assert.Equal(matches.Count, totalWins);
        Assert.Equal(matches.Count, totalLosses);
    }

    [Fact]
    public void FoldTournament_SummerChampion_GetsKoshienAppearance()
    {
        var r = NewRunner(Field(99, 7, 30), seed: 3); // 自校が確実に優勝する戦力差。
        PlayToEnd(r);
        Assert.True(r.IsChampion);
        var matches = r.BuildBracketView().Matches;

        var book = new SchoolRecordBook();
        book.FoldTournament(matches, TournamentKind.Summer, currentYear: 2028);

        var championRecord = book.For(1); // Sch(1, ...) が自校。
        Assert.Equal(1, championRecord.SummerAppearances);
        Assert.Equal(2028, championRecord.LastSummerYear);
        Assert.Equal("初出場", championRecord.SummerAppearanceLabel);
        Assert.Equal(BestResult.Appearance, championRecord.BestResult);
    }

    [Fact]
    public void FoldTournament_AutumnTournament_DoesNotRecordKoshienAppearance()
    {
        var r = NewRunner(Field(99, 7, 30), seed: 3);
        PlayToEnd(r);
        var matches = r.BuildBracketView().Matches;

        var book = new SchoolRecordBook();
        book.FoldTournament(matches, TournamentKind.Autumn, currentYear: 2028);

        Assert.Equal(0, book.For(1).SummerAppearances);
        Assert.Equal(0, book.For(1).TotalAppearances);
        // 秋も公式戦なので勝敗は積む。
        Assert.True(book.For(1).OfficialWins > 0);
    }

    /// <summary>強豪 vs 弱小で確実に大差コールドを起こすリゾルバ（GameEngineTests の弱小生成と同じ狙い）。</summary>
    private sealed class MercyProneResolver : IPlayerMatchResolver
    {
        public PlayerMatchDetail Resolve(School manager, School opponent, IRandomSource rng, bool mercyRuleEnabled)
        {
            var ctx = new GameContext { MercyRuleEnabled = mercyRuleEnabled };
            var result = GameEngine.Play(WeakTeam(opponent.Name), StrongTeam(manager.Name), ctx, rng.Fork(2));
            return new(result, ManagerIsAway: false);
        }

        public PlayerMatchLive BeginLive(School manager, School opponent, IRandomSource rng, bool mercyRuleEnabled)
            => throw new System.NotSupportedException();

        private static Team StrongTeam(string name) => MakeTeam(name, 90);
        private static Team WeakTeam(string name) => MakeTeam(name, 5);

        private static Team MakeTeam(string name, int ability)
        {
            Player Pos(FieldPosition p) => new()
            {
                Position = p, Contact = ability, Power = ability, LaunchTendency = 50, Discipline = ability,
                Speed = ability, ArmStrength = ability, Fielding = ability, Catching = ability,
            };
            var order = new List<Player>
            {
                Pos(FieldPosition.Catcher), Pos(FieldPosition.FirstBase), Pos(FieldPosition.SecondBase),
                Pos(FieldPosition.ThirdBase), Pos(FieldPosition.Shortstop), Pos(FieldPosition.LeftField),
                Pos(FieldPosition.CenterField), Pos(FieldPosition.RightField),
                Pos(FieldPosition.Pitcher) with
                {
                    Name = name + "P",
                    Pitching = new PitcherAttributes
                    {
                        MaxVelocityKmh = ability >= 50 ? 150 : 110, Control = ability, StaminaPitches = 90,
                        PitchRank = ability,
                    },
                },
            };
            return new Team { Name = name, BattingOrder = order, PitcherSlot = 8 };
        }
    }

    [Fact]
    public void ThrowsWhenManagerNotInField()
    {
        var field = Field(50, 7, 50);
        var outsider = Sch(999, 50);
        Assert.Throws<System.ArgumentException>(
            () => new TournamentRunner(field, outsider, Coeff, new Xoshiro256Random(1), Schedule, "x"));
    }
}
