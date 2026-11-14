using System.Collections.Generic;

namespace KokoSim.Engine.Nation;

/// <summary>設立区分（設計書05 §2.2）。</summary>
public enum Establishment { Public, Private }

/// <summary>伝統区分（名声の初期値・減衰傾向）。</summary>
public enum Tradition { Storied, Midlevel, Emerging } // 古豪 / 中堅 / 新興

/// <summary>
/// 架空校（設計書05 §2）。可変の強さ・名声を持つAI校。プレイヤー校もこの型で表す。
/// 強さは 0〜100 の連続値で持ち、ティアは強さから導出する。
/// </summary>
public sealed class School
{
    public required int Id { get; init; }
    private string _name = "";

    /// <summary>
    /// 校名（全国生成の校は「〜高校」込み）。作るときに入れた名前が<see cref="GeneratedName"/>になり、
    /// プレイヤーの改名（issue #946）は <see cref="Rename"/> で差し替える＝生成時の名前はいつでも戻せる。
    /// 校名は乱数・シード・指紋に入れない（改名の前後で試合結果は同じ）。
    /// </summary>
    public required string Name
    {
        get => _name;
        init { _name = value; GeneratedName = value; }
    }

    /// <summary>作ったとき（全国生成・転任で追加）の名前。改名を取り消すとこれに戻る。</summary>
    public string GeneratedName { get; private set; } = "";

    /// <summary>改名する（issue #946）。null なら生成時の名前に戻す。</summary>
    public void Rename(string? fullName) => _name = string.IsNullOrEmpty(fullName) ? GeneratedName : fullName;
    public required int PrefectureId { get; init; }
    public Establishment Establishment { get; init; }
    public Tradition Tradition { get; init; }

    /// <summary>チーム総合力(0〜100)。育成・世代交代・スカウトで年々変動する。</summary>
    public double Strength { get; set; }

    /// <summary>名声（全国区, 設計書04 §1.2）。新入生の質・シード等に影響。</summary>
    public double Fame { get; set; }

    /// <summary>校風＝采配の個性（設計書11 §3）。敵AIの手を偏らせる。</summary>
    public Match.Tactics.SchoolStyle Style { get; set; } = Match.Tactics.SchoolStyle.Standard;

    /// <summary>
    /// 監督傾向＝采配の癖（issue #55, 決定1: A-1）。校風と別軸で0〜2個まで重なる。空＝傾向なし。
    /// Nation は決定論生成（保存されず毎回再生成）なので、この新フィールドは既存セーブを壊さない
    /// （＝セーブに後付けで配らない: 既存世界を継続しても AI校は再生成され、破壊的変更にならない）。
    /// </summary>
    public IReadOnlyList<Match.Tactics.ManagerTrait> ManagerTraits { get; set; }
        = System.Array.Empty<Match.Tactics.ManagerTrait>();

    /// <summary>AI校の采配能力（1〜100, 設計書04/11 §1）。ティアと独立＝「引き出しは多いが凡将」等が生まれる。</summary>
    public int TacticalSense { get; set; } = 50;

    /// <summary>
    /// 県内地区のインデックス（設計書05 §2.2, pref-format の districts への添字）。
    /// 地理固定割（district_assignment=geographic）の県のみ保持。抽選制・地区なしは null。
    /// </summary>
    public int? DistrictId { get; set; }

    public Tier Tier => Tiers.FromStrength(Strength);
}
