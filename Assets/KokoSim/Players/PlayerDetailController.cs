using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using KokoSim.Unity.Shell;
using KokoSim.Unity.Components;

namespace KokoSim.Unity.Players
{
    /// <summary>
    /// 選手詳細のコントローラ（設計書06 §3.3、mock「選手詳細」）。
    /// PlayerSelection.Index の1名を PlayerDetailState から整形してバインドする。
    /// 能力バランスは Painter2D で実描画（現在能力から）。成長推移・公式戦成績はエンジン未接続で空状態。
    /// マスターディテール化（issue #131）: 独立画面ではなく、PlayerList と同じ GameObject・同じ
    /// UIDocument（PlayerList.uxml の右ペイン）に同居させる。切替入口は <see cref="RenderForSelection"/>
    /// （PlayerListController が行ホバーのたびに呼ぶ）。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class PlayerDetailController : MonoBehaviour
    {
        private PlayerDetailState _state;
        private VisualElement _root;
        private Button _designateButton;
        private RadarChartView _radar;

        // 成績タブ（issue #77）: 0=能力 / 1=成績。スコープ: 0=通算 / 1=公式戦 / 2=大会別。
        private int _tab;
        private int _scope;
        private PlayerDetailView _view;

        // 能力バランスの半径比（軸ラベルは出さず、左の能力バー一覧が凡例を兼ねる）。
        private const float RadiusFactor = 0.42f;

        // 球種変化チャート（プロスピ風・扇形セクター塗り）は部品辞書へ昇格した（issue #94）。
        // 描画実体は UiComponents 側の PitchChartView。ここは器への差し込みだけを持つ。
        private PitchChartView _pitchChart;

        private void OnEnable()
        {
            _state = new PlayerDetailState();
            _root = GetComponent<UIDocument>().rootVisualElement;

            // 背番号は純数字（index+1）＝コンデンス数字書体（design-16 §2「純数字セルのみ」）。62px の大見出し数字なので f-num-bd。
            _root.Q<Label>("number")?.AddToClassList("f-num-bd");

            // 主将に指名（設計書09 §8）: 新チーム発足時（夏の3年引退直後）だけ受け付ける。
            // 期間外・候補外は非活性にして理由を添えるため、ボタン参照を保持しておく。
            _designateButton = _root.Q<Button>("designate-captain");
            if (_designateButton != null) _designateButton.clicked += () =>
            {
                if (_state.DesignateCaptain(PlayerSelection.Index)) Render();
            };

            // レーダーは部品辞書の共通部品（チーム総合力・練習試合・試合開始前と同一の描画）。
            _radar = new RadarChartView(_root.Q<VisualElement>("radar"), RadiusFactor,
                labelSize: RadarLabelSize.None);

            _pitchChart = new PitchChartView(_root.Q<VisualElement>("pitch-chart"));

            // タブ（能力/成績）・スコープ（通算/公式戦/大会別）の切替（issue #77）。
            WireClick("tab-abilities-btn", () => { _tab = 0; ApplyTab(); });
            WireClick("tab-stats-btn", () => { _tab = 1; ApplyTab(); });
            WireClick("scope-career", () => { _scope = 0; ApplyScope(); });
            WireClick("scope-official", () => { _scope = 1; ApplyScope(); });
            WireClick("scope-tourn", () => { _scope = 2; ApplyScope(); });

            Render();
            ApplyTab();
        }

        /// <summary>
        /// マスターディテール化（issue #131）: 部員名簿（PlayerListController）が行ホバーのたびに呼ぶ入口。
        /// PlayerSelection.Index は呼び出し側が更新済みの前提。能力タブは Render() で再描画されるが、
        /// 成績タブ表示中は RenderStats() も呼ばないと選手を切り替えても数値が古いまま残る。
        /// </summary>
        public void RenderForSelection()
        {
            Render();
            if (_tab == 1) RenderStats();
        }

        private void Render()
        {
            var v = _state.BuildView(PlayerSelection.Index);
            _view = v;

            SetText("number", v.Number);
            SetText("name", v.Name);
            SetDisplay("captain-badge", v.IsCaptain);
            // 既に主将なら指名ボタンを隠す（重複指名の抑止）。
            SetDisplay("designate-captain", !v.IsCaptain);
            // 指名ウィンドウ外・候補外は押せない。理由はボタン脇に添える（設計書09 §8）。
            if (_designateButton != null) _designateButton.SetEnabled(v.CanDesignateCaptain);
            SetDisplay("designate-reason", !v.IsCaptain && !v.CanDesignateCaptain);
            SetText("designate-reason", v.DesignateReason);
            SetText("meta-grade", v.GradeLabel);
            SetText("meta-tb", v.ThrowsBats);
            // 投法・最速のメタ表記は廃止（issue #215）。ストレート最速は球種チャート内の表記で読める（issue #130）。
            // 故障（設計書03 §3.5）: 怪我している時だけ警告色で出す（UI原則②）。
            SetDisplay("meta-injury", v.Injury.Length > 0);
            SetText("meta-injury", v.Injury);

            BuildList("pitcher-abils", v.PitcherAbilities, BuildAbil);
            BuildList("fielder-abils", v.FielderAbilities, BuildAbil);
            BuildList("hidden-list", v.Hidden, BuildHidden);

            // 球種変化チャート（全選手・未習得ならストレートのみ, Issue #93）。空状態文言は廃止した。
            BuildPitchChart(v.Pitches, v.HasPitchData, v.TopVelocityKmh);

            // 特殊能力。
            BuildList("skills-list", v.Skills, BuildSkill);
            SetDisplay("skills-empty", !v.HasSkills);

            // 成績（issue #77）はタブ表示時に RenderStats で描く。

            // レーダー描画データ更新（塗り色は総合ランク連動＝部品辞書の既定）。
            _radar.SetData(v.Radar, v.OverallGrade);
        }

        // ===== 成績タブ（issue #77） =====

        private void WireClick(string name, System.Action onClick)
        {
            var el = _root.Q<Label>(name);
            if (el != null) el.RegisterCallback<ClickEvent>(_ => onClick());
        }

        private void ApplyTab()
        {
            SetDisplay("tab-abilities", _tab == 0);
            SetDisplay("tab-stats", _tab == 1);
            Toggle("tab-abilities-btn", "pd2-tab--on", _tab == 0);
            Toggle("tab-stats-btn", "pd2-tab--on", _tab == 1);
            if (_tab == 1) ApplyScope();
        }

        private void ApplyScope()
        {
            Toggle("scope-career", "pd2-scope__item--on", _scope == 0);
            Toggle("scope-official", "pd2-scope__item--on", _scope == 1);
            Toggle("scope-tourn", "pd2-scope__item--on", _scope == 2);
            RenderStats();
        }

        private void RenderStats()
        {
            if (_view == null) return;
            var isTourn = _scope == 2;
            SetDisplay("scope-panel", !isTourn);
            SetDisplay("tourn-panel", isTourn);

            if (isTourn)
            {
                RenderTournamentRows(_view.TournamentRows);
                SetDisplay("stats-empty", _view.TournamentRows.Count == 0);
                return;
            }

            var s = _scope == 1 ? _view.OfficialStatsFull : _view.CareerStatsFull;
            FillCells("bat-cells", s.Batting);
            FillCells("pit-cells", s.Pitching);
            RenderPitchTypeRows(s.PitchTypeBattingAgainst);
            SetDisplay("bat-card", s.HasBatting);
            SetDisplay("pit-card", s.HasPitching);
            SetDisplay("pitchtype-card", s.PitchTypeBattingAgainst.Count > 0);
            SetDisplay("stats-empty", !s.HasBatting && !s.HasPitching);
        }

        // 球種別被打率（issue #180）: 球種名（見出し）＋打数/安打/本塁打/被打率（統計セル行）。
        // 大会別アーカイブの pd2-trow と同じ構造（部品辞書の外に出ない）。
        private void RenderPitchTypeRows(List<PitchTypeStatRow> rows)
        {
            var box = _root.Q<VisualElement>("pitchtype-rows");
            if (box == null) return;
            box.Clear();
            foreach (var r in rows)
            {
                var row = new VisualElement();
                row.AddToClassList("pd2-trow");

                var head = new VisualElement();
                head.AddToClassList("pd2-trow__head");
                var slot = new Label(r.Label);
                slot.AddToClassList("pd2-trow__slot");
                head.Add(slot);
                row.Add(head);

                var grid = new VisualElement();
                grid.AddToClassList("pd2-statgrid");
                grid.Add(BuildStatCell(new StatCell("被打数", r.AtBats)));
                grid.Add(BuildStatCell(new StatCell("被安打", r.Hits)));
                grid.Add(BuildStatCell(new StatCell("被本塁打", r.HomeRuns)));
                grid.Add(BuildStatCell(new StatCell("被打率", r.Average)));
                row.Add(grid);

                box.Add(row);
            }
        }

        private void FillCells(string container, List<StatCell> cells)
        {
            var box = _root.Q<VisualElement>(container);
            if (box == null) return;
            box.Clear();
            foreach (var c in cells) box.Add(BuildStatCell(c));
        }

        // 1指標セル: ラベル（小）＋値（コンデンス数字・右揃え）。装飾なし（UI原則③）。
        private static VisualElement BuildStatCell(StatCell c)
        {
            var cell = new VisualElement();
            cell.AddToClassList("pd2-statcell");
            var k = new Label(c.Label);
            k.AddToClassList("pd2-statcell__k");
            var val = new Label(c.Value);
            val.AddToClassList("pd2-statcell__v");
            val.AddToClassList("f-num");
            cell.Add(k);
            cell.Add(val);
            return cell;
        }

        private void RenderTournamentRows(List<TournamentStatRow> rows)
        {
            var box = _root.Q<VisualElement>("tourn-rows");
            if (box == null) return;
            box.Clear();
            foreach (var r in rows)
            {
                var row = new VisualElement();
                row.AddToClassList("pd2-trow");

                var head = new VisualElement();
                head.AddToClassList("pd2-trow__head");
                var num = new Label(r.Number);
                num.AddToClassList("pd2-trow__num");
                num.AddToClassList("f-num");
                var slot = new Label(r.Slot);
                slot.AddToClassList("pd2-trow__slot");
                head.Add(num);
                head.Add(slot);
                row.Add(head);

                var bat = new VisualElement();
                bat.AddToClassList("pd2-statgrid");
                foreach (var c in r.Batting) bat.Add(BuildStatCell(c));
                row.Add(bat);

                if (r.HasPitching)
                {
                    var pit = new VisualElement();
                    pit.AddToClassList("pd2-statgrid");
                    pit.AddToClassList("pd2-statgrid--pit");
                    foreach (var c in r.Pitching) pit.Add(BuildStatCell(c));
                    row.Add(pit);
                }
                box.Add(row);
            }
        }

        private void Toggle(string name, string cls, bool on)
        {
            var el = _root.Q(name);
            if (el == null) return;
            if (on) el.AddToClassList(cls); else el.RemoveFromClassList(cls);
        }

        // ===== 行ビルダー =====

        private static VisualElement BuildAbil(AbilityBar a)
            => UiComponents.AbilityRow(new AbilityRowData
            {
                Label = a.Label,
                Value = a.Value.ToString(),
                Pct = a.Pct,
                Grade = a.Grade,
                TypeLabel = a.TypeLabel,
                HideBar = !string.IsNullOrEmpty(a.TypeLabel),
                Divided = true,
            });

        private static VisualElement BuildHidden(HiddenParam h)
        {
            var row = new VisualElement();
            row.AddToClassList("pd2-hidden-row");
            var k = new Label(h.Key); k.AddToClassList("pd2-hidden-k"); row.Add(k);
            var val = new Label(h.Known ? h.Value : "？");
            val.AddToClassList(h.Known ? "pd2-hidden-v" : "pd2-hidden-v--unknown");
            row.Add(val);
            return row;
        }

        // ===== 球種変化チャート（プロスピ風） =====

        // ViewModel の PitchData を部品辞書の PitchChartDatum へ詰め替えて描画部品へ渡す。
        // ストレートの最速は投手能力欄の球速と同じ v.TopVelocityKmh を流用し、値を必ず一致させる（Issue #130/#215）。
        private void BuildPitchChart(List<PitchData> pitches, bool has, int topVelocityKmh)
        {
            _pitchChart?.SetData(has ? PitchData.ToPitchChartData(pitches, topVelocityKmh) : null);
        }

        private static VisualElement BuildSkill(SkillInfo s)
        {
            var wrap = new VisualElement();
            wrap.AddToClassList("pd2-skill");
            var name = new Label(s.Name); name.AddToClassList("pd2-skill__name"); wrap.Add(name);
            var desc = new Label(s.Desc); desc.AddToClassList("pd2-skill__desc"); wrap.Add(desc);
            return wrap;
        }

        // ===== 補助 =====

        private void SetText(string name, string text)
        {
            var l = _root.Q<Label>(name);
            if (l != null) { l.text = text; return; }
            var b = _root.Q<Button>(name);
            if (b != null) b.text = text;
        }

        private void SetDisplay(string name, bool visible)
        {
            var e = _root.Q<VisualElement>(name);
            if (e != null) e.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void BuildList<T>(string container, List<T> items, System.Func<T, VisualElement> builder)
        {
            var box = _root.Q<VisualElement>(container);
            if (box == null) return;
            box.Clear();
            foreach (var it in items) box.Add(builder(it));
        }
    }
}
