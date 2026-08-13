namespace KokoSim.Engine.Season.Events;

/// <summary>イベント分類（設計書03 §5 / 04 §3）。</summary>
public enum EventKind
{
    Fixed,   // カレンダー連動固定イベント
    Random,  // ランダムイベント（重み付き抽選）
    Choice,  // 選択肢イベント（監督判断で分岐, Phase 5で効果接続）
    System,  // 別経路で起きた出来事の提示（怪我・成長・気づき等・issue #610〜）。週次抽選には載せない
}

/// <summary>
/// イベントが起こす効果（issue #613）。<b>データに書く</b>＝新しい寄付イベントを足すのに
/// コードを触らせない。適用は Shell（財布を持っている側）が行い、engine はここまでを定義する。
///
/// <para>0 の軸は「動かさない」。金額は万円単位（<c>Manager.Funds</c> と同じ）。</para>
/// </summary>
/// <param name="Funds">部費の増減（万円）。運営費の財布。</param>
/// <param name="FacilityFund">施設基金の増減（万円）。建物にしか使えない財布（design-04 §1.4）。</param>
/// <param name="TrainingMult">練習効率の倍率（1.0＝変化なし）。issue #615。</param>
/// <param name="TrainingWeeks">練習効率の補正が効く週数（0＝補正なし）。</param>
/// <param name="TrainingTargetIsPlayer">true＝主役の1人だけ／false＝部全体。</param>
public sealed record EventEffect(
    double Funds = 0.0,
    double FacilityFund = 0.0,
    double TrainingMult = 1.0,
    int TrainingWeeks = 0,
    bool TrainingTargetIsPlayer = false)
{
    /// <summary>何も起こさない（既定）。</summary>
    public static readonly EventEffect None = new();

    /// <summary>期間つきの練習効率補正を持つか（倍率と週数の<b>両方</b>が要る）。</summary>
    public bool HasTrainingBoost => TrainingWeeks > 0 && TrainingMult != 1.0;

    /// <summary>1つでも動く軸があるか。</summary>
    public bool IsEmpty => Funds == 0.0 && FacilityFund == 0.0 && !HasTrainingBoost;
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

    /// <summary>
    /// 発火したときに起こす効果（issue #613）。既定は <see cref="EventEffect.None"/>＝何も起こさない
    /// （文言だけのイベントはこのまま）。
    /// </summary>
    public EventEffect Effect { get; init; } = EventEffect.None;
}
