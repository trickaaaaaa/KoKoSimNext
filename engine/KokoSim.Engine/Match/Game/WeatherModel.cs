using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Pitching;

namespace KokoSim.Engine.Match.Game;

/// <summary>
/// 試合ごとの天候（気温）モデルの係数（Issue #120 / OPEN-QUESTIONS Q22, design-05 の大会単位の味付け）。
/// 気温という物理量を経由して弾道（空気密度ρ）と投手消耗という既存の物理層パラメータを動かす（不変条件#1）。
/// 表示能力値や確率は直接いじらない。
///
/// <para>既定は「無効（<see cref="Enabled"/>=false）」。<see cref="GameContext.Weather"/> が null または無効の間は
/// 試合挙動・統計帯は一切変わらない（従来と完全一致）。data/coefficients.yaml の weather ブロックで有効化する。</para>
/// </summary>
public sealed record WeatherCoefficients
{
    /// <summary>有効化フラグ。false の間は気温生成も派生も一切行わない（既定＝従来挙動）。</summary>
    public bool Enabled { get; init; }

    // ===== 気温分布（Issue #120 決定1: 夏の甲子園を想定, 28〜36℃・平均32℃）=====
    /// <summary>試合気温の平均[℃]。</summary>
    public double MeanTemperatureC { get; init; } = 32.0;
    /// <summary>試合気温の標準偏差[℃]。</summary>
    public double TemperatureStdDevC { get; init; } = 2.5;
    /// <summary>試合気温の下限[℃]（クランプ）。</summary>
    public double MinTemperatureC { get; init; } = 28.0;
    /// <summary>試合気温の上限[℃]（クランプ）。</summary>
    public double MaxTemperatureC { get; init; } = 36.0;

    // ===== 気温→空気密度（Issue #120 決定1=案B: 物理式に減衰係数 k を掛けて効きを抑える）=====
    /// <summary>空気密度の基準気温[℃]（この気温で <see cref="Aerodynamics.AirDensity"/> の既定値になる）。</summary>
    public double BaselineTemperatureC { get; init; } = 15.0;
    /// <summary>
    /// 密度変化の減衰係数 k（決定1=案B, 既定 0.5）。乾燥空気の実密度 ρ(T)=ρ0×(273.15+基準温)/(273.15+T) から
    /// 基準密度との差分に k を掛ける＝効きを k 倍に抑える（k=1 で物理そのまま, k=0 で無効）。極端な乱打戦を避ける。
    /// </summary>
    public double DensityDamping { get; init; } = 0.5;

    // ===== 気温→投手消耗（Issue #120 決定2=案A: 加算・線形）=====
    /// <summary>投手消耗の気温加算の基準気温[℃]。これ以下では気温項ゼロ（消耗は従来通り）。</summary>
    public double FatigueBaselineTemperatureC { get; init; } = 28.0;
    /// <summary>
    /// 投手消耗の気温係数 α[1/℃]（決定2=案A, 既定 0.01）。基準温からの超過1℃ごとに超過球数あたりの
    /// 球速天井・制球低下量を α だけ増やす: drop' = drop × (1 + α×max(0, T-基準温))。36℃で +8%。
    /// </summary>
    public double FatigueAlphaPerDegree { get; init; } = 0.01;
}

/// <summary>
/// 試合ごとの気温を決定論的に生成し（不変条件#2, 専用Forkストリーム）、
/// 気温から空気密度・投手消耗係数を導く純関数群（Issue #120）。
/// </summary>
public static class WeatherModel
{
    // 気温生成の専用Forkストリーム識別子（"WEATHER"）。試合本体の乱数列とは独立＝本体の乱数順に触れない。
    private const ulong WeatherStreamId = 0x5745_4154_4845_52_00UL;

    // 摂氏→ケルビンのオフセット。
    private const double KelvinOffset = 273.15;

    /// <summary>
    /// 試合気温[℃]を生成する。<paramref name="weatherRng"/> は試合本体とは別の専用ストリームを渡すこと。
    /// 平均・σの正規分布を [Min, Max] にクランプする（Issue #120 決定1）。
    /// </summary>
    public static double SampleTemperatureC(IRandomSource weatherRng, WeatherCoefficients c)
    {
        var t = weatherRng.NextGaussian(c.MeanTemperatureC, c.TemperatureStdDevC);
        return MathUtil.Clamp(t, c.MinTemperatureC, c.MaxTemperatureC);
    }

    /// <summary>
    /// 気温[℃]における実効空気密度[kg/m^3]（決定1=案B）。
    /// ρ_phys(T)=ρ0×(273.15+基準温)/(273.15+T)（気圧一定・乾燥空気）。基準との差分に減衰係数 k を掛ける。
    /// </summary>
    public static double AirDensityAt(double temperatureC, double baselineDensity, WeatherCoefficients c)
    {
        var physical = baselineDensity * (KelvinOffset + c.BaselineTemperatureC) / (KelvinOffset + temperatureC);
        return baselineDensity + c.DensityDamping * (physical - baselineDensity);
    }

    /// <summary>気温[℃]で空気密度を差し替えた派生 <see cref="Aerodynamics"/>（決定1）。</summary>
    public static Aerodynamics ApplyTo(Aerodynamics aero, double temperatureC, WeatherCoefficients c)
        => aero with { AirDensity = AirDensityAt(temperatureC, aero.AirDensity, c) };

    /// <summary>
    /// 気温[℃]で消耗係数を増やした派生 <see cref="FatigueCoefficients"/>（決定2=案A）。
    /// 基準温以下では従来値そのまま。超過1℃ごとに球速天井・制球の低下量を α 倍で増やす。
    /// </summary>
    public static FatigueCoefficients ApplyTo(FatigueCoefficients fatigue, double temperatureC, WeatherCoefficients c)
    {
        var over = System.Math.Max(0.0, temperatureC - c.FatigueBaselineTemperatureC);
        var factor = 1.0 + c.FatigueAlphaPerDegree * over;
        return fatigue with
        {
            VelocityDropPerOverPitch = fatigue.VelocityDropPerOverPitch * factor,
            ControlDropPerOverPitch = fatigue.ControlDropPerOverPitch * factor,
        };
    }

    /// <summary>
    /// 指定気温[℃]で空気密度・投手消耗を差し替えた派生 <see cref="GameContext"/>（純関数・テスト用）。
    /// <see cref="GameContext.Weather"/> が null または無効ならそのまま返す（従来挙動）。
    /// </summary>
    public static GameContext ApplyTo(GameContext ctx, double temperatureC)
    {
        if (ctx.Weather is not { Enabled: true } c) return ctx;
        return ctx with
        {
            Aerodynamics = ApplyTo(ctx.Aerodynamics, temperatureC, c),
            Fatigue = ApplyTo(ctx.Fatigue, temperatureC, c),
        };
    }

    /// <summary>
    /// 試合開始時に一度だけ呼ぶ配線点（GameEngine.NewProgress から）。専用ストリームを Fork して気温を生成し、
    /// 空気密度・投手消耗を差し替えた <see cref="GameContext"/> を返す。<paramref name="matchRng"/> は Fork のみで
    /// 消費しない（xoshiro の Fork は親状態を進めない）ため、試合本体の乱数順・決定論は不変（不変条件#2）。
    /// Weather が null／無効なら <paramref name="ctx"/> をそのまま返す。
    /// </summary>
    public static GameContext ApplyForMatch(GameContext ctx, IRandomSource matchRng)
    {
        if (ctx.Weather is not { Enabled: true } c) return ctx;
        var temperatureC = SampleTemperatureC(matchRng.Fork(WeatherStreamId), c);
        return ApplyTo(ctx, temperatureC);
    }
}
