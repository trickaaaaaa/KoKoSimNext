using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;

namespace KokoSim.Unity.Tournament
{
    /// <summary>
    /// 大会プレビュー画面の ViewModel（設計書06 §3.5b, mock-tournament-preview.html）。UnityEngine 非依存。
    /// 出場校は「今まさに開催中の大会」（GameSession.Field＝自校＋県内校）を使う。
    /// 注目選手・登録メンバーは実際の対戦相手ラインナップと同一ソース（StrengthTeamFactory.ForSchool）から
    /// 組まれるため、展望で見た選手が実戦でそのまま出てくる。
    /// UI（Controller）はこの View を描画するだけ。
    /// </summary>
    public sealed class TournamentPreviewState
    {
        public sealed class ContenderRow
        {
            public string MarkSym;      // ◎ ○ ▲
            public string MarkClass;    // fav / cont / dark（USS 色分け）
            public string MarkLabel;    // 優勝候補 / 対抗 / ダークホース
            public bool IsFavorite;
            public string Name;
            public string TierLetter;   // S..G（gradeチップ）
            public string SeedLabel;    // 第1シード / ノーシード
            public string Blurb;
            public int Batting, Pitching, Defense;
        }

        public sealed class NotableRow
        {
            public string Number;       // 背番号（丸バッジ・中立色。投打で塗り分けない, issue #379）
            public string Name;
            public string Sub;          // 校名・学年・投打・守備位置
            public string StatLine;     // 最速/防御率 or 打率/本塁打（今大会の実測値, issue #634）
            public string Blurb;
        }

        public sealed class MemberRow
        {
            public string Number;
            public string Name;         // 氏名（守備位置つき）
            public string Grade;        // 「2年」
            public string Hand;         // 「右投左打」
            // 今大会成績の3列（issue #634）。登板のある選手は投手の数字（防御率・投球回・奪三振）、
            // それ以外は打者の数字（打率・本塁打・打点）。出場が無ければ3つとも「—」。
            public string Stat1;
            public string Stat2;
            public string Stat3;
        }

        public sealed class RosterBlock
        {
            public string SchoolName;
            public string Tag;          // 「総合 A ／ 第1シード」
            public string TierLetter;
            public string Sub;          // チーム寸評
            public string StatsCaption; // 「県予選 6試合」（1試合もしていなければ「県予選 まだ試合なし」）
            // 3軸（打力/投手/守備）。甲子園の右ペインは展望カードと同じ物差しをその場で出す。
            public int Batting, Pitching, Defense;
            public List<MemberRow> Members;
        }

        /// <summary>樹形図の1スロット（カードの上側／下側）の表示行。</summary>
        public sealed class BracketSlotRow
        {
            public string Name;         // 校名。未確定枠（不戦勝の空き枠を含む）は「（未定）」
            public string Score;        // 消化済みカードのみ。未消化は空文字
            public bool IsManager;      // 自校ライン（アンバー強調）
            public bool IsWinner;       // 勝ち上がった側
            public bool IsLoser;        // 消化済みかつ敗者（グレーアウト）
            public bool IsDetermined;   // 校名が確定しているか
        }

        /// <summary>樹形図の1カード（対戦枠）。Round は0基点、SlotIndex はラウンド内のカード位置。</summary>
        public sealed class BracketCardRow
        {
            public int Round, SlotIndex;
            public string RoundName;
            public BracketSlotRow Top, Bottom;
            public bool ManagerInvolved;   // このカードに自校がいる（左からの接続線＋枠をアンバーに）
            public bool ManagerAdvances;   // 自校がこのカードを勝ち上がった（右への接続線をアンバーに）
            public bool IsBye;
            public bool MercyEnded;        // コールドゲーム（マーシールール）成立で打ち切られたか（設計書05 §1.3, Q18）
        }

        /// <summary>樹形図の1ラウンド＝1列。</summary>
        public sealed class BracketRoundColumn
        {
            public int Round;
            public string Name;
            public List<BracketCardRow> Cards = new List<BracketCardRow>();
        }

        /// <summary>樹形図（ラウンド×スロットの2次元）。Controller はこれを描画するだけ。</summary>
        public sealed class BracketTreeView
        {
            public List<BracketRoundColumn> Rounds = new List<BracketRoundColumn>();
            public string ChampionName;        // 未確定は null
            public bool ManagerIsChampion;
            public BracketCardRow ManagerFocus;   // 初期表示でスクロールする先（自校の最新カード）
        }

        /// <summary>
        /// 甲子園の出場校一覧の1行（設計書05 §1.1a・要望「N年ぶりN回目が見れる」）。
        /// 左ペインの名簿がこれを並べ、押すと右ペインにその校の詳細（県予選成績まで）が出る。
        /// </summary>
        public sealed class EntrantRow
        {
            public string RegionName;      // 代表（"北北海道" / "神奈川県"）
            public string SchoolName;
            public string TierLetter;      // S..G（ランクチップ）
            public string AppearanceLabel; // 「3年ぶり4回目」「初出場」
            public bool IsManager;         // 自校の行（面で強調）
            /// <summary>並び順（49区画の並び＝北から南へ）。表示しない。</summary>
            public int RegionOrder;
        }

        public sealed class View
        {
            public string Title;
            public string Meta;
            public string Lead;
            public List<ContenderRow> Contenders = new List<ContenderRow>();
            public List<NotableRow> Notables = new List<NotableRow>();
            /// <summary>出場校の名簿（甲子園のときだけ埋まる。県大会は空＝セクションごと出さない）。</summary>
            public List<EntrantRow> Entrants = new List<EntrantRow>();
        }

        /// <summary>
        /// 校別詳細の成績スコープ（issue 甲子園）。甲子園の展望は<b>県予選</b>を既定にする
        /// （「選手一人一人の県予選成績が見れる」＝要望）。大会が進めば甲子園側も見られる。
        /// </summary>
        public enum StatScope { CurrentTournament, PrefQualifier }

        /// <summary>大会が開催中でなければ null（呼び出し側は空状態を出す）。</summary>
        public View Build()
        {
            var d = KokoSim.Unity.Shell.GameSession.Current.Display;
            if (d == null || d.Field.Count == 0) return null;

            var koshien = d.Kind == KokoSim.Engine.Season.TournamentKind.SummerKoshien;
            var summer = d.Kind == KokoSim.Engine.Season.TournamentKind.Summer;
            // 注目選手・登録メンバーは実戦と同一の永続ロスター（#80）から引く＝展望で見た選手が実戦で出る。
            var preview = TournamentPreviewBuilder.Build(
                d.Title, d.Field,
                // 上位何校が次へ行くか。甲子園は次のステージが無いので枠の言い回し自体を出さない（engine 側で分岐）。
                berths: koshien || summer ? 1 : 2,
                nextStageName: koshien ? "" : summer ? "甲子園" : "地区大会",
                yearIndex: KokoSim.Unity.Shell.GameSession.Current.Year,
                teamProvider: TeamFor,
                // 注目選手の成績行は実測値で書く（issue #634）。甲子園は<b>県予選</b>の実測＝
                // 開幕前でも「この選手は県予選で打っている」が読める（今大会スコープはまだ空）。
                statsForSchool: id => BookFor(id, koshien ? StatScope.PrefQualifier : StatScope.CurrentTournament),
                stage: koshien ? PreviewStage.SummerKoshien
                    : summer ? PreviewStage.SummerPrefecture : PreviewStage.Autumn);

            var v = new View { Title = preview.Title, Meta = preview.Meta, Lead = preview.Lead };
            if (koshien) v.Entrants = BuildEntrants(d.Field);
            var session = KokoSim.Unity.Shell.GameSession.Current;

            // 通算戦績（issue #84）はGameSession側の記録簿にあり、engineのプレビュー生成は校名しか
            // 持たないため、ここで校名→Idを引いて寸評の頭に付け足す（新規パネル・新規要素は作らない）。
            // 同名校が居ても落ちないよう先勝ちで畳む（校名は生成物なので一意の保証が無い）。
            var idByName = new Dictionary<string, int>();
            foreach (var s in d.Field) if (!idByName.ContainsKey(s.Name)) idByName[s.Name] = s.Id;
            var records = session.Records;

            foreach (var c in preview.Contenders)
            {
                var history = idByName.TryGetValue(c.Name, out var schoolId)
                    ? KoshienHistoryText(records.For(schoolId))
                    : "";
                v.Contenders.Add(new ContenderRow
                {
                    MarkSym = Sym(c.Mark),
                    MarkClass = MarkClass(c.Mark),
                    MarkLabel = MarkLabel(c.Mark),
                    IsFavorite = c.Mark == ContenderMark.Favorite,
                    Name = c.Name,
                    TierLetter = c.Tier.ToString(),
                    SeedLabel = c.Mark == ContenderMark.DarkHorse ? "ノーシード" : "第" + c.Seed + "シード",
                    Blurb = history + c.Blurb,
                    Batting = c.Rating.Batting,
                    Pitching = c.Rating.Pitching,
                    Defense = c.Rating.Defense,
                });
            }

            foreach (var n in preview.NotablePlayers)
            {
                v.Notables.Add(new NotableRow
                {
                    Number = n.UniformNumber.ToString(),
                    Name = n.Name,
                    Sub = n.SchoolName + "・" + n.Grade + "年・" + n.HandednessLabel + "・" + n.PositionLabel,
                    StatLine = n.StatLine,
                    Blurb = n.Blurb,
                });
            }

            return v;
        }

        /// <summary>
        /// 樹形図でクリックされた1校の校別詳細（ベンチ入りメンバー表＋チーム寸評）を組む（issue #189）。
        /// 展望の Contenders（格付け校のみ）とは独立に、出場校なら誰でもオンデマンドで生成できる。
        /// 大会が開催中でない・該当校が見つからない場合は null（呼び出し側は既存ビューのままにする）。
        /// </summary>
        /// <param name="scope">
        /// 成績3列のスコープ。既定の今大会に加え、甲子園では<b>県予選</b>を選べる
        /// （設計書05 §1.4b・要望「選手一人一人の県予選成績が見れる」）。
        /// </param>
        public static RosterBlock BuildSchoolDetail(
            string schoolName, StatScope scope = StatScope.CurrentTournament)
        {
            var d = KokoSim.Unity.Shell.GameSession.Current.Display;
            if (d == null || d.Field.Count == 0) return null;

            var school = d.Field.FirstOrDefault(s => s.Name == schoolName);
            if (school == null) return null;

            var session = KokoSim.Unity.Shell.GameSession.Current;
            var koshien = d.Kind == KokoSim.Engine.Season.TournamentKind.SummerKoshien;
            var summer = d.Kind == KokoSim.Engine.Season.TournamentKind.Summer;
            var excerpt = TournamentPreviewBuilder.BuildRosterFor(
                school, d.Field, berths: koshien || summer ? 1 : 2, yearIndex: session.Year,
                teamProvider: TeamFor);

            // 通算戦績（issue #84）は寸評の頭に付け足す（展望の Contenders 行と同じ扱い）。
            var history = KoshienHistoryText(session.Records.For(school.Id));

            // 成績（issue #634 / 設計書05 §1.4b）。裏試合フルシム（#43）と代表校の打ち直しが積んだ実測値を
            // (校ID, 選手ID) で引く。帳簿は校ごとに1回だけ引き、行ごとに引き直さない。
            var book = BookFor(school.Id, scope);

            var block = new RosterBlock
            {
                SchoolName = excerpt.SchoolName,
                TierLetter = excerpt.Tier.ToString(),
                Tag = "総合 " + excerpt.Tier + " ／ " + excerpt.SeedLabel,
                Sub = history + excerpt.TeamBlurb,
                StatsCaption = GamesCaption(book, scope),
                Batting = excerpt.Rating.Batting,
                Pitching = excerpt.Rating.Pitching,
                Defense = excerpt.Rating.Defense,
                Members = new List<MemberRow>(excerpt.Members.Count),
            };
            foreach (var m in excerpt.Members)
            {
                var row = new MemberRow
                {
                    Number = m.UniformNumber.ToString(),
                    Name = m.Name + "（" + m.PositionLabel + "）",
                    Grade = m.Grade + "年",
                    Hand = m.HandednessLabel,
                };
                FillStats(row, book?.Get(m.SourceId));
                block.Members.Add(row);
            }
            return block;
        }

        /// <summary>
        /// 甲子園の出場校名簿（要望「N年ぶりN回目が見れる」）。並びは<b>代表の区画Id順</b>
        /// ＝北から南へ（実際の出場校一覧と同じ読み順）。同じ区画の中は校IDで安定化する。
        ///
        /// <para>「N年ぶりN回目」は engine の <see cref="AppearanceLabel"/> が
        /// 出場を記録した時点で焼いた文言（<see cref="SchoolRecord.SummerAppearanceLabel"/>）。
        /// <b>UI で数え直さない</b>＝出場回数の数え方が2箇所に散らない。</para>
        /// </summary>
        private static List<EntrantRow> BuildEntrants(IReadOnlyList<School> field)
        {
            var prefectures = KokoSim.Unity.Shell.NationService.Nation.Prefectures;
            var records = KokoSim.Unity.Shell.GameSession.Current.Records;
            var managerId = KokoSim.Unity.Shell.NationService.ManagerSchoolId;
            // 49区画は JIS順（北海道→沖縄）に並んでいる＝この並びの添字がそのまま「北から南へ」。
            // 校名や区画名の文字列で並べると読み順が崩れる（"三重県" < "京都府" < "佐賀県" になる）。
            var regions = SummerRegions.Build(prefectures);

            var rows = new List<EntrantRow>(field.Count);
            foreach (var s in field)
            {
                var order = int.MaxValue;
                var name = "";
                for (var i = 0; i < regions.Count; i++)
                    if (regions[i].Contains(s)) { order = i; name = regions[i].Name; break; }
                if (name.Length == 0 && s.Id == managerId)
                {
                    // 自校は専用IDで Contains がどの区画にも一致しない。DistrictId から実際に戦った区画
                    // （分割県なら北北海道/南北海道等）を決め直す＝TournamentEntry.BuildField と同じ区画。
                    var region = SummerRegions.RegionFor(prefectures, s.PrefectureId, s.DistrictId);
                    name = region.Name;
                    for (var i = 0; i < regions.Count; i++)
                        if (regions[i] == region) { order = i; break; }
                }
                else if (name.Length == 0)
                {
                    // どの区画にも属さない（想定外の学校）。県名へ落として県の位置へ並べる。
                    name = SummerRegions.NameOf(prefectures, s);
                    for (var i = 0; i < regions.Count; i++)
                        if (regions[i].PrefectureId == s.PrefectureId) { order = i; break; }
                }

                var label = records.For(s.Id).SummerAppearanceLabel;
                rows.Add(new EntrantRow
                {
                    RegionName = name,
                    SchoolName = s.Name,
                    TierLetter = s.Tier.ToString(),
                    // 未記録（旧セーブから来た年など）は空にせず「出場」と出す＝空欄で列が抜けたように見せない。
                    AppearanceLabel = string.IsNullOrEmpty(label) ? "出場" : label,
                    IsManager = s.Id == managerId,
                    RegionOrder = order,
                });
            }
            rows.Sort((a, b) =>
            {
                var c = a.RegionOrder.CompareTo(b.RegionOrder);
                return c != 0 ? c : string.CompareOrdinal(a.SchoolName, b.SchoolName);
            });
            return rows;
        }

        /// <summary>成績スコープ→校の帳簿（振り分けは engine の <see cref="SchoolStatsLookup"/> 1箇所）。</summary>
        private static KokoSim.Engine.Stats.StatBook BookFor(int schoolId, StatScope scope)
        {
            var lookup = KokoSim.Unity.Shell.NationService.StatsLookup;
            return scope == StatScope.PrefQualifier
                ? lookup.PrefQualifierBook(schoolId)
                : lookup.CurrentTournamentBook(schoolId);
        }

        /// <summary>
        /// 出場校1校のチーム（登録メンバー・注目選手の材料）。相手校は永続ロスター（#80）＝実戦と同一ソース。
        /// <b>自校だけは実戦と同じ入口（<see cref="KokoSim.Unity.Shell.PlayerMatchResolver.BuildManagerTeam"/>）
        /// から組む</b>——自校ID（-1）を全国ロスターへ渡すと架空の部員が生成され、実在しない選手名の表で
        /// メンバー表が出る（issue #634 の実装中に発見。成績列を足すと、その架空の名前に自校選手の
        /// 成績が並ぶ＝「出ていない選手が出たことになる」）。
        /// </summary>
        private static KokoSim.Engine.Match.Game.Team TeamFor(School school, int year)
            => school.Id == KokoSim.Unity.Shell.NationService.ManagerSchoolId
                ? KokoSim.Unity.Shell.PlayerMatchResolver.BuildManagerTeam(school.Name)
                : KokoSim.Unity.Shell.NationService.Rosters.TeamFor(school, year);

        /// <summary>
        /// 今大会成績の3セル（issue #634）。<b>登板があれば投手の数字</b>（防御率・投球回・奪三振）、
        /// 無ければ打者の数字（打率・本塁打・打点）。出場そのものが無ければ3つとも「—」。
        /// 表は背番号順の1本のまま＝並びは分けず、見せる数字だけ役割に合わせる（ライブ観戦の控え行と同じ作法, #381）。
        /// </summary>
        private static void FillStats(MemberRow row, KokoSim.Engine.Stats.PlayerStats stats)
        {
            row.Stat1 = row.Stat2 = row.Stat3 = "—";
            if (stats == null) return;

            if (stats.Pitching.Games > 0)
            {
                row.Stat1 = stats.Pitching.Era.ToString("0.00");
                row.Stat2 = stats.Pitching.InningsText;
                row.Stat3 = stats.Pitching.StrikeOuts.ToString();
                return;
            }
            if (stats.Batting.AtBats <= 0) return;
            row.Stat1 = KokoSim.Unity.Match.MatchLiveStatsProvider.Avg(stats.Batting.Average);
            row.Stat2 = stats.Batting.HomeRuns.ToString();
            row.Stat3 = stats.Batting.Rbi.ToString();
        }

        /// <summary>その校が今大会で消化した試合数（選手ごとの出場試合数の最大＝先発で全試合出た選手の数）。</summary>
        private static string GamesCaption(KokoSim.Engine.Stats.StatBook book, StatScope scope)
        {
            var games = 0;
            if (book != null)
                foreach (var kv in book.Players)
                {
                    var p = kv.Value;
                    if (p.Batting.Games > games) games = p.Batting.Games;
                    if (p.Pitching.Games > games) games = p.Pitching.Games;
                }
            var label = ScopeLabel(scope);
            return games > 0 ? label + " " + games + "試合" : label + " まだ試合なし";
        }

        /// <summary>成績スコープの呼び名（表の見出し・キャプション・タブが同じ語を使う）。</summary>
        public static string ScopeLabel(StatScope scope)
            => scope == StatScope.PrefQualifier ? "県予選" : "今大会";

        /// <summary>
        /// エンジンのブラケット出力（TournamentRunner.BuildBracketView）を樹形図の描画用に変換する。
        /// ここでは表示文言と強調フラグを決めるだけで、勝敗・スロット配置はエンジンの値をそのまま使う。
        /// </summary>
        public static BracketTreeView BuildBracketTree(TournamentBracketView view)
        {
            var tree = new BracketTreeView();
            if (view == null || view.Rounds == null) return tree;
            tree.ChampionName = view.ChampionName;
            tree.ManagerIsChampion = view.ManagerIsChampion;

            foreach (var r in view.Rounds)
            {
                var col = new BracketRoundColumn { Round = r.Round, Name = r.RoundName };
                foreach (var c in r.Cards)
                {
                    var row = new BracketCardRow
                    {
                        Round = c.Round,
                        SlotIndex = c.SlotIndex,
                        RoundName = c.RoundName,
                        IsBye = c.IsBye,
                        Top = Slot(c.Top, c),
                        Bottom = Slot(c.Bottom, c),
                        MercyEnded = c.MercyEnded,
                    };
                    row.ManagerInvolved = c.Top.IsManager || c.Bottom.IsManager;
                    row.ManagerAdvances = (row.Top.IsManager && row.Top.IsWinner)
                                          || (row.Bottom.IsManager && row.Bottom.IsWinner);
                    col.Cards.Add(row);
                    // ラウンド順に走査するので、最後に残るのが自校の最新カード（＝初期スクロール先）。
                    if (row.ManagerInvolved) tree.ManagerFocus = row;
                }
                tree.Rounds.Add(col);
            }
            return tree;
        }

        private static BracketSlotRow Slot(BracketSlot s, BracketCard card)
        {
            // 不戦勝カードの空き側は Controller 側で描画自体を省く（issue #188）。相手側は勝者扱いにして
            // 自校ラインを途切れさせない。
            var name = s.IsDetermined ? s.TeamName : "（未定）";
            var isWinner = s.IsWinner || (card.IsBye && s.IsDetermined);
            return new BracketSlotRow
            {
                Name = name,
                Score = s.Score.HasValue ? s.Score.Value.ToString() : "",
                IsManager = s.IsManager,
                IsWinner = isWinner,
                IsLoser = card.IsPlayed && !s.IsWinner,
                IsDetermined = s.IsDetermined,
            };
        }

        /// <summary>
        /// 優勝候補行の寸評に添える甲子園出場歴（issue #84「甲子園 夏5回・春2回」等）。未出場は空文字。
        /// 最高成績が準優勝以上なら添える（優勝は#65で甲子園本戦が実装されるまで発生しない）。
        /// </summary>
        private static string KoshienHistoryText(SchoolRecord r)
        {
            if (r.TotalAppearances == 0) return "";
            var parts = new List<string>();
            if (r.SummerAppearances > 0) parts.Add("夏" + r.SummerAppearances + "回");
            if (r.SpringAppearances > 0) parts.Add("春" + r.SpringAppearances + "回");
            var best = r.BestResult switch
            {
                BestResult.Champion => "優勝経験／",
                BestResult.RunnerUp => "準優勝経験／",
                _ => "",
            };
            return "甲子園 " + best + string.Join("・", parts) + "。";
        }

        private static string Sym(ContenderMark m) => m switch
        {
            ContenderMark.Favorite => "◎",
            ContenderMark.Contender => "○",
            ContenderMark.DarkHorse => "▲",
            _ => "",
        };

        private static string MarkClass(ContenderMark m) => m switch
        {
            ContenderMark.Favorite => "fav",
            ContenderMark.Contender => "cont",
            ContenderMark.DarkHorse => "dark",
            _ => "cont",
        };

        private static string MarkLabel(ContenderMark m) => m switch
        {
            ContenderMark.Favorite => "優勝候補",
            ContenderMark.Contender => "対抗",
            ContenderMark.DarkHorse => "ダークホース",
            _ => "",
        };
    }
}
