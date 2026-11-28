using System;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Nation;

namespace KokoSim.Engine.Season;

/// <summary>
/// 投手役の判定（Issue #366a / #377 / #1792）。「投手か否か」は選手が覚えている属性ではなく、
/// **その都度導出する**もの＝判定式はこのクラスにしか存在しない。
///
/// 2026-11-28（issue #1792）: 判定の物差しを守備位置適性から**実際の投手能力**へ差し替えた
/// （<see cref="LeansPitcher(DevelopingPlayer, RosterCoefficients)"/>）。投手総合
/// （<see cref="Nation.TeamStrengthProfile.PitcherComposite"/>）と打撃・守備の総合
/// （<see cref="Nation.TeamStrengthProfile.NonPitcherComposite"/>）を比べ、境目で行ったり来たりしない遊び
/// （ヒステリシス・<see cref="DevelopingPlayer.PitcherRoleLastLeansPitcher"/>）を効かせる。
///
/// **適性ベースの旧判定（<see cref="IsNaturalPitcher"/>・<see cref="PitcherWeight"/>・<see cref="RoleWeight"/>）は
/// 廃止していない**＝生成時に能力配分（投手系↔打撃・守備系の連続補間 w/wRole）を決める材料として
/// <see cref="ProspectGenerator"/> がそのまま使う（オーナー決定: 生まれてくる選手の能力分布は変えない）。
/// 「今この選手は投手寄りか」という在籍中の問いだけが能力ベースへ移った。
///
/// 旧い経緯（Issue #174）: 以前は生成側（ProspectGenerator）と選手側（DevelopingPlayer）に同じ適性判定が
/// 二重に置かれ、生成側だけが供給バイアスを加えていた。生成側が答えを <c>DevelopingPlayer.IsPitcher</c> へ
/// 書き込んでいたため食い違いは表に出なかったが、書き込みを撤去した瞬間に校正されていない側が主になり
/// 投手供給が半減した（実測 32.88% → 17.28%, 400,000個体）。判定を1箇所へ集約してその再発を断つ構造は変えていない。
/// </summary>
public static class PitcherRoleResolver
{
    private static readonly TeamStrengthCoefficients StrengthDefaults = new();
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
    /// この選手を投手として扱うか（#366b・#1792）。能力（投手総合 vs 打撃・守備総合＋ゲタ）からの導出であって、
    /// 選手が覚えている属性ではない。旧 <c>DevelopingPlayer.IsPitcher</c> の置き換えで、育成のお任せ配分・
    /// チーム編成の投手/野手分割など「真偽1つで足りる消費側」はここを呼ぶ。
    ///
    /// 係数は <see cref="RosterCoefficients.Default"/>（YAML と C# 既定が同値なので挙動は同一）。
    /// 係数を持っている呼び出し側は <see cref="LeansPitcher(DevelopingPlayer,RosterCoefficients)"/> を直接使うこと。
    /// </summary>
    public static bool LeansPitcher(DevelopingPlayer p) => LeansPitcher(p, RosterCoefficients.Default);

    /// <summary>
    /// <inheritdoc cref="LeansPitcher(DevelopingPlayer)"/>
    ///
    /// 遊び（ヒステリシス, issue #1792）: 投手総合−打撃守備総合が
    /// [<see cref="RosterCoefficients.PitcherRoleGeta"/>−<see cref="RosterCoefficients.PitcherRoleHysteresisBand"/>,
    /// +帯] の帯に収まっている間は <see cref="DevelopingPlayer.PitcherRoleLastLeansPitcher"/>（前回の判定）を保つ。
    /// 帯の外では能力が指す側へ素直に決まる。初回（null＝新規生成直後・旧セーブ）は遊び無しの素の比較。
    /// 決めた結果は次回の「前回判定」としてここで選手へ書き戻す（副作用。<see cref="DevelopingPlayer.Clone"/> は複製する）。
    /// </summary>
    public static bool LeansPitcher(DevelopingPlayer p, RosterCoefficients c)
    {
        var diff = RoleDiff(p, c);
        var upper = c.PitcherRoleGeta + c.PitcherRoleHysteresisBand;
        var lower = c.PitcherRoleGeta - c.PitcherRoleHysteresisBand;

        bool result;
        if (diff > upper) result = true;
        else if (diff < lower) result = false;
        else result = p.PitcherRoleLastLeansPitcher ?? diff > c.PitcherRoleGeta;

        p.PitcherRoleLastLeansPitcher = result;
        return result;
    }

    /// <summary>
    /// 今キャッシュされている判定をそのまま読む（issue #1792）。<see cref="LeansPitcher(DevelopingPlayer,RosterCoefficients)"/>
    /// と違い**再評価・上書きをしない**＝既に判定済み（<see cref="DevelopingPlayer.PitcherRoleLastLeansPitcher"/> が
    /// non-null）ならその値を信じる。まだ一度も判定していない（null）ときだけ <see cref="LeansPitcher(DevelopingPlayer,RosterCoefficients)"/>
    /// を1回呼んで確定させる（以後はそれを信じる）。
    ///
    /// 使い分け: 「今この選手は投手寄りか」を**決める**側（育成・お任せ配分・チーム編成・転任・スカウト・怪物）は
    /// 能力の変化を追わせるため <see cref="LeansPitcher(DevelopingPlayer,RosterCoefficients)"/> を呼ぶ。
    /// すでに組み上がったチーム（<see cref="Nation.ScoutedTeamProfile.ToDeveloping"/> が投影した個体など）の
    /// 役割を**読むだけ**の側（<see cref="DevelopingPlayer.AverageLevel(int)"/>・<see cref="Nation.TeamStrengthProfile.Compute"/>
    /// の投手/野手の振り分け）はこちらを呼ぶ。でないと、能力が遊びの帯を超えて上書きする側で決めた役割
    /// （例: 苦作で底上げされた投手は実際の投手能力が低いまま＝帯を超えて「野手」へ上書きされる）が、
    /// 同じチームを集計するだけのはずの側で食い違う（issue #1792 実害: AI校の総合が逆算配分の目標から数点ずれた）。
    /// </summary>
    public static bool CurrentRole(DevelopingPlayer p, RosterCoefficients c)
        => p.PitcherRoleLastLeansPitcher ?? LeansPitcher(p, c);

    /// <inheritdoc cref="CurrentRole(DevelopingPlayer,RosterCoefficients)"/>
    public static bool CurrentRole(DevelopingPlayer p) => CurrentRole(p, RosterCoefficients.Default);

    /// <summary>
    /// 投手総合−打撃守備総合（校の強さ補正込み, issue #1792）。<see cref="LeansPitcher(DevelopingPlayer,RosterCoefficients)"/>
    /// が判定に使う値そのもので、<see cref="ProspectGenerator.EnsurePitcherSupply"/> が「最も惜しい1人」を
    /// 選ぶときの順位にも使う（適性ではなく能力で惜しさを測る）。
    ///
    /// 校の強さ補正: 投手総合の一部（球速・スタミナ）は身体系ベースライン
    /// （<see cref="RosterCoefficients.PhysicalCenterFor"/>・talentCenterへの連動が専門配分と傾きが違う）を
    /// 引くため、素の差だけでは弱小校ほど投手寄り・強豪校ほど野手寄りに偏る
    /// （Issue #174「投手供給率は校の強さに連動しない」に抵触）。選手自身の baseline（両総合の平均）が
    /// 基準点からどれだけずれているかで補正し、物差しを一般校に揃える（w による投手寄せ自体は変えない）。
    /// </summary>
    public static double RoleDiff(DevelopingPlayer p, RosterCoefficients c)
    {
        var pitcher = TeamStrengthProfile.PitcherComposite(p, StrengthDefaults);
        var nonPitcher = TeamStrengthProfile.NonPitcherComposite(p, StrengthDefaults);
        var baseline = (pitcher + nonPitcher) / 2.0;
        return pitcher - nonPitcher + c.PitcherRoleStrengthSlope * (baseline - c.PitcherRoleStrengthReference);
    }

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
    /// 導出が指定の役割を返すよう投手適性と「前回判定」を置き直す（#377・#1792）。
    /// **役割が既知で適性が無い経路専用**＝試合層 <c>Player</c> からの逆投影（<see cref="Nation.ScoutedTeamProfile"/>）と
    /// テスト素体でだけ使う。実選手の適性は <see cref="AptitudeRoller"/> が振るので、生成経路からは呼ばないこと。
    ///
    /// 適性の置き方は「判定をちょうど跨ぐ最小の変更」にする（既定50一律なら投手43／野手42）。極端値
    /// （99/1）を置くと守備適性を見る他の計算へ波及するため（この適性自体は #1792 以降の判定には使われない。
    /// <see cref="IsNaturalPitcher"/>・<see cref="PitcherWeight"/>・<see cref="RoleWeight"/> など生成側の重み計算が
    /// 読む材料として残す）。
    ///
    /// #1792 で最終判定が能力ベースへ移ったため、<see cref="DevelopingPlayer.PitcherRoleLastLeansPitcher"/>
    /// （遊びの「前回判定」）を直接 <paramref name="pitcher"/> へ固定する。これにより、この後で能力を
    /// 均一値へ揃えるような検証用の素体（差0＝遊びの帯の中）でも、呼び出し側が指定した役割どおりに
    /// <see cref="LeansPitcher(DevelopingPlayer, RosterCoefficients)"/> が応答する
    /// （<see cref="RosterCoefficients.PitcherRoleHysteresisBand"/> ≥ |<see cref="RosterCoefficients.PitcherRoleGeta"/>| の
    /// 不変条件により、差0は常に遊びの帯の内側＝前回判定が勝つ）。
    /// </summary>
    public static void SeedAptitudeForRole(DevelopingPlayer p, bool pitcher)
    {
        p.PitcherRoleLastLeansPitcher = pitcher;
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
