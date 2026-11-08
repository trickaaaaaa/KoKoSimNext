using System.Collections.Generic;
using System.Text;
using KokoSim.Engine.Nation.Tournaments;

namespace KokoSim.Unity.Tournament
{
    /// <summary>
    /// 【開発用・エディタ限定】<see cref="TournamentPamphletHtml"/> の検査（issue #645 の完了条件「純関数にテスト1本以上」）。
    /// Unity 側にはテスト基盤が無いので、FlowCheck 系と同じく MCP の execute_code から
    /// <c>TournamentPamphletHtmlCheck.Run()</c> を呼び、返る文字列の PASS/FAIL で判定する。
    /// 固定のビューモデルから、各セクション見出し・自校強調・エスケープ・file URL を確かめる。
    /// </summary>
    public static class TournamentPamphletHtmlCheck
    {
        public static string Run()
        {
            var log = new StringBuilder();
            var fail = 0;
            void Check(bool ok, string what)
            {
                log.Append(ok ? "PASS " : "FAIL ").AppendLine(what);
                if (!ok) fail++;
            }

            var view = new TournamentPreviewState.View
            {
                Title = "第1回 テスト県大会",
                Meta = "出場 4校",
                Lead = "混戦<予想>",
            };
            view.Contenders.Add(new TournamentPreviewState.ContenderRow
            {
                MarkSym = "◎", MarkClass = "fav", MarkLabel = "優勝候補", Name = "A&B高", TierLetter = "S",
                SeedLabel = "第1シード", Blurb = "\"強打\"", Batting = 85, Pitching = 90, Defense = 80,
            });
            view.Notables.Add(new TournamentPreviewState.NotableRow
            {
                Number = "1", Name = "山田", Sub = "A&B高・3年", StatLine = "最速148km/h", Blurb = "エース",
            });

            TournamentPreviewState.RosterBlock Block(string name, string tier) => new TournamentPreviewState.RosterBlock
            {
                SchoolName = name, TierLetter = tier, Tag = "総合 " + tier + " ／ ノーシード", Sub = "寸評",
                StatsCaption = "今大会 1試合", Batting = 50, Pitching = 50, Defense = 50,
                Members = new List<TournamentPreviewState.MemberRow>
                {
                    new TournamentPreviewState.MemberRow { Number = "1", Name = "佐藤（投）", Grade = "3年", Hand = "右投右打", Stat1 = "1.50", Stat2 = "9", Stat3 = "10" },
                },
            };
            var schools = new List<TournamentPamphletHtml.School>
            {
                new TournamentPamphletHtml.School { Block = Block("A&B高", "S"), IsManager = false },
                new TournamentPamphletHtml.School { Block = Block("自校高", "C"), IsManager = true },
                new TournamentPamphletHtml.School { Block = Block("丙高", "E") },
                new TournamentPamphletHtml.School { Block = Block("丁高", "G") },
            };

            BracketSlot S(string n, bool w = false, int? sc = null, bool m = false) => new BracketSlot(n, m, w, sc);
            var bv = new TournamentBracketView("テスト", new List<BracketMatch>(), null, false, false, new List<BracketRound>
            {
                new BracketRound(0, "準決勝", new List<BracketCard>
                {
                    new BracketCard(0, 0, "準決勝", S("A&B高", false, 2), S("自校高", true, 5, m: true), false, true),
                    new BracketCard(0, 1, "準決勝", S("丙高"), S("丁高"), false, false),
                }),
                new BracketRound(1, "決勝", new List<BracketCard>
                {
                    new BracketCard(1, 0, "決勝", S("自校高", m: true), S(null), false, false),
                }),
            });
            var bracket = NewspaperBracketLayout.Build(bv, TournamentPamphletHtml.Metrics);
            var html = TournamentPamphletHtml.Render(view, bracket, schools);

            foreach (var h in new[] { "優勝争いの構図", "注目選手", "トーナメント表", "出場校の登録メンバー（4校）", "索引" })
                Check(html.Contains(h), "見出し: " + h);
            Check(html.Contains("<title>第1回 テスト県大会 大会パンフレット</title>"), "表題");
            Check(html.Contains("A&amp;B高") && !html.Contains(">A&B高<"), "校名のエスケープ（&）");
            Check(html.Contains("混戦&lt;予想&gt;"), "概観文のエスケープ（< >）");
            Check(html.Contains("&quot;強打&quot;"), "寸評のエスケープ（\"）");
            Check(html.Contains("class=\"school own\" id=\"s-1\""), "自校のメンバー表を面で強調");
            Check(html.Contains("<li class=\"own\"><a href=\"#s-1\">"), "索引の自校行を強調");
            Check(html.Contains("<a href=\"#s-0\"><text class=\"leaf out\""), "端の校名から校別メンバー表へ飛ぶ（敗退校はグレー）");
            Check(html.Contains("class=\"ln won fav\""), "自校の勝ち上がり線（太線＋自校色）");
            Check(!html.Contains("未定"), "「未定」を書かない");
            Check(html.Contains("<tspan class=\"cap\">優勝</tspan></text>"), "優勝校が決まるまで名前を出さない");
            Check(html.Contains("background:#E19900") && html.Contains(">S</span>"), "ランクはレター＋色の併記");
            Check(!html.Contains("<script") && !html.Contains("@font-face") && !html.Contains("<link") && !html.Contains("<img"),
                "単一ファイル完結（script/link/img/@font-face なし）");

            Check(TournamentPamphletHtml.FileUrl("/Users/a b/Library/Application Support/高校/previews/1-Summer.html")
                  == "file:///Users/a%20b/Library/Application%20Support/%E9%AB%98%E6%A0%A1/previews/1-Summer.html",
                "file URL（macOS・空白と日本語）");
            Check(TournamentPamphletHtml.FileUrl(@"C:\Users\山田 太郎\AppData\1-Summer.html")
                  == "file:///C:/Users/%E5%B1%B1%E7%94%B0%20%E5%A4%AA%E9%83%8E/AppData/1-Summer.html",
                "file URL（Windows・ドライブ／区切り）");

            RunLeague(Check);

            log.Insert(0, fail == 0 ? "ALL PASS\n" : "FAILED " + fail + "\n");
            return log.ToString();
        }

        /// <summary>
        /// リーグ戦版（issue #783）。合成の地区（自校＋26校・4校ブロック）で <see cref="BlockLeagueRunner"/> を
        /// 実際に回し、1節消化（途中）と全節消化（終了）の2時点で書き出して確かめる。書き出した HTML は
        /// out/pamphlet-league-{mid,end}.html に残す（ブラウザでの目視用）。
        /// </summary>
        private static void RunLeague(System.Action<bool, string> check)
        {
            var field = new List<KokoSim.Engine.Nation.School>();
            field.Add(new KokoSim.Engine.Nation.School { Id = 1, Name = "自校高", PrefectureId = 0, Strength = 60 });
            for (var i = 0; i < 26; i++)
                field.Add(new KokoSim.Engine.Nation.School
                {
                    Id = 100 + i, Name = "第" + (i + 1) + "高校", PrefectureId = 0, Strength = 30 + (i * 7) % 41,
                    Style = (KokoSim.Engine.Match.Tactics.SchoolStyle)(i % 6),
                });
            var blocks = BlockDraw.Split(field, 4, null, new KokoSim.Engine.Core.Xoshiro256Random(7));
            var runner = new BlockLeagueRunner(blocks, field[0], 2, new KokoSim.Engine.Nation.NationCoefficients(),
                new KokoSim.Engine.Core.Xoshiro256Random(42), new TournamentSchedule(), "秋季テスト地区予選");

            var preview = TournamentPreviewBuilder.Build("秋季テスト地区予選", field, berths: 2, nextStageName: "県大会",
                stage: PreviewStage.DistrictLeague);
            var view = new TournamentPreviewState.View
            {
                Title = preview.Title, Meta = preview.Meta, Lead = preview.Lead,
                ContendersHeading = TournamentPreviewBuilder.ContendersHeading(PreviewStage.DistrictLeague),
                IsLeague = true,
            };
            foreach (var c in preview.Contenders)
                view.Contenders.Add(new TournamentPreviewState.ContenderRow
                {
                    MarkSym = c.Mark == ContenderMark.Favorite ? "◎" : c.Mark == ContenderMark.Contender ? "○" : "▲",
                    MarkClass = "cont", MarkLabel = TournamentPreviewBuilder.MarkLabel(c.Mark, PreviewStage.DistrictLeague),
                    Name = c.Name, TierLetter = c.Tier.ToString(),
                    SeedLabel = TournamentPreviewBuilder.SeedLabel(c.Mark, c.Seed, PreviewStage.DistrictLeague),
                    Blurb = c.Blurb, Batting = c.Rating.Batting, Pitching = c.Rating.Pitching, Defense = c.Rating.Defense,
                });
            foreach (var n in preview.NotablePlayers)
                view.Notables.Add(new TournamentPreviewState.NotableRow
                {
                    Number = n.UniformNumber.ToString(), Name = n.Name, Sub = n.SchoolName, StatLine = n.StatLine, Blurb = n.Blurb,
                });

            string Write(string tag)
            {
                var views = runner.BuildBlockViews();
                var tables = new List<LeagueCrossTableView>();
                foreach (var b in views) tables.Add(LeagueCrossTable.Build(b));
                var own = views[runner.ManagerBlockIndex];
                var schools = new List<TournamentPamphletHtml.School>();
                foreach (var st in own.Standings)
                {
                    var school = field.Find(f => f.Id == st.SchoolId);
                    var ex = TournamentPreviewBuilder.BuildRosterFor(school, field, berths: 2, stage: PreviewStage.DistrictLeague);
                    var block = new TournamentPreviewState.RosterBlock
                    {
                        SchoolName = ex.SchoolName, TierLetter = ex.Tier.ToString(), Tag = "総合 " + ex.Tier + " ／ " + ex.SeedLabel,
                        Sub = ex.TeamBlurb, StatsCaption = "今大会", Batting = ex.Rating.Batting, Pitching = ex.Rating.Pitching,
                        Defense = ex.Rating.Defense, Members = new List<TournamentPreviewState.MemberRow>(),
                    };
                    foreach (var m in ex.Members)
                        block.Members.Add(new TournamentPreviewState.MemberRow
                        {
                            Number = m.UniformNumber.ToString(), Name = m.Name, Grade = m.Grade + "年", Hand = m.HandednessLabel,
                            Stat1 = "—", Stat2 = "—", Stat3 = "—",
                        });
                    schools.Add(new TournamentPamphletHtml.School { Block = block, IsManager = st.IsManager });
                }
                var html = TournamentPamphletHtml.RenderLeague(view, tables, schools);
                System.IO.Directory.CreateDirectory("out");
                System.IO.File.WriteAllText("out/pamphlet-league-" + tag + ".html", html, new UTF8Encoding(false));

                check(Count(html, "<div class=\"blk") == views.Count, tag + ": 全ブロックの星取表がある（" + views.Count + "）");
                check(html.IndexOf("<div class=\"blk own\">") == html.IndexOf("<div class=\"blk"), tag + ": 自校ブロックが先頭");
                check(Count(html, "<article class=\"school") == own.Standings.Count,
                    tag + ": メンバー表は自校ブロックの校数だけ（" + own.Standings.Count + "）");
                check(Count(html, "<ol class=\"idx-schools\">") == 1 && Count(html, "<li><a href=\"#s-") + Count(html, "<li class=\"own\"><a href=\"#s-") == own.Standings.Count,
                    tag + ": 索引は自校ブロックの学校だけ");
                check(!html.Contains("トーナメント") && !html.Contains("優勝") && !html.Contains("シード"),
                    tag + ": 「トーナメント」「優勝」「シード」の語が出ない");
                check(html.Contains("<h2>ブロック戦績</h2>") && html.Contains("<h2>有力校</h2>"), tag + ": 見出し（ブロック戦績・有力校）");
                check(html.Contains("<tr class=\"own\">"), tag + ": 自校の行を強調");
                check(Count(html, "<td class=\"xs\">／</td>") == field.Count, tag + ": 対角の斜線が校数ぶん");
                check(!html.Contains("<script") && !html.Contains("@font-face") && !html.Contains("<link") && !html.Contains("<img")
                      && !html.Contains("http"),
                    tag + ": 外部リソース参照なし");
                return html;
            }

            runner.PlayNextPlayerMatch();   // 1節だけ消化（途中）
            var mid = Write("mid");
            check(mid.Contains("○") && mid.Contains("<td class=\"xc\"></td>"), "mid: 消化済み（○●）と未消化（空欄）が混在");
            check(mid.Contains(">進出<"), "mid: 進出圏の印（文字）が出る");
            while (!runner.Finished) runner.PlayNextPlayerMatch();
            var end = Write("end");
            check(!end.Contains("<td class=\"xc\"></td>"), "end: 全節消化で空欄が無い");
            check(Count(end, ">進出<") == runner.BuildBlockViews().Count * 2, "end: 進出は各ブロック2校");
        }

        private static int Count(string s, string sub)
        {
            var n = 0;
            for (var i = s.IndexOf(sub, System.StringComparison.Ordinal); i >= 0; i = s.IndexOf(sub, i + sub.Length, System.StringComparison.Ordinal)) n++;
            return n;
        }
    }
}
