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
    /// 動いた項目1つ（「全治 2 週」「采配 ＋2」）。通知フィードの文と結果行を<b>同じ材料</b>から組む
    /// ための小片で、画面に直接は出ない（旧・効果帯の項目。帯は 2026-08-19 に廃止）。
    /// </summary>
    public sealed class EffectItem
    {
        public string Label;
        public string Value;
        public string Unit;

        public EffectItem(string label, string value, string unit = "")
        {
            Label = label; Value = value; Unit = unit;
        }

        /// <summary>「采配 ＋2」「全治 2 週」の形。</summary>
        public override string ToString()
            => string.IsNullOrEmpty(Label) ? Value + Unit : Label + " " + Value + Unit;
    }

    /// <summary>結果行の色分け（吉報＝空の青／凶報＝警告色。通知フィードの Up/Warn と同じ意味づけ）。</summary>
    public enum EffectTone { Good, Bad }

    /// <summary>
    /// イベントの結果＝会話の<b>最終行に出す1文</b>（issue event-result-line-in-dialogue・監督指示 2026-08-19）。
    /// 「{player} のミートが ＋5 上がった！」のように、誰の・何が・いくつ動いたかを会話の中で言い切る。
    ///
    /// <para>旧・案A2（会話で語り、数字は右下の帯へ）は撤回。帯は小さすぎて読まれなかった。
    /// 文は効果を知っている呼び出し元（<see cref="GrowthEventText"/> 等の単一ソース）が、
    /// 通知フィードの文と<b>同じ場所・同じ材料</b>で組む＝2箇所の数字が食い違わない。</para>
    /// </summary>
    public sealed class PresentedEffect
    {
        /// <summary>結果の1文（空なら結果行を出さない）。</summary>
        public string Sentence;
        /// <summary>色分け（既定＝吉報）。</summary>
        public EffectTone Tone = EffectTone.Good;

        public static PresentedEffect Good(string sentence)
            => new PresentedEffect { Sentence = sentence, Tone = EffectTone.Good };

        public static PresentedEffect Bad(string sentence)
            => new PresentedEffect { Sentence = sentence, Tone = EffectTone.Bad };

        /// <summary>出すものがあるか。</summary>
        public bool HasSentence => !string.IsNullOrEmpty(Sentence);
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
        /// <summary>会話（結果行 <see cref="EventRoles.Result"/> を含む＝提示層は足し直さない）。</summary>
        public IReadOnlyList<PresentedLine> Lines;
        /// <summary>結果（null＝結果行なし）。色分けの参照用で、文そのものは <see cref="Lines"/> の末尾に入っている。</summary>
        public PresentedEffect Effect;
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
        public static EventScheduler Scheduler
        {
            get
            {
                if (_scheduler == null)
                    _scheduler = new EventScheduler(EventTableProvider.Events, Coefficients);
                // 自校の地方（issue #616）。転校（新規ゲーム・転任）で変わるので毎回据える＝
                // 一度きりの初期化にすると、県を変えて始めた進行に前の県のイベントが出る。
                _scheduler.Region = KokoSim.Engine.Nation.PrefectureRegions.RegionOf(NationService.PrefectureId);
                return _scheduler;
            }
        }

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
                var effect = ApplyEffect(f.Event.Effect, f.Event.Id, SubjectId(), SubjectName());
                var e = new PresentedEvent
                {
                    Id = f.Event.Id,
                    FeedText = EventTextFormat.Apply(f.Event.Text, values),
                    Lines = WithResult(ResolveLines(f.Script, values), effect),
                    Effect = effect,
                };
                _pending.Add(e);
                _queuedThisWeek++;
                feed.Add(e.FeedText);
            }
            return feed;
        }

        /// <summary>
        /// 1回の週送りでノベルにできる件数の上限（issue #610）。故障・成長・気づきが重なった週に
        /// クリックを何度も要求しないための歯止め。<b>あふれた分もフィードには残る</b>ので情報は落ちない。
        /// </summary>
        private const int MaxNovelPerWeek = 3;

        private static int _queuedThisWeek;

        /// <summary>週送りの先頭で呼ぶ（この週にノベルへ載せた件数を数え直す）。</summary>
        public static void BeginWeek() => _queuedThisWeek = 0;

        /// <summary>
        /// 実際に起きた出来事を<b>主役つき</b>でノベルのキューへ積む（issue #610〜）。
        /// 週次抽選（<see cref="RunWeek"/>）とは別経路で、クールダウンも年次フラグも通さない
        /// ＝起きたことは必ず語られる。
        ///
        /// <para><b>通知フィードはここでは触らない</b>。呼び出し元（怪我・成長など）が診断名つきの
        /// 詳しい行をすでに流しているので、二重に出さない。</para>
        ///
        /// <para><paramref name="salt"/> には主役の選手IDを渡す＝同じ週に2人同時に故障しても
        /// 別の会話になる（同じ人・同じ週なら常に同じ会話）。</para>
        /// </summary>
        public static void Push(string eventId, string subjectName, PresentedEffect effect, int salt = 0)
        {
            var e = Scheduler.Find(eventId);
            if (e == null)
            {
                // 表から消えても進行は止めない（会話が出ないだけ）。黙って消えると気づけないので警告は出す。
                UnityEngine.Debug.LogWarning("[イベント] " + eventId + " が events.yaml に無い（提示を飛ばす）");
                return;
            }
            if (_queuedThisWeek >= MaxNovelPerWeek) return;
            _queuedThisWeek++;

            var values = BuildValues();
            if (!string.IsNullOrEmpty(subjectName)) values["player"] = subjectName;
            var script = EventScheduler.PickScript(e, Scheduler.AbsoluteWeek, salt, Rng());

            _pending.Add(new PresentedEvent
            {
                Id = e.Id,
                FeedText = EventTextFormat.Apply(e.Text, values),
                Lines = WithResult(ResolveLines(script, values), effect),
                Effect = effect,
            });
        }

        /// <summary>
        /// イベントの効果を適用し、結果行の1文にして返す（issue #613）。何も起こさないイベントは null。
        ///
        /// <para><b>適用はここ1箇所</b>。財布（部費・施設基金）を持っているのは Shell なので、
        /// engine はデータ定義まで、実際に足し引きするのはここ、と分ける。部費が足りなくても
        /// <b>マイナスにはしない</b>（払えないのに買えた、という状態を作らない）。文は<b>実際に動いた量</b>で
        /// 書く（下限で止まった分は出さない＝「−18万」と言って −5万しか引けていない嘘を作らない）。</para>
        /// </summary>
        private static PresentedEffect ApplyEffect(EventEffect effect, string sourceId, int subjectId, string subjectName)
        {
            if (effect == null || effect.IsEmpty) return null;

            var m = ManagerService.Manager;
            var parts = new List<string>();
            var bad = false;

            if (effect.Funds != 0.0)
            {
                var before = m.Funds;
                m.Funds = System.Math.Max(0.0, m.Funds + effect.Funds);
                var d = m.Funds - before;
                parts.Add("部費 " + Signed(d) + " 万");
                if (d < 0) bad = true;
            }
            if (effect.FacilityFund != 0.0)
            {
                var before = m.FacilityFund;
                m.FacilityFund = System.Math.Max(0.0, m.FacilityFund + effect.FacilityFund);
                var d = m.FacilityFund - before;
                parts.Add("施設基金 " + Signed(d) + " 万");
                if (d < 0) bad = true;
            }
            if (effect.HasTrainingBoost)
            {
                // 対象が「主役の1人」なら選手Id、そうでなければ 0＝部全体（issue #615）。
                var target = effect.TrainingTargetIsPlayer ? subjectId : 0;
                TrainingBoostService.Add(sourceId, effect.TrainingMult, effect.TrainingWeeks, target);
                var whose = effect.TrainingTargetIsPlayer && !string.IsNullOrEmpty(subjectName)
                    ? subjectName + " の練習効率"
                    : "部全体の練習効率";
                parts.Add(whose + " " + Signed((effect.TrainingMult - 1.0) * 100.0) + "%（" + effect.TrainingWeeks + " 週）");
                if (effect.TrainingMult < 1.0) bad = true;
            }
            if (parts.Count == 0) return null;

            var sentence = string.Join("、", parts.ToArray()) + (bad ? "" : "！");
            return bad ? PresentedEffect.Bad(sentence) : PresentedEffect.Good(sentence);
        }

        /// <summary>会話の末尾に結果行を1本足す（結果が無ければそのまま）。</summary>
        private static IReadOnlyList<PresentedLine> WithResult(IReadOnlyList<PresentedLine> lines, PresentedEffect effect)
        {
            if (effect == null || !effect.HasSentence) return lines;
            var list = new List<PresentedLine>(lines.Count + 1);
            list.AddRange(lines);
            list.Add(new PresentedLine { Who = EventRoles.Result, Name = "", Say = effect.Sentence });
            return list;
        }

        // 全角の符号（半角にすると Oswald と混植になって高さが揃わない）。
        private static string Signed(double value)
            => (value >= 0 ? "＋" : "−") + System.Math.Round(System.Math.Abs(value)).ToString("0");

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
            _queuedThisWeek = 0;
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
            var p = Subject();
            return p == null ? "" : p.Name;
        }

        /// <summary>会話の主役の選手Id（0＝居ない）。個人向けの効果（issue #615）が誰に付くかを決める。</summary>
        private static int SubjectId()
        {
            var p = Subject();
            return p == null ? 0 : p.Id;
        }

        private static KokoSim.Engine.Season.DevelopingPlayer Subject()
        {
            var roster = RosterService.Active;
            if (roster == null || roster.Count == 0) return null;
            var index = (GameClock.YearIndex * 100 + GameClock.Week) % roster.Count;
            return roster[index];
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
