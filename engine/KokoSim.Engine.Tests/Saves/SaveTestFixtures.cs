using System.Collections.Generic;
using KokoSim.Engine.Career;
using KokoSim.Engine.Career.Draft;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Players;
using KokoSim.Engine.Saves;
using KokoSim.Engine.Season;
using KokoSim.Engine.Stats;
using NationT = KokoSim.Engine.Nation.Nation;

namespace KokoSim.Engine.Tests.Saves;

/// <summary>セーブ往復テストの共有素材（issue #507）。実際の生成レシピ（seed=42 の自校ロスター・seed=2026 の
/// 全国）を踏襲しつつ、可変フィールドをひととおり触った「進行済み」の状態を組む。</summary>
internal static class SaveTestFixtures
{
    private static readonly NationCoefficients NationCoeff = new();
    private static readonly SchoolNameVocab Vocab = new();

    /// <summary>自校ロスターを生成し、能力/経験値/怪我/球種/スキル/練習計画/引退などを触って「進行済み」にする。</summary>
    public static List<DevelopingPlayer> BuildManagerRoster(ulong seed = 42)
    {
        var rng = new Xoshiro256Random(seed);
        var coeff = new RosterCoefficients();
        var list = new List<DevelopingPlayer>();
        for (var grade = 1; grade <= 3; grade++)
            foreach (var p in ProspectGenerator.Intake(grade, coeff, rng))
            {
                p.Grade = grade;
                list.Add(p);
            }
        for (var i = 0; i < list.Count; i++) list[i].Id = i + 1;
        UniformNumberAssigner.AutoAssign(list);
        CaptainSelector.EnsureCaptain(list);

        // 進行済みの痕跡を刻む（分数の経験値・怪我・調子・習得球種・スキル・練習計画・引退）。
        list[0].AddExp(AbilityKind.Contact, 12.5);
        list[0].AddExp(AbilityKind.Power, 7.25);
        list[0].AddAptitudeExp(FieldPosition.SecondBase, 3.5);
        list[0].SetLevel(AbilityKind.Contact, list[0].Level(AbilityKind.Contact) + 1);
        list[1].Injury = InjurySeverity.Moderate;
        list[1].InjurySite = InjurySite.Elbow;
        list[1].InjuryType = InjuryType.Strain;
        list[1].InjuryWeeksRemaining = 3;
        list[2].ConditionValue = 0.4275;
        list[3].LearnedPitches.Add(new LearnedPitch(PitchType.Slider, 3, 5));
        list[3].LearnedPitches.Add(new LearnedPitch(PitchType.Fork, -2, 4));
        list[4].Skills = new SkillSet(new[] { Skill.Grinder, Skill.SprayHitter }, new[] { Skill.Monster });
        list[5].Plan = new TrainingPlan
        {
            Preset = TrainingPreset.Custom,
            Allocations = new[]
            {
                new MenuAllocation(TrainingMenu.Batting, 120),
                new MenuAllocation(TrainingMenu.VelocityTraining, 60),
            },
        };
        list[6].IsRetired = true;
        list[7].UniformNumber = 18;
        return list;
    }

    public static NationT BuildNation(ulong seed = 2026)
        => NationGenerator.Generate(Vocab, NationCoeff, new Xoshiro256Random(seed));

    /// <summary>自校の合成ボックススコア1試合（away 側だけ埋める＝monagerIsAway で畳み込む）。</summary>
    public static GameResult SyntheticGame()
    {
        var byPitch = new Dictionary<PitchType, PitchTypeBattingLine>
        {
            [PitchType.Slider] = new PitchTypeBattingLine(AtBats: 3, Hits: 1, HomeRuns: 0),
            [PitchType.Fastball] = new PitchTypeBattingLine(AtBats: 2, Hits: 1, HomeRuns: 1),
        };
        return new GameResult
        {
            AwayName = "桜丘",
            HomeName = "相手校",
            AwayRuns = 4,
            HomeRuns = 2,
            InningsPlayed = 9,
            TotalPitches = 240,
            PitcherChanges = 0,
            AwayBatting = new[]
            {
                new BattingLine(1, FieldPosition.SecondBase, "一番", 5, 4, 2, 1, 0, 1, 3, 1, 1, SourceId: 1,
                    StolenBases: 1, Runs: 1),
                new BattingLine(2, FieldPosition.Shortstop, "二番", 4, 4, 1, 0, 0, 0, 0, 0, 1, SourceId: 2),
            },
            AwayPitching = new[]
            {
                new PitchingLine("先発", Outs: 27, BattersFaced: 34, Hits: 5, Runs: 2, StrikeOuts: 8, Walks: 2,
                    Pitches: 120, SourceId: 1, HomeRunsAllowed: 1, BattingAgainstByPitch: byPitch, EarnedRuns: 2),
            },
            AwayFielding = new[]
            {
                new FieldingLine(SourceId: 2, Position: FieldPosition.Shortstop, Name: "二番", Errors: 1),
            },
        };
    }

    /// <summary>ひととおり埋めた「進行済み」の SaveState を1つ組む（DTO 往復テストの素材）。</summary>
    public static SaveState BuildRichState()
    {
        var nation = BuildNation();
        var deps = new AiRosterDeps();

        // 全国ロスターを数校 materialize（年度3で3学年ブートストラップ）。
        var rosters = new NationRosters(deps);
        var sample = SampleSchools(nation, 6);
        rosters.BootstrapAll(sample, 3);

        var stats = new PlayerStatStore();
        stats.StartTournament();
        stats.FoldGame(SyntheticGame(), managerIsAway: true, isOfficial: true);
        // 大会別アーカイブ（issue #77）＋その年度（v8・issue #600）。学年・背番号・年が往復すること。
        stats.ArchiveCurrentTournament(TournamentSlot.SummerPref,
            new Dictionary<int, (int Grade, int UniformNumber)> { [1] = (3, 1), [2] = (2, 18) }, year: 2);

        // 全国の学校別成績（#43）。今大会と通算で値が別々になる状態にする（issue #634）＝
        // 前の大会ぶんを畳んでから StartTournament で今大会だけ消し、もう1試合積む。
        var nationStats = new NationTournamentStats();
        nationStats.FoldMatch(awaySchoolId: 200, homeSchoolId: 300, SyntheticGame());
        nationStats.StartTournament();
        nationStats.FoldMatch(awaySchoolId: 200, homeSchoolId: 300, SyntheticGame());

        var records = new SchoolRecordBook();
        records.RecordOfficialMatch(winnerId: 100, loserId: 200);
        records.RecordOfficialMatch(winnerId: 100, loserId: 300);
        records.RecordKoshienAppearance(100, KoshienKind.Summer, currentYear: 2027);

        var facilities = new FacilitySet();
        facilities.SetLevel("lighting", 2);
        facilities.SetLevel("indoor", 1);

        // 年契約（設計書04 §1.4a）。既定（スカウト網 Lv1 だけ）と区別できる状態にして往復を測る。
        var contracts = new ContractSet();
        contracts.SetLevel("scouting", 3);
        contracts.SetLevel("coach_pitching", 1);

        return new SaveState
        {
            MasterSeed = 0xDEADBEEFCAFEF00DUL,
            Week = 7,
            YearIndex = 2,
            Roster = BuildManagerRoster(),
            Manager = new Manager
            {
                Name = "テスト監督",
                CoachingBatting = 41.5,
                CoachingPitching = 33.0,
                CoachingDefense = 28.25,
                TalentEye = 37.0,
                TacticalSense = 55.0,
                Fame = 22.0,
                Trust = 61.0,
                Funds = 137.5,
                FacilityFund = 8642.5,
                CareerYears = 3,
                KoshienAppearances = 1,
            },
            Facilities = facilities,
            Contracts = contracts,
            ManagerLastGrownYear = 1,
            ManagerGrownWinsBaseline = 4,
            ManagerGrownKoshienBaseline = 1,
            NationRosters = new List<AiSchoolRoster>(rosters.Snapshot()),
            TournamentStats = nationStats,
            SessionYear = 2,
            Stats = stats,
            Records = records,
            NewTeamPending = true,
            NewTeamRetiredNames = new List<string> { "卒業太郎", "卒業次郎" },
            TeamColorOverride = new TeamColors(
                new RgbColor(0x1D, 0x32, 0x27), new RgbColor(0xED, 0xEB, 0xE0),
                new RgbColor(0xE8, 0x6A, 0x4A), new RgbColor(0xF5, 0xC6, 0x4A),
                new RgbColor(0x14, 0x23, 0x1B), new RgbColor(0x00, 0x00, 0x00),
                TeamColors.DefaultShoes, new TeamStripe(true, new RgbColor(0x23, 0x24, 0x2A), 40.0)),
            // 自校の同定（v4・issue #585）。既定（桜丘・神奈川13・地区null）と全部違う値にして往復を試す。
            SchoolName = "北嶺",
            PrefectureId = 27,      // 兵庫県
            DistrictId = 3,
            // 入部予定者（v5・issue #587）。3月に確定し、まだ合流していない状態で採る。
            Incoming = BuildIncomingClass(),
            // 能動勧誘（v6・issue #588）。秋にリストが出て、訪問を重ねた途中の状態で採る。
            Recruiting = BuildRecruitingState(),
            // 教え子名鑑（v7・issue #586）。過年度の指名を2件だけ積んだ状態で採る。
            Draft = BuildDraftHistory(),
            // 歴代大会結果（v8・issue #600）。優勝した年・敗退した年・優勝校が未確定の年を混ぜる。
            TournamentHistory = BuildTournamentHistory(),
        };
    }

    /// <summary>
    /// 歴代大会結果3件（issue #600）。優勝／準優勝／優勝校が空欄の3通りを混ぜて往復を測る。
    /// </summary>
    public static TournamentHistory BuildTournamentHistory()
    {
        var history = new TournamentHistory();
        history.Add(new TournamentHistoryEntry(
            Year: 1, Kind: TournamentKind.Summer, ReachedRoundName: "3回戦",
            ManagerIsChampion: false, ChampionId: 220, ChampionName: "弘学学園"));
        history.Add(new TournamentHistoryEntry(
            Year: 1, Kind: TournamentKind.Autumn, ReachedRoundName: "決勝",
            ManagerIsChampion: true, ChampionId: 100, ChampionName: "北嶺"));
        history.Add(new TournamentHistoryEntry(
            Year: 2, Kind: TournamentKind.Summer, ReachedRoundName: "準々決勝",
            ManagerIsChampion: false, ChampionId: 0, ChampionName: ""));
        return history;
    }

    /// <summary>
    /// 過年度の指名確定2件（issue #586）。実物の指名（<see cref="DraftEngine.RunNomination"/>）から採り、
    /// 指名が出ない引きに当たっても台帳が空にならないよう手組みの1件を足しておく。
    /// </summary>
    public static DraftHistory BuildDraftHistory()
    {
        var history = new DraftHistory();
        history.Add(new DraftRecord(
            Year: 2027, YearIndex: 2, PlayerId: 41, Name: "中田　大祐",
            UniformNumber: 1, Round: 2, Band: DraftRankBand.UpperRound, Notability: 78.5));
        history.Add(new DraftRecord(
            Year: 2028, YearIndex: 3, PlayerId: 52, Name: "岩崎　幹太",
            UniformNumber: 18, Round: 6, Band: DraftRankBand.LowerRound, Notability: 61.25));
        return history;
    }

    /// <summary>
    /// 訪問を重ねた途中の勧誘状態（issue #588）。実物の生成・訪問・見立てから採る
    /// （手で組んだ DTO では「保存できる形」しか試せない）。
    /// </summary>
    public static RecruitingState BuildRecruitingState()
    {
        var coeff = new RecruitCoefficients();
        var state = RecruitModel.BuildList(
            yearIndex: 3, slots: 2, homePrefectureId: 27, homeTier: Tier.C,
            talentCenter: new RosterCoefficients().TalentCenterDefault,
            roster: new RosterCoefficients(), c: coeff, rng: new Xoshiro256Random(9101));

        var visitRng = new Xoshiro256Random(9102);
        var scoutRng = new Xoshiro256Random(9103);
        var strength = new TeamStrengthCoefficients();
        var scoutCoeff = new ProspectScoutingCoefficients();
        for (var i = 0; i < state.Prospects.Count; i++)
        {
            var p = state.Prospects[i];
            p.Report = ProspectScouting.Survey(p.Player, 0.45, strength, scoutCoeff, scoutRng);
            if (i >= 2) continue;
            p.Targeted = true;                                   // 枠2つを使っている途中
            for (var v = 0; v <= i; v++) RecruitModel.Visit(p, coeff, visitRng);
        }
        return state;
    }

    /// <summary>
    /// 3月に確定した入部予定者（issue #587）。実物の生成（<see cref="RosterLifecycle.ConfirmIncoming"/>）と
    /// 実物の下見（<see cref="ProspectScouting.Survey"/>）から採る＝手で組んだ DTO では
    /// 「保存できる形」しか試せない。
    /// </summary>
    public static IncomingClass BuildIncomingClass()
    {
        var coeff = new RosterCoefficients();
        var roster = BuildManagerRoster(seed: 4242);
        var players = RosterLifecycle.ConfirmIncoming(roster, year: 3, coeff, new Xoshiro256Random(9001));
        var reports = ProspectScouting.Survey(
            players, disclosure: 0.45, new TeamStrengthCoefficients(),
            new ProspectScoutingCoefficients(), new Xoshiro256Random(9002));
        return new IncomingClass
        {
            YearIndex = 3,
            Disclosure = 0.45,
            Players = new List<DevelopingPlayer>(players),
            Reports = reports,
        };
    }

    /// <summary>
    /// 大会中セーブ（issue #565 F1）の素材。<b>実物の進行体から採る</b>＝手で組んだ DTO だと
    /// 「保存できる形」しか試せず、実際の進行が持つ形（不戦勝の null 枠・未到達ラウンドの null 行・
    /// 乱数の内部状態）を取りこぼす。
    /// </summary>
    public static TournamentSaveState BuildTournamentState()
    {
        // 12校＝16枠のうち4枠が不戦勝(null)になる素材。1試合消化した「試合と試合の間」で採る。
        var field = new List<School> { new() { Id = -1, Name = "自校", PrefectureId = 13, Strength = 62 } };
        for (var i = 0; i < 11; i++)
            field.Add(new School { Id = 500 + i, Name = $"校{i}", PrefectureId = 13, Strength = 30 + i * 5 });

        var runner = new TournamentRunner(
            field, field[0], NationCoeff, new Xoshiro256Random(20260810UL),
            new TournamentSchedule(), "2028年 選手権神奈川大会");
        runner.PlayNextPlayerMatch();

        return new TournamentSaveState
        {
            Kind = TournamentKind.Summer,
            Title = "2028年 選手権神奈川大会",
            TournamentDay = 3,
            BannerPending = false,
            FieldSchoolIds = field.ConvertAll(s => s.Id),
            Runner = runner.CaptureState(),
        };
    }

    /// <summary>本拠県内から先頭 n 校（自校ID -1 は含まない・SchoolId 昇順）。</summary>
    public static List<School> SampleSchools(NationT nation, int n)
    {
        var list = new List<School>();
        foreach (var s in nation.Schools)
        {
            if (s.Id < 0) continue;
            list.Add(s);
            if (list.Count >= n) break;
        }
        list.Sort((a, b) => a.Id.CompareTo(b.Id));
        return list;
    }
}
