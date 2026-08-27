using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Tactics;

namespace KokoSim.Engine.Match.Game;

/// <summary>
/// 本塁クロスプレー（バックホーム憤死, 設計書12 §3, F2）を走塁解決に注入する束（1打球ぶん）。
/// これを <see cref="BaserunningModel.ApplyDetailed"/> に渡すと、本塁生還の各判定が確率テーブルから
/// 「送り判定(<see cref="HomeSendDecision"/>)＋時間の勝負(<see cref="HomePlayResolver"/>)」へ切り替わる。
/// null を渡せば従来の確率テーブルにフォールバック（既存テスト・決定論を保存）。
/// </summary>
/// <param name="Field">球場幾何（塁間・本塁位置）。</param>
/// <param name="Situation">この打球の外野処理点・時刻・処理野手の肩。</param>
/// <param name="Tactics">送り判定の采配閾値。</param>
/// <param name="Aggression">三塁コーチの積極性（0=超慎重, 0.5=中立, 1=超積極）。
/// 三層（校風/ティア/采配）写像は残Q10。現状は中立固定で配線する。</param>
/// <param name="InfieldDepth">守備側の内野深さ（設計書12 §4/§5, G1）。前進=本塁で刺す/後退=献上を
/// ゴロ凡打の三塁走者判定へ反映する（深さの相互参照）。</param>
/// <param name="IsFly">この打球がフライか。ゴロ(false)は G1 の守備深さ駆動の本塁レース、フライ(true)は
/// 捕球時刻を起点とする犠飛タッチアップの物理レース（Issue #90, 設計書12 §3.5）へ分岐する。</param>
/// <param name="ThrowArriveSeconds">一塁送球がベースに到達した時刻[s]（内野ゴロのみ・接触基準）。ゴロ凡打の
/// 進塁打（二塁→三塁, Issue #318）の分岐C（守備が一塁を取ってから三塁へ転送する経路 P_relay）の起点。
/// null＝一塁送球が無い経路（転送起点が組めない＝分岐Cは無血進塁へフォールバック）。</param>
/// <param name="BatterToFirstSeconds">打者走者の一塁到達時刻[s]（内野ゴロのみ・接触基準）。守備解決が
/// 出塁/凡打を決めたのと同じ正典値（<see cref="Fielding.FieldingPlay.BatterToFirstSeconds"/>）。併殺の二段目＝
/// 二塁→一塁の転送送球 vs 打者走者の秒勝負（Issue #369）に使う。null＝内野ゴロでない/幾何が組めない経路
/// ＝物理レースは組めず従来の確率テーブル（<see cref="BaserunningCoefficients.DoublePlayProb"/>）へフォールバック。</param>
/// <param name="DoublePlayPivotBonusSeconds">「併殺の名手」スキル（<see cref="Players.Skill.DoublePlayArtist"/>）
/// による二塁経由の転送握り替えの短縮量[s]（時間の短縮＝確率でなく, Issue #369）。守備側の遊撃手/二塁手が
/// スキルを持つとき正値。既定0＝短縮なし（帯不変）。</param>
/// <param name="DoublePlayPivotArmMps">併殺の二塁→一塁転送を投げる野手（二塁カバーに実際に入った
/// 遊撃手/二塁手）の送球速度[m/s]（<c>FielderAttributes.ThrowSpeedMps</c>）。null＝呼び出し側が未対応
/// （単体テスト等）＝ <see cref="BaserunningCoefficients.RelayThrowSpeedMps"/>（カットマン用の共通定数の
/// 流用）へフォールバック。issue double-play-throw-pace-and-second-out-call ⑦（二塁手の地肩が併殺成立率に
/// 無効だった）の是正。</param>
public readonly record struct HomePlayContext(
    FieldGeometry Field,
    HomePlaySituation Situation,
    TacticsCoefficients Tactics,
    double Aggression,
    DefenseDepth InfieldDepth,
    bool IsFly,
    double? ThrowArriveSeconds = null,
    double? BatterToFirstSeconds = null,
    double DoublePlayPivotBonusSeconds = 0.0,
    double? DoublePlayPivotArmMps = null);
