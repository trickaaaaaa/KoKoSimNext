using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Season;
using KokoSim.Unity.Shell;

namespace KokoSim.Unity.Member
{
    // ── View DTO（コントローラは描画するだけ・エンジン非依存） ──

    /// <summary>背番号枠1つ分の表示データ。PlayerIndex<0 は未割当。</summary>
    public sealed class SlotView
    {
        public int Number;
        public string PosKanji = "";     // 1〜9のみ守備位置慣例（10〜20は空）
        public bool IsStarter;           // 背番号1〜9
        public bool IsPicked;            // 選択中の選手（この枠に居る）
        public int PlayerIndex = -1;     // 共有ロスターの安定index（-1=未割当）
        public string Name = "";
        public string GradeLabel = "";   // 学年（例「2年」）
        public string HandLabel = "";    // 投打（例「右投左打」）
        // 枠のランクチップ：1〜9は「その守備位置の適性」（投=投手ランク）、10〜20は総合。
        public string RankGrade = "";
        public bool IsCaptain;
    }

    /// <summary>割当元プールの1選手。</summary>
    public sealed class PoolView
    {
        public int Index;
        public string Name = "";
        public string GradeLabel = "";
        public string OverallGrade = "";
        public bool IsPicked;            // 選択中の選手
    }

    /// <summary>比較カード（A/B）。Present=false は空きスロット。</summary>
    public sealed class CompareCard
    {
        public bool Present;
        public string Name = "";
        public string GradeLabel = "";
        public string OverallGrade = "";
        public bool IsCaptain;
    }

    /// <summary>比較の1能力行。</summary>
    public sealed class CompareRow
    {
        public string Label = "";
        public int ValueA;
        public int ValueB;
        public bool HasA;
        public bool HasB;
        public int Winner;   // -1=A, 0=互角/該当なし, 1=B
    }


    public sealed class MemberSettingView
    {
        public int AssignedCount;
        public int BenchOutCount;
        public string TeamRankGrade = "C";
        public List<SlotView> Slots = new List<SlotView>();
        public List<PoolView> Pool = new List<PoolView>();
        public CompareCard CardA = new CompareCard();
        public CompareCard CardB = new CompareCard();
        public int Tab;                  // 0=打撃走塁（＋守備適性ダイヤ） / 1=守備投手
        public string[] TabLabels = { "打撃・走塁", "守備・投手" };
        public List<CompareRow> Rows = new List<CompareRow>();
        // 守備適性ダイヤ（打撃・走塁タブ内に併載・選手ごとに1枚）。ShowApt=false のタブでは描かない。
        // AptA/AptB は9守備位置（投捕一二三遊左中右順）のランク（G〜S）。未選択の側は Has*=false。
        public bool ShowApt;
        public bool HasAptA;
        public bool HasAptB;
        public List<string> AptA = new List<string>();
        public List<string> AptB = new List<string>();
    }

    /// <summary>
    /// メンバー設定（背番号1〜20割当＋2選手比較）の状態。全画面共有の RosterService.Roster を
    /// 単一ソースに、背番号割当は UniformNumberAssigner（エンジン）へ委譲する。ここはUnityEngine非依存。
    /// </summary>
    public sealed class MemberSettingState
    {
        private const int Slots = UniformNumberAssigner.BenchSize; // 20

        // 背番号1〜9の守備位置慣例（設計書06 §3.3b）。ダイヤモンド配置のヒント。
        private static readonly string[] StarterPos = { "投", "捕", "一", "二", "三", "遊", "左", "中", "右" };
        // 背番号1〜9 → 守備ポジション（適性の参照先）。
        private static readonly FieldPosition[] SlotPosition =
        {
            FieldPosition.Pitcher, FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase,
            FieldPosition.ThirdBase, FieldPosition.Shortstop, FieldPosition.LeftField, FieldPosition.CenterField,
            FieldPosition.RightField,
        };

        // 比較タブ①打撃・走塁（＋守備適性ダイヤ） ②守備・投手（守備力=Fielding含む）。全能力を網羅。
        private static readonly (string Label, Func<DevelopingPlayer, int> Get)[] TabBat =
        {
            ("ミート", p => p.Level(AbilityKind.Contact)),
            ("パワー", p => p.Level(AbilityKind.Power)),
            ("弾道", p => p.Level(AbilityKind.LaunchTendency)),
            ("選球眼", p => p.Level(AbilityKind.Discipline)),
            ("走力", p => p.Level(AbilityKind.Speed)),
            ("バント", p => p.Level(AbilityKind.Bunt)),
            ("盗塁", p => p.Level(AbilityKind.Steal)),
            ("走塁", p => p.Level(AbilityKind.Baserunning)),
            ("精神力", p => p.Mental),
        };
        private static readonly (string Label, Func<DevelopingPlayer, int> Get)[] TabDef =
        {
            ("守備力", p => p.Level(AbilityKind.Fielding)),
            ("捕球", p => p.Level(AbilityKind.Catching)),
            ("肩", p => p.Level(AbilityKind.ArmStrength)),
            ("送球", p => p.Level(AbilityKind.ThrowAccuracy)),
            ("リード", p => p.Lead),
            ("球速", p => p.Level(AbilityKind.Velocity)),
            ("制球", p => p.Level(AbilityKind.Control)),
            ("スタミナ", p => p.Level(AbilityKind.Stamina)),
            ("球種", p => p.Level(AbilityKind.PitchRank)),
        };

        private readonly IReadOnlyList<DevelopingPlayer> _roster;
        // 統合モデル：_picked＝クリックで選択中の選手（比較の左）。_hovered＝カーソルを合わせた選手（比較の右）。
        // もう一度クリックすると _picked と対象の背番号を交換する（＝「入れ替わる」）。
        private int _picked = -1;
        private int _hovered = -1;
        private int _tab;

        public MemberSettingState()
        {
            _roster = RosterService.Roster;
        }

        // ── 操作（コントローラから呼ぶ。呼び出し後に再描画する） ──

        /// <summary>
        /// 枠クリック。選手を選択中なら、その枠へ配置（占有していれば背番号を交換／空きなら移動）。
        /// 未選択で占有枠なら、その選手を選択する（枠から掴む）。
        /// </summary>
        public void ClickSlot(int number)
        {
            if (_picked >= 0)
            {
                UniformNumberAssigner.Place(_roster, _roster[_picked], number);
                _picked = -1;
                return;
            }
            var holder = IndexOfNumber(number);
            if (holder >= 0) _picked = holder;   // 占有枠から選択
        }

        /// <summary>
        /// プール（ベンチ外）選手クリック。未選択なら選択。枠から選択中の選手がいれば背番号を交換
        /// （＝そのベンチ入り選手は控え落ち）。同じ選手を再クリックで選択解除。
        /// </summary>
        public void ClickPool(int index)
        {
            if (index < 0 || index >= _roster.Count) return;
            if (_picked < 0) { _picked = index; return; }
            if (_picked == index) { _picked = -1; return; }
            UniformNumberAssigner.SwapPlayers(_roster[_picked], _roster[index]);
            _picked = -1;
        }

        /// <summary>枠の割当を解除（ベンチ外へ）。</summary>
        public void ClearSlot(int number)
        {
            var holder = IndexOfNumber(number);
            if (holder >= 0) UniformNumberAssigner.Clear(_roster[holder]);
        }

        /// <summary>カーソルを合わせた選手（比較の右）。選択中(_picked)がある時だけ右に反映される。</summary>
        public void SetHovered(int index) => _hovered = index;

        public void SetTab(int tab) => _tab = Math.Clamp(tab, 0, 1);

        /// <summary>能力順で背番号を仮割当し直す（自動割当ボタン）。</summary>
        public void AutoAssign() { UniformNumberAssigner.AutoAssign(_roster); _picked = -1; }

        // ── View 構築 ──

        public MemberSettingView BuildView()
        {
            var v = new MemberSettingView { Tab = _tab };

            for (var n = 1; n <= Slots; n++)
            {
                var idx = IndexOfNumber(n);
                var slot = new SlotView
                {
                    Number = n,
                    IsStarter = n <= 9,
                    PosKanji = n <= 9 ? StarterPos[n - 1] : "",
                    IsPicked = idx >= 0 && idx == _picked,
                    PlayerIndex = idx,
                };
                if (idx >= 0)
                {
                    var p = _roster[idx];
                    slot.Name = p.Name;
                    slot.GradeLabel = p.Grade + "年";
                    slot.HandLabel = HandLabel(p);
                    slot.RankGrade = n <= 9 ? SlotRank(p, n) : OverallGrade(p);
                    slot.IsCaptain = p.IsCaptain;
                    v.AssignedCount++;
                }
                v.Slots.Add(slot);
            }
            v.BenchOutCount = _roster.Count - v.AssignedCount;

            // プール：ベンチ外（背番号未割当）だけを総合力降順で並べる。
            var ordered = Enumerable.Range(0, _roster.Count)
                .Where(i => _roster[i].UniformNumber == 0)
                .OrderByDescending(i => _roster[i].AverageLevel());
            foreach (var i in ordered)
            {
                var p = _roster[i];
                v.Pool.Add(new PoolView
                {
                    Index = i,
                    Name = p.Name,
                    GradeLabel = p.Grade + "年",
                    OverallGrade = OverallGrade(p),
                    IsPicked = _picked == i,
                });
            }

            if (_roster.Count > 0)
                v.TeamRankGrade = Tiers.FromStrength(_roster.Average(p => p.AverageLevel())).ToString();

            // 左＝選択中(_picked)、右＝ホバー中(_hovered)。右は選択中があるときだけ／別人のときだけ。
            var leftIdx = _picked;
            var rightIdx = (_picked >= 0 && _hovered != _picked) ? _hovered : -1;
            v.CardA = MakeCard(leftIdx);
            v.CardB = MakeCard(rightIdx);
            BuildRows(v, leftIdx, rightIdx);
            BuildAptNodes(v, leftIdx, rightIdx);
            return v;
        }

        private void BuildRows(MemberSettingView v, int leftIdx, int rightIdx)
        {
            var a = leftIdx >= 0 ? _roster[leftIdx] : null;
            var b = rightIdx >= 0 ? _roster[rightIdx] : null;
            if (a == null && b == null) return;

            var table = _tab == 0 ? TabBat : TabDef;
            foreach (var (label, get) in table)
            {
                var row = new CompareRow { Label = label, HasA = a != null, HasB = b != null };
                if (a != null) row.ValueA = get(a);
                if (b != null) row.ValueB = get(b);
                if (a != null && b != null)
                    row.Winner = row.ValueA > row.ValueB ? -1 : row.ValueA < row.ValueB ? 1 : 0;
                v.Rows.Add(row);
            }
        }

        // 守備適性ダイヤ（打撃・走塁タブでのみ表示）。選手ごとに1枚ぶん、9守備位置のランクを作る。
        private void BuildAptNodes(MemberSettingView v, int leftIdx, int rightIdx)
        {
            var a = leftIdx >= 0 ? _roster[leftIdx] : null;
            var b = rightIdx >= 0 ? _roster[rightIdx] : null;
            v.ShowApt = _tab == 0 && (a != null || b != null);
            if (!v.ShowApt) return;

            v.HasAptA = a != null;
            v.HasAptB = b != null;
            for (var i = 0; i < SlotPosition.Length; i++)
            {
                if (a != null) v.AptA.Add(Tiers.FromStrength(a.Aptitude(SlotPosition[i])).ToString());
                if (b != null) v.AptB.Add(Tiers.FromStrength(b.Aptitude(SlotPosition[i])).ToString());
            }
        }

        private CompareCard MakeCard(int index)
        {
            if (index < 0 || index >= _roster.Count) return new CompareCard { Present = false };
            var p = _roster[index];
            return new CompareCard
            {
                Present = true,
                Name = p.Name,
                GradeLabel = p.Grade + "年",
                OverallGrade = OverallGrade(p),
                IsCaptain = p.IsCaptain,
            };
        }

        private int IndexOfNumber(int number)
        {
            for (var i = 0; i < _roster.Count; i++)
                if (_roster[i].UniformNumber == number) return i;
            return -1;
        }

        // 背番号1〜9のランク：投=投手ランク（投手能力平均）、他=その守備位置の適性ランク。
        private static string SlotRank(DevelopingPlayer p, int number)
        {
            if (number == 1)
            {
                var sum = 0.0;
                foreach (var k in AbilityKinds.Pitching) sum += p.Level(k);
                return Tiers.FromStrength(sum / AbilityKinds.Pitching.Length).ToString();
            }
            return Tiers.FromStrength(p.Aptitude(SlotPosition[number - 1])).ToString();
        }

        private static string OverallGrade(DevelopingPlayer p) => Tiers.FromStrength(p.AverageLevel()).ToString();

        // 投打（例「右投左打」）。列挙の名前先頭文字で判定（L=左, S=両, 既定=右）。
        private static string HandLabel(DevelopingPlayer p)
        {
            var t = p.Throws.ToString();
            var b = p.Bats.ToString();
            var tj = t.StartsWith("L") ? "左投" : t.StartsWith("S") ? "両投" : "右投";
            var bj = b.StartsWith("L") ? "左打" : b.StartsWith("S") ? "両打" : "右打";
            return tj + bj;
        }
    }
}
