using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Season;
using KokoSim.Engine.Stats;
using KokoSim.Unity.Shell;

namespace KokoSim.Unity.Lineup
{
    // ── View DTO（コントローラは描画するだけ・UnityEngine 非依存） ──

    /// <summary>打順1枠の表示データ。</summary>
    public sealed class LineupRowView
    {
        public int Order;                 // 打順 1〜9
        public string PosKanji = "";      // 守備位置漢字（DHは「指」・投手は「投」）
        public bool IsPitcherSlot;        // 非DHの投手スロット（守備位置は固定）
        public bool IsDhSlot;
        public bool PosEditable;          // 守備位置を変更できる（野手スロットのみ）
        public string Name = "";
        public string GradeLabel = "";
        public string HandLabel = "";
        public string OverallGrade = "C";
        public string StatText = "";      // 通算の簡易成績（打率・本・点。全員打撃成績・データ無しは「-」）
        public int PlayerIndex = -1;      // 共有ロスターの安定index
        public bool IsPicked;
        public bool IsCaptain;
    }

    /// <summary>控え（打順外）の1選手。</summary>
    public sealed class BenchRowView
    {
        public int Index;
        public string Name = "";
        public string GradeLabel = "";
        public string OverallGrade = "C";
        public bool IsPitcher;
        public bool IsPicked;
    }

    /// <summary>成績1項目（ラベル＋値）。値未接続は "—"。</summary>
    public sealed class StatItemView
    {
        public string Label = "";
        public string Value = "—";
    }

    /// <summary>成績1スコープ（通算 or 今大会）の列。</summary>
    public sealed class StatColumnView
    {
        public string Title = "";
        public List<StatItemView> Items = new List<StatItemView>();
    }

    /// <summary>比較カード（A/B）＝選手ヘッダ＋成績2列。</summary>
    public sealed class CompareCardView
    {
        public bool Present;
        public string Name = "";
        public string GradeLabel = "";
        public string OverallGrade = "C";
        public bool IsCaptain;
        public List<StatColumnView> Stats = new List<StatColumnView>();  // [通算, 今大会]
    }

    /// <summary>比較の1能力行。</summary>
    public sealed class CompareRowView
    {
        public string Label = "";
        public int ValueA;
        public int ValueB;
        public bool HasA;
        public bool HasB;
        public int Winner;   // -1=A, 0=互角, 1=B
    }

    public sealed class LineupSettingView
    {
        public List<LineupRowView> Rows = new List<LineupRowView>();
        public List<BenchRowView> Bench = new List<BenchRowView>();
        public bool UsesDh;
        public string StartingPitcherName = "";
        public string StartingPitcherGrade = "C";
        public bool StartingPitcherPicked;
        public string TeamRankGrade = "C";

        public CompareCardView CardA = new CompareCardView();
        public CompareCardView CardB = new CompareCardView();
        public int Tab;
        public string[] TabLabels = { "打撃・走塁", "守備・投手" };
        public bool CanConfirm = true;      // ベンチ入りが9人未満なら false（確定ボタンを塞ぐ）
        public string WarnText = "";        // 上の理由（空＝警告なし）
        public List<CompareRowView> Rows2 = new List<CompareRowView>();  // 比較能力行
        public bool ShowApt;
        public bool HasAptA;
        public bool HasAptB;
        public List<string> AptA = new List<string>();
        public List<string> AptB = new List<string>();
    }

    /// <summary>
    /// 試合前スタメン設定の状態。全画面共有 <see cref="RosterService.Roster"/> を単一ソースに、打順9人＋守備位置・
    /// DH・先発を編集し、確定で <see cref="GameSession.Lineup"/>（<see cref="LineupSpec"/>）へ書き出す。UnityEngine 非依存。
    /// </summary>
    public sealed class LineupSettingState
    {
        private enum PickKind { None, OrderPlayer, StartingPitcher }

        // 守備位置→漢字。
        private static readonly Dictionary<FieldPosition, string> PosKanji = new()
        {
            { FieldPosition.Pitcher, "投" }, { FieldPosition.Catcher, "捕" }, { FieldPosition.FirstBase, "一" },
            { FieldPosition.SecondBase, "二" }, { FieldPosition.ThirdBase, "三" }, { FieldPosition.Shortstop, "遊" },
            { FieldPosition.LeftField, "左" }, { FieldPosition.CenterField, "中" }, { FieldPosition.RightField, "右" },
        };

        // 選択できる守備位置（投手を除く8つ・野手スロットの守備位置ドロップダウン）。
        public static readonly FieldPosition[] FielderPositions =
        {
            FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase, FieldPosition.ThirdBase,
            FieldPosition.Shortstop, FieldPosition.LeftField, FieldPosition.CenterField, FieldPosition.RightField,
        };

        /// <summary>守備位置の漢字ラベル（守備位置ドロップダウン用）。</summary>
        public static string PosLabel(FieldPosition p) => PosKanji[p];

        // 比較タブ①打撃・走塁（＋守備適性ダイヤ） ②守備・投手。メンバー設定と同一定義。
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

        // 守備適性ダイヤ用の9守備位置（投捕一二三遊左中右順）。
        private static readonly FieldPosition[] AptOrder =
        {
            FieldPosition.Pitcher, FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase,
            FieldPosition.ThirdBase, FieldPosition.Shortstop, FieldPosition.LeftField, FieldPosition.CenterField,
            FieldPosition.RightField,
        };

        private readonly IReadOnlyList<DevelopingPlayer> _roster;

        // 出場登録できる選手＝ベンチ入り（背番号1〜20。設計書06 §3.3b）のロスターindex。
        // スタメン・控え・ブルペンはすべてこの集合からしか選べない（ベンチ外は候補に出さない）。
        private readonly List<int> _eligible;
        private readonly bool _shortage;   // ベンチ入りが9人未満＝打順を組めない（確定を塞ぐ）

        // 打順（9スロット）：各スロットのロスターindexと守備位置。
        private readonly int[] _idx = new int[9];
        private readonly FieldPosition[] _pos = new FieldPosition[9];
        private bool _usesDh;
        private int _dhSlot = -1;
        private int _pitcherSlot = 8;
        private int _spIdx = -1;         // DH制の先発投手（ロスターindex）

        // 操作状態：クリック選択＝比較の左＋入替の主体。ホバー＝比較の右。
        private int _picked = -1;
        private PickKind _pickKind = PickKind.None;
        private int _hovered = -1;
        private int _tab;

        public LineupSettingState()
        {
            _roster = RosterService.Roster;
            var benchIn = Enumerable.Range(0, _roster.Count).Where(i => _roster[i].UniformNumber >= 1).ToList();
            // ベンチ入りが9人に満たない編成では打順を組めない。画面は従来通り全部員で描いたうえで確定を塞ぎ、
            // メンバー設定でベンチ入りを増やしてもらう（不正な LineupSpec はエンジンが弾く）。
            _shortage = benchIn.Count < 9;
            _eligible = _shortage ? Enumerable.Range(0, _roster.Count).ToList() : benchIn;
            SeedFromUniformNumbers();
        }

        // 初期打順をシード（非DH）。守備位置は背番号2〜9の慣例（捕一二三遊左中右）を各自が保持しつつ、
        // 打順は打撃能力（ミート＋パワー）順に並べ替える＝強打者を中軸へ（背番号順のままだと弱打者が中軸に来て点が入らない）。
        private void SeedFromUniformNumbers()
        {
            // 8野手を背番号2〜9で拾い、守備位置を付ける。
            var fielders = new List<(int Idx, FieldPosition Pos)>(8);
            for (var i = 0; i < 8; i++)
            {
                var idx = IndexOfUniform(i + 2);
                if (idx < 0) idx = FallbackFielder(i);
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
            // 9番＝投手（背番号1）。
            var pit = IndexOfUniform(1);
            _idx[8] = pit >= 0 ? pit : FallbackPitcher();
            _pos[8] = FieldPosition.Pitcher;
            _usesDh = false;
            _dhSlot = -1;
            _pitcherSlot = 8;
        }

        // 打撃力の簡易指標（打順並べ替え用）。ミート＋パワー。
        private static int Hitting(DevelopingPlayer p) => p.Level(AbilityKind.Contact) + p.Level(AbilityKind.Power);

        private int IndexOfUniform(int number)
        {
            for (var i = 0; i < _roster.Count; i++)
                if (_roster[i].UniformNumber == number) return i;
            return -1;
        }

        private int FallbackFielder(int slot)
        {
            // 未割当時：まだ打順に居ない野手を能力順で拾う。
            foreach (var i in ByAbility())
                if (!_roster[i].IsPitcher && !InOrder(i)) return i;
            return ByAbility().First(i => !InOrder(i));
        }

        private int FallbackPitcher()
        {
            foreach (var i in ByAbility())
                if (_roster[i].IsPitcher && !InOrder(i)) return i;
            return ByAbility().First(i => !InOrder(i));
        }

        // 候補列挙は常にベンチ入りのみ（自動シード・控え一覧・ToLineupSpec のベンチ/ブルペンが全部これを通る）。
        private IEnumerable<int> ByAbility() =>
            _eligible.OrderByDescending(i => _roster[i].AverageLevel());

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

        /// <summary>先発投手ピル クリック（DH制）：先発を比較対象に選択＝以後の控え投手クリックで差し替え。</summary>
        public void ClickStartingPitcher()
        {
            if (!_usesDh) return;
            if (_pickKind == PickKind.StartingPitcher) { ClearPick(); return; }
            _picked = _spIdx; _pickKind = PickKind.StartingPitcher;
        }

        /// <summary>守備位置変更：slot の守備位置を pos に。既に pos を持つ野手スロットと位置を交換（一意を保つ）。</summary>
        public void SetSlotPosition(int slot, FieldPosition pos)
        {
            if (slot < 0 || slot > 8) return;
            if (_usesDh && slot == _dhSlot) return;         // DHは守備に就かない
            if (!_usesDh && slot == _pitcherSlot) return;   // 投手スロットは固定
            for (var i = 0; i < 9; i++)
            {
                if (i == slot) continue;
                if ((_usesDh && i == _dhSlot)) continue;
                if (_pos[i] == pos) { _pos[i] = _pos[slot]; break; } // 相手スロットへ元の位置を渡す
            }
            _pos[slot] = pos;
            ClearPick();
        }

        /// <summary>DH制のON/OFF切替。</summary>
        public void ToggleDh()
        {
            if (!_usesDh)
            {
                // ON：投手スロットの投手を先発（打順外）へ。空いた打順へ控えの最良野手をDHとして入れる。
                var pitcherIdx = _idx[_pitcherSlot];
                var dh = BestBenchHitter();
                if (dh < 0) return;                // 野手控えが居なければ切替不可
                _spIdx = pitcherIdx;
                _idx[_pitcherSlot] = dh;
                _dhSlot = _pitcherSlot;
                _usesDh = true;
            }
            else
            {
                // OFF：DHの選手を控えへ戻し、先発投手を打順（元の投手スロット）へ。
                _idx[_dhSlot] = _spIdx;
                _pos[_dhSlot] = FieldPosition.Pitcher;
                _pitcherSlot = _dhSlot;
                _spIdx = -1;
                _dhSlot = -1;
                _usesDh = false;
            }
            ClearPick();
        }

        private int BestBenchHitter()
        {
            foreach (var i in ByAbility())
                if (!_roster[i].IsPitcher && !InOrder(i)) return i;
            return -1;
        }

        /// <summary>スタメンを確定できるか（ベンチ入り9人未満なら不可）。</summary>
        public bool CanConfirm => !_shortage;

        public void SetHovered(int index) => _hovered = index;
        public void SetTab(int tab) => _tab = Math.Clamp(tab, 0, 1);
        private void ClearPick() { _picked = -1; _pickKind = PickKind.None; }

        /// <summary>編集内容を試合用スタメン仕様へ確定（GameSession.Lineup へ書き出す用）。</summary>
        public LineupSpec ToLineupSpec()
        {
            var order = new List<LineupSlot>(9);
            for (var i = 0; i < 9; i++)
                order.Add(new LineupSlot(_roster[_idx[i]], _pos[i]));

            var used = new HashSet<int>();
            for (var i = 0; i < 9; i++) used.Add(_idx[i]);
            if (_usesDh && _spIdx >= 0) used.Add(_spIdx);

            // 控え：打順外の投手＝ブルペン、野手＝ベンチ。
            var bullpen = new List<DevelopingPlayer>();
            var bench = new List<DevelopingPlayer>();
            foreach (var i in ByAbility())
            {
                if (used.Contains(i)) continue;
                if (_roster[i].IsPitcher) bullpen.Add(_roster[i]);
                else bench.Add(_roster[i]);
            }

            return new LineupSpec(
                order,
                PitcherSlot: _usesDh ? 8 : _pitcherSlot,
                DhSlot: _usesDh ? _dhSlot : -1,
                StartingPitcher: _usesDh && _spIdx >= 0 ? _roster[_spIdx] : null,
                Bullpen: bullpen,
                Bench: bench,
                Name: "桜丘");
        }

        // ── View 構築 ──

        public LineupSettingView BuildView()
        {
            var v = new LineupSettingView { Tab = _tab, UsesDh = _usesDh, CanConfirm = !_shortage };
            if (_shortage)
                v.WarnText = "ベンチ入りが9人未満です。メンバー設定で背番号を割り当ててください。";

            for (var i = 0; i < 9; i++)
            {
                var p = _roster[_idx[i]];
                var isDh = _usesDh && i == _dhSlot;
                var isPitcherSlot = !_usesDh && i == _pitcherSlot;
                v.Rows.Add(new LineupRowView
                {
                    Order = i + 1,
                    PosKanji = isDh ? "指" : PosKanji[_pos[i]],
                    IsPitcherSlot = isPitcherSlot,
                    IsDhSlot = isDh,
                    PosEditable = !isDh && !isPitcherSlot,
                    Name = p.Name,
                    GradeLabel = p.Grade + "年",
                    HandLabel = HandLabel(p),
                    OverallGrade = OverallGrade(p),
                    StatText = CareerBatLine(p),
                    PlayerIndex = _idx[i],
                    IsPicked = _picked == _idx[i] && _pickKind == PickKind.OrderPlayer,
                    IsCaptain = p.IsCaptain,
                });
            }

            if (_usesDh && _spIdx >= 0)
            {
                var sp = _roster[_spIdx];
                v.StartingPitcherName = sp.Name;
                v.StartingPitcherGrade = OverallGrade(sp);
                v.StartingPitcherPicked = _pickKind == PickKind.StartingPitcher;
            }

            // 控え（打順外・投手先頭→総合力降順）。
            var used = new HashSet<int>();
            for (var i = 0; i < 9; i++) used.Add(_idx[i]);
            if (_usesDh && _spIdx >= 0) used.Add(_spIdx);
            foreach (var i in _eligible
                         .Where(i => !used.Contains(i))
                         .OrderByDescending(i => _roster[i].IsPitcher ? 1 : 0)
                         .ThenByDescending(i => _roster[i].AverageLevel()))
            {
                var p = _roster[i];
                v.Bench.Add(new BenchRowView
                {
                    Index = i,
                    Name = p.Name,
                    GradeLabel = p.Grade + "年",
                    OverallGrade = OverallGrade(p),
                    IsPitcher = p.IsPitcher,
                    IsPicked = _picked == i && _pickKind == PickKind.OrderPlayer,
                });
            }

            // 共通トップバーのチーム総合力＝6指標のリーグ標準化総合（③, 全画面統一）。スタメン依存にしない。
            v.TeamRankGrade = KokoSim.Unity.Shell.TeamOverall.GradeOf(_roster);

            // 比較：左＝選択中、右＝ホバー中（選択があるとき・別人のとき）。
            var leftIdx = _picked;
            var rightIdx = (_picked >= 0 && _hovered != _picked) ? _hovered : -1;
            v.CardA = MakeCard(leftIdx);
            v.CardB = MakeCard(rightIdx);
            BuildRows(v, leftIdx, rightIdx);
            BuildAptNodes(v, leftIdx, rightIdx);
            return v;
        }

        private void BuildRows(LineupSettingView v, int leftIdx, int rightIdx)
        {
            var a = leftIdx >= 0 ? _roster[leftIdx] : null;
            var b = rightIdx >= 0 ? _roster[rightIdx] : null;
            if (a == null && b == null) return;

            var table = _tab == 0 ? TabBat : TabDef;
            foreach (var (label, get) in table)
            {
                var row = new CompareRowView { Label = label, HasA = a != null, HasB = b != null };
                if (a != null) row.ValueA = get(a);
                if (b != null) row.ValueB = get(b);
                if (a != null && b != null)
                    row.Winner = row.ValueA > row.ValueB ? -1 : row.ValueA < row.ValueB ? 1 : 0;
                v.Rows2.Add(row);
            }
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
                if (a != null) v.AptA.Add(Tiers.FromStrength(a.Aptitude(AptOrder[i])).ToString());
                if (b != null) v.AptB.Add(Tiers.FromStrength(b.Aptitude(AptOrder[i])).ToString());
            }
        }

        private CompareCardView MakeCard(int index)
        {
            if (index < 0 || index >= _roster.Count) return new CompareCardView { Present = false };
            var p = _roster[index];
            return new CompareCardView
            {
                Present = true,
                Name = p.Name,
                GradeLabel = p.Grade + "年",
                OverallGrade = OverallGrade(p),
                IsCaptain = p.IsCaptain,
                Stats = BuildStats(p),
            };
        }

        // 成績2列（通算／今大会）。GameSession.Stats を選手IDで引く。未接続は "—"。
        private static List<StatColumnView> BuildStats(DevelopingPlayer p)
        {
            var stats = GameSession.Current.Stats;
            return new List<StatColumnView>
            {
                Column("通算", stats.Career.Get(p.Id), p.IsPitcher),
                Column("今大会", stats.CurrentTournament.Get(p.Id), p.IsPitcher),
            };
        }

        private static StatColumnView Column(string title, PlayerStats s, bool pitcher)
        {
            var col = new StatColumnView { Title = title };
            if (pitcher)
            {
                var pit = s?.Pitching;
                col.Items.Add(new StatItemView { Label = "防御率", Value = pit != null && pit.Outs > 0 ? pit.Era.ToString("0.00") : "—" });
                col.Items.Add(new StatItemView { Label = "勝敗", Value = pit != null && pit.Games > 0 ? pit.Wins + "勝" + pit.Losses + "敗" : "—" });
                col.Items.Add(new StatItemView { Label = "奪三振", Value = pit != null && pit.Games > 0 ? pit.StrikeOuts.ToString() : "—" });
                col.Items.Add(new StatItemView { Label = "投球回", Value = pit != null && pit.Outs > 0 ? pit.InningsText : "—" });
            }
            else
            {
                var bat = s?.Batting;
                col.Items.Add(new StatItemView { Label = "打率", Value = bat != null && bat.AtBats > 0 ? bat.Average.ToString(".000") : "—" });
                col.Items.Add(new StatItemView { Label = "本塁打", Value = bat != null && bat.Games > 0 ? bat.HomeRuns.ToString() : "—" });
                col.Items.Add(new StatItemView { Label = "打点", Value = bat != null && bat.Games > 0 ? bat.Rbi.ToString() : "—" });
                col.Items.Add(new StatItemView { Label = "出塁率", Value = bat != null && (bat.AtBats + bat.Walks) > 0 ? bat.Obp.ToString(".000") : "—" });
            }
            return col;
        }

        // 打順行の簡易成績（通算のみ・全員打撃成績＝投手も打率/本/点）。データ無しは「-」。
        private static string CareerBatLine(DevelopingPlayer p)
        {
            var b = GameSession.Current.Stats.Career.Get(p.Id)?.Batting;
            if (b == null || b.Games == 0) return "打率 -　本 -　点 -";
            var avg = b.AtBats > 0 ? b.Average.ToString(".000") : "-";
            return "打率 " + avg + "　本 " + b.HomeRuns + "　点 " + b.Rbi;
        }

        private static string OverallGrade(DevelopingPlayer p) => Tiers.FromStrength(p.AverageLevel()).ToString();

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
