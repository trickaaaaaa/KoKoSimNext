using System;
using UnityEngine;
using UnityEngine.UIElements;
using KokoSim.Unity.Components;   // 部品辞書（NumUnit）
using KokoSim.Unity.Shell;        // SaveSlots / NationService / ScreenRouter

namespace KokoSim.Unity.Saves
{
    /// <summary>
    /// セーブスロット選択画面（issue #508・オーナー承認の案2 リスト＋詳細ペイン）。<b>手動3枠</b>を
    /// 並べ、選択枠の詳細（学校名・年度・週・保存日時）と読込／削除を出す
    /// （オート枠は廃止・監督決定 2026-08-09）。
    ///
    /// <para>タイトルの「つづきから」とホームのメニュー「セーブ」の2箇所から同じ画面を開く（issue #509）。
    /// <see cref="SaveSlots.Entry"/> で分岐する: タイトル経由（<see cref="SaveSlotsEntry.Title"/>）は
    /// 「今保存すべき進行状態が無い」ため読込／削除だけ（オーナー判断・issue #508）。ホーム経由
    /// （<see cref="SaveSlotsEntry.Home"/>）は保存（大会中は不可）と「戻る」も出す。</para>
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class SaveSlotsController : MonoBehaviour
    {
        private VisualElement _root;
        private VisualElement _confirm;
        private string _selected;
        private string _errorText = "";
        private bool _fromHome;

        private Button _loadBtn, _deleteBtn, _confirmYes, _confirmNo, _saveBtn, _backBtn;

        private void OnEnable()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;
            _confirm = _root.Q<VisualElement>("ss-confirm");
            _fromHome = SaveSlots.Entry == SaveSlotsEntry.Home;

            _loadBtn = Wire("ss-load", OnLoad);
            _deleteBtn = Wire("ss-delete", OnDeleteRequest);
            _confirmYes = Wire("ss-confirm-yes", OnDeleteConfirm);
            _confirmNo = Wire("ss-confirm-no", OnDeleteCancel);
            _saveBtn = Wire("ss-save", OnSave);
            _backBtn = Wire("ss-back", OnBack);
            if (_backBtn != null) _backBtn.style.display = _fromHome ? DisplayStyle.Flex : DisplayStyle.None;

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
            if (_saveBtn != null) _saveBtn.clicked -= OnSave;
            if (_backBtn != null) _backBtn.clicked -= OnBack;
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

        // 存在する最初のスロット（手動1〜3）を既定選択にする。タイトル側は SaveSlots.AnySaveExists()
        // を確認してから遷移するため通常どれかは存在するが、無い場合も手動1へフォールバックして落ちない。
        private static string DefaultSlot()
        {
            foreach (var id in SaveSlots.AllSlotIds)
                if (SaveSlots.ReadInfo(id).Exists) return id;
            return SaveSlots.AllSlotIds[0];
        }

        /// <summary>スクショ・自動検証用に枠を選ぶ（通常は行クリック）。空き枠は選べない。ただしホーム経由
        /// （保存モード）は空き枠も選べる＝新規に保存する先として選べる必要があるため。</summary>
        public void Select(string slotId)
        {
            var info = SaveSlots.ReadInfo(slotId);
            if (!info.Exists && !_fromHome) return;
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

            var selectable = info.Exists || _fromHome;
            if (!selectable)
            {
                row.AddToClassList("ss-row--empty");
                return row;   // 「押しても何も起きない見た目」＝クリックハンドラを配線しない
            }
            if (!info.Exists) row.AddToClassList("ss-row--empty");   // 空きの見た目は保つが保存モードでは選択可

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
            SetText("ss-d-sub", SlotLabel(_selected));

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
                note.text = !string.IsNullOrEmpty(_errorText)
                    ? _errorText
                    : _fromHome && !SaveGameIO.CanSave
                        ? SaveGameIO.SaveDisabledReason()
                        : !info.Exists
                            ? (_fromHome ? "空き枠です。「保存」でここに保存できます。" : "このスロットにはセーブがありません。")
                            : info.Corrupt
                                ? "セーブデータを読み込めません（壊れている、または未対応の版です）。削除はできます。"
                                : "";
                note.style.display = string.IsNullOrEmpty(note.text) ? DisplayStyle.None : DisplayStyle.Flex;
            }

            if (_loadBtn != null) _loadBtn.SetEnabled(info.Exists && !info.Corrupt);
            if (_deleteBtn != null) _deleteBtn.SetEnabled(info.Exists);
            if (_saveBtn != null)
            {
                _saveBtn.style.display = _fromHome ? DisplayStyle.Flex : DisplayStyle.None;
                _saveBtn.SetEnabled(_fromHome && SaveGameIO.CanSave);
            }
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

        /// <summary>ホーム経由（保存モード）のみ配線される。大会中は SetEnabled(false) で既に押せない
        /// 状態だが、経路が増えても落ちないよう再度ガードする（issue #509・オーナー申し送り）。</summary>
        private void OnSave()
        {
            if (!_fromHome || !SaveGameIO.CanSave) return;
            if (!SaveSlots.Save(_selected))
            {
                _errorText = "保存に失敗しました。";
                RenderDetail();
                return;
            }
            _errorText = "";
            Render();   // 一覧・詳細を保存直後の内容（年度/週/保存日時）へ更新して結果を見せる
        }

        private void OnBack()
        {
            if (!_fromHome) return;
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
