using KokoSim.Engine.Match.Field;

namespace KokoSim.Engine.Season;

/// <summary>
/// 捕手として読めるかの判定（#589・設計書04 §2.3）。<see cref="PitcherRoleResolver"/> と同じく
/// **判定式はこのクラスにしか存在しない**（閾値のコピーを各所に書かない）。
///
/// 投手役の判定が「投手適性 vs 最良守備適性」の比較なのに対し、捕手は8守備枠のうちの1つ＝
/// 誰かが必ず座る枠なので、比較ではなく**絶対の下限適性**で読む。下限は
/// <see cref="RosterCoefficients.CatcherAptitudeFloor"/>（既定50＝適性補正が付かない点）。
///
/// 「読める選手が1人も居ない」コホートを潰すのは <c>ProspectGenerator.EnsureCatcherSupply</c>。
/// </summary>
public static class CatcherRoleResolver
{
    /// <summary>捕手適性が下限に届くか（下限ちょうどは読める側に含める）。</summary>
    public static bool ReadsAsCatcher(int catcherAptitude, RosterCoefficients c)
        => catcherAptitude >= c.CatcherAptitudeFloor;

    /// <inheritdoc cref="ReadsAsCatcher(int,RosterCoefficients)"/>
    public static bool ReadsAsCatcher(DevelopingPlayer p, RosterCoefficients c)
        => ReadsAsCatcher(p.Aptitude(FieldPosition.Catcher), c);

    /// <summary>
    /// 係数を持たない呼び出し側向け（<see cref="RosterCoefficients.Default"/> を使う。
    /// YAML と C#既定は一致が固定されているので挙動は同一）。
    /// </summary>
    public static bool ReadsAsCatcher(DevelopingPlayer p) => ReadsAsCatcher(p, RosterCoefficients.Default);

    /// <summary>
    /// 判定をちょうど満たす最小の適性値（供給下限の引き上げ先）。<see cref="ReadsAsCatcher(int,RosterCoefficients)"/>
    /// が <c>&gt;=</c> 判定なので下限そのもの。切り上げるのは下限に小数を入れた場合の安全側。
    /// </summary>
    public static int MinimumAptitude(RosterCoefficients c)
        => (int)Core.MathUtil.Clamp(System.Math.Ceiling(c.CatcherAptitudeFloor), 1, 99);
}
