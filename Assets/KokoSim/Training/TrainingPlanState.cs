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
        public string Icon = "";
        public string MainEffectJp = "";
        public int Minutes;
        public string SectionJp = "";   // 非空＝このスロットの直前に節見出しを挿す（野手練習/投手練習, Issue #133-⑥）
    }

    /// <summary>今週の見込み（exp進捗）。1週ドライラン後の「次レベルまでの割合」を主効果能力ごとに提示し、
    /// レベルアップが起きない週でもバーが伸びて死に列にならないようにする（設計書02 §5.1）。</summary>
    public sealed class GainBar
    {
        public string AbilityJp = "";
        public string Icon = "";
        public double Progress;    // 0..1（次レベルまでの割合）
        public int LevelsGained;   // この週で上がったレベル数（0=進捗のみ）
        public int Value;          // 現在の能力値（0=未設定＝相対強調バー）
        public string Grade = "";  // 現在値のランク S〜G（空＝ランクを持たない相対バー）
        // 弾道など優劣のないタイプ軸（issue #219）: 非空なら Grade の代わりにこのラベルをチップ表示する
        public string TypeLabel = "";
    }

    /// <summary>プリセット選択肢（プリセット帯の1チップ。説明はツールチップで持つ, issue #222④）。</summary>
    public sealed class PresetOption
    {
        public TrainingPreset Preset;
        public string Jp = "";
        public string Desc = "";
        public bool Selected;
    }

    /// <summary>守備ポジション別の適性と割当（守備適性割当口）。</summary>
    public sealed class DefenseSlot
    {
        public FieldPosition Pos;
        public TrainingMenu Menu;
        public string Label = "";   // 投/捕/一…
        public int Aptitude;        // 現在の適性値
        public int Minutes;         // 割当分
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
        public string FocusSummary = "";   // 現在の主眼メニュー（分の多い順・行の余白に表示）
        public List<MenuSlot> Chips = new List<MenuSlot>();  // 積み上げ配分バーの元データ（メニュー別 分・固定順, Issue #133-③）
        public int UsedMin;
        public int UsagePct;
        public List<string> Gains = new List<string>();      // 伸び見込み（ミート+1 等・旧表示）
        public List<GainBar> GainBars = new List<GainBar>();  // 今週の見込み（exp進捗バー・新表示）
        public bool Selected;
    }

    public sealed class TrainingPlanView
    {
        public int Budget;
        public string WeekLabel = "";
        public int RosterCount;
        public string TeamRankGrade = "F";   // 共通トップバーのチーム総合力ランク

        // 合宿バナー
        public bool CampActive;
        public string CampTitle = "";
        public string CampMult = "";
        public string CampNote = "";

        // 個別指導3枠
        public List<string> Nominations = new List<string> { "空き", "空き", "空き" };

        // 選択選手（エディタ）
        public int SelectedIndex;
        public string SelectedName = "";
        public string SelNumText = "—";
        public bool SelBenchOut;
        public string SelYear = "1年";
        public string SelHand = "右投右打";
        public PlayerStrength SelStrength;   // カテゴリ別ランク（総合ランクは廃止, Issue #133-①）
        public bool SelNominated;
        public string SelNomLabel = "個別指導に指名";
        public int SelectedTotal;
        public int SelectedRemaining;
        public bool SelectedIsCustom;
        public bool DelegateOn;
        public string DelegateStateLabel = "手動";
        public bool SelHasPrev;
        public bool SelHasNext;
        public bool AtBudgetLimit;   // 残り0分（issue #222③・±/バーを無効表示にするための旗）

        public List<PlayerTrainRow> Rows = new List<PlayerTrainRow>();
        public List<MenuSlot> SelectedSlots = new List<MenuSlot>();   // 選択選手のメニュー別 分（編集用）
        public List<DefenseSlot> DefenseSlots = new List<DefenseSlot>(); // 守備適性割当口（9ポジション）
        public List<PresetOption> PresetOptions = new List<PresetOption>(); // 統合モーダルのプリセット一覧
        public List<GainBar> SelectedGrowth = new List<GainBar>();    // この設定で伸びる能力（現在設定の伸びバー）
        public List<string> Templates = new List<string>();          // 保存済み週テンプレ名
        public List<PlayerTrainRow> CopyRows = new List<PlayerTrainRow>(); // 複製ピッカー用の全部員（フィルタ非適用・学年順, Issue #133-⑤）

        // フィルタ/ソートの選択状態
        public int YearFilter;   // 0=全, 1..3=学年
        public int BenchFilter;  // 0=全員, 1=ベンチ入り, 2=ベンチ外（Issue #133-④）
        public int SortMode;     // 0=学年順 1=総合
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
            TrainingMenu.Batting, TrainingMenu.PowerHitting, TrainingMenu.PlateDiscipline, TrainingMenu.Strength,
            TrainingMenu.Defense, TrainingMenu.Throwing, TrainingMenu.BaseRunning, TrainingMenu.Bunt,
            TrainingMenu.Pitching, TrainingMenu.BreakingBall, TrainingMenu.VelocityTraining, TrainingMenu.Running,
        };

        // 積み上げ配分バーの表示順（Issue #133-③）: 野手系→投手系（EditableMenus と同順）→ポジション別守備。
        // 行ごとに同じ順で並べることで、縦スキャンで選手間の配分差を比較できる（UI原則①⑥）。休養は除外＝余白。
        private static readonly TrainingMenu[] BarMenuOrder =
        {
            TrainingMenu.Batting, TrainingMenu.PowerHitting, TrainingMenu.PlateDiscipline, TrainingMenu.Strength,
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

        private readonly SeasonCalendar _calendar = new SeasonCalendar();
        private readonly GrowthStageTable _stages = new GrowthStageTable();
        private readonly TrainingCoefficients _training = new TrainingCoefficients();

        /// <summary>週テンプレ1件（名前＋配分のスナップショット, issue #222⑤）。呼出で他選手へ適用する。</summary>
        private sealed class SavedTemplate
        {
            public string Name = "";
            public TrainingPlan Plan;
        }

        private readonly IReadOnlyList<DevelopingPlayer> _roster; // 表示用（全画面共有の RosterService.Active）
        private readonly TrainingPlan[] _plans;          // 選手索引→練習計画
        private readonly bool[] _delegated;              // 選手索引→委任フラグ（UI状態）
        private readonly List<SavedTemplate> _templates = new List<SavedTemplate>(); // 週テンプレ（UI状態）
        private int _selected;
        // モーダルを開いた（＝選手を選択した）時点の配分スナップショット。「元に戻す」の復元先（issue #222⑥）。
        private TrainingPlan _revertPlan;
        private int _revertIndex = -1;
        // 現在週は全画面共有の GameClock を単一ソースとする（週送りで変化。合宿バナー・成長段階に反映）。
        private static int _week => KokoSim.Unity.Shell.GameClock.Week;
        private int _yearFilter;
        private int _benchFilter;   // 0=全員, 1=ベンチ入り, 2=ベンチ外（Issue #133-④）
        private int _sortMode;

        public int Budget => _training.DefaultBudgetMinutes;
        public int SelectedIndex => _selected;

        public TrainingPlanState()
        {
            // 全画面で共有する単一ソースのロスター（背番号はメンバー設定画面と一致, RosterService）。
            _roster = RosterService.Active;
            _plans = new TrainingPlan[_roster.Count];
            _delegated = new bool[_roster.Count];
            for (var i = 0; i < _roster.Count; i++)
                _plans[i] = TrainingPlan.Auto(_roster[i].IsPitcher);
            // 背番号は DevelopingPlayer.UniformNumber（メンバー設定画面で編集）を単一ソースとして読む。
            // 現在週は GameClock（全画面共有）。既定はシーズン頭（週0＝4月）。冬合宿を見るには週送りする。
        }

        public void SelectPlayer(int index)
        {
            if (index < 0 || index >= _plans.Length) return;
            _selected = index;
            _revertPlan = _plans[index];
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
            _plans[_selected] = _revertPlan;
        }

        /// <summary>週送り（合宿・成長段階のプレビューが連動）。</summary>
        public void StepWeek(int delta)
        {
            KokoSim.Unity.Shell.GameClock.Advance(delta);   // 共有週を進める（全画面へ反映）
        }

        public void SetYearFilter(int grade) => _yearFilter = grade;
        public void SetBenchFilter(int mode) => _benchFilter = mode;
        public void SetSortMode(int mode) => _sortMode = mode;

        /// <summary>選手のプリセットを設定（統合モーダルから選択）。委任中は無効。
        /// Custom は現在の解決済み配分を引き継いで手動編集の起点にする。</summary>
        public void SetPreset(int index, TrainingPreset preset)
        {
            if (index < 0 || index >= _plans.Length || _delegated[index]) return;
            _plans[index] = preset == TrainingPreset.Custom
                ? new TrainingPlan { Preset = TrainingPreset.Custom, Allocations = CustomSnapshot(index) }
                : new TrainingPlan { Preset = preset };
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
            if (target < 0 || target >= _plans.Length || source < 0 || source >= _plans.Length) return;
            if (_delegated[target]) return;
            _plans[target] = _plans[source];
        }

        /// <summary>Custom編集: 指定メニューの分を増減（±ボタン・長押しリピート用）。プリセットは Custom へ切替。
        /// budget 超過は加算をクランプ（issue #222③・無音クランプ自体は据え置きだが、UI側で上限0を可視化する）。</summary>
        public void AdjustMinutes(int index, TrainingMenu menu, int deltaSteps)
        {
            if (index < 0 || index >= _plans.Length || _delegated[index]) return;
            var slots = new Dictionary<TrainingMenu, int>();
            foreach (var a in CustomSnapshot(index)) slots[a.Menu] = a.Minutes;
            slots.TryGetValue(menu, out var cur);
            ApplyMenuMinutes(index, slots, menu, cur + deltaSteps * MinuteStep);
        }

        /// <summary>Custom編集: 指定メニューの分を絶対値で設定（ドラッグバー用, issue #222②）。
        /// 10分単位へスナップし、他メニュー合計＋budget でクランプする。</summary>
        public void SetMinutes(int index, TrainingMenu menu, int minutesAbs)
        {
            if (index < 0 || index >= _plans.Length || _delegated[index]) return;
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
            _plans[index] = _plans[index] with { Preset = TrainingPreset.Custom, Allocations = alloc };
        }

        /// <summary>委任トグル（UI状態）。委任中は編成ロック＋バランス型に委ねる。</summary>
        public void ToggleDelegate(int index)
        {
            if (index < 0 || index >= _plans.Length) return;
            _delegated[index] = !_delegated[index];
            if (_delegated[index])
                _plans[index] = new TrainingPlan
                {
                    Preset = _roster[index].IsPitcher ? TrainingPreset.PitcherAuto : TrainingPreset.Balanced,
                };
        }

        public bool IsDelegated(int index) => index >= 0 && index < _delegated.Length && _delegated[index];

        /// <summary>個別指導3枠の指名トグル（設計書06 §3.3・Issue #126）。上限は係数
        /// TrainingCoefficients.IndividualCoachingSlots（既定3）。共有ロスター本体（DevelopingPlayer.
        /// IndividualCoaching）へ直接書き込むため、指名は育成式（DevelopmentModel）へ実効果を持つ。</summary>
        public void ToggleNominate(int index)
        {
            if (index < 0 || index >= _roster.Count) return;
            if (_roster[index].IndividualCoaching) { _roster[index].IndividualCoaching = false; return; }

            var count = 0;
            foreach (var p in _roster) if (p.IndividualCoaching) count++;
            if (count < _training.IndividualCoachingSlots) _roster[index].IndividualCoaching = true;
        }

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
            if (index < 0 || index >= _plans.Length || _delegated[index]) return;
            foreach (var t in _templates)
                if (t.Name == name) { _plans[index] = t.Plan; return; }
        }

        public void RemoveTemplate(string name) => _templates.RemoveAll(t => t.Name == name);

        public TrainingPlanView BuildView()
        {
            var view = new TrainingPlanView
            {
                Budget = Budget,
                RosterCount = _roster.Count,
                WeekLabel = KokoSim.Unity.Shell.GameClock.CurrentLabel(),   // 共通「YYYY年M月W週目」（共有現在週）
                SelectedIndex = _selected,
                SelectedName = _roster[_selected].Name,
                YearFilter = _yearFilter,
                BenchFilter = _benchFilter,
                SortMode = _sortMode,
                Templates = TemplateNames(),
            };

            // チーム総合力（共通トップバー表示）＝6指標のリーグ標準化総合を Tier 変換（③, 全画面統一）。
            view.TeamRankGrade = KokoSim.Unity.Shell.TeamOverall.GradeOf(_roster);

            BuildCampBanner(view);
            BuildNominations(view);

            // 行は「誰・カテゴリ別ランク・投打・現在のプリセット＋配分バー」を持つ（詳細編集は統合モーダル）。
            foreach (var i in SortedFilteredIndices()) view.Rows.Add(MakeRow(i));

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
                PresetJp = _delegated[i] ? "委任" : PresetJp(_plans[i].Preset),
                FocusSummary = _delegated[i] ? "コーチ一任" : FocusSummary(ResolvedAllocations(i)),
                Chips = AllocationChips(i),
                Selected = i == _selected,
            };
        }

        /// <summary>積み上げ配分バーの元データ（Issue #133-③）。解決済み配分を BarMenuOrder の固定順で列挙。
        /// 休養は除外＝バー右側の余白として残り時間が読める。</summary>
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
            foreach (var m in BarMenuOrder)
                if (alloc.TryGetValue(m, out var min))
                    list.Add(new MenuSlot { Menu = m, MenuJp = MenuJp(m), Minutes = min });
            return list;
        }

        private void BuildCampBanner(TrainingPlanView view)
        {
            var mult = _calendar.CampMultiplier(_week, _training);
            view.CampActive = mult > 1.0;
            if (!view.CampActive) return;
            var winter = mult >= _training.WinterCampMult;
            view.CampTitle = winter ? "冬合宿・鍛錬期" : "夏合宿";
            view.CampMult = "×" + mult.ToString("0.0");
            view.CampNote = winter
                ? "対外試合禁止期間（12〜3月）— 育成の山場です。"
                : "新チーム結成直後の底上げ期間です。";
        }

        private void BuildNominations(TrainingPlanView view)
        {
            var nominatedIdx = new List<int>();
            for (var i = 0; i < _roster.Count; i++) if (_roster[i].IndividualCoaching) nominatedIdx.Add(i);

            view.Nominations.Clear();
            for (var s = 0; s < _training.IndividualCoachingSlots; s++)
            {
                if (s < nominatedIdx.Count)
                {
                    var idx = nominatedIdx[s];
                    view.Nominations.Add(NumTextAt(idx) + " " + _roster[idx].Name);
                }
                else view.Nominations.Add("空き");
            }
        }

        private void BuildSelected(TrainingPlanView view)
        {
            var sel = _roster[_selected];
            view.SelNumText = NumTextAt(_selected);
            view.SelBenchOut = _roster[_selected].UniformNumber == 0;
            view.SelYear = sel.Grade + "年";
            view.SelHand = HandednessLabels.Combined(sel.Throws, sel.Bats);
            view.SelStrength = PlayerStrengthProfile.Compute(sel, Coeff);
            view.SelNominated = sel.IndividualCoaching;
            view.SelNomLabel = view.SelNominated ? "個別指導 指名中" : "個別指導に指名";
            view.DelegateOn = _delegated[_selected];
            view.DelegateStateLabel = _delegated[_selected] ? "委任中" : "手動";
            view.SelHasPrev = CanStepSelected(-1);
            view.SelHasNext = CanStepSelected(+1);

            // 能力系メニューの編集スロット。
            var selAlloc = new Dictionary<TrainingMenu, int>();
            foreach (var a in ResolvedAllocations(_selected)) selAlloc[a.Menu] = a.Minutes;
            var total = 0;
            foreach (var m in EditableMenus)
            {
                selAlloc.TryGetValue(m, out var min);
                total += min;
                view.SelectedSlots.Add(new MenuSlot
                {
                    Menu = m, MenuJp = MenuJp(m), Icon = MenuIcon(m), MainEffectJp = MainEffectJp(m), Minutes = min,
                    // 打撃系/投手系の繋ぎ目に節見出しを挿す（Issue #133-⑥。EditableMenus は野手系→投手系の順）。
                    SectionJp = m == EditableMenus[0] ? "野手練習" : m == TrainingMenu.Pitching ? "投手練習" : "",
                });
            }

            // 守備適性割当口（9ポジション）。
            foreach (var (pos, menu) in DefenseMenus)
            {
                selAlloc.TryGetValue(menu, out var dmin);
                total += dmin;
                view.DefenseSlots.Add(new DefenseSlot
                {
                    Pos = pos, Menu = menu, Label = PositionJp(pos), Aptitude = sel.Aptitude(pos), Minutes = dmin,
                });
            }

            view.SelectedTotal = total;
            view.SelectedRemaining = Budget - total;
            view.SelectedIsCustom = _plans[_selected].Preset == TrainingPreset.Custom;
            view.AtBudgetLimit = view.SelectedRemaining <= 0;

            // プリセット選択肢（チップ＋ツールチップの説明）。
            var cur = _plans[_selected].Preset;
            foreach (var p in SelectablePresets)
                view.PresetOptions.Add(new PresetOption
                {
                    Preset = p,
                    Jp = PresetJp(p),
                    Desc = PresetDesc(p),
                    Selected = !_delegated[_selected] && p == cur,
                });

            // 現在設定の伸びバー（この設定で伸びる能力・実効ドライラン）。
            view.SelectedGrowth.AddRange(BuildGrowthBars(_selected));
        }

        // 「この設定で伸びる能力」に常時並べる能力（打撃→走守→投手の全能力）。伸びない能力も出し、
        // 効果のある能力だけバーが伸びる。走塁/盗塁/送球は練習メニュー経路が無いので一覧から除く。
        private static readonly AbilityKind[] GrowthDisplayOrder =
        {
            AbilityKind.Contact, AbilityKind.Power, AbilityKind.LaunchTendency, AbilityKind.Discipline, AbilityKind.Bunt,
            AbilityKind.Speed, AbilityKind.ArmStrength, AbilityKind.Fielding, AbilityKind.Catching,
            AbilityKind.Velocity, AbilityKind.Control, AbilityKind.Stamina, AbilityKind.PitchRank,
        };

        /// <summary>現在の設定（解決済み配分）での各能力の1週成長を、非破壊ドライランで算出（設計書02 §5.1）。
        /// 全能力を常に列挙し、バーは「今週の成長量（＝進んだレベル進捗）」。効果の無い能力は 0（伸びない）。</summary>
        private List<GainBar> BuildGrowthBars(int index)
        {
            var bars = new List<GainBar>();
            var alloc = ResolvedAllocations(index);

            var p = _roster[index].Clone();   // 実選手の複製で非破壊ドライラン（Issue #223・捨てるインスタンス）
            // 個別指導3枠（Issue #126）: このプレビュー用インスタンスへ指名状態を写す（実選手側は
            // ToggleNominate が書き込み済み）。監督の分野別指導力（#115）も注入し、追加倍率を可視化する。
            p.IndividualCoaching = IsNominated(index);
            var coaching = CoachingProfile.FromManager(KokoSim.Unity.Shell.ManagerService.Manager);
            var beforeProg = new Dictionary<AbilityKind, double>();
            var beforeLv = new Dictionary<AbilityKind, int>();
            foreach (var k in GrowthDisplayOrder) { beforeProg[k] = LevelProgress(p, k); beforeLv[k] = p.Level(k); }

            if (_calendar.CanTrain(p.Grade, _week))
            {
                var camp = _calendar.CampMultiplier(_week, _training);
                var stage = _calendar.StageIndex(p.Grade, _week);
                DevelopmentModel.TrainWeekPlan(p, alloc, _training.ReferenceWeekMinutes, stage, camp, _stages, _training,
                    coaching: coaching);
            }

            foreach (var k in GrowthDisplayOrder)
            {
                var gain = LevelProgress(p, k) - beforeProg[k];   // 今週進んだレベル進捗（0=伸びない）
                if (gain < 0) gain = 0;
                bars.Add(new GainBar
                {
                    AbilityJp = AbilityJp(k), Icon = AbilityIcon(k),
                    Progress = System.Math.Clamp(gain, 0.0, 1.0),   // 1レベル=満タン、複数レベルはLevelsGainedで表示
                    LevelsGained = p.Level(k) - beforeLv[k],
                    // ランク併記は「今週の伸び」ではなく**現在の能力値**を表す（UI原則③）。
                    // 弾道は優劣のないタイプ軸なのでランクチップではなくタイプラベルを出す（issue #219）。
                    Value = p.Level(k),
                    Grade = k == AbilityKind.LaunchTendency ? "" : Tiers.FromStrength(p.Level(k)).ToString(),
                    TypeLabel = k == AbilityKind.LaunchTendency ? KokoSim.Unity.Shell.LaunchTendencyLabels.Jp(p.Level(k)) : "",
                });
            }
            return bars;
        }

        /// <summary>連続レベル進捗＝レベル＋（次までのexp割合）。週差分を取ると「今週の成長量」になる。</summary>
        private double LevelProgress(DevelopingPlayer p, AbilityKind k)
        {
            var req = _training.RequiredExp(p.Level(k));
            return p.Level(k) + (req > 0 ? p.Exp(k) / req : 0.0);
        }

        private static string PresetDesc(TrainingPreset preset) => preset switch
        {
            TrainingPreset.PitcherAuto => "投手向け。投げ込み・変化球・球速・走り込みを自動で配分します。",
            TrainingPreset.BatterAuto => "野手向け。打撃・長打・選球・守備・走塁を幅広く自動で配分します。",
            TrainingPreset.Balanced => "役割に応じて打撃と投球をバランス良く。迷ったらこれ。",
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

            idx.Sort((a, b) => _sortMode switch
            {
                1 => _roster[b].AverageLevel().CompareTo(_roster[a].AverageLevel()), // 総合 降順
                _ => GradeSort(a, b),                                                // 学年順（安定）
            });
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
            => TrainingPresets.Resolve(_plans[index], _roster[index].IsPitcher, Budget);

        /// <summary>背番号の表示テキスト（ベンチ外は「—」）。単一ソースは DevelopingPlayer.UniformNumber。</summary>
        private string NumTextAt(int index)
            => _roster[index].UniformNumber == 0 ? "—" : _roster[index].UniformNumber.ToString();

        /// <summary>現在の配分の主眼メニューを分の多い順に上位3件、「投込・変化球・球速」の形で。</summary>
        private static string FocusSummary(IReadOnlyList<MenuAllocation> alloc)
        {
            var sorted = new List<MenuAllocation>();
            foreach (var a in alloc) if (a.Menu != TrainingMenu.Rest && a.Minutes > 0) sorted.Add(a);
            sorted.Sort((x, y) => y.Minutes.CompareTo(x.Minutes));
            var names = new List<string>();
            foreach (var a in sorted) { if (names.Count >= 3) break; names.Add(MenuJp(a.Menu)); }
            return names.Count == 0 ? "休養のみ" : string.Join("・", names);
        }

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

        private static string PositionJp(FieldPosition pos) => pos switch
        {
            FieldPosition.Pitcher => "投",
            FieldPosition.Catcher => "捕",
            FieldPosition.FirstBase => "一",
            FieldPosition.SecondBase => "二",
            FieldPosition.ThirdBase => "三",
            FieldPosition.Shortstop => "遊",
            FieldPosition.LeftField => "左",
            FieldPosition.CenterField => "中",
            FieldPosition.RightField => "右",
            _ => "?",
        };

        private static string MenuJp(TrainingMenu m) => m switch
        {
            TrainingMenu.Batting => "打撃",
            TrainingMenu.PowerHitting => "長打",
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

        // SDF丸ゴフォントで確実に描画される漢字1文字アイコン（絵文字はグリフ欠落の恐れ）。
        private static string MenuIcon(TrainingMenu m) => m switch
        {
            TrainingMenu.Batting => "打",
            TrainingMenu.PowerHitting => "長",
            TrainingMenu.PlateDiscipline => "選",
            TrainingMenu.Strength => "筋",
            TrainingMenu.BaseRunning => "走",
            TrainingMenu.Defense => "守",
            TrainingMenu.Throwing => "遠",
            TrainingMenu.Pitching => "投",
            TrainingMenu.BreakingBall => "変",
            TrainingMenu.Running => "持",
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

        // 能力アイコン（SDF丸ゴ収録の漢字1文字）。
        private static string AbilityIcon(AbilityKind k) => k switch
        {
            AbilityKind.Contact => "ミ",
            AbilityKind.Power => "パ",
            AbilityKind.LaunchTendency => "弾",
            AbilityKind.Discipline => "選",
            AbilityKind.Speed => "走",
            AbilityKind.ArmStrength => "肩",
            AbilityKind.Fielding => "守",
            AbilityKind.Catching => "捕",
            AbilityKind.Velocity => "速",
            AbilityKind.Control => "制",
            AbilityKind.Stamina => "ス",
            AbilityKind.PitchRank => "球",
            AbilityKind.Bunt => "犠",
            AbilityKind.Steal => "盗",
            AbilityKind.Baserunning => "塁",
            AbilityKind.ThrowAccuracy => "送",
            _ => "能",
        };

        // 能力表示名は単一ソース（KokoSim.Unity.Shell.AbilityLabels）に集約（issue #94）。
        private static string AbilityJp(AbilityKind k) => KokoSim.Unity.Shell.AbilityLabels.Jp(k);
    }
}
