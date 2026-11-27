using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;      // 入部の条件（約束）の名前（ScoutPromises）
using KokoSim.Unity.Components;   // 部品辞書（CompareRow等）
using KokoSim.Unity.Shell;        // 球種チャート（PitchChartView / PitchChartDatum）・弾道ラベル
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
    /// 投枠プリセレクトは廃止（守備交代で野手能力が見えない体験の解消）。投枠へ自動で倒すフォールバックも
    /// issue #718 で廃止済み＝控え→枠／枠→控えのどちらの順でも選べ（<see cref="ClickCandidate"/>）、継投も枠を選んで入れる。</para>
    ///
    /// <para>操作は「誰を・どこへ」の一手に統一し、結果を導出する：
    /// 枠→枠＝入替（可逆・在フィールド同士）／枠(投)↔在フィールド野手＝投野交代／枠(指)↔守備位置＝DH解除／枠(指)↔枠(投)＝DHの登板（DH消滅・旧投手退場）／
    /// 枠→控え＝投入（不可逆・退場が出る）。継投・投野交代は AI と同じ候補集合（<see cref="SubstitutionOptions.ReliefCandidates"/>）
    /// を通り、engine の <see cref="MatchProgression.Relieve"/>／<see cref="MatchProgression.DefenseAssign"/>／
    /// <see cref="MatchProgression.SwapDefensivePositions"/>／<see cref="MatchProgression.ConvertPitcherAndRelieve"/>／
    /// <see cref="MatchProgression.ReleaseDh"/>／<see cref="MatchProgression.PromoteDhToMound"/> へ振り分ける。判断ロジックはエンジンに委ね、ここは表示と実行呼び出しだけ
    /// （不変条件#3 エンジン純度）。確定した交代は engine に下書きとして積まれ、確定してもモーダルは
    /// 閉じない（issue報告「投手交代と守備位置を一斉に変えられない」への対応）＝継投→守備位置の
    /// 入れ替えのように複数操作を同じセッションで続けて行える。「試合へ戻る」で試合へ持ち帰り、
    /// ✕／「全部やめる」なら確定した交代をまとめて取り消して開く前へ戻して閉じる（issue #718）。</para>
    /// </summary>
    public sealed class MatchSubstitutionPanel
    {
        private readonly VisualElement _root;
        private readonly System.Action _onApplied;

        private VisualElement _scrim, _kinds, _guide, _outRows, _inRows, _cmpRows, _cmpToggle, _cardA, _cardB, _apt, _note;
        private Label _phase, _outsCap, _insCap, _insCount, _capA, _capB, _queue;
        private Button _ok, _close, _cancel, _commit;
        private string _notePlain = "";
        // 確定した交代（issue #718・文言は #796）: 「試合へ戻る」までは「全部やめる」で開く前へ戻せる。
        private readonly List<string> _reserved = new();

        // 比較の表のタブ（0＝野手・1＝投手）。開いたときは野手。投手が絡む操作になったら投手へ自動で倒し、
        // 外れたら野手へ戻す（その間は手で切り替えられる）。
        private int _cmpTab;
        private bool _lastPitcherCtx;

        private MatchProgression _prog;
        private bool _teamIsAway;
        private SubstitutionOptions _opt;
        // 画面に映っている半回の攻撃側が表か。3アウト目の打球の再生中に開いても攻守（代打・代走タブ／守備交代）を
        // 画面の局面で出す＝次の半回の攻守を先に見せない（issue #1246）。
        private System.Func<bool?> _shownOffenseIsTop;
        private bool _open;
        private IVisualElementScheduledItem _cancelRetry;

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

        // カーソルを乗せているだけの行（選択ではない）。比較欄の右だけをこの行で仮に出す。-1＝乗せていない。
        // 控えの添字（攻撃＝Reserves・守備＝_cands）と、守備の枠の添字（枠⇄枠の2人目）。
        private int _hoverBench = -1;
        private int _hoverSlot = -1;

        // 入部の条件（約束）の見張り（設計書04 §2.4）。保留中の操作で約束を破る選手（描画のたびに引き直す）と、
        // 「約束を破って確定」を出している操作の鍵（null＝出していない。操作が変わったら下ろす）。
        private readonly List<Player> _breakers = new();
        private string _armedBreak;

        /// <param name="aptitudeGrades">
        /// 守備適性の9ポジションのランクを <see cref="Player.SourceId"/> から引く（issue #452）。
        /// 試合中の <see cref="Player"/> は適性配列を持たないので、名簿への join は Shell 側
        /// （<see cref="MatchLiveStatsProvider.AptitudeGradesOf"/>）に任せて注入する。null なら帯を出さない。
        /// </param>
        public MatchSubstitutionPanel(VisualElement root, System.Action onApplied,
            System.Func<int?, IReadOnlyList<int>> aptitudeValues = null,
            System.Func<Player, List<PitchChartDatum>> pitchChart = null,
            System.Func<Player, Condition> conditionOf = null)
        {
            _root = root;
            _onApplied = onApplied;
            _aptitudeValues = aptitudeValues;
            _pitchChart = pitchChart;
            _conditionOf = conditionOf;
        }

        // 守備適性の9ポジション（投捕一二三遊左中右）の実効守備力（MatchLiveStatsProvider.AptitudeValuesOf）。相手校・名簿外は null。
        private readonly System.Func<int?, IReadOnlyList<int>> _aptitudeValues;
        private readonly System.Func<Player, List<PitchChartDatum>> _pitchChart;
        // 調子の顔（試合中の HUD と同じ源＝MatchLiveStatsProvider.ConditionOf）。交代画面は自校だけなので真値で出す。
        private readonly System.Func<Player, Condition> _conditionOf;

        /// <summary>モーダルを開いている間は裏の進行（自動再生・次の打席へ）を止める。</summary>
        public bool IsOpen => _open;

        public void Bind()
        {
            _scrim = _root.Q<VisualElement>("sub-modal");
            _kinds = _root.Q<VisualElement>("msub-kinds");
            _guide = _root.Q<VisualElement>("msub-guide");
            _outRows = _root.Q<VisualElement>("msub-out-rows");
            _inRows = _root.Q<VisualElement>("msub-in-rows");
            _cmpRows = _root.Q<VisualElement>("msub-cmp-rows");
            _cmpToggle = _root.Q<VisualElement>("msub-cmp-toggle");
            _cardA = _root.Q<VisualElement>("msub-cmp-a");
            _cardB = _root.Q<VisualElement>("msub-cmp-b");
            _apt = _root.Q<VisualElement>("msub-apt");
            _phase = _root.Q<Label>("msub-phase");
            _outsCap = _root.Q<Label>("msub-outs-cap");
            _insCap = _root.Q<Label>("msub-ins-cap");
            _insCount = _root.Q<Label>("msub-ins-count");
            _capA = _root.Q<Label>("msub-cap-a");
            _capB = _root.Q<Label>("msub-cap-b");
            _note = _root.Q<VisualElement>("msub-note");
            _ok = _root.Q<Button>("msub-ok");
            _close = _root.Q<Button>("msub-close");
            _cancel = _root.Q<Button>("msub-cancel");
            _commit = _root.Q<Button>("msub-commit");
            _queue = _root.Q<Label>("msub-queue");

            // 線画（閉じる×・カードの間の矢印・ボタンの＋と▶）。
            AddIcon(_close, LineIcon.Kind.Close);
            AddIcon(_root.Q<VisualElement>("msub-cmp-arrow"), LineIcon.Kind.Walk);
            if (_ok != null && _ok.Q<Label>() == null)
            {
                _ok.text = "";
                _ok.Add(Text("この操作を確定", "sx-btn__t f-body-bd"));
                AddIcon(_ok, LineIcon.Kind.Plus);
            }
            if (_commit != null && _commit.Q<Label>() == null)
            {
                _commit.text = "";
                _commit.Add(Text("試合へ戻る", "sx-btn__t f-body-bd"));
                AddIcon(_commit, LineIcon.Kind.Play);
            }

            // ホバーを戻すのは、カーソルが一覧（本体の3列）の外へ出たときだけ。行どうし・列どうしの隙間では戻さない。
            _root.Q<VisualElement>(className: "sx-body")?.RegisterCallback<PointerLeaveEvent>(_ => SetHover(-1, -1));

            Click("msub-close", CancelAll);
            Click("msub-cancel", CancelAll);
            Click("msub-ok", Apply);
            Click("msub-commit", CommitAndClose);
            Close();
        }

        /// <summary>この試合の進行体と監督のチームを差し替える（試合ごとに呼ぶ）。</summary>
        public void SetMatch(MatchProgression prog, bool teamIsAway, System.Func<bool?> shownOffenseIsTop = null)
        {
            _prog = prog;
            _teamIsAway = teamIsAway;
            _shownOffenseIsTop = shownOffenseIsTop;
            Close();
        }

        public void Open()
        {
            if (_prog == null || _scrim == null) return;
            _opt = _prog.SubstitutionOptions(_teamIsAway, _shownOffenseIsTop?.Invoke());
            _offKind = SubstitutionKind.PinchHit;
            // 開いた時点を下書きの開始点にする（issue #718）。ここから先の交代は「全部やめる」で戻せる。
            _prog.BeginSubstitutionDraft();
            _reserved.Clear();
            ResetSelection();
            _open = true;
            _scrim.style.display = DisplayStyle.Flex;
            Render();
        }

        /// <summary>
        /// 週間球数制限で強制的に開いた投手交代待ち（<see cref="MatchProgression.PendingForcedPitcherChange"/>）の間は、
        /// 閉じても何も変えずに投げさせ続けることになるため、実際に継投するまで閉じさせない。
        /// </summary>
        private bool ForcedPitcherChangePending => _prog != null && _prog.PendingForcedPitcherChange;

        /// <summary>
        /// 閉じる。確定した交代が残っていれば取り消してから閉じる（確定は <see cref="CommitAndClose"/> だけ。issue #718）。
        /// </summary>
        public void Close()
        {
            if (_open && _prog != null && _prog.InSubstitutionDraft) { CancelAll(); return; }
            if (ForcedPitcherChangePending) return;
            _open = false;
            if (_scrim != null) _scrim.style.display = DisplayStyle.None;
        }

        // 「全部やめる」／✕: 確定した交代をまとめて取り消し、開く前の状態へ戻して閉じる（issue #718）。
        // 取り消しは engine の再生し直し（決定論）。球数制限の強制交代待ちに戻るなら閉じずに選び直させる。
        private void CancelAll()
        {
            if (_prog == null) return;
            if (BackgroundGameOp.Running)
            {
                // 他の背景処理（BackgroundGameOp）の実行中は進行体に触れない。黙って捨てると _open が立ったままパネルだけ出て
                // 進行が止まるので、背景処理が済み次第もう一度取り消しに来る。
                _cancelRetry ??= _scrim?.schedule.Execute(() => { _cancelRetry = null; if (_open) CancelAll(); }).StartingIn(50);
                return;
            }
            var hadReserved = _prog.SubstitutionDraftCount > 0;
            _prog.CancelSubstitutionDraft();
            _reserved.Clear();
            if (hadReserved) _onApplied?.Invoke();   // 盤面・スタメン列を開く前の姿へ戻す
            if (ForcedPitcherChangePending)
            {
                _prog.BeginSubstitutionDraft();
                _opt = _prog.SubstitutionOptions(_teamIsAway, _shownOffenseIsTop?.Invoke());
                ResetSelection();
                Render();
                return;
            }
            _open = false;
            if (_scrim != null) _scrim.style.display = DisplayStyle.None;
        }

        // 「試合へ戻る」: 確定した交代をそのまま試合へ持ち帰って閉じる（issue #718・文言は #796）。
        private void CommitAndClose()
        {
            if (_prog == null || ForcedPitcherChangePending) return;
            _prog.CommitSubstitutionDraft();
            _reserved.Clear();
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

        /// <summary>スクショ用: 控え一覧の n 番目の行にカーソルを乗せる（本物の PointerEnterEvent を行へ送る。issue #1728）。</summary>
        public void HoverCandidateForCapture(int index) => SendPointer(RowAt(_inRows, index), enter: true);

        /// <summary>スクショ用: 打順表の守備位置の枠の行にカーソルを乗せる（枠⇄枠の2人目。issue #1728）。</summary>
        public void HoverSlotForCapture(FieldPosition pos) => SendPointer(RowAt(_outRows, _slots.FindIndex(s => s.Pos == pos)), enter: true);

        /// <summary>スクショ用: 控え一覧の n 番目の行からカーソルが外れた（隣の行との隙間。一覧の外へは出ていない）。</summary>
        public void LeaveCandidateRowForCapture(int index) => SendPointer(RowAt(_inRows, index), enter: false);

        /// <summary>スクショ用: カーソルが一覧の外へ出た（本体の PointerLeaveEvent）。</summary>
        public void LeaveListForCapture() => SendPointer(_root?.Q<VisualElement>(className: "sx-body"), enter: false);

        private static VisualElement RowAt(VisualElement rows, int index)
            => rows != null && index >= 0 && index < rows.childCount ? rows[index] : null;

        private static void SendPointer(VisualElement el, bool enter)
        {
            if (el == null) return;
            if (enter) { using (var e = PointerEnterEvent.GetPooled()) { e.target = el; el.SendEvent(e); } }
            else { using (var e = PointerLeaveEvent.GetPooled()) { e.target = el; el.SendEvent(e); } }
        }

        /// <summary>
        /// スクショ用: いまその守備位置に就いている選手。モーダルの内部モデル（_slots）は開いた瞬間の
        /// スナップショットなので使わず、engine から選択肢を引き直す＝確定後の姿を読める。
        /// </summary>
        public Player FielderForCapture(FieldPosition pos)
        {
            if (_prog == null) return null;
            var opt = _prog.SubstitutionOptions(_teamIsAway, _shownOffenseIsTop?.Invoke());
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
                var opt = _prog.SubstitutionOptions(_teamIsAway, _shownOffenseIsTop?.Invoke());
                return opt.Reserves != null ? opt.Reserves.Count : -1;
            }
        }

        /// <summary>スクショ用: 投手が絡む比較の「投球／打撃・守備」トグルを切り替える（クリックと同じ経路）。</summary>
        public void ToggleCmpFielderViewForCapture()
        {
            if (!_open) return;
            _cmpTab = _cmpTab == 0 ? 1 : 0;
            Render();
        }

        /// <summary>スクショ用: 「この操作を確定」を押す（実行経路はボタンと同一）。issue #452 の操作一巡確認用。</summary>
        public void ApplyForCapture() => Apply();
        /// <summary>スクショ用（issue #718）: 確定した交代を持ち帰って閉じる（「試合へ戻る」）。</summary>
        public void CommitForCapture() => CommitAndClose();
        /// <summary>スクショ用（issue #718）: 確定した交代をまとめて取り消して閉じる（「全部やめる」）。</summary>
        public void CancelAllForCapture() => CancelAll();

        /// <summary>スクショ用: 保留中の操作の要約（フッター文）。確定できる状態かの確認に使う。</summary>
        public string NoteForCapture => _notePlain;

        /// <summary>
        /// スクショ用: 指枠の「そのまま退かせる」を選ぶ（<see cref="BuildDhWithdrawChoice"/> の行と同一経路）。
        /// 先に <see cref="PickSlotForCapture"/> で指枠を主選択しておくこと（<see cref="ResolveDef"/> は
        /// 主選択が指枠のときだけこのフラグを見る）。
        /// </summary>
        public void PickDhWithdrawForCapture()
        {
            if (!_open || _opt == null || _opt.IsOffense) return;
            _dhWithdraw = true;
            _pickedSlot2 = -1;
            Render();
        }

        /// <summary>スクショ用: いまDH制が生きているか（DH解除は不可逆なので前後の突き合わせに使う）。</summary>
        public bool UsesDhForCapture => _prog != null && _prog.SubstitutionOptions(_teamIsAway, _shownOffenseIsTop?.Invoke()).UsesDh;

        /// <summary>スクショ用: 指枠に居る選手（非DH＝null）。<see cref="FielderForCapture"/> と同じく engine から引き直す。</summary>
        public Player DhForCapture
        {
            get
            {
                if (_prog == null) return null;
                var opt = _prog.SubstitutionOptions(_teamIsAway, _shownOffenseIsTop?.Invoke());
                if (!opt.UsesDh || opt.DhSlot < 0 || opt.DhSlot >= opt.Lineup.Count) return null;
                return opt.Lineup[opt.DhSlot];
            }
        }

        /// <summary>スクショ用: 投手が入っている打順スロット（DH制は -1＝打順外）。DH解除で打順へ入ったかの証拠。</summary>
        public int PitcherSlotForCapture => _prog == null ? -2 : _prog.SubstitutionOptions(_teamIsAway, _shownOffenseIsTop?.Invoke()).PitcherSlot;

        /// <summary>
        /// スクショ用: 現在の打順9人（交代反映済み）。「誰が退いたか」は控えの増減では見えない
        /// （退場した先発は控え名簿に居ない）ので、打順から消えたことで確かめる。
        /// </summary>
        public IReadOnlyList<Player> LineupForCapture =>
            _prog == null ? System.Array.Empty<Player>() : _prog.SubstitutionOptions(_teamIsAway, _shownOffenseIsTop?.Invoke()).Lineup;

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
            _cmpTab = 0;
            _lastPitcherCtx = false;
            if (_opt != null && !_opt.IsOffense)
            {
                BuildDefenseModel();
                // 投枠のプリセレクトは廃止（issue #452 指摘4。開いた直後に投球比較しか出ず、守備交代で
                // 野手能力が見えない体験の主因だった）。「枠未選択なら投枠へ」のフォールバックも
                // issue #718 で廃止（ClickCandidate は控えを選んだまま次に押した枠を待つ）。
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
            _hoverBench = -1;
            _hoverSlot = -1;
            var forced = ForcedPitcherChangePending;
            if (_phase != null)
                _phase.text = _opt.IsFinished ? "試合終了" : _opt.IsOffense ? "攻撃中" : "守備中";
            // 交代するまで閉じさせない（Close 側のガードと対で、押せる/押せないを見た目にも出す）。
            _close?.SetEnabled(!forced);
            _cancel?.SetEnabled(!forced);
            CollectBreakers();
            BuildKinds();
            BuildGuide(forced);
            if (_opt.IsOffense) { BuildOffenseOut(); BuildOffenseIn(); }
            else { BuildDefenseAssign(); BuildDefenseCandidates(); }
            BuildCompare();
            BuildFooter();
        }

        // 攻撃中だけ「代打／代走」のつまみ型の切り替え（守備中は割当へ統合・issue #256）。
        private void BuildKinds()
        {
            if (_kinds == null) return;
            _kinds.Clear();
            _kinds.style.display = _opt.IsOffense ? DisplayStyle.Flex : DisplayStyle.None;
            if (!_opt.IsOffense) return;
            foreach (var (kind, label) in new[] { (SubstitutionKind.PinchHit, "代打"), (SubstitutionKind.PinchRun, "代走") })
            {
                var on = kind == _offKind;
                var cell = Text(label, on ? "sx-kinds__item sx-kinds__item--on f-body-bd" : "sx-kinds__item");
                if (!on && ((kind == SubstitutionKind.PinchRun && !_opt.CanPinchRun) || (kind == SubstitutionKind.PinchHit && !_opt.CanPinchHit)))
                    cell.AddToClassList("sx-kinds__item--off");
                var k = kind;
                cell.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); _offKind = k; ResetSelection(); Render(); });
                _kinds.Add(cell);
            }
        }

        // 手順の案内（① › ②）。球数制限の強制交代のときは代わりに赤字で理由を出す。
        private void BuildGuide(bool forced)
        {
            if (_guide == null) return;
            _guide.Clear();
            if (forced)
            {
                _guide.Add(Text("球数制限：投手を交代してください", "sx-guide__warn f-body-bd"));
                return;
            }
            string first, second;
            if (_opt.IsOffense && _offKind == SubstitutionKind.PinchRun) { first = "塁上の走者を選ぶ"; second = "控えから代走を選ぶ"; }
            else if (_opt.IsOffense) { first = "次打者が退く"; second = "控えから代打を選ぶ"; }
            else { first = "打順表から退く選手を選ぶ"; second = "控えから入る選手を選ぶ"; }
            _guide.Add(Text("1", "sx-guide__no f-body-bd"));
            _guide.Add(Text(first, "sx-guide__t"));
            var sep = new LineIcon(LineIcon.Kind.ChevronRight);
            sep.AddToClassList("sx-guide__sep");
            _guide.Add(sep);
            _guide.Add(Text("2", "sx-guide__no f-body-bd"));
            _guide.Add(Text(second, "sx-guide__t"));
        }

        // ── 攻撃中：左＝打順表（代打は次打者・代走は塁上の走者だけが対象） ──

        private void BuildOffenseOut()
        {
            if (_outRows == null) return;
            _outRows.Clear();
            if (_outsCap != null) _outsCap.text = "打順表";

            var incoming = _offInIndex >= 0 && _offInIndex < _opt.Reserves.Count ? _opt.Reserves[_offInIndex] : null;
            for (var i = 0; i < _opt.Lineup.Count; i++)
            {
                var p = _opt.Lineup[i];
                var slot = i;
                var tag = "";
                var selectable = false;
                var picked = false;
                if (_offKind == SubstitutionKind.PinchHit)
                {
                    if (i == _opt.UpcomingBatterSlot) { tag = "次打者"; picked = true; selectable = true; }
                }
                else
                {
                    var b = BaseOf(p);
                    if (b >= 0) { selectable = true; tag = BaseName(b); picked = _baseIndex == b; }
                }
                // 位置は「いま試合で就いている位置」＝DH の枠は「指」（Player.Position には本来の位置が残る・issue #931）。
                var preview = picked && incoming != null ? RowPreview.To(incoming) : (RowPreview?)null;
                var row = LineupRow(i + 1, p, PosJp(MatchScoutView.LineupPosition(_opt, i)), selectable, picked, tag,
                    preview, preview == null ? null : CancelOffensePick);
                if (selectable && _offKind == SubstitutionKind.PinchRun)
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
            if (_insCount != null) _insCount.text = _opt.Reserves.Count.ToString();
            for (var i = 0; i < _opt.Reserves.Count; i++)
            {
                var idx = i;
                var row = BenchRow(_opt.Reserves[i], used: false, picked: _offInIndex == i);
                row.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); _offInIndex = idx; Render(); });
                row.RegisterCallback<PointerEnterEvent>(_ => SetHover(idx, -1));
                _inRows.Add(row);
            }
            foreach (var p in _opt.UsedReserves) _inRows.Add(BenchRow(p, used: true, picked: false));
            if (_opt.Reserves.Count == 0 && _opt.UsedReserves.Count == 0)
                _inRows.Add(Text("控えが登録されていない。", "sx-empty"));
        }

        // ── 守備中：左＝打順表（守備の割当10枠） ──

        private void BuildDefenseAssign()
        {
            if (_outRows == null) return;
            _outRows.Clear();
            if (_outsCap != null) _outsCap.text = "打順表";

            // 操作が決まった行は「退く → 入る」の即時プレビューに切り替える（issue #452 指摘2・3）。
            var op = ResolveDef();
            for (var i = 0; i < _slots.Count; i++)
            {
                var s = _slots[i];
                var slotIdx = i;
                var picked = i == _pickedSlot || i == _pickedSlot2;
                var order = s.LineupSlot >= 0 ? s.LineupSlot + 1 : 0;   // DHの投手は打順外＝"—"
                var preview = DefPreview(op, i);
                var row = LineupRow(order, s.Player, PosJp(s.Pos), selectable: true, picked || preview != null, tag: "",
                    preview, preview == null ? null : CancelDefenseOp);
                row.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); ClickSlot(slotIdx); Render(); });
                row.RegisterCallback<PointerEnterEvent>(_ => SetHover(-1, slotIdx));
                _outRows.Add(row);
            }
        }

        /// <summary>行の「→ 後の姿」：入る選手／移る守備位置／退場のどれか1つ。</summary>
        private readonly struct RowPreview
        {
            public readonly Player In;
            public readonly FieldPosition? Pos;
            public readonly bool Leaves;
            private RowPreview(Player incoming, FieldPosition? pos, bool leaves) { In = incoming; Pos = pos; Leaves = leaves; }
            public static RowPreview To(Player p) => new RowPreview(p, null, false);
            public static RowPreview Move(FieldPosition pos) => new RowPreview(null, pos, false);
            public static RowPreview Out() => new RowPreview(null, null, true);
        }

        /// <summary>
        /// この枠に出す「→ 後の姿」（無い枠は null。issue #452 指摘2）。
        /// 入る選手／移る守備位置／退場を、確定前の見込みとして行内に出す。
        /// </summary>
        private RowPreview? DefPreview(in DefOp op, int slotIdx)
        {
            var s = _slots[slotIdx];
            switch (op.Kind)
            {
                case DefKind.Relieve:
                    return s.Role == SlotRole.Pitcher ? RowPreview.To(op.Incoming) : (RowPreview?)null;
                case DefKind.Convert:
                    if (s.Role == SlotRole.Pitcher) return RowPreview.To(op.Incoming);
                    if (s.Role == SlotRole.Fielder && s.Pos == op.Pos) return RowPreview.To(_opt.CurrentPitcher);
                    return null;
                case DefKind.PromoteFielder:
                    // 単純な入れ替え＝投手枠に野手、その守備位置に今の投手（issue #452）。
                    // DHでは投手が打順外＝入れ替えが成立せず、元DHがその守備位置に就く（DH解除）。
                    if (s.Role == SlotRole.Pitcher) return RowPreview.To(op.Incoming);
                    if (s.Role == SlotRole.Fielder && s.Pos == op.Pos) return RowPreview.To(SwapBackfiller());
                    if (s.Role == SlotRole.Dh) return RowPreview.Move(op.Pos);
                    return null;
                case DefKind.FielderSwap:
                    if (slotIdx == _pickedSlot) return RowPreview.Move(_slots[_pickedSlot2].Pos);
                    if (slotIdx == _pickedSlot2) return RowPreview.Move(_slots[_pickedSlot].Pos);
                    return null;
                case DefKind.Assign:
                    return slotIdx == _pickedSlot ? RowPreview.To(op.Incoming) : (RowPreview?)null;
                case DefKind.DhToPos:
                    if (s.Role == SlotRole.Dh) return RowPreview.Move(op.Pos);
                    if (s.Role == SlotRole.Fielder && s.Pos == op.Pos) return RowPreview.Out();
                    return null;
                case DefKind.DhWithdraw:
                    return s.Role == SlotRole.Dh ? RowPreview.Out() : (RowPreview?)null;
                case DefKind.DhToMound:
                    // DHの登板（issue #1725）：元DHがマウンドへ、今の投手は退場。
                    if (s.Role == SlotRole.Dh) return RowPreview.Move(FieldPosition.Pitcher);
                    if (s.Role == SlotRole.Pitcher) return RowPreview.Out();
                    return null;
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
            // 控えを先に選んでいた（枠は未選択）なら、この枠がその控えの入る先（issue #718）。
            if (_defInIndex >= 0 && _pickedSlot < 0)
            {
                _pickedSlot = slotIdx; _pickedSlot2 = -1; _convertTo = null; _dhWithdraw = false;
                return;
            }
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
            if (_insCap != null) _insCap.text = "控え";
            if (_insCount != null) _insCount.text = _cands.Count.ToString();

            for (var i = 0; i < _cands.Count; i++)
            {
                var idx = i;
                var row = BenchRow(_cands[i], used: false, picked: _defInIndex == i);
                row.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); ClickCandidate(idx); Render(); });
                row.RegisterCallback<PointerEnterEvent>(_ => SetHover(idx, -1));
                _inRows.Add(row);
            }
            // 出場済みは1本の名簿から一度だけ出す（旧 UsedBullpen＋UsedBench の連結を畳んだ。issue #381）。
            foreach (var p in _opt.UsedReserves)
                _inRows.Add(BenchRow(p, used: true, picked: false));
            if (_cands.Count == 0 && !_opt.UsedReserves.Any())
                _inRows.Add(Text("控えが登録されていない。", "sx-empty"));

            // 文脈の追加選択（選ばなければ既定挙動）。
            var a = Picked(_pickedSlot);
            if (a is { Role: SlotRole.Pitcher } && _defInIndex >= 0) BuildConvertChoices();
            else if (a is { Role: SlotRole.Dh }) BuildDhWithdrawChoice();
        }

        // 控え→枠／枠→控え のどちらの順でもよい（issue #718）。枠が未選択なら控えを選んだまま待ち、
        // 次にクリックした枠へ入れる（旧来は投枠へ自動で倒していた＝守備だけ変えたいのに継投になった）。
        // 枠未選択のまま同じ控えをもう一度押すと選択を外す。
        private void ClickCandidate(int idx)
        {
            if (_pickedSlot < 0 && _defInIndex == idx) { _defInIndex = -1; return; }
            _defInIndex = idx;
            _pickedSlot2 = -1;      // 控えを選んだら枠→枠は解除
            _convertTo = null;
            _dhWithdraw = false;
        }

        /// <summary>継投で元投手をどうするか（既定＝退場／守備位置を選ぶとコンバート継投）。見出し＋選択肢1行。</summary>
        private void BuildConvertChoices()
        {
            var box = ChoiceBox("元の投手");
            var opts = box.Q<VisualElement>(className: "sx-choice__opts");
            var retire = ChoiceOption("退場", _convertTo == null, "退場させる（通常の継投）");
            retire.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); _convertTo = null; Render(); });
            opts.Add(retire);
            // 行き先の列挙は守備位置順のまま（_slots は打順順になったので位置順で引き直す。issue #452）。
            foreach (var pos in FielderOrder)
            {
                var s = _slots.FirstOrDefault(t => t.Role == SlotRole.Fielder && t.Pos == pos);
                if (s.Player == null) continue;
                var to = pos;
                var chip = ChoiceOption(PosJp(to), _convertTo == to, PosJp(to) + "へ回す（" + NameOf(s.Player) + " が退場）");
                chip.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); _convertTo = to; Render(); });
                opts.Add(chip);
            }
            _inRows.Add(box);
        }

        /// <summary>指枠の行き先。守備位置の枠を押す＝その守備でDH解除／ここで「そのまま退かせる」も選べる。</summary>
        private void BuildDhWithdrawChoice()
        {
            var box = ChoiceBox("指名打者");
            var withdraw = ChoiceOption("そのまま退かせる", _dhWithdraw, "投手が打順に入る（守備位置の枠を押すとその守備でDH解除）");
            withdraw.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); _dhWithdraw = true; _pickedSlot2 = -1; Render(); });
            box.Q<VisualElement>(className: "sx-choice__opts").Add(withdraw);
            _inRows.Add(box);
        }

        private static VisualElement ChoiceBox(string caption)
        {
            var box = new VisualElement();
            box.AddToClassList("sx-choice");
            box.Add(Text(caption, "sx-choice__cap f-body-bd"));
            var opts = new VisualElement();
            opts.AddToClassList("sx-choice__opts");
            box.Add(opts);
            return box;
        }

        private static Label ChoiceOption(string text, bool on, string tip)
        {
            var o = Text(text, on ? "sx-choice__opt sx-choice__opt--on f-body-bd" : "sx-choice__opt");
            o.tooltip = tip;
            return o;
        }

        // ── 導出：守備の割当操作 ──

        private enum DefKind { None, Relieve, Convert, PromoteFielder, FielderSwap, Assign, DhToPos, DhWithdraw, DhToMound, Invalid }

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
                // 投⇄指＝DHの登板（規則5.11・issue #1725）。DHが自分の打順のままマウンドへ、今の投手は退場。
                if (a.Role == SlotRole.Dh && b.Role == SlotRole.Pitcher)
                    return new DefOp(DefKind.DhToMound, a.Player, outgoing: b.Player);
                if (a.Role == SlotRole.Pitcher && b.Role == SlotRole.Dh)
                    return new DefOp(DefKind.DhToMound, b.Player, outgoing: a.Player);
                return new DefOp(DefKind.Invalid);
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

        // ── 右：比較（2人のカード・野手／投手のタブの表・9列の守備適性） ──

        // カーソルを乗せた行を、クリックしたときと同じ選択として仮に当てて比較欄だけを描く（導出は ResolveDef / CompareSubjects をそのまま通す）。
        // 乗せても決まるのは右だけ＝左（退く側）が決まっていて、右がまだクリックで決まっていないときに限る。
        private void BuildCompare()
        {
            var (offIn, defIn, slot2) = (_offInIndex, _defInIndex, _pickedSlot2);
            ApplyHoverAsSelection();
            try { BuildCompareCore(); }
            finally { (_offInIndex, _defInIndex, _pickedSlot2) = (offIn, defIn, slot2); }
        }

        private void ApplyHoverAsSelection()
        {
            if (_hoverBench < 0 && _hoverSlot < 0) return;
            if (_opt.IsOffense)
            {
                var leftDecided = _offKind == SubstitutionKind.PinchHit ? _opt.UpcomingBatterSlot >= 0 : RunnerAt(_baseIndex) != null;
                if (_hoverBench >= 0 && _hoverBench < _opt.Reserves.Count && _offInIndex < 0 && leftDecided)
                    _offInIndex = _hoverBench;
                return;
            }
            if (_pickedSlot < 0 || _pickedSlot2 >= 0 || _defInIndex >= 0 || _dhWithdraw) return;
            if (_hoverBench >= 0 && _hoverBench < _cands.Count)
                _defInIndex = _hoverBench;
            else if (_hoverSlot >= 0 && _hoverSlot < _slots.Count && _hoverSlot != _pickedSlot)
                _pickedSlot2 = _hoverSlot;
        }

        private void SetHover(int bench, int slot)
        {
            if (!_open || _opt == null || (_hoverBench == bench && _hoverSlot == slot)) return;
            _hoverBench = bench;
            _hoverSlot = slot;
            BuildCompare();
        }

        private void BuildCompareCore()
        {
            var (outgoing, incoming, isPitcherCtx, capA, capB) = CompareSubjects();
            // 投手が絡む操作になったら投手タブへ、外れたら野手タブへ自動で倒す（その間は手で切り替えられる）。
            if (isPitcherCtx != _lastPitcherCtx) { _cmpTab = isPitcherCtx ? 1 : 0; _lastPitcherCtx = isPitcherCtx; }
            var pitcherView = _cmpTab == 1;

            FillCard(_cardA, outgoing, capA);
            FillCard(_cardB, incoming, capB);
            if (_capA != null) _capA.text = capA;
            if (_capB != null) _capB.text = capB;
            BuildCmpTabs();

            if (_cmpRows != null)
            {
                _cmpRows.Clear();
                _cmpRows.EnableInClassList("sx-table__rows--fill", !pitcherView);
                foreach (var (label, x, y, typeAxis) in MatchAbilityColumns.CompareRows(outgoing, incoming, pitcherView))
                    _cmpRows.Add(CompareRowEl(label, x, y, typeAxis, fill: !pitcherView));
                // 投手タブは表の下に2人の持ち球チャート（球速は km/h・球威はストレートのランクでここに出る＝他の画面と同じ）。
                if (pitcherView) _cmpRows.Add(PitchChartRow(outgoing, incoming));
            }
            BuildAptitude(outgoing, incoming, capA, capB);
        }

        private void BuildCmpTabs()
        {
            if (_cmpToggle == null) return;
            _cmpToggle.Clear();
            for (var i = 0; i < 2; i++)
            {
                var tab = i;
                var on = _cmpTab == i;
                var cell = Text(i == 0 ? "野手" : "投手", on ? "sx-tabs__item sx-tabs__item--on f-body-bd" : "sx-tabs__item");
                cell.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); _cmpTab = tab; BuildCompare(); });
                _cmpToggle.Add(cell);
            }
        }

        // 2人のカード：札（退く／入る）＋学年・投打、背番号の箱＋名前＋調子の顔。未選択なら「未選択」だけ。
        private void FillCard(VisualElement el, Player p, string caption)
        {
            if (el == null) return;
            el.Clear();
            if (p == null)
            {
                el.Add(Text("未選択", "sx-pcard__empty"));
                return;
            }
            var top = new VisualElement();
            top.AddToClassList("sx-pcard__top");
            top.Add(Text(caption, "sx-pcard__tag f-body-bd"));
            top.Add(Text(SubstitutionDisplay.GradeAndHand(p), "sx-pcard__meta"));
            el.Add(top);

            var who = new VisualElement();
            who.AddToClassList("sx-pcard__who");
            who.Add(NumBox(SubstitutionDisplay.Number(p), "sx-num--card"));
            who.Add(Text(p.Name, "sx-pcard__name f-display"));
            var face = CondFace(p, "sx-face24");
            if (face != null) who.Add(face);
            el.Add(who);
            AddInjuryTag(el, p);
        }

        // 比較の表の1行：項目名｜退く｜入る。セルは数値＋ランクのチップ（高いほうを太字・同値は両方）。
        // 弾道（タイプ軸）は文字だけ。選んでいない側は空。
        private static VisualElement CompareRowEl(string label, int a, int b, bool typeAxis, bool fill)
        {
            var row = new VisualElement();
            row.AddToClassList("sx-trow");
            if (fill) row.AddToClassList("sx-trow--fill");
            row.Add(Text(label, "sx-trow__label"));
            var weights = typeAxis ? (false, false) : SubstitutionDisplay.Weights(a, b);
            row.Add(CompareCell(a, typeAxis, weights.Item1));
            row.Add(CompareCell(b, typeAxis, weights.Item2));
            return row;
        }

        private static VisualElement CompareCell(int v, bool typeAxis, bool bold)
        {
            var cell = new VisualElement();
            cell.AddToClassList("sx-tcell");
            if (v < 0) return cell;
            if (typeAxis)
            {
                cell.Add(Text(LaunchTendencyLabels.Jp(v), "sx-tcell__text"));
                return cell;
            }
            cell.Add(Text(v.ToString(), bold ? "sx-tcell__v f-num-bd" : "sx-tcell__v f-body-md"));
            var chip = UiComponents.RankChipLegacy(KokoSim.Engine.Nation.Tiers.FromStrength(v).ToString());
            chip.AddToClassList("sx-chip22");
            cell.Add(chip);
            return cell;
        }

        // 投手タブの持ち球チャート（部品 PitchChartView・compact）。表の「退く｜入る」の列に揃えて並べる。
        private VisualElement PitchChartRow(Player a, Player b)
        {
            var row = new VisualElement();
            row.AddToClassList("sx-charts");
            row.Add(Text("持ち球", "sx-trow__label sx-charts__label"));
            row.Add(PitchChartHost(a));
            row.Add(PitchChartHost(b));
            return row;
        }

        private VisualElement PitchChartHost(Player p)
        {
            var host = new VisualElement();
            host.AddToClassList("sx-charts__host");
            if (p == null || _pitchChart == null) return host;
            var pitches = _pitchChart(p);
            if (pitches == null || pitches.Count == 0) return host;
            var chart = new VisualElement();
            chart.AddToClassList("sx-charts__chart");
            host.Add(chart);
            new PitchChartView(chart, compact: true).SetData(pitches);
            return host;
        }

        // 守備適性の9列の表（投捕一二三遊左中右 × 退く／入る）。値は実効守備力（メンバー設定・スタメン設定と同じ定義）。
        // 今回就く位置の列は赤枠で囲む。どちらのタブでも出す。
        private static readonly string[] AptPositionsJp = { "投", "捕", "一", "二", "三", "遊", "左", "中", "右" };
        private static readonly FieldPosition[] AptPositions =
        {
            FieldPosition.Pitcher, FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase, FieldPosition.ThirdBase,
            FieldPosition.Shortstop, FieldPosition.LeftField, FieldPosition.CenterField, FieldPosition.RightField,
        };

        private void BuildAptitude(Player a, Player b, string capA, string capB)
        {
            if (_apt == null) return;
            _apt.Clear();
            var head = new VisualElement();
            head.AddToClassList("sx-apt__head");
            head.Add(Text("守備適性", "sx-apt__title f-body-bd"));
            head.Add(Text("赤枠は今回就く位置", "sx-apt__note"));
            _apt.Add(head);

            var valsA = a == null || _aptitudeValues == null ? null : _aptitudeValues(a.SourceId);
            var valsB = b == null || _aptitudeValues == null ? null : _aptitudeValues(b.SourceId);
            var targets = AptTargets();

            var grid = new VisualElement();
            grid.AddToClassList("sx-apt__grid");
            var labels = new VisualElement();
            labels.AddToClassList("sx-apt__col");
            labels.AddToClassList("sx-apt__col--labels");
            labels.Add(Cell("sx-apt__pos"));
            var la = Cell("sx-apt__cell"); la.Add(Text(capA, "sx-tag sx-tag--out f-body-bd")); labels.Add(la);
            var lb = Cell("sx-apt__cell"); lb.Add(Text(capB, "sx-tag sx-tag--in f-body-bd")); labels.Add(lb);
            grid.Add(labels);

            for (var i = 0; i < AptPositions.Length; i++)
            {
                var col = new VisualElement();
                col.AddToClassList("sx-apt__col");
                var target = targets.Contains(AptPositions[i]);
                if (target) col.AddToClassList("sx-apt__col--target");
                var pos = Cell("sx-apt__pos");
                pos.Add(Text(AptPositionsJp[i], target ? "sx-apt__posname sx-apt__posname--target f-body-bd" : "sx-apt__posname"));
                col.Add(pos);
                col.Add(AptCell(valsA, i));
                col.Add(AptCell(valsB, i));
                grid.Add(col);
            }
            _apt.Add(grid);
        }

        private static VisualElement AptCell(IReadOnlyList<int> vals, int i)
        {
            var cell = Cell("sx-apt__cell");
            if (vals == null || i >= vals.Count) return cell;
            var chip = UiComponents.RankChipLegacy(KokoSim.Engine.Nation.Tiers.FromStrength(vals[i]).ToString());
            chip.AddToClassList("sx-chip20");
            cell.Add(chip);
            cell.Add(Text(vals[i].ToString(), "sx-apt__v f-num"));
            return cell;
        }

        /// <summary>今回就く守備位置（守備適性の赤枠）。入る選手が就く位置と、動く選手が移る先。攻撃中は無し。</summary>
        private HashSet<FieldPosition> AptTargets()
        {
            var set = new HashSet<FieldPosition>();
            if (_opt == null || _opt.IsOffense) return set;
            var op = ResolveDef();
            switch (op.Kind)
            {
                case DefKind.Assign:
                case DefKind.DhToPos:
                    set.Add(op.Pos);
                    break;
                case DefKind.Relieve:
                case DefKind.DhToMound:
                    set.Add(FieldPosition.Pitcher);
                    break;
                case DefKind.Convert:
                case DefKind.PromoteFielder:
                    set.Add(FieldPosition.Pitcher);
                    set.Add(op.Pos);
                    break;
                case DefKind.FielderSwap:
                    set.Add(_slots[_pickedSlot].Pos);
                    set.Add(_slots[_pickedSlot2].Pos);
                    break;
            }
            return set;
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
                case DefKind.DhToMound:
                    return (_opt.CurrentPitcher, op.Incoming, true, "降板", "登板");
                case DefKind.DhToPos:
                    return (op.Outgoing, _slots[_pickedSlot].Role == SlotRole.Dh ? _slots[_pickedSlot].Player : _slots[_pickedSlot2].Player,
                        false, "退く", "DH");
                default:
                    var a = Picked(_pickedSlot);
                    return (a?.Player, null, a is { Role: SlotRole.Pitcher }, "退く", "入る");
            }
        }

        // ── 下の操作の行：交代の内容の説明＋ボタン3つ ──

        private void BuildFooter()
        {
            var state = FooterState();
            var ready = state.Ready;
            var parts = state.Parts;
            var breakKey = ready ? BreakKey() : null;
            if (breakKey != _armedBreak) _armedBreak = null;   // 操作が変わったら押し直し
            _notePlain = SubstitutionDisplay.Plain(parts) + (breakKey != null ? "　約束を破る：" + breakKey : "");
            if (_note != null)
            {
                _note.Clear();
                if (ready)
                {
                    var icon = new LineIcon(LineIcon.Kind.Swap);
                    icon.AddToClassList("sx-note__icon");
                    _note.Add(icon);
                }
                foreach (var part in parts) _note.Add(NotePartEl(part, ready));
                if (breakKey != null) _note.Add(Text("　約束を破る：" + breakKey, "sx-note__warn f-body-bd"));
            }
            if (_ok != null)
            {
                var label = _ok.Q<Label>(className: "sx-btn__t");
                if (label != null)
                    label.text = _armedBreak != null ? "約束を破って確定"
                        : _opt.IsOffense
                            ? (_offKind == SubstitutionKind.PinchRun ? "この代走を確定" : "この代打を確定")
                            : "この操作を確定";
                _ok.SetEnabled(ready);
            }
            _commit?.SetEnabled(!ForcedPitcherChangePending);
            if (_cancel != null) _cancel.text = _reserved.Count > 0 ? "全部やめる" : "やめる";
            BuildQueue();
        }

        private static VisualElement NotePartEl(NotePart part, bool ready)
        {
            switch (part.Kind)
            {
                case NotePartKind.Player:
                    return Text(part.Text, "sx-note__player f-body-bd");
                case NotePartKind.Pos:
                    var chip = new PositionChip(part.Text);
                    chip.AddToClassList("sx-pos--note");
                    chip.AddToClassList("f-body-bd");
                    return chip;
                case NotePartKind.Muted:
                    return Text(part.Text, ready ? "sx-note__muted" : "sx-note__hint");
                default:
                    return Text(part.Text, "sx-note__t");
            }
        }

        // 確定した交代（試合へ戻る前）。見出しの右に件数の札だけ出す（中身は打順表がすでに交代後の姿）。
        private void BuildQueue()
        {
            if (_queue == null) return;
            _queue.style.display = _reserved.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            _queue.text = "確定済み " + _reserved.Count + "件";
            var lines = new List<string>();
            for (var i = 0; i < _reserved.Count; i++) lines.Add((i + 1) + ". " + _reserved[i]);
            _queue.tooltip = string.Join("\n", lines);
        }

        private (bool Ready, List<NotePart> Parts) FooterState()
        {
            var none = new List<NotePart>();
            if (_opt.IsFinished) return (false, SubstitutionDisplay.Hint("試合が終了している。"));
            if (_opt.IsOffense)
            {
                var incoming = _offInIndex >= 0 && _offInIndex < _opt.Reserves.Count ? _opt.Reserves[_offInIndex] : null;
                if (_offKind == SubstitutionKind.PinchHit)
                {
                    if (_opt.Reserves.Count == 0) return (false, SubstitutionDisplay.Hint("控えが残っていない。"));
                    var batter = _opt.UpcomingBatterSlot >= 0 && _opt.UpcomingBatterSlot < _opt.Lineup.Count ? _opt.Lineup[_opt.UpcomingBatterSlot] : null;
                    return incoming != null ? (true, SubstitutionDisplay.PinchHit(incoming, batter)) : (false, none);
                }
                if (_opt.Runners.Count == 0) return (false, SubstitutionDisplay.Hint("塁上に走者がいない。"));
                if (_opt.Reserves.Count == 0) return (false, SubstitutionDisplay.Hint("控えが残っていない。"));
                if (_baseIndex < 0) return (false, incoming != null ? SubstitutionDisplay.Hint("代走に出す走者を選ぶ。") : none);
                return incoming != null
                    ? (true, SubstitutionDisplay.PinchRun(incoming, BaseName(_baseIndex), RunnerAt(_baseIndex)))
                    : (false, SubstitutionDisplay.Hint("控えから代走を選ぶ。"));
            }

            var op = ResolveDef();
            switch (op.Kind)
            {
                case DefKind.Relieve:
                    return (true, SubstitutionDisplay.Relieve(op.Incoming, _opt.CurrentPitcher));
                case DefKind.Convert:
                    return (true, SubstitutionDisplay.Convert(op.Incoming, _opt.CurrentPitcher, op.Pos, op.Outgoing,
                        _opt.UsesDh ? _opt.Lineup[_opt.DhSlot] : null));
                case DefKind.PromoteFielder:
                    // 単純な入れ替え。非DHは誰も退場しない＝元投手は後で再登板できる（issue #452）。
                    return _opt.UsesDh
                        ? (true, SubstitutionDisplay.PromoteFielderDh(op.Incoming, SwapBackfiller(), op.Pos, _opt.CurrentPitcher))
                        : (true, SubstitutionDisplay.PromoteFielder(op.Incoming, _opt.CurrentPitcher, op.Pos));
                case DefKind.FielderSwap:
                    return (true, SubstitutionDisplay.FielderSwap(_slots[_pickedSlot].Player, _slots[_pickedSlot].Pos,
                        _slots[_pickedSlot2].Player, _slots[_pickedSlot2].Pos));
                case DefKind.Assign:
                    return (true, SubstitutionDisplay.Assign(op.Incoming, op.Pos, op.Outgoing));
                case DefKind.DhToPos:
                    var dh = _slots[_pickedSlot].Role == SlotRole.Dh ? _slots[_pickedSlot].Player : _slots[_pickedSlot2].Player;
                    return (true, SubstitutionDisplay.DhToPos(dh, op.Pos, op.Outgoing));
                case DefKind.DhWithdraw:
                    return (true, SubstitutionDisplay.DhWithdraw(op.Outgoing));
                case DefKind.DhToMound:
                    return (true, SubstitutionDisplay.DhToMound(op.Incoming, _opt.CurrentPitcher));
                case DefKind.Invalid:
                    return (false, SubstitutionDisplay.Hint("この組み合わせでは操作できない（控え→指は不可）。"));
                default:
                    var a = Picked(_pickedSlot);
                    if (a is { Role: SlotRole.Pitcher }) return (false, SubstitutionDisplay.Hint(_opt.UsesDh ? "控えを選べば継投／別の守備枠を選べば投野交代／指枠を選べばDHが登板。" : "控えを選べば継投／別の守備枠を選べば投野交代。"));
                    if (a is { Role: SlotRole.Dh }) return (false, SubstitutionDisplay.Hint("守備位置の枠を選べばDH解除／投枠を選べばDHが登板／控えは指枠に就けない。"));
                    if (a != null) return (false, SubstitutionDisplay.Hint("別の守備枠を選べば入替／控えを選べば守備固め。"));
                    if (_defInIndex >= 0) return (false, SubstitutionDisplay.Hint("入れる枠を選ぶ（投手の枠なら継投）。"));
                    return (false, none);
            }
        }

        private void Apply()
        {
            if (_prog == null || _opt == null) return;
            // 他の背景処理（BackgroundGameOp）の実行中は engine の進行体・共有状態をメインスレッドから触らない
            // （issue #288／#278 と同型の予防）。1球進行は背景スレッドでなくメインスレッド同期なので、
            // 試合の進行そのものでここが立つことは無い。モーダルは _paused 中しか開かない建付けなので通常ここは
            // 立たないが、押下配信は1フレーム遅れうるため二重に守る。競合させると共有状態を奪い合い、
            // 最悪 Unity ごとフリーズする（背景処理の列挙状態とラインナップの同時変更）。
            if (BackgroundGameOp.Running) return;
            // 約束を破る操作は、1回目は確認だけ（ボタンの文が「約束を破って確定」に変わる・設計書04 §2.4）。
            var breakKey = BreakKey();
            if (breakKey != null && _armedBreak != breakKey)
            {
                _armedBreak = breakKey;
                Render();
                return;
            }
            _armedBreak = null;
            var description = SubstitutionDisplay.Plain(FooterState().Parts);   // 確定した交代の一覧に残す一文（適用前の局面で組む）
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
                    DefKind.DhToMound => _prog.PromoteDhToMound(_teamIsAway),
                    _ => false,
                };
            }

            if (!ok)
            {
                // 局面がずれた（この操作はもう成立しない）。選択肢を引き直して理由を出す。
                _opt = _prog.SubstitutionOptions(_teamIsAway, _shownOffenseIsTop?.Invoke());
                ResetSelection();
                Render();
                if (_note != null)
                {
                    _note.Clear();
                    _note.Add(Text("この操作はもう行えない（局面が変わった）。", "sx-note__warn f-body-bd"));
                }
                return;
            }
            // 確定（issue #718・文言は #796）: 試合には即反映して表・比較を交代後の姿で見せるが、「試合へ戻る」を
            // 押すまでは「全部やめる」で開く前へ戻せる。続けて別の交代も確定できる。
            _reserved.Add(description);
            _opt = _prog.SubstitutionOptions(_teamIsAway, _shownOffenseIsTop?.Invoke());
            ResetSelection();
            _onApplied?.Invoke();
            Render();
        }

        // ── 入部の条件（約束）の見張り（設計書04 §2.4） ──

        /// <summary>
        /// 保留中の操作で約束を破る選手を集める。代打を送る打者（「代打を出さない」）・途中から出す控え
        /// （「1年夏は出さない」）・登板させる投手（「連投させない」）。判定は engine の <see cref="ScoutPromiseJudge"/>
        /// を Shell 経由で引く（公式戦のときだけ）。
        /// </summary>
        private void CollectBreakers()
        {
            _breakers.Clear();
            if (_opt == null || _opt.IsFinished) return;
            void Check(Player p, bool breaks)
            {
                if (p != null && breaks && !_breakers.Contains(p)) _breakers.Add(p);
            }
            if (_opt.IsOffense)
            {
                if (_offInIndex < 0 || _offInIndex >= _opt.Reserves.Count) return;
                var incoming = _opt.Reserves[_offInIndex];
                Check(incoming, ScoutPromiseTracker.BreaksOnEntry(incoming.SourceId, pitches: false));
                if (_offKind == SubstitutionKind.PinchHit
                    && _opt.UpcomingBatterSlot >= 0 && _opt.UpcomingBatterSlot < _opt.Lineup.Count)
                {
                    var batter = _opt.Lineup[_opt.UpcomingBatterSlot];
                    Check(batter, ScoutPromiseTracker.BreaksWhenPinchHitFor(batter.SourceId));
                }
                return;
            }
            var op = ResolveDef();
            switch (op.Kind)
            {
                case DefKind.Relieve:
                case DefKind.Convert:
                case DefKind.PromoteFielder:
                case DefKind.DhToMound:
                    Check(op.Incoming, ScoutPromiseTracker.BreaksOnEntry(op.Incoming?.SourceId, pitches: true));
                    break;
                case DefKind.Assign:
                    Check(op.Incoming, ScoutPromiseTracker.BreaksOnEntry(op.Incoming?.SourceId, pitches: false));
                    break;
            }
        }

        /// <summary>保留中の操作で破る約束を「名前（条件）」でつないだ文（破らなければ null）。確認の鍵にも使う。</summary>
        private string BreakKey()
        {
            if (_breakers.Count == 0) return null;
            var parts = new List<string>(_breakers.Count);
            foreach (var p in _breakers) parts.Add(NameOf(p) + "（" + PromiseLabel(p) + "）");
            return string.Join("・", parts);
        }

        /// <summary>選手の生きている約束の名前（無ければ空）。</summary>
        private static string PromiseLabel(Player p)
        {
            var holder = ScoutPromiseTracker.HolderOf(p?.SourceId);
            return holder == null ? "" : ScoutPromises.Label(holder.Promise);
        }

        /// <summary>名前の横の約束タグ（約束の無い選手は付けない）。この操作で破るなら警告色。</summary>
        private void AddPromiseTag(VisualElement row, Player p)
        {
            var label = PromiseLabel(p);
            if (label.Length == 0) return;
            row.Add(UiComponents.PromiseTag(label, _breakers.Contains(p)));
        }

        /// <summary>自校の故障中の選手の怪我タグ（傷病名・段階・あと N 日／週。スタメン設定と同じ部品）。健常・相手校は付けない。</summary>
        private static void AddInjuryTag(VisualElement host, Player p)
        {
            var chip = InjuryLabel.ChipOf(p?.SourceId);
            if (chip.IsEmpty) return;
            var tag = UiComponents.InjuryTag(chip);
            tag.AddToClassList("sx-injury");
            host.Add(tag);
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
        /// 打順表の1行：打順｜守備位置のチップ｜選手（背番号の箱＋名前）｜右端（札・投打・調子の顔）。
        /// 交代に関わる行（<paramref name="picked"/>）だけ赤枠＋薄い赤の地。<paramref name="preview"/> があれば
        /// 選手の列を「退く → 入る」（または「→ 守備位置へ」「→ 退場」）にして、右端を「× 取消」にする。
        /// 枠はふだんから透明で持つ＝選んでも行の大きさは変わらない。
        /// </summary>
        private VisualElement LineupRow(int order, Player p, string posText, bool selectable, bool picked, string tag,
            RowPreview? preview, System.Action onCancel)
        {
            var row = new VisualElement();
            row.AddToClassList("sx-lrow");
            if (picked) row.AddToClassList("sx-lrow--picked");
            if (!selectable && !picked) row.AddToClassList("sx-lrow--off");

            row.Add(Text(order > 0 ? order.ToString() : "—", "sx-lrow__ord f-display"));
            var posCell = new VisualElement();
            posCell.AddToClassList("sx-lrow__pos");
            var chip = new PositionChip(posText);
            chip.AddToClassList("sx-pos24");
            chip.AddToClassList("f-body-bd");
            posCell.Add(chip);
            row.Add(posCell);

            var who = new VisualElement();
            who.AddToClassList("sx-lrow__who");
            if (preview is { } pv)
            {
                who.Add(NumBox(SubstitutionDisplay.Number(p), "sx-num--gone"));
                who.Add(Text(p?.Name ?? "—", "sx-lrow__name sx-lrow__name--gone f-display-lg"));
                var arrow = new LineIcon(LineIcon.Kind.Walk);
                arrow.AddToClassList("sx-lrow__arrow");
                who.Add(arrow);
                if (pv.In != null)
                {
                    who.Add(NumBox(SubstitutionDisplay.Number(pv.In), "sx-num--in"));
                    who.Add(Text(pv.In.Name, "sx-lrow__name sx-lrow__name--in f-display"));
                }
                else if (pv.Pos is { } to)
                {
                    var toChip = new PositionChip(PosJp(to));
                    toChip.AddToClassList("sx-pos24");
                    toChip.AddToClassList("f-body-bd");
                    who.Add(toChip);
                    who.Add(Text("へ", "sx-lrow__to f-body-bd"));
                }
                else who.Add(Text("退場", "sx-lrow__to f-body-bd"));
            }
            else
            {
                who.Add(NumBox(SubstitutionDisplay.Number(p), null));
                who.Add(Text(p?.Name ?? "—", "sx-lrow__name f-display"));
                AddInjuryTag(who, p);
                AddPromiseTag(who, p);
            }
            row.Add(who);

            if (onCancel != null)
            {
                var x = new Button(() => onCancel()) { text = "" };
                x.AddToClassList("sx-cancel");
                AddIcon(x, LineIcon.Kind.Close);
                x.Add(Text("取消", "sx-cancel__t f-body-bd"));
                x.RegisterCallback<ClickEvent>(e => e.StopPropagation());   // 行クリック（枠選択）に化けさせない
                row.Add(x);
                return row;
            }

            var info = new VisualElement();
            info.AddToClassList("sx-lrow__info");
            if (!string.IsNullOrEmpty(tag)) info.Add(Text(tag, "sx-rtag f-body-bd"));
            info.Add(Text(HandLabel(p), "sx-hand"));
            var face = CondFace(p, "sx-face24");
            if (face != null) info.Add(face);
            row.Add(info);
            return row;
        }

        /// <summary>
        /// 控えの1行：背番号の箱｜名前｜投打｜調子の顔。入る選手として選んだ行は赤枠＋薄い赤の地＋背番号と名前も赤。
        /// 出場済み（<paramref name="used"/>・再出場できない）は名前を淡色にして末尾に置く（札は付けない・issue #1534）。
        /// </summary>
        private VisualElement BenchRow(Player p, bool used, bool picked)
        {
            var row = new VisualElement();
            row.AddToClassList("sx-brow");
            if (picked) row.AddToClassList("sx-brow--picked");
            if (used) row.AddToClassList("sx-brow--used");
            row.Add(NumBox(SubstitutionDisplay.Number(p), picked ? "sx-num--in" : used ? "sx-num--gone" : null));
            var name = Text(p?.Name ?? "—", "sx-brow__name f-display");
            row.Add(name);
            if (!used) AddInjuryTag(row, p);
            if (!used) AddPromiseTag(row, p);
            row.Add(Text(HandLabel(p), "sx-hand"));
            var face = CondFace(p, "sx-face22");
            if (face != null) row.Add(face);
            return row;
        }

        private ConditionFace CondFace(Player p, string cls)
        {
            if (_conditionOf == null || p == null) return null;
            var face = new ConditionFace();
            face.AddToClassList(cls);
            face.Set(_conditionOf(p));
            return face;
        }

        // 背番号の箱（白地＋墨の1.5px枠・角丸3）。extra で退く（淡色）／入る（赤）／カード（大）を足す。
        private static Label NumBox(string text, string extra)
        {
            var num = Text(text, "sx-num f-num-bd");
            if (!string.IsNullOrEmpty(extra)) num.AddToClassList(extra);
            return num;
        }

        private static Label Text(string text, string classes)
        {
            var t = new Label(text);
            foreach (var c in classes.Split(' ')) if (c.Length > 0) t.AddToClassList(c);
            return t;
        }

        private static VisualElement Cell(string cls)
        {
            var c = new VisualElement();
            c.AddToClassList(cls);
            return c;
        }

        private static void AddIcon(VisualElement host, LineIcon.Kind kind)
        {
            if (host == null || host.Q<LineIcon>() != null) return;
            host.Add(new LineIcon(kind));
        }

        private static string NameOf(Player p)
            => p == null ? "—" : p.UniformNumber > 0 ? p.UniformNumber + "  " + p.Name : p.Name;

        private static string HandLabel(Player p) => p == null ? "" : HandednessLabels.Combined(p.Throws, p.Bats);

        private static string PosJp(FieldPosition pos) => FieldPositionNames.Abbr(pos);

        private readonly List<(Button Btn, System.Action Action)> _bound = new();

        private void Click(string name, System.Action action)
        {
            var btn = _root.Q<Button>(name);
            if (btn == null) return;
            btn.clicked += action;
            _bound.Add((btn, action));
        }

        /// <summary>Bind で登録した購読を外す（画面を離れるたびに呼ぶ。外さないと OnEnable の往復で確定が多重発火しうる）。</summary>
        public void Unbind()
        {
            foreach (var (btn, action) in _bound) btn.clicked -= action;
            _bound.Clear();
            _cancelRetry?.Pause();
            _cancelRetry = null;
            _open = false;
        }

        /// <summary>
        /// 進行体に触れずに見た目だけ畳む。他の背景処理中（<see cref="BackgroundGameOp"/>）は下書きの取り消しが
        /// できないので、終局・画面離脱の場面だけがこれを使う。
        /// </summary>
        public void ForceHide()
        {
            _cancelRetry?.Pause();
            _cancelRetry = null;
            _open = false;
            if (_scrim != null) _scrim.style.display = DisplayStyle.None;
        }
    }
}
