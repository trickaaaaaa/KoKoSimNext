using System.Collections.Generic;
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
            public string Number;       // 背番号（丸バッジ）
            public bool IsPitcher;      // バッジ色分け（投手=アンバー / 野手=グリーン）
            public string Name;
            public string Sub;          // 校名・学年・投打・守備位置
            public string StatLine;     // 最速/防御率 or 打率/本塁打（合成の見込み値）
            public string Blurb;
        }

        public sealed class MemberRow
        {
            public string Number;
            public string Name;         // 氏名（守備位置つき）
            public string Grade;        // 「2年」
            public string Hand;         // 「右投左打」
        }

        public sealed class RosterBlock
        {
            public string SchoolName;
            public string Tag;          // 「総合 A ／ 第1シード」
            public string TierLetter;
            public string Sub;          // チーム寸評
            public List<MemberRow> Members;
        }

        /// <summary>樹形図の1スロット（カードの上側／下側）の表示行。</summary>
        public sealed class BracketSlotRow
        {
            public string Name;         // 校名。未確定枠は「（未定）」、不戦勝の空き枠は「（不戦勝）」
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
            public List<RosterBlock> Rosters = new List<RosterBlock>();
        }

        /// <summary>大会が開催中でなければ null（呼び出し側は空状態を出す）。</summary>
        public View Build()
        {
            var session = KokoSim.Unity.Shell.GameSession.Current;
            if (!session.InTournament || session.Field == null || session.Field.Count == 0) return null;

            // 上位2校が次のステージへ（秋＝地区/関東、夏＝甲子園）。現状の大会構造に合わせた表示上の既定。
            var summer = session.Kind == KokoSim.Engine.Season.TournamentKind.Summer;
            var preview = TournamentPreviewBuilder.Build(
                session.Title, session.Field,
                berths: summer ? 1 : 2,
                nextStageName: summer ? "甲子園" : "地区大会",
                yearIndex: session.Year);

            var v = new View { Title = preview.Title, Meta = preview.Meta, Lead = preview.Lead };

            foreach (var c in preview.Contenders)
            {
                v.Contenders.Add(new ContenderRow
                {
                    MarkSym = Sym(c.Mark),
                    MarkClass = MarkClass(c.Mark),
                    MarkLabel = MarkLabel(c.Mark),
                    IsFavorite = c.Mark == ContenderMark.Favorite,
                    Name = c.Name,
                    TierLetter = c.Tier.ToString(),
                    SeedLabel = c.Mark == ContenderMark.DarkHorse ? "ノーシード" : "第" + c.Seed + "シード",
                    Blurb = c.Blurb,
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
                    IsPitcher = n.IsPitcher,
                    Name = n.Name,
                    Sub = n.SchoolName + "・" + n.Grade + "年・" + n.HandednessLabel + "・" + n.PositionLabel,
                    StatLine = n.StatLine,
                    Blurb = n.Blurb,
                });
            }

            foreach (var r in preview.Rosters)
            {
                var block = new RosterBlock
                {
                    SchoolName = r.SchoolName,
                    TierLetter = r.Tier.ToString(),
                    Tag = "総合 " + r.Tier + " ／ " + r.SeedLabel,
                    Sub = r.TeamBlurb,
                    Members = new List<MemberRow>(r.Members.Count),
                };
                foreach (var m in r.Members)
                {
                    block.Members.Add(new MemberRow
                    {
                        Number = m.UniformNumber.ToString(),
                        Name = m.Name + "（" + m.PositionLabel + "）",
                        Grade = m.Grade + "年",
                        Hand = m.HandednessLabel,
                    });
                }
                v.Rosters.Add(block);
            }

            return v;
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
            // 不戦勝カードの空き側は「（不戦勝）」。相手側は勝者扱いにして自校ラインを途切れさせない。
            var name = s.IsDetermined ? s.TeamName : (card.IsBye ? "（不戦勝）" : "（未定）");
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
