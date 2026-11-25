using System.Collections.Generic;
using KokoSim.Engine.Season;
using KokoSim.Engine.Season.Events;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// スカウトの一年の入口（issue #1064）。夏の大会後の会話の中で、①スカウト網の契約（契約しない／Lv1〜3）→
    /// ②探す分野、の順に選ぶ。画面は移らず、会話の選択肢（<see cref="PresentedEvent.Menu"/>）で決めて終わる。
    /// 契約も分野も選んだ瞬間に確定する（年の途中で変えられるのは、ここで決めたあとの分野＝リスト生成前だけ）。
    /// 選択肢の中身は engine の <see cref="ScoutPitch"/>、出す週の判定は <see cref="ScoutMilestones"/>。
    /// 年額はこの契約のまま同じ年度の総括（第46週）で引かれる。
    /// 一度も契約したことのない学校には夏の入口として強制では出さず、プレイヤーがスカウト画面から自分で切り出したときだけ
    /// 初対面の調子で出す（issue #1594）。
    /// </summary>
    internal static class ScoutPitchService
    {
        private enum Stage { Contract, Field }

        /// <summary>入口の会話を積む。会話の表が欠けていれば何もせず false（待ちだけ立てて居座らない）。</summary>
        public static bool Raise()
        {
            var contracts = ScoutPitch.ContractChoices(ManagerService.ContractList);
            if (contracts.Count == 0) return false;

            var everContracted = ManagerService.Contracts.HasEverContracted(ScoutPitch.ContractId);
            var openerId = ScoutPitch.OpenerEventId(ScoutService.Level, everContracted);
            var opener = EventService.Scheduler.Find(openerId);
            if (opener == null) return false;

            var values = EventService.BuildValues();
            var script = EventScheduler.PickScript(opener, EventService.Scheduler.AbsoluteWeek, EventService.Rng(EventService.ScoutOfferPurpose));
            var stage = Stage.Contract;
            ScoutContractChoice picked = null;

            var e = new PresentedEvent
            {
                Id = opener.Id,
                FeedText = EventTextFormat.Apply(opener.Text, values),
                Lines = EventService.ResolveLines(script, values),
                Menu = ContractMenu(contracts),
            };
            e.OnMenuPick = (ev, value) =>
            {
                if (stage == Stage.Contract)
                {
                    picked = contracts[System.Math.Clamp(value, 0, contracts.Count - 1)];
                    ManagerService.Contracts.SetLevel(ScoutPitch.ContractId, picked.Level);
                    if (picked.Level <= 0)
                    {
                        ScoutService.SelectedField = ScoutField.Omakase;
                        Append(ev, ScoutPitch.DeclinedEventIdFor(everContracted), values);
                        ev.Menu = null;
                        return;
                    }
                    Append(ev, ScoutPitch.FieldAskEventId, values);
                    ev.Menu = FieldMenu();
                    stage = Stage.Field;
                    return;
                }

                var fields = ScoutPitch.FieldChoices;
                var field = fields[System.Math.Clamp(value, 0, fields.Count - 1)].Field;
                ScoutService.SelectedField = field;
                Append(ev, ScoutPitch.FieldDoneEventId, values);
                var effect = PresentedEffect.Good(ScoutPitch.ResultSentence(picked, field));
                ev.Lines = EventService.WithResult(ev.Lines, effect);
                ev.Effect = effect;
                ev.Menu = null;
            };
            EventService.PushPresented(e);
            return true;
        }

        private static List<PresentedMenuItem> ContractMenu(IReadOnlyList<ScoutContractChoice> contracts)
        {
            var list = new List<PresentedMenuItem>(contracts.Count);
            for (var i = 0; i < contracts.Count; i++)
                list.Add(new PresentedMenuItem { Value = i, Label = contracts[i].Label });
            return list;
        }

        private static List<PresentedMenuItem> FieldMenu()
        {
            var list = new List<PresentedMenuItem>(ScoutPitch.FieldChoices.Count);
            for (var i = 0; i < ScoutPitch.FieldChoices.Count; i++)
                list.Add(new PresentedMenuItem { Value = i, Label = ScoutPitch.FieldChoices[i].Label });
            return list;
        }

        /// <summary>続きの会話を会話の後ろへ足す（表に無ければ足さない＝進行は止めない）。</summary>
        private static void Append(PresentedEvent e, string eventId, Dictionary<string, string> values)
        {
            var next = EventService.Scheduler.Find(eventId);
            if (next == null) return;
            var script = EventScheduler.PickScript(next, EventService.Scheduler.AbsoluteWeek, EventService.Rng(EventService.ScoutFollowPurpose));
            var lines = new List<PresentedLine>(e.Lines);
            lines.AddRange(EventService.ResolveLines(script, values));
            e.Lines = lines;
        }
    }
}
