#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Players
{
    /// <summary>
    /// 【開発用・エディタ限定】投手能力の表示揃え（issue #561）のスクショ確認用。使い捨て。
    /// 選手詳細（能力タブ）と練習計画（能力タブ・モーダル）で、スタミナがレベル値になり
    /// 選手詳細に球威の行が出ることを撮る。持ち球の多い投手（背番号10）を選ぶ。
    /// </summary>
    public sealed class PitcherDisplayShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string Dir = "screenshots/issue-561";
        private const int PitcherIndex = 16;   // 背番号10・変化球3種

        private static readonly BindingFlags Priv = BindingFlags.Instance | BindingFlags.NonPublic;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);

            // ── 選手詳細（PlayerList と同じ GameObject 上の右ペイン） ──
            var list = Activate("PlayerList");
            if (list == null) { Debug.LogError("[PitcherShooter] PlayerList が見つからない"); yield break; }
            yield return null;

            PlayerSelection.Index = PitcherIndex;
            var detail = list.GetComponent<PlayerDetailController>();
            detail?.RenderForSelection();
            yield return Capture(list, "detail-pitcher");

            // ── 練習計画（能力タブをモーダルで開く） ──
            var training = Activate("TrainingPlan");
            if (training == null) { Debug.LogError("[PitcherShooter] TrainingPlan が見つからない"); yield break; }
            yield return null;

            // OpenModal は必ず配分タブから開く（_abilityTab=false を書く）ので、開いた後に能力タブへ倒して Render。
            training.GetType().GetMethod("OpenModal", Priv)?.Invoke(training, new object[] { PitcherIndex });
            training.GetType().GetField("_abilityTab", Priv)?.SetValue(training, true);
            training.GetType().GetMethod("Render", Priv)?.Invoke(training, null);
            yield return Capture(training, "training-pitcher");

            Debug.Log("[PitcherShooter] DONE");
        }

        private IEnumerator Capture(MonoBehaviour ctrl, string file)
        {
            yield return null;
            yield return new WaitForEndOfFrame();

            var ps = ctrl.GetComponent<UIDocument>().panelSettings;
            var prevRt = ps.targetTexture; var prevScale = ps.scaleMode; var prevRef = ps.referenceResolution;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32); rt.Create();
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(W, H);
            ps.targetTexture = rt;

            yield return null;
            yield return new WaitForEndOfFrame();
            yield return null;
            yield return new WaitForEndOfFrame();

            Texture2D tex = null;
            try
            {
                tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
                var prevActive = RenderTexture.active;
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                tex.Apply();
                RenderTexture.active = prevActive;
                File.WriteAllBytes(Path.Combine(Dir, file + ".png"), tex.EncodeToPNG());
                Debug.Log("[PitcherShooter] wrote " + file);
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); Destroy(rt);
                if (tex != null) Destroy(tex);
            }
        }

        // 対象画面だけを表示する（他画面の UIDocument が重なると描画が競合する）。
        private static MonoBehaviour Activate(string screen)
        {
            MonoBehaviour found = null;
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                var on = d.gameObject.name == screen;
                d.gameObject.SetActive(on);
                if (!on) continue;
                foreach (var mb in d.GetComponents<MonoBehaviour>())
                    if (mb != null && mb.GetType().Name.EndsWith("Controller")) found = mb;
            }
            return found;
        }
    }
}
#endif
