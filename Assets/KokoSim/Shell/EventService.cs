using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Saves;
using KokoSim.Engine.Season.Events;

namespace KokoSim.Unity.Shell
{
    /// <summary>会話1行の提示用（差し込み語を展開し終えた形）。</summary>
    public sealed class PresentedLine
    {
        /// <summary>役割ID（立ち絵スロット）。</summary>
        public string Who;
        /// <summary>話者名（空＝地の文）。</summary>
        public string Name;
        /// <summary>本文。</summary>
        public string Say;
    }

    /// <summary>
    /// 発火して「まだ見せていない」イベント1件（issue #609）。会話は<b>発火時に確定</b>しており、
    /// 提示層は選び直さない＝同じ週のイベントを何度開いても同じ文になる。
    /// </summary>
    public sealed class PresentedEvent
    {
        public string Id;
        /// <summary>通知フィードに残す1行（差し込み語を展開済み）。</summary>
        public string FeedText;
        public IReadOnlyList<PresentedLine> Lines;
    }

    /// <summary>
    /// 週次イベントの Unity 側の入口（設計書03 §5・issue #609）。engine の
    /// <see cref="EventScheduler"/> は Phase 1 からあったが<b>Unity から一度も呼ばれておらず</b>、
    /// data/events.yaml は実ゲームでは死んだデータだった。ここで週送りへ接続する。
    ///
    /// <para><b>発生と提示を分ける。</b>週送りが <see cref="RunWeek"/> で発火だけ済ませてキューへ積み、
    /// 画面側が <see cref="Dequeue"/> で1件ずつ見せる。大会開幕週・新チーム発足週は先にそちらの
    /// モーダルが出るので、イベントはキューに残ったまま次の機会に持ち越される（＝取りこぼさない）。</para>
    ///
    /// <para><b>帯不変</b>: 乱数は母種から導く独立ストリームで、本編（打席・試合）の乱数列は1ビットも進まない。
    /// 会話バリエーションの抽選はさらにその中の <see cref="IRandomSource.Fork"/> なので、
    /// 文言を足しても発火の抽選結果は変わらない。</para>
    /// </summary>
    public static class EventService
    {
        /// <summary>週次イベント抽選の独立ストリーム識別子（<see cref="GameSeed.Master"/> と混合）。</summary>
        private const ulong EventStream = 0x6090_0000UL;

        private static EventScheduler _scheduler;
        private static readonly List<PresentedEvent> _pending = new List<PresentedEvent>();

        /// <summary>係数（C#既定＝data/coefficients.yaml events: と同値）。</summary>
        public static EventCoefficients Coefficients { get; private set; } = new EventCoefficients();

        /// <summary>常駐スケジューラ（イベント表は StreamingAssets から一度だけ読む）。</summary>
        public static EventScheduler Scheduler =>
            _scheduler ?? (_scheduler = new EventScheduler(EventTableProvider.Events, Coefficients));

        /// <summary>未提示のイベント（週送りが積み、ノベル画面が消化する）。</summary>
        public static IReadOnlyList<PresentedEvent> Pending => _pending;

        /// <summary>見せ残しがあるか。</summary>
        public static bool HasPending => _pending.Count > 0;

        /// <summary>
        /// 今週のイベントを発火させ、提示キューへ積む（提示はしない）。
        /// 戻り値は通知フィード用の要約行＝<b>キューを消化しなくてもフィードには必ず残る</b>
        /// （見逃しても後から何が起きたか追える。設計書06 §3.1 の通知フィードは従来どおり）。
        /// </summary>
        public static IReadOnlyList<string> RunWeek()
        {
            var fired = Scheduler.Week(GameClock.Week, Rng());
            var feed = new List<string>(fired.Count);
            foreach (var f in fired)
            {
                var values = BuildValues();
                var e = new PresentedEvent
                {
                    Id = f.Event.Id,
                    FeedText = EventTextFormat.Apply(f.Event.Text, values),
                    Lines = ResolveLines(f.Script, values),
                };
                _pending.Add(e);
                feed.Add(e.FeedText);
            }
            return feed;
        }

        /// <summary>
        /// 先頭の未提示イベントを<b>取り出さずに</b>見る（無ければ null）。
        /// 提示中に画面を離れても消えないよう、キューから外すのは見終わってから（<see cref="Complete"/>）。
        /// </summary>
        public static PresentedEvent Peek() => _pending.Count == 0 ? null : _pending[0];

        /// <summary>先頭のイベントを見終わった＝キューから外す。</summary>
        public static void Complete()
        {
            if (_pending.Count > 0) _pending.RemoveAt(0);
        }

        /// <summary>テスト・新規ゲーム用（スケジューラの状態とキューを捨てる）。</summary>
        public static void Clear()
        {
            _scheduler = null;
            _pending.Clear();
        }

        /// <summary>セーブ保存: クールダウンと年次フラグ（issue #609・v9）。</summary>
        public static void Capture(SaveState s) => s.Events = EventSchedulerSaveState.From(Scheduler);

        /// <summary>
        /// セーブ復元: クールダウンと年次フラグを押し戻す。<b>キューは載せない</b>＝
        /// 「見せる直前の1件」はセーブできない地点（結果確認中）にしか存在しないので、復元時は必ず空。
        /// </summary>
        public static void Restore(SaveState s)
        {
            _scheduler = null;                    // イベント表を読み直してから状態を据える
            _pending.Clear();
            if (s.Events != null) s.Events.RestoreInto(Scheduler);
        }

        /// <summary>年度替わりで年次フラグを落とす（「今年はもう出た」の解除）。</summary>
        public static void OnYearChanged() => Scheduler.ResetAnnualFlags();

        /// <summary>
        /// 週次抽選の乱数（不変条件#2）。<see cref="KokoSim.Unity.Home.HomeState"/> の週次抽選と同じ作法で、
        /// 経過状態でなく<b>暦</b>から作る＝その週ならいつ引いても同じ結果。
        /// <b>大会日を混ぜる</b>のは、大会中は <see cref="GameClock.Week"/> が進まず
        /// <see cref="GameSession.TournamentDay"/> だけが進むため（混ぜないと大会をまたぐ週が同じ抽選を引く）。
        /// </summary>
        private static IRandomSource Rng()
        {
            var day = GameSession.Current.TournamentDay;
            var key = EventStream ^ ((ulong)day << 40) ^ (ulong)(GameClock.YearIndex * 100 + GameClock.Week);
            return new Xoshiro256Random(GameSeed.Master ^ key);
        }

        /// <summary>
        /// 差し込み語の値。<b>未知の語はここに入れない</b>＝<c>{key}</c> のまま画面に残り、
        /// data/events.yaml の書き間違いが絵で分かる（<see cref="EventTextFormat"/>）。
        /// </summary>
        private static Dictionary<string, string> BuildValues()
        {
            return new Dictionary<string, string>
            {
                { "school", NationService.ManagerSchoolName },
                { "manager", ManagerService.Manager.Name },
                { "player", SubjectName() },
                { "count", FirstYearCount().ToString() },
            };
        }

        /// <summary>
        /// 会話の主役になる部員名。<b>いまは在籍部員から暦で決まる1人</b>で、
        /// イベントの効果（誰が故障したか等）は #610 以降で実体と結び付ける＝
        /// そのとき主役はイベント側から渡されるようになる。部員が居なければ空。
        /// </summary>
        private static string SubjectName()
        {
            var roster = RosterService.Active;
            if (roster == null || roster.Count == 0) return "";
            var index = (GameClock.YearIndex * 100 + GameClock.Week) % roster.Count;
            return roster[index].Name;
        }

        /// <summary>{count}＝1年生の人数（入学式の「新入部員 N名」）。</summary>
        private static int FirstYearCount()
        {
            var n = 0;
            var roster = RosterService.Active;
            if (roster != null)
                foreach (var p in roster) if (p.Grade == 1) n++;
            return n;
        }

        private static IReadOnlyList<PresentedLine> ResolveLines(
            EventScript script, Dictionary<string, string> values)
        {
            var lines = new List<PresentedLine>(script.Lines.Count);
            foreach (var l in script.Lines)
            {
                var name = string.IsNullOrEmpty(l.Name) ? EventRoles.DefaultName(l.Who) : l.Name;
                lines.Add(new PresentedLine
                {
                    Who = l.Who,
                    Name = EventTextFormat.Apply(name, values),
                    Say = EventTextFormat.Apply(l.Say, values),
                });
            }
            return lines;
        }
    }
}
