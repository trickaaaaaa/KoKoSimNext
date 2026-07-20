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
/// <param name="IsFly">この打球がフライか（犠飛はタッグアップの時計が異なるため G1 の対象外＝従来テーブル）。</param>
public readonly record struct HomePlayContext(
    FieldGeometry Field,
    HomePlaySituation Situation,
    TacticsCoefficients Tactics,
    double Aggression,
    DefenseDepth InfieldDepth,
    bool IsFly);
