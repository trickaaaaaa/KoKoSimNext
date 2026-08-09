using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;      // AbilityKind（能力ラベル単一ソースの引数, issue #94）
using KokoSim.Unity.Components;   // 部品辞書（CompareRow等）
using KokoSim.Unity.Shell;        // 能力ラベル単一ソース（AbilityLabels, issue #94）
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 試合中の選手交代モーダル（設計書09 §6）。<c>sub-modal</c> にバインドする。
    ///
    /// <para><b>攻撃中</b>は代打／代走のタブ（従来どおり）。<b>守備中</b>は issue #256 で
    /// 旧4タブ（投手交代／守備交代／守備位置／DH解除）を廃し、スタメン設定（#233）と同じ
    /// <b>割当2ペイン</b>に統合した：左＝打順表（守備の割当10枠）、
    /// 中＝控え一覧（役割で分けずフラット・背番号順・出場済みは灰。#93）、右＝能力比較。</para>
    ///
    /// <para>issue #452（パワプロ方式）: 左の並びは<b>打順順</b>（1〜9番。DH制の投手＝打順外は末尾に「—」）。
    /// 操作が決まった時点で該当行を「旧 → 新」の<b>即時プレビュー</b>に切り替え、行の✕で選び直せる
    /// （保留は常に1件・フッター文は補助）。能力比較は選択中の枠の役割に追随し、開いた直後の
    /// 投枠プリセレクトは廃止（守備交代で野手能力が見えない体験の解消）。継投の最短手
    /// 「控えを1クリック→確定」は <see cref="ClickCandidate"/> の投枠フォールバックが引き続き担う。</para>
    ///
    /// <para>操作は「誰を・どこへ」の一手に統一し、結果を導出する：
    /// 枠→枠＝入替（可逆・在フィールド同士）／枠(投)↔在フィールド野手＝投野交代／枠(指)↔守備位置＝DH解除／
    /// 枠→控え＝投入（不可逆・退場が出る）。継投・投野交代は AI と同じ候補集合（<see cref="SubstitutionOptions.ReliefCandidates"/>）
    /// を通り、engine の <see cref="MatchProgression.Relieve"/>／<see cref="MatchProgression.DefenseAssign"/>／
    /// <see cref="MatchProgression.SwapDefensivePositions"/>／<see cref="MatchProgression.ConvertPitcherAndRelieve"/>／
    /// <see cref="MatchProgression.ReleaseDh"/> へ振り分ける。判断ロジックはエンジンに委ね、ここは表示と実行呼び出しだけ
    /// （不変条件#3 エンジン純度）。確定は即時・取り消し不可。</para>
    /// </summary>
    public sealed class MatchSubstitutionPanel
    {
        private readonly VisualElement _root;
        private readonly System.Action _onApplied;

        private VisualElement _scrim, _kinds, _outRows, _inRows, _cmpRows;
        private ScrollView _cmpScroll;
        private Label _phase, _outsCap, _insCap, _note;
        private Button _ok;

        private MatchProgression _prog;
        private bool _teamIsAway;
        private SubstitutionOptions _opt;
        private bool _open;

        // 攻撃タブ（守備中はタブ無し・割当へ統合）。
        private SubstitutionKind _offKind = SubstitutionKind.PinchHit;

        // ── 攻撃中の選択 ──
        private int _baseIndex = -1;     // 代走の塁の添字
        private int _offInIndex = -1;    // 控え（Bench）の添字

        // ── 守備中（割当2ペイン）の選択 ──
        private readonly List<DefenseSlot> _slots = new();  // 割当枠（投・捕…右・指）
        private List<Player> _cands = new();                // 控え一覧（1本の名簿・背番号順）
        private int _pickedSlot = -1;    // 主選択の割当枠
        private int _pickedSlot2 = -1;   // 対の割当枠（枠→枠）
        private int _defInIndex = -1;    // 控え一覧の添字（枠→控え）
        private FieldPosition? _convertTo;  // 継投時に元投手を回す守備位置（null=退場）
        private bool _dhWithdraw;        // 指枠を「そのまま退場」させる

        /// <param name="aptitudeGrades">
        /// 守備適性の9ポジションのランクを <see cref="Player.SourceId"/> から引く（issue #452）。
        /// 試合中の <see cref="Player"/> は適性配列を持たないので、名簿への join は Shell 側
        /// （<see cref="MatchLiveStatsProvider.AptitudeGradesOf"/>）に任せて注入する。null なら帯を出さない。
        /// </param>
        public MatchSubstitutionPanel(VisualElement root, System.Action onApplied,
            System.Func<int?, IReadOnlyList<string>> aptitudeGrades = null,
            System.Func<Player, List<PitchChartDatum>> pitchChart = null)
        {
            _root = root;
            _onApplied = onApplied;
            _aptitudeGrades = aptitudeGrades;
            _pitchChart = pitchChart;
        }

        private readonly System.Func<int?, IReadOnlyList<string>> _aptitudeGrades;
        private readonly System.Func<Player, List<PitchChartDatum>> _pitchChart;

        /// <summary>モーダルを開いている間は裏の進行（自動再生・次の打席へ）を止める。</summary>
        public bool IsOpen => _open;

        public void Bind()
        {
            _scrim = _root.Q<VisualElement>("sub-modal");
            _kinds = _root.Q<VisualElement>("msub-kinds");
            _outRows = _root.Q<VisualElement>("msub-out-rows");
            _inRows = _root.Q<VisualElement>("msub-in-rows");
            _cmpRows = _root.Q<VisualElement>("msub-cmp-rows");
            // ScrollView 本体（窓の高さを持つ要素）。_cmpRows.parent は contentContainer なので届かない。
            _cmpScroll = _root.Q<ScrollView>(className: "msub-cmp-scroll");
            _phase = _root.Q<Label>("msub-phase");
            _outsCap = _root.Q<Label>("msub-outs-cap");
            _insCap = _root.Q<Label>("msub-ins-cap");
            _note = _root.Q<Label>("msub-note");
            _ok = _root.Q<Button>("msub-ok");

            Click("msub-close", Close);
            Click("msub-cancel", Close);
            Click("msub-ok", Apply);
            Close();
        }

        /// <summary>この試合の進行体と監督のチームを差し替える（試合ごとに呼ぶ）。</summary>
        public void SetMatch(MatchProgression prog, bool teamIsAway)
        {
            _prog = prog;
            _teamIsAway = teamIsAway;
            Close();
        }

        public void Open()
        {
            if (_prog == null || _scrim == null) return;
            _opt = _prog.SubstitutionOptions(_teamIsAway);
            _offKind = SubstitutionKind.PinchHit;
            ResetSelection();
            _open = true;
            _scrim.style.display = DisplayStyle.Flex;
            Render();
        }

        public void Close()
        {
            _open = false;
            if (_scrim != null) _scrim.style.display = DisplayStyle.None;
        }

        /// <summary>スクショ用: 攻撃タブを選ぶ（0=代打 1=代走）。守備中はタブ無し（割当）＝index は無視。</summary>
        public void SelectKindForCapture(int index)
        {
            if (!_open || _opt == null || !_opt.IsOffense) return;
            _offKind = index == 1 ? SubstitutionKind.PinchRun : SubstitutionKind.PinchHit;
            ResetSelection();
            Render();
        }

        /// <summary>スクショ用: 守備の割当枠を守備位置で選ぶ（クリックと同じ経路）。issue #452 のプレビュー撮影用。</summary>
        public void PickSlotForCapture(FieldPosition pos)
        {
            if (!_open || _opt == null || _opt.IsOffense) return;
            var i = _slots.FindIndex(s => s.Pos == pos);
            if (i < 0) return;
            ClickSlot(i);
            Render();
        }

        /// <summary>スクショ用: 控え一覧の n 番目を選ぶ（守備＝割当・攻撃＝代打/代走の控え）。</summary>
        public void PickCandidateForCapture(int index)
        {
            if (!_open || _opt == null) return;
            if (_opt.IsOffense)
            {
                if (index >= 0 && index < _opt.Reserves.Count) { _offInIndex = index; Render(); }
                return;
            }
            if (index >= 0 && index < _cands.Count) { ClickCandidate(index); Render(); }
        }

        /// <summary>
        /// スクショ用: いまその守備位置に就いている選手。モーダルの内部モデル（_slots）は開いた瞬間の
        /// スナップショットなので使わず、engine から選択肢を引き直す＝確定後の姿を読める。
        /// </summary>
        public Player FielderForCapture(FieldPosition pos)
        {
            if (_prog == null) return null;
            var opt = _prog.SubstitutionOptions(_teamIsAway);
            if (pos == FieldPosition.Pitcher)
                return opt.UsesDh
                    ? opt.CurrentPitcher
                    : (opt.PitcherSlot >= 0 && opt.PitcherSlot < opt.Lineup.Count ? opt.Lineup[opt.PitcherSlot] : null);
            for (var i = 0; i < opt.Lineup.Count; i++)
            {
                if (opt.UsesDh && i == opt.DhSlot) continue;
                if (opt.Lineup[i] != null && opt.Lineup[i].Position == pos) return opt.Lineup[i];
            }
            return null;
        }

        /// <summary>スクショ用: 残っている控えの人数（交代が成立したかの一番素直な証拠）。</summary>
        public int BenchCountForCapture
        {
            get
            {
                if (_prog == null) return -1;
                var opt = _prog.SubstitutionOptions(_teamIsAway);
                return opt.Reserves != null ? opt.Reserves.Count : -1;
            }
        }

        /// <summary>スクショ用: 「この操作を確定」を押す（実行経路はボタンと同一）。issue #452 の操作一巡確認用。</summary>
        public void ApplyForCapture() => Apply();

        /// <summary>スクショ用: 保留中の操作の要約（フッター文）。確定できる状態かの確認に使う。</summary>
        public string NoteForCapture => _note?.text ?? "";

        /// <summary>スクショ用: 代走に出す走者を選ぶ（負値＝先頭の走者）。</summary>
        public void PickRunnerForCapture(int baseIndex)
        {
            if (!_open || _opt == null || !_opt.IsOffense || _opt.Runners.Count == 0) return;
            _baseIndex = baseIndex >= 0 ? baseIndex : _opt.Runners[0].BaseIndex;
            Render();
        }

        private void ResetSelection()
        {
            _baseIndex = -1;
            _offInIndex = -1;
            _pickedSlot = -1;
            _pickedSlot2 = -1;
            _defInIndex = -1;
            _convertTo = null;
            _dhWithdraw = false;
            if (_opt != null && !_opt.IsOffense)
            {
                BuildDefenseModel();
                // 投枠のプリセレクトは廃止（issue #452 指摘4。開いた直後に投球比較しか出ず、守備交代で
                // 野手能力が見えない体験の主因だった）。継投の最短手「控えを1クリック→確定」は
                // ClickCandidate の「枠未選択なら投枠へ」フォールバックが引き続き成立させる。
            }
        }

        // ── 守備割当モデル ──

        private enum SlotRole { Pitcher, Fielder, Dh }

        private readonly struct DefenseSlot
        {
            public readonly FieldPosition Pos;
            public readonly Player Player;
            public readonly int LineupSlot;   // 打順スロット（DH制の投手は打順外＝-1）
            public readonly SlotRole Role;
            public DefenseSlot(FieldPosition pos, Player player, int lineupSlot, SlotRole role)
            {
                Pos = pos; Player = player; LineupSlot = lineupSlot; Role = role;
            }
        }

        private static readonly FieldPosition[] FielderOrder =
        {
            FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase, FieldPosition.ThirdBase,
            FieldPosition.Shortstop, FieldPosition.LeftField, FieldPosition.CenterField, FieldPosition.RightField,
        };

        /// <summary>
        /// 守備割当10枠と控え一覧（1本の名簿・背番号順）を組む。並びは<b>打順順</b>（1〜9番。
        /// DH制の投手＝打順外は末尾。issue #452 指摘1）。
        /// </summary>
        private void BuildDefenseModel()
        {
            _slots.Clear();
            // 投（非DHは打順内・DHは打順外の CurrentPitcher）。
            if (_opt.UsesDh)
                _slots.Add(new DefenseSlot(FieldPosition.Pitcher, _opt.CurrentPitcher, -1, SlotRole.Pitcher));
            else if (_opt.PitcherSlot >= 0 && _opt.PitcherSlot < _opt.Lineup.Count)
                _slots.Add(new DefenseSlot(FieldPosition.Pitcher, _opt.Lineup[_opt.PitcherSlot], _opt.PitcherSlot, SlotRole.Pitcher));
            // 守備8枠（守備位置で引く）。
            foreach (var pos in FielderOrder)
            {
                var idx = LineupSlotOf(pos);
                if (idx >= 0) _slots.Add(new DefenseSlot(pos, _opt.Lineup[idx], idx, SlotRole.Fielder));
            }
            // 指（DH制のみ）。
            if (_opt.UsesDh && _opt.DhSlot >= 0 && _opt.DhSlot < _opt.Lineup.Count)
                _slots.Add(new DefenseSlot(FieldPosition.DesignatedHitter, _opt.Lineup[_opt.DhSlot], _opt.DhSlot, SlotRole.Dh));

            // 打順順に並べ替える（パワプロ方式・issue #452）。打順外（DH制の投手）だけ末尾＝"—"。
            _slots.Sort((x, y) => OrderKey(x).CompareTo(OrderKey(y)));

            // 控えは役割で分けない（#93 / #381: engine 側も1本の名簿）。念のため背番号順に揃える。
            _cands = _opt.Reserves.OrderBy(p => p.UniformNumber == 0 ? int.MaxValue : p.UniformNumber)
                .ThenBy(p => p.Name).ToList();
        }

        /// <summary>打順順ソートの鍵。打順外（DH制の投手＝LineupSlot -1）は末尾へ。</summary>
        private static int OrderKey(DefenseSlot s) => s.LineupSlot >= 0 ? s.LineupSlot : int.MaxValue;

        private int LineupSlotOf(FieldPosition pos)
        {
            for (var i = 0; i < _opt.Lineup.Count; i++)
            {
                if (_opt.UsesDh && i == _opt.DhSlot) continue;
                if (_opt.Lineup[i].Position == pos) return i;
            }
            return -1;
        }

        // ── 描画 ──

        private void Render()
        {
            if (_opt == null) return;
            if (_phase != null) _phase.text = _opt.IsFinished ? "試合終了" : _opt.IsOffense ? "攻撃中" : "守備中";
            BuildKinds();
            if (_opt.IsOffense) { BuildOffenseOut(); BuildOffenseIn(); }
            else { BuildDefenseAssign(); BuildDefenseCandidates(); }
            BuildCompare();
            BuildFooter();
        }

        private void BuildKinds()
        {
            if (_kinds == null) return;
            _kinds.Clear();
            if (!_opt.IsOffense) return;   // 守備中はタブ無し（割当へ統合, issue #256）
            foreach (var (kind, label) in new[] { (SubstitutionKind.PinchHit, "代打"), (SubstitutionKind.PinchRun, "代走") })
            {
                var cell = new Label(label);
                cell.AddToClassList("seg__cell");
                if (kind == _offKind) cell.AddToClassList("seg__cell--on");
                else if (kind == SubstitutionKind.PinchRun && !_opt.CanPinchRun) cell.AddToClassList("seg__cell--off");
                else if (kind == SubstitutionKind.PinchHit && !_opt.CanPinchHit) cell.AddToClassList("seg__cell--off");
                var k = kind;
                cell.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); _offKind = k; ResetSelection(); Render(); });
                _kinds.Add(cell);
            }
        }

        // ── 攻撃中：左＝退く選手／走者 ──

        private void BuildOffenseOut()
        {
            if (_outRows == null) return;
            _outRows.Clear();
            if (_outsCap != null) _outsCap.text = _offKind == SubstitutionKind.PinchRun ? "退く走者" : "退く選手";

            // 控えを選んだ時点で、該当行を「旧 → 新」の即時プレビューへ切り替える（issue #452 指摘2）。
            var inName = _offInIndex >= 0 && _offInIndex < _opt.Reserves.Count ? NameOf(_opt.Reserves[_offInIndex]) : null;
            for (var i = 0; i < _opt.Lineup.Count; i++)
            {
                var p = _opt.Lineup[i];
                var slot = i;
                var tag = "";
                var selectable = false;
                var picked = false;
                string preview = null;
                if (_offKind == SubstitutionKind.PinchHit)
                {
                    if (i == _opt.UpcomingBatterSlot) { tag = "次打者"; picked = true; preview = inName; }
                }
                else
                {
                    var b = BaseOf(p);
                    if (b >= 0) { selectable = true; tag = BaseName(b); picked = _baseIndex == b; if (picked) preview = inName; }
                }
                var row = LineupRow(i + 1, p, PosJp(p.Position), selectable, picked, tag, chipActive: false,
                    previewTo: preview, onCancel: preview == null ? null : CancelOffensePick);
                if (selectable)
                    row.RegisterCallback<ClickEvent>(e =>
                    {
                        e.StopPropagation();
                        _baseIndex = BaseOf(_opt.Lineup[slot]);
                        Render();
                    });
                _outRows.Add(row);
            }
        }

        private void BuildOffenseIn()
        {
            if (_inRows == null) return;
            _inRows.Clear();
            if (_insCap != null) _insCap.text = "控え";
            for (var i = 0; i < _opt.Reserves.Count; i++)
            {
                var idx = i;
                var row = BenchRow(_opt.Reserves[i], dim: false, picked: _offInIndex == i, note: "");
                row.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); _offInIndex = idx; Render(); });
                _inRows.Add(row);
            }
            foreach (var p in _opt.UsedReserves) _inRows.Add(BenchRow(p, dim: true, picked: false, note: "出場済"));
            if (_opt.Reserves.Count == 0 && _opt.UsedReserves.Count == 0)
            {
                var empty = new Label("控えが登録されていない。");
                empty.AddToClassList("cmp-card__empty");
                _inRows.Add(empty);
            }
        }

        // ── 守備中：左＝守備の割当10枠 ──

        private void BuildDefenseAssign()
        {
            if (_outRows == null) return;
            _outRows.Clear();
            if (_outsCap != null) _outsCap.text = "打順表";

            // 操作が決まった行は「旧 → 新」の即時プレビューに切り替える（issue #452 指摘2・3）。
            var op = ResolveDef();
            for (var i = 0; i < _slots.Count; i++)
            {
                var s = _slots[i];
                var slotIdx = i;
                var picked = i == _pickedSlot || i == _pickedSlot2;
                var order = s.LineupSlot >= 0 ? s.LineupSlot + 1 : 0;   // DHの投手は打順外＝"—"
                var preview = DefPreviewText(op, i);
                var row = LineupRow(order, s.Player, PosJp(s.Pos), selectable: true, picked, tag: "", chipActive: true,
                    previewTo: preview, onCancel: preview == null ? null : CancelDefenseOp);
                row.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); ClickSlot(slotIdx); Render(); });
                _outRows.Add(row);
            }
        }

        /// <summary>
        /// この枠に出す「→ 後の姿」のプレビュー文字列（無い枠は null。issue #452 指摘2）。
        /// 入る選手名／移る守備位置／退場・自動補充を、確定前の見込みとして行内に出す。
        /// </summary>
        private string DefPreviewText(in DefOp op, int slotIdx)
        {
            var s = _slots[slotIdx];
            switch (op.Kind)
            {
                case DefKind.Relieve:
                    return s.Role == SlotRole.Pitcher ? NameOf(op.Incoming) : null;
                case DefKind.Convert:
                    if (s.Role == SlotRole.Pitcher) return NameOf(op.Incoming);
                    if (s.Role == SlotRole.Fielder && s.Pos == op.Pos) return NameOf(_opt.CurrentPitcher);
                    return null;
                case DefKind.PromoteFielder:
                    // 単純な入れ替え＝投手枠に野手、その守備位置に今の投手（issue #452）。
                    // DHでは投手が打順外＝入れ替えが成立せず、元DHがその守備位置に就く（DH解除）。
                    if (s.Role == SlotRole.Pitcher) return NameOf(op.Incoming);
                    if (s.Role == SlotRole.Fielder && s.Pos == op.Pos) return NameOf(SwapBackfiller());
                    if (s.Role == SlotRole.Dh) return PosJp(op.Pos) + "へ";
                    return null;
                case DefKind.FielderSwap:
                    if (slotIdx == _pickedSlot) return PosJp(_slots[_pickedSlot2].Pos) + "へ";
                    if (slotIdx == _pickedSlot2) return PosJp(_slots[_pickedSlot].Pos) + "へ";
                    return null;
                case DefKind.Assign:
                    return slotIdx == _pickedSlot ? NameOf(op.Incoming) : null;
                case DefKind.DhToPos:
                    if (s.Role == SlotRole.Dh) return PosJp(op.Pos) + "へ";
                    if (s.Role == SlotRole.Fielder && s.Pos == op.Pos) return "退場";
                    return null;
                case DefKind.DhWithdraw:
                    return s.Role == SlotRole.Dh ? "退場" : null;
                default:
                    return null;   // None / Invalid はプレビュー無し
            }
        }

        /// <summary>行の✕＝保留中の操作の取り消し（保留は常に1件）。主選択の枠は残して選び直せる。</summary>
        private void CancelDefenseOp()
        {
            _pickedSlot2 = -1;
            _defInIndex = -1;
            _convertTo = null;
            _dhWithdraw = false;
            Render();
        }

        /// <summary>行の✕（攻撃側）＝選んだ控えの取り消し。</summary>
        private void CancelOffensePick()
        {
            _offInIndex = -1;
            Render();
        }

        /// <summary>
        /// 割当枠のクリック（スタメン設定と同じ2クリック操作。issue #233/#256）。
        /// 1つ目→主選択、別の枠で対（枠→枠）、控えを選ぶ前は自由に選び直せる。同じ枠の再クリックで解除。
        /// </summary>
        private void ClickSlot(int slotIdx)
        {
            _defInIndex = -1;   // 枠を触ったら控え選択は解除
            _convertTo = null;
            _dhWithdraw = false;
            if (_pickedSlot < 0) { _pickedSlot = slotIdx; _pickedSlot2 = -1; return; }
            if (slotIdx == _pickedSlot) { _pickedSlot = _pickedSlot2; _pickedSlot2 = -1; return; }
            if (slotIdx == _pickedSlot2) { _pickedSlot2 = -1; return; }
            if (_pickedSlot2 < 0) { _pickedSlot2 = slotIdx; return; }
            _pickedSlot = slotIdx; _pickedSlot2 = -1;   // 3枠目＝選び直し
        }

        // ── 守備中：中＝控え一覧（フラット・背番号順）＋文脈の追加選択 ──

        private void BuildDefenseCandidates()
        {
            if (_inRows == null) return;
            _inRows.Clear();
            if (_insCap != null) _insCap.text = "控え（背番号順）";

            for (var i = 0; i < _cands.Count; i++)
            {
                var idx = i;
                var row = BenchRow(_cands[i], dim: false, picked: _defInIndex == i, note: "");
                row.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); ClickCandidate(idx); Render(); });
                _inRows.Add(row);
            }
            // 出場済みは1本の名簿から一度だけ出す（旧 UsedBullpen＋UsedBench の連結を畳んだ。issue #381）。
            foreach (var p in _opt.UsedReserves)
                _inRows.Add(BenchRow(p, dim: true, picked: false, note: "出場済"));
            if (_cands.Count == 0 && !_opt.UsedReserves.Any())
            {
                var empty = new Label("控えが登録されていない。");
                empty.AddToClassList("cmp-card__empty");
                _inRows.Add(empty);
            }

            // 文脈の追加選択（選ばなければ既定挙動）。
            var a = Picked(_pickedSlot);
            if (a is { Role: SlotRole.Pitcher } && _defInIndex >= 0) BuildConvertChoices();
            else if (a is { Role: SlotRole.Dh }) BuildDhWithdrawChoice();
        }

        private void ClickCandidate(int idx)
        {
            _defInIndex = idx;
            _pickedSlot2 = -1;      // 控えを選んだら枠→枠は解除
            _convertTo = null;
            _dhWithdraw = false;
            // 控えは主選択の枠へ入る。枠が未選択なら投枠を主選択に（継投の最短手）。
            if (_pickedSlot < 0) _pickedSlot = _slots.FindIndex(s => s.Role == SlotRole.Pitcher);
        }

        /// <summary>継投で元投手をどうするか（既定＝退場／守備位置を選ぶとコンバート継投）。</summary>
        private void BuildConvertChoices()
        {
            var cap = new Label("元の投手");
            cap.AddToClassList("msub-group");
            _inRows.Add(cap);

            var retire = ChoiceRow("退場させる（通常の継投）", _convertTo == null);
            retire.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); _convertTo = null; Render(); });
            _inRows.Add(retire);

            // 行き先の列挙は守備位置順のまま（_slots は打順順になったので位置順で引き直す。issue #452）。
            foreach (var pos in FielderOrder)
            {
                var s = _slots.FirstOrDefault(t => t.Role == SlotRole.Fielder && t.Pos == pos);
                if (s.Player == null) continue;
                var to = pos;
                var row = ChoiceRow(PosJp(to) + "へ回す（" + NameOf(s.Player) + " が退場）", _convertTo == to);
                row.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); _convertTo = to; Render(); });
                _inRows.Add(row);
            }
        }

        /// <summary>指枠の行き先。守備位置枠をクリック＝その守備でDH解除／ここで「そのまま退場」も選べる。</summary>
        private void BuildDhWithdrawChoice()
        {
            var hint = new Label("守備位置の枠をクリック＝その守備に就いてDH解除。または:");
            hint.AddToClassList("msub-hint");
            _inRows.Add(hint);
            var withdraw = ChoiceRow("そのまま退かせる（投手が打席へ）", _dhWithdraw);
            withdraw.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); _dhWithdraw = true; _pickedSlot2 = -1; Render(); });
            _inRows.Add(withdraw);
        }

        // ── 導出：守備の割当操作 ──

        private enum DefKind { None, Relieve, Convert, PromoteFielder, FielderSwap, Assign, DhToPos, DhWithdraw, Invalid }

        private readonly struct DefOp
        {
            public readonly DefKind Kind;
            public readonly Player Incoming;      // 継投/割当で入る選手
            public readonly int SlotA, SlotB;     // 打順スロット（入替）
            public readonly FieldPosition Pos;    // コンバート/DH解除の守備位置
            public readonly Player Outgoing;       // 退場する選手（footer用・不確定なら null）
            public DefOp(DefKind kind, Player incoming = null, int slotA = -1, int slotB = -1,
                FieldPosition pos = FieldPosition.Pitcher, Player outgoing = null)
            {
                Kind = kind; Incoming = incoming; SlotA = slotA; SlotB = slotB; Pos = pos; Outgoing = outgoing;
            }
        }

        private DefenseSlot? Picked(int i) => i >= 0 && i < _slots.Count ? _slots[i] : (DefenseSlot?)null;

        private DefOp ResolveDef()
        {
            if (Picked(_pickedSlot) is not { } a) return new DefOp(DefKind.None);
            if (a.Role == SlotRole.Dh && _dhWithdraw)
                return new DefOp(DefKind.DhWithdraw, outgoing: a.Player);

            if (_defInIndex >= 0 && _defInIndex < _cands.Count)
            {
                var cand = _cands[_defInIndex];
                if (a.Role == SlotRole.Pitcher)
                    return _convertTo is { } to
                        ? new DefOp(DefKind.Convert, cand, pos: to, outgoing: FielderAt(to))
                        : new DefOp(DefKind.Relieve, cand, outgoing: _opt.CurrentPitcher);
                if (a.Role == SlotRole.Fielder)
                    return new DefOp(DefKind.Assign, cand, slotA: a.LineupSlot, pos: a.Pos, outgoing: a.Player);
                return new DefOp(DefKind.Invalid);   // 控え→指 は不可
            }

            if (Picked(_pickedSlot2) is { } b)
            {
                if (a.Role == SlotRole.Fielder && b.Role == SlotRole.Fielder)
                    return new DefOp(DefKind.FielderSwap, slotA: a.LineupSlot, slotB: b.LineupSlot);
                if (a.Role == SlotRole.Pitcher && b.Role == SlotRole.Fielder)
                    return new DefOp(DefKind.PromoteFielder, b.Player, pos: b.Pos);
                if (a.Role == SlotRole.Fielder && b.Role == SlotRole.Pitcher)
                    return new DefOp(DefKind.PromoteFielder, a.Player, pos: a.Pos);
                if (a.Role == SlotRole.Dh && b.Role == SlotRole.Fielder)
                    return new DefOp(DefKind.DhToPos, pos: b.Pos, outgoing: b.Player);
                if (a.Role == SlotRole.Fielder && b.Role == SlotRole.Dh)
                    return new DefOp(DefKind.DhToPos, pos: a.Pos, outgoing: a.Player);
                return new DefOp(DefKind.Invalid);   // 投⇄指 など
            }
            return new DefOp(DefKind.None);
        }

        private Player FielderAt(FieldPosition pos)
            => _slots.FirstOrDefault(s => s.Role == SlotRole.Fielder && s.Pos == pos).Player;

        /// <summary>
        /// 投野交代（<see cref="DefKind.PromoteFielder"/>）で、野手が空ける守備位置に入る選手（issue #452）。
        /// 非DHは今の投手＝そのまま入れ替え。DHは投手が打順外で入れ替えが成立しないので元DHが就く
        /// （engine の <c>TeamState.RelieveWith(..., allowBenchBackfill: false)</c> と同じ結論を先出しする）。
        /// </summary>
        private Player SwapBackfiller()
        {
            if (!_opt.UsesDh) return _opt.CurrentPitcher;
            var dh = _slots.FirstOrDefault(s => s.Role == SlotRole.Dh);
            return dh.Player;
        }

        // ── 右：能力比較 ──

        private void BuildCompare()
        {
            var (outgoing, incoming, pitcherView, capA, capB) = CompareSubjects();
            // 空カードには「次に何を選べばよいか」を出す（開いた直後の空状態の道標。issue #452 指摘4）。
            FillCard("msub-cmp-a", outgoing, EmptyCardTextA(), pitcherView);
            FillCard("msub-cmp-b", incoming, EmptyCardTextB(), pitcherView);

            // 投手ビューはカードに持ち球チャートが乗るぶん表の窓を縮め、モーダル全体の高さを一定に保つ。
            _cmpScroll?.EnableInClassList("msub-cmp-scroll--pitch", pitcherView);

            if (_cmpRows == null) return;
            _cmpRows.Clear();
            // 比較列の高さは左の打順表で決まる＝野手ビューの9行（弾道〜精神力）が素の組では入らず、
            // 守備交代の判断に一番要る「守備」「捕球」が既定で窓の外に落ちていた（issue #452）。
            // 密な組に切り替え、グループ名は独立行を持たせずヘッダのラベル列へ畳む。
            // 投手ビュー（5行）も同じ組にする＝同じ表が選択のたびに行高を変えると比較にならない。
            UiComponents.SetCompareDense(_cmpRows, true);
            _cmpRows.Add(UiComponents.CompareHeader(capA, capB, pitcherView ? "投球" : "打撃・走守"));
            foreach (var (label, x, y) in CompareRows(outgoing, incoming, pitcherView))
            {
                if (!pitcherView && label == AbilityLabels.Jp(AbilityKind.LaunchTendency))
                    _cmpRows.Add(CompareRowEl(label, x, y, TypeText(x), TypeText(y), hideBar: true));
                else
                    _cmpRows.Add(CompareRowEl(label, x, y));
            }

            // 守備適性の帯（issue #452・監督選択の案A）。メンバー設定・スタメン設定と同じく**野手ビューのときだけ**出す。
            // 盤面（252px）はモーダルの残り高さに入らないので、同じ情報を9列×2行の帯へ畳んだ部品を使う。
            if (!pitcherView && _aptitudeGrades != null)
            {
                var aptA = _aptitudeGrades(outgoing?.SourceId);
                var aptB = _aptitudeGrades(incoming?.SourceId);
                if (aptA != null || aptB != null)
                    _cmpRows.Add(UiComponents.AptitudeStrip(aptA, aptB, capA, capB));
            }
        }

        /// <summary>左カード（退く側）の空状態文言。何も選んでいないとき次の一手を指す。</summary>
        private string EmptyCardTextA()
        {
            if (_opt.IsOffense) return _offKind == SubstitutionKind.PinchRun ? "走者を選ぶ" : "次打者なし";
            return "枠を選ぶ";
        }

        /// <summary>右カード（入る側）の空状態文言。</summary>
        private string EmptyCardTextB()
        {
            if (_opt.IsOffense) return "控えを選ぶ";
            return Picked(_pickedSlot) == null ? "—" : "控えか枠を選ぶ";
        }

        private (Player Out, Player In, bool PitcherView, string CapA, string CapB) CompareSubjects()
        {
            if (_opt.IsOffense)
            {
                var outP = _offKind == SubstitutionKind.PinchHit
                    ? (_opt.UpcomingBatterSlot >= 0 ? _opt.Lineup[_opt.UpcomingBatterSlot] : null)
                    : RunnerAt(_baseIndex);
                var inP = _offInIndex >= 0 && _offInIndex < _opt.Reserves.Count ? _opt.Reserves[_offInIndex] : null;
                return (outP, inP, false, "退く", "入る");
            }

            var op = ResolveDef();
            switch (op.Kind)
            {
                case DefKind.Relieve:
                case DefKind.Convert:
                    return (_opt.CurrentPitcher, op.Incoming, true, "退く", "入る");
                case DefKind.PromoteFielder:
                    // 非DHは入れ替え＝現投手は退場せず守備へ回る。「退く」でなく「降板／登板」で呼ぶ（issue #452）。
                    return (_opt.CurrentPitcher, op.Incoming, true, "降板", "登板");
                case DefKind.FielderSwap:
                    return (_slots[_pickedSlot].Player, _slots[_pickedSlot2].Player, false, "1人目", "2人目");
                case DefKind.Assign:
                    return (op.Outgoing, op.Incoming, false, "退く", "入る");
                case DefKind.DhToPos:
                    return (op.Outgoing, _slots[_pickedSlot].Role == SlotRole.Dh ? _slots[_pickedSlot].Player : _slots[_pickedSlot2].Player,
                        false, "退く", "DH");
                default:
                    var a = Picked(_pickedSlot);
                    return (a?.Player, null, a is { Role: SlotRole.Pitcher }, "退く", "入る");
            }
        }

        // ── 注記＋確定 ──

        private void BuildFooter()
        {
            var (ready, note, warn) = FooterState();
            if (_note != null)
            {
                _note.text = note;
                _note.EnableInClassList("msub-note--warn", warn);
            }
            if (_ok != null)
            {
                _ok.text = _opt.IsOffense
                    ? (_offKind == SubstitutionKind.PinchRun ? "この代走を確定 ▶" : "この代打を確定 ▶")
                    : "この操作を確定 ▶";
                _ok.SetEnabled(ready);
            }
        }

        private (bool Ready, string Note, bool Warn) FooterState()
        {
            if (_opt.IsFinished) return (false, "試合が終了している。", true);
            if (_opt.IsOffense)
            {
                if (_offKind == SubstitutionKind.PinchHit)
                {
                    if (_opt.Reserves.Count == 0) return (false, "控えが残っていない。", true);
                    return _offInIndex >= 0
                        ? (true, "代打を送る。退いた選手は再出場できない。", false)
                        : (false, "控えから代打を選ぶ。", false);
                }
                if (_opt.Runners.Count == 0) return (false, "塁上に走者がいない。", true);
                if (_opt.Reserves.Count == 0) return (false, "控えが残っていない。", true);
                if (_baseIndex < 0) return (false, "代走に出す走者を選ぶ。", false);
                return _offInIndex >= 0
                    ? (true, "代走を送る。退いた選手は再出場できない。", false)
                    : (false, "控えから代走を選ぶ。", false);
            }

            var op = ResolveDef();
            switch (op.Kind)
            {
                case DefKind.Relieve:
                    return (true, NameOf(op.Incoming) + " が登板し、" + NameOf(_opt.CurrentPitcher) + " は退場（再登板できない）。", false);
                case DefKind.Convert:
                    return (true, NameOf(op.Incoming) + " が登板。" + NameOf(_opt.CurrentPitcher) + " は " + PosJp(op.Pos)
                        + " を守る（後で再登板できる）。" + PosJp(op.Pos) + " の " + NameOf(op.Outgoing) + " が退場。", false);
                case DefKind.PromoteFielder:
                    // 単純な入れ替え。非DHは誰も退場しない＝元投手は後で再登板できる（issue #452）。
                    return _opt.UsesDh
                        ? (true, NameOf(op.Incoming) + " をマウンドへ。DHの " + NameOf(SwapBackfiller()) + " が "
                            + PosJp(op.Pos) + " を守り、" + NameOf(_opt.CurrentPitcher)
                            + " は退場（以降DHなし）。", false)
                        : (true, NameOf(op.Incoming) + " と " + NameOf(_opt.CurrentPitcher) + " を入れ替える。"
                            + NameOf(_opt.CurrentPitcher) + " は " + PosJp(op.Pos)
                            + " を守る（誰も退場しない・後で再登板できる）。", false);
                case DefKind.FielderSwap:
                    return (true, "守備位置を入れ替える（誰も退場しない・何度でも行える）。", false);
                case DefKind.Assign:
                    return (true, NameOf(op.Incoming) + " が " + PosJp(op.Pos) + " に就き、" + NameOf(op.Outgoing)
                        + " が退場（再出場できない）。", false);
                case DefKind.DhToPos:
                    return (true, "DHが " + PosJp(op.Pos) + " を守り、" + NameOf(op.Outgoing)
                        + " が退場。投手が打順へ入る（以降DHなし）。", false);
                case DefKind.DhWithdraw:
                    return (true, "DHをそのまま退かせ、投手が打席へ入る（以降DHなし）。", false);
                case DefKind.Invalid:
                    return (false, "この組み合わせでは操作できない（投⇄指・控え→指は不可）。", true);
                default:
                    var a = Picked(_pickedSlot);
                    if (a is { Role: SlotRole.Pitcher }) return (false, "控えを選べば継投／別の守備枠を選べば投野交代。", false);
                    if (a is { Role: SlotRole.Dh }) return (false, "守備位置の枠を選べばDH解除／控えは指枠に就けない。", false);
                    if (a != null) return (false, "別の守備枠を選べば入替／控えを選べば守備固め。", false);
                    return (false, "動かす守備の枠を選ぶ。", false);
            }
        }

        private void Apply()
        {
            if (_prog == null || _opt == null) return;
            // 解決中（背景スレッドで _prog.Advance() 実行中）は engine の進行体をメインスレッドから触らない
            // （issue #288／#278 と同型の予防）。モーダルは _paused 中しか開かない建付けなので通常ここは
            // 立たないが、押下配信は1フレーム遅れうるため二重に守る。競合させると進行体を奪い合い、
            // 最悪 Unity ごとフリーズする（背景 Advance の列挙状態とラインナップの同時変更）。
            if (BackgroundGameOp.Running) return;
            bool ok;
            if (_opt.IsOffense)
            {
                if (_offInIndex < 0 || _offInIndex >= _opt.Reserves.Count) return;
                ok = _offKind == SubstitutionKind.PinchRun
                    ? _baseIndex >= 0 && _prog.PinchRun(_teamIsAway, _baseIndex, _opt.Reserves[_offInIndex])
                    : _prog.PinchHit(_teamIsAway, _opt.Reserves[_offInIndex]);
            }
            else
            {
                var op = ResolveDef();
                ok = op.Kind switch
                {
                    DefKind.Relieve => _prog.Relieve(_teamIsAway, op.Incoming),
                    DefKind.Convert => _prog.ConvertPitcherAndRelieve(_teamIsAway, op.Pos, op.Incoming),
                    // 投野交代は「入れ替え」で確定する＝控えからの自動補充をしない（issue #452）。
                    // Relieve は AI と共用のルータで、条件次第で控えが入って1人退場する枝へ倒れるため使わない。
                    DefKind.PromoteFielder => _prog.SwapPitcherWithFielder(_teamIsAway, op.Incoming),
                    DefKind.FielderSwap => _prog.SwapDefensivePositions(_teamIsAway, op.SlotA, op.SlotB),
                    DefKind.Assign => _prog.DefenseAssign(_teamIsAway, op.SlotA, op.Incoming),
                    DefKind.DhToPos => _prog.ReleaseDh(_teamIsAway, op.Pos),
                    DefKind.DhWithdraw => _prog.ReleaseDh(_teamIsAway, null),
                    _ => false,
                };
            }

            if (!ok)
            {
                // 局面がずれた（この操作はもう成立しない）。選択肢を引き直して理由を出す。
                _opt = _prog.SubstitutionOptions(_teamIsAway);
                ResetSelection();
                Render();
                if (_note != null)
                {
                    _note.text = "この操作はもう行えない（局面が変わった）。";
                    _note.AddToClassList("msub-note--warn");
                }
                return;
            }
            Close();
            _onApplied?.Invoke();
        }

        // ── 表示ヘルパ ──

        private Player RunnerAt(int baseIndex)
        {
            foreach (var r in _opt.Runners)
                if (r.BaseIndex == baseIndex) return r.Runner;
            return null;
        }

        private int BaseOf(Player p)
        {
            foreach (var r in _opt.Runners)
                if (r.Runner == p) return r.BaseIndex;
            return -1;
        }

        private static string BaseName(int i) => i == 0 ? "一塁" : i == 1 ? "二塁" : "三塁";

        /// <summary>
        /// 打順行（スタメン設定と同じ lineup-row 部品）。chipActive=true で守備位置チップを操作対象として活かす。
        /// previewTo を渡すと「旧名 → previewTo」の即時プレビュー行になり（issue #452 指摘2）、
        /// onCancel があれば行末に ✕（保留中の操作の選び直し）を出す。プレビュー中は投打の付帯情報を
        /// 省く（比較は右カードが担う）。
        /// </summary>
        private VisualElement LineupRow(int order, Player p, string posText, bool selectable, bool picked, string tag, bool chipActive,
            string previewTo = null, System.Action onCancel = null)
        {
            var row = new VisualElement();
            row.AddToClassList("lineup-row");
            if (picked) row.AddToClassList("lineup-row--picked");
            if (!selectable && !picked) row.AddToClassList("msub-row--off");

            var ord = new Label(order > 0 ? order.ToString() : "—");
            ord.AddToClassList("lineup-row__ord");
            row.Add(ord);

            var posCell = new VisualElement();
            posCell.AddToClassList("lineup-row__pos");
            var chip = new Label(posText);
            chip.AddToClassList("pos-chip");
            if (chipActive) { if (picked) chip.AddToClassList("pos-chip--picked"); }
            else chip.AddToClassList("pos-chip--fixed");
            posCell.Add(chip);
            row.Add(posCell);

            if (!string.IsNullOrEmpty(tag))
            {
                var t = new Label(tag);
                t.AddToClassList("msub-base-tag");
                row.Add(t);
            }

            var name = new Label(NameOf(p));
            name.AddToClassList("lineup-row__name");
            if (previewTo != null) name.AddToClassList("lineup-row__name--out");
            row.Add(name);

            if (previewTo != null)
            {
                var arrow = new Label("→");
                arrow.AddToClassList("lineup-row__arrow");
                row.Add(arrow);
                var incoming = new Label(previewTo);
                incoming.AddToClassList("lineup-row__incoming");
                incoming.AddToClassList("f-body-bd");
                row.Add(incoming);
            }
            else
            {
                var info = new Label(HandLabel(p));
                info.AddToClassList("lineup-row__info");
                row.Add(info);
            }

            if (onCancel != null)
            {
                var x = new Button(() => onCancel()) { text = "✕" };
                x.AddToClassList("lineup-row__x");
                x.RegisterCallback<ClickEvent>(e => e.StopPropagation());   // 行クリック（枠選択）に化けさせない
                row.Add(x);
            }

            return row;
        }

        private static VisualElement ChoiceRow(string text, bool picked)
        {
            var row = new VisualElement();
            row.AddToClassList("bench-row");
            if (picked) row.AddToClassList("bench-row--picked");
            var name = new Label(text);
            name.AddToClassList("bench-row__name");
            // 選択肢は選手名でなく**文**（「退場させる（通常の継投）」等）なので折り返させる。
            // bench-row__name は名前用に1行省略なので、そのままだと控え列を詰めた分だけ
            // 文末が「…」で落ちて何を選ぶのか読めない（issue #452）。
            name.AddToClassList("msub-choice__text");
            row.Add(name);
            return row;
        }

        /// <summary>控え行（スタメン設定と同じ bench-row 部品）。タグは選手本来の守備位置。</summary>
        private VisualElement BenchRow(Player p, bool dim, bool picked, string note)
        {
            var row = new VisualElement();
            row.AddToClassList("bench-row");
            if (picked) row.AddToClassList("bench-row--picked");
            if (dim) row.AddToClassList("msub-row--off");

            var name = new Label(NameOf(p));
            name.AddToClassList("bench-row__name");
            row.Add(name);

            var tag = new Label(PosJp(p.Position));
            tag.AddToClassList("bench-row__tag");
            row.Add(tag);

            if (!string.IsNullOrEmpty(note))
            {
                var n = new Label(note);
                n.AddToClassList("bench-row__tag");
                row.Add(n);
            }
            return row;
        }

        private void FillCard(string host, Player p, string placeholder, bool pitcherView)
        {
            var el = _root.Q<VisualElement>(host);
            if (el == null) return;
            el.Clear();
            if (p == null)
            {
                var empty = new Label(placeholder);
                empty.AddToClassList("cmp-card__empty");
                el.Add(empty);
                return;
            }
            var name = new Label(NameOf(p));
            name.AddToClassList("cmp-card__name");
            el.Add(name);

            var meta = new VisualElement();
            meta.AddToClassList("cmp-card__meta");
            var hand = new Label(HandLabel(p));
            hand.AddToClassList("bench-row__tag");
            meta.Add(hand);
            el.Add(meta);

            // 持ち球のミニ球種チャート（部品 PitchChartView・compact）。メンバー設定・スタメン設定と同じく
            // 投手ビューのときだけ出す（ビューでゲート・投/野区分ではゲートしない, issue #93/#293）。
            // 継投は「同じキレでもスライダーかフォークか」で判断が変わる（監督決定 2026-08-09）。
            if (!pitcherView || _pitchChart == null) return;
            var pitches = _pitchChart(p);
            if (pitches == null || pitches.Count == 0) return;
            var chartHost = new VisualElement();
            chartHost.AddToClassList("cmp-card__pitch");
            el.Add(chartHost);
            new PitchChartView(chartHost, compact: true).SetData(pitches);
        }

        private static IEnumerable<(string Label, int A, int B)> CompareRows(Player a, Player b, bool pitcherView)
        {
            if (pitcherView)
            {
                // 並び・項目はエンジンの単一ソース AbilityKinds.Pitching（球速/制球/スタミナ/キレ/球威）に合わせる
                // ＝メンバー設定・スタメン設定・練習計画と同じ順（監督決定 2026-08-09）。精神力は AbilityKind 外の
                // 項目なのでここで足し、向こう（PlayerCompareTabs）の投手能力タブにも同じ位置で足してある。
                // 球速はこの表に出さない＝カードの持ち球チャートのストレートに km/h で添える
                // （監督決定 2026-08-09, issue #560。メンバー設定・スタメン設定と同じ扱い）。
                yield return (AbilityLabels.Jp(AbilityKind.Control), a?.Pitching?.Control ?? -1, b?.Pitching?.Control ?? -1);
                yield return (AbilityLabels.Jp(AbilityKind.Stamina), Stamina(a), Stamina(b));
                yield return (AbilityLabels.Jp(AbilityKind.PitchRank), a?.Pitching?.PitchRank ?? -1, b?.Pitching?.PitchRank ?? -1);
                yield return (AbilityLabels.Jp(AbilityKind.Stuff), Stuff(a), Stuff(b));
                yield return ("精神力", a?.Mental ?? -1, b?.Mental ?? -1);
                yield break;
            }
            // 並びはメンバー設定・スタメン設定（PlayerCompareTabs）と同じ相対順にする＝画面をまたいでも目が滑らない。
            // 盗塁・走塁は代走の判断に要るのに出ていなかったので足した（監督決定 2026-08-09）。
            // ここに無いのは バント／送球／リード の3つで、これは向こうにだけ有る（画面ごとの差は承知の上）。
            yield return ("弾道", a?.LaunchTendency ?? -1, b?.LaunchTendency ?? -1);
            yield return ("ミート", a?.Contact ?? -1, b?.Contact ?? -1);
            yield return ("パワー", a?.Power ?? -1, b?.Power ?? -1);
            yield return ("選球眼", a?.Discipline ?? -1, b?.Discipline ?? -1);
            yield return ("走力", a?.Speed ?? -1, b?.Speed ?? -1);
            yield return ("盗塁", a?.Steal ?? -1, b?.Steal ?? -1);
            yield return ("走塁", a?.Baserunning ?? -1, b?.Baserunning ?? -1);
            yield return ("守備", a?.Fielding ?? -1, b?.Fielding ?? -1);
            yield return ("捕球", a?.Catching ?? -1, b?.Catching ?? -1);
            yield return ("肩", a?.ArmStrength ?? -1, b?.ArmStrength ?? -1);
            yield return ("精神力", a?.Mental ?? -1, b?.Mental ?? -1);
        }

        // スタミナは Level（0〜100）で出す＝メンバー設定・スタメン設定と同じもの（監督決定 2026-08-09）。
        // 試合用の Player が持つのは目安投球数（issue #356）だけなので、順変換と同じ定数から出す
        // エンジンの逆変換で戻す（UI側で式を作り直さない・不変条件#1）。上限なしなので Lv100超はそのまま。
        private static int Stamina(Player p)
            => p?.Pitching == null ? -1 : PitcherAttributes.LevelFromStaminaPitches(p.Pitching.StaminaPitches);

        // 球威＝ストレートの有効軸（issue #274）。試合用の Player は育成能力を持たないので、
        // レパートリーのストレート枠の Power から引く（RosterTeamBuilder が Stuff レベルを入れている値）。
        private static int Stuff(Player p)
        {
            if (p?.Pitching == null) return -1;
            foreach (var slot in p.Pitching.EffectiveRepertoire)
                if (slot.Type == PitchType.Fastball) return slot.Power;
            return -1;
        }

        // 単位つきの値（旧・スタミナの「135球」）はスタミナのLevel化で無くなった＝unit は取らない。
        private static VisualElement CompareRowEl(string label, int a, int b, string textA = null, string textB = null, bool hideBar = false)
            => UiComponents.CompareRow(new CompareRowData
            {
                Label = label,
                ValueA = a, ValueB = b,
                HasA = a >= 0, HasB = b >= 0,
                Winner = hideBar || a < 0 || b < 0 ? 0 : a > b ? -1 : b > a ? 1 : 0,
                TextA = a >= 0 ? textA : null,
                TextB = b >= 0 ? textB : null,
                // ランク併記（監督決定 2026-08-09）。メンバー設定・スタメン設定（PlayerCompareTabs）と
                // 同じ規則にする＝弾道のようなタイプ軸（優劣なし＝hideBar）以外の全行にチップを出し、
                // 段はエンジンの Tiers.FromStrength 一本（UI側で帯を作り直さない, issue #326②）。
                GradeA = hideBar || a < 0 ? "" : KokoSim.Engine.Nation.Tiers.FromStrength(a).ToString(),
                GradeB = hideBar || b < 0 ? "" : KokoSim.Engine.Nation.Tiers.FromStrength(b).ToString(),
                HideBar = hideBar,
            });

        private static string TypeText(int launchTendency)
            => launchTendency < 0 ? null : LaunchTendencyLabels.Jp(launchTendency);

        private static string NameOf(Player p)
            => p == null ? "—" : p.UniformNumber > 0 ? p.UniformNumber + "  " + p.Name : p.Name;

        private static string HandLabel(Player p) => p == null ? "" : HandednessLabels.Combined(p.Throws, p.Bats);

        private static string PosJp(FieldPosition pos) => pos switch
        {
            FieldPosition.Pitcher => "投",
            FieldPosition.Catcher => "捕",
            FieldPosition.FirstBase => "一",
            FieldPosition.SecondBase => "二",
            FieldPosition.ThirdBase => "三",
            FieldPosition.Shortstop => "遊",
            FieldPosition.LeftField => "左",
            FieldPosition.CenterField => "中",
            FieldPosition.RightField => "右",
            FieldPosition.DesignatedHitter => "指",
            _ => "—",
        };

        private void Click(string name, System.Action action)
        {
            var btn = _root.Q<Button>(name);
            if (btn != null) btn.clicked += action;
        }
    }
}
