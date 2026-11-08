using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Match.Game;

/// <summary>
/// 試合ごとの天候（気温）モデルの係数（Issue #120 / OPEN-QUESTIONS Q22, design-05 の大会単位の味付け）。
/// 気温という物理量を経由して弾道（空気密度ρ）と投手消耗という既存の物理層パラメータを動かす（不変条件#1）。
/// 表示能力値や確率は直接いじらない。
///
/// <para>既定は「夏（<see cref="Enabled"/>=true）」。<see cref="GameContext.Weather"/> が null または無効の間は
/// 試合挙動・統計帯は一切変わらない（従来と完全一致）。data/coefficients.yaml の weather ブロックで差し替える。
/// 季節別の分布は <see cref="WeatherModel.Spring"/>/<see cref="WeatherModel.Summer"/>/<see cref="WeatherModel.Autumn"/>
/// （Issue #803, オーナー決定2026-11-09）。</para>
/// </summary>
public sealed record WeatherCoefficients
{
    /// <summary>有効化フラグ。false の間は気温生成も派生も一切行わない。</summary>
    public bool Enabled { get; init; } = true;

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

    // ===== 気温→天気の状態（Issue #803 決定1: 晴れ/曇り/雨は気温と同じ乱数から導く）=====
    /// <summary>
    /// 雨のしきい値（気温のz値。これ以下＝雨）。気温は<b>この乱数自体から</b>導く＝追加の乱数消費なしで
    /// 「雨の日は涼しい」という相関を内在させる（別々に抽選すると相関を別途作り込む必要が出る）。
    /// </summary>
    public double RainZThreshold { get; init; } = -0.75;
    /// <summary>曇りのしきい値（気温のz値。これ以下＝曇り、それ以上＝晴れ。<see cref="RainZThreshold"/>未満は雨）。</summary>
    public double CloudyZThreshold { get; init; } = 0.25;
}

/// <summary>天気の状態（Issue #803 オーナー決定2026-11-09: 表示専用。試合には気温チャネルだけが効く）。</summary>
public enum WeatherState { Sunny, Cloudy, Rainy }

/// <summary>1試合分の天気の抽選結果（状態＋気温）。</summary>
public sealed record WeatherSample(WeatherState State, double TemperatureC);

/// <summary>季節（Issue #803 決定4: 気温分布は季節別・地域差なし）。</summary>
public enum WeatherSeason { Spring, Summer, Autumn }

/// <summary>
/// 季節別の天気係数一式（Issue #803）。<see cref="GameContext.Weather"/> には試合の季節で選んだ
/// 1つ（<see cref="WeatherModel.ForSeason"/>）だけを通す＝解決式は季節を知らなくてよい。
/// </summary>
public sealed record SeasonalWeatherCoefficients
{
    public WeatherCoefficients Spring { get; init; } = WeatherModel.Spring;
    public WeatherCoefficients Summer { get; init; } = WeatherModel.Summer;
    public WeatherCoefficients Autumn { get; init; } = WeatherModel.Autumn;
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

    // ===== 季節別プリセット（Issue #803 決定4: 季節別・地域差なし）=====
    // 気温分布（平均/σ/下限/上限）だけが季節で変わる。物理変換（基準気温・減衰係数・消耗係数・天気しきい値）は
    // 季節に依らない定数なので、既定（夏）から mean/std/min/max だけを差し替える。

    /// <summary>夏（既定）。data/coefficients.yaml <c>weather.summer</c> と同値（夏の甲子園を想定, 28〜36℃）。</summary>
    public static WeatherCoefficients Summer { get; } = new();

    /// <summary>春。data/coefficients.yaml <c>weather.spring</c> と同値（センバツ期を想定, 8〜24℃）。</summary>
    public static WeatherCoefficients Spring { get; } = Summer with
    {
        MeanTemperatureC = 16.0, TemperatureStdDevC = 3.0, MinTemperatureC = 8.0, MaxTemperatureC = 24.0,
    };

    /// <summary>秋。data/coefficients.yaml <c>weather.autumn</c> と同値（秋季大会期を想定, 14〜30℃）。</summary>
    public static WeatherCoefficients Autumn { get; } = Summer with
    {
        MeanTemperatureC = 22.0, TemperatureStdDevC = 3.0, MinTemperatureC = 14.0, MaxTemperatureC = 30.0,
    };

    /// <summary>季節→係数の単一入口。GameContext.Weather へ通すのは常にこの戻り値（1季節ぶん）。</summary>
    public static WeatherCoefficients ForSeason(WeatherSeason season) => season switch
    {
        WeatherSeason.Spring => Spring,
        WeatherSeason.Autumn => Autumn,
        _ => Summer,
    };

    /// <summary>
    /// 大会種別→季節（Issue #803）。センバツ本戦は春、夏系（県大会・甲子園）は夏、
    /// それ以外（秋季県大会・秋季地区予選・秋季地区大会・神宮）は秋。
    /// </summary>
    public static WeatherSeason SeasonFor(TournamentKind kind) => kind switch
    {
        TournamentKind.SenbatsuKoshien => WeatherSeason.Spring,
        TournamentKind.Summer or TournamentKind.SummerKoshien => WeatherSeason.Summer,
        _ => WeatherSeason.Autumn,   // Autumn, AutumnDistrict, Regional, Jingu
    };

    /// <summary>
    /// 暦月（1〜12）→季節。大会に紐付かない試合（練習試合等）向け。3〜5月=春, 6〜8月=夏, 9〜11月=秋。
    /// オフシーズン（12〜2月, 公式戦が無い期間）は最も涼しい季節帯＝春を流用する。
    /// </summary>
    public static WeatherSeason SeasonFor(int calendarMonth) => calendarMonth switch
    {
        3 or 4 or 5 => WeatherSeason.Spring,
        6 or 7 or 8 => WeatherSeason.Summer,
        9 or 10 or 11 => WeatherSeason.Autumn,
        _ => WeatherSeason.Spring,
    };

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
    /// 気温[℃]から天気の状態を導く（Issue #803 決定1）。気温のz値で3値化する純関数＝追加の乱数は使わない
    /// （気温と同じ1回の抽選から状態まで決まる＝「雨の日は涼しい」の相関が自然に出る）。
    /// </summary>
    public static WeatherState StateFor(double temperatureC, WeatherCoefficients c)
    {
        var z = (temperatureC - c.MeanTemperatureC) / c.TemperatureStdDevC;
        if (z <= c.RainZThreshold) return WeatherState.Rainy;
        if (z <= c.CloudyZThreshold) return WeatherState.Cloudy;
        return WeatherState.Sunny;
    }

    /// <summary>
    /// 天気（状態＋気温）を1回の抽選で生成する（Issue #803）。<see cref="SampleTemperatureC"/> と同じ1回の
    /// Gaussian 抽選だけを使う＝<see cref="ApplyForMatch"/> の乱数消費・帯は不変（状態は気温からの導出のみ）。
    /// </summary>
    public static WeatherSample SampleWeather(IRandomSource weatherRng, WeatherCoefficients c)
    {
        var t = SampleTemperatureC(weatherRng, c);
        return new WeatherSample(StateFor(t, c), t);
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
            // 目安の手前から効くカーブ（issue fatigue-gradual-before-threshold）の目安時低下量は気温で増やさない
            // ＝夏場の上乗せは従来どおり「目安超過1球あたりの勾配」だけ。両方を増やすと夏場帯（games_10k_summer 4.9–5.8）を
            // 超えた（実測 5.87・seed42）。夏場をもっと効かせるかは帯の張り直しと一緒に別途判断する。
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
