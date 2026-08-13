namespace KokoSim.Engine.Season.Events;

/// <summary>イベント分類（設計書03 §5 / 04 §3）。</summary>
public enum EventKind
{
    Fixed,   // カレンダー連動固定イベント
    Random,  // ランダムイベント（重み付き抽選）
    Choice,  // 選択肢イベント（監督判断で分岐, Phase 5で効果接続）
}

/// <summary>
/// イベント定義（データ駆動, 設計書04 §3.1）。Phase 3 は発火の基盤のみで、効果適用は Phase 5 で接続する。
/// </summary>
public sealed record GameEvent
{
    public required string Id { get; init; }
    public EventKind Kind { get; init; } = EventKind.Random;

    /// <summary>Fixed の発火週（0〜49）。Random/Choice では null。</summary>
    public int? CalendarWeek { get; init; }

    /// <summary>Random 抽選の重み。</summary>
    public double Weight { get; init; } = 1.0;

    /// <summary>同一イベントのクールダウン週数。</summary>
    public int CooldownWeeks { get; init; } = 4;

    /// <summary>発火時に立てる年次フラグ（あれば）。</summary>
    public string? SetsAnnualFlag { get; init; }

    /// <summary>この年次フラグが立っていると発火しない。</summary>
    public string? RequiresNotAnnualFlag { get; init; }

    /// <summary>
    /// 通知フィード1行の要約（従来の <c>text:</c>）。ノベル画面で読み終えたあとも履歴として残る行なので、
    /// バリエーションを持たせず<b>常に同じ1行</b>にする（後から一覧で追えることを優先）。
    /// </summary>
    public string Text { get; init; } = "";

    /// <summary>
    /// ノベル提示用の会話（issue #609）。<b>会話まるごと</b>で N 通り持ち、発火時に1つだけ選ぶ。
    /// 空なら <see cref="Text"/> の1行だけを地の文として見せる（旧データの後方互換）。
    /// </summary>
    public IReadOnlyList<EventScript> Variants { get; init; } = System.Array.Empty<EventScript>();
}
