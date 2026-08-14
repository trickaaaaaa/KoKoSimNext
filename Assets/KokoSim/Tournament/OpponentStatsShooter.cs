#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Season;
using KokoSim.Unity.Match;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Tournament
{
    /// <summary>
    /// 【開発用・エディタ限定】相手校の今大会成績／通算成績が出ること（issue #634）の受け入れ絵。使い捨て。
    ///
    /// <para>1回戦を消化してから <b>2回戦の相手校</b>（＝1回戦を戦って勝ち上がってきた校）を撮る。
    /// 相手校に成績が積まれるのは1試合以上戦ってからなので、初戦の相手を撮っても「—」で正しい
    /// ＝症状の再現・確認には必ず1ラウンド進める必要がある。</para>
    ///
    /// <para>撮るのは①大会画面の校別詳細（今大会3列）②ライブ観戦HUD（相手校の通算が出る半回）。
    /// 全国裏試合（他県46）は起動しない＝自県ぶんの畳み込みだけで足りる。</para>
    ///
    /// 出力: screenshots/opponent-stats-634/*.png ／ out/opponent-stats-634/measured.tsv
    /// </summary>
    public sealed class OpponentStatsShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string Dir = "screenshots/opponent-stats-634";
        private const string OutDir = "out/opponent-stats-634";
        private const string SpawnFlag = "KokoSim.OpponentStatsShooter.Spawn";
        private const int PrefectureId = 13;   // 神奈川（HomeState と同じ）

        public static void Arm() => UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            if (!UnityEditor.EditorPrefs.GetBool(SpawnFlag, false)) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            new GameObject("~OpponentStatsAuto").AddComponent<OpponentStatsShooter>();
        }

        private readonly StringBuilder _log = new StringBuilder();

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Time.captureDeltaTime = 1f / 30f;
            Directory.CreateDirectory(Dir);
            Directory.CreateDirectory(OutDir);

            // ── 1. 大会モードへ入り、自校が勝ち上がるまで1回戦を消化する ──
            School opponent = null;
            for (var attempt = 0; attempt < 8 && opponent == null; attempt++)
            {
                SetUpTournament((ulong)(4242 + attempt * 977));
                GameSession.Current.PlayMatch();
                GameSession.Current.ConsumeResult();
                // 自校が敗退すると大会モードを抜けて Runner が消える＝null 安全に読む。
                opponent = GameSession.Current.InTournament && GameSession.Current.Runner != null
                    ? GameSession.Current.Runner.NextOpponent : null;
                _log.AppendLine($"attempt\t{attempt}\t自校勝ち上がり\t{(opponent != null ? "○" : "×（敗退）")}");
                Debug.Log($"[#634Shot] attempt {attempt}: {(opponent != null ? opponent.Name : "敗退")}");
                yield return null;
            }
            if (opponent == null) { Debug.LogError("[#634Shot] 自校が1回戦を突破できなかった"); yield break; }

            var stats = NationService.TournamentStats;
            var book = stats.ForSchool(opponent.Id);
            var career = stats.CareerForSchool(opponent.Id);
            _log.AppendLine($"2回戦の相手\t{opponent.Name}\tId={opponent.Id}");
            _log.AppendLine($"今大会の帳簿\t{(book == null ? "null" : book.Players.Count + "人")}");
            _log.AppendLine($"通算の帳簿\t{(career == null ? "null" : career.Players.Count + "人")}");
            if (book != null)
                foreach (var kv in book.Players)
                    _log.AppendLine($"  選手\t{kv.Key}\t打席{kv.Value.Batting.PlateAppearances}"
                                    + $"\t安打{kv.Value.Batting.Hits}\t投球回{kv.Value.Pitching.InningsText}");

            // ── 2. 大会画面の校別詳細（今大会3列） ──
            EnsureOnly("TournamentPreview");
            yield return null; yield return null;
            EnsureOnly("TournamentPreview");
            var doc = FindDoc("TournamentPreview");
            var ctrl = doc != null ? doc.GetComponent<TournamentPreviewController>() : null;
            if (ctrl == null) { Debug.LogError("[#634Shot] TournamentPreview が見つからない"); yield break; }
            yield return null;

            ctrl.ShowSchoolDetail(opponent.Name);
            yield return null;
            var scroll = doc.rootVisualElement.Q<ScrollView>();
            if (scroll != null) scroll.scrollOffset = Vector2.zero;
            yield return null; yield return null;
            yield return CaptureDoc(doc, "01-detail-opponent");

            // 表の下（背番号の大きい控え）まで送って、投手と野手が混じった並びを見る。
            if (scroll != null) scroll.scrollOffset = new Vector2(0, 420);
            yield return null; yield return null;
            yield return CaptureDoc(doc, "02-detail-opponent-bench");

            // 自校の校別詳細（自校ストア側から引けているか＝校IDの振り分けの逆側）。
            ctrl.ShowSchoolDetail(NationService.ManagerSchoolName);
            yield return null;
            if (scroll != null) scroll.scrollOffset = Vector2.zero;
            yield return null; yield return null;
            yield return CaptureDoc(doc, "03-detail-manager");

            // ── 3. ライブ観戦HUD（相手校の通算が出るか） ──
            yield return ShootLiveHud();

            File.WriteAllText(Path.Combine(OutDir, "measured.tsv"), _log.ToString());
            Debug.Log("[#634Shot] DONE\n" + _log);
        }

        private IEnumerator ShootLiveHud()
        {
            var live = GameSession.Current.BeginMatch();
            if (live == null) { Debug.LogError("[#634Shot] BeginMatch が null"); yield break; }
            _log.AppendLine($"ライブ相手\t{live.OpponentName}\tId={live.OpponentId}\t自校先攻={live.ManagerIsAway}");

            MatchLiveController.CaptureMode = true;
            MatchLiveController.Pending = new MatchLiveController.LiveMatchRequest
            {
                Progression = live.Progression,
                ManagerIsAway = live.ManagerIsAway,
                AwayName = live.ManagerIsAway ? NationService.ManagerSchoolName : live.OpponentName,
                HomeName = live.ManagerIsAway ? live.OpponentName : NationService.ManagerSchoolName,
                AwaySchoolId = live.ManagerIsAway ? NationService.ManagerSchoolId : live.OpponentId,
                HomeSchoolId = live.ManagerIsAway ? live.OpponentId : NationService.ManagerSchoolId,
                ManagerTacticalSense = ManagerService.TacticalSenseForAi,
            };

            EnsureOnly("MatchLive");
            yield return null; yield return null;
            EnsureOnly("MatchLive");
            var doc = FindDoc("MatchLive");
            var ctrl = doc != null ? doc.GetComponent<MatchLiveController>() : null;
            if (ctrl == null) { Debug.LogError("[#634Shot] MatchLive が見つからない"); yield break; }
            yield return null;

            // 表と裏の両方を撮る＝打者HUD・投手HUDの片方ずつが必ず相手校になる。
            var shotTop = false;
            var shotBottom = false;
            for (var plate = 1; plate <= 40 && !(shotTop && shotBottom); plate++)
            {
                if (!ctrl.AdvanceForCapture()) break;
                var snap = ctrl.SnapshotForCapture();
                if (snap == null) continue;
                ctrl.FreezeLiveForCapture(true);
                yield return null; yield return null;

                if (snap.OffenseIsTop && !shotTop)
                {
                    shotTop = true;
                    yield return CaptureDoc(doc, "04-hud-top");
                }
                else if (!snap.OffenseIsTop && !shotBottom)
                {
                    shotBottom = true;
                    yield return CaptureDoc(doc, "05-hud-bottom");
                }
                ctrl.FreezeLiveForCapture(false);
            }
            _log.AppendLine($"HUD撮影\t表={shotTop}\t裏={shotBottom}");
        }

        private static void SetUpTournament(ulong seed)
        {
            var roster = RosterService.Active;
            var manager = new School
            {
                Id = NationService.ManagerSchoolId,
                Name = NationService.ManagerSchoolName,
                PrefectureId = PrefectureId,
                Strength = TeamOverall.Of(roster),
            };

            var coeff = new NationCoefficients();
            var field = new List<School> { manager };
            foreach (var s in NationService.Nation.InPrefecture(PrefectureId))
            {
                if (s.Id == manager.Id) continue;
                field.Add(s);
            }

            // TournamentEntry.Enter と同じ組み立て（全国裏試合 NationBackgroundSim だけ起動しない）。
            var stats = NationService.TournamentStats;
            stats.StartTournament();
            var brains = new EnemyAiBrainFactory();
            var bg = new BackgroundMatchResolver(
                NationService.Rosters, new GameContext(), yearIndex: 1, stats: null,
                modernRules: null, calendarYear: SeasonClock.SeasonBaseYear, brains: brains);
            var runner = new TournamentRunner(
                field, manager, coeff, new Xoshiro256Random(seed), new TournamentSchedule(),
                "2027年 秋季神奈川県大会", new PlayerMatchResolver(), bg,
                onBackgroundMatch: (awayId, homeId, g) => stats.FoldMatch(awayId, homeId, g));

            GameSession.Current.Year = 1;
            GameSession.Current.EnterTournament(
                TournamentKind.Autumn, "2027年 秋季神奈川県大会", runner, field);
            GameSession.Current.ConsumeBanner();
        }

        private IEnumerator CaptureDoc(UIDocument doc, string file)
        {
            yield return null;
            yield return new WaitForEndOfFrame();
            yield return null;

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
                Debug.Log("[#634Shot] wrote " + file);
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                if (tex != null) DestroyImmediate(tex);
                rt.Release(); DestroyImmediate(rt);
            }
            yield return null;
        }

        private static void EnsureOnly(string name)
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                d.gameObject.SetActive(d.gameObject.name == name);
            }
        }

        private static UIDocument FindDoc(string name)
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                if (d.gameObject.name == name) return d;
            }
            return null;
        }
    }
}
#endif
