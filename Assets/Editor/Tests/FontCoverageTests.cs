using System.Collections.Generic;
using System.Linq;
using KokoSim.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.TextCore.Text;

namespace KokoSim.EditorTests
{
    /// <summary>
    /// 使っている文字（data/ の YAML・画面のコードの文字列・UXML）が、焼いたフォント（Static のアトラス）に全部入っているか。
    ///
    /// <para>Editor と Mac は足りない字を OS のフォントで代わりに描くので気づけないが、ブラウザ版はそれが無く □ になる
    /// （2026-11-23 の Web お試しビルドで ▶ ¥ ℃ や会話の漢字「礼・挨拶・噂」などが □ だった。data/ に文言を足したあと
    /// 焼き直していなかった）。赤くなったら「KokoSim/Fonts/本番用の SDF を生成」を回す。</para>
    ///
    /// <para>元の書体そのものに字形が無い字（絵文字など）は焼けないので対象外。</para>
    /// </summary>
    public class FontCoverageTests
    {
        private static readonly string[] Fonts =
        {
            "NotoSansJP-Regular", "NotoSansJP-Medium", "NotoSansJP-Bold",
            "ShipporiMinchoB1-Bold", "ShipporiMinchoB1-ExtraBold", "DotGothic16-Regular",
        };

        [TestCaseSource(nameof(Fonts))]
        public void EveryUsedCharacter_IsBakedIntoTheFont(string font)
        {
            var asset = AssetDatabase.LoadAssetAtPath<FontAsset>("Assets/UI/Fonts/Generated/" + font + ".asset");
            Assert.IsNotNull(asset, font + " の SDF が無い");
            var ttf = AssetDatabase.LoadAssetAtPath<Font>("Assets/UI/Fonts/" + font + ".ttf");
            Assert.IsNotNull(ttf, font + " の TTF が無い");

            var baked = new HashSet<uint>(asset.characterTable.Select(c => c.unicode));
            Assert.AreEqual(FontEngineError.Success, FontEngine.LoadFontFace(ttf, 64));
            var missing = FontAssetBuilder.ProductionCharacterSet()
                .Where(c => !char.IsWhiteSpace(c) && !baked.Contains(c))
                .Where(c => FontEngine.TryGetGlyphIndex(c, out var index) && index != 0)   // 元の書体にある字だけ
                .Distinct()
                .OrderBy(c => c)
                .ToList();

            Assert.IsEmpty(missing,
                font + " に焼かれていない字が " + missing.Count + " 字ある（ブラウザ版で □ になる）。"
                + "KokoSim/Fonts/本番用の SDF を生成 を回す:\n" + new string(missing.Take(200).ToArray()));
        }
    }
}
