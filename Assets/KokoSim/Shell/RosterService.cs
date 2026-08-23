using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Season;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 自校部員（DevelopingPlayer 群）の単一ソース。従来は各画面が seed=42 で別インスタンスの
    /// ロスターを毎回再生成していたため、背番号など可変状態が画面間で共有できなかった。ここで
    /// 1回だけ生成した同一リストを全画面へ供給し、メンバー設定で割り当てた背番号が練習など他画面へ
    /// 一貫して反映されるようにする（設計書06 §3.3b）。
    ///
    /// 生成レシピは従来の PlayerListState/TrainingPlanState と同一（seed=42・grade 1〜3・
    /// ProspectGenerator.Intake）。生成直後に UniformNumberAssigner.AutoAssign で初期背番号（能力順）を振る。
    /// エンジン非依存の純データを保持するだけで、UnityEngine には依存しない。
    ///
    /// <para>年度替わり（4月）の進級・卒業・新入生は <see cref="AdvanceYear"/> で適用する（issue #535）。
    /// ロジックの実体は engine の <see cref="RosterLifecycle.AdvanceYear"/> に集約し、SeasonEngine と1ビット一致で
    /// 回す。卒業生は <see cref="Roster"/> からは外れるが、通算/大会別成績（#77）の名前解決のため
    /// <see cref="Graduated"/> へ退避して残す。</para>
    /// </summary>
    public static class RosterService
    {
        public const ulong DefaultSeed = 42;

        /// <summary>
        /// 新設校の初年度の部員数（issue #585・確定事項1）。1年生ちょうどこの人数だけで始める
        /// （<c>IntakeMean/Min/Max</c> の抽選ではない）。9人を割らない最小限＋控え1人。
        /// </summary>
        public const int NewSchoolIntake = 10;

        /// <summary>年度替わりの新入生生成に使う決定論ストリームの識別子（<see cref="GameSeed.Master"/> と混合, issue #535）。</summary>
        private const ulong YearRolloverStream = 0x5535_0000UL;

        /// <summary>
        /// その年度の新入生生成に使う乱数源（issue #535 / #587）。3月の確定（<see cref="ProspectService"/>）と
        /// 4月のその場生成が<b>同じ母種・同じストリーム</b>を使うための単一ソース。ここを分けると
        /// 「確定を前に倒しただけ」のはずが別の新入生になる。
        /// </summary>
        public static IRandomSource YearRng(int newYearIndex)
            => new Xoshiro256Random(GameSeed.Master ^ (YearRolloverStream ^ (ulong)newYearIndex));

        /// <summary>
        /// 新入生の才能中心値（design-04 §1「名声 → 新入生の質」＋年契約スカウト網の上乗せ）。名声は
        /// 飽和カーブで頭打ちなのに対し、スカウト網は毎年払い続ける固定費＝「金で買う上振れ」として別軸で足す。
        /// 3月の確定と4月のその場生成が同じ値を使うためにここへ集約する（issue #587）。
        /// </summary>
        public static double TalentCenter()
            => new RosterCoefficients().TalentCenterFromFame(ManagerService.Manager.Fame)
               + ManagerService.ContractEffects.ScoutTalentAdd;

        /// <summary>
        /// 新設校モード（issue #585）。true なら初回生成が「1年生 <see cref="NewSchoolIntake"/> 人だけ」になる。
        /// 新規ゲーム設定画面の確定で立ち、初年度の生成に一度だけ効く（翌年度からは通常の
        /// <see cref="AdvanceYear"/> ＝新入生 Intake に合流する）。
        /// </summary>
        private static bool _newSchool;

        private static List<DevelopingPlayer> _roster;
        private static List<DevelopingPlayer> _graduates;
        // 新入生へ振る安定ID（成績集計の帰属キー）の次の値。単調増加＝既存・卒業生の Id と衝突しない（issue #535）。
        private static int _nextId;

        /// <summary>共有ロスター（セッション内で同一インスタンス・順序安定）。初回アクセスで生成。夏引退者は含むが卒業生は含まない。</summary>
        public static IReadOnlyList<DevelopingPlayer> Roster => _roster ??= Build(DefaultSeed);

        /// <summary>
        /// 卒業してロスターを外れた選手（年度替わりで Grade&gt;3 になり除去された者）。成績の名前解決
        /// （<see cref="TeamStatsService"/> の NameIndex）で当時の氏名を引くために退避しておく単一ソース（issue #535）。
        /// </summary>
        public static IReadOnlyList<DevelopingPlayer> Graduated
        {
            get { _ = Roster; return _graduates; }
        }

        /// <summary>
        /// 在籍中の部員だけを抜き出したロスター（夏で引退した3年を除く, RosterLifecycle）。
        /// 選手一覧・メンバー設定・スタメン・練習・試合編成はこちらを単一ソースにする
        /// （引退者は「フラグを立てて残す」方式なので <see cref="Roster"/> には残り続ける）。
        /// 呼ぶたびに元ロスター順のスナップショットを作る＝画面をまたいでも index が一致する。
        /// </summary>
        public static IReadOnlyList<DevelopingPlayer> Active => RosterLifecycle.Active(Roster);

        /// <summary>
        /// 年度替わり（4月・設計書03 §2）: 自校ロスターへ昇級・卒業・新入生を適用する（issue #535）。
        /// ロジックの実体は engine の <see cref="RosterLifecycle.AdvanceYear"/>（SeasonEngine と共有＝二重実装なし）。
        /// 卒業生は <see cref="Graduated"/> へ退避し、新入生には単調増加の安定 Id を振る（既存・卒業生と非衝突）。
        /// 名声（<see cref="ManagerService"/> の Fame）で新入生の質（talentCenter）が変わる（design-04 §1）。
        /// 乱数は年度ごとの決定論ストリーム（<see cref="GameSeed.Master"/> ^ 年度）＝Build の seed=42 を使い回さない（不変条件#2）。
        /// </summary>
        public static void AdvanceYear(int newYearIndex)
        {
            _ = Roster;   // 初回アクセスなら生成（_roster / _graduates / _nextId を確定させる）

            var coefficients = new RosterCoefficients();

            // 3月に確定済みの入部予定者（issue #587）を合流させる。null＝3月を跨いでいない進行・旧セーブで、
            // その場合だけ従来どおり年度替わりでその場生成する（乱数源は同じ YearRng）。
            var incoming = ProspectService.TakeFor(newYearIndex);

            var rollover = RosterLifecycle.AdvanceYear(
                _roster, newYearIndex, coefficients, YearRng(newYearIndex),
                talentCenter: TalentCenter(), nextId: _nextId, incoming: incoming);
            _nextId = rollover.NextId;
            if (incoming != null)
            {
                ProspectService.MarkJoined(rollover.Newcomers);
                GameClock.ProspectJoined = true;   // ホームが通知フィードへ流す（遷移とUI提示の分離）
            }
            _graduates.AddRange(rollover.Graduated);   // 卒業生を退避（名前解決に使う）。

            // 卒業・引退で空いた背番号を、在籍部員（新入生含むベンチ外）へ小さい番号から詰める
            // （#531 と同型・既存割当は壊さない非破壊。監督はメンバー設定画面で後から自由に振り直せる）。
            UniformNumberAssigner.FillVacancies(RosterLifecycle.Active(_roster));
        }

        /// <summary>セーブ保存: 現在の共有ロスター＋退避済み卒業生を DTO へ写す（issue #507 / #535）。
        /// 卒業生（Grade&gt;3）も同じリストに載せ、復元時に学年で振り分ける（名前解決を保つため）。</summary>
        public static void Capture(KokoSim.Engine.Saves.SaveState s)
        {
            _ = Roster;
            var all = new List<DevelopingPlayer>(_roster);
            all.AddRange(_graduates);
            s.Roster = all;
        }

        /// <summary>セーブ復元: 保存したロスターを在籍（Grade≤3）と卒業生（Grade&gt;3）へ振り分けて据える。
        /// 背番号・成長・怪我・引退フラグを丸ごと持ち越し、安定 Id カウンタも最大値の次から再開する。</summary>
        public static void Restore(KokoSim.Engine.Saves.SaveState s)
        {
            _roster = s.Roster.Where(p => p.Grade <= 3).ToList();
            _graduates = s.Roster.Where(p => p.Grade > 3).ToList();
            _nextId = s.Roster.Count == 0 ? 1 : s.Roster.Max(p => p.Id) + 1;
        }

        /// <summary>新規ゲームの完全リセット: 次アクセスで既定シード（42）から再生成させる。</summary>
        public static void Reset()
        {
            _roster = null; _graduates = null; _nextId = 0; _newSchool = false;
            ProspectService.Reset();   // 予定者もこの進行のものなので一緒に捨てる（issue #587）
            RecruitService.Reset();    // 勧誘の中学生リストも同じ（issue #588）
            DraftService.Reset();      // 教え子名鑑（過年度の指名確定）も同じ（issue #586）
        }

        /// <summary>
        /// 新設校として作り直す（issue #585）。次アクセスの生成が「1年生 <see cref="NewSchoolIntake"/> 人だけ」になる。
        /// 新規ゲーム設定の確定から呼ぶ。<see cref="Reset"/> と違いモードを立てる点だけが違う。
        /// </summary>
        public static void ResetAsNewSchool()
        {
            Reset();
            _newSchool = true;
        }

        /// <summary>
        /// 転任先の新校として作り直す（設計書04 §1.3）。部員は旧校に残る想定（issue「監督の転任システムが
        /// 実ゲームに完全未配線」の決定）なので、新設校と同じ「1年生 <see cref="NewSchoolIntake"/> 人だけ」で
        /// 作り直す。<see cref="Reset"/> と違い <see cref="DraftService.Reset"/> は呼ばない＝教え子名鑑
        /// （プロ入りOB台帳）はキャリア横断の記録なので転任では捨てない（design-04 §1.3「監督能力と名声は持ち越し」）。
        /// </summary>
        public static void ResetForTransfer()
        {
            _roster = null; _graduates = null; _nextId = 0; _newSchool = true;
            ProspectService.Reset();   // 旧校の入部予定者は同行しない
            RecruitService.Reset();    // 旧校で勧誘中だった中学生リストも同様
        }

        private static List<DevelopingPlayer> Build(ulong seed)
        {
            var rng = new Xoshiro256Random(seed);
            var coefficients = new RosterCoefficients();
            var list = new List<DevelopingPlayer>();
            // 新設校の初年度は1年生だけ（2・3年は作らない）。人数も抽選せず固定する（issue #585 確定事項1）。
            // 投手・捕手の供給下限（#377 / #589）は Intake の中で効くので、10人でも役が必ず立つ。
            var lastGrade = _newSchool ? 1 : 3;
            for (var grade = 1; grade <= lastGrade; grade++)
            {
                var intake = ProspectGenerator.Intake(
                    grade, coefficients, rng, fixedCount: _newSchool ? NewSchoolIntake : (int?)null);
                foreach (var p in intake)
                {
                    p.Grade = grade;
                    list.Add(p);
                }
            }

            // 成績集計の帰属キー（安定ID）を連番付与。生成順＝安定順なので決定論（乱数不使用）。
            // 1始まり（0は「未割当＝集計対象外」の番兵と衝突させない）。
            for (var i = 0; i < list.Count; i++) list[i].Id = i + 1;
            _nextId = list.Count + 1;                  // 次の新入生から振る単調増加カウンタの初期値（issue #535）。
            _graduates = new List<DevelopingPlayer>();  // 卒業生の退避先（初期は空）。

            UniformNumberAssigner.AutoAssign(list);   // 初期背番号を能力順で仮割当（プレイヤーが後で編集）

            // 主将（設計書09 §8）: ゲーム開始時点で必ず1名決まっている（最上級生の統率力最大）。
            // プレイヤーが選び直せるのは夏の3年引退後＝新チーム発足時だけ（CaptainSelector.IsDesignationWindow）。
            CaptainSelector.EnsureCaptain(list);
            return list;
        }
    }
}
