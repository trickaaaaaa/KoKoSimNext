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
    private int _battingIndex;

    // ===== 可変ラインナップ（設計書09 §6, 選手交代）。無交代なら初期＝_team.BattingOrder と一致。 =====
    private readonly Player[] _lineup;          // 打順9スロットの現在の選手
    // 控え（打順外の全員）。継投・代打・代走・守備交代の共通の供給元＝投手控えと野手控えに分けない
    // （issue #381。二分は「投手か否か」という属性を前提にした構造で #366 と衝突する）。
    // 並びは背番号順＝プレイヤーが決めた優先度（issue #270）。使った選手はここから抜く。
    private readonly List<Player> _reserves;
    private readonly HashSet<Player> _retired = new(); // 退場者（高校野球=リエントリー禁止で再出場不可）
    private readonly string?[] _replacedAtSlot = new string?[9]; // 各スロットで直近に退いた選手名（ライブ観戦の交代表示・観測用）
    private bool _usesDh;
    private int _dhSlot;
    private int _pitcherSlot;
    private readonly Player _starter;           // 先発投手。ボックススコアの基準。

    public TeamState(Team team)
    {
        _team = team;
        _lineup = team.BattingOrder.ToArray();
        _reserves = new List<Player>(team.Reserves);
        _usesDh = team.UsesDh;
        _dhSlot = team.DhSlot;
        _pitcherSlot = team.PitcherSlot;
        // DH制（設計書09 §6）: 投手は打順外＝StartingPitcher から供給。
        var starter = team.UsesDh
            ? team.StartingPitcher ?? throw new ArgumentException("DH制では StartingPitcher が必須です。", nameof(team))
            : team.BattingOrder[team.PitcherSlot];
        // 「投」枠に誰を置いても、その選手自身の投手能力で投げる（#93 / #381）。全選手が
        // PitcherAttributes を持つので、先発が普段マウンドに立たない選手でも実体化は要らない。
        CurrentPitcher = _starter = starter;
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
    /// <summary>直近の継投の詳細（issue #209 表現。イニング非依存。ドライバがイニングを付けてログ化する）。観測専用。</summary>
    public PitchingChangeOutcome? LastPitchingChange { get; private set; }
    /// <summary>本塁クロスプレーで刺された自軍走者の数（バックホーム憤死, 設計書12 §3 F2。統計参考値）。</summary>
    public int HomePlayOuts { get; set; }
    /// <summary>単打の一塁→三塁レースで三塁憤死した自軍走者の数（Issue #89, 設計書12 §3.5。統計参考値）。</summary>
    public int ThirdPlayOuts { get; set; }
    /// <summary>この攻撃で併殺打（二塁封殺＋打者一塁アウト）を打った数（GIDP, Issue #369。統計参考値）。</summary>
    public int DoublePlayCount { get; set; }

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
    private sealed class BatAccum { public int PA, AB, H, Doubles, Triples, HR, RBI, BB, SO, HBP, SB, CS, R, SF; }
    private sealed class PitAccum
    {
        public int BF, H, Runs, EarnedRuns, SO, BB, Outs, Pitches, HB, HRA;
        public readonly Dictionary<PitchType, PitchTypeAccum> ByPitch = new();
    }
    private sealed class PitchTypeAccum { public int AtBats, Hits, HomeRuns; }
    private sealed class FieldAccum { public FieldPosition Position; public int Errors; }
    private readonly Dictionary<Player, BatAccum> _bat = new();
    private readonly Dictionary<Player, PitAccum> _pit = new();
    private readonly Dictionary<Player, FieldAccum> _field = new();

    /// <summary>打者の1打席を記録（rbi ≈ そのプレーで入った得点）。isSacFly＝犠飛（issue #68）は打数から除外する。</summary>
    public void RecordBatting(Player batter, PlateAppearanceResult r, int rbi, bool isSacFly = false)
    {
        if (!_bat.TryGetValue(batter, out var a)) { a = new BatAccum(); _bat[batter] = a; }
        a.PA++;
        if (isSacFly) a.SF++;
        if (r.IsAtBat() && !isSacFly) a.AB++;
        if (r.IsHit()) a.H++;
        if (r == PlateAppearanceResult.Double) a.Doubles++;
        if (r == PlateAppearanceResult.Triple) a.Triples++;
        if (r == PlateAppearanceResult.HomeRun) a.HR++;
        if (r == PlateAppearanceResult.Walk) a.BB++;
        if (r == PlateAppearanceResult.HitByPitch) a.HBP++;
        if (r == PlateAppearanceResult.Strikeout) a.SO++;
        a.RBI += rbi;
    }

    /// <summary>走者/打者の生還を記録（得点＝個人の生還数, issue #77）。打席と独立に本塁到達で1加算。</summary>
    public void RecordRun(Player runner)
    {
        if (!_bat.TryGetValue(runner, out var a)) { a = new BatAccum(); _bat[runner] = a; }
        a.R++;
    }

    /// <summary>投手の対戦1打者を記録。decisivePitch＝打席確定球の球種（インプレー/被安打を生んだ球のみ, issue #180）。
    /// unearnedRuns＝runs の内、失策/失策連鎖に起因する自責点対象外の得点数（issue #69）。</summary>
    public void RecordPitching(Player pitcher, PlateAppearanceResult r, int runs, int outs, int pitches,
        PitchType? decisivePitch = null, int unearnedRuns = 0)
    {
        if (!_pit.TryGetValue(pitcher, out var a)) { a = new PitAccum(); _pit[pitcher] = a; }
        a.BF++;
        if (r.IsHit()) a.H++;
        a.Runs += runs;
        a.EarnedRuns += runs - unearnedRuns;
        if (r == PlateAppearanceResult.HomeRun) a.HRA++;   // 被本塁打（issue #77）
        if (r == PlateAppearanceResult.Strikeout) a.SO++;
        if (r == PlateAppearanceResult.Walk) a.BB++;
        if (r == PlateAppearanceResult.HitByPitch) a.HB++;
        a.Outs += outs;
        a.Pitches += pitches;
        if (decisivePitch.HasValue && r.IsBattedBall())
        {
            if (!a.ByPitch.TryGetValue(decisivePitch.Value, out var pa)) { pa = new PitchTypeAccum(); a.ByPitch[decisivePitch.Value] = pa; }
            pa.AtBats++;
            if (r.IsHit()) pa.Hits++;
            if (r == PlateAppearanceResult.HomeRun) pa.HomeRuns++;
        }
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
            // 投手枠は実体化後のインスタンス（成績の帰属キー）で引く。実体化が恒等なら _team.BattingOrder[i] と同一。
            var p = !_team.UsesDh && i == _team.PitcherSlot ? _starter : _team.BattingOrder[i];
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
                a.HBP, a.SB, a.CS, a.R, a.SF);
    }

    /// <summary>失策があった選手の守備成績（issue #91）。0件の選手は載せない＝合計はチーム計 <see cref="Errors"/> と一致する。</summary>
    public IReadOnlyList<FieldingLine> BuildFieldingLines()
    {
        var lines = new List<FieldingLine>(_field.Count);
        foreach (var kv in _field)
            lines.Add(new FieldingLine(kv.Key.SourceId, kv.Value.Position, kv.Key.Name, kv.Value.Errors));
        return lines;
    }

    /// <summary>登板順の投手成績（先発→登板した控え）。</summary>
    public IReadOnlyList<PitchingLine> BuildPitchingLines()
    {
        var lines = new List<PitchingLine>();
        var seen = new HashSet<Player>();
        void Add(Player p)
        {
            if (p == null || seen.Contains(p) || !_pit.TryGetValue(p, out var a)) return;
            seen.Add(p);
            IReadOnlyDictionary<PitchType, PitchTypeBattingLine>? byPitch = a.ByPitch.Count > 0
                ? a.ByPitch.ToDictionary(kv => kv.Key, kv => new PitchTypeBattingLine(kv.Value.AtBats, kv.Value.Hits, kv.Value.HomeRuns))
                : null;
            lines.Add(new PitchingLine(p.Name, a.Outs, a.BF, a.H, a.Runs, a.SO, a.BB, a.Pitches, p.SourceId, a.HB, p.UniformNumber, a.HRA, byPitch, a.EarnedRuns));
        }
        Add(_starter);
        foreach (var p in _team.Reserves) Add(p);
        // 在フィールドから昇格した野手（issue #209 Phase C）は Team.Reserves に居ないので上の2経路では
        // 拾えない。実際に投げた投手（_pit に成績がある）で未計上の者を登板順（挿入順）に追加する。
        // 先発・控えは既に seen なのでスキップされ、通常試合の投手行は不変（＝決定論digestも不変）。
        foreach (var p in _pit.Keys) Add(p);
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
                a?.AB ?? 0, a?.H ?? 0, a?.RBI ?? 0, _replacedAtSlot[i], p.ConditionValue, p.Grade));
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

    /// <summary>
    /// ライブ観戦の現投手の今日の成績（球数/投球回/失点/奪三振）。観測データ。
    /// <paramref name="pendingPitches"/>＝進行中の打席で既に投げた球数（未確定・issue #397）。
    /// スタミナ残量にも同じ値をギア倍率抜き（1.0）の近似で加える（打席確定時に正値へ収束）。
    /// </summary>
    public LivePitcherToday LivePitcherLine(int pendingPitches = 0)
    {
        var p = CurrentPitcher;
        var a = _pit.TryGetValue(p, out var acc) ? acc : null;
        return new LivePitcherToday(
            p.SourceId, p.UniformNumber, p.Name, p.Throws,
            a?.Pitches ?? 0, a?.Outs ?? 0, a?.Runs ?? 0, a?.SO ?? 0, p.ConditionValue,
            p.Grade, PitcherChanges + 1, StaminaRemainingOf(p, FatiguePitches + pendingPitches),
            pendingPitches, a?.BB ?? 0, a?.HB ?? 0);
    }

    /// <summary>ライブ観戦の控え（未出場の交代候補・背番号順）。打順は持たないので Order=0。観測データ。</summary>
    public IReadOnlyList<LiveBatterSlot> LiveReserves()
    {
        var list = new List<LiveBatterSlot>(_reserves.Count);
        foreach (var p in _reserves)
        {
            var a = _bat.TryGetValue(p, out var acc) ? acc : null;
            list.Add(new LiveBatterSlot(
                0, p.SourceId, p.UniformNumber, p.Name, p.Position, p.Bats,
                a?.AB ?? 0, a?.H ?? 0, a?.RBI ?? 0, null, p.ConditionValue, p.Grade));
        }
        return list;
    }

    /// <summary>
    /// ライブ観戦の「投げられる控え」（未登板・名簿順）。観測データ。
    /// 控えは1本なので、ここでは投手として置かれている控え（Position==Pitcher）だけを拾う＝
    /// 継投の見通しを出すための絞り込みであって、登板できる範囲を狭めるものではない（#93）。
    /// </summary>
    public IReadOnlyList<LivePitcherToday> LiveReservePitchers()
    {
        var list = new List<LivePitcherToday>();
        foreach (var p in _reserves)
        {
            if (p.Position != FieldPosition.Pitcher) continue;
            var a = _pit.TryGetValue(p, out var acc) ? acc : null;
            list.Add(new LivePitcherToday(
                p.SourceId, p.UniformNumber, p.Name, p.Throws,
                a?.Pitches ?? 0, a?.Outs ?? 0, a?.Runs ?? 0, a?.SO ?? 0, p.ConditionValue,
                p.Grade, PitcherChanges + 2, 1.0, Walks: a?.BB ?? 0, HitByPitch: a?.HB ?? 0));
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

    // ===== プレイヤーの采配指示（2026-07-31・采配カードの拡充）=====
    // 寿命が2種類ある。①「解除するまで続く方針」＝打席頭で毎回読むだけ（消費しない） ②「その場の1手」＝
    // 読んだら消える。エンジンの既存モデル（守備指示＝常時設定 / 1球指示＝使い捨て）にそのまま重ねている。
    // どれも未設定なら読み取りだけでRNGを1発も引かない＝バッチ実行と完全一致（統計帯・digest不変）。

    /// <summary>打撃姿勢（解除するまで継続）。既定 Normal＝従来と完全一致。</summary>
    public BattingApproach BattingApproach { get; private set; } = BattingApproach.Normal;

    /// <summary>打撃姿勢を設定する（<see cref="BattingApproach.Normal"/> で解除）。</summary>
    public void SetBattingApproach(BattingApproach v) => BattingApproach = v;

    private PitchPolicy? _standingPolicy;
    private Pitching.PitcherGear? _standingGear;
    private DefenseDepth? _standingInfield;
    private DefenseDepth? _standingOutfield;

    /// <summary>守備の常時方針（配球・ギア・内野/外野の深さ）。null の項目は brain の判断のまま。</summary>
    public void SetStandingDefense(PitchPolicy? policy, Pitching.PitcherGear? gear,
        DefenseDepth? infield, DefenseDepth? outfield)
    {
        _standingPolicy = policy;
        _standingGear = gear;
        _standingInfield = infield;
        _standingOutfield = outfield;
    }

    /// <summary>
    /// brain が返した守備指示へ常時方針を上書きする（打席頭。指定のある項目だけ差し替える）。
    /// 敬遠・バントシフトは brain の判断をそのまま通す＝プレイヤーの方針カードとは別の軸。
    /// </summary>
    public DefensiveTactics ApplyStandingDefense(DefensiveTactics d)
    {
        if (_standingPolicy is null && _standingGear is null
            && _standingInfield is null && _standingOutfield is null) return d;
        return d with
        {
            Policy = _standingPolicy ?? d.Policy,
            Gear = _standingGear ?? d.Gear,
            Infield = _standingInfield ?? d.Infield,
            Outfield = _standingOutfield ?? d.Outfield,
        };
    }

    private OffensiveSign? _pendingOffensiveSign;

    /// <summary>次の打席の攻撃サインを予約する（エンドラン等）。null=解除。1打席限り。</summary>
    public void SetPendingOffensiveSign(OffensiveSign? v) => _pendingOffensiveSign = v;

    /// <summary>予約された攻撃サインを読み出して消費する（1打席限り）。</summary>
    public OffensiveSign? ConsumePendingOffensiveSign()
    {
        var v = _pendingOffensiveSign;
        _pendingOffensiveSign = null;
        return v;
    }

    private StartType? _pendingSteal;
    private StealTarget _pendingStealTarget = StealTarget.Second;

    /// <summary>次の1球で盗塁を試みる（<paramref name="start"/> null=解除）。</summary>
    public void SetPendingSteal(StartType? start, StealTarget target)
    {
        _pendingSteal = start;
        _pendingStealTarget = target;
    }

    /// <summary>予約された盗塁を読み出して消費する（1球限り）。</summary>
    public (StartType Start, StealTarget Target)? ConsumePendingSteal()
    {
        if (_pendingSteal is not { } start) return null;
        var target = _pendingStealTarget;
        _pendingSteal = null;
        _pendingStealTarget = StealTarget.Second;
        return (start, target);
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

    /// <summary>
    /// スタミナ残量（1=満タン、0=目安投球数を使い切った）。継投の目安として見せるための観測値で、
    /// 実際の継投判定は <see cref="PitchingFatigue.ShouldRelieve"/>（目安投球数＋マージン。ハードキャップ撤廃, issue #356）が行う。
    /// </summary>
    private static double StaminaRemainingOf(Player pitcher, double fatiguePitches)
    {
        var stamina = pitcher.Pitching.StaminaPitches;
        if (stamina <= 0) return 0.0;
        var remaining = 1.0 - fatiguePitches / stamina;
        return remaining < 0.0 ? 0.0 : remaining > 1.0 ? 1.0 : remaining;
    }

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

    /// <summary>
    /// 控えが残っていれば継投する（既定の指名ラッパ）。動揺はリセット。
    /// 採るのは<b>名簿順（背番号順）で最初の「投手として置かれた控え」</b>＝控えを二分していた頃の先頭と同じ選手。
    /// 投手枠の控えが尽きたら名簿先頭の控えが緊急登板する（#93: 内部の投/野区分でゲートしない）。
    /// </summary>
    public bool TryChangePitcher()
    {
        if (_reserves.Count == 0) return false;
        foreach (var p in _reserves)
            if (p.Position == FieldPosition.Pitcher) return ChangePitcherTo(p);
        return ChangePitcherTo(_reserves[0]);
    }

    /// <summary>
    /// 控えの任意の選手を指名して継投する（設計書09 §6・プレイヤー采配の継投）。
    /// 控えに sub が居なければ false（＝既に登板済み／退場済みは指名できない＝リエントリー禁止）。
    /// 動揺はリセット（新しい投手は落ち着いて入る）。
    /// </summary>
    public bool ChangePitcherTo(Player sub)
    {
        if (!_reserves.Remove(sub)) return false;
        // 退いた投手は再登板できない（リエントリー禁止）。非DHでは打順からも外れる。
        var outgoing = CurrentPitcher;
        // 実況上の区別は「控えから出たか／守備に就いていた者が回ったか」。ここは常に控えからの登板。
        _retired.Add(outgoing);
        // 誰が上がっても本人の投手能力で投げる（#381。旧実装はここで潜在能力を詰め替えていた）。
        CurrentPitcher = sub;
        // 非DH制では投手は打順内。新投手を投手スロットへ反映（打者としても入れ替わる）。
        if (!_usesDh) _lineup[_pitcherSlot] = CurrentPitcher;
        // 球数・疲労は選手単位（issue #235）。退場した投手は戻れないので従来と同じ見え方になる。
        SaveStintOf(outgoing);
        LoadStintOf(CurrentPitcher);
        PitcherChanges++;
        LastPitchingChange = new PitchingChangeOutcome(
            PitchingChangeKind.ReserveRelief,
            outgoing.Name, outgoing.UniformNumber, outgoing.SourceId,
            CurrentPitcher.Name, CurrentPitcher.UniformNumber, CurrentPitcher.SourceId,
            null, null, 0, null);
        ClearRattled();
        return true;
    }

    /// <summary>まだ使っていない控え（名簿順＝背番号順）。交代UIの指名候補。</summary>
    public IReadOnlyList<Player> AvailableReserves => _reserves;

    /// <summary>
    /// 投手交代で指名できる全候補＝控え全員（issue #137: 誰でも投手として登板できる）。
    /// 添字は <see cref="ChangePitcherTo"/> 呼び出しの Player 引数解決に使う（SubstitutionCommands/GameReplay 共通）。
    /// </summary>
    public IReadOnlyList<Player> AvailablePitcherCandidates => _reserves;

    /// <summary>
    /// 継投人選のランキング候補（issue #209 Phase C）。控え全員（Phase B）に加え、
    /// <b>在フィールドのレギュラー野手</b>（現投手を除く打順の守備者）を含める。緊急登板で二刀流の主力を
    /// マウンドへ上げられるようにする。在フィールド野手を引くと守備に穴が開くため、実登板は
    /// <see cref="RelieveWith"/>→<see cref="PromoteFielderToMound"/> が投野交代で backfill する。
    /// DH制でも在フィールド野手を含める（issue #209 Phase C で DH の投野交代を実装済み。<see cref="PromoteFielderToMoundDh"/>
    /// が野手登板＝DH解除として backfill する）。除くのは非DHなら現投手スロット、DHならDHスロット（守備に就かない）。
    /// 返す要素は <see cref="_lineup"/> の実インスタンス（<see cref="RelieveWith"/> の参照一致判定に使う）。
    /// </summary>
    public IReadOnlyList<Player> ReliefRankingCandidates
    {
        get
        {
            var list = new List<Player>(_reserves.Count + 8);
            list.AddRange(_reserves);
            // 在フィールドの守備野手を横断候補に含める。非DHは現投手スロットを、DHはDHスロット（守備に就かない）を除く。
            // DHでは投手は打順外＝_lineup に居ないので、DHスロットを除けば残り8人がそのまま守備8人になる。
            var skip = _usesDh ? _dhSlot : _pitcherSlot;
            for (var i = 0; i < _lineup.Length; i++)
            {
                if (i != skip) list.Add(_lineup[i]);
            }
            return list;
        }
    }

    /// <summary>
    /// 指名された投手で継投する（issue #209 Phase C の交代ルータ）。sub が在フィールドのレギュラー野手なら
    /// 投野交代（<see cref="PromoteFielderToMound"/>）で backfill し、控えからなら従来の
    /// <see cref="ChangePitcherTo"/>。参照一致で「今守備に就いている sub」を特定する。
    /// <paramref name="backfill"/> は在フィールド野手登板時の ①/② 判定係数（null＝既定）。
    /// </summary>
    public bool RelieveWith(Player sub, ReliefBackfillCoefficients? backfill = null)
    {
        var skip = _usesDh ? _dhSlot : _pitcherSlot;
        for (var i = 0; i < _lineup.Length; i++)
        {
            if (i != skip && ReferenceEquals(_lineup[i], sub))
                return _usesDh
                    ? PromoteFielderToMoundDh(sub, i, backfill ?? new ReliefBackfillCoefficients())
                    : PromoteFielderToMound(sub, i, backfill ?? new ReliefBackfillCoefficients());
        }
        return ChangePitcherTo(sub);
    }

    /// <summary>
    /// 在フィールド野手の緊急登板（issue #209 Phase C）。<paramref name="fielder"/>（打順スロット
    /// <paramref name="fSlot"/>）をマウンドへ上げ、空いた守備位置を backfill する。ベンチにその守備を旧投手より
    /// 明確に上手く守れる野手が居て、かつ旧投手を残す打撃的価値が低ければ ②（旧投手退場＋ベンチ野手で補充）、
    /// それ以外は ①（投野交代＝旧投手を空き守備へ回す・両者残留）。判定は <paramref name="backfill"/> 係数駆動。
    /// </summary>
    private bool PromoteFielderToMound(Player fielder, int fSlot, ReliefBackfillCoefficients backfill)
    {
        var retiringPitcher = CurrentPitcher;
        var vacated = fielder.Position;   // 野手が空ける守備位置
        // マウンドへ動かすのは守備位置だけ（issue #381）。投手能力は全選手が最初から持っているので、
        // 登板の瞬間に詰め替える工程（旧 Materialize）は要らない＝詰め替え漏れという失敗の形が消える。
        var mound = fielder with { Position = FieldPosition.Pitcher };

        // ②判定: 空き守備を旧投手より明確に上手く守れるベンチ野手が居るか（打力で基準を補正）。
        var benchFielder = BestReserveDefender();
        if (benchFielder is not null)
        {
            var battingDelta = (retiringPitcher.Contact + retiringPitcher.Power) / 2.0 - backfill.LeagueAverageBatting;
            var requiredMargin = backfill.BaseFieldingMargin + backfill.BattingRetentionWeight * battingDelta;
            if (benchFielder.Fielding - retiringPitcher.Fielding >= requiredMargin)
            {
                // ②: 旧投手は退場（リエントリー禁止）。ベンチ野手が旧投手の打順スロットへ入り、空いた守備位置を守る。
                _reserves.Remove(benchFielder);
                _retired.Add(retiringPitcher);
                _replacedAtSlot[_pitcherSlot] = retiringPitcher.Name;
                _lineup[_pitcherSlot] = benchFielder with { Position = vacated };
                _lineup[fSlot] = mound;
                CurrentPitcher = mound;
                _pitcherSlot = fSlot;
                SaveStintOf(retiringPitcher);
                LoadStintOf(mound);
                PitcherChanges++;
                Substitutions++;   // ベンチ野手の投入は選手交代として計上
                LastPitchingChange = new PitchingChangeOutcome(
                    PitchingChangeKind.FielderReliefWithBackfill,
                    retiringPitcher.Name, retiringPitcher.UniformNumber, retiringPitcher.SourceId,
                    mound.Name, mound.UniformNumber, mound.SourceId,
                    vacated, benchFielder.Name, benchFielder.UniformNumber, benchFielder.SourceId);
                ClearRattled();
                return true;
            }
        }

        // ①: 投野交代（旧投手を空き守備へ回す・両者残留）。_retired/_bench に触れない＝リエントリー禁止に抵触しない。
        _lineup[_pitcherSlot] = retiringPitcher with { Position = vacated };
        _lineup[fSlot] = mound;
        CurrentPitcher = mound;
        _pitcherSlot = fSlot;   // 投手の打順スロットは新投手（元野手）のスロットへ移る（非DH不変条件を維持）
        // ①は旧投手が守備に残る＝後で再登板できる。降板時の球数・疲労を本人に紐づけて持ち回す（issue #235）。
        SaveStintOf(retiringPitcher);
        LoadStintOf(mound);
        PitcherChanges++;
        // ①の backfill は旧投手本人（空いた守備位置へ）。
        LastPitchingChange = new PitchingChangeOutcome(
            PitchingChangeKind.TwoWaySwap,
            retiringPitcher.Name, retiringPitcher.UniformNumber, retiringPitcher.SourceId,
            mound.Name, mound.UniformNumber, mound.SourceId,
            vacated, retiringPitcher.Name, retiringPitcher.UniformNumber, retiringPitcher.SourceId);
        ClearRattled();
        return true;
    }

    /// <summary>
    /// DH制での在フィールド野手の緊急登板（issue #209）。<paramref name="fielder"/>（打順スロット <paramref name="fSlot"/>）を
    /// マウンドへ上げ、DHを解除する。DHでは旧投手は打順外なので**必ず退場**（非DHの①「両者残留」は成立しない）。
    /// 空いた守備位置の補充は非DHと同じ②判定：ベンチ最良守備者が元DHより明確に上手ければベンチ野手が入り元DHは退場、
    /// そうでなければ元DHが守備に就く（DHを失って野手化・打順スロットは維持）。判定は <paramref name="backfill"/> 係数駆動。
    /// </summary>
    private bool PromoteFielderToMoundDh(Player fielder, int fSlot, ReliefBackfillCoefficients backfill)
    {
        var retiringPitcher = CurrentPitcher;   // DHでは打順外＝必ず退場
        var vacated = fielder.Position;         // 野手が空ける守備位置
        var mound = fielder with { Position = FieldPosition.Pitcher };
        var dh = _lineup[_dhSlot];              // 元DH（守備に就いていない・打順スロット _dhSlot）

        // ②判定: 空き守備を元DHより明確に上手く守れるベンチ野手が居るか（元DHの打力で基準を補正＝好打者DHは残す）。
        var benchFielder = BestReserveDefender();
        var useBench = false;
        if (benchFielder is not null)
        {
            var battingDelta = (dh.Contact + dh.Power) / 2.0 - backfill.LeagueAverageBatting;
            var requiredMargin = backfill.BaseFieldingMargin + backfill.BattingRetentionWeight * battingDelta;
            useBench = benchFielder.Fielding - dh.Fielding >= requiredMargin;
        }

        // 共通: 野手Fをマウンドへ（打順スロット維持）、旧投手Pは退場、DH解除。
        _retired.Add(retiringPitcher);
        _lineup[fSlot] = mound;
        CurrentPitcher = mound;
        _pitcherSlot = fSlot;

        Player backfiller;
        if (useBench)
        {
            // ②: ベンチ野手が空き守備へ・元DHの打順スロットへ入る。元DHは退場（リエントリー禁止）。
            _reserves.Remove(benchFielder!);
            _retired.Add(dh);
            _replacedAtSlot[_dhSlot] = dh.Name;
            _lineup[_dhSlot] = benchFielder! with { Position = vacated };
            backfiller = benchFielder!;
        }
        else
        {
            // ①相当: 元DHが空き守備に就く（DHを失って野手化・打順スロット維持）。
            _lineup[_dhSlot] = dh with { Position = vacated };
            backfiller = dh;
        }

        _usesDh = false;   // 野手登板でDHは消滅（設計書09 §6 の ReleaseDh と同じ終着＝非DH整合状態）
        SaveStintOf(retiringPitcher);
        LoadStintOf(mound);
        PitcherChanges++;
        Substitutions++;   // DH解除はロスターイベント（ReleaseDh と同じく計上）
        LastPitchingChange = new PitchingChangeOutcome(
            PitchingChangeKind.FielderReliefWithBackfill,
            retiringPitcher.Name, retiringPitcher.UniformNumber, retiringPitcher.SourceId,
            mound.Name, mound.UniformNumber, mound.SourceId,
            vacated, backfiller.Name, backfiller.UniformNumber, backfiller.SourceId);
        ClearRattled();
        return true;
    }

    /// <summary>控えで最も守備値が高い選手（空き守備位置の backfill 候補。居なければ null）。決定論・同値は先頭優先。</summary>
    private Player? BestReserveDefender()
    {
        Player? best = null;
        foreach (var p in _reserves)
        {
            if (best is null || p.Fielding > best.Fielding) best = p;
        }
        return best;
    }

    // ===== 選手交代（設計書09 §6）。高校野球ルール＝リエントリー禁止（退いた選手は再出場できない）。 =====
    /// <summary>現在の打順9人（交代反映済み）。UI・テスト・采配判断用。</summary>
    public IReadOnlyList<Player> CurrentLineup => _lineup;
    /// <summary>控え（まだ使っていない交代要員・名簿順）。</summary>
    public IReadOnlyList<Player> Reserves => _reserves;
    /// <summary>この試合で行われた選手交代の回数（投手交代は含まない）。</summary>
    public int Substitutions { get; private set; }
    /// <summary>DH制が生きているか（DH解除後は false）。</summary>
    public bool UsesDh => _usesDh;

    /// <summary>控えにこの選手が残っているか（交代可否の判定）。</summary>
    public bool IsAvailable(Player sub) => _reserves.Contains(sub);

    /// <summary>この試合で退場済みか（リエントリー禁止＝再出場できない）。交代UIのグレーアウト判定に使う。</summary>
    public bool IsRetired(Player p) => _retired.Contains(p);

    /// <summary>試合開始時の控え（交代UIが「使い切った控え」をグレーアウト表示するための原本）。</summary>
    public IReadOnlyList<Player> RosterReserves => _team.Reserves;

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
    /// 出場中の2人の守備位置を入れ替える（コンバート・設計書09 §2 / issue #235）。
    /// 選手の出入りが無いので野球ルール上「交代」ではない＝<see cref="Substitutions"/> を消費せず、
    /// 回数制限もない（審判告知のみ）。リエントリー禁止にも抵触しない。
    /// 交換なので9守備位置の一意性は自動で保たれる（<see cref="Season.DefenseAssignment"/> と同じ規則）。
    /// 投手スロット（非DH）とDHスロットは対象外＝投手が動くのは登板の変更なので
    /// <see cref="RelieveWith"/> / <see cref="ConvertPitcherAndRelieve"/> を使う。
    /// </summary>
    public bool SwapDefensivePositions(int slotA, int slotB)
    {
        if (slotA == slotB || !IsSwappableDefenseSlot(slotA) || !IsSwappableDefenseSlot(slotB)) return false;
        // 守備位置の入替規則はスタメン設定（試合前）と同一の単一ソース Season.DefenseAssignment.Swap を通す
        // （issue #233/#256 の「規則の二重実装を解消」）。2枠の交換なので挙動は従来と同一＝帯不変。
        var positions = new FieldPosition[_lineup.Length];
        for (var i = 0; i < _lineup.Length; i++) positions[i] = _lineup[i].Position;
        var next = Season.DefenseAssignment.Swap(positions, slotA, slotB);
        for (var i = 0; i < _lineup.Length; i++)
            if (_lineup[i].Position != next[i]) _lineup[i] = _lineup[i] with { Position = next[i] };
        return true;
    }

    /// <summary>守備位置を入れ替えられる打順スロットか（投手・DHは対象外）。UIの選択可否と同じ判定。</summary>
    public bool IsSwappableDefenseSlot(int slot)
    {
        if (slot < 0 || slot > 8) return false;
        return _usesDh ? slot != _dhSlot : slot != _pitcherSlot;
    }

    /// <summary>
    /// 守備割当（issue #256 統合守備割当）。控えの <paramref name="sub"/> を守備スロット
    /// <paramref name="slot"/> に就かせる（守備固め／守備割当）。就いていた選手は退場する（リエントリー禁止）。
    /// 就く守備位置は slot の現守備位置を継承する。投手スロット・DHスロットは対象外＝登板の変更は
    /// <see cref="RelieveWith"/>、DH解除は <see cref="ReleaseDh"/> を使う。役割（投/野）で候補を分けない（#93）。
    /// </summary>
    public bool AssignFielderToSlot(int slot, Player sub)
        => IsSwappableDefenseSlot(slot) && SubstituteAtSlot(slot, sub);

    /// <summary>
    /// コンバート継投（issue #235 方針②）: 現投手を守備位置 <paramref name="to"/> へ回し、控え投手
    /// <paramref name="reliever"/>（控えの誰か）を登板させる。<paramref name="to"/> を守っていた野手は
    /// 退場し、その打順スロットが空く。
    /// 非DH＝元投手は自分の打順スロットに残ったまま守備位置だけ移り、新投手が空いたスロットへ入る。
    /// DH制＝元投手は打順外だったので、空いた打順スロットを引き継いで守備に就く（DHは維持・新投手は打順外）。
    /// 元投手は<b>退場していない</b>＝打席に立ち続け、後で再登板もできる（リエントリー禁止は退場者にのみ適用）。
    /// 球数・スティント疲労は選手単位で持ち回すので、コンバートを挟んでもスタミナは回復しない。
    /// </summary>
    public bool ConvertPitcherAndRelieve(FieldPosition to, Player reliever)
    {
        if (to is FieldPosition.Pitcher or FieldPosition.DesignatedHitter) return false;
        var fSlot = SlotFieldingAt(to);
        if (fSlot < 0) return false;
        if (!_reserves.Remove(reliever)) return false;

        var oldPitcher = CurrentPitcher;
        var displaced = _lineup[fSlot];
        var mound = reliever with { Position = FieldPosition.Pitcher };

        _retired.Add(displaced);
        _replacedAtSlot[fSlot] = displaced.Name;
        if (_usesDh)
        {
            _lineup[fSlot] = oldPitcher with { Position = to };
        }
        else
        {
            _lineup[_pitcherSlot] = oldPitcher with { Position = to };
            _lineup[fSlot] = mound;
            _pitcherSlot = fSlot;
        }

        SaveStintOf(oldPitcher);
        CurrentPitcher = mound;
        LoadStintOf(mound);
        PitcherChanges++;
        Substitutions++;   // 押し出された野手が退場して控えが入る＝選手交代1回（位置移動そのものは無料）
        LastPitchingChange = new PitchingChangeOutcome(
            PitchingChangeKind.PitcherConvertedRelief,
            oldPitcher.Name, oldPitcher.UniformNumber, oldPitcher.SourceId,
            mound.Name, mound.UniformNumber, mound.SourceId,
            to, oldPitcher.Name, oldPitcher.UniformNumber, oldPitcher.SourceId);
        ClearRattled();
        return true;
    }

    /// <summary>その守備位置に就いている打順スロット（投手・DHの枠は除く）。居なければ -1。</summary>
    private int SlotFieldingAt(FieldPosition pos)
    {
        for (var i = 0; i < 9; i++)
        {
            if (!IsSwappableDefenseSlot(i)) continue;
            if (_lineup[i].Position == pos) return i;
        }
        return -1;
    }

    // ===== スティント疲労の選手単位持ち回し（issue #235） =====
    // コンバートで守備に回った元投手は退場していない＝後で再登板できる。降板時の球数・疲労を選手ごとに
    // 保存し、再登板時に復元することで「コンバートを挟むとスタミナが回復する」抜け道を塞ぐ。
    // 退場した投手は戻れないので、既存の交代経路の見え方は変わらない。
    private readonly Dictionary<Player, (int Pitches, double Fatigue)> _stint = new();

    private void SaveStintOf(Player pitcher) => _stint[pitcher] = (PitchesThrown, FatiguePitches);

    /// <summary>その投手の前回降板時点の球数・疲労を復元する（初登板なら0＝従来どおり）。</summary>
    private void LoadStintOf(Player pitcher)
    {
        if (_stint.TryGetValue(pitcher, out var s)) { PitchesThrown = s.Pitches; FatiguePitches = s.Fatigue; }
        else { PitchesThrown = 0; FatiguePitches = 0; }
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
        // 控えは1本の名簿＝代打・代走・守備交代も継投も同じプールから起用する（#93 / issue #256 / #381）。
        if (!_reserves.Remove(sub)) return false;
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
