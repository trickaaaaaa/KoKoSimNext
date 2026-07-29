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
    /// 案B「領域別」＝プリセットから選ばせず、領域ごとに色を送る（自由度優先・2026-07-29 監督選定）。
    /// 選べる色は<b>パレット表に出てくる色だけ</b>（<see cref="TeamColorPalette.Swatches"/>）＝
    /// 自由に見えて破綻した配色は作れない。
    ///
    /// 表示専用のデータで試合結果には触れない。編集中は下書き（<see cref="_draft"/>）に置き、
    /// 「この配色にする」を押したときだけ <see cref="TeamColorProvider.ManagerOverride"/> へ確定する。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class TeamSettingController : MonoBehaviour
    {
        /// <summary>編集できる領域。順序はそのまま画面の行順。</summary>
        private enum Slot { Shirt, Pants, Undershirt, Socks, Cap, StripeOn, StripeColor }

        private static readonly (Slot Slot, string Label, bool GroupTop)[] Rows =
        {
            (Slot.Shirt, "ユニフォーム上", false),
            (Slot.Pants, "ズボン", false),
            (Slot.Undershirt, "アンダーシャツ", false),
            (Slot.Socks, "ソックス", false),
            // 帽子はクラウンとつばで1色。購入素体の帽子が1メッシュで塗り分けられないため（design-18 §3.5.1）
            (Slot.Cap, "帽子", false),
            (Slot.StripeOn, "縦縞", true),
            (Slot.StripeColor, "縞色", false),
        };

        /// <summary>縞の本数[本/m]。実物のピンストライプは間隔2.5cm＝40本/m（design-18 §3.5.3）。</summary>
        private const double StripeFreq = 40.0;

        // ---- プレビュー（実物優先・模式図フォールバック） ----
        // 配色は最終的に対決ビューの絵で決まるので、見せるのは実物の方が正しい（2026-07-29 監督指摘）。
        // 3Dアセットが無い環境（購入素体・クリップ未導入）では組めないので、そのときだけ模式図へ落ちる。

        /// <summary>打者を正面から見る構図。打者は (BatterX,0,BatterZ) に yaw90＝+X を向いて立つ。</summary>
        private static readonly Vector3 PreviewCamPos = new(1.9f, 1.20f, 0.10f);
        private static readonly Vector3 PreviewLookAt = new(-0.9f, 1.05f, 0f);
        private const float PreviewFovDeg = 48f;   // 構えたバットの先まで入る画角（実測で決めた）

        private VisualElement _root;
        private VisualElement _rowHost;
        private UniformPreviewElement _preview;      // 模式図（フォールバック）
        private Match.DuelStageRig _rig;             // 実物（優先）
        private Match.DuelViewElement _rigView;
        private Label _changed;

        private TeamColors _draft;
        private TeamColors _applied;

        private void OnEnable()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;
            _rowHost = _root.Q<VisualElement>("ts-rows");
            _changed = _root.Q<Label>("ts-changed");

            BuildPreview(_root.Q<VisualElement>("ts-preview"));

            var own = _root.Q<Label>("ts-own");
            if (own != null) own.text = NationService.ManagerSchoolName;

            _root.Q<Button>("ts-apply")?.RegisterCallback<ClickEvent>(_ => Apply());

            // 共通トップバーの「今週を進める」（他のタブ画面と同じ配線。引退週・開幕週はホームへ回送される）
            var advance = _root.Q<Button>("advance");
            if (advance != null) advance.clicked += () => WeekAdvance.FromSideScreen(Render);

            _applied = TeamColorProvider.ManagerColors;
            _draft = _applied;
            Render();
        }

        private void Apply()
        {
            TeamColorProvider.ManagerOverride = _draft;
            _applied = _draft;
            Render();
        }

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
                _rig.FramePreview(PreviewCamPos, PreviewLookAt, PreviewFovDeg);
                _rigView = new Match.DuelViewElement();
                _rigView.BindRig(_rig);
                host.Add(_rigView);
                SetPreviewLabels("対決ビュー",
                    "試合と同じ描画です。ここで見えたとおりに試合でも映ります。");
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
            if (_rowHost == null || _draft == null) return;
            _rowHost.Clear();
            foreach (var r in Rows) _rowHost.Add(BuildRow(r.Slot, r.Label, r.GroupTop));
            // 実物は守備側・攻撃側の両方へ同じ配色を流す（どの選手が映っても自校の色になる）
            _rig?.SetTeamColors(_draft, _draft);
            _preview?.SetColors(_draft);
            if (_changed != null)
                _changed.text = Equals(_draft, _applied) ? "" : "未確定の変更あり";
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

        private VisualElement BuildRow(Slot slot, string label, bool groupTop)
        {
            var row = new VisualElement();
            row.AddToClassList("ts-row");
            if (groupTop) row.AddToClassList("ts-row--group");

            var name = new Label(label);
            name.AddToClassList("ts-cell");
            name.AddToClassList("ts-cell--name");
            row.Add(name);

            var value = new Label(ValueText(slot));
            value.AddToClassList("ts-cell");
            value.AddToClassList("ts-cell--val");
            row.Add(value);

            var swatchCell = new VisualElement();
            swatchCell.AddToClassList("ts-cell");
            swatchCell.AddToClassList("ts-cell--swatch");
            if (TryColorOf(slot, out var shown))
            {
                var chip = new VisualElement();
                chip.AddToClassList("ts-swatch");
                chip.style.backgroundColor = ToColor(shown);
                swatchCell.Add(chip);
            }
            row.Add(swatchCell);

            var pick = new VisualElement();
            pick.AddToClassList("ts-pick");
            pick.Add(Arrow("◀", () => Step(slot, -1)));
            pick.Add(Arrow("▶", () => Step(slot, +1)));
            row.Add(pick);

            return row;
        }

        private Button Arrow(string glyph, System.Action onClick)
        {
            var b = new Button(onClick) { text = glyph };
            b.AddToClassList("ts-arrow");
            return b;
        }

        private static Color ToColor(RgbColor c) => new(c.R / 255f, c.G / 255f, c.B / 255f);

        private string ValueText(Slot slot)
        {
            if (slot == Slot.StripeOn) return _draft.Stripe.On ? "あり" : "なし";
            if (slot == Slot.StripeColor && !_draft.Stripe.On) return "—";
            return TryColorOf(slot, out var c) ? TeamColorProvider.Palette.NameOf(c) : "—";
        }

        private bool TryColorOf(Slot slot, out RgbColor color)
        {
            switch (slot)
            {
                case Slot.Shirt: color = _draft.Shirt; return true;
                case Slot.Pants: color = _draft.Pants; return true;
                case Slot.Undershirt: color = _draft.Undershirt; return true;
                case Slot.Socks: color = _draft.Socks; return true;
                case Slot.Cap: color = _draft.CapCrown; return true;
                case Slot.StripeColor when _draft.Stripe.On: color = _draft.Stripe.Color; return true;
                default: color = default; return false;
            }
        }

        // ---- 編集 --------------------------------------------------------------

        private void Step(Slot slot, int delta)
        {
            if (slot == Slot.StripeOn)
            {
                // 縞を入れるときの色は現在の帽子色を初期値にする（無地から入れた瞬間に
                // 「縞色 未設定」の空状態を作らないため）
                _draft = _draft with
                {
                    Stripe = _draft.Stripe.On
                        ? TeamStripe.None
                        : new TeamStripe(true, _draft.CapCrown, StripeFreq),
                };
                Render();
                return;
            }
            if (slot == Slot.StripeColor && !_draft.Stripe.On) return;   // 縞なしの間は送っても意味がない

            if (!TryColorOf(slot, out var current)) return;
            var next = NextSwatch(current, delta);
            _draft = slot switch
            {
                Slot.Shirt => _draft with { Shirt = next },
                Slot.Pants => _draft with { Pants = next },
                Slot.Undershirt => _draft with { Undershirt = next },
                Slot.Socks => _draft with { Socks = next },
                // つばはクラウンと1メッシュなので必ず一緒に動かす（別々に持たせても絵に出ない）
                Slot.Cap => _draft with { CapCrown = next, CapVisor = next },
                Slot.StripeColor => _draft with { Stripe = _draft.Stripe with { Color = next } },
                _ => _draft,
            };
            Render();
        }

        private static RgbColor NextSwatch(RgbColor current, int delta)
        {
            IReadOnlyList<RgbColor> sw = TeamColorProvider.Palette.Swatches;
            if (sw.Count == 0) return current;
            int i = 0;
            for (int k = 0; k < sw.Count; k++) if (sw[k].Equals(current)) { i = k; break; }
            // 表に無い色（将来の上書き復元など）から送っても必ず一覧内へ入るよう剰余で回す
            return sw[((i + delta) % sw.Count + sw.Count) % sw.Count];
        }

#if UNITY_EDITOR
        /// <summary>【スクショ用】行を機械的に送る（人手のクリックなしで見た目の網羅を撮るため）。</summary>
        internal void StepForCapture(int rowIndex, int delta)
        {
            if (rowIndex < 0 || rowIndex >= Rows.Length) return;
            Step(Rows[rowIndex].Slot, delta);
        }
#endif
    }
}
