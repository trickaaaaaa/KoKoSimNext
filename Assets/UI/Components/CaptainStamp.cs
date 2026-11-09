using UnityEngine.UIElements;

namespace KokoSim.Unity.Components
{
    /// <summary>
    /// 主将マーク（スコアブック試作スキン・scorebook.uss の .sbk-captain）。
    /// 差し色の丸枠に「主」の判子。傾きは USS の rotate。枠と字の色は差し色クラス（acc-bd / acc-fg）で、
    /// 実行時のスクールカラー差し替え（SchoolAccent.Apply）に乗る。
    /// </summary>
    public sealed class CaptainStamp : VisualElement
    {
        public CaptainStamp()
        {
            AddToClassList("sbk-captain");
            AddToClassList("acc-bd");
            pickingMode = PickingMode.Ignore;
            var ch = new Label("主");
            ch.AddToClassList("sbk-captain__ch");
            ch.AddToClassList("f-body-bd");
            ch.AddToClassList("acc-fg");
            ch.pickingMode = PickingMode.Ignore;
            Add(ch);
        }
    }
}
