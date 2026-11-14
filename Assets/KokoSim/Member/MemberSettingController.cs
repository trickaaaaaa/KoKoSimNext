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

        private const float ChipRise = 2f;   // 立ち位置点からチップ下端までの持ち上げ[px]（点はチップの下端に接する・issue #732）

        private MemberSettingState _state;
        private VisualElement _root;
        private Button _advanceBtn;
        private VisualElement _fieldHost;
        private BaseballFieldElement _field;
        private FieldMarkersElement _markers;   // 立ち位置点＋引き出し線
        private VisualElement _popover;         // ホバー/選択時のフルカード
        private int _popoverSlot = -1;          // 表示中フルカードの背番号（-1=非表示）
        private Color _accent = KokoSim.Unity.Shell.SchoolAccent.Default;   // 比較表の自チーム側の色（オーダー画面と同じ）

        private void OnEnable()
        {
            _state = new MemberSettingState();
            _accent = KokoSim.Unity.Shell.SchoolAccent.ForManager();
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

            // 共通トップバーの「今週を進める」を配線（issue #134: 以前は未配線で死にボタンだった）。
            // ScreenRouter が SetActive で付け外しするだけなので、OnDisable で必ず外す（多重登録＝多重進週の防止）。
            _advanceBtn = _root.Q<Button>("advance");
            if (_advanceBtn != null) _advanceBtn.clicked += OnAdvance;

            Render();
        }

        private void OnDisable()
        {
            if (_advanceBtn != null) _advanceBtn.clicked -= OnAdvance;
        }

        // 全タブ共通の進週処理へ集約（大会モード中はホームへ回送して日送りへ引き継ぐ）。
        private void OnAdvance() => KokoSim.Unity.Shell.WeekAdvance.FromSideScreen(Render);

        private void Render()
        {
            var v = _state.BuildView();

            KokoSim.Unity.Components.ScoreboardStrip.Fill(_root);
            KokoSim.Unity.Shell.TopBarMeters.Fill(_root);   // 部費残高・名声・信頼度（ManagerService 単一ソース）
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
                // 引き出し線は点がチップの幅の外へ出たときだけ（下端の真下にあるうちは点だけ＝線が文字を横切らない。issue #732）。
                var offset = Mathf.Abs(it.Cx - it.Dot.x) > it.W * 0.5f - 4f;
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

        // 枠カードの共通中身（2行）：①背番号＋名前(中央) ②学年・投打＋解除（背番号10〜20は総合ランク非表示, issue #220）。
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

        // 球場図の常設チップは2段（issue #732 案A）: 上段＝背番号＋ランク、下段＝名前（フル）。学年/投打/×はポップオーバーへ。
        private void FillChip(VisualElement el, SlotView slot)
        {
            var top = new VisualElement();
            top.AddToClassList("field-chip__top");
            var badge = new Label(slot.Number.ToString());
            badge.AddToClassList("field-chip__num");
            badge.AddToClassList("f-num");
            top.Add(badge);
            el.Add(top);

            if (slot.PlayerIndex >= 0)
            {
                top.Add(UiComponents.RankChip(slot.RankGrade));
                var name = new Label(slot.Name);
                name.AddToClassList("field-chip__name");
                el.Add(name);
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
            // チップ上端の少し上。チップの高さは実寸で取る（2段チップ＝issue #732 で 22px 固定ではなくなった）。
            var chipH = ChipHeightOf(_popoverSlot);
            var top = Mathf.Max(2f, dot.y - ChipRise - chipH - ph - 4f);
            _popover.style.left = left;
            _popover.style.top = top;
        }

        private float ChipHeightOf(int number)
        {
            var host = _root.Q<VisualElement>("ms-field-slots");
            if (host != null)
                foreach (var child in host.Children())
                    if (child.userData is int n && n == number && !float.IsNaN(child.layout.height) && child.layout.height > 0f)
                        return child.layout.height;
            return 46f;
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
                body.Add(meta);
                chip.Add(body);

                var idx = p.Index;
                chip.RegisterCallback<ClickEvent>(_ => { _state.ClickPool(idx); Render(); });
                chip.RegisterCallback<PointerEnterEvent>(_ => { _state.SetHovered(idx); RenderCompareOnly(); });
                chip.RegisterCallback<PointerLeaveEvent>(_ => { _state.ClearHovered(idx); RenderCompareOnly(); });
                host.Add(chip);
            }
        }

        // ── 2選手比較（共通部品 CompareTable＝オーダー画面と同じ表。操作説明の文字は出さない） ──
        private void BuildCompare(MemberSettingView v)
        {
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

            var head = _root.Q<VisualElement>("ms-cmp-head");
            if (head != null)
            {
                head.Clear();
                head.Add(CompareTable.Head(HeadOf(v.CardA), HeadOf(v.CardB)));
            }

            var rows = _root.Q<VisualElement>("ms-cmp-rows");
            if (rows != null)
            {
                rows.Clear();
                foreach (var r in v.Rows) rows.Add(CompareTable.Row(r));
            }

            BuildApt(v);

            // 持ち球のミニ球種変化チャート（投手能力タブのときだけ・タブでゲートする, Issue #93/#293）。
            var chart = _root.Q<VisualElement>("ms-cmp-chart");
            if (chart != null)
            {
                chart.Clear();
                if (v.ShowPitchChart && (v.CardA.Present || v.CardB.Present))
                    chart.Add(CompareTable.PitchCharts(v.CardA.Present, v.CardA.Pitches, v.CardB.Present, v.CardB.Pitches));
            }

            KokoSim.Unity.Shell.SchoolAccent.Apply(_root.Q<VisualElement>(className: "ms-compare"), _accent);
        }

        private static CompareTable.PlayerHeadData HeadOf(CompareCard card) => new CompareTable.PlayerHeadData
        {
            Present = card.Present,
            Name = card.Name,
            Meta = card.GradeLabel + "\u3000" + card.RoleText,
            Strength = card.Strength,
            Skills = card.Skills,
        };

        // ── 守備適性（野手能力タブ内・9マスの帯。行見出しは姓） ──
        private void BuildApt(MemberSettingView v)
        {
            var host = _root.Q<VisualElement>("ms-cmp-apt");
            if (host == null) return;
            host.Clear();
            host.EnableInClassList("ms-hidden", !v.ShowApt);
            if (!v.ShowApt) return;
            host.Add(UiComponents.AptitudeStrip(
                v.HasAptA ? v.AptA : null, v.HasAptB ? v.AptB : null,
                v.CardA.Present ? Surname(v.CardA.Name) : "", v.CardB.Present ? Surname(v.CardB.Name) : "",
                tight: true));
        }

        private static string Surname(string name) => KokoSim.Engine.Season.PlayerNameGenerator.Surname(name);

        // ── 部品ヘルパ ──

        private static Label NumBadge(int n)
        {
            var b = new Label(n.ToString());
            b.AddToClassList("num-badge");
            b.AddToClassList("f-num");
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
