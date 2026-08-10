#if UNITY_EDITOR
using System.Collections;
using System.IO;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Saves
{
    /// <summary>
    /// 【開発用・エディタ限定】共通ナビの「システム」タブ（issue #565・A案）を撮る。使い捨て。
    ///
    /// <para>撮るのは4枚。①通常週にタブから開いた姿（保存できる）②大会中にタブから開いた姿
    /// （大会中でも保存できることの確認）③タイトル経由（トップバーが出ないこと＝入口で見た目が変わる）
    /// ④壊れた枠を選んだ姿（issue #575①＝案内文は本文色・本当の警告だけ警告色）。</para>
    ///
    /// <para><b>タブ経由とホームの ≡メニュー経由で見た目が変わる</b>のがこの変更の肝なので、
    /// 「開けた」ではなく<b>タブ帯が出ているか・戻るボタンが消えているか</b>を絵で見る。</para>
    /// </summary>
    public sealed class SystemTabShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/system-tab";
        private const int W = 1600;
        private const int H = 900;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);

            SaveGameIO.ResetForNewGame();
            GameSeed.Reset(20260809UL);
            GameClock.Advance(8);

            // ① 通常週にタブから開く（入口は Show の前に立てる＝OnEnable が読む）。
            SaveSlots.Entry = SaveSlotsEntry.Tab;
            var doc = Open("SaveSlots");
            if (doc == null) { Debug.LogError("[SysTab] SaveSlots 画面が無い"); yield break; }
            yield return null; yield return null;
            Debug.Log("[SysTab] 通常週 canSave=" + SaveGameIO.CanSave
                      + " topbar=" + Shown(doc, "ss-topbar") + " back=" + Shown(doc, "ss-back"));
            yield return Capture(doc, "1-normal-week");

            // ② 大会中にタブから開く（大会開幕まで週を送り、バナーを閉じてから）。
            Open("HomeDashboard");
            yield return null;
            for (var i = 0; i < 40 && GameSession.Current.Mode != GameMode.Tournament; i++)
            {
                GameClock.Advance(1);
                yield return null;
            }
            GameSession.Current.ConsumeBanner();
            SaveSlots.Entry = SaveSlotsEntry.Tab;
            doc = Open("SaveSlots");
            yield return null; yield return null;
            Debug.Log("[SysTab] 大会中 mode=" + GameSession.Current.Mode + " canSave=" + SaveGameIO.CanSave
                      + " reason=" + SaveGameIO.SaveDisabledReason());
            yield return Capture(doc, "2-in-tournament");

            // ③ タイトル経由（トップバーを出さない＝シェルの遅延初期化を起こさない入口）。
            SaveSlots.Entry = SaveSlotsEntry.Title;
            doc = Open("SaveSlots");
            yield return null; yield return null;
            Debug.Log("[SysTab] タイトル経由 topbar=" + Shown(doc, "ss-topbar") + " back=" + Shown(doc, "ss-back"));
            yield return Capture(doc, "3-from-title");

            // ④ 壊れた枠を選んだ姿（issue #575①）。案内文を本文色へ落としたあとも、
            //    本当の警告＝「読み込めません」だけは警告色で残ることを絵で確かめる。
            File.WriteAllText(SaveSlots.PathFor("manual2"), "{ これはセーブではない");
            SaveSlots.Entry = SaveSlotsEntry.Tab;
            doc = Open("SaveSlots");
            yield return null;
            var ctrl = doc.GetComponent<SaveSlotsController>();
            if (ctrl != null) ctrl.Select("manual2");
            yield return null; yield return null;
            Debug.Log("[SysTab] 壊れた枠 corrupt=" + SaveSlots.ReadInfo("manual2").Corrupt);
            yield return Capture(doc, "4-corrupt");
            SaveSlots.Delete("manual2");

            Debug.Log("[SysTab] DONE");
        }

        private static string Shown(UIDocument doc, string name)
        {
            var e = doc.rootVisualElement.Q<VisualElement>(name);
            return e == null ? "(無し)" : (e.style.display == DisplayStyle.None ? "非表示" : "表示");
        }

        /// <summary>
        /// 実機と同じ経路（<see cref="ScreenRouter.Show"/>）で画面を出す。
        /// <b>自前で SetActive しない</b>＝ルータがやるタブのハイライト付けを飛ばしてしまい、
        /// 「タブが光っていない絵」を撮ってしまう（実測 2026-08-10）。
        /// 直前に別画面を挟むのは、同じ画面を SetActive(true) しても OnEnable が再発火せず
        /// 入口（<see cref="SaveSlotsEntry"/>）の切り替えが反映されないため。
        /// </summary>
        private static UIDocument Open(string screen)
        {
            var router = ScreenRouter.Instance;
            if (router != null)
            {
                if (screen != "HomeDashboard") router.Show("HomeDashboard");
                router.Show(screen);
            }
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                if (d.gameObject.name == screen) return d;
            }
            return null;
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
