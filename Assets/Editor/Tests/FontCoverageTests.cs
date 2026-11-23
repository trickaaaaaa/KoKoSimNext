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

        /// <summary>
        /// 画面のコードに書いた字は、本文の書体（Noto Sans JP）に字形があること。絵文字（⚡ など）は
        /// どの書体にも無く焼けない＝ブラウザ版で必ず □ になる（Mac/Editor は OS の絵文字で描くので気づけない）。
        /// </summary>
        [Test]
        public void UiText_UsesOnlyCharactersTheBodyFontHas()
        {
            var ttf = AssetDatabase.LoadAssetAtPath<Font>("Assets/UI/Fonts/NotoSansJP-Regular.ttf");
            Assert.AreEqual(FontEngineError.Success, FontEngine.LoadFontFace(ttf, 64));
            // ⅓ ⅔ は投球回の内部の印。画面では LineupSettingController が「整数＋小さな 1/3」に組み直す（書体ルール）。
            var internalMarks = new HashSet<char> { '⅓', '⅔' };
            var noGlyph = FontAssetBuilder.UiTextCharacters()
                .Where(c => !internalMarks.Contains(c))
                .Where(c => !(FontEngine.TryGetGlyphIndex(c, out var index) && index != 0))
                .Distinct().OrderBy(c => c).ToList();
            Assert.IsEmpty(noGlyph,
                "画面のコードに、書体に字形が無い字がある（ブラウザ版で □ になる）。別の字か絵にする:\n"
                + string.Join(" ", noGlyph.Select(c => c + "(U+" + ((int)c).ToString("X4") + ")")));
        }
    }
}
