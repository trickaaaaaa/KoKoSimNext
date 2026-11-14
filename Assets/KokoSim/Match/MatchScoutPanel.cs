using System.Collections.Generic;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Nation;      // Tiers（ランクの単一ソース）
using KokoSim.Engine.Players;
using KokoSim.Unity.Components;   // 部品辞書（RankChip / TypeChip / SegTabs / AbilityRow / StatTable / PitchChartView）
using KokoSim.Unity.Shell;        // LaunchTendencyLabels / PlayerCompareTabs
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 試合中の選手能力ビューア（issue #602 → #923 で作り直し）。<c>scout-modal</c> にバインドする。
    /// タイム（<c>pause-modal</c>）から開き、閉じるとポップアップへ戻る（交代モーダルと同じ構造）。
    ///
    /// <para>監督決定のB案＝<b>左に一覧・右に詳細欄</b>（オーダー画面型）。左は［自校］［相手］のタブで校を
    /// 切り替える一覧（打線・DH制の現投手・控え）、右はカーソルを当てた1人の詳細（能力の棒＋ランク・
    /// 持ち球チャート・今日と通算の成績）。何も当てていないときは1番打者を出す（オーダー画面と同じ既定）。
    /// ［野手能力］［投手能力］のタブは一覧の列と詳細欄の中身をまとめて切り替える（役割でゲートしない・#93/#381）。</para>
    ///
    /// <para><b>出さないもの</b>: 守備適性（この画面は判断画面の例外に入っていない）・ゲームが選手に付けた
    /// 本来の守備位置（位置は「いま試合で就いている位置」だけ＝DH の打者は「指」・issue #931）・
    /// 相手校の特殊能力（試合中は開示しない）。何を出すかは engine の <see cref="MatchScoutView"/> が決める。</para>
    ///
    /// <para><b>開示度</b>: 相手校も自校と同じく数値まで全開示する（見極め/Insight によるぼかしはしない・
    /// issue #602 確認済み仕様）。</para>
    ///
    /// <para><b>読むだけ</b>＝エンジンに一切触れない。<see cref="MatchProgression.SubstitutionOptions"/> と
    /// 今日の成績（<see cref="MatchProgression.TodayBattingOf"/> 等）は観測データを組んで返すだけで状態を変えず、
    /// 乱数も引かない＝伝令は減らず、開閉・カーソル移動が試合結果に影響しない（不変条件#2）。
    /// 能力の列・並び・ラベルは <see cref="MatchAbilityColumns"/> が単一ソース。</para>
    /// </summary>
    public sealed class MatchScoutPanel
    {
        private readonly VisualElement _root;
        private readonly MatchLiveStatsProvider _stats;

        private VisualElement _scrim, _tabs, _teams;
        private ScrollView _rowsHost, _detailHost;
        private Label _situation, _school;

        private MatchProgression _prog;
        private bool _managerIsAway;
        private string _awayName = "", _homeName = "";
        private int _awaySchoolId = int.MinValue, _homeSchoolId = int.MinValue;
        private bool _open;

        /// <summary>0＝野手能力／1＝投手能力。一覧の列と詳細欄をまとめて切り替える。</summary>
        private int _tab;

        /// <summary>true＝自校／false＝相手を一覧に出している。</summary>
        private bool _ownTeam = true;

        // 一覧の行と、その見た目（詳細に出している行の印を付け替えるため）。
        private IReadOnlyList<ScoutRosterRow> _rows = new List<ScoutRosterRow>();
        private readonly List<VisualElement> _rowEls = new List<VisualElement>();

        /// <summary>詳細欄に出している行（-1＝まだ当てていない＝1番打者）。</summary>
        private int _shown = -1;

        /// <summary>局面テキスト（ポップアップと同じ文言）を供給する。</summary>
        private readonly System.Func<string> _situationText;

        public MatchScoutPanel(VisualElement root, MatchLiveStatsProvider stats, System.Func<string> situationText = null)
        {
            _root = root;
            _stats = stats;
            _situationText = situationText;
        }

        /// <summary>開いている間は裏の進行（自動再生・次の打席へ）を止める（交代モーダルと同じ扱い）。</summary>
        public bool IsOpen => _open;

        public void Bind()
        {
            _scrim = _root.Q<VisualElement>("scout-modal");
            _tabs = _root.Q<VisualElement>("mscout-tabs");
            _teams = _root.Q<VisualElement>("mscout-teams");
            _school = _root.Q<Label>("mscout-school");
            _rowsHost = _root.Q<ScrollView>("mscout-rows");
            _detailHost = _root.Q<ScrollView>("mscout-detail");
            _situation = _root.Q<Label>("mscout-situation");

            var close = _root.Q<Button>("mscout-close");
            if (close != null) close.clicked += Close;
            Close();
        }

        /// <summary>この試合の進行体・自校の攻守・両校の表示名と校IDを差し替える（試合ごとに呼ぶ）。</summary>
        public void SetMatch(MatchProgression prog, bool managerIsAway, string awayName, string homeName,
            int awaySchoolId, int homeSchoolId)
        {
            _prog = prog;
            _managerIsAway = managerIsAway;
            _awayName = awayName ?? "";
            _homeName = homeName ?? "";
            _awaySchoolId = awaySchoolId;
            _homeSchoolId = homeSchoolId;
            Close();
        }

        public void Open()
        {
            if (_prog == null || _scrim == null) return;
            _open = true;
            _ownTeam = true;   // 開くたびに自校の1番打者から
            _shown = -1;
            _scrim.style.display = DisplayStyle.Flex;
            Render();
        }

        public void Close()
        {
            _open = false;
            if (_scrim != null) _scrim.style.display = DisplayStyle.None;
        }

        // ── いま一覧に出している校 ──────────────────────────────

        // engine の観測 seam は「先攻(away)か」で引くので、自校の攻守から相手を出す。
        private bool TeamIsAway => _ownTeam ? _managerIsAway : !_managerIsAway;
        private int SchoolId => TeamIsAway ? _awaySchoolId : _homeSchoolId;
        private string SchoolName => TeamIsAway ? _awayName : _homeName;

        // ── 描画 ────────────────────────────────────────────────

        private void Render()
        {
            if (_prog == null) return;

            if (_situation != null && _situationText != null) _situation.text = _situationText();

            if (_tabs != null)
            {
                _tabs.Clear();
                _tabs.Add(UiComponents.SegTabs(PlayerCompareTabs.TabLabels, _tab, i => { _tab = i; Render(); }));
            }
            if (_teams != null)
            {
                _teams.Clear();
                _teams.Add(UiComponents.SegTabs(new[] { "自校", "相手" }, _ownTeam ? 0 : 1, i =>
                {
                    var own = i == 0;
                    if (own == _ownTeam) return;
                    _ownTeam = own;
                    _shown = -1;   // 校を替えたらその校の1番打者から
                    Render();
                }));
            }
            if (_school != null) _school.text = SchoolName;

            _rows = MatchScoutView.Roster(_prog.SubstitutionOptions(TeamIsAway));
            if (_shown >= _rows.Count) _shown = -1;
            RenderList();
            RenderDetail();
        }

        private void RenderList()
        {
            if (_rowsHost == null) return;
            _rowsHost.Clear();
            _rowEls.Clear();

            var columns = _tab == 0 ? MatchAbilityColumns.Fielder : MatchAbilityColumns.Pitcher;
            _rowsHost.Add(HeadRow(columns));

            ScoutSection? section = null;
            var shown = ShownIndex();
            for (var i = 0; i < _rows.Count; i++)
            {
                var r = _rows[i];
                if (section != r.Section)
                {
                    section = r.Section;
                    _rowsHost.Add(Section(SectionLabel(r.Section)));
                }
                var el = Row(r, columns);
                el.EnableInClassList("mscout-row--shown", i == shown);
                var index = i;
                // 当てた行を詳細欄に出す。外しても戻さない＝詳細欄へカーソルを動かす途中で別の行を
                // 通っても、最後に当てた1人が残る。
                el.RegisterCallback<PointerEnterEvent>(_ => Show(index));
                _rowsHost.Add(el);
                _rowEls.Add(el);
            }

            var hasReserves = false;
            foreach (var r in _rows) if (r.Section == ScoutSection.Reserves) { hasReserves = true; break; }
            if (!hasReserves)
            {
                _rowsHost.Add(Section(SectionLabel(ScoutSection.Reserves)));
                _rowsHost.Add(UiComponents.EmptyState("控えはいない。"));
            }
        }

        private void Show(int index)
        {
            if (index == ShownIndex()) return;
            var prev = ShownIndex();
            _shown = index;
            if (prev >= 0 && prev < _rowEls.Count) _rowEls[prev].RemoveFromClassList("mscout-row--shown");
            if (index >= 0 && index < _rowEls.Count) _rowEls[index].AddToClassList("mscout-row--shown");
            RenderDetail();
        }

        /// <summary>詳細欄に出す行の添字（まだ当てていなければ1番打者）。</summary>
        private int ShownIndex()
        {
            if (_shown >= 0 && _shown < _rows.Count) return _shown;
            var def = MatchScoutView.DefaultRow(_rows);
            if (def == null) return -1;
            for (var i = 0; i < _rows.Count; i++)
                if (ReferenceEquals(_rows[i], def)) return i;
            return -1;
        }

        private static string SectionLabel(ScoutSection s) => s switch
        {
            ScoutSection.Lineup => "打線",
            ScoutSection.Pitcher => "投手",
            _ => "控え",
        };

        // ── 詳細欄（右） ────────────────────────────────────────

        private void RenderDetail()
        {
            if (_detailHost == null) return;
            _detailHost.Clear();

            var index = ShownIndex();
            if (index < 0) return;
            var row = _rows[index];
            var p = row.Player;
            var pitcherView = _tab == 1;
            var schoolId = SchoolId;

            var d = MatchScoutView.Detail(row, own: _ownTeam, pitcherView,
                _prog.TodayBattingOf(TeamIsAway, p), _prog.TodayPitchingOf(TeamIsAway, p),
                _stats?.CareerBatting(schoolId, p.SourceId), _stats?.CareerPitching(schoolId, p.SourceId));

            _detailHost.Add(DetailHead(d));

            if (d.ShowSkills)
            {
                var skills = UiComponents.SkillChipsFor(_stats?.OwnSkills(schoolId, p.SourceId), 6, "mscout-detail__skills");
                if (skills != null) _detailHost.Add(skills);
            }

            // 持ち球チャート（投手能力の見方のときだけ・両校）。球速はストレートに添えて出るので、
            // チャートが出るときは能力の行から球速を落とす（交代モーダルと同じ・issue #560）。
            List<PitchChartDatum> chart = null;
            if (d.ShowPitchChart) chart = _stats?.PitchChartOf(schoolId, p);
            var hasChart = chart != null && chart.Count > 0;

            var abil = new VisualElement();
            abil.AddToClassList("mscout-detail__abil");
            foreach (var c in pitcherView ? MatchAbilityColumns.Pitcher : MatchAbilityColumns.Fielder)
            {
                if (c.Kind == AbilityColumnKind.Unit && hasChart) continue;
                abil.Add(AbilityRowOf(c, p));
            }
            _detailHost.Add(abil);

            if (hasChart)
            {
                _detailHost.Add(DetailSection("持ち球"));
                var host = new VisualElement();
                host.AddToClassList("mscout-detail__pitch");
                _detailHost.Add(host);
                new PitchChartView(host, compact: true).SetData(chart);
            }

            _detailHost.Add(DetailSection("成績"));
            var stats = new List<(string, IReadOnlyList<string>)>(d.Stats.Count);
            foreach (var s in d.Stats) stats.Add((s.Label, new[] { s.Today, s.Career }));
            var table = UiComponents.StatTable(d.StatScopes, stats);
            table.AddToClassList("mscout-detail__stats");
            _detailHost.Add(table);
        }

        /// <summary>見出し: 背番号・名前（明朝）、その下に いまの位置／学年／投打 のタグ。</summary>
        private static VisualElement DetailHead(ScoutDetail d)
        {
            var p = d.Player;
            var head = new VisualElement();
            head.AddToClassList("mscout-detail__head");

            var nameRow = new VisualElement();
            nameRow.AddToClassList("mscout-detail__namerow");
            if (p.UniformNumber > 0)
            {
                var no = new Label(p.UniformNumber.ToString());
                no.AddToClassList("mscout-detail__no");
                no.AddToClassList("f-num-bd");
                nameRow.Add(no);
            }
            var name = new Label(p.Name);
            name.AddToClassList("cmp-card__name");
            name.AddToClassList("f-display");   // 人名は明朝（書体3役）
            nameRow.Add(name);
            head.Add(nameRow);

            var meta = new VisualElement();
            meta.AddToClassList("cmp-card__meta");
            // 位置は「いま試合で就いている位置」だけ（控えは無し）。ゲームが付けた本来の位置は出さない（#931）。
            if (d.Position is { } pos) meta.Add(Tag(MatchLiveStatsProvider.PosJp(pos)));
            if (p.Grade > 0) meta.Add(Tag(p.Grade + "年"));
            meta.Add(Tag(HandednessLabels.Combined(p.Throws, p.Bats)));
            head.Add(meta);
            return head;
        }

        private static Label Tag(string text)
        {
            var l = new Label(text);
            l.AddToClassList("bench-row__tag");
            return l;
        }

        private static Label DetailSection(string text)
        {
            var l = new Label(text);
            l.AddToClassList("mscout-section");
            return l;
        }

        /// <summary>
        /// 能力1行（部品 AbilityRow・行高32pxの組）。優劣軸＝ランクチップ＋棒＋数値。弾道はタイプの段だけ
        /// （優劣が無いので色も棒も付けない・issue #219）。球速は km/h の実寸（ランク化しない・issue #94）。
        /// </summary>
        private static VisualElement AbilityRowOf(AbilityColumn c, Player p)
        {
            var v = c.Get(p);
            var data = new AbilityRowData { Label = c.Label, Size = AbilityRowSize.Compact };
            if (v < 0)
            {
                data.HideBar = true;
                return UiComponents.AbilityRow(data);
            }
            switch (c.Kind)
            {
                case AbilityColumnKind.Type:
                    data.TypeLabel = LaunchTendencyLabels.Jp(v);
                    data.HideBar = true;
                    break;
                case AbilityColumnKind.Unit:
                    data.Value = v.ToString();
                    data.Unit = "km/h";
                    data.HideBar = true;
                    data.KeepChipSlot = true;
                    break;
                default:
                    data.Grade = Tiers.FromStrength(v).ToString();
                    data.Value = v.ToString();
                    data.Pct = v / 100f;
                    break;
            }
            return UiComponents.AbilityRow(data);
        }

        // ── 一覧（左） ──────────────────────────────────────────

        private static VisualElement Section(string text)
        {
            var label = new Label(text);
            label.AddToClassList("mscout-section");
            return label;
        }

        private static VisualElement HeadRow(IReadOnlyList<AbilityColumn> columns)
        {
            var row = new VisualElement();
            row.AddToClassList("mscout-row");
            row.AddToClassList("mscout-row--head");
            row.Add(Cell("打", "msc__ord"));
            row.Add(Cell("位", "msc__pos"));
            row.Add(Cell("No", "msc__no"));
            row.Add(Cell("名前", "msc__name"));
            row.Add(Cell("学", "msc__grade"));
            row.Add(Cell("投打", "msc__hand"));

            var abil = new VisualElement();
            abil.AddToClassList("msc__abil");
            foreach (var c in columns)
            {
                var cell = Cell(c.Head, "msc__cell");
                if (c.Kind == AbilityColumnKind.Unit) cell.AddToClassList("msc__cell--unit");
                abil.Add(cell);
            }
            row.Add(abil);
            return row;
        }

        private static VisualElement Row(ScoutRosterRow r, IReadOnlyList<AbilityColumn> columns)
        {
            var p = r.Player;
            var row = new VisualElement();
            row.AddToClassList("mscout-row");
            if (r.IsNow) row.AddToClassList("mscout-row--now");
            if (r.IsUsed) row.AddToClassList("mscout-row--used");

            row.Add(Cell(r.Order > 0 ? r.Order.ToString() : "—", "msc__ord", numeric: true));
            // 位置は engine が決めた「いま試合で就いている位置」だけ（DH の枠は「指」・控えは無し。issue #931）。
            row.Add(Cell(r.Position is { } pos ? MatchLiveStatsProvider.PosAbbr(pos) : "—", "msc__pos"));
            row.Add(Cell(p != null && p.UniformNumber > 0 ? p.UniformNumber.ToString() : "—", "msc__no", numeric: true));
            row.Add(Cell(p?.Name ?? "—", "msc__name", display: true));
            row.Add(Cell(p != null && p.Grade > 0 ? p.Grade + "年" : "—", "msc__grade"));
            row.Add(Cell(p == null ? "—" : HandednessLabels.Combined(p.Throws, p.Bats), "msc__hand"));

            var abil = new VisualElement();
            abil.AddToClassList("msc__abil");
            foreach (var c in columns) abil.Add(AbilityCell(c, p));
            row.Add(abil);
            return row;
        }

        /// <summary>
        /// 能力1つぶんのセル。優劣軸だけランクチップにする＝弾道（型の段）と球速（実寸）に色を付けない
        /// （色は状態コードなので、優劣の無い値に付けると誤読させる。issue #219 / UI原則②③）。
        /// </summary>
        private static VisualElement AbilityCell(AbilityColumn c, Player p)
        {
            var cell = new VisualElement();
            cell.AddToClassList("msc__cell");
            var v = p == null ? -1 : c.Get(p);

            if (v < 0)
            {
                cell.Add(Cell("—", "msc__none"));
                return cell;
            }

            switch (c.Kind)
            {
                case AbilityColumnKind.Type:
                    // 弾道は1〜5の段（ゴロ型〜フライ型）。中立色の TypeChip で出す。
                    cell.Add(UiComponents.TypeChip(LaunchTendencyLabels.Step(v).ToString()));
                    break;
                case AbilityColumnKind.Unit:
                    // 実寸は単位まで出す（「136」だけでは何の量か読めない）。数字と単位で Label を分ける（書体3役ルール）。
                    cell.AddToClassList("msc__cell--unit");
                    cell.Add(Cell(v.ToString(), "msc__unitnum", numeric: true));
                    cell.Add(Cell("km/h", "msc__unit"));
                    break;
                default:
                    // ランクチップだけを置く（生値は添えない）。一覧に「チップ＋2桁」を並べると隣の列とくっついて
                    // 1つの塊に見え、列がスキャンできなくなる（実測）。数値は右の詳細欄が持つ。
                    cell.Add(UiComponents.RankChip(Tiers.FromStrength(v).ToString(), RankChipSize.Small));
                    break;
            }
            return cell;
        }

        private static Label Cell(string text, string cls, bool numeric = false, bool display = false)
        {
            var label = new Label(text);
            label.AddToClassList(cls);
            if (numeric) label.AddToClassList("f-num");
            if (display) label.AddToClassList("f-display");   // 人名は明朝（書体3役）
            return label;
        }

        // ── スクショハーネス用（バッチ撮影から叩く。issue #602/#923 の受け入れ絵） ──
        public void SelectTabForCapture(int tab) { _tab = tab; if (_open) Render(); }

        public void SelectTeamForCapture(bool own)
        {
            _ownTeam = own;
            _shown = -1;
            if (_open) Render();
        }

        /// <summary>一覧の index 行目（見出し・区分を除いた選手の行）にカーソルを当てた状態にする。</summary>
        public void HoverRowForCapture(int index)
        {
            if (!_open || index < 0 || index >= _rows.Count) return;
            Show(index);
        }
    }
}
