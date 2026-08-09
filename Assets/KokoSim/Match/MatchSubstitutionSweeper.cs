#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Text;
using KokoSim.Engine.Match.Field;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】交代操作の一巡確認（issue #452 の完了条件）。使い捨て。
    /// 継投／コンバート／守備固め／守備位置の入替／代打／代走 を実際に<b>確定まで</b>実行し、
    /// 毎回 打順表の中身を読み直して「狙った選手が狙った枠に入ったか」を突き合わせる。
    /// 結果は screenshots/issue-452-sweep/report.txt と各操作後のスクショに出す。
    /// <para>絵だけでは「押せた」ことしか分からないので、確定後のラインナップを文字で残すのが主目的。</para>
    /// </summary>
    public sealed class MatchSubstitutionSweeper : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string Dir = "screenshots/issue-452-sweep";

        private readonly StringBuilder _log = new StringBuilder();
        private int _pass;
        private int _fail;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;
            EnsureOnlyMatchLive();
            yield return null;
            yield return null;
            EnsureOnlyMatchLive();

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[Sweep] MatchLive/Controller が見つからない"); yield break; }
            yield return null;
            Directory.CreateDirectory(Dir);

            // 順番は「控えを使う操作を、控えが尽きる前に」。控えは4人で、ベンチを使うのは
            // 代打・代走・継投・守備固めの4つ（入替と投野交代は控えを消費しない。issue #452）。
            // 攻撃でしか出せない操作（代打・代走）を先に済ませる。
            // ── ① 代打（自校の攻撃で次打者を控えへ差し替え） ──
            for (var i = 0; i < 80 && !ctrl.ManagerOnOffenseForCapture && ctrl.StageNameForCapture != "Over"; i++)
                ctrl.AdvanceForCapture();
            if (ctrl.ManagerOnOffenseForCapture)
            {
                var beforeBench = ctrl.BenchCountForCapture;
                ctrl.OpenSubstitutionForCapture();
                ctrl.PickSubstitutionCandidateForCapture(0);
                var noteHit = ctrl.SubstitutionNoteForCapture;
                ctrl.ApplySubstitutionForCapture();
                yield return null;
                Line("① 代打", !ctrl.SubstitutionOpenForCapture && ctrl.BenchCountForCapture < beforeBench,
                    "控え " + beforeBench + " → " + ctrl.BenchCountForCapture + "   [" + Trim(noteHit) + "]");
                yield return Capture(ctrl, "1-pinch-hit");
            }
            else
            {
                Line("① 代打", false, "自校の攻撃へ到達しなかった（進行=" + ctrl.StageNameForCapture + "）");
            }

            // ── ② 代走（走者の出る打席境界まで進めてから。成立条件が一番厳しいので早めに試す） ──
            var reached = false;
            for (var i = 0; i < 200 && ctrl.StageNameForCapture != "Over"; i++)
            {
                if (ctrl.ManagerCanPinchRunForCapture) { reached = true; break; }
                ctrl.AdvanceForCapture();
            }
            if (reached)
            {
                var beforeRunners = ctrl.BenchCountForCapture;
                ctrl.OpenSubstitutionForCapture();
                ctrl.SelectSubstitutionKindForCapture(1);
                ctrl.PickSubstitutionRunnerForCapture(-1);
                ctrl.PickSubstitutionCandidateForCapture(0);
                var noteRun = ctrl.SubstitutionNoteForCapture;
                ctrl.ApplySubstitutionForCapture();
                yield return null;
                Line("② 代走", !ctrl.SubstitutionOpenForCapture && ctrl.BenchCountForCapture < beforeRunners,
                    "控え " + beforeRunners + " → " + ctrl.BenchCountForCapture + "   [" + Trim(noteRun) + "]");
                yield return Capture(ctrl, "2-pinch-run");
            }
            else
            {
                Line("② 代走", false, "走者の出る打席境界に到達しなかった（進行=" + ctrl.StageNameForCapture + "）");
            }

            // ── ③ 継投（投枠 → 控え。元投手は退場） ──
            for (var i = 0; i < 60 && ctrl.ManagerOnOffenseForCapture; i++) ctrl.AdvanceForCapture();
            var beforeP = SlotName(ctrl, FieldPosition.Pitcher);
            ctrl.OpenSubstitutionForCapture();
            ctrl.PickSubstitutionSlotForCapture(FieldPosition.Pitcher);
            ctrl.PickSubstitutionCandidateForCapture(0);
            var note1 = ctrl.SubstitutionNoteForCapture;
            ctrl.ApplySubstitutionForCapture();
            yield return null;
            Check("③ 継投", beforeP, SlotName(ctrl, FieldPosition.Pitcher), ctrl, note1);
            yield return Capture(ctrl, "3-relieve");

            // ── ④ 守備固め（左翼枠 → 控え。元の左翼手は退場） ──
            var beforeLf = SlotName(ctrl, FieldPosition.LeftField);
            ctrl.OpenSubstitutionForCapture();
            ctrl.PickSubstitutionSlotForCapture(FieldPosition.LeftField);
            ctrl.PickSubstitutionCandidateForCapture(0);
            var note2 = ctrl.SubstitutionNoteForCapture;
            ctrl.ApplySubstitutionForCapture();
            yield return null;
            Check("④ 守備固め", beforeLf, SlotName(ctrl, FieldPosition.LeftField), ctrl, note2);
            yield return Capture(ctrl, "4-defensive-sub");

            // ── ③ 守備位置の入替（枠→枠。二塁 ↔ 遊撃。可逆＝退場は出ない） ──
            var before2b = SlotName(ctrl, FieldPosition.SecondBase);
            var beforeSs = SlotName(ctrl, FieldPosition.Shortstop);
            ctrl.OpenSubstitutionForCapture();
            ctrl.PickSubstitutionSlotForCapture(FieldPosition.SecondBase);
            ctrl.PickSubstitutionSlotForCapture(FieldPosition.Shortstop);
            var note3 = ctrl.SubstitutionNoteForCapture;
            ctrl.ApplySubstitutionForCapture();
            yield return null;
            CheckSwap("⑤ 入替", before2b, beforeSs,
                SlotName(ctrl, FieldPosition.SecondBase), SlotName(ctrl, FieldPosition.Shortstop), ctrl, note3);
            yield return Capture(ctrl, "5-swap");

            // ── ⑥ 投野交代（投枠 ↔ 在フィールドの野手＝2人をそのまま入れ替える。issue #452） ──
            var beforeP4 = SlotName(ctrl, FieldPosition.Pitcher);
            var beforeRf = SlotName(ctrl, FieldPosition.RightField);
            ctrl.OpenSubstitutionForCapture();
            ctrl.PickSubstitutionSlotForCapture(FieldPosition.Pitcher);
            ctrl.PickSubstitutionSlotForCapture(FieldPosition.RightField);
            var note4 = ctrl.SubstitutionNoteForCapture;
            yield return Capture(ctrl, "6-convert-pending", reopen: false);   // 確定前のプレビュー＋フッタ
            ctrl.ApplySubstitutionForCapture();
            yield return null;
            // 非DHは単純な入れ替え＝控えは動かない・誰も退場しない（旧仕様の「空き枠は自動補充」は廃止）。
            CheckSwap("⑥ 投野交代", beforeP4, beforeRf,
                SlotName(ctrl, FieldPosition.Pitcher), SlotName(ctrl, FieldPosition.RightField), ctrl, note4);
            yield return Capture(ctrl, "6-convert");

            _log.Append("\n合計: 成功 ").Append(_pass).Append(" / 失敗 ").Append(_fail).Append("\n");
            File.WriteAllText(Path.Combine(Dir, "report.txt"), _log.ToString());
            Debug.Log("[Sweep] DONE pass=" + _pass + " fail=" + _fail + "\n" + _log);
        }

        // ── 判定 ──

        private void Check(string title, string before, string after, MatchLiveController ctrl, string note)
        {
            var changed = before != after && !string.IsNullOrEmpty(after);
            Line(title, changed && !ctrl.SubstitutionOpenForCapture, before + " → " + after + "   [" + Trim(note) + "]");
        }

        private void CheckSwap(string title, string beforeA, string beforeB, string afterA, string afterB,
            MatchLiveController ctrl, string note)
        {
            var swapped = beforeA == afterB && beforeB == afterA && beforeA != beforeB;
            Line(title, swapped && !ctrl.SubstitutionOpenForCapture,
                beforeA + "/" + beforeB + " → " + afterA + "/" + afterB + "   [" + Trim(note) + "]");
        }

        private void Line(string title, bool ok, string detail)
        {
            if (ok) _pass++; else _fail++;
            _log.Append(ok ? "OK   " : "NG   ").Append(title).Append("  ").Append(detail).Append("\n");
        }

        private static string Trim(string s) => s == null ? "" : s.Replace("\n", " ");

        /// <summary>打順表（左ペイン）から、その守備位置に就いている選手名を読む。</summary>
        private static string SlotName(MatchLiveController ctrl, FieldPosition pos)
        {
            var p = ctrl.FielderForCapture(pos);
            return p == null ? "—" : p.Name;
        }

        // ── 撮影・下ごしらえ（他のシューターと同型） ──

        /// <param name="reopen">
        /// true＝モーダルを開き直して確定後の打順表を見せる。false＝いま開いている選択状態のまま撮る
        /// （確定前のプレビューとフッタを見るとき。Open() は選択をリセットするので開き直せない）。
        /// </param>
        private IEnumerator Capture(MatchLiveController ctrl, string file, bool reopen = true)
        {
            if (reopen) ctrl.OpenSubstitutionForCapture();   // 確定後の姿を打順表で見せる
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
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); Destroy(rt);
                if (tex != null) Destroy(tex);
            }
            ctrl.CloseSubstitutionForCapture();
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
