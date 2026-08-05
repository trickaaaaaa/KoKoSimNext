#if UNITY_EDITOR
using System.Collections;
using System.IO;
using KokoSim.Engine.Match.Timeline.Playback;
using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定・使い捨て】「ドット表現を綺麗にしたらどうなるか」の見比べ撮影。
    /// 同一場面を凍結したまま RT解像度4段（480/640/960/1920）× ポスタライズ{6段, OFF} の
    /// 8構成で <see cref="DuelStageRig.Target"/> を PNG に落とす。場面は2つ:
    ///   pitch = 対決構図（見逃しストライクの球が本塁手前に浮いている瞬間）
    ///   field = フィールド構図（センターフライを追っている中盤）
    /// 設定はすべて実行時のみ（PosterizeLevels は撮影後に復元・EnsureSize はPlay停止で消える）。
    /// 使い方（MCP execute_code）: DuelCleanCompareShooter.Arm(); → Play 開始で自動湧き。
    /// 出力: screenshots/clean-compare/（コミットしない）
    /// </summary>
    public sealed class DuelCleanCompareShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/clean-compare";
        private const string SpawnFlag = "KokoSim.DuelCleanCompareShooter.Spawn";

        private static readonly Vector2Int[] Sizes =
        {
            new Vector2Int(480, 270),
            new Vector2Int(640, 360),
            new Vector2Int(960, 540),
            new Vector2Int(1920, 1080),
        };

        /// <summary>2 未満は素通し Blit（DuelStageRig.OnEndCameraRendering の分岐）。</summary>
        private static readonly float[] Posters = { 6f, 1f };

        private const string MotionFlag = "KokoSim.DuelCleanCompareShooter.SpawnMotion";

        /// <summary>
        /// 動き比較の3構成（静止画の8構成から代表を絞る）。クリーンは1080でなく960×540＝
        /// GIFのサイズ都合の代表値。時間量子化（12fpsコマ打ち）の見え方は解像度に依らない。
        /// </summary>
        private static readonly Vector3Int[] MotionConfigs =
        {
            new Vector3Int(480, 270, 6),
            new Vector3Int(640, 360, 6),
            new Vector3Int(960, 540, 1),
        };

        public static void Arm() => UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);

        /// <summary>連番撮影（投球＋スイングの2場面 × MotionConfigs）。30fps相当の偶数コマだけ保存。</summary>
        public static void ArmMotion() => UnityEditor.EditorPrefs.SetBool(MotionFlag, true);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            bool still = UnityEditor.EditorPrefs.GetBool(SpawnFlag, false);
            bool motion = UnityEditor.EditorPrefs.GetBool(MotionFlag, false);
            if (!still && !motion) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            UnityEditor.EditorPrefs.SetBool(MotionFlag, false);
            var go = new GameObject("~DuelCleanCompareAuto").AddComponent<DuelCleanCompareShooter>();
            go._motion = motion;
        }

        private DuelStageRig _rig;
        private float _posterBefore;
        private bool _motion;
        private KokoSim.Engine.Match.Timeline.Playback.PlaybackBallSegment _batted;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);
            _posterBefore = DuelStageRig.PosterizeLevels;

            if (_motion)
            {
                foreach (var cfg in MotionConfigs)
                {
                    yield return RecordScene("pitch", cfg, false);
                    yield return RecordScene("swing", cfg, true);
                }
            }
            else
            {
                yield return PitchScene();
                yield return FieldScene();
            }

            DuelStageRig.PosterizeLevels = _posterBefore;
            Debug.Log("[CleanCompare] DONE -> " + Dir);
        }

        /// <summary>
        /// 1場面を実再生（1フレーム=Tick(1/60)）しながら偶数コマだけ連番PNGに落とす＝30fps相当。
        /// 構成間の同期はシード固定＋固定刻みの決定論で担保する（同じコマ数・同じ場面が撮れる）。
        /// </summary>
        private IEnumerator RecordScene(string scene, Vector3Int cfg, bool swing)
        {
            _rig = new DuelStageRig();
            if (!_rig.Available) { Debug.LogError("[CleanCompare] rig が組めない"); yield break; }
            DuelStageRig.PosterizeLevels = cfg.z;
            _rig.EnsureSize(cfg.x, cfg.y);
            for (int i = 0; i < 90; i++) { _rig.Tick(1f / 60f); yield return null; }

            string dir = Path.Combine(Dir, "seq_" + scene + "_" + cfg.x + "x" + cfg.y + "_p" + cfg.z);
            Directory.CreateDirectory(dir);

            if (swing && _batted == null) _batted = RealBatted("フライ");

            _rig.OnPitchWindup();
            int releaseTick = Mathf.CeilToInt(DuelStageRig.WindupSeconds * 60f);
            // 投球=捕球＋据え置きまで0.95s / スイング=接触〜振り抜き・打球の行方まで1.5s
            int total = releaseTick + Mathf.CeilToInt((swing ? 1.5f : 0.95f) * 60f);
            int saved = 0;
            for (int i = 0; i < total; i++)
            {
                _rig.Tick(1f / 60f);
                if (i == releaseTick)
                {
                    if (swing) _rig.OnPitchReleased(0f, DuelPitchOutcome.InPlayContact, null, _batted);
                    else _rig.OnPitchReleased(0f, DuelPitchOutcome.CalledStrike);
                }
                yield return new WaitForEndOfFrame();
                if ((i & 1) == 0)
                {
                    Save(_rig.Target, Path.Combine("seq_" + scene + "_" + cfg.x + "x" + cfg.y + "_p" + cfg.z,
                        "f" + (saved++).ToString("0000")));
                }
                yield return null;
            }
            Debug.Log("[CleanCompare] seq " + scene + " " + cfg.x + "x" + cfg.y + " p" + cfg.z + " -> " + saved + "コマ");
            _rig.Dispose();
            _rig = null;
        }

        /// <summary>
        /// 実試合を1つ回して打球セグメントを採る（DuelBattedShooter.RealBatted と同じ。
        /// シード固定なので構成間で同じ打球になる）。
        /// </summary>
        private static KokoSim.Engine.Match.Timeline.Playback.PlaybackBallSegment RealBatted(string resultContains)
        {
            var rng = new KokoSim.Engine.Core.Xoshiro256Random(20260731UL);
            var away = KokoSim.Engine.Nation.StrengthTeamFactory.Create(58, "撮影A", rng);
            var home = KokoSim.Engine.Nation.StrengthTeamFactory.Create(58, "撮影B",
                new KokoSim.Engine.Core.Xoshiro256Random(20260731UL ^ 0xABCDUL));
            var ctx = new KokoSim.Engine.Match.Game.GameContext { CaptureTimelines = true };
            var prog = new KokoSim.Engine.Match.Timeline.Playback.MatchProgression(away, home, ctx, 20260731UL);
            int guard = 0;
            while (guard++ < 400 && prog.Advance())
            {
                var play = prog.Current?.Play;
                if (play == null || play.Result == null || !play.Result.Contains(resultContains)) continue;
                foreach (var s in play.Ball)
                {
                    if (s is KokoSim.Engine.Match.Timeline.Playback.PitchSegment) continue;
                    if (s is KokoSim.Engine.Match.Timeline.Playback.EndpointBallSegment eb
                        && eb.Kind == KokoSim.Engine.Match.Timeline.BallSegmentKind.Pitch) continue;
                    return s;
                }
            }
            return null;
        }

        private void OnDisable()
        {
            if (_posterBefore > 0f) DuelStageRig.PosterizeLevels = _posterBefore;
            if (_rig != null) { _rig.Dispose(); _rig = null; }
        }

        private IEnumerator PitchScene()
        {
            _rig = new DuelStageRig();
            if (!_rig.Available) { Debug.LogError("[CleanCompare] rig が組めない（素体/クリップ未導入）"); yield break; }
            _rig.EnsureSize(480, 270);
            // 構えを落ち着かせてから投げる（DuelBattedShooter.ProbeSeek と同じ手順。
            // 湧かせた直後に投げるとバット向きの焼き付け元が落ち着かない）
            for (int i = 0; i < 90; i++) { _rig.Tick(1f / 60f); yield return null; }
            _rig.OnPitchWindup();
            for (float t = 0f; t < DuelStageRig.WindupSeconds; t += 1f / 60f)
            {
                _rig.Tick(1f / 60f);
                yield return null;
            }
            _rig.OnPitchReleased(0f, DuelPitchOutcome.CalledStrike);
            // 既定の飛翔 0.45s のうち 0.32s ＝球が本塁の少し手前に浮いている
            for (float t = 0f; t < 0.32f; t += 1f / 60f)
            {
                _rig.Tick(1f / 60f);
                yield return null;
            }
            yield return Sweep("pitch");
            _rig.Dispose();
            _rig = null;
        }

        private IEnumerator FieldScene()
        {
            var play = PlaybackSamples.All[1];   // ② センターフライ（DuelFieldPlayShot と同じ添字）
            _rig = new DuelStageRig();
            if (!_rig.Available) yield break;
            _rig.EnsureSize(480, 270);
            for (int i = 0; i < 8; i++) { _rig.Tick(1f / 60f); yield return null; }
            float contact = ContactOf(play);
            _rig.BeginFieldPlay(play, contact);
            // 打球が上がって野手が追っている中盤で止める
            float target = contact + ((float)play.Dur - contact) * 0.55f;
            for (float t = contact; t < target; t += 1f / 60f)
            {
                _rig.Tick(1f / 60f);
                _rig.SetFieldTime(t);
                yield return null;
            }
            yield return Sweep("field");
            _rig.Dispose();
            _rig = null;
        }

        /// <summary>
        /// 場面を凍結（Tickしない）したまま8構成で焼く。EnsureSize のRT差し替え直後は真っ黒に
        /// 焼ける回がある（Issue397PitchCountShooter の教訓）ので、1フレーム待ってから
        /// RenderNow で描き直して確定させる。
        /// </summary>
        private IEnumerator Sweep(string scene)
        {
            foreach (var size in Sizes)
            {
                foreach (var poster in Posters)
                {
                    DuelStageRig.PosterizeLevels = poster;
                    _rig.EnsureSize(size.x, size.y);
                    yield return null;
                    yield return new WaitForEndOfFrame();
                    _rig.RenderNow();
                    Save(_rig.Target, scene + "_" + size.x + "x" + size.y + "_p" + (int)poster);
                    yield return null;
                }
            }
            DuelStageRig.PosterizeLevels = _posterBefore;
            _rig.EnsureSize(480, 270);
        }

        /// <summary>打球の瞬間＝先頭の投球セグメントの終わり（DuelFieldPlayShot.ContactOf と同定義）。</summary>
        private static float ContactOf(PlaybackPlay play)
        {
            if (play == null || play.Ball.Count == 0) return 0f;
            var head = play.Ball[0];
            bool isPitch = head is PitchSegment
                || (head is EndpointBallSegment eb
                    && eb.Kind == KokoSim.Engine.Match.Timeline.BallSegmentKind.Pitch);
            return isPitch ? (float)head.T1 : 0f;
        }

        private static void Save(RenderTexture rt, string name)
        {
            var prev = RenderTexture.active;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(Path.Combine(Dir, name + ".png"), tex.EncodeToPNG());
            Destroy(tex);
            Debug.Log("[CleanCompare] wrote " + name + " (" + rt.width + "x" + rt.height + ")");
        }
    }
}
#endif
