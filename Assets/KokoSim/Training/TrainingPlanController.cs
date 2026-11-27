using System.Collections.Generic;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using UnityEngine;
using UnityEngine.UIElements;
using KokoSim.Unity.Components; // 部品辞書（RankChip / LabeledStackBar / DashedFrame / TimeAllocSlider）
using KokoSim.Unity.Shell;      // LineIcon / SchoolAccent

namespace KokoSim.Unity.Training
{
    /// <summary>
    /// 練習画面のコントローラ（設計書06 §3.3・練習画面のリデザイン）。UIDocument（TrainingPlan.uxml）へ
    /// TrainingPlanState（ViewModel）をバインドする。個別指導の帯（3枠）・部員別の練習割当（行＝練習名と分数を
    /// 書いた横棒）・練習メニューの配分のポップアップ（タブなしの1画面：野手練習｜投手練習＋守備位置｜伸びる能力）を組む。
    /// 機能の入口（指名・絞り込み・変更・複製・型・±・テンプレート・元に戻す・決定・前後・手動）は ViewModel のまま。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class TrainingPlanController : MonoBehaviour
    {
        private static readonly (string Label, int Grade)[] YearFilters =
            { ("全学年", 0), ("1年", 1), ("2年", 2), ("3年", 3) };
        private static readonly (string Label, int Mode)[] BenchFilters =
            { ("全員", 0), ("ベンチ入り", 1), ("ベンチ外", 2) };

        // 練習の行の棒が満タンになる分数の、1日の分数に対する割合（300分なら120分）。
        private const float BarFullShareOfBudget = 0.4f;

        // 空き枠の点線（--sbk-star-off と同じ色。DashedFrame は色を C# で受け取る部品）。
        private static readonly Color EmptySlotDash = new Color32(0xC9, 0xC2, 0xB4, 0xFF);

        private TrainingPlanState _state;
        private VisualElement _root;
        private VisualElement _modal;
        private VisualElement _copyModal;
        private bool _modalOpen;
        // モーダルを開いている間は後ろの部員一覧を作り直さない（決定を押すまで一覧の表示を変えない・issue #1534）。
        private bool _rowsFrozen;
        private bool _copyOpen;     // 複製ピッカー（コピー元を選ぶ, Issue #133-⑤）
        private int _copyTarget;    // コピー先の選手索引
        private bool _tplMenuOpen;  // 週テンプレートの一覧（選択欄から開く）
        private Color _accent;

        private void OnEnable()
        {
            _state = new TrainingPlanState();
            // 画面を離れて戻ると UIDocument が木を作り直す。古い木を指す部品・シグネチャ・開閉状態を持ち越さない。
            _modalOpen = false;
            _rowsFrozen = false;
            _copyOpen = false;
            _tplMenuOpen = false;
            _copyTarget = 0;
            _menuSig = null;
            _menuRows.Clear();
            _toastJob = null;
            _root = GetComponent<UIDocument>().rootVisualElement;
            _modal = _root.Q<VisualElement>("tp-modal");
            _copyModal = _root.Q<VisualElement>("tp-copy-modal");
            AddIcons();
            WireStaticButtons();
            WireOverlayClose(_modal, () => _modalOpen, CancelModal);   // 暗幕・Esc は「決定」以外＝取り消し（issue #1534）
            WireOverlayClose(_copyModal, () => _copyOpen, CloseCopyPicker);
            WireTemplateMenuDismiss();
            Render();
        }

        private void OnDisable()
        {
            _toastJob?.Pause();
            _toastJob = null;
        }

        // UXML のアイコンだけのボタンに線画を差す（木は画面に戻るたびに作り直されるので毎回）。
        private void AddIcons()
        {
            AddIcon("sel-prev", LineIcon.Kind.ChevronLeft);
            AddIcon("sel-next", LineIcon.Kind.ChevronRight);
            AddIcon("modal-close", LineIcon.Kind.Close);
            AddIcon("copy-close", LineIcon.Kind.Close);
            AddIcon("tpl-select", LineIcon.Kind.ChevronDown);
        }

        private void AddIcon(string name, LineIcon.Kind kind)
        {
            var host = _root.Q<VisualElement>(name);
            if (host == null || host.Q<LineIcon>() != null) return;
            host.Add(new LineIcon(kind));
        }

        /// <summary>スクリムクリック・Escで閉じる（issue #222⑥）。カード内クリックは伝播停止し、
        /// スクリム自体（カードの外側）のクリックだけが閉じる。Escはオーバーレイへフォーカスして拾う。</summary>
        private static void WireOverlayClose(VisualElement scrim, System.Func<bool> isOpen, System.Action close)
        {
            if (scrim == null) return;
            scrim.focusable = true;
            scrim.RegisterCallback<ClickEvent>(_ => { if (isOpen()) close(); });
            scrim.RegisterCallback<KeyDownEvent>(e =>
            {
                if (isOpen() && e.keyCode == KeyCode.Escape) { close(); e.StopPropagation(); }
            });
            var card = scrim.Q<VisualElement>(className: "modal-card");
            card?.RegisterCallback<ClickEvent>(e => e.StopPropagation());
        }

        // 週テンプレートの一覧は、選択欄と一覧の外を押したら閉じる。
        private void WireTemplateMenuDismiss()
        {
            var card = _modal?.Q<VisualElement>(className: "modal-card");
            if (card == null) return;
            card.RegisterCallback<ClickEvent>(e =>
            {
                if (!_tplMenuOpen) return;
                var t = e.target as VisualElement;
                var sel = _root.Q<VisualElement>("tpl-select");
                var menu = _root.Q<VisualElement>("tpl-menu");
                if (t != null && ((sel != null && sel.Contains(t)) || (menu != null && menu.Contains(t)))) return;
                _tplMenuOpen = false;
                Render();
            });
        }

        private void WireStaticButtons()
        {
            // 共通トップバーの「今週を進める」で週送り。全タブ共通の進週処理へ集約（issue #134）。
            Click("advance", () => WeekAdvance.FromSideScreen(() =>
            {
                // 年度替わりで部員が入れ替わったら一覧を引き直す。開いていたモーダルは別人を指すので閉じる（issue #1250）。
                if (_state.SyncRoster()) { _modalOpen = false; _copyOpen = false; }
                Render();
            }));
            // モーダル内の操作（対象は選択選手）。押下後も Render で開いたまま最新化する。
            Click("template-save", () => { _state.SaveTemplate(); _tplMenuOpen = false; Toast("週テンプレートを保存しました"); Render(); });
            Click("tpl-select", () =>
            {
                if (_state.BuildView().Templates.Count == 0) return;
                _tplMenuOpen = !_tplMenuOpen;
                Render();
            });
            Click("delegate-toggle", () => { _state.ToggleDelegate(_state.SelectedIndex); Render(); });
            Click("sel-nom", () => Nominate(_state.SelectedIndex));
            // 前後選手切替（issue #222⑥・一覧の現在フィルタ/ソート順）。
            Click("sel-prev", () => { _state.StepSelected(-1); _tplMenuOpen = false; Render(); _modal?.Focus(); });
            Click("sel-next", () => { _state.StepSelected(+1); _tplMenuOpen = false; Render(); _modal?.Focus(); });
            Click("modal-revert", () => { _state.RevertSelected(); Toast("配分を元に戻しました"); Render(); });
            // 「決定」だけが変更を残す。×・暗幕・Esc は開いた時点へ戻して閉じる（issue #1534）。
            Click("modal-close", CancelModal);
            Click("modal-done", CommitModal);
            Click("copy-close", CloseCopyPicker);
        }

        private void OpenCopyPicker(int target)
        {
            _copyTarget = target;
            _copyOpen = true;
            Render();
            _copyModal?.BringToFront();
            _root.Q<Label>("toast")?.BringToFront();
            _copyModal?.Focus();
        }

        private void CloseCopyPicker()
        {
            _copyOpen = false;
            Render();
        }

        private void OpenModal(int index)
        {
            _state.SelectPlayer(index);
            _state.BeginEdit();   // 決定以外で閉じたら戻す控え（issue #1534）
            _modalOpen = true;
            _tplMenuOpen = false;
            Render();
            _modal?.BringToFront();
            _root.Q<Label>("toast")?.BringToFront();
            _modal?.Focus();
        }

        /// <summary>「決定」: モーダルでの変更を残して閉じる。</summary>
        public void CommitModal()
        {
            _state.CommitEdit();
            CloseModal();
        }

        /// <summary>×・暗幕・Esc: モーダルを開いた時点の状態へ戻して閉じる（issue #1534）。</summary>
        public void CancelModal()
        {
            _state.CancelEdit();
            CloseModal();
        }

        private void CloseModal()
        {
            _modalOpen = false;
            _tplMenuOpen = false;
            Render();
        }

        private void Render()
        {
            var v = _state.BuildView();
            _accent = SchoolAccent.ForManager();

            // 共通トップバー（スコアボード）: 掲示板の升目とチーム総合力ランクを埋める。
            ScoreboardStrip.Fill(_root);
            TopBarMeters.Fill(_root);   // 部費残高・名声（ManagerService 単一ソース）
            var rank = _root.Q<VisualElement>("team-rank");
            if (rank != null) { rank.Clear(); rank.Add(UiComponents.RankChip(v.TeamRankGrade)); }

            SetText("roster-count", v.ShownCount.ToString());
            SetText("thead-menu", "練習メニュー（1日 " + v.Budget + "分）");

            BuildNominations(v);
            BuildFilters(v);
            if (!(_modalOpen && _rowsFrozen)) BuildRows(v);
            _rowsFrozen = _modalOpen;
            BuildModal(v);
            BuildCopyModal(v);
            SchoolAccent.Apply(_root, _accent);
        }

        // ── 個別指導3枠 ─────────────────────────────
        /// <summary>指名・解除（部員表の★・枠の×・モーダルのボタンの共通入口, issue #707）。
        /// 枠が埋まっているときは黙って無視せず、トーストで理由を出す。</summary>
        private void Nominate(int index)
        {
            var result = _state.ToggleNominate(index);
            if (result == NominationResult.Full)
                Toast("個別指導の枠がいっぱいです（" + _nomSlotCount + "枠）。先に誰かを外してください");
            Render();
        }

        private int _nomSlotCount = 3;   // 直近の描画での枠数（係数 IndividualCoachingSlots）。トーストの文言用

        private void BuildNominations(TrainingPlanView v)
        {
            _nomSlotCount = v.Nominations.Count;
            SetText("nom-count", v.NominatedCount.ToString());
            SetText("nom-cap", "/ " + v.Nominations.Count + "枠");
            var host = _root.Q<VisualElement>("nom-slots");
            if (host == null) return;
            host.Clear();
            foreach (var n in v.Nominations) host.Add(n.Empty ? EmptySlot() : FilledSlot(n));
        }

        private VisualElement FilledSlot(NominationSlot n)
        {
            var slot = new VisualElement();
            slot.AddToClassList("tp-slot");
            slot.AddToClassList("acc-bd");
            slot.style.backgroundColor = SchoolAccent.Tint(_accent, 0.06f);

            var star = new LineIcon(LineIcon.Kind.StarFilled);
            star.AddToClassList("tp-slot__star");
            slot.Add(star);

            slot.Add(NumBox(n.NumText, "tp-slot__num"));

            var body = new VisualElement();
            body.AddToClassList("tp-slot__body");
            body.Add(Text(n.Name, "tp-slot__name f-display"));
            body.Add(Text(n.GradeLabel + " " + n.HandLabel, "tp-slot__meta"));
            slot.Add(body);

            var idx = n.Index; // 捕捉
            var x = IconButton(LineIcon.Kind.Close, "tp-slot__x", () => Nominate(idx));
            x.tooltip = "個別指導から外す";
            slot.Add(x);
            return slot;
        }

        private static VisualElement EmptySlot()
        {
            var slot = new VisualElement();
            slot.AddToClassList("tp-slot");
            slot.AddToClassList("tp-slot--empty");
            slot.Add(new DashedFrame { LineColor = EmptySlotDash, LineWidth = 1.5f, Radius = 10f });
            var row = new VisualElement();
            row.AddToClassList("tp-slot__empty");
            row.Add(Text("空き ― 一覧の", "tp-slot__empty-t"));
            var star = new LineIcon(LineIcon.Kind.Star);
            star.AddToClassList("tp-slot__empty-star");
            row.Add(star);
            row.Add(Text("で指名", "tp-slot__empty-t"));
            slot.Add(row);
            return slot;
        }

        // ── 絞り込み ───────────────────────────────
        private void BuildFilters(TrainingPlanView v)
        {
            var benchHost = _root.Q<VisualElement>("bench-filters");
            if (benchHost != null)
            {
                benchHost.Clear();
                foreach (var (label, mode) in BenchFilters)
                {
                    var m = mode; // 捕捉
                    benchHost.Add(SegItem(label, v.BenchFilter == mode, () => { _state.SetBenchFilter(m); Render(); }));
                }
            }

            var yearHost = _root.Q<VisualElement>("year-filters");
            if (yearHost != null)
            {
                yearHost.Clear();
                foreach (var (label, grade) in YearFilters)
                {
                    var g = grade; // 捕捉
                    yearHost.Add(SegItem(label, v.YearFilter == grade, () => { _state.SetYearFilter(g); Render(); }));
                }
            }
        }

        private static VisualElement SegItem(string text, bool on, System.Action onClick)
        {
            var item = new Label(text);
            item.AddToClassList("tp-seg__item");
            if (on) { item.AddToClassList("tp-seg__item--on"); item.AddToClassList("f-body-bd"); }
            item.RegisterCallback<ClickEvent>(_ => onClick());
            return item;
        }

        // ── 部員別 練習割当 ─────────────────────────
        private void BuildRows(TrainingPlanView v)
        {
            var rows = _root.Q<VisualElement>("train-rows");
            if (rows == null) return;
            rows.Clear();
            foreach (var r in v.Rows) rows.Add(BuildRow(r, v.Budget));
        }

        private VisualElement BuildRow(PlayerTrainRow r, int budget)
        {
            var idx = r.Index; // 捕捉
            var row = new VisualElement();
            row.AddToClassList("trow");

            // 指導：星のボタン（押すたびに指名⇄解除, issue #707）
            var nomCell = Cell("tp-col--nom");
            var star = IconButton(r.Nominated ? LineIcon.Kind.StarFilled : LineIcon.Kind.Star, "tp-star", () => Nominate(idx));
            if (r.Nominated) star.AddToClassList("tp-star--on");
            star.tooltip = r.Nominated ? "個別指導から外す" : "個別指導に指名";
            nomCell.Add(star);
            row.Add(nomCell);

            // 選手：背番号の箱・名前・学年と投打（ベンチ外は背番号「—」）
            var idCell = Cell("tp-col--id");
            idCell.AddToClassList("tp-cell--id");
            idCell.Add(NumBox(r.NumText, "tp-num--row"));
            var name = Text(r.Name, "trow__name f-display");
            name.RegisterCallback<ClickEvent>(_ => OpenModal(idx));
            idCell.Add(name);
            idCell.Add(Text(r.GradeLabel + " " + r.HandLabel, "trow__meta"));
            row.Add(idCell);

            // 能力：打・走・守・投
            var rankCell = Cell("tp-col--rank");
            rankCell.Add(RankPairs(r.Strength, "tp-chip22"));
            row.Add(rankCell);

            // 練習メニュー：型の名前｜練習名と分数を書いた横棒（押すと配分のポップアップ）
            var menuCell = Cell("tp-col--menu");
            menuCell.Add(Text(r.PresetJp, "tp-menu__type f-body-bd"));
            var barHost = new VisualElement();
            barHost.AddToClassList("tp-menu__bar");
            barHost.Add(AllocationBar(r.Chips, budget, true));
            menuCell.Add(barHost);
            menuCell.RegisterCallback<ClickEvent>(_ => OpenModal(idx));
            row.Add(menuCell);

            // 変更 ›
            var goCell = Cell("tp-col--go");
            var go = new Button(() => OpenModal(idx));
            go.text = "";
            go.AddToClassList("tp-go");
            go.Add(Text("変更", "tp-go__t f-body-bd"));
            go.Add(new LineIcon(LineIcon.Kind.ChevronRight));
            goCell.Add(go);
            row.Add(goCell);

            // 複製（コピー元を選んでこの選手へ写す, Issue #133-⑤）
            var copyCell = Cell("tp-col--copy");
            var copy = IconButton(LineIcon.Kind.Copy, "tp-copy", () => OpenCopyPicker(idx));
            copy.tooltip = "ほかの選手から複製";
            copyCell.Add(copy);
            row.Add(copyCell);
            return row;
        }

        // ── 練習メニューの配分（ポップアップ） ─────────────
        private void BuildModal(TrainingPlanView v)
        {
            if (_modal != null) _modal.style.display = _modalOpen ? DisplayStyle.Flex : DisplayStyle.None;
            if (!_modalOpen) return;

            // 選手の見出し（背番号・名前・学年/投打＋カテゴリ別ランク。投/野ポジションは出さない）
            SetText("sel-num", v.SelNumText);
            SetText("sel-name", v.SelectedName);
            SetText("sel-meta", v.SelYear + "・" + v.SelHand);
            var cats = _root.Q<VisualElement>("sel-cats");
            if (cats != null) { cats.Clear(); cats.Add(RankPairs(v.SelStrength, "tp-chip20")); }

            var nom = _root.Q<Button>("sel-nom");
            if (nom != null)
            {
                nom.text = "";
                nom.Clear();
                nom.Add(new LineIcon(v.SelNominated ? LineIcon.Kind.StarFilled : LineIcon.Kind.Star));
                nom.Add(Text(v.SelNominated ? "個別指導 指名中" : "個別指導に指名",
                    v.SelNominated ? "tpm-nom__t f-body-bd" : "tpm-nom__t"));
                nom.EnableInClassList("tpm-nom--on", v.SelNominated);
            }

            var delToggle = _root.Q<VisualElement>("delegate-toggle");
            if (delToggle != null) delToggle.EnableInClassList("switch--on", v.DelegateOn);
            SetText("delegate-state", v.DelegateStateLabel);

            // 前後選手（issue #222⑥）：一覧の端では押せない。
            _root.Q<Button>("sel-prev")?.SetEnabled(v.SelHasPrev);
            _root.Q<Button>("sel-next")?.SetEnabled(v.SelHasNext);

            BuildPresetOptions(v);
            ShowIf("menu-lock-hint", v.DelegateOn);
            BuildBudget(v);
            BuildMenuRows(v);
            BuildGrowth(v);
            BuildTemplates(v);
            KeepOverlayFocus();
        }

        // ±や再構築でフォーカスを持つ要素が木から消える／無効化されると Esc がスクリムへ届かない。開いているオーバーレイへ戻す。
        private void KeepOverlayFocus()
        {
            var overlay = _copyOpen ? _copyModal : _modalOpen ? _modal : null;
            if (overlay == null) return;
            var focused = _root.panel?.focusController?.focusedElement as VisualElement;
            if (focused == null || !focused.enabledInHierarchy || !overlay.Contains(focused)) overlay.Focus();
        }

        private void ShowIf(string name, bool on)
        {
            var el = _root.Q<VisualElement>(name);
            if (el != null) el.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // メニューの型（7つ）。説明はツールチップ。委任中は選べない。
        private void BuildPresetOptions(TrainingPlanView v)
        {
            var host = _root.Q<VisualElement>("tpm-presets");
            if (host == null) return;
            host.Clear();
            var sel = v.SelectedIndex;
            foreach (var po in v.PresetOptions)
            {
                var preset = po.Preset; // 捕捉
                var chip = new Label(po.Jp);
                chip.AddToClassList("tpm-preset");
                if (po.Selected) { chip.AddToClassList("tpm-preset--on"); chip.AddToClassList("f-body-bd"); }
                if (v.DelegateOn) chip.AddToClassList("tpm-preset--locked");
                chip.tooltip = po.Desc;
                chip.RegisterCallback<ClickEvent>(_ => { _state.SetPreset(sel, preset); Render(); });
                host.Add(chip);
            }
        }

        // 1日の配分：合計（満たないときは色を変えて「残り N分」）と、使っている練習の色の棒（文字なし・余りは地の色）。
        private void BuildBudget(TrainingPlanView v)
        {
            SetText("sel-total", v.SelectedTotal.ToString());
            SetText("sel-budget", "/ " + v.Budget + "分");
            var short_ = v.SelectedRemaining > 0;
            _root.Q<Label>("sel-total")?.EnableInClassList("tpm-budget__total--short", short_);
            SetText("sel-remain", "残り " + v.SelectedRemaining + "分");
            ShowIf("sel-remain", short_);
            var host = _root.Q<VisualElement>("sel-alloc-bar");
            if (host != null) { host.Clear(); host.Add(AllocationBar(v.SelectedChips, v.Budget, false)); }
            SetText("sum-fielder", v.SelFielderMinutes.ToString());
            SetText("sum-pitcher", v.SelPitcherMinutes.ToString());
            SetText("sum-defense", v.SelDefenseMinutes.ToString());
        }

        // 練習の行は値が変わっても作り直さない（作り直すと掴んだポインタ／長押しの予約先が消える・issue #1278）。
        // 構成（選手・委任・予算）が前回と同じなら既存の行の値だけ更新する。
        private string _menuSig;
        private readonly List<MenuRowView> _menuRows = new List<MenuRowView>();

        private void BuildMenuRows(TrainingPlanView v)
        {
            var fielderHost = _root.Q<VisualElement>("tpm-fielder");
            var pitcherHost = _root.Q<VisualElement>("tpm-pitcher");
            var defenseHost = _root.Q<VisualElement>("tpm-defense");
            if (fielderHost == null || pitcherHost == null || defenseHost == null) return;
            var sel = v.SelectedIndex;
            var full = Mathf.Max(TrainingPresets.StepMinutes,
                Mathf.RoundToInt(v.Budget * BarFullShareOfBudget / TrainingPresets.StepMinutes) * TrainingPresets.StepMinutes);

            var sig = sel + "|" + v.DelegateOn + "|" + v.Budget + "|" + fielderHost.childCount + "|" + defenseHost.childCount;
            var count = v.SelectedSlots.Count + v.DefenseSlots.Count;
            if (sig == _menuSig && _menuRows.Count == count)
            {
                var i = 0;
                foreach (var s in v.SelectedSlots) _menuRows[i++].Update(s.Minutes, full, v.AtBudgetLimit);
                foreach (var d in v.DefenseSlots) _menuRows[i++].Update(d.Minutes, full, v.AtBudgetLimit);
                return;
            }

            fielderHost.Clear(); pitcherHost.Clear(); defenseHost.Clear();
            _menuRows.Clear();
            foreach (var s in v.SelectedSlots)
            {
                var menu = s.Menu; // 捕捉
                var row = new MenuRowView(
                    s.Icon, s.MenuJp, null, null, s.MainEffectJp.Length > 0 ? "主効果 " + s.MainEffectJp : null,
                    "tm-bg--" + UiComponents.TrainingMenuColorClass(menu), UiComponents.TrainingMenuColorClass(menu),
                    !s.Pitcher, s.Minutes, full, v.DelegateOn, v.AtBudgetLimit,
                    () => { _state.AdjustMinutes(sel, menu, -1); Render(); },
                    () => { _state.AdjustMinutes(sel, menu, +1); Render(); },
                    m => { _state.SetMinutes(sel, menu, m); Render(); });
                _menuRows.Add(row);
                (s.Pitcher ? pitcherHost : fielderHost).Add(row);
            }
            foreach (var d in v.DefenseSlots)
            {
                var menu = d.Menu; // 捕捉
                var row = new MenuRowView(
                    d.Label, d.FullJp, d.Grade, d.Aptitude.ToString(), null,
                    "tm-bg--defpos", "defpos",
                    false, d.Minutes, full, v.DelegateOn, v.AtBudgetLimit,
                    () => { _state.AdjustMinutes(sel, menu, -1); Render(); },
                    () => { _state.AdjustMinutes(sel, menu, +1); Render(); },
                    m => { _state.SetMinutes(sel, menu, m); Render(); });
                _menuRows.Add(row);
                defenseHost.Add(row);
            }
            _menuSig = sel + "|" + v.DelegateOn + "|" + v.Budget + "|" + fielderHost.childCount + "|" + defenseHost.childCount;
        }

        /// <summary>
        /// 練習の1行：記号｜項目名｜(適性)｜棒（ドラッグで分数を変える）｜分数｜−＋。
        /// 0分の行は薄く、使っている行は項目名を太字に。−は0分で、＋は合計が1日の分数に達したら押せない色。
        /// </summary>
        private sealed class MenuRowView : VisualElement
        {
            private readonly UiComponents.TimeAllocSliderElement _slider;
            private readonly Label _name;
            private readonly Label _minutes;
            private readonly Button _minus;
            private readonly Button _plus;
            private readonly bool _locked;
            private bool _minusOn, _plusOn;

            public MenuRowView(
                string icon, string name, string aptGrade, string aptValue, string tip,
                string iconClass, string sliderColorClass, bool tall,
                int minutes, int fullMinutes, bool locked, bool atLimit,
                System.Action onMinus, System.Action onPlus, System.Action<int> onSet)
            {
                _locked = locked;
                AddToClassList("mrow");
                if (tall) AddToClassList("mrow--tall");
                if (!string.IsNullOrEmpty(tip)) tooltip = tip;

                var ic = new Label(icon);
                ic.AddToClassList("mrow__icon");
                ic.AddToClassList("f-body-bd");
                ic.AddToClassList(iconClass);
                Add(ic);

                _name = new Label(name);
                _name.AddToClassList("mrow__name");
                Add(_name);

                if (aptGrade != null)
                {
                    var apt = new VisualElement();
                    apt.AddToClassList("mrow__apt");
                    var chip = UiComponents.RankChipLegacy(aptGrade);
                    chip.AddToClassList("tp-chip20");
                    apt.Add(chip);
                    var val = new Label(aptValue);
                    val.AddToClassList("mrow__apt-v");
                    val.AddToClassList("f-num");
                    apt.Add(val);
                    Add(apt);
                }

                var barHost = new VisualElement();
                barHost.AddToClassList("mrow__bar");
                _slider = UiComponents.TimeAllocSlider(minutes, fullMinutes, TrainingPresets.StepMinutes, sliderColorClass, locked, onSet);
                barHost.Add(_slider);
                Add(barHost);

                var min = new VisualElement();
                min.AddToClassList("mrow__min");
                _minutes = new Label();
                _minutes.AddToClassList("mrow__min-v");
                _minutes.AddToClassList("f-num-bd");
                min.Add(_minutes);
                var unit = new Label("分");
                unit.AddToClassList("mrow__min-u");
                min.Add(unit);
                Add(min);

                var step = new VisualElement();
                step.AddToClassList("mrow__step");
                _minus = StepButton("−", () => { if (_minusOn) onMinus(); });
                _plus = StepButton("＋", () => { if (_plusOn) onPlus(); });
                _plus.AddToClassList("mrow__btn--plus");
                step.Add(_minus);
                step.Add(_plus);
                Add(step);

                Update(minutes, fullMinutes, atLimit);
            }

            public void Update(int minutes, int fullMinutes, bool atLimit)
            {
                _slider.SetValue(minutes, fullMinutes);
                _minutes.text = minutes.ToString();
                var zero = minutes <= 0;
                EnableInClassList("mrow--zero", zero);
                _name.EnableInClassList("f-body-bd", !zero);
                _minusOn = !_locked && !zero;
                _plusOn = !_locked && !atLimit;
                _minus.EnableInClassList("mrow__btn--off", !_minusOn);
                _plus.EnableInClassList("mrow__btn--full", !_plusOn);
            }

            // 長押しオートリピート（issue #222②）: 押しっぱなし時に一定間隔で再発火する。
            private static Button StepButton(string text, System.Action onClick)
            {
                var b = new Button { text = text };
                b.clickable = new Clickable(onClick, 400, 80);
                b.AddToClassList("mrow__btn");
                return b;
            }
        }

        // この配分で伸びる能力：野手11・投手5。今の能力値とランク＋伸びの棒（スクールカラー・伸びの量に比例）。
        private void BuildGrowth(TrainingPlanView v)
        {
            var fielder = _root.Q<VisualElement>("tpm-growth-fielder");
            var pitcher = _root.Q<VisualElement>("tpm-growth-pitcher");
            if (fielder == null || pitcher == null) return;
            fielder.Clear(); pitcher.Clear();
            foreach (var g in v.SelectedGrowth) (g.Pitcher ? pitcher : fielder).Add(GrowthRow(g));
        }

        private static VisualElement GrowthRow(GainBar g)
        {
            var row = new VisualElement();
            row.AddToClassList("grow");
            if (!g.Grows) row.AddToClassList("grow--still");
            row.Add(Text(g.AbilityJp, "grow__name"));

            if (g.Text.Length > 0)
            {
                // 数値のない能力（弾道＝タイプ・球速＝km/h）：ランク〜棒の列をまとめて文字で出す。
                row.Add(Text(g.Text, "grow__text"));
                if (g.Grows) row.Add(Text("伸びる", "grow__tag f-body-bd acc-bg"));
                return row;
            }

            var chip = UiComponents.RankChipLegacy(g.Grade);
            chip.AddToClassList("tp-chip20");
            chip.AddToClassList("grow__chip");
            row.Add(chip);
            row.Add(Text(g.Value.ToString(), "grow__val f-num"));
            var track = new VisualElement();
            track.AddToClassList("grow__track");
            if (g.Grows)
            {
                var fill = new VisualElement();
                fill.AddToClassList("grow__fill");
                fill.AddToClassList("acc-bg");
                fill.style.width = Length.Percent((float)g.Fraction * 100f);
                track.Add(fill);
            }
            row.Add(track);
            return row;
        }

        // 週テンプレート：選択欄（今の配分と同じ中身のテンプレートの名前）＋上に開く一覧（押すと適用・×で削除）。
        private void BuildTemplates(TrainingPlanView v)
        {
            var none = v.Templates.Count == 0;
            var label = _root.Q<Label>("tpl-current");
            if (label != null)
            {
                label.text = none ? "保存なし" : v.ActiveTemplate.Length > 0 ? v.ActiveTemplate : "未選択";
                label.EnableInClassList("tpm-tplsel__label--none", v.ActiveTemplate.Length == 0);
            }
            _root.Q<VisualElement>("tpl-select")?.EnableInClassList("tpm-tplsel--locked", none);
            if (none) _tplMenuOpen = false;

            var menu = _root.Q<VisualElement>("tpl-menu");
            if (menu == null) return;
            menu.style.display = _tplMenuOpen ? DisplayStyle.Flex : DisplayStyle.None;
            menu.Clear();
            if (!_tplMenuOpen) return;
            menu.BringToFront();
            var sel = v.SelectedIndex;
            foreach (var name in v.Templates)
            {
                var n = name; // 捕捉
                var item = new VisualElement();
                item.AddToClassList("tpm-tplmenu__item");
                if (n == v.ActiveTemplate) item.AddToClassList("tpm-tplmenu__item--on");
                var lbl = Text(n, n == v.ActiveTemplate ? "tpm-tplmenu__name f-body-bd" : "tpm-tplmenu__name");
                item.Add(lbl);
                if (!v.DelegateOn)
                    item.RegisterCallback<ClickEvent>(_ =>
                    {
                        _state.ApplyTemplate(sel, n);
                        _tplMenuOpen = false;
                        Toast(n + " を適用しました");
                        Render();
                    });
                var del = IconButton(LineIcon.Kind.Close, "tpm-tplmenu__del", () => { _state.RemoveTemplate(n); Render(); });
                del.tooltip = "削除";
                del.RegisterCallback<ClickEvent>(e => e.StopPropagation());
                item.Add(del);
                menu.Add(item);
            }
        }

        // ── 複製ピッカー（コピー元を選ぶ・Issue #133-⑤） ─────────
        // 対象（コピー先）は _copyTarget。一覧のフィルタに関わらず全部員から選べる（v.CopyRows・学年順）。
        private void BuildCopyModal(TrainingPlanView v)
        {
            if (_copyModal != null) _copyModal.style.display = _copyOpen ? DisplayStyle.Flex : DisplayStyle.None;
            if (!_copyOpen) return;

            var targetName = "";
            foreach (var r in v.CopyRows) if (r.Index == _copyTarget) { targetName = r.Name; break; }
            SetText("copy-name", targetName);

            var host = _root.Q<VisualElement>("copy-list");
            if (host == null) return;
            host.Clear();
            foreach (var r in v.CopyRows)
            {
                if (r.Index == _copyTarget) continue;   // 自分自身は選べない
                var src = r.Index; var srcName = r.Name; // 捕捉
                var row = new VisualElement();
                row.AddToClassList("tpc-row");
                row.Add(NumBox(r.NumText, "tp-num--row"));

                var body = new VisualElement(); body.AddToClassList("tpc-row__body");
                body.Add(Text(r.Name, "tpc-row__name f-display"));
                body.Add(Text(r.GradeLabel + " " + r.HandLabel, "tpc-row__meta"));
                row.Add(body);

                row.Add(Text(r.PresetJp, "tpc-row__preset f-body-bd"));
                var bar = new VisualElement(); bar.AddToClassList("tpc-row__bar");
                bar.Add(AllocationBar(r.Chips, v.Budget, true));   // どんな配分かを見て選べる（一覧と同じ横棒）
                row.Add(bar);

                row.RegisterCallback<ClickEvent>(_ =>
                {
                    _state.CopyPlanFrom(_copyTarget, src);
                    _copyOpen = false;
                    Toast(srcName + " の計画を複製しました");
                    Render();
                });
                host.Add(row);
            }
        }

        // ── 部品ファクトリ ─────────────────────────
        // 練習の配分の横棒（部品辞書 LabeledStackBar）。長さは分に比例し、1日の分数に満たないぶんは塗らない。
        private static VisualElement AllocationBar(List<MenuSlot> chips, int budget, bool showText)
        {
            var segs = new List<UiComponents.LabeledStackSegment>();
            foreach (var c in chips)
                segs.Add(new UiComponents.LabeledStackSegment
                {
                    ColorClass = "tm-bg--" + (c.AltShade ? "defpos-alt" : UiComponents.TrainingMenuColorClass(c.Menu)),
                    Weight = c.Minutes,
                    Name = c.ShortJp,
                    Count = c.Minutes.ToString(),
                    Tooltip = c.MenuJp + " " + c.Minutes + "分",
                });
            return UiComponents.LabeledStackBar(segs, budget, showText);
        }

        // 「打・走・守・投」の見出しとランクのチップの組（チップの大きさはクラスで渡す）。
        private static VisualElement RankPairs(PlayerStrength s, string chipClass)
        {
            var box = new VisualElement();
            box.AddToClassList("tp-ranks");
            AddRankPair(box, "打", s.BattingTier, chipClass);
            AddRankPair(box, "走", s.MobilityTier, chipClass);
            AddRankPair(box, "守", s.DefenseTier, chipClass);
            AddRankPair(box, "投", s.PitchingTier, chipClass);
            return box;
        }

        private static void AddRankPair(VisualElement box, string label, Tier tier, string chipClass)
        {
            var pair = new VisualElement();
            pair.AddToClassList("tp-ranks__pair");
            pair.Add(Text(label, "tp-ranks__label"));
            var chip = UiComponents.RankChipLegacy(tier.ToString());
            chip.AddToClassList(chipClass);
            pair.Add(chip);
            box.Add(pair);
        }

        private static Label NumBox(string text, string extraClass)
        {
            var num = new Label(text);
            num.AddToClassList("tp-num");
            num.AddToClassList("f-num-bd");
            if (!string.IsNullOrEmpty(extraClass)) num.AddToClassList(extraClass);
            return num;
        }

        private static VisualElement Cell(string colClass)
        {
            var c = new VisualElement();
            c.AddToClassList("tp-cell");
            c.AddToClassList(colClass);
            return c;
        }

        private static Button IconButton(LineIcon.Kind kind, string cls, System.Action onClick)
        {
            var b = new Button(onClick) { text = "" };
            b.AddToClassList(cls);
            b.Add(new LineIcon(kind));
            return b;
        }

        private static Label Text(string text, string classes)
        {
            var t = new Label(text);
            foreach (var c in classes.Split(' ')) if (!string.IsNullOrEmpty(c)) t.AddToClassList(c);
            return t;
        }

        // ── ユーティリティ ─────────────────────────
        private IVisualElementScheduledItem _toastJob;

        private void Toast(string msg)
        {
            var toast = _root.Q<Label>("toast");
            if (toast == null) return;
            toast.text = msg;
            toast.style.display = DisplayStyle.Flex;
            toast.BringToFront();   // モーダルの暗幕より手前へ（モーダル操作で出すトーストも読める）
            _toastJob?.Pause();     // 続けて出したとき前の予約が新しいトーストを消さない
            _toastJob = toast.schedule.Execute(() => { toast.style.display = DisplayStyle.None; });
            _toastJob.ExecuteLater(1600);
        }

        private void Click(string name, System.Action action)
        {
            var btn = _root.Q<Button>(name);
            if (btn != null) { btn.clicked += action; return; }
            var el = _root.Q<VisualElement>(name);
            if (el != null) el.RegisterCallback<ClickEvent>(_ => action());
        }

        private void SetText(string name, string text)
        {
            var label = _root.Q<Label>(name);
            if (label != null) { label.text = text; return; }
            var btn = _root.Q<Button>(name);
            if (btn != null) btn.text = text;
        }
    }
}
