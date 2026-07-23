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

            var back = _root.Q<Button>("back-list");
            if (back != null) back.clicked += () => FindObjectOfType<ScreenRouter>()?.Show("PlayerList");

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

        private void Render()
        {
            var v = _state.BuildView(PlayerSelection.Index);
            _view = v;

            SetText("number", v.Number);
            SetText("name", v.Name);
            SetText("cond", v.Condition);
            SetColor("cond", v.ConditionColorHex);
            // 調子は表情顔（ConditionFace）が主。文字表記は詳細画面なので併記する（issue #51）。
            var condFaceHost = _root.Q<VisualElement>("cond-face");
            if (condFaceHost != null)
            {
                condFaceHost.Clear();
                var face = new ConditionFace();
                face.Set(v.ConditionLevel);
                condFaceHost.Add(face);
            }
            SetDisplay("captain-badge", v.IsCaptain);
            // 既に主将なら指名ボタンを隠す（重複指名の抑止）。
            SetDisplay("designate-captain", !v.IsCaptain);
            // 指名ウィンドウ外・候補外は押せない。理由はボタン脇に添える（設計書09 §8）。
            if (_designateButton != null) _designateButton.SetEnabled(v.CanDesignateCaptain);
            SetDisplay("designate-reason", !v.IsCaptain && !v.CanDesignateCaptain);
            SetText("designate-reason", v.DesignateReason);
            SetText("meta-grade", v.GradeLabel);
            SetText("meta-tb", v.ThrowsBats);
            // 投法・最速は全選手で表示（役割でゲートしない, Issue #93）。
            SetText("meta-style", v.PitchStyle);
            SetText("meta-velo", "最速 " + v.TopVelocityKmh + " km/h");
            // 故障（設計書03 §3.5）: 怪我している時だけ警告色で出す（UI原則②）。
            SetDisplay("meta-injury", v.Injury.Length > 0);
            SetText("meta-injury", v.Injury);

            var chip = _root.Q<VisualElement>("overall-chip");
            if (chip != null) { chip.Clear(); chip.Add(UiComponents.RankChip(v.OverallGrade, RankChipSize.Large)); }

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
            SetDisplay("bat-card", s.HasBatting);
            SetDisplay("pit-card", s.HasPitching);
            SetDisplay("stats-empty", !s.HasBatting && !s.HasPitching);
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
        // ストレートの最速だけは meta-velo と同じ v.TopVelocityKmh を流用し、値を必ず一致させる（Issue #130）。
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

        private static Color Hex(string hex)
        {
            return ColorUtility.TryParseHtmlString(hex, out var c) ? c : Color.white;
        }

        private void SetText(string name, string text)
        {
            var l = _root.Q<Label>(name);
            if (l != null) { l.text = text; return; }
            var b = _root.Q<Button>(name);
            if (b != null) b.text = text;
        }

        private void SetColor(string name, string hex)
        {
            var l = _root.Q<Label>(name);
            if (l != null) l.style.color = Hex(hex);
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
