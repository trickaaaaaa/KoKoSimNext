using System.Collections.Generic;
using KokoSim.Engine.Nation;
using KokoSim.Unity.Components;   // 部品辞書（RankChip）
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Squad
{
    /// <summary>
    /// チーム設定画面（設計書18 §3.5.1）。自校のユニフォーム配色を領域ごとに決める。
    ///
    /// 領域行そのものは部品辞書の <see cref="UniformColorPicker"/>（新規ゲーム設定画面と共有）。
    /// この画面が持つのは<b>プレビュー（実物/模式図）と確定先</b>だけ。
    ///
    /// 表示専用のデータで試合結果には触れない。編集中は部品側の下書きに置き、
    /// 「この配色にする」を押したときだけ <see cref="TeamColorProvider.ManagerOverride"/> へ確定する。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class TeamSettingController : MonoBehaviour
    {
        // ---- プレビュー（実物優先・模式図フォールバック） ----
        // 配色は最終的に対決ビューの絵で決まるので、見せるのは実物の方が正しい（2026-07-29 監督指摘）。
        // 3Dアセットが無い環境（購入素体・クリップ未導入）では組めないので、そのときだけ模式図へ落ちる。

        /// <summary>
        /// 直立の投手を正面から見る構図。投手はマウンド (0, MoundHeight, 18.44) に
        /// yaw236°（購入AAクリップは素で+Z向き）で立つので、その正面＝(-0.83, 0, -0.56) 方向へカメラを置く。
        /// 数値は実測で詰めた（目測でカメラを置くと外す・グラブ検証と同じ教訓）。
        /// </summary>
        private static readonly Vector3 PreviewCamPos = new(-2.35f, 1.30f, 16.85f);
        private static readonly Vector3 PreviewLookAt = new(0f, 1.10f, 18.44f);
        /// <summary>
        /// 画面の縦に収める実寸[m]。角度でなく寸法で渡す＝欄の縦横比が変わっても背丈が変わらない
        /// （画角固定にすると横長の欄で頭と足が切れる。2026-07-29 実測）。1.75mの素体＋余白。
        /// </summary>
        private const float PreviewFrameHeightM = 1.95f;

        private VisualElement _root;
        private readonly UniformColorPicker _picker = new UniformColorPicker();
        private UniformPreviewElement _preview;      // 模式図（フォールバック）
        private Match.DuelStageRig _rig;             // 実物（優先）
        private Match.DuelViewElement _rigView;
        private Label _changed;

        private TeamColors _applied;

        /// <summary>編集中の下書き（部品が保持する）。</summary>
        private TeamColors Draft => _picker.Colors;

        private void OnEnable()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;
            _picker.Bind(_root.Q<VisualElement>("ts-rows"));
            _picker.Changed -= OnPickerChanged;
            _picker.Changed += OnPickerChanged;
            _changed = _root.Q<Label>("ts-changed");

            BuildPreview(_root.Q<VisualElement>("ts-preview"));

            var own = _root.Q<Label>("ts-own");
            if (own != null) own.text = NationService.ManagerSchoolName;

            _root.Q<Button>("ts-apply")?.RegisterCallback<ClickEvent>(_ => Apply());

            // 共通トップバーの「今週を進める」（他のタブ画面と同じ配線。引退週・開幕週はホームへ回送される）
            var advance = _root.Q<Button>("advance");
            if (advance != null) advance.clicked += () => WeekAdvance.FromSideScreen(Render);

            _applied = TeamColorProvider.ManagerColors;
            _picker.SetColors(_applied);
            Render();
        }

        private void Apply()
        {
            TeamColorProvider.ManagerOverride = Draft;
            _applied = Draft;
            Render();
        }

        /// <summary>部品側で色が動いたら、プレビューと「未確定の変更あり」を追従させる。</summary>
        private void OnPickerChanged() => Render();

        // ---- プレビュー ---------------------------------------------------------

        /// <summary>
        /// 実物（対決ビューと同じ <see cref="Match.DuelStageRig"/>）を組み、打者を正面から見る構図にする。
        /// 組めなければ模式図へ落ちる。<b>rig は画面を離れるときに必ず捨てる</b>＝
        /// HideAndDontSave の残骸がPlay終了・ドメインリロードを跨いで生き残るため。
        /// </summary>
        private void BuildPreview(VisualElement host)
        {
            if (host == null) return;
            host.Clear();

            _rig = new Match.DuelStageRig();
            if (_rig.Available)
            {
                _rig.PosePreviewStanding();   // 投手だけ直立で残す（配色以外を画から追い出す）
                _rig.FramePreview(PreviewCamPos, PreviewLookAt, PreviewFrameHeightM);
                _rigView = new Match.DuelViewElement();
                _rigView.BindRig(_rig);
                host.Add(_rigView);
                SetPreviewLabels("対決ビュー", "試合と同じ描画です。");
                return;
            }

            _rig.Dispose();
            _rig = null;
            Debug.Log("[チーム設定] 対決ビューの素体が使えないので模式図でプレビューする");
            _preview = new UniformPreviewElement();
            _preview.style.flexGrow = 1f;
            host.Add(_preview);
            SetPreviewLabels("模式図",
                "3Dの素体が読み込めないため模式図で代用しています。試合では見え方が異なります。");
        }

        private void SetPreviewLabels(string kind, string note)
        {
            var k = _root?.Q<Label>("ts-preview-kind");
            if (k != null) k.text = kind;
            var n = _root?.Q<Label>("ts-note");
            if (n != null) n.text = note;
        }

        private void OnDisable()
        {
            _picker.Changed -= OnPickerChanged;
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

        // ---- 描画 --------------------------------------------------------------

        private void Render()
        {
            RenderTopBar();
            if (Draft == null) return;
            // 実物は守備側・攻撃側の両方へ同じ配色を流す（どの選手が映っても自校の色になる）
            _rig?.SetTeamColors(Draft, Draft);
            _preview?.SetColors(Draft);
            if (_changed != null)
                _changed.text = Equals(Draft, _applied) ? "" : "未確定の変更あり";
        }

        /// <summary>共通トップバーの動的値（週・メーター・自校の総合ランク）を埋める。他画面と同じ単一ソース。</summary>
        private void RenderTopBar()
        {
            if (_root == null) return;
            KokoSim.Unity.Components.ScoreboardStrip.Fill(_root);
            TopBarMeters.Fill(_root);
            var rank = _root.Q<VisualElement>("team-rank");
            if (rank == null) return;
            rank.Clear();
            rank.Add(UiComponents.RankChipLegacy(TeamOverall.GradeOf(RosterService.Active)));
        }

#if UNITY_EDITOR
        /// <summary>【スクショ用】行を機械的に送る（人手のクリックなしで見た目の網羅を撮るため）。</summary>
        internal void StepForCapture(int rowIndex, int delta) => _picker.StepRow(rowIndex, delta);
#endif
    }
}
