using KokoSim.Engine.Core;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Tactics;

/// <summary>
/// 監督采配の入口（設計書09）。プレイヤーの手動采配・委任・敵AI（設計書11）の全てがこの1つの
/// インターフェースを実装する＝AI専用の裏ロジックを作らない（不変条件）。
/// 試合エンジンは打席ごとに呼び、null（無指示）なら従来どおりの挙動＝統計帯を変えない。
/// </summary>
public interface ITacticsBrain
{
    /// <summary>
    /// 攻撃サイン（自軍攻撃時、打席開始前に1回）。盗塁（<see cref="OffensiveSign.Steal"/>）は
    /// 設計書15 Phase D-2d で <see cref="IPitchTacticsBrain.CallPitchAction"/> の毎球判断へ移した
    /// ためここでは返さない（試みるか＋始動種別は毎球独立に判定する）。
    /// </summary>
    OffensiveSign CallOffense(in TacticsSituation s, IRandomSource rng);

    /// <summary>守備指示（自軍守備時、打席開始前に1回。方針＝常時設定の反映）。</summary>
    DefensiveTactics CallDefense(in TacticsSituation s, IRandomSource rng);

    /// <summary>攻撃伝令を使うか（残回数がある時のみ問い合わせ, 設計書09 §3）。</summary>
    bool CallOffenseTimeout(in TacticsSituation s, IRandomSource rng);

    /// <summary>守備伝令（マウンドへ）を使うか。</summary>
    bool CallDefenseTimeout(in TacticsSituation s, IRandomSource rng);

    /// <summary>
    /// 次打者への代打（設計書09 §6, 攻撃側）。起用する控えを返す。null=代打しない。
    /// 試合エンジンは控えが非空のときだけ問い合わせる（既定＝控え空で従来と完全一致）。
    /// </summary>
    Player? CallPinchHit(in SubstitutionSituation s, IRandomSource rng);

    /// <summary>塁上走者への代走（攻撃側）。差し替える (走者, 控え) を返す。null=代走しない。</summary>
    (Player Runner, Player Sub)? CallPinchRun(in SubstitutionSituation s, IRandomSource rng);

    /// <summary>守備固め（守備側, イニング頭）。差し替える (退場, 控え) を返す。null=交代しない。</summary>
    (Player Out, Player Sub)? CallDefensiveSub(in SubstitutionSituation s, IRandomSource rng);
}
