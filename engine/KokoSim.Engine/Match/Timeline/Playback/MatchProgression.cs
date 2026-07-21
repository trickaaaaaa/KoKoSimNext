using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;

namespace KokoSim.Engine.Match.Timeline.Playback;

/// <summary>
/// 1打席の観戦データ（対話進行の逐次供給）。タイムラインがある打球/盗塁のみ <see cref="Play"/> が非null。
/// 三振・四球などタイムラインを持たない打席は Play=null で、UI は結果テキストだけを短く見せる。
/// </summary>
public sealed class LivePlateAppearance
{
    public required int Inning { get; init; }
    public required bool IsTop { get; init; }
    public required int AwayScore { get; init; }
    public required int HomeScore { get; init; }
    public required string BatterName { get; init; }
    public required int RunsScored { get; init; }
    public required PlateAppearanceResult Result { get; init; }
    public PlaybackPlay? Play { get; init; }

    // ── 中継風カウント表示用（#4）。すべて観測データで結果には影響しない ──
    /// <summary>合成した投球列（B/S を1球ずつ点灯させる。結果と整合・独立シード）。</summary>
    public PitchSequence PitchSeq { get; init; } = PitchSequence.Empty;
    /// <summary>打席開始時のアウト数（0-2）。</summary>
    public int OutsBefore { get; init; }
    /// <summary>打席前の塁占有（塁ダイヤ初期表示）。</summary>
    public bool BaseFirstBefore { get; init; }
    public bool BaseSecondBefore { get; init; }
    public bool BaseThirdBefore { get; init; }
    /// <summary>打席解決後の塁占有（塁ダイヤを結果へ更新）。</summary>
    public bool BaseFirstAfter { get; init; }
    public bool BaseSecondAfter { get; init; }
    public bool BaseThirdAfter { get; init; }

    // ── 現在の対戦（マッチアップHUD＋スタメン列ハイライト用）。すべて観測データで結果には影響しない ──
    /// <summary>この打者の打順（1-9。攻撃側列のハイライト用）。</summary>
    public int BatterOrder { get; init; }
    /// <summary>打者の選手ID（自校のみ。相手校は null）。通算・背番号の join キー。</summary>
    public int? BatterSourceId { get; init; }
    /// <summary>打者の左右打。</summary>
    public Players.Handedness BatterBats { get; init; }
    /// <summary>打者の守備位置（相手校の背番号フォールバック用）。</summary>
    public Field.FieldPosition BatterPosition { get; init; }
    /// <summary>対戦投手の選手ID（自校のみ。相手校は null）。</summary>
    public int? PitcherSourceId { get; init; }
    /// <summary>対戦投手名。</summary>
    public string PitcherName { get; init; } = "";
    /// <summary>対戦投手の左右投。</summary>
    public Players.Handedness PitcherThrows { get; init; }
    /// <summary>打者の背番号（0=番号なし）。</summary>
    public int BatterNumber { get; init; }
    /// <summary>対戦投手の背番号（0=番号なし）。</summary>
    public int PitcherNumber { get; init; }
}

/// <summary>設計書15 Phase D-1: <see cref="MatchProgression.AdvancePitch"/> が止まった境界の種類。</summary>
public enum AdvancePitchResult
{
    /// <summary>1球ごとの采配窓（<see cref="GameStepKind.Pitch"/>）で止まった。まだ打席は確定していない。</summary>
    Pitch,
    /// <summary>打席が確定した（<see cref="MatchProgression.Current"/> が更新された）。</summary>
    PlateAppearance,
    /// <summary>試合が終了した。</summary>
    Finished,
}

/// <summary>
/// 実試合を「打席単位で解決→再生→采配窓→次」で進める逐次供給ドライバ（設計書09 采配進行の土台）。
/// <see cref="GameEngine.Steps"/> を1打席ずつ引き、各打席を <see cref="LivePlateAppearance"/> として供給する。
/// 采配（今回は代打）は打席間に注入し、<see cref="Save"/> で保存できる決定列として記録する。
/// 「スキップ」は残りを委任AI（設計書11）へ委ねて一括解決する。バッチ Play とは単一コードパスで、
/// 采配なしで全打席進めれば結果はバッチと完全一致する（＝MatchProgressionSteppingTests で保証）。
/// Unity 非依存で dotnet テスト可能。
/// </summary>
public sealed class MatchProgression
{
    private readonly GameProgress _p;
    private readonly IEnumerator<GameStep> _steps;
    private readonly List<GameDecision> _decisions = new();
    private readonly ulong _seed;
    private readonly bool _seedable;   // seed ベースで構築＝Save() で中断保存できるか

    private int _confirmed;   // 確定した打席数
    private int _away, _home; // 進行中スコア
    private int _pitchIndexInCurrentPa; // AdvancePitch() で現在の打席内に何回Pitch窓が来たか（0始まり）

    public MatchProgression(Team away, Team home, GameContext ctx, ulong seed)
    {
        _seed = seed;
        _seedable = true;
        _p = GameEngine.NewProgress(away, home, ctx, new Xoshiro256Random(seed));
        _steps = GameEngine.Steps(_p).GetEnumerator();
    }

    /// <summary>
    /// 注入乱数で駆動する（大会の隔離Fork ストリームをそのまま渡す用）。<see cref="GameEngine.Play"/> に
    /// 同じ away/home/ctx/rng を渡した結果とバイト一致で進行する（＝観戦しても大会結果は不変）。
    /// seed を持たないため <see cref="Save"/> による中断保存は非対応（本配線は別タスク）。
    /// </summary>
    public MatchProgression(Team away, Team home, GameContext ctx, IRandomSource rng)
    {
        _seed = 0;
        _seedable = false;
        _p = GameEngine.NewProgress(away, home, ctx, rng);
        _steps = GameEngine.Steps(_p).GetEnumerator();
    }

    /// <summary>直近に確定した打席（未進行なら null）。</summary>
    public LivePlateAppearance? Current { get; private set; }
    public bool IsFinished { get; private set; }
    public int ConfirmedPlateAppearances => _confirmed;
    public int AwayScore => _away;
    public int HomeScore => _home;

    /// <summary>
    /// 設計書15 Phase D-1: 今ここで <see cref="SetPitchBattingOverride"/>/<see cref="SetPitchDefenseOverride"/>
    /// を呼んだら、現在進行中の打席の何球目（0始まり）に適用されるか。<see cref="Advance"/> だけを使う限り常に0
    /// （＝打席の最初の球、従来どおり）。<see cref="AdvancePitch"/> で <see cref="GameStepKind.Pitch"/> に
    /// 止まった直後だけ、その球の添字に更新される。
    /// </summary>
    public int PendingPitchIndex { get; private set; }

    /// <summary>中断保存の状態（シード＋確定打席数＋采配決定列）。<see cref="GameReplay.Restore"/> で復元できる。</summary>
    public GameSaveState Save()
    {
        if (!_seedable)
            throw new System.InvalidOperationException(
                "注入乱数で構築した MatchProgression は seed を持たないため Save() で中断保存できません。");
        return new(_seed, _confirmed) { Decisions = _decisions.ToArray() };
    }

    /// <summary>
    /// 采配窓（打席前）: 次打席の攻撃側に代打を送る。offenseIsAway=攻撃側が先攻(away)か。
    /// 適用できたら保存/委任でも再現できるよう決定列に記録する。采配なし（未呼び出し）ならバッチと一致。
    /// </summary>
    public bool PinchHitUpcoming(bool offenseIsAway, int benchIndex)
    {
        var team = _p.OffenseOf(offenseIsAway);
        if (benchIndex < 0 || benchIndex >= team.Bench.Count) return false;
        if (!team.PinchHitNext(team.Bench[benchIndex])) return false;
        _decisions.Add(new GameDecision(_confirmed, GameDecisionKind.PinchHit, offenseIsAway, benchIndex));
        return true;
    }

    /// <summary>
    /// 1球采配（設計書15 Phase C-3）: 次の1球への打撃指示（強攻/待て）を予約する。
    /// offenseIsAway=打撃指示の対象（攻撃側）が先攻(away)か。null=指示解除。1球限り（消費後は自動で解除）。
    /// <see cref="ITacticsBrain"/> を経由しない手動操作＝プレイヤーの直接介入（PinchHitUpcoming と同型）。
    /// </summary>
    public void SetPitchBattingOverride(bool offenseIsAway, PitchBattingOverride? battingOverride)
    {
        _p.OffenseOf(offenseIsAway).SetPendingPitchBattingOverride(battingOverride);
        _decisions.Add(new GameDecision(_confirmed, GameDecisionKind.PitchBattingOverride, offenseIsAway, BenchIndex: 0,
            Batting: battingOverride, PitchIndex: PendingPitchIndex));
    }

    /// <summary>
    /// 1球采配（設計書15 Phase C-3）: 次の1球への配球方針/ギア上書きを予約する。
    /// offenseIsAway=攻撃側が先攻(away)か（守備側はその逆側に適用）。両方nullで指示解除。1球限り。
    /// </summary>
    public void SetPitchDefenseOverride(bool offenseIsAway, PitchPolicy? policy, Pitching.PitcherGear? gear)
    {
        _p.OffenseOf(!offenseIsAway).SetPendingPitchDefenseOverride(policy, gear);
        _decisions.Add(new GameDecision(_confirmed, GameDecisionKind.PitchDefenseOverride, offenseIsAway, BenchIndex: 0,
            Policy: policy, Gear: gear, PitchIndex: PendingPitchIndex));
    }

    /// <summary>次の打席を1つ解決して <see cref="Current"/> に載せる。試合終了なら false。</summary>
    public bool Advance()
    {
        if (IsFinished) return false;
        // 1球ごとの采配窓（GameStepKind.Pitch, 設計書15）は読み飛ばし、打席が確定した境界だけを表に出す。
        // 従来どおりの打席単位供給（Pitch 窓で一時停止したいなら AdvancePitch を使う＝Phase D-1）。
        GameStep step;
        do
        {
            if (!_steps.MoveNext())
            {
                IsFinished = true;
                Current = null;
                return false;
            }
            step = _steps.Current;
        } while (step.Kind != GameStepKind.PlateAppearance);
        ConfirmPlateAppearance(step);
        return true;
    }

    /// <summary>
    /// 設計書15 Phase D-1: 真の1球進行。<see cref="GameStepKind.Pitch"/> 窓でも実際に止まり、
    /// 手動1球指示（<see cref="SetPitchBattingOverride"/>/<see cref="SetPitchDefenseOverride"/>）が
    /// 打席内の任意の球に届くようにする。無指示で呼び続けた場合は <see cref="Advance"/> と結果・RNG消費が完全一致する
    /// （進行粒度が細かくなるだけ＝帯不変）。
    /// </summary>
    public AdvancePitchResult AdvancePitch()
    {
        if (IsFinished) return AdvancePitchResult.Finished;
        if (!_steps.MoveNext())
        {
            IsFinished = true;
            Current = null;
            return AdvancePitchResult.Finished;
        }
        var step = _steps.Current;
        if (step.Kind == GameStepKind.Pitch)
        {
            PendingPitchIndex = _pitchIndexInCurrentPa;
            _pitchIndexInCurrentPa++;
            return AdvancePitchResult.Pitch;
        }
        ConfirmPlateAppearance(step);
        return AdvancePitchResult.PlateAppearance;
    }

    private void ConfirmPlateAppearance(GameStep step)
    {
        var e = _p.Log[step.LogIndex];
        if (e.IsTop) _away += e.RunsScored; else _home += e.RunsScored;

        // 投球列（設計書15 §4）: AtBatSession が解いた実データを最優先。バント/スクイズ等 PitchLog が無い
        // 経路（統一はPhase D）だけ、結果と整合する合成列にフォールバックする（打席固有シード＝メインRNG非依存）。
        PitchSequence pitchSeq;
        if (e.PitchLog is { Count: > 0 } pitchLog)
        {
            pitchSeq = new PitchSequence(
                pitchLog.Select(p => new PitchToken(
                    p.Kind, p.BallsAfter, p.StrikesAfter, p.PitchType, p.VelocityKmh)).ToList());
        }
        else
        {
            var seed = PitchSequenceSynthesizer.SeedFrom(step.LogIndex, step.Inning, step.IsTop, e.Pitches, e.Result);
            pitchSeq = PitchSequenceSynthesizer.Synthesize(e.Result, e.Pitches, seed);
        }
        Current = new LivePlateAppearance
        {
            Inning = step.Inning,
            IsTop = step.IsTop,
            AwayScore = _away,
            HomeScore = _home,
            BatterName = e.BatterName,
            RunsScored = e.RunsScored,
            Result = e.Result,
            Play = e.Timeline is null ? null : PlayTimelineAdapter.ToPlaybackPlay(e.Timeline),
            PitchSeq = pitchSeq,
            OutsBefore = e.OutsBefore,
            BaseFirstBefore = e.BaseFirstBefore,
            BaseSecondBefore = e.BaseSecondBefore,
            BaseThirdBefore = e.BaseThirdBefore,
            BaseFirstAfter = e.BaseFirstAfter,
            BaseSecondAfter = e.BaseSecondAfter,
            BaseThirdAfter = e.BaseThirdAfter,
            BatterOrder = e.BatterOrder,
            BatterSourceId = e.BatterSourceId,
            BatterBats = e.BatterBats,
            BatterPosition = e.BatterPosition,
            PitcherSourceId = e.PitcherSourceId,
            PitcherName = e.PitcherName ?? "",
            PitcherThrows = e.PitcherThrows,
            BatterNumber = e.BatterNumber,
            PitcherNumber = e.PitcherNumber,
        };
        _confirmed++;
        _pitchIndexInCurrentPa = 0;
        PendingPitchIndex = 0;
    }

    /// <summary>
    /// ライブ観戦のスナップショット（両チームの現ラインナップ＋現投手の今日の成績・現在の攻撃側/打者）。
    /// 3カラムのスタメン列とマッチアップHUDの今日成績を、UI再計算せずここから引く（不変条件: 数値はエンジン集計から）。
    /// <see cref="Advance"/> 後に呼ぶ（<see cref="Current"/> の攻撃側/打順を反映）。観測データ＝試合結果に影響しない。
    /// </summary>
    public MatchLiveSnapshot Snapshot()
    {
        var away = new LiveTeamSnapshot(_p.Away.LiveLineup(), _p.Away.LivePitcherLine(),
            _p.Away.LiveBench(), _p.Away.LiveBullpen());
        var home = new LiveTeamSnapshot(_p.Home.LiveLineup(), _p.Home.LivePitcherLine(),
            _p.Home.LiveBench(), _p.Home.LiveBullpen());
        return new MatchLiveSnapshot(away, home, Current?.IsTop ?? true, Current?.BatterOrder ?? 0);
    }

    /// <summary>
    /// スキップ: 以降の采配を委任AI（設計書11・自分の采配能力＝TacticalSense）へ委ね、残りを一括解決して
    /// 最終結果を返す。途中までの采配（適用済みの代打など）は状態に載っているので当然引き継がれる。
    /// managerIsAway=委任する監督のチームが先攻(away)か。委任先は該当チームのみ（相手は従来どおり）。
    /// </summary>
    public GameResult SkipDelegateToAi(bool managerIsAway, int managerTacticalSense, int tierRank = 5)
    {
        var brain = new AiTacticsBrain(AiProfile.Delegated(managerTacticalSense, tierRank));
        _p.OffenseOf(managerIsAway).OverrideTactics(brain);
        return DrainToEnd();
    }

    /// <summary>残りを采配追加なしで一括解決して最終結果を返す（「手動で最後まで」の残り消化）。</summary>
    public GameResult FinishRemaining() => DrainToEnd();

    private GameResult DrainToEnd()
    {
        while (_steps.MoveNext())
        {
            // Pitch 窓（設計書15）は打席確定ではないのでスコア加算・確定カウントの対象外。
            if (_steps.Current.Kind != GameStepKind.PlateAppearance) continue;
            var e = _p.Log[_steps.Current.LogIndex];
            if (e.IsTop) _away += e.RunsScored; else _home += e.RunsScored;
            _confirmed++;
        }
        IsFinished = true;
        Current = null;
        return GameEngine.BuildResult(_p);
    }

    /// <summary>ここまでの進行から最終結果を組む（全打席 Advance し終えた後に呼ぶ）。</summary>
    public GameResult BuildResult() => GameEngine.BuildResult(_p);
}
