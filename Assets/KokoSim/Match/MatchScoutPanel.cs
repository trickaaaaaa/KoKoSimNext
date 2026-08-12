using System.Collections.Generic;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Nation;      // Tiers（ランクの単一ソース）
using KokoSim.Engine.Players;
using KokoSim.Unity.Components;   // 部品辞書（RankChip / TypeChip / SegTabs）
using KokoSim.Unity.Shell;        // LaunchTendencyLabels
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 試合中の選手能力ビューア（issue #602）。<c>scout-modal</c> にバインドする。
    /// タイム（<c>pause-modal</c>）から開き、閉じるとポップアップへ戻る（交代モーダルと同じ構造）。
    ///
    /// <para>監督決定のA案＝<b>両校を左右に並べ</b>、各行の右に能力をランクチップで出す。
    /// タブ往復なしで「相手打線の並び」と「自校ベンチの持ち駒」を同時に見比べられる。
    /// ［野手能力］［投手能力］のタブは<b>両校まとめて</b>列を切り替える（役割でゲートしない・issue #93/#381
    /// ＝控えの誰が継投候補かはプレイヤーが決める）。</para>
    ///
    /// <para><b>開示度</b>: 相手校も自校と同じく数値まで全開示する（見極め/Insight によるぼかしはしない・
    /// issue #602 確認済み仕様）。</para>
    ///
    /// <para><b>読むだけ</b>＝エンジンに一切触れない。<see cref="MatchProgression.SubstitutionOptions"/> は
    /// 観測データを組んで返すだけで状態を変えず、乱数も引かない＝伝令は減らず、開閉が試合結果に影響しない
    /// （不変条件#2）。能力の列・並び・ラベルは <see cref="MatchAbilityColumns"/> が単一ソース。</para>
    /// </summary>
    public sealed class MatchScoutPanel
    {
        private readonly VisualElement _root;

        private VisualElement _scrim, _tabs, _left, _right;
        private Label _situation;

        private MatchProgression _prog;
        private bool _managerIsAway;
        private string _awayName = "", _homeName = "";
        private bool _open;

        /// <summary>0＝野手能力／1＝投手能力。両校まとめて切り替える。</summary>
        private int _tab;

        /// <summary>局面テキスト（ポップアップと同じ文言）を供給する。</summary>
        private readonly System.Func<string> _situationText;

        public MatchScoutPanel(VisualElement root, System.Func<string> situationText = null)
        {
            _root = root;
            _situationText = situationText;
        }

        /// <summary>開いている間は裏の進行（自動再生・次の打席へ）を止める（交代モーダルと同じ扱い）。</summary>
        public bool IsOpen => _open;

        public void Bind()
        {
            _scrim = _root.Q<VisualElement>("scout-modal");
            _tabs = _root.Q<VisualElement>("mscout-tabs");
            _left = _root.Q<VisualElement>("mscout-left");
            _right = _root.Q<VisualElement>("mscout-right");
            _situation = _root.Q<Label>("mscout-situation");

            var close = _root.Q<Button>("mscout-close");
            if (close != null) close.clicked += Close;
            Close();
        }

        /// <summary>この試合の進行体・自校の攻守・両校の表示名を差し替える（試合ごとに呼ぶ）。</summary>
        public void SetMatch(MatchProgression prog, bool managerIsAway, string awayName, string homeName)
        {
            _prog = prog;
            _managerIsAway = managerIsAway;
            _awayName = awayName ?? "";
            _homeName = homeName ?? "";
            Close();
        }

        public void Open()
        {
            if (_prog == null || _scrim == null) return;
            _open = true;
            _scrim.style.display = DisplayStyle.Flex;
            Render();
        }

        public void Close()
        {
            _open = false;
            if (_scrim != null) _scrim.style.display = DisplayStyle.None;
        }

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

            // 自校＝左、相手＝右。engine の観測 seam は「先攻(away)か」で引くので、自校の攻守から相手を出す。
            RenderTeam(_left, _managerIsAway, own: true);
            RenderTeam(_right, !_managerIsAway, own: false);
        }

        private void RenderTeam(VisualElement host, bool teamIsAway, bool own)
        {
            if (host == null) return;
            host.Clear();

            var opt = _prog.SubstitutionOptions(teamIsAway);
            var columns = _tab == 0 ? MatchAbilityColumns.Fielder : MatchAbilityColumns.Pitcher;

            // 見出し（校名＋自校/相手）。校名は明朝（書体3役）。
            var head = new VisualElement();
            head.AddToClassList("mscout-team__hd");
            var name = new Label(teamIsAway ? _awayName : _homeName);
            name.AddToClassList("mscout-team__name");
            name.AddToClassList("f-display");
            head.Add(name);
            var tag = new Label(own ? "自校" : "相手");
            tag.AddToClassList("chip");
            tag.AddToClassList("chip--tag");
            tag.AddToClassList("mscout-team__tag");
            head.Add(tag);
            host.Add(head);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("mscout-scroll");
            host.Add(scroll);

            scroll.Add(HeadRow(columns));

            // 打線（打順1〜9・交代反映済み）。次打者の行を点灯させる。
            scroll.Add(Section("打線"));
            for (var i = 0; i < opt.Lineup.Count; i++)
            {
                var p = opt.Lineup[i];
                // 「いま出ている選手」＝攻撃中なら次打者・守備中なら現投手。守備側の打順は回っていないので
                // 次打者を点灯させても意味が無い（点けるとどの行が今かを誤読させる）。
                var isNow = opt.IsOffense
                    ? i == opt.UpcomingBatterSlot
                    : ReferenceEquals(p, opt.CurrentPitcher);
                scroll.Add(Row(p, columns, order: (i + 1).ToString(), current: isNow));
            }

            // 現投手は DH制（打順外）のときだけ独立の区分で出す。非DH制では打順9人の中に居るので、
            // ここに出すと同じ選手が2行に出る（打線の行を点灯させれば足りる）。
            if (opt.PitcherSlot < 0)
            {
                scroll.Add(Section("投手"));
                scroll.Add(Row(opt.CurrentPitcher, columns, order: "—", current: true));
            }

            // 控え（役割で分けない1本の名簿・背番号順。使用済みは沈める＝リエントリー禁止が一目で分かる）。
            scroll.Add(Section("控え"));
            foreach (var p in opt.Reserves) scroll.Add(Row(p, columns, order: "—", current: false));
            foreach (var p in opt.UsedReserves) scroll.Add(Row(p, columns, order: "—", current: false, used: true));
            if (opt.Reserves.Count == 0 && opt.UsedReserves.Count == 0)
                scroll.Add(UiComponents.EmptyState("控えはいない。"));
        }

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

        private static VisualElement Row(Player p, IReadOnlyList<AbilityColumn> columns,
            string order, bool current, bool used = false)
        {
            var row = new VisualElement();
            row.AddToClassList("mscout-row");
            if (current) row.AddToClassList("mscout-row--now");
            if (used) row.AddToClassList("mscout-row--used");

            row.Add(Cell(order, "msc__ord", numeric: true));
            row.Add(Cell(p == null ? "—" : MatchLiveStatsProvider.PosAbbr(p.Position), "msc__pos"));
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
                    // 実寸は単位まで出す（「136」だけでは何の量か読めない）。Oswald は欧文専用なので
                    // 数字と単位で Label を分ける（書体3役ルール・設計書16 §2）。
                    cell.AddToClassList("msc__cell--unit");
                    cell.Add(Cell(v.ToString(), "msc__unitnum", numeric: true));
                    cell.Add(Cell("km/h", "msc__unit"));
                    break;
                default:
                    // ランクチップだけを置く（生値は添えない）。20行×11列の一覧に「チップ＋2桁」を
                    // 並べると隣の列とくっついて1つの塊に見え、列がスキャンできなくなる（実測）。
                    // 細かい差が要る比較は交代モーダルの能力比較が担当する。
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

        // ── スクショハーネス用（バッチ撮影から叩く。issue #602 の受け入れ絵） ──
        public void SelectTabForCapture(int tab) { _tab = tab; if (_open) Render(); }
    }
}
