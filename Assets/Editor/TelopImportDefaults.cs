using UnityEditor;
using UnityEngine;

namespace KokoSim.EditorTools
{
    /// <summary>
    /// 試合テロップ画像（Assets/UI/Telops 配下、issue #1179）の初回インポート既定。
    ///
    /// <para>素材は透過PNGで、UI Toolkitの画像テロップ（プレイボール・判定・ホームラン・ゲームセット）に
    /// そのまま貼る。Texture Type＝Sprite (2D and UI)・ミップマップなし・圧縮なし（issue本文の指定）に
    /// 固定する。いちばん広い素材（playball_face.png 2086×503・gameset_face.png 2096×496 等）が
    /// 既定の maxTextureSize(2048) では縮小されてしまうため、4096へ上げる。</para>
    ///
    /// <para><see cref="DuelMotionImportDefaults"/>（モーション）は importSettingsMissing（初回のみ）で
    /// 当てるが、ここは常に当てる。Telop画像は全部が同じ既定で揃っていればよく（クリップごとの手調整が要る
    /// モーションと違い、画像ごとに違う設定が要る理由が無い）、毎回当てたほうが「新しい画像を増やし忘れて
    /// 既定インポート(Texture Type=Default)のまま紛れ込む」事故を防げる。既存素材（PR #1183 取り込み時点では
    /// 既定インポートの .meta が先にコミットされていた）は、この postprocessor を書いた際に一度 .meta を
    /// 直接書き換えて揃えてある＝以後はここが単一ソース。</para>
    /// </summary>
    public sealed class TelopImportDefaults : AssetPostprocessor
    {
        private const string TelopRoot = "Assets/UI/Telops/";

        private void OnPreprocessTexture()
        {
            var ti = assetImporter as TextureImporter;
            if (ti == null) return;
            if (!assetPath.StartsWith(TelopRoot, System.StringComparison.Ordinal)) return;

            ti.textureType = TextureImporterType.Sprite;
            ti.spriteImportMode = SpriteImportMode.Single;
            ti.mipmapEnabled = false;
            ti.alphaIsTransparency = true;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.filterMode = FilterMode.Bilinear;
            ti.maxTextureSize = 4096;
            ti.textureCompression = TextureImporterCompression.Uncompressed;
        }
    }
}
