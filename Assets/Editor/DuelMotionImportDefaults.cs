using System.IO;
using UnityEditor;
using UnityEngine;

namespace KokoSim.EditorTools
{
    /// <summary>
    /// 自作モーション（Assets/Motions 配下＝Rokoko収録FBX）の初回インポート既定。
    ///
    /// 対決ビューは素体（UBC SuperheroMale）へのHumanoidリターゲット前提なので、収録FBXは
    /// 「自分のアバターを作るHumanoid・アニメーションだけ使う」形に揃える必要がある。これを手作業の
    /// Inspector操作でなくコードで固定する（設計書18の掟＝アセット構成はスクリプトで再現可能にする）。
    ///
    /// クリップ側は名前以外を既定のまま通す。ルート姿勢の扱い（Bake Into Pose / Based Upon）を
    /// 購入パック（AAは既定のまま運用）と揃えるためで、素の向きの違いは DuelStageRig 側の
    /// yaw定数（RunFacingProbe実測）で吸収する＝クリップ内部には手を入れない。
    ///
    /// 当てるのは importSettingsMissing のとき（＝.metaがまだ無い初回）だけ。以後の微調整は
    /// Inspector／個別スクリプトが勝ち、再インポートで巻き戻らない。
    /// </summary>
    public sealed class DuelMotionImportDefaults : AssetPostprocessor
    {
        private const string MotionRoot = "Assets/Motions/";

        private bool? _apply;

        private bool ShouldApply()
        {
            if (_apply == null)
            {
                var mi = assetImporter as ModelImporter;
                _apply = mi != null
                    && assetPath.StartsWith(MotionRoot, System.StringComparison.Ordinal)
                    && mi.importSettingsMissing;
            }
            return _apply.Value;
        }

        private void OnPreprocessModel()
        {
            if (!ShouldApply()) return;
            var mi = (ModelImporter)assetImporter;

            mi.animationType = ModelImporterAnimationType.Human;
            mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            mi.importAnimation = true;

            // 収録アクターの見た目は使わない（素体へリターゲットする）。マテリアル等は持ち込まない
            mi.materialImportMode = ModelImporterMaterialImportMode.None;
            mi.importCameras = false;
            mi.importLights = false;

            // モーキャップの緩急をそのまま保持する（掟: クリップ内部の緩急を改変しない）
            mi.animationCompression = ModelImporterAnimationCompression.Off;
        }

        private void OnPreprocessAnimation()
        {
            if (!ShouldApply()) return;
            var mi = (ModelImporter)assetImporter;

            var clips = mi.defaultClipAnimations;
            if (clips == null || clips.Length == 0) return;

            // クリップ名は収録テイク名（Take 001 等）でなくファイル名基準にする。
            // DuelStageRig.LoadClip が名前引きするため、供給元をまたいで一意で安定した名前が要る。
            var baseName = Path.GetFileNameWithoutExtension(assetPath);
            for (int i = 0; i < clips.Length; i++)
                clips[i].name = clips.Length == 1 ? baseName : baseName + "_" + (i + 1).ToString("00");
            mi.clipAnimations = clips;
        }
    }
}
