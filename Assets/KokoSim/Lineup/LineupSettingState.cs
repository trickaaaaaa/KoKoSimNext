using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using KokoSim.Engine.Stats;
using KokoSim.Unity.Components;   // PitchChartDatum（持ち球ミニチャート, issue #94）
using KokoSim.Unity.Players;      // PlayerDetailState.BuildPitchData / PitchData（球種データ単一ソース）
using KokoSim.Unity.Shell;

namespace KokoSim.Unity.Lineup
{
    // ── View DTO（コントローラは描画するだけ・UnityEngine 非依存） ──

    /// <summary>打順1枠の表示データ。</summary>
    public sealed class LineupRowView
    {
        public int Order;                 // 打順 1〜9
        public int UniformNumber;         // 背番号（メンバー設定で割当。0＝未割当）
        public string PosKanji = "";      // 守備位置漢字（DHは「指」・投手は「投」）
        // 守備位置チップは9枠すべて編集可（「投」も含めて入れ替えられる, issue #233）。
        // IsDhSlot は見た目（指＝守備に就かない）を分けるためだけに残す。
        public bool IsDhSlot;
        public string Name = "";
        public string GradeLabel = "";
        public string HandLabel = "";
        public string StatText = "";      // 通算の簡易成績（打率・本・点。全員打撃成績・データ無しは「-」）
        public int PlayerIndex = -1;      // 共有ロスターの安定index
        public bool IsPicked;
        public bool IsPosPicked;          // 守備位置チップが選択中（次のチップクリックで入替）
        public bool IsCaptain;
        /// <summary>故障中の短縮表示（例「捻挫・中度」）。健常なら空（設計書03 §3.5）。</summary>
        public string Injury = "";
        /// <summary>調子（常に自校の真値。UI原則⑥・ConditionFaceで表情顔として出す）。</summary>
        public Condition Condition = Condition.Normal;
    }

    /// <summary>控え（打順外）の1選手。</summary>
    public sealed class BenchRowView
    {
        public int Index;
        public int UniformNumber;         // 背番号（メンバー設定で割当。0＝未割当）
        public string Name = "";
        public string GradeLabel = "";
        public bool IsPicked;
        /// <summary>故障中の短縮表示（例「捻挫・中度」）。健常なら空（設計書03 §3.5）。</summary>
        public string Injury = "";
        /// <summary>調子（常に自校の真値。UI原則⑥・ConditionFaceで表情顔として出す）。</summary>
        public Condition Condition = Condition.Normal;
    }

    /// <summary>
    /// 成績表の1行＝指標ラベル＋スコープごとの値（issue #388）。<see cref="Values"/> の並びは
    /// <see cref="StatTableView.Scopes"/> と同順。値未接続は "—"。
    /// </summary>
    public sealed class StatRowView
    {
        public string Label = "";
        public List<string> Values = new List<string>();
    }

    /// <summary>
    /// 成績表（issue #388）。ラベルを左に1列だけ置き、スコープ（通算/公式戦/今大会）を横に並べる。
    ///
    /// 旧構造は「スコープごとの箱」で、各箱がラベルを繰り返し持っていた。3列化した時点で
    /// 1列の内寸が98pt しか無くなり、3文字ラベル(50pt)＋値(最大55pt) が収まらず文字が重なっていた。
    /// ラベルを1本にすると値へ約96pt 使えるので接触が構造的に起きない上、同じ指標のスコープ間比較が
    /// 横一行で読める＝3列にした目的そのものが果たせる。
    /// </summary>
    public sealed class StatTableView
    {
        public List<string> Scopes = new List<string>();
        public List<StatRowView> Rows = new List<StatRowView>();
    }

    /// <summary>比較カード（A/B）＝選手ヘッダ＋成績2列。</summary>
    public sealed class CompareCardView
    {
        public bool Present;
        public string Name = "";
        public string GradeLabel = "";
        /// <summary>カテゴリ別ランク（打撃力/走力/守備力/投手力）。選手個人の総合ランクは廃止（Issue #30/#270）。</summary>
        public PlayerStrength Strength = new PlayerStrength(0, 0, 0, 0);
        public bool IsCaptain;
        public StatTableView Stats = new StatTableView();
        // 持ち球のミニ球種変化チャート用データ（issue #94 案C）。未習得選手はストレート1球のみ。
        public List<PitchChartDatum> Pitches = new List<PitchChartDatum>();
    }

    public sealed class LineupSettingView
    {
        public List<LineupRowView> Rows = new List<LineupRowView>();
        public List<BenchRowView> Bench = new List<BenchRowView>();
        public bool UsesDh;
        public string StartingPitcherName = "";
        public bool StartingPitcherPicked;
        public string TeamRankGrade = "C";

        public CompareCardView CardA = new CompareCardView();
        public CompareCardView CardB = new CompareCardView();
        public int Tab;
        public string[] TabLabels = PlayerCompareTabs.TabLabels;
        // 持ち球のミニ球種チャートを出すタブか（issue #293）。投手能力タブのみ。単一ソースは PlayerCompareTabs。
        public bool ShowPitchChart;
        public bool CanConfirm = true;      // ベンチ入りが9人未満なら false（確定ボタンを塞ぐ）
        public string WarnText = "";        // 上の理由（空＝警告なし）
        public string HintText = "";        // 守備位置・先発の操作説明（打順表の下・控えめ）
        public List<PlayerCompareRow> Rows2 = new List<PlayerCompareRow>();  // 比較能力行
        public bool ShowApt;
        public bool HasAptA;
        public bool HasAptB;
        public List<string> AptA = new List<string>();
        public List<string> AptB = new List<string>();
    }

    /// <summary>
    /// 試合前スタメン設定の状態。全画面共有 <see cref="RosterService.Active"/> を単一ソースに、打順9人＋守備位置・
    /// DH・先発を編集し、確定で <see cref="GameSession.Lineup"/>（<see cref="LineupSpec"/>）へ書き出す。UnityEngine 非依存。
    /// </summary>
    public sealed class LineupSettingState
    {
        private enum PickKind { None, OrderPlayer, StartingPitcher, SlotPosition }

        // カテゴリ別ランク（比較カード）の合成係数。メンバー設定・選手一覧と同一の単一ソース。
        private static readonly TeamStrengthCoefficients Coeff = TeamStrengthCoeff.Default;
        // 実効守備力（Fielding×適性補正）の係数（単一ソース、issue #371）。
        private static readonly Engine.Match.Fielding.FieldingCoefficients FieldingCoeffDefault = FieldingCoeff.Default;

        // 守備位置→漢字。DHは指名打者の「指」（enum由来・issue #70）。
        private static readonly Dictionary<FieldPosition, string> PosKanji = new()
        {
            { FieldPosition.Pitcher, "投" }, { FieldPosition.Catcher, "捕" }, { FieldPosition.FirstBase, "一" },
            { FieldPosition.SecondBase, "二" }, { FieldPosition.ThirdBase, "三" }, { FieldPosition.Shortstop, "遊" },
            { FieldPosition.LeftField, "左" }, { FieldPosition.CenterField, "中" }, { FieldPosition.RightField, "右" },
            { FieldPosition.DesignatedHitter, "指" },
        };

        // 野手の守備位置（投手を除く8つ・背番号2〜9の慣例順）。初期打順のシードに使う。
        private static readonly FieldPosition[] FielderPositions =
        {
            FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase, FieldPosition.ThirdBase,
            FieldPosition.Shortstop, FieldPosition.LeftField, FieldPosition.CenterField, FieldPosition.RightField,
        };

        // 比較タブ①野手能力（＋守備適性ダイヤ） ②投手能力。タブ定義・行組み立ては単一ソース
        // KokoSim.Unity.Shell.PlayerCompareTabs（メンバー設定画面と共有, issue #192）。

        // 守備適性ダイヤ用の9守備位置（投捕一二三遊左中右順）。
        private static readonly FieldPosition[] AptOrder =
        {
            FieldPosition.Pitcher, FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase,
            FieldPosition.ThirdBase, FieldPosition.Shortstop, FieldPosition.LeftField, FieldPosition.CenterField,
            FieldPosition.RightField,
        };

        private readonly IReadOnlyList<DevelopingPlayer> _roster;

        // カテゴリ別指標のメモ（並べ替えキー・比較カードで共用。null＝未集計）。
        private readonly PlayerStrength[] _strength;

        // 出場登録できる選手＝ベンチ入り（背番号1〜20。設計書06 §3.3b）のロスターindex。
        // スタメン・控えはすべてこの集合からしか選べない（ベンチ外は候補に出さない）。
        private readonly List<int> _eligible;
        private readonly bool _shortage;   // ベンチ入りが9人未満＝打順を組めない（確定を塞ぐ）

        // 打順（9スロット）：各スロットのロスターindexと守備位置。
        private readonly int[] _idx = new int[9];
        private readonly FieldPosition[] _pos = new FieldPosition[9];
        private bool _usesDh;
        private int _spIdx = -1;         // DH制の先発投手（ロスターindex）

        // 投手枠・DH枠は独立フィールドで持たず、割当配列 _pos から引き直す（DefenseAssignment の規約）。
        // 打順入替（ClickRow）は選手と守備位置を丸ごと交換するので、別フィールドで持つと
        // 「投を別の打順へ動かしたのに PitcherSlot が古いまま」のズレが起き、確定した LineupSpec が
        // 9ポジション網羅を満たさず試合開始時に engine が例外を投げる。
        private int PitcherSlot => DefenseAssignment.SlotOf(_pos, FieldPosition.Pitcher);
        private int DhSlot => DefenseAssignment.SlotOf(_pos, FieldPosition.DesignatedHitter);

        // 操作状態：クリック選択＝比較の左＋入替の主体。ホバー＝比較の右。
        private int _picked = -1;
        private int _posPickSlot = -1;   // 守備位置ピック中の打順スロット（PickKind.SlotPosition のとき有効）
        private PickKind _pickKind = PickKind.None;
        private int _hovered = -1;
        private int _tab;

        public LineupSettingState()
        {
            _roster = RosterService.Active;
            _strength = new PlayerStrength[_roster.Count];
            var benchIn = Enumerable.Range(0, _roster.Count).Where(i => _roster[i].UniformNumber >= 1).ToList();
            // ベンチ入りが9人に満たない編成では打順を組めない。画面は従来通り全部員で描いたうえで確定を塞ぎ、
            // メンバー設定でベンチ入りを増やしてもらう（不正な LineupSpec はエンジンが弾く）。
            _shortage = benchIn.Count < 9;
            _eligible = _shortage ? Enumerable.Range(0, _roster.Count).ToList() : benchIn;
            if (!TryRestoreLastLineup()) SeedFromUniformNumbers();
        }

        // 前の試合で確定したスタメンから始める（issue lineup-carry-over-previous-game）。
        // 照合・穴埋め・DHの畳み・守備網羅の確認は engine の純関数 LineupRestorer が単一ソース。
        // 穴埋めの順位は自動シードと同じ（打順＝打撃力順・投手枠＝投手力順）を渡す。
        // ベンチ入り9人未満（_shortage）は従来どおり全部員で描いて確定を塞ぐだけなので復元しない。
        private bool TryRestoreLastLineup()
        {
            if (_shortage) return false;
            var memory = KokoSim.Unity.Shell.GameSession.Current.LastLineup;
            if (memory == null) return false;
            var eligible = new HashSet<int>(_eligible);
            var r = LineupRestorer.Restore(
                memory, _roster, eligible.Contains, ByBatting().ToList(), ByPitching().ToList());
            if (!r.Restored) return false;
            Array.Copy(r.RosterIndices, _idx, 9);
            Array.Copy(r.Positions, _pos, 9);
            _usesDh = r.UsesDh;
            _spIdx = r.StartingPitcherIndex;
            return true;
        }

        // 初期打順をシード（非DH）。守備位置は背番号2〜9の慣例（捕一二三遊左中右）を各自が保持しつつ、
        // 打順は打撃能力（ミート＋パワー）順に並べ替える＝強打者を中軸へ（背番号順のままだと弱打者が中軸に来て点が入らない）。
        private void SeedFromUniformNumbers()
        {
            // 埋める前に打順を空にする。既定の 0 のままだと InOrder(0) が真になり、穴埋めが
            // roster[0] を絶対に選べない（かつ前回シードの DH が残る）。
            Array.Fill(_idx, -1);
            _usesDh = false;

            // 9番＝投手（背番号1）。**打順より先に押さえる**＝穴埋めが役割で候補を絞らなくなったので
            // （#379）、先に打順を埋めると投手力上位が野手枠に吸われて投手枠へ2番手が回る。
            var pit = IndexOfUniform(1);
            _idx[8] = pit >= 0 ? pit : FallbackPitcher();
            _pos[8] = FieldPosition.Pitcher;

            // 8野手を背番号2〜9で拾い、守備位置を付ける。
            var fielders = new List<(int Idx, FieldPosition Pos)>(8);
            for (var i = 0; i < 8; i++)
            {
                var idx = IndexOfUniform(i + 2);
                if (idx < 0) idx = FallbackFielder(fielders);
                fielders.Add((idx, FielderPositions[i]));
            }
            // 打撃力（ミート＋パワー）降順に並べ、強打者を 3→4→2→5→1→6→7→8 番の順に配置（典型的な強打順）。
            fielders.Sort((a, b) => Hitting(_roster[b.Idx]).CompareTo(Hitting(_roster[a.Idx])));
            var slotFor = new[] { 2, 3, 1, 4, 0, 5, 6, 7 };   // k番目に強い打者を置く打順スロット（0基点）
            for (var k = 0; k < 8; k++)
            {
                _idx[slotFor[k]] = fielders[k].Idx;
                _pos[slotFor[k]] = fielders[k].Pos;
            }
        }

        // 打撃力の簡易指標（打順並べ替え用）。ミート＋パワー。
        private static int Hitting(DevelopingPlayer p) => p.Level(AbilityKind.Contact) + p.Level(AbilityKind.Power);

        private int IndexOfUniform(int number)
        {
            for (var i = 0; i < _roster.Count; i++)
                if (_roster[i].UniformNumber == number) return i;
            return -1;
        }

        // 未割当時：まだ打順に居ない選手を打撃力順で拾う（打順に入れる枠なので打てる順）。
        // 役割では絞らない＝打てる投手はそのまま打順に入る（#366「投手は配置であって属性ではない」/#379）。
        // picked は同じシード内で確保済みの枠（_idx へはまだ書いていないので InOrder では拾えない）。
        private int FallbackFielder(List<(int Idx, FieldPosition Pos)> picked)
        {
            foreach (var i in ByBatting())
                if (!InOrder(i) && !picked.Any(x => x.Idx == i)) return i;
            return ByBatting().First(i => !InOrder(i));
        }

        // 投手枠は投手力順（打撃力で選ぶと投げられない選手が9番に来る）。ここも役割では絞らない
        // ＝投手力が一番高い者が立つ。順位付けは Strength(i).Pitching＝カテゴリ別ランクと同じ単一ソース。
        private int FallbackPitcher() => ByPitching().First(i => !InOrder(i));

        // 候補列挙は常にベンチ入りのみ（自動シード・控え一覧・ToLineupSpec の控えが全部これを通る）。
        // 並べ替えキーは枠の用途に合わせる（issue #270／#30 で「選手個人の総合ランク」は廃止済み）。
        // 打順・控え＝打撃力、投手枠＝投手力。指標はカテゴリ別ランクと同じ単一ソース。
        private IEnumerable<int> ByBatting() =>
            _eligible.OrderByDescending(i => Strength(i).Batting);

        private IEnumerable<int> ByPitching() =>
            _eligible.OrderByDescending(i => Strength(i).Pitching);

        // 控え（名簿・控え一覧の表示）は背番号順。プレイヤーがメンバー設定で振った番号が
        // そのまま控えの優先度＝継投順・代打順になる（オーナー決定 2026-07-26, issue #270）。
        // ベンチ入り9人未満の異常編成では背番号なし（0）が混じるので末尾へ送る。
        private IEnumerable<int> ByUniformNumber() =>
            _eligible.OrderBy(i => _roster[i].UniformNumber == 0 ? int.MaxValue : _roster[i].UniformNumber);

        // カテゴリ別指標はロスターが変わらない限り不変なので1度だけ集計する（並べ替えのたびに再計算しない）。
        private PlayerStrength Strength(int index)
        {
            if (_strength[index] == null) _strength[index] = PlayerStrengthProfile.Compute(_roster[index], Coeff);
            return _strength[index];
        }

        private bool InOrder(int idx)
        {
            for (var i = 0; i < 9; i++) if (_idx[i] == idx) return true;
            return _usesDh && _spIdx == idx;
        }

        private int SlotOf(int idx)
        {
            for (var i = 0; i < 9; i++) if (_idx[i] == idx) return i;
            return -1;
        }

        // ── 操作（コントローラから呼ぶ。呼び出し後に再描画する） ──

        /// <summary>打順行クリック：未選択なら選択（比較の左）。選択中に別行なら2行を入替（打順の並べ替え）。</summary>
        public void ClickRow(int slot)
        {
            if (slot < 0 || slot > 8) return;
            if (_pickKind == PickKind.SlotPosition) ClearPick();   // 守備位置ピックとは排他

            // 先発投手ピルを選んだ状態でのクリック＝先発と打順選手の入替（DH制。打順内の任意選手を
            // 先発にできる, issue #233）。元の先発はその打順枠へ入り、守備位置は枠のものを引き継ぐ。
            if (_pickKind == PickKind.StartingPitcher && _spIdx >= 0)
            {
                (_idx[slot], _spIdx) = (_spIdx, _idx[slot]);
                ClearPick();
                return;
            }

            var idx = _idx[slot];
            if (_picked < 0) { _picked = idx; _pickKind = PickKind.OrderPlayer; return; }
            if (_picked == idx) { ClearPick(); return; }

            var from = SlotOf(_picked);
            if (from >= 0)
            {
                // 打順入替：選手＋守備位置を丸ごと交換（各自の守備位置は本人に付いて動く）。
                (_idx[from], _idx[slot]) = (_idx[slot], _idx[from]);
                (_pos[from], _pos[slot]) = (_pos[slot], _pos[from]);
                ClearPick();
                return;
            }
            // 選択が控え/先発だった → この打順スロットへ投入（守備位置はスロットのものを継承）。
            _idx[slot] = _picked;
            ClearPick();
        }

        /// <summary>控えクリック：未選択なら選択。選択中の打順選手があればこの控えと入替（守備位置はスロット継承）。</summary>
        public void ClickBench(int idx)
        {
            if (idx < 0 || idx >= _roster.Count) return;
            if (!_eligible.Contains(idx)) return;   // ベンチ外は出場登録できない（安全網。候補にも出していない）
            if (_pickKind == PickKind.SlotPosition) ClearPick();   // 守備位置ピックとは排他
            if (_picked < 0) { _picked = idx; _pickKind = PickKind.OrderPlayer; return; }
            if (_picked == idx) { ClearPick(); return; }

            if (_pickKind == PickKind.StartingPitcher)
            {
                _spIdx = idx;   // 先発投手を差し替え
                ClearPick();
                return;
            }

            var from = SlotOf(_picked);
            if (from >= 0) { _idx[from] = idx; ClearPick(); return; } // 打順選手⇄控え（交代）
            _picked = idx; _pickKind = PickKind.OrderPlayer;          // 控え⇄控えは再選択
        }

        /// <summary>
        /// 先発投手ピル クリック（DH制）：先発を選択＝以後の控え/打順クリックで差し替え。
        /// 逆順（先に選手を選んでからピル）も同じ結果になるようにしてある（issue #233）。
        /// </summary>
        public void ClickStartingPitcher()
        {
            if (!_usesDh) return;
            if (_pickKind == PickKind.StartingPitcher) { ClearPick(); return; }

            if (_pickKind == PickKind.OrderPlayer && _picked >= 0 && _picked != _spIdx)
            {
                var from = SlotOf(_picked);
                // 打順の選手を選んだ状態でピル＝その選手が先発投手、元の先発が空いた打順枠へ。
                if (from >= 0 && _spIdx >= 0) { (_idx[from], _spIdx) = (_spIdx, _idx[from]); ClearPick(); return; }
                // 控えを選んだ状態でピル＝先発を差し替え（元の先発は控えへ戻る）。
                if (from < 0) { _spIdx = _picked; ClearPick(); return; }
            }

            ClearPick();   // 守備位置ピックとは排他
            _picked = _spIdx; _pickKind = PickKind.StartingPitcher;
        }

        /// <summary>
        /// 守備位置チップ クリック：1回目で選択、2回目に別スロットのチップで両者の守備位置を入替（打順ピックと同じ操作モデル）。
        /// 同じチップの再クリックで解除。9枠すべてが対象＝「投」「指」も動かせる（issue #233）。
        /// </summary>
        public void ClickPosition(int slot)
        {
            if (slot < 0 || slot > 8) return;
            if (_pickKind == PickKind.SlotPosition)
            {
                if (_posPickSlot == slot) { ClearPick(); return; }
                SwapSlotPositions(_posPickSlot, slot);
                return;
            }
            ClearPick();                     // 打順・先発の選択とは排他（片方を選ぶともう片方は解除）
            _posPickSlot = slot;
            _pickKind = PickKind.SlotPosition;
        }

        /// <summary>
        /// 2つの打順スロットの守備位置を入れ替える。交換なので9守備位置の一意性は保たれる。
        /// 「投」を任意の枠へ動かせる＝任意の選手を投手にできる（issue #233。投/野でゲートしない, issue #93）。
        /// 入替規則と投手枠・DH枠の追従は engine の <see cref="DefenseAssignment"/> が単一ソース。
        /// </summary>
        public void SwapSlotPositions(int a, int b)
        {
            if (a == b || a < 0 || b < 0 || a > 8 || b > 8) return;
            var next = DefenseAssignment.Swap(_pos, a, b);
            for (var i = 0; i < 9; i++) _pos[i] = next[i];
            // 投手枠・DH枠は配列から引き直す＝PitcherSlot / DhSlot が自動で追従する（別に持たない）。
            ClearPick();
        }

        /// <summary>DH制のON/OFF切替。</summary>
        public void ToggleDh()
        {
            if (!_usesDh)
            {
                // ON：投手スロットの投手を先発（打順外）へ。空いた打順へ控えの最良野手をDHとして入れる。
                var slot = PitcherSlot;
                if (slot < 0) return;              // 投手枠が無い（起こらないが安全網）
                var pitcherIdx = _idx[slot];
                var dh = BestBenchHitter();
                if (dh < 0) return;                // 野手控えが居なければ切替不可
                _spIdx = pitcherIdx;
                _idx[slot] = dh;
                // 「指」は守備位置として保持する（守備に就かない枠を配列上でも一意に表す）。
                // これで DH枠も他の枠と同じ入替1手で動かせる（issue #233）。
                _pos[slot] = FieldPosition.DesignatedHitter;
                _usesDh = true;
            }
            else
            {
                // OFF：DHの選手を控えへ戻し、先発投手を打順（「指」の枠＝DH解除で投手が戻る枠）へ。
                var slot = DhSlot;
                if (slot < 0) return;              // 「指」が無い（起こらないが安全網）
                _idx[slot] = _spIdx;
                _pos[slot] = FieldPosition.Pitcher;
                _spIdx = -1;
                _usesDh = false;
            }
            ClearPick();
        }

        // DH に据える打者＝打順に居ない中で最も打てる者。役割では絞らない（#379）＝控え投手でも
        // 打力が一番なら DH に入る。登板中の投手は既に打順（投手枠）に居るので InOrder が弾く。
        private int BestBenchHitter()
        {
            foreach (var i in ByBatting())
                if (!InOrder(i)) return i;
            return -1;
        }

        /// <summary>
        /// スタメンを確定できるか（ベンチ入り9人未満なら不可）。守備網羅は engine の受け入れ条件
        /// （<see cref="DefenseAssignment.CoversAllPositions"/>＝RosterTeamBuilder.BuildFromLineup と同じ判定）
        /// をそのまま安全網に使う。ここを通した仕様が試合開始時に例外で弾かれることは無い、が契約。
        /// </summary>
        public bool CanConfirm => !_shortage && CoversAllPositions;

        private bool CoversAllPositions
            => DefenseAssignment.CoversAllPositions(_pos, _usesDh ? DhSlot : -1);

        /// <summary>カーソルを合わせた選手。未選択なら比較の左、選択中なら比較の右に出る。-1＝ホバーなし。</summary>
        public void SetHovered(int index) => _hovered = index;

        /// <summary>カーソルが外れた（行/チップの PointerLeave）。表示が残らないようホバーを解除する。</summary>
        public void ClearHovered(int index) { if (_hovered == index) _hovered = -1; }
        public void SetTab(int tab) => _tab = Math.Clamp(tab, 0, 1);
        private void ClearPick() { _picked = -1; _posPickSlot = -1; _pickKind = PickKind.None; }

        /// <summary>編集内容を試合用スタメン仕様へ確定（GameSession.Lineup へ書き出す用）。</summary>
        public LineupSpec ToLineupSpec()
        {
            var order = new List<LineupSlot>(9);
            for (var i = 0; i < 9; i++)
            {
                // DH枠の守備位置は守備に就かない＝engine 側は読み飛ばす（RosterTeamBuilder が DhSlot をスキップし、
                // TeamState が表示を「指」へ差し替える）。従来どおり投手を渡して受け口の入力を変えない。
                var pos = _pos[i] == FieldPosition.DesignatedHitter ? FieldPosition.Pitcher : _pos[i];
                order.Add(new LineupSlot(_roster[_idx[i]], pos));
            }

            var used = new HashSet<int>();
            for (var i = 0; i < 9; i++) used.Add(_idx[i]);
            if (_usesDh && _spIdx >= 0) used.Add(_spIdx);

            // 控えは打順外の全員を1本の名簿にする（issue #381）。継投も代打も代走も守備固めも同じ名簿から出る。
            // 順番は背番号順（オーナー決定 2026-07-26, issue #270）＝控えの優先度は
            // メンバー設定で背番号を振った時点でプレイヤーが決めている。控え一覧の表示もこれと同じ順。
            var reserves = new List<DevelopingPlayer>();
            foreach (var i in ByUniformNumber())
            {
                if (used.Contains(i)) continue;
                reserves.Add(_roster[i]);
            }

            return new LineupSpec(
                order,
                PitcherSlot: _usesDh ? 8 : PitcherSlot,
                DhSlot: _usesDh ? DhSlot : -1,
                StartingPitcher: _usesDh && _spIdx >= 0 ? _roster[_spIdx] : null,
                Reserves: reserves,
                Name: KokoSim.Unity.Shell.NationService.ManagerSchoolName);
        }

        // ── View 構築 ──

        /// <summary>故障中の短縮表示（傷病名・段階）。健常なら空（設計書03 §3.5）。</summary>
        private static string InjuryChipText(DevelopingPlayer p)
            => p.Injury == KokoSim.Engine.Players.InjurySeverity.None
                ? ""
                : KokoSim.Unity.Shell.InjuryLabel.Type(p.InjuryType) + "・"
                  + KokoSim.Unity.Shell.InjuryLabel.Severity(p.Injury);

        /// <summary>
        /// スタメン（＋DH先発）に怪我人がいるときの警告（設計書03 §3.5「押して出場」）。
        /// 出場させると悪化する可能性があり、悪化しなくても全治が延びる＝原則として損な選択であることを明示する。
        /// </summary>
        private string InjuredStarterWarning()
        {
            var names = new List<string>();
            for (var i = 0; i < 9; i++)
            {
                var p = _roster[_idx[i]];
                if (p.Injury != KokoSim.Engine.Players.InjurySeverity.None) names.Add(p.Name);
            }
            if (_usesDh && _spIdx >= 0)
            {
                var sp = _roster[_spIdx];
                if (sp.Injury != KokoSim.Engine.Players.InjurySeverity.None) names.Add(sp.Name);
            }
            if (names.Count == 0) return "";
            return "故障者を起用中（" + string.Join("・", names)
                   + "）。出場すると全治が延び、悪化する場合があります。";
        }


        public LineupSettingView BuildView()
        {
            var v = new LineupSettingView { Tab = _tab, UsesDh = _usesDh, CanConfirm = CanConfirm };
            if (_shortage)
                v.WarnText = "ベンチ入りが9人未満です。メンバー設定で背番号を割り当ててください。";
            else if (!CoversAllPositions)
                v.WarnText = "守備位置が9ポジションを網羅していません。「守」チップの入替で直してください。";
            else
                v.WarnText = InjuredStarterWarning();

            // 操作の入口を1行だけ明記する（見つからない操作は無いのと同じ, issue #233）。
            v.HintText = _usesDh
                ? "守備位置：「守」チップを2つクリックで入替　先発投手：先発ピル→選手をクリック"
                : "守備位置：「守」チップを2つクリックで入替（投も動かせる＝任意の選手を投手にできる）";

            for (var i = 0; i < 9; i++)
            {
                var p = _roster[_idx[i]];
                v.Rows.Add(new LineupRowView
                {
                    Order = i + 1,
                    UniformNumber = p.UniformNumber,
                    PosKanji = PosKanji[_pos[i]],
                    IsDhSlot = _usesDh && i == DhSlot,
                    Name = p.Name,
                    GradeLabel = p.Grade + "年",
                    HandLabel = HandednessLabels.Combined(p.Throws, p.Bats),
                    StatText = CareerBatLine(p),
                    PlayerIndex = _idx[i],
                    IsPicked = _picked == _idx[i] && _pickKind == PickKind.OrderPlayer,
                    IsPosPicked = _pickKind == PickKind.SlotPosition && _posPickSlot == i,
                    IsCaptain = p.IsCaptain,
                    Injury = InjuryChipText(p),
                    Condition = FormModel.Quantize(p.ConditionValue),
                });
            }

            if (_usesDh && _spIdx >= 0)
            {
                var sp = _roster[_spIdx];
                v.StartingPitcherName = sp.Name;
                v.StartingPitcherPicked = _pickKind == PickKind.StartingPitcher;
            }

            // 控え（打順外・背番号順）。ToLineupSpec が積む控え名簿と同じ順なので、
            // この一覧の上から順に継投・代打で出る（表示と実際が食い違わない, issue #270）。
            var used = new HashSet<int>();
            for (var i = 0; i < 9; i++) used.Add(_idx[i]);
            if (_usesDh && _spIdx >= 0) used.Add(_spIdx);
            foreach (var i in ByUniformNumber())
            {
                if (used.Contains(i)) continue;
                var p = _roster[i];
                v.Bench.Add(new BenchRowView
                {
                    Index = i,
                    UniformNumber = p.UniformNumber,
                    Name = p.Name,
                    GradeLabel = p.Grade + "年",
                    IsPicked = _picked == i && _pickKind == PickKind.OrderPlayer,
                    Injury = InjuryChipText(p),
                    Condition = FormModel.Quantize(p.ConditionValue),
                });
            }

            // 共通トップバーのチーム総合力＝6指標のリーグ標準化総合（③, 全画面統一）。スタメン依存にしない。
            v.TeamRankGrade = KokoSim.Unity.Shell.TeamOverall.GradeOf(_roster);

            // 比較：左＝選択中、右＝ホバー中（選択があるとき・別人のとき）。
            // 未選択のときはホバーだけで能力値を見せる（左＝ホバー選手）。クリック不要で一覧を舐められる。
            var leftIdx = _picked >= 0 ? _picked : _hovered;
            var rightIdx = (_picked >= 0 && _hovered != _picked) ? _hovered : -1;
            v.CardA = MakeCard(leftIdx);
            v.CardB = MakeCard(rightIdx);
            v.ShowPitchChart = PlayerCompareTabs.ShowPitchChart(_tab);
            BuildRows(v, leftIdx, rightIdx);
            BuildAptNodes(v, leftIdx, rightIdx);
            return v;
        }

        private void BuildRows(LineupSettingView v, int leftIdx, int rightIdx)
        {
            var a = leftIdx >= 0 ? _roster[leftIdx] : null;
            var b = rightIdx >= 0 ? _roster[rightIdx] : null;
            v.Rows2 = PlayerCompareTabs.BuildRows(_tab, a, b);
        }

        private void BuildAptNodes(LineupSettingView v, int leftIdx, int rightIdx)
        {
            var a = leftIdx >= 0 ? _roster[leftIdx] : null;
            var b = rightIdx >= 0 ? _roster[rightIdx] : null;
            v.ShowApt = _tab == 0 && (a != null || b != null);
            if (!v.ShowApt) return;
            v.HasAptA = a != null;
            v.HasAptB = b != null;
            for (var i = 0; i < AptOrder.Length; i++)
            {
                // 判断画面は実効守備力ランク一本（issue #371）＝ Fielding×適性補正。生の適性ランクは出さない。
                if (a != null) v.AptA.Add(Tiers.FromStrength(EffectiveFielding.Rank(a, AptOrder[i], FieldingCoeffDefault)).ToString());
                if (b != null) v.AptB.Add(Tiers.FromStrength(EffectiveFielding.Rank(b, AptOrder[i], FieldingCoeffDefault)).ToString());
            }
        }

        private CompareCardView MakeCard(int index)
        {
            if (index < 0 || index >= _roster.Count) return new CompareCardView { Present = false };
            var p = _roster[index];
            var topKmh = (int)System.Math.Round(PitcherAttributes.VelocityKmhFromLevel(p.Level(AbilityKind.Velocity)));
            return new CompareCardView
            {
                Present = true,
                Name = p.Name,
                GradeLabel = p.Grade + "年",
                // カテゴリ別ランク（打撃力/走力/守備力/投手力）。メンバー設定の比較カードと同じ部品・同じ係数。
                // 並べ替えキー（ByBatting/ByPitching）と同じメモを引く＝表示と並びで指標がズレない。
                Strength = Strength(index),
                IsCaptain = p.IsCaptain,
                Stats = BuildStats(p),
                // 持ち球（ミニ球種チャート）は詳細画面と同じ生成ロジック（単一ソース, issue #94 案C）。
                Pitches = PitchData.ToPitchChartData(PlayerDetailState.BuildPitchData(p), topKmh),
            };
        }

        // 成績表（通算／公式戦通算／今大会）。GameSession.Stats を選手IDで引く。未接続は "—"。
        // 「通算」は練習試合を含む全試合、「公式戦」は大会の試合だけ（練習試合は入らない）。
        private static StatTableView BuildStats(DevelopingPlayer p)
        {
            var stats = GameSession.Current.Stats;
            var scopes = new[]
            {
                ("通算", stats.Career.Get(p.Id)),
                ("公式戦", stats.Official.Get(p.Id)),
                ("今大会", stats.CurrentTournament.Get(p.Id)),
            };

            // **どの指標を並べるかは通算（最も広いスコープ）で1度だけ決める**（issue #388）。
            // 旧構造はスコープごとに決めていたが、表にすると列で行が食い違う
            // （例: 通算では投げたが今大会は投げていない選手で、列ごとに別の指標が並ぶ）。
            var table = new StatTableView { Scopes = scopes.Select(x => x.Item1).ToList() };
            foreach (var metric in MetricsFor(scopes[0].Item2))
            {
                table.Rows.Add(new StatRowView
                {
                    Label = metric.Label,
                    Values = scopes.Select(x => metric.Value(x.Item2)).ToList(),
                });
            }
            return table;
        }

        /// <summary>成績表の1指標（ラベルと、スコープの成績から値を引く関数）。</summary>
        private sealed class StatMetric
        {
            public string Label = "";
            public System.Func<PlayerStats, string> Value = _ => "—";
        }

        // 載せる指標は**出場実績**で決める（導出役割で切り替えない, #366/#379）。役割で切ると
        // 「打率.350の投手」「防御率1.42の野手」の片側が消える。判定は選手詳細
        // （PlayerDetailState.BuildScope）と同じ条件で揃える。
        //
        // 行数は投打どちらでも4に保つ。表形式にして横幅の制約は消えた（#388）が、比較パネルの
        // **縦**は2枚のカードで分け合うので、ここは一覧の走り読み用に絞ったまま置く
        // ＝全指標は選手詳細が持つ。二刀流は投打の代表指標を2つずつ。
        private static List<StatMetric> MetricsFor(PlayerStats career)
        {
            var pit = career?.Pitching;
            var bat = career?.Batting;
            var hasPitching = pit != null && (pit.Games > 0 || pit.BattersFaced > 0);
            var hasBatting = bat != null && (bat.Games > 0 || bat.PlateAppearances > 0);

            if (hasPitching && hasBatting)
            {
                return new List<StatMetric>
                {
                    EraMetric(), StrikeOutsMetric(), AverageMetric(), HomeRunsMetric(),
                };
            }
            if (hasPitching)
            {
                // 勝敗投手は概念ごと撤去（高校野球では主役でない・監督判断2026-08-27）。
                // 4指標を RecordRoom.PitchingCategories（記録タブの投手部門）と同じ顔ぶれに揃える。
                return new List<StatMetric>
                {
                    EraMetric(),
                    new StatMetric { Label = "WHIP", Value = s => s?.Pitching is { Outs: > 0 } q
                        ? q.Whip.ToString("0.00") : "—" },
                    StrikeOutsMetric(),
                    new StatMetric { Label = "投球回", Value = s => s?.Pitching is { Outs: > 0 } q
                        ? q.InningsText : "—" },
                };
            }
            // 打撃実績のみ、および**どちらの実績も無い**とき（＝全項目「—」で枠を保つ）。
            return new List<StatMetric>
            {
                AverageMetric(), HomeRunsMetric(),
                new StatMetric { Label = "打点", Value = s => s?.Batting is { Games: > 0 } b
                    ? b.Rbi.ToString() : "—" },
                new StatMetric { Label = "出塁率", Value = s => s?.Batting is { } b && (b.AtBats + b.Walks) > 0
                    ? b.Obp.ToString(".000") : "—" },
            };
        }

        // 指標の定義（ラベルと値の書式）。表の全スコープで同じ関数を通す＝書式が列でぶれない。
        private static StatMetric EraMetric() => new StatMetric
        {
            Label = "防御率",
            Value = s => s?.Pitching is { Outs: > 0 } p ? p.Era.ToString("0.00") : "—",
        };

        private static StatMetric StrikeOutsMetric() => new StatMetric
        {
            Label = "奪三振",
            Value = s => s?.Pitching is { Games: > 0 } p ? p.StrikeOuts.ToString() : "—",
        };

        private static StatMetric AverageMetric() => new StatMetric
        {
            Label = "打率",
            Value = s => s?.Batting is { AtBats: > 0 } b ? b.Average.ToString(".000") : "—",
        };

        private static StatMetric HomeRunsMetric() => new StatMetric
        {
            Label = "本塁打",
            Value = s => s?.Batting is { Games: > 0 } b ? b.HomeRuns.ToString() : "—",
        };

        // 打順行の簡易成績（通算のみ・全員打撃成績＝投手も打率/本/点）。データ無しは「-」。
        private static string CareerBatLine(DevelopingPlayer p)
        {
            var b = GameSession.Current.Stats.Career.Get(p.Id)?.Batting;
            if (b == null || b.Games == 0) return "打率 -　本 -　点 -";
            var avg = b.AtBats > 0 ? b.Average.ToString(".000") : "-";
            return "打率 " + avg + "　本 " + b.HomeRuns + "　点 " + b.Rbi;
        }
    }
}
