using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Unity.Shell;

namespace KokoSim.Unity.Tournament
{
    /// <summary>
    /// 大会展望の「大会パンフレット」HTML（issue #645・紙面は案B＝左に索引の2段）。
    /// ビューモデル→文字列の純関数。UI（VisualElement）にも IO にも触らない＝書き出しとブラウザ起動は Controller の薄い層。
    /// 単一ファイル完結（CSS インライン・JS/CDN/画像/フォント埋め込みなし）。書体は汎用 serif / sans-serif だけを指定する
    /// （フォントの同梱・指名は再配布問題＝2026-08-12 の件）。配色は design-19「デーゲーム・フルNB」の昼値。
    /// </summary>
    public static class TournamentPamphletHtml
    {
        /// <summary>出場校1校ぶん（登録メンバー表＋索引の1行）。</summary>
        public sealed class School
        {
            public TournamentPreviewState.RosterBlock Block;
            /// <summary>代表区画（甲子園「神奈川県」等）。県大会は空。</summary>
            public string Region = "";
            public bool IsManager;
        }

        /// <summary>紙面用の寸法（画面より文字が小さいぶん詰める）。</summary>
        public static readonly NewspaperBracketMetrics Metrics =
            new NewspaperBracketMetrics(NameWidth: 150, RoundWidth: 56, RowPitch: 22, HeaderHeight: 28, ChampionGap: 44);

        public static string Render(TournamentPreviewState.View view, NewspaperBracket bracket, IReadOnlyList<School> schools)
        {
            var sb = new StringBuilder(64 * 1024);
            sb.Append("<!doctype html>\n<html lang=\"ja\">\n<head>\n<meta charset=\"utf-8\">\n")
              .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
              .Append("<title>").Append(Esc(view.Title)).Append(" 大会パンフレット</title>\n<style>")
              .Append(Css).Append("</style>\n</head>\n<body>\n<div class=\"layout\">\n");

            AppendIndex(sb, schools);

            sb.Append("<main>\n<header class=\"mast\" id=\"top\">\n<div class=\"kicker\">大会展望</div>\n")
              .Append("<h1>").Append(Esc(view.Title)).Append("</h1>\n")
              .Append("<div class=\"meta\">").Append(Esc(view.Meta)).Append("</div>\n</header>\n");
            sb.Append("<section id=\"lead\"><p class=\"lead\">").Append(Esc(view.Lead)).Append("</p></section>\n");

            AppendContenders(sb, view.Contenders);
            AppendNotables(sb, view.Notables);

            sb.Append("<section id=\"bracket\">\n<h2>トーナメント表</h2>\n<div class=\"bracket\">");
            AppendBracketSvg(sb, bracket, schools);
            sb.Append("</div>\n</section>\n");

            AppendSchools(sb, schools);
            sb.Append("</main>\n</div>\n</body>\n</html>\n");
            return sb.ToString();
        }

        // ===== 左の索引（セクション＋出場校。印刷では隠す） =====

        private static void AppendIndex(StringBuilder sb, IReadOnlyList<School> schools)
        {
            sb.Append("<nav class=\"index\">\n<div class=\"idx-h\">索引</div>\n<ul class=\"idx-sec\">")
              .Append("<li><a href=\"#lead\">展望</a></li><li><a href=\"#contenders\">優勝争い</a></li>")
              .Append("<li><a href=\"#notables\">注目選手</a></li><li><a href=\"#bracket\">トーナメント表</a></li>")
              .Append("<li><a href=\"#schools\">出場校</a></li></ul>\n<ol class=\"idx-schools\">\n");
            for (var i = 0; i < schools.Count; i++)
            {
                var s = schools[i];
                sb.Append("<li").Append(s.IsManager ? " class=\"own\"" : "").Append("><a href=\"#s-").Append(i).Append("\">");
                if (s.Region.Length > 0) sb.Append("<span class=\"rg\">").Append(Esc(s.Region)).Append("</span>");
                sb.Append("<span class=\"nm\">").Append(Esc(s.Block.SchoolName)).Append("</span>")
                  .Append(Rank(s.Block.TierLetter)).Append("</a></li>\n");
            }
            sb.Append("</ol>\n</nav>\n");
        }

        // ===== 本文 =====

        private static void AppendContenders(StringBuilder sb, List<TournamentPreviewState.ContenderRow> rows)
        {
            sb.Append("<section id=\"contenders\">\n<h2>優勝争いの構図</h2>\n<table class=\"grid\">\n<thead><tr>")
              .Append("<th>印</th><th class=\"l\">校名</th><th>総合</th><th class=\"l\">シード</th>")
              .Append("<th class=\"n\">打力</th><th class=\"n\">投手</th><th class=\"n\">守備</th><th class=\"l\">寸評</th></tr></thead>\n<tbody>\n");
            foreach (var c in rows)
            {
                sb.Append("<tr><td class=\"mk mk-").Append(Esc(c.MarkClass)).Append("\">").Append(Esc(c.MarkSym))
                  .Append("<small>").Append(Esc(c.MarkLabel)).Append("</small></td>")
                  .Append("<td class=\"l sn\">").Append(Esc(c.Name)).Append("</td>")
                  .Append("<td>").Append(Rank(c.TierLetter)).Append("</td>")
                  .Append("<td class=\"l sub\">").Append(Esc(c.SeedLabel)).Append("</td>")
                  .Append(Num(c.Batting)).Append(Num(c.Pitching)).Append(Num(c.Defense))
                  .Append("<td class=\"l blurb\">").Append(Esc(c.Blurb)).Append("</td></tr>\n");
            }
            sb.Append("</tbody>\n</table>\n</section>\n");
        }

        private static void AppendNotables(StringBuilder sb, List<TournamentPreviewState.NotableRow> rows)
        {
            sb.Append("<section id=\"notables\">\n<h2>注目選手</h2>\n<table class=\"grid\">\n<thead><tr>")
              .Append("<th class=\"n\">背番号</th><th class=\"l\">氏名</th><th class=\"l\">所属</th>")
              .Append("<th class=\"l\">成績</th><th class=\"l\">寸評</th></tr></thead>\n<tbody>\n");
            foreach (var n in rows)
            {
                sb.Append("<tr><td class=\"n num\">").Append(Esc(n.Number)).Append("</td>")
                  .Append("<td class=\"l person\">").Append(Esc(n.Name)).Append("</td>")
                  .Append("<td class=\"l sub\">").Append(Esc(n.Sub)).Append("</td>")
                  .Append("<td class=\"l stat\">").Append(Esc(n.StatLine)).Append("</td>")
                  .Append("<td class=\"l blurb\">").Append(Esc(n.Blurb)).Append("</td></tr>\n");
            }
            sb.Append("</tbody>\n</table>\n</section>\n");
        }

        private static void AppendSchools(StringBuilder sb, IReadOnlyList<School> schools)
        {
            sb.Append("<section id=\"schools\">\n<h2>出場校の登録メンバー（").Append(schools.Count).Append("校）</h2>\n");
            for (var i = 0; i < schools.Count; i++)
            {
                var s = schools[i];
                var r = s.Block;
                // 自校は面で強調（画面の名簿と同じ扱い）。
                sb.Append("<article class=\"school").Append(s.IsManager ? " own" : "").Append("\" id=\"s-").Append(i).Append("\">\n")
                  .Append("<div class=\"sh\">");
                if (s.Region.Length > 0) sb.Append("<span class=\"rg\">").Append(Esc(s.Region)).Append("</span>");
                sb.Append("<h3>").Append(Esc(r.SchoolName)).Append("</h3>").Append(Rank(r.TierLetter))
                  .Append("<span class=\"tag\">").Append(Esc(r.Tag)).Append(" ／ ").Append(Esc(r.StatsCaption)).Append("</span>")
                  .Append("<span class=\"axes\">打力<b>").Append(r.Batting).Append("</b>投手<b>").Append(r.Pitching)
                  .Append("</b>守備<b>").Append(r.Defense).Append("</b></span></div>\n")
                  .Append("<p class=\"blurb\">").Append(Esc(r.Sub)).Append("</p>\n")
                  .Append("<table class=\"grid roster\">\n<thead><tr><th class=\"n\">背番号</th><th class=\"l\">選手</th>")
                  .Append("<th class=\"l\">学年</th><th class=\"l\">投打</th><th class=\"n\">打率/防</th><th class=\"n\">本/回</th>")
                  .Append("<th class=\"n\">点/奪</th></tr></thead>\n<tbody>\n");
                foreach (var m in r.Members)
                {
                    sb.Append("<tr><td class=\"n num\">").Append(Esc(m.Number)).Append("</td>")
                      .Append("<td class=\"l person\">").Append(Esc(m.Name)).Append("</td>")
                      .Append("<td class=\"l\">").Append(Esc(m.Grade)).Append("</td>")
                      .Append("<td class=\"l sub\">").Append(Esc(m.Hand)).Append("</td>")
                      .Append("<td class=\"n num\">").Append(Esc(m.Stat1)).Append("</td>")
                      .Append("<td class=\"n num\">").Append(Esc(m.Stat2)).Append("</td>")
                      .Append("<td class=\"n num\">").Append(Esc(m.Stat3)).Append("</td></tr>\n");
                }
                sb.Append("</tbody>\n</table>\n<a class=\"totop\" href=\"#top\">▲ 先頭へ</a>\n</article>\n");
            }
            sb.Append("</section>\n");
        }

        // ===== トーナメント表（画面と同じ座標を SVG に描く） =====

        private static void AppendBracketSvg(StringBuilder sb, NewspaperBracket b, IReadOnlyList<School> schools)
        {
            if (b.Leaves.Count == 0)
            {
                sb.Append("<p class=\"sub\">トーナメント表はまだ組まれていません。</p>");
                return;
            }
            var anchorOf = new Dictionary<string, int>();
            for (var i = 0; i < schools.Count; i++)
                if (!anchorOf.ContainsKey(schools[i].Block.SchoolName)) anchorOf[schools[i].Block.SchoolName] = i;

            sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append(F(b.Width)).Append("\" height=\"")
              .Append(F(b.Height)).Append("\" viewBox=\"0 0 ").Append(F(b.Width)).Append(' ').Append(F(b.Height)).Append("\">\n");

            foreach (var h in b.Heads)
                sb.Append("<text class=\"hd\" x=\"").Append(F(h.X)).Append("\" y=\"").Append(F(Metrics.HeaderHeight / 2))
                  .Append("\">").Append(Esc(h.Name)).Append("</text>\n");

            // 細線→太線→自校の順に重ねる（画面と同じ）。
            for (var pass = 0; pass < 3; pass++)
                foreach (var p in b.Paths)
                {
                    var won = p.State == BracketLineState.Won;
                    if ((p.IsManager ? 2 : won ? 1 : 0) != pass) continue;
                    sb.Append("<path class=\"ln").Append(won ? " won" : "").Append(p.IsManager ? " fav" : "").Append("\" d=\"M");
                    for (var i = 0; i < p.Points.Count; i++)
                    {
                        if (i > 0) sb.Append(" L");
                        sb.Append(F(p.Points[i].X)).Append(' ').Append(F(p.Points[i].Y));
                    }
                    sb.Append("\"/>\n");
                }

            foreach (var l in b.Leaves)
            {
                var right = l.Side == BracketSide.Right;
                var cls = "leaf" + (l.IsManager ? " fav" : l.IsEliminated ? " out" : "");
                var text = "<text class=\"" + cls + "\" x=\"" + F(l.LineStart.X + (right ? 6 : -6)) + "\" y=\"" + F(l.LineStart.Y)
                           + "\" text-anchor=\"" + (right ? "start" : "end") + "\">" + Esc(l.Name) + "</text>";
                if (anchorOf.TryGetValue(l.Name, out var idx)) sb.Append("<a href=\"#s-").Append(idx).Append("\">").Append(text).Append("</a>\n");
                else sb.Append(text).Append('\n');
            }

            foreach (var s in b.Scores)
            {
                var toLeft = s.Side == BracketSide.Right;
                var cls = "sc" + (s.IsManager ? " fav" : !s.IsWinner ? " out" : "");
                sb.Append("<text class=\"").Append(cls).Append("\" x=\"").Append(F(s.Anchor.X)).Append("\" y=\"").Append(F(s.Anchor.Y))
                  .Append("\" text-anchor=\"").Append(toLeft ? "end" : "start").Append("\">").Append(Esc(s.Text));
                if (s.Mercy) sb.Append("<tspan class=\"mercy\">コ</tspan>");
                sb.Append("</text>\n");
            }

            // 優勝欄。優勝校が決まるまで名前は出さない。
            sb.Append("<text class=\"champ\" x=\"").Append(F(b.ChampionAnchor.X)).Append("\" y=\"").Append(F(b.ChampionAnchor.Y - 8))
              .Append("\"><tspan class=\"cap\">優勝</tspan>");
            if (b.ChampionName != null)
                sb.Append("<tspan class=\"nm").Append(b.ManagerIsChampion ? " fav" : "").Append("\" dx=\"6\">").Append(Esc(b.ChampionName)).Append("</tspan>");
            sb.Append("</text>\n</svg>");
        }

        // ===== 小物 =====

        /// <summary>ランクはレター＋色の併記（色だけに頼らない＝UI原則③）。</summary>
        private static string Rank(string letter)
            => "<span class=\"rk\"><i style=\"background:" + RankPalette.Hex(letter) + "\"></i>" + Esc(letter) + "</span>";

        private static string Num(int v) => "<td class=\"n num\">" + v.ToString(CultureInfo.InvariantCulture) + "</td>";

        private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        /// <summary>HTML エスケープ（校名・選手名・寸評は生成物だが、記号を含みうるので必ず通す）。</summary>
        public static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (var ch in s)
            {
                switch (ch)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&#39;"); break;
                    default: sb.Append(ch); break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// ローカルファイルの file:// URL。パスの各要素を個別に URI エスケープする（空白・日本語）。
        /// macOS の persistentDataPath は「Application Support」を含む＝空白必須で通る道。
        /// Windows のドライブ（C:）はエスケープせず、区切りは / に揃える。
        /// </summary>
        public static string FileUrl(string path)
        {
            var parts = path.Replace('\\', '/').Split('/');
            var sb = new StringBuilder("file://");
            for (var i = 0; i < parts.Length; i++)
            {
                if (i == 0 && parts[0].Length == 0) continue;   // POSIX の先頭 / は区切りとして下で足す
                sb.Append('/');
                var seg = parts[i];
                var isDrive = i == 0 && seg.Length == 2 && seg[1] == ':';
                sb.Append(isDrive ? seg : Uri.EscapeDataString(seg));
            }
            return sb.ToString();
        }

        private const string Css = @"
:root{--base:#F7F5EF;--card:#FFFFFF;--inset:#EFEBDD;--ink:#131512;--tx2:#5B5A50;--tx3:#8A887B;--div:#E4E0D2;--sky:#3E8ED0}
*{box-sizing:border-box}
html{scroll-behavior:smooth}
body{margin:0;background:var(--base);color:var(--ink);font-family:sans-serif;font-size:14px;line-height:1.5}
a{color:inherit}
.layout{display:flex;align-items:flex-start}
nav.index{position:sticky;top:0;flex:none;width:230px;height:100vh;overflow-y:auto;background:var(--card);border-right:2px solid var(--ink);padding:12px 0}
.idx-h{font-family:serif;font-weight:700;font-size:16px;padding:0 14px 6px;border-bottom:2px solid var(--ink)}
.idx-sec,.idx-schools{list-style:none;margin:0;padding:0}
.idx-sec{padding:6px 0;border-bottom:1px solid var(--div)}
.idx-sec a,.idx-schools a{display:flex;align-items:center;gap:6px;padding:3px 14px;text-decoration:none;min-height:26px}
.idx-sec a:hover,.idx-schools a:hover{background:var(--inset)}
.idx-schools .rg{width:64px;flex:none;color:var(--tx2);font-size:12px}
.idx-schools .nm{flex:1;min-width:0;font-family:serif;font-weight:700;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.idx-schools .own a,.school.own{background:var(--inset)}
.idx-schools .own .nm,.school.own h3{color:var(--sky)}
main{flex:1;min-width:0;padding:20px 28px 60px}
.mast{border-bottom:3px solid var(--ink);padding-bottom:10px;margin-bottom:12px}
.kicker{display:inline-block;background:var(--ink);color:var(--card);font-family:serif;font-weight:700;padding:2px 10px;font-size:13px}
h1{font-family:serif;font-weight:900;font-size:30px;margin:6px 0 2px}
.meta{color:var(--tx2)}
h2{font-family:serif;font-weight:700;font-size:20px;margin:28px 0 8px;padding-bottom:4px;border-bottom:2px solid var(--ink)}
.lead{font-size:15px;margin:8px 0}
table.grid{border-collapse:collapse;width:100%;background:var(--card)}
.grid th{font-weight:400;font-size:12px;color:var(--tx2);text-align:center;padding:4px 8px;border-bottom:2px solid var(--ink);white-space:nowrap}
.grid td{padding:0 8px;height:30px;border-bottom:1px solid var(--div);text-align:center;vertical-align:middle}
.grid .l{text-align:left}
.grid .n{text-align:right}
.num{font-variant-numeric:tabular-nums;font-family:sans-serif;font-stretch:condensed}
.sn,.person{font-family:serif;font-weight:700;white-space:nowrap}
.sub{color:var(--tx2);white-space:nowrap}
.blurb{color:var(--tx2);font-size:13px}
.stat{white-space:nowrap}
.mk{font-size:18px;white-space:nowrap}
.mk small{font-size:11px;color:var(--tx2);margin-left:4px}
.rk{display:inline-flex;align-items:center;gap:3px;font-weight:700;font-size:12px}
.rk i{display:inline-block;width:10px;height:10px}
.bracket{overflow-x:auto;background:var(--card);padding:8px}
.bracket svg{display:block}
.bracket text{dominant-baseline:central}
.bracket .hd{font-size:12px;fill:var(--tx2);text-anchor:middle}
.bracket .ln{fill:none;stroke:var(--tx3);stroke-width:1.2}
.bracket .ln.won{stroke:var(--ink);stroke-width:3}
.bracket .ln.fav{stroke:var(--sky)}
.bracket .leaf{font-family:serif;font-weight:700;font-size:13px;fill:var(--ink)}
.bracket a:hover .leaf{fill:var(--sky)}
.bracket .leaf.out,.bracket .sc.out{fill:var(--tx3)}
.bracket .leaf.fav,.bracket .sc.fav,.bracket .nm.fav{fill:var(--sky)}
.bracket .sc{font-size:13px;font-variant-numeric:tabular-nums;fill:var(--ink)}
.bracket .mercy{font-size:10px;fill:var(--tx2)}
.bracket .champ{text-anchor:middle;font-size:14px}
.bracket .champ .cap{font-size:11px;fill:var(--tx2)}
.bracket .champ .nm{font-family:serif;font-weight:700;fill:var(--ink)}
.school{display:block;margin:0 0 18px;padding:10px 12px;background:var(--card)}
.sh{display:flex;align-items:center;flex-wrap:wrap;gap:10px}
.sh .rg{color:var(--tx2);font-size:12px}
.sh h3{font-family:serif;font-weight:700;font-size:18px;margin:0}
.sh .tag{color:var(--tx2);font-size:12px}
.sh .axes{margin-left:auto;font-size:12px;color:var(--tx2)}
.sh .axes b{font-variant-numeric:tabular-nums;color:var(--ink);font-size:14px;margin:0 10px 0 4px}
.school .blurb{margin:4px 0 6px}
.totop{display:inline-block;margin-top:4px;font-size:12px;color:var(--tx2)}
@media (max-width:760px){.layout{display:block}nav.index{position:static;width:auto;height:auto;border-right:0;border-bottom:2px solid var(--ink)}main{padding:12px 16px}}
@media print{nav.index{display:none}.totop{display:none}body{background:#fff}.school{break-inside:avoid}}
";
    }
}
