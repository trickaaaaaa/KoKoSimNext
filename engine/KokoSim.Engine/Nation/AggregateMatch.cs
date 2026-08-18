using KokoSim.Engine.Core;

namespace KokoSim.Engine.Nation;

/// <summary>
/// 集計マッチモデル（設計書05 §1.4 の第3層）。チーム力差から勝敗を直接サンプリングする。
/// ロジスティック尺度は StrengthTeamFactory＋GameEngine の実分布にキャリブレーションする。
///
/// 裏試合3層のキャリブレーション（§1.4 / 11 §5）: フル采配（AiTacticsBrain＝①能力値②ティア③校風）を
/// 入れても勝率は強さ差モデルで表せる。理由は実測（NationTests のHeavyキャリブレーション）で確認済み＝
/// ②ティアは強さに内包、①采配能力の純W/L効果は誤差内、③校風（小技）は強さ優位を数%削るのみで許容帯内。
/// よって W/L 補正項は置かない（難易度補正なし・no-fudge）。校風の個性は試合内の采配活動（盗塁/犠打の頻度,
/// 設計書11 §3・A-2 の TacticsTally）として表れ、勝敗期待値には効かせない。
/// </summary>
public static class AggregateMatch
{
    /// <summary>
    /// 強さ差から A の勝率を出す。差は<b>ティア圧縮（issue #578）を通した能力中心どうし</b>で取る。
    ///
    /// <para>この集計モデルは「フルエンジンで回したら何が起きるか」の近道なので、フルエンジンが見る能力
    /// （＝<see cref="StrengthTeamFactory"/> が生成する中心）と同じ尺度で差を測らないと**裏表が食い違う**。
    /// 生の <see cref="School.Strength"/> 差のまま置くと、圧縮でフル側の勝率だけが 0.92→0.8 台へ寝て、
    /// 集計で消化した裏試合の方が番狂わせが起きない世界になる（Heavy の
    /// <c>AggregateModel_MatchesGameEngineWinRate</c> がこれを検出する）。</para>
    ///
    /// <para><paramref name="roster"/> 省略時は C#既定＝実ゲームと同じ圧縮。2026-08-19 以降は圧縮が恒等（slope 1.0）で
    /// 差は raw のまま、寝かせる役は表示→物理の効き（設計書21 <c>ability_effect</c>）が担う＝<c>AggregateScale</c> を
    /// フルシムの実測（差10→≈80%）へ 7.2 に再校正した。</para>
    /// </summary>
    public static double WinProbability(double strengthA, double strengthB, NationCoefficients c,
        Season.RosterCoefficients? roster = null)
    {
        var rc = roster ?? Season.RosterCoefficients.Default;
        return MathUtil.Logistic((rc.CompressStrength(strengthA) - rc.CompressStrength(strengthB)) / c.AggregateScale);
    }

    /// <summary>A が勝つか。</summary>
    public static bool AWins(double strengthA, double strengthB, NationCoefficients c, IRandomSource rng)
        => rng.NextDouble() < WinProbability(strengthA, strengthB, c);

    /// <summary>2校の対戦で勝者を返す。</summary>
    public static School Play(School a, School b, NationCoefficients c, IRandomSource rng)
        => AWins(a.Strength, b.Strength, c, rng) ? a : b;

    /// <summary>
    /// 勝者・敗者・得点差を返す（リーグ戦の順位付けに使う, 設計書05 §1.5「同率は…得失点」）。
    /// 得点差はチーム力差が大きいほど開く傾向（強い相手には僅差、弱い相手には大勝）。決定論。
    /// </summary>
    public static (School Winner, School Loser, int Margin) PlayDetailed(
        School a, School b, NationCoefficients c, IRandomSource rng)
    {
        var winner = Play(a, b, c, rng);
        var loser = ReferenceEquals(winner, a) ? b : a;
        // 得点差も勾配の一部なので勝率と同じ尺度（圧縮後の能力中心）で測る（issue #578）。
        // 生の強度差のままだと「勝率は僅差なのにスコアだけ大差」というちぐはぐな裏試合になる。
        var rc = Season.RosterCoefficients.Default;
        var gap = Math.Abs(rc.CompressStrength(winner.Strength) - rc.CompressStrength(loser.Strength));
        var margin = 1 + (int)Math.Round(MathUtil.Clamp(gap / 8.0 + rng.NextGaussian(0, 1.2), 0.0, 12.0));
        return (winner, loser, margin);
    }
}
