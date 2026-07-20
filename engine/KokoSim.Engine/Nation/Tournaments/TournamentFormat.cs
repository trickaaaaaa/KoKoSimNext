namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>ステージ型（設計書05 §1.5: この3種のみ）。</summary>
public enum StageType
{
    Knockout,    // トーナメント（シード・不戦勝処理つき）
    RoundRobin,  // 総当たりリーグ（勝点制。同率は直接対決→得失点）
    GroupSplit,  // 地区ブロック分割（各ブロックで子ステージ→上位が次へ）
}

/// <summary>県内地区の割当方式（設計書05 §2.2 / CHANGELOG 28）。</summary>
public enum DistrictAssignment
{
    None,        // 地区なし（県一括）
    Geographic,  // 地理固定割（学校が地区を保持）
    Draw,        // 抽選制（大会時に振り分け）
}

/// <summary>group_split のブロック分け方（地理割 / 抽選）。</summary>
public enum GroupingMode
{
    Geographic,
    Draw,
}

/// <summary>敗者復活（設計書05 §1.5 / CHANGELOG 27）。各ブロックで代表決定戦を行う。</summary>
public sealed record LoserBracketRule(bool Enabled, int? Advance);

/// <summary>group_split の子ステージ（各ブロック内で実行する下位トーナメント/リーグ）。</summary>
public sealed record ChildStage(StageType Type, int? TeamsPerGroup);

/// <summary>1ステージの定義（型ごとに使うフィールドが異なる）。</summary>
public sealed record StageFormat
{
    public string Name { get; init; } = "";
    public StageType Type { get; init; }

    /// <summary>knockout: 3位決定戦を行うか（準決勝敗退2校で3位を決める）。</summary>
    public bool ThirdPlaceMatch { get; init; }

    /// <summary>group_split: ブロック数。</summary>
    public int? Groups { get; init; }
    public GroupingMode Grouping { get; init; } = GroupingMode.Geographic;
    public ChildStage? Child { get; init; }
    /// <summary>各ブロックからの進出数（null=1）。</summary>
    public int? AdvancePerGroup { get; init; }
    public LoserBracketRule? LoserBracket { get; init; }

    /// <summary>参加校数の実数（county-checkや UI 表示用。null=前ステージからの持ち越し）。</summary>
    public int? Entries { get; init; }
}

/// <summary>
/// 県ごとの大会フォーマット（設計書05 §1.5, data/pref-formats/*.yaml）。基準年スナップショット。
/// 形式差がそのまま育成環境の差になる（リーグ戦の県は公式戦数が多く実戦経験を稼ぎやすい）。
/// </summary>
public sealed record PrefFormat
{
    public string Pref { get; init; } = "";
    public int SnapshotYear { get; init; }
    /// <summary>所属地区（regional-tournaments.yaml の region キー）。</summary>
    public string Region { get; init; } = "";
    public IReadOnlyList<string> Districts { get; init; } = System.Array.Empty<string>();
    public DistrictAssignment DistrictAssignment { get; init; } = DistrictAssignment.None;
    /// <summary>地区大会への出場枠（§1.5 の regional_berths）。</summary>
    public int RegionalBerths { get; init; } = 1;
    /// <summary>夏甲子園校の予選免除・県大会推薦（神奈川・兵庫。CHANGELOG 27）。</summary>
    public bool SeedExemption { get; init; }
    public IReadOnlyList<StageFormat> Stages { get; init; } = System.Array.Empty<StageFormat>();

    /// <summary>使用球場プラン（設計書13-stadiums §2-2）。序盤=early 抽選・準決勝/決勝=final。既定は割当なし。</summary>
    public StadiumPlan Stadiums { get; init; } = StadiumPlan.None;
}
