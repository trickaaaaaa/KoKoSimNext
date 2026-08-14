using System;
using System.Collections.Generic;

namespace KokoSim.Engine.Match.Timeline.Playback;

/// <summary>
/// 結果表示・得点加算を走者の絵の到達に同期させるための時刻計算（issue #438。描画専用の純関数）。
///
/// <see cref="PlaybackPlay.ResAt"/> は「プレーが内部的に決着した時刻」（送球到達・捕球・着地）で、
/// 打者がセーフで出塁するプレーでは走者の到達（レッグの T1）より平均1.5〜3秒早い
/// （実測 2026-08-04: 30試合1682プレー中、安打・失策の952件すべてで先行）。
/// 結果があらかじめ決まっているのは内部の都合であって、表示は絵の到達と同期してほしい——が issue の要求。
///
/// アウト側は逆に「確定の瞬間に出す」が正（フライ捕球=#260、一塁フォースは送球到達=#328。
/// 走者は判定確定後も走り切る絵のまま）ので、遅らせるのは**打者がセーフで生きる結果だけ**。
/// </summary>
public static class PlaybackDisplaySync
{
    /// <summary>本塁（原点）から2m以内なら本塁到達とみなす（丸め誤差吸収。一塁27.43mとは混同しない）。</summary>
    private const double NearHomeSq = 4.0;

    /// <summary>
    /// 打球の無い得点（押し出しの四球・死球）を掲示板へ出すまでの間[s]（issue score-display-before-home-touch）。
    ///
    /// <para>四球・死球はタイムラインを持たない＝<b>本塁到達の時刻がそもそも存在しない</b>ので、
    /// <see cref="RunScoreTimes"/> の「絵の到達で1点ずつ」に乗せられない。結果確定と同時に加算すると
    /// 走者が歩いて還る前に点だけが増えるため、固定の一拍だけ待ってから掲げる。</para>
    ///
    /// <para>物理の再現ではなく<b>演出値</b>（監督決定 2026-08-13・初期値 1.5〜2.0 秒）。
    /// 実測 300試合で押し出しは43点（四球40・死球3）＝この経路を通る得点は全体の 1.4%。</para>
    /// </summary>
    public const double NonBattedScoreDelaySeconds = 1.8;

    /// <summary>
    /// 結果テキスト（「レフト前ヒット」等）を出す時刻。
    /// ・本塁打＝柵越え（<see cref="PlaybackPlay.ResAt"/>＝着地）で確定。ダイヤモンド一周は待たない
    /// ・打者アウト（レッグ終端アウト or 「打」のアウト判定イベント）＝確定の瞬間（ResAt）のまま
    /// ・打者がセーフで生きる結果（安打・失策・野選）＝打者走者の最終レッグ到達（T1）まで待つ
    /// </summary>
    public static double ResultShowTime(PlaybackPlay play)
    {
        if (play is null) return 0;
        var t = play.ResAt;
        foreach (var j in play.Judgements)
        {
            if (j.Kind == JudgementKind.HomeRun) return t;
            // フライ捕球アウトは打者レッグが終端アウトにならない（捕球で確定し、走者は走り切る絵のまま）
            // ため、レッグではなく判定イベントで見分ける（#260）。
            if (j.Kind == JudgementKind.Out && j.Label == "打") return t;
        }
        foreach (var r in play.Runners)
        {
            if (r.Label != "打") continue;
            var leg = FinalMovingLeg(r);
            if (leg is not null && !leg.OutAtEnd && leg.T1 > t) t = leg.T1;
            break;
        }
        return t;
    }

    /// <summary>
    /// 得点を1点ずつ加算する時刻（昇順）。各走者の本塁到達レッグ（To が本塁・終端セーフ）の T1。
    /// 生還数より本塁到達が多い場合（第3アウトと同時のタイムプレー等）は先着から
    /// <paramref name="runsScored"/> 本だけ数える。足りない場合（保険。実測30試合で0件）は
    /// 最後の既知時刻から <paramref name="fallbackStepSeconds"/> 間隔で続ける＝従来の等間隔挙動へ縮退。
    /// </summary>
    public static IReadOnlyList<double> RunScoreTimes(PlaybackPlay play, int runsScored, double fallbackStepSeconds)
    {
        if (play is null || runsScored <= 0) return Array.Empty<double>();
        var arrivals = new List<double>();
        foreach (var r in play.Runners)
        {
            var leg = FinalMovingLeg(r);
            if (leg is null || leg.OutAtEnd) continue;
            var to = leg.To;
            if (to.X * to.X + to.Y * to.Y < NearHomeSq) arrivals.Add(leg.T1);
        }
        arrivals.Sort();
        if (arrivals.Count > runsScored) arrivals.RemoveRange(runsScored, arrivals.Count - runsScored);
        var last = arrivals.Count > 0 ? arrivals[^1] : play.ResAt;
        while (arrivals.Count < runsScored)
        {
            last += fallbackStepSeconds;
            arrivals.Add(last);
        }
        return arrivals;
    }

    /// <summary>
    /// 走者が最後に「実際に動いた」レッグ（待機・定点レッグは To==From で張られるため除外）。
    /// <c>DuelStageRig.RunnerFinal</c>（3D側の到達検出）と同じ 0.5m 閾値。
    /// </summary>
    private static PlaybackRun? FinalMovingLeg(PlaybackRunner r)
    {
        if (r?.Segs is null) return null;
        for (var i = r.Segs.Count - 1; i >= 0; i--)
        {
            var s = r.Segs[i];
            var dx = s.To.X - s.From.X;
            var dy = s.To.Y - s.From.Y;
            if (dx * dx + dy * dy >= 0.25) return s;
        }
        return null;
    }
}
