#if UNITY_EDITOR
using System.Collections;
using System.IO;
using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定・使い捨て】打者・走者ヘルメット（issue #966）の寄り＋全景撮影。
    /// 右打者・左打者（鏡映）それぞれで 正面/横/斜め と本番に近い構図、走者ヘルメットの寄りを撮る。
    /// </summary>
    public sealed class DuelBatterHelmetShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/i966";
        private const int W = 1200;
        private const int H = 900;

        private DuelStageRig _rig;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);
            _rig = new DuelStageRig();
            if (!_rig.Available) { Debug.LogError("[i966] rig が組めない"); yield break; }
            _rig.EnsureSize(W, H);
            for (int i = 0; i < 8; i++) { _rig.Tick(1f / 60f); yield return null; }

            yield return Batter("r", false);
            yield return Batter("l", true);

            if (_rig.RunnerHelmetFrameForCapture(new Vector3(6f, 0f, 6f), out var c, out var f, out var r))
            {
                for (int i = 0; i < 6; i++) { _rig.Tick(1f / 60f); yield return null; }
                _rig.RunnerHelmetFrameForCapture(new Vector3(6f, 0f, 6f), out c, out f, out r);
                yield return Three("run", c, f, r, 0.7f);
            }
            else Debug.LogWarning("[i966] 走者枠が無い");
            Debug.Log("[i966] DONE");
        }

        private void OnDisable() { if (_rig != null) { _rig.Dispose(); _rig = null; } }

        private IEnumerator Batter(string tag, bool left)
        {
            _rig.SetHandedness(left, false);
            for (int i = 0; i < 8; i++) { _rig.Tick(1f / 60f); yield return null; }
            if (!_rig.BatterHelmetFrameForCapture(out var c, out var f, out var r)) yield break;
            Debug.Log("[i966] " + tag + " center=" + c + " fwd=" + f + " right=" + r);
            yield return Three(tag, c, f, r, 0.55f);
            // 本番寄りの構図（打者の上半身）: 投手側から
            yield return Shot(tag + "-bust", c + f * 2.2f + Vector3.up * 0.1f, c - Vector3.up * 0.25f, 1.6f);
        }

        private IEnumerator Three(string tag, Vector3 c, Vector3 f, Vector3 r, float h)
        {
            float d = 1.4f;
            yield return Shot(tag + "-front", c + f * d, c, h);
            yield return Shot(tag + "-side", c + r * d, c, h);
            yield return Shot(tag + "-diag", c + (f + r).normalized * d, c, h);
            yield return Shot(tag + "-back", c - f * d + Vector3.up * 0.1f, c, h);
        }

        private IEnumerator Shot(string name, Vector3 cam, Vector3 look, float h)
        {
            _rig.FramePreview(cam, look, h);
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
            Debug.Log("[i966] wrote " + name);
        }
    }
}
#endif
