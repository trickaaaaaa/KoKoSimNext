using System.Collections.Generic;

namespace KokoSim.Engine.Match.Field;

/// <summary>球場の格（設計書13-stadiums §1）。序盤=市営級、準決勝/決勝=県営級、聖地=全国級。</summary>
public enum StadiumTier
{
    Municipal,    // 市営級（狭め・低フェンス。序盤ラウンド）
    Prefectural,  // 県営級（広め。準決勝・決勝）
    National,     // 全国級（甲子園・神宮）
}

/// <summary>
/// 球場（設計書13-stadiums ①物理層＋②環境の風）。寸法をパラメータ化し FieldGeometry を生成する。
/// ②環境は風だけ実装済み（issue #1614）。<see cref="WindBearingDeg"/> が球場固有の風の癖（甲子園の浜風のみ）。
/// ③ビジュアルは後回し。エンジン純度維持（不変条件#3）: 生成・ロードは KokoSim.Config 側で行う。
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

    /// <summary>
    /// 球場固有の風の癖（issue #1614・設計書13-stadiums §5改訂）: 風向きの平均バイアス[度]
    /// （<see cref="FieldGeometry.FenceDistance"/> と同じ方位角の規約。0=中堅方向、+=一塁/右翼側、−=三塁/左翼側）。
    /// null＝癖なし（<see cref="Game.WindModel"/> が向きを一様乱数で引く＝平均すると無風と同じ）。
    /// 2026-11-28 オーナー決定: 最初は甲子園の浜風（右翼から左翼へ＝-90°）だけ設定し、他の球場は省略する。
    /// </summary>
    public double? WindBearingDeg { get; init; }

    /// <summary>
    /// 3D表示に使う球場モデルの名前（<c>data/stadiums/&lt;Model&gt;.obj</c>。issue #904）。null なら汎用の
    /// standard を判定寸法へ伸縮して描く。<b>表示専用</b>＝判定にも乱数にも使わない。同じ球場を使う複数の id
    /// （神宮大会と東西東京の決勝）は同じモデル名を持つ。
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// 屋内（ドーム）か（issue #1020・data/stadiums.yaml の <c>indoor: true</c>）。true なら天候・気温
    /// （<see cref="Game.WeatherModel"/>・#971 の月の表）を効かせない＝空気密度も投手の消耗も基準のまま。
    /// <see cref="ToFieldGeometry"/> で <see cref="FieldGeometry.Indoor"/> へ写す。
    /// </summary>
    public bool Indoor { get; init; }

    /// <summary>寸法から判定用の <see cref="FieldGeometry"/> を生成する。</summary>
    public FieldGeometry ToFieldGeometry() => new()
    {
        LeftFenceM = LeftM,
        RightFenceM = RightM,
        CenterFenceM = CenterM,
        FenceHeightM = FenceHeightM,
        Indoor = Indoor,
        WindBiasBearingDeg = WindBearingDeg,
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
        Model = "koshien",
        WindBearingDeg = -90.0,   // 浜風＝右翼から左翼へ（issue #1614・2026-11-28 オーナー決定）
    };

    /// <summary>
    /// 明治神宮野球場（issue #904）。明治神宮大会と、東京（東東京・西東京の夏、秋の都大会）の準決勝・決勝の球場。
    /// <b>値は <c>data/stadiums.yaml</c> の <c>jingu</c> と一致させること</b>（不変条件#4。
    /// <c>StadiumLoaderTests.JinguDefault_MatchesTheRepositoryYaml</c> が固定する）。
    /// </summary>
    public static Stadium Jingu { get; } = new()
    {
        Id = "jingu",
        Name = "明治神宮野球場",
        Tier = StadiumTier.National,
        LeftM = 97.5,
        RightM = 97.5,
        CenterM = 120.0,
        FenceHeightM = 3.3,
        Model = "jingu",
    };

    /// <summary>
    /// 横浜スタジアム（issue #956）。神奈川の夏の準決勝・決勝の球場（秋は保土ケ谷＝<see cref="AutumnFinals"/>・issue #1020）。
    /// <b>値は <c>data/stadiums.yaml</c> の <c>kanagawa_final</c> と一致させること</b>（不変条件#4。
    /// <c>StadiumLoaderTests.YokohamaDefault_MatchesTheRepositoryYaml</c> が固定する）。
    /// </summary>
    public static Stadium Yokohama { get; } = new()
    {
        Id = "kanagawa_final",
        Name = "横浜スタジアム",
        Tier = StadiumTier.Prefectural,
        LeftM = 94.2,
        RightM = 94.2,
        CenterM = 117.7,
        FenceHeightM = 5.0,
        Model = "yokohama",
    };

    /// <summary>
    /// 北海道ボールパーク（issue #959。実在はエスコンフィールドHOKKAIDO・命名権を外した名前）。
    /// 北海道（北北海道・南北海道）の夏の準決勝・決勝の球場（秋は札幌ドーム＝<see cref="AutumnFinals"/>）。左右非対称（左97・右99）。
    /// <b>値は <c>data/stadiums.yaml</c> の <c>kita_hokkaido_final</c> と一致させること</b>（不変条件#4。
    /// <c>StadiumLoaderTests.HokkaidoBallparkDefault_MatchesTheRepositoryYaml</c> が固定する）。
    /// </summary>
    public static Stadium HokkaidoBallpark { get; } = new()
    {
        Id = "kita_hokkaido_final",
        Name = "北海道ボールパーク",
        Tier = StadiumTier.Prefectural,
        LeftM = 97.0,
        RightM = 99.0,
        CenterM = 121.0,
        FenceHeightM = 2.8,
        Model = "hokkaido_ballpark",
    };

    /// <summary>
    /// 宮城球場（issue #961。楽天の本拠地・命名権を外した名前）。宮城の夏の準決勝・決勝の球場（秋は仙台市民球場＝<see cref="AutumnFinals"/>）。
    /// フェンス高は資料が割れて未確認のため2.8m（監督決定）。
    /// <b>値は <c>data/stadiums.yaml</c> の <c>miyagi_final</c> と一致させること</b>（不変条件#4。
    /// <c>StadiumLoaderTests.MiyagiDefault_MatchesTheRepositoryYaml</c> が固定する）。
    /// </summary>
    public static Stadium Miyagi { get; } = new()
    {
        Id = "miyagi_final",
        Name = "宮城球場",
        Tier = StadiumTier.Prefectural,
        LeftM = 100.1,
        RightM = 100.1,
        CenterM = 122.0,
        FenceHeightM = 2.8,
        Model = "miyagi",
    };

    /// <summary>
    /// 広島市民球場（issue #963。カープの本拠地・命名権を外した名前）。広島の夏の準決勝・決勝の球場（秋はみよし＝<see cref="AutumnFinals"/>）。
    /// 左右非対称（左101・右100）。フェンス高は代表値の3.0m（監督決定）。
    /// <b>値は <c>data/stadiums.yaml</c> の <c>hiroshima_final</c> と一致させること</b>（不変条件#4。
    /// <c>StadiumLoaderTests.HiroshimaDefault_MatchesTheRepositoryYaml</c> が固定する）。
    /// </summary>
    public static Stadium Hiroshima { get; } = new()
    {
        Id = "hiroshima_final",
        Name = "広島市民球場",
        Tier = StadiumTier.Prefectural,
        LeftM = 101.0,
        RightM = 100.0,
        CenterM = 122.0,
        FenceHeightM = 3.0,
        Model = "hiroshima",
    };

    /// <summary>
    /// 秋季県大会の準決勝・決勝の球場（issue #1020・2026-11-15 監督決定）。県ID → 球場。夏と会場が違う県だけを持ち、
    /// 載っていない県の秋は汎用の県営級（<see cref="PrefLarge"/>。東京は夏秋とも <see cref="Jingu"/>）。
    /// 3D モデルを持たない会場は standard を判定寸法へ伸縮して描く（見た目は汎用・名前と寸法は実在の会場）。
    /// <b>値は <c>data/stadiums.yaml</c> の <c>&lt;県&gt;_autumn_final</c> と一致させること</b>（不変条件#4。
    /// <c>StadiumLoaderTests.AutumnFinalDefaults_MatchTheRepositoryYaml</c> が全件を固定する）。
    /// 札幌ドームは屋内（<see cref="Indoor"/>）。
    /// </summary>
    public static IReadOnlyDictionary<int, Stadium> AutumnFinals { get; } = new Dictionary<int, Stadium>
    {
        [0] = AutumnFinal("hokkaido_autumn_final", "札幌ドーム", 100, 122, 5.75) with { Indoor = true },   // 秋季全道大会
        [3] = AutumnFinal("miyagi_autumn_final", "仙台市民球場", 100, 122),
        [6] = AutumnFinal("fukushima_autumn_final", "いわきグリーンスタジアム", 100, 122),
        [7] = AutumnFinal("ibaraki_autumn_final", "ひたちなか市民球場", 100, 122),
        [8] = AutumnFinal("tochigi_autumn_final", "宇都宮清原球場", 100, 122),
        [9] = AutumnFinal("gunma_autumn_final", "桐生球場", 100, 122),
        [11] = AutumnFinal("chiba_autumn_final", "千葉県総合スポーツセンター野球場", 98, 122),
        [13] = AutumnFinal("kanagawa_autumn_final", "保土ケ谷球場", 95, 120),
        [19] = AutumnFinal("nagano_autumn_final", "長野オリンピックスタジアム", 99.1, 122, 4.0),
        [22] = AutumnFinal("aichi_autumn_final", "小牧市民球場", 92, 120),
        [23] = AutumnFinal("mie_autumn_final", "津市営球場", 91, 119),
        [27] = AutumnFinal("hyogo_autumn_final", "明石公園第一野球場", 100, 122),
        [30] = AutumnFinal("tottori_autumn_final", "米子市民球場", 99.1, 122),
        [33] = AutumnFinal("hiroshima_autumn_final", "みよし運動公園野球場", 100, 122),
        [34] = AutumnFinal("yamaguchi_autumn_final", "下関球場", 100, 122),
        [39] = AutumnFinal("fukuoka_autumn_final", "久留米市野球場", 98, 122),
        [46] = AutumnFinal("okinawa_autumn_final", "名護市営球場", 100, 122),
    };

    /// <summary>
    /// 秋の決勝会場（左右対称・県営級・3Dモデルなし）。フェンス高の資料が無い会場は 3.0m＝汎用の県営級（<see cref="PrefLarge"/>）と同じ
    /// （2026-11-15 監督決定。YAML 側も 3.0m を明記している）。
    /// </summary>
    private static Stadium AutumnFinal(string id, string name, double wing, double center, double fenceHeight = 3.0) => new()
    {
        Id = id,
        Name = name,
        Tier = StadiumTier.Prefectural,
        LeftM = wing,
        RightM = wing,
        CenterM = center,
        FenceHeightM = fenceHeight,
    };

    /// <summary>
    /// 地方大会（県大会）の汎用ティア球場（設計書13 §1・issue #41系「地方大会の球場割当」）。
    /// 47県固有の実在球場は追わず、data/stadiums.yaml の汎用タイプ3種を序盤/準決勝・決勝で使い分ける
    /// （<see cref="RegionalStadiumTier"/>）。<b>値は data/stadiums.yaml と一致させること</b>（不変条件#4。
    /// <c>StadiumLoaderTests.RegionalTierDefaults_MatchTheRepositoryYaml</c> が固定する）。
    /// <para>issue #976（2026-11-15）: 市営級2種を 両翼+1m・中堅+1m・フェンス高 2.0→3.0m（旧 91/115/2.0・93/116/2.0）。
    /// 県大会の試合の大半を占めるこの2種が本塁打の出すぎの最大の源だったため、柵越えの一部をフェンス直撃の長打へ振り替える。
    /// 「狭い」性格（標準 95/118 より狭い）は残す。</para>
    /// </summary>
    public static Stadium MunicipalSmall01 { get; } = new()
    {
        Id = "municipal_small_01",
        Name = "市営球場（狭い）",
        Tier = StadiumTier.Municipal,
        LeftM = 92.0,
        RightM = 92.0,
        CenterM = 116.0,
        FenceHeightM = 3.0,
    };

    /// <summary>同上（序盤ラウンドのもう一方）。</summary>
    public static Stadium MunicipalSmall02 { get; } = new()
    {
        Id = "municipal_small_02",
        Name = "市営球場（やや狭い）",
        Tier = StadiumTier.Municipal,
        LeftM = 94.0,
        RightM = 94.0,
        CenterM = 117.0,
        FenceHeightM = 3.0,
    };

    /// <summary>同上（準決勝・決勝＝県営級。地方の県営級の実測基準として design-02/13 で参照される寸法）。</summary>
    public static Stadium PrefLarge { get; } = new()
    {
        Id = "pref_large_01",
        Name = "県営球場（広い）",
        Tier = StadiumTier.Prefectural,
        LeftM = 100.0,
        RightM = 100.0,
        CenterM = 122.0,
        FenceHeightM = 3.0,
    };
}
