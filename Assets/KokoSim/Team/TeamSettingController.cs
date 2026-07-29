using System.Collections.Generic;
using KokoSim.Engine.Nation;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Squad
{
    /// <summary>
    /// チーム設定画面（設計書18 §3.5.1）。自校のユニフォーム配色を領域ごとに決める。
    ///
    /// 案B「領域別」＝プリセットから選ばせず、領域ごとに色を送る（自由度優先・2026-07-29 監督選定）。
    /// 選べる色は<b>パレット表に出てくる色だけ</b>（<see cref="TeamColorPalette.Swatches"/>）＝
    /// 自由に見えて破綻した配色は作れない。
    ///
    /// 表示専用のデータで試合結果には触れない。編集中は下書き（<see cref="_draft"/>）に置き、
    /// 「この配色にする」を押したときだけ <see cref="TeamColorProvider.ManagerOverride"/> へ確定する。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class TeamSettingController : MonoBehaviour
    {
        /// <summary>編集できる領域。順序はそのまま画面の行順。</summary>
        private enum Slot { Shirt, Pants, Undershirt, Socks, Cap, StripeOn, StripeColor }

        private static readonly (Slot Slot, string Label, bool GroupTop)[] Rows =
        {
            (Slot.Shirt, "ユニフォーム上", false),
            (Slot.Pants, "ズボン", false),
            (Slot.Undershirt, "アンダーシャツ", false),
            (Slot.Socks, "ソックス", false),
            // 帽子はクラウンとつばで1色。購入素体の帽子が1メッシュで塗り分けられないため（design-18 §3.5.1）
            (Slot.Cap, "帽子", false),
            (Slot.StripeOn, "縦縞", true),
            (Slot.StripeColor, "縞色", false),
        };

        /// <summary>縞の本数[本/m]。実物のピンストライプは間隔2.5cm＝40本/m（design-18 §3.5.3）。</summary>
        private const double StripeFreq = 40.0;

        private VisualElement _root;
        private VisualElement _rowHost;
        private UniformPreviewElement _preview;
        private Label _changed;

        private TeamColors _draft;
        private TeamColors _applied;

        private void OnEnable()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;
            _rowHost = _root.Q<VisualElement>("ts-rows");
            _changed = _root.Q<Label>("ts-changed");

            var previewHost = _root.Q<VisualElement>("ts-preview");
            if (previewHost != null)
            {
                _preview = new UniformPreviewElement();
                _preview.style.flexGrow = 1f;
                previewHost.Clear();
                previewHost.Add(_preview);
            }

            var own = _root.Q<Label>("ts-own");
            if (own != null) own.text = NationService.ManagerSchoolName;

            _root.Q<Button>("ts-apply")?.RegisterCallback<ClickEvent>(_ => Apply());
            _root.Q<Button>("ts-back")?.RegisterCallback<ClickEvent>(
                _ => ScreenRouter.Instance?.ShowDeferred("MemberSetting"));

            _applied = TeamColorProvider.ManagerColors;
            _draft = _applied;
            Render();
        }

        private void Apply()
        {
            TeamColorProvider.ManagerOverride = _draft;
            _applied = _draft;
            Render();
        }

        // ---- 描画 --------------------------------------------------------------

        private void Render()
        {
            if (_rowHost == null || _draft == null) return;
            _rowHost.Clear();
            foreach (var r in Rows) _rowHost.Add(BuildRow(r.Slot, r.Label, r.GroupTop));
            _preview?.SetColors(_draft);
            if (_changed != null)
                _changed.text = Equals(_draft, _applied) ? "" : "未確定の変更あり";
        }

        private VisualElement BuildRow(Slot slot, string label, bool groupTop)
        {
            var row = new VisualElement();
            row.AddToClassList("ts-row");
            if (groupTop) row.AddToClassList("ts-row--group");

            var name = new Label(label);
            name.AddToClassList("ts-cell");
            name.AddToClassList("ts-cell--name");
            row.Add(name);

            var value = new Label(ValueText(slot));
            value.AddToClassList("ts-cell");
            value.AddToClassList("ts-cell--val");
            row.Add(value);

            var swatchCell = new VisualElement();
            swatchCell.AddToClassList("ts-cell");
            swatchCell.AddToClassList("ts-cell--swatch");
            if (TryColorOf(slot, out var shown))
            {
                var chip = new VisualElement();
                chip.AddToClassList("ts-swatch");
                chip.style.backgroundColor = ToColor(shown);
                swatchCell.Add(chip);
            }
            row.Add(swatchCell);

            var pick = new VisualElement();
            pick.AddToClassList("ts-pick");
            pick.Add(Arrow("◀", () => Step(slot, -1)));
            pick.Add(Arrow("▶", () => Step(slot, +1)));
            row.Add(pick);

            return row;
        }

        private Button Arrow(string glyph, System.Action onClick)
        {
            var b = new Button(onClick) { text = glyph };
            b.AddToClassList("ts-arrow");
            return b;
        }

        private static Color ToColor(RgbColor c) => new(c.R / 255f, c.G / 255f, c.B / 255f);

        private string ValueText(Slot slot)
        {
            if (slot == Slot.StripeOn) return _draft.Stripe.On ? "あり" : "なし";
            if (slot == Slot.StripeColor && !_draft.Stripe.On) return "—";
            return TryColorOf(slot, out var c) ? TeamColorProvider.Palette.NameOf(c) : "—";
        }

        private bool TryColorOf(Slot slot, out RgbColor color)
        {
            switch (slot)
            {
                case Slot.Shirt: color = _draft.Shirt; return true;
                case Slot.Pants: color = _draft.Pants; return true;
                case Slot.Undershirt: color = _draft.Undershirt; return true;
                case Slot.Socks: color = _draft.Socks; return true;
                case Slot.Cap: color = _draft.CapCrown; return true;
                case Slot.StripeColor when _draft.Stripe.On: color = _draft.Stripe.Color; return true;
                default: color = default; return false;
            }
        }

        // ---- 編集 --------------------------------------------------------------

        private void Step(Slot slot, int delta)
        {
            if (slot == Slot.StripeOn)
            {
                // 縞を入れるときの色は現在の帽子色を初期値にする（無地から入れた瞬間に
                // 「縞色 未設定」の空状態を作らないため）
                _draft = _draft with
                {
                    Stripe = _draft.Stripe.On
                        ? TeamStripe.None
                        : new TeamStripe(true, _draft.CapCrown, StripeFreq),
                };
                Render();
                return;
            }
            if (slot == Slot.StripeColor && !_draft.Stripe.On) return;   // 縞なしの間は送っても意味がない

            if (!TryColorOf(slot, out var current)) return;
            var next = NextSwatch(current, delta);
            _draft = slot switch
            {
                Slot.Shirt => _draft with { Shirt = next },
                Slot.Pants => _draft with { Pants = next },
                Slot.Undershirt => _draft with { Undershirt = next },
                Slot.Socks => _draft with { Socks = next },
                // つばはクラウンと1メッシュなので必ず一緒に動かす（別々に持たせても絵に出ない）
                Slot.Cap => _draft with { CapCrown = next, CapVisor = next },
                Slot.StripeColor => _draft with { Stripe = _draft.Stripe with { Color = next } },
                _ => _draft,
            };
            Render();
        }

        private static RgbColor NextSwatch(RgbColor current, int delta)
        {
            IReadOnlyList<RgbColor> sw = TeamColorProvider.Palette.Swatches;
            if (sw.Count == 0) return current;
            int i = 0;
            for (int k = 0; k < sw.Count; k++) if (sw[k].Equals(current)) { i = k; break; }
            // 表に無い色（将来の上書き復元など）から送っても必ず一覧内へ入るよう剰余で回す
            return sw[((i + delta) % sw.Count + sw.Count) % sw.Count];
        }

#if UNITY_EDITOR
        /// <summary>【スクショ用】行を機械的に送る（人手のクリックなしで見た目の網羅を撮るため）。</summary>
        internal void StepForCapture(int rowIndex, int delta)
        {
            if (rowIndex < 0 || rowIndex >= Rows.Length) return;
            Step(Rows[rowIndex].Slot, delta);
        }
#endif
    }
}
