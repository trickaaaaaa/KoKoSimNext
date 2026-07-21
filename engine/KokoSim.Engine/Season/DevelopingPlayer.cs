using System;
using System.Linq;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Season;

/// <summary>
/// 習得済み変化球（設計書02 §2.2）。ランクは PitchRank レベルからのオフセットで持ち、
/// 育成で PitchRank が伸びれば全球種が底上げされる（投影時に絶対値へ変換）。
/// </summary>
public sealed record LearnedPitch(PitchType Type, int PowerOffset, int SharpnessOffset);

/// <summary>
/// 育成中の選手（可変）。表示層能力の現在値・経験値・才能上限（隠し）・疲労・成長タイプを持つ。
/// 試合出場時は Players.Player へ投影する（Phase 2 資産の再利用）。
/// </summary>
public sealed class DevelopingPlayer
{
    private static readonly int Count = AbilityKinds.All.Length;
    private static readonly int PositionCount = Enum.GetValues(typeof(FieldPosition)).Length;

    private readonly int[] _level = new int[Count];
    private readonly double[] _exp = new double[Count];
    private readonly int[] _cap = new int[Count];
    // 守備位置適性（設計書01 §1.1）: 全9ポジション個別。練習・実戦で上昇。既定は中庸50。
    private readonly int[] _aptitude = Enumerable.Repeat(50, PositionCount).ToArray();
    private readonly double[] _aptitudeExp = new double[PositionCount];
    private readonly int[] _aptitudeCap = Enumerable.Repeat(99, PositionCount).ToArray();

    /// <summary>
    /// ロスター内で一意・安定な選手ID（成績集計の帰属キー）。ロスター構築時に一度だけ連番付与し以後不変
    /// （乱数なし・決定論、不変条件#2）。投影時に Player.SourceId → ボックススコア SourceId へ伝播し、
    /// 通算/今大会成績へ紐づく。既定0＝未割当（相手校の生成選手など集計対象外）。
    /// </summary>
    public int Id { get; set; }

    public string Name { get; init; } = "部員";
    public int Grade { get; set; } = 1;             // 学年 1〜3
    public bool IsPitcher { get; init; }

    /// <summary>投打の利き（設計書01 §1.1c）。生成分布は 2B で実装、既定は右。</summary>
    public Handedness Throws { get; init; } = Handedness.Right;
    public Handedness Bats { get; init; } = Handedness.Right;

    /// <summary>隠し属性「投手経歴」（設計書01 §1.1b）。野手の変化球上振れの素。</summary>
    public bool HasPitcherBackground { get; init; }

    /// <summary>
    /// 習得済み変化球（ストレートは必修のため含めない, 設計書02 §2.2）。
    /// 投手1〜3球種／野手0〜1（投手経歴持ちは上振れ）。習得イベントで後から増える。
    /// </summary>
    public List<LearnedPitch> LearnedPitches { get; } = new();

    public GrowthType GrowthType { get; init; } = GrowthType.Standard;
    public double PersonalityFactor { get; init; } = 1.0; // 練習効率 0.8〜1.2

    /// <summary>精神力（設計書02 §3）。実戦出場でのみ成長する系（練習では伸びない, MatchGrowthModel）。</summary>
    public int Mental { get; set; } = 50;
    /// <summary>精神力の実戦経験値（設計書02 §5.3a, Q8）。必要expは能力値と同じ曲線。</summary>
    public double MentalExp { get; set; }
    /// <summary>精神力の隠し上限（Q8。生成時に才能ギャップ＋Late上振れで決まる）。</summary>
    public int MentalCap { get; set; } = 99;

    /// <summary>
    /// 捕手リード（配球の質, 設計書01 §2①）。天性（野球脳＝Mental相関）で素地が決まり、
    /// 捕手として実戦出場すると伸びる系（練習では伸びない, MatchGrowthModel）。捕手のみ意味を持つ。
    /// </summary>
    public int Lead { get; set; } = 50;
    /// <summary>捕手リードの実戦経験値（設計書02 §5.3a, Q8）。</summary>
    public double LeadExp { get; set; }
    /// <summary>捕手リードの隠し上限（Q8。野球脳と相関＋Late上振れ＝「名捕手の天井」）。</summary>
    public int LeadCap { get; set; } = 99;

    /// <summary>統率傾向（性格の一部, CHANGELOG 22b）。主将選定の軸（設計書09 §8）。</summary>
    public int Leadership { get; init; } = 50;

    /// <summary>
    /// 性格タイプ（設計書01 §1.1, CHANGELOG 22b）。②素直さ→指導の効き、③勤勉さ→PersonalityFactor、
    /// ④自己犠牲⇔目立ちたがり→試合姿勢へ効く。①統率は Leadership が担う。既定 Normal は無補正。
    /// </summary>
    public Players.Personality Personality { get; init; } = Players.Personality.Normal;

    /// <summary>
    /// 主将か（設計書09 §8）。ロスター上で1名のみ true。年度替わりで3年生が抜けたら選び直す（CaptainSelector）。
    /// 試合編成時に Team.Captain へ投影され、在場×統率力でプレッシャー負補正を緩和する。
    /// </summary>
    public bool IsCaptain { get; set; }

    /// <summary>
    /// 引退済みか（設計書03 §2: 夏の第17週で3年生が引退）。引退してもロスターからは除去せず
    /// このフラグを立てて残す（＝記録は残るが、練習・試合・選手一覧の対象から外れる）。
    /// 除去は年度替わり（4月）の卒業でまとめて行う。フラグ操作は <see cref="RosterLifecycle"/> に集約する。
    /// </summary>
    public bool IsRetired { get; set; }

    /// <summary>
    /// 背番号（設計書06 §3.3b: 1〜20＝ベンチ入り、0＝ベンチ外）。監督がメンバー設定画面で割り当てる。
    /// ロスター内で1〜20は一意（重複させない）。割当・検証は <see cref="UniformNumberAssigner"/> に集約する。
    /// 選抜（RosterTeamBuilder）とは独立に持つ純データで、乱数を含まない（不変条件#2）。
    /// </summary>
    public int UniformNumber { get; set; }

    /// <summary>特殊能力（設計書10）。生成時保有＋後天取得。set 可（習得・気づきイベント）。</summary>
    public Players.SkillSet Skills { get; set; } = Players.SkillSet.Empty;

    // 伸びしろ（隠し・分野別成長効率倍率, 設計書01 §1.1 / 02 §5.1）。現在値と独立。既定1.0で中立。
    // set 可: 伸び悩みイベント（設計書03 §5.5）が恒久的に下げることがある。
    public double PitchingGrowth { get; set; } = 1.0;
    public double BattingGrowth { get; set; } = 1.0;
    public double DefenseGrowth { get; set; } = 1.0;

    /// <summary>練習計画（設計書03 §3.1）。null = お任せ（IsPitcher で自動選択）。UIから設定・コピー。</summary>
    public TrainingPlan? Plan { get; set; }

    /// <summary>調子の内部連続値（-1〜+1, 設計書02 §3.3）。週次更新。表示・投影は FormModel.Quantize で5段階化。</summary>
    public double ConditionValue { get; set; }

    // --- 怪我（設計書03 §3.5: 段階制・常に可視） ---
    public InjurySeverity Injury { get; set; } = InjurySeverity.None;
    public InjurySite InjurySite { get; set; }
    /// <summary>傷病の種類（表示名は data/injuries.yaml。None=種類なしの旧データ）。</summary>
    public InjuryType InjuryType { get; set; } = InjuryType.None;
    public int InjuryWeeksRemaining { get; set; }
    /// <summary>怪我耐性（隠し, 1〜100。高いほど発生しにくい）。</summary>
    public double InjuryResistance { get; init; } = 50.0;

    // --- 成長イベント状態（設計書03 §5.5） ---
    /// <summary>スランプ残り週（>0 の間は一時的な能力ダウン。立て直せる）。</summary>
    public int SlumpWeeks { get; set; }
    /// <summary>イップス（送球/制球の不調, 恒久寄り。克服イベントで解消）。</summary>
    public bool HasYips { get; set; }

    public int Level(AbilityKind k) => _level[(int)k];
    public int Cap(AbilityKind k) => _cap[(int)k];
    public double Exp(AbilityKind k) => _exp[(int)k];

    public void SetLevel(AbilityKind k, int value) => _level[(int)k] = value;
    public void SetCap(AbilityKind k, int value) => _cap[(int)k] = value;
    public void AddExp(AbilityKind k, double value) => _exp[(int)k] += value;
    public void ConsumeExp(AbilityKind k, double value) => _exp[(int)k] -= value;
    public void IncrementLevel(AbilityKind k) => _level[(int)k]++;

    public int Aptitude(FieldPosition pos) => _aptitude[(int)pos];
    public void SetAptitude(FieldPosition pos, int value) => _aptitude[(int)pos] = value;

    // 守備位置適性の育成（練習でポジション別に上昇, 設計書03 §3.1）。レベル制と同じ必要exp曲線を使う。
    public double AptitudeExp(FieldPosition pos) => _aptitudeExp[(int)pos];
    public int AptitudeCap(FieldPosition pos) => _aptitudeCap[(int)pos];
    public void SetAptitudeCap(FieldPosition pos, int value) => _aptitudeCap[(int)pos] = value;
    public void AddAptitudeExp(FieldPosition pos, double value) => _aptitudeExp[(int)pos] += value;
    public void ConsumeAptitudeExp(FieldPosition pos, double value) => _aptitudeExp[(int)pos] -= value;
    public void IncrementAptitude(FieldPosition pos) => _aptitude[(int)pos]++;

    /// <summary>能力が属する分野の伸びしろ倍率（設計書02 §5.1: 経験値式に乗算）。</summary>
    public double GrowthMultiplier(AbilityKind k)
    {
        if (AbilityKinds.IsPitching(k)) return PitchingGrowth;
        if (AbilityKinds.IsDefense(k)) return DefenseGrowth;
        return BattingGrowth; // 打撃・走塁系は打撃分野の伸びしろに紐づける（2Bで細分化可）
    }

    // 育成曲線の観測に使う「実際に練習で伸びる中核能力」。
    // 弾道・選球眼は練習メニューに伸長経路が無い型属性のため平均から除く（OPEN-QUESTIONS Q4）。
    private static readonly AbilityKind[] BatterCore =
    {
        AbilityKind.Contact, AbilityKind.Power, AbilityKind.Speed,
        AbilityKind.ArmStrength, AbilityKind.Fielding, AbilityKind.Catching,
    };

    /// <summary>中核能力の平均レベル。育成曲線の観測に使う。</summary>
    public double AverageLevel()
    {
        var kinds = IsPitcher ? AbilityKinds.Pitching : BatterCore;
        var sum = 0.0;
        foreach (var k in kinds) sum += _level[(int)k];
        return sum / kinds.Length;
    }
}
