// ViewModel層（設計書06 §1: エンジン→ViewModel→UXML）。UnityEngine 非依存に保つ。
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Career;
using KokoSim.Engine.Core;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;

namespace KokoSim.Unity.Shell
{
    /// <summary>主将指名ダイアログの候補1行（可視情報だけ。統率力の生値は出さない, 設計書09 §8）。</summary>
    public sealed class CaptainCandidateRow
    {
        /// <summary><see cref="RosterService.Active"/> 内の index（右ペインの詳細ビューを引くキー）。</summary>
        public int ActiveIndex;
        public string Number = "–";
        public string Name = "";
        public string GradeLabel = "3年";
        public int Mental;
        /// <summary>スタメンで主将にしたときの底上げの段階（G〜S。式は engine CaptainEffectTiers）。</summary>
        public string StarterTier = "G";
        /// <summary>ベンチで伝令に走らせたときの効きの段階（G〜S）。</summary>
        public string BenchTier = "G";
        /// <summary>この選手を主将にした場合の結束の段階（G〜S。設計書23 §4・issue #935）。</summary>
        public string CohesionTier = "D";
        /// <summary>自動選出された暫定主将か（初期選択に使う）。</summary>
        public bool IsInterim;
    }

    /// <summary>新チーム発足ダイアログに表示する一式（スナップショット）。</summary>
    public sealed class NewTeamView
    {
        public string Headline = "新チーム始動";
        public string Lead = "";
        public string RetiredLabel = "";
        /// <summary>新規開始の主将指名か（1年目・ゲーム開始時の1回）。</summary>
        public bool AtGameStart;
        public List<CaptainCandidateRow> Candidates = new List<CaptainCandidateRow>();
    }

    /// <summary>
    /// 新チーム発足（夏の3年引退の翌週）の導線。<see cref="GameClock"/> の週送りフックから開き、
    /// プレイヤーが次期主将を1回だけ指名して閉じる（設計書09 §8）。
    /// 状態はセッション静的（週と同じくシェル共有）。UnityEngine 非依存。
    /// </summary>
    public static class NewTeamService
    {
        private static readonly SeasonCalendar Calendar = new SeasonCalendar();
        private static readonly List<string> RetiredNames = new List<string>();

        /// <summary>主将指名の入力待ちか（ダイアログを出す条件）。</summary>
        public static bool Pending { get; private set; }

        /// <summary>
        /// 新チームを発足させた年度（<see cref="GameClock.YearIndex"/>。0＝今年度はまだ）（issue #954）。
        /// 夏の敗退直後に発足させた年は、第18週の固定フックを安全網として素通りさせる。
        /// </summary>
        public static int StartedYear { get; private set; }

        /// <summary>今年度の新チームはもう発足しているか（敗退直後の発足・第18週の安全網のどちらでも立つ）。</summary>
        public static bool IsStartedThisYear => StartedYear != 0 && StartedYear == GameClock.YearIndex;

        /// <summary>
        /// 新チーム発足（夏の敗退直後 or 第18週の安全網）から呼ぶ。引退者を控え、指名待ちにし、
        /// 今年度は発足済みと印を付ける（同じ年度に二度開かない）。
        /// </summary>
        internal static void Open(NewTeamTransition transition)
        {
            RetiredNames.Clear();
            foreach (var p in transition.Retired) RetiredNames.Add(p.Name);
            StartedYear = GameClock.YearIndex;
            // 誰も引退しない夏（全員1年生の1年目）は顔ぶれが変わらない＝主将は新規開始で指名済み
            // （CaptainSelector.BeginNewTeam がそのまま続ける）。指名を出さずに発足の流れを閉じる。
            if (transition.Retired.Count == 0)
            {
                Skip();
                return;
            }
            Pending = true;
        }

        /// <summary>
        /// 新規開始の主将指名（1年目）。ゲームを始めたときに1回だけ出す。夏に誰も引退しない1年目は
        /// 新チーム発足の指名を出さないので、ここで選んだ主将が1年目の夏の後も続く。
        /// </summary>
        public static void OpenAtGameStart()
        {
            RetiredNames.Clear();
            Pending = true;
        }

        /// <summary>新規開始の主将指名を待っているか（1年目で、今年度の新チームはまだ発足していない）。</summary>
        public static bool AtGameStart => Pending && GameClock.YearIndex == 1 && !IsStartedThisYear;

        /// <summary>
        /// 指名できる候補が居ない（新チームが空）ときに導線を閉じる。発足済みの印は残す
        /// （<see cref="Clear"/> と違い、第18週の安全網に二度目の発足をさせない）。
        /// </summary>
        public static void Skip()
        {
            Pending = false;
            ScoutService.RaiseAfterNewTeamStarted();   // 新チーム発足の流れはここで終わり（issue #1035）
        }

        /// <summary>テスト・リセット用（週を巻き戻したときなど）。新規ゲームの完全リセットも兼ねる。</summary>
        public static void Clear()
        {
            RetiredNames.Clear();
            Pending = false;
            StartedYear = 0;
        }

        /// <summary>セーブ保存: 主将指名待ちの有無と引退者名・発足年度を DTO へ写す（issue #507 / #954）。</summary>
        public static void Capture(KokoSim.Engine.Saves.SaveState s)
        {
            s.NewTeamPending = Pending;
            s.NewTeamRetiredNames = new List<string>(RetiredNames);
            s.NewTeamStartedYear = StartedYear;
        }

        /// <summary>セーブ復元: 主将指名待ちと引退者名・発足年度を据える。</summary>
        public static void Restore(KokoSim.Engine.Saves.SaveState s)
        {
            RetiredNames.Clear();
            RetiredNames.AddRange(s.NewTeamRetiredNames);
            Pending = s.NewTeamPending;
            StartedYear = s.NewTeamStartedYear;
        }

        public static NewTeamView BuildView()
        {
            var roster = RosterService.Roster;
            var active = RosterService.Active;
            var atStart = AtGameStart;
            var view = new NewTeamView
            {
                Lead = atStart ? "チームの主将を指名してください。" : "3年生が引退しました。新チームの主将を指名してください。",
                RetiredLabel = atStart ? "" : RetiredNames.Count + " 名が引退",
                AtGameStart = atStart,
            };

            foreach (var p in CaptainSelector.DesignationCandidates(roster))
            {
                view.Candidates.Add(new CaptainCandidateRow
                {
                    ActiveIndex = IndexIn(active, p),
                    Number = p.UniformNumber >= 1 ? p.UniformNumber.ToString() : "–",
                    Name = p.Name,
                    GradeLabel = p.Grade + "年",
                    Mental = p.Mental,
                    StarterTier = CaptainEffectTiers.Starter(p).ToString(),
                    BenchTier = CaptainEffectTiers.Bench(p).ToString(),
                    CohesionTier = ChemistryDisplay.CohesionGrade(ChemistryDisplay.CohesionIfCaptain(p)),
                    IsInterim = p.IsCaptain,
                });
            }
            return view;
        }

        /// <summary>
        /// 指名を確定して導線を閉じる（設計書09 §8: 指名できるのは新チーム発足時の1回だけ）。
        /// 候補外・期間外なら何もせず false（暫定主将のまま）。
        /// </summary>
        public static bool Confirm(int activeIndex)
        {
            var active = RosterService.Active;
            if (activeIndex < 0 || activeIndex >= active.Count) return false;
            var roster = RosterService.Roster;
            var pick = active[activeIndex];
            var atStart = AtGameStart;
            // 県予選で早く負けた年は第18週より前に発足する＝発足した時点から指名を受け付ける（issue #954）。
            // 新規開始の指名は期間に関わらず受け付ける。
            if (!CaptainSelector.CanDesignate(roster, pick, GameClock.Week, Calendar, IsStartedThisYear || atStart)) return false;
            CaptainSelector.Designate(roster, pick);
            Pending = false;
            // 新規開始は背番号がもう決まっている＝背番号決定へは進まない。
            if (atStart) return true;
            ScoutPromiseTracker.AfterCaptainNamed();   // 「主将にしてほしい」の約束（本人が2年の秋）
            // 主将の次は新チームの背番号決定（issue #954・案C）。確定するまで週を進められない。
            UniformNumberService.Open();
            return true;
        }

        private static int IndexIn(IReadOnlyList<DevelopingPlayer> list, DevelopingPlayer p)
        {
            for (var i = 0; i < list.Count; i++) if (ReferenceEquals(list[i], p)) return i;
            return -1;
        }
    }

    /// <summary>
    /// 新チームの背番号（ベンチ入り）決定の導線（issue #954・案C）。主将指名の確定で開き、メンバー設定画面へ
    /// 直行させる。初期案は引退時に <see cref="UniformNumberAssigner.FillVacancies"/> が詰めた割当（
    /// <see cref="RosterLifecycle.BeginNewTeam"/>）そのもの＝手を入れずに確定してもよい。
    /// 確定するまで週を進められず、メンバー設定画面から他の画面へ出られない（<see cref="ScreenRouter"/>）。
    /// 状態はセッション静的・セーブ対象（v33）。UnityEngine 非依存。
    /// </summary>
    public static class UniformNumberService
    {
        /// <summary>メンバー設定画面の上部帯に出す文。</summary>
        public const string Lead = "新チームの背番号を決めてください";

        /// <summary>背番号決定の確定待ちか。</summary>
        public static bool Pending { get; private set; }

        /// <summary>主将指名の確定から呼ぶ。新チームに誰も居なければ決めようがないので開かない。</summary>
        internal static void Open()
        {
            Pending = CaptainSelector.NewTeamMembers(RosterService.Roster).Count > 0;
            if (!Pending) ScoutService.RaiseAfterNewTeamStarted();   // 決める背番号が無い＝流れの終わり（issue #1035）
        }

        /// <summary>プレイヤーが「背番号を確定」を押した。割当はメンバー設定画面で編集済み＝ここでは待ちを下ろすだけ。</summary>
        public static void Confirm()
        {
            Pending = false;
            ScoutPromiseTracker.AfterNumbersDecided();   // 「一桁の背番号」の約束（1年の秋）
            ScoutService.RaiseAfterNewTeamStarted();   // 引退→主将指名→背番号決定の終わり＝スカウトが分野を聞きに来る（issue #1035）
        }

        /// <summary>テスト・新規ゲーム用。</summary>
        public static void Clear() => Pending = false;

        /// <summary>セーブ保存（v33）。</summary>
        public static void Capture(KokoSim.Engine.Saves.SaveState s) => s.UniformNumberPending = Pending;

        /// <summary>セーブ復元（v33）。</summary>
        public static void Restore(KokoSim.Engine.Saves.SaveState s) => Pending = s.UniformNumberPending;
    }

    /// <summary>
    /// 夏の敗退直後の3年引退（issue #954）。大会の後始末（<c>HomeState.DismissResult</c>）から呼ぶ。
    /// 自校の夏がここで終わった（県予選で敗退／甲子園で敗退／甲子園で優勝）なら、その場で3年を引退させて
    /// 新チームを発足し、引退の会話を積んでから主将指名を開く。順序は「引退の会話 → 主将指名 → 背番号決定」
    /// （ホームが会話を先に出す）。第18週の固定フックは安全網として残り、発足済みの年は素通りする。
    /// AI校の代替わりは第18週のまま（全国裏試合の完了待ちと結びついているため・表に見えない）。
    /// </summary>
    public static class SummerRetirementService
    {
        /// <summary>
        /// 終わった大会と結果を渡す。自校の夏がここで終わったなら引退＋新チーム発足を行い true。
        /// 夏が続く（県大会で優勝して甲子園へ進む）・夏以外の大会・今年度は発足済み、なら何もせず false。
        /// </summary>
        public static bool OnTournamentEnded(TournamentKind ended, bool isChampion)
        {
            if (!(SummerRetirement.Classify(ended, isChampion) is SummerExit exit)) return false;
            if (NewTeamService.IsStartedThisYear) return false;

            var transition = RosterLifecycle.BeginNewTeam(RosterService.Roster);
            // 会話は主将指名より先に読ませる（ホームが引退の会話のキューを見て先に開く）。
            // 引退した3年生が0人（全員1年生の1年目など）の夏は引退の会話を出さない（issue #1531）。
            var retirementEventId = SummerRetirement.EventIdFor(exit, transition);
            if (retirementEventId != null) EventService.PushMilestone(retirementEventId);
            // マネージャー（NPC）も3年の夏で入れ替わる（issue #1430）。引退の会話の直後に引き継ぎの会話を
            // 続けて積む＝EventNovelView は同じ週に積まれた会話を続けて見せるので、主将指名より先に読める。
            if (TeamManagerLifecycle.IsRetiring(ManagerService.Manager))
            {
                TeamManagerLifecycle.Succeed(
                    ManagerService.Manager, new Xoshiro256Random(GameSeed.Master ^ (0x1430_0003UL ^ (ulong)GameClock.YearIndex)));
                EventService.PushMilestone(TeamManagerLifecycle.EventId);
            }
            // 誰も引退しない夏（全員1年生の1年目）は顔ぶれが変わらない＝説明・主将指名・スタメンの破棄は要らない
            // （主将は新規開始で指名済み。Open が指名を出さずに流れを閉じる）。
            var anyRetired = transition.Retired.Count > 0;
            // 初めての夏の引退: 新チームと主将の説明（issue #1496）。引退の会話の後・主将指名の前に読む。
            if (anyRetired) TutorialService.Raise(KokoSim.Engine.Career.TutorialScene.SummerRetirement);
            NewTeamService.Open(transition);
            // 引退で枠の大半が穴になった前チームのスタメンは捨てる（issue #955・第18週の発足と同じ）。
            if (anyRetired) GameSession.Current.LastLineup = null;
            return true;
        }
    }
}
