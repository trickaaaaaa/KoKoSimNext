#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using KokoSim.Engine.Career;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Home
{
    /// <summary>
    /// 【開発用・エディタ限定】監督オファー画面（設計書04 §1.3・issue「監督の転任システムが実ゲームに完全未配線」）の
    /// 受け入れスクショ。強制転任（残留不可・候補1件）／フリー化（残留可・候補あり、行選択で詳細が切り替わる様子）の
    /// 2パターンを ManagerTransferService.Open へ直接候補を注入して撮る。使い捨て。
    /// </summary>
    public sealed class TransferOfferShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/transfer-offer";
        private const int W = 1600;
        private const int H = 900;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);
            for (var i = 0; i < 8; i++) { yield return null; yield return new WaitForEndOfFrame(); }

            SaveGameIO.ResetForNewGame();

            var home = Open("HomeDashboard");
            if (home == null) { Debug.LogError("[XT] HomeDashboard が見つからない"); yield break; }

            // ① 強制転任: 残留不可・候補1件だけ。
            ManagerTransferService.Open(new TransferResult(
                TransferEventKind.ForcedTransfer,
                new List<TransferCandidate> { new TransferCandidate(501, "静岡実業", 21, null, 33.0, 4.0) },
                CanStay: false));
            home.gameObject.SetActive(false); home.gameObject.SetActive(true);
            yield return null; yield return null;
            yield return Capture(home, "00-forced-transfer");

            // ② フリー化: 残留可・オファー2件。1件目（残留）で撮った後、2件目へ選択を切り替えてもう1枚。
            ManagerTransferService.Open(new TransferResult(
                TransferEventKind.BecameFreeAgent,
                new List<TransferCandidate>
                {
                    new TransferCandidate(701, "神奈川学園", 13, null, 68.5, 22.0),
                    new TransferCandidate(702, "海浜工業", 27, 2, 51.0, 9.0),
                },
                CanStay: true));
            home.gameObject.SetActive(false); home.gameObject.SetActive(true);
            yield return null; yield return null;
            yield return Capture(home, "01-became-free-agent-stay-selected");

            var ctrl = home.GetComponent<HomeDashboardController>();
            var pickField = ctrl.GetType().GetField("_xtPick",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var renderMethod = ctrl.GetType().GetMethod("RenderTransferOffer",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (pickField != null && renderMethod != null)
            {
                pickField.SetValue(ctrl, 701);
                renderMethod.Invoke(ctrl, new object[] { ManagerTransferService.BuildView() });
                yield return null; yield return null;
                yield return Capture(home, "02-became-free-agent-school-selected");
            }
            else Debug.LogWarning("[XT] リフレクションで選択を切り替えられなかった（private フィールド名の変更?）");

            ManagerTransferService.Clear();
            Debug.Log("[XT] DONE all shots");
        }

        private static UIDocument Open(string screen)
        {
            UIDocument target = null;
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                if (d.gameObject.name == screen) target = d;
                d.gameObject.SetActive(false);
            }
            if (target != null) target.gameObject.SetActive(true);
            return target;
        }

        private IEnumerator Capture(UIDocument doc, string file)
        {
            var ps = doc.panelSettings;
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
                Debug.Log("[XT] wrote " + file);
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); Destroy(rt);
                if (tex != null) Destroy(tex);
            }
        }
    }
}
#endif
