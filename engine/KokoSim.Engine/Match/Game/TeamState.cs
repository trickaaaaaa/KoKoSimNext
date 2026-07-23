using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Game;

/// <summary>試合中のチーム可変状態（打順位置・現投手・疲労・得点）。</summary>
public sealed class TeamState
{
    private readonly Team _team;
    private readonly List<Player> _bullpen;   // 未登板の控え投手（先頭＝登板順の既定。指名継投で任意位置を抜く）
    private int _battingIndex;

    // ===== 可変ラインナップ（設計書09 §6, 選手交代）。無交代なら初期＝_team.BattingOrder と一致。 =====
    private readonly Player[] _lineup;          // 打順9スロットの現在の選手
    private readonly List<Player> _bench;       // 野手控え（代打・代走・守備交代の供給元）
    private readonly HashSet<Player> _retired = new(); // 退場者（高校野球=リエントリー禁止で再出場不可）
    private readonly string?[] _replacedAtSlot = new string?[9]; // 各スロットで直近に退いた選手名（ライブ観戦の交代表示・観測用）
    private bool _usesDh;
    private int _dhSlot;
    private int _pitcherSlot;

    public TeamState(Team team)
    {
        _team = team;
        _bullpen = new List<Player>(team.Bullpen);
        _lineup = team.BattingOrder.ToArray();
        _bench = new List<Player>(team.Bench);
        _usesDh = team.UsesDh;
        _dhSlot = team.DhSlot;
        _pitcherSlot = team.PitcherSlot;
        // DH制（設計書09 §6）: 投手は打順外＝StartingPitcher から供給。
        CurrentPitcher = team.UsesDh
            ? team.StartingPitcher ?? throw new ArgumentException("DH制では StartingPitcher が必須です。", nameof(team))
            : team.BattingOrder[team.PitcherSlot];
        PitchesThrown = 0;
        Runs = 0;
    }

    private readonly List<int> _inningRuns = new();

    public string Name => _team.Name;
    public Player CurrentPitcher { get; private set; }

    /// <summary>捕手（盗塁刺しの送球主体）。捕手が打順にいなければ先頭打者で代用。</summary>
    public Player Catcher => _lineup.FirstOrDefault(p => p.Position == FieldPosition.Catcher)
                             ?? _lineup[0];

    /// <summary>その守備位置に就いている選手（不在なら null）。観測用＝結果・乱数順に影響しない。</summary>
    public Player? PlayerAtPosition(FieldPosition pos)
        => pos == FieldPosition.Pitcher
            ? CurrentPitcher
            : _lineup.FirstOrDefault(p => p.Position == pos);
    public int PitchesThrown { get; private set; }
    public int Runs { get; set; }
    public int PitcherChanges { get; private set; }
    /// <summary>本塁クロスプレーで刺された自軍走者の数（バックホーム憤死, 設計書12 §3 F2。統計参考値）。</summary>
    public int HomePlayOuts { get; set; }
    /// <summary>単打の一塁→三塁レースで三塁憤死した自軍走者の数（Issue #89, 設計書12 §3.5。統計参考値）。</summary>
    public int ThirdPlayOuts { get; set; }

    // ===== design-14 第1段（P1）新プレー発生数（統計参考値。試合結果には影響しない） =====
    public int FieldersChoiceCount { get; set; }
    public int DroppedThirdStrikeCount { get; set; }
    public int ErrorExtraAdvanceCount { get; set; }
    public int PickoffCount { get; set; }
    public int IntentionalWalkCount { get; set; }
    public int DoubleStealThirdBreakCount { get; set; }
    /// <summary>暴投・パスボールの合算発生数（design-14 P2-8。当面は投手責/捕手責を分けず統計参考値のみ）。</summary>
    public int WildPitchCount { get; set; }

    // ===== 速報記録（試合結果に影響しない集計） =====
    public int Hits { get; private set; }
    public int Errors { get; private set; }
    /// <summary>攻撃したイニングごとの得点（表/裏の各ハーフで1要素追加）。</summary>
    public IReadOnlyList<int> InningRuns => _inningRuns;
    public void AddHit() => Hits++;
    public void RecordInningRuns(int runs) => _inningRuns.Add(runs);

    /// <summary>
    /// 失策をチーム計＋当該守備選手へ帰属させる（issue #91）。fielder が解決できない場合（想定外）でも
    /// チーム計は必ず加算する＝<see cref="Errors"/> と個人失策の合計が食い違わないようにする。
    /// </summary>
    public void RecordFieldingError(Player? fielder, FieldPosition role)
    {
        Errors++;
        if (fielder is null) return;
        if (!_field.TryGetValue(fielder, out var a)) { a = new FieldAccum(); _field[fielder] = a; }
        a.Position = role;
        a.Errors++;
    }

    // ===== 采配の集計（設計書09。記録のみ＝試合結果には影響しない。無指示なら常に0） =====
    public int StealAttempts { get; private set; }
    public int StealSuccesses { get; private set; }
    public int SacrificeBunts { get; private set; }
    public int SacrificeBuntSuccesses { get; private set; }
    public int Squeezes { get; private set; }

    /// <summary>盗塁企図を走者個人＋チーム計へ記録する（issue #91）。</summary>
    public void RecordSteal(Player runner, bool success)
    {
        StealAttempts++;
        if (success) StealSuccesses++;
        if (!_bat.TryGetValue(runner, out var a)) { a = new BatAccum(); _bat[runner] = a; }
        if (success) a.SB++; else a.CS++;
    }
    public void RecordSacrificeBunt(bool success) { SacrificeBunts++; if (success) SacrificeBuntSuccesses++; }
    public void RecordSqueeze() => Squeezes++;

    // ===== 個人成績（ボックススコア） =====
    private sealed class BatAccum { public int PA, AB, H, Doubles, Triples, HR, RBI, BB, SO, HBP, SB, CS; }
    private sealed class PitAccum { public int BF, H, Runs, SO, BB, Outs, Pitches, HB; }
    private sealed class FieldAccum { public FieldPosition Position; public int Errors; }
    private readonly Dictionary<Player, BatAccum> _bat = new();
    private readonly Dictionary<Player, PitAccum> _pit = new();
    private readonly Dictionary<Player, FieldAccum> _field = new();

    /// <summary>打者の1打席を記録（rbi ≈ そのプレーで入った得点）。</summary>
    public void RecordBatting(Player batter, PlateAppearanceResult r, int rbi)
    {
        if (!_bat.TryGetValue(batter, out var a)) { a = new BatAccum(); _bat[batter] = a; }
        a.PA++;
        if (r.IsAtBat()) a.AB++;
        if (r.IsHit()) a.H++;
        if (r == PlateAppearanceResult.Double) a.Doubles++;
        if (r == PlateAppearanceResult.Triple) a.Triples++;
        if (r == PlateAppearanceResult.HomeRun) a.HR++;
        if (r == PlateAppearanceResult.Walk) a.BB++;
        if (r == PlateAppearanceResult.HitByPitch) a.HBP++;
        if (r == PlateAppearanceResult.Strikeout) a.SO++;
        a.RBI += rbi;
    }

    /// <summary>投手の対戦1打者を記録。</summary>
    public void RecordPitching(Player pitcher, PlateAppearanceResult r, int runs, int outs, int pitches)
    {
        if (!_pit.TryGetValue(pitcher, out var a)) { a = new PitAccum(); _pit[pitcher] = a; }
        a.BF++;
        if (r.IsHit()) a.H++;
        a.Runs += runs;
        if (r == PlateAppearanceResult.Strikeout) a.SO++;
        if (r == PlateAppearanceResult.Walk) a.BB++;
        if (r == PlateAppearanceResult.HitByPitch) a.HB++;
        a.Outs += outs;
        a.Pitches += pitches;
    }

    /// <summary>当該試合でこの打者が既に完了した打席数（尻上がり等の試合内非線形スキル用, 設計書10）。</summary>
    public int PriorPlateAppearances(Player batter) => _bat.TryGetValue(batter, out var a) ? a.PA : 0;

    /// <summary>当該試合でこの投手が既に対戦した打者数（尻上がり/打者一巡用, 設計書10）。継投でリセット。</summary>
    public int PriorBattersFaced(Player pitcher) => _pit.TryGetValue(pitcher, out var a) ? a.BF : 0;

    /// <summary>打順順の打撃成績（スタメン9人＋途中出場で打席に立った選手）。</summary>
    public IReadOnlyList<BattingLine> BuildBattingLines()
    {
        var lines = new List<BattingLine>(9);
        var starters = new HashSet<Player>();
        for (var i = 0; i < 9; i++)
        {
            var p = _team.BattingOrder[i];
            starters.Add(p);
            var a = _bat.TryGetValue(p, out var acc) ? acc : new BatAccum();
            // DHスロットは守備に就かない＝表示だけ FieldPosition.DesignatedHitter（内部の本来守備位置は
            // Player.Position に温存＝守備適性計算・DH解除時の引き継ぎに使う, issue #70）。
            var displayPos = _team.UsesDh && i == _team.DhSlot ? FieldPosition.DesignatedHitter : p.Position;
            lines.Add(Line(i + 1, p, displayPos, a));
        }
        // 救援投手など途中出場で打席に立った選手を追記（合計がチーム安打と一致する）。
        foreach (var kv in _bat)
        {
            if (starters.Contains(kv.Key)) continue;
            lines.Add(Line(_team.PitcherSlot + 1, kv.Key, kv.Key.Position, kv.Value));
        }
        return lines;

        static BattingLine Line(int order, Player p, FieldPosition displayPos, BatAccum a)
            => new(order, displayPos, p.Name, a.PA, a.AB, a.H, a.Doubles, a.Triples, a.HR, a.RBI, a.BB, a.SO, p.SourceId,
                a.HBP, a.SB, a.CS);
    }

    /// <summary>失策があった選手の守備成績（issue #91）。0件の選手は載せない＝合計はチーム計 <see cref="Errors"/> と一致する。</summary>
    public IReadOnlyList<FieldingLine> BuildFieldingLines()
    {
        var lines = new List<FieldingLine>(_field.Count);
        foreach (var kv in _field)
            lines.Add(new FieldingLine(kv.Key.SourceId, kv.Value.Position, kv.Key.Name, kv.Value.Errors));
        return lines;
    }

    /// <summary>登板順の投手成績（先発→登板したブルペン）。</summary>
    public IReadOnlyList<PitchingLine> BuildPitchingLines()
    {
        var lines = new List<PitchingLine>();
        var seen = new HashSet<Player>();
        void Add(Player p)
        {
            if (p == null || seen.Contains(p) || !_pit.TryGetValue(p, out var a)) return;
            seen.Add(p);
            lines.Add(new PitchingLine(p.Name, a.Outs, a.BF, a.H, a.Runs, a.SO, a.BB, a.Pitches, p.SourceId, a.HB, p.UniformNumber));
        }
        Add(_team.UsesDh ? _team.StartingPitcher! : _team.BattingOrder[_team.PitcherSlot]);
        foreach (var p in _team.Bullpen) Add(p);
        return lines;
    }

    /// <summary>
    /// ライブ観戦のスタメン列（現在の打順9人・交代反映済み）＋各選手の今日の成績（打数/安打/打点）。
    /// <see cref="BuildBattingLines"/> はオリジナル9人基準のボックススコアなので、現ラインナップの per-slot 表示には
    /// こちらを使う。観測データ＝試合結果に影響しない。
    /// </summary>
    public IReadOnlyList<LiveBatterSlot> LiveLineup()
    {
        var slots = new List<LiveBatterSlot>(9);
        for (var i = 0; i < 9; i++)
        {
            var p = _lineup[i];
            var a = _bat.TryGetValue(p, out var acc) ? acc : null;
            // 現在DHが解除されていれば(_usesDh=false)このスロットは実守備位置を表示する（issue #70）。
            var displayPos = _usesDh && i == _dhSlot ? FieldPosition.DesignatedHitter : p.Position;
            slots.Add(new LiveBatterSlot(
                i + 1, p.SourceId, p.UniformNumber, p.Name, displayPos, p.Bats,
                a?.AB ?? 0, a?.H ?? 0, a?.RBI ?? 0, _replacedAtSlot[i], p.ConditionValue));
        }
        return slots;
    }

    /// <summary>
    /// ライブ観戦のラインスコア（回別得点＋R/H/E）。<see cref="InningRuns"/> は半回が終わって初めて
    /// 記録されるので、進行中の半回で入った点は「総得点 − 記録済みの合計」として分離して返す
    /// （UI側で差分を取らせない＝数値はエンジン集計から引く、の徹底）。観測データ。
    /// </summary>
    public LiveLineScore LiveLine()
    {
        var recorded = 0;
        foreach (var r in _inningRuns) recorded += r;
        return new LiveLineScore(Name, _inningRuns.ToList(), Runs - recorded, Runs, Hits, Errors);
    }

    /// <summary>ライブ観戦の現投手の今日の成績（球数/投球回/失点/奪三振）。観測データ。</summary>
    public LivePitcherToday LivePitcherLine()
    {
        var p = CurrentPitcher;
        var a = _pit.TryGetValue(p, out var acc) ? acc : null;
        return new LivePitcherToday(
            p.SourceId, p.UniformNumber, p.Name, p.Throws,
            a?.Pitches ?? 0, a?.Outs ?? 0, a?.Runs ?? 0, a?.SO ?? 0, p.ConditionValue);
    }

    /// <summary>ライブ観戦の野手控え（未出場の交代候補）。打順は持たないので Order=0。観測データ。</summary>
    public IReadOnlyList<LiveBatterSlot> LiveBench()
    {
        var list = new List<LiveBatterSlot>(_bench.Count);
        foreach (var p in _bench)
        {
            var a = _bat.TryGetValue(p, out var acc) ? acc : null;
            list.Add(new LiveBatterSlot(
                0, p.SourceId, p.UniformNumber, p.Name, p.Position, p.Bats,
                a?.AB ?? 0, a?.H ?? 0, a?.RBI ?? 0, null, p.ConditionValue));
        }
        return list;
    }

    /// <summary>ライブ観戦の控え投手（未登板のブルペン）。観測データ。</summary>
    public IReadOnlyList<LivePitcherToday> LiveBullpen()
    {
        var list = new List<LivePitcherToday>(_bullpen.Count);
        foreach (var p in _bullpen)   // 登板順で列挙
        {
            var a = _pit.TryGetValue(p, out var acc) ? acc : null;
            list.Add(new LivePitcherToday(
                p.SourceId, p.UniformNumber, p.Name, p.Throws,
                a?.Pitches ?? 0, a?.Outs ?? 0, a?.Runs ?? 0, a?.SO ?? 0, p.ConditionValue));
        }
        return list;
    }

    /// <summary>次の打者を返し、打順を1つ進める。投手スロットは現投手が打席に立つ（DH制では投手は打たない）。</summary>
    public Player NextBatter()
    {
        var batter = PeekBatter();
        _battingIndex = (_battingIndex + 1) % 9;
        return batter;
    }

    /// <summary>次の打者（打順は進めない）。采配判断・サイン実行が打順消費前に打者を知るために使う。</summary>
    public Player PeekBatter() => BatterAt(_battingIndex);

    /// <summary>現在の打者の打順（1-9）。ライブ観戦のスタメン列ハイライト用（打順消費前に読むこと）。</summary>
    public int CurrentBatterOrder => _battingIndex + 1;

    /// <summary>打順をn人分さかのぼった打者（タイブレークの走者配置用, 設計書09 §7）。</summary>
    public Player PreviousBatter(int back) => BatterAt(((_battingIndex - back) % 9 + 9) % 9);

    private Player BatterAt(int slot) => _lineup[slot];

    // ===== 采配（設計書09） =====
    private ITacticsBrain? _tacticsOverride;
    public ITacticsBrain? Tactics => _tacticsOverride ?? _team.Tactics;

    /// <summary>
    /// 試合中に采配ブレインを差し替える（スキップ＝以降を委任する時＝設計書11の委任采配）。null で解除し Team 既定へ戻す。
    /// 既定（未設定）では _team.Tactics がそのまま流れるため、従来挙動・決定論ゲートに影響しない。
    /// </summary>
    public void OverrideTactics(ITacticsBrain? brain) => _tacticsOverride = brain;

    // ===== 1球采配の手動指示（設計書15 §2.3, Phase C-3）=====
    // プレイヤーの手動1球指示は ITacticsBrain を経由せず、ここへ直接セットする（PinchHitNext と同型の
    // 「保留→次に読まれた時に1回だけ消費してクリア」パターン）。無指示（未セット）ならバッチと完全一致。
    private PitchBattingOverride? _pendingPitchBattingOverride;
    private PitchPolicy? _pendingPitchPolicy;
    private Pitching.PitcherGear? _pendingPitchGear;

    /// <summary>次の1球への打撃指示（強攻/待て）を予約する。null=指示解除。</summary>
    public void SetPendingPitchBattingOverride(PitchBattingOverride? v) => _pendingPitchBattingOverride = v;

    /// <summary>次の1球への配球方針/ギア上書きを予約する。両方null=指示解除。</summary>
    public void SetPendingPitchDefenseOverride(PitchPolicy? policy, Pitching.PitcherGear? gear)
    {
        _pendingPitchPolicy = policy;
        _pendingPitchGear = gear;
    }

    /// <summary>予約された打撃指示を読み出して消費する（1球限り＝次球は自動でnullに戻る）。</summary>
    public PitchBattingOverride? ConsumePendingPitchBattingOverride()
    {
        var v = _pendingPitchBattingOverride;
        _pendingPitchBattingOverride = null;
        return v;
    }

    /// <summary>予約された配球指示を読み出して消費する（1球限り）。</summary>
    public (PitchPolicy? Policy, Pitching.PitcherGear? Gear) ConsumePendingPitchDefenseOverride()
    {
        var v = (_pendingPitchPolicy, _pendingPitchGear);
        _pendingPitchPolicy = null;
        _pendingPitchGear = null;
        return v;
    }

    /// <summary>伝令の残回数（攻守各3回, §3）。延長は1イニングごとに1回追加。</summary>
    public int OffenseTimeoutsLeft { get; private set; } = 3;
    public int DefenseTimeoutsLeft { get; private set; } = 3;
    public void GrantExtraInningTimeouts()
    {
        OffenseTimeoutsLeft++;
        DefenseTimeoutsLeft++;
    }

    public bool TryUseOffenseTimeout()
    {
        if (OffenseTimeoutsLeft <= 0) return false;
        OffenseTimeoutsLeft--;
        return true;
    }

    public bool TryUseDefenseTimeout()
    {
        if (DefenseTimeoutsLeft <= 0) return false;
        DefenseTimeoutsLeft--;
        return true;
    }

    /// <summary>伝令の効果窓（残り打席数）。効いている間はプレッシャー負補正を緩和。</summary>
    public int OffenseCalmPa { get; private set; }
    public int DefenseCalmPa { get; private set; }
    public void StartOffenseCalm(int pa) => OffenseCalmPa = Math.Max(OffenseCalmPa, pa);
    public void StartDefenseCalm(int pa) => DefenseCalmPa = Math.Max(DefenseCalmPa, pa);
    public void TickOffenseCalm() { if (OffenseCalmPa > 0) OffenseCalmPa--; }
    public void TickDefenseCalm() { if (DefenseCalmPa > 0) DefenseCalmPa--; }

    /// <summary>投手の「動揺」（連続出塁で発生, §3）。伝令・イニング跨ぎ・継投、またはアウト累積の自然回復で解除。</summary>
    public bool PitcherRattled { get; private set; }
    private int _consecutiveBaserunners;
    private int _outsSinceRattled;

    /// <summary>
    /// rattledThreshold: 精神力込みの発生閾値（<see cref="Tactics.TacticsCoefficients.RattledThresholdFor"/>）。
    /// outsThisPa/recoveryOuts: 動揺中に無失点でアウトを積んだ数がrecoveryOutsに達すると自然回復（issue #73）。
    /// </summary>
    public void NotePitchingResult(PlateAppearanceResult r, int rattledThreshold, int outsThisPa, int recoveryOuts)
    {
        if (r.IsHit() || r is PlateAppearanceResult.Walk or PlateAppearanceResult.HitByPitch
            or PlateAppearanceResult.ReachedOnError)
        {
            _consecutiveBaserunners++;
            if (_consecutiveBaserunners >= rattledThreshold) PitcherRattled = true;
            _outsSinceRattled = 0;
        }
        else
        {
            _consecutiveBaserunners = 0;
            if (PitcherRattled)
            {
                _outsSinceRattled += outsThisPa;
                if (_outsSinceRattled >= recoveryOuts) ClearRattled();
            }
        }
    }

    public void ClearRattled()
    {
        PitcherRattled = false;
        _consecutiveBaserunners = 0;
        _outsSinceRattled = 0;
    }

    /// <summary>
    /// プレッシャー負補正の緩和量（0〜1）: 主将の在場×統率力（§8）＋伝令の効果窓（§3）。
    /// offense=true なら攻撃側（打者へ）、false なら守備側（投手へ）。
    /// </summary>
    public double MitigationFor(bool offense, TacticsCoefficients c, SkillCoefficients? skills = null)
    {
        var m = CaptainMitigation(c, skills);
        if (offense && OffenseCalmPa > 0) m += c.TimeoutMitigation;
        if (!offense && DefenseCalmPa > 0) m += c.TimeoutMitigation;
        return Math.Min(1.0, m);
    }

    /// <summary>
    /// 主将の緩和量。統率力 = 統率傾向×精神力/100（0〜100）。ベンチ時は大きく減衰、不在は0。
    /// スキル「精神的支柱」（設計書10）を持つ主将は緩和量が拡大する（設計書09 §8 の統率力寄与）。
    /// </summary>
    public double CaptainMitigation(TacticsCoefficients c, SkillCoefficients? skills = null)
    {
        var cap = _team.Captain;
        if (cap is null) return 0.0;
        var power = cap.Leadership * cap.Mental / 100.0;
        var presence = IsOnField(cap) ? 1.0 : c.CaptainBenchFactor;
        var pillar = cap.Skills.Has(Skill.SpiritualPillar)
            ? (skills ?? new SkillCoefficients()).SpiritualPillarCaptainFactor
            : 1.0;
        return power * c.CaptainMitigationPerPower * presence * pillar;
    }

    private bool IsOnField(Player p)
    {
        if (p == CurrentPitcher) return true;
        for (var i = 0; i < 9; i++)
        {
            if (_usesDh && i == _dhSlot) continue;   // DHは守備に就かない
            if (!_usesDh && i == _pitcherSlot) continue; // 投手枠は現投手で判定済み
            if (_lineup[i] == p) return true;
        }
        return false;
    }

    /// <summary>
    /// チーム別の投手疲労係数（issue #55, 監督傾向, 決定4: B-1）。null=既定＝呼び出し側が ctx.Fatigue を使う。
    /// GameEngine は継投判定・実効能力の算出で <c>defense.Fatigue ?? ctx.Fatigue</c> を渡す。
    /// </summary>
    public FatigueCoefficients? Fatigue => _team.Fatigue;

    /// <summary>ギア重み込みの実効消費球数（設計書02 §1.1e-f）。疲労判定はこちらを使う。</summary>
    public double FatiguePitches { get; private set; }

    /// <summary>球数を加算。staminaWeight はギア「飛ばす/流す」の消耗倍率（既定1.0＝Normal）。</summary>
    public void AddPitches(int pitches, double staminaWeight = 1.0)
    {
        PitchesThrown += pitches;
        FatiguePitches += pitches * staminaWeight;
    }

    // ===== 球数制限（設計書05 §1.3, 現代ルール。既定は上限なしで無効） =====
    private IReadOnlyDictionary<Player, int>? _priorWeekPitches;
    private int _weeklyPitchLimit = int.MaxValue;

    /// <summary>週の球数上限と持ち越し球数を設定（GameEngine.Play から注入。null/上限なしで無効）。</summary>
    public void SetWeeklyPitchBudget(int limit, IReadOnlyDictionary<Player, int>? priorWeekPitches)
    {
        _weeklyPitchLimit = limit;
        _priorWeekPitches = priorWeekPitches;
    }

    /// <summary>その投手の週間累計球数（持ち越し＋当試合の記録球数）。</summary>
    public int WeeklyPitchesFor(Player pitcher)
    {
        var prior = _priorWeekPitches is not null && _priorWeekPitches.TryGetValue(pitcher, out var v) ? v : 0;
        var inGame = _pit.TryGetValue(pitcher, out var a) ? a.Pitches : 0;
        return prior + inGame;
    }

    /// <summary>現投手が球数上限に達したか（達したら次打者は投げられない＝継投必須）。</summary>
    public bool CurrentPitcherAtWeeklyLimit() => WeeklyPitchesFor(CurrentPitcher) >= _weeklyPitchLimit;

    /// <summary>控え投手が残っていれば継投する（ブルペン先頭＝登板順の指名ラッパ）。動揺はリセット。</summary>
    public bool TryChangePitcher()
        => _bullpen.Count > 0 && ChangePitcherTo(_bullpen[0]);

    /// <summary>
    /// ブルペン中の任意の投手を指名して継投する（設計書09 §6・プレイヤー采配の継投）。
    /// ブルペンに sub が居なければ false（＝既に登板済み／退場済みは指名できない＝リエントリー禁止）。
    /// 動揺はリセット（新しい投手は落ち着いて入る）。
    /// </summary>
    public bool ChangePitcherTo(Player sub)
    {
        if (!_bullpen.Remove(sub) && !_bench.Remove(sub)) return false;
        // 退いた投手は再登板できない（リエントリー禁止）。非DHでは打順からも外れる。
        _retired.Add(CurrentPitcher);
        CurrentPitcher = sub;
        // 非DH制では投手は打順内。新投手を投手スロットへ反映（打者としても入れ替わる）。
        if (!_usesDh) _lineup[_pitcherSlot] = CurrentPitcher;
        PitchesThrown = 0;
        FatiguePitches = 0;
        PitcherChanges++;
        ClearRattled();
        return true;
    }

    /// <summary>まだ登板していない控え投手（登板順＝Team.Bullpen の並び）。交代UIの指名候補。</summary>
    public IReadOnlyList<Player> AvailableBullpen => _bullpen;

    /// <summary>
    /// 投手交代で指名できる全候補（ブルペン＋野手控え。issue #137: 野手も投手として登板できる）。
    /// 添字は <see cref="ChangePitcherTo"/> 呼び出しの Player 引数解決に使う（SubstitutionCommands/GameReplay 共通）。
    /// </summary>
    public IReadOnlyList<Player> AvailablePitcherCandidates => _bullpen.Concat(_bench).ToList();

    // ===== 選手交代（設計書09 §6）。高校野球ルール＝リエントリー禁止（退いた選手は再出場できない）。 =====
    /// <summary>現在の打順9人（交代反映済み）。UI・テスト・采配判断用。</summary>
    public IReadOnlyList<Player> CurrentLineup => _lineup;
    /// <summary>野手控え（まだ使っていない交代要員）。</summary>
    public IReadOnlyList<Player> Bench => _bench;
    /// <summary>この試合で行われた選手交代の回数（投手交代は含まない）。</summary>
    public int Substitutions { get; private set; }
    /// <summary>DH制が生きているか（DH解除後は false）。</summary>
    public bool UsesDh => _usesDh;

    /// <summary>控えにこの選手が残っているか（交代可否の判定）。</summary>
    public bool IsAvailable(Player sub) => _bench.Contains(sub);

    /// <summary>この試合で退場済みか（リエントリー禁止＝再出場できない）。交代UIのグレーアウト判定に使う。</summary>
    public bool IsRetired(Player p) => _retired.Contains(p);

    /// <summary>試合開始時の野手控え（交代UIが「使い切った控え」をグレーアウト表示するための原本）。</summary>
    public IReadOnlyList<Player> RosterBench => _team.Bench;

    /// <summary>試合開始時のブルペン（同上・登板済みをグレーアウト表示するための原本）。</summary>
    public IReadOnlyList<Player> RosterBullpen => _team.Bullpen;

    /// <summary>投手が入っている打順スロット（0始まり。DH制では「投手が入る予定の」スロット＝DH解除で確定）。</summary>
    public int PitcherSlot => _pitcherSlot;

    /// <summary>DHが入っている打順スロット（0始まり。DH制のときだけ意味を持つ）。</summary>
    public int DhSlot => _dhSlot;

    /// <summary>次打者へ代打を送る（退いた打者の打順・守備位置を継承）。控えに sub が無ければ false。</summary>
    public bool PinchHitNext(Player sub) => SubstituteAtSlot(_battingIndex, sub);

    /// <summary>塁上の走者 runner を控え sub に代える（代走）。打順・守備位置を継承。塁の差し替えは呼び出し側（BaseState）。</summary>
    public bool PinchRunFor(Player runner, Player sub)
    {
        var slot = IndexOf(runner);
        return slot >= 0 && SubstituteAtSlot(slot, sub);
    }

    /// <summary>守備交代: 出場中の outgoing を控え sub に代える（守備位置を継承）。</summary>
    public bool DefensiveSub(Player outgoing, Player sub)
    {
        var slot = IndexOf(outgoing);
        return slot >= 0 && SubstituteAtSlot(slot, sub);
    }

    /// <summary>
    /// DH解除（設計書09 §6）: 投手が打順に入り、以降 DH は消滅。dhFieldsAt を指定するとDHの選手がその守備位置に就く
    /// （元の守備者は退場）。null なら DH の選手はそのまま退場（投手が打席へ立つだけ）。
    /// </summary>
    public bool ReleaseDh(FieldPosition? dhFieldsAt = null)
    {
        if (!_usesDh) return false;
        var dhPlayer = _lineup[_dhSlot];
        if (dhFieldsAt is { } pos)
        {
            var target = IndexOf(_lineup.FirstOrDefault(p => p != dhPlayer && p.Position == pos)!);
            if (target < 0) return false;
            _retired.Add(_lineup[target]);
            _replacedAtSlot[target] = _lineup[target].Name;   // 観測: 守備位置に就いた選手が退いた
            _lineup[target] = dhPlayer with { Position = pos };
        }
        else
        {
            _retired.Add(dhPlayer);
        }
        // 投手が DH の打順スロットへ入り、そこが投手スロットになる。
        _replacedAtSlot[_dhSlot] = dhPlayer.Name;   // 観測: DHが退いて投手が打順へ
        _lineup[_dhSlot] = CurrentPitcher with { Position = FieldPosition.Pitcher };
        CurrentPitcher = _lineup[_dhSlot];
        _pitcherSlot = _dhSlot;
        _usesDh = false;
        Substitutions++;
        return true;
    }

    /// <summary>
    /// スロット slot の選手を控え sub に置換する共通処理（退いた選手の守備位置を継承・リエントリー禁止）。
    /// 非DHの投手スロットへ代打した場合、投手本人は退場扱いになるため、守備再開前に必ず継投すること
    /// （C-2 の GameEngine が強制。TryChangePitcher が投手スロットの打者を新投手で上書きする）。
    /// </summary>
    private bool SubstituteAtSlot(int slot, Player sub)
    {
        if (!_bench.Remove(sub)) return false;
        var outgoing = _lineup[slot];
        _lineup[slot] = sub with { Position = outgoing.Position };
        _retired.Add(outgoing);
        _replacedAtSlot[slot] = outgoing.Name;   // ライブ観戦の交代表示用（観測データ）
        Substitutions++;
        return true;
    }

    private int IndexOf(Player p)
    {
        for (var i = 0; i < 9; i++)
            if (_lineup[i] == p) return i;
        return -1;
    }

    /// <summary>守備陣（打順の非投手8人＋現投手）を守備位置に配置する。DH制ではDHが守備に就かない（§6）。</summary>
    public IReadOnlyList<Fielder> DefensiveAlignment(FieldGeometry field)
    {
        var byPos = new Dictionary<FieldPosition, FielderAttributes>();
        var skipSlot = _usesDh ? _dhSlot : _pitcherSlot;
        for (var i = 0; i < 9; i++)
        {
            if (i == skipSlot) continue;
            var p = _lineup[i];
            byPos[p.Position] = p.ToFielder();
        }
        byPos[FieldPosition.Pitcher] = CurrentPitcher.ToFielder();
        return field.AlignmentFrom(byPos);
    }
}
