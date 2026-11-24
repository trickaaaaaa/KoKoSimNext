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
/// イベントの場面（issue #1516）。ノベル画面の背景を選ぶための<b>場所</b>で、判定には一切使わない（表示専用）。
/// data/events.yaml の <c>scene:</c> に書く。省略したイベントは <see cref="Ground"/>（グラウンド）。
/// </summary>
public enum EventScene
{
    Ground,    // グラウンド・練習
    Bench,     // 球場のベンチ・大会
    School,    // 校舎・教室
    Clubroom,  // 部室
    Office,    // 監督室・応接（スカウト・OB・後援会）
    Camp,      // 合宿所
    Hall,      // 体育館・式典
    Night,     // 夜のグラウンド（自主練）
}

/// <summary>
/// イベントが起こす効果（issue #613）。<b>データに書く</b>＝新しい寄付イベントを足すのに
/// コードを触らせない。適用は Shell（財布を持っている側）が行い、engine はここまでを定義する。
///
/// <para>0 の軸は「動かさない」。金額は万円単位（<c>Manager.Funds</c> と同じ）。</para>
/// </summary>
/// <param name="Funds">部費の増減（万円）。財布はこれ1つ（design-04 §1.4・issue #1082）。</param>
/// <param name="TrainingMult">練習効率の倍率（1.0＝変化なし）。issue #615。</param>
/// <param name="TrainingWeeks">練習効率の補正が効く週数（0＝補正なし）。</param>
/// <param name="TrainingTargetIsPlayer">true＝主役の1人だけ／false＝部全体。</param>
/// <param name="Trust">監督の信頼度の増減（0〜100で止める）。issue #1270 E1。</param>
/// <param name="Fame">監督の名声の増減（0〜100で止める・その場で動く）。issue #1270 E1／#1467。</param>
/// <param name="PressureNextMatch">次の公式戦1試合だけ、自校側のプレッシャー指数に足す点（issue #1467 P2）。</param>
public sealed record EventEffect(
    double Funds = 0.0,
    double TrainingMult = 1.0,
    int TrainingWeeks = 0,
    bool TrainingTargetIsPlayer = false,
    double Trust = 0.0,
    double Fame = 0.0,
    int PressureNextMatch = 0)
{
    /// <summary>何も起こさない（既定）。</summary>
    public static readonly EventEffect None = new();

    /// <summary>期間つきの練習効率補正を持つか（倍率と週数の<b>両方</b>が要る）。</summary>
    public bool HasTrainingBoost => TrainingWeeks > 0 && TrainingMult != 1.0;

    /// <summary>1つでも動く軸があるか。</summary>
    public bool IsEmpty => Funds == 0.0 && !HasTrainingBoost && Trust == 0.0 && Fame == 0.0
                           && PressureNextMatch == 0;
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
    /// この地方の学校でだけ発火する（issue #616。null＝地方を問わない）。
    /// 判定に使う地方は <see cref="EventScheduler.Region"/>。<b>Region が未設定（null）のときは
    /// 地方指定つきイベントは発火しない</b>＝ヘッドレスシムには地方の概念が無いので黙って混ざらない。
    /// </summary>
    public Nation.JapanRegion? RequiresRegion { get; init; }

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

    /// <summary>
    /// 監督の対応の選択肢（issue #945）。空なら選択肢なし（読むだけのイベント）。あれば会話の最後に3択を出し、
    /// 選んだ対応と当事者の性格・主将の統率力で好転/悪化を引き、<see cref="Outcomes"/> の会話を続けて結束を動かす。
    /// </summary>
    public IReadOnlyList<EventChoice> Choices { get; init; } = System.Array.Empty<EventChoice>();

    /// <summary>選択肢の結果の会話（対応×好転/悪化）。</summary>
    public IReadOnlyList<EventOutcome> Outcomes { get; init; } = System.Array.Empty<EventOutcome>();

    /// <summary>当事者（{player}）と相手（{other}）の選び方（issue #945）。既定＝誰でも・相手なし。</summary>
    public EventCast Cast { get; init; } = EventCast.Default;

    /// <summary>場面（ノベル画面の背景・issue #1516）。表示専用で発火・効果には関わらない。</summary>
    public EventScene Scene { get; init; } = EventScene.Ground;

    /// <summary>選択肢を持つか。</summary>
    public bool HasChoices => Choices.Count > 0;
}
