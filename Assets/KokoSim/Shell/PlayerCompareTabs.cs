using System;
using System.Collections.Generic;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;

namespace KokoSim.Unity.Shell
{
    /// <summary>比較の1能力行（メンバー設定／スタメン決定 共通, issue #192）。</summary>
    public sealed class PlayerCompareRow
    {
        public string Label = "";
        public int ValueA;
        public int ValueB;
        public bool HasA;
        public bool HasB;
        public int Winner;   // -1=A, 0=互角/該当なし, 1=B
        // 物理量表示（issue #94）: 球速行だけ km/h テキスト＋Levelバー幅を持つ。null/負なら Level 生値表示。
        public string TextA;
        public string TextB;
        public int FillA = -1;
        public int FillB = -1;
        // 弾道など優劣のないタイプ軸（issue #219）: true ならバーを出さず優劣ハイライトもしない
        public bool HideBar;
    }

    /// <summary>
    /// メンバー設定／スタメン決定の2選手比較タブ・比較行ビルドの単一ソース（issue #192）。
    /// タブの AbilityKind 割当自体は <see cref="KokoSim.Engine.Season.AbilityCompareTabs"/>（エンジン・テスト対象）
    /// が単一ソース。ここは表示ラベル・Lead/Mental（AbilityKind外の2項目）・行組み立てを足す。
    /// ①野手能力（打撃＋走塁＋守備＋精神・守備適性ダイヤ併載） ②投手能力。
    /// </summary>
    public static class PlayerCompareTabs
    {
        public static readonly string[] TabLabels = { "野手能力", "投手能力" };

        private static readonly (string Label, Func<DevelopingPlayer, int> Get)[] FielderRows = BuildFielderRows();
        private static readonly (string Label, Func<DevelopingPlayer, int> Get)[] PitcherRows = BuildPitcherRows();

        private static (string, Func<DevelopingPlayer, int>)[] BuildFielderRows()
        {
            var list = new List<(string, Func<DevelopingPlayer, int>)>();
            foreach (var k in AbilityCompareTabs.FielderAbilities)
            {
                var kind = k;   // ラムダのキャプチャ用ローカルコピー
                list.Add((AbilityLabels.Jp(kind), p => p.Level(kind)));
            }
            // Lead・Mental は AbilityKind ではないため、エンジン側の割当表には出てこない（issue #192）。
            list.Add(("リード", p => p.Lead));
            list.Add(("精神力", p => p.Mental));
            return list.ToArray();
        }

        private static (string, Func<DevelopingPlayer, int>)[] BuildPitcherRows()
        {
            var list = new List<(string, Func<DevelopingPlayer, int>)>();
            foreach (var k in AbilityCompareTabs.PitcherAbilities)
            {
                var kind = k;
                list.Add((AbilityLabels.Jp(kind), p => p.Level(kind)));
            }
            return list.ToArray();
        }

        // 球速行の判定と km/h 表示（issue #94）。変換はエンジンの公開APIに一本化（UI側で式を再実装しない）。
        private static readonly string VelocityLabel = AbilityLabels.Jp(AbilityKind.Velocity);
        private static string KmhText(int velLevel)
            => (int)System.Math.Round(PitcherAttributes.VelocityKmhFromLevel(velLevel)) + "km/h";

        // 弾道はゴロ型〜フライ型のタイプ軸で優劣軸ではない（issue #219）。数値・バーではなくタイプラベルで表示する。
        private static readonly string LaunchTendencyLabel = AbilityLabels.Jp(AbilityKind.LaunchTendency);

        /// <summary>指定タブ（0=野手能力/1=投手能力）の比較行を組む。a/b は未選択なら null。</summary>
        public static List<PlayerCompareRow> BuildRows(int tab, DevelopingPlayer a, DevelopingPlayer b)
        {
            var rows = new List<PlayerCompareRow>();
            if (a == null && b == null) return rows;

            var table = tab == 0 ? FielderRows : PitcherRows;
            foreach (var (label, get) in table)
            {
                var row = new PlayerCompareRow { Label = label, HasA = a != null, HasB = b != null };
                if (a != null) row.ValueA = get(a);
                if (b != null) row.ValueB = get(b);
                if (a != null && b != null)
                    row.Winner = row.ValueA > row.ValueB ? -1 : row.ValueA < row.ValueB ? 1 : 0;
                if (label == VelocityLabel)
                {
                    if (a != null) { row.TextA = KmhText(row.ValueA); row.FillA = row.ValueA; }
                    if (b != null) { row.TextB = KmhText(row.ValueB); row.FillB = row.ValueB; }
                }
                else if (label == LaunchTendencyLabel)
                {
                    row.HideBar = true;
                    row.Winner = 0;
                    if (a != null) row.TextA = LaunchTendencyLabels.Jp(row.ValueA);
                    if (b != null) row.TextB = LaunchTendencyLabels.Jp(row.ValueB);
                }
                rows.Add(row);
            }
            return rows;
        }
    }
}
