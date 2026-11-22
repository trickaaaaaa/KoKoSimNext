using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Match.Game;

/// <summary>
/// 試合ごとの天候（気温）モデルの係数（Issue #120 / OPEN-QUESTIONS Q22, design-05 の大会単位の味付け）。
/// 気温という物理量を経由して弾道（空気密度ρ）と投手消耗という既存の物理層パラメータを動かす（不変条件#1）。
/// 表示能力値や確率は直接いじらない。
///
/// <para>既定は「8月（夏の甲子園）」（<see cref="Enabled"/>=true）。<see cref="GameContext.Weather"/> が null または無効の間は
/// 試合挙動・統計帯は一切変わらない（従来と完全一致）。data/coefficients.yaml の weather ブロックで差し替える。
/// 気温の分布は試合日の月で引く（<see cref="WeatherModel.ForMonth"/>・Issue #971, オーナー決定2026-11-15。
/// 季節3つ→12か月の表）。</para>
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

    // ===== 気温→投手消耗（Issue #1071 オーナー決定2026-11-17: 「実効の目安球数」そのものを縮める方式に一本化）=====
    // 旧実装（Issue #120 決定2=案A）は「目安を超えた後の落ち方の傾き」（VelocityDropPerOverPitch/ControlDropPerOverPitch）
    // だけに気温項 ×(1+α×超過℃) を掛けていた。36℃（最大 +8%）でも目安を20球超えた時点の差が体感で分からない
    // （球速−3.0→−3.08km/h・制球−7.4→−7.6）ため見送り、issue #1071 で「実効目安球数そのものを縮める」方式へ
    // 置き換えた。目安を縮めればバテ始め（OnsetRatio）・目安到達時の低下量・ShouldRelieve（継投判断）が
    // 同じ式で全部ついてくる＝「暑いと継投が早まる」が自然に出る。旧方式（FatigueBaselineTemperatureC /
    // FatigueAlphaPerDegree）は二重取りを避けるため撤去＝このブロックの3定数だけが気温→投手消耗の入口。
    /// <summary>実効目安球数の縮小が始まる基準気温[℃]。これ以下では気温項ゼロ（目安は従来通り）。</summary>
    public double FatiguePitchBaselineTemperatureC { get; init; } = 30.0;
    /// <summary>
    /// 基準気温の上側アンカー点[℃]（既定36℃＝月別の表の最高気温と同じ）。<see cref="FatiguePitchReferenceReductionFraction"/>
    /// はこの気温での縮小率を直接指定する＝「36℃で−10%」のような決め方をそのままパラメータにできる。
    /// </summary>
    public double FatiguePitchReferenceTemperatureC { get; init; } = 36.0;
    /// <summary>
    /// <see cref="FatiguePitchReferenceTemperatureC"/> における実効目安球数の縮小率（既定 0.10＝36℃で−10%。
    /// 120球の投手なら108球）。基準気温からアンカー点まで線形に効き、アンカー点を超えても頭打ち
    /// （月別の表の最高気温はこの既定と同じ36℃なので実質頭打ちには当たらない）。
    /// </summary>
    public double FatiguePitchReferenceReductionFraction { get; init; } = 0.10;

    // ===== 天気の状態（Issue #803 決定1・2026-11-09 改定: 気温と同じ専用ストリームで別に1回抽選する）=====
    /// <summary>雨の確率（試合ごと）。状態は気温より先に引き、雨・曇りは気温の平均を下げる＝暑い日の雨も低確率で出る。</summary>
    public double RainProb { get; init; } = 0.15;
    /// <summary>曇りの確率（試合ごと）。残り（1 − 雨 − 曇り）が晴れ。</summary>
    public double CloudyProb { get; init; } = 0.35;
    /// <summary>雨の日の気温の平均のずれ[℃]（負＝涼しい）。σ・上下限は晴れと同じ。</summary>
    public double RainTemperatureOffsetC { get; init; } = -3.0;
    /// <summary>曇りの日の気温の平均のずれ[℃]。</summary>
    public double CloudyTemperatureOffsetC { get; init; } = -1.0;
}

/// <summary>天気の状態（Issue #803 オーナー決定2026-11-09: 表示専用。試合には気温チャネルだけが効く）。</summary>
public enum WeatherState { Sunny, Cloudy, Rainy }

/// <summary>1試合分の天気の抽選結果（状態＋気温）。</summary>
public sealed record WeatherSample(WeatherState State, double TemperatureC);

/// <summary>
/// 1か月ぶんの気温の分布と雨の確率（Issue #971）。物理変換の定数（基準気温・減衰係数・消耗係数・雨/曇りのずれ）は
/// 月に依らないので <see cref="MonthlyWeatherCoefficients.Base"/> から取り、月で変わるのはこの5つだけ。
/// </summary>
public sealed record WeatherMonthProfile(
    int Month, double MeanTemperatureC, double TemperatureStdDevC, double MinTemperatureC, double MaxTemperatureC,
    double RainProb);

/// <summary>
/// 月別の天気係数一式（Issue #971・オーナー決定2026-11-15: 試合日の月で引く・1年を通して12か月の表・地域差なし）。
/// <see cref="GameContext.Weather"/> には試合日の月で選んだ1つ（<see cref="ForMonth"/>）だけを通す＝解決式は月を知らなくてよい。
/// </summary>
public sealed record MonthlyWeatherCoefficients
{
    /// <summary>物理変換の定数と、表に無い月の既定（8月＝夏の甲子園の分布）。</summary>
    public WeatherCoefficients Base { get; init; } = WeatherModel.Base;

    /// <summary>1〜12月の気温の分布（月ごとに1件）。</summary>
    public IReadOnlyList<WeatherMonthProfile> Months { get; init; } = WeatherModel.DefaultMonths;

    /// <summary>暦月（1〜12。範囲外は折り返す）→その月の係数。表に無い月は <see cref="Base"/>。</summary>
    public WeatherCoefficients ForMonth(int calendarMonth)
    {
        var month = WeatherModel.NormalizeMonth(calendarMonth);
        foreach (var m in Months)
        {
            if (m.Month != month) continue;
            return Base with
            {
                MeanTemperatureC = m.MeanTemperatureC, TemperatureStdDevC = m.TemperatureStdDevC,
                MinTemperatureC = m.MinTemperatureC, MaxTemperatureC = m.MaxTemperatureC, RainProb = m.RainProb,
            };
        }
        return Base;
    }
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

    // ===== 月別の表（Issue #971）=====
    // 平均は日最高気温の平年値（試合は日中）。夏の甲子園（8月）は従来どおり 32℃・σ2.5・28〜36℃＝games_10k_summer の帯は動かさない。
    // σ3 の月は平均±8℃で切る（従来の春・秋と同じ幅）。雨の確率は従来の季節値（春25%・夏15%・秋20%）のまま月へ割り当てた。

    /// <summary>基準（既定）。data/coefficients.yaml <c>weather.base</c> と同値（8月・夏の甲子園, 28〜36℃・平均32℃）。</summary>
    public static WeatherCoefficients Base { get; } = new();

    /// <summary>月別の表の既定。data/coefficients.yaml <c>weather.months</c> と同値。</summary>
    public static IReadOnlyList<WeatherMonthProfile> DefaultMonths { get; } = new WeatherMonthProfile[]
    {
        new(1, 10.0, 3.0, 2.0, 18.0, 0.25),
        new(2, 11.0, 3.0, 3.0, 19.0, 0.25),
        new(3, 14.0, 3.0, 6.0, 22.0, 0.25),
        new(4, 19.0, 3.0, 11.0, 27.0, 0.25),
        new(5, 23.0, 3.0, 15.0, 31.0, 0.25),
        new(6, 26.0, 3.0, 18.0, 34.0, 0.15),
        new(7, 30.0, 2.5, 26.0, 34.0, 0.15),
        new(8, 32.0, 2.5, 28.0, 36.0, 0.15),
        new(9, 27.0, 3.0, 19.0, 35.0, 0.20),
        new(10, 22.0, 3.0, 14.0, 30.0, 0.20),
        new(11, 17.0, 3.0, 9.0, 25.0, 0.20),
        new(12, 12.0, 3.0, 4.0, 20.0, 0.25),
    };

    // 実ゲームの入口は YAML を読まずこの既定の表を引く。YAML との一致は CoefficientsDefaultsConsistencyTests が固定する（issue #1212）。
    private static readonly MonthlyWeatherCoefficients Default = new();

    /// <summary>暦月を 1〜12 へ折り返す（0→12, 13→1）。</summary>
    public static int NormalizeMonth(int calendarMonth) => ((calendarMonth - 1) % 12 + 12) % 12 + 1;

    /// <summary>
    /// 試合日の月→係数の単一入口（Issue #971）。大会も練習試合もここを通る＝GameContext.Weather へ通すのは
    /// 常にこの戻り値（1か月ぶん）。
    /// </summary>
    public static WeatherCoefficients ForMonth(int calendarMonth) => Default.ForMonth(calendarMonth);

    /// <summary>
    /// 大会の天気係数（背景で解く裏試合など、試合ごとの日付を持たない呼び出し向け）。大会の開幕月で引く
    /// （夏の県大会＝7月・甲子園＝8月・秋季県大会＝9月・地区大会＝10月・神宮＝11月・センバツ＝3月）。
    /// </summary>
    public static WeatherCoefficients ForTournament(TournamentSchedule schedule, TournamentKind kind)
        => ForMonth(TournamentDate.OpeningOf(schedule, kind).Month);

    /// <summary>月別の表（既定）に出てくる最低気温[℃]。弾道表の先読み範囲用。</summary>
    public static double CoolestTemperatureC => DefaultMonths.Min(m => m.MinTemperatureC);

    /// <summary>月別の表（既定）に出てくる最高気温[℃]。弾道表の先読み範囲用。</summary>
    public static double HottestTemperatureC => DefaultMonths.Max(m => m.MaxTemperatureC);

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
    /// 天気の状態を引く（Issue #803 決定1）。一様乱数1つで 雨／曇り／晴れ を決める。
    /// </summary>
    public static WeatherState SampleState(IRandomSource weatherRng, WeatherCoefficients c)
    {
        var u = weatherRng.NextDouble();
        if (u < c.RainProb) return WeatherState.Rainy;
        if (u < c.RainProb + c.CloudyProb) return WeatherState.Cloudy;
        return WeatherState.Sunny;
    }

    /// <summary>状態ごとの気温の平均のずれ[℃]。</summary>
    public static double TemperatureOffsetC(WeatherState state, WeatherCoefficients c) => state switch
    {
        WeatherState.Rainy => c.RainTemperatureOffsetC,
        WeatherState.Cloudy => c.CloudyTemperatureOffsetC,
        _ => 0.0,
    };

    /// <summary>
    /// 天気（状態＋気温）を引く（Issue #803）。専用ストリームから<b>状態→気温</b>の順に引く
    /// （状態で気温の平均をずらし、[Min, Max] にクランプ）。<see cref="ApplyForMatch"/> と画面の表示は
    /// 同じストリーム（<c>matchRng.Fork(WeatherStreamId)</c>）からこれを呼ぶ＝表示と試合が食い違わない。
    /// </summary>
    public static WeatherSample SampleWeather(IRandomSource weatherRng, WeatherCoefficients c)
    {
        var state = SampleState(weatherRng, c);
        var t = weatherRng.NextGaussian(c.MeanTemperatureC + TemperatureOffsetC(state, c), c.TemperatureStdDevC);
        return new WeatherSample(state, MathUtil.Clamp(t, c.MinTemperatureC, c.MaxTemperatureC));
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
    /// 気温[℃]における実効目安球数の倍率（Issue #1071。基準温以下は1.0＝不変、基準温からアンカー点まで線形に
    /// 下がり、<see cref="WeatherCoefficients.FatiguePitchReferenceReductionFraction"/> で頭打ち）。
    /// <see cref="PitchingFatigue"/> の目安投球数（<see cref="TeamState.EffectiveBaseStamina"/>）へ掛ける。
    /// </summary>
    public static double StaminaPitchFactor(double temperatureC, WeatherCoefficients c)
    {
        var span = c.FatiguePitchReferenceTemperatureC - c.FatiguePitchBaselineTemperatureC;
        if (span <= 0.0) return 1.0;
        var over = System.Math.Max(0.0, temperatureC - c.FatiguePitchBaselineTemperatureC);
        var ratio = System.Math.Min(1.0, over / span);
        return 1.0 - c.FatiguePitchReferenceReductionFraction * ratio;
    }

    /// <summary>
    /// 指定気温[℃]で空気密度を差し替えた派生 <see cref="GameContext"/>（純関数・テスト用）。投手消耗は
    /// 「実効目安球数」側（<see cref="StaminaPitchFactor"/>・<see cref="TeamState.SetWeatherStaminaFactor"/>）で
    /// 効かせるため、<see cref="GameContext.Fatigue"/> 自体は気温で変えない（Issue #1071・二重取り回避）。
    /// <see cref="GameContext.Weather"/> が null または無効ならそのまま返す（従来挙動）。
    /// </summary>
    public static GameContext ApplyTo(GameContext ctx, double temperatureC)
    {
        if (ctx.Weather is not { Enabled: true } c) return ctx;
        return ctx with { Aerodynamics = ApplyTo(ctx.Aerodynamics, temperatureC, c) };
    }

    /// <summary>
    /// 試合開始時に一度だけ呼ぶ配線点（GameEngine.NewProgress から）。専用ストリームを Fork して気温を生成し、
    /// 空気密度・投手消耗を差し替えた <see cref="GameContext"/> を返す。<paramref name="matchRng"/> は Fork のみで
    /// 消費しない（xoshiro の Fork は親状態を進めない）ため、試合本体の乱数順・決定論は不変（不変条件#2）。
    /// Weather が null／無効、または屋内の球場（<see cref="Field.FieldGeometry.Indoor"/>・issue #1020）なら
    /// <paramref name="ctx"/> をそのまま返す（屋内は気温の影響なし＝基準の空気密度・消耗。天気も出さない）。
    /// </summary>
    public static GameContext ApplyForMatch(GameContext ctx, IRandomSource matchRng)
    {
        if (ctx.Weather is not { Enabled: true } c) return ctx;
        if (ctx.Field.Indoor) return ctx;
        var sample = SampleForMatch(matchRng, c);
        return ApplyTo(ctx, sample.TemperatureC) with { ResolvedWeather = sample };
    }

    /// <summary>
    /// その試合の天気（状態＋気温）。<see cref="ApplyForMatch"/> と同じ専用ストリームから引く＝画面に出す天気と
    /// 試合に効く気温が必ず一致する。<paramref name="matchRng"/> は Fork のみで消費しない（不変条件#2）。
    /// </summary>
    public static WeatherSample SampleForMatch(IRandomSource matchRng, WeatherCoefficients c)
        => SampleWeather(matchRng.Fork(WeatherStreamId), c);
}
