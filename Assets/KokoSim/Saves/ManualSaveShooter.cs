#if UNITY_EDITOR
using System.Collections;
using System.IO;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Saves
{
    /// <summary>
    /// 【開発用・エディタ限定】オートセーブ廃止＝手動3枠への切り替えの受け入れスクショ（監督決定 2026-08-09）。
    /// ①空のスロット一覧（ホーム経由＝空き枠も選べて保存が出る）②手動1へ保存した後の一覧
    /// ③「タイトルへ戻る」確認（未保存の経過1行）を撮る。使い捨て。
    /// </summary>
    public sealed class ManualSaveShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/manual-save";
        private const int W = 1600;
        private const int H = 900;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);

            // 空のスロットから始める（前回の実行や実プレイのセーブに絵を左右されないため）。
            foreach (var id in SaveSlots.AllSlotIds) SaveSlots.Delete(id);
            SaveGameIO.ResetForNewGame();
            GameSeed.Reset(20260809UL);
            GameClock.Advance(8);

            // ── ① 空き3枠（ホーム経由＝保存ボタンが出て空き枠も選べる） ──
            SaveSlots.Entry = SaveSlotsEntry.Home;
            var doc = Open("SaveSlots");
            if (doc == null) { Debug.LogError("[ManualSave] SaveSlots 画面が無い"); yield break; }
            yield return null; yield return null;
            Debug.Log("[ManualSave] rows=" + RowCount(doc) + "（3であること）");
            yield return Capture(doc, "1-empty-slots");

            // ── ② 手動1へ保存した直後 ──
            var save = doc.rootVisualElement.Q<Button>("ss-save");
            if (save == null) Debug.LogError("[ManualSave] 保存ボタンが無い");
            else Submit(save);
            yield return null; yield return null;
            Debug.Log("[ManualSave] lastSavedAt=" + (SaveGameIO.LastSavedAt is { } a ? a.YearIndex + "/" + a.Week : "(なし)"));
            yield return Capture(doc, "2-saved-manual1");

            // ── ③ 数週進めてから「タイトルへ戻る」確認（未保存の経過が出る） ──
            GameClock.Advance(4);
            var home = Open("HomeDashboard");
            if (home == null) { Debug.LogError("[ManualSave] HomeDashboard が無い"); yield break; }
            yield return null; yield return null;
            Submit(home.rootVisualElement.Q<Button>("hm-menu-btn"));
            yield return null;
            Submit(home.rootVisualElement.Q<Button>("hm-menu-title"));
            yield return null; yield return null;
            var since = home.rootVisualElement.Q<Label>("hm-menu-title-since");
            Debug.Log("[ManualSave] since=" + (since == null ? "(ラベル無し)" : since.text));
            yield return Capture(home, "3-title-confirm");

            Debug.Log("[ManualSave] DONE");
        }

        private static int RowCount(UIDocument doc)
        {
            var host = doc.rootVisualElement.Q<VisualElement>("ss-rows");
            return host == null ? -1 : host.childCount;
        }

        private static void Submit(VisualElement target)
        {
            if (target == null) return;
            using (var e = NavigationSubmitEvent.GetPooled())
            {
                e.target = target;
                target.SendEvent(e);
            }
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
