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
public sealed record GameDecision(
    int AtStep, GameDecisionKind Kind, bool OffenseIsAway, int BenchIndex,
    PitchBattingOverride? Batting = null, PitchPolicy? Policy = null, Pitching.PitcherGear? Gear = null,
    int PitchIndex = 0);

public enum GameDecisionKind
{
    PinchHit,
    /// <summary>1球采配の手動指示（設計書15 Phase C-3）。OffenseIsAway=打撃指示の対象側（攻撃側）か。</summary>
    PitchBattingOverride,
    /// <summary>1球采配の守備側手動指示（設計書15 Phase C-3）。OffenseIsAway=攻撃側の判定に使う（守備側はその逆）。</summary>
    PitchDefenseOverride,
    // 将来: PinchRun, DefensiveSub, Sign, Timeout, ...
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
        var p = GameEngine.NewProgress(awayTeam, homeTeam, ctx, new Xoshiro256Random(save.Seed));
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
        while (paIndex < save.ConfirmedPlateAppearances)
        {
            if (!e.MoveNext()) break; // 試合が先に終わっていた（保存ステップ数が過大）→ そこで打ち切り
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
                    var team = p.OffenseOf(d.OffenseIsAway); // OffenseOf(isTop): isTop=true=先攻away
                    var bench = team.Bench;
                    if (d.BenchIndex >= 0 && d.BenchIndex < bench.Count)
                        team.PinchHitNext(bench[d.BenchIndex]);
                    break;
                case GameDecisionKind.PitchBattingOverride:
                    p.OffenseOf(d.OffenseIsAway).SetPendingPitchBattingOverride(d.Batting);
                    break;
                case GameDecisionKind.PitchDefenseOverride:
                    p.OffenseOf(!d.OffenseIsAway).SetPendingPitchDefenseOverride(d.Policy, d.Gear);
                    break;
            }
        }
    }
}
