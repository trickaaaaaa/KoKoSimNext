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
    /// （issue autumn-district-tournament-not-wired）。既定の本拠県（神奈川・4地区）で走らせる想定。
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

            yield return PlayOutCurrentTournament();
            var wonDistrict = GameSession.Current.LastOutcome != null && GameSession.Current.LastOutcome.IsChampion;
            Note("地区予選の結果: " + (wonDistrict ? "優勝" : "敗退"));

            HomeState.Current.DismissResult();
            yield return null;

            if (!wonDistrict)
            {
                Note("自校が地区予選で敗退＝秋季県大会には出場しない年（正常系。ここで終了）。");
                Debug.Log("[DISTRICTFLOW] " + (_fail == 0 ? "PASS" : "FAIL(" + _fail + ")") + "\n" + _log);
                yield break;
            }

            Check("秋季県大会（Autumn）へ連鎖した",
                GameSession.Current.InTournament && GameSession.Current.Kind == TournamentKind.Autumn,
                "kind=" + GameSession.Current.Kind);
            var prefFieldCount = GameSession.Current.Field.Count;
            Note("秋季県大会の出場校数（地区代表の集合のはず）: " + prefFieldCount);
            Check("秋季県大会の出場校は地区予選より少ない（全県ではなく地区代表の集合）",
                prefFieldCount <= districtFieldCount || prefFieldCount < NationService.PrefectureSchools.Count,
                "district=" + districtFieldCount + " pref大会=" + prefFieldCount);
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

        private static IEnumerator PlayOutCurrentTournament()
        {
            var guard = 0;
            while (GameSession.Current.InTournament && !GameSession.Current.Runner.Finished && guard++ < 40)
            {
                GameSession.Current.PlayMatch();
                GameSession.Current.ConsumeResult();
                yield return null;
            }
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
