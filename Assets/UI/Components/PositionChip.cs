using UnityEngine.UIElements;

namespace KokoSim.Unity.Components
{
    /// <summary>
    /// 守備位置チップ（スコアブック試作スキン・scorebook.uss の .sbk-pos）。
    /// 系統で色分けする：投手／捕手／内野（一二三遊）／外野（左中右）／指名打者。
    /// 出してよいのは「プレイヤーが決めた配置」か「試合で実際に就いた位置」だけ（CLAUDE.md UI原則の注記）。
    /// 押せるチップにするときは <see cref="Clickable"/> を true にする（ホバーで枠が出る）。
    /// </summary>
    public sealed class PositionChip : Label
    {
        private static readonly string[] Groups = { "p", "c", "if", "of", "dh" };

        public PositionChip(string kanji)
        {
            AddToClassList("sbk-pos");
            AddToClassList("f-body-bd");
            Set(kanji);
        }

        /// <summary>表示する守備位置（漢字1字）を差し替える。系統の色も付け替える。</summary>
        public void Set(string kanji)
        {
            text = kanji ?? "";
            foreach (var g in Groups) RemoveFromClassList("sbk-pos--" + g);
            AddToClassList("sbk-pos--" + GroupOf(text));
        }

        /// <summary>1つ目に選ばれている（次のチップで入替）状態。</summary>
        public bool Picked { set => EnableInClassList("sbk-pos--picked", value); }

        /// <summary>ボタンとして押せるチップか（ホバーの枠）。</summary>
        public bool Clickable { set => EnableInClassList("sbk-pos--button", value); }

        /// <summary>守備位置の漢字→系統クラスの接尾辞。未知の字は内野扱い。</summary>
        public static string GroupOf(string kanji) => kanji switch
        {
            "投" => "p",
            "捕" => "c",
            "左" or "中" or "右" => "of",
            "指" => "dh",
            _ => "if",
        };
    }
}
