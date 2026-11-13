#if KOKOSIM_DEBUG || UNITY_EDITOR || DEVELOPMENT_BUILD
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Unity.Match;
using KokoSim.Unity.Shell;

namespace KokoSim.Unity.Debugging
{
    /// <summary>デバッグ試合の画面側の指定（issue #905）。engine の <see cref="DebugMatchOptions"/> に表示専用の項目を足したもの。</summary>
    public sealed class DebugMatchRequest
    {
        public string StadiumId;
        public CrowdTurnout Crowd = CrowdTurnout.Full;
        /// <summary>場面（scenarios.yaml の id）。空なら1回表から。</summary>
        public string ScenarioId;
        /// <summary>母種（16進）。空なら既定値。</summary>
        public string SeedHex;
        /// <summary>プレイヤーが采配する側が先攻か。</summary>
        public bool ManagerIsAway;
        /// <summary>アルプスの応援（甲子園の音の確認用）。</summary>
        public bool BrassBand;
    }

    /// <summary>
    /// セーブ無しで、選んだ球場の試合画面（MatchLive）を開く（issue #905）。
    /// 試合の中身は engine の <see cref="DebugMatchFactory"/> が組み、ここは <see cref="MatchLiveController.LiveMatchRequest"/>
    /// へ詰めて遷移するだけ。本番の試合フロー（<see cref="MatchStartFlow"/>）は通らず、成績もどこにも残らない。
    /// </summary>
    public static class DebugMatchLauncher
    {
        /// <summary>配色だけを借りる校ID（パレットから決定論で引く。実在校との対応に意味は無い）。</summary>
        private const int AwayColorId = 9051;
        private const int HomeColorId = 9052;

        /// <summary>試合を組んで MatchLive へ遷移する。失敗したら理由を返す（null＝成功）。</summary>
        /// <param name="returnTo">試合（結果画面）を閉じたあとに戻る画面。</param>
        public static string Launch(DebugMatchRequest req, string returnTo)
        {
            var stadium = StadiumCatalogProvider.Find(req.StadiumId);
            if (stadium == null) return "球場が見つからない: " + req.StadiumId;

            ScenarioDefinition scenario = null;
            if (!string.IsNullOrEmpty(req.ScenarioId)
                && !DebugScenarios.Catalog.TryGet(req.ScenarioId, out scenario))
                return "場面が見つからない: " + req.ScenarioId;

            ulong? seed = null;
            if (!string.IsNullOrWhiteSpace(req.SeedHex))
            {
                if (!ulong.TryParse(req.SeedHex.Trim().Replace("0x", ""),
                        System.Globalization.NumberStyles.HexNumber, null, out var s))
                    return "シードが16進数でない: " + req.SeedHex;
                seed = s;
            }

            var built = DebugMatchFactory.Build(stadium, new DebugMatchOptions { Seed = seed, Scenario = scenario });
            var ctx = DebugTraceHub.AttachTo(built.Ctx);   // HUD が閉じていれば恒等
            var prog = new MatchProgression(built.Away, built.Home, ctx, built.Seed, built.Start);
            if (scenario != null && ForcedOutcomes.TryParse(scenario.Force, out var force)) prog.ForceNext(force);

            MatchLiveController.Pending = new MatchLiveController.LiveMatchRequest
            {
                Progression = prog,
                ManagerIsAway = req.ManagerIsAway,
                AwayName = built.Away.Name,
                HomeName = built.Home.Name,
                AwayColors = TeamColorProvider.Palette.ColorsFor(AwayColorId),
                HomeColors = TeamColorProvider.Palette.ColorsFor(HomeColorId),
                // 校IDは持たない＝通算成績の join は「—」になる（デモ経路と同じ）
                AwaySchoolId = int.MinValue,
                HomeSchoolId = int.MinValue,
                ManagerTacticalSense = 70,
                RematchOnTie = false,
                Crowd = req.Crowd,
                BrassBand = req.BrassBand,
                // 結果画面の「閉じる」で呼ばれる。保存はしない＝戻るだけ
                OnComplete = _ => ScreenRouter.Instance?.ShowDeferred(returnTo),
            };
            ScreenRouter.Instance?.ShowDeferred("MatchLive");
            return null;
        }

        /// <summary>甲子園なら応援を既定でONにする（本番と同じ「応援は甲子園だけ」の規則）。</summary>
        public static bool DefaultBrassBandFor(Stadium stadium) => stadium?.Id == Stadium.Koshien.Id;
    }
}
#endif
