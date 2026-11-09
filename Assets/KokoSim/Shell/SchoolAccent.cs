using KokoSim.Engine.Nation;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// スクールカラー（画面の差し色）。スコアブック試作スキン（scorebook.uss）の --color-accent を
    /// 自校の配色で実行時に差し替える。USS変数は C# から書き換えられないので、差し色の付く要素は
    /// 4つのクラス（acc-fg / acc-bg / acc-bd / acc-bd-top）で印を付けておき、ここで直接色を流し込む。
    /// </summary>
    public static class SchoolAccent
    {
        /// <summary>既定のスクールカラー（臙脂）。scorebook.uss の --color-accent と同じ値。</summary>
        public static readonly Color Default = new(0x7A / 255f, 0x1F / 255f, 0x2B / 255f);

        /// <summary>
        /// 自校の配色から差し色を1色選ぶ。帽子（つば以外）→アンダーシャツ→ストッキングの順に見て、
        /// 白地の上で文字色・塗りに使える（暗めで色味がある）最初の色を採る。どれも白・灰・黒なら既定の臙脂。
        /// </summary>
        public static Color ForManager()
        {
            var colors = TeamColorProvider.ManagerColors;
            return colors == null ? Default : Pick(colors);
        }

        public static Color Pick(TeamColors colors)
        {
            foreach (var c in new[] { colors.CapCrown, colors.Undershirt, colors.Socks })
                if (Usable(c)) return new Color(c.R / 255f, c.G / 255f, c.B / 255f);
            return Default;
        }

        // 白文字を載せられる暗さ（相対輝度 0.30 以下）で、無彩色ではない（彩度の幅 0.15 以上）。
        // 墨（比較相手の色）と紛れる黒・紺の濃すぎる色も、彩度の条件で多くは落ちる。
        private static bool Usable(RgbColor c)
        {
            float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f;
            var lum = 0.2126f * Lin(r) + 0.7152f * Lin(g) + 0.0722f * Lin(b);
            var chroma = Mathf.Max(r, Mathf.Max(g, b)) - Mathf.Min(r, Mathf.Min(g, b));
            return lum <= 0.30f && chroma >= 0.15f;
        }

        private static float Lin(float v) => v <= 0.04045f ? v / 12.92f : Mathf.Pow((v + 0.055f) / 1.055f, 2.4f);

        /// <summary>root 配下の差し色クラスの付いた要素へ色を流し込む。再描画のたびに呼ぶ。</summary>
        public static void Apply(VisualElement root, Color accent)
        {
            if (root == null) return;
            root.Query<VisualElement>(className: "acc-fg").ForEach(e => e.style.color = accent);
            root.Query<VisualElement>(className: "acc-bg").ForEach(e => e.style.backgroundColor = accent);
            root.Query<VisualElement>(className: "acc-bd").ForEach(e =>
            {
                e.style.borderTopColor = accent; e.style.borderBottomColor = accent;
                e.style.borderLeftColor = accent; e.style.borderRightColor = accent;
            });
            root.Query<VisualElement>(className: "acc-bd-top").ForEach(e => e.style.borderTopColor = accent);
        }
    }
}
