#if UNITY_EDITOR
using System.Collections;
using System.IO;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Home
{
    /// <summary>
    /// 【開発用・エディタ限定】選手間イベントの3択（issue #945・案A＝会話ボックスの中に縦並び）の受け入れスクショ。
    /// 選択肢つきイベントを週次抽選を通さずに積み（<see cref="EventService.PushChoiceEvent"/>）、
    /// ①3択が出た状態 ②選んだ後の結果の会話の各行（最終行＝結果行つき）を撮る。
    /// 好転は乱数を0に、悪化は0.999に差し替えて必ず両方を出す（確率は0.10〜0.90に丸めてあるため）。
    ///
    /// <para>回し方: Play 前に <c>EventChoiceShooter.Arm()</c> を呼んで Play に入る（AfterSceneLoad で自動起動）。
    /// 出力は <c>screenshots/event-choice/</c>（Git 管理外）。ログは <c>[EVC]</c>。</para>
    /// </summary>
    public sealed class EventChoiceShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/event-choice";
        private const string SpawnFlag = "KokoSim.EventChoiceShooter.Spawn";
        private const int W = 1600;
        private const int H = 900;

        public static void Arm() => UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            if (!UnityEditor.EditorPrefs.GetBool(SpawnFlag, false)) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            new GameObject("~EventChoiceAuto").AddComponent<EventChoiceShooter>();
        }

        // (イベント, 乱数の差し替え, 選ぶ対応の番号, 撮影名の頭)
        private static readonly (string Id, double Roll, int Pick, string Tag)[] Cases =
        {
            ("grade_friction", 0.0, 1, "A-improved"),      // 話を聞く → 好転
            ("selfish_clash", 0.999, 0, "B-worsened"),     // 叱る → 悪化
            ("skipping_practice", 0.0, 2, "C-improved"),   // 主将に任せる → 好転
        };

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);
            NewTeamService.Clear();     // 指名モーダルが先に出てノベルが隠れるのを避ける
            UniformNumberService.Clear();   // 背番号決定待ち（issue #954）も畳む

            foreach (var c in Cases)
            {
                EventService.Clear();
                var before = GameSession.Current.CohesionDelta;
                if (!EventService.PushChoiceEvent(c.Id, c.Roll))
                {
                    Debug.LogError("[EVC] " + c.Id + " を積めなかった（当事者を立てられない部員構成？ roster="
                        + RosterService.Active.Count + "）");
                    continue;
                }

                var doc = Open("HomeDashboard");
                if (doc == null) { Debug.LogError("[EVC] HomeDashboard が見つからない"); yield break; }
                yield return null; yield return null;
                var novel = doc.rootVisualElement.Q<VisualElement>("ev-novel");
                var choices = doc.rootVisualElement.Q<VisualElement>("ev-choices");

                // 出だしの会話を最後の行まで送る（3択が出たら止まる）。
                for (var i = 0; i < 10 && choices.childCount == 0; i++)
                {
                    Click(novel);
                    yield return null; yield return null;
                }
                if (choices.childCount == 0) { Debug.LogError("[EVC] " + c.Id + ": 3択が出なかった"); continue; }
                Debug.Log("[EVC] " + c.Id + " choices=" + choices.childCount + " say=" + Text(doc, "ev-say"));
                yield return Capture(doc, c.Tag + "-01-choices");

                // 面を押しても進まないこと（選ぶまで待つ）。
                Click(novel);
                yield return null; yield return null;
                if (choices.childCount == 0) Debug.LogError("[EVC] " + c.Id + ": 面のクリックで3択が消えた");

                var button = choices[c.Pick];
                Debug.Log("[EVC] pick: " + ((Button)button).text);
                Submit(button);
                yield return null; yield return null;

                // 結果の会話を1行ずつ撮る（最後の1枚が結果行）。
                var shot = 2;
                for (var i = 0; i < 10 && novel.resolvedStyle.display != DisplayStyle.None; i++)
                {
                    Debug.Log("[EVC] " + c.Id + " " + Text(doc, "ev-progress") + " " + Text(doc, "ev-speaker")
                        + " / " + Text(doc, "ev-say") + " hint=" + Text(doc, "ev-hint"));
                    yield return Capture(doc, string.Format("{0}-{1:00}-line", c.Tag, shot++));
                    Click(novel);
                    yield return null; yield return null;
                }
                Debug.Log("[EVC] " + c.Id + " 結束 ΔU " + before.ToString("0.0") + " → "
                    + GameSession.Current.CohesionDelta.ToString("0.0"));
            }
            Debug.Log("[EVC] DONE");
        }

        private static string Text(UIDocument doc, string name)
        {
            var l = doc.rootVisualElement.Q<Label>(name);
            return l == null ? "(null)" : l.text;
        }

        // Button は Clickable 経由なので合成 ClickEvent では発火しない＝決定と同じ NavigationSubmitEvent を送る（EventNovelShooter と同じ作法）。
        private static void Submit(VisualElement target)
        {
            using (var e = NavigationSubmitEvent.GetPooled())
            {
                e.target = target;
                target.SendEvent(e);
            }
        }

        private static void Click(VisualElement target)
        {
            using (var e = PointerDownEvent.GetPooled())
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
                Debug.Log("[EVC] wrote " + file);
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
