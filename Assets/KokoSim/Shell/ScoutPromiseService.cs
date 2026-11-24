using System.Collections.Generic;
using KokoSim.Engine.Season;
using KokoSim.Engine.Season.Events;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 入部の条件（約束）の会話（設計書04 §2.4）。訪問のとき候補が条件を出してきたら、スカウトとの会話の中で
    /// 「約束する／約束はできない」を選ぶ。選んだ瞬間に確定し、手応えもその場で取り直す。
    /// 条件の種類と誰に出すかは engine の <see cref="ScoutPromises"/>、ここは会話を組んで返事を受けるだけ。
    /// 読み切らずに離れたら「約束しなかった」のまま（待ちはセーブしない＝スカウトの節目と同じ作法）。
    /// </summary>
    internal static class ScoutPromiseService
    {
        private const int Accept = 1;
        private const int Decline = 0;

        /// <summary>条件の会話を積む。会話の表が欠けていれば何もせず false（待ちだけ立てて居座らない）。</summary>
        /// <param name="onAnswered">返事が決まった直後に呼ぶ（手応えの取り直し）。</param>
        public static bool Raise(ScoutCandidate candidate, System.Action onAnswered)
        {
            var opener = EventService.Scheduler.Find(ScoutPromises.AskEventId(candidate.Promise));
            if (opener == null) return false;

            var values = EventService.BuildValues();
            values["player"] = candidate.Player.Name;
            var script = EventScheduler.PickScript(
                opener, EventService.Scheduler.AbsoluteWeek, EventService.Rng(EventService.ScoutOfferPurpose));

            var e = new PresentedEvent
            {
                Id = opener.Id,
                FeedText = EventTextFormat.Apply(opener.Text, values),
                Lines = EventService.ResolveLines(script, values),
                Menu = new List<PresentedMenuItem>
                {
                    new PresentedMenuItem { Value = Accept, Label = "約束する（" + ScoutPromises.Label(candidate.Promise) + "）" },
                    new PresentedMenuItem { Value = Decline, Label = "約束はできない" },
                },
            };
            e.OnMenuPick = (ev, value) =>
            {
                var accept = value == Accept;
                ScoutPromises.Answer(candidate, accept);
                onAnswered?.Invoke();
                Append(ev, accept ? ScoutPromises.AcceptedEventId : ScoutPromises.DeclinedEventId, values);
                if (accept)
                {
                    var effect = PresentedEffect.Good(
                        candidate.Player.Name + " と約束した: " + ScoutPromises.Label(candidate.Promise));
                    ev.Lines = EventService.WithResult(ev.Lines, effect);
                    ev.Effect = effect;
                }
                ev.Menu = null;
            };
            EventService.PushPresented(e);
            return true;
        }

        /// <summary>続きの会話を会話の後ろへ足す（表に無ければ足さない＝進行は止めない）。</summary>
        private static void Append(PresentedEvent e, string eventId, Dictionary<string, string> values)
        {
            var next = EventService.Scheduler.Find(eventId);
            if (next == null) return;
            var script = EventScheduler.PickScript(
                next, EventService.Scheduler.AbsoluteWeek, EventService.Rng(EventService.ScoutFollowPurpose));
            var lines = new List<PresentedLine>(e.Lines);
            lines.AddRange(EventService.ResolveLines(script, values));
            e.Lines = lines;
        }
    }
}
