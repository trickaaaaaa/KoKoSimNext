#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Text;
using KokoSim.Unity.Debugging;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】issue #662 の実測。盗塁の場面（data/debug/scenarios.yaml の steal-*-lab）から
    /// 試合を始め、捕手の二塁送球を対決ビューで再生する。毎コマ捕手の向き・手・姿勢を記録し
    /// （<see cref="DuelStageRig.CatcherThrowDiagForCapture"/>）、送球の頭・リリース付近・対決ビューへ
    /// 戻った直後と0.5秒後を撮る。結果は screenshots/steal-throw-662/。
    /// </summary>
    public sealed class StealThrowShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/steal-throw-662";
        private const int W = 1600, H = 900;
        public static string[] Scenarios = { "steal-caught-lab", "steal-safe-lab" };
        /// <summary>再生速度（×1/×2/×4）。速度ごとに場面を回す＝速いときだけ後始末が間に合わない、を拾う。</summary>
        public static float[] Speeds = { 1f };
        /// <summary>監督を攻撃側に置き、采配窓で盗塁カードを押す（＝自分で盗塁サインを出す経路。issue #662 ②）。</summary>
        public static bool PressStealSign;

        private readonly StringBuilder _log = new StringBuilder();
        private bool _pressed;

        /// <summary>送球クリップ内の撮影時刻[s]（リリースは収録 1.16s）。</summary>
        private static readonly float[] ThrowShots = { 0.66f, 0.88f, 1.10f, 1.21f };

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Time.captureDeltaTime = 1f / 30f;   // コマ送りを決定論に（撮影の待ちで時計が飛ばない）
            Directory.CreateDirectory(Dir);
            _log.Append("scenario\tt\tstage\tfield\tclip\thold\tduelThrow\tbaseYaw\tyaw\tchestYaw\thand\thipsH\ttargetYaw\tpos\thandMoveYaw\n");
            foreach (var speed in Speeds)
            foreach (var id0 in Scenarios)
            {
                var id = id0;
                var tag = id0 + (speed != 1f ? "-x" + speed.ToString("F0") : "");
                MatchLiveController.ScenarioIdForCapture = id;
                var found = DebugScenarios.Catalog.TryGet(id, out var def);
                // 既定は監督＝守備側（相手の盗塁を自校の捕手が刺す）。サイン経路は監督＝攻撃側
                MatchLiveController.ManagerIsAwayOverrideForCapture = found && (PressStealSign ? def.Top : !def.Top);
                EnsureOnlyMatchLive(false);
                yield return null;
                EnsureOnlyMatchLive(true);
                yield return null; yield return null;
                var ctrl = FindController();
                if (ctrl == null) { Debug.LogError("[662] controller なし"); yield break; }
                if (!ctrl.DuelViewOnForCapture) ctrl.ToggleDuelViewForCapture();
                ctrl.SetSpeedForCapture(speed);

                var rig = ctrl.DuelRigForCapture;
                _pressed = false;
                float fieldAt = 0f; int fieldShots = 0;
                float t = 0f;
                bool sawThrow = false, sawField = false, backToDuel = false;
                float backAt = -1f, backTime = -1f;
                int shotsThrow = 0;
                Vector3? prevHand = null;
                while (t < 40f)
                {
                    var diag = rig != null ? rig.CatcherThrowDiagForCapture() : "-";
                    var cols = diag.Split('\t');
                    string moveYaw = "";
                    if (cols.Length >= 7)
                    {
                        var p = cols[6].Split(',');
                        if (p.Length == 3 && float.TryParse(p[0], out var hx) && float.TryParse(p[1], out var hy)
                            && float.TryParse(p[2], out var hz))
                        {
                            var hand = new Vector3(hx, hy, hz);
                            if (prevHand.HasValue)
                            {
                                var d = hand - prevHand.Value;
                                if (new Vector2(d.x, d.z).magnitude > 0.02f)
                                    moveYaw = (Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg).ToString("F1");
                            }
                            prevHand = hand;
                        }
                    }
                    if (PressStealSign && !sawThrow && ctrl.AtWindowForCapture && ctrl.ManagerOnOffenseForCapture
                        && ctrl.RunnerOnFirstForCapture && !_pressed)
                    {
                        ctrl.SelectSignForCapture("run-steal");
                        _pressed = true;   // カードはトグル＝押し続けると次の窓で消灯する
                        _log.Append("# pressed run-steal at ").Append(t.ToString("F3")).Append('\n');
                    }
                    var field = ctrl.FieldPlayActiveForCapture;
                    _log.Append(tag).Append('\t').Append(t.ToString("F3")).Append('\t').Append(ctrl.StageNameForCapture)
                        .Append('\t').Append(field ? "field" : "duel").Append('\t').Append(diag).Append('\t').Append(moveYaw).Append('\n');

                    var state = cols.Length >= 3 ? cols[2] : "-";
                    float clipT = -1f;
                    if (cols.Length >= 1 && cols[0].StartsWith("AA_Baseball_Catcher_Throwing@"))
                        float.TryParse(cols[0].Substring(cols[0].IndexOf('@') + 1), out clipT);
                    if (state == "throw" && !sawThrow)
                    {
                        sawThrow = true;
                        yield return Capture(ctrl, tag + "-throw-0");
                    }
                    // リリース（収録 1.16s）の前後をクリップ内時刻で撮る
                    if (sawThrow && !field && shotsThrow < ThrowShots.Length && clipT >= ThrowShots[shotsThrow])
                    {
                        yield return Capture(ctrl, tag + "-throw-clip" + ThrowShots[shotsThrow].ToString("F2"));
                        shotsThrow++;
                    }
                    // 守備ビューへ切り替わった直後（送球はリリースで引き渡す）
                    if (sawThrow && field && !sawField)
                    {
                        sawField = true;
                        fieldAt = t;
                        _log.Append("# field from ").Append(t.ToString("F3")).Append('\n');
                        yield return Capture(ctrl, tag + "-field-0");
                        fieldShots = 1;
                    }
                    // 守備ビューへ入ってからの帯（切り替え直後に全景へ引いていないか・送球の枠へ入るか）
                    if (sawField && field && fieldShots < 5 && t - fieldAt >= fieldShots * 0.15f)
                    {
                        yield return Capture(ctrl, tag + "-field-" + fieldShots);
                        fieldShots++;
                    }
                    // ⚠ 盗塁の送球より前のプレー（初球の打球など）から戻った分は数えない
                    if (sawField && !field && !backToDuel)
                    {
                        backToDuel = true;
                        backAt = t; backTime = t;
                        _log.Append("# back to duel at ").Append(t.ToString("F3")).Append('\n');
                        yield return Capture(ctrl, tag + "-back-0s");
                    }
                    if (backToDuel && backAt >= 0f && t - backAt >= 0.5f)
                    {
                        yield return Capture(ctrl, tag + "-back-0.5s");
                        backAt = -1f;
                    }
                    if (backToDuel && t - backTime > 2.5f) break;   // 戻った後 2.5 秒（次の投球の構えまで）記録して終える
                    t += Time.deltaTime;
                    yield return null;
                }
                _log.Append("# ").Append(tag).Append(" sawThrow=").Append(sawThrow).Append(" sawField=").Append(sawField)
                    .Append(" backToDuel=").Append(backToDuel).Append('\n');
                File.WriteAllText(Path.Combine(Dir, "log.tsv"), _log.ToString());
            }
            _log.Append("DONE\n");
            File.WriteAllText(Path.Combine(Dir, "log.tsv"), _log.ToString());
            MatchLiveController.ScenarioIdForCapture = null;
            MatchLiveController.ManagerIsAwayOverrideForCapture = null;
            Time.captureDeltaTime = 0f;
            Debug.Log("[662] DONE");
        }

        private IEnumerator Capture(MatchLiveController ctrl, string file)
        {
            ctrl.DuelRigForCapture?.RenderNow();
            yield return null;
            var ps = ctrl.GetComponent<UIDocument>().panelSettings;
            var prevRt = ps.targetTexture; var prevScale = ps.scaleMode; var prevRef = ps.referenceResolution;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32); rt.Create();
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(W, H);
            ps.targetTexture = rt;
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

        private static void EnsureOnlyMatchLive(bool on)
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                d.gameObject.SetActive(on && d.gameObject.name == "MatchLive");
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
