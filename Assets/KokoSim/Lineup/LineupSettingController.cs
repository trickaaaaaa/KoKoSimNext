using System.Collections.Generic;
using KokoSim.Engine.Match.Field;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Lineup
{
    /// <summary>
    /// 試合前スタメン設定のコントローラ。UIDocument（sourceAsset = LineupSetting.uxml）へ LineupSettingState を
    /// バインドし、打順1〜9・守備位置・DH・先発・控えの編集と、選んだ2選手の能力比較＋守備適性＋通算/今大会成績を
    /// 描画・操作する。見た目は部品辞書（components.uss）＋トークンのみ。ロジックは State/エンジンに委ねる。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class LineupSettingController : MonoBehaviour
    {
        // 守備適性ミニダイヤ（比較パネル内）のノード配置（top%, left%）。メンバー設定と同一。
        private static readonly Dictionary<int, (float Top, float Left)> AptPos = new()
        {
            { 1, (50f, 50f) }, { 2, (86f, 50f) }, { 3, (60f, 87f) }, { 4, (40f, 69f) }, { 5, (60f, 13f) },
            { 6, (40f, 31f) }, { 7, (17f, 13f) }, { 8, (12f, 50f) }, { 9, (17f, 87f) },
        };

        private LineupSettingState _state;
        private VisualElement _root;
        private VisualElement _posMenu;   // 守備位置ドロップダウン（浮遊）
        private int _posMenuSlot = -1;

        private void OnEnable()
        {
            _state = new LineupSettingState();
            _root = GetComponent<UIDocument>().rootVisualElement;

            _posMenu = new VisualElement();
            _posMenu.AddToClassList("pos-menu");
            _posMenu.style.display = DisplayStyle.None;
            _root.Add(_posMenu);
            // 盤面クリックでメニューを閉じる（メニュー自身のクリックは伝播停止で除外）。
            _root.RegisterCallback<ClickEvent>(_ => HidePosMenu());

            Click("lu-ok", OnOk);
            Click("lu-cancel", OnCancel);
            var sw = _root.Q<VisualElement>("lu-dh-switch");
            if (sw != null) sw.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); _state.ToggleDh(); Render(); });

            Render();
        }

        private void Render()
        {
            HidePosMenu();
            var v = _state.BuildView();

            // 画面専用ヘッダーに「この試合」の文脈を出す（共通ナビは非表示）。大会外表示（テスト等）は空。
            var gs = KokoSim.Unity.Shell.GameSession.Current;
            var runner = gs.Runner;
            var match = "";
            if (runner != null)
            {
                match = gs.Title;
                if (!runner.Finished && runner.NextOpponent != null)
                    match += "　" + runner.RoundName + "　vs " + runner.NextOpponent.Name;
            }
            SetText("lu-match", match);

            var rank = _root.Q<VisualElement>("lu-team-rank");
            if (rank != null) { rank.Clear(); rank.Add(RankChip(v.TeamRankGrade)); }

            BuildRows(v);
            BuildDhBar(v);
            BuildBench(v);
            BuildCompare(v);
        }

        // ── 打順テーブル ──
        private void BuildRows(LineupSettingView v)
        {
            var host = _root.Q<VisualElement>("lu-rows");
            if (host == null) return;
            host.Clear();
            foreach (var r in v.Rows)
            {
                var row = new VisualElement();
                row.AddToClassList("lineup-row");
                if (r.IsPicked) row.AddToClassList("lineup-row--picked");

                var ord = new Label(r.Order.ToString());
                ord.AddToClassList("lineup-row__ord");
                row.Add(ord);

                var posCell = new VisualElement();
                posCell.AddToClassList("lineup-row__pos");
                posCell.Add(PosChip(r));
                row.Add(posCell);

                if (r.IsCaptain) row.Add(CaptainMark());
                var name = new Label(r.Name);
                name.AddToClassList("lineup-row__name");
                row.Add(name);

                // 選手名と「学年・投打」の間に通算の簡易成績（控えめグレー・名前より目立たせない）。
                var stat = new Label(r.StatText);
                stat.AddToClassList("lineup-row__stat");
                row.Add(stat);

                var info = new Label(r.GradeLabel + "  " + r.HandLabel);
                info.AddToClassList("lineup-row__info");
                row.Add(info);

                row.Add(RankChip(r.OverallGrade));

                var slot = r.Order - 1;
                var pidx = r.PlayerIndex;
                row.RegisterCallback<ClickEvent>(_ => { _state.ClickRow(slot); Render(); });
                row.RegisterCallback<PointerEnterEvent>(_ => { _state.SetHovered(pidx); RenderCompareOnly(); });
                host.Add(row);
            }
        }

        private VisualElement PosChip(LineupRowView r)
        {
            var chip = new Label(r.PosKanji);
            chip.AddToClassList("pos-chip");
            if (r.IsDhSlot) chip.AddToClassList("pos-chip--dh");
            else if (r.IsPitcherSlot) chip.AddToClassList("pos-chip--fixed");
            if (r.PosEditable)
            {
                var slot = r.Order - 1;
                chip.RegisterCallback<ClickEvent>(evt => { evt.StopPropagation(); TogglePosMenu(slot, chip); });
            }
            return chip;
        }

        // ── 守備位置ドロップダウン ──
        private void TogglePosMenu(int slot, VisualElement anchor)
        {
            if (_posMenuSlot == slot && _posMenu.style.display == DisplayStyle.Flex) { HidePosMenu(); return; }
            _posMenu.Clear();
            _posMenuSlot = slot;
            foreach (var pos in LineupSettingState.FielderPositions)
            {
                var item = new Label(LineupSettingState.PosLabel(pos));
                item.AddToClassList("pos-menu__item");
                var p = pos;
                item.RegisterCallback<ClickEvent>(evt => { evt.StopPropagation(); _state.SetSlotPosition(slot, p); Render(); });
                _posMenu.Add(item);
            }
            _posMenu.style.display = DisplayStyle.Flex;
            _posMenu.BringToFront();
            _posMenu.schedule.Execute(() =>
            {
                var wb = anchor.worldBound;
                var local = _root.WorldToLocal(new Vector2(wb.xMin, wb.yMax + 2f));
                _posMenu.style.left = local.x;
                _posMenu.style.top = local.y;
            });
        }

        private void HidePosMenu()
        {
            if (_posMenu == null) return;
            _posMenu.style.display = DisplayStyle.None;
            _posMenuSlot = -1;
        }

        // ── DH制トグル＋先発投手 ──
        private void BuildDhBar(LineupSettingView v)
        {
            var sw = _root.Q<VisualElement>("lu-dh-switch");
            if (sw != null) sw.EnableInClassList("switch--on", v.UsesDh);

            var sp = _root.Q<VisualElement>("lu-sp");
            if (sp == null) return;
            sp.Clear();
            if (!v.UsesDh) return;   // DH制のときだけ先発投手ピルを出す

            var pill = new VisualElement();
            pill.AddToClassList("sp-pill");
            if (v.StartingPitcherPicked) pill.AddToClassList("sp-pill--picked");
            var cap = new Label("先発");
            cap.AddToClassList("sp-pill__cap");
            pill.Add(cap);
            var name = new Label(v.StartingPitcherName);
            name.AddToClassList("sp-pill__name");
            pill.Add(name);
            pill.Add(RankChip(v.StartingPitcherGrade));
            pill.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); _state.ClickStartingPitcher(); Render(); });
            sp.Add(pill);
        }

        // ── 控え ──
        private void BuildBench(LineupSettingView v)
        {
            var host = _root.Q<VisualElement>("lu-bench");
            if (host == null) return;
            host.Clear();
            foreach (var b in v.Bench)
            {
                var row = new VisualElement();
                row.AddToClassList("bench-row");
                if (b.IsPicked) row.AddToClassList("bench-row--picked");

                var name = new Label(b.Name);
                name.AddToClassList("bench-row__name");
                row.Add(name);
                var tag = new Label(b.IsPitcher ? "投" : "野");
                tag.AddToClassList("bench-row__tag");
                row.Add(tag);
                row.Add(RankChip(b.OverallGrade));

                var idx = b.Index;
                row.RegisterCallback<ClickEvent>(_ => { _state.ClickBench(idx); Render(); });
                row.RegisterCallback<PointerEnterEvent>(_ => { _state.SetHovered(idx); RenderCompareOnly(); });
                host.Add(row);
            }
        }

        // ── 2選手比較＋成績 ──
        private void BuildCompare(LineupSettingView v)
        {
            FillCompareCard("lu-cmp-a", true, v.CardA);
            FillCompareCard("lu-cmp-b", false, v.CardB);

            var tabs = _root.Q<VisualElement>("lu-cmp-tabs");
            if (tabs != null)
            {
                tabs.Clear();
                for (var i = 0; i < v.TabLabels.Length; i++)
                {
                    var t = new Label(v.TabLabels[i]);
                    t.AddToClassList("lu-tab");
                    if (v.Tab == i) t.AddToClassList("lu-tab--on");
                    var tab = i;
                    t.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); _state.SetTab(tab); Render(); });
                    tabs.Add(t);
                }
            }

            var rows = _root.Q<VisualElement>("lu-cmp-rows");
            if (rows != null)
            {
                rows.Clear();
                if (v.Rows2.Count == 0)
                {
                    var empty = new Label("選手をクリックで選択 → 別の選手にカーソルを合わせて比較");
                    empty.AddToClassList("lu-cmp-empty");
                    rows.Add(empty);
                }
                else
                {
                    var group = new Label(v.TabLabels[v.Tab]);
                    group.AddToClassList("lu-cmp-group");
                    rows.Add(group);
                    foreach (var r in v.Rows2) rows.Add(CompareRowEl(r));
                }
            }

            BuildApt(v);
            BuildStats(v);
        }

        private void BuildApt(LineupSettingView v)
        {
            var host = _root.Q<VisualElement>("lu-cmp-apt");
            if (host == null) return;
            host.Clear();
            if (!v.ShowApt) return;

            var title = new Label("守備適性");
            title.AddToClassList("lu-cmp-group");
            host.Add(title);

            var pair = new VisualElement();
            pair.AddToClassList("apt-pair");
            pair.Add(AptField("apt-field--a", v.HasAptA, v.AptA));
            pair.Add(AptField("apt-field--b", v.HasAptB, v.AptB));
            host.Add(pair);
        }

        // 通算/今大会成績（選んだ選手ごとに1枚＝2列）。
        private void BuildStats(LineupSettingView v)
        {
            var host = _root.Q<VisualElement>("lu-cmp-stats");
            if (host == null) return;
            host.Clear();
            if (!v.CardA.Present && !v.CardB.Present) return;

            var title = new Label("成績");
            title.AddToClassList("lu-cmp-group");
            host.Add(title);
            if (v.CardA.Present) host.Add(StatCard(v.CardA));
            if (v.CardB.Present) host.Add(StatCard(v.CardB));
        }

        private VisualElement StatCard(CompareCardView card)
        {
            var wrap = new VisualElement();
            var name = new Label(card.Name);
            name.AddToClassList("lu-stat-name");
            wrap.Add(name);

            var cols = new VisualElement();
            cols.AddToClassList("stat-cols");
            for (var i = 0; i < card.Stats.Count; i++)
            {
                var col = new VisualElement();
                col.AddToClassList("stat-col");
                col.AddToClassList(i == 0 ? "stat-col--a" : "stat-col--b");
                var t = new Label(card.Stats[i].Title);
                t.AddToClassList("stat-col__title");
                col.Add(t);
                foreach (var it in card.Stats[i].Items)
                {
                    var sr = new VisualElement();
                    sr.AddToClassList("stat-row");
                    var lab = new Label(it.Label);
                    lab.AddToClassList("stat-row__lab");
                    var val = new Label(it.Value);
                    val.AddToClassList("stat-row__val");
                    sr.Add(lab);
                    sr.Add(val);
                    col.Add(sr);
                }
                cols.Add(col);
            }
            wrap.Add(cols);
            return wrap;
        }

        private VisualElement AptField(string accentMod, bool has, List<string> grades)
        {
            var field = new VisualElement();
            field.AddToClassList("apt-field");
            field.AddToClassList(accentMod);
            field.Add(NewChild("apt-field__infield"));
            field.Add(NewChild("apt-field__home"));
            var slots = new VisualElement();
            slots.AddToClassList("apt-field__slots");
            for (var i = 0; i < 9; i++)
            {
                var el = new VisualElement();
                el.AddToClassList("apt-node");
                var pos = AptPos[i + 1];
                el.style.top = Length.Percent(pos.Top);
                el.style.left = Length.Percent(pos.Left);
                el.Add(has ? AptRank(grades[i]) : AptDash());
                slots.Add(el);
            }
            field.Add(slots);
            return field;
        }

        private void FillCompareCard(string host, bool isA, CompareCardView card)
        {
            var el = _root.Q<VisualElement>(host);
            if (el == null) return;
            el.Clear();
            if (!card.Present)
            {
                var empty = new Label(isA ? "選手をクリック" : "選手にカーソル");
                empty.AddToClassList("cmp-card__empty");
                el.Add(empty);
                return;
            }
            var name = new Label(card.Name);
            name.AddToClassList("cmp-card__name");
            el.Add(name);

            var meta = new VisualElement();
            meta.AddToClassList("cmp-card__meta");
            meta.Add(GradeLabel(card.GradeLabel));
            meta.Add(RankChip(card.OverallGrade));
            if (card.IsCaptain)
            {
                var cap = new Label("主将");
                cap.AddToClassList("chip");
                cap.AddToClassList("chip--tag");
                cap.AddToClassList("chip--out");
                meta.Add(cap);
            }
            el.Add(meta);
        }

        private VisualElement CompareRowEl(CompareRowView r)
        {
            var row = new VisualElement();
            row.AddToClassList("cmp-row");

            row.Add(ValueLabel(r.HasA ? r.ValueA.ToString() : "—", "cmp-row__val--l", r.HasA && r.Winner == -1));
            var sideL = new VisualElement();
            sideL.AddToClassList("cmp-row__side");
            sideL.AddToClassList("cmp-row__side--l");
            if (r.HasA) sideL.Add(Bar("cmp-row__bar--a", r.ValueA));
            row.Add(sideL);

            var lab = new Label(r.Label);
            lab.AddToClassList("cmp-row__lab");
            row.Add(lab);

            var sideR = new VisualElement();
            sideR.AddToClassList("cmp-row__side");
            sideR.AddToClassList("cmp-row__side--r");
            if (r.HasB) sideR.Add(Bar("cmp-row__bar--b", r.ValueB));
            row.Add(sideR);
            row.Add(ValueLabel(r.HasB ? r.ValueB.ToString() : "—", "cmp-row__val--r", r.HasB && r.Winner == 1));

            return row;
        }

        // ── 確定操作 ──
        private void OnOk()
        {
            KokoSim.Unity.Shell.GameSession.Current.Lineup = _state.ToLineupSpec();
            // AwaitingMatchStart は立てたまま → ホーム復帰で自校戦を消化する。
            KokoSim.Unity.Shell.ScreenRouter.Instance?.Show("HomeDashboard");
        }

        private void OnCancel()
        {
            KokoSim.Unity.Shell.GameSession.Current.AwaitingMatchStart = false;   // 試合は消化しない
            KokoSim.Unity.Shell.ScreenRouter.Instance?.Show("HomeDashboard");
        }

        private void RenderCompareOnly() => BuildCompare(_state.BuildView());

        // ── 部品ヘルパ ──
        private static Label CaptainMark()
        {
            var l = new Label("主");
            l.style.fontSize = 11;
            l.style.color = new Color(0.961f, 0.776f, 0.290f); // --color-lamp
            l.style.unityFontStyleAndWeight = FontStyle.Bold;
            l.style.marginRight = 3;
            return l;
        }

        private static Label GradeLabel(string text)
        {
            var l = new Label(text);
            l.style.fontSize = 11;
            l.style.color = new Color(0.60f, 0.65f, 0.63f);
            l.style.marginRight = 5;
            return l;
        }

        private static Label RankChip(string grade)
        {
            var c = new Label(grade);
            c.AddToClassList("rank-chip");
            c.AddToClassList("rank-chip--" + grade);
            return c;
        }

        private static Label AptRank(string grade)
        {
            var c = new Label(grade);
            c.AddToClassList("rank-chip");
            c.AddToClassList("rank-chip--" + grade);
            return c;
        }

        private static Label AptDash()
        {
            var d = new Label("—");
            d.AddToClassList("apt-node__dash");
            return d;
        }

        private static VisualElement NewChild(string cls)
        {
            var el = new VisualElement();
            el.AddToClassList(cls);
            return el;
        }

        private static Label ValueLabel(string text, string sideMod, bool win)
        {
            var l = new Label(text);
            l.AddToClassList("cmp-row__val");
            l.AddToClassList(sideMod);
            if (win) l.AddToClassList("cmp-row__val--win");
            return l;
        }

        private static VisualElement Bar(string colorMod, int value)
        {
            var bar = new VisualElement();
            bar.AddToClassList("cmp-row__bar");
            bar.AddToClassList(colorMod);
            bar.style.width = Length.Percent(Mathf.Clamp(value, 0, 100));
            return bar;
        }

        private void SetText(string name, string text)
        {
            var label = _root.Q<Label>(name);
            if (label != null) label.text = text;
        }

        private void Click(string name, System.Action action)
        {
            var btn = _root.Q<Button>(name);
            if (btn != null) btn.clicked += action;
        }
    }
}
