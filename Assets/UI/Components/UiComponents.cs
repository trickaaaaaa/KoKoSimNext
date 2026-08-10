using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using KokoSim.Engine.Nation;  // PlayerStrength（カテゴリ別ランクチップの入力）
using KokoSim.Unity.Shell;   // RankPalette（ランク色のコード側単一ソース）

namespace KokoSim.Unity.Components
{
    /// <summary>ランクチップの大きさ（通常 / 大 / 特大）。</summary>
    public enum RankChipSize { Normal, Large, XLarge }

    /// <summary>能力バー行の大きさ。Normal＝選手詳細、Large＝チーム総合力パネル、
    /// Compact＝行高32px固定（練習計画モーダルの14行級一覧・issue #327②-b）。</summary>
    public enum AbilityRowSize { Normal, Large, Compact }

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
        // --- 上限のない量の数値表示（issue #356）: スタミナ＝目安投球数など 0..100 バーに載らない量は
        //     バーを出さず「数字＋和文単位」を右端に出す（HideBar と併用。数字は Oswald・単位はサンセリフ＝NumUnit）。 ---
        public string Unit = "";         // 非空かつ HideBar のとき Value をこの単位付き数値（例「135球」）で右端表示
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
        // --- ランク併記（issue #326②）: 値の隣にランクチップを出す（Tiers.FromStrength 経由、呼び出し側で算出）。
        //     空文字ならチップを出さない。弾道など HideBar 行は呼び出し側で設定しないこと。 ---
        public string GradeA = "";
        public string GradeB = "";
        // --- タイプ軸表示（issue #219）: 弾道など優劣のない軸はバーを出さずラベルのみにする。
        //     Winner も呼び出し側で 0 にして優劣ハイライトを出さないこと。 ---
        public bool HideBar;
        // --- 上限のない量の数値表示（issue #356）: スタミナ＝目安投球数は 0..100 バーに載らないので
        //     HideBar でバーを消し、TextA/TextB の数値にこの和文単位を NumUnit で添える（例「135球」）。 ---
        public string Unit;
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
        /// <paramref name="small"/>＝比較カード埋め込み用（issue #452）。カード幅（約156px）に4組を
        /// <b>1行で</b>収める＝2段組（<see cref="CategoryRankChipsTwoRow"/>）より縦に42px薄い。
        /// 比較列は表13行＋守備適性の帯を同時に見せたいので、この縦の差がそのまま効く。
        /// </summary>
        public static VisualElement CategoryRankChipsCompact(PlayerStrength s, bool small = false)
        {
            var box = new VisualElement();
            box.AddToClassList("cat-rank-chips");
            box.AddToClassList("cat-rank-chips--compact");
            if (small) box.AddToClassList("cat-rank-chips--sm");
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
        /// <paramref name="small"/>＝比較カード埋め込み用（issue #326①）。カード幅（約156px）に収まるよう
        /// ラベル幅・チップを一段小さくする（<c>cat-rank-rows--sm</c>, components.uss）。
        /// </summary>
        public static VisualElement CategoryRankChipsTwoRow(PlayerStrength s, bool small = false)
        {
            var box = new VisualElement();
            box.AddToClassList("cat-rank-rows");
            if (small) box.AddToClassList("cat-rank-rows--sm");
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

        // ===== CallText（盤面の判定オーバーレイの大型テキスト）=====

        /// <summary>
        /// 判定テキスト（PitchCall / JudgementCall）の字送り。参照モック
        /// docs/design/mock-pitch-view.html の実測値で、USS では表現できないぶんだけをここに置く。
        /// <list type="bullet">
        /// <item>大きさ＝盤面幅の10%を 30〜64px でクランプ（USS に vw も clamp も無い）</item>
        /// <item>字間＝0.14em（USS の letter-spacing は px 指定なので font-size 依存で計算する）</item>
        /// </list>
        /// </summary>
        public const float CallFontWidthRatio = 0.10f;
        public const float CallFontMinPx = 30f;
        public const float CallFontMaxPx = 64f;
        public const float CallLetterSpacingEm = 0.14f;

        /// <summary>
        /// ぼかし影の半径（字の大きさに対する比）。
        ///
        /// ⚠ 参照モックの CSS は 64px の字に <c>0 0 26px</c> を当てているが、**この数値をそのまま
        /// UI Toolkit へ持ってくると影が絵に出ない**（2026-08-05 実測）。CSS の box/text-shadow の
        /// ぼかし半径と UI Toolkit の <see cref="TextShadow.blurRadius"/> は減衰の定義が違い、26 では
        /// 拡散しきって1画素あたりの濃さが視認限界を下回る。実測では 8px 前後で CSS の 26px と
        /// 同じ「地から起きて見える」濃さになったので、**見た目の重さで合わせて比で持つ**
        /// （字が縮む狭い盤面でも影の相対的な濃さが変わらない）。数値を参照へ戻すときは必ず絵で確かめること。
        /// </summary>
        public const float CallShadowBlurEm = 0.125f;
        private static readonly Color CallShadowColor = new Color(0f, 0f, 0f, 0.9f);

        /// <summary>
        /// 判定テキストの面（<paramref name="face"/>）と硬い影の複製（<paramref name="shadow"/>）へ
        /// 同じ文字を書く。USS の text-shadow は1要素1つしか持てないので、ぼかし影は面が、
        /// 硬い影（0 3px 0）はこの複製が担う＝**必ず対で書く**（片方だけ書くと影が前の判定のまま残る）。
        /// </summary>
        public static void SetCallText(Label face, Label shadow, string text)
        {
            if (face != null) face.text = text;
            if (shadow != null) shadow.text = text;
        }

        /// <summary>
        /// 判定テキストの大きさと字間を器の実幅から算出して面と影へ入れる。
        /// 影は面と1pxでも違うと輪郭がぶれるので、**必ず同じ値**を両方へ入れる。
        /// </summary>
        public static void ApplyCallTextMetrics(float hostWidth, Label face, Label shadow)
        {
            if (hostWidth <= 0f || float.IsNaN(hostWidth)) return;
            var size = Mathf.Clamp(hostWidth * CallFontWidthRatio, CallFontMinPx, CallFontMaxPx);
            var spacing = size * CallLetterSpacingEm;
            foreach (var l in new[] { face, shadow })
            {
                if (l == null) continue;
                l.style.fontSize = size;
                l.style.letterSpacing = spacing;
            }
            // ぼかし影（①）は面だけ。影の複製（②硬い影）に重ねると輪郭が二重に濁る。
            if (face != null)
                face.style.textShadow = new TextShadow
                {
                    offset = Vector2.zero,
                    blurRadius = size * CallShadowBlurEm,
                    color = CallShadowColor,
                };
            if (shadow != null)
                shadow.style.textShadow = new TextShadow
                {
                    offset = Vector2.zero,
                    blurRadius = 0f,
                    color = Color.clear,
                };
        }

        // ===== AptitudeStrip（守備適性の帯・issue #452）=====

        /// <summary>
        /// 守備適性の9ポジションの並び（投→捕→一→二→三→遊→左→中→右）。
        /// 呼び出し側が渡す grades の並びと<b>必ず一致させる</b>（MemberSettingState.SlotPosition /
        /// LineupSettingState.AptOrder と同じ順）。ここを入れ替えると絵だけ静かにずれる。
        /// </summary>
        private static readonly string[] AptPositions = { "投", "捕", "一", "二", "三", "遊", "左", "中", "右" };

        /// <summary>
        /// 守備適性を「9ポジション × 2選手」の帯で出す（issue #452・監督選択の案A, 2026-08-09）。
        /// <para>盤面表示（<c>apt-field</c>）は 210px×2枚＋見出し＝<b>252px</b> あり、試合中の交代モーダルに
        /// 残っている高さ（56px）に入らない。同じ情報＝ポジション別の<b>実効守備力ランク</b>を約90pxへ畳んだのがこれで、
        /// 3画面（交代／メンバー設定／スタメン設定）で共有する。</para>
        /// <para>grades は 0〜8 が上の並びに対応する。null や要素不足の側は「—」を出す
        /// （相手校のように <see cref="Players.Player.SourceId"/> が無く名簿へ join できない選手がある）。</para>
        /// </summary>
        /// <param name="tight">
        /// 詰め版（issue #452）。比較列（メンバー設定／スタメン設定）は表13行と帯を同時に見せたいので、
        /// 見出し行を薄く・データ行をチップの高さぎりぎりまで寄せて 90px→70px にする。
        /// 交代モーダルは高さが別に決まっているので既定（false）のまま＝あちらの見た目は変えない。
        /// </param>
        public static VisualElement AptitudeStrip(
            IReadOnlyList<string> gradesA, IReadOnlyList<string> gradesB,
            string labelA, string labelB, string caption = "守備適性", bool tight = false)
        {
            var strip = new VisualElement();
            strip.AddToClassList("apt-strip");
            if (tight) strip.AddToClassList("apt-strip--tight");

            var head = new VisualElement();
            head.AddToClassList("apt-strip__row");
            head.AddToClassList("apt-strip__row--head");   // 詰め版で見出し行だけ薄くするため（USS に nth-child は無い）
            var cap = new Label(caption);
            cap.AddToClassList("apt-strip__lab");
            head.Add(cap);
            foreach (var pos in AptPositions)
            {
                var cell = new VisualElement();
                cell.AddToClassList("apt-strip__cell");
                var lab = new Label(pos);
                lab.AddToClassList("apt-strip__pos");
                cell.Add(lab);
                head.Add(cell);
            }
            strip.Add(head);

            strip.Add(AptStripRow(gradesA, labelA, "a"));
            strip.Add(AptStripRow(gradesB, labelB, "b"));
            return strip;
        }

        private static VisualElement AptStripRow(IReadOnlyList<string> grades, string label, string side)
        {
            var row = new VisualElement();
            row.AddToClassList("apt-strip__row");

            var lab = new Label(label ?? "");
            lab.AddToClassList("apt-strip__lab");
            lab.AddToClassList("apt-strip__lab--" + side);
            row.Add(lab);

            for (var i = 0; i < AptPositions.Length; i++)
            {
                var cell = new VisualElement();
                cell.AddToClassList("apt-strip__cell");
                if (grades != null && i < grades.Count && !string.IsNullOrEmpty(grades[i]))
                {
                    cell.Add(RankChip(grades[i]));
                }
                else
                {
                    var dash = new Label("—");
                    dash.AddToClassList("apt-strip__dash");
                    cell.Add(dash);
                }
                row.Add(cell);
            }
            return row;
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
            if (d.Size == AbilityRowSize.Compact) row.AddToClassList("abil-row--compact");
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
            else if (!string.IsNullOrEmpty(d.Value) && !string.IsNullOrEmpty(d.Unit))
            {
                // バーの無い数値行（スタミナ＝目安投球数など上限のない量, issue #356）。右端に数字＋和文単位を出す。
                // 数字は Oswald（f-num）・単位はサンセリフに Label を分ける（NumUnit・書体3役ルール）。
                row.Add(NumUnit(d.Value, d.Unit, false, "abil-row__numunit"));
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
        /// 比較表のヘッダ（2カラムの見出し）。<see cref="CompareRow"/> の並びの直前に1本だけ置く。
        /// ラベルは**意味のある語だけ**を渡す（例: 選手交代の「退く」「入る」）。
        /// 「A」「B」のような抽象記号は意味を持たないので渡さない（issue #234）。既定引数を置かないのは
        /// プレースホルダが漏れるのを防ぐため＝どちらのカラムかは、上に並ぶ比較カードの**選手名**と
        /// カラムのアクセント色（--cmp-a / --cmp-b）で判別する。見出しが要らない画面はこの部品を呼ばない
        /// （メンバー設定・スタメン決定がその形）。
        /// <para><paramref name="caption"/> を渡すと、表のグループ名（例「打撃・走守」）を
        /// <b>ラベル列の見出し</b>として空セルへ畳む（issue #452）。独立した見出し行を持たない分だけ
        /// 縦を稼ぐので、<see cref="SetCompareDense"/> と対で使う。渡さなければ従来どおり空セル。</para>
        /// </summary>
        public static VisualElement CompareHeader(string labelA, string labelB, string caption = null)
        {
            var head = new VisualElement();
            head.AddToClassList("cmp-head");

            var spacer = new VisualElement();
            spacer.AddToClassList("cmp-head__spacer");
            if (!string.IsNullOrEmpty(caption))
            {
                var cap = new Label(caption);
                cap.AddToClassList("cmp-head__cap");
                spacer.Add(cap);
            }
            head.Add(spacer);

            head.Add(HeadCol(labelA, "a"));
            head.Add(HeadCol(labelB, "b"));
            return head;
        }

        /// <summary>
        /// 比較表の値セル幅を「単位つきの物理量（例「131km/h」）が収まる幅」に切り替える（issue #246）。
        /// <paramref name="rowsHost"/>＝<see cref="CompareRow"/> を並べるホスト要素に付けるので、
        /// <b>同じ表の全行が同じ幅</b>になりバーの起点と桁が揃ったままになる（行ごとに広げると崩れる）。
        /// 投手能力の表のように単位つきの行を含むときだけ true にする。
        /// </summary>
        public static void SetCompareUnitColumn(VisualElement rowsHost, bool on)
            => rowsHost?.EnableInClassList("cmp-rows--unit", on);

        /// <summary>
        /// 比較表を密な組（行のピッチを UI原則①の32px基準へ）に切り替える（issue #452）。
        /// <b>列の高さが自分では決まらない</b>置き場所——交代モーダルのように隣の列（打順表）が
        /// パネルの高さを決め、比較表はその余りしか使えない——で、野手の9項目（弾道〜精神力）を
        /// 内部スクロールなしで収めるための組。素の組（43pxピッチ）だと5.5行しか窓に入らず、
        /// <b>守備交代の判断に一番要る「守備」「捕球」が既定で窓の外</b>に落ちる。
        /// 文字サイズは落とさず、Label 既定の上下 margin/padding（＝43pxの実体）だけを削る。
        /// グループ名は <see cref="CompareHeader"/> の caption へ畳む（見出し1行ぶんも縦に回す）。
        /// </summary>
        public static void SetCompareDense(VisualElement rowsHost, bool on)
            => rowsHost?.EnableInClassList("cmp-rows--dense", on);

        /// <summary>
        /// 2選手比較の1行（項目名｜A: 値＋ランクチップ｜B: 値＋ランクチップ）。
        /// 横バーは撤去済み（issue #405）。数値とランクチップの併記だけで優劣を読む。
        /// </summary>
        public static VisualElement CompareRow(CompareRowData d)
        {
            var row = new VisualElement();
            row.AddToClassList("cmp-row");
            if (d.HideBar) row.AddToClassList("cmp-row--type");   // 弾道など優劣のないタイプ軸（issue #219）

            var lab = new Label(d.Label);
            lab.AddToClassList("cmp-row__lab");
            row.Add(lab);

            row.Add(CompareSide("a", d.HasA, d.ValueA, d.TextA, d.FillA, d.Winner == -1, d.GradeA, d.HideBar, d.Unit));
            row.Add(CompareSide("b", d.HasB, d.ValueB, d.TextB, d.FillB, d.Winner == 1, d.GradeB, d.HideBar, d.Unit));
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

        // text: 値ラベルの上書き（null＝value.ToString()）。fill は横バー撤去（issue #405）で未使用だが、
        // CompareRowData.FillA/FillB（球速など物理量の呼び出し元, issue #94）は残すため引数は残す。
        // grade: ランクチップ併記（issue #326②）。空文字ならチップを出さない。
        private static VisualElement CompareSide(string side, bool has, int value, string text, int fill, bool win, string grade, bool hideBar = false, string unit = null)
        {
            var el = new VisualElement();
            el.AddToClassList("cmp-row__side");
            el.AddToClassList("cmp-row__side--" + side);

            if (has && hideBar && !string.IsNullOrEmpty(unit) && !string.IsNullOrEmpty(text))
            {
                // 上限のない量（スタミナ＝目安投球数, issue #356）: 数字＋和文単位を分割して出す
                // （数字は Oswald・単位はサンセリフ＝NumUnit・書体3役ルール）。バーは出さない。
                var nu = NumUnit(text, unit, false, "cmp-row__val cmp-row__numunit");
                el.Add(nu);
                return el;
            }

            var val = new Label(has ? (text ?? value.ToString()) : "—");
            val.AddToClassList("cmp-row__val");
            // タイプ軸ラベル（弾道など、issue #219）は和文なので Oswald(f-num) を当てない（書体3役ルール）。
            if (!hideBar) val.AddToClassList("f-num");   // 比較値は純数値＝コンデンス体（km/h も欧文なので Oswald で可, 決定2-B）
            if (has && win) val.AddToClassList("cmp-row__val--win");
            el.Add(val);

            if (has && !hideBar && !string.IsNullOrEmpty(grade)) el.Add(RankChip(grade));

            return el;
        }

        // ===== LevelPips（段階レベルの升目: 経営画面の施設レベル, Issue #128） =====

        /// <summary>
        /// 段階レベルを升目で示す（保有段＝インク塗り／未保有段＝地色）。数字を読まなくても
        /// 「何段まで上げたか」「あと何段あるか」が一覧のスキャンで拾える（UI原則⑥）。
        /// 色は有無の2値だけでアクセントを使わない（UI原則②）。<paramref name="maxLevel"/> が
        /// 0以下なら空の器を返す（升目を出さない）。
        /// </summary>
        public static VisualElement LevelPips(int level, int maxLevel)
        {
            var wrap = new VisualElement();
            wrap.AddToClassList("lv-pips");
            for (var i = 1; i <= maxLevel; i++)
            {
                var pip = new VisualElement();
                pip.AddToClassList("lv-pip");
                if (i <= level) pip.AddToClassList("lv-pip--on");
                wrap.Add(pip);
            }
            return wrap;
        }
    }
}
