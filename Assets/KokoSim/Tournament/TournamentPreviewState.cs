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
            public string StatsCaption; // 「今大会 2試合」（1試合もしていなければ「今大会 まだ試合なし」）
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

        public sealed class View
        {
            public string Title;
            public string Meta;
            public string Lead;
            public List<ContenderRow> Contenders = new List<ContenderRow>();
            public List<NotableRow> Notables = new List<NotableRow>();
        }

        /// <summary>大会が開催中でなければ null（呼び出し側は空状態を出す）。</summary>
        public View Build()
        {
            var session = KokoSim.Unity.Shell.GameSession.Current;
            if (!session.InTournament || session.Field == null || session.Field.Count == 0) return null;

            // 上位2校が次のステージへ（秋＝地区/関東、夏＝甲子園）。現状の大会構造に合わせた表示上の既定。
            var summer = session.Kind == KokoSim.Engine.Season.TournamentKind.Summer;
            // 注目選手・登録メンバーは実戦と同一の永続ロスター（#80）から引く＝展望で見た選手が実戦で出る。
            var preview = TournamentPreviewBuilder.Build(
                session.Title, session.Field,
                berths: summer ? 1 : 2,
                nextStageName: summer ? "甲子園" : "地区大会",
                yearIndex: session.Year,
                teamProvider: TeamFor,
                // 注目選手の成績行は今大会の実測値で書く（issue #634。以前は能力から合成した見込み値だった）。
                statsForSchool: id => KokoSim.Unity.Shell.NationService.StatsLookup.CurrentTournamentBook(id));

            var v = new View { Title = preview.Title, Meta = preview.Meta, Lead = preview.Lead };

            // 通算戦績（issue #84）はGameSession側の記録簿にあり、engineのプレビュー生成は校名しか
            // 持たないため、ここで校名→Idを引いて寸評の頭に付け足す（新規パネル・新規要素は作らない）。
            var idByName = session.Field.ToDictionary(s => s.Name, s => s.Id);
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
        public static RosterBlock BuildSchoolDetail(string schoolName)
        {
            var session = KokoSim.Unity.Shell.GameSession.Current;
            if (!session.InTournament || session.Field == null || session.Field.Count == 0) return null;

            var school = session.Field.FirstOrDefault(s => s.Name == schoolName);
            if (school == null) return null;

            var summer = session.Kind == KokoSim.Engine.Season.TournamentKind.Summer;
            var excerpt = TournamentPreviewBuilder.BuildRosterFor(
                school, session.Field, berths: summer ? 1 : 2, yearIndex: session.Year,
                teamProvider: TeamFor);

            // 通算戦績（issue #84）は寸評の頭に付け足す（展望の Contenders 行と同じ扱い）。
            var history = KoshienHistoryText(session.Records.For(school.Id));

            // 今大会成績（issue #634）。裏試合フルシム（#43）が積んだ実測値を (校ID, 選手ID) で引く。
            // 帳簿は校ごとに1回だけ引き、行ごとに引き直さない。
            var book = KokoSim.Unity.Shell.NationService.StatsLookup.CurrentTournamentBook(school.Id);

            var block = new RosterBlock
            {
                SchoolName = excerpt.SchoolName,
                TierLetter = excerpt.Tier.ToString(),
                Tag = "総合 " + excerpt.Tier + " ／ " + excerpt.SeedLabel,
                Sub = history + excerpt.TeamBlurb,
                StatsCaption = GamesCaption(book),
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
        private static string GamesCaption(KokoSim.Engine.Stats.StatBook book)
        {
            var games = 0;
            if (book != null)
                foreach (var kv in book.Players)
                {
                    var p = kv.Value;
                    if (p.Batting.Games > games) games = p.Batting.Games;
                    if (p.Pitching.Games > games) games = p.Pitching.Games;
                }
            return games > 0 ? "今大会 " + games + "試合" : "今大会 まだ試合なし";
        }

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
