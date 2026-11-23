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

        /// <summary>色味の無い学校（白・灰・黒）の表示用の色（炭色）。</summary>
        public static readonly Color Charcoal = new(0x34 / 255f, 0x30 / 255f, 0x2B / 255f);

        /// <summary>RGB の最大と最小の差がこれ未満なら色味の無い色とみなす（0〜255）。</summary>
        private const int AchromaticSpread = 40;

        /// <summary>白い文字を載せても読める、白とのコントラスト比の下限。</summary>
        private const float MinContrastWithWhite = 4.5f;

        /// <summary>
        /// 自校のスクールカラー（表示用の色）。帽子（つば以外）→アンダーシャツ→ストッキングの順に見て、
        /// 最初の色味のある色を採り、<see cref="ToDisplay"/> で白い文字が読める暗さへ寄せる。
        /// どれも白・灰・黒なら炭色。配色がまだ無いときは既定の臙脂。
        /// </summary>
        public static Color ForManager()
        {
            var colors = TeamColorProvider.ManagerColors;
            return colors == null ? Default : Pick(colors);
        }

        public static Color Pick(TeamColors colors)
        {
            foreach (var c in new[] { colors.CapCrown, colors.Undershirt, colors.Socks })
                if (!IsAchromatic(c)) return ToDisplay(c);
            return Charcoal;
        }

        /// <summary>
        /// スクールカラー → 表示用の色。色味の無い色は炭色に置き換え、それ以外は白とのコントラスト比が
        /// 4.5:1 以上になるまで RGB に 0.92 を掛けて暗くする（白は炭色・黄色はからし色・水色は紺寄りになる）。
        /// </summary>
        public static Color ToDisplay(RgbColor c)
        {
            if (IsAchromatic(c)) return Charcoal;
            float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f;
            for (var i = 0; i < 64 && ContrastWithWhite(r, g, b) < MinContrastWithWhite; i++)
            {
                r *= 0.92f; g *= 0.92f; b *= 0.92f;
            }
            return new Color(r, g, b);
        }

        /// <summary>同じ色の薄い背景（既定は不透明度8%）。</summary>
        public static Color Tint(Color accent, float alpha = 0.08f) => new Color(accent.r, accent.g, accent.b, alpha);

        private static bool IsAchromatic(RgbColor c)
            => Mathf.Max(c.R, Mathf.Max(c.G, c.B)) - Mathf.Min(c.R, Mathf.Min(c.G, c.B)) < AchromaticSpread;

        private static float ContrastWithWhite(float r, float g, float b)
        {
            var lum = 0.2126f * Lin(r) + 0.7152f * Lin(g) + 0.0722f * Lin(b);
            return 1.05f / (lum + 0.05f);
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
            root.Query<VisualElement>(className: "acc-bd-left").ForEach(e => e.style.borderLeftColor = accent);
            root.Query<VisualElement>(className: "acc-tint").ForEach(e => e.style.backgroundColor = Tint(accent));
        }
    }
}
