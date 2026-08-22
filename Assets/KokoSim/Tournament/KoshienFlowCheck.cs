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
    /// 【開発用・エディタ限定】夏の甲子園の実プレー経路を通しで確かめる（設計書05 §1.1a）。
    /// スクショでは分からない「順序と副作用」を見る:
    /// 県予選 → 結果を閉じる → 激励金/抽選会のノベルが積まれる → 甲子園へ入る → 3年がまだ引退していない。
    /// 使い捨て。<c>Debug.Log</c> に PASS/FAIL を出すだけで、ゲーム状態は書き換えたまま止まる。
    /// </summary>
    public sealed class KoshienFlowCheck : MonoBehaviour
    {
        private readonly StringBuilder _log = new StringBuilder();
        private int _fail;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            yield return null;

            // 自校を圧倒的に強くして県予選を確実に勝ち抜かせる（分岐①＝出場する年を見たいため）。
            BoostManagerRoster();

            // 実フローと同じ入口で夏の県大会へ入る（全国46県の背景シムもここで走る）。
            GameClock.Reset();
            GameClock.Advance(13);           // 第13週＝夏の県大会の開幕週
            Check("県大会に入った", GameSession.Current.InTournament);
            Check("種別が夏の県大会", GameSession.Current.Kind == TournamentKind.Summer);
            GameSession.Current.ConsumeBanner();

            // 自校の試合を全部消化する（1試合ずつ・背景シムを進ませるため毎回1フレーム待つ）。
            var guard = 0;
            while (GameSession.Current.InTournament && !GameSession.Current.Runner.Finished && guard++ < 20)
            {
                GameSession.Current.PlayMatch();
                GameSession.Current.ConsumeResult();
                yield return null;
            }
            var wonPref = GameSession.Current.LastOutcome != null && GameSession.Current.LastOutcome.IsChampion;
            Note("県大会の結果: " + (wonPref ? "優勝" : "敗退"));

            var fundsBefore = ManagerService.Manager.Funds;
            var weekBefore = GameClock.Week;
            EventService.Clear();            // 週送りで溜まった分と混ざらないようにしてから測る

            // ★実フローの後始末（週送り → 甲子園へ接続）。
            HomeState.Current.DismissResult();
            yield return null;

            Note("週: " + weekBefore + " → " + GameClock.Week);
            Check("3年がまだ引退していない（甲子園の前）",
                !NewTeamService.Pending && GameClock.Week <= 17,
                "week=" + GameClock.Week + " newTeamPending=" + NewTeamService.Pending);

            if (wonPref)
            {
                Check("甲子園の対話モードへ入った",
                    GameSession.Current.InTournament
                    && GameSession.Current.Kind == TournamentKind.SummerKoshien);
                Check("出場校が49代表", GameSession.Current.Field.Count == 49,
                    "field=" + GameSession.Current.Field.Count);
                Check("激励金で部費が増えた", ManagerService.Manager.Funds > fundsBefore,
                    fundsBefore + " → " + ManagerService.Manager.Funds);
                CheckQueued("koshien_berth_donation");
                CheckQueued("koshien_draw");
            }
            else
            {
                Check("観るだけの甲子園が始まった", GameSession.Current.HasSpectatedKoshien);
                Check("対話モードには入っていない", !GameSession.Current.InTournament);
                Check("出場校が49代表", GameSession.Current.KoshienField.Count == 49,
                    "field=" + GameSession.Current.KoshienField.Count);
            }

            // 出場校ぜんぶに「N年ぶりN回目」が付いている（＝展望の出場列が空にならない）。
            var field = GameSession.Current.InTournament
                ? GameSession.Current.Field : GameSession.Current.KoshienField;
            var missing = 0;
            foreach (var s in field)
                if (string.IsNullOrEmpty(GameSession.Current.Records.For(s.Id).SummerAppearanceLabel)) missing++;
            Check("全代表に出場歴の文言が付いた", missing == 0, "未記録 " + missing + " 校");

            // 県予選成績（自校ぶんは焼き取り済み・他県は背景で打ち直し中）。
            var lookup = NationService.StatsLookup;
            var own = lookup.PrefQualifierBook(NationService.ManagerSchoolId);
            Check("自校の県予選成績が焼き取られた", own != null && own.Players.Count > 0,
                "players=" + (own == null ? 0 : own.Players.Count));

            Note("他県代表の打ち直し: " + KoshienQualifierStatsSim.Done + "/" + KoshienQualifierStatsSim.Total);
            KoshienQualifierStatsSim.EnsureCompleted();
            var filled = 0;
            foreach (var s in field)
            {
                if (s.Id == NationService.ManagerSchoolId) continue;
                var b = lookup.PrefQualifierBook(s.Id);
                if (b != null && b.Players.Count > 0) filled++;
            }
            Check("他県代表の県予選成績が埋まった", filled >= field.Count - 2,
                filled + "/" + (field.Count - 1) + " 校");

            Debug.Log("[KOFLOW] " + (_fail == 0 ? "PASS" : "FAIL(" + _fail + ")") + "\n" + _log);
        }

        /// <summary>自校の部員を上限まで引き上げる（県予選を確実に勝ち抜かせるための検証用の細工）。</summary>
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

        private void CheckQueued(string eventId)
        {
            var found = false;
            foreach (var e in EventService.Pending) if (e.Id == eventId) { found = true; break; }
            Check("ノベルに " + eventId + " が積まれた", found, "pending=" + EventService.Pending.Count);
        }

        private void Note(string text) => _log.Append("  ・    ").Append(text).Append('\n');
    }
}
#endif
