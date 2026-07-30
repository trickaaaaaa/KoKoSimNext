#if UNITY_EDITOR
using System.Collections;
using System.IO;
using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定・使い捨て】issue #337（左右ミラー）の受け入れ撮影。
    /// 4通り（左右打者×左右投手）の立ち絵と、左投手の投球モーション（ルートモーション実測込み）、
    /// 右/右の往復復元（回帰）を1回の Play で撮り切る。確認後に削除してよい。
    /// </summary>
    public sealed class Duel337Shooter : MonoBehaviour
    {
        private const string Dir = "screenshots/i337";
        private const int W = 1600;
        private const int H = 900;

        private DuelStageRig _rig;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);

            _rig = new DuelStageRig();
            if (!_rig.Available) { Debug.LogError("[i337] rig が組めない"); yield break; }
            _rig.EnsureSize(W, H);
            for (int i = 0; i < 8; i++) { _rig.Tick(1f / 60f); yield return null; }

            // ① 基準（右打者×右投手）— 恒等ノード挿入後の絵。これが従来と変わっていないことは
            //    PitcherDiag の数値（従来実測と同値）と絵の目視で確認する
            yield return Combo("rr", false, false);

            // ② 左打者×右投手 / ③ 右打者×左投手 / ④ 左打者×左投手
            yield return Combo("lr", true, false);
            yield return Combo("rl", false, true);
            yield return Combo("ll", true, true);

            // ⑤ 回帰: 右/右へ戻して基準と同じ絵に復元されること（バウンズ復元も含む）
            yield return Combo("rr-restored", false, false);

            // ⑥ 左投手の投球モーション: アンカー同期の実駆動（OnPitchWindup→リリース→フォロー）。
            //    位置・向きのずれは絵では切り分けられないので PitcherDiag を各コマで残す（design-18 §7-2）
            _rig.SetHandedness(batterLeft: true, pitcherLeft: true);
            _rig.OnPitchWindup();
            float releaseIn = DuelStageRig.WindupSeconds;   // ワインドアップ開始→リリースまで
            var marks = new[] { 0.4f, 0.8f, 1.2f, releaseIn, releaseIn + 0.35f };
            float t = 0f;
            for (int m = 0; m < marks.Length; m++)
            {
                while (t < marks[m]) { _rig.Tick(1f / 60f); t += 1f / 60f; yield return null; }
                Debug.Log("[i337] pitch t=" + t.ToString("F2") + " " + _rig.PitcherDiagForCapture());
                yield return Shot("60-pitch-ll-t" + m, Wide);
            }

            Debug.Log("[i337] DONE");
        }

        private void OnDisable()
        {
            if (_rig != null) { _rig.Dispose(); _rig = null; }
        }

        /// <summary>1組の左右で 全景＋打者寄り＋投手寄り を撮り、実測ログを残す。</summary>
        private IEnumerator Combo(string tag, bool batterLeft, bool pitcherLeft)
        {
            _rig.SetHandedness(batterLeft, pitcherLeft);
            for (int i = 0; i < 6; i++) { _rig.Tick(1f / 60f); yield return null; }
            Debug.Log("[i337] === " + tag + " (batterLeft=" + batterLeft + " pitcherLeft=" + pitcherLeft + ") ===\n"
                + _rig.GloveDiagForCapture()
                + "[i337] " + _rig.PitcherDiagForCapture());
            yield return Shot(tag + "-0wide", Wide);
            yield return Shot(tag + "-1batter", BatterClose);
            yield return Shot(tag + "-2pitcher", PitcherClose);
        }

        // 構図（狙点・距離・縦収め実寸[m]）。カメラは実装のR-comp60でなく検証用の寄り。
        private static readonly (Vector3 cam, Vector3 look, float h) Wide =
            (new Vector3(0f, 1.3f, 22.94f), new Vector3(0f, 0.9f, 9f), 4.5f);
        private static readonly (Vector3 cam, Vector3 look, float h) BatterClose =
            (new Vector3(0f, 1.1f, 2.6f), new Vector3(0f, 0.9f, 0f), 2.4f);
        private static readonly (Vector3 cam, Vector3 look, float h) PitcherClose =
            (new Vector3(0f, 1.2f, 16.0f), new Vector3(0f, 1.0f, 18.44f), 2.4f);

        private IEnumerator Shot(string name, (Vector3 cam, Vector3 look, float h) f)
        {
            _rig.FramePreview(f.cam, f.look, f.h);
            yield return null;
            yield return new WaitForEndOfFrame();
            yield return null;

            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            var prev = RenderTexture.active;
            RenderTexture.active = _rig.Target;
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(Path.Combine(Dir, name + ".png"), tex.EncodeToPNG());
            Destroy(tex);
            Debug.Log("[i337] wrote " + name);
        }
    }
}
#endif
