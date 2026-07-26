using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;      // AbilityKind（能力ラベル単一ソースの引数, issue #94）
using KokoSim.Unity.Components;   // 部品辞書（CompareRow等）
using KokoSim.Unity.Shell;        // 能力ラベル単一ソース（AbilityLabels, issue #94）
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 試合中の選手交代モーダル（設計書09 §6 / issue #22 C）。MatchLive.uxml の <c>sub-modal</c> にバインドし、
    /// 代打・代走・投手交代（指名）・守備交代・DH解除の5種を1つのカードで出し分ける。
    ///
    /// 見た目はスタメン設定（LineupSetting）と同じ部品辞書（lineup-row / bench-row / pos-chip / rank-chip /
    /// cmp-card / cmp-row）を使い、配置だけ MatchSubstitution.uss が持つ。判断ロジックはエンジンの
    /// <see cref="SubstitutionOptions"/> に委ね、ここは表示と1回の実行呼び出しだけを行う（不変条件#3 エンジン純度）。
    ///
    /// スタメン設定との違い（issue #22 C）:
    ///   ・退場済み／使用済みの選手はグレーアウトして選べない（高校野球＝リエントリー禁止）
    ///   ・確定は即時・取り消し不可（プレビューもロールバックも無い）
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
        private SubstitutionKind _kind = SubstitutionKind.PinchHit;

        // 選択中の「退く側」と「入る側」。DH解除だけは _dhAt（守備位置 or null＝そのまま退場）を使う。
        private int _outIndex = -1;      // 代打/代走/守備交代=打順スロット, 代走は塁の添字を _baseIndex で別持ち
        private int _baseIndex = -1;
        private int _inIndex = -1;       // 控え（Bench）またはブルペン（Bullpen）の添字
        private FieldPosition? _dhAt;
        private bool _dhChoiceMade;

        // 守備位置まわり（issue #235）。
        private int _swapA = -1, _swapB = -1;   // 「守備位置」タブで入れ替える2つの打順スロット
        private FieldPosition? _subAt;          // 守備交代で入る選手が就く守備位置（null＝退く選手の位置を継承）
        private FieldPosition? _convertTo;      // 投手交代で元投手を残す守備位置（null＝退場＝通常の継投）

        public MatchSubstitutionPanel(VisualElement root, System.Action onApplied)
        {
            _root = root;
            _onApplied = onApplied;
        }

        private bool _open;

        /// <summary>モーダルを開いている間は裏の進行（自動再生・次の打席へ）を止める。</summary>
        public bool IsOpen => _open;

        /// <summary>UIDocument の要素を引き当てる（MatchLiveController の OnEnable から1回だけ呼ぶ）。</summary>
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
            // 局面に合う種別から開く（攻撃中＝代打 / 守備中＝投手交代）。
            _kind = _opt.IsOffense ? SubstitutionKind.PinchHit : SubstitutionKind.ChangePitcher;
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

        /// <summary>スクショ用: 種別タブを選ぶ（0=代打 1=代走 2=投手交代 3=守備交代 4=守備位置 5=DH解除）。</summary>
        public void SelectKindForCapture(int index)
        {
            if (!_open || index < 0 || index >= KindTabs.Length) return;
            _kind = KindTabs[index].Kind;
            ResetSelection();
            Render();
        }

        private void ResetSelection()
        {
            _outIndex = -1;
            _baseIndex = -1;
            _inIndex = -1;
            _dhAt = null;
            _dhChoiceMade = false;
            _swapA = -1;
            _swapB = -1;
            _subAt = null;
            _convertTo = null;
            // 代打・投手交代は「退く側」が局面で一意に決まるので最初から選んでおく。
            if (_kind == SubstitutionKind.PinchHit) _outIndex = _opt.UpcomingBatterSlot;
        }

        // ── 描画 ──

        private void Render()
        {
            if (_opt == null) return;
            if (_phase != null) _phase.text = _opt.IsFinished ? "試合終了" : _opt.IsOffense ? "攻撃中" : "守備中";
            BuildKinds();
            BuildOutRows();
            BuildInRows();
            BuildCompare();
            BuildFooter();
        }

        private static readonly (SubstitutionKind Kind, string Label)[] KindTabs =
        {
            (SubstitutionKind.PinchHit, "代打"),
            (SubstitutionKind.PinchRun, "代走"),
            (SubstitutionKind.ChangePitcher, "投手交代"),
            (SubstitutionKind.DefensiveSub, "守備交代"),
            // 選手の出入りが無い＝交代回数を消費しない操作は別タブに分ける（境界が見えるようにする, issue #235）。
            (SubstitutionKind.SwapDefense, "守備位置"),
            (SubstitutionKind.ReleaseDh, "DH解除"),
        };

        private void BuildKinds()
        {
            if (_kinds == null) return;
            _kinds.Clear();
            foreach (var (kind, label) in KindTabs)
            {
                var cell = new Label(label);
                cell.AddToClassList("seg__cell");
                var enabled = CanUse(kind);
                if (kind == _kind) cell.AddToClassList("seg__cell--on");
                else if (!enabled) cell.AddToClassList("seg__cell--off");
                var k = kind;
                cell.RegisterCallback<ClickEvent>(e =>
                {
                    e.StopPropagation();
                    _kind = k;             // 選べない種別も選択は許し、理由を1行で出す（issue #22 C）
                    ResetSelection();
                    Render();
                });
                _kinds.Add(cell);
            }
        }

        private bool CanUse(SubstitutionKind kind) => kind switch
        {
            SubstitutionKind.PinchHit => _opt.CanPinchHit,
            SubstitutionKind.PinchRun => _opt.CanPinchRun,
            SubstitutionKind.ChangePitcher => _opt.CanChangePitcher,
            SubstitutionKind.DefensiveSub => _opt.CanDefensiveSub,
            SubstitutionKind.SwapDefense => _opt.CanSwapDefense,
            SubstitutionKind.ReleaseDh => _opt.CanReleaseDh,
            _ => false,
        };

        // ── 左：退く選手 ──

        private void BuildOutRows()
        {
            if (_outRows == null) return;
            _outRows.Clear();
            if (_outsCap != null)
                _outsCap.text = _kind switch
                {
                    SubstitutionKind.PinchRun => "退く走者",
                    SubstitutionKind.ChangePitcher => "降板する投手",
                    SubstitutionKind.ReleaseDh => "打順（DHの位置）",
                    SubstitutionKind.SwapDefense => "守備（2つクリックで入替）",
                    _ => "退く選手",
                };

            if (_kind == SubstitutionKind.ChangePitcher)
            {
                _outRows.Add(LineupRow(0, _opt.CurrentPitcher, selectable: false, picked: true, tag: "登板中"));
                return;
            }

            for (var i = 0; i < _opt.Lineup.Count; i++)
            {
                var p = _opt.Lineup[i];
                var slot = i;
                var tag = "";
                var selectable = false;
                switch (_kind)
                {
                    case SubstitutionKind.PinchHit:
                        selectable = false;                       // 対象は次打者で一意
                        if (i == _opt.UpcomingBatterSlot) tag = "次打者";
                        break;
                    case SubstitutionKind.PinchRun:
                        var b = BaseOf(p);
                        if (b >= 0) { selectable = true; tag = BaseName(b); }
                        break;
                    case SubstitutionKind.DefensiveSub:
                        // 投手の交代は「投手交代」タブに集約する（継投を伴わない入替を作らない）。
                        selectable = p != _opt.CurrentPitcher;
                        if (!selectable) tag = "投手";
                        break;
                    case SubstitutionKind.ReleaseDh:
                        if (_opt.UsesDh && i == _opt.DhSlot) tag = "DH";
                        break;
                    case SubstitutionKind.SwapDefense:
                        // 守備に就いている枠だけ動かせる（投手＝登板の変更なので投手交代タブ、DHは守備に就かない）。
                        selectable = _opt.SwappableSlots.Contains(i);
                        if (!selectable) tag = _opt.UsesDh && i == _opt.DhSlot ? "DH" : "投手";
                        break;
                }
                var picked = _kind == SubstitutionKind.PinchHit ? i == _opt.UpcomingBatterSlot
                    : _kind == SubstitutionKind.ReleaseDh ? _opt.UsesDh && i == _opt.DhSlot
                    : _kind == SubstitutionKind.SwapDefense ? i == _swapA || i == _swapB
                    : _outIndex == i;
                var row = LineupRow(i + 1, p, selectable, picked, tag);
                if (selectable)
                    row.RegisterCallback<ClickEvent>(e =>
                    {
                        e.StopPropagation();
                        if (_kind == SubstitutionKind.SwapDefense) { PickSwap(slot); Render(); return; }
                        _outIndex = slot;
                        _baseIndex = _kind == SubstitutionKind.PinchRun ? BaseOf(_opt.Lineup[slot]) : -1;
                        Render();
                    });
                _outRows.Add(row);
            }
        }

        /// <summary>
        /// 守備位置の入替の選択（スタメン設定と同じ2クリック操作, issue #233/#235）。
        /// 1つ目→2つ目で組み、同じ枠の再クリックで解除、2つ揃った状態で別の枠を押したら選び直し。
        /// </summary>
        private void PickSwap(int slot)
        {
            if (_swapA == slot) { _swapA = _swapB; _swapB = -1; return; }
            if (_swapB == slot) { _swapB = -1; return; }
            if (_swapA < 0) { _swapA = slot; return; }
            if (_swapB < 0) { _swapB = slot; return; }
            _swapA = slot;
            _swapB = -1;
        }

        private int BaseOf(Player p)
        {
            foreach (var r in _opt.Runners)
                if (r.Runner == p) return r.BaseIndex;
            return -1;
        }

        private static string BaseName(int i) => i == 0 ? "一塁" : i == 1 ? "二塁" : "三塁";

        /// <summary>打順行（スタメン設定と同じ lineup-row 部品）。</summary>
        private VisualElement LineupRow(int order, Player p, bool selectable, bool picked, string tag)
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
            var chip = new Label(PosJp(p.Position));
            chip.AddToClassList("pos-chip");
            // 「守備位置」タブでは守備位置そのものが操作対象なので、動かせる枠のチップは活きて見せる
            // （動かせない投手・DHだけ --fixed）。他のタブでは全部が表示専用（issue #235）。
            if (_kind == SubstitutionKind.SwapDefense && selectable)
            {
                if (picked) chip.AddToClassList("pos-chip--picked");
            }
            else
            {
                chip.AddToClassList("pos-chip--fixed");
            }
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

        // ── 中：入る選手 ──

        private void BuildInRows()
        {
            if (_inRows == null) return;
            _inRows.Clear();
            if (_kind == SubstitutionKind.ReleaseDh)
            {
                if (_insCap != null) _insCap.text = "DHの行き先";
                BuildDhChoices();
                return;
            }
            if (_kind == SubstitutionKind.SwapDefense)
            {
                if (_insCap != null) _insCap.text = "入替の内容";
                BuildSwapPreview();
                return;
            }

            var usePen = _kind == SubstitutionKind.ChangePitcher;
            if (_insCap != null) _insCap.text = usePen ? "投手候補" : "控え";

            // 投手交代の候補はブルペン＋ベンチ（野手含む・issue #137）。
            var available = usePen ? _opt.PitcherCandidates : _opt.Bench;
            for (var i = 0; i < available.Count; i++)
            {
                var idx = i;
                var row = BenchRow(available[i], dim: false, picked: _inIndex == i, note: "");
                row.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); _inIndex = idx; Render(); });
                _inRows.Add(row);
            }

            // 使い切った控え／登板済みの投手はグレーアウトで残す（リエントリー禁止が一目で分かる）。
            var used = usePen ? _opt.UsedPitcherCandidates : _opt.UsedBench;
            foreach (var p in used) _inRows.Add(BenchRow(p, dim: true, picked: false, note: "出場済"));

            if (available.Count == 0 && used.Count == 0)
            {
                var empty = new Label("控えが登録されていない。");
                empty.AddToClassList("cmp-card__empty");
                _inRows.Add(empty);
            }

            // 入る選手を決めたあとの追加選択（issue #235）。既定は従来どおりなので、選ばなければ挙動は変わらない。
            if (_kind == SubstitutionKind.DefensiveSub && _outIndex >= 0 && _inIndex >= 0) BuildSubPositionChoices();
            else if (_kind == SubstitutionKind.ChangePitcher && _inIndex >= 0) BuildConvertChoices();
        }

        /// <summary>「守備位置」タブの中列＝いま何と何を入れ替えるのかの確認（選択そのものは左列で行う）。</summary>
        private void BuildSwapPreview()
        {
            var note = new Label("選手は動かず、守備位置だけを交換する。交代回数は消費しない。");
            note.AddToClassList("msub-hint");
            _inRows.Add(note);
            if (_swapA < 0) return;

            _inRows.Add(ChoiceRow(SlotLabel(_swapA), picked: true));
            if (_swapB < 0)
            {
                var more = new Label("もう1人をクリックする。");
                more.AddToClassList("msub-hint");
                _inRows.Add(more);
                return;
            }
            _inRows.Add(ChoiceRow(SlotLabel(_swapB), picked: true));
            _inRows.Add(ChoiceRow(
                PosJp(_opt.Lineup[_swapA].Position) + " ⇄ " + PosJp(_opt.Lineup[_swapB].Position), picked: false));
        }

        private string SlotLabel(int slot)
            => (slot + 1) + "番 " + PosJp(_opt.Lineup[slot].Position) + "  " + NameOf(_opt.Lineup[slot]);

        /// <summary>守備交代で入る選手が就く守備位置（既定＝退く選手の位置を継承）。任意割当の入口（issue #235）。</summary>
        private void BuildSubPositionChoices()
        {
            var cap = new Label("就く守備位置");
            cap.AddToClassList("msub-cmp-group");
            _inRows.Add(cap);

            var inherited = _opt.Lineup[_outIndex].Position;
            var keep = ChoiceRow("そのまま（" + PosJp(inherited) + "）", _subAt == null);
            keep.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); _subAt = null; Render(); });
            _inRows.Add(keep);

            foreach (var slot in _opt.SwappableSlots)
            {
                var pos = _opt.Lineup[slot].Position;
                if (pos == inherited) continue;   // 「そのまま」と同じ
                var p = pos;
                var row = ChoiceRow(PosJp(pos) + "（" + NameOf(_opt.Lineup[slot]) + " が " + PosJp(inherited) + " へ）",
                    _subAt == pos);
                row.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); _subAt = p; Render(); });
                _inRows.Add(row);
            }
        }

        /// <summary>投手交代で元投手をどうするか。既定＝退場（従来の継投）／守備位置を選ぶとコンバート継投（issue #235）。</summary>
        private void BuildConvertChoices()
        {
            var cap = new Label("元の投手");
            cap.AddToClassList("msub-cmp-group");
            _inRows.Add(cap);

            var retire = ChoiceRow("退場させる（通常の継投）", _convertTo == null);
            retire.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); _convertTo = null; Render(); });
            _inRows.Add(retire);

            foreach (var slot in _opt.SwappableSlots)
            {
                var pos = _opt.Lineup[slot].Position;
                var p = pos;
                var row = ChoiceRow(PosJp(pos) + "へ回す（" + NameOf(_opt.Lineup[slot]) + " が退場）", _convertTo == pos);
                row.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); _convertTo = p; Render(); });
                _inRows.Add(row);
            }
        }

        private void BuildDhChoices()
        {
            if (!_opt.UsesDh)
            {
                // DH未使用（または解除済み）。理由は下の1行に出るので、ここは選択肢を出さない。
                var none = new Label("選べる行き先はない。");
                none.AddToClassList("cmp-card__empty");
                _inRows.Add(none);
                return;
            }
            // 分岐1: DHの選手がその守備位置に就く（元の守備者が退場）。分岐2: DHはそのまま退場。
            foreach (var pos in _opt.DhFieldingChoices())
            {
                var p = pos;
                var row = ChoiceRow(PosJp(pos) + "を守らせる", _dhChoiceMade && _dhAt == pos);
                row.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); _dhAt = p; _dhChoiceMade = true; Render(); });
                _inRows.Add(row);
            }
            var withdraw = ChoiceRow("そのまま退かせる（投手が打席へ）", _dhChoiceMade && _dhAt == null);
            withdraw.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); _dhAt = null; _dhChoiceMade = true; Render(); });
            _inRows.Add(withdraw);
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

        /// <summary>控え行（スタメン設定と同じ bench-row 部品）。タグは選手本来の守備位置
        /// （投手交代候補の野手はそのまま守備位置を表示＝ブルペン投手は Position が投手なので従来通り「投」）。</summary>
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

        // ── 右：能力比較（スタメン設定と同じ cmp-card / cmp-row 部品） ──

        private void BuildCompare()
        {
            var outgoing = SelectedOutgoing();
            var incoming = SelectedIncoming();
            FillCard("msub-cmp-a", outgoing, "退く選手を選ぶ");
            FillCard("msub-cmp-b", incoming, "入る選手を選ぶ");

            if (_cmpRows == null) return;
            _cmpRows.Clear();
            var pitcherView = _kind == SubstitutionKind.ChangePitcher;
            var group = new Label(pitcherView ? "投球" : "打撃・走守");
            group.AddToClassList("msub-cmp-group");
            _cmpRows.Add(group);
            _cmpRows.Add(_kind == SubstitutionKind.SwapDefense
                ? UiComponents.CompareHeader("1人目", "2人目")   // 入替は「退く／入る」ではない
                : UiComponents.CompareHeader("退く", "入る"));
            foreach (var (label, a, b) in CompareRows(outgoing, incoming, pitcherView))
            {
                // 球速だけ物理量なので km/h テキスト表示（バー幅は Level のまま＝他能力と同じ土俵, issue #94）。
                if (pitcherView && label == AbilityLabels.Jp(AbilityKind.Velocity))
                    _cmpRows.Add(CompareRowEl(label, a, b, KmhText(outgoing), KmhText(incoming)));
                // 弾道は優劣のないタイプ軸なので数値・バーではなくタイプラベルで表示する（issue #219）。
                else if (!pitcherView && label == AbilityLabels.Jp(AbilityKind.LaunchTendency))
                    _cmpRows.Add(CompareRowEl(label, a, b, TypeText(a), TypeText(b), hideBar: true));
                else
                    _cmpRows.Add(CompareRowEl(label, a, b));
            }
        }

        private Player SelectedOutgoing()
        {
            switch (_kind)
            {
                case SubstitutionKind.ChangePitcher: return _opt.CurrentPitcher;
                case SubstitutionKind.SwapDefense:
                    return _swapA >= 0 && _swapA < _opt.Lineup.Count ? _opt.Lineup[_swapA] : null;
                case SubstitutionKind.PinchHit:
                    return _opt.UpcomingBatterSlot >= 0 ? _opt.Lineup[_opt.UpcomingBatterSlot] : null;
                case SubstitutionKind.ReleaseDh:
                    return _opt.UsesDh && _opt.DhSlot >= 0 && _opt.DhSlot < _opt.Lineup.Count
                        ? _opt.Lineup[_opt.DhSlot] : null;
                default:
                    return _outIndex >= 0 && _outIndex < _opt.Lineup.Count ? _opt.Lineup[_outIndex] : null;
            }
        }

        private Player SelectedIncoming()
        {
            if (_kind == SubstitutionKind.ReleaseDh) return null;
            if (_kind == SubstitutionKind.SwapDefense)
                return _swapB >= 0 && _swapB < _opt.Lineup.Count ? _opt.Lineup[_swapB] : null;
            var list = _kind == SubstitutionKind.ChangePitcher ? _opt.PitcherCandidates : _opt.Bench;
            return _inIndex >= 0 && _inIndex < list.Count ? list[_inIndex] : null;
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

        // 球速(km/h)→Level(0-100)は物理量→表示層の逆変換をエンジン公開APIに一本化（独自式廃止, issue #94）。
        // バーは他画面（Member/Lineup/詳細）と同じ Level 土俵に揃う。km/h のテキスト表示は Phase 2 で対応。
        private static int Velocity(Player p)
            => p?.Pitching == null ? -1 : PitcherAttributes.LevelFromVelocityKmh(p.Pitching.MaxVelocityKmh);

        private static int Stamina(Player p)
            => p?.Pitching == null ? -1 : Mathf.Clamp(Mathf.RoundToInt((float)p.Pitching.StaminaPitches * 100f / 160f), 0, 100);

        // 行の見た目は部品辞書（UiComponents.CompareRow）に集約。負値は「その選手がいない」を表す。
        // textA/textB を渡すと値ラベルを上書き（球速の km/h 表示用, issue #94）。バー幅は a/b(Level) のまま。
        // hideBar=true は優劣のないタイプ軸（弾道, issue #219）向け：バーを出さず優劣ハイライトもしない。
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

        // 球速の km/h テキスト（表示専用・変換はエンジン公開APIに集約, issue #94）。投手でなければ空。
        private static string KmhText(Player p)
            => p?.Pitching == null ? null : (int)System.Math.Round(p.Pitching.MaxVelocityKmh) + "km/h";

        // 弾道タイプのテキスト（issue #219）。値が無ければ null（"—" 表示）。
        private static string TypeText(int launchTendency)
            => launchTendency < 0 ? null : LaunchTendencyLabels.Jp(launchTendency);

        // ── 注記＋確定 ──

        private void BuildFooter()
        {
            var blocked = _opt.BlockedReasonFor(_kind);
            var ready = blocked == null && SelectionComplete();
            if (_note != null)
            {
                _note.text = blocked ?? (ready ? ReadyNote() : SelectionHint());
                _note.EnableInClassList("msub-note--warn", blocked != null);
            }
            // 守備位置の入替は「交代」ではないので確定ボタンの言葉も変える（issue #235 方針①）。
            if (_ok != null) _ok.text = _kind == SubstitutionKind.SwapDefense ? "この入替を確定 ▶" : "この交代を確定 ▶";
            _ok?.SetEnabled(ready);
        }

        /// <summary>選択が揃ったときの注記。何が起きるか（誰が退場するのか）を1行で言い切る。</summary>
        private string ReadyNote()
        {
            // 選手の出入りが無い操作は「取り消せない・再出場できない」の警句が当たらない（issue #235 方針①）。
            if (_kind == SubstitutionKind.SwapDefense)
                return "守備位置の入替は交代回数を消費しない（何度でも行える）。";
            // コンバート継投は退場するのが「元の投手」ではなく「押し出された野手」（issue #235 方針②）。
            if (_kind == SubstitutionKind.ChangePitcher && _convertTo is { } to)
                return "元の投手は退場せず" + PosJp(to) + "を守る（後で再登板もできる）。"
                       + PosJp(to) + "の選手が退場する。";
            // 任意割当の守備交代は「押し出された野手がどこへ回るか」を明示する（issue #235）。
            if (_kind == SubstitutionKind.DefensiveSub && _subAt is { } at && _outIndex >= 0
                && at != _opt.Lineup[_outIndex].Position)
                return "入る選手が" + PosJp(at) + "に就き、" + PosJp(at) + "の選手が"
                       + PosJp(_opt.Lineup[_outIndex].Position) + "へ回る。退いた選手は再出場できない。";
            return "この交代は即時に確定し、取り消せない（退いた選手は再出場できない）。";
        }

        private bool SelectionComplete() => _kind switch
        {
            SubstitutionKind.PinchHit => _inIndex >= 0,
            SubstitutionKind.PinchRun => _baseIndex >= 0 && _inIndex >= 0,
            SubstitutionKind.ChangePitcher => _inIndex >= 0,
            SubstitutionKind.DefensiveSub => _outIndex >= 0 && _inIndex >= 0,
            SubstitutionKind.SwapDefense => _swapA >= 0 && _swapB >= 0,
            SubstitutionKind.ReleaseDh => _dhChoiceMade,
            _ => false,
        };

        private string SelectionHint() => _kind switch
        {
            SubstitutionKind.PinchRun when _baseIndex < 0 => "代走に出す走者を選ぶ。",
            SubstitutionKind.DefensiveSub when _outIndex < 0 => "退く守備者を選ぶ。",
            SubstitutionKind.SwapDefense when _swapA < 0 => "入れ替える守備を2つクリックする。",
            SubstitutionKind.SwapDefense => "もう1つの守備をクリックする。",
            SubstitutionKind.ReleaseDh => "DHの行き先を選ぶ。",
            SubstitutionKind.ChangePitcher => "登板させる投手を選ぶ。",
            _ => "控えから入る選手を選ぶ。",
        };

        private void Apply()
        {
            if (_prog == null || _opt == null || !SelectionComplete()) return;
            var ok = _kind switch
            {
                SubstitutionKind.PinchHit => _prog.PinchHit(_teamIsAway, _opt.Bench[_inIndex]),
                SubstitutionKind.PinchRun => _prog.PinchRun(_teamIsAway, _baseIndex, _opt.Bench[_inIndex]),
                // 元投手の行き先を選んでいればコンバート継投（元投手は残って守備へ・issue #235）。
                SubstitutionKind.ChangePitcher => _convertTo is { } to
                    ? _prog.ConvertPitcherAndRelieve(_teamIsAway, to, _opt.PitcherCandidates[_inIndex])
                    : _prog.ChangePitcher(_teamIsAway, _opt.PitcherCandidates[_inIndex]),
                SubstitutionKind.DefensiveSub => ApplyDefensiveSub(),
                SubstitutionKind.SwapDefense => _prog.SwapDefensivePositions(_teamIsAway, _swapA, _swapB),
                SubstitutionKind.ReleaseDh => _prog.ReleaseDh(_teamIsAway, _dhAt),
                _ => false,
            };
            if (!ok)
            {
                // 局面がずれていた（＝この交代はもう成立しない）。最新の選択肢を引き直して理由を出す。
                _opt = _prog.SubstitutionOptions(_teamIsAway);
                ResetSelection();
                Render();
                if (_note != null)
                {
                    _note.text = "この交代はもう行えない（局面が変わった）。";
                    _note.AddToClassList("msub-note--warn");
                }
                return;
            }
            Close();
            _onApplied?.Invoke();
        }

        /// <summary>
        /// 守備交代の適用。入る位置を指定していれば、交代直後に守備位置だけを入れ替える（issue #235）。
        /// 入替は交代回数を消費しないので、消費するのは交代1回のまま。
        /// </summary>
        private bool ApplyDefensiveSub()
        {
            var outgoing = _opt.Lineup[_outIndex];
            if (!_prog.DefensiveSub(_teamIsAway, outgoing, _opt.Bench[_inIndex])) return false;
            if (_subAt is not { } at || at == outgoing.Position) return true;
            foreach (var slot in _opt.SwappableSlots)
            {
                if (_opt.Lineup[slot].Position != at) continue;
                _prog.SwapDefensivePositions(_teamIsAway, _outIndex, slot);
                break;
            }
            return true;
        }

        // ── 表示ヘルパ ──

        private static string NameOf(Player p)
            => p.UniformNumber > 0 ? p.UniformNumber + "  " + p.Name : p.Name;

        private static string HandLabel(Player p) => HandednessLabels.Combined(p.Throws, p.Bats);

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
            _ => "—",
        };

        private void Click(string name, System.Action action)
        {
            var btn = _root.Q<Button>(name);
            if (btn != null) btn.clicked += action;
        }
    }
}
