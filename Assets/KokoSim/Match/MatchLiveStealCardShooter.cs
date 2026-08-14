#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】盗塁カードが塁状況で書き換わるか（issue #622）を実機で撮る。使い捨て。
    ///
    /// 狙った塁状況は作れないので<b>試合を進めて出るのを待つ</b>。一塁のみ／二塁のみ／三塁のみ／
    /// 一・二塁（＝単独盗塁が成立しない並び）の4つを拾い、それぞれ1枚ずつ撮って札の文字と可否を記録する。
    /// </summary>
    public sealed class MatchLiveStealCardShooter : MonoBehaviour
    {
        private const int W = 900;
        private const int H = 900;
        private const string Dir = "screenshots/match2d-live";
        private const int MaxAdvances = 400;
        private const int MaxGames = 6;

        private const string SpawnFlag = "KokoSim.MatchLiveStealCardShooter.Spawn";
        private const string OnlyKey = "KokoSim.MatchLiveStealCardShooter.Only";

        public static void Arm() => Arm("");

        /// <summary>
        /// <paramref name="only"/> に「三盗」「本盗」を渡すと、その再生だけを狙う（残りは撮らない）。
        /// 狙いの塁状況は待たないと出ないので、1試合で両方拾えないことがある＝分けて撮れるようにする。
        /// </summary>
        public static void Arm(string only)
        {
            UnityEditor.EditorPrefs.SetString(OnlyKey, only ?? "");
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            if (!UnityEditor.EditorPrefs.GetBool(SpawnFlag, false)) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            new GameObject("~StealCardShootAuto").AddComponent<MatchLiveStealCardShooter>();
        }

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[StealCard] MatchLive/Controller が見つからない"); yield break; }
            yield return null;
            Directory.CreateDirectory(Dir);

            var only = UnityEditor.EditorPrefs.GetString(OnlyKey, "");

            // 撮りたい塁状況 → まだ撮っていないもの（撮り終えたら消す）。
            var want = new Dictionary<string, string>
            {
                { "一", "steal-card-1-first" },
                { "二", "steal-card-2-second" },
                { "三", "steal-card-3-third" },
                { "一二", "steal-card-4-none" },
            };

            if (only.Length > 0) want.Clear();   // 再生だけ狙う回はカードの絵を撮り直さない

            for (var i = 0; i < MaxAdvances && want.Count > 0; i++)
            {
                if (!ctrl.AdvanceForCapture()) break;
                var bases = ctrl.BasesForCapture;
                if (!want.TryGetValue(bases, out var file)) continue;
                want.Remove(bases);

                Debug.Log($"[StealCard] 塁={bases} 札=「{ctrl.StealCardLabelForCapture}」"
                          + $" 押せる={ctrl.StealCardEnabledForCapture} 盤面の札={DeckLabels(ctrl)}");
                yield return Capture(ctrl, file);
            }

            if (want.Count > 0)
            {
                foreach (var kv in want) Debug.LogWarning("[StealCard] 出なかった塁状況: " + kv.Key);
            }

            // ② 指示した三盗・本盗の**絵**が出るか。カードの可否と違い、再生は実時間の進行でしか
            //    動かない（AdvanceForCapture は打席を一気に解決してしまう）ので、狙いの窓まで手動で
            //    進めてからカードを押し、そこだけ通常進行へ返して再生の開始を待つ。
            if (only.Length == 0 || only == "三盗") yield return ShootStealPlay(ctrl, "三盗", "steal-play-third");
            if (only.Length == 0 || only == "本盗") yield return ShootStealPlay(ctrl, "本盗", "steal-play-home");

            Debug.Log("[StealCard] DONE");
        }

        /// <summary>
        /// 指定の札（三盗/本盗）が出る窓まで進めて押し、盗塁の再生が始まったところを撮る。
        /// 押した球がファウル・打球だと盗塁は無かったことになる（issue #626）ので、始まるまで何度でも押す。
        /// </summary>
        private IEnumerator ShootStealPlay(MatchLiveController ctrl, string wantLabel, string file)
        {
            var games = 0;
            for (var i = 0; i < MaxAdvances; i++)
            {
                MatchLiveController.CaptureMode = true;
                if (!ctrl.AdvanceForCapture())
                {
                    // 三塁のみ・自校攻撃のような窓は1試合で出ないことがある＝シードを変えて撮り直す。
                    if (++games >= MaxGames) break;
                    MatchLiveController.GameSeedOverrideForCapture = 20260814UL + (ulong)games;
                    var go = ctrl.gameObject;
                    go.SetActive(false);
                    yield return null;
                    go.SetActive(true);
                    yield return null; yield return null;
                    Debug.Log("[StealCard] 試合を撮り直す（" + (games + 1) + "試合目）");
                    continue;
                }
                if (ctrl.StealCardLabelForCapture != wantLabel) continue;
                if (!ctrl.SelectStealForCapture()) continue;   // 守備の窓＝押せない

                // ここだけ通常進行へ返す。再生が始まったら撮って手動駆動へ戻す。
                MatchLiveController.CaptureMode = false;
                ctrl.SetSpeedForCapture(4f);
                var waited = 0f;
                while (waited < 20f && !ctrl.StealPlayActiveForCapture)
                {
                    waited += Time.deltaTime;
                    yield return null;
                }
                var started = ctrl.StealPlayActiveForCapture;
                if (started)
                {
                    // 走者が動いている最中を撮る＝進行を止めてから撮る（打席結果へ飛ばすと
                    // その瞬間が消えて、投球判定の絵になる。2026-08-14 実害）。
                    for (var f = 0; f < 12; f++) yield return null;   // 走り出しが見える位置まで進める
                    ctrl.SetPausedForCapture(true);
                    Debug.Log($"[StealCard] {wantLabel} の再生が始まった（塁={ctrl.BasesForCapture}）");
                    yield return Capture(ctrl, file, freeze: false);
                    ctrl.SetPausedForCapture(false);
                }
                MatchLiveController.CaptureMode = true;
                ctrl.SetSpeedForCapture(1f);
                if (started) yield break;
            }
            Debug.LogWarning("[StealCard] " + wantLabel + " の再生が撮れなかった");
        }

        /// <summary>実際に描かれているカード名（札の文字が引き直されているかの実測）。</summary>
        private static string DeckLabels(MatchLiveController ctrl)
        {
            var root = ctrl.GetComponent<UIDocument>()?.rootVisualElement;
            if (root == null) return "(root なし)";
            var names = new List<string>();
            root.Query<Label>(className: "tcard__name").ForEach(l => names.Add(l.text.Replace("\n", "")));
            return string.Join("/", names);
        }

        private IEnumerator Capture(MatchLiveController ctrl, string file, bool freeze = true)
        {
            if (freeze) ctrl.FreezeCurrentAtResult();
            yield return null;
            yield return new WaitForEndOfFrame();
            yield return null;

            var ps = ctrl.GetComponent<UIDocument>().panelSettings;
            var prevRt = ps.targetTexture; var prevScale = ps.scaleMode; var prevRef = ps.referenceResolution;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32); rt.Create();
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(W, H);
            ps.targetTexture = rt;

            yield return null; yield return new WaitForEndOfFrame();
            yield return null; yield return new WaitForEndOfFrame();

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
                Debug.Log("[StealCard] wrote " + file);
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); Destroy(rt);
                if (tex != null) Destroy(tex);
            }
        }

        private static void EnsureOnlyMatchLive()
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                d.gameObject.SetActive(d.gameObject.name == "MatchLive");
            }
        }

        private static MatchLiveController FindController()
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                if (d.gameObject.name == "MatchLive") return d.GetComponent<MatchLiveController>();
            }
            return null;
        }
    }
}
#endif
