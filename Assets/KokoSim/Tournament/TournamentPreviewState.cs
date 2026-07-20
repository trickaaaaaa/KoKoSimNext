using System;
using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;

namespace KokoSim.Unity.Tournament
{
    /// <summary>
    /// 大会プレビュー画面の ViewModel（設計書06 §3.5b, mock-tournament-preview.html）。UnityEngine 非依存。
    /// 出場校（実名・強さ・校風）を決定論生成し、エンジンの TournamentPreviewBuilder でプレビューを組む。
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

        public sealed class View
        {
            public string Title;
            public string Meta;
            public string Lead;
            public List<ContenderRow> Contenders;
        }

        public View Build()
        {
            var preview = TournamentPreviewBuilder.Build(
                "2027年度 秋季神奈川県大会", GenerateField(), berths: 2, nextStageName: "関東大会");

            var rows = new List<ContenderRow>(preview.Contenders.Count);
            foreach (var c in preview.Contenders)
            {
                rows.Add(new ContenderRow
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
            return new View { Title = preview.Title, Meta = preview.Meta, Lead = preview.Lead, Contenders = rows };
        }

        // 出場校は生成済み全国から1県ぶんを引く（校名は NationGenerator が県内ユニーク化済み）。
        // 決定論生成なので静的キャッシュで初回のみ生成（画面再表示のたびの再生成を避ける）。
        private const int PreviewPrefectureId = 32; // 校数 ≈ 64（mock「出場64校」相当）
        private static KokoSim.Engine.Nation.Nation _nation;

        private static List<School> GenerateField()
        {
            if (_nation == null)
            {
                _nation = NationGenerator.Generate(
                    KokoSim.Unity.Shell.SchoolNameVocabProvider.Default, new NationCoefficients(), new Xoshiro256Random(2027));
            }
            var field = new List<School>();
            foreach (var s in _nation.InPrefecture(PreviewPrefectureId)) field.Add(s);
            return field;
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
