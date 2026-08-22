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

    /// <summary>
    /// 甲子園（設計書13 §1・OPEN-QUESTIONS Q11）。夏の甲子園の全試合はここで行われる＝
    /// 大会ごとの球場割当（<see cref="Nation.Tournaments.StadiumPlan"/>）と違い固定なので定数で持つ。
    ///
    /// <para><b>両翼が狭い変則球場</b>（95m）で中堅は深い（118m）。地方の県営級（100/122）より
    /// 両翼への一発が出やすい（実測 HR% 3.22% vs 2.03%）＝「地方の広い球場で勝ち上がった打線が
    /// 甲子園の浅い両翼で一発を打つ」が再現される。設計書12-stadiums の旧記述（甲子園は本塁打が
    /// 出にくい）は実寸では成立しないため、実測を採って設計書13を改訂済み。</para>
    ///
    /// <para><b>値は <c>data/stadiums.yaml</c> の <c>koshien</c> と一致させること</b>（不変条件#4）。
    /// <c>StadiumDefaultsConsistencyTests</c> が両者の一致を固定する。</para>
    /// </summary>
    public static Stadium Koshien { get; } = new()
    {
        Id = "koshien",
        Name = "甲子園",
        Tier = StadiumTier.National,
        LeftM = 95.0,
        RightM = 95.0,
        CenterM = 118.0,
        FenceHeightM = 2.6,
    };
}
