using KokoSim.Engine.Season;
using UnityEngine;
using UnityEngine.UIElements;
using KokoSim.Unity.Components; // 部品辞書（RankChip / AbilityRow）

namespace KokoSim.Unity.Training
{
    /// <summary>
    /// 練習計画のコントローラ（設計書06 §3.3）。UIDocument（sourceAsset = TrainingPlan.uxml）へ
    /// TrainingPlanState（ViewModel）をバインドする。共通トップバー・合宿バナー・個別指導3枠・
    /// 部員別テーブル（背番号/総合/練習設定/複製・全幅）を動的生成し、設定編集は統合モーダル
    /// （プリセット選択＋カスタム±30分＋守備適性＋伸びバー）で行う。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class TrainingPlanController : MonoBehaviour
    {
        private static readonly (string Label, int Grade)[] YearFilters =
            { ("全学年", 0), ("1年", 1), ("2年", 2), ("3年", 3) };
        private static readonly (string Label, int Mode)[] BenchFilters =
            { ("全員", 0), ("ベンチ入り", 1), ("ベンチ外", 2) };
        private static readonly string[] SortLabels = { "学年順", "総合" };

        private TrainingPlanState _state;
        private VisualElement _root;
        private VisualElement _modal;
        private VisualElement _copyModal;
        private bool _modalOpen;
        private bool _copyOpen;     // 複製ピッカー（コピー元を選ぶ, Issue #133-⑤）
        private int _copyTarget;    // コピー先の選手索引

        private void OnEnable()
        {
            _state = new TrainingPlanState();
            _root = GetComponent<UIDocument>().rootVisualElement;
            _modal = _root.Q<VisualElement>("tp-modal");
            _copyModal = _root.Q<VisualElement>("tp-copy-modal");
            WireStaticButtons();
            WireOverlayClose(_modal, () => _modalOpen, CloseModal);
            WireOverlayClose(_copyModal, () => _copyOpen, CloseCopyPicker);
            Render();
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

        private void WireStaticButtons()
        {
            // 共通トップバーの「今週を進める」で週送り（合宿・成長段階プレビューが連動）。
            // 全タブ共通の進週処理へ集約（issue #134: 大会モード中はホームへ回送して日送りへ引き継ぐ）。
            Click("advance", () => KokoSim.Unity.Shell.WeekAdvance.FromSideScreen(Render));
            // モーダル内の操作（対象は選択選手）。押下後も Render で開いたまま最新化する。
            Click("template-save", () => { _state.SaveTemplate(); Toast("週テンプレを保存しました"); Render(); });
            Click("delegate-toggle", () => { _state.ToggleDelegate(_state.SelectedIndex); Render(); });
            Click("sel-nom", () => { _state.ToggleNominate(_state.SelectedIndex); Render(); });
            // 前後選手切替（issue #222⑥・一覧の現在フィルタ/ソート順）。
            Click("sel-prev", () => { _state.StepSelected(-1); Render(); _modal?.Focus(); });
            Click("sel-next", () => { _state.StepSelected(+1); Render(); _modal?.Focus(); });
            Click("modal-revert", () => { _state.RevertSelected(); Toast("配分を元に戻しました"); Render(); });
            Click("modal-close", CloseModal);
            Click("modal-done", CloseModal);
            Click("copy-close", CloseCopyPicker);
        }

        private void OpenCopyPicker(int target)
        {
            _copyTarget = target;
            _copyOpen = true;
            Render();
            _copyModal?.BringToFront();
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
            _modalOpen = true;
            Render();
            _modal?.BringToFront();
            _modal?.Focus();
        }

        private void CloseModal()
        {
            _modalOpen = false;
            Render();
        }

        private void Render()
        {
            var v = _state.BuildView();

            // 共通トップバー（スコアボード）: 掲示板の升目（週・夏予選までの残り）とチーム総合力ランクを埋める。
            KokoSim.Unity.Components.ScoreboardStrip.Fill(_root);
            KokoSim.Unity.Shell.TopBarMeters.Fill(_root);   // 部費残高・名声・信頼度（ManagerService 単一ソース）
            var rank = _root.Q<VisualElement>("team-rank");
            if (rank != null) { rank.Clear(); rank.Add(UiComponents.RankChip(v.TeamRankGrade)); }

            SetText("roster-title", "部員別 練習割当");
            SetText("roster-count", "/ " + v.RosterCount + "名");

            BuildCampBanner(v);
            BuildNominations(v);
            BuildFilters(v);
            BuildRows(v);
            BuildModal(v);
            BuildCopyModal(v);
        }

        // ── 合宿バナー ──────────────────────────────
        private void BuildCampBanner(TrainingPlanView v)
        {
            var banner = _root.Q<VisualElement>("camp-banner");
            if (banner != null) banner.style.display = v.CampActive ? DisplayStyle.Flex : DisplayStyle.None;
            SetText("camp-title", v.CampTitle);
            SetText("camp-mult", v.CampMult + " 経験値");
            SetText("camp-note", v.CampNote);
        }

        // ── 個別指導3枠 ─────────────────────────────
        private void BuildNominations(TrainingPlanView v)
        {
            var host = _root.Q<VisualElement>("nom-slots");
            if (host == null) return;
            host.Clear();
            for (var s = 0; s < v.Nominations.Count; s++)
            {
                var slot = new VisualElement();
                slot.AddToClassList("nom-slot");
                if (v.Nominations[s] == "空き") slot.AddToClassList("nom-slot--empty");

                var num = new Label((s + 1).ToString());
                num.AddToClassList("nom-slot__num");
                slot.Add(num);

                var name = new Label(v.Nominations[s]);
                name.AddToClassList("nom-slot__name");
                slot.Add(name);

                host.Add(slot);
            }
        }

        // ── フィルタ／ソート ─────────────────────────
        private void BuildFilters(TrainingPlanView v)
        {
            var yearHost = _root.Q<VisualElement>("year-filters");
            if (yearHost != null)
            {
                yearHost.Clear();
                foreach (var (label, grade) in YearFilters)
                {
                    var g = grade; // 捕捉
                    var chip = MakeChip(label, v.YearFilter == grade);
                    chip.RegisterCallback<ClickEvent>(_ => { _state.SetYearFilter(g); Render(); });
                    yearHost.Add(chip);
                }
            }

            // 在籍（全員/ベンチ入り/ベンチ外, Issue #133-④）。ベンチ判定は背番号1–20=入り・0=外。
            var benchHost = _root.Q<VisualElement>("bench-filters");
            if (benchHost != null)
            {
                benchHost.Clear();
                foreach (var (label, mode) in BenchFilters)
                {
                    var m = mode; // 捕捉
                    var chip = MakeChip(label, v.BenchFilter == mode);
                    chip.RegisterCallback<ClickEvent>(_ => { _state.SetBenchFilter(m); Render(); });
                    benchHost.Add(chip);
                }
            }

            var sortHost = _root.Q<VisualElement>("sort-modes");
            if (sortHost != null)
            {
                sortHost.Clear();
                for (var i = 0; i < SortLabels.Length; i++)
                {
                    var mode = i; // 捕捉
                    var chip = MakeChip(SortLabels[i], v.SortMode == i);
                    chip.RegisterCallback<ClickEvent>(_ => { _state.SetSortMode(mode); Render(); });
                    sortHost.Add(chip);
                }
            }
        }

        // ── 部員別テーブル（全幅・すっきり） ─────────────
        private void BuildRows(TrainingPlanView v)
        {
            var rows = _root.Q<VisualElement>("train-rows");
            if (rows == null) return;
            rows.Clear();
            foreach (var r in v.Rows)
            {
                var idx = r.Index; // 捕捉
                var row = new VisualElement();
                row.AddToClassList("trow");
                row.AddToClassList("row-panel");   // 共通部品：白パネル＋インク枠＋ベタ影（components.uss）
                if (r.Selected) row.AddToClassList("row-panel--selected");

                // 背番号／選手／学年（投/野ポジションは非表示）
                var idCell = new VisualElement();
                idCell.AddToClassList("tcell"); idCell.AddToClassList("tcell--id");
                var num = new Label(r.NumText);
                num.AddToClassList("num-badge"); num.AddToClassList("trow__num");
                if (r.BenchOut) num.AddToClassList("num-badge--out");
                idCell.Add(num);
                var idText = new VisualElement();
                idText.AddToClassList("trow__idtext");
                var nameLbl = new Label(r.Name);
                nameLbl.AddToClassList("trow__name");
                nameLbl.RegisterCallback<ClickEvent>(_ => OpenModal(idx));
                idText.Add(nameLbl);
                var meta = new VisualElement();
                meta.AddToClassList("trow__meta");
                meta.Add(Tag(r.GradeLabel, "chip chip--tag"));
                meta.Add(Tag(r.HandLabel, "chip chip--tag"));   // 投打（Issue #133-②）
                if (r.BenchOut) meta.Add(Tag("ベンチ外", "chip chip--tag chip--out"));
                // カテゴリ別ランク（打/走/守/投・総合ランクは廃止, Issue #133-①。部品辞書コンパクト版）。
                meta.Add(UiComponents.CategoryRankChipsCompact(r.Strength));
                idText.Add(meta);
                idCell.Add(idText);
                row.Add(idCell);

                // 練習設定（プリセット名＋主眼メニュー｜配分の積み上げ横バー｜変更▸・クリックでモーダル）
                var presetCell = new VisualElement();
                presetCell.AddToClassList("tcell"); presetCell.AddToClassList("tcell--preset");
                var open = new VisualElement();
                open.AddToClassList("preset-open");
                var texts = new VisualElement(); texts.AddToClassList("preset-open__texts");
                var pl = new Label(r.PresetJp); pl.AddToClassList("preset-open__label");
                if (r.PresetJp == "カスタム") pl.AddToClassList("preset-open__label--custom");
                texts.Add(pl);
                var focus = new Label(r.FocusSummary); focus.AddToClassList("preset-open__focus"); texts.Add(focus);
                open.Add(texts);
                open.Add(AllocationBar(r.Chips, v.Budget));   // 空き帯にメニュー別色の積み上げバー（Issue #133-③）
                var go = new Label("変更 ▸"); go.AddToClassList("preset-open__go"); open.Add(go);
                open.RegisterCallback<ClickEvent>(_ => OpenModal(idx));
                presetCell.Add(open);
                row.Add(presetCell);

                // 複製（コピー元を選んでこの選手へ写す, Issue #133-⑤。直上固定「↑」は廃止）
                var copyCell = new VisualElement();
                copyCell.AddToClassList("tcell"); copyCell.AddToClassList("tcell--copy");
                var copy = new Label("複");
                copy.AddToClassList("copy-btn");
                copy.RegisterCallback<ClickEvent>(_ => OpenCopyPicker(idx));
                copyCell.Add(copy);
                row.Add(copyCell);

                rows.Add(row);
            }
        }

        // ── 統合モーダル ────────────────────────────
        private void BuildModal(TrainingPlanView v)
        {
            if (_modal != null) _modal.style.display = _modalOpen ? DisplayStyle.Flex : DisplayStyle.None;
            if (!_modalOpen) return;

            // ヘッダー（背番号・名前・学年/利き手＋カテゴリ別ランク。総合ランクは廃止・投/野ポジションは出さない）
            SetText("sel-num", v.SelNumText);
            SetText("sel-name", v.SelectedName);
            SetText("sel-meta", v.SelYear + "・" + v.SelHand);
            var cats = _root.Q<VisualElement>("sel-cats");
            if (cats != null) { cats.Clear(); cats.Add(UiComponents.CategoryRankChipsCompact(v.SelStrength)); }
            var benchTag = _root.Q<Label>("sel-benchout");
            if (benchTag != null) benchTag.style.display = v.SelBenchOut ? DisplayStyle.Flex : DisplayStyle.None;

            var nom = _root.Q<Button>("sel-nom");
            if (nom != null) { nom.text = "★ " + v.SelNomLabel; nom.EnableInClassList("nom-btn--on", v.SelNominated); }

            var delToggle = _root.Q<VisualElement>("delegate-toggle");
            if (delToggle != null) delToggle.EnableInClassList("switch--on", v.DelegateOn);
            SetText("delegate-state", v.DelegateStateLabel);
            var lockHint = _root.Q<Label>("menu-lock-hint");
            if (lockHint != null) lockHint.style.display = v.DelegateOn ? DisplayStyle.Flex : DisplayStyle.None;

            // 前後選手（issue #222⑥）：一覧の端では押せない（見た目でも分かる）。
            var prevBtn = _root.Q<Button>("sel-prev");
            if (prevBtn != null) prevBtn.SetEnabled(v.SelHasPrev);
            var nextBtn = _root.Q<Button>("sel-next");
            if (nextBtn != null) nextBtn.SetEnabled(v.SelHasNext);

            BuildPresetOptions(v);
            BuildMenuList(v);
            BuildDefenseGrid(v);
            BuildGrowth(v);

            // 週バジェット残り＋バー（残り0分は強調して「なぜ増えないか」を視界内で分かるようにする, issue #222③）
            SetText("sel-remain", "残り " + v.SelectedRemaining + " / " + v.Budget + "分");
            var remainLabel = _root.Q<Label>("sel-remain");
            remainLabel?.EnableInClassList("tp-ed-bar__remain--limit", v.AtBudgetLimit);
            var budgetFill = _root.Q<VisualElement>("sel-budget-fill");
            if (budgetFill != null)
            {
                var pct = v.Budget > 0 ? Mathf.Clamp01((float)v.SelectedTotal / v.Budget) * 100f : 0f;
                budgetFill.style.width = Length.Percent(pct);
            }

            BuildTemplates(v);
        }

        // プリセット帯（issue #222④）：横1行のチップ列。説明はツールチップで持たせ、
        // カード内の「伸びる能力」5本バーは廃止（右カラムの実効ドライランが同じ役割を実データで担う）。
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
                chip.AddToClassList("tpm-preset-chip");
                if (po.Selected) chip.AddToClassList("tpm-preset-chip--on");
                chip.tooltip = po.Desc;
                chip.RegisterCallback<ClickEvent>(_ => { _state.SetPreset(sel, preset); Render(); });
                host.Add(chip);
            }
        }

        private void BuildMenuList(TrainingPlanView v)
        {
            var host = _root.Q<VisualElement>("tpm-menus");
            if (host == null) return;
            host.Clear();
            var sel = v.SelectedIndex;
            foreach (var s in v.SelectedSlots)
            {
                // 打撃系/投手系の繋ぎ目に節見出し（Issue #133-⑥。既存の節見出しスタイルを流用）。
                if (s.SectionJp != "")
                {
                    var head = new Label(s.SectionJp);
                    head.AddToClassList("tpm-menugroup");
                    host.Add(head);
                }

                var menu = s.Menu; // 捕捉
                var maxForSlot = s.Minutes + v.SelectedRemaining;
                host.Add(MenuRow(
                    s.Icon, s.MenuJp, null, "主効果 " + s.MainEffectJp,
                    s.Minutes, maxForSlot, UiComponents.TrainingMenuColorClass(menu),
                    v.DelegateOn, v.AtBudgetLimit,
                    () => { _state.AdjustMinutes(sel, menu, -1); Render(); },
                    () => { _state.AdjustMinutes(sel, menu, +1); Render(); },
                    m => { _state.SetMinutes(sel, menu, m); Render(); }));
            }
        }

        // 守備適性も攻撃メニューと同じ粒度（mslot 行）で縦に並べる（1画面3カラム化, issue #222①）。
        private void BuildDefenseGrid(TrainingPlanView v)
        {
            var host = _root.Q<VisualElement>("tpm-defense");
            if (host == null) return;
            host.Clear();
            var sel = v.SelectedIndex;
            foreach (var d in v.DefenseSlots)
            {
                var menu = d.Menu; // 捕捉
                var maxForSlot = d.Minutes + v.SelectedRemaining;
                host.Add(MenuRow(
                    d.Label, d.Label + "守備", "適" + d.Aptitude, null,
                    d.Minutes, maxForSlot, "defpos",
                    v.DelegateOn, v.AtBudgetLimit,
                    () => { _state.AdjustMinutes(sel, menu, -1); Render(); },
                    () => { _state.AdjustMinutes(sel, menu, +1); Render(); },
                    m => { _state.SetMinutes(sel, menu, m); Render(); }));
            }
        }

        // 練習メニュー1行（issue #222①②③）: アイコン／名前／(任意)適性値／ドラッグバー／分／±。
        // 攻撃メニューと守備ポジションで共用（36px基準の1行に圧縮・行高32px基準の趣旨に沿う）。
        private static VisualElement MenuRow(
            string icon, string name, string meta, string tip,
            int minutes, int maxMinutes, string colorClass,
            bool locked, bool atLimit,
            System.Action onMinus, System.Action onPlus, System.Action<int> onSet)
        {
            var row = new VisualElement();
            row.AddToClassList("mslot");
            if (locked) row.AddToClassList("mslot--locked");
            if (!string.IsNullOrEmpty(tip)) row.tooltip = tip;

            var ic = new Label(icon); ic.AddToClassList("mslot__icon"); row.Add(ic);
            var nm = new Label(name); nm.AddToClassList("mslot__name"); row.Add(nm);

            if (!string.IsNullOrEmpty(meta))
            {
                // 「適50」等の和文＋数字混植（Oswaldは和文グリフを持たないため単一Labelへf-numを付けない）。
                var mt = new Label(meta); mt.AddToClassList("mslot__meta"); row.Add(mt);
            }

            var slider = UiComponents.TimeAllocSlider(minutes, maxMinutes, TrainingPresets.StepMinutes, colorClass, locked, onSet);
            slider.AddToClassList("mslot__slider");
            row.Add(slider);

            row.Add(UiComponents.NumUnitAuto(minutes + "分", false, "mslot__min"));

            row.Add(StepButton("−", onMinus, locked));
            row.Add(StepButton("＋", onPlus, locked || atLimit));

            return row;
        }

        // この設定で伸びる能力（実効ドライラン）。
        private void BuildGrowth(TrainingPlanView v)
        {
            var host = _root.Q<VisualElement>("tpm-growth");
            if (host == null) return;
            host.Clear();
            if (v.SelectedGrowth.Count == 0)
            {
                var none = new Label("休養のみ（この設定では伸びません）");
                none.AddToClassList("gain-none");
                host.Add(none);
                return;
            }
            foreach (var gb in v.SelectedGrowth) host.Add(AbilityBar(gb, true));
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

                var num = new Label(r.NumText);
                num.AddToClassList("num-badge");
                if (r.BenchOut) num.AddToClassList("num-badge--out");
                row.Add(num);

                var body = new VisualElement(); body.AddToClassList("tpc-row__body");
                var name = new Label(r.Name); name.AddToClassList("tpc-row__name"); body.Add(name);
                var meta = new Label(r.GradeLabel + "・" + r.HandLabel); meta.AddToClassList("tpc-row__meta"); body.Add(meta);
                row.Add(body);

                var plan = new VisualElement(); plan.AddToClassList("tpc-row__plan");
                var preset = new Label(r.PresetJp); preset.AddToClassList("tpc-row__preset"); plan.Add(preset);
                plan.Add(AllocationBar(r.Chips, v.Budget));   // どんな配分かを見て選べる（一覧と同じ積み上げバー）
                row.Add(plan);

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

        // 週テンプレの呼出＋削除（issue #222⑤）: チップクリック＝選択選手へ適用、×＝削除。
        private void BuildTemplates(TrainingPlanView v)
        {
            var host = _root.Q<VisualElement>("template-list");
            if (host == null) return;
            host.Clear();
            if (v.Templates.Count == 0)
            {
                var none = new Label("保存なし");
                none.AddToClassList("tpl-chip"); none.AddToClassList("tpl-chip--none");
                host.Add(none);
                return;
            }
            var sel = v.SelectedIndex;
            foreach (var name in v.Templates)
            {
                var n = name; // 捕捉
                var chip = new VisualElement();
                chip.AddToClassList("tpl-chip");
                if (v.DelegateOn) chip.AddToClassList("tpl-chip--locked");

                var label = new Label(n);
                label.AddToClassList("tpl-chip__label");
                if (!v.DelegateOn)
                    label.RegisterCallback<ClickEvent>(_ =>
                    {
                        _state.ApplyTemplate(sel, n);
                        Toast(n + " を適用しました");
                        Render();
                    });
                chip.Add(label);

                var del = new Label("×");
                del.AddToClassList("tpl-chip__del");
                del.RegisterCallback<ClickEvent>(e =>
                {
                    e.StopPropagation();
                    _state.RemoveTemplate(n);
                    Render();
                });
                chip.Add(del);

                host.Add(chip);
            }
        }

        // ── 部品ファクトリ ─────────────────────────
        // 能力バー1行は部品辞書の AbilityRow に一本化（UI原則⑤）。
        // showLevel=true で「今週の成長」ラベル（＋N / ％ / —）を右端に付す。バーは今週の伸び、
        // ランクチップは**現在の能力値**を表す（相対強調バーは Grade 空でチップ無し）。
        private static VisualElement AbilityBar(GainBar gb, bool showLevel)
        {
            var grows = gb.LevelsGained > 0 || gb.Progress > 0.0001;
            return UiComponents.AbilityRow(new AbilityRowData
            {
                Icon = gb.Icon,
                Label = gb.AbilityJp,
                Pct = (float)gb.Progress,
                Grade = gb.Grade,
                Dim = showLevel && !grows,   // 伸びない能力は淡色（一覧には常に出す）
                Note = showLevel
                    ? (gb.LevelsGained > 0 ? "＋" + gb.LevelsGained
                        : grows ? Mathf.RoundToInt((float)gb.Progress * 100f) + "%" : "—")
                    : "",
                NoteMuted = !grows,
            });
        }

        // 練習配分の積み上げ横バー（Issue #133-③・部品辞書 StackedBar）。幅は Budget を分母に正規化し、
        // 休養は塗らない＝右側の余白として残す。色クラス対応（メニュー別）は部品辞書に集約。
        private static VisualElement AllocationBar(System.Collections.Generic.List<MenuSlot> chips, int budget)
        {
            var segs = new System.Collections.Generic.List<UiComponents.StackedBarSegment>();
            foreach (var c in chips)
                segs.Add(new UiComponents.StackedBarSegment
                {
                    StyleClass = UiComponents.TrainingMenuColorClass(c.Menu),
                    Fraction = budget > 0 ? (float)c.Minutes / budget : 0f,
                    Tooltip = c.MenuJp + " " + c.Minutes + "分",
                });
            var bar = UiComponents.StackedBar(segs);
            bar.AddToClassList("preset-open__bar");
            return bar;
        }

        private static Label Tag(string text, string classes)
        {
            var t = new Label(text);
            foreach (var c in classes.Split(' ')) if (!string.IsNullOrEmpty(c)) t.AddToClassList(c);
            return t;
        }

        private static VisualElement MakeChip(string text, bool on)
        {
            var c = new Label(text);
            c.AddToClassList("filter-chip");
            if (on) c.AddToClassList("filter-chip--on");
            return c;
        }

        // 長押しオートリピート（issue #222②）: UITK Clickable(action, delay, interval) で
        // 押しっぱなし時に一定間隔で onClick を再発火させる（既定Buttonの単発クリックだけでは連打が要る）。
        private static Button StepButton(string text, System.Action onClick, bool disabled)
        {
            var b = new Button { text = text };
            b.clickable = new Clickable(onClick, 400, 80);
            b.AddToClassList("step-btn");
            if (disabled) { b.SetEnabled(false); b.AddToClassList("step-btn--locked"); }
            return b;
        }

        // ── ユーティリティ ─────────────────────────
        private void Toast(string msg)
        {
            var toast = _root.Q<Label>("toast");
            if (toast == null) return;
            toast.text = msg;
            toast.style.display = DisplayStyle.Flex;
            toast.schedule.Execute(() => { if (toast != null) toast.style.display = DisplayStyle.None; }).ExecuteLater(1600);
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
