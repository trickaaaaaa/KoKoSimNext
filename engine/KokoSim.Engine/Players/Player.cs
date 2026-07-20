using KokoSim.Engine.Match.Field;

namespace KokoSim.Engine.Players;

/// <summary>
/// 試合に出場する1選手。打撃・走塁・守備の能力を一体で持ち、必要に応じて投手能力(Pitching)を伴う。
/// 既存の解決器へは ToBatter()/ToFielder() で投影して再利用する。
/// </summary>
public sealed record Player
{
    public string Name { get; init; } = "選手";
    public FieldPosition Position { get; init; } = FieldPosition.CenterField;

    /// <summary>
    /// 投影元の育成選手ID（成績集計の帰属キー）。相手校の生成選手（DevelopingPlayer 不在）は null。
    /// 乱数を含まない純データ（不変条件#2）。ボックススコアの SourceId 経由で通算/今大会成績へ紐づく。
    /// </summary>
    public int? SourceId { get; init; }

    /// <summary>
    /// 背番号（ベンチ入りメンバの識別・表示用）。1〜=ベンチ入り、0=番号なし（ベンチ外/未割当）。
    /// 自校は DevelopingPlayer.UniformNumber を焼き込み、相手校は生成時に採番する。乱数を含まない純データ。
    /// </summary>
    public int UniformNumber { get; init; }

    /// <summary>
    /// 学年（1〜3）。表示用（大会展望の登録メンバー・試合盤面のプロフィール）。0=未設定。
    /// 自校は DevelopingPlayer.Grade を焼き込み、相手校は生成時に採番する。乱数を含まない純データ。
    /// </summary>
    public int Grade { get; init; }

    /// <summary>投げ手・打ち手の利き（設計書01 §1.1c）。</summary>
    public Handedness Throws { get; init; } = Handedness.Right;
    public Handedness Bats { get; init; } = Handedness.Right;

    public int Contact { get; init; } = 50;
    public int Power { get; init; } = 50;
    public int LaunchTendency { get; init; } = 50;
    public int Discipline { get; init; } = 50;
    public int Speed { get; init; } = 50;
    public int ArmStrength { get; init; } = 50;

    /// <summary>送球精度（肩＝ArmStrengthから分離, 設計書01 §1.2）。</summary>
    public int ThrowAccuracy { get; init; } = 50;
    public int Fielding { get; init; } = 50;
    public int Catching { get; init; } = 50;

    /// <summary>捕手リード（配球の質, 設計書01 §2①）。高いほど良い配球。捕手のみ意味を持つ。50=平均で恒等。</summary>
    public int Lead { get; init; } = 50;

    /// <summary>走塁系連続パラメータ（設計書02 §4）。旧走塁系スキルの後継。</summary>
    public int Bunt { get; init; } = 50;
    public int Steal { get; init; } = 50;
    public int Baserunning { get; init; } = 50;

    /// <summary>精神力（設計書02 §3）。プレッシャー指数との積で場面補正になる。50=補正なし。</summary>
    public int Mental { get; init; } = 50;

    /// <summary>統率傾向（性格の一部, CHANGELOG 22b）。主将の統率力 = 統率傾向×精神力（設計書09 §8）。</summary>
    public int Leadership { get; init; } = 50;

    /// <summary>性格タイプ（設計書01 §1.1）。表示・采配AI用のラベル。試合効果は下の解決済みスカラーに焼き込む。</summary>
    public Personality Personality { get; init; } = Personality.Normal;

    /// <summary>性格④の犠打/進塁打 成功率加算（自己犠牲+/目立ち−）。投影時に性格表から解決。既定0＝無補正。</summary>
    public double BuntSuccessBonus { get; init; }

    /// <summary>性格④の得点圏強攻での長打質(Power)倍率（目立ちたがり>1/自己犠牲<1）。既定1.0＝無補正。</summary>
    public double ChanceHitFactor { get; init; } = 1.0;

    /// <summary>隠し属性「投手経歴」（設計書01 §1.1b）。持つ野手は変化球保有数・質が上振れ。</summary>
    public bool HasPitcherBackground { get; init; }

    /// <summary>特殊能力（設計書10）。有無フラグ制。可視/隠しを内包。既定は空（従来挙動と一致）。</summary>
    public SkillSet Skills { get; init; } = SkillSet.Empty;

    /// <summary>調子（設計書02 §3.3）。週次の波を Season 層が更新し、投影時に載せる。試合中は補正係数として作用。</summary>
    public Condition Condition { get; init; } = Condition.Normal;

    /// <summary>怪我の段階（設計書03 §3.5, 常に可視）。能力ダウンは投影時に一律係数で反映済み。采配判断用の表示。</summary>
    public InjurySeverity Injury { get; init; } = InjurySeverity.None;

    /// <summary>投手能力（投手のみ非null）。</summary>
    public PitcherAttributes? Pitching { get; init; }

    public BatterAttributes ToBatter() => new()
    {
        Contact = Contact,
        Power = Power,
        LaunchTendency = LaunchTendency,
        Discipline = Discipline,
        Speed = Speed,
        Bats = Bats,
    };

    public FielderAttributes ToFielder() => new()
    {
        Speed = Speed,
        ArmStrength = ArmStrength,
        ThrowAccuracy = ThrowAccuracy,
        Fielding = Fielding,
        Catching = Catching,
        Lead = Lead,
    };
}
