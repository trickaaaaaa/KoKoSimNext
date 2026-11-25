using System;
using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Game;

/// <summary>
/// チームの結束の係数（設計書23 §3・issue #934。YAML駆動 data/coefficients.yaml cohesion:）。
/// 結束 U（0〜100・50がふつう）＝顔ぶれの土台 U0 ＋出来事による上下 ΔU。効き先は精神面だけ
/// （動揺の起きにくさ・伝令の効き）で、能力そのものには効かせない。U=50 で効き先はすべて恒等。
/// </summary>
public sealed record CohesionCoefficients
{
    // ---- U0（顔ぶれの土台）。OPEN-QUESTIONS Q54 決着（2026-11-14）: 各項を「ふつうの部」の値からの差で測る形にし、
    //      顔ぶれだけで U0≥70 の部が上位約15%・U0<30 の部が下位約15% になるよう校正した（AI校の母集団で実測）。
    //      基準値（*Neutral）は全国の AI校のベンチ入りメンバーの平均＝ふつうの部で各項が0、U0≈50。

    /// <summary>U0: 主将の統率力（統率傾向×精神力/100）が基準を上回る1あたりの上乗せ b1（下回れば減点）。主将不在は0。</summary>
    public double CaptainPowerWeight { get; init; } = 0.9;
    /// <summary>U0: 主将の統率力の基準（ふつうの部の主将）。</summary>
    public double CaptainPowerNeutral { get; init; } = 49.0;
    /// <summary>U0: 「引っ張る」選手（統率傾向がこの値以上）の閾値（design-23 §3.2 「①が65以上」）。</summary>
    public int LeaderThreshold { get; init; } = 65;
    /// <summary>U0: 引っ張る選手の割合（ベンチ入りに占める）が基準を上回る分に掛ける上乗せ b2（下回れば減点）。</summary>
    public double LeaderShareWeight { get; init; } = 80.0;
    /// <summary>U0: 引っ張る選手の割合の基準（ふつうの部）。</summary>
    public double LeaderShareNeutral { get; init; } = 0.267;
    /// <summary>U0: 目立ちたがり（④&lt;50）の割合が基準を上回る分に掛ける減点 b3（下回れば上乗せ）。</summary>
    public double SelfishShareWeight { get; init; } = 70.0;
    /// <summary>U0: 目立ちたがりの割合の基準（ふつうの部）。</summary>
    public double SelfishShareNeutral { get; init; } = 0.385;
    /// <summary>U0: 一匹狼（①↓②↓③↓④目立ち）の割合が基準を上回る分に掛ける減点 b4（下回れば上乗せ）。</summary>
    public double LoneWolfShareWeight { get; init; } = 120.0;
    /// <summary>U0: 一匹狼の割合の基準（ふつうの部）。</summary>
    public double LoneWolfShareNeutral { get; init; } = 0.064;

    /// <summary>ΔU: 夏合宿（通常）の上乗せ（design-04 §4「結束↑」）。</summary>
    public double SummerCampDelta { get; init; } = 6.0;
    /// <summary>ΔU: 冬合宿（強化）の上乗せ（「結束↑↑」）。</summary>
    public double WinterCampDelta { get; init; } = 10.0;

    // ---- issue #1599: 選手間イベントの振れ幅を対応ごとに持つ（オーナー決定 2026-11-25・#945 の一律 +3/−5 を置き換え）----
    // 叱る＝大きく振れる・話を聞く＝小さく安全・主将に任せる＝主将の統率力次第（強い主将なら上振れ大・弱い/不在なら下振れ大）。
    // 式・係数は EventChoiceResolver.CohesionDelta。

    /// <summary>ΔU: 叱って好転したとき（大きく振れる対応の上振れ）。</summary>
    public double ScoldImproveDelta { get; init; } = 5.0;
    /// <summary>ΔU: 叱って悪化したとき（大きく振れる対応の下振れ）。</summary>
    public double ScoldWorsenDelta { get; init; } = -7.0;
    /// <summary>ΔU: 話を聞いて好転したとき（小さく安全な対応の上振れ）。</summary>
    public double ListenImproveDelta { get; init; } = 2.0;
    /// <summary>ΔU: 話を聞いて悪化したとき（小さく安全な対応の下振れ）。</summary>
    public double ListenWorsenDelta { get; init; } = -2.0;
    /// <summary>ΔU: 主将に任せて好転したときの基準（主将の統率力が <see cref="CaptainPowerNeutral"/> のとき）。</summary>
    public double CaptainImproveDeltaBase { get; init; } = 4.0;
    /// <summary>ΔU: 主将に任せたときの好転側が d主将（−1〜+1）1あたりで動く量（強い主将ほど上振れが大きい）。</summary>
    public double CaptainImproveDeltaPerPower { get; init; } = 3.0;
    /// <summary>ΔU: 主将に任せて悪化したときの基準。</summary>
    public double CaptainWorsenDeltaBase { get; init; } = -5.0;
    /// <summary>ΔU: 主将に任せたときの悪化側が d主将 1あたりで動く量（弱い/不在の主将ほど下振れが大きい）。</summary>
    public double CaptainWorsenDeltaPerPower { get; init; } = 3.0;

    // ---- 選択肢つき選手間イベントの好転の確率（issue #945・設計書23 §3.3。式は EventChoiceResolver）----

    /// <summary>選択肢つきイベントの好転の確率の下限（どの対応でも1割は好転する）。</summary>
    public double ChoiceProbMin { get; init; } = 0.10;

    /// <summary>好転の確率の上限（どの対応でも1割は悪化する）。</summary>
    public double ChoiceProbMax { get; init; } = 0.90;

    /// <summary>全対応共通: 当事者の勤勉さ d（(軸−50)/50）に掛ける重み（コツコツ型は立ち直りやすい）。</summary>
    public double ChoiceDiligenceWeight { get; init; } = 0.10;

    /// <summary>厳しく叱る: 基準（当事者の性格が中立のときの好転率）。</summary>
    public double ScoldBase { get; init; } = 0.50;

    /// <summary>厳しく叱る: 素直さ d の重み（素直なら効く・頑固なら反発）。</summary>
    public double ScoldReceptivenessWeight { get; init; } = 0.35;

    /// <summary>
    /// 話を聞く: 基準。issue #1599 で 0.55→0.52 に調整（素直さ・姿勢の重みを強めた分、平均的な部員の好転率を
    /// 叱る（0.50）に寄せ直した。素直さ・姿勢の両方が頑固/目立ちたがり側に寄った当事者で好転7〜8割、
    /// 逆に寄った当事者で2〜3割に収まるよう校正）。
    /// </summary>
    public double ListenBase { get; init; } = 0.52;

    /// <summary>話を聞く: 素直さ d の重み（負＝頑固な当事者ほど効く）。issue #1599 で -0.15→-0.18 に強化。</summary>
    public double ListenReceptivenessWeight { get; init; } = -0.18;

    /// <summary>話を聞く: 試合での姿勢 d の重み（負＝目立ちたがりほど効く＝聞いてもらいたい）。issue #1599 で -0.10→-0.16 に強化。</summary>
    public double ListenSelflessnessWeight { get; init; } = -0.16;

    /// <summary>
    /// 主将に任せる: 基準（主将の統率力が <see cref="CaptainPowerNeutral"/> で当事者の統率傾向が中立のとき）。
    /// issue #1599 で 0.45→0.50 に調整（他の2対応と同じ「中立50%」に揃え、強い主将×引っ張らない当事者＝7〜8割、
    /// 弱い主将×引っ張る当事者＝2〜3割に収まるよう校正）。
    /// </summary>
    public double CaptainBase { get; init; } = 0.50;

    /// <summary>主将に任せる: 主将の統率力の d主将（−1〜+1）の重み。主将不在は d主将=−1。issue #1599 で 0.40→0.26 に調整。</summary>
    public double CaptainPowerSlope { get; init; } = 0.26;

    /// <summary>主将に任せる: d主将＝(統率力−captain_power_neutral)/この幅。</summary>
    public double ChoiceCaptainPowerScale { get; init; } = 25.0;

    /// <summary>主将に任せる: 当事者の統率傾向 d の重み（負＝「引っ張る」当事者は同輩に任されるのを嫌う）。issue #1599 で -0.15→-0.11 に調整。</summary>
    public double CaptainSubjectLeadershipWeight { get; init; } = -0.11;

    /// <summary>ΔU が週ごとに0へ戻る割合（10%）。</summary>
    public double WeeklyDecay { get; init; } = 0.10;

    /// <summary>効き先: U がこの値以上なら動揺の発生閾値（連続出塁数）+1。</summary>
    public double HighThreshold { get; init; } = 70.0;
    /// <summary>効き先: U がこの値未満なら動揺の発生閾値 −1。</summary>
    public double LowThreshold { get; init; } = 30.0;
    /// <summary>効き先: 伝令の効き 1 + c1×(U−50)/50 の c1（U=80 で+6%）。</summary>
    public double MessengerPerCohesion { get; init; } = 0.10;

    // ---- issue #1601: チーム状態の効き先を足す（オーナー決定 2026-11-25） ----

    /// <summary>
    /// 効き先②: 守備側がエラーした後、その回の間だけ失策の起きやすさに掛ける倍率の上限／下限
    /// （<see cref="Cohesion.ErrorStreakMultiplier"/> の 1 + k×(50−U)/50 を ±ErrorStreakCap でクランプする側の値）。
    /// U=<see cref="ErrorStreakFloorU"/> で丁度この値に達する（オーナー決定: U=15 で ×1.5 まで）。
    /// </summary>
    public double ErrorStreakCap { get; init; } = 1.5;

    /// <summary>効き先②: 上の倍率が <see cref="ErrorStreakCap"/> に達する U（これ以下は頭打ち。対称に 100−この値 以上は底打ち）。</summary>
    public double ErrorStreakFloorU { get; init; } = 15.0;

    /// <summary>
    /// 効き先④: 練習の伸びに掛ける倍率 1 + k×(U−50)/50 の k（自校だけ）。
    /// オーナー決定（U=85 で +5%）から逆算: k = 0.05 × 50 / 35。
    /// </summary>
    public double TrainingPerCohesion { get; init; } = 0.05 * 50.0 / 35.0;

    /// <summary>
    /// 効き先⑤: 選択肢つき選手間イベント（kind: Choice）の抽選の重みに掛ける倍率の上限（自校だけ・悪いほど起きやすい）。
    /// U=<see cref="EventWeightFloorU"/> で丁度この値に達する（オーナー決定: U=15 で ×2 で頭打ち）。
    /// U≥50（まあまあ以上）は恒等＝良いチームがさらに得をする設計ではない。
    /// </summary>
    public double EventWeightCap { get; init; } = 2.0;

    /// <summary>効き先⑤: 上の倍率が <see cref="EventWeightCap"/> に達する U（これ以下は頭打ち）。</summary>
    public double EventWeightFloorU { get; init; } = 15.0;
}

/// <summary>
/// チームの結束（設計書23 §3）。純関数・乱数不使用。「チームの勢い」（#863・試合の結果で上がる）とは別物で、
/// 結束は人の顔ぶれと出来事で決まる＝二重に効かせない（勢いは打撃・制球の倍率、結束は動揺と伝令だけ）。
/// </summary>
public static class Cohesion
{
    public const double Neutral = 50.0;

    /// <summary>
    /// 顔ぶれの土台 U0（0〜100）。<paramref name="members"/>＝ベンチ入りの部員（試合では打順＋控え）。
    /// 主将の統率力は design-09 §8 の 統率傾向×精神力/100。
    /// <code>
    /// U0 = 50 + b1×(主将の統率力 − 基準)
    ///         + b2×(引っ張る選手の割合 − 基準) − b3×(目立ちたがりの割合 − 基準) − b4×(一匹狼の割合 − 基準)
    /// </code>
    /// 割合の分母はベンチ入り全員。性格を持たない手組みの選手（<see cref="Personality.Normal"/>）は顔ぶれに数えない＝
    /// どの項も基準どおりとみなす（主将が手組みなら主将の項も0）。全員が手組みのチームは 50 ちょうど（Q57）。
    /// </summary>
    public static double BaseValue(IReadOnlyCollection<Player> members, Player? captain, CohesionCoefficients c,
        int delta = AbilityScale.Delta)
    {
        if (members.Count == 0) return Neutral;
        var u = Neutral;
        if (captain is not null && captain.Personality != Personality.Normal)
            u += c.CaptainPowerWeight * (Season.CaptainSelector.LeadershipPower(captain, delta) - c.CaptainPowerNeutral);
        double leaders = 0, selfish = 0, loneWolves = 0;
        foreach (var p in members)
        {
            if (p.Personality == Personality.Normal) continue;   // 手組みの無補正の選手は顔ぶれに数えない（Q57）
            leaders += (p.Leadership >= c.LeaderThreshold ? 1 : 0) - c.LeaderShareNeutral;
            selfish += (Personalities.High(p.Selflessness) ? 0 : 1) - c.SelfishShareNeutral;
            loneWolves += (p.Personality == Personality.LoneWolf ? 1 : 0) - c.LoneWolfShareNeutral;
        }
        var n = (double)members.Count;
        u += c.LeaderShareWeight * leaders / n;
        u -= c.SelfishShareWeight * selfish / n;
        u -= c.LoneWolfShareWeight * loneWolves / n;
        return MathUtil.Clamp(u, 0.0, 100.0);
    }

    /// <summary>結束 U ＝ U0 ＋ ΔU（0〜100）。</summary>
    public static double Value(double baseValue, double delta) => MathUtil.Clamp(baseValue + delta, 0.0, 100.0);

    /// <summary>
    /// チームの結束 U（試合と画面の単一ソース・issue #935）。顔ぶれ＝打順＋控え＋DHの先発投手、主将は
    /// <see cref="Team.Captain"/>、上下は <see cref="Team.CohesionDelta"/>。
    /// </summary>
    public static double ForTeam(Team team, CohesionCoefficients c, int delta = AbilityScale.Delta) => ForTeam(team, team.Captain, c, delta);

    /// <summary>主将を差し替えた場合の結束（主将指名画面の「指名したら」の見込み・issue #935）。</summary>
    public static double ForTeam(Team team, Player? captain, CohesionCoefficients c, int delta = AbilityScale.Delta)
    {
        return Value(BaseValue(team.BenchEntry, captain, c, delta), team.CohesionDelta);
    }

    /// <summary>効き先①: 動揺の発生閾値の段（U≥高 で+1、U&lt;低 で−1、それ以外0）。</summary>
    public static int RattledThresholdStep(double u, CohesionCoefficients c)
        => u >= c.HighThreshold ? 1 : u < c.LowThreshold ? -1 : 0;

    /// <summary>効き先②: 伝令の効きの倍率 1 + c1×(U−50)/50（U=50 で1）。</summary>
    public static double MessengerMultiplier(double u, CohesionCoefficients c)
        => 1.0 + c.MessengerPerCohesion * (u - Neutral) / 50.0;

    /// <summary>
    /// issue #1601 ②: 守備側がエラーした後、その回の間だけ失策の起きやすさに掛ける倍率。
    /// 1 + k×(50−U)/50（U=50 で恒等）。k は「U=<see cref="CohesionCoefficients.ErrorStreakFloorU"/> で丁度
    /// <see cref="CohesionCoefficients.ErrorStreakCap"/> に達する」よう逆算し、その上限／下限（対称に 2−Cap）でクランプする
    /// ＝U が低いほど崩れ（頭打ち）、高いほど立て直す（底打ち）。AI校にも U0 で効く（帯が動きうる＝統計回帰で確認）。
    /// </summary>
    public static double ErrorStreakMultiplier(double u, CohesionCoefficients c)
    {
        var span = Math.Max(1e-6, Neutral - c.ErrorStreakFloorU);
        var k = (c.ErrorStreakCap - 1.0) / (span / 50.0);
        var raw = 1.0 + k * (Neutral - u) / 50.0;
        return MathUtil.Clamp(raw, 2.0 - c.ErrorStreakCap, c.ErrorStreakCap);
    }

    /// <summary>
    /// issue #1601 ②: <see cref="ErrorStreakMultiplier"/> を守備係数へ適用する。失策の基準確率5種
    /// （捕球・フライ落球・悪送球・受け手・外野ファンブル）を同じ倍率で伸縮する。mult=1.0 は恒等（同一参照を返す）。
    /// </summary>
    public static FieldingCoefficients ApplyErrorStreak(FieldingCoefficients c, double mult)
        => mult == 1.0 ? c : c with
        {
            ErrorBaseProb = c.ErrorBaseProb * mult,
            FlyErrorBaseProb = c.FlyErrorBaseProb * mult,
            ThrowErrorBaseProb = c.ThrowErrorBaseProb * mult,
            ReceiveErrorBaseProb = c.ReceiveErrorBaseProb * mult,
            OutfieldFumbleBaseProb = c.OutfieldFumbleBaseProb * mult,
        };

    /// <summary>
    /// issue #1601 ④: 練習の伸びに掛ける倍率 1 + k×(U−50)/50（U=50 で恒等・自校だけ）。上限は付けない
    /// （U の定義域 0〜100 で自然に止まる）。施設・道具・勤勉さと同じ「練習効率」の並びに掛ける。
    /// </summary>
    public static double TrainingMultiplier(double u, CohesionCoefficients c)
        => 1.0 + c.TrainingPerCohesion * (u - Neutral) / 50.0;

    /// <summary>
    /// issue #1601 ⑤: 選択肢つき選手間イベント（kind: Choice）の抽選の重みに掛ける倍率（自校だけ・悪いほど起きやすい）。
    /// U≥50 は恒等＝1.0。U が <see cref="CohesionCoefficients.EventWeightFloorU"/> 以下なら
    /// <see cref="CohesionCoefficients.EventWeightCap"/> で頭打ち（悪循環の歯止め。cooldown_weeks は別に保つ）。
    /// </summary>
    public static double EventWeightMultiplier(double u, CohesionCoefficients c)
    {
        if (u >= Neutral) return 1.0;
        var span = Math.Max(1e-6, Neutral - c.EventWeightFloorU);
        var frac = MathUtil.Clamp((Neutral - u) / span, 0.0, 1.0);
        return 1.0 + (c.EventWeightCap - 1.0) * frac;
    }

    // ===== ΔU（自校だけ。AI校は常に0＝Q40） =====

    /// <summary>週ごとの減衰（10%ずつ0へ戻す）。</summary>
    public static double DecayWeek(double delta, CohesionCoefficients c) => delta * (1.0 - c.WeeklyDecay);

    /// <summary>合宿（夏＝通常／冬＝強化）。</summary>
    public static double AfterCamp(double delta, bool winter, CohesionCoefficients c)
        => delta + (winter ? c.WinterCampDelta : c.SummerCampDelta);

    /// <summary>
    /// 合宿を見送る（issue #1482）。週の頭の <see cref="AdvanceWeek"/> は合宿の週に上乗せ済みなので、
    /// 見送ったぶんをここで取り戻す＝「合宿をしなかった週」と同じ値になる。
    /// </summary>
    public static double SkipCamp(double delta, bool winter, CohesionCoefficients c)
        => ClampDelta(delta - (winter ? c.WinterCampDelta : c.SummerCampDelta));

    /// <summary>
    /// 選手間イベントの好転／悪化。<paramref name="eventDelta"/> は対応ごとの振れ幅そのもの
    /// （<see cref="Season.Events.EventChoiceResolver.CohesionDelta"/> が対応・好転/悪化・主将の統率力から求める。issue #1599）。
    /// </summary>
    public static double AfterEvent(double delta, double eventDelta) => delta + eventDelta;

    /// <summary>
    /// 1週ぶん進める（自校の週送りが呼ぶ単一の入口）: まず 10% 戻し、合宿の週なら上乗せする
    /// （夏合宿＝通常・冬合宿＝強化。週は <see cref="Season.SeasonCalendar"/> の合宿週）。
    /// </summary>
    public static double AdvanceWeek(double delta, int week, Season.SeasonCalendar calendar, CohesionCoefficients c)
    {
        var d = DecayWeek(delta, c);
        if (week == calendar.SummerCampWeek) d = AfterCamp(d, winter: false, c);
        else if (week == calendar.WinterCampWeek) d = AfterCamp(d, winter: true, c);
        return ClampDelta(d);
    }

    /// <summary>ΔU が取りうる範囲（U が 0〜100 を外れない幅）に丸める。</summary>
    public static double ClampDelta(double delta) => MathUtil.Clamp(delta, -100.0, 100.0);
}
