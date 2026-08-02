using System;
using KokoSim.Engine.Match.Field;

namespace KokoSim.Engine.Season;

/// <summary>
/// 投手役の判定（Issue #366a / #377）。「投手か否か」は選手が覚えている属性ではなく、
/// 守備位置適性から**その都度導出する**もの＝判定式はこのクラスにしか存在しない。
///
/// 以前は生成側（ProspectGenerator）と選手側（DevelopingPlayer）に同じ判定が二重に置かれ、
/// 生成側だけが供給バイアスを加えていた（選手側はバイアス無し）。生成側が答えを
/// <c>DevelopingPlayer.IsPitcher</c> へ書き込んでいたため食い違いは表に出なかったが、
/// 書き込みを撤去した瞬間に校正されていない側が主になり投手供給が半減する
/// （実測 32.88% → 17.28%, 400,000個体）。判定をここへ集約してその再発を断つ。
/// </summary>
public static class PitcherRoleResolver
{
    /// <summary>
    /// 投手適性が「最良の守備位置適性」を供給バイアスぶん上回るか
    /// （<see cref="RosterCoefficients.PitcherAptitudeBias"/>）。
    ///
    /// バイアスはバランス調整のゲタではなく**比較の構造的な歪みの補正**である。左辺は1回ロールした
    /// 投手適性、右辺は8回ロールした守備位置適性の最大値で、同じ分布から引いても後者が高く出る。
    /// 素直に比べると投手はほとんど勝てない（無バイアスの実測は 17.28%）。この加算で投手供給率を
    /// 狙い値へ寄せる（帯校正で決める）。
    ///
    /// 同値（全適性が既定50一律など）は投手側に倒れる＝特例を足さない（オーナー判断 2026-08-02）。
    /// </summary>
    public static bool IsNaturalPitcher(int pitcherAptitude, int bestFieldAptitude, RosterCoefficients c)
        => pitcherAptitude + c.PitcherAptitudeBias > bestFieldAptitude;

    /// <summary><see cref="AptitudeRoller.Roll"/> の出力配列から直接判定する。</summary>
    public static bool IsNaturalPitcher(int[] aptitudes, RosterCoefficients c)
        => IsNaturalPitcher(aptitudes[(int)FieldPosition.Pitcher], AptitudeRoller.BestField(aptitudes), c);

    /// <summary>
    /// 投手寄せ重み w∈[0,1]（能力の連続補間に使う, Issue #174）。判定と違い**バイアスを混ぜない**
    /// ＝明確な野手は w≈0 で打撃が total を保つ（バイアスを w に混ぜると全野手の打撃が下がり帯が動く）。
    /// </summary>
    public static double PitcherWeight(int pitcherAptitude, int bestFieldAptitude, RosterCoefficients c)
        => 1.0 / (1.0 + Math.Exp(-(pitcherAptitude - bestFieldAptitude) / c.PitcherAptitudeWeightSpan));

    /// <summary><see cref="AptitudeRoller.Roll"/> の出力配列から重み w を求める。</summary>
    public static double PitcherWeight(int[] aptitudes, RosterCoefficients c)
        => PitcherWeight(aptitudes[(int)FieldPosition.Pitcher], AptitudeRoller.BestField(aptitudes), c);

    /// <summary>
    /// 「投手としてどれだけ読めるか」の連続量 wRole∈[0,1]（#377）。<see cref="PitcherWeight"/> と違い
    /// **供給バイアスを含む**＝<see cref="IsNaturalPitcher"/> の判定境界でちょうど 0.5 になる。
    ///
    /// 能力の配分（打撃⇔投球）には <see cref="PitcherWeight"/> を、「投手らしさ」で決まる持ち物
    /// （変化球レパートリー・投手経歴・投法の妙）にはこちらを使う。バイアスを能力側の w に混ぜると
    /// 全野手の打撃が下がって帯が大きくずれるので、2つは分けたまま扱うこと。
    /// </summary>
    public static double RoleWeight(int pitcherAptitude, int bestFieldAptitude, RosterCoefficients c)
        => 1.0 / (1.0 + Math.Exp(-(pitcherAptitude + c.PitcherAptitudeBias - bestFieldAptitude)
                                 / c.PitcherAptitudeWeightSpan));

    /// <summary>
    /// この選手を投手として扱うか（#366b）。守備位置適性からの導出であって、選手が覚えている状態ではない。
    /// 旧 <c>DevelopingPlayer.IsPitcher</c> の置き換えで、育成のお任せ配分・チーム編成の投手/野手分割など
    /// 「真偽1つで足りる消費側」はここを呼ぶ。
    ///
    /// 係数は <see cref="RosterCoefficients.Default"/>（YAML と C# 既定が同値なので挙動は同一）。
    /// 係数を持っている呼び出し側は <see cref="IsNaturalPitcher(int,int,RosterCoefficients)"/> を直接使うこと。
    /// </summary>
    public static bool LeansPitcher(DevelopingPlayer p) => LeansPitcher(p, RosterCoefficients.Default);

    /// <inheritdoc cref="LeansPitcher(DevelopingPlayer)"/>
    public static bool LeansPitcher(DevelopingPlayer p, RosterCoefficients c)
        => IsNaturalPitcher(p.Aptitude(FieldPosition.Pitcher), BestFieldAptitude(p), c);

    /// <summary>
    /// 投手を除く8守備位置中の最良適性（投手適性との比較対象, Issue #174）。
    /// 走査は <see cref="AptitudeRoller.AllPositions"/>＝**実在する9守備位置だけ**にする。
    /// <see cref="FieldPosition"/> には打順枠の <see cref="FieldPosition.DesignatedHitter"/> が含まれるが
    /// これは守備位置ではなく適性もロールされない（既定50のまま）。適性配列を添字で素朴に回すと
    /// その50が最良守備適性の下限として効き、投手適性50未満の選手が誰も投手になれなくなる。
    /// </summary>
    public static int BestFieldAptitude(DevelopingPlayer p)
    {
        var best = int.MinValue;
        foreach (var pos in AptitudeRoller.AllPositions)
            if (pos != FieldPosition.Pitcher) best = Math.Max(best, p.Aptitude(pos));
        return best;
    }

    /// <summary>
    /// 導出が指定の役割を返すよう投手適性だけを置き直す（#377）。**役割が既知で適性が無い経路専用**＝
    /// 試合層 <c>Player</c> からの逆投影（<see cref="Nation.ScoutedTeamProfile"/>）とテスト素体でだけ使う。
    /// 実選手の適性は <see cref="AptitudeRoller"/> が振るので、生成経路からは呼ばないこと。
    ///
    /// 置く値は「判定をちょうど跨ぐ最小の変更」にする（既定50一律なら投手43／野手42）。極端値
    /// （99/1）を置くと守備適性を見る他の計算へ波及するため。
    /// </summary>
    public static void SeedAptitudeForRole(DevelopingPlayer p, bool pitcher)
    {
        var bias = (int)Math.Ceiling(RosterCoefficients.Default.PitcherAptitudeBias);
        var bestPos = FieldPosition.Catcher;
        var bestField = int.MinValue;
        foreach (var pos in AptitudeRoller.AllPositions)
        {
            if (pos == FieldPosition.Pitcher || p.Aptitude(pos) <= bestField) continue;
            bestField = p.Aptitude(pos);
            bestPos = pos;
        }

        // 判定は apt + bias > bestField ＝ apt > bestField − bias。
        if (!pitcher && bestField - bias < 1)
        {
            // 守備適性が低すぎると投手適性を下限(1)まで下げても跨げない。守備側を最小限だけ持ち上げる。
            bestField = Math.Min(99, bias + 1);
            p.SetAptitude(bestPos, bestField);
        }
        var threshold = bestField - bias;
        p.SetAptitude(FieldPosition.Pitcher, Math.Clamp(pitcher ? threshold + 1 : threshold, 1, 99));
    }
}
