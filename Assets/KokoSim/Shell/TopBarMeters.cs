using KokoSim.Engine.Nation;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 共通トップバー右側メーター（部費残高・名声・信頼度）の一括埋め（issue #172 追補）。
    /// 値は <see cref="ManagerService.Manager"/> が単一ソースで、名声・信頼度は全画面共通の
    /// <see cref="Tiers.FromStrength"/>（S〜G）で文字化する。旧来のプレースホルダ（HomeState の
    /// ハードコード "D"/"C"・他画面の UXML 既定値）を置き換え、#171 で配線した監督成長が
    /// どの画面でも常時見えるようにする。各画面のコントローラが ScoreboardStrip.Fill と並べて呼ぶ。
    /// </summary>
    public static class TopBarMeters
    {
        public static void Fill(VisualElement root)
        {
            if (root == null) return;
            var m = ManagerService.Manager;
            Set(root, "funds", "¥" + m.Funds.ToString("0") + "万");
            Set(root, "fame", Tiers.FromStrength(m.Fame).ToString());
            Set(root, "trust", Tiers.FromStrength(m.Trust).ToString());
        }

        private static void Set(VisualElement root, string name, string text)
        {
            var label = root.Q<Label>(name);
            if (label != null) label.text = text;
        }
    }
}
