// ViewModel層（設計書06 §1: エンジン→ViewModel→UXML）。UnityEngine 非依存に保つ。
using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Career.Draft;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using KokoSim.Unity.Shell;

namespace KokoSim.Unity.Home
{
    public enum FeedKind { Normal, Up, Warn, Discover }

    public sealed class FeedItem
    {
        public string When = "今週";
        public string Text = "";
        public FeedKind Kind = FeedKind.Normal;
        public string Tag = "情報";   // 成長 / 警告 / 発見 / 情報
    }

    public sealed class GradeChip
    {
        public string Grade = "D";   // S〜G
        public string Label = "";
    }

    /// <summary>故障者1名ぶんの表示行（設計書03 §3.5: 症状は常に可視）。</summary>
    public sealed class InjuredRow
    {
        public string Number = "—";
        public string Name = "";
        public string Site = "";       // 肩 / 肘 / 腰 / 膝 / 足首 / 手
        public string Type = "";       // 捻挫 / 肉離れ / 骨折 / 打撲 / 靭帯損傷 / 疲労性炎症（種類なしは空）
        public string Severity = "";   // 軽度 / 中度 / 重度
        public string Back = "";       // 復帰まで（例「あと 4 週」）
        public bool Severe;            // 重度＝警告色（UI原則②: 本当の警告だけ）
    }

    public sealed class GuidanceSlot
    {
        public string Name = "";
        public string Focus = "";
        public bool Empty;
    }

    /// <summary>ホーム画面に表示する一式（スナップショット）。</summary>
    public sealed class HomeView
    {
        public string TeamRankGrade = "C";
        public string CountdownLabel = "夏予選まで";
        public string CountdownValue = "";
        /// <summary>掲示板の升目に載せる短縮カウントダウン（"15週" / "3日" / "本日試合" / "開催中" / "―"）。</summary>
        public string CountdownCells = "";
        /// <summary>年の小書きに続けて出す補足（"夏の大会 7月13日"）。升目には載せない。</summary>
        public string WeekSuffix = "";

        public string HomeTeam = "桜丘";
        public string AwayTeam = "北都大付属";
        public string GameTag = "練習試合";
        public string HomeSub = "自校";
        public string AwaySub = "県立・強豪";
        public string GameDate = "";
        public string Venue = "自校グラウンド";
        public string Weather = "曇";

        public bool TournamentMode;   // 大会モード中は次戦カード/カウントダウンを大会仕様に切替える

        // 主役ヒーロー帯・通常週のカウントダウン（数字は Oswald にするため単位と分割して持つ）。
        public string HeroBigValue = "";   // "15"（週数）／数字が無い状態は "開催中"
        public string HeroBigUnit = "";    // "週"／数字が無い状態は空

        public int GuidanceUsed;
        public int GuidanceTotal = 3;

        public List<InjuredRow> Injured = new List<InjuredRow>();
        public List<FeedItem> Feed = new List<FeedItem>();
        public List<GuidanceSlot> Guidance = new List<GuidanceSlot>();
    }

    /// <summary>
    /// ホーム画面の状態機械。純エンジンを駆動して「今週を進める」を実体化する。
    /// 週送りで練習→育成・怪我・イベントを回し、結果をフィードに流す（設計書06 §2: 通知フィード駆動）。
    ///
    /// 部員は全画面共有の <see cref="RosterService"/> を単一ソースにする（2026-07-21）。
    /// 以前はここだけ seed=42 で私物のロスターを再生成しており、週送りの成長が共有ロスターへ届かず
    /// 画面往復で消えていた（引退は GameClock 経由で共有側に起きるため、育てた選手と引退する選手が別物だった）。
    /// あわせて状態自体もセッション常駐（<see cref="Current"/>）にし、通知フィードが画面往復で消えないようにする。
    /// </summary>
    public sealed class HomeState
    {
        private readonly IRandomSource _rng;
        private readonly SeasonCalendar _calendar = new SeasonCalendar();
        private readonly TrainingCoefficients _training = new TrainingCoefficients();
        private readonly InjuryCoefficients _injury = new InjuryCoefficients();
        private readonly SkillCoefficients _skills = new SkillCoefficients();
        private readonly InsightCoefficients _insight = new InsightCoefficients();
        private readonly GrowthEventCoefficients _growth = new GrowthEventCoefficients();
        private readonly List<FeedItem> _feed = new List<FeedItem>();

        // 個別指導枠の並べ替えに使うカテゴリ別指標の係数。他画面の比較カードと同一の単一ソース。
        private static readonly TeamStrengthCoefficients GuidanceCoeff = TeamStrengthCoeff.Default;

        // 現在週/年度は全画面共有の GameClock、大会モードの進行は GameSession を単一ソースとする（画面ごとに持たない）。
        // 大会モードの構築（進行体・裏試合フルシム）は TournamentEntry（Shell）へ移した（issue #134）。

        private static HomeState _current;

        /// <summary>セッション常駐の実体（通知フィードが画面往復で消えないようにする）。</summary>
        public static HomeState Current => _current ?? (_current = new HomeState());

        /// <summary>「タイトルへ戻る」用の完全リセット（issue #509）。通知フィードはセーブ対象外の一時状態
        /// だが、次に始める新規ゲームへ前セッションの出来事を混ぜないためここで捨てる。次回 <see cref="Current"/>
        /// アクセスで新品を作り直す（コンストラクタの「新チームが始動した」フィード込み）。</summary>
        public static void Reset() => _current = null;

        /// <summary>週送りの対象＝全画面共有の在籍部員（引退者は含まない）。</summary>
        private static IReadOnlyList<DevelopingPlayer> Roster => RosterService.Active;

        // 週次抽選のストリームID（互いに独立＝どちらかの抽選回数が変わっても他方がずれない）。
        private const ulong InjuryStream = 0x1213_0000UL;
        private const ulong InsightStream = 0x1E51_0000UL;
        private const ulong GrowthStream = 0x6110_0000UL;   // 選手成長イベント（issue #611）

        /// <summary>
        /// 週次抽選の乱数ストリーム種（不変条件#2）。「その週ならいつ引いても同じ結果」になるよう、
        /// 経過状態でなく<b>暦</b>から作る。
        ///
        /// <para><b>大会日を混ぜるのが要点。</b>大会中は <see cref="KokoSim.Unity.Shell.GameClock.Week"/> が
        /// 進まず、進むのは <see cref="GameSession.TournamentDay"/> だけなので、年度・週だけを種にすると
        /// 大会をまたぐ練習週（夏の県予選で4〜6週ぶん）が毎回まったく同じ抽選を引く。
        /// 大会外は TournamentDay=0 で <c>day &lt;&lt; 40</c> が消えるため、
        /// 従来の <c>year*100 + week</c> と<b>1ビット一致</b>する＝挙動が変わるのは大会中だけ。</para>
        ///
        /// <para>週は0..51、年度は現実的な上限（200年プレイでも200程度）なので
        /// <c>year*100 + week</c> は (年度,週) に対して単射で、40ビット上の大会日とも重ならない。</para>
        /// </summary>
        private static ulong WeeklyStream(ulong streamId)
        {
            var week = KokoSim.Unity.Shell.GameClock.Week;
            var year = KokoSim.Unity.Shell.GameClock.YearIndex;
            var day = GameSession.Current.TournamentDay;
            return streamId ^ ((ulong)day << 40) ^ (ulong)(year * 100 + week);
        }

        public HomeState(ulong seed = 42)
        {
            _rng = new Xoshiro256Random(seed);
            _feed.Add(new FeedItem { When = "先週", Text = "新チームが始動した。", Kind = FeedKind.Normal, Tag = "情報" });
        }

        /// <summary>今週を進める: 全部員を1週練習させ→怪我の週次処理→共有週を進める。
        /// 大会開幕週の大会モード遷移は共通クロック <see cref="GameClock"/> の週入りフックへ集約した
        /// （issue #134: どのタブから進めても同じ地点で判定する）。開幕バナーは <see cref="BannerPending"/> を
        /// 見てホームが出す。</summary>
        public void AdvanceWeek()
        {
            KokoSim.Unity.Shell.EventService.BeginWeek();   // この週のノベル件数を数え直す（issue #610）
            RunPracticeWeek(1.0);                       // 通常週の練習（効果1.0）
            RunInjuryWeek();                            // 怪我の発生判定と回復（設計書03 §3.5）
            RunGrowthWeek();                            // 選手成長イベント（設計書03 §5.5）
            RunInsightWeek();                           // 気づき（設計書03 §5.6）
            KokoSim.Unity.Shell.GameClock.Advance();    // 共有週を進める（大会入り判定は EnterWeek に集約）
            RunProspectFeed();                          // 入部予定者の確定/入部（設計書04 §2.1）
            RunDraftFeed();                             // ドラフト候補入り・評価変化・指名確定（設計書20 §5）
            RunEventWeek();                             // 週次イベントの発火（設計書03 §5）
        }

        /// <summary>
        /// 選手成長イベント（設計書03 §5.5・issue #611）。engine の <see cref="GrowthEventModel"/> は
        /// Phase 1 からあったが<b>ヘッドレスシム（SeasonEngine）からしか呼ばれておらず</b>、
        /// 実プレイでは覚醒もスランプも一度も起きていなかった。ここで週送りへ接続する。
        ///
        /// <para>乱数は専用ストリーム（<see cref="GrowthStream"/>）。怪我・気づきと同じ作法で
        /// <see cref="IRandomSource.Fork"/> するので、既に走っている抽選の乱数列はずれない。</para>
        ///
        /// <para>効果の適用は engine が済ませる。ここは提示だけ＝<b>数値は係数と実測から作る</b>
        /// （スランプ週数は適用後の <see cref="DevelopingPlayer.SlumpWeeks"/> をそのまま読む）。</para>
        /// </summary>
        private void RunGrowthWeek()
        {
            var rng = _rng.Fork(WeeklyStream(GrowthStream));
            var notices = GrowthEventModel.Week(Roster, rng, _growth);
            if (notices.Count == 0) return;

            var items = new List<FeedItem>();
            foreach (var n in notices)
            {
                items.Add(new FeedItem
                {
                    When = "今週",
                    Text = GrowthEventText.Feed(n, _growth),
                    Kind = GrowthEventText.IsGood(n.Type) ? FeedKind.Up : FeedKind.Warn,
                    Tag = GrowthEventText.IsGood(n.Type) ? "成長" : "警告",
                });
                KokoSim.Unity.Shell.EventService.Push(
                    GrowthEventText.EventId(n.Type), n.Player.Name,
                    GrowthEventText.Effect(n, _growth), n.Player.Id);
            }
            PushFeed(items);
        }

        /// <summary>
        /// 週次イベント（設計書03 §5・issue #609）。<b>発火だけ</b>行い、ノベル画面での提示は
        /// <see cref="KokoSim.Unity.Shell.EventService"/> のキューに積んで画面側へ委ねる
        /// （大会開幕週・新チーム発足週は先にそちらのモーダルが出るので、取りこぼさないよう分離する）。
        ///
        /// <para><see cref="KokoSim.Unity.Shell.GameClock.Advance"/> の<b>後</b>に呼ぶ＝入った週のイベントを
        /// 出す。入学式（週0）の会話が「新入部員 N名」を語れるのは、合流が Advance の中で済んでいるため。</para>
        ///
        /// <para>提示を見逃してもフィードには必ず残す（後から何が起きたか追える）。</para>
        /// </summary>
        private void RunEventWeek()
        {
            var items = new List<FeedItem>();
            foreach (var text in KokoSim.Unity.Shell.EventService.RunWeek())
                items.Add(new FeedItem { When = "今週", Text = text, Kind = FeedKind.Normal, Tag = "情報" });
            if (items.Count > 0) PushFeed(items);
        }

        /// <summary>
        /// 入部予定者の開示演出（設計書04 §2.1・issue #587）。3月の確定と4月の入部を通知フィードへ流す。
        /// 遷移そのものは <see cref="KokoSim.Unity.Shell.GameClock"/> と
        /// <see cref="KokoSim.Unity.Shell.RosterService"/> が済ませており、ここは提示だけ
        /// （大会開幕バナーと同じ「遷移とUI提示の分離」）。フラグは流したら下ろす。
        /// </summary>
        private void RunProspectFeed()
        {
            var items = new List<FeedItem>();
            if (KokoSim.Unity.Shell.GameClock.ProspectConfirmed)
            {
                KokoSim.Unity.Shell.GameClock.ProspectConfirmed = false;
                var n = KokoSim.Unity.Shell.ProspectService.Current?.Count ?? 0;
                items.Add(new FeedItem
                {
                    When = "今週",
                    Text = "来春の入部予定者 " + n + "名 が決まった。スカウトの見立てが届いている。",
                    Kind = FeedKind.Discover,
                    Tag = "発見",
                });
            }
            if (KokoSim.Unity.Shell.GameClock.RecruitListOpened)
            {
                KokoSim.Unity.Shell.GameClock.RecruitListOpened = false;
                var n = KokoSim.Unity.Shell.RecruitService.Current?.Prospects.Count ?? 0;
                items.Add(new FeedItem
                {
                    When = "今週",
                    Text = "中学生 " + n + "名 のリストが届いた。勧誘枠は "
                           + KokoSim.Unity.Shell.RecruitService.Slots + "名。",
                    Kind = FeedKind.Discover,
                    Tag = "発見",
                });
            }
            if (KokoSim.Unity.Shell.GameClock.RecruitsWon > 0)
            {
                var won = KokoSim.Unity.Shell.GameClock.RecruitsWon;
                KokoSim.Unity.Shell.GameClock.RecruitsWon = 0;
                items.Add(new FeedItem
                {
                    When = "今週",
                    Text = "勧誘した中学生 " + won + "名 が入部を決めた。",
                    Kind = FeedKind.Up,
                    Tag = "成長",
                });
            }
            if (KokoSim.Unity.Shell.GameClock.ProspectJoined)
            {
                KokoSim.Unity.Shell.GameClock.ProspectJoined = false;
                var n = KokoSim.Unity.Shell.ProspectService.Current?.Count ?? 0;
                items.Add(new FeedItem
                {
                    When = "今週",
                    Text = "新入生 " + n + "名 が入部した。能力が見えるようになった。",
                    Kind = FeedKind.Discover,
                    Tag = "発見",
                });
            }
            if (items.Count > 0) PushFeed(items);
        }

        /// <summary>
        /// ドラフトの通知フィード（設計書20 §5・issue #586）。候補入り／評価の増減は週次のバンド差分、
        /// 指名確定は10月最終週の結果から出す。判定そのものは
        /// <see cref="KokoSim.Unity.Shell.DraftService"/> が済ませており、ここは提示だけ。
        /// </summary>
        private void RunDraftFeed()
        {
            var items = new List<FeedItem>();

            foreach (var c in KokoSim.Unity.Shell.DraftService.DrainChanges())
            {
                if (c.Entered)
                {
                    items.Add(new FeedItem
                    {
                        When = "今週",
                        Text = c.Name + " がドラフト候補に挙がった（" + DraftRankBands.Label(c.To) + "）",
                        Kind = FeedKind.Discover,
                        Tag = "発見",
                    });
                    continue;
                }
                // 圏外へ落ちたときは「圏外」ではなく、候補から外れたことを言い切る。
                var to = c.To == DraftRankBand.None ? "候補から外れた" : DraftRankBands.Label(c.To);
                items.Add(new FeedItem
                {
                    When = "今週",
                    Text = c.Name + " の評価が" + (c.Improved ? "上がった" : "下がった")
                           + "（" + DraftRankBands.Label(c.From) + "→" + to + "）",
                    Kind = c.Improved ? FeedKind.Up : FeedKind.Warn,
                    Tag = c.Improved ? "成長" : "警告",
                });
            }

            var result = KokoSim.Unity.Shell.DraftService.LastNomination;
            if (result != null)
            {
                KokoSim.Unity.Shell.DraftService.LastNomination = null;
                foreach (var pick in result.Picks)
                {
                    items.Add(pick.Nominated
                        ? new FeedItem
                        {
                            When = "今週",
                            Text = pick.Name + " がドラフト" + pick.Round + "位指名！",
                            Kind = FeedKind.Discover,
                            Tag = "発見",
                        }
                        : new FeedItem
                        {
                            When = "今週",
                            Text = pick.Name + " は指名されなかった。",
                            Kind = FeedKind.Normal,
                            Tag = "情報",
                        });
                }
            }

            if (items.Count > 0) PushFeed(items);
        }

        /// <summary>開幕演出（バナー）を出す直前に「大会が開幕した」を通知フィードへ流す（設計書06 §2）。
        /// 大会モード遷移自体は <see cref="TournamentEntry"/> が行うため、フィード投入だけをここに残す。</summary>
        public void PushTournamentOpenFeed()
        {
            PushFeed(new List<FeedItem>
            {
                new FeedItem { When = "本日", Text = GameSession.Current.Title + " が開幕した。", Kind = FeedKind.Discover, Tag = "情報" },
            });
        }

        /// <summary>1週ぶんの練習を適用する。extraMult は大会モード時の効果低下（&lt;1.0）に使う。
        /// 選手ごとの練習計画（<see cref="DevelopingPlayer.Plan"/>・null=お任せ）＋監督の分野別指導力・
        /// 性格・体質スキル・施設連動 budget を通す単一ソース <see cref="TrainingWeek.Apply"/> へ委譲する
        /// （issue #484: 固定7周期メニューの一律適用を廃止し、練習計画画面のプレビューと同じ入力・同じAPIへ束ねる）。</summary>
        private void RunPracticeWeek(double extraMult)
        {
            var week = KokoSim.Unity.Shell.GameClock.Week;
            var newItems = new List<FeedItem>();

            foreach (var p in Roster)
            {
                if (!KokoSim.Unity.Shell.TrainingWeek.CanTrain(p.Grade, week)) continue;
                var before = Snapshot(p);
                KokoSim.Unity.Shell.TrainingWeek.Apply(p, week, extraMult);
                foreach (var k in AbilityKinds.All)
                {
                    var gained = p.Level(k) - before[k];
                    if (gained > 0)
                    {
                        newItems.Add(new FeedItem
                        {
                            When = "今週",
                            Text = p.Name + " の " + AbilityJp(k) + " が +" + gained,
                            Kind = FeedKind.Up,
                            Tag = "成長",
                        });
                    }
                }
            }

            PushFeed(newItems);
        }

        /// <summary>
        /// 怪我の週次処理（設計書03 §3.5: 発生判定→回復進行）。SeasonEngine と同じ順序・同じ独立ストリームで回す。
        /// 発生・復帰はフィードへ流す（通知フィード駆動）。怪我中の選手は DevelopmentModel 側で練習が止まる。
        ///
        /// <para>乱数の種には<b>大会日も混ぜる</b>（<see cref="InsightStream"/> と同じ作法）。大会中は
        /// <see cref="KokoSim.Unity.Shell.GameClock.Week"/> が進まない（進むのは
        /// <see cref="GameSession.TournamentDay"/> だけ）ので、年度・週だけを種にすると大会をまたぐ
        /// 練習週で毎回まったく同じ抽選を引いてしまう（夏の県予選で4〜6週ぶん）。
        /// 大会外は TournamentDay=0 なので<b>従来と1ビット一致</b>＝直るのは大会中だけ。</para>
        /// </summary>
        private void RunInjuryWeek()
        {
            var rng = _rng.Fork(WeeklyStream(InjuryStream));
            var newItems = new List<FeedItem>();

            // 医務室（施設）の効果。発生率は WeeklyCheck へ倍率で、全治週は InjuryCoefficients の
            // MedicalRecoveryFactor（設計書04の備品用に空いていた口）へ載せる。医務室Lv0で 1.0＝従来一致。
            var facility = KokoSim.Unity.Shell.TrainingWeek.Effects;
            var injury = _injury with { MedicalRecoveryFactor = facility.InjuryRecoveryFactor };

            foreach (var p in Roster)
            {
                if (p.Injury == InjurySeverity.None)
                {
                    if (!InjuryModel.WeeklyCheck(p, rng, injury, _skills, InjuryCatalog.Default,
                            facility.InjuryProbFactor)) continue;
                    newItems.Add(new FeedItem
                    {
                        When = "今週",
                        Text = p.Name + " が " + InjuryLabel.Diagnosis(p.InjuryType, p.InjurySite) + "（"
                               + InjuryLabel.Severity(p.Injury) + "・全治 "
                               + InjuryLabel.WeeksToFullRecovery(p) + " 週）",
                        Kind = FeedKind.Warn,
                        Tag = "警告",
                    });
                    // ノベルで見せる（issue #610）。診断名と離脱週は効果帯へ回し、会話には数字を入れない。
                    KokoSim.Unity.Shell.EventService.Push("injury_occurred", p.Name,
                        new KokoSim.Unity.Shell.PresentedEffect
                        {
                            Subject = p.Name,
                            Detail = InjuryLabel.Diagnosis(p.InjuryType, p.InjurySite)
                                     + "（" + InjuryLabel.Severity(p.Injury) + "）",
                            Items = new[]
                            {
                                new KokoSim.Unity.Shell.EffectItem(
                                    "全治", InjuryLabel.WeeksToFullRecovery(p).ToString(), "週"),
                            },
                        }, p.Id);
                }
                else
                {
                    InjuryModel.WeeklyRecover(p, _injury, InjuryCatalog.Default);
                    if (p.Injury != InjurySeverity.None) continue;
                    newItems.Add(new FeedItem
                    {
                        When = "今週",
                        Text = p.Name + " が怪我から復帰した。",
                        Kind = FeedKind.Up,
                        Tag = "情報",
                    });
                    KokoSim.Unity.Shell.EventService.Push("injury_returned", p.Name,
                        new KokoSim.Unity.Shell.PresentedEffect
                        {
                            Subject = p.Name,
                            Detail = "全快（練習に合流）",
                        }, p.Id);
                }
            }

            PushFeed(newItems);
        }

        /// <summary>
        /// 気づきの週次処理（設計書03 §5.6）。監督の育成眼を通じて隠しパラメータの兆しを受動的に開示する。
        /// 状態は一切変えない＝通知フィードへ流すだけ。
        ///
        /// <para>備品「ビデオ・分析機材」（設計書04 §1.4a）が頻度と精度を上げる。未購入なら 1.0 / 0.0＝
        /// エンジンの既定と1ビット一致。</para>
        ///
        /// <para><b><see cref="InsightNotice.IsAccurate"/> はプレイヤーに見せない</b>（内部検証用）。
        /// 読み違えも同じ「〜かもしれない」の温度で出す＝当たっているか分からないことが気づきの価値。</para>
        ///
        /// <para>乱数は怪我と同じ作法（<see cref="WeeklyStream"/>）で独立ストリームを Fork して引く
        /// （不変条件#2）。怪我とは別のストリームIDにする＝どちらかの抽選回数が変わっても他方がずれない。</para>
        /// </summary>
        private void RunInsightWeek()
        {
            var rng = _rng.Fork(WeeklyStream(InsightStream));

            var gear = KokoSim.Unity.Shell.TrainingWeek.Effects;
            var notices = InsightModel.Week(
                Roster, KokoSim.Unity.Shell.ManagerService.Manager.TalentEye, rng, _insight,
                gear.InsightProbFactor, gear.InsightAccuracyAdd);
            if (notices.Count == 0) return;

            var newItems = new List<FeedItem>();
            foreach (var n in notices)
            {
                // タグ語彙は 成長 / 警告 / 発見 / 情報 の4つだけ（FeedItem の定義）。
                // 「気づき」のような新語を足すと HomeDashboardController.TagColor が拾えず
                // 既定の灰色に落ちる＝コードは通るのに画面だけ間違う。気づきは「発見」に載せる。
                newItems.Add(new FeedItem
                {
                    When = "今週",
                    Text = InsightText(n),
                    Kind = FeedKind.Discover,
                    Tag = "発見",
                });
            }
            PushFeed(newItems);
        }

        /// <summary>
        /// 気づき1件の文言。断定しない温度で出す（設計書03 §5.6）。当たり外れは文面に出さない。
        /// </summary>
        private static string InsightText(InsightNotice n)
        {
            var name = n.Player.Name;
            switch (n.Topic)
            {
                case InsightTopic.RecentGrowth:
                    return name + " は最近伸びてきているようだ。";
                case InsightTopic.Personality:
                    return name + " は " + n.Domain + " な性格かもしれない。";
                case InsightTopic.HighGrowthDomain:
                    return name + " は " + DomainJp(n.Domain) + " にまだ伸びる余地がありそうだ。";
                case InsightTopic.LowGrowthDomain:
                    return name + " の " + DomainJp(n.Domain) + " は伸びしろがないかもしれない。";
                case InsightTopic.GrowthType:
                    return name + " は大器晩成かもしれない。";
                default:
                    return name + " について何か気づいた。";
            }
        }

        /// <summary>成長分野の内部名（InsightModel.GrowthExtremes が返す3種）を日本語にする。</summary>
        private static string DomainJp(string domain)
        {
            switch (domain)
            {
                case "pitching": return "投球";
                case "batting": return "打撃";
                case "defense": return "守備";
                default: return domain;
            }
        }

        // ===== 大会モード（要件1〜7） =====

        public bool InTournament => GameSession.Current.InTournament;
        public bool BannerPending => GameSession.Current.BannerPending;
        public bool ReachedMatchDay => GameSession.Current.ReachedMatchDay;
        public bool TournamentFinished => GameSession.Current.Runner?.Finished ?? false;
        public PlayerMatchOutcome LastOutcome => GameSession.Current.LastOutcome;

        /// <summary>開幕バナー上段（例「2028年　夏」）。</summary>
        public string BannerTop
        {
            get
            {
                var year = SeasonBaseDisplayYear();
                return year + "年　" + (GameSession.Current.Kind == TournamentKind.Summer ? "夏" : "秋");
            }
        }
        /// <summary>開幕バナー中段（大会名）。</summary>
        public string BannerName => GameSession.Current.Title;

        /// <summary>次戦ダイアログ用ラウンド名（例「２回戦」）。</summary>
        public string NextRoundLabel => GameSession.Current.Runner?.RoundName ?? "";

        /// <summary>次戦ダイアログ用の対戦表記（例「vs 享栄 [B]」）。</summary>
        public string NextVsLabel
        {
            get
            {
                var r = GameSession.Current.Runner;
                if (r == null || r.NextOpponent == null) return "";
                return "vs " + r.NextOpponent.Name + " [" + r.NextOpponentTier + "]";
            }
        }

        /// <summary>大会モードで1日進める。7日跨ぎで効果低下の練習1週を適用する（要件3・6）。</summary>
        public void AdvanceDay()
        {
            var practiceDue = GameSession.Current.AdvanceDay();
            if (!practiceDue) return;
            RunPracticeWeek(_training.TournamentPracticeMult);
            RunInjuryWeek();
            RunInsightWeek();
        }

        /// <summary>自校の次戦を自動消化する（要件7「はい」）。結果をフィード化する。</summary>
        public PlayerMatchOutcome PlayMatch()
        {
            var o = GameSession.Current.PlayMatch();
            PushMatchFeed(o);
            return o;
        }

        /// <summary>自校の次戦をライブ観戦で始める（試合開始フロー・観戦画面へ渡す進行体を得る）。</summary>
        public LivePlayerMatch BeginMatch() => GameSession.Current.BeginMatch();

        /// <summary>ライブ観戦した自校戦の終局結果を大会へ反映し、結果をフィード化する（PlayMatch と同じ通知）。</summary>
        public PlayerMatchOutcome CompleteMatch(KokoSim.Engine.Match.Game.GameResult result)
        {
            var o = GameSession.Current.CompleteMatch(result);
            PushMatchFeed(o);
            return o;
        }

        private void PushMatchFeed(PlayerMatchOutcome o)
        {
            var verb = o.ManagerWon ? "○ 勝利" : "● 敗戦";
            var items = new List<FeedItem>
            {
                new FeedItem
                {
                    When = "本日",
                    Text = o.RoundName + " vs " + o.OpponentName + "　" + o.ManagerScore + "-" + o.OpponentScore + "　" + verb,
                    Kind = o.ManagerWon ? FeedKind.Up : FeedKind.Warn,
                    Tag = o.ManagerWon ? "情報" : "警告",
                },
            };
            items.AddRange(InjuryFeedItems());
            PushFeed(items);
        }

        /// <summary>
        /// 試合中の受傷・押して出場の結果を通知フィードへ流す（設計書03 §3.5・設計書06 §3.1）。
        /// 文言はエンジンの結果（傷病名・部位・段階・全治週）をそのまま整形するだけ。
        /// </summary>
        private static List<FeedItem> InjuryFeedItems()
        {
            var items = new List<FeedItem>();
            foreach (var n in GameSession.Current.DrainInjuryNotices())
            {
                string text;
                switch (n.Kind)
                {
                    case MatchInjuryOutcomeKind.Occurred:
                        text = n.PlayerName + " が試合中に " + InjuryLabel.Diagnosis(n.Type, n.Site) + "（"
                               + InjuryLabel.Severity(n.Severity) + "・全治 " + n.WeeksRemaining + " 週）";
                        break;
                    case MatchInjuryOutcomeKind.Worsened:
                        text = n.PlayerName + " が押して出場し悪化（" + InjuryLabel.Severity(n.Severity)
                               + "・あと " + n.WeeksRemaining + " 週）";
                        break;
                    default:
                        text = n.PlayerName + " は怪我を押して出場した（復帰が " + n.WeeksRemaining + " 週先へ）";
                        break;
                }
                items.Add(new FeedItem { When = "本日", Text = text, Kind = FeedKind.Warn, Tag = "警告" });
            }
            return items;
        }

        /// <summary>
        /// 結果表示を閉じる。大会モード自体は敗退/優勝を検知した時点で <see cref="GameSession"/> が
        /// 既に抜けている（issue #139）。ここでは後始末（週送り・通知フィード）だけを行う。
        /// </summary>
        public void DismissResult()
        {
            GameSession.Current.ConsumeResult();
            var wrapUp = GameSession.Current.ConsumeTournamentWrapUp();
            if (wrapUp is { } w)
            {
                var summary = w.IsChampion ? w.Title + " 優勝！" : w.Title + " 敗退";
                PushFeed(new List<FeedItem>
                {
                    new FeedItem { When = "大会", Text = summary, Kind = w.IsChampion ? FeedKind.Up : FeedKind.Normal, Tag = "情報" },
                });
                // 大会が消費した週ぶん共有週を進める（大会モードは既に抜けているのでここでは時計だけ）。
                // 大会が消費した週数＝最終日がどの週に入るか（切り捨てで正しい: 18日目は開幕週+2週の
                // 範囲 14〜20日に入る）。A案の日程（issue #583）で8ラウンド県18日＝2週・9ラウンド県21日＝3週。
                var weeks = System.Math.Max(1, w.TournamentDay / 7);
                KokoSim.Unity.Shell.GameClock.Advance(weeks);
            }
        }

        public void ConsumeBanner() => GameSession.Current.ConsumeBanner();

        private static int SeasonBaseDisplayYear()
            => KokoSim.Unity.Shell.SeasonClock.SeasonBaseYear + (KokoSim.Unity.Shell.GameClock.YearIndex - 1);

        private void PushFeed(List<FeedItem> newItems)
        {
            foreach (var it in newItems.Take(6)) _feed.Insert(0, it);
            while (_feed.Count > 12) _feed.RemoveAt(_feed.Count - 1);
        }

        /// <summary>
        /// 次戦相手カードに添える通算戦績の一言（issue #84）。新規パネルは作らず既存の1行に追記するだけに留める。
        /// まだ公式戦の対戦がない校（0勝0敗）は表示しない＝全校が同じ文言で埋まるノイズを避ける。
        /// </summary>
        private static string OfficialRecordSuffix(SchoolRecord record)
        {
            var total = record.OfficialWins + record.OfficialLosses;
            return total > 0 ? " ／ 通算" + record.OfficialWins + "勝" + record.OfficialLosses + "敗" : "";
        }

        /// <summary>次の試合カード＋カウントダウンを大会仕様で埋める（要件4・5）。</summary>
        private void FillTournamentView(HomeView view)
        {
            var s = GameSession.Current;
            var r = s.Runner;
            view.TournamentMode = true;
            view.CountdownLabel = "次戦まで";

            // 大会中は実月日で出す（issue #583）。週暦は50週×7日で実暦と一致しないため、
            // 大会期間だけ実カレンダーで数える（書式は GameSession.DateSuffix が単一ソース）。
            view.WeekSuffix = s.DateSuffix;

            view.HomeTeam = NationService.ManagerSchoolName;
            view.HomeSub = "自校";
            view.Venue = s.Title;
            view.Weather = "―";

            if (r.Finished)
            {
                view.CountdownValue = "―";
                view.CountdownCells = "―";
                view.GameTag = r.IsChampion ? "優勝" : "敗退";
                view.AwayTeam = "―";
                view.AwaySub = "";
                view.GameDate = r.IsChampion ? "全試合終了" : "大会敗退";
            }
            else
            {
                var days = s.DaysUntilNextMatch;
                var reached = s.ReachedMatchDay;
                view.CountdownValue = reached ? "本日 試合" : days + " 日";
                view.CountdownCells = reached ? "本日試合" : days + "日";
                view.GameTag = r.RoundName;
                view.AwayTeam = r.NextOpponent?.Name ?? "―";
                view.AwaySub = r.NextOpponent != null
                    ? "ランク " + r.NextOpponentTier + OfficialRecordSuffix(s.Records.For(r.NextOpponent.Id))
                    : "";
                // 日付欄は実月日（issue #583）。カウントダウンの升目は「あと N 日」のまま
                // ＝「いつ」と「あと何日」は別の情報なので両方出す。
                view.GameDate = reached ? "本日 試合" : s.NextMatchDateLabel + " 試合";
            }
        }

        public HomeView BuildView()
        {
            var week = KokoSim.Unity.Shell.GameClock.Week;        // 共有現在週
            var view = new HomeView();
            // 資金・名声・信頼度のトップバー表示は TopBarMeters（ManagerService 単一ソース）へ集約済み。

            var roster = Roster;
            view.TeamRankGrade = KokoSim.Unity.Shell.TeamOverall.GradeOf(roster);

            // 故障者（設計書03 §3.5: 部位・程度は常に可視）。重い順→学年順で並べる。
            foreach (var p in roster.Where(x => x.Injury != InjurySeverity.None)
                         .OrderByDescending(x => (int)x.Injury).ThenBy(x => x.Id))
            {
                view.Injured.Add(new InjuredRow
                {
                    Number = p.UniformNumber == 0 ? "—" : p.UniformNumber.ToString(),
                    Name = p.Name,
                    Site = InjuryLabel.Site(p.InjurySite),
                    Type = InjuryLabel.Type(p.InjuryType),
                    Severity = InjuryLabel.Severity(p.Injury),
                    Back = "あと " + InjuryLabel.WeeksToFullRecovery(p) + " 週",
                    Severe = p.Injury == InjurySeverity.Severe,
                });
            }

            if (GameSession.Current.InTournament)
            {
                FillTournamentView(view);
            }
            else
            {
                // 通常モード: **次の公式戦**までの残り週（issue #582）。夏の後は秋、秋の後は翌年の夏。
                // 引き算はここに置かず SeasonCalendar.NextTournament を通す（トップバーと同じ単一ソース）。
                // 「次の試合」カードは大会モード中だけ出す（通常週はダミーの対戦カードを出さない）。
                view.TournamentMode = false;
                var next = _calendar.NextTournament(week);
                var left = next.WeeksLeft;
                view.CountdownLabel = SeasonClock.CountdownLabel(next.Kind);
                view.CountdownValue = left > 0 ? "残り " + left + " 週" : "開催中";
                view.CountdownCells = left > 0 ? left + "週" : "開催中";
                // 主役ヒーロー帯（通常週）: 数字＝Oswald にするため単位と分けて持つ。
                view.HeroBigValue = left > 0 ? left.ToString() : "開催中";
                view.HeroBigUnit = left > 0 ? "週" : "";
            }

            // 個別指導（設計書04。枠のみ＝上位2名を仮表示＋空き1。効果はエンジン未接続 Q7）。
            // 並べ替えキーは打撃力（「選手個人の総合ランク」は #30 で廃止, issue #270）。
            var top = roster.OrderByDescending(x => PlayerStrengthProfile.Compute(x, GuidanceCoeff).Batting)
                .Take(2).ToList();
            view.Guidance.Add(new GuidanceSlot { Name = top.Count > 0 ? top[0].Name : "", Focus = "打撃強化", Empty = top.Count == 0 });
            view.Guidance.Add(new GuidanceSlot { Name = top.Count > 1 ? top[1].Name : "", Focus = "投球", Empty = top.Count <= 1 });
            view.Guidance.Add(new GuidanceSlot { Empty = true });
            view.GuidanceUsed = view.Guidance.Count(g => !g.Empty);

            view.Feed = _feed.ToList();
            return view;
        }

        // ===== 表示用の変換 =====

        private static Dictionary<AbilityKind, int> Snapshot(DevelopingPlayer p)
        {
            var d = new Dictionary<AbilityKind, int>();
            foreach (var k in AbilityKinds.All) d[k] = p.Level(k);
            return d;
        }

        // 能力表示名は単一ソース（KokoSim.Unity.Shell.AbilityLabels）に集約（issue #94）。
        private static string AbilityJp(AbilityKind k) => KokoSim.Unity.Shell.AbilityLabels.Jp(k);
    }
}
