using UnityEngine;
using UnityEngine.UIElements;
using KokoSim.Unity.Shell;   // RankPalette（ランク色のコード側単一ソース）

namespace KokoSim.Unity.Components
{
    /// <summary>ランクチップの大きさ（通常 / 大 / 特大）。</summary>
    public enum RankChipSize { Normal, Large, XLarge }

    /// <summary>能力バー行の大きさ。Normal＝選手詳細・練習計画、Large＝チーム総合力パネル。</summary>
    public enum AbilityRowSize { Normal, Large }

    /// <summary>
    /// 能力バー行1本ぶんの入力（UI原則③「ランクは必ずカラーチップ＋文字併記」）。
    /// Grade が空なら（＝ランクを持たない相対バー）チップを出さない。
    /// </summary>
    public sealed class AbilityRowData
    {
        public string Label = "";        // 能力名
        public string Sub = "";          // 副題（算出内訳など・任意）
        public string Icon = "";         // 先頭アイコン（任意）
        public string Value = "";        // 数値テキスト（任意）
        public float Pct;                // バー充填率 0..1
        public string Grade = "";        // ランク S〜G（空ならチップ無し）
        public string Note = "";         // 右端の補足（今週の成長など・任意）
        public bool NoteMuted;           // 補足を淡色（伸びない能力など）
        public bool Dim;                 // 行全体を淡色
        public bool Divided;             // 行下に区切り線
        public AbilityRowSize Size = AbilityRowSize.Normal;
    }

    /// <summary>
    /// 2選手比較行1本ぶんの入力。値は 0..100 の表示スケール。
    /// 片側が未選択なら Has* を false にする（値は「—」・バーなしで表示）。
    /// </summary>
    public sealed class CompareRowData
    {
        public string Label = "";   // 能力名
        public int ValueA;          // A の値（HasA が true のときのみ使う）
        public int ValueB;          // B の値（HasB が true のときのみ使う）
        public bool HasA;
        public bool HasB;
        public int Winner;          // -1=A が優位 / 1=B が優位 / 0=同値・比較不能
    }

    /// <summary>
    /// 部品辞書（docs/design/UI-BUILD-METHOD.md Step 2）の生成ファクトリ。
    /// 見た目は Assets/UI/Components/components.uss のクラスに定義し、ここは組み立てだけを行う。
    /// 各画面コントローラでチップ・バー行を private に自作しないこと（UI原則⑤）。
    /// </summary>
    public static class UiComponents
    {
        // ===== RankChip =====

        /// <summary>ランクチップ（部品辞書版・components.uss を読み込む画面用）。</summary>
        public static Label RankChip(string grade, RankChipSize size = RankChipSize.Normal)
        {
            var chip = new Label(grade);
            chip.AddToClassList("rank-chip");
            chip.AddToClassList("rank-chip--" + grade);
            switch (size)
            {
                case RankChipSize.Large: chip.AddToClassList("rank-chip--lg"); break;
                case RankChipSize.XLarge: chip.AddToClassList("rank-chip--xl"); break;
            }
            return chip;
        }

        /// <summary>
        /// ランクチップ（KokoSimTheme.uss の .grade 系＝旧ミラー）。
        /// components.uss を読み込んでいない画面（選手一覧・ホーム・大会）向けの互換口。
        /// 全画面が部品辞書を読み込むようになったら <see cref="RankChip"/> に統合して廃止する。
        /// </summary>
        public static Label RankChipLegacy(string grade, RankChipSize size = RankChipSize.Normal)
        {
            var chip = new Label(grade);
            chip.AddToClassList("grade");
            chip.AddToClassList("grade--" + grade);
            switch (size)
            {
                case RankChipSize.Large: chip.AddToClassList("grade--lg"); break;
                case RankChipSize.XLarge: chip.AddToClassList("grade--xl"); break;
            }
            return chip;
        }

        // ===== AbilityRow =====

        /// <summary>
        /// 能力バー行（アイコン／能力名＋副題／数値／バー／ランクチップ／補足）。
        /// バー色はランク連動（RankPalette＝tokens.uss --rank-*）で、画面側で色を決めない。
        /// </summary>
        public static VisualElement AbilityRow(AbilityRowData d)
        {
            var row = new VisualElement();
            row.AddToClassList("abil-row");
            if (d.Size == AbilityRowSize.Large) row.AddToClassList("abil-row--lg");
            if (d.Dim) row.AddToClassList("abil-row--dim");
            if (d.Divided) row.AddToClassList("abil-row--divided");

            if (!string.IsNullOrEmpty(d.Icon))
            {
                var icon = new Label(d.Icon);
                icon.AddToClassList("abil-row__icon");
                row.Add(icon);
            }

            var name = new VisualElement();
            name.AddToClassList("abil-row__name");
            var label = new Label(d.Label);
            label.AddToClassList("abil-row__label");
            name.Add(label);
            if (!string.IsNullOrEmpty(d.Sub))
            {
                var sub = new Label(d.Sub);
                sub.AddToClassList("abil-row__sub");
                name.Add(sub);
            }
            row.Add(name);

            if (!string.IsNullOrEmpty(d.Value))
            {
                var val = new Label(d.Value);
                val.AddToClassList("abil-row__value");
                row.Add(val);
            }

            var bar = new VisualElement();
            bar.AddToClassList("abil-row__bar");
            var fill = new VisualElement();
            fill.AddToClassList("abil-row__fill");
            fill.style.width = Length.Percent(Mathf.Clamp01(d.Pct) * 100f);
            fill.style.backgroundColor = RankPalette.Of(string.IsNullOrEmpty(d.Grade) ? "D" : d.Grade);
            bar.Add(fill);
            row.Add(bar);

            if (!string.IsNullOrEmpty(d.Grade)) row.Add(RankChip(d.Grade));

            if (!string.IsNullOrEmpty(d.Note))
            {
                var note = new Label(d.Note);
                note.AddToClassList("abil-row__note");
                if (d.NoteMuted) note.AddToClassList("abil-row__note--muted");
                row.Add(note);
            }

            return row;
        }

        // ===== CompareRow（2選手の能力対比） =====

        /// <summary>
        /// 比較表のヘッダ（A/B カラムの見出し）。どちらのカラムかを色に頼らず読めるようにする。
        /// <see cref="CompareRow"/> の並びの直前に1本だけ置く。
        /// </summary>
        public static VisualElement CompareHeader(string labelA = "A", string labelB = "B")
        {
            var head = new VisualElement();
            head.AddToClassList("cmp-head");

            var spacer = new VisualElement();
            spacer.AddToClassList("cmp-head__spacer");
            head.Add(spacer);

            head.Add(HeadCol(labelA, "a"));
            head.Add(HeadCol(labelB, "b"));
            return head;
        }

        /// <summary>
        /// 2選手比較の1行（項目名｜A: 値＋バー｜B: 値＋バー）。
        /// A/B のバーはどちらも同じ左起点から右へ同一スケールで伸びるので、長さの差がそのまま優劣になる。
        /// </summary>
        public static VisualElement CompareRow(CompareRowData d)
        {
            var row = new VisualElement();
            row.AddToClassList("cmp-row");

            var lab = new Label(d.Label);
            lab.AddToClassList("cmp-row__lab");
            row.Add(lab);

            row.Add(CompareSide("a", d.HasA, d.ValueA, d.Winner == -1));
            row.Add(CompareSide("b", d.HasB, d.ValueB, d.Winner == 1));
            return row;
        }

        private static VisualElement HeadCol(string text, string side)
        {
            var col = new VisualElement();
            col.AddToClassList("cmp-head__col");
            col.AddToClassList("cmp-head__col--" + side);

            var lab = new Label(text);
            lab.AddToClassList("cmp-head__lab");
            lab.AddToClassList("cmp-head__lab--" + side);
            col.Add(lab);
            return col;
        }

        private static VisualElement CompareSide(string side, bool has, int value, bool win)
        {
            var el = new VisualElement();
            el.AddToClassList("cmp-row__side");
            el.AddToClassList("cmp-row__side--" + side);

            var val = new Label(has ? value.ToString() : "—");
            val.AddToClassList("cmp-row__val");
            if (has && win) val.AddToClassList("cmp-row__val--win");
            el.Add(val);

            var track = new VisualElement();
            track.AddToClassList("cmp-row__track");
            if (has)
            {
                var bar = new VisualElement();
                bar.AddToClassList("cmp-row__bar");
                bar.AddToClassList("cmp-row__bar--" + side);
                bar.style.width = Length.Percent(Mathf.Clamp(value, 0, 100));
                track.Add(bar);
            }
            el.Add(track);
            return el;
        }
    }
}
