using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Nation;
using KokoSim.Unity.Components;   // 部品辞書（UniformColorPicker）
using KokoSim.Unity.Shell;
using KokoSim.Unity.Squad;        // 模式図プレビュー（UniformPreviewElement）
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.NewGame
{
    /// <summary>
    /// 新規ゲーム設定画面（issue #585・B案「上下2段」2026-08-11 監督選定）。
    /// タイトルの「はじめから」から来て、**新設校を作って**ホームへ渡す唯一の入口。
    ///
    /// 決めるのは4つ: 都道府県 / 県内地区（採用県のみ） / 校名 / ユニフォーム配色。
    /// 配色UIは部品辞書の <see cref="UniformColorPicker"/>（チーム設定画面と共有＝同じ実装を2つ作らない）。
    /// 確定は <see cref="SaveGameIO.StartNewSchool"/> の1経路だけを通す（開始手順を画面に散らさない）。
    ///
    /// <para><b>県は全国生成に影響しない</b>（seed=2026 固定・不変条件#2）。選ぶのは「どの県を本拠にするか」だけで、
    /// 全国4000校の顔ぶれ・強さはどの県を選んでも同じ。</para>
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class NewGameSetupController : MonoBehaviour
    {
        /// <summary>校名の上限文字数（UXML の max-length と一致させる）。</summary>
        private const int MaxNameLength = 12;

        /// <summary>
        /// 校名候補として並べる数。校名と同じ行に置くので**折り返さない本数**に抑える
        /// （独立行にすると下段の配色カードから最終2行が溢れて切れる）。
        /// </summary>
        private const int SuggestCount = 6;

        // プレビューの構図はチーム設定画面と同じ（直立の投手を正面から。数値は実測で詰めたもの）。
        private static readonly Vector3 PreviewCamPos = new(-2.35f, 1.30f, 16.85f);
        private static readonly Vector3 PreviewLookAt = new(0f, 1.10f, 18.44f);
        private const float PreviewFrameHeightM = 1.95f;

        private VisualElement _root;
        private readonly UniformColorPicker _picker = new UniformColorPicker();
        private DropdownField _pref;
        private DropdownField _district;
        private Label _districtLabel;
        private TextField _name;
        private VisualElement _suggestHost;
        private Label _summary;
        private Label _error;
        private Button _start;

        private Match.DuelStageRig _rig;
        private Match.DuelViewElement _rigView;
        private UniformPreviewElement _preview;

        private int _prefectureId = NationService.DefaultPrefectureId;
        private int? _districtId;

        private void OnEnable()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;

            _pref = _root.Q<DropdownField>("ngs-pref");
            _district = _root.Q<DropdownField>("ngs-district");
            _districtLabel = _root.Q<Label>("ngs-district-lbl");
            _name = _root.Q<TextField>("ngs-name");
            _suggestHost = _root.Q<VisualElement>("ngs-suggest");
            _summary = _root.Q<Label>("ngs-summary-text");
            _error = _root.Q<Label>("ngs-error");
            _start = _root.Q<Button>("ngs-start");

            BuildPrefectureDropdown();
            BuildPreview(_root.Q<VisualElement>("ngs-preview"));

            _picker.Bind(_root.Q<VisualElement>("ngs-rows"));
            _picker.Changed -= OnColorsChanged;
            _picker.Changed += OnColorsChanged;
            // 初期配色は自校IDから自動割り当てされる色（＝何も選ばなくても破綻しない状態から始める）。
            _picker.SetColors(TeamColorProvider.ManagerColors);

            if (_name != null)
            {
                _name.value = "";
                _name.RegisterValueChangedCallback(_ => Render());
            }
            if (_start != null) _start.clicked += OnStart;

            var hint = _root.Q<Label>("ngs-hint");
            if (hint != null) hint.text = "初年度は1年生 " + RosterService.NewSchoolIntake + " 人だけで始まります";

            ApplyPrefecture(NationService.DefaultPrefectureId);
        }

        private void OnDisable()
        {
            _picker.Changed -= OnColorsChanged;
            if (_start != null) _start.clicked -= OnStart;
            _rig?.Dispose();
            _rig = null;
            _rigView = null;
            _preview = null;
        }

        /// <summary>実物プレビューはポーズを進める（止めるとTポーズ直後の硬い絵で固まる）。</summary>
        private void Update()
        {
            if (_rig != null && _rig.Available) _rig.Tick(Time.deltaTime);
        }

        // ---- 都道府県・県内地区 --------------------------------------------------

        private void BuildPrefectureDropdown()
        {
            if (_pref == null) return;
            _pref.choices = PrefectureNames.Japanese.ToList();
            _pref.index = NationService.DefaultPrefectureId;
            _pref.RegisterValueChangedCallback(e => ApplyPrefecture(PrefectureNames.Japanese.ToList().IndexOf(e.newValue)));
        }

        /// <summary>県を切り替える。地区欄の出し入れ・校名候補の入れ替えがここにぶら下がる。</summary>
        private void ApplyPrefecture(int prefectureId)
        {
            if (prefectureId < 0 || prefectureId >= PrefectureNames.Japanese.Count) return;
            _prefectureId = prefectureId;

            // 県内地区は group_split 採用県だけ（現状は神奈川4・兵庫5）。それ以外は欄ごと出さない。
            var districts = DistrictPlanProvider.DistrictsOf(prefectureId);
            var has = districts > 0;
            _districtId = has ? 0 : (int?)null;
            if (_district != null)
            {
                _district.style.display = has ? DisplayStyle.Flex : DisplayStyle.None;
                if (has)
                {
                    _district.choices = Enumerable.Range(0, districts)
                        .Select(d => DistrictPlanProvider.DistrictLabel(prefectureId, d)).ToList();
                    _district.index = 0;
                    _district.UnregisterValueChangedCallback(OnDistrictChanged);
                    _district.RegisterValueChangedCallback(OnDistrictChanged);
                }
            }
            if (_districtLabel != null)
                _districtLabel.style.display = has ? DisplayStyle.Flex : DisplayStyle.None;

            BuildSuggestions();
            Render();
        }

        private void OnDistrictChanged(ChangeEvent<string> e)
        {
            _districtId = _district?.index ?? 0;
            Render();
        }

        // ---- 校名 ---------------------------------------------------------------

        /// <summary>その県の地名から校名候補を出す（校名語彙 data/school-names.yaml と同じ表）。</summary>
        private void BuildSuggestions()
        {
            if (_suggestHost == null) return;
            _suggestHost.Clear();
            var vocab = SchoolNameVocabProvider.Default;
            if (vocab.PlacesByPrefecture == null
                || !vocab.PlacesByPrefecture.TryGetValue(_prefectureId, out var places)
                || places == null) return;

            foreach (var place in places.Take(SuggestCount))
            {
                var text = place;
                var b = new Button(() => { if (_name != null) _name.value = text; }) { text = text };
                b.AddToClassList("ngs-sug");
                _suggestHost.Add(b);
            }
        }

        private string TypedName => (_name?.value ?? "").Trim();

        /// <summary>入力が通らない理由（通るなら空文字）。</summary>
        private string Validate()
        {
            var name = TypedName;
            if (name.Length == 0) return "校名を入力してください";
            if (name.Length > MaxNameLength) return "校名は " + MaxNameLength + " 文字までです";
            // 同じ県に同名の生成校が居ると大会表で見分けが付かない。県内だけ見れば足りる
            // （対戦相手として並ぶのは県内校だから）。
            var full = name + "高校";
            foreach (var s in NationService.Nation.InPrefecture(_prefectureId))
                if (s.Name == full || s.Name == name) return "その校名は県内の学校と重複しています";
            return "";
        }

        // ---- 描画 ---------------------------------------------------------------

        private void OnColorsChanged() => Render();

        private void Render()
        {
            var error = Validate();
            if (_error != null) _error.text = error;
            if (_summary != null)
            {
                var name = TypedName.Length == 0 ? "―" : TypedName + "高校";
                var line = PrefectureNames.JapaneseOf(_prefectureId) + "　" + name;
                if (_districtId is { } d) line += "　／　" + DistrictPlanProvider.DistrictLabel(_prefectureId, d);
                _summary.text = line;
            }
            _start?.SetEnabled(error.Length == 0);

            // 実物は守備側・攻撃側の両方へ同じ配色を流す（どの選手が映っても自校の色になる）
            if (_picker.Colors != null)
            {
                _rig?.SetTeamColors(_picker.Colors, _picker.Colors);
                _preview?.SetColors(_picker.Colors);
            }
        }

        /// <summary>
        /// 実物（対決ビューと同じ <see cref="Match.DuelStageRig"/>）を組む。組めなければ模式図へ落ちる。
        /// <b>rig は画面を離れるときに必ず捨てる</b>＝HideAndDontSave の残骸が Play 終了・
        /// ドメインリロードを跨いで生き残るため。
        /// </summary>
        private void BuildPreview(VisualElement host)
        {
            if (host == null) return;
            host.Clear();

            _rig = new Match.DuelStageRig();
            if (_rig.Available)
            {
                _rig.PosePreviewStanding();
                _rig.FramePreview(PreviewCamPos, PreviewLookAt, PreviewFrameHeightM);
                _rigView = new Match.DuelViewElement();
                _rigView.BindRig(_rig);
                host.Add(_rigView);
                SetPreviewKind("対決ビュー");
                return;
            }

            _rig.Dispose();
            _rig = null;
            _preview = new UniformPreviewElement();
            _preview.style.flexGrow = 1f;
            host.Add(_preview);
            SetPreviewKind("模式図");
        }

        private void SetPreviewKind(string kind)
        {
            var k = _root?.Q<Label>("ngs-preview-kind");
            if (k != null) k.text = kind;
        }

        // ---- 確定 ---------------------------------------------------------------

        private void OnStart()
        {
            if (Validate().Length > 0) return;

            // 開始手順は SaveGameIO の1経路だけ（リセット → 同定を据える → 新設校ロスター）。
            SaveGameIO.StartNewSchool(TypedName, _prefectureId, _districtId);
            // 配色はリセットで消えるので、同定を据えたあとに確定する（順序が逆だと選んだ色が消える）。
            TeamColorProvider.ManagerOverride = _picker.Colors;
            KokoSim.Unity.Home.HomeState.Reset();   // 前セッションの通知フィードを持ち越さない

            Debug.Log($"[新規ゲーム] {PrefectureNames.JapaneseOf(_prefectureId)} {TypedName}高校"
                      + $" 地区={(_districtId.HasValue ? _districtId.Value.ToString() : "なし")}"
                      + $" 部員={RosterService.Active.Count}人");

            // クリック配信中に同期 Show すると UITK のイベント木が壊れる（ScreenRouter.cs 冒頭コメント）。
            ScreenRouter.Instance?.ShowDeferred("HomeDashboard");
        }

#if UNITY_EDITOR
        /// <summary>【スクショ用】県・地区・校名を機械的に据える（人手の入力なしで絵を撮るため）。</summary>
        internal void SetForCapture(int prefectureId, string name)
        {
            if (_pref != null) _pref.index = prefectureId;
            ApplyPrefecture(prefectureId);
            if (_name != null) _name.value = name;
            Render();
        }

        /// <summary>【スクショ用】配色の行を機械的に送る。</summary>
        internal void StepColorForCapture(int rowIndex, int delta) => _picker.StepRow(rowIndex, delta);
#endif
    }
}
