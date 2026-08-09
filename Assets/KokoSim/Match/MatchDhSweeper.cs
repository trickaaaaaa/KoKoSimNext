#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Text;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using KokoSim.Unity.Lineup;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】<b>DH制でしか出ない交代操作</b>の一巡確認（issue #452 の残件）。使い捨て。
    ///
    /// <para>デモ試合は能力順の自動編成（<c>RosterTeamBuilder.Build</c>）で組むので必ず非DHになり、
    /// <see cref="MatchSubstitutionSweeper"/> の6操作にはDH由来の枝が1つも含まれていない。ここでは
    /// 本番と同じ経路（スタメン設定＝<see cref="LineupSettingState"/> → <c>LineupSpec</c> →
    /// <c>RosterTeamBuilder.BuildFromLineup</c>）でDH制の自校を組み、engine の3つの終着を確かめる。</para>
    ///
    /// <list type="number">
    ///   <item>⑦ DH解除（指枠 → 守備位置枠）＝元DHがその守備に就き、元の守備者が退場・投手が打順へ</item>
    ///   <item>⑧ DHそのまま退場（指枠 →「そのまま退かせる」）＝守備陣は不変・投手が打順へ</item>
    ///   <item>⑨ DH下の投野交代（投枠 ↔ 野手枠）＝野手が登板し元DHが空き守備へ・旧投手は退場</item>
    /// </list>
    ///
    /// <para><b>DH解除は不可逆</b>（<c>TeamState._usesDh</c> は false へ倒れるだけ）なので、1試合につき
    /// 1操作しか試せない。パスごとに MatchLive を作り直して新しい試合を始める。</para>
    ///
    /// 結果は screenshots/issue-452-dh/report.txt と各操作後のスクショに出す。
    /// </summary>
    public sealed class MatchDhSweeper : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string Dir = "screenshots/issue-452-dh";

        private readonly StringBuilder _log = new StringBuilder();
        private int _pass;
        private int _fail;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;
            MatchLiveController.HomeTeamOverrideForCapture = BuildDhHome;
            Directory.CreateDirectory(Dir);

            yield return Run("⑦ DH解除（指→左翼）", DhToPos, "7-dh-to-pos");
            yield return Run("⑧ DHそのまま退場", DhWithdraw, "8-dh-withdraw");
            yield return Run("⑨ 投野交代（DH下・投↔右翼）", PromoteUnderDh, "9-promote-under-dh");

            MatchLiveController.HomeTeamOverrideForCapture = null;
            _log.Append("\n合計: 成功 ").Append(_pass).Append(" / 失敗 ").Append(_fail).Append("\n");
            File.WriteAllText(Path.Combine(Dir, "report.txt"), _log.ToString());
            Debug.Log("[DhSweep] DONE pass=" + _pass + " fail=" + _fail + "\n" + _log);
        }

        /// <summary>
        /// 本番と同じ経路でDH制の自校を組む。スタメン画面を開かずに <see cref="LineupSettingState"/> を直に
        /// 使うのは、この確認が見たいのが「画面の操作」ではなく「DH制で始まった試合の交代」だから。
        ///
        /// <para>注意（絵の見え方だけ）: デモ経路はこの後 <c>BuildBench()</c> の2人を Merge する。あちらは
        /// 自動編成の控え（投手2枚）と重ならない背番号16/17を選んでいるので、控え全員が載るこのチームでは
        /// 背番号が重複して見える。実ゲーム（<c>PlayerMatchResolver.BuildManagerTeam</c>）はこのMergeを通らない。</para>
        /// </summary>
        private static Team BuildDhHome()
        {
            var state = new LineupSettingState();
            state.ToggleDh();
            return RosterTeamBuilder.BuildFromLineup(state.ToLineupSpec());
        }

        // ── パスの骨格 ──

        private IEnumerator Run(string title, System.Func<MatchLiveController, IEnumerator> body, string file)
        {
            yield return NewGame();
            var ctrl = FindController();
            if (ctrl == null) { Line(title, false, "MatchLive/Controller が見つからない"); yield break; }

            // 前提: DH制で始まっているか（投手が打順外＝PitcherSlot -1・指枠に選手が居る）。
            if (!ctrl.UsesDhForCapture || ctrl.PitcherSlotForCapture != -1 || ctrl.DhForCapture == null)
            {
                Line(title, false, "DH制で始まっていない（UsesDh=" + ctrl.UsesDhForCapture
                    + " PitcherSlot=" + ctrl.PitcherSlotForCapture + " 指=" + NameOf(ctrl.DhForCapture) + "）");
                yield break;
            }

            // 守備の窓まで進める（DH由来の操作はすべて守備側）。
            for (var i = 0; i < 80 && ctrl.ManagerOnOffenseForCapture && ctrl.StageNameForCapture != "Over"; i++)
                ctrl.AdvanceForCapture();
            if (ctrl.ManagerOnOffenseForCapture)
            {
                Line(title, false, "守備の窓へ到達しなかった（進行=" + ctrl.StageNameForCapture + "）");
                yield break;
            }

            yield return body(ctrl);
            yield return Capture(ctrl, file);
        }

        // ── ⑦ DH解除（指枠 → 左翼枠） ──
        // 期待: 元DHが左翼へ／元の左翼手は退場（打順から消える）／投手が打順へ入る／DHは消滅。
        private IEnumerator DhToPos(MatchLiveController ctrl)
        {
            // DH解除の「前」も1枚撮る。DH制でしか出ない打順表の姿（指枠が在る／投手は打順外＝"—"で末尾。
            // issue #452 指摘1）は、非DHのデモしか無かったこれまで一度も絵になっていない。
            yield return Capture(ctrl, "0-dh-start");

            var dh = ctrl.DhForCapture;
            var lf = ctrl.FielderForCapture(FieldPosition.LeftField);
            var pitcher = ctrl.FielderForCapture(FieldPosition.Pitcher);

            ctrl.OpenSubstitutionForCapture();
            ctrl.PickSubstitutionSlotForCapture(FieldPosition.DesignatedHitter);
            ctrl.PickSubstitutionSlotForCapture(FieldPosition.LeftField);
            var note = ctrl.SubstitutionNoteForCapture;
            ctrl.ApplySubstitutionForCapture();
            yield return null;

            var ok = !ctrl.SubstitutionOpenForCapture
                     && !ctrl.UsesDhForCapture
                     && Same(ctrl.FielderForCapture(FieldPosition.LeftField), dh)
                     && !InLineup(ctrl, lf)
                     && InLineup(ctrl, pitcher)
                     && ctrl.PitcherSlotForCapture >= 0
                     && Same(ctrl.FielderForCapture(FieldPosition.Pitcher), pitcher);
            Line("⑦ DH解除（指→左翼）", ok,
                "指 " + NameOf(dh) + " → 左翼（元左翼 " + NameOf(lf) + " は退場）"
                + " ／ 投手 " + NameOf(pitcher) + " の打順スロット " + ctrl.PitcherSlotForCapture
                + " ／ DH=" + ctrl.UsesDhForCapture + "   [" + Trim(note) + "]");
        }

        // ── ⑧ DHをそのまま退場（指枠 →「そのまま退かせる」） ──
        // 期待: 守備陣は誰も動かない／元DHは打順から消える／投手が打順へ入る／DHは消滅。
        private IEnumerator DhWithdraw(MatchLiveController ctrl)
        {
            var dh = ctrl.DhForCapture;
            var pitcher = ctrl.FielderForCapture(FieldPosition.Pitcher);
            var before = Defense(ctrl);

            ctrl.OpenSubstitutionForCapture();
            ctrl.PickSubstitutionSlotForCapture(FieldPosition.DesignatedHitter);
            ctrl.PickSubstitutionDhWithdrawForCapture();
            var note = ctrl.SubstitutionNoteForCapture;
            ctrl.ApplySubstitutionForCapture();
            yield return null;

            var after = Defense(ctrl);
            var ok = !ctrl.SubstitutionOpenForCapture
                     && !ctrl.UsesDhForCapture
                     && before == after
                     && !InLineup(ctrl, dh)
                     && InLineup(ctrl, pitcher)
                     && ctrl.PitcherSlotForCapture >= 0;
            Line("⑧ DHそのまま退場", ok,
                "指 " + NameOf(dh) + " が退場 ／ 投手 " + NameOf(pitcher) + " の打順スロット "
                + ctrl.PitcherSlotForCapture + " ／ 守備陣は" + (before == after ? "不変" : "変化(" + after + ")")
                + " ／ DH=" + ctrl.UsesDhForCapture + "   [" + Trim(note) + "]");
        }

        // ── ⑨ DH下の投野交代（投枠 ↔ 右翼枠） ──
        // 期待: 右翼手が登板／元DHが右翼へ（DHでは投手が打順外なので入れ替えが成立しない）／旧投手は退場。
        private IEnumerator PromoteUnderDh(MatchLiveController ctrl)
        {
            var dh = ctrl.DhForCapture;
            var pitcher = ctrl.FielderForCapture(FieldPosition.Pitcher);
            var rf = ctrl.FielderForCapture(FieldPosition.RightField);

            ctrl.OpenSubstitutionForCapture();
            ctrl.PickSubstitutionSlotForCapture(FieldPosition.Pitcher);
            ctrl.PickSubstitutionSlotForCapture(FieldPosition.RightField);
            var note = ctrl.SubstitutionNoteForCapture;
            ctrl.ApplySubstitutionForCapture();
            yield return null;

            var ok = !ctrl.SubstitutionOpenForCapture
                     && !ctrl.UsesDhForCapture
                     && Same(ctrl.FielderForCapture(FieldPosition.Pitcher), rf)
                     && Same(ctrl.FielderForCapture(FieldPosition.RightField), dh)
                     && !InLineup(ctrl, pitcher);
            Line("⑨ 投野交代（DH下・投↔右翼）", ok,
                "右翼 " + NameOf(rf) + " が登板 ／ 指 " + NameOf(dh) + " が右翼へ ／ 旧投手 "
                + NameOf(pitcher) + " は退場 ／ DH=" + ctrl.UsesDhForCapture + "   [" + Trim(note) + "]");
        }

        // ── 判定ヘルパ ──

        /// <summary>守備位置→選手名の一覧（守備陣が動いていないことの突き合わせ用）。</summary>
        private static string Defense(MatchLiveController ctrl)
        {
            var sb = new StringBuilder();
            foreach (var pos in FielderOrder)
                sb.Append(NameOf(ctrl.FielderForCapture(pos))).Append('/');
            return sb.ToString();
        }

        private static readonly FieldPosition[] FielderOrder =
        {
            FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase, FieldPosition.ThirdBase,
            FieldPosition.Shortstop, FieldPosition.LeftField, FieldPosition.CenterField, FieldPosition.RightField,
        };

        /// <summary>その選手が今も打順に居るか。退場（リエントリー禁止）は打順から消えることで確かめる。</summary>
        private static bool InLineup(MatchLiveController ctrl, Player p)
        {
            if (p == null) return false;
            foreach (var q in ctrl.LineupForCapture)
                if (Same(q, p)) return true;
            return false;
        }

        // Player は record で、守備位置を変えると別インスタンスになる（`with { Position = ... }`）。
        // 同一人物かは名前＋背番号で見る（SourceId は自校ロスター由来なら入るが、デモ生成の控えは持たない）。
        private static bool Same(Player a, Player b)
            => a != null && b != null && a.Name == b.Name && a.UniformNumber == b.UniformNumber;

        private static string NameOf(Player p) => p == null ? "—" : p.Name;

        private void Line(string title, bool ok, string detail)
        {
            if (ok) _pass++; else _fail++;
            _log.Append(ok ? "OK   " : "NG   ").Append(title).Append("  ").Append(detail).Append("\n");
        }

        private static string Trim(string s) => s == null ? "" : s.Replace("\n", " ");

        // ── 試合の作り直し・撮影 ──

        /// <summary>
        /// MatchLive を付け直して新しい試合を始める（DH解除が不可逆なのでパスごとに要る）。
        /// <c>OnEnable</c> が <c>BuildGame</c> まで含めて全部やり直す建付けなので SetActive の往復で足りる。
        /// </summary>
        private static IEnumerator NewGame()
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                d.gameObject.SetActive(false);
            }
            yield return null;
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                if (d.gameObject.name == "MatchLive") d.gameObject.SetActive(true);
            }
            yield return null;
            yield return null;
        }

        private IEnumerator Capture(MatchLiveController ctrl, string file)
        {
            ctrl.OpenSubstitutionForCapture();   // 確定後の姿を打順表で見せる
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
