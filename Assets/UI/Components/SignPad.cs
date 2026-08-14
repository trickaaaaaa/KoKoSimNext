using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Components
{
    /// <summary>
    /// SignPad（指示パネル。issue #289）: 試合盤面・投手HUD直下に置く采配指示タイル。
    /// 設計書15 Phase C-3 の常駐1行帯の後継で、攻撃中は攻撃指示だけ・守備中は守備指示だけを出す（SetPhase）。
    ///
    /// 定義配列駆動: <see cref="SignDef"/> を1件足せばタイルが1枚増える（画面側のレイアウト変更は不要）。
    /// 将来のアイコン化は Icon 指定でタイルの描画だけが差し替わる想定＝呼び出し側の配線は変わらない。
    /// 排他はグループ単位。既定タイル（IsDefault）の選択は「無点灯」＝指示なしを表す
    /// （設計書15 Q12-2「既定は目立たせない」の視覚言語を維持）。
    /// </summary>
    public sealed class SignPad : VisualElement
    {
        /// <summary>指示1件の定義。増やすときはこれを1件足すだけ（UI原則⑤: 直書き禁止の器）。</summary>
        public sealed class SignDef
        {
            public string Id;
            public string Label;
            /// <summary>
            /// 局面で変わる表示名（null=<see cref="Label"/> 固定）。同じ1枚の意味が塁状況で変わる札
            /// （盗塁→三盗→本盗・issue #622）のために、<see cref="TacticsCardDeck.RefreshEnabled"/> が
            /// 可否と一緒に文字も引き直す。
            /// </summary>
            public Func<string> LabelOf;
            /// <summary>将来のアイコン化用（null=文字タイル）。指定時はアイコンを主表示に回す。</summary>
            public string Icon;
            /// <summary>true=攻撃中に出す指示 / false=守備中に出す指示。</summary>
            public bool Offense;
            /// <summary>排他グループ（例: "batting" / "policy"）。同グループ内で1つだけ選べる。</summary>
            public string Group;
            public string GroupLabel;
            /// <summary>既定（任せ/通常）。選択すると同グループの点灯が消える＝指示なし。</summary>
            public bool IsDefault;
            /// <summary>有効条件（null=常に有効）。走者・カウント等の局面条件を将来ここへ。</summary>
            public Func<bool> EnabledIf;
            public Action OnSelect;
            /// <summary>
            /// 選択を解いたときに呼ぶ（<see cref="TacticsCardDeck"/> のトグル用。null=解除操作なし）。
            /// 「解除するまで続く方針」のカードは、既定タイルを別に置く代わりに同じカードをもう一度押して消す。
            /// </summary>
            public Action OnClear;
        }

        private readonly Label _phaseLabel;
        private readonly VisualElement _groupsHost;
        private IReadOnlyList<SignDef> _defs = Array.Empty<SignDef>();
        // グループ→選択中の非既定タイルId（既定選択はエントリなし＝無点灯）。フェーズ切替でも保持する。
        private readonly Dictionary<string, string> _selected = new();
        private readonly Dictionary<string, Label> _tiles = new();   // 現在表示中のタイル（Id→要素）
        private bool _offense;
        private bool _built;

        public SignPad()
        {
            AddToClassList("signpad");

            var head = new VisualElement();
            head.AddToClassList("signpad__head");
            var title = new Label("指示") { pickingMode = PickingMode.Ignore };
            title.AddToClassList("signpad__title");
            head.Add(title);
            _phaseLabel = new Label("") { pickingMode = PickingMode.Ignore };
            _phaseLabel.AddToClassList("signpad__phase");
            head.Add(_phaseLabel);
            Add(head);

            _groupsHost = new VisualElement();
            Add(_groupsHost);
        }

        /// <summary>指示の定義一式を差し替える（初期化・将来の種類追加）。</summary>
        public void SetDefs(IReadOnlyList<SignDef> defs)
        {
            _defs = defs ?? Array.Empty<SignDef>();
            _built = false;
            Rebuild();
        }

        /// <summary>攻守フェーズを切り替える（攻撃中=攻撃指示だけ／守備中=守備指示だけを表示）。</summary>
        public void SetPhase(bool offense)
        {
            if (_built && _offense == offense) return;
            _offense = offense;
            Rebuild();
        }

        /// <summary>プログラム選択（スクショハーネス・クリックと同じ経路で OnSelect も呼ぶ）。</summary>
        public void Select(string id)
        {
            SignDef def = null;
            foreach (var d in _defs) if (d.Id == id) { def = d; break; }
            if (def == null) return;
            if (def.IsDefault) _selected.Remove(def.Group);
            else _selected[def.Group] = def.Id;
            def.OnSelect?.Invoke();
            RefreshHighlights();
        }

        /// <summary>全グループを既定（無点灯）へ戻す。OnSelect は呼ばない（1球消費後の自動解除用）。</summary>
        public void ClearSelection()
        {
            _selected.Clear();
            RefreshHighlights();
        }

        private void Rebuild()
        {
            _built = true;
            _phaseLabel.text = _offense ? "攻撃" : "守備";
            _groupsHost.Clear();
            _tiles.Clear();

            // 定義順を保ったままグループへまとめる（グループ初出順に行を作る）。
            var rows = new Dictionary<string, VisualElement>();
            foreach (var def in _defs)
            {
                if (def.Offense != _offense) continue;
                if (!rows.TryGetValue(def.Group, out var row))
                {
                    row = new VisualElement();
                    row.AddToClassList("signpad__group");
                    var lab = new Label(def.GroupLabel ?? "") { pickingMode = PickingMode.Ignore };
                    lab.AddToClassList("signpad__grouplab");
                    row.Add(lab);
                    rows[def.Group] = row;
                    _groupsHost.Add(row);
                }
                row.Add(BuildTile(def));
            }
            RefreshHighlights();
        }

        private Label BuildTile(SignDef def)
        {
            // アイコン化（将来）: Icon があればそれを主表示にする。タイル生成だけがこの差を知る。
            var tile = new Label(string.IsNullOrEmpty(def.Icon) ? def.Label : def.Icon)
            {
                pickingMode = PickingMode.Position,
            };
            tile.AddToClassList("signpad__tile");
            var enabled = def.EnabledIf?.Invoke() ?? true;
            if (!enabled) tile.AddToClassList("signpad__tile--off");
            tile.RegisterCallback<ClickEvent>(_ =>
            {
                if (tile.ClassListContains("signpad__tile--off")) return;
                Select(def.Id);
            });
            _tiles[def.Id] = tile;
            return tile;
        }

        private void RefreshHighlights()
        {
            foreach (var (id, tile) in _tiles)
            {
                SignDef def = null;
                foreach (var d in _defs) if (d.Id == id) { def = d; break; }
                var on = def != null && !def.IsDefault
                         && _selected.TryGetValue(def.Group, out var sel) && sel == id;
                tile.EnableInClassList("signpad__tile--on", on);
            }
        }
    }
}
