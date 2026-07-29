#if UNITY_EDITOR
using System.Collections;
using System.IO;
using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定・使い捨て】チーム設定の立ち姿プレビューを高解像度で寄って撮る診断ハーネス。
    /// UIを通さず <see cref="DuelStageRig"/> のRTを直接読む＝画面レイアウトの都合と切り離して
    /// 「グラブの装着」「ベルトの見え」だけを判定できるようにする。
    ///
    /// 狙いは目測でなく <see cref="DuelStageRig.PreviewProbeForCapture"/> の実測値から決める
    /// （カメラの狙い外しと未装着は絵では区別できない＝グラブ検証の教訓）。
    /// </summary>
    public sealed class DuelPreviewProbeShooter : MonoBehaviour
    {
        private const string Dir = "screenshots";
        private const int W = 1440;
        private const int H = 1080;   // 4:3（EnsureSize が許す最も縦長）

        /// <summary>出力ファイル名の頭。連続検証で上書きしないよう起動側が変える。</summary>
        public static string Prefix = "i-duelprev";

        /// <summary>
        /// 狙点からカメラへ向かう方向（TeamSettingController の構図と同じ＝投手の正面から見る）。
        /// 符号は camPos - lookAt で取る。逆にすると背面が撮れるが、絵だけでは正面と背面の区別が
        /// つきにくい（帽子・のっぺらぼうの頭は前後が似ている）ので必ず式で確かめる。
        /// </summary>
        private static readonly Vector3 ViewDir =
            new Vector3(-2.35f, 0.20f, -1.59f).normalized;

        private DuelStageRig _rig;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);

            _rig = new DuelStageRig();
            if (!_rig.Available) { Debug.LogError("[PrevProbe] rig が組めない"); yield break; }
            _rig.PosePreviewStanding();
            _rig.EnsureSize(W, H);
            for (int i = 0; i < 8; i++) { _rig.Tick(1f / 60f); yield return null; }

            Vector3 palm, glove, hips; Quaternion gloveRot; string handInGlove;
            _rig.PreviewProbeForCapture(out palm, out glove, out hips, out gloveRot, out handInGlove);
            Debug.Log("[PrevProbe] 掌=" + palm.ToString("F3")
                + " グラブ=" + glove.ToString("F3")
                + " 腰=" + hips.ToString("F3")
                + " 距離=" + Vector3.Distance(palm, glove).ToString("F3") + "m"
                + "\n[PrevProbe] グラブ座標系での手: " + handInGlove);

            // ベルトは正面がバックル・横が帯なので、腰は正面と横の2枚を撮る（片方だけだと
            // 「バックルはあるが帯が無い」のような欠けを見落とす）
            var side = Vector3.Cross(Vector3.up, ViewDir).normalized + Vector3.up * 0.08f;
            yield return Shot("0-body", new Vector3(0f, 1.10f, 18.44f), 2.84f, 1.85f);
            yield return Shot("1-glove", glove, 0.70f, 0.42f);
            yield return Shot("2-waist", hips, 0.90f, 0.60f);
            yield return ShotDir("3-waist-side", hips, 0.90f, 0.60f, side.normalized);
            // グラブは**装備自身の軸**からも撮る。斜めからの絵では手とグラブの前後・左右が
            // 分離できず、「ずれている」のか「そう見えるだけ」なのか判定できない
            yield return ShotDir("4-glove-pocket", glove, 0.55f, 0.42f, gloveRot * Vector3.forward);
            yield return ShotDir("5-glove-side", glove, 0.55f, 0.42f, gloveRot * Vector3.right);

            Debug.Log("[PrevProbe] DONE");
        }

        private void OnDisable()
        {
            if (_rig != null) { _rig.Dispose(); _rig = null; }
        }

        private IEnumerator Shot(string name, Vector3 target, float radius, float frameHeightM)
            => ShotDir(name, target, radius, frameHeightM, ViewDir);

        /// <summary>狙点 target から dir 方向に radius[m] 離れて、縦 frameHeightM[m] を収めて撮る。</summary>
        private IEnumerator ShotDir(string name, Vector3 target, float radius, float frameHeightM, Vector3 dir)
        {
            _rig.FramePreview(target + dir * radius, target, frameHeightM);
            for (int i = 0; i < 4; i++) { _rig.Tick(1f / 60f); yield return null; }
            yield return new WaitForEndOfFrame();

            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            var prev = RenderTexture.active;
            RenderTexture.active = _rig.Target;
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(Path.Combine(Dir, Prefix + "-" + name + ".png"), tex.EncodeToPNG());
            Destroy(tex);
            Debug.Log("[PrevProbe] wrote " + name);
        }
    }
}
#endif
