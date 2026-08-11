using System;
using System.Collections.Generic;
using KokoSim.Engine.Nation;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Components
{
    /// <summary>
    /// ユニフォーム配色の領域行（部品辞書 / 設計書18 §3.5.1）。領域ごとに色を「送って」決める。
    /// 見た目は Components/components.uss の <c>.ucp-*</c> に、組み立てはここに置く。
    ///
    /// 案B「領域別」＝プリセットから選ばせない（自由度優先・2026-07-29 監督選定）。選べる色は
    /// <b>パレット表に出てくる色だけ</b>（<see cref="TeamColorPalette.Swatches"/>）＝
    /// 自由に見えて破綻した配色は作れない。
    ///
    /// チーム設定画面（<c>TeamSettingController</c>）と新規ゲーム設定画面（<c>NewGameSetupController</c>）が
    /// 共有する＝同じ実装を2つ作らない（issue #585）。確定（どこへ書き戻すか）は持ち主の画面が決め、
    /// この部品は<b>下書きの保持と描画だけ</b>を担う。
    /// </summary>
    public sealed class UniformColorPicker
    {
        /// <summary>編集できる領域。順序はそのまま画面の行順。</summary>
        private enum Slot { Shirt, Pants, Undershirt, Socks, Cap, Shoes, StripeOn, StripeColor }

        private static readonly (Slot Slot, string Label, bool GroupTop)[] RowDefs =
        {
            (Slot.Shirt, "ユニフォーム上", false),
            (Slot.Pants, "ズボン", false),
            (Slot.Undershirt, "アンダーシャツ", false),
            (Slot.Socks, "ソックス", false),
            // 帽子はクラウンとつばで1色。購入素体の帽子が1メッシュで塗り分けられないため（design-18 §3.5.1）
            (Slot.Cap, "帽子", false),
            // スパイクだけは全見本でなく白・黒の二択（実物がその2色しかない）
            (Slot.Shoes, "スパイク", false),
            (Slot.StripeOn, "縦縞", true),
            (Slot.StripeColor, "縞色", false),
        };

        /// <summary>縞の本数[本/m]。実物のピンストライプは間隔2.5cm＝40本/m（design-18 §3.5.3）。</summary>
        private const double StripeFreq = 40.0;

        /// <summary>行数（スクショ用ハーネスが行を機械的に送るときの上限）。</summary>
        public static int RowCount => RowDefs.Length;

        private VisualElement _host;

        /// <summary>編集中の配色（下書き）。確定は持ち主の画面が行う。</summary>
        public TeamColors Colors { get; private set; }

        /// <summary>色が変わったときに呼ばれる（プレビュー更新・未確定表示の更新に使う）。</summary>
        public event Action Changed;

        /// <summary>行を並べる器を指定する。</summary>
        public void Bind(VisualElement rowHost) => _host = rowHost;

        /// <summary>下書きを差し替える（画面を開いたときの初期値の投入）。<see cref="Changed"/> は発火しない。</summary>
        public void SetColors(TeamColors colors)
        {
            Colors = colors;
            Render();
        }

        /// <summary>行を組み直す。</summary>
        public void Render()
        {
            if (_host == null || Colors == null) return;
            _host.Clear();
            foreach (var r in RowDefs) _host.Add(BuildRow(r.Slot, r.Label, r.GroupTop));
        }

        /// <summary>行を機械的に送る（スクショ用ハーネス。人手のクリックなしで見た目の網羅を撮るため）。</summary>
        public void StepRow(int rowIndex, int delta)
        {
            if (rowIndex < 0 || rowIndex >= RowDefs.Length) return;
            Step(RowDefs[rowIndex].Slot, delta);
        }

        // ---- 描画 --------------------------------------------------------------

        private VisualElement BuildRow(Slot slot, string label, bool groupTop)
        {
            var row = new VisualElement();
            row.AddToClassList("ucp-row");
            if (groupTop) row.AddToClassList("ucp-row--group");

            var name = new Label(label);
            name.AddToClassList("ucp-cell");
            name.AddToClassList("ucp-cell--name");
            row.Add(name);

            var value = new Label(ValueText(slot));
            value.AddToClassList("ucp-cell");
            value.AddToClassList("ucp-cell--val");
            row.Add(value);

            var swatchCell = new VisualElement();
            swatchCell.AddToClassList("ucp-cell");
            swatchCell.AddToClassList("ucp-cell--swatch");
            if (TryColorOf(slot, out var shown))
            {
                var chip = new VisualElement();
                chip.AddToClassList("ucp-swatch");
                chip.style.backgroundColor = ToColor(shown);
                swatchCell.Add(chip);
            }
            row.Add(swatchCell);

            var pick = new VisualElement();
            pick.AddToClassList("ucp-pick");
            pick.Add(Arrow("◀", () => Step(slot, -1)));
            pick.Add(Arrow("▶", () => Step(slot, +1)));
            row.Add(pick);

            return row;
        }

        private static Button Arrow(string glyph, Action onClick)
        {
            var b = new Button(onClick) { text = glyph };
            b.AddToClassList("ucp-arrow");
            return b;
        }

        private static Color ToColor(RgbColor c) => new(c.R / 255f, c.G / 255f, c.B / 255f);

        private string ValueText(Slot slot)
        {
            if (slot == Slot.StripeOn) return Colors.Stripe.On ? "あり" : "なし";
            if (slot == Slot.StripeColor && !Colors.Stripe.On) return "—";
            return TryColorOf(slot, out var c) ? TeamColorProvider.Palette.NameOf(c) : "—";
        }

        private bool TryColorOf(Slot slot, out RgbColor color)
        {
            switch (slot)
            {
                case Slot.Shirt: color = Colors.Shirt; return true;
                case Slot.Pants: color = Colors.Pants; return true;
                case Slot.Undershirt: color = Colors.Undershirt; return true;
                case Slot.Socks: color = Colors.Socks; return true;
                case Slot.Cap: color = Colors.CapCrown; return true;
                case Slot.Shoes: color = Colors.Shoes; return true;
                case Slot.StripeColor when Colors.Stripe.On: color = Colors.Stripe.Color; return true;
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
                Colors = Colors with
                {
                    Stripe = Colors.Stripe.On
                        ? TeamStripe.None
                        : new TeamStripe(true, Colors.CapCrown, StripeFreq),
                };
                Render();
                Changed?.Invoke();
                return;
            }
            if (slot == Slot.StripeColor && !Colors.Stripe.On) return;   // 縞なしの間は送っても意味がない

            if (!TryColorOf(slot, out var current)) return;
            // スパイクだけは見本一覧でなく専用の二択（白・黒）を回す
            var next = slot == Slot.Shoes
                ? Cycle(TeamColors.ShoeChoices, current, delta)
                : Cycle(TeamColorProvider.Palette.Swatches, current, delta);
            Colors = slot switch
            {
                Slot.Shirt => Colors with { Shirt = next },
                Slot.Pants => Colors with { Pants = next },
                Slot.Undershirt => Colors with { Undershirt = next },
                Slot.Socks => Colors with { Socks = next },
                Slot.Shoes => Colors with { Shoes = next },
                // つばはクラウンと1メッシュなので必ず一緒に動かす（別々に持たせても絵に出ない）
                Slot.Cap => Colors with { CapCrown = next, CapVisor = next },
                Slot.StripeColor => Colors with { Stripe = Colors.Stripe with { Color = next } },
                _ => Colors,
            };
            Render();
            Changed?.Invoke();
        }

        private static RgbColor Cycle(IReadOnlyList<RgbColor> choices, RgbColor current, int delta)
        {
            if (choices == null || choices.Count == 0) return current;
            var i = 0;
            for (var k = 0; k < choices.Count; k++) if (choices[k].Equals(current)) { i = k; break; }
            // 一覧に無い色（将来の上書き復元など）から送っても必ず一覧内へ入るよう剰余で回す
            return choices[((i + delta) % choices.Count + choices.Count) % choices.Count];
        }
    }
}
