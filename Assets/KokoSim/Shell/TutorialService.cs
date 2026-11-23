using System.Collections.Generic;
using KokoSim.Engine.Career;
using KokoSim.Engine.Saves;
using KokoSim.Engine.Season;
using KokoSim.Engine.Season.Events;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 赴任イベントとチュートリアルの Unity 側の入口（issue #1496・design-04 §3.4）。何を出すかの判定は engine の
    /// <see cref="Tutorial"/> が1箇所で持ち、ここは進み具合（<see cref="TutorialProgress"/>）の常駐・セーブへの出し入れと、
    /// 返った会話をホームのノベル（<see cref="EventService"/> のキュー）へ積むことだけをする。
    ///
    /// <para>チュートリアルの会話は最後に「分かった／もう説明はいらない」の2択（<see cref="PresentedEvent.Menu"/>）を付け、
    /// 赴任イベントとチュートリアルは会話を飛ばせる（<see cref="PresentedEvent.Skippable"/>）。乱数は会話の言い回しの選択だけで、
    /// <see cref="EventService.Rng"/> の専用の用途（<see cref="TutorialPurpose"/>）から引く＝本編の乱数列は動かない。</para>
    /// </summary>
    public static class TutorialService
    {
        private const ulong TutorialPurpose = 0x0005_0000_0000_0000UL;

        private static TutorialProgress _progress = TutorialProgress.Fresh();

        /// <summary>いまの進み具合（読むだけ。撮影レシピの read 用）。</summary>
        public static TutorialProgress Progress => _progress;

        /// <summary>新規ゲーム: 何も見ていない状態へ。</summary>
        public static void Reset()
        {
            _progress = TutorialProgress.Fresh();
            LineupAfterNovel = false;
        }

        /// <summary>セーブ保存（v42）。保存後に進行が変わってもセーブ側が動かないよう写しを渡す。</summary>
        public static void Capture(SaveState s)
            => s.Tutorial = TutorialProgress.Restore(_progress.Seen, _progress.Dismissed);

        /// <summary>セーブ復元。旧セーブ（v41 以前）は SaveCodec が「全部見た扱い」で埋めてある。</summary>
        public static void Restore(SaveState s)
        {
            _progress = s.Tutorial ?? TutorialProgress.AllSeen();
            LineupAfterNovel = false;
        }

        // ===== 場面のフック =====

        /// <summary>「はじめから」で新設校を作った直後（ホームに着く前）。赴任イベント（新設校用）→最初の説明を積む。</summary>
        public static void OnNewSchoolStarted()
        {
            foreach (var id in Tutorial.OnNewSchoolStarted(_progress)) Push(id);
        }

        /// <summary>転任を確定した直後。赴任イベント（転任用）だけを積む（チュートリアルは出さない）。</summary>
        public static void OnTransferred()
        {
            foreach (var id in Tutorial.OnTransferred(_progress)) Push(id);
        }

        /// <summary>場面に入った。未見なら説明を積んで true（「もう説明はいらない」・既に見た、なら何もせず false）。</summary>
        public static bool Raise(TutorialScene scene)
        {
            var id = Tutorial.OnScene(_progress, scene);
            if (id == null) return false;
            Push(id);
            return true;
        }

        /// <summary>
        /// 自校の試合を始める直前。公式戦・練習試合を問わず最初の1試合でだけ采配の説明を積んで true。
        /// </summary>
        public static bool RaiseBeforeMatch(Tutorial.MatchKind kind)
        {
            var id = Tutorial.BeforeMatch(_progress, kind);
            if (id == null) return false;
            Push(id);
            return true;
        }

        /// <summary>
        /// 練習試合の申し込みが成立し、采配の説明をホームで読んでからスタメン設定へ進む待ち（セーブ対象外＝
        /// 練習試合の導線の途中はそもそもセーブできない）。ホームが会話を読み終えたら <see cref="TakeLineupAfterNovel"/> で下ろす。
        /// </summary>
        public static bool LineupAfterNovel { get; private set; }

        /// <summary>練習試合の申し込みが成立した直後に呼ぶ。説明を積んだら true（呼び出し側はホームへ移り、読み終えたらスタメン設定へ）。</summary>
        public static bool RaiseBeforePracticeMatch()
        {
            if (!RaiseBeforeMatch(Tutorial.MatchKind.Practice)) return false;
            LineupAfterNovel = true;
            return true;
        }

        /// <summary>待ちを下ろして、スタメン設定へ移ってよいか返す（ホームが会話を読み終えたとき呼ぶ）。</summary>
        public static bool TakeLineupAfterNovel()
        {
            var was = LineupAfterNovel;
            LineupAfterNovel = false;
            return was;
        }

        /// <summary>週送りの後に呼ぶ。部費で何か1つでも買えるようになっていれば、部費の説明を積む（初回だけ）。</summary>
        public static bool RaisePurchasesIfAffordable()
        {
            if (!_progress.ShouldShow(TutorialScene.Purchases)) return false;
            if (!Tutorial.CanBuySomething(ManagerService.Manager.Funds, ManagerService.Catalog, ManagerService.Facilities))
                return false;
            return Raise(TutorialScene.Purchases);
        }

        // ===== 積む =====

        private static void Push(string eventId)
        {
            var e = Build(eventId);
            if (e == null) return;
            e.Skippable = true;
            if (Tutorial.IsTutorialEvent(eventId))
            {
                e.Menu = TutorialMenu();
                e.OnMenuPick = (ev, value) =>
                {
                    ev.Menu = null;
                    if (value != Tutorial.ChoiceDismiss) return;
                    _progress.Dismiss();
                    var reply = Build(Tutorial.DismissedEventId);
                    if (reply == null) return;
                    var lines = new List<PresentedLine>(ev.Lines);
                    lines.AddRange(reply.Lines);
                    ev.Lines = lines;
                };
            }
            EventService.PushPresented(e);
        }

        /// <summary>会話を1本組む（表に無ければ null＝進行は止めない。黙って消えると気づけないので警告は出す）。</summary>
        private static PresentedEvent Build(string eventId)
        {
            var def = EventService.Scheduler.Find(eventId);
            if (def == null)
            {
                UnityEngine.Debug.LogWarning("[チュートリアル] " + eventId + " が events.yaml に無い（提示を飛ばす）");
                return null;
            }
            var values = EventService.BuildValues();
            var script = EventScheduler.PickScript(def, EventService.Scheduler.AbsoluteWeek, EventService.Rng(TutorialPurpose));
            return new PresentedEvent
            {
                Id = def.Id,
                FeedText = EventTextFormat.Apply(def.Text, values),
                Lines = EventService.ResolveLines(script, values),
            };
        }

        private static List<PresentedMenuItem> TutorialMenu()
            => new List<PresentedMenuItem>
            {
                new PresentedMenuItem { Value = Tutorial.ChoiceContinue, Label = Tutorial.ContinueLabel },
                new PresentedMenuItem { Value = Tutorial.ChoiceDismiss, Label = Tutorial.DismissLabel },
            };
    }
}
