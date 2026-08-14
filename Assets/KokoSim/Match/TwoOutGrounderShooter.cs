#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定】2アウト・一塁の内野ゴロでフォースの走者が動くことの絵の確認（issue #625）。使い捨て。
    ///
    /// <para><b>なぜ実試合を流すか</b>: engine 側の <c>FieldingPlay</c> は required メンバーを持ち、
    /// Unity は C# 9 なので<b>Unity 側から組めない</b>（リグへ直接流す手が使えない）。
    /// 場面自体は珍しくない（2アウト・走者一塁のゴロアウト）ので、打席を送って自然発生を待つ。</para>
    ///
    /// <para>撮るのは①接触直後 ②打者が一塁でアウトになる頃 ③プレー終わり。3D守備ビューと
    /// 小ダイヤモンド（#595）が同じ画面に写るので1枚で両方確認できる。</para>
    ///
    /// 出力: screenshots/two-out-grounder-625/*.png ／ out/two-out-grounder-625/plays.tsv
    /// </summary>
    public sealed class TwoOutGrounderShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string ShotDir = "screenshots/two-out-grounder-625";
        private const string OutDir = "out/two-out-grounder-625";
        private const string SpawnFlag = "KokoSim.TwoOutGrounderShooter.Spawn";

        /// <summary>打席を送る上限（該当場面が出るまで）。</summary>
        private const int MaxPlates = 400;

        public static void Arm() => UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            if (!UnityEditor.EditorPrefs.GetBool(SpawnFlag, false)) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            new GameObject("~TwoOutGrounderAuto").AddComponent<TwoOutGrounderShooter>();
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
            if (_ctrl == null) { Debug.LogError("[#625Shot] MatchLive/Controller が見つからない"); yield break; }
            yield return null;

            var log = new StringBuilder("打席\t前アウト\t一塁\t守備フォーカス\t結果\t撮った\n");
            var plates = 0;
            var shot = false;

            // 采配窓で見た「その打席に入る前の」状況。守備フォーカスが立った時にはもう更新されている
            // ことがあるので、窓のうちに控えておく（#595 と同じ理由）。
            var outsBefore = -1;
            var firstBefore = false;

            // ⚠ AdvanceForCapture は「1打席を解決して再生を始める」。毎フレーム叩くと守備フォーカスが
            // 立つ前に次の打席へ進んでしまい、**試合だけが最後まで終わる**（初回実測: 撮影0枚・gameOver）。
            // 1打席送ったら再生の面倒を見終わるまで待つ。
            while (!shot && plates < MaxPlates && !_ctrl.IsGameOver)
            {
                // この打席に入る前の状況を控える（守備フォーカスが立つ頃には更新されている）
                var at = _ctrl.CurrentAtBatForCapture;
                outsBefore = at?.Outs ?? -1;
                firstBefore = at?.BaseFirst ?? false;
                plates++;

                if (!_ctrl.AdvanceForCapture()) break;

                // 再生が始まるのを待つ（三振・四球など打球の出ない打席は立たないので上限で抜ける）
                var waited = 0;
                while (!_ctrl.FieldPlayActiveForCapture && waited++ < 180) yield return null;
                var active = _ctrl.FieldPlayActiveForCapture;

                // 打球の種別まで見る（2アウト・一塁でも外野への打球では意味が無い＝初回実測で右飛を撮った）。
                var result = _ctrl.CurrentPa?.Play?.Result ?? "-";
                bool want = active && outsBefore == 2 && firstBefore && result.Contains("ゴロ");
                log.Append(plates).Append('\t').Append(outsBefore).Append('\t')
                   .Append(firstBefore ? "有" : "—").Append('\t').Append(active ? "有" : "—").Append('\t')
                   .Append(result).Append('\t').Append(want ? "★" : "—").Append('\n');

                if (want)
                {
                    shot = true;
                    // ①接触直後 ②打者が一塁へ着く頃 ③プレー終わり
                    yield return WaitFrames(6);
                    yield return Shoot("1-contact");
                    yield return WaitFrames(70);
                    yield return Shoot("2-batter-out");
                    yield return WaitFrames(50);
                    yield return Shoot("3-end");
                    break;
                }

                // 再生が畳まれるまで待ってから次の打席へ
                waited = 0;
                while (_ctrl.FieldPlayActiveForCapture && waited++ < 600) yield return null;
                yield return null;
            }

            File.WriteAllText(Path.Combine(OutDir, "plays.tsv"), log.ToString());
            Debug.Log($"[#625Shot] {(shot ? "撮れた" : "★該当場面が出ないまま打ち切り")} 打席={plates} → {ShotDir}");
            Time.captureDeltaTime = 0f;
        }

        private IEnumerator WaitFrames(int n)
        {
            for (int i = 0; i < n; i++) yield return null;
        }

        /// <summary>画面まるごと1枚（撮影の間は進行を凍らせる＝節目がずれない。#595 と同じ作法）。</summary>
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
