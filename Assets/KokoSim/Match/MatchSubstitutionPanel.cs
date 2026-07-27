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
    /// <b>割当2ペイン</b>に統合した：左＝守備の割当10枠（投・捕・一・二・三・遊・左・中・右・指）、
    /// 中＝控え一覧（役割で分けずフラット・背番号順・出場済みは灰。#93）、右＝能力比較。</para>
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
        private List<Player> _cands = new();                // 控え一覧（ブルペン＋ベンチ・背番号順）
        private int _pickedSlot = -1;    // 主選択の割当枠
        private int _pickedSlot2 = -1;   // 対の割当枠（枠→枠）
        private int _defInIndex = -1;    // 控え一覧の添字（枠→控え）
        private FieldPosition? _convertTo;  // 継投時に元投手を回す守備位置（null=退場）
        private bool _dhWithdraw;        // 指枠を「そのまま退場」させる

        public MatchSubstitutionPanel(VisualElement root, System.Action onApplied)
        {
            _root = root;
            _onApplied = onApplied;
        }

        /// <summary>モーダルを開いている間は裏の進行（自動再生・次の打席へ）を止める。</summary>
        public bool IsOpen => _open;

        public void Bind()
        {
            _scrim = _root.Q<VisualElement>("sub-modal");
            _kinds = _root.Q<VisualElement>("msub-kinds");
            _outRows = _root.Q<VisualElement>("msub-out-rows");
            _inRows = _root.Q<VisualElement>("msub-in-rows");
            _cmpRows = _root.Q<VisualElement>("msub-cmp-rows");
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
                // 継投は最頻操作。開いたら [投] 枠をプリセレクトして「控えを1クリック→確定」で済ませる（issue #256）。
                _pickedSlot = _slots.FindIndex(s => s.Role == SlotRole.Pitcher);
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

        /// <summary>守備割当10枠（投→捕…右→指）と控え一覧（ブルペン＋ベンチ・背番号順）を組む。</summary>
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

            // 控えは役割で分けない＝ブルペン＋ベンチをフラットに背番号順（#93）。
            _cands = _opt.Bullpen.Concat(_opt.Bench).OrderBy(p => p.UniformNumber == 0 ? int.MaxValue : p.UniformNumber)
                .ThenBy(p => p.Name).ToList();
        }

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

            for (var i = 0; i < _opt.Lineup.Count; i++)
            {
                var p = _opt.Lineup[i];
                var slot = i;
                var tag = "";
                var selectable = false;
                var picked = false;
                if (_offKind == SubstitutionKind.PinchHit)
                {
                    if (i == _opt.UpcomingBatterSlot) { tag = "次打者"; picked = true; }
                }
                else
                {
                    var b = BaseOf(p);
                    if (b >= 0) { selectable = true; tag = BaseName(b); picked = _baseIndex == b; }
                }
                var row = LineupRow(i + 1, p, PosJp(p.Position), selectable, picked, tag, chipActive: false);
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
            for (var i = 0; i < _opt.Bench.Count; i++)
            {
                var idx = i;
                var row = BenchRow(_opt.Bench[i], dim: false, picked: _offInIndex == i, note: "");
                row.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); _offInIndex = idx; Render(); });
                _inRows.Add(row);
            }
            foreach (var p in _opt.UsedBench) _inRows.Add(BenchRow(p, dim: true, picked: false, note: "出場済"));
            if (_opt.Bench.Count == 0 && _opt.UsedBench.Count == 0)
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
            if (_outsCap != null) _outsCap.text = "守備の割当";

            for (var i = 0; i < _slots.Count; i++)
            {
                var s = _slots[i];
                var slotIdx = i;
                var picked = i == _pickedSlot || i == _pickedSlot2;
                var order = s.LineupSlot >= 0 ? s.LineupSlot + 1 : 0;   // DHの投手は打順外＝"—"
                var row = LineupRow(order, s.Player, PosJp(s.Pos), selectable: true, picked, tag: "", chipActive: true);
                row.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); ClickSlot(slotIdx); Render(); });
                _outRows.Add(row);
            }
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
            foreach (var p in _opt.UsedBullpen.Concat(_opt.UsedBench))
                _inRows.Add(BenchRow(p, dim: true, picked: false, note: "出場済"));
            if (_cands.Count == 0 && !_opt.UsedBullpen.Any() && !_opt.UsedBench.Any())
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
            cap.AddToClassList("msub-cmp-group");
            _inRows.Add(cap);

            var retire = ChoiceRow("退場させる（通常の継投）", _convertTo == null);
            retire.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); _convertTo = null; Render(); });
            _inRows.Add(retire);

            foreach (var s in _slots.Where(s => s.Role == SlotRole.Fielder))
            {
                var pos = s.Pos;
                var row = ChoiceRow(PosJp(pos) + "へ回す（" + NameOf(s.Player) + " が退場）", _convertTo == pos);
                row.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); _convertTo = pos; Render(); });
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

        // ── 右：能力比較 ──

        private void BuildCompare()
        {
            var (outgoing, incoming, pitcherView, capA, capB) = CompareSubjects();
            FillCard("msub-cmp-a", outgoing, capA);
            FillCard("msub-cmp-b", incoming, capB);

            if (_cmpRows == null) return;
            _cmpRows.Clear();
            UiComponents.SetCompareUnitColumn(_cmpRows, pitcherView);
            var group = new Label(pitcherView ? "投球" : "打撃・走守");
            group.AddToClassList("msub-cmp-group");
            _cmpRows.Add(group);
            _cmpRows.Add(UiComponents.CompareHeader(capA, capB));
            foreach (var (label, x, y) in CompareRows(outgoing, incoming, pitcherView))
            {
                if (pitcherView && label == AbilityLabels.Jp(AbilityKind.Velocity))
                    _cmpRows.Add(CompareRowEl(label, x, y, KmhText(outgoing), KmhText(incoming)));
                else if (!pitcherView && label == AbilityLabels.Jp(AbilityKind.LaunchTendency))
                    _cmpRows.Add(CompareRowEl(label, x, y, TypeText(x), TypeText(y), hideBar: true));
                else
                    _cmpRows.Add(CompareRowEl(label, x, y));
            }
        }

        private (Player Out, Player In, bool PitcherView, string CapA, string CapB) CompareSubjects()
        {
            if (_opt.IsOffense)
            {
                var outP = _offKind == SubstitutionKind.PinchHit
                    ? (_opt.UpcomingBatterSlot >= 0 ? _opt.Lineup[_opt.UpcomingBatterSlot] : null)
                    : RunnerAt(_baseIndex);
                var inP = _offInIndex >= 0 && _offInIndex < _opt.Bench.Count ? _opt.Bench[_offInIndex] : null;
                return (outP, inP, false, "退く", "入る");
            }

            var op = ResolveDef();
            switch (op.Kind)
            {
                case DefKind.Relieve:
                case DefKind.Convert:
                    return (_opt.CurrentPitcher, op.Incoming, true, "退く", "入る");
                case DefKind.PromoteFielder:
                    return (_opt.CurrentPitcher, op.Incoming, true, "退く投手", "登板野手");
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
                    if (_opt.Bench.Count == 0) return (false, "控えの野手が残っていない。", true);
                    return _offInIndex >= 0
                        ? (true, "代打を送る。退いた選手は再出場できない。", false)
                        : (false, "控えから代打を選ぶ。", false);
                }
                if (_opt.Runners.Count == 0) return (false, "塁上に走者がいない。", true);
                if (_opt.Bench.Count == 0) return (false, "控えの野手が残っていない。", true);
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
                    return (true, NameOf(op.Incoming) + " をマウンドへ（投野交代）。空いた " + PosJp(op.Pos)
                        + " は自動で補充する（控えが入る場合は1人退場することがある）。", false);
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
                if (_offInIndex < 0 || _offInIndex >= _opt.Bench.Count) return;
                ok = _offKind == SubstitutionKind.PinchRun
                    ? _baseIndex >= 0 && _prog.PinchRun(_teamIsAway, _baseIndex, _opt.Bench[_offInIndex])
                    : _prog.PinchHit(_teamIsAway, _opt.Bench[_offInIndex]);
            }
            else
            {
                var op = ResolveDef();
                ok = op.Kind switch
                {
                    DefKind.Relieve => _prog.Relieve(_teamIsAway, op.Incoming),
                    DefKind.Convert => _prog.ConvertPitcherAndRelieve(_teamIsAway, op.Pos, op.Incoming),
                    DefKind.PromoteFielder => _prog.Relieve(_teamIsAway, op.Incoming),
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

        /// <summary>打順行（スタメン設定と同じ lineup-row 部品）。chipActive=true で守備位置チップを操作対象として活かす。</summary>
        private VisualElement LineupRow(int order, Player p, string posText, bool selectable, bool picked, string tag, bool chipActive)
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
            row.Add(name);

            var info = new Label(HandLabel(p));
            info.AddToClassList("lineup-row__info");
            row.Add(info);

            return row;
        }

        private static VisualElement ChoiceRow(string text, bool picked)
        {
            var row = new VisualElement();
            row.AddToClassList("bench-row");
            if (picked) row.AddToClassList("bench-row--picked");
            var name = new Label(text);
            name.AddToClassList("bench-row__name");
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

        private void FillCard(string host, Player p, string placeholder)
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
        }

        private static IEnumerable<(string Label, int A, int B)> CompareRows(Player a, Player b, bool pitcherView)
        {
            if (pitcherView)
            {
                yield return (AbilityLabels.Jp(AbilityKind.Velocity), Velocity(a), Velocity(b));
                yield return (AbilityLabels.Jp(AbilityKind.Control), a?.Pitching?.Control ?? -1, b?.Pitching?.Control ?? -1);
                yield return (AbilityLabels.Jp(AbilityKind.PitchRank), a?.Pitching?.PitchRank ?? -1, b?.Pitching?.PitchRank ?? -1);
                yield return (AbilityLabels.Jp(AbilityKind.Stamina), Stamina(a), Stamina(b));
                yield return ("精神力", a?.Mental ?? -1, b?.Mental ?? -1);
                yield break;
            }
            yield return ("ミート", a?.Contact ?? -1, b?.Contact ?? -1);
            yield return ("パワー", a?.Power ?? -1, b?.Power ?? -1);
            yield return ("弾道", a?.LaunchTendency ?? -1, b?.LaunchTendency ?? -1);
            yield return ("選球眼", a?.Discipline ?? -1, b?.Discipline ?? -1);
            yield return ("走力", a?.Speed ?? -1, b?.Speed ?? -1);
            yield return ("肩", a?.ArmStrength ?? -1, b?.ArmStrength ?? -1);
            yield return ("守備", a?.Fielding ?? -1, b?.Fielding ?? -1);
            yield return ("捕球", a?.Catching ?? -1, b?.Catching ?? -1);
            yield return ("精神力", a?.Mental ?? -1, b?.Mental ?? -1);
        }

        private static int Velocity(Player p)
            => p?.Pitching == null ? -1 : PitcherAttributes.LevelFromVelocityKmh(p.Pitching.MaxVelocityKmh);

        private static int Stamina(Player p)
            => p?.Pitching == null ? -1 : UnityEngine.Mathf.Clamp(UnityEngine.Mathf.RoundToInt((float)p.Pitching.StaminaPitches * 100f / 160f), 0, 100);

        private static VisualElement CompareRowEl(string label, int a, int b, string textA = null, string textB = null, bool hideBar = false)
            => UiComponents.CompareRow(new CompareRowData
            {
                Label = label,
                ValueA = a, ValueB = b,
                HasA = a >= 0, HasB = b >= 0,
                Winner = hideBar || a < 0 || b < 0 ? 0 : a > b ? -1 : b > a ? 1 : 0,
                TextA = a >= 0 ? textA : null,
                TextB = b >= 0 ? textB : null,
                HideBar = hideBar,
            });

        private static string KmhText(Player p)
            => p?.Pitching == null ? null : (int)System.Math.Round(p.Pitching.MaxVelocityKmh) + "km/h";

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
