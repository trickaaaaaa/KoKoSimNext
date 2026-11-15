using KokoSim.Unity.Components;
using KokoSim.Unity.Shell;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Home
{
    /// <summary>
    /// 年契約の更新モーダル（issue #1034・ワイヤー案B）。契約中の系統を1枚ずつめくって段を選び、
    /// 最後のページで年額合計と部費の見込みを確かめて確定する。状態は <see cref="ContractRenewalService"/>、
    /// ここは描画と入力の中継だけ。ホームの UXML（cr-modal）の中で display の切り替えで開閉する。
    /// </summary>
    public sealed class ContractRenewalModal
    {
        private readonly VisualElement _modal;
        private readonly Label _title;
        private readonly VisualElement _body;
        private readonly Button _back, _next;
        private readonly System.Action _onClosed;

        public ContractRenewalModal(VisualElement root, System.Action onClosed)
        {
            _onClosed = onClosed;
            _modal = root.Q<VisualElement>("cr-modal");
            _title = root.Q<Label>("cr-title");
            _body = root.Q<VisualElement>("cr-body");
            _back = root.Q<Button>("cr-back");
            _next = root.Q<Button>("cr-next");
            if (_back != null) _back.clicked += OnBack;
            if (_next != null) _next.clicked += OnNext;
        }

        public void Dispose()
        {
            if (_back != null) _back.clicked -= OnBack;
            if (_next != null) _next.clicked -= OnNext;
        }

        public bool IsOpen => _modal != null && _modal.resolvedStyle.display != DisplayStyle.None;

        /// <summary>待ちがあれば開く（無ければ何もしない）。開けたら true。</summary>
        public bool OpenIfPending()
        {
            if (_modal == null || !ContractRenewalService.Pending) return false;
            Render();
            _modal.style.display = DisplayStyle.Flex;
            _modal.BringToFront();
            return true;
        }

        private void OnBack()
        {
            ContractRenewalService.Back();
            Render();
        }

        private void OnNext()
        {
            if (ContractRenewalService.BuildView().IsSummary)
            {
                ContractRenewalService.Confirm();
                _modal.style.display = DisplayStyle.None;
                _onClosed?.Invoke();
                return;
            }
            ContractRenewalService.Next();
            Render();
        }

        private void OnSelect(int level)
        {
            ContractRenewalService.Select(level);
            Render();
        }

        private void Render()
        {
            var v = ContractRenewalService.BuildView();
            if (_title != null) _title.text = v.Title;
            if (_body != null)
            {
                _body.Clear();
                if (v.IsSummary) BuildSummary(v); else BuildCard(v);
            }
            if (_back != null) _back.style.display = v.CanGoBack ? DisplayStyle.Flex : DisplayStyle.None;
            if (_next != null) _next.text = v.IsSummary ? "この内容で更新" : "次へ";
        }

        private void BuildCard(ContractRenewalView v)
        {
            var card = v.Card;
            var name = new Label(card.Name);
            name.AddToClassList("cr-name");
            _body.Add(name);

            var sub = new Label(card.CurrentLevel == 0
                ? "いまは契約していません"
                : "いま Lv" + card.CurrentLevel + " ／ 年 " + card.CurrentCost.ToString("0") + "万");
            sub.AddToClassList("cr-sub");
            _body.Add(sub);

            foreach (var o in card.Options)
            {
                var level = o.Level;
                _body.Add(UiComponents.TierChoice(o.Label, o.Cost, o.Effect, o.Selected, () => OnSelect(level)));
            }
        }

        private void BuildSummary(ContractRenewalView v)
        {
            foreach (var r in v.Rows)
            {
                var row = new VisualElement();
                row.AddToClassList("cr-row");
                if (r.Changed) row.AddToClassList("cr-row--changed");

                var name = new Label(r.Name);
                name.AddToClassList("cr-row__name");
                row.Add(name);

                var lv = new Label(r.LevelText);
                lv.AddToClassList("cr-row__lv");
                row.Add(lv);

                row.Add(UiComponents.NumUnit(r.Cost.ToString("0"), "万", r.Changed, "cr-row__cost"));
                _body.Add(row);
            }

            var sep = new VisualElement();
            sep.AddToClassList("cr-sep");
            _body.Add(sep);

            _body.Add(Total("年額合計", v.TotalCost, false));
            _body.Add(Total("部費 年間差引", v.Net, v.Net < 0, signed: true));
            _body.Add(Projection(v));
        }

        private static VisualElement Total(string key, double value, bool warn, bool signed = false)
        {
            var row = new VisualElement();
            row.AddToClassList("cr-row");
            var k = new Label(key);
            k.AddToClassList("cr-row__name");
            row.Add(k);
            var sign = !signed ? "" : value < 0 ? "-" : "+";
            var cost = UiComponents.NumUnit(sign + System.Math.Abs(value).ToString("0"), "万", true, "cr-row__cost");
            if (warn) cost.AddToClassList("cr-warn");
            row.Add(cost);
            return row;
        }

        private static VisualElement Projection(ContractRenewalView v)
        {
            var row = new VisualElement();
            row.AddToClassList("cr-row");
            var k = new Label("部費 見込み");
            k.AddToClassList("cr-row__name");
            row.Add(k);
            row.Add(UiComponents.NumUnit(v.Funds.ToString("0"), "万", false, "cr-row__cost"));
            var arrow = new Label("→");
            arrow.AddToClassList("cr-arrow");
            row.Add(arrow);
            var after = UiComponents.NumUnit(v.ProjectedFunds.ToString("0"), "万", true, "cr-row__cost");
            if (v.ProjectedFunds < v.Funds) after.AddToClassList("cr-warn");
            row.Add(after);
            return row;
        }
    }
}
