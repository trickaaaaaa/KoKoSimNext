using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using KokoSim.Engine.Nation;  // PlayerStrength（カテゴリ別ランクチップの入力）
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
        // --- タイプ軸表示（issue #219）: 弾道など優劣のない軸はランクチップの代わりにこの中立ラベルを出す ---
        public string TypeLabel = "";    // 非空ならランクチップの代わりにこのラベルを表示（Grade は無視）
        public bool HideBar;             // true: バー＋数値を出さずアイコン/名前/タイプラベルだけにする
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
        // --- 物理量表示（issue #94）: 表示テキストとバー幅を分離。既定 null/負値なら ValueA/ValueB をそのまま使う
        //     ＝従来 caller は無改修で同一表示。球速行だけ TextA="148km/h"（engine変換）＋FillA=Level(0-100) を渡す。 ---
        public string TextA;        // A の値ラベル上書き（null＝ValueA.ToString()）
        public string TextB;        // B の値ラベル上書き（null＝ValueB.ToString()）
        public int FillA = -1;      // A のバー幅[0-100]（負＝ValueA を流用）。表示スケール統一のため Level を渡す
        public int FillB = -1;      // B のバー幅[0-100]（負＝ValueB を流用）
        // --- タイプ軸表示（issue #219）: 弾道など優劣のない軸はバーを出さずラベルのみにする。
        //     Winner も呼び出し側で 0 にして優劣ハイライトを出さないこと。 ---
        public bool HideBar;
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

        // ===== TypeChip（issue #219） =====

        /// <summary>
        /// タイプ軸バッジ（弾道＝ゴロ型〜フライ型など優劣のない値の表示）。
        /// RankChip と違い単色・中立色（tokens.uss --color-panel-deep/--color-chalk）で優劣色に見せない。
        /// </summary>
        public static Label TypeChip(string label)
        {
            var chip = new Label(label);
            chip.AddToClassList("type-chip");
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

        // ===== CategoryRankChips（選手のカテゴリ別ランク: 打撃力/走力/守備力/投手力, Issue #30/#140） =====

        /// <summary>
        /// 選手1名のカテゴリ別ランクチップを4つ並べる（PlayerStrength＝打撃力/走力/守備力/投手力）。
        /// 各画面がチップ＋ラベルを直書きしない（UI原則⑤）。components.uss 未読み込みの画面
        /// （選手一覧など）にも出せるよう <see cref="RankChipLegacy"/> を使う。
        /// </summary>
        public static VisualElement CategoryRankChips(PlayerStrength s)
        {
            var box = new VisualElement();
            box.AddToClassList("cat-rank-chips");
            AddCategoryChip(box, "打撃力", s.BattingTier);
            AddCategoryChip(box, "走力", s.MobilityTier);
            AddCategoryChip(box, "守備力", s.DefenseTier);
            AddCategoryChip(box, "投手力", s.PitchingTier);
            return box;
        }

        /// <summary>
        /// カテゴリ別ランクのコンパクト版（1字ラベル「打走守投」＋小チップ）。行のメタ枠など
        /// 幅の限られた場所用（Issue #133・練習計画）。フル版と同じくラベル＋文字併記（UI原則③）。
        /// </summary>
        public static VisualElement CategoryRankChipsCompact(PlayerStrength s)
        {
            var box = new VisualElement();
            box.AddToClassList("cat-rank-chips");
            box.AddToClassList("cat-rank-chips--compact");
            AddCategoryChip(box, "打", s.BattingTier);
            AddCategoryChip(box, "走", s.MobilityTier);
            AddCategoryChip(box, "守", s.DefenseTier);
            AddCategoryChip(box, "投", s.PitchingTier);
            return box;
        }

        private static void AddCategoryChip(VisualElement box, string label, Tier tier)
        {
            var lbl = new Label(label);
            lbl.AddToClassList("cat-rank-chips__label");
            box.Add(lbl);
            box.Add(RankChipLegacy(tier.ToString()));
        }

        /// <summary>
        /// カテゴリ別ランクの2段組（上=打撃力・走力／下=守備力・投手力）。1段4組では行幅が足りず
        /// 中途半端に折り返す一覧行用（選手一覧）。ラベルを固定幅・右揃えにして上下段のチップ列を
        /// 縦に揃える（UI原則①「整列で捌く」③「桁を揃える」）。
        /// </summary>
        public static VisualElement CategoryRankChipsTwoRow(PlayerStrength s)
        {
            var box = new VisualElement();
            box.AddToClassList("cat-rank-rows");
            box.Add(CategoryChipRow("打撃力", s.BattingTier, "走力", s.MobilityTier));
            box.Add(CategoryChipRow("守備力", s.DefenseTier, "投手力", s.PitchingTier));
            return box;
        }

        private static VisualElement CategoryChipRow(string label1, Tier t1, string label2, Tier t2)
        {
            var row = new VisualElement();
            row.AddToClassList("cat-rank-rows__row");
            AddCategoryPair(row, label1, t1);
            AddCategoryPair(row, label2, t2);
            return row;
        }

        private static void AddCategoryPair(VisualElement row, string label, Tier tier)
        {
            var lbl = new Label(label);
            lbl.AddToClassList("cat-rank-rows__label");
            row.Add(lbl);
            row.Add(RankChipLegacy(tier.ToString()));
        }

        // ===== StackedBar（積み上げ横バー: 練習配分の割合可視化, Issue #133） =====

        /// <summary>
        /// 積み上げ横バーのセグメント1本ぶんの入力。色は components.uss の
        /// `.stack-bar__seg--{StyleClass}`（値は tokens.uss のトークン）で与える＝直書き禁止（UI原則⑤）。
        /// </summary>
        public sealed class StackedBarSegment
        {
            public string StyleClass = "";  // 色クラス接尾辞（空＝色なし）
            public float Fraction;          // バー全幅に対する割合 0..1
            public string Tooltip = "";     // ホバー凡例（メニュー名＋分）
        }

        /// <summary>
        /// 積み上げ横バー。セグメントの合計が 1 未満なら右側が余白として残る
        /// （練習配分では余白＝休養。塗らないことで「残り時間」が読める）。
        /// </summary>
        public static VisualElement StackedBar(IReadOnlyList<StackedBarSegment> segments)
        {
            var bar = new VisualElement();
            bar.AddToClassList("stack-bar");
            foreach (var s in segments)
            {
                var seg = new VisualElement();
                seg.AddToClassList("stack-bar__seg");
                if (!string.IsNullOrEmpty(s.StyleClass)) seg.AddToClassList("stack-bar__seg--" + s.StyleClass);
                seg.style.width = Length.Percent(Mathf.Clamp01(s.Fraction) * 100f);
                seg.tooltip = s.Tooltip;
                bar.Add(seg);
            }
            return bar;
        }

        /// <summary>
        /// 練習メニュー → 積み上げバー色クラス（tokens.uss --tm-*）の対応（Issue #133・ユーザー確定
        /// 「メニュー別に色分け」）。ポジション別守備＋内外野汎用は判別不能な色数になるため defpos の
        /// 1色に集約。休養は空文字＝塗らない（バー右側の余白として残す）。
        /// </summary>
        public static string TrainingMenuColorClass(KokoSim.Engine.Season.TrainingMenu m) => m switch
        {
            KokoSim.Engine.Season.TrainingMenu.Batting => "batting",
            KokoSim.Engine.Season.TrainingMenu.PowerHitting => "power",
            KokoSim.Engine.Season.TrainingMenu.PlateDiscipline => "discipline",
            KokoSim.Engine.Season.TrainingMenu.Strength => "strength",
            KokoSim.Engine.Season.TrainingMenu.Defense => "defense",
            KokoSim.Engine.Season.TrainingMenu.Throwing => "throwing",
            KokoSim.Engine.Season.TrainingMenu.BaseRunning => "baserun",
            KokoSim.Engine.Season.TrainingMenu.Bunt => "bunt",
            KokoSim.Engine.Season.TrainingMenu.Pitching => "pitching",
            KokoSim.Engine.Season.TrainingMenu.BreakingBall => "breaking",
            KokoSim.Engine.Season.TrainingMenu.VelocityTraining => "velocity",
            KokoSim.Engine.Season.TrainingMenu.Running => "running",
            KokoSim.Engine.Season.TrainingMenu.Rest => "",
            _ => "defpos",   // ポジション別守備9種＋内野/外野汎用
        };

        // ===== TimeAllocSlider（練習時間ドラッグバー・issue #222②）=====

        /// <summary>
        /// 練習時間の割当をドラッグで調整するバー。トラック上のポインタ位置を stepMinutes 単位へ
        /// スナップし、0..maxMinutes（＝現在値＋残り時間でクランプ済みの上限）へ収めて onChange を呼ぶ。
        /// ±ボタンと役割を分けず併用する（①ドラッグで大きく動かす／②±で微調整）。
        /// locked（委任中）は onChange を配線しない＝見た目だけの静的バーになる。
        /// </summary>
        public static VisualElement TimeAllocSlider(
            int minutes, int maxMinutes, int stepMinutes, string colorClass, bool locked, System.Action<int> onChange)
        {
            var track = new VisualElement();
            track.AddToClassList("ta-slider");
            if (locked) track.AddToClassList("ta-slider--locked");

            var fill = new VisualElement();
            fill.AddToClassList("ta-slider__fill");
            if (!string.IsNullOrEmpty(colorClass)) fill.AddToClassList("ta-slider__fill--" + colorClass);
            fill.style.width = Length.Percent((maxMinutes > 0 ? Mathf.Clamp01((float)minutes / maxMinutes) : 0f) * 100f);
            track.Add(fill);

            if (locked || onChange == null || maxMinutes <= 0) return track;

            void Seek(Vector2 localPos)
            {
                var w = track.resolvedStyle.width;
                if (w <= 0f) return;
                var frac = Mathf.Clamp01(localPos.x / w);
                var raw = Mathf.RoundToInt(frac * maxMinutes);
                var snapped = Mathf.Max(0, (raw / stepMinutes) * stepMinutes);
                onChange(snapped);
            }

            track.RegisterCallback<PointerDownEvent>(e =>
            {
                track.CapturePointer(e.pointerId);
                Seek(e.localPosition);
                e.StopPropagation();
            });
            track.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (track.HasPointerCapture(e.pointerId)) Seek(e.localPosition);
            });
            track.RegisterCallback<PointerUpEvent>(e =>
            {
                if (track.HasPointerCapture(e.pointerId)) track.ReleasePointer(e.pointerId);
            });

            return track;
        }

        // ===== SchoolName（設計書16 §4-3。校名は常に太明朝） =====

        /// <summary>
        /// 校名＋小書き（都道府県・シード等）。校名だけを太明朝（.f-display）にし、
        /// 小書きはサンセリフのまま残す＝明朝を固有名詞に絞る（design-16 §1 の境界規則）。
        /// large は「1画面で1つだけ大きく出す」主役校用（UI原則②）。
        /// </summary>
        public static VisualElement SchoolName(string name, string sub = "", bool large = false)
        {
            var box = new VisualElement();
            box.AddToClassList("school-name");
            if (large) box.AddToClassList("school-name--lg");

            var main = new Label(name);
            main.AddToClassList("school-name__main");
            main.AddToClassList("f-display");   // 太明朝（ExtraBold。26px以下はこちらでないと横画が残らない）
            box.Add(main);

            if (!string.IsNullOrEmpty(sub))
            {
                var s = new Label(sub);
                s.AddToClassList("school-name__sub");
                box.Add(s);
            }
            return box;
        }

        // ===== VerticalName（縦書き選手名・甲子園両翼式。設計書16 §9-2 の試作） =====

        /// <summary>
        /// 縦書きの選手名1列（打順／守備／姓を1字1行）。UI Toolkit に縦書きが無いので
        /// 1文字＝1 Label のスタックで組む。姓だけを立てるのが実物の両翼（名は出さない）。
        /// </summary>
        public static VisualElement VerticalName(string order, string position, string family, bool lit = false)
        {
            var col = new VisualElement();
            col.AddToClassList("vname");
            if (lit) col.AddToClassList("vname--lit");

            var ord = new Label(order);
            ord.AddToClassList("vname__ord");
            ord.AddToClassList("f-dot");
            col.Add(ord);

            var pos = new Label(position);
            pos.AddToClassList("vname__pos");
            pos.AddToClassList("f-dot");
            col.Add(pos);

            foreach (var ch in family ?? "")
            {
                var c = new Label(ch.ToString());
                c.AddToClassList("vname__ch");
                c.AddToClassList("f-display");   // 校名と同じ太明朝（人名も掲示板の外の声）
                col.Add(c);
            }
            return col;
        }

        // ===== RuleHead（新聞の柱見出し。英語 eyebrow の置き換え） =====

        /// <summary>縦罫＋明朝の小見出し。アンバーは使わない（見出しは頻出するため）。</summary>
        public static VisualElement RuleHead(string text)
        {
            var head = new VisualElement();
            head.AddToClassList("rule-head");
            var rule = new VisualElement();
            rule.AddToClassList("rule-head__rule");
            head.Add(rule);
            var label = new Label(text);
            label.AddToClassList("rule-head__text");
            label.AddToClassList("f-display");
            head.Add(label);
            return head;
        }

        // ===== NumUnit（数字＋和文単位の混植セル）=====

        /// <summary>
        /// 数字＋和文単位を Label 分割して並べる（設計書16 §2・決定2-B「混植セルは Label を分ける」）。
        /// 数字だけコンデンス体（Oswald＝f-num / bold で f-num-bd）に載せ、単位はサンセリフのまま。
        /// 例: NumUnit("2", "年")／NumUnit("45", "名")／NumUnit("15", "週")。
        /// containerClass に既存の列クラス（"cell cell--narrow" 等）を渡してレイアウトを流用する。
        /// </summary>
        public static VisualElement NumUnit(string digits, string unit, bool bold = false, string containerClass = null)
        {
            var wrap = new VisualElement();
            wrap.AddToClassList("nu-wrap");
            if (!string.IsNullOrEmpty(containerClass))
                foreach (var c in containerClass.Split(' '))
                    if (c.Length > 0) wrap.AddToClassList(c);
            var num = new Label(digits);
            num.AddToClassList(bold ? "f-num-bd" : "f-num");
            num.AddToClassList("nu-num");
            wrap.Add(num);
            if (!string.IsNullOrEmpty(unit))
            {
                var u = new Label(unit);
                u.AddToClassList("nu-unit");
                wrap.Add(u);
            }
            return wrap;
        }

        /// <summary>
        /// "2年" / "45名" のような「先頭の半角数字＋和文単位」文字列を分割して <see cref="NumUnit"/> にする。
        /// 数字を含まない（"—" 等）ときは分割せずそのまま1枚の Label で返す（豆腐・空セル対策）。
        /// </summary>
        public static VisualElement NumUnitAuto(string text, bool bold = false, string containerClass = null)
        {
            var n = 0;
            while (n < text.Length && text[n] >= '0' && text[n] <= '9') n++;
            if (n == 0)
            {
                var wrap = new VisualElement();
                wrap.AddToClassList("nu-wrap");
                if (!string.IsNullOrEmpty(containerClass))
                    foreach (var c in containerClass.Split(' '))
                        if (c.Length > 0) wrap.AddToClassList(c);
                wrap.Add(new Label(text));
                return wrap;
            }
            return NumUnit(text.Substring(0, n), text.Substring(n), bold, containerClass);
        }

        // ===== AbilityRow =====

        /// <summary>
        /// 能力バー行（アイコン／能力名＋副題／ランクチップ／バー／数値／補足）。
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

            if (!string.IsNullOrEmpty(d.TypeLabel)) row.Add(TypeChip(d.TypeLabel));
            else if (!string.IsNullOrEmpty(d.Grade)) row.Add(RankChip(d.Grade));

            if (!d.HideBar)
            {
                var bar = new VisualElement();
                bar.AddToClassList("abil-row__bar");
                var fill = new VisualElement();
                fill.AddToClassList("abil-row__fill");
                fill.style.width = Length.Percent(Mathf.Clamp01(d.Pct) * 100f);
                fill.style.backgroundColor = RankPalette.Of(string.IsNullOrEmpty(d.Grade) ? "D" : d.Grade);
                bar.Add(fill);
                row.Add(bar);

                if (!string.IsNullOrEmpty(d.Value))
                {
                    var val = new Label(d.Value);
                    val.AddToClassList("abil-row__value");
                    val.AddToClassList("f-num");   // 能力内部値は純数値＝コンデンス体（決定2-B・部品駆動の全画面へ一括適用）
                    row.Add(val);
                }
            }

            if (!string.IsNullOrEmpty(d.Note))
            {
                var note = new Label(d.Note);
                note.AddToClassList("abil-row__note");
                if (d.NoteMuted) note.AddToClassList("abil-row__note--muted");
                row.Add(note);
            }

            return row;
        }

        // ===== SimProgress（背景処理の進捗ストリップ・#208） =====

        /// <summary>
        /// 画面下端の進捗ストリップ「ロード中」＋充填バー（#208）。ブースト画面（スタメン設定・結果）で
        /// 背景の全国裏試合を集中消化している間だけ出す。大会名・県数などの内部情報は一切出さず、
        /// ラベルは「ロード中」固定・バーの充填だけで進捗を表す。生成後は <see cref="SetSimProgress"/> で
        /// 充填率を更新し、完了したら display:none で即消す（駆動は <c>SimProgressOverlay</c>）。
        /// </summary>
        public static VisualElement SimProgress()
        {
            var strip = new VisualElement { name = "sim-load" };
            strip.AddToClassList("sim-load");

            var label = new Label("ロード中");
            label.AddToClassList("sim-load__label");
            label.AddToClassList("f-body");   // 和文ラベル＝サンセリフ（書体3役ルール）
            strip.Add(label);

            var track = new VisualElement();
            track.AddToClassList("sim-load__track");
            var fill = new VisualElement { name = "sim-load__fill" };
            fill.AddToClassList("sim-load__fill");
            track.Add(fill);
            strip.Add(track);

            return strip;
        }

        /// <summary><see cref="SimProgress"/> のバー充填率(0..1)を更新する。</summary>
        public static void SetSimProgress(VisualElement strip, float pct)
        {
            var fill = strip?.Q<VisualElement>("sim-load__fill");
            if (fill != null) fill.style.width = Length.Percent(Mathf.Clamp01(pct) * 100f);
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

            row.Add(CompareSide("a", d.HasA, d.ValueA, d.TextA, d.FillA, d.Winner == -1, d.HideBar));
            row.Add(CompareSide("b", d.HasB, d.ValueB, d.TextB, d.FillB, d.Winner == 1, d.HideBar));
            return row;
        }

        // ===== BoxRow（ボックススコア表の1行） =====

        /// <summary>
        /// ボックススコア表の1行（試合結果画面のイニングスコア／打撃／投手／打席結果に共通）。
        /// セルは (テキスト, 列種別) の並びで渡す。列種別は components.uss の .bs-cell--* に対応し、
        /// 空白区切りで複数指定できる（例 "pa dim"＝打席結果セルを淡く）。
        /// 見出し行とデータ行が同じ列種別を使うことで、幅が構造的に揃う（横ずれの対策）。
        /// </summary>
        public static VisualElement BoxRow(
            IEnumerable<(string Text, string Kind)> cells, bool header = false, bool alt = false)
        {
            var row = new VisualElement();
            row.AddToClassList("bs-row");
            if (header) row.AddToClassList("bs-row--head");
            if (alt) row.AddToClassList("bs-row--alt");

            foreach (var (text, kind) in cells)
            {
                var cell = new Label(text);
                cell.AddToClassList("bs-cell");
                foreach (var k in kind.Split(' '))
                    if (k.Length > 0) cell.AddToClassList("bs-cell--" + k);
                // 数字だけのセル（回別得点・R/H/E・打数など、ヘッダーの回数字も含む）はコンデンス体に。
                // 校名・打席結果など和文を含むセルは対象外＝内容判定で豆腐を避ける（決定2-B）。
                if (IsNumericCell(text)) cell.AddToClassList("f-num");
                row.Add(cell);
            }
            return row;
        }

        /// <summary>Oswald（欧文専用）に載せてよい「数字だけのセル」か。数字・小数点・記号のみを許可し、
        /// 和文/かなを含むものは false。空文字は false（無駄なクラス付与を避ける）。</summary>
        private static bool IsNumericCell(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (var c in text)
            {
                var ok = (c >= '0' && c <= '9') || c == '.' || c == ',' || c == ':' ||
                         c == '/' || c == '%' || c == '+' || c == '-' || c == '–' || c == '—' || c == ' ';
                if (!ok) return false;
            }
            return true;
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

        // text: 値ラベルの上書き（null＝value.ToString()）。fill: バー幅[0-100]（負＝value を流用）。
        // 表示テキストとバー幅を分離し、球速など物理量は「テキスト=km/h・バー=Level(0-100)」で出せるようにする（issue #94）。
        private static VisualElement CompareSide(string side, bool has, int value, string text, int fill, bool win, bool hideBar = false)
        {
            var el = new VisualElement();
            el.AddToClassList("cmp-row__side");
            el.AddToClassList("cmp-row__side--" + side);

            var val = new Label(has ? (text ?? value.ToString()) : "—");
            val.AddToClassList("cmp-row__val");
            // タイプ軸ラベル（弾道など、issue #219）は和文なので Oswald(f-num) を当てない（書体3役ルール）。
            if (!hideBar) val.AddToClassList("f-num");   // 比較値は純数値＝コンデンス体（km/h も欧文なので Oswald で可, 決定2-B）
            if (has && win) val.AddToClassList("cmp-row__val--win");
            el.Add(val);

            var track = new VisualElement();
            track.AddToClassList("cmp-row__track");
            if (has && !hideBar)
            {
                var bar = new VisualElement();
                bar.AddToClassList("cmp-row__bar");
                bar.AddToClassList("cmp-row__bar--" + side);
                bar.style.width = Length.Percent(Mathf.Clamp(fill >= 0 ? fill : value, 0, 100));
                track.Add(bar);
            }
            el.Add(track);
            return el;
        }
    }
}
