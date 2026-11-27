// ViewModel層（設計書06 §3.3 練習計画）。UnityEngine 非依存に保つ。
// 部員一人ひとりに練習計画(TrainingPlan: 複数メニュー×練習時間[分])を割当する名簿型。
// 練習時間の総量は budget（施設で増える, 現状は TrainingCoefficients.DefaultBudgetMinutes）。
// プリセット（お任せ6種）・他選手からのコピー・Custom分編集・守備ポジション別割当に対応。
// 効果プレビューは実選手のクローン（DevelopingPlayer.Clone, Issue #223）で1週ドライラン（非破壊・決定論）。
//
// 【エンジン実データ駆動】背番号ランク・総合力グレード(Tiers.FromStrength)・使用率・伸び見込み・
//   守備適性(Aptitude/DefenseXメニュー)・合宿倍率＆週送り(SeasonCalendar)・
//   個別指導3枠（DevelopingPlayer.IndividualCoaching, Issue #126）: 指名は共有ロスター本体へ書き込み、
//   分野別指導力（監督メタ, #115）由来の追加倍率が主効果expへ実際に乗る（DevelopmentModel）。
// 【UIセッション状態（エンジン未実装機能の視覚シェル）】委任トグル・週テンプレ保存/呼出・
//   学年フィルタ/ソート。これらは成長ロジックに未接続（design-03/04で今後実装予定の枠）。
using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using KokoSim.Unity.Shell;

namespace KokoSim.Unity.Training
{
    public sealed class MenuSlot
    {
        public TrainingMenu Menu;
        public string MenuJp = "";
        public string ShortJp = "";      // 一覧の横棒に書く略称（打撃・フライ・左守 など）
        public string Icon = "";         // ポップアップの記号（漢字1文字）
        public string MainEffectJp = "";
        public int Minutes;
        public bool Pitcher;             // 投手練習のまとまり（false＝野手練習）
        public bool AltShade;            // 守備位置の区切りを1つおきに暗くする（隣り合う区切りの区別）
    }

    /// <summary>
    /// 「この配分で伸びる能力」の1行。今の能力値とランク＋この配分で1週に伸びる量（レベル単位の実数）。
    /// 伸びの棒の長さ＝<see cref="Fraction"/>（その選手の取りうる最大の伸びを1とした割合）。
    /// </summary>
    public sealed class GainBar
    {
        public AbilityKind Kind;
        public string AbilityJp = "";
        public bool Pitcher;       // 投手能力のまとまり（false＝野手能力）
        public int Value;          // 今の能力値
        public string Grade = "";  // 今の能力値のランク S〜G（文字の行は空）
        // 数値を出さない能力（弾道＝タイプ、球速＝km/h）は文字で出す。非空ならランク〜棒の列をこの文字にまとめる。
        public string Text = "";
        public double Amount;      // この配分での1週の伸び（レベル単位・0＝伸びない。弾道は向きを問わず絶対値）
        public double Fraction;    // 0..1（取りうる最大の伸びで満タン）
        public bool Grows;         // 伸びる（false の行は淡色・棒なし）
    }

    /// <summary>プリセット選択肢（メニューの型のボタン1つ。説明はツールチップで持つ）。</summary>
    public sealed class PresetOption
    {
        public TrainingPreset Preset;
        public string Jp = "";
        public string Desc = "";
        public bool Selected;
    }

    /// <summary>守備ポジション別の適性と割当（守備位置の練習の行）。</summary>
    public sealed class DefenseSlot
    {
        public FieldPosition Pos;
        public TrainingMenu Menu;
        public string Label = "";      // 投/捕/一…（記号）
        public string FullJp = "";     // 投手/捕手/一塁…（項目名）
        public int Aptitude;           // 現在の適性値
        public string Grade = "";      // 適性のランク
        public int Minutes;            // 割当分
    }

    /// <summary>個別指導の枠1つ（issue #707）。空き枠は Index=-1。</summary>
    public sealed class NominationSlot
    {
        public int Index = -1;
        public string NumText = "";
        public string Name = "";
        public string GradeLabel = "";
        public string HandLabel = "";
        public bool Empty => Index < 0;
    }

    public sealed class PlayerTrainRow
    {
        public int Index;
        public string Name = "";
        public string NumText = "—";       // 背番号（ベンチ外は「—」）
        public bool BenchOut;
        public string GradeLabel = "1年";
        public string HandLabel = "右投右打";
        public PlayerStrength Strength;    // カテゴリ別ランク4種（打撃/走力/守備/投手, Issue #133。総合ランクは廃止）
        public string PresetJp = "お任せ";
        public List<MenuSlot> Chips = new List<MenuSlot>();  // 横棒の元データ（メニュー別 分・固定順, Issue #133-③）
        public bool Selected;
        public bool Nominated;   // 個別指導に指名中（部員表の★列, issue #707）
    }

    public sealed class TrainingPlanView
    {
        public int Budget;
        public int RosterCount;      // 在籍の全員
        public int ShownCount;       // 絞り込み後に一覧へ出ている人数
        public string TeamRankGrade = "F";   // 共通トップバーのチーム総合力ランク

        // 個別指導3枠（Index＝ロスター添字・空き枠は -1。枠の×で外すのに使う, issue #707）
        public List<NominationSlot> Nominations = new List<NominationSlot>();
        public int NominatedCount;

        // 選択選手（ポップアップ）
        public int SelectedIndex;
        public string SelectedName = "";
        public string SelNumText = "—";
        public string SelYear = "1年";
        public string SelHand = "右投右打";
        public PlayerStrength SelStrength;   // カテゴリ別ランク（総合ランクは廃止, Issue #133-①）
        public bool SelNominated;
        public int SelectedTotal;
        public int SelectedRemaining;
        public int SelFielderMinutes;   // 野手練習の合計
        public int SelPitcherMinutes;   // 投手練習の合計
        public int SelDefenseMinutes;   // 守備位置の合計
        public bool DelegateOn;
        public string DelegateStateLabel = "手動";
        public bool SelHasPrev;
        public bool SelHasNext;
        public bool AtBudgetLimit;   // 残り0分（＋を押せない）

        public List<PlayerTrainRow> Rows = new List<PlayerTrainRow>();
        public List<MenuSlot> SelectedSlots = new List<MenuSlot>();   // 選択選手のメニュー別 分（野手→投手の順）
        public List<DefenseSlot> DefenseSlots = new List<DefenseSlot>(); // 守備位置の練習（9ポジション）
        public List<PresetOption> PresetOptions = new List<PresetOption>(); // メニューの型
        public List<MenuSlot> SelectedChips = new List<MenuSlot>();   // 1日の配分の横棒（一覧と同じ順）
        public List<GainBar> SelectedGrowth = new List<GainBar>();    // この配分で伸びる能力（野手11→投手5）
        public List<string> Templates = new List<string>();          // 保存済み週テンプレ名
        public string ActiveTemplate = "";   // 今の配分と同じ中身の週テンプレ（無ければ空＝「保存なし」/未選択）
        public List<PlayerTrainRow> CopyRows = new List<PlayerTrainRow>(); // 複製ピッカー用の全部員（フィルタ非適用・学年順, Issue #133-⑤）

        // フィルタの選択状態
        public int YearFilter;   // 0=全, 1..3=学年
        public int BenchFilter;  // 0=全員, 1=ベンチ入り, 2=ベンチ外（Issue #133-④）
    }

    /// <summary>
    /// 練習計画の状態。部員ごとの TrainingPlan（複数メニュー×分）と練習時間 budget を保持し、
    /// 純エンジンで選手ごとに1週ドライランして伸び見込みを予測する（非破壊・決定論）。
    /// </summary>
    public sealed class TrainingPlanState
    {
        private const int MinuteStep = TrainingPresets.StepMinutes;   // Custom編集の増減単位（10分・engineと共有）

        // カテゴリ別ランクの重み（単一静的ソースを参照, Issue #30/#140/#133。PlayerListState と同型）。
        private static readonly TeamStrengthCoefficients Coeff = TeamStrengthCoeff.Default;

        // Custom編集で提示する能力系メニュー（守備適性は別枠 DefenseSlots）。休養は列挙しない
        // （配分しなかった分＝残りが自動的に休養に相当する）。
        private static readonly TrainingMenu[] EditableMenus =
        {
            TrainingMenu.Batting, TrainingMenu.PowerHitting, TrainingMenu.FlyBallHitting, TrainingMenu.GroundBallHitting,
            TrainingMenu.PlateDiscipline, TrainingMenu.Strength,
            TrainingMenu.Defense, TrainingMenu.Throwing, TrainingMenu.BaseRunning, TrainingMenu.Bunt,
            TrainingMenu.Pitching, TrainingMenu.BreakingBall, TrainingMenu.VelocityTraining, TrainingMenu.Running,
        };

        // 積み上げ配分バーの表示順（Issue #133-③）: 野手系→投手系（EditableMenus と同順）→ポジション別守備。
        // 行ごとに同じ順で並べることで、縦スキャンで選手間の配分差を比較できる（UI原則①⑥）。休養は除外＝余白。
        private static readonly TrainingMenu[] BarMenuOrder =
        {
            TrainingMenu.Batting, TrainingMenu.PowerHitting, TrainingMenu.FlyBallHitting, TrainingMenu.GroundBallHitting,
            TrainingMenu.PlateDiscipline, TrainingMenu.Strength,
            TrainingMenu.Defense, TrainingMenu.Throwing, TrainingMenu.BaseRunning, TrainingMenu.Bunt,
            TrainingMenu.Pitching, TrainingMenu.BreakingBall, TrainingMenu.VelocityTraining, TrainingMenu.Running,
            TrainingMenu.DefenseP, TrainingMenu.DefenseC, TrainingMenu.Defense1B, TrainingMenu.Defense2B,
            TrainingMenu.Defense3B, TrainingMenu.DefenseSS, TrainingMenu.DefenseLF, TrainingMenu.DefenseCF,
            TrainingMenu.DefenseRF, TrainingMenu.DefenseInfield, TrainingMenu.DefenseOutfield,
        };

        // 守備適性割当口（9ポジション）: ポジション → 専用メニュー。
        private static readonly (FieldPosition Pos, TrainingMenu Menu)[] DefenseMenus =
        {
            (FieldPosition.Pitcher, TrainingMenu.DefenseP),
            (FieldPosition.Catcher, TrainingMenu.DefenseC),
            (FieldPosition.FirstBase, TrainingMenu.Defense1B),
            (FieldPosition.SecondBase, TrainingMenu.Defense2B),
            (FieldPosition.ThirdBase, TrainingMenu.Defense3B),
            (FieldPosition.Shortstop, TrainingMenu.DefenseSS),
            (FieldPosition.LeftField, TrainingMenu.DefenseLF),
            (FieldPosition.CenterField, TrainingMenu.DefenseCF),
            (FieldPosition.RightField, TrainingMenu.DefenseRF),
        };

        // 統合モーダルに並べるプリセット選択肢（最後に手動＝Custom）。
        private static readonly TrainingPreset[] SelectablePresets =
        {
            TrainingPreset.PitcherAuto, TrainingPreset.BatterAuto, TrainingPreset.Balanced,
            TrainingPreset.DefenseFocus, TrainingPreset.AceDevelopment, TrainingPreset.SluggerDevelopment,
            TrainingPreset.Custom,
        };

        private readonly TrainingCoefficients _training = new TrainingCoefficients();

        /// <summary>週テンプレ1件（名前＋配分のスナップショット, issue #222⑤）。呼出で他選手へ適用する。</summary>
        private sealed class SavedTemplate
        {
            public string Name = "";
            public TrainingPlan Plan;
        }

        // 練習計画の単一ソースは engine の DevelopingPlayer.Plan（共有ロスター本体）へ移した（issue #484）。
        // 以前は画面ローカルの _plans[] を並行して持っており、①週送りの実適用が固定7周期メニューだけを見て
        // この配分を無視していた ②画面を離れて戻ると _plans が既定に戻る、の2重の乖離があった。
        // Plan は共有ロスターに載るので画面往復で保持され、週送り（TrainingWeek.Apply）と同じソースを読む。
        private IReadOnlyList<DevelopingPlayer> _roster; // 表示用＋計画の単一ソース（RosterService.Active）
        private bool[] _delegated;              // 選手索引→委任フラグ（UI状態・issue #484 要判断: 恒久化は保留）
        private readonly List<SavedTemplate> _templates = new List<SavedTemplate>(); // 週テンプレ（UI状態）
        private int _selected;
        // モーダルを開いた（＝選手を選択した）時点の配分スナップショット。「元に戻す」の復元先（issue #222⑥）。
        // null＝お任せ（DevelopingPlayer.Plan の未設定）も復元できるよう nullable で持つ。
        private TrainingPlan _revertPlan;
        private int _revertIndex = -1;
        // 現在週は全画面共有の GameClock を単一ソースとする（週送りで変化。合宿バナー・成長段階に反映）。
        private static int _week => KokoSim.Unity.Shell.GameClock.Week;
        private int _yearFilter;
        private int _benchFilter;   // 0=全員, 1=ベンチ入り, 2=ベンチ外（Issue #133-④）

        // 練習時間 budget は施設連動（Issue #128）。週送りの実適用と同じ単一ソースを読む（issue #484）。
        public int Budget => KokoSim.Unity.Shell.TrainingWeek.Budget;
        public int SelectedIndex => _selected;

        public TrainingPlanState()
        {
            // 全画面で共有する単一ソースのロスター（背番号はメンバー設定画面と一致, RosterService）。
            // 練習計画は DevelopingPlayer.Plan（null=お任せ）が単一ソース＝ここで別配列に初期化しない。
            _roster = RosterService.Active;
            _delegated = new bool[_roster.Count];
            // 背番号は DevelopingPlayer.UniformNumber（メンバー設定画面で編集）を単一ソースとして読む。
            // 現在週は GameClock（全画面共有）。既定はシーズン頭（週0＝4月）。冬合宿を見るには週送りする。
        }

        /// <summary>
        /// 在籍一覧が変わっていたら引き直す（週送りの年度替わり・issue #1250）。委任フラグ・選択は同じ選手を
        /// 引き継ぎ、いなくなっていれば選択は先頭へ戻す。変わったら true。
        /// </summary>
        public bool SyncRoster()
        {
            var old = _roster;
            var oldDelegated = _delegated;
            var selected = _selected < old.Count ? old[_selected] : null;
            if (!RosterService.RefreshActive(ref _roster)) return false;

            var flags = new Dictionary<DevelopingPlayer, bool>();
            for (var i = 0; i < old.Count; i++) flags[old[i]] = oldDelegated[i];
            _delegated = new bool[_roster.Count];
            for (var i = 0; i < _roster.Count; i++)
                _delegated[i] = flags.TryGetValue(_roster[i], out var on) && on;

            var at = selected == null ? -1 : IndexOfPlayer(selected);
            _selected = at >= 0 ? at : 0;
            _revertIndex = at >= 0 && _revertIndex >= 0 ? at : -1;
            _editSnapshot = null;   // 部員が入れ替わった＝控えは別人を指す（モーダルは呼び出し側が閉じる）
            return true;
        }

        // モーダルを開いた時点の控え（issue #1534）。モーダルの操作は共有ロスターへ即時に書くので、
        // 「決定」以外（×・暗幕・Esc）で閉じたらこの控えへ戻す。選手ごとの計画・委任・個別指導の指名を持つ。
        private sealed class EditSnapshot
        {
            public DevelopingPlayer[] Players;
            public TrainingPlan[] Plans;   // null＝お任せ（未設定）もそのまま戻す
            public bool[] Delegated;
            public bool[] Nominated;
        }

        private EditSnapshot _editSnapshot;

        /// <summary>練習設定のモーダルを開いた時点の状態を控える（issue #1534）。</summary>
        public void BeginEdit()
        {
            var n = _roster.Count;
            var snap = new EditSnapshot
            {
                Players = new DevelopingPlayer[n],
                Plans = new TrainingPlan[n],
                Delegated = new bool[n],
                Nominated = new bool[n],
            };
            for (var i = 0; i < n; i++)
            {
                snap.Players[i] = _roster[i];
                snap.Plans[i] = _roster[i].Plan;
                snap.Delegated[i] = _delegated[i];
                snap.Nominated[i] = _roster[i].IndividualCoaching;
            }
            _editSnapshot = snap;
        }

        /// <summary>「決定」: モーダルでの変更をそのまま残す（控えを捨てる）。</summary>
        public void CommitEdit() => _editSnapshot = null;

        /// <summary>「決定」以外で閉じた: モーダルを開いた時点の状態へ戻す（issue #1534）。</summary>
        public void CancelEdit()
        {
            var snap = _editSnapshot;
            _editSnapshot = null;
            if (snap == null) return;
            for (var i = 0; i < snap.Players.Length; i++)
            {
                var p = snap.Players[i];
                p.Plan = snap.Plans[i];
                p.IndividualCoaching = snap.Nominated[i];
                var at = IndexOfPlayer(p);
                if (at >= 0) _delegated[at] = snap.Delegated[i];
            }
        }

        private int IndexOfPlayer(DevelopingPlayer p)
        {
            for (var i = 0; i < _roster.Count; i++) if (ReferenceEquals(_roster[i], p)) return i;
            return -1;
        }

        /// <summary>選手の練習計画（単一ソースは <see cref="DevelopingPlayer.Plan"/>）。
        /// null=未設定は全員バランス型＝週送りの <see cref="KokoSim.Unity.Shell.TrainingWeek"/> と同じ既定（issue #680）。</summary>
        private TrainingPlan PlanOf(int index)
            => _roster[index].Plan ?? TrainingPlan.Default;

        /// <summary>選手の練習計画を書き込む（単一ソース <see cref="DevelopingPlayer.Plan"/>。共有ロスターなので画面往復で保持される）。</summary>
        private void SetPlan(int index, TrainingPlan plan) => _roster[index].Plan = plan;

        public void SelectPlayer(int index)
        {
            if (index < 0 || index >= _roster.Count) return;
            _selected = index;
            _revertPlan = _roster[index].Plan;   // 生の Plan（null=お任せ）を復元先に取る
            _revertIndex = index;
        }

        /// <summary>ヘッダーの◀前／次▶（issue #222⑥）。一覧の現在フィルタ/ソート順で前後の選手へ切替。</summary>
        public void StepSelected(int delta)
        {
            var idx = SortedFilteredIndices();
            var pos = idx.IndexOf(_selected);
            if (pos < 0) return;
            var next = pos + delta;
            if (next < 0 || next >= idx.Count) return;
            SelectPlayer(idx[next]);
        }

        private bool CanStepSelected(int delta)
        {
            var idx = SortedFilteredIndices();
            var pos = idx.IndexOf(_selected);
            if (pos < 0) return false;
            var next = pos + delta;
            return next >= 0 && next < idx.Count;
        }

        /// <summary>「元に戻す」：このモーダルを開いた（＝選手を選択した）時点の配分へ復元（issue #222⑥）。</summary>
        public void RevertSelected()
        {
            if (_revertIndex != _selected || _delegated[_selected]) return;
            _roster[_selected].Plan = _revertPlan;   // null=お任せへも戻せる
        }

        public void SetYearFilter(int grade) => _yearFilter = grade;
        public void SetBenchFilter(int mode) => _benchFilter = mode;

        /// <summary>選手のプリセットを設定（統合モーダルから選択）。委任中は無効。
        /// Custom は現在の解決済み配分を引き継いで手動編集の起点にする。</summary>
        public void SetPreset(int index, TrainingPreset preset)
        {
            if (index < 0 || index >= _roster.Count || _delegated[index]) return;
            SetPlan(index, preset == TrainingPreset.Custom
                ? new TrainingPlan { Preset = TrainingPreset.Custom, Allocations = CustomSnapshot(index) }
                : new TrainingPlan { Preset = preset });
        }

        /// <summary>現在の解決済み配分を Custom 編集の起点として写す。休養は budget を占有させず「残り」として扱う
        /// ため除外する（含めると休養分だけ編集可能上限が Budget 未満に張り付くバグになる）。</summary>
        private List<MenuAllocation> CustomSnapshot(int index)
        {
            var list = new List<MenuAllocation>();
            foreach (var a in ResolvedAllocations(index))
                if (a.Menu != TrainingMenu.Rest && a.Minutes > 0) list.Add(a);
            return list;
        }

        /// <summary>他選手から練習計画をコピー。immutable なのでコピー元は無傷。</summary>
        public void CopyPlanFrom(int target, int source)
        {
            if (target < 0 || target >= _roster.Count || source < 0 || source >= _roster.Count) return;
            if (_delegated[target]) return;
            SetPlan(target, PlanOf(source));   // お任せのコピーは既定プリセットを実体化して写す（従来と同挙動）
        }

        /// <summary>Custom編集: 指定メニューの分を増減（±ボタン・長押しリピート用）。プリセットは Custom へ切替。
        /// budget 超過は加算をクランプ（issue #222③・無音クランプ自体は据え置きだが、UI側で上限0を可視化する）。</summary>
        public void AdjustMinutes(int index, TrainingMenu menu, int deltaSteps)
        {
            if (index < 0 || index >= _roster.Count || _delegated[index]) return;
            var slots = new Dictionary<TrainingMenu, int>();
            foreach (var a in CustomSnapshot(index)) slots[a.Menu] = a.Minutes;
            slots.TryGetValue(menu, out var cur);
            ApplyMenuMinutes(index, slots, menu, cur + deltaSteps * MinuteStep);
        }

        /// <summary>Custom編集: 指定メニューの分を絶対値で設定（ドラッグバー用, issue #222②）。
        /// 10分単位へスナップし、他メニュー合計＋budget でクランプする。</summary>
        public void SetMinutes(int index, TrainingMenu menu, int minutesAbs)
        {
            if (index < 0 || index >= _roster.Count || _delegated[index]) return;
            var slots = new Dictionary<TrainingMenu, int>();
            foreach (var a in CustomSnapshot(index)) slots[a.Menu] = a.Minutes;
            var snapped = (System.Math.Max(0, minutesAbs) / MinuteStep) * MinuteStep;
            ApplyMenuMinutes(index, slots, menu, snapped);
        }

        private void ApplyMenuMinutes(int index, Dictionary<TrainingMenu, int> slots, TrainingMenu menu, int rawNext)
        {
            var next = System.Math.Max(0, rawNext);
            // budget 超過は加算分を抑える。
            var others = 0;
            foreach (var kv in slots) if (kv.Key != menu) others += kv.Value;
            next = System.Math.Min(next, System.Math.Max(0, Budget - others));
            slots[menu] = next;

            var alloc = new List<MenuAllocation>();
            foreach (var kv in slots) if (kv.Value > 0) alloc.Add(new MenuAllocation(kv.Key, kv.Value));
            SetPlan(index, PlanOf(index) with { Preset = TrainingPreset.Custom, Allocations = alloc });
        }

        /// <summary>委任トグル（UI状態）。委任中は編成ロック＋バランス型に委ねる。</summary>
        public void ToggleDelegate(int index)
        {
            if (index < 0 || index >= _roster.Count) return;
            _delegated[index] = !_delegated[index];
            if (_delegated[index])
                SetPlan(index, new TrainingPlan { Preset = TrainingPreset.Balanced });   // ゲームの判定で選ばない（issue #680）
        }

        public bool IsDelegated(int index) => index >= 0 && index < _delegated.Length && _delegated[index];

        /// <summary>個別指導3枠の指名トグル（設計書06 §3.3・Issue #126/#707）。上限は係数
        /// TrainingCoefficients.IndividualCoachingSlots（既定3）。共有ロスター本体（DevelopingPlayer.
        /// IndividualCoaching）へ直接書き込むため、指名は育成式（DevelopmentModel）へ実効果を持つ。
        /// 判定は engine の IndividualCoachingNomination に集約（部員表の★・枠の×・モーダルの3入口が共有）。</summary>
        public NominationResult ToggleNominate(int index)
            => IndividualCoachingNomination.Toggle(_roster, index, _training.IndividualCoachingSlots);

        public bool IsNominated(int index) => index >= 0 && index < _roster.Count && _roster[index].IndividualCoaching;

        /// <summary>選択選手の現在配分を週テンプレとして保存（UI状態）。</summary>
        public void SaveTemplate()
        {
            var plan = new TrainingPlan { Preset = TrainingPreset.Custom, Allocations = CustomSnapshot(_selected) };
            _templates.Add(new SavedTemplate { Name = "テンプレ" + (_templates.Count + 1), Plan = plan });
        }

        /// <summary>週テンプレの呼出（issue #222⑤）: 保存済み配分を選手へ適用する。委任中は無効。</summary>
        public void ApplyTemplate(int index, string name)
        {
            if (index < 0 || index >= _roster.Count || _delegated[index]) return;
            foreach (var t in _templates)
                if (t.Name == name) { SetPlan(index, t.Plan); return; }
        }

        public void RemoveTemplate(string name) => _templates.RemoveAll(t => t.Name == name);

        public TrainingPlanView BuildView()
        {
            var view = new TrainingPlanView
            {
                Budget = Budget,
                RosterCount = _roster.Count,
                SelectedIndex = _selected,
                SelectedName = _roster[_selected].Name,
                YearFilter = _yearFilter,
                BenchFilter = _benchFilter,
                Templates = TemplateNames(),
            };

            // チーム総合力（共通トップバー表示）＝6指標のリーグ標準化総合を Tier 変換（③, 全画面統一）。
            view.TeamRankGrade = KokoSim.Unity.Shell.TeamOverall.GradeOf(_roster);

            BuildNominations(view);

            // 行は「誰・カテゴリ別ランク・投打・現在のプリセット＋配分の横棒」を持つ（詳細編集はポップアップ）。
            foreach (var i in SortedFilteredIndices()) view.Rows.Add(MakeRow(i));
            view.ShownCount = view.Rows.Count;

            // 複製ピッカー（コピー元を選ぶ, Issue #133-⑤）は一覧のフィルタに関わらず全部員から選べる（学年順・安定）。
            var all = new List<int>();
            for (var i = 0; i < _roster.Count; i++) all.Add(i);
            all.Sort(GradeSort);
            foreach (var i in all) view.CopyRows.Add(MakeRow(i));

            BuildSelected(view);
            return view;
        }

        /// <summary>一覧・複製ピッカーで共用する行データ（Issue #133）。</summary>
        private PlayerTrainRow MakeRow(int i)
        {
            var src = _roster[i];
            return new PlayerTrainRow
            {
                Index = i,
                Name = src.Name,
                NumText = NumTextAt(i),
                BenchOut = src.UniformNumber == 0,
                GradeLabel = src.Grade + "年",
                HandLabel = HandednessLabels.Combined(src.Throws, src.Bats),
                Strength = PlayerStrengthProfile.Compute(src, Coeff),
                PresetJp = _delegated[i] ? "委任" : PresetJp(PlanOf(i).Preset),
                Chips = AllocationChips(i),
                Selected = i == _selected,
                Nominated = src.IndividualCoaching,
            };
        }

        /// <summary>配分の横棒の元データ（Issue #133-③）。解決済み配分を BarMenuOrder の固定順で列挙。
        /// 休養は除外＝棒の右側の余白として残り時間が読める。守備位置は1つおきに暗い色（AltShade）。</summary>
        private List<MenuSlot> AllocationChips(int index)
        {
            var alloc = new Dictionary<TrainingMenu, int>();
            foreach (var a in ResolvedAllocations(index))
                if (a.Menu != TrainingMenu.Rest && a.Minutes > 0)
                {
                    alloc.TryGetValue(a.Menu, out var cur);
                    alloc[a.Menu] = cur + a.Minutes;
                }

            var list = new List<MenuSlot>();
            var defPos = 0;
            foreach (var m in BarMenuOrder)
            {
                if (!alloc.TryGetValue(m, out var min)) continue;
                var isPos = UiColorIsDefensePosition(m);
                list.Add(new MenuSlot
                {
                    Menu = m, MenuJp = MenuJp(m), ShortJp = MenuShortJp(m), Icon = MenuIcon(m), Minutes = min,
                    Pitcher = IsPitcherMenu(m),
                    AltShade = isPos && (defPos++ % 2 == 1),
                });
            }
            return list;
        }

        private void BuildNominations(TrainingPlanView view)
        {
            var nominatedIdx = new List<int>();
            for (var i = 0; i < _roster.Count; i++) if (_roster[i].IndividualCoaching) nominatedIdx.Add(i);
            view.NominatedCount = nominatedIdx.Count;

            view.Nominations.Clear();
            for (var s = 0; s < _training.IndividualCoachingSlots; s++)
            {
                if (s < nominatedIdx.Count)
                {
                    var idx = nominatedIdx[s];
                    var p = _roster[idx];
                    view.Nominations.Add(new NominationSlot
                    {
                        Index = idx, NumText = NumTextAt(idx), Name = p.Name,
                        GradeLabel = p.Grade + "年", HandLabel = HandednessLabels.Combined(p.Throws, p.Bats),
                    });
                }
                else view.Nominations.Add(new NominationSlot());
            }
        }

        private void BuildSelected(TrainingPlanView view)
        {
            var sel = _roster[_selected];
            view.SelNumText = NumTextAt(_selected);
            view.SelYear = sel.Grade + "年";
            view.SelHand = HandednessLabels.Combined(sel.Throws, sel.Bats);
            view.SelStrength = PlayerStrengthProfile.Compute(sel, Coeff);
            view.SelNominated = sel.IndividualCoaching;
            view.DelegateOn = _delegated[_selected];
            view.DelegateStateLabel = _delegated[_selected] ? "委任中" : "手動";
            view.SelHasPrev = CanStepSelected(-1);
            view.SelHasNext = CanStepSelected(+1);

            // 野手練習・投手練習の編集スロット。
            var selAlloc = new Dictionary<TrainingMenu, int>();
            foreach (var a in ResolvedAllocations(_selected)) selAlloc[a.Menu] = a.Minutes;
            var total = 0;
            foreach (var m in EditableMenus)
            {
                selAlloc.TryGetValue(m, out var min);
                total += min;
                var pitcher = IsPitcherMenu(m);
                if (pitcher) view.SelPitcherMinutes += min; else view.SelFielderMinutes += min;
                view.SelectedSlots.Add(new MenuSlot
                {
                    Menu = m, MenuJp = MenuJp(m), ShortJp = MenuShortJp(m), Icon = MenuIcon(m),
                    MainEffectJp = MainEffectJp(m), Minutes = min, Pitcher = pitcher,
                });
            }

            // 守備位置の練習（9ポジション・守備適性つき）。
            foreach (var (pos, menu) in DefenseMenus)
            {
                selAlloc.TryGetValue(menu, out var dmin);
                total += dmin;
                view.SelDefenseMinutes += dmin;
                var apt = sel.Aptitude(pos);
                view.DefenseSlots.Add(new DefenseSlot
                {
                    Pos = pos, Menu = menu, Label = PositionJp(pos), FullJp = PositionFullJp(pos),
                    Aptitude = apt, Grade = Tiers.FromStrength(apt).ToString(), Minutes = dmin,
                });
            }

            // 汎用守備（内野/外野）は編集の行が無いが、プリセット等で入っていれば合計に数える（予算の残りと揃える）。
            foreach (var m in new[] { TrainingMenu.DefenseInfield, TrainingMenu.DefenseOutfield })
                if (selAlloc.TryGetValue(m, out var gmin)) { total += gmin; view.SelDefenseMinutes += gmin; }

            view.SelectedTotal = total;
            view.SelectedRemaining = Budget - total;
            view.AtBudgetLimit = view.SelectedRemaining <= 0;
            view.SelectedChips = AllocationChips(_selected);

            // プリセット選択肢（ボタン＋ツールチップの説明）。
            var cur = PlanOf(_selected).Preset;
            foreach (var p in SelectablePresets)
                view.PresetOptions.Add(new PresetOption
                {
                    Preset = p,
                    Jp = PresetJp(p),
                    Desc = PresetDesc(p),
                    Selected = !_delegated[_selected] && p == cur,
                });

            // この配分で伸びる能力（今の能力値＋1週の伸び）。
            view.SelectedGrowth.AddRange(BuildGrowthBars(_selected));
            view.ActiveTemplate = ActiveTemplateName(_selected);
        }

        // 「この配分で伸びる能力」に並べる能力。野手11→投手5。変化球（球種）は出さない（キレに含まれる）。
        private static readonly AbilityKind[] FielderGrowthOrder =
        {
            AbilityKind.LaunchTendency, AbilityKind.Contact, AbilityKind.Power, AbilityKind.Discipline, AbilityKind.Bunt,
            AbilityKind.Speed, AbilityKind.Steal, AbilityKind.ArmStrength, AbilityKind.ThrowAccuracy, AbilityKind.Fielding, AbilityKind.Catching,
        };
        private static readonly AbilityKind[] PitcherGrowthOrder =
        {
            AbilityKind.Velocity, AbilityKind.Control, AbilityKind.Stamina, AbilityKind.PitchRank, AbilityKind.Stuff,
        };

        // 伸びとみなす下限（浮動小数の誤差を伸びに数えない）。
        private const double GrowthEpsilon = 1e-6;

        /// <summary>
        /// 今の配分での各能力の1週の伸びを、非破壊ドライランで求める（設計書02 §5.1）。
        /// 棒の長さは「この選手が1つの練習に1日の全部を注いだときの、全能力の中で最大の伸び」を満タンにした割合。
        /// 伸びない能力も行は出す（淡色・棒なし）。
        /// </summary>
        private List<GainBar> BuildGrowthBars(int index)
        {
            var src = _roster[index];
            var gains = DryRunGains(src, null);
            var max = MaxPossibleGain(index);

            var bars = new List<GainBar>();
            foreach (var k in FielderGrowthOrder) bars.Add(MakeGainBar(src, k, false, gains[k], max));
            foreach (var k in PitcherGrowthOrder) bars.Add(MakeGainBar(src, k, true, gains[k], max));
            return bars;
        }

        private static GainBar MakeGainBar(DevelopingPlayer p, AbilityKind k, bool pitcher, double amount, double max)
        {
            var v = p.Level(k);
            var grows = amount > GrowthEpsilon;
            var bar = new GainBar
            {
                Kind = k, AbilityJp = AbilityJp(k), Pitcher = pitcher, Value = v,
                Amount = grows ? amount : 0.0,
                Grows = grows,
                Fraction = grows && max > GrowthEpsilon ? System.Math.Clamp(amount / max, 0.0, 1.0) : 0.0,
            };
            // 弾道は優劣のないタイプ軸（issue #219）、球速は km/h という実寸（issue #736）＝どちらも文字で出す。
            if (k == AbilityKind.LaunchTendency)
                bar.Text = KokoSim.Unity.Shell.LaunchTendencyLabels.Jp(v);
            else if (k == AbilityKind.Velocity)
                bar.Text = (int)System.Math.Round(PitcherAttributes.VelocityKmhFromLevel(v)) + " km/h";
            else
                bar.Grade = Tiers.FromStrength(v).ToString();
            return bar;
        }

        /// <summary>
        /// 選手の複製に1週の練習をかけ、能力ごとの伸び（レベル＋次までの割合の差分）を返す。
        /// <paramref name="planOverride"/> が null なら選手の今の計画。週送りの実適用（TrainingWeek.Apply）と
        /// 同じ入口を通す＝見込みと実際の伸びが一致する（issue #484）。弾道は下げる練習もあるので絶対値。
        /// </summary>
        private Dictionary<AbilityKind, double> DryRunGains(DevelopingPlayer src, TrainingPlan planOverride)
        {
            var p = src.Clone();
            if (planOverride != null) p.Plan = planOverride;
            var before = new Dictionary<AbilityKind, double>();
            foreach (var k in FielderGrowthOrder) before[k] = LevelProgress(p, k);
            foreach (var k in PitcherGrowthOrder) before[k] = LevelProgress(p, k);

            // チーム状態（結束）の効き先④（issue #1601）も実適用と同じ単一ソースから渡す。
            KokoSim.Unity.Shell.TrainingWeek.Apply(p, _week, roster: _roster,
                cohesionMultiplier: KokoSim.Unity.Shell.ChemistryDisplay.CurrentTrainingMultiplier());   // 練習不可週は何もしない（全て0）

            var gains = new Dictionary<AbilityKind, double>();
            foreach (var kv in before)
            {
                var d = LevelProgress(p, kv.Key) - kv.Value;
                gains[kv.Key] = kv.Key == AbilityKind.LaunchTendency ? System.Math.Abs(d) : System.Math.Max(0.0, d);
            }
            return gains;
        }

        // 取りうる最大の伸びのキャッシュ（±のたびに14回のドライランを回さない）。配分では変わらない入力だけをキーにする。
        private DevelopingPlayer _maxGainPlayer;
        private string _maxGainKey;
        private double _maxGain;

        /// <summary>
        /// この選手が、1日の全部を1つの練習（野手・投手練習の各メニュー）に注いだときの伸びの最大（全能力の中で）。
        /// 伸びの棒の満タン。個別指導・週・予算・チーム状態で変わるのでキーに含める。
        /// </summary>
        private double MaxPossibleGain(int index)
        {
            var src = _roster[index];
            var key = _week + "|" + Budget + "|" + src.IndividualCoaching + "|"
                + KokoSim.Unity.Shell.ChemistryDisplay.CurrentTrainingMultiplier().ToString("R");
            if (ReferenceEquals(src, _maxGainPlayer) && key == _maxGainKey) return _maxGain;

            var max = 0.0;
            foreach (var m in EditableMenus)
            {
                var plan = new TrainingPlan
                {
                    Preset = TrainingPreset.Custom,
                    Allocations = new List<MenuAllocation> { new MenuAllocation(m, Budget) },
                };
                foreach (var g in DryRunGains(src, plan).Values) if (g > max) max = g;
            }
            _maxGainPlayer = src;
            _maxGainKey = key;
            _maxGain = max;
            return max;
        }

        /// <summary>連続レベル進捗＝レベル＋（次までのexp割合）。週差分を取ると「今週の成長量」になる。</summary>
        private double LevelProgress(DevelopingPlayer p, AbilityKind k)
        {
            var req = _training.RequiredExp(p.Level(k));
            return p.Level(k) + (req > 0 ? p.Exp(k) / req : 0.0);
        }

        /// <summary>今の配分と中身が同じ週テンプレの名前（無ければ空）。選択欄の表示に使う。</summary>
        private string ActiveTemplateName(int index)
        {
            var cur = AllocationKey(CustomSnapshot(index));
            foreach (var t in _templates)
                if (AllocationKey(t.Plan.Allocations) == cur) return t.Name;
            return "";
        }

        private static string AllocationKey(IEnumerable<MenuAllocation> alloc)
        {
            var sum = new SortedDictionary<int, int>();
            if (alloc != null)
                foreach (var a in alloc)
                {
                    if (a.Menu == TrainingMenu.Rest || a.Minutes <= 0) continue;
                    sum.TryGetValue((int)a.Menu, out var cur);
                    sum[(int)a.Menu] = cur + a.Minutes;
                }
            var sb = new System.Text.StringBuilder();
            foreach (var kv in sum) sb.Append(kv.Key).Append(':').Append(kv.Value).Append(';');
            return sb.ToString();
        }

        private static bool IsPitcherMenu(TrainingMenu m)
            => m == TrainingMenu.Pitching || m == TrainingMenu.BreakingBall
               || m == TrainingMenu.VelocityTraining || m == TrainingMenu.Running;

        private static bool UiColorIsDefensePosition(TrainingMenu m)
            => m >= TrainingMenu.DefenseP && m <= TrainingMenu.DefenseOutfield;

        private static string PresetDesc(TrainingPreset preset) => preset switch
        {
            TrainingPreset.PitcherAuto => "投手向け。投げ込み・変化球・球速・走り込みを自動で配分します。",
            TrainingPreset.BatterAuto => "野手向け。打撃・長打・選球・守備・走塁を幅広く自動で配分します。",
            TrainingPreset.Balanced => "打撃・投球・守備をまんべんなく。迷ったらこれ。",
            TrainingPreset.DefenseFocus => "守備と送球を重点強化。堅い守りのチーム作りに。",
            TrainingPreset.AceDevelopment => "投げ込み・球速・変化球に集中。エースを一気に育てます。",
            TrainingPreset.SluggerDevelopment => "長打・打撃・筋力に集中。主砲の一発を伸ばします。",
            _ => "各メニューの時間を自分で配分します（10分単位）。プリセットなし＝カスタム。",
        };

        /// <summary>フィルタ＋ソート後の選手索引列（Row.Index は素の名簿索引を保持）。</summary>
        private List<int> SortedFilteredIndices()
        {
            var idx = new List<int>();
            for (var i = 0; i < _roster.Count; i++)
            {
                if (_yearFilter != 0 && _roster[i].Grade != _yearFilter) continue;
                // ベンチ入り＝背番号1–20、ベンチ外＝0（DevelopingPlayer.UniformNumber の確立済み判定, Issue #133-④）。
                var benchOut = _roster[i].UniformNumber == 0;
                if (_benchFilter == 1 && benchOut) continue;
                if (_benchFilter == 2 && !benchOut) continue;
                idx.Add(i);
            }

            idx.Sort(GradeSort);
            return idx;
        }

        /// <summary>学年昇順→名簿索引（＝生成時の固定ID）昇順。レギュラー変動で並びが動かない安定順。</summary>
        private int GradeSort(int a, int b)
        {
            var g = _roster[a].Grade.CompareTo(_roster[b].Grade);
            return g != 0 ? g : a.CompareTo(b);
        }

        private List<string> TemplateNames()
        {
            var names = new List<string>();
            foreach (var t in _templates) names.Add(t.Name);
            return names;
        }

        private IReadOnlyList<MenuAllocation> ResolvedAllocations(int index)
            => TrainingPresets.Resolve(PlanOf(index), Budget);

        /// <summary>背番号の表示テキスト（ベンチ外は「—」）。単一ソースは DevelopingPlayer.UniformNumber。</summary>
        private string NumTextAt(int index)
            => _roster[index].UniformNumber == 0 ? "—" : _roster[index].UniformNumber.ToString();

        private static string PresetJp(TrainingPreset preset) => preset switch
        {
            TrainingPreset.PitcherAuto => "投手お任せ",
            TrainingPreset.BatterAuto => "野手お任せ",
            TrainingPreset.Balanced => "バランス型",
            TrainingPreset.DefenseFocus => "守備重視型",
            TrainingPreset.AceDevelopment => "エース育成型",
            TrainingPreset.SluggerDevelopment => "主砲育成型",
            _ => "カスタム",
        };

        private static string PositionJp(FieldPosition pos) => FieldPositionNames.Abbr(pos);

        /// <summary>能力タブの守備適性行の名前（2字。1字の PositionJp はアイコンに使う, issue #327③）。</summary>
        private static string PositionFullJp(FieldPosition pos) => FieldPositionNames.Kanji2(pos);

        private static string MenuJp(TrainingMenu m) => m switch
        {
            TrainingMenu.Batting => "打撃",
            TrainingMenu.PowerHitting => "長打",
            TrainingMenu.FlyBallHitting => "フライ打撃",
            TrainingMenu.GroundBallHitting => "ゴロ打撃",
            TrainingMenu.PlateDiscipline => "選球",
            TrainingMenu.Strength => "筋力",
            TrainingMenu.BaseRunning => "走塁",
            TrainingMenu.Defense => "守備",
            TrainingMenu.Throwing => "遠投",
            TrainingMenu.Pitching => "投込",
            TrainingMenu.BreakingBall => "変化球",
            TrainingMenu.Running => "走込",
            TrainingMenu.VelocityTraining => "球速",
            TrainingMenu.Bunt => "バント",
            TrainingMenu.DefenseP => "投手守備",
            TrainingMenu.DefenseC => "捕手守備",
            TrainingMenu.Defense1B => "一塁守備",
            TrainingMenu.Defense2B => "二塁守備",
            TrainingMenu.Defense3B => "三塁守備",
            TrainingMenu.DefenseSS => "遊撃守備",
            TrainingMenu.DefenseLF => "左翼守備",
            TrainingMenu.DefenseCF => "中堅守備",
            TrainingMenu.DefenseRF => "右翼守備",
            TrainingMenu.DefenseInfield => "内野",
            TrainingMenu.DefenseOutfield => "外野",
            TrainingMenu.Rest => "休養",
            _ => m.ToString(),
        };

        /// <summary>一覧の横棒に書く略称（2〜3字）。守備位置は「左守」の形。</summary>
        private static string MenuShortJp(TrainingMenu m) => m switch
        {
            TrainingMenu.FlyBallHitting => "フライ",
            TrainingMenu.GroundBallHitting => "ゴロ",
            TrainingMenu.DefenseP => "投守",
            TrainingMenu.DefenseC => "捕守",
            TrainingMenu.Defense1B => "一守",
            TrainingMenu.Defense2B => "二守",
            TrainingMenu.Defense3B => "三守",
            TrainingMenu.DefenseSS => "遊守",
            TrainingMenu.DefenseLF => "左守",
            TrainingMenu.DefenseCF => "中守",
            TrainingMenu.DefenseRF => "右守",
            _ => MenuJp(m),
        };

        // SDF丸ゴフォントで確実に描画される漢字1文字アイコン（絵文字はグリフ欠落の恐れ）。
        private static string MenuIcon(TrainingMenu m) => m switch
        {
            TrainingMenu.Batting => "打",
            TrainingMenu.PowerHitting => "長",
            TrainingMenu.FlyBallHitting => "飛",
            TrainingMenu.GroundBallHitting => "地",
            TrainingMenu.PlateDiscipline => "選",
            TrainingMenu.Strength => "筋",
            TrainingMenu.BaseRunning => "走",
            TrainingMenu.Defense => "守",
            TrainingMenu.Throwing => "遠",
            TrainingMenu.Pitching => "投",
            TrainingMenu.BreakingBall => "変",
            TrainingMenu.Running => "ス",
            TrainingMenu.VelocityTraining => "速",
            TrainingMenu.Bunt => "犠",
            TrainingMenu.Rest => "休",
            _ => "守",
        };

        private static string MainEffectJp(TrainingMenu m)
        {
            var eff = TrainingMenus.Effects(m);
            if (eff.Main is AbilityKind k) return AbilityJp(k);
            return m == TrainingMenu.Rest ? "休養" : "守備適性";
        }

        // 能力表示名は単一ソース（KokoSim.Unity.Shell.AbilityLabels）に集約（issue #94）。
        private static string AbilityJp(AbilityKind k) => KokoSim.Unity.Shell.AbilityLabels.Jp(k);
    }
}
