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
    /// 【開発用・エディタ限定】春のセンバツの実プレー経路を通しで確かめる（設計書05 §1.5b）。
    /// 秋季県大会 → 地区大会 → 明治神宮大会 → センバツ選考発表 → センバツ本戦、の連鎖を1本で追う。
    /// 使い捨て。<c>Debug.Log</c> に PASS/FAIL を出すだけで、ゲーム状態は書き換えたまま止まる。
    /// </summary>
    public sealed class SenbatsuFlowCheck : MonoBehaviour
    {
        private readonly StringBuilder _log = new StringBuilder();
        private int _fail;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            yield return null;

            // 自校を圧倒的に強くして各大会を確実に勝ち抜かせる（分岐①＝全大会に出場する年を見るため）。
            // 「途中で観戦/選外に落ちる」分岐（RegionalEntry の !qualifies・JinguEntry の
            // championIsManager=false・SenbatsuEntry の非選出）を確かめたいときは、下の BoostLevel を
            // 0（無敗ブーストなし）や中間値へ書き換え、必要なら GameSeed.Reset(0x...) で母種も固定して使う
            // （GameSeed.Master は起動ごとに非決定的＝DateTime.UtcNow.Ticks 由来なので、特定の分岐を
            // 再現よく狙うには母種の固定が要る。2026-08-22 の検証で3分岐とも実機確認済み）。
            BoostManagerRoster(BoostLevel, BoostCap);
            var cal = new SeasonCalendar();

            GameClock.Reset();
            // 第13週＝夏の県大会の開幕週。秋季(23週)より前にここを跨ぐため、夏を消化しないまま23週へ
            // 直行すると「夏の県大会が対話モードのまま止まっている」状態で足止めされる（Mode!=Normal の間は
            // 週ループの大会開幕フックが働かない）。夏→(甲子園) を先に片づけてから秋へ進む。
            GameClock.Advance(cal.SummerTournamentStartWeek);
            Check("夏の県大会に入った", GameSession.Current.InTournament);
            Check("種別が夏の県大会", GameSession.Current.Kind == TournamentKind.Summer);
            GameSession.Current.ConsumeBanner();
            EventService.Clear();
            yield return StartCoroutine(PlayChainToCompletion());
            Note("週: " + GameClock.Week + "（夏の大会後の連鎖が済んだ時点）");

            GameClock.Advance(cal.AutumnTournamentStartWeek - GameClock.Week);   // 秋季県大会の開幕週まで進める
            Check("秋季県大会に入った", GameSession.Current.InTournament);
            Check("種別が秋季県大会", GameSession.Current.Kind == TournamentKind.Autumn);
            GameSession.Current.ConsumeBanner();

            EventService.Clear();
            yield return StartCoroutine(PlayChainToCompletion());

            Note("週: " + GameClock.Week + "（秋季大会後の連鎖が済んだ時点） mode=" + GameSession.Current.Mode
                + " inTournament=" + GameSession.Current.InTournament
                + (GameSession.Current.InTournament ? " kind=" + GameSession.Current.Kind : ""));
            var pending = GameSession.Current.PendingSenbatsuFieldIds;
            Check("センバツ32校が確定した", pending != null && pending.Count == 32,
                "count=" + (pending == null ? 0 : pending.Count));

            var toGo = cal.SenbatsuAnnounceWeek - GameClock.Week;
            Note("選考発表週まで " + toGo + " 週");
            if (toGo > 0)
            {
                var fundsBefore = ManagerService.Manager.Funds;
                EventService.Clear();
                GameClock.Advance(toGo);
                yield return null;
                Check("選考発表週に到達", GameClock.Week == cal.SenbatsuAnnounceWeek,
                    "week=" + GameClock.Week);

                var selected = GameSession.Current.InTournament
                    && GameSession.Current.Kind == TournamentKind.SenbatsuKoshien;
                Note("センバツ: " + (selected ? "選出（対話）" : "選出（対話）でなければ選外/観戦"));

                if (selected)
                {
                    Check("激励金で部費が増えた", ManagerService.Manager.Funds > fundsBefore,
                        fundsBefore + " → " + ManagerService.Manager.Funds);
                    CheckQueued("senbatsu_berth_donation");
                    CheckQueued("senbatsu_draw");
                    Check("出場校が32校", GameSession.Current.Field.Count == 32,
                        "field=" + GameSession.Current.Field.Count);
                    yield return StartCoroutine(PlayChainToCompletion());
                }
                else
                {
                    var spectated = GameSession.Current.HasSpectatedSenbatsu;
                    Check("観るだけのセンバツ or 選外イベントのいずれか",
                        spectated || HasQueued("senbatsu_not_selected"),
                        "spectated=" + spectated);
                    if (spectated)
                        Check("観戦の出場校が32校", GameSession.Current.SenbatsuField.Count == 32,
                            "field=" + GameSession.Current.SenbatsuField.Count);
                }
            }

            // 出場歴の文言（春）が付いていること。
            var recordsBook = GameSession.Current.Records;
            var missing = 0;
            var checkedCount = 0;
            foreach (var id in pending ?? new System.Collections.Generic.List<int>())
            {
                checkedCount++;
                if (string.IsNullOrEmpty(recordsBook.For(id).SpringAppearanceLabel)) missing++;
            }
            Check("センバツ32校に出場歴の文言が付いた", missing == 0,
                "未記録 " + missing + "/" + checkedCount + " 校");

            Debug.Log("[SBFLOW] " + (_fail == 0 ? "PASS" : "FAIL(" + _fail + ")") + "\n" + _log);
        }

        /// <summary>
        /// 対話中の大会があれば決着まで消化し、結果を閉じる（連鎖で次の大会へ入ればそれも消化する）。
        /// 秋季県大会→地区大会→神宮大会の連鎖を1本のループで追う。
        /// </summary>
        private IEnumerator PlayChainToCompletion()
        {
            var guard = 0;
            while (guard++ < 10)
            {
                if (GameSession.Current.InTournament) LogPreview();
                // 秋季県大会は夏の分割地区(SummerRegions)と違い県全体が1本の裏ブラケット（神奈川で212校規模）。
                // 背景解決はフレーム毎にスロットルされるため、自校の1ラウンド進行に複数フレームかかることがある
                // （実測: コルーチンでは60回試行してラウンド2までしか進まないが、フレーム制約のない直接呼び出しでは
                // 1呼び出し1ラウンドで即決着する＝ロジック自体は正しく、この上限だけが小さすぎた）。
                var matchGuard = 0;
                while (GameSession.Current.InTournament && !GameSession.Current.Runner.Finished && matchGuard++ < 400)
                {
                    GameSession.Current.PlayMatch();
                    GameSession.Current.ConsumeResult();
                    yield return null;
                }
                if (GameSession.Current.PendingTournamentWrapUp == null) yield break;
                var kind = GameSession.Current.PendingTournamentWrapUp.Kind;
                var champ = GameSession.Current.LastOutcome != null && GameSession.Current.LastOutcome.IsChampion;
                Note(kind + " の結果: " + (champ ? "優勝" : "敗退"));
                HomeState.Current.DismissResult();
                yield return null;
            }
        }

        /// <summary>0＝ブーストしない（平均的な強さのまま＝途中で観戦/選外へ落ちる分岐を確かめる用）。
        /// 実行前に書き換えて使う使い捨てのつまみ（本体コードには出てこない）。</summary>
        private const int BoostLevel = 95;
        private const int BoostCap = 99;

        private static void BoostManagerRoster(int level, int cap)
        {
            if (level <= 0) return;
            foreach (var p in RosterService.Roster)
            {
                foreach (var k in AbilityKinds.All)
                {
                    p.SetCap(k, cap);
                    p.SetLevel(k, level);
                }
                p.Mental = System.Math.Min(95, level);
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

        private bool HasQueued(string eventId)
        {
            foreach (var e in EventService.Pending) if (e.Id == eventId) return true;
            return false;
        }

        private void CheckQueued(string eventId) => Check("ノベルに " + eventId + " が積まれた", HasQueued(eventId),
            "pending=" + EventService.Pending.Count);

        private void Note(string text) => _log.Append("  ・    ").Append(text).Append('\n');

        /// <summary>
        /// 大会展望（TournamentPreviewState.Build）を今の対話中大会に対して1回組んでみる（設計書05 §1.5b
        /// 計画F6の検証）。地区大会・神宮大会・センバツ本戦それぞれの見出し文言・出場校一覧の有無だけ確かめる
        /// （UIのレイアウトはControllerが既存のまま描くだけなので、ここではUnityEngine非依存のViewModelを見る）。
        /// </summary>
        private void LogPreview()
        {
            var kind = GameSession.Current.Kind;
            var v = new TournamentPreviewState().Build();
            Check(kind + " の展望が組めた", v != null, "kind=" + kind);
            if (v == null) return;
            Note("展望[" + kind + "]: " + v.Title + " ／ " + v.Meta
                + " ／ 出場一覧=" + v.Entrants.Count + "件");
        }

    }
}
#endif
