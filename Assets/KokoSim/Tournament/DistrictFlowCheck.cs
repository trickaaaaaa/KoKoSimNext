#if UNITY_EDITOR
using System.Collections;
using System.Text;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using KokoSim.Unity.Home;
using KokoSim.Unity.Shell;
using UnityEngine;

namespace KokoSim.Unity.Tournament
{
    /// <summary>
    /// 【開発用・エディタ限定】秋季地区予選→秋季県大会の実プレー経路を通しで確かめる
    /// （issue autumn-district-tournament-not-wired）。既定の本拠県（神奈川・7地区）で走らせる想定。
    /// 使い捨て。<c>Debug.Log</c> に PASS/FAIL を出すだけで、ゲーム状態は書き換えたまま止まる。
    /// </summary>
    public sealed class DistrictFlowCheck : MonoBehaviour
    {
        private readonly StringBuilder _log = new StringBuilder();
        private int _fail;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            yield return null;

            // 自校を圧倒的に強くして地区予選を確実に勝ち抜かせる（分岐を固定して検証を単純にする）。
            BoostManagerRoster();

            GameClock.Reset();

            // 週送りは対話大会（InTournament）に入ると足踏みする（自校の試合を消化しないと先へ進めない）。
            // 秋季地区予選（週23）に着くまでに夏（県大会→甲子園）を通るので、間に挟まる大会は
            // 汎用的に消化しながら進む（KoshienFlowCheckと同じ「週を1つずつ入れる」作法）。
            var guard = 0;
            while (GameSession.Current.Kind != TournamentKind.AutumnDistrict && guard++ < 200)
            {
                if (GameSession.Current.InTournament)
                {
                    yield return PlayOutCurrentTournament();
                    HomeState.Current.DismissResult();
                    yield return null;
                }
                else if (GameClock.Week >= 30)
                {
                    Check("秋季地区予選（週23）に到達した", false,
                        "週30を過ぎても AutumnDistrict にならない＝districted県で開始していない可能性");
                    Debug.Log("[DISTRICTFLOW] " + "FAIL(" + _fail + ")" + "\n" + _log);
                    yield break;
                }
                else
                {
                    GameClock.Advance(1);
                    yield return null;
                }
            }

            Check("大会モードに入った", GameSession.Current.InTournament);
            Check("種別が秋季地区予選（districted県のはず）",
                GameSession.Current.Kind == TournamentKind.AutumnDistrict,
                "kind=" + GameSession.Current.Kind);
            var districtFieldCount = GameSession.Current.Field.Count;
            Check("地区予選の出場校が県全体より少ない",
                districtFieldCount > 0 && districtFieldCount < NationService.PrefectureSchools.Count + 1,
                "district=" + districtFieldCount + " pref=" + (NationService.PrefectureSchools.Count + 1));
            GameSession.Current.ConsumeBanner();

            // ブロックリーグ戦の県（神奈川・issue #772）: 進行体がリーグ戦で、自校はブロックの全校と1回ずつ戦う
            var prefId = NationService.PrefectureId;
            var isLeague = DistrictPlanProvider.IsBlockLeagueOf(prefId);
            var league = GameSession.Current.Runner as KokoSim.Engine.Nation.Tournaments.BlockLeagueRunner;
            Check("地区予選の進行体がYAMLの形式どおり（リーグ戦＝BlockLeagueRunner）", isLeague == (league != null),
                "isLeague=" + isLeague + " runner=" + GameSession.Current.Runner?.GetType().Name);
            var myBlockSize = 0;
            var blockCount = 0;
            if (league != null)
            {
                var views = league.BuildBlockViews();
                blockCount = views.Count;
                myBlockSize = views[league.ManagerBlockIndex].Standings.Count;
                Note("地区のブロック: " + blockCount + "ブロック（自校のブロック " + myBlockSize + "校）");
                Check("ブロックは4校か3校", System.Linq.Enumerable.All(views, v => v.Standings.Count is 3 or 4));
            }

            var districtGames = 0;
            yield return PlayOutCurrentTournament(n => districtGames = n);
            var wonDistrict = GameSession.Current.LastOutcome != null && GameSession.Current.LastOutcome.IsChampion;
            Note("地区予選の結果: " + (league != null ? (league.ManagerQualified ? "県大会進出" : "敗退") : wonDistrict ? "優勝" : "敗退")
                + "（自校の試合 " + districtGames + "）");
            if (league != null)
            {
                Check("自校はブロックの全校と1回ずつ戦った（負けても途中で終わらない）", districtGames == myBlockSize - 1,
                    "games=" + districtGames + " block=" + myBlockSize);
                var q = GameSession.Current.LastDistrictQualifierIds;
                Check("自地区の進出校＝ブロック数×上位" + DistrictPlanProvider.AdvancePerBlockOf(prefId) + "校",
                    q != null && q.Count == blockCount * DistrictPlanProvider.AdvancePerBlockOf(prefId),
                    "qualifiers=" + (q?.Count ?? -1));
                Check("夏の順位が控えてある（ブロック分けのシード分散の材料）",
                    GameSession.Current.LastSummerPlacementIds is { Count: > 0 });
            }

            HomeState.Current.DismissResult();
            yield return null;

            // 地区予選の上位N校（YAML の advance_per_group・issue #688）は優勝しなくても県大会へ進む。
            // 圏外で敗退した年だけは県大会を観るだけで処理して地区大会へ直接つなぐ（例外を投げないこと）。
            if (!(GameSession.Current.InTournament && GameSession.Current.Kind == TournamentKind.Autumn))
            {
                Note("自校は県大会進出圏外（地区予選の結果: " + (wonDistrict ? "優勝" : "敗退")
                    + "）＝県大会は観るだけで処理した年（正常系）。");
                Debug.Log("[DISTRICTFLOW] " + (_fail == 0 ? "PASS" : "FAIL(" + _fail + ")") + "\n" + _log);
                yield break;
            }

            Check("秋季県大会（Autumn）へ連鎖した",
                GameSession.Current.InTournament && GameSession.Current.Kind == TournamentKind.Autumn,
                "kind=" + GameSession.Current.Kind);
            var prefFieldCount = GameSession.Current.Field.Count;
            var advance = DistrictPlanProvider.AdvancePerDistrictOf(NationService.PrefectureId);
            var districts = DistrictPlanProvider.AutumnDistrictsOf(NationService.PrefectureId);
            var seedExtra = DistrictPlanProvider.SeedExemptionOf(NationService.PrefectureId) ? 1 : 0;
            var maxField = districts * advance + seedExtra;
            if (isLeague)
            {
                // リーグ戦: 各地区の（ブロック数×上位N校）の合計。自校の地区は自校のぶん1校多い
                var per = DistrictPlanProvider.TeamsPerBlockOf(prefId);
                var adv = DistrictPlanProvider.AdvancePerBlockOf(prefId);
                maxField = seedExtra;
                for (var d = 0; d < districts; d++)
                {
                    var n = System.Linq.Enumerable.Count(NationService.PrefectureSchools, x => x.DistrictId == d)
                            + (d == (NationService.DistrictId ?? 0) ? 1 : 0);
                    maxField += System.Math.Min(n, (n + per - 1) / per * adv);
                }
            }
            Note("秋季県大会の出場校数（上限 " + maxField + "）: " + prefFieldCount);
            Check("秋季県大会の出場校数が 地区の進出校数の合計（＋夏代表の推薦）に収まる",
                prefFieldCount > districts && prefFieldCount <= maxField,
                "pref大会=" + prefFieldCount + " 上限=" + maxField);
            if (isLeague)
                Check("リーグ戦の県は進出校の合計とほぼ一致する（推薦が重複したぶん以内）",
                    prefFieldCount >= maxField - seedExtra, "pref大会=" + prefFieldCount + " 上限=" + maxField);
            GameSession.Current.ConsumeBanner();

            yield return PlayOutCurrentTournament();
            var wonPref = GameSession.Current.LastOutcome != null && GameSession.Current.LastOutcome.IsChampion;
            Note("秋季県大会の結果: " + (wonPref ? "優勝" : "敗退"));

            HomeState.Current.DismissResult();
            yield return null;

            // 地区大会（Regional）へ連鎖するはず＝既存のRegionalEntryが正しく後続していることの確認
            // （敗退年は地区大会に出ない年もある＝出場していなくても異常ではない）。
            Note("この後の遷移: InTournament=" + GameSession.Current.InTournament
                + " Kind=" + GameSession.Current.Kind);

            Debug.Log("[DISTRICTFLOW] " + (_fail == 0 ? "PASS" : "FAIL(" + _fail + ")") + "\n" + _log);
        }

        private static IEnumerator PlayOutCurrentTournament(System.Action<int> played = null)
        {
            var guard = 0;
            var games = 0;
            while (GameSession.Current.InTournament && !GameSession.Current.Runner.Finished && guard++ < 40)
            {
                GameSession.Current.PlayMatch();
                GameSession.Current.ConsumeResult();
                games++;
                yield return null;
            }
            played?.Invoke(games);
        }

        /// <summary>自校の部員を上限まで引き上げる（大会を確実に勝ち抜かせるための検証用の細工）。</summary>
        private static void BoostManagerRoster()
        {
            foreach (var p in RosterService.Roster)
            {
                foreach (var k in AbilityKinds.All)
                {
                    p.SetCap(k, 99);
                    p.SetLevel(k, 95);
                }
                p.Mental = 95;
                p.ConditionValue = 1.0;
            }
        }

        private void Check(string what, bool ok, string detail = null)
        {
            if (!ok) _fail++;
            _log.Append(ok ? "  ok   " : "  FAIL ").Append(what);
            if (!string.IsNullOrEmpty(detail)) _log.Append("  [").Append(detail).Append(']');
            _log.Append('\n');
        }

        private void Note(string text) => _log.Append("  ・    ").Append(text).Append('\n');
    }
}
#endif
