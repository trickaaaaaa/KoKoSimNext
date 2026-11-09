using UnityEngine.UIElements;

namespace KokoSim.Unity.Components
{
    /// <summary>
    /// 背番号布（スコアブック試作スキン・scorebook.uss の .sbk-patch）。ユニフォームに縫い付けた布の見た目。
    /// 数字は Oswald（数字だけのセル＝書体3役）。0 は未割当として空にする。
    /// </summary>
    public sealed class NumberPatch : Label
    {
        public NumberPatch(int number = 0)
        {
            AddToClassList("sbk-patch");
            AddToClassList("f-num-bd");
            Set(number);
        }

        public void Set(int number) => text = number > 0 ? number.ToString() : "";
    }
}
