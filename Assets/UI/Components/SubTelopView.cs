using UnityEngine.UIElements;
using KokoSim.Engine.Match.Broadcast;

namespace KokoSim.Unity.Components
{
    /// <summary>
    /// 交代テロップ（issue #920・components.uss の .sub-telop）。1枚＝1チームの同じ間合いの交代。
    /// 中身（種類・打順・背番号・名前・いま就いた位置・退く選手）は engine の <see cref="SubstitutionTelop"/> が組み、
    /// ここは並べるだけ。名前は人名なので明朝、ほかは本文書体。
    /// </summary>
    public sealed class SubTelopView
    {
        private readonly VisualElement _host;
        private readonly Label _team;
        private readonly VisualElement _rows;

        /// <param name="host">UXML の .sub-telop（既定非表示）。中に .sub-telop__panel を自前で組む。</param>
        public SubTelopView(VisualElement host)
        {
            _host = host;
            _host.Clear();
            var panel = new VisualElement();
            panel.AddToClassList("sub-telop__panel");
            panel.pickingMode = PickingMode.Ignore;
            _team = new Label();
            _team.AddToClassList("sub-telop__team");
            _team.AddToClassList("f-display");
            _team.pickingMode = PickingMode.Ignore;
            panel.Add(_team);
            _rows = new VisualElement { pickingMode = PickingMode.Ignore };
            panel.Add(_rows);
            _host.Add(panel);
        }

        public VisualElement Host => _host;

        /// <summary>
        /// 1枚を描いて出す。<paramref name="teamName"/> は交代したチームの校名。
        /// <paramref name="clearBottom"/> は避けたい下の帯（投手HUD）の上端までの高さ[px]。
        /// 負なら USS の既定（--sub-telop-bottom）のまま。
        /// </summary>
        public void Show(TelopCard card, string teamName, float clearBottom = -1f)
        {
            if (clearBottom > 0f && !float.IsNaN(clearBottom)) _host.style.bottom = clearBottom;
            else _host.style.bottom = StyleKeyword.Null;
            _team.text = teamName ?? "";
            _rows.Clear();
            foreach (var r in card.Rows) _rows.Add(Row(r));
            _host.style.display = DisplayStyle.Flex;
            _host.BringToFront();
        }

        public void Hide() => _host.style.display = DisplayStyle.None;

        private static VisualElement Row(TelopRow r)
        {
            var row = new VisualElement { pickingMode = PickingMode.Ignore };
            row.AddToClassList("sub-telop__row");
            row.Add(Text(r.Kind, "sub-telop__kind", "f-body-md"));
            if (r.Order.Length > 0) row.Add(Text(r.Order, "sub-telop__order", "f-body"));
            if (r.Number > 0) row.Add(Text(r.Number.ToString(), "num-badge", "f-num-bd"));
            row.Add(Text(r.Name, "sub-telop__name", "f-display-lg"));
            if (r.Position.Length > 0) row.Add(Text(r.Position, "sub-telop__pos", "f-body-bd"));
            if (r.OutName != null) row.Add(Text("← " + r.OutName, "sub-telop__out", "f-body"));
            return row;
        }

        private static Label Text(string text, string cls, string font)
        {
            var l = new Label(text) { pickingMode = PickingMode.Ignore };
            l.AddToClassList(cls);
            l.AddToClassList(font);
            return l;
        }
    }
}
