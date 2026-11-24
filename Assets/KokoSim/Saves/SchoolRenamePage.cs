using System;
using System.Collections.Generic;
using KokoSim.Engine.Nation;
using KokoSim.Unity.Shell;   // NationService
using UnityEngine.UIElements;

namespace KokoSim.Unity.Saves
{
    /// <summary>
    /// 校名の編集（issue #946・監督決定のA案）。システム画面（SaveSlots）の中のもう1ページ。
    ///
    /// <para>左＝県の切り替え＋その県の学校一覧（自校は先頭に固定・改名済みには「改名」の印）、
    /// 右＝選んだ学校の今の名前・元の名前・新しい名前の入力欄（幹だけ入力し「高校」は固定表示）・元に戻す／確定。
    /// 改名そのものと「過去の記録も今の名前で出す」は <see cref="NationService.RenameSchool"/> /
    /// <see cref="NationService.DisplayName"/> が持つ＝ここは表示と入力だけ。</para>
    ///
    /// <para>選択の切り替えは行のクラスの付け替えだけで済ませ、一覧は県を替えたときと確定したときだけ作り直す
    /// （入力中に木を作り直すと入力欄のフォーカスが飛ぶ）。</para>
    /// </summary>
    public sealed class SchoolRenamePage
    {
        private const int PrefectureCount = 47;
        private const string RenamedMark = "改名";

        private readonly VisualElement _root;
        private readonly Action _onRenamed;
        private readonly Dictionary<int, VisualElement> _rows = new Dictionary<int, VisualElement>();

        private DropdownField _pref;
        private TextField _name;
        private BrowserNamePicker _browserName;   // ブラウザ版だけ入力欄を候補選択に置き換える（#1323）
        private Button _revert, _confirm;
        private int _prefId;
        private int _selectedId = NationService.ManagerSchoolId;

        /// <param name="onRenamed">確定・元に戻すの後に呼ぶ（トップバーの校名の再描画など）。</param>
        public SchoolRenamePage(VisualElement root, Action onRenamed)
        {
            _root = root;
            _onRenamed = onRenamed;
        }

        /// <summary>いま選んでいる学校（自校は <see cref="NationService.ManagerSchoolId"/>）。</summary>
        public int SelectedSchoolId => _selectedId;

        public void Bind()
        {
            _pref = _root.Q<DropdownField>("sr-pref");
            if (_pref != null)
            {
                var choices = new List<string>(PrefectureCount);
                for (var id = 0; id < PrefectureCount; id++) choices.Add(PrefectureNames.JapaneseOf(id));
                _pref.choices = choices;
                _pref.RegisterValueChangedCallback(OnPrefChanged);
            }
            _name = _root.Q<TextField>("sr-name");
            if (_name != null)
            {
                _name.maxLength = SchoolNames.MaxStemLength;
                _name.RegisterValueChangedCallback(OnInputChanged);
                // ブラウザ版は日本語を打てないので、入力欄を隠して選んだ学校の県の地名から選ぶ（値は入力欄が持つ）
                _browserName = new BrowserNamePicker(_name, _root.Q<VisualElement>("sr-cands"), "tool-btn", "sr-text", "f-display");
            }
            _revert = _root.Q<Button>("sr-revert");
            if (_revert != null) _revert.clicked += OnRevert;
            _confirm = _root.Q<Button>("sr-confirm");
            if (_confirm != null) _confirm.clicked += OnConfirm;
        }

        public void Unbind()
        {
            _pref?.UnregisterValueChangedCallback(OnPrefChanged);
            _name?.UnregisterValueChangedCallback(OnInputChanged);
            if (_revert != null) _revert.clicked -= OnRevert;
            if (_confirm != null) _confirm.clicked -= OnConfirm;
        }

        /// <summary>ページを開く: 自校の県で、自校を選んだ状態から始める。</summary>
        public void Open()
        {
            _selectedId = NationService.ManagerSchoolId;
            SelectPrefecture(NationService.PrefectureId);
        }

        /// <summary>県を切り替える（一覧を作り直す。選択中の学校はそのまま＝右の編集欄は動かない）。</summary>
        public void SelectPrefecture(int prefectureId)
        {
            _prefId = prefectureId;
            _pref?.SetValueWithoutNotify(PrefectureNames.JapaneseOf(prefectureId));
            RenderList();
            RenderDetail();
        }

        /// <summary>学校を選ぶ（行クリック／撮影ハーネス）。</summary>
        public void SelectSchool(int schoolId)
        {
            if (_selectedId == schoolId) return;
            if (_rows.TryGetValue(_selectedId, out var prev)) prev.RemoveFromClassList("row-panel--selected");
            _selectedId = schoolId;
            if (_rows.TryGetValue(_selectedId, out var next)) next.AddToClassList("row-panel--selected");
            RenderDetail();
        }

        /// <summary>入力欄へ文字を入れて確定する（撮影ハーネス用。通常は画面の入力欄と「確定」）。</summary>
        public bool ConfirmInput(string input)
        {
            _name?.SetValueWithoutNotify(input ?? "");
            _browserName?.Sync();
            return Apply(input);
        }

        // ===== 一覧 =====

        private void RenderList()
        {
            var host = _root.Q<VisualElement>("sr-rows");
            if (host == null) return;
            host.Clear();
            _rows.Clear();

            // 自校は県によらず先頭に固定する（自校は全国の外の専用ID＝県の一覧には入っていない）。
            AddRow(host, NationService.ManagerSchoolId);
            foreach (var s in NationService.Nation.InPrefecture(_prefId))
                AddRow(host, s.Id);
        }

        private void AddRow(VisualElement host, int schoolId)
        {
            var row = new VisualElement();
            row.AddToClassList("row-panel");
            row.AddToClassList("row-panel--dense");
            row.AddToClassList("sr-row");
            if (schoolId == _selectedId) row.AddToClassList("row-panel--selected");

            var name = new Label(CurrentName(schoolId));
            name.AddToClassList("sr-row__name");
            name.AddToClassList("f-display");
            row.Add(name);

            if (NationService.IsRenamed(schoolId))
            {
                var mark = new Label(RenamedMark);
                mark.AddToClassList("chip");
                mark.AddToClassList("chip--tag");
                row.Add(mark);
            }

            row.RegisterCallback<ClickEvent>(_ => SelectSchool(schoolId));
            host.Add(row);
            _rows[schoolId] = row;
        }

        // ===== 編集欄 =====

        private void RenderDetail()
        {
            SetText("sr-d-name", CurrentName(_selectedId));
            SetText("sr-d-sub", NationService.PrefectureName(PrefectureOf(_selectedId)));
            SetText("sr-d-gen", GeneratedName(_selectedId));
            _name?.SetValueWithoutNotify(SchoolNames.Stem(CurrentName(_selectedId)));
            _browserName?.SetCandidates(PrefectureOf(_selectedId));
            _browserName?.Sync();
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            var renamed = NationService.IsRenamed(_selectedId);
            _revert?.SetEnabled(renamed);
            if (_confirm == null) return;
            // 確定できるのは「今の名前から変わる」入力だけ。空欄は取り消し＝改名しているときだけ意味がある。
            var ok = SchoolNames.TryNormalizeStem(_name?.value, out var stem);
            var current = SchoolNames.Stem(CurrentName(_selectedId));
            var taken = ok && stem != null && stem != current && NationService.IsNameTaken(_selectedId, stem);
            _confirm.SetEnabled(ok && !taken && (stem == null ? renamed : stem != current));
            var note = _root.Q<Label>("sr-d-note");
            if (note != null) note.style.display = taken ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void OnPrefChanged(ChangeEvent<string> e)
        {
            var id = _pref.choices.IndexOf(e.newValue);
            if (id >= 0) SelectPrefecture(id);
        }

        private void OnInputChanged(ChangeEvent<string> e) => UpdateButtons();

        private void OnConfirm() => Apply(_name?.value);

        private void OnRevert() => Apply("");

        private bool Apply(string input)
        {
            if (!NationService.RenameSchool(_selectedId, input)) return false;
            RenderList();
            RenderDetail();
            _onRenamed?.Invoke();
            return true;
        }

        // ===== 名前の引き方（自校は幹で持つので「高校」を付けて出す） =====

        private static string CurrentName(int schoolId)
        {
            if (schoolId == NationService.ManagerSchoolId) return SchoolNames.Full(NationService.ManagerSchoolName);
            var s = NationService.SchoolById(schoolId);
            return s != null ? s.Name : "";
        }

        private static string GeneratedName(int schoolId)
        {
            if (schoolId == NationService.ManagerSchoolId)
                return SchoolNames.Full(NationService.ManagerSchoolGeneratedName);
            var s = NationService.SchoolById(schoolId);
            return s != null ? s.GeneratedName : "";
        }

        private static int PrefectureOf(int schoolId)
        {
            if (schoolId == NationService.ManagerSchoolId) return NationService.PrefectureId;
            var s = NationService.SchoolById(schoolId);
            return s != null ? s.PrefectureId : NationService.PrefectureId;
        }

        private void SetText(string name, string text)
        {
            var l = _root.Q<Label>(name);
            if (l != null) l.text = text;
        }
    }
}
