#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using KokoSim.Engine.Players;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Players
{
    /// <summary>
    /// 【開発用・エディタ限定・使い捨て】issue #935: 性格の4軸・バッテリーの相性・結束の表示を撮る。
    /// 選手詳細（3軸だけ分かった選手／4軸そろった選手）・チーム総合力パネル・スタメン設定・主将指名。
    /// 見極めの読みとバッテリーの積み重ねはここで値を入れて場面を作る。
    /// </summary>
    public sealed class Issue935Shooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        public static string Dir = "screenshots/issue-935";
        private static readonly BindingFlags Priv = BindingFlags.Instance | BindingFlags.NonPublic;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);
            for (int i = 0; i < 4; i++) { yield return null; yield return new WaitForEndOfFrame(); }

            var roster = RosterService.Roster;
            var active = RosterService.Active;
            // 見極めの読み: 0番は3軸（素直・勤勉は高い側、姿勢は低い側＝目立ちたがり、統率は未判明）、1番は4軸そろい
            active[0].PersonalityReading = default(PersonalityReading)
                .With(PersonalityAxis.Receptiveness, true).With(PersonalityAxis.Diligence, true)
                .With(PersonalityAxis.Selflessness, false);
            active[1].PersonalityReading = default(PersonalityReading)
                .With(PersonalityAxis.Receptiveness, false).With(PersonalityAxis.Diligence, true)
                .With(PersonalityAxis.Selflessness, true).With(PersonalityAxis.Leadership, true);
            // バッテリーの積み重ね: 部員どうしの全組に少しずつ（捕手がだれになっても棒が出る）
            foreach (var p in active)
                foreach (var c in active)
                    if (p.Id != c.Id) GameSession.Current.BatteryLedger.Add(p.Id, c.Id, (p.Id * 7 + c.Id * 3) % 90);

            // ── 選手詳細 ──
            var list = Activate("PlayerList");
            yield return null;
            var detail = list.GetComponent<PlayerDetailController>();
            for (int k = 0; k < 2; k++)
            {
                PlayerSelection.Index = k;
                detail?.RenderForSelection();
                yield return Capture(list, "detail-" + k, 1500);
            }

            // ── チーム総合力パネル ──
            var ts = Activate("TeamStrength");
            yield return null;
            ts.GetType().GetMethod("Render", Priv)?.Invoke(ts, null);
            yield return Capture(ts, "team-strength", 1400);

            // ── スタメン設定 ──
            var lu = Activate("LineupSetting");
            yield return null;
            yield return Capture(lu, "lineup");

            // ── 主将指名 ──
            var home = Activate("HomeDashboard");
            yield return null;
            typeof(NewTeamService).GetProperty("Pending").SetValue(null, true);
            home.GetType().GetMethod("ShowNewTeam", Priv)?.Invoke(home, null);
            yield return Capture(home, "captain");
            typeof(NewTeamService).GetProperty("Pending").SetValue(null, false);

            Debug.Log("[935] DONE");
        }

        private IEnumerator Capture(MonoBehaviour ctrl, string file, int h = H)
        {
            yield return null;
            yield return new WaitForEndOfFrame();
            var ps = ctrl.GetComponent<UIDocument>().panelSettings;
            var prevRt = ps.targetTexture; var prevScale = ps.scaleMode; var prevRef = ps.referenceResolution;
            var rt = new RenderTexture(W, h, 24, RenderTextureFormat.ARGB32); rt.Create();
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(W, h);
            ps.targetTexture = rt;
            yield return null; yield return new WaitForEndOfFrame();
            yield return null; yield return new WaitForEndOfFrame();
            var tex = new Texture2D(W, h, TextureFormat.RGBA32, false);
            try
            {
                var prevActive = RenderTexture.active;
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, W, h), 0, 0);
                tex.Apply();
                RenderTexture.active = prevActive;
                File.WriteAllBytes(Path.Combine(Dir, file + ".png"), tex.EncodeToPNG());
                Debug.Log("[935] wrote " + file);
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); Destroy(rt); Destroy(tex);
            }
        }

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
