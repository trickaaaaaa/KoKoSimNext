namespace KokoSim.Engine.Match.Field;

/// <summary>球場の格（設計書13-stadiums §1）。序盤=市営級、準決勝/決勝=県営級、聖地=全国級。</summary>
public enum StadiumTier
{
    Municipal,    // 市営級（狭め・低フェンス。序盤ラウンド）
    Prefectural,  // 県営級（広め。準決勝・決勝）
    National,     // 全国級（甲子園・神宮）
}

/// <summary>
/// 球場（設計書13-stadiums ①物理層）。寸法をパラメータ化し FieldGeometry を生成する。
/// ②環境（風）・③ビジュアルは後回し。<see cref="Wind"/> は②用の箱（現状 null）。
/// エンジン純度維持（不変条件#3）: 生成・ロードは KokoSim.Config 側で行う。
/// </summary>
public sealed record Stadium
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public StadiumTier Tier { get; init; } = StadiumTier.Prefectural;

    /// <summary>左翼（三塁側）ポール距離 [m]。</summary>
    public double LeftM { get; init; } = 95.0;

    /// <summary>右翼（一塁側）ポール距離 [m]。</summary>
    public double RightM { get; init; } = 95.0;

    /// <summary>中堅（センター）距離 [m]。</summary>
    public double CenterM { get; init; } = 118.0;

    /// <summary>フェンス高 [m]。</summary>
    public double FenceHeightM { get; init; } = 2.5;

    /// <summary>②環境用の箱（球場固有風）。今回未使用（設計書13-stadiums §5）。</summary>
    public string? Wind { get; init; }

    /// <summary>寸法から判定用の <see cref="FieldGeometry"/> を生成する。</summary>
    public FieldGeometry ToFieldGeometry() => new()
    {
        LeftFenceM = LeftM,
        RightFenceM = RightM,
        CenterFenceM = CenterM,
        FenceHeightM = FenceHeightM,
    };
}
