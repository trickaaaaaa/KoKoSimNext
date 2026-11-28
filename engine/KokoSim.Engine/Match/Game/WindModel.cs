using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;

namespace KokoSim.Engine.Match.Game;

/// <summary>
/// 試合ごとの風（向き・強さ）モデルの係数（issue #1614, design-13-stadiums §5 の「後回しの箱」を実装）。
/// 打球の飛び（<see cref="Pitching.BallisticIntegrator.IntegrateToGround"/>）にだけ効かせる。投球には効かせない
/// （design-13 §5 の決定・弾道テーブル<see cref="Pitching.TrajectoryFeatureTable"/>の再構築を避ける）。
///
/// <para><b>気温（<see cref="WeatherModel"/>）の有効フラグに相乗り</b>する: <see cref="WindModel.ApplyForMatch"/> は
/// <c>ctx.Weather</c> が有効（かつ屋外）のときだけ風も引く。天候（気温・雨/曇り）と風を1つの「その日の天気」として
/// まとめて切れる＝Balance CLI の既存の <c>applyWeather</c> 既定オフが風にも自動で及び、
/// 既存の「weather off の帯は従来と1ビットも変わらない」テストを動かさずに済む。風だけを単独で切るには
/// <see cref="WindCoefficients.Enabled"/>=false を渡す（気温は有効のまま）。</para>
/// </summary>
public sealed record WindCoefficients
{
    /// <summary>有効化フラグ。false の間は風の生成も派生も一切行わない。</summary>
    public bool Enabled { get; init; } = true;

    // ===== 強さ（2026-11-28 オーナー決定: 0〜6 m/s・強風は稀。ほとんどの日は0〜3m/s、たまに5〜6m/s）=====
    /// <summary>
    /// 強さ分布の標準偏差[m/s]。半正規分布 |N(0, σ)| を <see cref="MaxSpeedMps"/> でクランプする
    /// （σ=2.0 で P(&gt;3m/s)≈13%・P(&gt;5m/s)≈1%＝「ほとんど0〜3、たまに5〜6」に合う）。
    /// </summary>
    public double SpeedStdDevMps { get; init; } = 2.0;

    /// <summary>強さの上限[m/s]（クランプ）。</summary>
    public double MaxSpeedMps { get; init; } = 6.0;

    // ===== 向き（球場の癖＝<see cref="FieldGeometry.WindBiasBearingDeg"/>。無い球場は一様乱数）=====
    /// <summary>
    /// 球場固有の風の癖がある場合、その平均方位角を中心に散らす標準偏差[度]。癖の無い球場は使わない
    /// （一様乱数 [0, 360°) で引く＝向きが打ち消し合い平均すると無風と同じになる）。
    /// </summary>
    public double BiasBearingSpreadDeg { get; init; } = 35.0;
}

/// <summary>
/// 1試合分の風の抽選結果（issue #1614）。方位角は <see cref="FieldGeometry.FenceDistance"/> と同じ規約
/// （0=中堅方向、+=一塁/右翼側、−=三塁/左翼側）。表示（矢印＋m/s、issue #1614）は
/// <see cref="BearingRad"/>・<see cref="SpeedMps"/> からそのまま作れる。
/// </summary>
public sealed record WindSample(double BearingRad, double SpeedMps)
{
    /// <summary>水平の風速ベクトル[m/s]（地面基準、Y=0）。<see cref="BallisticIntegrator"/> へそのまま渡す値。</summary>
    public Vector3D Velocity => new(SpeedMps * Math.Sin(BearingRad), 0.0, SpeedMps * Math.Cos(BearingRad));
}

/// <summary>
/// 試合ごとの風を決定論的に生成する純関数群（issue #1614）。<see cref="WeatherModel"/> と対になる構造
/// （専用Forkストリーム・屋内球場は無風・ApplyForMatch で<see cref="GameContext"/>へ差し替え）。
/// </summary>
public static class WindModel
{
    // 風生成の専用Forkストリーム識別子（"WIND"）。試合本体・気温（"WEATHER"）とは独立した乱数列。
    private const ulong WindStreamId = 0x57_49_4E_44_00_00_00_00UL;

    /// <summary>基準（既定）。data/coefficients.yaml <c>wind</c> と同値。</summary>
    public static WindCoefficients Base { get; } = new();

    /// <summary>
    /// 強さ[m/s]を引く（半正規分布 |N(0,σ)| を [0, Max] にクランプ）。平均すると無風と同じになるのは
    /// 向き側（一様乱数の打ち消し）が担うので、強さ自体は常に非負＝大きさだけの分布でよい。
    /// </summary>
    public static double SampleSpeedMps(IRandomSource windRng, WindCoefficients c)
    {
        var raw = Math.Abs(windRng.NextGaussian(0.0, c.SpeedStdDevMps));
        return Math.Min(raw, c.MaxSpeedMps);
    }

    /// <summary>
    /// 方位角[rad]を引く。<paramref name="field"/> に球場固有の癖（<see cref="FieldGeometry.WindBiasBearingDeg"/>）が
    /// あればその方向を中心に正規分布で散らし、無ければ一様乱数 [0, 2π) を返す（平均すると無風と同じ＝2026-11-28決定）。
    /// </summary>
    public static double SampleBearingRad(IRandomSource windRng, WindCoefficients c, FieldGeometry field)
    {
        if (field.WindBiasBearingDeg is { } biasDeg)
        {
            var biasRad = biasDeg * Math.PI / 180.0;
            var spreadRad = c.BiasBearingSpreadDeg * Math.PI / 180.0;
            return biasRad + windRng.NextGaussian(0.0, spreadRad);
        }
        return windRng.NextDouble() * 2.0 * Math.PI;
    }

    /// <summary>向き→強さの順に引いて1試合分の風を作る。</summary>
    public static WindSample SampleWind(IRandomSource windRng, WindCoefficients c, FieldGeometry field)
    {
        var bearing = SampleBearingRad(windRng, c, field);
        var speed = SampleSpeedMps(windRng, c);
        return new WindSample(bearing, speed);
    }

    /// <summary>風速ベクトルで <see cref="FieldGeometry.Wind"/> を差し替えた派生 <see cref="FieldGeometry"/>。</summary>
    public static FieldGeometry ApplyTo(FieldGeometry field, WindSample sample) => field with { Wind = sample.Velocity };

    /// <summary>風速ベクトルで <see cref="GameContext.Field"/> を差し替えた派生 <see cref="GameContext"/>（純関数・テスト用）。</summary>
    public static GameContext ApplyTo(GameContext ctx, WindSample sample) => ctx with { Field = ApplyTo(ctx.Field, sample) };

    /// <summary>
    /// 試合開始時に一度だけ呼ぶ配線点（<see cref="GameEngine.NewProgress"/> から、<see cref="WeatherModel.ApplyForMatch"/> の直後）。
    /// 専用ストリームを Fork して風を生成し、<see cref="FieldGeometry.Wind"/> を差し替えた <see cref="GameContext"/> を返す。
    /// <paramref name="matchRng"/> は Fork のみで消費しない（不変条件#2）。
    /// <c>ctx.Weather</c> が null／無効、<see cref="WindCoefficients.Enabled"/>=false、または屋内の球場
    /// （<see cref="FieldGeometry.Indoor"/>）なら <paramref name="ctx"/> をそのまま返す（無風・<see cref="GameContext.ResolvedWind"/> も null）。
    /// </summary>
    public static GameContext ApplyForMatch(GameContext ctx, IRandomSource matchRng)
    {
        if (ctx.Weather is not { Enabled: true }) return ctx;
        if (ctx.Wind is not { Enabled: true } c) return ctx;
        if (ctx.Field.Indoor) return ctx;
        var sample = SampleForMatch(matchRng, c, ctx.Field);
        return ApplyTo(ctx, sample) with { ResolvedWind = sample };
    }

    /// <summary>
    /// その試合の風。<see cref="ApplyForMatch"/> と同じ専用ストリームから引く＝画面に出す風と試合に効く風が必ず一致する。
    /// <paramref name="matchRng"/> は Fork のみで消費しない（不変条件#2）。
    /// </summary>
    public static WindSample SampleForMatch(IRandomSource matchRng, WindCoefficients c, FieldGeometry field)
        => SampleWind(matchRng.Fork(WindStreamId), c, field);
}
