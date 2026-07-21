using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Game;

/// <summary>
/// 試合の中断保存（設計書09 采配進行の中断安全＝「最後に確定した打席までの状態を保存」）。
///
/// 決定論エンジンなので、状態グラフ（TeamState/RNG/走者…）をそのままシリアライズする代わりに、
/// **シード＋確定打席数（＋将来は采配決定列）だけ**を保存し、復元は「同シードで先頭から確定打席数ぶん
/// 再生して同一状態を再構築」する。保存物は小さく完全にシリアライズ可能で、これ自体が決定論の一部。
/// 打席途中で保存しても、再生が enumerator の位置を正確に復元するため中断なし実行と一致する。
/// </summary>
public sealed record GameSaveState(ulong Seed, int ConfirmedPlateAppearances)
{
    // 将来: 適用した采配決定列（代打/代走/サイン/伝令）をここに持たせ、再生時に同じ打席で適用する。
    public IReadOnlyList<GameDecision> Decisions { get; init; } = System.Array.Empty<GameDecision>();

    /// <summary>
    /// 開始時点の RNG 内部状態（設計書17 §3.2, F0）。非null なら <see cref="Seed"/> より優先して復元に使う。
    /// シードを持たない乱数源（大会の隔離Fork ストリーム）で始まった試合を中断保存できるようにするための追加。
    /// 既存セーブは null＝従来どおり <see cref="Seed"/> から復元する（完全後方互換）。
    /// </summary>
    public ulong[]? RngState { get; init; }

    /// <summary>
    /// 現在の打席の中で確定済みの投球窓の数（設計書17 §3.2 / §6.2, F0）。
    /// <see cref="ConfirmedPlateAppearances"/> ぶん再生したあと、さらにこの数だけ
    /// <see cref="GameStepKind.Pitch"/> 窓を進めた位置で復元する＝pitch粒度のシーク。
    /// 既定0＝打席境界での復元（従来と完全一致）。
    /// </summary>
    public int ConfirmedPitchesInCurrentPa { get; init; }
}

/// <summary>
/// 采配決定の1件（中断保存で再生時に適用する）。代打（BenchIndex）と1球采配（設計書15 Phase C-3,
/// PitchBattingOverride/PitchPolicy/PitcherGear）の両方をこの1レコードで表現する（種別ごとに使うフィールドのみ埋める）。
/// </summary>
/// <param name="PitchIndex">
/// 設計書15 Phase D: AtStep で示す打席の中で、何球目（0始まり）に適用するか。既定0＝打席の最初の球。
/// Advance() ベースの旧セーブは常に0（打席頭適用）で、そのまま新しい適用点でも互換に読める。
/// AdvancePitch() で打席途中に一時停止して予約した場合のみ、その時点の球数が入る。
/// </param>
/// <param name="BenchIndex">
/// 交代で「入る」選手の添字。<see cref="GameDecisionKind.PinchHit"/>/<see cref="GameDecisionKind.PinchRun"/>/
/// <see cref="GameDecisionKind.DefensiveSub"/> は野手控え（TeamState.Bench）の添字、
/// <see cref="GameDecisionKind.ChangePitcher"/> はブルペン（TeamState.AvailableBullpen）の添字。
/// </param>
/// <param name="TargetIndex">
/// 交代で「退く」側の指定。<see cref="GameDecisionKind.PinchRun"/> は塁の添字（0=一塁,1=二塁,2=三塁）、
/// <see cref="GameDecisionKind.DefensiveSub"/> は打順スロット（0-8）。他の種別では未使用。
/// </param>
/// <param name="At">
/// <see cref="GameDecisionKind.ReleaseDh"/> でDHの選手が就く守備位置（null＝DHはそのまま退場）。
/// </param>
public sealed record GameDecision(
    int AtStep, GameDecisionKind Kind, bool OffenseIsAway, int BenchIndex,
    PitchBattingOverride? Batting = null, PitchPolicy? Policy = null, Pitching.PitcherGear? Gear = null,
    int PitchIndex = 0, int TargetIndex = 0, Field.FieldPosition? At = null);

public enum GameDecisionKind
{
    PinchHit,
    /// <summary>1球采配の手動指示（設計書15 Phase C-3）。OffenseIsAway=打撃指示の対象側（攻撃側）か。</summary>
    PitchBattingOverride,
    /// <summary>1球采配の守備側手動指示（設計書15 Phase C-3）。OffenseIsAway=攻撃側の判定に使う（守備側はその逆）。</summary>
    PitchDefenseOverride,
    /// <summary>代走（設計書09 §6）。OffenseIsAway=攻撃側が先攻か。TargetIndex=塁の添字。</summary>
    PinchRun,
    /// <summary>投手交代（指名継投, 設計書09 §6）。OffenseIsAway=**守備側**が先攻か。BenchIndex=ブルペン添字。</summary>
    ChangePitcher,
    /// <summary>守備交代（設計書09 §6）。OffenseIsAway=**守備側**が先攻か。TargetIndex=打順スロット。</summary>
    DefensiveSub,
    /// <summary>DH解除（設計書09 §6・不可逆）。OffenseIsAway=**守備側**が先攻か。At=DHが就く守備位置。</summary>
    ReleaseDh,
    // 将来: Sign, Timeout, ...
}

/// <summary>復元された進行（状態＋続きを回すための enumerator）。drain して <see cref="GameEngine.BuildResult"/> へ。</summary>
public sealed class ResumedGame
{
    public GameProgress Progress { get; }
    public IEnumerator<GameStep> Steps { get; }

    internal ResumedGame(GameProgress progress, IEnumerator<GameStep> steps)
    {
        Progress = progress;
        Steps = steps;
    }
}

public static class GameReplay
{
    /// <summary>
    /// 保存状態から進行を復元する。同シードで先頭から <see cref="GameSaveState.ConfirmedPlateAppearances"/> ぶん
    /// 再生し（enumerator を進め）、その位置から続行できる <see cref="ResumedGame"/> を返す。
    /// awayTeam/homeTeam/ctx は試合設定（保存ファイル側で別途シリアライズする前提）。
    /// </summary>
    public static ResumedGame Restore(Team awayTeam, Team homeTeam, GameContext ctx, GameSaveState save)
    {
        // 開始乱数源: RngState があればそこから（大会Fork経路の中断保存, 設計書17 §3.2）、
        // 無ければ従来どおりシードから。既存セーブは RngState=null なので挙動不変。
        var rng = save.RngState is { } st
            ? Xoshiro256Random.FromState(st)
            : new Xoshiro256Random(save.Seed);
        var p = GameEngine.NewProgress(awayTeam, homeTeam, ctx, rng);
        var e = GameEngine.Steps(p).GetEnumerator();

        // 采配決定を「その球の直前」に適用しながら、確定打席数ぶん再生する（設計書15 Phase D-1）。
        // PitchIndex 既定0＝打席頭適用のため、Pitch 窓を経由しない旧セーブと完全互換。
        //
        // タイミングの根拠: GameEngine.PlayHalfSteps の投球ループは `yield return Pitch()` の直後で
        // pending override を読む（ConsumePendingPitchBattingOverride）。つまり「N回目に Pitch 窓で
        // 止まった直後」に予約した指示は、次の MoveNext（＝その窓が指す1球の解決）で消費される。
        // これは PendingPitchIndex を「インクリメント前」の値として公開する MatchProgression.AdvancePitch と
        // 同じ数え方（1回目の停止でPendingPitchIndex=0）なので、ここでも増分前の値で適用する。
        var decisionsByStep = IndexDecisions(save.Decisions);
        var paIndex = 0;
        var pitchIndex = 0;
        ApplyDecisionsBefore(p, decisionsByStep, paIndex, pitchIndex); // 打席頭（call#1より前）
        var exhausted = false;
        while (paIndex < save.ConfirmedPlateAppearances)
        {
            if (!e.MoveNext()) { exhausted = true; break; } // 試合が先に終わっていた（保存ステップ数が過大）→ そこで打ち切り
            if (e.Current.Kind == GameStepKind.Pitch)
            {
                ApplyDecisionsBefore(p, decisionsByStep, paIndex, pitchIndex); // 増分前＝この窓が指す球への予約
                pitchIndex++;
            }
            else
            {
                paIndex++;
                pitchIndex = 0;
                ApplyDecisionsBefore(p, decisionsByStep, paIndex, pitchIndex);
            }
        }

        // pitch粒度の復元（設計書17 §3.2 / §6.2, F0）: 打席境界まで戻したあと、保存時に消化済みだった
        // 投球窓のぶんだけさらに進める。MatchProgression.AdvancePitch と同じ数え方（増分前の値で予約適用）。
        // 既存セーブは ConfirmedPitchesInCurrentPa=0 なのでこのループを1回も回らない＝挙動不変。
        while (!exhausted && pitchIndex < save.ConfirmedPitchesInCurrentPa)
        {
            if (!e.MoveNext()) break;
            if (e.Current.Kind != GameStepKind.Pitch)
            {
                // 保存球数が実際の打席の球数を超えていた（壊れたセーブ）→ 打席確定として受け止めて打ち切る。
                paIndex++;
                pitchIndex = 0;
                ApplyDecisionsBefore(p, decisionsByStep, paIndex, pitchIndex);
                break;
            }
            ApplyDecisionsBefore(p, decisionsByStep, paIndex, pitchIndex);
            pitchIndex++;
        }
        return new ResumedGame(p, e);
    }

    private static Dictionary<(int AtStep, int PitchIndex), List<GameDecision>> IndexDecisions(
        IReadOnlyList<GameDecision> decisions)
    {
        var map = new Dictionary<(int, int), List<GameDecision>>();
        foreach (var d in decisions)
        {
            var key = (d.AtStep, d.PitchIndex);
            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<GameDecision>();
            list.Add(d);
        }
        return map;
    }

    private static void ApplyDecisionsBefore(
        GameProgress p, Dictionary<(int AtStep, int PitchIndex), List<GameDecision>> byStep, int step, int pitchIndex)
    {
        var key = (step, pitchIndex);
        if (!byStep.TryGetValue(key, out var list)) return;
        // 適用後は取り除く: PitchIndex=0 は「打席頭」と「1球目のPitch窓」の両方から辿り着き得るため
        // （どちらのタイミングで適用しても消費側には同じ効果だが、PinchHit は二重適用が禁物）、
        // 一度適用した鍵への再訪問を安全な no-op にする。
        byStep.Remove(key);
        foreach (var d in list)
        {
            switch (d.Kind)
            {
                case GameDecisionKind.PinchHit:
                    SubstitutionCommands.PinchHit(p, d.OffenseIsAway, d.BenchIndex);
                    break;
                case GameDecisionKind.PitchBattingOverride:
                    p.OffenseOf(d.OffenseIsAway).SetPendingPitchBattingOverride(d.Batting);
                    break;
                case GameDecisionKind.PitchDefenseOverride:
                    p.OffenseOf(!d.OffenseIsAway).SetPendingPitchDefenseOverride(d.Policy, d.Gear);
                    break;
                case GameDecisionKind.PinchRun:
                    SubstitutionCommands.PinchRun(p, d.OffenseIsAway, d.TargetIndex, d.BenchIndex);
                    break;
                case GameDecisionKind.ChangePitcher:
                    SubstitutionCommands.ChangePitcher(p, d.OffenseIsAway, d.BenchIndex);
                    break;
                case GameDecisionKind.DefensiveSub:
                    SubstitutionCommands.DefensiveSub(p, d.OffenseIsAway, d.TargetIndex, d.BenchIndex);
                    break;
                case GameDecisionKind.ReleaseDh:
                    SubstitutionCommands.ReleaseDh(p, d.OffenseIsAway, d.At);
                    break;
            }
        }
    }
}
