using KokoSim.Balance;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Balance;

/// <summary>
/// 打球方向の分布の回帰（issue #262・設計書01 §2④⑤）。
/// 修正前は方位角が「平均0・σ20°の正規分布」だけで、引っ張り/流しもタイミングのズレも打球種別も
/// 見ていなかったため、フライ/ライナーの捕球者がセンターに 54.5%（外野手だけなら 56.2%）集中していた。
/// タイミングのズレ変数（④）→ 打球方向（⑤）を通した後は、左右へ散ることをここで固定する。
/// </summary>
[Trait("Category", "Heavy")]
public sealed class BattedBallBearingTests
{
    private const int AtBats = 60000;

    private readonly ITestOutputHelper _out;

    public BattedBallBearingTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(42UL)]
    [InlineData(2024UL)]
    public void OutfieldCatches_AreNotCenterHeavy(ulong seed)
    {
        var p = AtBatSimulation.RunBearingProfile(
            AtBats, seed, BalanceRegressionTests.FindDataFile("coefficients.yaml"));
        _out.WriteLine(p.ToMarkdown());

        // しきい値は実測で決めた（オーナー決定 2026-07-27）。issue 起票時の目安は 40% だったが、
        // 打球方向をどれだけ散らしてもセンター比は 42〜43% で頭打ちになることが実測で分かった
        // （引っ張り角 ground 28°→40°→55° でセンター比 48.0→44.3→43.1%）。
        // センターの担当弧は ±14°＝フェア地域の31%だが、加えて左中間・右中間の打球も追うため。
        // 40% を割るには外野の守備位置を ±28°→±22° へ寄せる必要があり、それは打球方向とは別の変更。
        // ここでは「修正前（54.5%）より明確に下がっている」ことを守る。
        Assert.True(p.CenterShare <= 0.50,
            $"外野捕球のセンター比 {p.CenterShare:P1} が 50% を超えている（打球方向が中央に寄りすぎ）");
        Assert.True(p.LeftShare >= 0.25,
            $"外野捕球のレフト比 {p.LeftShare:P1} が 25% 未満");
        Assert.True(p.RightShare >= 0.25,
            $"外野捕球のライト比 {p.RightShare:P1} が 25% 未満");
    }

    [Theory]
    [InlineData(42UL)]
    [InlineData(2024UL)]
    public void BearingHistogram_IsNotConcentratedInTheMiddleThird(ulong seed)
    {
        var p = AtBatSimulation.RunBearingProfile(
            AtBats, seed, BalanceRegressionTests.FindDataFile("coefficients.yaml"));
        _out.WriteLine(p.ToMarkdown());

        // 中央3分の1（|方位| < 15°）。修正前は 56.1%。
        Assert.True(p.CenterThirdShare <= 0.45,
            $"中央3分の1の比率 {p.CenterThirdShare:P1} が 45% を超えている");
    }

    /// <summary>
    /// 引っ張り方向は打席の左右で反転する（設計書01 §2⑤）。座標規約は −＝左翼（三塁側）／＋＝右翼（一塁側）
    /// なので、右打者の分布の重心は負・左打者は正になる。左右が効いていない実装への逆戻りをここで止める。
    /// </summary>
    [Fact]
    public void PullDirection_FlipsWithBatterHandedness()
    {
        var coeff = BalanceRegressionTests.FindDataFile("coefficients.yaml");
        var right = AtBatSimulation.RunBearingProfile(AtBats, 7UL, coeff, batsOverride: Engine.Players.Handedness.Right);
        var left = AtBatSimulation.RunBearingProfile(AtBats, 7UL, coeff, batsOverride: Engine.Players.Handedness.Left);
        _out.WriteLine("右打\n" + right.ToMarkdown());
        _out.WriteLine("左打\n" + left.ToMarkdown());

        Assert.True(right.MeanBearingDeg < -1.0,
            $"右打者の平均方位角 {right.MeanBearingDeg:F2}° が左翼側へ寄っていない");
        Assert.True(left.MeanBearingDeg > 1.0,
            $"左打者の平均方位角 {left.MeanBearingDeg:F2}° が右翼側へ寄っていない");
        Assert.True(right.LeftShare > right.RightShare, "右打者はレフト方向の捕球が多いはず");
        Assert.True(left.RightShare > left.LeftShare, "左打者はライト方向の捕球が多いはず");
    }
}
