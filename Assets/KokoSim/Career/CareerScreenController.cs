using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Career;
using KokoSim.Engine.Career.Draft;
using KokoSim.Engine.Nation;      // Tiers / SchoolRecord / BestResult
using KokoSim.Unity.Components;   // 部品辞書（AbilityRow / RankChip / Timeline）
using KokoSim.Unity.Shell;        // ManagerService / GameSession / DraftService / WeekAdvance
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.CareerMeta
{
    /// <summary>
    /// キャリア画面（設計書06 §3.8・issue #586）。旧「監督」タブ（ManagerScreen）を統合した後継で、
    /// 上段に「今の監督」（指導力・采配育成・名声信頼・通算成績）、下段に「歩み」（キャリア年表・
    /// 教え子名鑑・歴任校）を出す。
    ///
    /// <para>表示専用＝ロジックを持たない（UI-BUILD-METHOD 大原則④）。通算成績は
    /// <see cref="GameSession.Records"/> の既存集計を読むだけで、ここで新しい集計を作らない。</para>
    ///
    /// <para><b>歴任校は現状かならず1行</b>（転任＝設計書04 §1.2 が未実装のため）。転任が入ったら
    /// <see cref="BuildPostings"/> が返す行が増えるだけで、表側は触らなくてよい形にしてある。</para>
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class CareerScreenController : MonoBehaviour
    {
        private VisualElement _root;

        private void OnEnable()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;

            // 全タブ共通の進週処理へ集約（issue #134: 大会モード中はホームへ回送して日送りへ引き継ぐ）。
            var advance = _root.Q<Button>("advance");
            if (advance != null) advance.clicked += () => WeekAdvance.FromSideScreen(Render);

            Render();
        }

        private void Render()
        {
            var m = ManagerService.Manager;
            var record = GameSession.Current.Records.For(NationService.ManagerSchoolId);

            RenderTopBar();
            RenderIdentity(m);

            FillRows("cr-coaching",
                Ability("打撃指導", m.CoachingBatting),
                Ability("投手指導", m.CoachingPitching),
                Ability("守備指導", m.CoachingDefense));

            FillRows("cr-tactics",
                Ability("采配", m.TacticalSense),
                Ability("育成眼", m.TalentEye));

            FillRows("cr-eval",
                Ability("名声", m.Fame),
                Ability("信頼度", m.Trust));

            RenderRecord(record);
            RenderTimeline(record);
            RenderAlumni();
            RenderPostings(record);
        }

        /// <summary>共通トップバーの動的値（週・メーター・自校の総合ランク）を埋める。他画面と同じ単一ソース。</summary>
        private void RenderTopBar()
        {
            ScoreboardStrip.Fill(_root);
            TopBarMeters.Fill(_root);
            var rank = _root.Q<VisualElement>("team-rank");
            if (rank == null) return;
            rank.Clear();
            rank.Add(UiComponents.RankChipLegacy(TeamOverall.GradeOf(RosterService.Active)));
        }

        /// <summary>
        /// 見出しの身元。<b>主役は校名</b>（OPEN-QUESTIONS Q25・2026-08-12 監督決定）。
        ///
        /// <para>設計書16 §3.8 のワイヤーは監督の氏名を主役に置いていたが、<c>Manager.Name</c> は
        /// engine 既定の "監督" のままどこからも設定されない＝氏名を主役にすると見出しに「監督」とだけ出る。
        /// 氏名を決める導線（新規ゲーム設定の入力欄）は<b>足さない</b>と決めたので、見出しは校名に差し替える。
        /// 偽の氏名を勝手に生成はしない（<c>m.Status</c> の教員/フリーは氏名なしでも意味のある事実なので残す）。</para>
        ///
        /// <para>⚠ 転任（設計書04 §1.2）が入ると校名は変わる＝そのときは「歴任校のうち今の1校」に
        /// 意味が変わる。主役の据え直しはその実装とセットで考える。</para>
        /// </summary>
        private void RenderIdentity(Manager m)
        {
            SetText("cr-name", TopBarMeters.SchoolDisplayName());
            SetText("cr-status", m.Status == ManagerStatus.Teacher ? "教員監督" : "フリー監督");
            SetText("cr-post", "監督就任" + TenureYears + "年目");
        }

        // ── 通算成績 ────────────────────────────────────────────

        private void RenderRecord(SchoolRecord r)
        {
            var games = r.OfficialWins + r.OfficialLosses;
            var pct = games > 0 ? ((double)r.OfficialWins / games).ToString("0.000").TrimStart('0') : "―";
            var koGames = r.KoshienWins + r.KoshienLosses;

            FillRows("cr-record",
                RecordRow("公式戦", r.OfficialWins + "勝 " + r.OfficialLosses + "敗", games > 0),
                RecordRow("勝率", pct, games > 0),
                RecordRow("甲子園出場",
                    "夏" + r.SummerAppearances + "回・春" + r.SpringAppearances + "回",
                    r.TotalAppearances > 0),
                RecordRow("甲子園成績", r.KoshienWins + "勝 " + r.KoshienLosses + "敗", koGames > 0),
                RecordRow("最高成績", BestResultLabel(r.BestResult), r.BestResult != BestResult.None));
        }

        /// <summary>通算成績の1行。記録が無い欄は最弱文字で沈める（UI原則⑥「有るものだけが目に入る」）。</summary>
        private static VisualElement RecordRow(string key, string value, bool has)
        {
            var row = new VisualElement();
            row.AddToClassList("cr-rec");
            var k = new Label(key);
            k.AddToClassList("cr-rec__k");
            row.Add(k);
            var v = new Label(value);
            v.AddToClassList("cr-rec__v");
            if (!has) v.AddToClassList("cr-rec__v--none");
            row.Add(v);
            return row;
        }

        private static string BestResultLabel(BestResult b)
        {
            switch (b)
            {
                case BestResult.Champion: return "全国優勝";
                case BestResult.RunnerUp: return "準優勝";
                case BestResult.RoundOf4: return "ベスト4";
                case BestResult.RoundOf8: return "ベスト8";
                case BestResult.Appearance: return "出場";
                default: return "―";
            }
        }

        // ── キャリア年表 ─────────────────────────────────────────

        /// <summary>
        /// 年表の節点は<b>既にある記録からしか作らない</b>（就任・甲子園出場・ドラフト指名・現在）。
        /// 年ごとの県大会成績は「その年の成績」を残す記録がまだ無いため出さない（新しい集計を作らない・
        /// issue #586 の方針）。転任が入ったら赴任ごとの就任節点が増える。
        /// </summary>
        private void RenderTimeline(SchoolRecord r)
        {
            var nodes = new List<(int Year, int Rank, UiComponents.TimelineNodeData Node)>
            {
                (StartYear, 0, new UiComponents.TimelineNodeData
                {
                    Year = StartYear.ToString(),
                    Title = TopBarMeters.SchoolDisplayName() + " 就任",
                    Note = NationService.PrefectureName(NationService.PrefectureId),
                }),
            };

            // 甲子園出場は「直近の出場年とそのときの何年ぶり何回目」しか記録が無い（SchoolRecord）。
            // 出場した全年を並べるには年ごとの記録が要るので、ここは持っているぶんだけ出す。
            if (r.LastSummerYear is int summer)
                nodes.Add((summer, 1, new UiComponents.TimelineNodeData
                {
                    Year = summer.ToString(),
                    Title = "夏の甲子園 出場",
                    Note = string.IsNullOrEmpty(r.SummerAppearanceLabel) ? null : r.SummerAppearanceLabel,
                }));
            if (r.LastSpringYear is int spring)
                nodes.Add((spring, 1, new UiComponents.TimelineNodeData
                {
                    Year = spring.ToString(),
                    Title = "春の甲子園 出場",
                    Note = string.IsNullOrEmpty(r.SpringAppearanceLabel) ? null : r.SpringAppearanceLabel,
                }));

            foreach (var g in DraftService.History.Records.GroupBy(d => d.Year))
            {
                var names = string.Join("・", g.OrderBy(d => d.Round).Select(d => d.Name));
                nodes.Add((g.Key, 2, new UiComponents.TimelineNodeData
                {
                    Year = g.Key.ToString(),
                    Title = "ドラフト " + g.Count() + "名が指名される",
                    Note = names,
                }));
            }

            // 「現在」は年で並べず必ず末尾に置く。ここが中間へ紛れると縦軸がそこで途切れて
            // 年表が2本に割れて見える（＝Timeline は最後の節点の下に線を引かないため）。
            var ordered = nodes.OrderBy(n => n.Year).ThenBy(n => n.Rank).Select(n => n.Node).ToList();
            ordered.Add(new UiComponents.TimelineNodeData
            {
                Year = CurrentYear.ToString(),
                Title = "（現在）" + TenureYears + "年目",
                Current = true,
            });
            SetText("cr-timeline-sub", ordered.Count + "件");
            FillRows("cr-timeline", UiComponents.Timeline(ordered));
        }

        // ── 教え子名鑑 ───────────────────────────────────────────

        private void RenderAlumni()
        {
            // 新しい年が上（勲章は直近ほど話題になる）。同年は指名順位の上から。
            var records = DraftService.History.Records
                .OrderByDescending(d => d.Year).ThenBy(d => d.Round).ToList();

            var rows = new List<VisualElement>();
            foreach (var d in records)
            {
                var row = new VisualElement();
                row.AddToClassList("cr-al");

                var year = new Label(d.Year.ToString());
                year.AddToClassList("cr-al__year");
                year.AddToClassList("f-num");
                row.Add(year);

                var no = new Label(d.UniformNumber > 0 ? d.UniformNumber.ToString() : "―");
                no.AddToClassList("cr-al__no");
                no.AddToClassList("f-num");
                row.Add(no);

                var name = new Label(d.Name);
                name.AddToClassList("cr-al__name");
                name.AddToClassList("f-display");   // 人名は明朝（書体3役）
                row.Add(name);

                var round = new Label(d.Round + "位指名");
                round.AddToClassList("cr-al__round");
                row.Add(round);

                rows.Add(row);
            }

            FillRows("cr-alumni", rows.ToArray());

            // 空のときは見出し行ごと伏せる（空の表を残さない）。
            var has = rows.Count > 0;
            Show("cr-alumni-head", has);
            SetText("cr-alumni-sub", has ? "プロ入り " + rows.Count + "名" : "");
            var empty = _root.Q<Label>("cr-alumni-empty");
            if (empty == null) return;
            empty.style.display = has ? DisplayStyle.None : DisplayStyle.Flex;
            empty.text = has ? "" : "まだプロへ送り出した教え子はいない。";
        }

        // ── 歴任校 ──────────────────────────────────────────────

        /// <summary>
        /// 歴任した学校の行（転任が入ったら赴任ごとに増える）。今は就任中の1校だけで、その戦績は
        /// 自校の通算戦績と同じ＝赴任が1つしか無いあいだは通算＝この赴任ぶんになる。
        /// </summary>
        private static IReadOnlyList<(string Year, string School, string Term, string Wl, string Ko)>
            BuildPostings(SchoolRecord r) => new[]
        {
            (StartYear + "-",
             TopBarMeters.SchoolDisplayName(),
             TenureYears + "年目",
             r.OfficialWins + "勝 " + r.OfficialLosses + "敗",
             r.TotalAppearances > 0 ? "夏" + r.SummerAppearances + "・春" + r.SpringAppearances : "―"),
        };

        private void RenderPostings(SchoolRecord r)
        {
            var rows = new List<VisualElement>();
            foreach (var p in BuildPostings(r))
            {
                var row = new VisualElement();
                row.AddToClassList("cr-post");
                row.Add(Cell(p.Year, "cr-post__year", numeric: true));
                row.Add(Cell(p.School, "cr-post__school", display: true));
                row.Add(Cell(p.Term, "cr-post__term"));
                row.Add(Cell(p.Wl, "cr-post__wl"));
                row.Add(Cell(p.Ko, "cr-post__ko"));
                rows.Add(row);
            }
            FillRows("cr-posts", rows.ToArray());
        }

        private static Label Cell(string text, string cls, bool numeric = false, bool display = false)
        {
            var label = new Label(text);
            label.AddToClassList(cls);
            if (numeric) label.AddToClassList("f-num");
            if (display) label.AddToClassList("f-display");   // 校名は明朝（書体3役）
            return label;
        }

        // ── 共通 ────────────────────────────────────────────────

        /// <summary>
        /// 就任からの年数（1始まり）。転任が未実装なので<b>年度＝在任年数</b>で足りる。
        /// 転任が入ったらここが「今の赴任の開始年度からの差」になる（呼び出し側は触らない）。
        /// </summary>
        private static int TenureYears => GameClock.YearIndex;

        /// <summary>就任した暦年。</summary>
        private static int StartYear => SeasonClock.SeasonBaseYear;

        /// <summary>今の暦年（4月始まりの繰り上がりを含む）。</summary>
        private static int CurrentYear => SeasonClock.CalendarYear(GameClock.YearIndex, GameClock.Week);

        /// <summary>監督メタ1本ぶんの能力行（0〜99帯）。ランクは全画面共通の Tiers 変換で併記する。</summary>
        private static VisualElement Ability(string label, double value)
        {
            var v = (int)System.Math.Round(value);
            return UiComponents.AbilityRow(new AbilityRowData
            {
                Label = label,
                Value = v.ToString(),
                Pct = (float)(value / 100.0),
                Grade = Tiers.FromStrength(value).ToString(),
            });
        }

        private void FillRows(string hostName, params VisualElement[] rows)
        {
            var host = _root.Q<VisualElement>(hostName);
            if (host == null) return;
            host.Clear();
            foreach (var row in rows) host.Add(row);
        }

        private void SetText(string name, string text)
        {
            var label = _root.Q<Label>(name);
            if (label != null) label.text = text;
        }

        private void Show(string name, bool visible)
        {
            var el = _root.Q<VisualElement>(name);
            if (el != null) el.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }
    }
}
