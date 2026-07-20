using System.Collections.Generic;
using System.Linq;

namespace KokoSim.Engine.Players;

/// <summary>スキルの分類（設計書10 §2）。</summary>
public enum SkillCategory
{
    Batting,       // 打撃系（挙動の癖・スタイル）
    Pitching,      // 投手系（挙動の癖）
    Fielding,      // 守備系（質的な癖）
    Team,          // チーム系（本人以外へ波及）
    Constitution,  // 体質・成長系（能力値でない体の性質）
    Special,       // 目玉（少数の華・極稀）
}

/// <summary>
/// 特殊能力（設計書10）。**有無のみ（ランクなし）** で、強弱は連続パラメータが担う。
/// パラメータ・精神力システムで表現できるものは含めない（二重計上禁止の鉄則）:
/// 走塁の巧拙・肩/送球精度・左右相性・チャンス/逆境/大舞台などは能力値や PressureModel が担当。
/// 残すのは非線形挙動・質的スタイル・チーム波及・体質だけ。効果は控えめ。
/// </summary>
public enum Skill
{
    // --- 打撃系 ---
    SlowStarterBat,    // 尻上がり: 打席を重ねるほど当たりが出る（試合内の非線形）
    Streaky,           // ムラっけ: 好不調の振れ幅が大きい（両刃）
    SprayHitter,       // 広角打法: 打球方向が広くシフトを無効化しやすい（方向の質、打力ではない）
    FirstPitchSwinger, // 初球から振る: 初球スイング傾向（積極性）
    Grinder,           // 粘り打ち: ファウルで粘り球数を稼ぐ（行動特性）

    // --- 投手系 ---
    SlowStarterPitch,  // 尻上がり: イニングを追うごとに安定
    SecondTimeThrough, // 打者一巡: 2巡目以降に崩れやすい（対戦回数依存の癖・負）
    EffectivelyWild,   // 荒れ球: 軌道が不安定で打ちにくいが四球も増える（両刃）
    DeceptiveBall,     // クセ球: 見た目より打ちにくい球質（物理では出しにくい質的個性）

    // --- 守備系 ---
    DoublePlayArtist,  // 併殺の名手: 二塁経由の送球が速い（連携動作の質）
    MasterCatcher,     // 名捕手: 配球の質を上げる（リードという非数値の技）

    // --- チーム系（本人以外へ波及） ---
    Moodmaker,         // ムードメーカー: チーム士気の底上げ
    SpiritualPillar,   // 精神的支柱: 主将適性（設計書09 §8の統率力に寄与）
    PracticeLeader,    // 練習リーダー: 周囲の練習効率に微ボーナス
    RoleModel,         // お手本: 同ポジション後輩の成長を促進

    // --- 体質・成長系 ---
    Diligent,          // 練習熱心: 経験値効率アップ
    Lazy,              // サボり癖: 経験値効率ダウン（負）
    Durable,           // 故障しにくい: 怪我判定を軽減
    InjuryProne,       // ケガしやすい: 怪我判定が増える（負）

    // --- 目玉（極稀） ---
    Monster,           // 怪物: 複数分野に強力補正の超レア
    SubmarineMastery,  // アンダースローの妙: 特定投法×質の職人技
}

/// <summary>
/// 選手の保有スキル集合（設計書10 §0/§6）。可視スキルと隠しスキルを分けて持つ。
/// 隠しスキル: 能力表にもスキル欄にも出ず、条件で発現する眠った素質。育成眼で気配を察知できる。
/// 効果判定は可視/隠しを区別しない（<see cref="Has"/>）。UI表示だけが <see cref="Visible"/> を使う。
/// </summary>
public sealed record SkillSet
{
    private readonly HashSet<Skill> _visible;
    private readonly HashSet<Skill> _hidden;

    public SkillSet(IEnumerable<Skill>? visible = null, IEnumerable<Skill>? hidden = null)
    {
        _visible = visible is null ? new HashSet<Skill>() : new HashSet<Skill>(visible);
        _hidden = hidden is null ? new HashSet<Skill>() : new HashSet<Skill>(hidden);
    }

    /// <summary>可視スキル（選手詳細のスキル欄に出る）。</summary>
    public IReadOnlyCollection<Skill> Visible => _visible;

    /// <summary>隠しスキル（気づきイベントで部分開示される眠った素質）。</summary>
    public IReadOnlyCollection<Skill> Hidden => _hidden;

    /// <summary>効果を持つか（可視・隠しを問わない）。</summary>
    public bool Has(Skill skill) => _visible.Contains(skill) || _hidden.Contains(skill);

    public bool IsHidden(Skill skill) => _hidden.Contains(skill) && !_visible.Contains(skill);

    public bool IsEmpty => _visible.Count == 0 && _hidden.Count == 0;

    /// <summary>隠しスキルを可視化する（気づき・発現イベント用, 設計書03 §5.6）。</summary>
    public SkillSet Reveal(Skill skill)
    {
        if (!_hidden.Contains(skill)) return this;
        return new SkillSet(_visible.Append(skill), _hidden.Where(s => s != skill));
    }

    /// <summary>空集合（スキルなし）。</summary>
    public static SkillSet Empty { get; } = new();
}
