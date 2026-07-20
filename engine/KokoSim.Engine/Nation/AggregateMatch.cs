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
    public static double WinProbability(double strengthA, double strengthB, NationCoefficients c)
        => MathUtil.Logistic((strengthA - strengthB) / c.AggregateScale);

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
        var gap = Math.Abs(winner.Strength - loser.Strength);
        var margin = 1 + (int)Math.Round(MathUtil.Clamp(gap / 8.0 + rng.NextGaussian(0, 1.2), 0.0, 12.0));
        return (winner, loser, margin);
    }
}
