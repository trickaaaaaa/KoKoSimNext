using System;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Game;

/// <summary>
/// 走者1人の移動記録（タイムライン用, CHANGELOG 32）。FromBase: 0=打席, 1〜3=各塁。ToBase: 4=本塁生還。
/// </summary>
public sealed record RunnerMove(Player Runner, int FromBase, int ToBase, bool Out)
{
    /// <summary>
    /// 判定オーバーレイ（Issue #59）: 守備との時間の勝負で決着した際、margin絶対値が
    /// <see cref="BaserunningCoefficients.CloseCallMarginSeconds"/> 未満（際どい）だったか。
    /// 判定を伴わない進塁（無風の進塁等）は既定 false。表示専用・結果不変。
    /// </summary>
    public bool CloseCall { get; init; }
}

/// <summary>
/// 打席結果に応じて走者を進め、得点と追加アウト（併殺）を返す（設計書02 §4.1 の進塁を確率化）。
/// </summary>
public static class BaserunningModel
{
    /// <summary>進塁を適用。currentOuts はこの打席前のアウト数。戻り値=(得点, 追加アウト)。</summary>
    public static (int Runs, int ExtraOuts) Apply(
        BaseState bases, PlateAppearanceResult result, Player batter,
        int currentOuts, BaserunningCoefficients coeff, IRandomSource rng, HomePlayContext? home = null)
    {
        var (runs, extraOuts, _, _, _, _) = ApplyDetailed(bases, result, batter, currentOuts, coeff, rng, collectMoves: false, home);
        return (runs, extraOuts);
        // 注: ApplyDetailed の第3要素は (Home, Third) の憤死内訳（本塁クロスプレー/三塁到達レース）。
    }

    /// <summary>この打席で打者自身がアウトになるか。野選（FC）成立時は先行走者のみアウトで打者は生存（design-14 P1-1）。
    /// 振り逃げ（design-14 P1-2）成立時は三振でも打者は生存。</summary>
    public static bool IsBatterOut(PlateAppearanceResult result, bool batterSafeOnFc, bool droppedThirdStrikeReached = false)
        => (result is PlateAppearanceResult.Strikeout && !droppedThirdStrikeReached)
           || (result is PlateAppearanceResult.InPlayOut && !batterSafeOnFc);

    /// <summary>暴投・パスボール（design-14 P2-8）: 全走者が1つ進塁し、三塁走者は生還する。戻り値=得点。</summary>
    public static int ApplyBatteryMiss(BaseState bases)
    {
        var runs = 0;
        if (bases.Third is not null) { runs++; bases.Third = null; }
        if (bases.Second is not null) { bases.Third = bases.Second; bases.Second = null; }
        if (bases.First is not null) { bases.Second = bases.First; bases.First = null; }
        return runs;
    }

    /// <summary>
    /// 進塁の詳細版（走者の動きを記録, タイムライン用）。判定・乱数消費順は Apply と同一。
    /// BaseOuts=(Home, Third) 塁上での憤死内訳。Home=本塁クロスプレー（F2/G1）、Third=単打の一塁→三塁
    /// レース（Issue #89）で刺された走者数（いずれも ExtraOuts の内数。統計参考値）。
    /// BatterSafeOnFc=野選（FC, design-14 P1-1）成立で打者が一塁生存したか。
    /// ErrorExtraAdvanceOccurred=失策連鎖（design-14 P1-6）が成立したか（統計参考値）。
    /// </summary>
    public static (int Runs, int ExtraOuts, (int Home, int Third) BaseOuts, bool BatterSafeOnFc,
        bool ErrorExtraAdvanceOccurred, IReadOnlyList<RunnerMove> Moves) ApplyDetailed(
        BaseState bases, PlateAppearanceResult result, Player batter,
        int currentOuts, BaserunningCoefficients coeff, IRandomSource rng, bool collectMoves = true,
        HomePlayContext? home = null, StartType r1Start = StartType.Normal)
    {
        var mv = collectMoves ? new List<RunnerMove>() : null;
        var r1 = bases.First;
        var r2 = bases.Second;
        var r3 = bases.Third;

        switch (result)
        {
            case PlateAppearanceResult.Strikeout:
                return (0, 0, (0, 0), false, false, Moves(mv));

            case PlateAppearanceResult.Walk:
            case PlateAppearanceResult.HitByPitch: // 死球はボールデッド＝四球と同じフォース進塁のみ
            {
                var (runs, outs) = ApplyWalk(bases, batter, r1, r2, r3, mv);
                return (runs, outs, (0, 0), false, false, Moves(mv));
            }

            case PlateAppearanceResult.Single:
            {
                var (runs, outs, homeOuts, thirdOuts) = ApplyRunnerAdvance(bases, batter, r1, r2, r3, currentOuts, coeff, rng, isDouble: false, mv, home);
                return (runs, outs, (homeOuts, thirdOuts), false, false, Moves(mv)); // 安打の追加アウト=本塁憤死＋三塁憤死
            }

            case PlateAppearanceResult.ReachedOnError:
            {
                var (runs, outs, homeOuts, thirdOuts) = ApplyRunnerAdvance(bases, batter, r1, r2, r3, currentOuts, coeff, rng, isDouble: false, mv, home);
                // 失策の連鎖（design-14 P1-6）: 悪送球で全走者＋打者が追加1個進む。既定オフ(ErrorExtraAdvanceProb=0)
                // では MathUtil.Chance 自体を呼ばずrng消費ゼロ＝Single と乱数消費順・結果とも完全一致。
                var errorExtraAdvanceOccurred = false;
                if (coeff.ErrorExtraAdvanceProb > 0.0)
                {
                    var (extraRuns, occurred) = ApplyErrorExtraAdvance(bases, batter, currentOuts, outs, coeff, rng, mv);
                    runs += extraRuns;
                    errorExtraAdvanceOccurred = occurred;
                }
                return (runs, outs, (homeOuts, thirdOuts), false, errorExtraAdvanceOccurred, Moves(mv));
            }

            case PlateAppearanceResult.Double:
            {
                var (runs, outs, homeOuts, thirdOuts) = ApplyRunnerAdvance(bases, batter, r1, r2, r3, currentOuts, coeff, rng, isDouble: true, mv, home);
                return (runs, outs, (homeOuts, thirdOuts), false, false, Moves(mv));
            }

            case PlateAppearanceResult.Triple:
            {
                var runs = bases.RunnerCount;
                if (r3 is not null) mv?.Add(new RunnerMove(r3, 3, 4, false));
                if (r2 is not null) mv?.Add(new RunnerMove(r2, 2, 4, false));
                if (r1 is not null) mv?.Add(new RunnerMove(r1, 1, 4, false));
                mv?.Add(new RunnerMove(batter, 0, 3, false));
                bases.Clear();
                bases.Third = batter;
                return (runs, 0, (0, 0), false, false, Moves(mv));
            }

            case PlateAppearanceResult.HomeRun:
            {
                var runs = bases.RunnerCount + 1;
                if (r3 is not null) mv?.Add(new RunnerMove(r3, 3, 4, false));
                if (r2 is not null) mv?.Add(new RunnerMove(r2, 2, 4, false));
                if (r1 is not null) mv?.Add(new RunnerMove(r1, 1, 4, false));
                mv?.Add(new RunnerMove(batter, 0, 4, false));
                bases.Clear();
                return (runs, 0, (0, 0), false, false, Moves(mv));
            }

            case PlateAppearanceResult.InPlayOut:
            {
                var (runs, outs, homeOuts, batterSafeOnFc) = ApplyInPlayOut(bases, batter, r1, r2, r3, currentOuts, coeff, rng, mv, home, r1Start);
                return (runs, outs, (homeOuts, 0), batterSafeOnFc, false, Moves(mv)); // 凡打の塁上憤死は本塁のみ
            }

            default:
                return (0, 0, (0, 0), false, false, Moves(mv));
        }
    }

    private static IReadOnlyList<RunnerMove> Moves(List<RunnerMove>? mv)
        => mv ?? (IReadOnlyList<RunnerMove>)System.Array.Empty<RunnerMove>();

    // 進塁判断の成功確率。スタート・回すか自重かの的確さは「走塁判断(Baserunning)」で決まる（設計書02 §4.1）。
    // ※スプリント自体は走力だが、進塁の可否判断はこのパラメータ。
    private static double P(double baseProb, int baserunning, BaserunningCoefficients coeff)
        => MathUtil.Clamp(baseProb + coeff.SpeedSlope * (baserunning - 50), 0.02, 0.98);

    private static (int, int) ApplyWalk(BaseState bases, Player batter, Player? r1, Player? r2, Player? r3,
        List<RunnerMove>? mv)
    {
        var runs = 0;
        if (r1 is not null)
        {
            if (r2 is not null)
            {
                if (r3 is not null)
                {
                    runs++;         // 満塁押し出し
                    mv?.Add(new RunnerMove(r3, 3, 4, false));
                }
                bases.Third = r2;
                mv?.Add(new RunnerMove(r2, 2, 3, false));
            }
            bases.Second = r1;
            mv?.Add(new RunnerMove(r1, 1, 2, false));
        }
        bases.First = batter;
        mv?.Add(new RunnerMove(batter, 0, 1, false));
        return (runs, 0);
    }

    /// <summary>本塁突入の裁定結果（設計書12 §3, F2）。</summary>
    private enum HomeVerdict { Scored, Held, OutAtHome }

    private static (int Runs, int ExtraOuts, int HomeOuts, int ThirdOuts) ApplyRunnerAdvance(
        BaseState bases, Player batter, Player? r1, Player? r2, Player? r3, int currentOuts,
        BaserunningCoefficients coeff, IRandomSource rng, bool isDouble, List<RunnerMove>? mv, HomePlayContext? home)
    {
        bases.Clear();
        var runs = 0;
        var extraOuts = 0;
        var homeOuts = 0;   // 本塁クロスプレー憤死（統計参考値。extraOuts の内数）
        var thirdOuts = 0;  // 単打の一塁→三塁レース憤死（Issue #89。extraOuts の内数）
        // 1プレーで送球先は1箇所＝アウトは最大1（1球は2箇所へ投げられない）。本塁・三塁で共有する（#89）。
        var outThrowUsed = false;

        // 打者の到達塁（単打/野選=1塁、二塁打=2塁）。先行走者から順にこの塁を明け渡す義務があるかを決める。
        var batterBase = isDouble ? 2 : 1;
        // フォース進塁（#87）: 打者の到達塁とその先の塁が連続して埋まっている走者は自重できない
        // （＝バックアップの塁が無い）。二塁打の一塁走者は打者が二塁に入るだけで一塁は空くため対象外。
        var r2Forced = batterBase == 2 ? r2 is not null : r1 is not null && r2 is not null;
        var r3Forced = r2Forced && r3 is not null;

        // 走者の行き先を塁ごとに保持（#87: newSecond/newThird の2変数上書きで走者が消える構造を廃止）。
        Player? third = null;
        Player? second = null;
        Player? first = null;

        // 本塁突入の裁定。home=null は従来の確率テーブル（決定論・乱数消費順を保存）。
        // tableUnconditional=true は「テーブル時は無条件生還」（三塁走者・二塁打の二塁走者）。
        // CloseCall（判定オーバーレイ, Issue #59）は物理レース（home!=null）で決着した場合のみ算出する
        // 純計算（rng非消費）で、乱数消費順・結果には一切影響しない。
        (HomeVerdict Verdict, bool CloseCall) TryHome(Player runner, int fromBase, double tableProb, bool tableUnconditional)
        {
            if (currentOuts + extraOuts >= 3) return (HomeVerdict.Held, false); // 既に3アウト＝以降は進めない
            if (home is not { } h)
                return (tableUnconditional || MathUtil.Chance(P(tableProb, runner.Baserunning, coeff), rng)
                    ? HomeVerdict.Scored : HomeVerdict.Held, false);

            // F2: 送り判定（勝てる時だけ送る）→ 時間の勝負（外野中継 vs 走者）。
            var prob = HomePlayResolver.SuccessProbability(runner, fromBase, h.Situation, h.Field, coeff);
            if (!HomeSendDecision.ShouldSend(prob, currentOuts + extraOuts, h.Aggression, h.Tactics))
                return (HomeVerdict.Held, false); // 自重（RNG消費なし）
            var margin = HomePlayResolver.Margin(runner, fromBase, h.Situation, h.Field, coeff);
            var closeCall = Math.Abs(margin) < coeff.CloseCallMarginSeconds;
            var res = HomePlayResolver.Resolve(runner, fromBase, h.Situation, h.Field, coeff, rng);
            if (res == HomePlayResult.Safe || outThrowUsed) return (HomeVerdict.Scored, closeCall);
            outThrowUsed = true;
            return (HomeVerdict.OutAtHome, closeCall);
        }

        // 三塁走者（先行）: テーブル時は無条件生還。フォース時（満塁）は自重不可＝RNGを消費せず確定生還
        // （凡打側の r3Forced と同じ流儀。物理レース化は #89 で扱う）。
        if (r3 is not null)
        {
            if (r3Forced)
            {
                runs++;
                mv?.Add(new RunnerMove(r3, 3, 4, false));
            }
            else
            {
                var (verdict3, closeCall3) = TryHome(r3, 3, 0.0, tableUnconditional: true);
                switch (verdict3)
                {
                    case HomeVerdict.Scored: runs++; mv?.Add(new RunnerMove(r3, 3, 4, false) { CloseCall = closeCall3 }); break;
                    case HomeVerdict.OutAtHome: extraOuts++; homeOuts++; mv?.Add(new RunnerMove(r3, 3, 4, Out: true) { CloseCall = closeCall3 }); break;
                    default: third = r3; break; // 自重＝三塁で止まる（移動なし）
                }
            }
        }

        // 二塁走者: 単打は SecondToHomeOnSingle、二塁打はテーブル時無条件。
        // フォース時（一塁走者あり、または二塁打で打者が二塁を占有）は自重不可＝三塁へ確定進塁。
        // r3Forced により三塁は必ず空いている（上のブロックで確定済み）。
        if (r2 is not null)
        {
            var (verdict2, closeCall2) = TryHome(r2, 2, coeff.SecondToHomeOnSingle, tableUnconditional: isDouble);
            switch (verdict2)
            {
                case HomeVerdict.Scored: runs++; mv?.Add(new RunnerMove(r2, 2, 4, false) { CloseCall = closeCall2 }); break;
                case HomeVerdict.OutAtHome: extraOuts++; homeOuts++; mv?.Add(new RunnerMove(r2, 2, 4, Out: true) { CloseCall = closeCall2 }); break;
                default:
                    if (r2Forced || third is null) { third = r2; mv?.Add(new RunnerMove(r2, 2, 3, false)); }
                    else second = r2; // 三塁が塞がっていれば（非フォース）二塁に留まる
                    break;
            }
        }

        // 一塁走者: 二塁打なら本塁を狙う(FirstToHomeOnDouble)、単打は三塁狙い（本塁は狙わない・従来テーブル）。
        if (r1 is not null)
        {
            if (isDouble)
            {
                // 二塁打では打者が二塁を占有する＝一塁走者は一塁にも二塁にも戻れない（#88）。
                var (verdict1, closeCall1) = TryHome(r1, 1, coeff.FirstToHomeOnDouble, tableUnconditional: false);
                switch (verdict1)
                {
                    case HomeVerdict.Scored: runs++; mv?.Add(new RunnerMove(r1, 1, 4, false) { CloseCall = closeCall1 }); break;
                    case HomeVerdict.OutAtHome: extraOuts++; homeOuts++; mv?.Add(new RunnerMove(r1, 1, 4, Out: true) { CloseCall = closeCall1 }); break;
                    default:
                        if (third is null) { third = r1; mv?.Add(new RunnerMove(r1, 1, 3, false)); }
                        else if (currentOuts + extraOuts < 3)
                        {
                            // 三塁に先行走者が自重中＝一塁走者は追い越せないため、先行走者を本塁へ押し出して
                            // 三塁を明け渡させる（フォースの延長。#88）。
                            var pushed = third;
                            runs++;
                            PushHeldRunnerHome(mv, pushed, fromBase: ReferenceEquals(pushed, r2) ? 2 : 3);
                            third = r1;
                            mv?.Add(new RunnerMove(r1, 1, 3, false));
                        }
                        else first = r1; // 既に3アウト＝以降の塁状況は結果に影響しない
                        break;
                }
            }
            else if (home is { } h3 && third is null)
            {
                // 単打の一塁→三塁を物理レース化（Issue #89）。打球の深さ・方向（外野処理点）と外野の肩から
                // 三塁送球所要を組み、走者(走力＋走塁判断)との秒勝負で解く。三塁が塞がっていれば二塁止まり。
                // 送り判定で自重した場合は本塁と同じ流儀で RNG を消費しない（決定論・消費順は home!=null 経路の慣習）。
                var third3 = HomePlayResolver.ThirdParams(h3.Field, coeff);
                var prob = HomePlayResolver.SuccessProbability(r1, 1, h3.Situation, h3.Field, coeff, third3);
                if (!HomeSendDecision.ShouldSendThird(prob, currentOuts + extraOuts, h3.Aggression, h3.Tactics))
                {
                    second = r1; // 自重＝二塁で止まる（RNG非消費）
                    mv?.Add(new RunnerMove(r1, 1, 2, false));
                }
                else if (outThrowUsed)
                {
                    third = r1; // 送球は既に他塁で使用済み＝三塁は無血進塁（RNG非消費）
                    mv?.Add(new RunnerMove(r1, 1, 3, false));
                }
                else
                {
                    var margin = HomePlayResolver.Margin(r1, 1, h3.Situation, h3.Field, coeff, third3);
                    var closeCall = Math.Abs(margin) < coeff.CloseCallMarginSeconds;
                    if (HomePlayResolver.ResolveSafe(r1, 1, h3.Situation, h3.Field, coeff, third3, rng))
                    {
                        third = r1;
                        mv?.Add(new RunnerMove(r1, 1, 3, false) { CloseCall = closeCall });
                    }
                    else
                    {
                        extraOuts++; thirdOuts++; outThrowUsed = true; // 三塁で刺殺（走者はアウト＝塁上に残らない）
                        mv?.Add(new RunnerMove(r1, 1, 3, Out: true) { CloseCall = closeCall });
                    }
                }
            }
            else if (home is not null)
            {
                // home!=null かつ三塁が先行走者で塞がっている＝一塁走者は追い越せず二塁止まり（送球なし＝RNG非消費）。
                second = r1;
                mv?.Add(new RunnerMove(r1, 1, 2, false));
            }
            else
            {
                // 従来テーブル（home==null: 単体テスト・純テーブル経路）。決定論・乱数消費順を保存する。
                // Chance() の評価順は変更前と同一に保つ（third の空き判定を先に短絡させない＝RNG消費順を保存）。
                if (MathUtil.Chance(P(coeff.FirstToThirdOnSingle, r1.Baserunning, coeff), rng) && third is null)
                {
                    third = r1;
                    mv?.Add(new RunnerMove(r1, 1, 3, false));
                }
                else
                {
                    second = r1;
                    mv?.Add(new RunnerMove(r1, 1, 2, false));
                }
            }
        }

        // 各走者は互いに異なる塁へ割り当て済み（フォース済みの先行走者は必ず塁を明け渡す）ため、
        // 打者の占有と衝突しない。
        if (third is not null) bases.Third = third;
        if (second is not null) bases.Second = second;
        if (first is not null) bases.First = first;
        if (isDouble) bases.Second = batter; else bases.First = batter;
        mv?.Add(new RunnerMove(batter, 0, isDouble ? 2 : 1, false));

        return (runs, extraOuts, homeOuts, thirdOuts);
    }

    /// <summary>三塁で自重していた先行走者を、後続走者に追い越されるため本塁へ押し出す（#88）。
    /// 既にその走者の三塁到達move（FromBase→3）が記録済みなら、それを直接本塁行きへ書き換えて単一の
    /// moveにする（2段階の見た目上の移動を避ける）。move未記録（三塁で静止していた走者）なら新規追加する。</summary>
    private static void PushHeldRunnerHome(List<RunnerMove>? mv, Player runner, int fromBase)
    {
        if (mv is null) return;
        for (var i = mv.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(mv[i].Runner, runner) && mv[i].ToBase == 3)
            {
                mv[i] = mv[i] with { ToBase = 4 };
                return;
            }
        }
        mv.Add(new RunnerMove(runner, fromBase, 4, false));
    }

    /// <summary>失策の連鎖（design-14 P1-6）: 悪送球1本で塁上の走者全員＋打者走者が1つ多く進む、という単一事象として
    /// 一括モデル化する（個別確率ではない）。<paramref name="coeff"/>.ErrorExtraAdvanceProb &gt; 0 のときのみ呼ばれる。</summary>
    private static (int Runs, bool Occurred) ApplyErrorExtraAdvance(
        BaseState bases, Player batter, int currentOuts, int extraOuts,
        BaserunningCoefficients coeff, IRandomSource rng, List<RunnerMove>? mv)
    {
        if (currentOuts + extraOuts >= 3) return (0, false);
        if (!MathUtil.Chance(coeff.ErrorExtraAdvanceProb, rng)) return (0, false);

        var runs = 0;
        var third = bases.Third;
        var second = bases.Second;
        var first = bases.First; // ReachedOnError では常に打者走者。

        if (third is not null)
        {
            runs++;
            mv?.Add(new RunnerMove(third, 3, 4, false));
            bases.Third = null;
        }
        if (second is not null)
        {
            mv?.Add(new RunnerMove(second, 2, 3, false));
            bases.Third = second;
            bases.Second = null;
        }
        if (first is not null && bases.Second is null)
        {
            mv?.Add(new RunnerMove(first, 1, 2, false));
            bases.Second = first;
            bases.First = null;
        }
        return (runs, true);
    }

    private static (int, int, int, bool) ApplyInPlayOut(
        BaseState bases, Player batter, Player? r1, Player? r2, Player? r3, int currentOuts,
        BaserunningCoefficients coeff, IRandomSource rng, List<RunnerMove>? mv, HomePlayContext? home = null,
        StartType r1Start = StartType.Normal)
    {
        if (currentOuts >= 2) return (0, 0, 0, false); // この凡打が3アウト目。進塁なし。

        // フォース進塁（2026-07-20, OPEN-QUESTIONS 2026-07-19項）: ゴロ凡打では打者走者が一塁へ向かうため、
        // 後方の占有で押し出される走者（一塁走者=常時 / 二塁走者=一塁走者あり / 三塁走者=満塁）は塁を
        // 明け渡さざるを得ない。守備が確実なアウト（一塁 or 併殺）を選んだ時点で押し出された走者の次塁は
        // 空く＝確定進塁。ProductiveOutAdvanceProb は非フォースの進塁打（二塁→三塁の右方向ゴロ等）専用。
        // フライ（タッグアップ＝時計が別物）と home 無し（従来互換・単体テスト経路）はフォース対象外。
        // フォース判定は打席開始時の占有で確定させる（併殺/野選で r1 が消えても後続の押し出しは起きていた）。
        var isGrounder = home is { IsFly: false };
        var r1Forced = isGrounder && r1 is not null;
        var r2Forced = isGrounder && r1 is not null && r2 is not null;
        var r3Forced = isGrounder && r1 is not null && r2 is not null && r3 is not null;

        var extraOuts = 0;
        var homeOuts = 0;
        var batterSafeOnFc = false;
        // 併殺（1塁走者あり）。
        if (r1 is not null && MathUtil.Chance(
                MathUtil.Clamp(coeff.DoublePlayProb - coeff.SpeedSlope * (r1.Speed - 50), 0.02, 0.5), rng))
        {
            extraOuts = 1;
            mv?.Add(new RunnerMove(r1, 1, 2, Out: true)); // 二塁封殺
            bases.First = null;
            r1 = null;
        }
        // 野選（FC, design-14 P1-1）: DP不成立時のみ判定。既定0では分岐自体に入らず rng を一切消費しない
        // （MathUtil.Chance は確率0でも rng.NextDouble() を1回消費するため、確率式へ0を渡すだけでは
        //  乱数消費順が既存と不一致になる。ガードで分岐そのものをスキップして完全一致を保つ）。
        else if (r1 is not null && coeff.FieldersChoiceProb > 0.0 && MathUtil.Chance(
                MathUtil.Clamp(coeff.FieldersChoiceProb + coeff.SpeedSlope * (batter.Speed - 50), 0.0, 0.95), rng))
        {
            extraOuts = 1;
            batterSafeOnFc = true;
            mv?.Add(new RunnerMove(r1, 1, 2, Out: true)); // 二塁封殺（野選、打者は生存）
            bases.First = batter;
            mv?.Add(new RunnerMove(batter, 0, 1, false));
            r1 = null;
        }

        // ライナー併殺（設計書12 §4, G2）: コンタクト始動(エンドラン等)の一塁走者は打球が空中で捕られた時点で
        // 既に走り出している＝塁へ戻れるかの時間の勝負になる。上の一般併殺判定を生き残った場合のみ追加で効く
        // （同一走者への二重計上を避ける）。フライ/ライナー以外（ゴロ）は対象外。
        if (r1 is not null && r1Start == StartType.Contact && home is { IsFly: true } liner)
        {
            var catchToFirstM = (liner.Field.FirstBase - liner.Situation.BallFieldedPoint).Length;
            if (DoubledOffResolver.Resolve(
                    r1, liner.Situation.BallFieldedAtSeconds, catchToFirstM, liner.Situation.OutfielderThrowSpeedMps,
                    coeff, rng, liner.Situation.OutfielderFielding) == DoubledOffResult.DoubledOff)
            {
                extraOuts++;
                mv?.Add(new RunnerMove(r1, 1, 1, Out: true)); // 一塁へ戻れず憤死
                bases.First = null;
                r1 = null;
            }
        }

        // 打者自身がこの打席でアウトになるか。野選（FC）成立時は打者は生存するため0（design-14 P1-1）。
        var battersOwnOut = batterSafeOnFc ? 0 : 1;
        if (currentOuts + battersOwnOut + extraOuts >= 3) return (0, extraOuts, homeOuts, batterSafeOnFc); // イニング終了。

        var runs = 0;
        // 3塁走者の生還。内野ゴロ(併殺なし)は守備深さ駆動の本塁レース（G1, 設計書12 §4/§5）、
        // フライ(犠飛)は捕球時刻起点のタッチアップ物理レース（Issue #90, 設計書12 §3.5）、
        // home無し（単体テスト・純テーブル経路）は従来の SacFlyScoreProb テーブル。
        if (r3 is not null)
        {
            // フォースの三塁走者（満塁ゴロ）: 自重できない（後続に押し出される）。
            // 内野前進×併殺なしは本塁封殺の勝負（送り判定なしで必ず走る）、それ以外は守備が
            // 一塁/併殺を選んだ＝本塁は空く＝確定生還。
            if (r3Forced && home is { IsFly: false } hforce && extraOuts == 0
                && hforce.InfieldDepth == Tactics.DefenseDepth.In)
            {
                var delay = coeff.HomeGrounderStartDelaySeconds;
                var forceHomeMargin = HomePlayResolver.Margin(r3, 3, hforce.Situation, hforce.Field, coeff, delay);
                var forceHomeCloseCall = Math.Abs(forceHomeMargin) < coeff.CloseCallMarginSeconds;
                if (HomePlayResolver.Resolve(r3, 3, hforce.Situation, hforce.Field, coeff, rng, delay) == HomePlayResult.Safe)
                {
                    runs++;
                    mv?.Add(new RunnerMove(r3, 3, 4, false) { CloseCall = forceHomeCloseCall });
                }
                else
                {
                    extraOuts++; homeOuts++; // 本塁封殺（フォースアウト）
                    mv?.Add(new RunnerMove(r3, 3, 4, Out: true) { CloseCall = forceHomeCloseCall });
                }
                bases.Third = null;
                r3 = null;
            }
            else if (r3Forced)
            {
                runs++; // 守備は一塁/併殺を選択＝フォースの三塁走者は悠々生還。
                mv?.Add(new RunnerMove(r3, 3, 4, false));
                bases.Third = null;
                r3 = null;
            }
            // 深さが本塁プレーの有無を決める（G1）: 後退=献上して還す／前進=本塁で勝負／通常=従来テーブル。
            else if (home is { IsFly: false } h && extraOuts == 0 && h.InfieldDepth == Tactics.DefenseDepth.Deep)
            {
                runs++; // 内野後退＝献上。三塁走者は悠々生還。
                mv?.Add(new RunnerMove(r3, 3, 4, false));
                bases.Third = null;
                r3 = null;
            }
            else if (home is { IsFly: false } hi && extraOuts == 0 && hi.InfieldDepth == Tactics.DefenseDepth.In)
            {
                // 内野前進＝本塁で時間の勝負。ゴロ走者は打球を読んでから走る分だけ遅い（delay）。
                // 際どければ自重（三塁残留＝無得点）、行けば生還 or 憤死。
                var delay = coeff.HomeGrounderStartDelaySeconds;
                var prob = HomePlayResolver.SuccessProbability(r3, 3, hi.Situation, hi.Field, coeff, delay);
                if (HomeSendDecision.ShouldSend(prob, currentOuts, hi.Aggression, hi.Tactics))
                {
                    var backHomeMargin = HomePlayResolver.Margin(r3, 3, hi.Situation, hi.Field, coeff, delay);
                    var backHomeCloseCall = Math.Abs(backHomeMargin) < coeff.CloseCallMarginSeconds;
                    if (HomePlayResolver.Resolve(r3, 3, hi.Situation, hi.Field, coeff, rng, delay) == HomePlayResult.Safe)
                    {
                        runs++;
                        mv?.Add(new RunnerMove(r3, 3, 4, false) { CloseCall = backHomeCloseCall });
                    }
                    else
                    {
                        extraOuts++; homeOuts++; // バックホーム憤死（本塁で刺殺）
                        mv?.Add(new RunnerMove(r3, 3, 4, Out: true) { CloseCall = backHomeCloseCall });
                    }
                    bases.Third = null;
                    r3 = null;
                }
                // 自重は三塁に残る（bases.Third は r3 のまま）。
            }
            // 犠飛のタッチアップ（Issue #90, 設計書12 §3.5）: フライ捕球の実試合経路（home!=null・IsFly）は
            // 定数テーブルでなく物理レース。走者は捕球時刻にスタート（二次リードなし＝全塁間を走る）、深い外野
            // フライは送球が長く生還、浅いフライは自重。送り判定→秒の勝負で生還 or 本塁憤死（本塁と同じ流儀）。
            else if (home is { IsFly: true } hfly && extraOuts == 0)
            {
                var tagUp = HomePlayResolver.TagUpHomeParams(coeff);
                var prob = HomePlayResolver.TagUpSuccessProbability(r3, 3, hfly.Situation, hfly.Field, coeff, tagUp);
                if (HomeSendDecision.ShouldSend(prob, currentOuts, hfly.Aggression, hfly.Tactics))
                {
                    var tagMargin = HomePlayResolver.TagUpMargin(r3, 3, hfly.Situation, hfly.Field, coeff, tagUp);
                    var tagCloseCall = Math.Abs(tagMargin) < coeff.CloseCallMarginSeconds;
                    if (HomePlayResolver.TagUpResolveSafe(r3, 3, hfly.Situation, hfly.Field, coeff, tagUp, rng))
                    {
                        runs++;
                        mv?.Add(new RunnerMove(r3, 3, 4, false) { CloseCall = tagCloseCall });
                    }
                    else
                    {
                        extraOuts++; homeOuts++; // タッチアップ失敗＝本塁での走塁死
                        mv?.Add(new RunnerMove(r3, 3, 4, Out: true) { CloseCall = tagCloseCall });
                    }
                    bases.Third = null;
                    r3 = null;
                }
                // 自重（浅いフライ）は三塁に残る（RNG非消費＝本塁レースの流儀, 不変条件#2）。
            }
            else if (MathUtil.Chance(coeff.SacFlyScoreProb, rng)) // home無し＝従来テーブル（単体テスト・純テーブル経路）
            {
                runs++;
                mv?.Add(new RunnerMove(r3, 3, 4, false));
                bases.Third = null;
                r3 = null;
            }
        }

        if (currentOuts + battersOwnOut + extraOuts >= 3) return (runs, extraOuts, homeOuts, batterSafeOnFc); // 本塁憤死で3アウト＝以降の進塁なし。

        // 進塁打で走者が1つ進む（フォースは確定進塁・rng非消費。非フォースのみ従来の確率判定）。
        if (r2 is not null && bases.Third is null && (r2Forced || MathUtil.Chance(coeff.ProductiveOutAdvanceProb, rng)))
        {
            mv?.Add(new RunnerMove(r2, 2, 3, false));
            bases.Third = r2;
            bases.Second = null;
            r2 = null;
        }
        if (r1 is not null && bases.Second is null && (r1Forced || MathUtil.Chance(coeff.ProductiveOutAdvanceProb, rng)))
        {
            mv?.Add(new RunnerMove(r1, 1, 2, false));
            bases.Second = r1;
            bases.First = null;
        }

        return (runs, extraOuts, homeOuts, batterSafeOnFc);
    }
}
