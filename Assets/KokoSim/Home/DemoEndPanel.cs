using KokoSim.Engine.Nation;
using KokoSim.Unity.CareerMeta;
using KokoSim.Unity.Components;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Home
{
    /// <summary>
    /// 体験版の終了画面（#1323・2026-11-24 オーナー決定＝配置A「中央のカード1枚」）。ホームの上に幕を掛け、
    /// 見出し・2年間の成績・製品版の案内とストアへのボタン・閉じる／タイトルへ を出す。
    /// 閉じるとホームに戻って各タブは見て回れる。週送りを押すと <see cref="DemoGate"/> がまた止めてここを出す。
    ///
    /// 成績は既にある記録だけから出す（キャリアタブの通算成績と同じ <see cref="SchoolRecord"/>。体験版は
    /// 赴任1校目の2年間なので通算＝2年間）。新しい集計は作らない。
    /// </summary>
    public sealed class DemoEndPanel
    {
        private readonly VisualElement _root;
        private VisualElement _modal;
        private Button _store, _close, _title;

        public DemoEndPanel(VisualElement root) { _root = root; }

        /// <summary>今開いているか。</summary>
        public bool IsOpen => _modal != null && _modal.resolvedStyle.display == DisplayStyle.Flex;

        public void Bind()
        {
            _modal = _root.Q<VisualElement>("de-modal");
            _store = _root.Q<Button>("de-store");
            _close = _root.Q<Button>("de-close");
            _title = _root.Q<Button>("de-title");
            if (_store != null) _store.clicked += OnStore;
            if (_close != null) _close.clicked += Close;
            if (_title != null) _title.clicked += OnTitle;
        }

        public void Unbind()
        {
            if (_store != null) _store.clicked -= OnStore;
            if (_close != null) _close.clicked -= Close;
            if (_title != null) _title.clicked -= OnTitle;
        }

        public void Show()
        {
            if (_modal == null) return;
            Fill();
            _modal.style.display = DisplayStyle.Flex;
            _modal.BringToFront();
        }

        public void Close()
        {
            if (_modal != null) _modal.style.display = DisplayStyle.None;
        }

        private void Fill()
        {
            var school = _root.Q<Label>("de-school");
            if (school != null) school.text = TopBarMeters.SchoolDisplayName() + "の2年間";

            var rows = _root.Q<VisualElement>("de-rows");
            if (rows != null)
            {
                rows.Clear();
                var r = GameSession.Current.Records.For(NationService.ManagerSchoolId);
                var games = r.OfficialWins + r.OfficialLosses;
                var pct = games > 0 ? ((double)r.OfficialWins / games).ToString("0.000").TrimStart('0') : "―";
                var koshien = KokoSim.Engine.Season.DisplayNames.Current.Koshien;

                rows.Add(Row("公式戦", Text(r.OfficialWins + "勝 " + r.OfficialLosses + "敗　" + pct, games > 0)));
                rows.Add(Row(koshien + "出場",
                    Text("夏" + r.SummerAppearances + "回・春" + r.SpringAppearances + "回", r.TotalAppearances > 0)));
                rows.Add(Row("最高成績",
                    Text(CareerScreenController.BestResultLabel(r.BestResult), r.BestResult != BestResult.None)));

                var overall = TeamOverall.Of(RosterService.Active);
                var team = new VisualElement();
                team.AddToClassList("de-row__v");
                team.Add(UiComponents.RankChipLegacy(Tiers.FromStrength(overall).ToString()));
                var num = new Label(overall.ToString("0"));
                num.AddToClassList("de-num");
                num.AddToClassList("f-num");
                team.Add(num);
                rows.Add(Row("チーム総合", team));

                var fame = new VisualElement();
                fame.AddToClassList("de-row__v");
                fame.Add(UiComponents.RankChipLegacy(Tiers.FromStrength(ManagerService.Manager.Fame).ToString()));
                rows.Add(Row("名声", fame));
            }

            // ストアページができるまではボタンを出さない（DemoGate.StoreUrl が空）
            if (_store != null)
                _store.style.display = string.IsNullOrEmpty(DemoGate.StoreUrl) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private static VisualElement Row(string key, VisualElement value)
        {
            var row = new VisualElement();
            row.AddToClassList("de-row");
            var k = new Label(key);
            k.AddToClassList("de-row__k");
            k.AddToClassList("f-body");
            row.Add(k);
            row.Add(value);
            return row;
        }

        /// <summary>値の文字。記録が無い欄は最弱文字で沈める（キャリアタブの通算成績と同じ扱い）。</summary>
        private static VisualElement Text(string value, bool has)
        {
            var v = new Label(value);
            v.AddToClassList("de-row__v");
            v.AddToClassList("f-num");
            if (!has) v.AddToClassList("de-row__v--none");
            return v;
        }

        private static void OnStore()
        {
            if (!string.IsNullOrEmpty(DemoGate.StoreUrl)) Application.OpenURL(DemoGate.StoreUrl);
        }

        private void OnTitle()
        {
            Close();
            TitleReturn.Run(_root);   // システムタブの「タイトルへ戻る」と同じ手順
        }
    }
}
