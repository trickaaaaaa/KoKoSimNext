namespace KokoSim.Engine.Career.Draft;

/// <summary>
/// ドラフト（NPB指名）の係数（設計書20, YAML駆動＝不変条件#4）。
/// C# 既定値が Unity 実プレイの真値、data/draft.yaml は sim/テスト調整用
/// （既存の sim-vs-unity 係数分割に倣う）。注目度は「評価スコア」であり
/// 打席・打球・守備の解決確率ではない＝二層構造（不変条件#1）の対象外。
/// </summary>
public sealed record DraftCoefficients
{
    // --- 注目度の合成加重（能力 vs 実績） ---
    /// <summary>能力合成スコアの加重。</summary>
    public double AbilityWeight { get; init; } = 0.5;
    /// <summary>実績スコアの加重。</summary>
    public double PerformanceWeight { get; init; } = 0.5;

    /// <summary>能力合成に混ぜる隠し上限（才能キャップ）の比率（0=現在値のみ, 1=上限のみ）。
    /// プロは伸びしろ・天井を見る＝表示能力の丸写しを避ける（設計書20 §2.1）。</summary>
    public double CeilingBlend { get; init; } = 0.35;

    // --- 打者の実績スコア ---
    public double BatterOpsBase { get; init; } = 0.700;
    public double BatterOpsScale { get; init; } = 45.0;
    /// <summary>1試合あたり本塁打への加点。</summary>
    public double BatterHrPerGameScale { get; init; } = 6.0;
    /// <summary>実績が満点加重に達する打席数（これ未満は中立50へ収縮）。</summary>
    public int BatterMinPlateAppearances { get; init; } = 40;

    // --- 投手の実績スコア ---
    public double PitcherEraBase { get; init; } = 3.50;
    public double PitcherEraScale { get; init; } = 6.0;
    public double PitcherK9Base { get; init; } = 7.0;
    public double PitcherK9Scale { get; init; } = 2.0;
    /// <summary>実績が満点加重に達する対戦打者数（これ未満は中立50へ収縮）。</summary>
    public int PitcherMinBattersFaced { get; init; } = 60;

    // --- 予想順位バンド境界（注目度→バンド, 設計書20 §3.1） ---
    public double FirstRoundThreshold { get; init; } = 86.0;
    public double UpperRoundThreshold { get; init; } = 76.0;
    public double MiddleRoundThreshold { get; init; } = 66.0;
    /// <summary>候補入りの下限（＝下位/育成バンドの下限）。これ未満は候補でない。</summary>
    public double CandidateThreshold { get; init; } = 58.0;

    // --- 指名確定（10月最終週・3年のみ, 設計書20 §3.2） ---
    /// <summary>指名確率ロジスティックの中点（この注目度で指名確率50%）。</summary>
    public double NominationMidpoint { get; init; } = 64.0;
    /// <summary>指名確率ロジスティックの傾き。</summary>
    public double NominationSpread { get; init; } = 6.0;
}
