using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using KokoSim.Unity.Components; // 部品辞書（RankChip / AbilityRow）

namespace KokoSim.Unity.Member
{
    /// <summary>
    /// メンバー設定のコントローラ（設計書06 §3.3b）。UIDocument（sourceAsset = MemberSetting.uxml）へ
    /// MemberSettingState をバインドし、背番号1〜20の割当と2選手比較を描画・操作する。
    /// 見た目は部品辞書（components.uss）＋トークンのみ。ロジックは State/エンジンに委ねる。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class MemberSettingController : MonoBehaviour
    {
        // 背番号1〜9の標準守備位置＝球場座標[m]。検算済み定数 MemberFieldLayout（エンジン）を単一ソースに引く。
        private static bool TryDefPos(int n, out Vector2 m)
        {
            if (n < 1 || n > 9) { m = default; return false; }
            var p = KokoSim.Engine.Match.Field.MemberFieldLayout.DefensivePosition(n);
            m = new Vector2((float)p.X, (float)p.Y);
            return true;
        }

        // 守備適性ミニダイヤ（比較パネル内・抽象表示）のノード配置（top%, left%）。本図とは別系統。
        private static readonly Dictionary<int, (float Top, float Left)> AptPos = new()
        {
            { 1, (50f, 50f) }, { 2, (86f, 50f) }, { 3, (60f, 87f) }, { 4, (40f, 69f) }, { 5, (60f, 13f) },
            { 6, (40f, 31f) }, { 7, (17f, 13f) }, { 8, (12f, 50f) }, { 9, (17f, 87f) },
        };

        private const float ChipRise = 8f;   // 立ち位置点からチップ下端までの持ち上げ[px]

        private MemberSettingState _state;
        private VisualElement _root;
        private VisualElement _fieldHost;
        private BaseballFieldElement _field;
        private FieldMarkersElement _markers;   // 立ち位置点＋引き出し線
        private VisualElement _popover;         // ホバー/選択時のフルカード
        private int _popoverSlot = -1;          // 表示中フルカードの背番号（-1=非表示）

        private void OnEnable()
        {
            _state = new MemberSettingState();
            _root = GetComponent<UIDocument>().rootVisualElement;

            // 球場図（座標系ドリブンで芝/ファウル/ダート/ライン/ベース/フェンスを描く）をスロットの背面へ。
            _fieldHost = _root.Q<VisualElement>("ms-field");
            if (_fieldHost != null)
            {
                _field = new BaseballFieldElement();
                _fieldHost.Insert(0, _field);
                _markers = new FieldMarkersElement();
                _fieldHost.Insert(1, _markers);   // 芝の上・チップの下
                _popover = new VisualElement();
                _popover.AddToClassList("field-pop");
                _popover.pickingMode = PickingMode.Ignore;
                _popover.style.display = DisplayStyle.None;
                _fieldHost.Add(_popover);          // 最前面
                _fieldHost.RegisterCallback<GeometryChangedEvent>(_ => PositionFieldCards());
            }

            Click("ms-auto", () => { _state.AutoAssign(); Render(); });

            Render();
        }

        private void Render()
        {
            var v = _state.BuildView();

            SetText("week", KokoSim.Unity.Shell.GameClock.CurrentLabel());
            var rank = _root.Q<VisualElement>("team-rank");
            if (rank != null)
            {
                rank.Clear();
                rank.Add(UiComponents.RankChip(v.TeamRankGrade));
            }

            SetText("ms-pool-title",
                "部員　登録 " + v.AssignedCount + " / 20　・　ベンチ外 " + v.BenchOutCount);

            BuildField(v);
            BuildBench(v);
            BuildPool(v);
            BuildCompare(v);
        }

        // ── 背番号1〜9：守備位置（球場座標）に小型チップを置く ──
        private void BuildField(MemberSettingView v)
        {
            var host = _root.Q<VisualElement>("ms-field-slots");
            if (host == null) return;
            host.Clear();
            for (var n = 1; n <= 9; n++)
            {
                var slot = v.Slots[n - 1];
                var chip = new VisualElement();
                chip.AddToClassList("field-chip");
                if (slot.IsPicked) chip.AddToClassList("field-chip--picked");
                chip.userData = n;   // 配置（PlaceChip）で守備位置を引くための添字

                FillChip(chip, slot);
                WireChip(chip, slot);
                chip.RegisterCallback<GeometryChangedEvent>(_ => PositionFieldCards());
                host.Add(chip);
            }
            PositionFieldCards();
        }

        // 全チップを球場座標へ配置し、立ち位置点との重なりを回避（点は不動・チップだけ左右にオフセット）。
        private void PositionFieldCards()
        {
            if (_field == null || _markers == null) return;
            var host = _root.Q<VisualElement>("ms-field-slots");
            if (host == null) return;
            var fw = _field.contentRect.width;
            if (float.IsNaN(fw) || fw <= 0f) return;

            // 各チップの立ち位置点・目標中心X・実寸を集める。
            var items = new List<PlacedChip>();
            foreach (var child in host.Children())
            {
                if (!(child.userData is int n) || !TryDefPos(n, out var m)) continue;
                var w = child.layout.width;
                var h = child.layout.height;
                if (float.IsNaN(w) || float.IsNaN(h) || w <= 0f || h <= 0f) return; // 未確定→後で再試行
                var dot = _field.FieldToLocal(m);
                items.Add(new PlacedChip { El = child, Dot = dot, Cx = dot.x, W = w, H = h });
            }

            ResolveOverlaps(items, fw);

            // チップを配置（下端中央＝点の ChipRise px 上）し、点＆引き出し線をオーバーレイへ。
            var markers = new List<FieldMarkersElement.Marker>();
            foreach (var it in items)
            {
                var bottomY = it.Dot.y - ChipRise;
                it.El.style.left = it.Cx - it.W * 0.5f;
                it.El.style.top = bottomY - it.H;
                var offset = Mathf.Abs(it.Cx - it.Dot.x) > 1.5f;
                markers.Add(new FieldMarkersElement.Marker
                {
                    Dot = it.Dot,
                    ChipAnchor = new Vector2(it.Cx, bottomY),
                    Lead = offset,
                });
            }
            _markers.SetMarkers(markers);
            if (_popoverSlot >= 0) PositionPopover();
        }

        private sealed class PlacedChip
        {
            public VisualElement El;
            public Vector2 Dot;   // 立ち位置[px]（不動）
            public float Cx;      // チップ中心X（重なり回避で可変）
            public float W;
            public float H;
        }

        // 立ち位置点は動かさず、チップ中心Xだけ左右にずらして横方向の重なりを解く（縦が近いチップ同士）。
        private static void ResolveOverlaps(List<PlacedChip> items, float fieldWidth)
        {
            const float gap = 4f;
            items.Sort((a, b) => a.Cx.CompareTo(b.Cx));
            for (var pass = 0; pass < 4; pass++)
            {
                for (var i = 0; i < items.Count; i++)
                    for (var j = i + 1; j < items.Count; j++)
                    {
                        var a = items[i];
                        var b = items[j];
                        var vClose = Mathf.Abs(a.Dot.y - b.Dot.y) < (a.H + b.H) * 0.5f + 6f;
                        if (!vClose) continue;
                        var need = (a.W + b.W) * 0.5f + gap;
                        var dx = b.Cx - a.Cx;
                        if (Mathf.Abs(dx) >= need) continue;
                        var push = (need - Mathf.Abs(dx)) * 0.5f;
                        if (dx >= 0) { a.Cx -= push; b.Cx += push; }
                        else { a.Cx += push; b.Cx -= push; }
                    }
            }
            // 端はみ出しをクランプ（チップは常に画面内・点は不動なので引き出し線が伸びる）。余白は広めに取る。
            foreach (var it in items)
                it.Cx = Mathf.Clamp(it.Cx, it.W * 0.5f + 6f, fieldWidth - it.W * 0.5f - 6f);
        }

        // ── 背番号10〜20：控え横並び ──
        private void BuildBench(MemberSettingView v)
        {
            var host = _root.Q<VisualElement>("ms-bench");
            if (host == null) return;
            host.Clear();
            for (var n = 10; n <= 20; n++)
            {
                var slot = v.Slots[n - 1];
                var el = new VisualElement();
                el.AddToClassList("bench-slot");
                if (slot.IsPicked) el.AddToClassList("bench-slot--picked");

                FillCard(el, slot);
                WireSlot(el, slot);
                host.Add(el);
            }
        }

        // 控え/枠の配線（クリックで選択・配置、ホバーで比較の右へ）。球場チップは WireChip を使う。
        private void WireSlot(VisualElement el, SlotView slot)
        {
            var n = slot.Number;
            var pidx = slot.PlayerIndex;
            el.RegisterCallback<ClickEvent>(_ => { _state.ClickSlot(n); Render(); });
            el.RegisterCallback<PointerEnterEvent>(_ => { _state.SetHovered(pidx); RenderCompareOnly(); });
            el.RegisterCallback<PointerLeaveEvent>(_ => { _state.ClearHovered(pidx); RenderCompareOnly(); });
        }

        // 枠カードの共通中身（2行）：①背番号＋名前(中央)＋位置ランク ②学年・投打＋解除。
        private void FillCard(VisualElement el, SlotView slot)
        {
            var top = new VisualElement();
            top.AddToClassList("slot-card__top");
            top.Add(NumBadge(slot.Number));

            if (slot.PlayerIndex >= 0)
            {
                if (slot.IsCaptain) top.Add(CaptainMark());
                var name = new Label(slot.Name);
                name.AddToClassList("slot-card__name");
                top.Add(name);
                top.Add(UiComponents.RankChip(slot.RankGrade));
                el.Add(top);

                var sub = new VisualElement();
                sub.AddToClassList("slot-card__sub");
                var info = new Label(slot.GradeLabel + "  " + slot.HandLabel);
                info.AddToClassList("slot-card__sub-txt");
                sub.Add(info);
                var spacer = new VisualElement();
                spacer.AddToClassList("slot-card__spacer");
                sub.Add(spacer);
                sub.Add(ClearButton(slot.Number));
                el.Add(sub);
            }
            else
            {
                var empty = new Label("未割当");
                empty.AddToClassList("slot-card__empty");
                top.Add(empty);
                el.Add(top);
            }
        }

        // 球場図の常設は小型チップのみ（背番号＋姓＋ランク・高さ24px以下）。学年/投打/×はポップオーバーへ。
        private void FillChip(VisualElement el, SlotView slot)
        {
            var badge = new Label(slot.Number.ToString());
            badge.AddToClassList("field-chip__num");
            el.Add(badge);

            if (slot.PlayerIndex >= 0)
            {
                var name = new Label(Surname(slot.Name));
                name.AddToClassList("field-chip__name");
                el.Add(name);
                el.Add(UiComponents.RankChip(slot.RankGrade));
            }
            else
            {
                var empty = new Label("空");
                empty.AddToClassList("field-chip__empty");
                el.Add(empty);
            }
        }

        private void WireChip(VisualElement el, SlotView slot)
        {
            var n = slot.Number;
            var pidx = slot.PlayerIndex;
            el.RegisterCallback<ClickEvent>(_ => { _state.ClickSlot(n); Render(); });
            // ホバー：比較の右へ＋フルカードのポップオーバー表示。
            el.RegisterCallback<PointerEnterEvent>(_ =>
            {
                _state.SetHovered(pidx);
                ShowPopover(n);
                RenderCompareOnly();
            });
            el.RegisterCallback<PointerLeaveEvent>(_ =>
            {
                HidePopover(n);
                _state.ClearHovered(pidx);
                RenderCompareOnly();
            });
        }

        // 姓（フルネームの先頭トークン）。チップは省スペースなので姓のみ。フル名はポップオーバー。
        private static string Surname(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return fullName;
            var sp = fullName.IndexOf(' ');
            return sp > 0 ? fullName.Substring(0, sp) : fullName;
        }

        // ── フルカードのポップオーバー（ホバー/選択時のみ） ──
        private void ShowPopover(int number)
        {
            if (_popover == null || number < 1 || number > 9) return;
            var slot = _state.BuildView().Slots[number - 1];
            if (slot.PlayerIndex < 0) { HidePopover(number); return; }

            _popoverSlot = number;
            _popover.Clear();
            _popover.style.display = DisplayStyle.Flex;

            var top = new VisualElement();
            top.AddToClassList("slot-card__top");
            top.Add(NumBadge(slot.Number));
            if (slot.IsCaptain) top.Add(CaptainMark());
            var name = new Label(slot.Name);
            name.AddToClassList("slot-card__name");
            top.Add(name);
            top.Add(UiComponents.RankChip(slot.RankGrade));
            _popover.Add(top);

            var sub = new VisualElement();
            sub.AddToClassList("slot-card__sub");
            var info = new Label(slot.GradeLabel + "  " + slot.HandLabel);
            info.AddToClassList("slot-card__sub-txt");
            sub.Add(info);
            var spacer = new VisualElement();
            spacer.AddToClassList("slot-card__spacer");
            sub.Add(spacer);
            sub.Add(ClearButton(slot.Number));
            _popover.Add(sub);

            PositionPopover();
        }

        private void HidePopover(int number)
        {
            if (_popover == null || _popoverSlot != number) return;
            _popoverSlot = -1;
            _popover.style.display = DisplayStyle.None;
        }

        // ポップオーバーを対象チップの真上に置く（画面端はクランプ）。
        private void PositionPopover()
        {
            if (_popover == null || _popoverSlot < 0 || _field == null) return;
            if (!TryDefPos(_popoverSlot, out var m)) return;
            var dot = _field.FieldToLocal(m);
            var pw = _popover.layout.width;
            var ph = _popover.layout.height;
            if (float.IsNaN(pw) || pw <= 0f) { _popover.schedule.Execute(PositionPopover); return; }
            var fw = _field.contentRect.width;
            var left = Mathf.Clamp(dot.x - pw * 0.5f, 2f, fw - pw - 2f);
            var top = Mathf.Max(2f, dot.y - ChipRise - 22f - ph - 4f); // チップ上端の少し上
            _popover.style.left = left;
            _popover.style.top = top;
        }

        // カーソル移動での比較プレビューは比較パネルだけ更新（盤面は再構築しない＝ホバー対象がぶれない）。
        private void RenderCompareOnly() => BuildCompare(_state.BuildView());

        // ── プール ──
        private void BuildPool(MemberSettingView v)
        {
            var host = _root.Q<VisualElement>("ms-pool");
            if (host == null) return;
            host.Clear();
            foreach (var p in v.Pool)
            {
                var chip = new VisualElement();
                chip.AddToClassList("pool-chip");
                if (p.IsPicked) chip.AddToClassList("pool-chip--picked");

                var body = new VisualElement();
                body.AddToClassList("pool-chip__body");
                var name = new Label(p.Name);
                name.AddToClassList("pool-chip__name");
                body.Add(name);
                var meta = new VisualElement();
                meta.AddToClassList("pool-chip__meta");
                meta.Add(GradeLabel(p.GradeLabel));
                meta.Add(UiComponents.RankChip(p.OverallGrade));
                body.Add(meta);
                chip.Add(body);

                var idx = p.Index;
                chip.RegisterCallback<ClickEvent>(_ => { _state.ClickPool(idx); Render(); });
                chip.RegisterCallback<PointerEnterEvent>(_ => { _state.SetHovered(idx); RenderCompareOnly(); });
                chip.RegisterCallback<PointerLeaveEvent>(_ => { _state.ClearHovered(idx); RenderCompareOnly(); });
                host.Add(chip);
            }
        }

        // ── 2選手比較 ──
        private void BuildCompare(MemberSettingView v)
        {
            FillCompareCard("ms-cmp-a", true, v.CardA);
            FillCompareCard("ms-cmp-b", false, v.CardB);

            var tabs = _root.Q<VisualElement>("ms-cmp-tabs");
            if (tabs != null)
            {
                tabs.Clear();
                for (var i = 0; i < v.TabLabels.Length; i++)
                {
                    var t = new Label(v.TabLabels[i]);
                    t.AddToClassList("ms-tab");
                    if (v.Tab == i) t.AddToClassList("ms-tab--on");
                    var tab = i;
                    t.RegisterCallback<ClickEvent>(_ => { _state.SetTab(tab); Render(); });
                    tabs.Add(t);
                }
            }

            var rows = _root.Q<VisualElement>("ms-cmp-rows");
            if (rows == null) return;
            rows.Clear();
            if (v.Rows.Count == 0)
            {
                var empty = new Label("選手をクリックで選択 → 別の選手にカーソルを合わせて比較");
                empty.AddToClassList("ms-cmp-empty");
                rows.Add(empty);
            }
            else
            {
                var group = new Label(v.TabLabels[v.Tab]);
                group.AddToClassList("ms-cmp-group");
                rows.Add(group);
                foreach (var r in v.Rows) rows.Add(CompareRowEl(r));
            }

            BuildApt(v);
        }

        // ── 守備適性ダイヤ（打撃・走塁タブ内・選手ごとに1枚を横並び） ──
        private void BuildApt(MemberSettingView v)
        {
            var host = _root.Q<VisualElement>("ms-cmp-apt");
            if (host == null) return;
            host.Clear();
            if (!v.ShowApt) return;

            var title = new Label("守備適性");
            title.AddToClassList("ms-cmp-group");
            host.Add(title);

            var pair = new VisualElement();
            pair.AddToClassList("apt-pair");
            pair.Add(AptField("apt-field--a", v.HasAptA, v.AptA));
            pair.Add(AptField("apt-field--b", v.HasAptB, v.AptB));
            host.Add(pair);
        }

        // 1選手ぶんの守備適性ダイヤ。9守備位置にランクチップ（G〜S）を配置。未選択なら「—」を薄く。
        private VisualElement AptField(string accentMod, bool has, System.Collections.Generic.List<string> grades)
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
                el.Add(has ? UiComponents.RankChip(grades[i]) : AptDash());
                slots.Add(el);
            }
            field.Add(slots);
            return field;
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

        private void FillCompareCard(string host, bool isA, CompareCard card)
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
            meta.Add(UiComponents.RankChip(card.OverallGrade));
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

        private VisualElement CompareRowEl(CompareRow r)
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

        // ── 部品ヘルパ ──

        private static Label NumBadge(int n)
        {
            var b = new Label(n.ToString());
            b.AddToClassList("num-badge");
            return b;
        }

        // 主将マーカー（省スペースの漢字1文字・SDFフォント収録字）。名前の省略で消えない固定要素。
        private static Label CaptainMark()
        {
            var l = new Label("主");
            l.style.fontSize = 11;
            l.style.color = new Color(0.961f, 0.776f, 0.290f); // --color-lamp
            l.style.unityFontStyleAndWeight = FontStyle.Bold;
            l.style.marginLeft = 3;
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


        private Label ClearButton(int number)
        {
            var x = new Label("✕");
            x.AddToClassList("slot-card__clear");
            x.RegisterCallback<ClickEvent>(evt =>
            {
                evt.StopPropagation();
                _state.ClearSlot(number);
                Render();
            });
            return x;
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
