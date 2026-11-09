using KokoSim.Unity.Shell;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Components
{
    /// <summary>
    /// 2選手の能力比較表（スコアブック・スキン。scorebook.uss の .sbk-cmp*）。
    /// 列は「能力名64｜自チーム側（差し色の棒）｜間24｜比較相手（墨の棒）」で、見出し・能力行・守備適性・
    /// 持ち球チャートが同じ列に乗る。オーダー画面とメンバー設定で共有する（design-22 G2）。
    /// 選手がいない側は線だけ出し、操作説明の文字は出さない。
    /// </summary>
    public static class CompareTable
    {
        /// <summary>見出しの片側1人ぶん。</summary>
        public struct PlayerHeadData
        {
            public bool Present;
            public string Name;
            public string Meta;   // 学年・立場など1行
            public KokoSim.Engine.Nation.PlayerStrength Strength;
            public KokoSim.Engine.Players.SkillSet Skills;
        }

        /// <summary>見出し行（能力名の列は空ける）。</summary>
        public static VisualElement Head(PlayerHeadData a, PlayerHeadData b)
        {
            var head = new VisualElement();
            head.AddToClassList("sbk-cmp__head");
            head.Add(Spacer("sbk-cmp-lab"));
            head.Add(PlayerHead(a, true));
            head.Add(Spacer("sbk-cmp-gap"));
            head.Add(PlayerHead(b, false));
            return head;
        }

        private static VisualElement PlayerHead(PlayerHeadData d, bool own)
        {
            var col = new VisualElement();
            col.AddToClassList("sbk-cmp-col");
            col.AddToClassList("sbk-cmp-pl");
            if (!d.Present)
            {
                col.AddToClassList("sbk-cmp-pl--none");
                return col;
            }
            col.AddToClassList(own ? "acc-bd-top" : "sbk-cmp-pl--b");

            var name = new Label(Nm(d.Name));
            name.AddToClassList("sbk-cmp-pl__name");
            name.AddToClassList("f-display");
            col.Add(name);

            var meta = new Label(d.Meta ?? "");
            meta.AddToClassList("sbk-cmp-pl__meta");
            col.Add(meta);

            // カテゴリ別ランク（打走守投）。選手個人の総合ランクは出さない（Issue #30）。
            col.Add(UiComponents.CategoryRankChipsCompact(d.Strength, small: true));
            // 特殊能力（見えている分だけ。説明はホバー, issue #738）。持っていなければ段ごと出さない。
            var skills = UiComponents.SkillChipsFor(d.Skills, 4);
            if (skills != null) col.Add(skills);
            return col;
        }

        /// <summary>能力の1行：能力名｜自チーム側｜間｜比較相手。</summary>
        public static VisualElement Row(PlayerCompareRow r)
        {
            var row = new VisualElement();
            row.AddToClassList("sbk-cmp-row");
            var lab = new Label(r.Label);
            lab.AddToClassList("sbk-cmp-lab");
            row.Add(lab);
            row.Add(Side(r, StatBarRow.Side.Own));
            row.Add(Spacer("sbk-cmp-gap"));
            row.Add(Side(r, StatBarRow.Side.Other));
            return row;
        }

        private static VisualElement Side(PlayerCompareRow r, StatBarRow.Side side)
        {
            var own = side == StatBarRow.Side.Own;
            VisualElement cell;
            if (!(own ? r.HasA : r.HasB)) cell = StatBarRow.Empty();
            else if (r.HideBar) cell = StatBarRow.TextOnly(own ? r.TextA : r.TextB);   // 弾道（タイプ軸, issue #219）
            else
            {
                var value = own ? r.ValueA : r.ValueB;
                var fill = own ? r.FillA : r.FillB;
                cell = new StatBarRow(own ? r.GradeA : r.GradeB, value, fill >= 0 ? fill : value,
                    side, StatBarRow.OutcomeFor(side, r.Winner));
            }
            cell.AddToClassList("sbk-cmp-col");
            return cell;
        }

        /// <summary>持ち球のミニ球種変化チャートを表の列に並べる（いない側は空セル）。</summary>
        public static VisualElement PitchCharts(bool presentA, System.Collections.Generic.List<PitchChartDatum> a,
            bool presentB, System.Collections.Generic.List<PitchChartDatum> b)
        {
            var host = new VisualElement();
            host.AddToClassList("sbk-cmp__chart");
            host.Add(Spacer("sbk-cmp-lab"));
            host.Add(ChartCell(presentA, a));
            host.Add(Spacer("sbk-cmp-gap"));
            host.Add(ChartCell(presentB, b));
            return host;
        }

        private static VisualElement ChartCell(bool present, System.Collections.Generic.List<PitchChartDatum> pitches)
        {
            var cell = new VisualElement();
            cell.AddToClassList("sbk-cmp__chart-cell");
            if (present) new PitchChartView(cell, compact: true).SetData(pitches);
            return cell;
        }

        public static VisualElement Spacer(string cls)
        {
            var el = new VisualElement();
            el.AddToClassList(cls);
            return el;
        }

        // 人名の姓名の区切り（全角空白）は明朝＋字間で間延びするので、表示だけ半角に詰める。
        private static string Nm(string name) => (name ?? "").Replace('　', ' ');
    }
}
