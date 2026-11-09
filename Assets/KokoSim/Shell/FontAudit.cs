using System.Collections.Generic;
using System.Text;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 書体の監査（design-22 §3）。画面上の文字要素の「解決済みフォント」を数え、許可した書体以外が
    /// 使われている要素を列挙する。共通部品が USS で書体を直に指定していると、既定フォントを替えても
    /// 旧書体が残る＝目視では気づきにくいので、数で確かめる。
    /// 許可: Noto Sans JP（本文・数字）／Shippori Mincho B1（名前・見出し）／DotGothic16（掲示板の中だけ）。
    /// </summary>
    public static class FontAudit
    {
        private static readonly string[] Allowed = { "NotoSansJP", "ShipporiMinchoB1", "DotGothic16" };

        /// <summary>root 配下を数えて1行目に内訳、2行目以降に許可外の要素（書体・クラス・文字）を返す。</summary>
        public static string Report(VisualElement root)
        {
            var counts = new SortedDictionary<string, int>();
            var stray = new StringBuilder();
            var seen = new HashSet<string>();
            root?.Query<TextElement>().ForEach(t =>
            {
                if (string.IsNullOrEmpty(t.text) || !t.visible || t.resolvedStyle.display == DisplayStyle.None) return;
                var def = t.resolvedStyle.unityFontDefinition;
                var name = def.fontAsset != null ? def.fontAsset.name
                    : def.font != null ? "font:" + def.font.name : "(none)";
                counts[name] = counts.TryGetValue(name, out var c) ? c + 1 : 1;
                if (IsAllowed(name)) return;
                var key = name + " | " + string.Join(".", t.GetClasses());
                if (!seen.Add(key)) return;   // 同じ書体×同じクラスは1回だけ書く
                var text = t.text.Length > 16 ? t.text.Substring(0, 16) + "…" : t.text;
                stray.Append("\n  ").Append(key).Append(" | \"").Append(text.Replace("\n", " ")).Append('"');
            });
            var sb = new StringBuilder();
            foreach (var kv in counts) sb.Append(kv.Key).Append('=').Append(kv.Value).Append(' ');
            sb.Append(stray.Length == 0 ? "\n  許可外: なし" : stray.ToString());
            return sb.ToString();
        }

        private static bool IsAllowed(string name)
        {
            foreach (var a in Allowed) if (name.StartsWith(a)) return true;
            return false;
        }
    }
}
