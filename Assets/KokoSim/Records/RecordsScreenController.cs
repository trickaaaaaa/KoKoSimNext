using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Nation;      // TournamentHistory / SchoolRecord / BestResult / PrefectureNames
using KokoSim.Engine.Season;      // TournamentKind
using KokoSim.Engine.Stats;       // RecordRoom / RecordCategory
using KokoSim.Unity.Components;   // 部品辞書（SegTabs / RecordCell / EmptyState）
using KokoSim.Unity.Shell;        // GameSession / RosterService / WeekAdvance
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Records
{
    /// <summary>
    /// 記録画面（設計書06 §3.9・issue #600）。主語は<b>学校</b>で、キャリア画面（#586・主語＝監督）と
    /// 対になる。監督が代わっても学校に残る記録＝年度別の大会結果と校歴代の個人記録を出す。
    ///
    /// <para>監督決定のC案＝画面内タブ（<see cref="UiComponents.SegTabs"/>）で［歴代大会］［個人記録］を
    /// 排他に切り替える。各ページが全幅を使えるので、個人記録は3位まで出す。</para>
    ///
    /// <para>表示専用＝ロジックを持たない（UI-BUILD-METHOD 大原則④）。順位付け・規定打席の判定は
    /// engine の <see cref="RecordRoom"/> が単一ソースで、ここで集計を書かない。</para>
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class RecordsScreenController : MonoBehaviour
    {
        /// <summary>各部門で出す順位の数（全幅タブなので3位まで置ける）。</summary>
        private const int TopCount = 3;

        private static readonly string[] PageLabels = { "歴代大会", "個人記録" };
        private static readonly string[] ScopeLabels = { "通算", "シーズン" };

        private VisualElement _root;

        // タブの選択状態は画面が持つ（部品は状態を持たない）。週送りや再表示では保たれる。
        private int _page;
        private int _scope;

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
            RenderTopBar();
            RenderIdentity();
            RenderTabs();

            RenderTournamentHistory();
            RenderSchoolSummary(GameSession.Current.Records.For(NationService.ManagerSchoolId));
            RenderPersonalRecords();

            Show("rc-page-tournaments", _page == 0);
            Show("rc-page-records", _page == 1);
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

        private void RenderIdentity()
        {
            SetText("rc-school", TopBarMeters.SchoolDisplayName());
            SetText("rc-pref", NationService.PrefectureName(NationService.PrefectureId));
        }

        private void RenderTabs()
        {
            FillRows("rc-tabs", UiComponents.SegTabs(PageLabels, _page, i => { _page = i; Render(); }));
            FillRows("rc-scope-tabs", UiComponents.SegTabs(ScopeLabels, _scope, i => { _scope = i; Render(); }));
        }

        // ── 歴代大会結果 ─────────────────────────────────────────

        /// <summary>
        /// 年度別の到達ラウンドと優勝校。新しい年が上（直近ほど話題になる）＝同年は夏が上（秋より後の大会）。
        /// 積んだ順は古い順なので、ここで反転する。
        /// </summary>
        private void RenderTournamentHistory()
        {
            var entries = GameSession.Current.History.Entries
                .Select((e, i) => (Entry: e, Index: i))
                .OrderByDescending(x => x.Index)
                .Select(x => x.Entry)
                .ToList();

            var rows = new List<VisualElement>();
            foreach (var e in entries)
            {
                var row = new VisualElement();
                row.AddToClassList("rc-th");
                if (e.ManagerIsChampion) row.AddToClassList("rc-th--own");

                row.Add(Cell(CalendarYear(e.Year).ToString(), "rc-th__year", numeric: true));
                row.Add(Cell(CupName(e.Kind), "rc-th__cup"));

                // 優勝の年は行の面で示す（.rc-th--own）。文字色は足さない＝アクセントを増やさない（UI原則②）。
                row.Add(Cell(TournamentResultLabel.For(e), "rc-th__res"));

                var hasChampion = !string.IsNullOrEmpty(e.ChampionName);
                var champ = Cell(hasChampion ? e.ChampionName : "―", "rc-th__champ", display: true);
                if (!hasChampion) champ.AddToClassList("rc-th__champ--none");
                row.Add(champ);

                rows.Add(row);
            }

            FillRows("rc-th-rows", rows.ToArray());

            // 空のときは見出し行ごと伏せる（空の表を残さない）。
            var has = rows.Count > 0;
            Show("rc-th-head", has);
            SetText("rc-th-sub", has ? rows.Count + "大会" : "");
            FillRows("rc-th-empty", has
                ? new VisualElement[0]
                : new[] { UiComponents.EmptyState("まだ大会を終えていない。", "夏と秋の大会が終わるたびに1行ずつ増える。") });
        }

        /// <summary>
        /// 大会名（「夏 神奈川」）。県は<b>いまの本拠県</b>から引く。転任（設計書04 §1.2）が入ったら
        /// 過去の大会は当時の県で出す必要があるので、そのときに歴代大会結果へ県を焼く。
        /// </summary>
        private static string CupName(TournamentKind kind)
            => (kind == TournamentKind.Summer ? "夏 " : "秋 ")
               + PrefectureNames.ShortOf(NationService.PrefectureId);

        // ── 学校の通算 ───────────────────────────────────────────

        /// <summary>
        /// 学校の通算戦績。<b>新しい集計を作らず</b> <see cref="SchoolRecordBook"/> をそのまま読む
        /// （キャリア画面と同じ記録を、あちらは監督の視点・こちらは学校の視点で出す）。
        /// </summary>
        private void RenderSchoolSummary(SchoolRecord r)
        {
            var games = r.OfficialWins + r.OfficialLosses;
            var pct = games > 0 ? RecordRoom.Rate3((double)r.OfficialWins / games) : "―";
            var koGames = r.KoshienWins + r.KoshienLosses;

            FillRows("rc-summary",
                SummaryRow("公式戦", r.OfficialWins + "勝 " + r.OfficialLosses + "敗", games > 0),
                SummaryRow("勝率", pct, games > 0),
                SummaryRow("甲子園出場",
                    "夏" + r.SummerAppearances + "回・春" + r.SpringAppearances + "回",
                    r.TotalAppearances > 0),
                SummaryRow("直近の甲子園",
                    string.IsNullOrEmpty(r.SummerAppearanceLabel) ? "―" : r.SummerAppearanceLabel,
                    !string.IsNullOrEmpty(r.SummerAppearanceLabel)),
                SummaryRow("甲子園成績", r.KoshienWins + "勝 " + r.KoshienLosses + "敗", koGames > 0),
                SummaryRow("最高成績", BestResultLabel(r.BestResult), r.BestResult != BestResult.None));
        }

        /// <summary>通算の1行。記録が無い欄は最弱文字で沈める（UI原則⑥「有るものだけが目に入る」）。</summary>
        private static VisualElement SummaryRow(string key, string value, bool has)
        {
            var row = new VisualElement();
            row.AddToClassList("rc-sum");
            var k = new Label(key);
            k.AddToClassList("rc-sum__k");
            row.Add(k);
            var v = new Label(value);
            v.AddToClassList("rc-sum__v");
            if (!has) v.AddToClassList("rc-sum__v--none");
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

        // ── 校歴代 個人記録 ───────────────────────────────────────

        /// <summary>
        /// 部門ごとの上位3件。通算は監督のキャリア通算（練習試合を含む全試合）、シーズンは
        /// 大会別アーカイブを「選手×学年」＝1シーズンへ畳んだもの（<see cref="RecordRoom"/>）。
        /// </summary>
        private void RenderPersonalRecords()
        {
            var store = GameSession.Current.Stats;
            var career = _scope == 0;
            var candidates = career
                ? RecordRoom.CareerCandidates(store.Career)
                : RecordRoom.SeasonCandidates(store.Archive);

            var names = NameIndex();
            var rows = new List<VisualElement>();
            var any = false;

            rows.Add(SectionLabel("打撃"));
            foreach (var c in RecordRoom.BattingCategories)
                any |= AddCategoryRow(rows, candidates, c, names, career);

            rows.Add(SectionLabel("投手"));
            foreach (var c in RecordRoom.PitchingCategories)
                any |= AddCategoryRow(rows, candidates, c, names, career);

            FillRows("rc-rr-rows", any ? rows.ToArray() : new VisualElement[0]);
            SetText("rc-rr-sub", career ? "練習試合を含む通算" : "1シーズン（学年ごと）");
            FillRows("rc-rr-empty", any
                ? new VisualElement[0]
                : new[] { UiComponents.EmptyState(
                    career ? "まだ試合をしていない。" : "まだ大会を終えていない。",
                    "規定打席・規定投球回に届いた選手から載る。") });
        }

        /// <summary>1部門ぶんの行を積み、1件でも載ったかを返す。</summary>
        private bool AddCategoryRow(List<VisualElement> rows, IReadOnlyList<RecordCandidate> candidates,
            RecordCategory category, IReadOnlyDictionary<int, string> names, bool career)
        {
            var top = RecordRoom.Top(candidates, category, TopCount);

            var row = new VisualElement();
            row.AddToClassList("rc-rr");
            row.Add(Cell(RecordRoom.LabelOf(category), "rc-rr__cat"));

            for (var i = 0; i < TopCount; i++)
            {
                if (i >= top.Count) { row.Add(UiComponents.RecordCell(null)); continue; }
                var e = top[i];
                row.Add(UiComponents.RecordCell(new UiComponents.RecordCellData
                {
                    Value = e.Display,
                    Name = names.TryGetValue(e.PlayerId, out var n) ? n : "―",
                    // 通算記録は特定の年に紐づかないので年を出さない（シーズン記録だけが年を持つ）。
                    Year = career || e.Year <= 0 ? null : CalendarYear(e.Year).ToString(),
                }));
            }

            rows.Add(row);
            return top.Count > 0;
        }

        private static VisualElement SectionLabel(string text)
        {
            var label = new Label(text);
            label.AddToClassList("rc-rr__section");
            return label;
        }

        /// <summary>成績の帰属キー（DevelopingPlayer.Id）→ 表示名。卒業生も引ける（issue #535）。</summary>
        private static IReadOnlyDictionary<int, string> NameIndex()
        {
            var map = new Dictionary<int, string>();
            foreach (var p in RosterService.Roster) map[p.Id] = p.Name;
            foreach (var p in RosterService.Graduated) map[p.Id] = p.Name;
            return map;
        }

        // ── 共通 ────────────────────────────────────────────────

        /// <summary>
        /// 年度（YearIndex）→ 暦年。engine は暦の起点を持たない（不変条件#3）ので、
        /// 変換は Unity 側の <see cref="SeasonClock"/> に集約する。大会は4〜12月なので年跨ぎは起きない。
        /// </summary>
        private static int CalendarYear(int yearIndex) => SeasonClock.SeasonBaseYear + (yearIndex - 1);

        private static Label Cell(string text, string cls, bool numeric = false, bool display = false)
        {
            var label = new Label(text);
            label.AddToClassList(cls);
            if (numeric) label.AddToClassList("f-num");
            if (display) label.AddToClassList("f-display");   // 校名は明朝（書体3役）
            return label;
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
