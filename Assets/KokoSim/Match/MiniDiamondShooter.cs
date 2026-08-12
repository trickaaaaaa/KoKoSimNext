#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using KokoSim.Engine.Debugging;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】走者の小ダイヤモンド（issue #595）の受け入れ。使い捨て。
    ///
    /// <para><b>なぜ実試合を流すか</b>: 壊れ得るのは engine の出力ではなく<b>画面の配線</b>
    /// （出る場面・畳む場面・3Dの走者との同時刻性）で、モックのプレーを rig へ直接流しても
    /// コントローラを一通も通らない＝何も確かめられない（<c>MatchLiveFieldPlayShooter</c> と同じ理由）。</para>
    ///
    /// <para>確かめること:
    /// ①守備フォーカス中に出る（打球・盗塁・牽制・振り逃げ・捕逸のすべて）
    /// ②打席中（対決の寄り）には出ない
    /// ③<c>EndFieldPlay</c> で畳む
    /// ④ドットの位置が3Dの走者と<b>0.000m 一致</b>する（同じ <c>RunnerAt</c> を同じ <c>t</c> で引いている裏取り）
    /// ⑤走者が塁で止まったのか次塁へ回ったのかが絵で読める（＝ドットが塁マスの上か塁間か）
    /// ⑥アウトになった走者はその時点でドットが消える（2026-08-12 監督指示。3Dの役者は残るので
    ///   「消えた」行が出ること自体が裏取りになる）</para>
    ///
    /// 出力:
    ///   out/minidia/plays.tsv     守備フォーカス1本＝1行（種類・出たか・走者数・ドットずれの最大）
    ///   out/minidia/runners.tsv   コマごとの走者位置（ドット[m] vs 3Dの役者[m] vs 差）
    ///   screenshots/minidia/*.png 節目の絵
    /// </summary>
    public sealed class MiniDiamondShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string ShotDir = "screenshots/minidia";
        private const string OutDir = "out/minidia";
        private const string SpawnFlag = "KokoSim.MiniDiamondShooter.Spawn";

        /// <summary>絵を撮る守備フォーカスの本数（1本で3枚出るので増やしすぎない）。</summary>
        private const int ShotPlays = 3;
        /// <summary>数える守備フォーカスの本数。</summary>
        private const int MaxPlays = 30;

        /// <summary>
        /// 拾いたい守備フォーカスの種類（issue #595 の完了条件「打球・盗塁・牽制・振り逃げ・捕逸のすべて」）。
        /// 打球以外は自然発生を待つと20打席流しても1本も出ない（初回実測: 19本すべて打球）ので、
        /// <c>ForcedOutcome</c> で発動そのものを固定する（設計書17 §6.1。強制した試合は
        /// <c>HasForcedOutcomes</c> が立って digest・統計から自動的に外れる＝本番の帯には触れない）。
        /// </summary>
        private static readonly string[] Wanted = { "打球", "steal", "pickoff", "battery-miss", "drop-third" };

        private readonly HashSet<string> _seen = new();

        public static void Arm() => UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            if (!UnityEditor.EditorPrefs.GetBool(SpawnFlag, false)) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            new GameObject("~MiniDiaAuto").AddComponent<MiniDiamondShooter>();
        }

        private MatchLiveController _ctrl;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Time.captureDeltaTime = 1f / 30f;
            Directory.CreateDirectory(ShotDir);
            Directory.CreateDirectory(OutDir);

            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();
            _ctrl = FindController();
            if (_ctrl == null) { Debug.LogError("[MiniDia] MatchLive/Controller が見つからない"); yield break; }
            yield return null;

            var plays = new StringBuilder("#\t種類\t結果\t小ダイヤ\t走者最大\tドットずれ最大[m]\tアウトで消えた\t畳んだ\n");
            var runners = new StringBuilder();

            // 打席中（対決の寄り）に出ていないこと。**守備フォーカスの外で1コマでも出たら違反**なので
            // 全コマ数える（節目だけ見ると見逃す）。
            var idleFrames = 0;
            var idleShown = 0;
            var shotIdle = false;

            var playNo = 0;
            var inPlay = false;
            var kind = ""; var result = "";
            var maxRunners = 0; var maxErr = 0f;
            var everShown = false;
            var shotA = false; var shotB = false; var shotC = false;
            var shooting = false; var shotKinds = 0;
            var hidThisPlay = false; var sawHidden = false; var shotHidden = false;
            var frames = 0;

            var prevWindow = false;
            var guard = 0;
            while ((playNo < MaxPlays || _seen.Count < Wanted.Length)
                   && playNo < MaxPlays * 3 && !_ctrl.IsGameOver && guard++ < 200000)
            {
                yield return null;
                var active = _ctrl.FieldPlayActiveForCapture;
                var shown = _ctrl.MiniDiamondVisibleForCapture;

                // ── 采配窓の頭で1回だけ、まだ拾えていない種類を強制で予約する ──
                var win = _ctrl.AtWindowForCapture;
                if (win && !prevWindow)
                {
                    var want = NextForced();
                    if (want != ForcedOutcome.None) _ctrl.ForceNextForCapture(want);
                    // 盗塁は監督の采配なのでサインも併用する（強制は成否を固定する側）。
                    if (_ctrl.ManagerOnOffenseForCapture && !_seen.Contains("steal"))
                        _ctrl.SelectSignForCapture("run-steal");
                }
                prevWindow = win;

                // ── 守備フォーカスの外 ─────────────────────────────────────
                if (!active)
                {
                    idleFrames++;
                    if (shown) idleShown++;
                    // 打席の寄りが立ち上がってしばらくした頃に1枚だけ「出ていない」絵を残す
                    if (!shotIdle && idleFrames > 120)
                    {
                        shotIdle = true;
                        yield return Shoot("0-at-bat-none");
                    }
                }

                // ── プレーの頭 ─────────────────────────────────────────────
                if (active && !inPlay)
                {
                    inPlay = true;
                    playNo++;
                    frames = 0;
                    maxRunners = 0; maxErr = 0f; everShown = false; hidThisPlay = false;
                    shotA = shotB = shotC = false;
                    var mid = _ctrl.MidPaPlayForCapture;
                    kind = string.IsNullOrEmpty(mid) ? "打球" : mid;
                    // 絵は**種類ごとに1本目だけ**撮る（打球を3本撮っても盗塁の絵は出ない）。
                    shooting = !_seen.Contains(kind) && shotKinds < ShotPlays + Wanted.Length;
                    if (shooting) shotKinds++;
                    _seen.Add(kind);
                    result = (string.IsNullOrEmpty(mid)
                        ? _ctrl.CurrentPa?.Play?.Result
                        : _ctrl.MidPaPlayResultForCapture) ?? "-";
                    runners.Append("── ").Append(playNo).Append('\t').Append(kind)
                           .Append('\t').Append(result).Append('\n');
                }

                // ── プレー中 ───────────────────────────────────────────────
                if (inPlay && active)
                {
                    frames++;
                    if (shown) everShown = true;

                    // ドット[m] vs 3Dの役者[m]。差は 0.000 のはず（同じ RunnerXZ・同じ _fieldT）。
                    var diag = _ctrl.MiniDiamondDiagForCapture;
                    if (shooting) runners.Append(diag);
                    var n = 0;
                    foreach (var line in diag.Split('\n'))
                    {
                        var c = line.Split('\t');
                        if (c.Length < 4) continue;
                        // アウトで消したドット（3Dの役者は残るので突き合わせない）
                        if (c[3] == "消えた") { hidThisPlay = true; sawHidden = true; continue; }
                        float d;
                        if (!float.TryParse(c[3], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out d)) continue;
                        n++;
                        maxErr = Mathf.Max(maxErr, d);
                    }
                    maxRunners = Mathf.Max(maxRunners, n);

                    // アウトの走者が消えた瞬間を1度だけ絵に残す（監督指示 2026-08-12 の受け入れ）。
                    if (hidThisPlay && !shotHidden)
                    {
                        shotHidden = true;
                        yield return Shoot("x-runner-out-hidden");
                    }

                    if (shooting)
                    {
                        // 頭・中盤・終盤の3枚。走者が塁で止まるのは終盤なので、
                        // 「塁間を走っている絵」と「塁マスの上で止まっている絵」が両方残る。
                        var tag = kind + "-" + playNo;
                        if (!shotA && frames >= 6) { shotA = true; yield return Shoot(tag + "-a-start"); }
                        else if (!shotB && frames >= 30) { shotB = true; yield return Shoot(tag + "-b-run"); }
                        else if (!shotC && frames >= 70) { shotC = true; yield return Shoot(tag + "-c-settle"); }
                    }
                }

                // ── プレーの終わり ─────────────────────────────────────────
                if (!active && inPlay)
                {
                    inPlay = false;
                    // ⚠ 畳んだかは**次のコマで**採る。RefreshMiniDiamond は Update の頭で走る＝
                    // プレーが終わったコマではまだ「出ている」状態で通り過ぎており、さらに
                    // resolvedStyle はパネルの更新まで前の値を返す（同じコマで見ると必ず「残った」になる）。
                    yield return null;
                    plays.Append(playNo).Append('\t').Append(kind).Append('\t').Append(result).Append('\t')
                         .Append(everShown ? "出た" : "★出ない").Append('\t')
                         .Append(maxRunners).Append('\t').Append(maxErr.ToString("F3")).Append('\t')
                         .Append(hidThisPlay ? "消えた" : "—").Append('\t')
                         .Append(_ctrl.MiniDiamondVisibleForCapture ? "★残った" : "畳んだ").Append('\n');
                }
            }

            File.WriteAllText(Path.Combine(OutDir, "plays.tsv"), plays.ToString());
            File.WriteAllText(Path.Combine(OutDir, "runners.tsv"), runners.ToString());
            var missing = new List<string>();
            foreach (var w in Wanted) if (!_seen.Contains(w)) missing.Add(w);
            Debug.Log("[MiniDia] DONE 守備フォーカス=" + playNo + "本"
                + " 打席中に出たコマ=" + idleShown + "/" + idleFrames
                + " 拾えた種類=" + string.Join(",", new List<string>(_seen).ToArray())
                + " 拾えなかった=" + (missing.Count == 0 ? "なし" : string.Join(",", missing.ToArray()))
                + " アウトで消えた場面=" + (sawHidden ? "有り" : "★無し")
                + " gameOver=" + _ctrl.IsGameOver + "\n" + plays);
            Time.captureDeltaTime = 0f;
        }

        /// <summary>まだ拾えていない種類を出すための強制発動（走塁系は走者一塁・二塁空きが要る）。</summary>
        private ForcedOutcome NextForced()
        {
            if (_ctrl.CanForceStealPlayForCapture)
            {
                if (!_seen.Contains("steal")) return ForcedOutcome.StealSuccess;
                if (!_seen.Contains("pickoff")) return ForcedOutcome.PickoffOut;
            }
            if (!_seen.Contains("battery-miss")) return ForcedOutcome.WildPitch;
            if (!_seen.Contains("drop-third")) return ForcedOutcome.DroppedThirdStrike;
            return ForcedOutcome.None;
        }

        /// <summary>
        /// 画面まるごと1枚。撮影の間は進行を凍らせる（<c>Time.captureDeltaTime</c> は実時間から
        /// 切り離すが、待ったフレームの数ぶんは確実に進むので、凍らせないと節目の絵が節目からずれる）。
        /// </summary>
        private IEnumerator Shoot(string file)
        {
            _ctrl.FreezeLiveForCapture(true);
            yield return null;
            yield return new WaitForEndOfFrame();
            _ctrl.DuelRigForCapture?.RenderNow();   // シューター経路だけ選手が黒く焼ける回がある
            yield return null;

            var ps = _ctrl.GetComponent<UIDocument>().panelSettings;
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
                File.WriteAllBytes(Path.Combine(ShotDir, file + ".png"), tex.EncodeToPNG());
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); Destroy(rt);
                if (tex != null) Destroy(tex);
                _ctrl.FreezeLiveForCapture(false);
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
