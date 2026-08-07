using System;
using UnityEngine;
using UnityEngine.UIElements;
using KokoSim.Unity.Components;   // 部品辞書（NumUnit）
using KokoSim.Unity.Shell;        // SaveSlots / NationService / ScreenRouter

namespace KokoSim.Unity.Saves
{
    /// <summary>
    /// セーブスロット選択画面（issue #508・オーナー承認の案2 リスト＋詳細ペイン）。手動3枠＋オート1枠を
    /// 並べ、選択枠の詳細（学校名・年度・週・保存日時）と読込／削除を出す。
    ///
    /// <para>保存／上書きは今回のUIに出さない（タイトル経由では保存すべき進行状態が無いため。
    /// サービス側は <see cref="SaveSlots.Save"/> に用意済み＝#509 のホーム画面入口で使う）。</para>
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class SaveSlotsController : MonoBehaviour
    {
        private VisualElement _root;
        private VisualElement _confirm;
        private string _selected;
        private string _errorText = "";

        private Button _loadBtn, _deleteBtn, _confirmYes, _confirmNo;

        private void OnEnable()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;
            _confirm = _root.Q<VisualElement>("ss-confirm");

            _loadBtn = Wire("ss-load", OnLoad);
            _deleteBtn = Wire("ss-delete", OnDeleteRequest);
            _confirmYes = Wire("ss-confirm-yes", OnDeleteConfirm);
            _confirmNo = Wire("ss-confirm-no", OnDeleteCancel);

            _selected = DefaultSlot();
            _errorText = "";
            Render();
        }

        // ScreenRouter は同じ GameObject を SetActive で付け外しするだけなので、外さないと
        // 画面往復のたびに多重登録される（HomeDashboardController と同じ作法）。
        private void OnDisable()
        {
            if (_loadBtn != null) _loadBtn.clicked -= OnLoad;
            if (_deleteBtn != null) _deleteBtn.clicked -= OnDeleteRequest;
            if (_confirmYes != null) _confirmYes.clicked -= OnDeleteConfirm;
            if (_confirmNo != null) _confirmNo.clicked -= OnDeleteCancel;
        }

        private Button Wire(string name, Action handler)
        {
            var btn = _root.Q<Button>(name);
            if (btn != null) btn.clicked += handler;
            return btn;
        }

        private void SetText(string name, string text)
        {
            var l = _root.Q<Label>(name);
            if (l != null) l.text = text;
        }

        // 存在する最初のスロット（手動1〜3→オート）を既定選択にする。タイトル側は SaveSlots.AnySaveExists()
        // を確認してから遷移するため通常どれかは存在するが、無い場合も手動1へフォールバックして落ちない。
        private static string DefaultSlot()
        {
            foreach (var id in SaveSlots.AllSlotIds)
                if (SaveSlots.ReadInfo(id).Exists) return id;
            return SaveSlots.AllSlotIds[0];
        }

        /// <summary>スクショ・自動検証用に枠を選ぶ（通常は行クリック）。空き枠は選べない。</summary>
        public void Select(string slotId)
        {
            if (!SaveSlots.ReadInfo(slotId).Exists) return;
            _selected = slotId;
            _errorText = "";
            Render();
        }

        private void Render()
        {
            RenderList();
            RenderDetail();
        }

        private void RenderList()
        {
            var host = _root.Q<VisualElement>("ss-rows");
            if (host == null) return;
            host.Clear();
            foreach (var id in SaveSlots.AllSlotIds)
                host.Add(BuildRow(SaveSlots.ReadInfo(id)));
        }

        private VisualElement BuildRow(SaveSlotInfo info)
        {
            var row = new VisualElement();
            row.AddToClassList("ss-row");

            var slot = new Label(SlotLabel(info.SlotId));
            slot.AddToClassList("ss-row__slot");
            row.Add(slot);

            var school = new Label(SchoolCellText(info));
            school.AddToClassList("f-display");
            school.AddToClassList("ss-row__school");
            row.Add(school);

            var yw = new Label(info.Exists && !info.Corrupt ? $"{info.YearIndex}/{info.Week}" : "");
            yw.AddToClassList("f-num");
            yw.AddToClassList("ss-row__yw");
            row.Add(yw);

            if (!info.Exists)
            {
                row.AddToClassList("ss-row--empty");
                return row;   // 「押しても何も起きない見た目」＝クリックハンドラを配線しない
            }

            if (info.SlotId == _selected) row.AddToClassList("ss-row--on");
            var id = info.SlotId;
            row.RegisterCallback<ClickEvent>(_ => Select(id));
            return row;
        }

        private void RenderDetail()
        {
            var info = SaveSlots.ReadInfo(_selected);

            SetText("ss-d-name", info.Exists
                ? (info.Corrupt ? "読み込めません" : NationService.ManagerSchoolName)
                : "── 空き ──");
            SetText("ss-d-sub", SlotLabel(_selected) + (info.IsAuto ? "（自動保存）" : ""));

            var facts = _root.Q<VisualElement>("ss-d-facts");
            if (facts != null)
            {
                facts.Clear();
                if (info.Exists && !info.Corrupt)
                {
                    facts.Add(FactRow("年度", UiComponents.NumUnit(info.YearIndex.ToString(), "年目")));
                    facts.Add(FactRow("週", UiComponents.NumUnit(info.Week.ToString(), "週")));
                    facts.Add(FactRow("保存日時", DateValue(info.SavedAt)));
                }
            }

            var note = _root.Q<Label>("ss-d-note");
            if (note != null)
            {
                note.text = !info.Exists
                    ? "このスロットにはセーブがありません。"
                    : info.Corrupt
                        ? "セーブデータを読み込めません（壊れている、または未対応の版です）。削除はできます。"
                        : _errorText;
                note.style.display = string.IsNullOrEmpty(note.text) ? DisplayStyle.None : DisplayStyle.Flex;
            }

            if (_loadBtn != null) _loadBtn.SetEnabled(info.Exists && !info.Corrupt);
            if (_deleteBtn != null) _deleteBtn.SetEnabled(info.Exists);
        }

        private static string SchoolCellText(SaveSlotInfo info)
        {
            if (!info.Exists) return "── 空き ──";
            return info.Corrupt ? "（読み込み不可）" : NationService.ManagerSchoolName;
        }

        private static VisualElement FactRow(string label, VisualElement value)
        {
            var row = new VisualElement();
            row.AddToClassList("ss-d__row");
            var k = new Label(label);
            k.AddToClassList("ss-d__row-k");
            row.Add(k);
            row.Add(value);
            return row;
        }

        private static VisualElement DateValue(DateTime savedAt)
        {
            var l = new Label(savedAt.ToString("yyyy/MM/dd HH:mm"));
            l.AddToClassList("f-num");
            return l;
        }

        private static string SlotLabel(string slotId) => slotId switch
        {
            "manual1" => "手動1",
            "manual2" => "手動2",
            "manual3" => "手動3",
            SaveSlots.AutoSlotId => "オート",
            _ => slotId,
        };

        private void OnLoad()
        {
            if (!SaveSlots.Load(_selected, out var error))
            {
                _errorText = string.IsNullOrEmpty(error) ? "読み込みに失敗しました。" : error;
                RenderDetail();
                return;
            }
            // クリック配信中の同期 Show は UITK のイベント木を壊すため遅延切替を使う（ScreenRouter.cs 冒頭コメント参照）。
            ScreenRouter.Instance?.ShowDeferred("HomeDashboard");
        }

        private void OnDeleteRequest()
        {
            if (_confirm == null) return;
            var q = _root.Q<Label>("ss-confirm-q");
            if (q != null) q.text = SlotLabel(_selected) + "を削除しますか？（元に戻せません）";
            _confirm.style.display = DisplayStyle.Flex;
            _confirm.BringToFront();
        }

        private void OnDeleteCancel()
        {
            if (_confirm != null) _confirm.style.display = DisplayStyle.None;
        }

        private void OnDeleteConfirm()
        {
            if (_confirm != null) _confirm.style.display = DisplayStyle.None;
            SaveSlots.Delete(_selected);
            _selected = DefaultSlot();
            _errorText = "";
            Render();
        }
    }
}
