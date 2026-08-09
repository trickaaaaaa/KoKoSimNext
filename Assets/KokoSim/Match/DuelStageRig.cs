using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace KokoSim.Unity.Match
{
    /// <summary>対決ビューへ渡す1球の結果分類（design-18 §7-2 第2弾）。エンジンのPitchKindから写像する。</summary>
    public enum DuelPitchOutcome
    {
        Take,           // ボール見送り（打者は待機のまま・捕手が捕球）
        CalledStrike,   // 見逃しストライク（Batter_Strike＋捕球）
        SwingMiss,      // 空振り（Swing_Miss＋捕球）
        FoulContact,    // ファール（Swing_Hitting・走り出し前でカット・捕球なし）
        InPlayContact,  // インプレー（Swing_Hittingフル＝走り出しまで・捕球なし）
        HitByPitch,     // 死球（Hit_ByPitch・捕球なし）
        // 捕手が捕り損ねる球（issue #525）＝暴投・捕逸・振り逃げ。既存6分類の意味は変えず**追加**する。
        //
        // ⚠ この分類は**捕手側だけ**を言う。打者が振ったかどうかは別の軸で、振り逃げは定義上
        // 空振り（または見逃し）の三振なので、ここへ倒すだけだと**打者のスイングが消える**
        // （SwingMiss を置き換えてしまうため）。打者は OnPitchReleased の batterSwung で別に駆動する。
        NotCaught,
    }

    /// <summary>
    /// 1球の飛翔（issue #332）。<see cref="PitchToken"/> の観測データ（実着弾位置＋観測弾道）の写しで、
    /// 実データが無い球（合成列・弾道を積分しない試合）はそもそもこの構造体を作らない＝null を渡す。
    /// <see cref="AimX"/>/<see cref="AimY"/>（issue #396）は狙い位置。捕手は投球前からこの狙いを知っているため、
    /// 投球前の構え（クリップ選択・立ち位置）はこちらを使ってよい。<see cref="PlateX"/>/<see cref="PlateY"/>
    /// （コントロール散布込みの実着弾位置）はリリース後（残差の体寄せ）にしか使わない。
    /// </summary>
    public readonly struct DuelPitchFlight
    {
        public readonly float PlateX, PlateY;                            // 本塁面の実着弾位置[m]（+x=一塁側）
        public readonly float AimX, AimY;                                 // 狙い位置[m]（投球前に判明済み）
        public readonly float FlightSeconds;                             // 飛翔時間[s]
        public readonly float InducedVerticalBreakM, InducedHorizontalBreakM;   // 誘発変化[m]

        public DuelPitchFlight(float plateX, float plateY, float aimX, float aimY,
            float flightSeconds, float ivb, float ihb)
        {
            PlateX = plateX;
            PlateY = plateY;
            AimX = aimX;
            AimY = aimY;
            FlightSeconds = flightSeconds;
            InducedVerticalBreakM = ivb;
            InducedHorizontalBreakM = ihb;
        }
    }

    /// <summary>
    /// 対決ビューの絵作りパラメータ一式（カメラ構図＋空色＋シェーダのライト）。既定値は確定構図
    /// R-comp60（design-18 §7-1・2026-07-26改定）と DuelStageProxyLambert のシェーダ既定に一致し、
    /// 既定のままなら従来のハードコード時代と同じ絵になる。
    /// DuelCalibrationWindow（Assets/Editor）がPlay中にスライダで差し替え、確定値は Default へ焼き戻す運用。
    /// </summary>
    [System.Serializable]
    public struct DuelCalibration
    {
        /// <summary>スキーマ版。旧版のJSON・シリアライズ値は新項目が0埋めで来るため、Upgradeで既定へ補完する。</summary>
        public int Version;
        public float Dist;       // 注視点からの後退距離[m]（望遠圧縮の強さ。カメラz = マウンド + Dist）
        public float AimY;       // 注視点の高さ[m]
        public float AimZ;       // 注視点のz[m]（本塁=0・正=マウンド側）
        public float PitchDeg;   // 俯角[度]
        public float YawDeg;     // 横角[度]（正=既定と同じ側＝カメラx負）
        public float FovDeg;     // 16:9時の垂直FOV[度]（ApplyFovが縦横比から逆算する基準）
        public Color Sky;        // カメラ背景色（夏空）
        public float Ambient;    // シェーダ _Ambient（環境光の下限）
        public Vector3 LightDir; // シェーダ _LightDir（ワールド・面へ向かう向き）
        // 立ち位置[m]（ステージローカル。本塁=原点・+z=マウンド。yは接地系が持つため対象外）。
        // 投手はプレート位置がルール寸法（18.44m）なので較正対象にしない
        public float BatterX, BatterZ;
        public float CatcherX, CatcherZ;
        public float UmpireX, UmpireZ;
        // 被り物の装着オフセット[m]（gear-headコンテナ＝実寸・頭蓋基準。x=左右/y=頭蓋上/z=顔前）
        public Vector3 CapOffset;             // 投手の帽子
        public Vector3 HelmetOffset;          // 打者のヘルメット
        public Vector3 CatcherHelmetOffset;   // 捕手のヘルメット（殻）
        public Vector3 CatcherMaskOffset;     // 捕手のマスク（ケージ）
        // 胸プロテクター[m]（gear-chestコンテナ＝胸ボーン原点・+Z体前）。yは肩ボーン実測に載る補正ぶん
        public Vector3 CatcherChestOffset;
        /// <summary>
        /// 打者ヘルメットの被り角[度]（x=前後の傾き/y=ヨー/z=左右の傾き）。
        /// 回転の中心は**殻の中心**であって枠の原点（頭ボーン＝首の付け根）ではない。
        /// 首で回すと角度を変えるたびにヘルメットが振り出されて位置まで動き、
        /// 「向きだけ直す」ができないため（ApplyCalibration が中心を保つ補正を掛ける）。
        /// </summary>
        public Vector3 HelmetEuler;

        // ---- 打撃フォームの実行時補正（design-18 §7-2。適用は DuelBatterPose）----
        // 収録モーキャップの破綻（両手が23cm開く・手首が限界まで折れる・頭が下を向いて振れる・
        // 前脚の膝が開く）を、リターゲット後の骨へ毎コマ当て直すためのつまみ。
        // Blenderで焼くのをやめて実行時にしたのは、絵を見ながら追い込むため（往復3分→即時）。
        public float BatterYawDeg;          // 打者の立ち向き[度]（足並びを投手線へ揃える。実測+33.8）
        public float GripGapM;              // 両手首の間隔の目標[m]（実物の握り＝拳2つ分で0.09〜0.12）
        public float GripReachGuard;        // 肩→手首を腕長のこの割合までに抑える（1.0=伸び切り＝肘ロック）
        public float GripReachGuardSwing;   // スイング中の同上（接触では実際に腕が伸びる）
        public float GripSwingFromSec, GripSwingToSec;   // 上を使う区間（クリップ内秒）
        public float GripEndSec;            // 握りの補正を切る時刻（走り出し＝バットを放す）
        // 握りは左右で別々に効かせる（収録の破綻が左右対称でないため。2026-07-29 実測で
        // 手首の折れは左143°/右155°、拳の向きは65〜72°食い違う）。
        // 無印＝左手（収録＝右打者の下の手・ノブ側）／Top＝右手（上の手・バレル側）。
        // ミラー再生（左打者）では骨ごと入れ替わる＝上下の役も入れ替わることに注意。
        public float GripPullBias;          // 寄せの配分（-1=左手を動かさない／+1=右手を動かさない）
        public float GripElbowPoleDeg;      // 左手の肘の向き[度]。+で肘が回る
        public float GripElbowPoleDegTop;   // 右手の肘の向き[度]
        public float GripReachGuardTop;     // 右腕の伸び上限（無印は左腕）
        public float GripReachGuardSwingTop;// 同 スイング中
        public float GripWristFlexDeg;      // 左手首の目標の伸び[度]（180=まっすぐ）
        public float GripWristFlexStrength; // 上を戻す強さ（0=収録のまま）
        public float GripWristFlexDegTop;   // 右手首の目標の伸び[度]
        public float GripWristFlexStrengthTop;
        public float GripWristTwistDeg;     // 左手のひねり[度]（手の長軸まわり＝拳を動かさず掌だけ回る）
        public float GripWristTwistDegTop;  // 右手のひねり[度]
        public float GripWristSideDeg;      // 左手の横振り[度]（前腕軸まわり＝曲げ面の外へ拳が振れる）
        public float GripWristSideDegTop;   // 右手の横振り[度]
        // 掌がバットを向いていない（実測: 掌の法線と握り線の成す角が右12°＝掌が軸と平行）のは
        // 手首でなく前腕のひねり（回内・回外）。手首だけで90°回すと関節が折れて見える
        public float GripForearmRollDeg;    // 左前腕のひねり[度]（肘→手首軸まわり。手首の位置は動かない）
        public float GripForearmRollDegTop; // 右前腕のひねり[度]
        // 収録は指を撮っていない＝手が開いたままバットが通り抜ける（2026-07-29 絵で確認）。
        // 指の曲げは残す価値のある収録データが無いので、静的な握り込みで閉じる
        public float GripFingerCurl;        // 左手の指の握り込み（0=収録のまま／1=握り込む）
        public float GripFingerCurlTop;     // 右手の指の握り込み
        public float HeadYawOffsetDeg;      // 視線のヨー微調整[度]（0=投手をまっすぐ見る）
        public float HeadElevDeg;           // 視線の仰角[度]（+=上向き）
        public float HeadStrength;          // 固定の強さ（0=収録のまま／1=完全固定）
        public float HeadNeckShare;         // 補正のうち首が受け持つ割合（残りが頭）
        public float HeadUntilSec;          // ここまで見続ける（スイング後は収録のまま）
        public float KneeDeg;               // 踏み込み脚を股関節→足首軸まわりに回す角[度]（体を残す）
        public float KneeFromSec, KneeFullSec, KneeToSec;   // 効き始め／全開／戻し切り
        // ---- 体の開き（FixTorso・2026-07-30 監督指摘「開きを抑えたい」）----
        // 胴を鉛直軸まわりに戻して「開かずに待つ」絵を作る。腰は回さない（脚ごと回って接地が滑るため）＝
        // 背骨チェーン（Spine/Chest/UpperChest）へ配分する。1関節に寄せると素体の可動域で折れる。
        public float TorsoCloseDeg;         // 胴を戻す角[度]（+/−で回る向きが変わる。0=無効）
        public float TorsoFromSec, TorsoFullSec, TorsoToSec;   // 効き始め／全開／戻し切り（クリップ内秒）
        // ---- 振りの速さ（2026-07-30 監督指摘「振りを早くしたい」）----
        // クリップ内時刻の進み方を一様に変えるだけ＝緩急の比は保つ。ポーズは12fps量子化なので、
        // 上げるほど1振りに使えるコマが減る（0.70sの振りは 1.0倍で約8コマ・1.5倍で約6コマ）。
        // 頭出しが負になる速度でも面通過は球からずれない（SetSwing のカウントダウン開始）＝
        // つまみの全域 0.5〜3.0 が安全（issue #368）
        public float SwingSpeed;            // 1=収録のまま。スイング系の一発物にだけ掛かる
        // ---- バットの置き方（AlignBat）----
        // 握りが直った今は「両手を結んだ線＝バットの軸」がそのまま使える。旧実装は両手が23cm開いて
        // 拳が積み重ならない収録を前提に、ヨーだけ実測して仰角を決め打ちしていた（合成軸）。
        // BatAxisFromGrip=1 で実測線、0 で旧来の合成軸。手が縦に積み重なると合成軸は水平成分が
        // 消えてバットが真上を向くため（2026-07-29 実測 axis=(0,1,0)）、既定は実測線。
        public float BatAxisFromGrip;   // 0=合成軸 / 1=握り線そのまま
        public float BatGripBelowM;     // 握りの中点からノブ端までの距離[m]
        public float BatLengthM;        // バット全長[m]（メッシュはlocalScaleで伸縮）
        public float BatStanceElevDeg;  // 合成軸の構え仰角[度]（BatAxisFromGrip<1のときだけ効く）
        public float BatFollowElevDeg;  // 合成軸の振り抜き仰角[度]（同上）
        public float BatHandsApartM;    // これ以上手が離れたら片手追従へ移る[m]
        // ---- スイング面（2026-07-30 監督指摘「足元のボールを打っているように見える」）----
        // 収録のスイングは面が低く、ど真ん中の球を打つ絵にならない。クリップは触らず、
        // **スイング中だけ握りを持ち上げて傾ける**＝腕はIKが自然に追いてくる（手追従のまま効く）。
        // 効く区間は握りの許容を上げるのと同じ窓（GripSwingFromSec〜GripSwingToSec）。
        public float SwingLiftM;         // 握りの中点を持ち上げる量[m]（+で高い面）
        public float SwingPlaneTiltDeg;  // 握り線の傾き[度]（+/−でバレル側が上下。符号は実測で決める）
        // 上2つを**どちらへ掛けるか**の配分（2026-07-31 監督要望「腕追いかけずにバットだけ調整」）。
        // 0=握りを動かして腕を追わせる（IKが追いてくる／手とバットは離れない）
        // 1=バット単体を動かす（腕は収録のまま）。傾きは**握り点まわりの回転**なので手から離れないが、
        //   持ち上げは平行移動＝その量だけバットが手から浮く（＝浮きが要らないなら傾きだけで作る）
        public float SwingPlaneBatOnly;
        // ---- 構えだけ別クリップ（2026-07-31 監督要望「構えだけ元の自作モーションにもできる？」）----
        // 0=待機も打撃クリップ（Mixamo）の構え区間 / 1=自作Rokoko（Batter_Batting_01）の構え区間。
        // スイングは常に打撃クリップ＝**構えとスイングでクリップが分かれる**ので切り口はクロスフェードで繋ぐ。
        public float IdleFromOwnClip;
        // 自作構えのときの立ち向き[度]。素の向きが約30°違う（RunFacingProbe 実測 chest 52°/82°）ため別に持つ
        public float IdleYawDeg;
        // バント構え（Bunt_Loop・Bunt_Hitting・Bunt_Miss）の立ち向き[度]（issue #514）。squaring＝
        // 投手正対なので、打撃系2値（BatterYawDeg/IdleYawDeg）とは別の第3の向きを持つ。
        // RunFacingProbe実測（bunt-loop）: chest=0°で始点/中間/終点とも安定（回らない構えループ）
        public float BuntYawDeg;
        // バント構えへの移行（AA_Baseball_Batter_Idle_To_Bunt）専用の立ち向き[度]。この1本だけ、
        // クリップ自身が「回り込んでsquaringする」芝居を内部に持つため、始点(RunFacingProbe実測
        // chest=1°)と終点(同54°)で向きが違う＝BuntYawDegと共有できない。終点側を採るのは、
        // このクリップの終わりがBunt_Loopの開始へ**クロスフェード無しでハードカット**するため
        // （FinishOneShot）。始点側（=前段の構えに近い値）を採ると、そのカット面で50°超の
        // 瞬間回転（実害）が起きる。始点側の見た目のギャップは、直前のクロスフェード
        // （SyncBatterYawのsmoothstep補間）で吸収する
        public float BuntTransitionYawDeg;
        // 自作構えのバット軸の採り方（<see cref="BatAxisFromGrip"/> のクリップ別版）。自作収録は両手が
        // 23cm開いて実測線が寝るので、合成軸（BatStanceElevDeg）寄りの旧値0.154が既定
        public float IdleBatAxisFromGrip;
        // ---- ストライクゾーンの重畳（2026-07-31 監督要望「うっすら表示できる？」）----
        // 矩形の寸法は engine の StrikeZone（判定に使われている実物）から採る＝ここには持たない。
        // 持つのは見た目だけ。ポスタライズの後に GL で引くので、薄いアルファがそのまま絵に出る
        public float ZoneShow;        // 0=出さない / 1=出す
        public Color ZoneColor;       // 線と面の色（アルファは下の2つが決める）
        public float ZoneLineAlpha;   // 枠の濃さ
        public float ZoneFillAlpha;   // 面の濃さ（0=枠だけ）
        public float ZoneLineM;       // 枠の太さ[m]（実寸＝遠近でちゃんと細くなる）
        // ---- 打球の行方を一瞬だけ見せる（2026-07-31 監督要望「フライかな？ゴロかな？」）----
        // 確定構図（FOV 2.9°）は本塁のところで幅7.0m×高5.3mしかなく、打球は0.10〜0.22秒で枠外へ抜ける
        // （実測: センターフライ0.13s / ライナー0.22s / ゴロ0.21s）。球だけだと3〜13コマで消えて何も読めない
        // ので、残像の線で「打ち出しの角度と速さ」を残す。画角には触らない（C案採用）
        public float BattedShow;          // 0=出さない / 1=出す
        public float BattedTrailSeconds;  // 尾の長さ[秒]（球が抜けた後もこの秒数だけ線が残る）
        public float BattedTrailWidthM;   // 頭の太さ[m]（実寸＝遠近で細くなる。尾へ向けて0まで絞る）
        public Color BattedTrailColor;    // 線の色
        public float BattedTrailAlpha;    // 頭の濃さ（尾へ向けて0まで抜く）
        // ---- バットの軌跡（2026-08-01・スイングが12fpsで描けない件への対処）----
        // ポーズは12fpsなので、スイングのピーク区間は**隣り合う2枚の絵の間で手が175°回る**
        // （実測 2026-08-01）。これは「カクカク」ではなく回転方向が判別できない領域＝バットが
        // 1〜2コマ消えて見える。SwingSpeed を上げるとサンプル数が減ってさらに悪化する（100→124°/コマ）。
        // 手描きアニメが速い動きを12fpsで見せる流儀（ブレ絵＋動線）に倣い、コマ間をバットが掃いた
        // 面と先端の軌跡で埋める。ポーズのレートは変えない（design-18 §4.5-② の掟はそのまま）。
        public float BatTrailShow;          // 0=出さない / 1=出す
        public float BatTrailSeconds;       // 尾の長さ[秒]
        public Color BatTrailColor;         // 面・線に共通の色
        public float BatTrailFillAlpha;     // 掃いた面の濃さ（頭。尾へ向けて0まで抜く）
        public float BatTrailEdgeAlpha;     // 先端の線の濃さ（同上）
        public float BatTrailEdgeWidthM;    // 先端の線の太さ[m]（実寸＝遠近で細くなる）
        public float BatTrailMinDegPerFrame; // これ未満しか回っていないコマは積まない（構え・据え置きを自動で外す）
        public float BatTrailArcStepDeg;    // 弧の分割粒度[度]（バットは剛体なので中間姿勢は幾何で復元できる）
        // ---- ボールの出所（issue #332・2026-07-30）----
        // ボールはリリースまで**投球する手の子**なので、位置は手ボーンローカルの微調整だけで足りる
        // （基準は design-18 §3.3.1 の掟どおり「手の中心＝手首→中指付け根の中点」）。
        public Vector3 BallInHandOffset;   // 手の中心からのオフセット[m]（手ボーンローカル）
        // 発射する瞬間のクリップ内秒。**アンカーの ReleaseTime(3.05) とは別物**: 3.05 は捕球・ミート・
        // コールの逆算基準として調整済み（#331/#335）だが、投球クリップの手はそこでは既に振り下ろし後で
        // 高さ1.0mまで落ちている。手の速度ピーク（＝実際に球が離れる瞬間）は実測 clipT=2.95〜3.00 で
        // 高さ1.52m（2026-07-30 実測。18.6m/s）。球の出所だけこの時刻の手から採る
        public float BallReleaseClipTime;

        private const int CurrentVersion = 24;

        public static DuelCalibration Default => new DuelCalibration
        {
            Version = CurrentVersion,
            Dist = 60f,
            AimY = 0.8f,
            AimZ = 1f,
            // 俯角4.5°・横角3.8°: 旧ハードコードのオフセット係数（0.0782 / 0.0668）を角度へ逆算した値
            PitchDeg = Mathf.Atan(0.0782f) * Mathf.Rad2Deg,
            YawDeg = Mathf.Atan(0.0668f) * Mathf.Rad2Deg,
            FovDeg = 2.9f,
            Sky = new Color(0.55f, 0.78f, 0.90f),
            Ambient = 0.45f,
            LightDir = new Vector3(-0.35f, -0.85f, -0.4f),
            // 立ち位置は**絵の好みでなく幾何で決まる**（v19）。接触の瞬間に「握り→打点＝腕の長さ(0.605m)」
            // でないと、芯を球に乗せるためにバットが前腕へ折り返る＝こねた形になる。
            // 実測（接触1.17）: -0.71→0.382m / **-0.96→0.631m** / -1.16→0.826m（design-18 §4.5-⑪）
            BatterX = -0.96f, BatterZ = 0.06f,
            CatcherX = -0.02f, CatcherZ = -1.0f,
            UmpireX = 0.18f, UmpireZ = -1.88f,   // #291のスロット位置から微調整
            CapOffset = Vector3.zero,
            HelmetOffset = new Vector3(-0.003f, -0.014f, -0.052f),
            CatcherHelmetOffset = Vector3.zero,
            CatcherMaskOffset = Vector3.zero,
            CatcherChestOffset = Vector3.zero,
            HelmetEuler = Vector3.zero,
            // 打撃フォームの既定は 2026-07-29 に打撃フォーム確認窓で追い込んだ実測値
            // （out/duel-calibration.json v8 からの焼き戻し。以降もツール→JSON→ここの順で更新する）
            //
            // 2026-07-30 Mixamo「Baseball Strike」への差し替えに伴い、**クリップ内秒に依存する項目だけ**を
            // 新クリップの実測タイムライン（構え〜0.85 / スイング0.90-1.60（接触1.30）/ フォロー〜2.60）へ
            // 張り替えた。角度・長さ系（手首の折れ・指の握り込み・バット寸法）は旧クリップの破綻に
            // 合わせて追い込んだ値がそのまま残っている＝**新クリップでは要再調整**（打撃フォーム確認窓）。
            // 旧90+33.8 → 90+3.8（RunFacingProbeのchest実測差 52°-82°=-30° ぶん戻した）→ +45 を上乗せ。
            //
            // 2026-08-01（監督決定）: 収録のスイングは接触の瞬間に腰・胸が三塁側へ回り切っていて
            // （実測 腰-4°/胸-2°＝真正面を通り越している）、引っ張ってファールにしかならない形に見えた。
            // **立ち向きを閉じると足・腰・胸がまとめて閉じる**のがポイントで、既存の TorsoCloseDeg は
            // 背骨しか回さない（腰から回すと脚ごと持って行かれて足裏が滑るため意図的にそうしてある）ので、
            // 8→55°まで振っても前足と腰は1度も動かないことを実測で確認している。
            //
            // 構えは自作クリップ側の IdleYawDeg(119°) が担当するので**この値を変えても構えは動かない**
            // ＝スイング区間だけが閉じる（SyncBatterYaw が両者をクロスフェードの重みで繋ぐ）。
            // なお 138.8 は IdleYawDeg より約20°閉じた側なので、振り出しで体が順方向へ回り込む。
            //
            // 角度は +0/+25/+30/+35/+40/+45 を並べて選定。接触時の「握り線とバット軸のずれ」
            // （大きいほど手首をこねる。design-18 §4.5-⑪）は +0:26° / +25:6° / +45:30° と谷を描き、
            // 数字だけなら +21〜25° が底だが、絵は +45 が良いという判断。
            BatterYawDeg = 90f + 3.8f + 45f,
            GripGapM = 0.10f,
            GripReachGuard = 0.911f,
            GripReachGuardSwing = 0.901f,
            GripSwingFromSec = 0.85f, GripSwingToSec = 1.55f,   // 旧2.75/3.20（スイング区間に合わせる）
            GripEndSec = 2.60f,                                  // 旧3.25。走り出しが無いので打ち切り点まで握らせる
            GripPullBias = 0f,
            GripElbowPoleDeg = 0f,
            GripElbowPoleDegTop = 21f,
            GripReachGuardTop = 0.874f,
            GripReachGuardSwingTop = 0.97f,
            GripWristFlexDeg = 148.3f,
            GripWristFlexStrength = 0.888f,
            // 右手首は収録が155°（左ほど折れていない）＝曲げは触らない。指も右は開いたまま（未調整）
            GripWristFlexDegTop = 165f,
            GripWristFlexStrengthTop = 0f,
            GripWristTwistDeg = 2f, GripWristTwistDegTop = 0f,
            GripWristSideDeg = 12f, GripWristSideDegTop = -12f,
            GripFingerCurl = 0.394f, GripFingerCurlTop = 0f,
            GripForearmRollDeg = -7f, GripForearmRollDegTop = 7f,
            HeadYawOffsetDeg = 0f,
            HeadElevDeg = -3f,
            HeadStrength = 0.913f,
            HeadNeckShare = 0.413f,
            // クリップ終端まで投手を見続ける（v20で 1.70→3.4 へ戻した）。収録の頭は振り抜きの後に
            // **捕手の方を振り向く**動きを持っており（実測: 視線を離した 1.40 以降に頭ヨーが
            // 1°→−23°→+32°→67°→…→105° と回り切る）、監督判断でこれは要らない。
            // クリップを短く切っても消えない＝インプレーは clip 1.96 まで映るのに振り向きは 1.75 から
            // 始まるので、**視線を当て続ける**のが唯一の消し方（design-18 §4.5-⑬）
            HeadUntilSec = 3.4f,
            // 膝は**構えの間だけ**効かせている（0.14〜0.90秒）。スイング中は収録のまま＝
            // 当初の「体を残す」とは別の使い方になっているので、直すならこの3つの秒を戻す
            KneeDeg = -54f,
            KneeFromSec = 3.4f, KneeFullSec = 0.14f, KneeToSec = 0.90f,   // 旧ToSec=1.86（構えの終わりに合わせる）
            // 開きの抑えはスイング区間だけ（構えは収録のまま／フォローは開いてよい）。
            // 全開を接触（1.30）に置くのは、開きが絵に出るのが「振り出し〜当たる瞬間」だから。
            //
            // 符号は推測でなく実測から（掟: 符号の取り違えは絵でしか気づけない）。
            // RunRootPathProbe の胸向き実測（Batter_Mixamo_Strike_01）:
            //   t=1.00 chest=(-0.90,+0.44)=-64° → t=1.25 (+0.06,-1.00)=+177° → t=1.50 (+1.00,-0.05)=+93°
            // ＝スイング中の胸ヨーは**単調減少**（-64→-183→-267）。開き＝負の回転なので、
            // 抑える補正は**正**。負を入れると開きを助長する
            // 既定を控えめ（8°）にしてあるのは、いまバットが**手追従**だから。胴を閉じるとその分だけ
            // 腕とバットが引き戻され、18°では接触の瞬間にバットが胸元へ畳まれて球に届かなくなる
            // （2026-07-30 実測）。バットをコマごとの手動配置へ移したら、この上限は外れるので
            // そこで本調整する
            TorsoCloseDeg = 8f,
            // 全開は「接触の瞬間」に置く約束なので、ミートの実測値（1.30→1.12→1.00→1.05）に追従させる
            TorsoFromSec = 0.77f, TorsoFullSec = 1.17f, TorsoToSec = 1.57f,   // 全開＝接触（v19で+0.12）
            // 監督選定（2026-07-30・4通りのコマ送り比較）: **開き8°＋速度1.4倍**。
            // 1.4倍のとき面通過（リリース+0.45s）のクリップ時刻は実測1.253s＝アンカー1.30sの
            // 1コマ以内（12fpsの刻みは0.117s）。振りは0.70s→0.50s＝1振りに使えるコマは8→6に減る
            // 振り出し（clip 0.90）から接触（1.12）までは 0.22 クリップ秒＝実時間はこの倍率で割った値。
            // 1.4 で 0.157s ＝ 実際のスイング（振り出し→接触 0.15〜0.20s）に入る。
            // 「振りが遅い」（2026-07-31 監督指摘）の主因はこの倍率ではなく、**ポーズが倍率ぶん
            // 進んでいなかったこと**（StepActor 参照）で、実効 1.0倍＝0.22s で振っていた。
            //
            // **2.0（2026-08-01 監督確定・issue #368）。** 1.4 を選んだのは「振り終わりの腕が
            // 据え置き中も回り続ける」不具合が残っていた頃の比較なので、腕を直した絵で
            // 1.4/1.7/2.0 を撮り直して選び直した（実時間再生で比較）。実測:
            //   1.4 → 0.157s・1.9コマ ／ 1.7 → 0.129s・1.6コマ ／ **2.0 → 0.110s・1.3コマ**
            // 実物のスイング（振り出し→接触 0.15〜0.20s）より速いが、**実寸に合わせるだけでは
            // 絵として遅い**というのが監督判断。12fps のポーズが2コマを切るのは承知の上
            SwingSpeed = 2.0f,
            // 旧0.154（＝ほぼ合成軸）。自作Rokokoは両手が開いて拳が積み重ならない収録だったので
            // 実測線が使えなかったが、Mixamoは元からバットを両手で握った収録なので実測線が素直に使える。
            // 0.154のままだと合成軸の仰角35.7°が頭の方向へ立ち上がり、構えでバットが顔を貫く（2026-07-30 絵で確認）
            BatAxisFromGrip = 1f,
            BatGripBelowM = 0.111f,
            BatLengthM = 0.869f,
            BatStanceElevDeg = 35.7f,
            BatFollowElevDeg = 32.4f,
            BatHandsApartM = 0.246f,
            // 監督が打撃フォーム確認窓で追い込んだ値（2026-07-31・out/duel-calibration.json v13 から焼き戻し）。
            // −63°＝バレル端を63°下げる。収録の握り線は接触の前後で拳が縦に積み重なり、実測軸が
            // 上を向く瞬間がある（2026-07-30 実測：14msの差で軸が飛ぶ）。そこを下げて振り面を
            // 「ど真ん中を叩く高さ」へ寝かせるのがこの値の意味＝**大きいのは軸の暴れを吸っているから**。
            // 持ち上げは−0.018mとほぼ0＝高さは傾きだけで作れている（平行移動はバットが手から浮くので）
            // スイング面のつまみは 2026-07-31 に**0へ戻した**。−63°/−0.018m は「軸が握り線から採れていて
            // ミート近傍で暴れる」のを打ち消す当て木で、横ずれを詰める代わりに高さを悪化させていた
            // （どちらの値でもバットは球に当たっていなかった）。振り出し以降は手の中の向きを剛体で運ぶ
            // ようにしたので当て木は不要＝入れると担いだバットが振り出しで下を向く。
            // 実測（傾き−63°→0）: 振り出し(clip 0.90)のバット 下18°→**上46°**／1コマの飛び 47°→**16.5°**／
            // 接触の芯→打点 0.107m→0.105m（当たりは変わらない）。つまみ自体は残してある
            SwingLiftM = 0f,
            SwingPlaneTiltDeg = 0f,
            SwingPlaneBatOnly = 1f,   // 既定はバットだけ動かす（腕は収録のまま）
            // 構えは自作Rokoko（監督選定 2026-07-31・打撃フォーム確認窓で確認）。スイングはMixamoのまま。
            IdleFromOwnClip = 1f,
            // 旧・自作の立ち向きは 90+33.8=123.8 だったが、Mixamoのスイングと繋ぐぶん 119 へ寄せた
            IdleYawDeg = 119f,
            // バント構え（issue #514・RunFacingProbe実測 2026-08-07。desired=(0,0,1)＝投手正対）。
            // bunt-loop chest=0°で全区間安定（回らないループなのでそのまま採用）
            BuntYawDeg = 0f,
            // bunt-in(Idle_To_Bunt) chest: 始点1° / 終点54°。Bunt_Loopへハードカットする終点側を採る
            // （BuntYawDeg欄のコメント参照）
            BuntTransitionYawDeg = 54f,
            // 予想（合成軸寄りの旧値0.154）は外れで、実測線そのままが良かった。
            // 旧値は「両手が23cm開いた収録」を前提にしていたが、いまは FixGrip が握りを10cmまで
            // 詰めてから軸を採るので、自作収録でも実測線が素直に使える
            IdleBatAxisFromGrip = 1f,
            // ゾーンは既定で出す。色は壁（0.12,0.20,0.16）の上で沈まない白寄り
            ZoneShow = 1f,
            ZoneColor = new Color(0.95f, 0.96f, 1f),
            ZoneLineAlpha = 0.22f,
            ZoneFillAlpha = 0.05f,
            ZoneLineM = 0.012f,
            BattedShow = 1f,
            // 尾は「接触から俯瞰へ渡るまで」（接触=リリース+0.45s / 切替=+1.0s＝約0.55s）の中で
            // 消え切る長さにする。長すぎると切り替わる瞬間まで線が残って俯瞰へのカットが濁る
            BattedTrailSeconds = 0.28f,
            // 太さは球（直径0.073m）より**細く**する。0.09mだと絵の上で帯になり、打者の体に貼った
            // 板のように見える（2026-07-31 実測比較）。0.045m＝本塁のところで約10px＝ぶれの線に見える
            BattedTrailWidthM = 0.045f,
            BattedTrailColor = Color.white,
            BattedTrailAlpha = 0.45f,
            BatTrailShow = 1f,
            // 尾は「1ポーズコマ（12fps＝83ms）を2〜3枚ぶん繋ぐ」長さ。長くすると振り切った後まで
            // 扇が残って円盤に見えるので、コマ間を埋める最小限に留める
            BatTrailSeconds = 0.20f,
            // 打球残像と同じ白寄り。バット色（tan）だと壁の上で沈み、体に貼った板に見える
            BatTrailColor = Color.white,
            BatTrailFillAlpha = 0.20f,   // 面は薄く（濃いと扇が円盤になる）
            BatTrailEdgeAlpha = 0.50f,   // 先端の線は濃く＝軌道の形はこっちで読ませる
            BatTrailEdgeWidthM = 0.035f, // 打球残像(0.045)より細い＝バットの方が近くて太く映るため
            BatTrailMinDegPerFrame = 25f,
            BatTrailArcStepDeg = 15f,    // 175°なら12分割。剛体なので追加のポーズ評価は要らない
            // 手の中心からのオフセットは0＝掟どおり「手首→中指付け根の中点」そのまま（実測で追い込む）
            BallInHandOffset = Vector3.zero,
            BallReleaseClipTime = 2.97f,   // 手の速度ピーク実測 2.95〜3.00（18.6m/s・高さ1.52m）の中間
        };

        /// <summary>
        /// 旧版の値を現行スキーマへ補完する。0埋めのままだと打者が本塁上（x=0）に立つ等の
        /// 壊れた絵になるため、読み込み・適用の入口で必ず通す。
        ///
        /// **版ごとに「その版で増えた項目だけ」を埋める**こと。全項目を一括で既定へ戻すと、
        /// 版を上げるたびに調整済みのJSONが白紙に戻る（ツールでライブ調整→JSONへ書き出す運用なので
        /// これは実害）。新項目を足すときは CurrentVersion を上げて、下に1ブロック足す。
        /// </summary>
        public static DuelCalibration Upgrade(DuelCalibration c)
        {
            if (c.Version >= CurrentVersion) return c;
            var d = Default;
            if (c.Version < 2)   // v2: 立ち位置と被り物オフセットを追加
            {
                c.BatterX = d.BatterX; c.BatterZ = d.BatterZ;
                c.CatcherX = d.CatcherX; c.CatcherZ = d.CatcherZ;
                c.UmpireX = d.UmpireX; c.UmpireZ = d.UmpireZ;
                c.CapOffset = d.CapOffset;
                c.HelmetOffset = d.HelmetOffset;
            }
            if (c.Version < 3)   // v3: 捕手のヘルメット・マスクを分離
            {
                c.CatcherHelmetOffset = d.CatcherHelmetOffset;
                c.CatcherMaskOffset = d.CatcherMaskOffset;
            }
            if (c.Version < 4)   // v4: 胸プロテクターを生成メッシュへ差し替え
                c.CatcherChestOffset = d.CatcherChestOffset;
            if (c.Version < 5)   // v5: 打者ヘルメットの被り角
                c.HelmetEuler = d.HelmetEuler;
            if (c.Version < 6)   // v6: 打撃フォームの実行時補正（握り・頭・膝・立ち向き）
            {
                c.BatterYawDeg = d.BatterYawDeg;
                c.GripGapM = d.GripGapM;
                c.GripReachGuard = d.GripReachGuard;
                c.GripReachGuardSwing = d.GripReachGuardSwing;
                c.GripSwingFromSec = d.GripSwingFromSec; c.GripSwingToSec = d.GripSwingToSec;
                c.GripEndSec = d.GripEndSec;
                c.GripElbowPoleDeg = d.GripElbowPoleDeg;
                c.GripElbowPoleDegTop = d.GripElbowPoleDegTop;
                c.GripWristFlexDeg = d.GripWristFlexDeg;
                c.GripWristFlexStrength = d.GripWristFlexStrength;
                c.HeadYawOffsetDeg = d.HeadYawOffsetDeg;
                c.HeadElevDeg = d.HeadElevDeg;
                c.HeadStrength = d.HeadStrength;
                c.HeadNeckShare = d.HeadNeckShare;
                c.HeadUntilSec = d.HeadUntilSec;
                c.KneeDeg = d.KneeDeg;
                c.KneeFromSec = d.KneeFromSec; c.KneeFullSec = d.KneeFullSec; c.KneeToSec = d.KneeToSec;
            }
            if (c.Version < 7)   // v7: バットの置き方（握り線を直したので軸の採り方も変わる）
            {
                c.BatAxisFromGrip = d.BatAxisFromGrip;
                c.BatGripBelowM = d.BatGripBelowM;
                c.BatLengthM = d.BatLengthM;
                c.BatStanceElevDeg = d.BatStanceElevDeg;
                c.BatFollowElevDeg = d.BatFollowElevDeg;
                c.BatHandsApartM = d.BatHandsApartM;
            }
            if (c.Version < 8)   // v8: 握りを左右別に（寄せの配分・腕の伸び上限・手首3軸）
            {
                c.GripPullBias = d.GripPullBias;
                c.GripReachGuardTop = d.GripReachGuardTop;
                c.GripReachGuardSwingTop = d.GripReachGuardSwingTop;
                c.GripWristFlexDegTop = d.GripWristFlexDegTop;
                c.GripWristFlexStrengthTop = d.GripWristFlexStrengthTop;
                c.GripWristTwistDeg = d.GripWristTwistDeg;
                c.GripWristTwistDegTop = d.GripWristTwistDegTop;
                c.GripWristSideDeg = d.GripWristSideDeg;
                c.GripWristSideDegTop = d.GripWristSideDegTop;
                c.GripFingerCurl = d.GripFingerCurl;
                c.GripFingerCurlTop = d.GripFingerCurlTop;
                c.GripForearmRollDeg = d.GripForearmRollDeg;
                c.GripForearmRollDegTop = d.GripForearmRollDegTop;
            }
            if (c.Version < 9)   // v9: ボールの出所（手の中の位置・発射する瞬間のクリップ時刻）
            {
                c.BallInHandOffset = d.BallInHandOffset;
                c.BallReleaseClipTime = d.BallReleaseClipTime;
            }
            if (c.Version < 10)   // v10: 体の開きの抑え（背骨チェーンへのヨー戻し）と振りの速さ
            {
                c.TorsoCloseDeg = d.TorsoCloseDeg;
                c.TorsoFromSec = d.TorsoFromSec;
                c.TorsoFullSec = d.TorsoFullSec;
                c.TorsoToSec = d.TorsoToSec;
                c.SwingSpeed = d.SwingSpeed;
            }
            // v11（バットをコマごとに置く＝BatFromPath）は 2026-07-31 に廃止した。
            // 版番号は詰めない＝古いJSONの Version 判定がずれるため、欠番として残す
            if (c.Version < 12)   // v12: スイング面の高さ・傾き
            {
                c.SwingLiftM = d.SwingLiftM;
                c.SwingPlaneTiltDeg = d.SwingPlaneTiltDeg;
            }
            if (c.Version < 13)   // v13: スイング面を腕でなくバット単体へ掛ける配分
            {
                c.SwingPlaneBatOnly = d.SwingPlaneBatOnly;
            }
            if (c.Version < 14)   // v14: 構えだけ自作クリップへ差し替える
            {
                c.IdleFromOwnClip = d.IdleFromOwnClip;
                c.IdleYawDeg = d.IdleYawDeg;
                c.IdleBatAxisFromGrip = d.IdleBatAxisFromGrip;
            }
            if (c.Version < 15)   // v15: ストライクゾーンの重畳
            {
                c.ZoneShow = d.ZoneShow;
                c.ZoneColor = d.ZoneColor;
                c.ZoneLineAlpha = d.ZoneLineAlpha;
                c.ZoneFillAlpha = d.ZoneFillAlpha;
                c.ZoneLineM = d.ZoneLineM;
            }
            if (c.Version < 16)   // v16: 打球の行方（残像の線）
            {
                c.BattedShow = d.BattedShow;
                c.BattedTrailSeconds = d.BattedTrailSeconds;
                c.BattedTrailWidthM = d.BattedTrailWidthM;
                c.BattedTrailColor = d.BattedTrailColor;
                c.BattedTrailAlpha = d.BattedTrailAlpha;
            }
            if (c.Version < 17)   // v17: 接触のクリップ時刻を1.12→1.00へ採り直した（頭出しワープの是正）
            {
                // 新項目ではないが、胴の開きの窓は「全開＝接触の瞬間」という約束でアンカーに
                // ぶら下がっている。アンカーが動いた版では調整済みJSONの値も一緒に動かさないと、
                // 全開が接触の1コマ後に来て「開き切る前に当たる」絵になる
                c.TorsoFromSec = d.TorsoFromSec;
                c.TorsoFullSec = d.TorsoFullSec;
                c.TorsoToSec = d.TorsoToSec;
            }
            if (c.Version < 18)   // v18: 打つ球を判定面より前で捌く（接触1.00→1.05）
            {
                // v17と同じ理由＝胴の開きの窓はアンカーにぶら下がっている
                c.TorsoFromSec = d.TorsoFromSec;
                c.TorsoFullSec = d.TorsoFullSec;
                c.TorsoToSec = d.TorsoToSec;
            }
            if (c.Version < 19)   // v19: 接触を腕が届く時刻へ（1.05→1.17）＋立ち位置を幾何で解いた
            {
                // 立ち位置は絵の好みでなく「接触で握り→打点＝腕の長さ」から決まる値になった＝
                // 調整済みJSONの値を残すと、こねた形（バットが前腕へ折り返る）が残る
                c.BatterX = d.BatterX;
                c.TorsoFromSec = d.TorsoFromSec;
                c.TorsoFullSec = d.TorsoFullSec;
                c.TorsoToSec = d.TorsoToSec;
            }
            if (c.Version < 20)   // v20: 振り抜き後の「捕手を振り向く」収録動作を消す
            {
                c.HeadUntilSec = d.HeadUntilSec;
            }
            if (c.Version < 21)   // v21: 振りの速さ 1.4→2.0（issue #368・監督確定）
            {
                // 新項目ではないが、1.4 は「振り終わりの腕が据え置き中も回り続ける」不具合が
                // 残っていた頃の比較で選んだ値。腕を直した絵で 1.4/1.7/2.0 を撮り直して
                // 選び直したので、調整済みJSONも一緒に動かす（残すと古い判断の値が生き続ける）
                c.SwingSpeed = d.SwingSpeed;
            }
            if (c.Version < 22)   // v22: バットの軌跡（12fpsのコマ間を面＋先端の線で埋める）
            {
                c.BatTrailShow = d.BatTrailShow;
                c.BatTrailSeconds = d.BatTrailSeconds;
                c.BatTrailColor = d.BatTrailColor;
                c.BatTrailFillAlpha = d.BatTrailFillAlpha;
                c.BatTrailEdgeAlpha = d.BatTrailEdgeAlpha;
                c.BatTrailEdgeWidthM = d.BatTrailEdgeWidthM;
                c.BatTrailMinDegPerFrame = d.BatTrailMinDegPerFrame;
                c.BatTrailArcStepDeg = d.BatTrailArcStepDeg;
            }
            if (c.Version < 23)   // v23: 立ち向きを45°閉じる（開き切りの是正・監督決定）
            {
                // 新項目ではないが、調整済みJSONの値を残すと「接触で腰も胸も三塁側を向き切っている」
                // 旧フォームがそのまま生き続ける。角度は絵を並べて選び直したので一緒に動かす
                c.BatterYawDeg = d.BatterYawDeg;
            }
            if (c.Version < 24)   // v24: バント構えの立ち向き（issue #514・新項目）
            {
                c.BuntYawDeg = d.BuntYawDeg;
                c.BuntTransitionYawDeg = d.BuntTransitionYawDeg;
            }
            c.Version = CurrentVersion;
            return c;
        }
    }

    /// <summary>
    /// 対決ビューの実体（design-18 §7-1 A案・配線スパイク段階）。
    /// DuelStage レイヤーに簡易ステージ＋素体4体（UBC）を組み、StageCamera（確定構図R）が
    /// RT（基準960×540・Point。2026-08-05クリーン化改定）へ毎フレーム描画する。
    /// キャラのポーズ更新だけ 12fps に量子化（§4.2 確定値）、カメラ・RTはフルレート。ステージは主カメラに映り込まないよう y=-500 へ退避して置き、
    /// HideAndDontSave でシーンを汚さない。
    ///
    /// 素体FBXとモーション（Assets/Animo＝再配布NG・gitignore）はエディタ実行時のみ AssetDatabase から
    /// 引く。ビルドへの同梱方法は本実装時に決める（この段階では Available=false で何もしない）。
    /// 表示内容は当面のループ芝居（投手=投球ループ／打者・捕手・審判=待機）。エンジンのpitch進行との
    /// 同期は §7-2 アンカー同期の実装時に接続する。
    /// </summary>
    // 守備・走塁の3D化（打球処理の3D一本化）で足す分は DuelStageRig.Field.cs に置く。
    // 型を切り出さず partial にするのは、nested な Actor も _root 等の private も
    // アクセス変更なしで新ファイルから使えるため＝較正の巣（本ファイル）への diff を最小にする。
    public sealed partial class DuelStageRig : System.IDisposable
    {
        /// <summary>
        /// 基準ドット密度（16:9・全画面時の確定解像度）。実RTはカラム実寸から適応する（EnsureSize）。
        /// 監督判定（2026-08-05・実機8構成＋動きGIFの見比べ）: 480×270・ポスタライズ6段の
        /// ドット表現から **960×540・ポスタライズOFF（クリーン）** へ改定。design-18 §4.2 の
        /// 「顔の実効ピクセル2〜4px下限」制約は実絵比較の上で意図的に上書きした（同§4.2改定注記）。
        /// </summary>
        public const int BaseWidth = 960;
        public const int BaseHeight = 540;

        private const string LayerName = "DuelStage";
        private const float MoundDistance = 18.44f;
        private const float MoundHeight = 0.254f;
        private const float TargetHeight = 1.75f;
        // §4.2 確定（2026-07-26 監督判定）。撮影ハーネスは**この刻みで通す**必要がある
        // （飛び石で進めるとレート制限も足の運びも測ったことにならない）ので public
        public const float PoseFps = 12f;
        private static readonly Vector3 StageOrigin = new(0f, -500f, 0f);

        private const string BodyFbx = "Assets/DuelSpike/UBC/SuperheroMale.fbx";

        /// <summary>
        /// 着衣メッシュ（DuelUniformBaker が焼いた U2 資産。design-18 §3.5）。
        /// 痩せ強度は Slim1（中＝がっしり型。肩・胴×0.90/上腕×0.84/腿×0.92）＝2026-07-28 比較シートで監督選定。
        /// 無ければ素体のまま出す（着衣は表示品質の話であり対決ビュー自体は止めない）。
        /// </summary>
        private const string UniformMeshAsset = "Assets/DuelSpike/Generated/Body_Uniform_Slim1.asset";

        /// <summary>
        /// 球審専用の着衣メッシュ（恰幅Stout＝比較シート3案からStoutC選定＋スラックス脚。
        /// issue #445・2026-08-05 監督選定）。選手側の Slim1 は不変。
        /// 無ければ選手と同じ Slim1 へ落ちる（体型が痩せるだけで対決ビューは止めない）。
        /// </summary>
        private const string UmpireMeshAsset = "Assets/DuelSpike/Generated/Body_Uniform_Stout.asset";
        private const string BodyMeshName = "SuperHero_Male";

        /// <summary>
        /// 購入素体（School Baseball Player 0001。design-18 §3.1 で再配布NG＝gitignore）。
        /// 投手・打者・捕手に使う。無い環境（他端末・CI）では自動でU2素体へ落ちる＝対決ビューは止めない。
        /// 球審だけはU2素体のまま残す（2026-07-29 決定）: 購入素体のシルエットは野球のユニフォームそのもので、
        /// 紺に塗っても審判のスラックス・短つば帽にはならないため。素体2系統の併存を許容する。
        /// </summary>
        private const string VendorBodyFbx =
            "Assets/DuelSpike/Vendor/SchoolBaseballPlayer0001/School_Baseball_Player_0001.fbx";

        /// <summary>DuelVendorRegionBaker が焼いた領域IDつきメッシュ（UV1に領域ID）。</summary>
        private const string VendorRegionMeshDir = "Assets/DuelSpike/Vendor/Generated/";
        private const string VendorRegionMeshSuffix = "_Regions.asset";

        // ビルド同梱の索引を焼く側（DuelAssetCatalogBaker）から見るための読み取り口。
        // パスの単一ソースは上の const のまま＝ベイカ側にパスを書き写さない
        // ⚠ internal ではダメ（Assembly-CSharp と Assembly-CSharp-Editor は別アセンブリ＝見えない。
        //    PoseFps で一度踏んだのと同じ。CS0117 になる）
        public static string BodyFbxPath => BodyFbx;
        public static string VendorBodyFbxPath => VendorBodyFbx;
        public static string UniformMeshAssetPath => UniformMeshAsset;
        public static string UmpireMeshAssetPath => UmpireMeshAsset;
        public static string VendorRegionMeshDirPath => VendorRegionMeshDir;
        public static string VendorRegionMeshSuffixPath => VendorRegionMeshSuffix;

        /// <summary>購入素体の帽子（独立メッシュ）。打者はこれを消してヘルメットを付ける。</summary>
        private const string VendorCapMeshName = "A_Players_0001_Cap_0001";

        /// <summary>
        /// 役割別の領域配色（DuelUniformMeshBuilder.Region の並びで _RegionColors[8] に流し込む）。
        /// 実データはエンジンの <see cref="KokoSim.Engine.Nation.TeamColors"/>（data/team-colors.yaml 由来）で、
        /// ここは Unity の Color へ移した表現＋肌・シューズの固定色。
        ///
        /// <b>両校とも自校の配色で描く</b>（実物準拠・2026-07-29 監督決定）。旧実装は守備=白／打者=薄グレーの
        /// 定数で「輪郭が重なっても見分く」ことを保証していたが、実データ化でその保証は無くなった
        /// ＝紺同士の対戦では両者が似て見える。承知の上の仕様。
        /// </summary>
        private readonly struct UniformPalette
        {
            /// <summary>パレット表の「黒」#23242A。ベルト・既定のスパイクをこれに揃える。</summary>
            private static readonly Color PaletteBlack = new(0x23 / 255f, 0x24 / 255f, 0x2A / 255f);

            /// <summary>肌の固定色（全役割共通）。</summary>
            private static readonly Color SkinTone = new(0.85f, 0.68f, 0.55f);

            public readonly Color Shirt, Pants, Undershirt, Socks, Skin, Shoes, Cap, Belt;
            public readonly bool StripeOn;
            public readonly Color StripeColor;
            /// <summary>縞の本数[本/m]。シェーダへはアクターのスケールを掛けて渡す（RegionMaterial）。</summary>
            public readonly float StripeFreqPerMeter;

            public UniformPalette(Color shirt, Color pants, Color undershirt, Color socks, Color cap,
                bool stripeOn = false, Color stripeColor = default, float stripeFreqPerMeter = 0f,
                Color? shoes = null)
            {
                Shirt = shirt;
                Pants = pants;
                Undershirt = undershirt;
                Socks = socks;
                Cap = cap;
                Skin = SkinTone;
                // ベルトは固定の黒革（高校球児は黒ベルトが多数派）。値はパレット表の「黒」#23242A と
                // 揃える＝画面で選べる黒と同じ黒になる。**わずかでも赤に寄せると濃い臙脂に見える**
                // （ポスタライズが暖色側のバケットへ落とす。2026-07-29 実測）
                Shoes = shoes ?? PaletteBlack;
                Belt = PaletteBlack;
                StripeOn = stripeOn;
                StripeColor = stripeColor;
                StripeFreqPerMeter = stripeFreqPerMeter;
            }

            /// <summary>エンジンの確定配色（7領域＋縞）から作る。つばは帽子と1メッシュなので使わない。</summary>
            public static UniformPalette From(KokoSim.Engine.Nation.TeamColors c) => new(
                Rgb(c.Shirt), Rgb(c.Pants), Rgb(c.Undershirt), Rgb(c.Socks), Rgb(c.CapCrown),
                c.Stripe.On, Rgb(c.Stripe.Color), (float)c.Stripe.FreqPerMeter, Rgb(c.Shoes));

            private static Color Rgb(KokoSim.Engine.Nation.RgbColor c)
                => new(c.R / 255f, c.G / 255f, c.B / 255f);

            /// <summary>チーム色が届かない場面（スクショ・デモ・engine未接続）の既定＝白ユニ＋紺。</summary>
            public static UniformPalette Default => new(
                new Color(0.93f, 0.92f, 0.88f), new Color(0.93f, 0.92f, 0.88f),
                new Color(0.16f, 0.20f, 0.38f), new Color(0.16f, 0.20f, 0.38f),
                new Color(0.16f, 0.20f, 0.38f));

            /// <summary>
            /// 球審: 薄いブルーのシャツ＋黒スラックス（実物の球審装束。issue #445）。チーム色の対象外。
            /// シャツは候補3色の比較シートから B2 を監督選定（2026-08-05）。スラックスはパレット表の
            /// 黒 #23242A＝わずかでも赤に寄せるとポスタライズで臙脂に落ちる（この struct 冒頭の掟）。
            /// アンダーシャツは未着用（2026-08-05 監督指示）＝Stout メッシュから領域自体が消えているが、
            /// 古い焼き資産へフォールバックした環境でも腕が素肌に見えるよう配色も肌色にしておく。
            /// 帽子・ソックスは現行の濃紺のまま。
            /// </summary>
            public static UniformPalette Umpire => new(
                new Color(0.50f, 0.65f, 0.77f), PaletteBlack,
                SkinTone, new Color(0.14f, 0.16f, 0.22f),
                new Color(0.14f, 0.16f, 0.22f));
        }

        /// <summary>縦縞の線の太さ[m]（実物のピンストライプは間隔に依らず3mm前後の細線）。</summary>
        private const float StripeLineWidthM = 0.0035f;

        // 守備側（投手・捕手）と攻撃側（打者）の配色。役割は固定で、攻守交代で入れ替わるのは
        // 「どちらの学校の色か」の方＝SetTeamColors で塗り替える（アクターは作り直さない）
        private UniformPalette _defenseColors = UniformPalette.Default;
        private UniformPalette _offenseColors = UniformPalette.Default;

        /// <summary>
        /// 生成前に一度だけ渡す初期配色（ステージ構築時に読む）。null なら既定（白＋紺）。
        /// 構築後の変更は <see cref="SetTeamColors"/> を使う。
        /// </summary>
        public static (KokoSim.Engine.Nation.TeamColors Defense, KokoSim.Engine.Nation.TeamColors Offense)? InitialTeamColors { get; set; }

        /// <summary>
        /// 守備側・攻撃側の配色を差し替える（攻守交代のたびに呼ぶ）。素体とクリップは作り直さず
        /// マテリアルだけ塗り替える＝進行中のポーズ・アンカー同期を壊さない。
        /// </summary>
        public void SetTeamColors(KokoSim.Engine.Nation.TeamColors defense, KokoSim.Engine.Nation.TeamColors offense)
        {
            if (defense == null || offense == null) return;
            _defenseColors = UniformPalette.From(defense);
            _offenseColors = UniformPalette.From(offense);

            Repaint(_pitcher, _defenseColors);
            Repaint(_catcher, _defenseColors);
            Repaint(_batter, _offenseColors);   // 打者ヘルメットは暗紺固定＝チーム色の装備は無い
        }

        private static void Repaint(Actor a, UniformPalette pal)
        {
            if (a == null) return;
            if (a.RegionMat != null) ApplyRegionColors(a.RegionMat, pal, a.BodyScale);
            foreach (var m in a.TeamMats) if (m != null) m.SetColor("_BaseColor", pal.Cap);
        }

        /// <summary>
        /// 装着直後に「チーム色で塗った装備」のマテリアルを拾っておく（攻守交代で塗り替える対象）。
        /// 装備はプリミティブの寄せ集めで、同じ呼び出しの中に固定色（ケージの黒・パッド）も混ざるため、
        /// <b>色の一致で選ぶ</b>。装着時点の accent と厳密に同じ Color を入れているので取りこぼさない。
        /// </summary>
        private static void CollectTeamMats(Actor a, Color accent)
        {
            if (a == null || a.Go == null) return;
            foreach (var r in a.Go.GetComponentsInChildren<Renderer>(true))
            {
                var m = r.sharedMaterial;
                // 装備は必ず FlatMaterial（自前シェーダ）なので、_UseRegions を持たないマテリアル
                // （素体に残る URP Lit 等）は対象外。HasProperty で弾かないと毎フレーム警告が出る
                if (m == null || !m.HasProperty("_BaseColor") || !m.HasProperty("_UseRegions")) continue;
                // 素体の領域マテリアルは _BaseColor が白のダミー＝ここで拾ってはいけない
                // （チーム色に白を選ばれた瞬間に素体まで塗り替え対象になる）。RegionMat 側で塗り替わる
                if (m.GetFloat("_UseRegions") > 0.5f) continue;
                if (m.GetColor("_BaseColor") == accent && !a.TeamMats.Contains(m)) a.TeamMats.Add(m);
            }
        }

        /// <summary>
        /// クリップ供給元（先勝ち＝同名なら先の要素が勝つ）。自作モーション（Assets/Motions＝Rokoko収録・
        /// コミット可）を先に見て、無ければ購入パック（Assets/Animo＝再配布NG・gitignore）へ落ちる。
        /// プローブ（DuelMotionSpike / DuelMotionCatalog）も同じ入口を通す＝実測と本番でクリップがずれない。
        /// </summary>
        public static readonly string[] ClipFbxSources =
        {
            // Mixamo「Baseball Strike」（Adobe素材＝gitignore。自作収録の代替候補として検証中）
            "Assets/Motions/Mixamo/Batter_Mixamo_Strike_01.fbx",
            // 自作Rokokoの投球クリップ（Pitcher_Pitching_01.fbx）は2026-07-29にセットポジション投球へ
            // 差し替えたため供給元から外した。戻すときはこの行と PitcherClipName を一緒に戻す
            "Assets/Motions/Duel/Batter_Batting_01.fbx",
            "Assets/Animo/AA_Basic_Baseball_Movement_Animations_Package/Animations/AA_Basic_Baseball_Movement_Animations_Package.fbx",
        };

        /// <summary>
        /// 投手の投球クリップ名。購入パック（AA）のセットポジション投球（design-18 §7-2）。
        /// 自作Rokokoモーションは振りかぶり込みで尺が長く、走者の有無に関わらずセットから投げる方針
        /// （時間短縮）に合わないため 2026-07-29 に差し替えた。
        /// </summary>
        public const string PitcherClipName = "AA_Baseball_Pitcher_Pitching_type01";

        // 対決4体が使うクリップ名。ここに直書きせず定数にしてあるのは、ビルド同梱の索引
        // （DuelAssetCatalog）を**コード側の名前表から自動で埋める**ため（F8）。
        public const string CatcherIdleClipName = "AA_Baseball_Catcher_Idle";
        public const string UmpireIdleClipName = "AA_Baseball_Umpire_Crouch_Idle";
        public const string UmpireStrikeClipName = "AA_Baseball_Umpire_Crouch_Strike_Type01";
        // 見逃し三振専用のコール（issue #533）。無い環境ではnull＝UmpireStrikeClipNameのままフォールバック
        public const string UmpireStrikeOutClipName = "AA_Baseball_Umpire_Crouch_Strike_Out_Type01";
        public const string BatterHbpClipName = "AA_Baseball_Batter_Hit_ByPitch_Type01";
        // バント構え（issue #490）。購入パック（AA）に既存の4クリップ。無い環境では黙って通常の
        // 打撃クリップへフォールバックする（他の一発物クリップと同じ「無いものはnull」流儀）。
        public const string BatterIdleToBuntClipName = "AA_Baseball_Batter_Idle_To_Bunt";
        public const string BatterBuntLoopClipName = "AA_Baseball_Batter_Bunt_Loop";
        public const string BatterBuntHitClipName = "AA_Baseball_Batter_Bunt_Hitting";
        public const string BatterBuntMissClipName = "AA_Baseball_Batter_Bunt_Miss";

        /// <summary>
        /// 投手クリップを本塁向き（−Z）へ揃えるyaw[度]。
        /// 収録モーキャップはセット姿勢の向きと実際に踏み込んで投げる方向が一致しない。
        /// 見えるべきは「打者へ投げている」ことなので、`RunFacingProbe` のセット姿勢基準ではなく
        /// `RunRootPathProbe` の踏み込みベクトル基準を採る。
        /// 2026-07-29実測（AAクリップ・旧yaw236で計測）: t=3.5sの変位(0.73,-1.06)＝本塁方向から+34.6°ずれ
        /// → 236+34.6≒270。素のクリップの踏み込みはほぼ純粋な−X（変位(-1.44,+0.02)）で、270°で−Zへ乗る。
        /// このとき セット姿勢の胸法線は−X＝三塁側を向く（右投手のセットとして正しい）。
        /// </summary>
        public const float PitcherYaw = 270f;

        /// <summary>
        /// 打者のクリップ名。Mixamo「Baseball Strike」の絵が馴染むかの検証中（2026-07-30）。
        /// **構え→スイング→フォローが1本**に入っているため、待機と一発物で同じクリップを区間違いで使う。
        /// 素の向きは RunFacingProbe 実測 chest=52°/hips=69°（自作Rokokoは82°/103°）＝約30°ぶん
        /// 開いた構えなので、<see cref="DuelCalibration.BatterYawDeg"/> をその差ぶん戻してある。
        ///
        /// 戻すときは、この定数と下の3つのアンカー・SwingMissPlane/SwingHitContact・
        /// ClipFbxSources の Mixamo 行・DuelCalibration.Default の打撃フォーム秒（Grip*/Head*/Knee*）を
        /// まとめて自作Rokoko値へ戻す。旧値はいずれも各行のコメントに残してある。
        /// </summary>
        public const string BatterClipName = "Batter_Mixamo_Strike_01";

        // 打者クリップ内の実測アンカー（RunClipSpeedProbe 0.05s刻み・右手速度 2026-07-30・len=6.93s）:
        // 0.0-0.85s=構え（0.5m/s前後で静止） / 0.90-1.60s=スイング（ピーク6.1m/s @1.30s） /
        // 1.60-2.60s=フォロー〜戻し / 2.60-6.30s=**ほぼ静止の間延び（使わない）** / 6.30s-=小さな動き。
        // 走り出しは収録に無い（RunRootPathProbe: 全域で移動±0.25m以内＝その場で振るだけ）。
        // 待機は構え区間だけをループさせ、スイングは頭出しで呼ぶ。
        //
        // 旧・自作Rokoko（Batter_Batting_01）の値: IdleEnd=1.70 / SwingStart=2.80 / RunStart=3.25
        /// <summary>
        /// 自作Rokoko収録の打撃クリップ。2026-07-30 に打撃はMixamoへ差し替えたが、
        /// **構えだけこちらを使える**ようにしてある（<see cref="DuelCalibration.IdleFromOwnClip"/>）。
        /// Mixamo FBX は再配布NGでgitignoreなので、クローン直後の環境ではこちらが打撃クリップの代替にもなる。
        /// </summary>
        public const string BatterOwnIdleClipName = "Batter_Batting_01";

        /// <summary>自作クリップの構え区間の折り返し[秒]（差し替え前の BatterIdleEnd）。</summary>
        private const float BatterOwnIdleEnd = 1.70f;

        /// <summary>
        /// 自作構えを使うときにポーズ補正を評価するクリップ内秒。
        /// 補正の窓（膝・胴・握り）はMixamoの時刻表なので、自作クリップの時刻をそのまま渡すと
        /// 窓の境目が待機ループの途中に現れる（膝が抜ける・胴が回り出す）。構えは静止した1ポーズなので
        /// Mixamoの構えの真ん中で固定評価する＝「いまの構えと同じ補正」がそのまま乗る。
        /// </summary>
        private const float OwnIdlePoseRefSec = 0.50f;

        private const float BatterIdleEnd = 0.85f;     // 待機ループの折り返し（ここから先を待機中に見せない）
        private const float BatterSwingStart = 0.90f;  // ここから先が「振っている」区間（バット片手判定の許可域）
        private const float BatterRunStart = 2.60f;    // フォローの収まり。ファール・空振りはここで打ち切って構えへ戻す
                                                       // （走り出しが無いので「間延びの手前」の意味になる）

        public RenderTexture Target { get; private set; }

        /// <summary>
        /// 色数制限（ポスタライズ）の段数。0または1で無効＝素通し。
        /// 監督選定（2026-07-28・絵柄マトリクス）: 6段・輪郭なし を採用既定とした。
        /// → 監督判定（2026-08-05・クリーン化見比べ）: **無効（1）** へ改定。ポストの実装・
        /// ブリット経路は温存する（戻すときは 6f に上げるだけ。DuelStagePixelPost Pass0）。
        /// </summary>
        public static float PosterizeLevels = 1f;

        private RenderTexture _colorRt;   // ポスト前のカメラ出力（ポスタライズ有効時のみ生成）
        private Material _postMat;
        private Material _zoneMat;   // ストライクゾーン重畳（ポストの後に GL で引く）
        public bool Available { get; private set; }

        private GameObject _root;
        private Camera _camera;
        private GameObject _bat;
        private readonly List<Actor> _actors = new();
        // 生成した全マテリアル（HideAndDontSave はドメインリロードを跨いで残るため、Disposeで明示破棄する。
        // 従来も漏れていたが、領域色でマテリアル数が増えるのを機に修正）
        private readonly List<Material> _materials = new();
        private Actor _pitcher, _batter, _catcher, _umpire;
        private DuelCalibration _cal = DuelCalibration.Default;   // 実行時ポーズ補正が参照する現在値
        private float _acc;
        private int _poseGen;
        private float _fovBaseDeg = 2.9f;   // 16:9時の垂直FOV（ApplyCalibrationが上書き。既定=R-comp60）
        private Transform _capMount, _helmetMount;   // 被り物の位置オフセット枠（gear-head直下・実寸[m]）
        private Transform _cHelmetMount, _cMaskMount;   // 捕手のヘルメット・マスク（同上）
        private Transform _cChestMount;                 // 捕手の胸プロテクター（gear-chest直下）

        // 結果分岐の一発物クリップ（design-18 §7-2 第2弾。無いものはnull＝その分岐は待機のまま）
        private AnimationClip _swingMissClip, _swingHitClip, _hbpClip, _umpireStrikeClip, _umpireStrikeOutClip;
        // 球審の実際に予約したコールクリップ（Type01 or 見逃し三振専用。ScheduleReactionsが選ぶ）
        private AnimationClip _pendingUmpireClip;
        // バント構え一式（issue #490）。無い環境ではすべてnull＝バント指示が出ても通常の打撃構えのまま
        // （他の一発物クリップと同じ「無いものは待機のまま」流儀）。
        private AnimationClip _buntIdleToStanceClip, _buntLoopClip, _buntHitClip, _buntMissClip;
        private AnimationClip _batterOwnIdle;   // 自作Rokokoの打撃クリップ（構えだけ使う。無い環境ならnull）

        // 捕球クリップ（5方向）。CatchAnchors と同じ並び。取れなかったものはnull＝選択から外れる
        private AnimationClip[] _catchClips;
        // 後逸クリップ（4本。issue #525）。PassedBallAnchors と同じ並び。購入パックに無い環境では
        // 全てnull＝捕り損ねる球でも捕手は待機のまま（他の一発物と同じ「無いものは待機のまま」流儀）。
        // ただし**球は捕らせない**（捕らない判断は SelectCatchAnchor 側なのでクリップの有無に依らない）
        private AnimationClip[] _passedBallClips;

        // ボール（issue #332・design-18 §4.5-②「ボールとカメラはフルレート維持」）。
        //
        // 状態は2つ:
        //  ①**手に貼り付け**（振りかぶり〜発射）= 投球する手ボーンの子。手がどこへ動いても球は手の中
        //  ②**飛翔**（発射〜到達）= ステージの子。Tick の毎フレームで進める（12fps量子化の対象外）
        // ①にしているのは、リリース点を毎回実測して置く方式だと出所が合わなかったため（2026-07-30）:
        //   ・アンカーの ReleaseTime(3.05) は手が既に振り下ろされた後（高さ1.0m・実測）で腰から球が出た
        //   ・SeekActor は「ポーズだけ合わせ位置は据え置く」（#335）ので、リリース瞬間だけ手のz座標が
        //     0.55m前へワープし、次の12fpsコマで戻る＝置いた点と絵の手が食い違う
        // 手の子にすれば、どちらも構造的に起きない（グラブ・バットと同じ実績パターン）
        private GameObject _ball;
        private GameObject _ballShadow;    // 打球の影（地面の楕円プリミティブ・実寸。issue #401①）。守備ビューでのみ追従させる
        private Transform _ballHand;       // 貼り付け先（投球する手ボーン）。null=まだ解決していない
        private Vector3 _ballHeldLocal;    // 手ボーンローカルでの球の位置（手の中心＋つまみ）
        private bool _ballHeld;            // 手に貼り付いているか
        private Vector3 _ballRelease;      // 発射点（ステージローカル・飛翔の弦の始点）
        private Vector2 _ballPlate;        // 本塁面の実着弾位置（x=左右, y=高さ）
        private float _ballFlight;         // この球の飛翔時間[s]
        private float _ballIvb, _ballIhb;  // 誘発縦/横変化[m]（弾道なしは0＝素の垂れだけ）
        private float _ballT = -1f;        // 発射からの経過[s]。負=飛んでいない

        // 捕球（③ミットの中）。判定面（z=0）で止めると球はミットの0.6m手前で消える（実測 2026-07-30）＝
        // 捕られる球は捕球面まで接線で延長し、着いたらミット（左手ボーン）の子へ移して一緒に引かれる
        private float _ballArrival;         // 発射から捕球面に達するまで[s]（捕られない球は飛翔時間そのまま）
        private Vector3 _ballCatchPoint;    // 捕球面での球の位置（ステージローカル）＝ミットIKの狙点
        private bool _ballWillBeCaught;     // この球は捕手が捕るか
        private bool _ballInMitt;           // ミットの子になっているか
        private float _ballInMittRemaining; // ミットの中で残す秒数

        // ---- 打球（接触後の一瞬。2026-07-31）----
        // 軌道は engine の打球セグメントをそのまま評価する＝この後すぐ俯瞰が描くのと**同じ単一ソース**。
        // Unity側で打球を積分し直すと、対決ビューと俯瞰で行き先が食い違う（投球の掟と同じ理由）
        private KokoSim.Engine.Match.Timeline.Playback.PlaybackBallSegment _pendingBatted;   // 次のリリースで仕込む
        private KokoSim.Engine.Match.Timeline.Playback.PlaybackBallSegment _batted;
        private float _battedT = -1f;        // 接触からの経過[s]。負=打球になっていない
        private float _battedDur;            // セグメントの長さ[s]（超えたら位置を止める）
        private Vector3 _battedOffset;       // 接触点 − セグメント始点（下の LaunchFadeSeconds で解消）
        private float _battedFade;           // 上を解消するまでの秒数＝打点からの自由落下時間
        private readonly List<Vector4> _trail = new();   // 残像（xyz=ステージローカル / w=残像時計[s]）
        /// <summary>
        /// 残像の「現在時刻」（<see cref="DrawBattedTrail"/> のフェード計算の基準）。対決ビューは
        /// <see cref="_battedT"/>（接触からの経過）、守備ビューはプレー内の絶対時計をそのまま使う
        /// （issue #398 ①）。フェード計算は差分（now - 各点のw）しか見ないので、基準が変わった
        /// 瞬間（対決→守備の引き継ぎ）は古い基準の点が「もう古い」扱いで一括で刈られるだけ＝
        /// 数フレームぶん短く見えるが実害はない（切り替わり自体が構図のカットと重なるため絵に出ない）。
        /// 残像が無ければ負。
        /// </summary>
        private float _trailNow = -1f;

        // ---- バットの軌跡（2026-08-01）----
        // 打球の残像（上）は「点の軌跡＝線」だが、バットは棒なので**掃いた面**が要る＝両端を持つ。
        // 座標系と w の使い方は _trail に揃える（xyz=ステージローカル / w=時計[s]）。
        private readonly List<Vector4> _batTrailKnob = new();   // ノブ端
        private readonly List<Vector4> _batTrailTip = new();    // バレル先端
        private float _batTrailT;                               // ステージの連続時計[s]（打席をまたいでも巻き戻さない）
        private Vector3 _batTrailLastAxis;                      // 前サンプルのバット軸（角速度ゲート用）
        private bool _batTrailHasLast;
        // スイング区間の武装フラグ（issue #444）。StartSwing で立ち、FinishOneShot（待機復帰）と
        // 打席の仕切り直しで下りる。角速度ゲートだけだと「構えへ戻る速い動き」も軌跡になるため、
        // 打ちにいく振りの間だけ積む（判定は SampleBatTrail の毎コマ状態比較＝1コマ副作用にしない）
        private bool _batTrailSwing;
#if UNITY_EDITOR
        /// <summary>【実測プローブ用】武装ゲートで実際に積んだサンプル数（累計）。</summary>
        public int BatTrailSampledForCapture { get; private set; }
        /// <summary>【実測プローブ用】角速度は閾値超（旧実装なら積んでいた）だが武装外で抑止した数（累計）。</summary>
        public int BatTrailSuppressedForCapture { get; private set; }
        /// <summary>【実測プローブ用】カウンタを0へ戻す。</summary>
        public void ResetBatTrailCountersForCapture() { BatTrailSampledForCapture = 0; BatTrailSuppressedForCapture = 0; }
#endif

        private sealed class Actor
        {
            public GameObject Go;
            public Animator Animator;
            public PlayableGraph Graph;
            public AnimationClipPlayable Playable;
            public AnimationClip Clip;
            public float Time;
            // クロスフェード（切り口の繋ぎ）。Output直下は常に Mixer で、in1=現行クリップ・in0=退場ポーズ。
            // フェード中でないときは in0 未接続・in1 の重み1＝実質ミキサー無しと同じ絵になる
            public AnimationMixerPlayable Mixer;
            public AnimationClipPlayable FadeFrom;   // 退場側（Pauseして進めない＝ルートモーション寄与ゼロ）
            public AnimationClip FadeFromClip;       // 退場側の素のクリップ参照（SyncBatterYawが家系判定に使う）
            public float FadeRemaining;
            public float FadeDuration;
            public Vector3 BasePos;
            public Quaternion BaseRot;
            public float GroundY;
            public float AnkleOffset;
            public bool HoldAtEnd;   // クリップ末尾で頭姿勢のまま停止する（投手＝アンカー同期の合間はセット姿勢で待つ）
            public bool Holding;
            public AnimationPlayableOutput Output;   // クリップ差し替え（結果分岐の一発物）用
            public AnimationClip IdleClip;           // 一発物の終了後に戻る待機ループ（null=戻らない）
            public float IdleEndAt;                  // 待機ループの折り返し時刻（0=クリップ末尾）。
                                                     // 構えとスイングが同じクリップの打者は、ループのたびに張り直す必要がある
            public float EndAt;                      // 一発物の早期終了時刻（0=クリップ末尾まで）
            public float DefaultEndAt;               // 一発物の終了後に戻す EndAt（0=末尾まで）。
                                                     // 投手は購入クリップ6.00sのうちフォロースルーまでで打ち切りたいので使う
            // 再生速度（1=収録のまま）。クリップ内時刻の進み方だけを一様に変える＝**緩急の比は保たれる**
            // ので「内部緩急を改変しない」掟には触れない（禁止なのはキーの打ち直し）。
            // 速度を入れたら**アンカーの逆算も実秒→クリップ秒へ換算し直す**こと（Speed倍する）。
            // 忘れると面通過が球の到達からずれ、絵では「差し替えたら合わなくなった」としか見えない
            public float Speed = 1f;
            public float HoldSeconds;                // 末尾到達時、待機へ戻る前にそのポーズで止める秒数（0=即戻る）
            public float HoldRemaining;               // ↑の残り時間（>0の間はポーズ据え置き＝Graph.Evaluateしない）
            public Material RegionMat;               // 領域塗り分けマテリアル（攻守交代の塗り替え用。一色フォールバック時はnull）
            public float BodyScale;                  // ワールドスケール（縞の刻みをオブジェクト空間へ換算するのに要る）
            public readonly List<Material> TeamMats = new();  // チーム色で塗った装備のマテリアル（帽子・捕手ヘルメット殻）
            public Vector3 HeadUpLocal;              // 頭蓋の上方向（頭ボーンローカル）。素立ち＝頭が水平な唯一のポーズで実測
            public Vector3 HeadFwdLocal;             // 頭蓋の前方向（同上）。視線ターゲット由来の水平前だとロールする（斜め被り）ため実測で持つ
            public bool Mirrored;                    // 鏡映親ノードが反転中か。Foot IK の同期（SyncFootIk）に使う

            /// <summary>
            /// 直前の <see cref="StepActor"/> でこの体のポーズを**書き直したか**。
            ///
            /// ポーズ補正（<see cref="FixBatterPose"/> / <see cref="FixCatcherPose"/>）は骨への
            /// **相対**回転なので、同じポーズへ二度当てると寄りすぎる（design-18 §4.5-③）。
            /// 据え置き（<see cref="HoldRemaining"/>）・恒久停止（<see cref="Holding"/>）・
            /// 間引き（<see cref="PoseInterval"/>）はどれも <c>Graph.Evaluate</c> を呼ばずに抜けるので、
            /// 「コマが進んだ＝補正して良い」ではない。実測 2026-08-01（issue #368・空振り・速度1.4）:
            /// クリップが 1.733 で止まっている据え置き 0.8秒の間、右前腕が
            /// (0.33,-0.73,0.60)→(-0.06,-0.80,0.60)→(-0.45,-0.65,0.61)→(-0.72,-0.32,0.62)→(-0.79,0.12,0.60)
            /// と**1コマ28°で回り続けていた**＝振り切って止めたはずのフィニッシュで腕だけが回る。
            /// このポーズは画面に最も長く映る（監督指摘「振り終わりの腕がおかしい」）。
            /// </summary>
            public bool PoseAdvanced;

            // ---- 守備・走塁のN体化で足した分（既定値は現行4体の挙動と同じ＝絵は不変） ----

            /// <summary>
            /// 待機ループ中だけ水平位置を <see cref="BasePos"/> へ毎コマ戻す（収録の水平ドリフト対策・#291）。
            /// 打者だけ true。旧実装は StepActor 内の `a == _batter` identity 判定だったが、
            /// 守備・走者を同じ StepActor で回すためフラグへ移した。
            /// </summary>
            public bool RebaseXZWhileIdle;

            /// <summary>
            /// スイング再生中（頭出し後〜待機へ戻るまで）、水平位置の**x成分だけ**毎コマ
            /// <see cref="BasePos"/> へ戻す（収録ルートモーションの横ドリフト対策・issue #446）。
            /// 打者だけ true。z（投手方向の踏み込み）は戻さない＝踏み込みの見た目は残す。
            /// 頭出し直後の一括リセットは <see cref="RebaseActorXZ"/>（x/z両方・一度きり）が受け持ち、
            /// 以降の毎コマぶんはこちらが引き継ぐ。
            /// </summary>
            public bool RebaseXWhileSwinging;

            /// <summary>false の間は StepActor が何もしない（打席中の野手など・Tick ごと飛ばす）。</summary>
            public bool Enabled = true;

            /// <summary>
            /// ポーズ更新の最小間隔[秒]。0＝ポーズ量子化（12fps）のコマごとに毎回進める＝現行の挙動。
            /// 遠い外野手のように数pxでしか映らない役を間引くための逃げ道。
            /// ⚠ **アクターごとの固定間隔**であること。「特定のコマでだけ」の分岐にすると
            /// 12fps格子に乗るかが位相次第になり、乗らない結果だけ黙って壊れる（design-18 §4.5-⑩）。
            /// </summary>
            public float PoseInterval;

            /// <summary>PoseInterval の積み残し[秒]。</summary>
            public float PoseAccum;
        }

        /// <summary>
        /// Humanoid Foot IK をミラー状態に同期する（ミラー中はOFF・非ミラーは既定どおりON）。
        /// AnimationClipPlayable は既定で Foot IK 有効で、足ゴールを**ワールド空間の鏡映盲な回転数学**で
        /// 解くため、負スケール祖先の下では両足が「鏡映されない側」のゴールへ引っ張られ、
        /// 脚だけ頭の高さまで折り畳まれる（issue #337 受け入れ撮影で実害: ズボン・靴が胴に隠れて
        /// 「腰から下が消えた」ように見える）。Playable は差し替えで作り直されるので、
        /// 生成した直後にも必ず呼ぶこと。
        /// </summary>
        private static void SyncFootIk(Actor a)
        {
            if (a == null) return;
            if (a.Playable.IsValid()) a.Playable.SetApplyFootIK(!a.Mirrored);
            if (a.FadeFrom.IsValid()) a.FadeFrom.SetApplyFootIK(!a.Mirrored);
        }

        /// <summary>
        /// ルートモーションを伴う Graph.Evaluate（Foot IK と同根の鏡映盲対策・issue #447）。
        /// Unity 組み込みのルートモーション適用は、deltaPosition（ワールド空間）を localPosition へ
        /// 積むときに**祖先の負スケール（鏡映）を無視する**らしく、ミラー中も非ミラーと同じ符号の x が
        /// 積まれ続ける。ポーズ（各ボーン）は鏡映親ノードのスケールで正しく鏡映されるのに、ルート自体の
        /// 横移動だけ鏡映されない＝体が接地とずれて横へ滑りながら投げる絵になる（左投手のみ発症・
        /// 恒等スケールの右投手は無関係）。ここで適用前後の localPosition.x 差分を読み直し、
        /// 鏡映面（x=0）で反転して当て直す。回転（`localRotation *= deltaRotation`）は祖先スケールに
        /// 一切依存しない純粋なクォータニオン合成で、鏡映後の見え方は描画時に祖先スケールが正しく
        /// 効くため触らない（回転だけ直すと今度は回転が二重に鏡映されて壊れる）。
        /// 非ミラー（a.Mirrored=false）は分岐なしで従来の Evaluate 呼び出しと完全に同じ経路＝
        /// 右投手の絵はビット一致のまま。
        /// </summary>
        private static void EvaluateWithMirrorFix(Actor a, float dt)
        {
            if (a == null || !a.Graph.IsValid()) return;
            if (!a.Mirrored || a.Animator == null || !a.Animator.applyRootMotion)
            {
                a.Graph.Evaluate(dt);
                return;
            }
            float xBefore = a.Go.transform.localPosition.x;
            a.Graph.Evaluate(dt);
            var pos = a.Go.transform.localPosition;
            pos.x = 2f * xBefore - pos.x;   // 観測した x デルタを反転して当て直す
            a.Go.transform.localPosition = pos;
        }

        public DuelStageRig()
        {
            int layer = LayerMask.NameToLayer(LayerName);
            var body = LoadBody();
            var pitchClip = LoadClip(PitcherClipName);
            _batterOwnIdle = LoadClip(BatterOwnIdleClipName);
            // Mixamo FBX は再配布NGでgitignore＝クローン直後の環境には無い。黙って null のまま組むと
            // 打者だけ素立ちのTポーズになるので、自作クリップへ落とす（アンカーは合わないが絵は出る）
            var batClip = LoadClip(BatterClipName);
            if (batClip == null)
            {
                batClip = _batterOwnIdle;
                Debug.LogWarning("[DuelStage] 打撃クリップ " + BatterClipName
                    + " が無いので自作 " + BatterOwnIdleClipName + " で代用する（アンカーは未調整）");
            }
            var catClip = LoadClip(CatcherIdleClipName);
            var umpClip = LoadClip(UmpireIdleClipName);
            if (layer < 0 || body == null || pitchClip == null || batClip == null || catClip == null || umpClip == null)
            {
                Debug.LogWarning("[DuelStageRig] assets unavailable (layer/body/clips) - duel view disabled");
                return;
            }

            // 残骸掃除: HideAndDontSave はPlay終了・ドメインリロードを跨いで生き残るため、
            // Disposeが走らず孤児化した過去のステージが同座標に重なって二重に映る（2026-07-26実害）。
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (go != null && go.name == "~DuelStageLive" && go.transform.parent == null
                    && (go.hideFlags & HideFlags.HideAndDontSave) != 0)
                    Object.DestroyImmediate(go);
            }

            _root = new GameObject("~DuelStageLive") { hideFlags = HideFlags.HideAndDontSave };
            _root.transform.position = StageOrigin;
            BuildGround(layer);

            // チーム配色は素体を作る前に確定させる（SpawnActor がマテリアルを焼くため）。
            // 未指定＝スクショ・デモ経路は既定（白＋紺）で立つ
            if (InitialTeamColors is { } tc)
            {
                _defenseColors = UniformPalette.From(tc.Defense);
                _offenseColors = UniformPalette.From(tc.Offense);
            }

            // 立ち位置・装備位置は絵作りパラメータ（DuelCalibration.Default が単一ソース。
            // 球審の#291スロット位置の経緯コメントも Default 側の値に対応する）。
            // スポーン位置から較正値で置く＝装備の視線ベイク（AttachGear）が較正後の位置で走る
            var cal0 = DuelCalibration.Upgrade(CalibrationOverride ?? DuelCalibration.Default);

            // yawはクリップごとの素の向き実測（DuelMotionSpike.RunFacingProbe）から:
            // 投手クリップ（自作Rokoko）は素で+Z向き＝PitcherYaw、脇3クリップ（購入AA）も素で+Z向き
            // 投手・打者・捕手は購入素体（無い環境ではU2素体へ自動フォールバック）。球審だけはU2素体のまま。
            var vendor = LoadVendorBody();
            bool useVendor = vendor != null;
            var playerBody = useVendor ? vendor : body;

            _pitcher = SpawnActor(playerBody, pitchClip, new Vector3(0f, MoundHeight, MoundDistance), PitcherYaw,
                _defenseColors, MoundHeight, layer, useVendor);
            _pitcher.HoldAtEnd = true;   // 投手は投げ切ったらセット姿勢で次のアンカー同期を待つ（勝手にループしない）
            // 購入クリップは6.00sあり、リリース後もマウンドを降りて戻る所まで入っている。
            // フォロースルーを見せ切ったところで打ち切ってセット姿勢へ戻す（DefaultEndAt は FinishOneShot が張り直す）
            _pitcher.DefaultEndAt = _pitcher.EndAt = ReleaseTime + FollowSeconds;
            _batter = SpawnActor(playerBody, batClip, new Vector3(cal0.BatterX, 0f, cal0.BatterZ), cal0.BatterYawDeg,
                _offenseColors, 0f, layer, useVendor);
            // 購入素体は帽子を素で持っている。打者はヘルメットを被せるので、その帽子を落とす
            if (useVendor) SetChildActive(_batter.Go, VendorCapMeshName, false);
            _batter.IdleClip = batClip;
            _batter.IdleEndAt = BatterIdleEnd;
            _batter.EndAt = BatterIdleEnd;   // 生成直後の初回ループでスイングまで流さない
            _batter.RebaseXZWhileIdle = true;   // 構え区間の水平ドリフトを毎コマ戻す（#291。旧: StepActor の identity 判定）
            _batter.RebaseXWhileSwinging = true;   // スイング区間はx（横）だけ毎コマ戻す（#446）
            _catcher = SpawnActor(playerBody, catClip, new Vector3(cal0.CatcherX, 0f, cal0.CatcherZ), 0f,
                _defenseColors, 0f, layer, useVendor);
            // 捕手も素の帽子を落とす。日本の高校野球の捕手は「捕手用ヘルメット＋マスク」で、
            // 帽子の上にマスクを重ねる形ではない（帽子を残すと頭が二重に膨らむ 2026-07-29 監督指摘）。
            // ヘルメットとマスクは CatcherGear 側（殻＋ケージバー）が受け持つ
            if (useVendor) SetChildActive(_catcher.Go, VendorCapMeshName, false);
            _catcher.IdleClip = catClip;
            // 球審は捕手の真後ろ・打者（x=-0.9側）から遠い肩越しに覗く「スロット」位置（#291）。
            // 旧値(-0.35,-1.7)は捕手とのz間隔0.7mしか無く、しゃがみ姿勢同士の胴回りでめり込んで見えていた。
            // z間隔を1.0mへ広げ、x offsetは打者と反対側（+x）へ倒して基準構図R-comp60から見て
            // 捕手の背後に自然に収まるようにする。
            _umpire = SpawnActor(body, umpClip, new Vector3(cal0.UmpireX, 0f, cal0.UmpireZ), 0f,
                UniformPalette.Umpire, 0f, layer, umpireBody: true);
            _umpire.IdleClip = umpClip;

            // 結果分岐の一発物（§7-2第2弾）。素の向きは打者系/捕手系とも+Z（RunFacingProbe実測の規約どおり）。
            // 空振り/ミートとも自作打撃クリップの同じスイング区間を使う（収録が1本なので分岐を持てない）。
            // 差は打ち切り位置だけ＝空振り・ファールは走り出し前で構えへ戻す（ScheduleReactions）
            _swingMissClip = batClip;
            _swingHitClip = batClip;
            _hbpClip = LoadClip(BatterHbpClipName);
            // バント構え一式（issue #490）。購入パックに無い環境では全てnullのまま＝バント指示が出ても
            // 通常の打撃構え・スイングで代用する（他の一発物と同じ「無いものは待機のまま」流儀）。
            _buntIdleToStanceClip = LoadClip(BatterIdleToBuntClipName);
            _buntLoopClip = LoadClip(BatterBuntLoopClipName);
            _buntHitClip = LoadClip(BatterBuntHitClipName);
            _buntMissClip = LoadClip(BatterBuntMissClipName);
            _catchClips = new AnimationClip[CatchAnchors.Length];
            for (int i = 0; i < CatchAnchors.Length; i++) _catchClips[i] = LoadClip(CatchAnchors[i].Clip);
            // 後逸（issue #525）。捕球クリップと同じ作り＝取れなかったものはnullで選択から外れる
            _passedBallClips = new AnimationClip[PassedBallAnchors.Length];
            for (int i = 0; i < PassedBallAnchors.Length; i++) _passedBallClips[i] = LoadClip(PassedBallAnchors[i].Clip);
            // 球審ストライクコール。Type01(3.33s)を**確定採用**（2026-07-31 監督判断・#353 の録画で確認）。
            // Type02(4.67s)は突き出しが2.05s・戻り終わりが3.4sで、リリース間隔2.8sに収まらないため不採用。
            _umpireStrikeClip = LoadClip(UmpireStrikeClipName);
            // 見逃し三振専用コール（issue #533）。無い環境ではnull＝ScheduleReactionsがStrike_Type01へ
            // フォールバックする（他の一発物と同じ流儀）。
            _umpireStrikeOutClip = LoadClip(UmpireStrikeOutClipName);

            _bat = SpawnBat(layer);

            // ボール（issue #332）。硬式球は直径7.4cm。ポスタライズ後はシルエットだけなので球で足りる。
            // 白は壁（暗い緑）と芝の両方に対して十分明るく、ポスタライズ6段でも最上段に乗って潰れない
            _ball = Primitive(PrimitiveType.Sphere, "prop-ball", Vector3.zero,
                Vector3.one * 0.074f, new Color(0.95f, 0.95f, 0.92f), layer);
            _ball.SetActive(false);

            // 打球の影（issue #401①・監督決定2026-08-03）。地面に張り付く楕円プリミティブ（ブロブ影・実寸）。
            // 芝の上の影が高さの唯一の手がかり＝球を潰した薄い球で作る（塁・打席と同じ「地面から数mm浮かせる」
            // 作法で z-fighting を避ける）。守備ビューでのみ球のXZに追従させる（SetFieldTime）
            _ballShadow = Primitive(PrimitiveType.Sphere, "prop-ball-shadow", Vector3.zero,
                new Vector3(BallRealDiameterM, BallShadowThicknessM, BallRealDiameterM),
                BallShadowColor, layer);
            _ballShadow.SetActive(false);

            Target = CreateRt(BaseWidth, BaseHeight);

            var camGo = new GameObject("StageCamera");
            camGo.transform.SetParent(_root.transform, false);
            _camera = camGo.AddComponent<Camera>();
            _camera.cullingMask = 1 << layer;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.nearClipPlane = 0.1f;
            _camera.farClipPlane = 200f;
            _camera.targetTexture = Target;

            // ポスタライズ常設（監督選定 2026-07-28: 6段・輪郭なし）。カメラは中間RTへ描き、
            // カメラ描画完了フックで Pass0 を当てて Target へ落とす。シェーダ欠落時は従来どおり素通し
            var postShader = Shader.Find("KokoSim/DuelStagePixelPost");
            if (postShader != null)
            {
                _postMat = new Material(postShader) { hideFlags = HideFlags.HideAndDontSave };
                _colorRt = CreateRt(BaseWidth, BaseHeight);
                _camera.targetTexture = _colorRt;
                UnityEngine.Rendering.RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
            }

            // 初期ポーズを1回打っておく（トグル直後の素立ちTポーズを見せない）
            foreach (var a in _actors) StepActor(a, 0.001f);
            GroundFix(_pitcher);
            FixBatterPose();
            AlignBat();

            // 装備（design-18 §3.3: 打者=ヘルメット/守備=帽子）。初期ポーズ評価後に
            // 視線ターゲット（投手→本塁、他3者→マウンド）から向きを実測して焼き付ける（AttachGear仕様）。
            // 投手だけはセット姿勢の顔が投球方向と約68°ずれる（PitcherYawの経緯と同じ収録特性）ため、
            // 顔が確実に本塁を向くリリース時刻の姿勢で焼き、初期姿勢へ巻き戻す
            var mound = new Vector3(0f, MoundHeight, MoundDistance);
            SeekActor(_pitcher, ReleaseTime);
            // 購入素体は帽子を素で持っている（頭ボーン直付けの剛体メッシュ）ので生成帽子は載せない＝二重帽子を避ける
            if (!useVendor)
                AttachGear(_pitcher, GearKind.Cap, Vector3.zero, _defenseColors.Cap, layer);
            SeekActor(_pitcher, 0.001f);
            GroundFix(_pitcher);
            // 生成直後はセット姿勢で止めておく。止めないと Tick が勝手にクリップを頭から流し、
            // 打席が始まる前に**振りかぶりつきの1球**を投げてしまう（＝カウントに乗らない幽霊投球。
            // 差し替え前は全球が振りかぶりだったので紛れていたが、セット投球にしたら露見した 2026-07-30）
            _pitcher.Time = 0f;
            _pitcher.Holding = true;
            // ヘルメットはチーム色でなく暗紺: 実際の高校野球のヘルメットは黒/紺が大半で、
            // チーム色（えんじ等）だと肌の顔と並んで「髪」に見える（2026-07-28 スクショで実害）
            // ヘルメットの紺は壁(0.12,0.20,0.16)と同輝度だと頭が背景に溶けて「頭が無い」絵になる
            // （2026-07-28監督指摘）。ポスタライズ6段で2段ぶん明るい紺にして輪郭を立てる
            AttachGear(_batter, GearKind.Helmet, mound, new Color(0.28f, 0.31f, 0.45f), layer);
            AttachGear(_catcher, GearKind.CatcherGear, mound, _defenseColors.Cap, layer);
            // accent=シャツ色: 球審の胸プロテクター（薄型3段＋肩当て）は「服の下に着る」前提の
            // シルエットなので、シャツから突き出た部分がシャツの膨らみに読めるようシャツ色へ追随させる。
            // 旧固定色 (0.13,0.13,0.15) は紺シャツ時代の値＝薄いブルーのシャツだと黒い塊が腹に浮いた
            // （issue #445 の候補出しで実測）。マスクと背面ストラップは AttachGear 内の dark 固定のまま
            AttachGear(_umpire, GearKind.UmpireGear, mound, UniformPalette.Umpire.Shirt, layer);

            // 攻守交代で塗り替える装備を拾っておく（投手の帽子＝U2経路のみ／捕手のヘルメット殻）。
            // 購入素体の帽子は素体メッシュの領域なので、ここではなく RegionMat 側で替わる
            CollectTeamMats(_pitcher, _defenseColors.Cap);
            CollectTeamMats(_catcher, _defenseColors.Cap);

            // グラブ（被り物と同じく初期ポーズ評価後＝視線実測が要るため）。利き手でない側に着ける。
            // 投手は 2026-07-29 まで**グラブを持っていなかった**（AttachMitt が捕手専用だった）
            // 捕手ミットは野手グラブと別メッシュ（指の区切りが無く縁が厚い＝流用すると捕手に見えない）
            AttachGlove(_catcher, HumanBodyBones.LeftHand, HumanBodyBones.LeftMiddleProximal,
                mound, MittSize, layer, DuelGearMeshes.Mitt());
            AttachGlove(_pitcher, HumanBodyBones.LeftHand, HumanBodyBones.LeftMiddleProximal,
                Vector3.zero, GloveSize, layer, DuelGearMeshes.Glove());

            // 絵作り一式（構図R-comp60・夏空・ライト・被り物オフセット）。数値の実体は
            // DuelCalibration.Default に集約し、キャリブレーションツール（DuelCalibrationWindow）が
            // 上書き中ならその値で作る。全マテリアル・装備が出揃った後に呼ぶ＝漏れなく反映する
            ApplyCalibration(cal0);

            // 左右ミラーの枠（issue #337）。装備の視線ベイクが済んだ後に挟む。恒等スケールの間は
            // ワールド行列が変わらない＝右打者・右投手の絵は従来と一致（回帰の根拠）
            _batterMirror = WrapInMirrorNode(_batter, "mirror-batter");
            _pitcherMirror = WrapInMirrorNode(_pitcher, "mirror-pitcher");

            Available = true;
            Live = this;
        }

        /// <summary>生きている対決ステージ（Play中に対決ビューを開いている間だけ非null）。キャリブレーションツールの入口。</summary>
        public static DuelStageRig Live { get; private set; }

        /// <summary>
        /// キャリブレーションツールの上書き値（null=既定）。設定中はrigを作り直しても
        /// （打席送り・RebuildDuelRigForCapture）調整中の見た目が維持される。
        /// </summary>
        public static DuelCalibration? CalibrationOverride;

        /// <summary>
        /// 絵作りパラメータを反映する（生成時と、キャリブレーションツールのスライダ操作から毎回）。
        /// カメラ位置は「注視点(0, AimY, AimZ)から俯角・横角の方向へ後退」の逆算で、
        /// 既定値なら従来のハードコード定数（-dz*0.0668 / +dz*0.0782）と同じ位置になる。
        /// </summary>
        public void ApplyCalibration(DuelCalibration cal)
        {
            if (_camera == null) return;
            cal = DuelCalibration.Upgrade(cal);
            _cal = cal;
            _fovBaseDeg = cal.FovDeg;
            _camera.backgroundColor = cal.Sky;
            float camZ = MoundDistance + cal.Dist;
            float dz = camZ - cal.AimZ;
            _camera.transform.localPosition = new Vector3(
                -dz * Mathf.Tan(cal.YawDeg * Mathf.Deg2Rad),
                cal.AimY + dz * Mathf.Tan(cal.PitchDeg * Mathf.Deg2Rad),
                camZ);
            _camera.transform.LookAt(_root.transform.position + new Vector3(0f, cal.AimY, cal.AimZ));
            ApplyFov();
            foreach (var m in _materials)
            {
                if (m == null) continue;
                m.SetFloat("_Ambient", cal.Ambient);
                m.SetVector("_LightDir", cal.LightDir);
            }
            // 立ち位置（x/zのみ・yは接地系）。装備の視線ベイクはスポーン時の位置のままなので、
            // ライブ移動中はつば等の向きがわずかに古い＝焼き戻し後の rig 再生成で正しくベイクされる
            // 打者は生の較正値のまま（左打者は鏡映親ノードが x を映す＝#337）。
            // 捕手・球審は反転しない代わりに並びだけ打者と反対側へ寄せる（BatterSideSign）
            SetActorXZ(_batter, cal.BatterX, cal.BatterZ);
            ApplyBatterIdleClip(cal);   // 立ち向きは待機クリップで変わるので、差し替えを先に済ませる
            SyncBatterYaw();
            SetActorXZ(_catcher, cal.CatcherX * BatterSideSign, cal.CatcherZ);
            SetActorXZ(_umpire, cal.UmpireX * BatterSideSign, cal.UmpireZ);
            if (_capMount != null) _capMount.localPosition = cal.CapOffset;
            if (_helmetMount != null)
            {
                // 被り角は**殻の中心**を軸に回す。枠の原点（頭ボーン＝首の付け根）で回すと
                // 角度を1度いじるたびにヘルメットが弧を描いて位置まで動き、
                // 「位置は決まったので向きだけ直す」ができない。中心を保つぶんを位置で打ち消す
                var helmetRot = Quaternion.Euler(cal.HelmetEuler);
                _helmetMount.localRotation = helmetRot;
                _helmetMount.localPosition =
                    cal.HelmetOffset + HelmetPivot - helmetRot * HelmetPivot;
            }
            if (_cHelmetMount != null) _cHelmetMount.localPosition = cal.CatcherHelmetOffset;
            if (_cMaskMount != null) _cMaskMount.localPosition = cal.CatcherMaskOffset;
            if (_cChestMount != null) _cChestMount.localPosition = cal.CatcherChestOffset;
            // 手の中の球はスライダを動かした瞬間に動かす（貼り付け中だけ＝飛翔中は触らない）
            if (_ballHeld) HoldBallInHand();
        }

        /// <summary>
        /// 立ち向き[度]を差し替える。現在の回転とBaseRotの両方を置き換える＝
        /// 巻き戻し・ループの基準ごと向きを変える（位置と同じ考え方）。
        /// ルートモーションで回った分は次のループ先頭で基準へ戻る。
        /// </summary>
        private static void SetActorYaw(Actor a, float yawDeg)
        {
            if (a == null || a.Go == null) return;
            var rot = Quaternion.Euler(0f, yawDeg, 0f);
            var delta = rot * Quaternion.Inverse(a.BaseRot);
            a.Go.transform.localRotation = delta * a.Go.transform.localRotation;
            a.BaseRot = rot;
        }

        /// <summary>いま待機として自作クリップ（構えだけ差し替え）が流れているか。</summary>
        private bool BatterOnOwnIdle =>
            _batter != null && _batterOwnIdle != null && _batter.Clip == _batterOwnIdle
            && _batterOwnIdle != _swingMissClip;

        /// <summary>
        /// 待機（構え）に使うクリップを較正から選び直す。既定は打撃クリップの構え区間。
        /// <see cref="DuelCalibration.IdleFromOwnClip"/> が立っているときだけ自作Rokokoの構え区間にする。
        /// 待機中でなければ差し替えは <see cref="FinishOneShot"/> の復帰時に効く（振っている最中に足元が飛ばない）。
        /// </summary>
        private void ApplyBatterIdleClip(in DuelCalibration cal)
        {
            if (_batter == null || _swingMissClip == null) return;
            bool own = cal.IdleFromOwnClip >= 0.5f && _batterOwnIdle != null
                && _batterOwnIdle != _swingMissClip;
            var want = own ? _batterOwnIdle : _swingMissClip;
            float endAt = own ? BatterOwnIdleEnd : BatterIdleEnd;
            if (_batter.IdleClip == want && Mathf.Approximately(_batter.IdleEndAt, endAt)) return;
            bool wasIdling = _batter.Clip == _batter.IdleClip;
            _batter.IdleClip = want;
            _batter.IdleEndAt = endAt;
            if (!wasIdling) return;
            SetActorClip(_batter, want, 0f, endAt);
            GroundFix(_batter);   // クリップごとに根の高さが違う＝差し替えると足が浮く／沈む
        }

        /// <summary>いまバント構えの待機（<see cref="_buntLoopClip"/>）を出しているか。</summary>
        private bool _batterBunting;

        /// <summary>
        /// 打者をバント構えへ切り替える/戻す（issue #490）。この球にバント/セーフティバント/スクイズの
        /// 指示が出ているかを、リリース直前に毎球渡す（<see cref="ApplyBatterIdleClip"/> と同じ「待機中で
        /// なければ FinishOneShot の復帰時に効く」流儀＝振っている最中に構えが割り込んで壊れない）。
        /// バント構えクリップが無い環境（購入パック未取得）では常に無視して通常の打撃構えのまま。
        /// </summary>
        /// <summary>
        /// 次に投げる球にバント/セーフティバント/スクイズの指示が出ているかを、**投球の描画に入る前**に渡す
        /// （issue #490 実機確認 2026-08-06）。構えへの移行 <see cref="BatterIdleToBuntClipName"/> は 1.10秒
        /// かかる一方、リリースから着弾までは 0.45秒しかない＝<see cref="OnPitchReleased"/> で切り替えると
        /// **構えに入り切る前に球が来る**（実測: バント指示の球で打者が通常の構えのままフルスイングしていた）。
        /// </summary>
        /// <summary>
        /// スクショ用: 打者がいま出しているクリップ（名前・クリップ内時刻・待機クリップ・フェード残り・
        /// バント構えの状態）。「構えが変わらない」ときに、切り替えが届いていないのか届いた上で
        /// 別のものに上書きされているのかは**絵では区別できない**ので実測で採る（design-18 §3.3 と同型）。
        /// </summary>
        public string BatterClipDiagForCapture()
            => _batter == null ? "-\t-\t-\t-\t-"
               : (_batter.Clip == null ? "null" : _batter.Clip.name) + "\t"
                 + _batter.Time.ToString("F2") + "\t"
                 + (_batter.IdleClip == null ? "null" : _batter.IdleClip.name) + "\t"
                 + _batter.FadeRemaining.ToString("F2") + "\t" + _batterBunting;

        /// <param name="leadSeconds">
        /// この呼び出しから球が来るまでの猶予[秒]。移行クリップ（1.10秒）を通せるだけの猶予がある
        /// ときだけ「構えに入る芝居」を見せ、足りなければ**構えそのものへ直接入る**。
        /// 実物のバントも投手のリリース前には squaring を終えているので、猶予が無い球で
        /// 中途半端な移行を見せるより直接構えたほうが正しい絵になる。
        /// </param>
        public void PrepareBatterBunt(bool bunting, float leadSeconds = 0f)
        {
            if (!Available) return;
            SetBatterBunting(bunting, leadSeconds);
        }

        private void SetBatterBunting(bool bunting, float leadSeconds = 0f)
        {
            if (_batter == null) return;
            if (bunting && _buntLoopClip == null) bunting = false;
            if (bunting == _batterBunting) return;
            _batterBunting = bunting;
            if (!bunting)
            {
                ApplyBatterIdleClip(_cal);   // 通常の構え（較正の own/購入クリップ選択）へ戻す
                return;
            }
            bool wasIdling = _batter.Clip == _batter.IdleClip;
            _batter.IdleClip = _buntLoopClip;
            _batter.IdleEndAt = 0f;   // ループそのまま（末尾まで）
            if (!wasIdling) return;   // 一発物（前球のスイング据え置き等）の最中は復帰時に切り替わる
            // 移行クリップを通せるのは猶予がある球（打席の初球＝振りかぶり 1.30秒）だけ。
            // 2球目以降は投球の予約が届くのがリリース直前なので、途中まで進んだ squaring で球を
            // 迎えてしまう（実測 2026-08-06: リリース時点で移行クリップが毎回 t=0.00 で、
            // 着弾までの 0.45秒では 4割しか進まない＝通常の構えにしか見えない）。
            bool useTransition = _buntIdleToStanceClip != null
                                 && leadSeconds >= _buntIdleToStanceClip.length;
            CrossfadeActorToClip(_batter, useTransition ? _buntIdleToStanceClip : _buntLoopClip,
                0f, 0f, CrossfadeSeconds);
            GroundFix(_batter);
        }

        /// <summary>
        /// 打者クリップの「立ち向きの家系」を判定し、対応する較正yawを返す（issue #514）。
        /// 家系は4つ: 自作Rokoko構え(own idle・<see cref="_cal"/>.IdleYawDeg) / バント構え・結果
        /// （Bunt_Loop・Bunt_Hitting・Bunt_Miss＝<see cref="_cal"/>.BuntYawDeg。squaring＝投手正対なので
        /// 他の家系と別枠） / バントへの移行（Idle_To_Bunt＝<see cref="_cal"/>.BuntTransitionYawDeg。
        /// クリップ自身が回り込む芝居を持つため、ハードカットで直結するBunt_Loopに合わせた専用値が要る。
        /// 理由は<see cref="DuelCalibration.BuntTransitionYawDeg"/>のコメント参照） / それ以外＝
        /// 購入AA打撃系の既定（<see cref="_cal"/>.BatterYawDeg）。
        /// </summary>
        private float YawForBatterClip(AnimationClip clip)
        {
            // Mixamo素材（gitignore）が無い環境では _swingMissClip が _batterOwnIdle と同一クリップへ
            // フォールバックする（コンストラクタ参照）。その場合は BatterOnOwnIdle と同じく「own」扱い
            // しない＝常に既定(BatterYawDeg)側で安定させる（さもないと環境依存で向きが変わる）
            if (clip != null && clip == _batterOwnIdle && _batterOwnIdle != _swingMissClip)
                return _cal.IdleYawDeg;
            if (clip != null && clip == _buntIdleToStanceClip) return _cal.BuntTransitionYawDeg;
            if (clip != null && (clip == _buntLoopClip || clip == _buntHitClip || clip == _buntMissClip))
                return _cal.BuntYawDeg;
            return _cal.BatterYawDeg;
        }

        /// <summary>
        /// 打者の立ち向きを「いま流れているクリップの家系」に合わせる（<see cref="YawForBatterClip"/>）。
        /// 素の向きは家系ごとに異なる（RunFacingProbe 実測 chest: Mixamo 52° / 自作 82° / バント一式は
        /// 別途実測）ので、構えを差し替えると向きも替える必要がある。
        /// クロスフェード中は退場側の家系から現在の家系へその重みで補間する＝体だけ1コマで数十度回る絵にしない。
        /// </summary>
        private void SyncBatterYaw()
        {
            if (_batter == null) return;
            float targetYaw = YawForBatterClip(_batter.Clip);
            if (_batter.FadeFrom.IsValid() && _batter.FadeDuration > 0f)
            {
                float fromYaw = YawForBatterClip(_batter.FadeFromClip);
                float u = Mathf.Clamp01(1f - _batter.FadeRemaining / _batter.FadeDuration);
                float w = u * u * (3f - 2f * u);   // StepActor の重み送りと同じ smoothstep
                targetYaw = Mathf.Lerp(fromYaw, targetYaw, w);
            }
            SetActorYaw(_batter, targetYaw);
        }

        /// <summary>
        /// 打撃フォームの実行時補正を当てる（design-18 §7-2・実装は <see cref="DuelBatterPose"/>）。
        /// 呼ぶ順は「クリップ評価 → 接地 → ここ → バット整列」。バットは詰めた後の手から軸を採る。
        /// </summary>
        private void FixBatterPose()
        {
            if (_batter == null || _batter.Animator == null) return;
            // 向きは補正より先（骨のワールド位置が動く＝視線の狙いが変わる）。
            // 構えを差し替えていないときは触らない＝従来の絵と1ビットも変えない
            if (_cal.IdleFromOwnClip >= 0.5f) SyncBatterYaw();
            DuelBatterPose.HeadForwardLocal = _batter.HeadFwdLocal;
            // 視線の狙点は投手の頭（居ない場合はマウンドの高さ1.6m）。実物の打者は投手を追うので、
            // 固定方向でなく相手の位置から引く
            Vector3 aim;
            var pitcherHead = _pitcher != null && _pitcher.Animator != null
                ? _pitcher.Animator.GetBoneTransform(HumanBodyBones.Head) : null;
            if (pitcherHead != null) aim = pitcherHead.position;
            else aim = _root.transform.position + new Vector3(0f, 1.6f, MoundDistance);
            // 補正の窓（膝・胴・握り）はMixamoの時刻表。自作構えのときは別クリップの時刻が来るので
            // 構えを代表する固定秒で評価する（そのまま渡すと窓の境目が待機ループの途中に出る）
            float poseT = BatterOnOwnIdle ? OwnIdlePoseRefSec : _batter.Time;
            DuelBatterPose.Apply(_batter.Animator, _cal, poseT, aim);
        }

        /// <summary>
        /// 立ち位置のx/zだけ差し替える。現在位置とBasePosの両方へ同じ差分を足す＝
        /// 巻き戻し（RewindActor）・待機ドリフト補正（StepActor）の基準ごと動かす。
        /// </summary>
        private static void SetActorXZ(Actor a, float x, float z)
        {
            if (a == null) return;
            var delta = new Vector3(x - a.BasePos.x, 0f, z - a.BasePos.z);
            if (delta.sqrMagnitude < 1e-10f) return;
            a.BasePos += delta;
            a.Go.transform.localPosition += delta;
        }

        // ── §7-2 アンカー同期（第1弾＝投手のみ） ──────────────────────────
        // リリースのアンカー時刻は右手速度ピーク実測。AAクリップ（6.00s）は
        // RunClipSpeedProbe(0.05s刻み) の 3.05s＝15.3m/s がピーク（2026-07-29実測）。
        // 自作Rokokoクリップ（4.27s）も同じ3.05sだったため、差し替えでアンカーは動いていない。
        // 本実装ではAnimationEventの注釈付けに置き換える（design-18 §7-2）。
        // MatchLiveController が対決ビュー中の1球周期（duelPitchIntervalSeconds）の基準にも使う。
        public const float ReleaseTime = 3.05f;

        /// <summary>
        /// セットポジション投球の切り口＝実際に再生を始めるクリップ内時刻[秒]。
        ///
        /// AAクリップは 0秒がセット姿勢（体90°横向き・両手へそ前1.16m）で、そこから 1.35秒かけて
        /// 両手を頭上（1.86m）へ振り上げてから足を上げる。この振りかぶり区間は**足が1mmも動かない**ので、
        /// 走者の有無に関わらずセットから投げる方針（時間短縮）のため丸ごと飛ばす。
        ///
        /// 1.35秒でなく1.75秒なのは、1.35秒だと手がまだ頭上（1.79m）にあり、繋ぎのブレンドで
        /// 一度跳ね上がって見えるため（試合ビュー480×270換算で手の上がりが 38px → 11px に減る。
        /// 2026-07-29 骨座標＋実カメラ投影で比較）。1.75秒は足上げの途中で、
        /// セット姿勢からのポーズ差が最大関節50px＝12fps1コマ（中央値12px）を超えるので
        /// <see cref="CrossfadeActorTo"/> で繋ぐ。
        /// </summary>
        public const float DeliveryStartTime = 1.75f;

        /// <summary>動き出しからリリースまでの実尺[秒]。投球時計の巻き戻し量＝この値。</summary>
        public const float WindupSeconds = ReleaseTime - DeliveryStartTime;   // 1.30s

        /// <summary>
        /// 対決ビュー中の既定リリース間隔[秒]。撮影ハーネスの逆算もこれを使う。
        ///
        /// 3.2（2026-08-01 監督指摘「むしろもう少し間を開けていいかも」）。1球ずつ采配窓で止まる進行
        /// （設計書15 §8）へ移して球間が「見るだけの間」から「決める間」に変わったため、2026-07-31 に
        /// 2.4/2.8/3.2 を撮り比べて選んだ 2.8 から**同じ比較の中の1段上**へ移した。
        /// 3本とも球審のコールは次の投球より前に終わる（コール〜構え戻りは間隔に依らず約2.05秒で一定）ので、
        /// 差は戻ってから次の投球までの間だけ: 2.4s→0.38s / 2.8s→0.74s / 3.2s→1.17s。
        /// 変えるときは <see cref="UmpireCallSettleSeconds"/>（2.55秒）＋コール開始の待ち
        /// （捕球+反応＝飛翔0.55s+0.10s≒0.65秒。2026-08-05にコール開始を捕球後へ移した）が
        /// 収まるかを確認すること。収まらない間隔では戻りが途中で切れる。
        ///
        /// 3.6（issue #493）。采配カードの受付締切を「次球の投手モーション開始」へ前倒ししたところ、
        /// 3.2sだと締切（リリース+1.90s）までの受付が短すぎたため、締切をリリース+2.30sまで伸ばす側で
        /// 延ばした（<see cref="OnPitchReleased(float,DuelPitchOutcome)"/> の preRoll は WindupSeconds＝1.30s
        /// で頭打ちなので、間隔を伸ばすと締切だけが後ろへ延びる＝フォロースルー〜次の振りかぶりの見た目は
        /// 変わらない）。3.2sで丁度だった球審コールの戻り終わりは 0.40s の余裕に変わる＝収まる方向のみの変更。
        ///
        /// ⚠ シーンの MatchLive は <c>duelPitchIntervalSeconds</c> を直列化して持つ＝**ここだけ変えても
        /// 実機は変わらない**（SampleScene.unity 側も一緒に変える）。
        /// </summary>
        public const float DefaultPitchInterval = 3.6f;

        /// <summary>切り口のクロスフェード長[秒]。12fps量子化で3コマぶん。</summary>
        private const float CrossfadeSeconds = 0.25f;

        private const float FollowSeconds = 1.5f;   // リリース後に見せるフォロースルーの長さ
        private float _cutCountdown = -1f;   // 次のカットバックまでの残り（プレー時間秒）。負=予約なし
        private float _cutClipTime;

        /// <summary>
        /// 次のリリースへ向けた振りかぶりへ既に切り替わったか。<see cref="OnPitchReleased"/> が
        /// カットバックを予約した直後は false、実際に <see cref="Tick"/> で切り替わった瞬間に true になる。
        /// 次の <see cref="OnPitchReleased"/>（新しい予約）または <see cref="ResetForNewPlateAppearance"/> で
        /// false へ戻る。采配カードの受付締切判定に使う（issue #493）。
        /// </summary>
        public bool HasCutToNextWindup { get; private set; }
        private float _umpireCallCountdown = -1f;   // 球審ストライクコール開始まで（負=予約なし）
        private float _umpireCallStartAt;           // コールクリップの頭出し位置（アンカー逆算）
        private float _umpireCallEndAt;             // コールクリップの打ち切り時刻（0=末尾まで）
        private float _catchCountdown = -1f;        // 捕球クリップ開始まで（負=予約なし）
        private int _catchIndex = -1;               // 今の球で使う捕球クリップ（CatchAnchors の添字。負=捕らない）
        private bool _catchIsStrike;                // 今の球の判定がストライク系（見逃し/空振り）か。CatcherLingerSeconds(bool) の分岐に使う（issue #527）
        private float _passedBallCountdown = -1f;   // 後逸クリップ開始まで（負=予約なし。issue #525）
        private int _passedBallIndex = -1;          // 今の球で使う後逸クリップ（PassedBallAnchors の添字。負=無し）
        private float _swingCountdown = -1f;        // スイング開始まで（負=予約なし。SetSwing 参照）
        private AnimationClip _swingPendingClip;    // 予約中のスイングクリップ
        private float _swingPendingAnchor;          // 予約中の接触アンカー[クリップ秒]
        private float _swingPendingEndAt;           // 予約中の打ち切り[クリップ秒]
        private float _swingPendingHold;            // 予約中の据え置き[秒]（SwingFinishHoldSeconds）

        /// <summary>
        /// 打席頭の投球入口。セット姿勢（クリップ0秒で停止中）から、振りかぶりを飛ばして
        /// <see cref="DeliveryStartTime"/> へクロスフェードで入り、そこから等速で再生する。
        /// 呼び出し側が投球時計を −WindupSeconds から始めれば、時計0＝リリースにそのまま一致する。
        /// 仕切り直しは <see cref="ResetForNewPlateAppearance"/> に一本化（投手はセット姿勢で静止）し、
        /// 振りかぶりの切り口へ入る＋静止解除をここで足す。
        /// </summary>
        public void OnPitchWindup()
        {
            if (!Available || _pitcher == null) return;
            ResetForNewPlateAppearance();
            // 采配窓の間はセット姿勢（クリップ0秒）で静止している（ResetForNewPlateAppearance）ので、
            // ここで振りかぶりの切り口へ入って等速再生を始める＝時計 −WindupSeconds と一致する。
            CrossfadeActorTo(_pitcher, DeliveryStartTime, CrossfadeSeconds);
            GroundFix(_pitcher);
            _pitcher.Speed = 1f;   // 采配窓の静止を解いて等速再生を始める
        }

        /// <summary>
        /// 打席の頭で打者・捕手・球審・投手を待機へ戻し、球を投手の手へ戻す（前打席の一発物が残っていても仕切り直す）。
        ///
        /// **対決ビューを開ける側は、開けると同時にこれを呼ぶこと。**一発物クリップが進むのは
        /// <see cref="Tick"/> の中だけで、その Tick は対決ビューが映っている間しか回らない＝
        /// 打球再生で俯瞰へ逃がしている間、打者は**振っている途中のコマで凍る**。次の打席の頭で
        /// 対決ビューへ戻した瞬間にその続きが再生され、**新しい打者が球も無いのに振る**
        /// （2026-08-01 監督指摘「打席の最初に謎の幽霊空振りが入る」）。
        ///
        /// 投手も同型。3D守備ビューへ逃がしている間はフォロースルーの途中のコマで凍り、対決ビューへ
        /// 復帰した瞬間に Tick が再開してその続き（投げ終わりの残像）が再生される
        /// （2026-08-05 監督報告。issue #468）。ここでセット姿勢（クリップ0秒）へクロスフェードで
        /// 寄せて仕切り直す（振りかぶりの切り口へは <see cref="OnPitchWindup"/> が投球開始時に入る）。
        ///
        /// 1球進行（設計書15 §8）へ移すまでは、対決ビューを開けるコマと <see cref="OnPitchWindup"/> が
        /// 同じフレームだったので凍った残りは1コマも映らなかった＝**移行で初めて絵に出た**。
        /// 采配窓の間（`paHeadDwellSeconds`）が挟まる今は、開けた側が明示的に仕切り直す必要がある。
        /// </summary>
        public void ResetForNewPlateAppearance()
        {
            if (!Available) return;
            if (_batter != null && _batter.IdleClip != null)
            {
                SetActorClip(_batter, _batter.IdleClip, 0f, _batter.IdleEndAt);
                // スイングの倍率を持ち越さない。**SetActorClip では戻らない**（守備・走塁が毎コマ
                // 入れ直す前提でそう作ってある）ので、仕切り直す側が明示的に戻す。戻さないと
                // 振っている途中で打席が終わった次の打者が、構えを SwingSpeed 倍で刻む
                // （実測 2026-08-01: 前の打席の 2.6倍が待機ループに残った）。
                // 振り切って待機へ戻る通常経路は FinishOneShot が戻すのでここは来ない
                _batter.Speed = 1f;
            }
            _batTrailSwing = false;   // 打席の仕切り直し＝バット軌跡の武装も持ち越さない（issue #444）
            // 前の打席最後の投球の狙い寄り・残差の体寄せ（issue #396 ①②）を引きずらない＝
            // 較正の固定位置へ戻してから待機クリップへ（順序が逆だと RewindActor が古い BasePos へ戻す）
            SetActorXZ(_catcher, CatcherBaseX, _cal.CatcherZ);
            _catcherResidual = Vector2.zero;
            if (_catcher != null && _catcher.IdleClip != null) SetActorClip(_catcher, _catcher.IdleClip, 0f, _catcher.IdleEndAt);
            _umpireCallCountdown = -1f;
            PendingResultHoldSeconds = 0f;   // 打席をまたいで前の球の据え置き延長を引き継がない（issue #533）
            _catchCountdown = -1f;
            _catchIndex = -1;
            _catchIsStrike = false;
            _passedBallCountdown = -1f;   // 後逸の予約も打席をまたがない（issue #525）
            _passedBallIndex = -1;
            _swingCountdown = -1f;   // 打席をまたいで「幽霊スイング」を引き継がない
            ClearBatTrail();         // 前の打席の振りの尾を持ち越さない
            if (_umpire != null && _umpire.IdleClip != null) SetActorClip(_umpire, _umpire.IdleClip, 0f, 0f);
            if (_pitcher != null)
            {
                // 投手は打者・捕手・球審と違って「待機クリップ」が無い（1本のクリップを
                // 振りかぶり〜フォロースルーまで頭出し無しで使い回す）ので、SetActorClip の
                // 頭出しでは跳ぶ・向きが飛ぶ（design-18 §4.5-③）。クロスフェードで寄せる。
                // カットバック予約（_cutCountdown）が残っていると寄せた
                // 直後に別クリップ内時刻へまた跳ぶので、寄せる前に破棄する。
                //
                // 行き先は**セット姿勢（クリップ0秒）**。旧実装は DeliveryStartTime（1.75s＝足上げの
                // 途中）で凍らせていたため、守備ビューから戻った采配窓の間じゅう「投げかけのポーズ」で
                // 静止して見えた（2026-08-05 監督指摘「投手がモーションに入ってしまっている。
                // すこしセットポジションで静止しといて」）。振りかぶりの切り口へは OnPitchWindup が
                // 投球開始の瞬間に改めて入る＝リリースの時計（−WindupSeconds 起点）は変わらない
                _cutCountdown = -1f;
                HasCutToNextWindup = false;
                CrossfadeActorTo(_pitcher, 0f, CrossfadeSeconds);
                GroundFix(_pitcher);
                // クロスフェードは実時間（Tick は対決ビューが映っている間ずっと回る）で進むので、
                // 進めたままだと采配窓の間（paHeadDwellSeconds）に投球動作が先走って見える。
                // OnPitchWindup が実際の投球開始でここを1へ戻すまで進めない＝止まって見えるのが
                // 意図どおり（完了条件「セット姿勢または静止した待機」）
                _pitcher.Speed = 0f;
            }
            HoldBallInHand();   // 振りかぶりから球を手に持たせる（issue #332）
        }

        // 結果分岐のアンカー（スイングはType02をRunClipSpeedProbeで実測 2026-07-27。値はクリップ内秒）:
        // 空振り=スイング面通過1.2s（右手速度ピーク6.7m/s） / ミート=当たり1.25s（ピーク1.3sの直前） /
        // 見逃し=振り返り2.0s / 死球=のけぞり1.3s / 捕球=ミット提示0.4s（冒頭0.15sで構えて保持する作り）。
        // 盤面の投球飛翔は0.45s（MatchLiveのSetTime基準）＝リリース時に「アンカー−0.45」へ頭出しすると
        // ボール到達とアンカーが一致する。
        //
        // issue #332 の判断（アンカーを実飛翔時間で置き換えるか据え置くか）: **置き換える**。
        // 弾道が載っている球は実測の飛翔時間（140km/h級で約0.42s・軟投で約0.52s）を使い、打者のスイング・
        // 捕手の捕球・球審のコールも同じ値で逆算する。ボールだけ実速度で飛ばしてアンカーを0.45固定にすると
        // 「速球はミットが閉じてから球が着く」ズレが球速差ぶん出る＝球の速さを絵で見せる目的と噛み合わない。
        // この定数は弾道が無い球（合成列・CaptureTimelines なしの試合）の既定値として残す。
        private const float BallFlightSeconds = 0.45f;

        /// <summary>
        /// 打つ球を**判定面より何m投手側で捌くか**（2026-07-31 監督判断）。
        ///
        /// 実物の接触は本塁の上でなく 0.3〜0.6m 前で起きる。判定面（z=0）で当てていたときは、握りが
        /// 接触時に z=-0.24＝本塁より捕手側にあるため、握り→打点のベクトルに投手方向成分がほとんど
        /// 残らず（横0.52 に対し前0.24＝25°）、絵は「上から本塁を叩いている」ようにしか読めなかった。
        ///
        /// **1つの定数から3つ（①投球の描画が終わる点 ②打球の開始点 ③バットの狙点）を出す**のが要点。
        /// 3つとも <see cref="_ballArrival"/> 経由で決まるので、ここを動かすだけで揃って動く
        /// （別々に持つと必ずずれる）。判定そのものは判定面のままなので**帯は動かない**。
        /// 見逃し・空振り・死球は従来どおり判定面〜ミットまで飛ぶ。
        ///
        /// **⚠ 量は真横の絵だけで決めない**。画面の横軸は ±x で、投手方向 ±z は試合の構図
        /// （センターからの望遠）では**奥行き＝潰れる軸**。前へ出すほど真横では長く、試合の構図では
        /// 短く見える。実測（バットの見かけの長さ／実長）:
        ///   0m → 真横53% / 構図93%　　0.25m → 71% / 75%　　0.40m → 78% / 65%
        /// </summary>
        private const float ContactAheadM = 0.30f;
        // 面通過・ミートのクリップ内時刻。旧1.30は**右手の速度ピーク**（2026-07-30実測）で採った値だが、
        // ①バットの芯は手より遅れて来る ②当時はポーズが speed 倍で進んでおらず遅れていた（下の
        // StepActor の項）、の2つで実際の通過点とずれていた。ポーズを直したうえで**握りが本塁へ
        // 最接近するクリップ時刻**を0.042刻みで掃いて採り直した（2026-07-31）:
        //   clip 1.08→0.560m / 1.12→0.478m / 1.16→0.449m(最小) / 1.20→0.672m / 1.28→0.815m
        // ……ただしこの「握りが本塁へ最接近する時刻」は、当時まだ残っていた**頭出しのワープ**
        // （SetSwing の項。振り始めに打者が0.17m三塁側へ飛んでいた）込みで測った値だった。
        // ワープを直して打者が正しい位置に立つと、1.12 では握りが打点より**0.19m投手側へ出ており**、
        // 芯は届かず（打点まで0.180m）バットは捕手を向く＝絵では「振り遅れて棒が寝ている」。
        // 握りの本塁からの距離を採り直すと（2026-07-31 第2弾。腕＝握り→芯は0.605m）:
        //   clip 0.84→0.668m / 0.96→**0.595m** / 1.07→0.428m(近すぎ) / 1.19→0.885m
        // 腕とほぼ一致する 0.96 が正しい接触＝ポーズ1コマぶん手前。頭出しの都合で 1.00 を採る。
        // 空振りは判定面（本塁の上）を通すので 1.00 のまま。**当てる球だけ**は判定面より
        // ContactAheadM=0.30m 投手側で捌くので、握りが腕の長さ（0.605m）になる時刻がその分だけ
        // 後ろへ動く（2026-07-31 実測。握り→打点 0.95→0.770 / 1.00→0.718 / **1.05→0.598** / 1.10→0.387）。
        // 副次的に、腕が実際に動く区間（1コマあたり0.25m。前は0.04〜0.11m）に接触が入る＝
        // 「手にバットがついてこない」も一緒に解ける
        //
        // **v19 で採り直した（2026-07-31 第5弾）。** 上の 1.00/1.05 は「握り→打点＝腕の長さ」だけで
        // 決めていて、**握りが打点の手前どこに居るか**を見ていなかった。実測すると 1.05 の握りは
        // z=-0.11＝打点(z=+0.30)より 0.41m **捕手側**で、球は前方斜めにある。そこで芯を球に乗せると
        // バットは前腕に対して**129°＝折り返った形**になる＝監督指摘「バットをこねている」。
        //
        // 正しい採り方は**握りが狙点と同じ深さ（z）に来る時刻**。そこでは球が握りの真横にあり、
        // バットは腕の延長として素直に出る。実測（BatterX=-0.96）:
        //   clip 1.05 → 握り z=-0.11・球向き(0.81,-0.11,0.57)・前腕⇔球 116°
        //   clip 1.17 → 握り z=+0.32・球向き(**1.00,-0.05,-0.04**)・前腕⇔球 **46°**
        // 打つ球は判定面より ContactAheadM=0.30m 前（z=+0.30）なので 1.17、
        // 空振りは判定面（z=0）なので、握りの速さ（3.7 m/クリップ秒）で 0.30m ぶん手前の 1.08。
        // 立ち位置 BatterX も同時に解く必要がある（この時刻の握り→打点が腕の長さ 0.605m になる値）＝
        // -0.71 では 0.382m しかなく芯が球を通り越す。design-18 §4.5-⑪
        private const float SwingMissPlane = 1.08f;
        private const float SwingHitContact = 1.17f;

        /// <summary>
        /// バント（issue #490）の面通過・当てのクリップ内時刻。<see cref="SwingMissPlane"/>/
        /// <see cref="SwingHitContact"/> と同じ役割だが、まだ <c>DuelMotionSpike.RunClipSpeedProbe</c> で
        /// 実測していない**初期値**（クリップの尺・緩急が不明なため中間あたりを仮置き）。
        /// design-18 §4.5-③の掟どおり、購入クリップを取り込み次第 実測して値を更新すること。
        /// </summary>
        private const float BuntMissPlane = 0.50f;
        private const float BuntHitContact = 0.55f;

        /// <summary>
        /// スイングを打ち切る時刻[クリップ秒]。**振り抜いた所で止める**（2026-07-31 監督判断
        /// 「バットを戻す動作も消して欲しい／スイングして振り抜いた時点で止める」）。
        ///
        /// 収録はこの後、胴を巻き戻してバットを構えへ戻す。止め所は**胴の回りが最大になるコマ**＝
        /// そこから先が「戻り」。実測（胸ヨー・ステージローカル）:
        ///   1.40→−40° / 1.52→−64° / 1.63→−72°（最大） / 1.75→−58° / 1.87→−30° / 2.10→+2°
        /// バットの巻き付き（③）が終わるのは 打つ 1.52 / 空振り 1.43 なので、両方この後に収まる。
        /// 旧値は <see cref="BatterRunStart"/>(2.60)＝走り出しの手前で、戻りが丸ごと映っていた。
        ///
        /// **1.433 へ変更（2026-08-02 監督判断「止め所が遅い」）。** 1.63 は手が低く前に残ったまま
        /// 「捻り切って固まった」形になっていた。Mixamo サイトのプレビュー指定コマ43/208（30fps）を
        /// クリップ秒へ換算した値。3案の実測比較（据え置き0.30秒後・右打者・インプレー）:
        ///   1.63→胸ヨー−54.0°・右手1.21m・接触→静止0.23秒／1.52→−46.5°・1.28m・0.18秒／
        ///   **1.433→−31.7°・1.33m・0.13秒（採用）**。手が頭の横まで上がりバットが顔の上へ抜ける
        ///   ＝「振り抜いた形」で止まる。issue #395
        /// </summary>
        private const float SwingFinishSec = 1.433f;
        private const float HbpReact = 1.3f;
        /// <summary>
        /// 振り切りポーズで据え置く秒数（#291。目安0.6〜1.0sの中間・投球間隔2.8sに余裕で収まる）。
        /// **空振りだけでなく全スイングに要る**（2026-07-31）。<see cref="SwingFinishSec"/> で打ち切ると、
        /// 据え置きが無い結果は打ち切った瞬間に待機へ戻る＝実測: インプレーは接触の 0.36秒後（t=0.90s）に
        /// **構えへ復帰**していた。対決ビューはインプレーを t=1.0s まで映すので、
        /// 「打った直後に構えている打者」が絵に出る。
        /// </summary>
        private const float SwingFinishHoldSeconds = 0.8f;

        /// <summary>
        /// 捕球クリップ1本ぶんのアンカー（実測 2026-07-30・0.01s刻み・ミット中心＝手首と中指付け根の中点）。
        ///
        /// <see cref="Mitt"/> は**捕手の立ち位置からの相対**（素体ローカル・yaw0・+z=投手側）。
        /// ステージ座標を直に持つと <c>CatcherX/CatcherZ</c> を動かした瞬間に表が腐るため。
        ///
        /// <see cref="Moment"/> は「球がミットに当たる瞬間」のクリップ内秒。定義がクリップで2種あるので
        /// 一律の式では出せず、実測から個別に決めている:
        ///  ・中央(C) はミットを構えた姿勢から始まり、当たった衝撃で引かれる＝**引き始め**（0.06s。
        ///    ここを球到達に合わせると引きが球のせいに見える。旧実装の0.4sは引き終わって静止した後）
        ///  ・上下左右(U/D/L/R) はミットを的へ運ぶ＝**運び終わり**（速度が落ちる点）
        /// </summary>
        private readonly struct CatchAnchor
        {
            public readonly string Clip;
            public readonly Vector3 Mitt;
            public readonly float Moment;
            public CatchAnchor(string clip, Vector3 mitt, float moment)
            {
                Clip = clip; Mitt = mitt; Moment = moment;
            }
        }

        // 先頭が中央（既定・同点時に勝つ側）。CatchSelectionBias はこれ以外へ課す下駄
        private static readonly CatchAnchor[] CatchAnchors =
        {
            new("AA_Baseball_Catcher_Catching_C", new Vector3(-0.081f, 0.633f, 0.400f), 0.06f),
            new("AA_Baseball_Catcher_Catching_U", new Vector3(-0.071f, 0.918f, 0.498f), 0.21f),
            new("AA_Baseball_Catcher_Catching_D", new Vector3(-0.036f, 0.320f, 0.185f), 0.20f),
            new("AA_Baseball_Catcher_Catching_L", new Vector3(-0.263f, 0.662f, 0.495f), 0.17f),
            new("AA_Baseball_Catcher_Catching_R", new Vector3(0.195f, 0.638f, 0.407f), 0.19f),
        };

        /// <summary>
        /// 後逸クリップ1本ぶんのアンカー（issue #525・実測 2026-08-08・0.02s刻み・ミット＝左手ボーン）。
        ///
        /// <see cref="CatchAnchor"/> と同じ形だが、<see cref="CatchAnchor.Mitt"/> の意味が違う——捕球は
        /// 「球をそこへ運ぶ狙点」だが、後逸は**球が入らない**ので、ここは「その手が届く所」＝
        /// クリップを選ぶための代表点でしかない（球の位置には一切使わない）。
        ///
        /// <see cref="CatchAnchor.Moment"/> は「捕り損ねる瞬間」のクリップ内秒。方向クリップと同じ
        /// **運び終わり**（＝その側への伸び切り。速度が落ちきる点）で採った。定義がクリップごとに違う罠
        /// （design-18 §4.5-③）そのもので、**変位が最大になる時刻を一律に使うと外れる**:
        ///   L    len=2.67 変位最大 0.62（v 2.75→1.57 で減衰しきる点＝伸び切り。**一致**）
        ///   L_S  len=2.17 変位最大 1.76 だが、これは後逸後の追いかけ。伸び切りは **0.24**（v 4.11→0.64）
        ///   R    len=2.67 変位最大 2.24 も追いかけ。伸び切りは **0.96**（v 2.03→0.44・x が右端 +0.221）
        ///   R_S  len=2.83 変位最大 1.72 も追いかけ。伸び切りは **0.48**（v 3.20→0.82）
        /// ＝4本中3本は「変位最大」を採ると追いかけの区間に当たり、球が抜けた後に手を出す絵になる。
        ///
        /// 4本の伸び切り位置（捕手ローカル・-x=捕手から見て左）が低左/高左/中右/高右に分かれるので、
        /// 捕球クリップと同じ「一番近いものを選ぶ」でコース別に使い分けられる（<see cref="SelectPassedBall"/>）。
        /// </summary>
        private static readonly CatchAnchor[] PassedBallAnchors =
        {
            new("AA_Baseball_Catcher_Passed_Ball_L", new Vector3(-0.421f, 0.188f, -0.199f), 0.62f),
            new("AA_Baseball_Catcher_Passed_Ball_L_S", new Vector3(-0.644f, 0.810f, 0.406f), 0.24f),
            new("AA_Baseball_Catcher_Passed_Ball_R", new Vector3(0.221f, 0.594f, 0.240f), 0.96f),
            new("AA_Baseball_Catcher_Passed_Ball_R_S", new Vector3(0.165f, 0.887f, 0.690f), 0.48f),
        };

        /// <summary>
        /// 中央以外を選ぶための下駄[m]。ゾーン中央付近ではCとU/Dの距離が数cm差で並ぶため、
        /// これが無いとコースのわずかな違いでミットが上下に飛ぶ（見た目が落ち着かない）。
        /// 「はっきり外れたコースだけ方向クリップへ移る」ようにする閾値で、実測の並びから 0.08 と置いた。
        /// </summary>
        private const float CatchSelectionBias = 0.08f;
        // 球審ストライクコールのアンカー同期（#333の積み残し／#353）。
        //
        // 他の役者と同じ方式＝「クリップ内の実測アンカーが、球の時計上の狙った瞬間に一致する」よう
        // 再生開始を逆算する。クリップ内部の緩急は改変しない（design-18 §4.5 / §7-2）。
        //
        // RunClipSpeedProbe("AA_Baseball_Umpire_Crouch_Strike_Type01","RightHand",0.05f) の実測
        // （2026-07-31・len=3.33s。右手速度[m/s]）:
        //   0.00→0.0 …… 0.60→2.7 0.70→1.0 0.80→**3.0(ピーク)** 0.85→1.6 0.90→0.2
        //   0.90〜1.65 ≈ 0.1（腕を出したまま静止＝コールを見せている区間）
        //   1.70〜2.50 で戻り（2.25→2.4がピーク）／2.55以降 ≈ 0.1（しゃがみへ収まって静止）
        // ＝このクリップは「突き出す(0〜0.85) → 見せる(0.85〜1.65) → 戻る(1.65〜2.55)」の3区間。
        // アンカーは突き出しが終わる 0.85s（＝腕が伸び切る瞬間＝コールの絵）。
        // 戻りまで含めた実尺は2.55sで、末尾0.8sは静止＝間隔2.8sでも切らずに収まる。
        //
        // ⚠ 差し替え時は必ず測り直す。コールの瞬間の定義はクリップごとに違う（design-18 §4.5-③）:
        // Type02(len=4.67s) は同じプローブで 2.00s に鋭いピーク（3.9m/s）＝突き出しが2.05s、
        // 戻り終わりが3.4s で、間隔2.8sには収まらない（採用するなら間合いの側を変える必要がある）。
        private const float UmpireCallAnchorSeconds = 0.85f;

        /// <summary>
        /// 球審ストライクコールの再生倍率（issue #527）。クリップ内部の緩急は変えず、一様な倍率で
        /// クリップ内時刻の進みだけを速める＝「内部緩急を改変しない」掟（design-18 §4.5/§7-2）には
        /// 触れない（禁じているのはキーの打ち直し。打者スイングの<see cref="Actor.Speed"/>と同じ作法）。
        /// 捕球からテロップまでを0.5秒前後に収める狙いで <c>0.85 / (0.50 − <see cref="UmpireCallReactSeconds"/>)
        /// ≈ 2.1</c> と置いた。速度を入れたら実秒の窓（<see cref="UmpireCallAnchorSeconds"/>・
        /// <see cref="UmpireCallSettleSeconds"/>）はどちらもクリップ秒なので、実秒へ戻すときはこの値で割る
        /// （<see cref="CallMomentSeconds"/>・<see cref="ScheduleReactions"/> 参照）。
        /// </summary>
        private const float UmpireCallPlaybackRate = 2.1f;

        /// <summary>
        /// 戻り終わり＝しゃがみ姿勢へ収まるクリップ内時刻[秒]（実測 2.55s。以降は末尾まで静止）。
        /// 「コールが途中で切れて構えへ戻らない」を避ける打ち切り可否の基準に使う。<see cref="UmpireCallPlaybackRate"/>
        /// 倍で再生するため、実秒での戻り終わりは <c>UmpireCallSettleSeconds / UmpireCallPlaybackRate</c>。
        /// </summary>
        private const float UmpireCallSettleSeconds = 2.55f;

        /// <summary>
        /// 捕球を見てからコールの**動作を始める**までの反応[秒]。
        ///
        /// 旧実装はコールの瞬間（アンカー0.85s）を「捕球+0.30s」へ合わせる逆算で、クリップ開始が
        /// 捕球より前＝実質リリース直後に来て、突き出しの助走が球の飛翔中に見えていた
        /// （2026-08-05 監督指摘「ちゃんとボールがミットにおさまってからジャッジして」）。
        /// 今はクリップ開始そのものを「捕球 + この値」に置く（頭出し無し）。クリップ内部の緩急は
        /// 改変しない掟（design-18 §7-2）ゆえ、コール（腕が伸び切る瞬間）は
        /// 捕球 + 反応 + <see cref="UmpireCallAnchorSeconds"/> に着地する＝判定テロップもそこに揃える。
        /// </summary>
        private const float UmpireCallReactSeconds = 0.10f;

        /// <summary>
        /// この球の判定を画面へ出してよい時刻[秒]（リリース起点）。<see cref="OnPitchReleased"/> の後だけ
        /// 意味を持つ。見逃し・空振り（球審がコールの絵を出す）は捕球する瞬間（ミットに収まる時刻＋
        /// <see cref="UmpireCallReactSeconds"/>＋<see cref="UmpireCallAnchorSeconds"/>÷<see cref="UmpireCallPlaybackRate"/>）、
        /// ボール（コールの絵が無い＝issue #527 C）は反応だけ（ミットに収まる時刻＋<see cref="UmpireCallReactSeconds"/>）、
        /// 打つ球・死球は当たった瞬間。
        ///
        /// UI（MatchLive）は長らく盤面（2D俯瞰）の投球尺 0.45 秒固定でテロップを出していた。対決ビューの球は
        /// **実速度**で飛ぶ（実測 飛翔 0.52〜0.59 秒・捕球面 0.54〜0.60 秒）ので、固定値のままだと
        /// **捕る前に「ストライク」が出る**（2026-08-01 実測: 捕られた17球すべて捕球前・平均 0.113 秒早い。
        /// 監督指摘「捕球するより前に出ている気がする」）。issue #332 で打者・捕手・球審は実飛翔時間へ
        /// 移したが、UI のテロップだけが取り残されていた＝同じ罠の最後の1箇所。
        ///
        /// 捕球からテロップまでが0.95秒と長く、しかもボールはコールの絵が無いのに同じだけ待たされていた
        /// （issue #527）。ボールは反応秒だけへ縮め、見逃し・空振りは <see cref="UmpireCallPlaybackRate"/>
        /// でコールの再生自体を速めて約0.5秒へ詰めた。
        /// </summary>
        public float CallMomentSeconds { get; private set; } = BallFlightSeconds;

        /// <summary>
        /// この球の結果保持（打球の無い打席終了＝三振・四球等）に最低限必要な据え置き秒数[s]
        /// （<see cref="CallMomentSeconds"/> 起点）。既定は0（呼び出し側の <c>noPlayHoldSeconds</c> の
        /// ままでよい＝従来どおり）。見逃し三振で専用クリップ（<see cref="UmpireStrikeOutClipName"/>。
        /// Type01比+45%長い＝4.83s）を選んだ球だけ、そのクリップが**自然終了まで途中で切れて構えへ
        /// 戻らない**（issue #533 実装メモ5節「打ち切りの罠」）ことを保証する値を持つ。
        ///
        /// 実測に頼らない安全側の計算＝「アンカー(<see cref="UmpireCallAnchorSeconds"/>)以降を
        /// 全部見せ切るのに要る実秒」= (クリップ長 − アンカー) / <see cref="UmpireCallPlaybackRate"/>。
        /// クリップ長はロード済み <see cref="AnimationClip.length"/>（実測不要・常に正確）から取るので
        /// この値自体は保証できるが、**アンカーの実測はしていない**（Unity Editor 未接続で
        /// RunClipSpeedProbe が回せなかった＝2026-08-08 夜間ラン）ため Type01 の値を暫定流用している。
        /// 呼び出し側は <c>Mathf.Max(noPlayHoldSeconds, PendingResultHoldSeconds)</c> で使う。
        /// </summary>
        public float PendingResultHoldSeconds { get; private set; }

        /// <summary>
        /// 盤面の「ボールが手を離れた瞬間」（BeginPitchBall）に呼ぶアンカー同期の入口。
        /// 投手クリップをリリース時刻へスナップし、次の1球があれば（nextReleaseIn=リリース間隔）
        /// フォロースルーを FollowSeconds 見せてから次の振りかぶり位置へ切り替える予約を入れる。
        /// 間隔が振りかぶり＋フォローに足りない場合だけプリロールを削って間に合わせる。
        /// あわせて1球の結果（outcome）に応じた打者・捕手の一発物をボール到達アンカーで頭出しする。
        /// クリップ内部の緩急は改変しない＝再生開始位置の逆算だけで合わせる（設計書18の掟）。
        /// </summary>
        public void OnPitchReleased(float nextReleaseIn, DuelPitchOutcome outcome)
            => OnPitchReleased(nextReleaseIn, outcome, null);

        /// <summary>
        /// コース・弾道つきのリリース（issue #332）。<paramref name="pitch"/> が null（＝合成列や
        /// 弾道を積分しない試合）のときはコースを主張せず、本塁中央へ見た目用の球を飛ばす。
        /// </summary>
        public void OnPitchReleased(float nextReleaseIn, DuelPitchOutcome outcome, DuelPitchFlight? pitch)
            => OnPitchReleased(nextReleaseIn, outcome, pitch, null);

        /// <summary>
        /// 打球つきのリリース（2026-07-31）。<paramref name="batted"/> は engine の打球セグメント
        /// （<c>PlaybackPlay.Ball</c> の投球以外の先頭＝この後すぐ俯瞰が描く物そのもの）。渡すと接触の
        /// 瞬間に球がそのセグメントへ乗り換え、俯瞰へ切り替わるまでの一瞬だけ行方が見える。
        /// null（＝打球にならない球・打球データを持たない試合）なら従来どおり判定面の先で球を畳む。
        /// </summary>
        public void OnPitchReleased(float nextReleaseIn, DuelPitchOutcome outcome, DuelPitchFlight? pitch,
            KokoSim.Engine.Match.Timeline.Playback.PlaybackBallSegment batted)
            => OnPitchReleased(nextReleaseIn, outcome, pitch, batted, isBuntAttempt: false);

        /// <summary>
        /// バント/セーフティバント/スクイズの指示つきのリリース（issue #490）。<paramref name="isBuntAttempt"/>
        /// が真なら、打者の構え・接触/空振りの一発物をバント用クリップへ差し替える（無ければ通常の
        /// 打撃クリップのまま＝他の一発物と同じ「無いものは待機のまま」流儀）。
        /// </summary>
        public void OnPitchReleased(float nextReleaseIn, DuelPitchOutcome outcome, DuelPitchFlight? pitch,
            KokoSim.Engine.Match.Timeline.Playback.PlaybackBallSegment batted, bool isBuntAttempt)
            => OnPitchReleased(nextReleaseIn, outcome, pitch, batted, isBuntAttempt, isCalledStrikeout: false);

        /// <summary>
        /// 見逃し三振の指示つきのリリース（issue #533）。<paramref name="isCalledStrikeout"/> が真なら、
        /// 球審のストライクコールを見逃し三振専用クリップ（<see cref="UmpireStrikeOutClipName"/>）へ
        /// 差し替える（無ければ通常の <see cref="UmpireStrikeClipName"/> のまま＝他の一発物と同じ
        /// 「無いものは待機のまま」流儀）。<paramref name="nextReleaseIn"/>（＝isLastの推測）からは
        /// 判定しない――盗塁死等でイニングが終わる打席は最後の球が見逃しストライクでも三振ではない
        /// ため、呼び出し側（controller）が打席結果（<c>pt.StrikesAfter &gt;= 3</c>）から明示的に渡す
        /// （<paramref name="isBuntAttempt"/> と同じ作法。issue #533 実装メモ2節）。
        /// </summary>
        public void OnPitchReleased(float nextReleaseIn, DuelPitchOutcome outcome, DuelPitchFlight? pitch,
            KokoSim.Engine.Match.Timeline.Playback.PlaybackBallSegment batted, bool isBuntAttempt,
            bool isCalledStrikeout)
            => OnPitchReleased(nextReleaseIn, outcome, pitch, batted, isBuntAttempt, isCalledStrikeout,
                batterSwung: false);

        /// <summary>
        /// 捕り損ねる球（<see cref="DuelPitchOutcome.NotCaught"/>）で打者が振ったかを添えるリリース（issue #525）。
        ///
        /// 分類は捕手側の事情（捕るか捕らないか）しか表せないのに対し、打者が振ったかは別の軸で、
        /// **振り逃げは定義上そのほとんどが空振り**＝<see cref="DuelPitchOutcome.SwingMiss"/> を
        /// NotCaught で置き換えると打者のスイングが丸ごと消える。この引数で打者だけ別に駆動する。
        /// NotCaught 以外の分類では使わない（打者の芝居は従来どおり分類が決める）。
        /// </summary>
        public void OnPitchReleased(float nextReleaseIn, DuelPitchOutcome outcome, DuelPitchFlight? pitch,
            KokoSim.Engine.Match.Timeline.Playback.PlaybackBallSegment batted, bool isBuntAttempt,
            bool isCalledStrikeout, bool batterSwung)
        {
            if (!Available || _pitcher == null) return;
            // ⚠ ここで構えを切り替えない（issue #490 実機確認 2026-08-06）。リリース時点から始めると
            // 移行 1.10秒に対して着弾まで 0.45秒しかなく squaring の途中で球が来る。構えは
            // 振りかぶりの前に PrepareBatterBunt で入れる＝**この球の指示だけを見て下ろすと、
            // 指示の無い球のリリースが予約済みの構えを巻き戻す**（実測: リリース時点で移行クリップが
            // 毎回 t=0.00 だった）。構えの真偽はコントローラが予約と実指示の両方から決める。
            // isBuntAttempt はこの下の一発物（当てる/空振り）の差し替えにだけ使う。
            _pendingBatted = batted;
            // リリースの再同期は**ポーズと向きだけ**合わせ、立ち位置は据え置く。
            //
            // `SeekActor` は BasePos へ巻き戻して60fpsで積分し直すが、実再生は12fpsコマ打ち。
            // ルートモーションの移動量は**その時の向きの上で**足されるので、回りながら踏み込む投球では
            // 刻み幅が違うと軌跡が食い違う。実測（2026-07-30 DuelRootMotionProbe・切り口1.75s→3.0s）:
            //   60fps積分 dPos=(0.03,-0.75) / 12fps積分 dPos=(-0.31,-0.58) — **位置だけ0.38m飛ぶ**
            //   yaw は両者 -107.4° で一致 — **向きは刻み幅に依存しない**
            // ＝位置を戻すと「投げる瞬間に右へスライド」（監督指摘）。だから位置は据え置く。
            // 逆に向きまで据え置くと、リリースが12fpsコマ境界のどこで来たかで最大1コマぶん
            // （リリース近傍は1コマ20〜40°回る）取りこぼし、フォロースルーの向きが球ごとに
            // 40°ばらつく（実測: 1球目 -107° / 2球目 -148°）。向きは Seek の値をそのまま使う。
            var keepPos = _pitcher.Go.transform.localPosition;
            // 先に**発射時刻**の姿勢へ送って球の出所を採る（アンカーの ReleaseTime では手が振り下ろされた後）。
            // Seek は BasePos から積分し直す決定論なので、続けてアンカー時刻へ送り直せば姿勢は同じに揃う。
            //
            // 帰結として、発射直後のコマは「球=発射時刻(2.97)の手の位置／投手=アンカー時刻(3.05)の姿勢」＝
            // 80ms ぶん投手のポーズが先行する。これは承知の上でこうしている:
            //   ・発射点をアンカー時刻の手にすると腰の高さ（実測1.00m）から球が出て「下手投げ」に見える
            //   ・アンカーの ReleaseTime 自体は捕球・ミート・コールの逆算基準として調整済み（#331/#335）で動かせない
            //   ・80ms は12fpsコマ打ちの1コマ弱＝投手のポーズはどちらにしても振り下ろし後になる
            // 好みの追い込みは BallReleaseClipTime のつまみ（キャリブレーション窓）で行う
            SeekActor(_pitcher, _cal.BallReleaseClipTime);
            _pitcher.Go.transform.localPosition = keepPos;
            GroundFix(_pitcher);
            // 捕球するクリップを先に決める（球をどこまで運ぶか＝捕球面がクリップごとに違う）
            int catchIndex = SelectCatchAnchor(outcome, pitch);
            // 捕手の構えを狙い位置へ（issue #396 ①）。狙いは投球前から捕手が知っている情報なので
            // ここ（リリース＝ボールが手を離れる直前）で使ってよい。実着弾位置との差（残差＝コントロール
            // 誤差そのもの）は FixCatcherPose がリリース後の体寄せとして別に吸収する（②）。
            StanceCatcherToAim(pitch);
            bool meetsBat = outcome == DuelPitchOutcome.InPlayContact
                || outcome == DuelPitchOutcome.FoulContact;
            float flight = LaunchBall(pitch, catchIndex, meetsBat);
            // 見逃し・空振り（ストライク系）は球審のコール（＝捕球後に動き出した腕が伸び切る瞬間）に、
            // ボール（コールの絵が無い＝issue #527 C）は反応だけに、打つ球・死球は当たった瞬間に
            // 判定テロップを合わせる（下の項）。
            bool isStrike = outcome == DuelPitchOutcome.CalledStrike || outcome == DuelPitchOutcome.SwingMiss;
            CallMomentSeconds = _ball == null ? flight
                : isStrike ? _ballArrival + UmpireCallReactSeconds + UmpireCallAnchorSeconds / UmpireCallPlaybackRate
                : catchIndex >= 0 ? _ballArrival + UmpireCallReactSeconds
                : _ballArrival;

            SeekActor(_pitcher, ReleaseTime);
            _pitcher.Go.transform.localPosition = keepPos;
            GroundFix(_pitcher);
            HasCutToNextWindup = false;   // 新しいリリースぶんの予約＝まだ切り替わっていない
            if (nextReleaseIn > 0f)
            {
                // 上限は WindupSeconds＝カットバック先が DeliveryStartTime より手前（＝振りかぶり区間）に
                // 入らないようにする。間隔が足りないときだけ下限0.5まで削って間に合わせる
                float preRoll = Mathf.Clamp(nextReleaseIn - FollowSeconds, 0.5f, WindupSeconds);
                _cutCountdown = nextReleaseIn - preRoll;
                _cutClipTime = ReleaseTime - preRoll;
            }
            else
            {
                _cutCountdown = -1f;   // 最後の1球: フォロースルーを最後まで→クリップ末尾でセット姿勢
            }
            ScheduleReactions(outcome, nextReleaseIn, flight, catchIndex, isStrike, isBuntAttempt,
                isCalledStrikeout, pitch, batterSwung);
        }

        /// <summary>打者・捕手・球審の結果分岐（§7-2第2弾）。クリップが無い分岐は待機のまま何もしない。
        /// <paramref name="isBuntAttempt"/>（issue #490）が真なら、当てる/空振りの一発物をバント用
        /// クリップへ差し替える（無ければ通常の打撃クリップのまま）。<paramref name="isCalledStrikeout"/>
        /// （issue #533）が真なら、球審のコールを見逃し三振専用クリップへ差し替える。</summary>
        private void ScheduleReactions(DuelPitchOutcome outcome, float nextReleaseIn, float flight,
            int catchIndex, bool isStrike, bool isBuntAttempt = false, bool isCalledStrikeout = false,
            DuelPitchFlight? pitch = null, bool batterSwung = false)
        {
            // 球審: ストライク（見逃し・空振り）でコールを予約、それ以外は前球の予約をキャンセル（#333）
            // バットを球へ向けるのは当たる球だけ。空振りで向けると「当たっているのに空振り」になる
            _batAimsBall = outcome == DuelPitchOutcome.InPlayContact
                || outcome == DuelPitchOutcome.FoulContact;
            _umpireCallCountdown = -1f;
            _swingCountdown = -1f;   // 振らない結果は前球のスイング予約を持ち越さない
            PendingResultHoldSeconds = 0f;
            // 見逃し三振（issue #533）: 専用クリップが揃っている・この球が実際に見逃し三振の球である
            // 両方が真の球だけ差し替える（無い環境や空振り三振では従来の Strike_Type01 のまま＝対象外）。
            bool useStrikeOut = isCalledStrikeout && outcome == DuelPitchOutcome.CalledStrike
                && _umpireStrikeOutClip != null;
            _pendingUmpireClip = useStrikeOut ? _umpireStrikeOutClip : _umpireStrikeClip;
            // ⚠ isStrike は NotCaught（暴投・捕逸・振り逃げ。issue #525）では偽＝球審のコール動作は
            // 出さない。コールの逆算基準が「ミットに収まる時刻」で、収まらない球には置きようがないため
            // （頭出しでごまかすと球が転がっているのに構えたままコールする絵になる）。
            // **判定テロップは別**で、CallMomentSeconds が球の到達時刻を返す＝捕れなくても判定は出る。
            if (isStrike && _pendingUmpireClip != null)
            {
                // 開始同期: クリップ開始（突き出しの助走の頭）を「捕球 + 反応」に置く＝
                // ミットに収まる前に球審が動き出さない（UmpireCallReactSeconds の項）。
                // 頭出しはしない（頭こそがジャッジの動作なので捨てる区間が無い）。
                _umpireCallCountdown = flight + UmpireCallReactSeconds;
                _umpireCallStartAt = 0f;
                // 打ち切りは「戻り終わり(UmpireCallSettleSeconds、クリップ秒)まで再生できないとき」だけ。
                // 再生は UmpireCallPlaybackRate 倍速なので、実秒の持ち時間(playable)をクリップ秒へ
                // 換算してから比べる（速度を入れたらアンカーの逆算も実秒→クリップ秒へ換算し直す掟）。
                // 間隔3.6s・飛翔0.55sなら開始0.65sで戻りまで2.55/2.1≒1.21sが余裕を持って収まる。
                // 切る場合も腕を出す前で切れないよう、次のリリース直前まで目一杯回す。
                //
                // ⚠ 見逃し三振は常に打席最後の球（nextReleaseIn=-1）＝playableは常にfloat.MaxValueで
                // ここでは絶対に切られない（この分岐は次の投球が同じ打席内にある場合だけ効く）。
                // 4.83sの新クリップが「途中で切れて構えへ戻らない」罠（実装メモ5節）は下の
                // PendingResultHoldSeconds（呼び出し側のnoPlayHoldSecondsを底上げする値）で防ぐ。
                float playable = nextReleaseIn > 0f ? nextReleaseIn - _umpireCallCountdown : float.MaxValue;
                float playableClipSeconds = playable * UmpireCallPlaybackRate;
                _umpireCallEndAt = playableClipSeconds >= UmpireCallSettleSeconds
                    ? 0f                                                   // 末尾まで（尻尾は静止＝絵は変わらない）
                    : Mathf.Max(0f, playableClipSeconds);
                if (useStrikeOut)
                {
                    // アンカー(UmpireCallAnchorSeconds)以降を全部見せ切るのに要る実秒。
                    // クリップ長はAnimationClip.lengthの実値（実測不要・常に正確）を使うので、
                    // アンカーそのものの実測（RunClipSpeedProbe。issue #533実装メモ3節）が
                    // 済んでいなくても「クリップが自然終了より手前で切れない」ことだけは保証できる
                    // （実測はテロップと腕の一致精度だけに影響し、打ち切りの罠には影響しない）。
                    PendingResultHoldSeconds = Mathf.Max(0f,
                        (_umpireStrikeOutClip.length - UmpireCallAnchorSeconds) / UmpireCallPlaybackRate);
                }
            }

            // バント/セーフティバント/スクイズの指示が出ていて、かつバント用クリップが揃っている球だけ
            // 一発物をバント用へ差し替える（issue #490）。無い環境では通常の打撃クリップのまま（フォールバック）。
            bool useBunt = isBuntAttempt && _buntHitClip != null && _buntMissClip != null;
            var missClip = useBunt ? _buntMissClip : _swingMissClip;
            var missAnchor = useBunt ? BuntMissPlane : SwingMissPlane;
            var hitClip = useBunt ? _buntHitClip : _swingHitClip;
            var hitAnchor = useBunt ? BuntHitContact : SwingHitContact;

            switch (outcome)
            {
                // どのスイングも**振り抜いた所で止めて据え置く**（design-18 §4.5-⑭）。
                // 打ち切りだけで据え置きが無いと、打ち切った瞬間に待機へ戻る
                case DuelPitchOutcome.SwingMiss:
                    // 空振り専用の収録が無いので同じスイングを使う（バントは Bunt_Miss を使う）
                    SetSwing(missClip, missAnchor, flight, EffectiveSwingFinishSec(), SwingFinishHoldSeconds);
                    break;
                // 当てる球は判定面でなく**捌く瞬間**（_ballArrival＝判定面の ContactAheadM 手前）で合わせる。
                // flight を渡すと、前で捌くぶんバットが遅れて着く（0.30m/40m·s⁻¹ ≒ 8ms）
                case DuelPitchOutcome.FoulContact:
                    SetSwing(hitClip, hitAnchor, _ballArrival, EffectiveSwingFinishSec(), SwingFinishHoldSeconds);
                    break;
                case DuelPitchOutcome.InPlayContact:
                    SetSwing(hitClip, hitAnchor, _ballArrival, EffectiveSwingFinishSec(), SwingFinishHoldSeconds);
                    break;
                // CalledStrike（見逃し）は打者を待機のままにする＝ここで何もしない（#291）。
                // バントの見送り（Sign=Bunt・Kind=Ball等でoutcome=Take）も同様＝バント構えのまま何もしない。
                // 捕手の捕球は下の catcherCatches に含めたまま残す
                case DuelPitchOutcome.HitByPitch:
                    SetActorClip(_batter, _hbpClip, HbpReact - flight, 4.2f);
                    break;
                // NotCaught（暴投・捕逸・振り逃げ。issue #525）。打者が振ったかは分類でなく batterSwung が持つ
                // ＝振り逃げ（ほぼ空振り三振）で打者のスイングが消えないようにする。見送りなら待機のまま
                case DuelPitchOutcome.NotCaught:
                    if (batterSwung)
                        SetSwing(missClip, missAnchor, flight, EffectiveSwingFinishSec(), SwingFinishHoldSeconds);
                    break;
            }
            // 捕り損ねる球は捕球でなく後逸を予約する（issue #525）。catchIndex は SelectCatchAnchor が
            // 既に -1 を返しているので、ここで ScheduleCatch を呼んでも何も起きない＝分岐を明示する
            if (outcome == DuelPitchOutcome.NotCaught) SchedulePassedBall(SelectPassedBall(pitch));
            else ScheduleCatch(catchIndex, isStrike);
        }

        /// <summary>
        /// 打者のスイングを「アンカーが球の到達に一致する」ように予約する（速度対応版）。
        ///
        /// 頭出しは<b>クリップ秒</b>、飛翔時間は<b>実秒</b>なので、再生速度を掛けたら
        /// <c>anchorClipSec − flight × Speed</c> にしないと面通過が球からずれる。
        /// 速度1では従来式（anchor − flight）と一致する。
        ///
        /// 頭出しは**クリップを前へ飛ばす**ことしかできないので、速度を上げると
        /// <c>anchor − flight × k</c> はいずれ負になる（実測の境目＝空振り 1.08/0.52≒2.08倍・
        /// 打つ 1.17/0.52≒2.25倍）。そのまま <see cref="SetActorClip"/> へ渡すと <c>Max(0,…)</c> で
        /// 潰れ、**黙って**リリース直後に振り始める（捕球アンカーで実際に踏んだ罠。design-18 §4.5-③）。
        /// つまみは 0.5〜3.0 まで回るので、旧実装の「上限2.0で余裕を作る」は**実装と食い違っていた**
        /// （issue #368）。足りないのは「後ろへずらす」なので、捕球・球審コールと同じ
        /// **カウントダウン開始**（<see cref="_swingCountdown"/>）へ作り替えてある＝
        /// クリップ頭（構え）から始めて、アンカーが球に一致するまで開始そのものを待つ。
        /// これでつまみの全域が安全になり、速度を上げても面通過は球からずれない。
        /// </summary>
        private void SetSwing(AnimationClip clip, float anchorClipSec, float flight, float endAt, float hold)
        {
            if (_batter == null || clip == null) return;
            float k = Mathf.Max(0.1f, _cal.SwingSpeed);
            float seek = anchorClipSec - flight * k;
            _swingCountdown = -1f;
            if (seek < 0f)
            {
                // 頭出しでは間に合わない＝クリップ頭から回しても球より早くアンカーへ着く。
                // 開始を `flight − anchor/k` だけ待つ（クリップ内の緩急は触らない＝design-18 の掟）
                _swingPendingClip = clip;
                _swingPendingAnchor = anchorClipSec;
                _swingPendingEndAt = endAt;
                _swingPendingHold = hold;
                _swingCountdown = flight - anchorClipSec / k;
                return;
            }
            StartSwing(clip, anchorClipSec, seek, endAt, k, hold);
        }

        /// <summary>
        /// 予約したスイングを実際に始める（<see cref="SetSwing"/> の即時分岐と予約の発火で共有）。
        /// 据え置き（<paramref name="hold"/>）は必ずここで張る＝<see cref="SetActorClip"/> が
        /// 差し替えのたびに <c>HoldSeconds</c> を0へ落とすので、呼び元で先に張ると予約側で消える。
        /// </summary>
        private void StartSwing(AnimationClip clip, float anchorClipSec, float seek, float endAt, float k,
            float hold)
        {
            if (_batter == null || clip == null) return;
            // 構えを別クリップにしていると、ここが**クリップの切り口**になる（構え→スイング）。
            // 頭出しだけだと姿勢が飛ぶので投手のカットバックと同じクロスフェードで繋ぐ。
            // 同じクリップのとき（既定）は従来どおり頭出しだけ＝絵は1ビットも変わらない
            if (_batter.Clip != clip) CrossfadeActorToClip(_batter, clip, seek, endAt, CrossfadeSeconds);
            else SetActorClip(_batter, clip, seek, endAt);
            _batter.Speed = k;   // SetActorClip の後（差し替えで持ち越さないため）
            _batter.HoldSeconds = hold;   // 同上（SetActorClip が保留中の据え置きを打ち消す）
#if UNITY_EDITOR
            if (SwingAnchorOverrideForCapture > 0f) anchorClipSec = SwingAnchorOverrideForCapture;
#endif
            _swingAnchorClip = anchorClipSec;   // バット軌道（SwingBatAxis）の終点＝接触の時刻
            _batWrapValid = false;
            _batTrailSwing = true;   // バット軌跡の武装（issue #444）。解除は FinishOneShot／打席の仕切り直し
            _batRestValid = false;
            // 頭出しは「基準位置へ戻して0→seekをルートモーション込みで積分し直す」ので、
            // **クリップ頭からseekまでの水平移動がそのまま位置の差分として乗る**。待機ループ側は
            // 毎コマ x/z を基準位置へ戻している（StepActor）ので、振り始めた瞬間に打者がその差分ぶん
            // ワープする。実測 2026-07-31: seek=0.47 で (-0.173, -0.204)＝**0.17m 三塁側へ・0.20m 奥へ**、
            // 画面では「スイングと同時に右へ一歩ずれる」（監督指摘）。ここで戻すのは**この瞬間だけ**の
            // 頭出しワープぶん。seek 以降、毎コマ乗り続ける横（x）ドリフトは StepActor 側の
            // RebaseXWhileSwinging が引き継いで戻す（issue #446）。z（投手方向の踏み込み）は
            // どちらの機構も触らないのでそのまま残る
            RebaseActorXZ(_batter);
        }

        /// <summary>頭出し直後の水平位置を基準位置へ戻す（縦＝接地は GroundFix が別に見るので触らない）。</summary>
        private static void RebaseActorXZ(Actor a)
        {
            if (a == null || a.Go == null) return;
            var lp = a.Go.transform.localPosition;
            lp.x = a.BasePos.x;
            lp.z = a.BasePos.z;
            a.Go.transform.localPosition = lp;
        }

        /// <summary>
        /// 捕球クリップを「捕球の瞬間が球の到達に一致する」ように予約する（issue #332 の第2弾）。
        ///
        /// 旧実装は <c>SetActorClip(..., CatchMoment - flight, ...)</c> の頭出しだけだった。頭出しは
        /// **クリップを前へ飛ばす**ことしかできないので、捕球の瞬間（実測0.06〜0.21s）より飛翔時間
        /// （0.42〜0.52s）が長い＝常に負になり <c>Max(0,…)</c> で0へ潰れ、リリース直後に捕っていた
        /// （球到達の0.36s前。2026-07-30 監督指摘）。足りないのは「後ろへずらす」＝開始の予約なので、
        /// 球審コール（<see cref="_umpireCallCountdown"/>）と同じ作法でカウントダウンを持つ。
        /// </summary>
        private void ScheduleCatch(int catchIndex, bool isStrike)
        {
            _catchCountdown = -1f;
            _catchIndex = catchIndex;
            _catchIsStrike = isStrike;
            if (catchIndex < 0) return;
            var clip = _catchClips[catchIndex];
            float start = _ballArrival - CatchAnchors[catchIndex].Moment;
            if (start > 0f) _catchCountdown = start;
            else
            {
                // 捕球が遅いクリップは頭出しで詰める。捕球の瞬間（クリップ内 Moment）で打ち切り＋
                // 据え置き＝捕った形のまま判定表示が消えるまで静止する（監督指示 2026-08-05。
                // 収録の吸収・立ち上がりは据え置きに置き換わるが、実物の捕手も判定の間は
                // ミットを止めて見せる＝フレーミング）。HoldSeconds は SetActorClip の後に張る
                //（差し替えが保留中のホールド予約を打ち消すため。打者のスイングと同じ作法）
                SetActorClip(_catcher, clip, -start, CatchAnchors[catchIndex].Moment);
                _catcher.HoldSeconds = CatcherLingerSeconds(isStrike);
            }
        }

        /// <summary>
        /// 後逸クリップの添字（issue #525）。捕り損ねる球（<see cref="DuelPitchOutcome.NotCaught"/>）だけで
        /// 使い、無ければ -1＝捕手は待機のまま（球は抜けたまま。捕らない判断はクリップの有無に依らない）。
        ///
        /// 左右は<b>実着弾位置</b>（<see cref="DuelPitchFlight.PlateX"/>/<see cref="DuelPitchFlight.PlateY"/>）で
        /// 選ぶ。捕球クリップが<b>狙い</b>で選ぶ（捕手は未来を知らない）のとは逆で、後逸は「どこへ逸れたか」が
        /// そのまま手を出す方向だから＝この球が捕れない球であることは既に確定している（engine が
        /// 着弾コースで抽選している）。乱数は引かない＝同じコースなら常に同じクリップ（決定論）。
        /// </summary>
        private int SelectPassedBall(DuelPitchFlight? pitch)
        {
            if (_catcher == null || _passedBallClips == null) return -1;
            float px = pitch?.PlateX ?? 0f;
            float py = pitch?.PlateY ?? DefaultPlateY;
            float catcherX = CatcherBaseX;
            int best = -1;
            float bestCost = float.MaxValue;
            for (int i = 0; i < PassedBallAnchors.Length; i++)
            {
                if (_passedBallClips[i] == null) continue;
                var m = PassedBallAnchors[i].Mitt;
                float dx = px - (catcherX + m.x);
                float dy = py - m.y;
                float cost = Mathf.Sqrt(dx * dx + dy * dy);
                if (cost < bestCost) { bestCost = cost; best = i; }
            }
            return best;
        }

        /// <summary>
        /// 後逸を予約する（issue #525）。<see cref="ScheduleCatch"/> と同じ逆算＝クリップ内の
        /// 「捕り損ねる瞬間」（<see cref="PassedBallAnchors"/> の Moment）が球の到達に一致するよう
        /// 再生開始をずらす。到達より Moment が後ろのクリップは頭出しで詰める（捕球と同じ作法）。
        ///
        /// 打ち切りはしない＝後逸の後は捕手が球を追う動きが続くのが自然で、そこを見せて構わない
        /// （捕球のような「判定の間ミットを止めて見せる」据え置きは要らない）。
        /// </summary>
        private void SchedulePassedBall(int index)
        {
            _catchCountdown = -1f;
            _catchIndex = -1;                 // 球はミットへ入らない＝捕球の予約としては扱わない
            _passedBallCountdown = -1f;
            _passedBallIndex = index;
            if (index < 0) return;
            var clip = _passedBallClips[index];
            float start = _ballArrival - PassedBallAnchors[index].Moment;
            if (start > 0f) _passedBallCountdown = start;
            else SetActorClip(_catcher, clip, -start, 0f);
        }

        /// <summary>
        /// コースに一番近い捕球クリップの添字（無ければ-1＝捕手は待機のまま）。
        /// 距離は本塁面の左右・高さで測る（奥行きの差は延長15ms＝2cm未満なので効かない）。
        /// 乱数を引かない純粋な選択＝同じコースなら常に同じクリップ（決定論）。
        ///
        /// 基準は<b>狙い位置</b>（<see cref="DuelPitchFlight.AimX"/>/<see cref="DuelPitchFlight.AimY"/>。
        /// issue #396）＝捕手は投球前からその狙いを知っているので、構え（クリップ選択）に使ってよい。
        /// 実着弾位置は使わない（投球前に実着弾へ寄せると「捕手が未来を知っている」ことになり、
        /// リリース後の残差の体寄せ（<see cref="FixCatcherPose"/>）の効果を自分で潰す）。
        /// 基準のx（<see cref="CatcherBaseX"/>）も較正の固定値を使う——<see cref="StanceCatcherToAim"/> で
        /// 立ち位置を狙いへ寄せた後の位置を読むと、寄せた分だけ狙いの符号が相殺されクリップが動かなくなる。
        /// </summary>
        private int SelectCatchAnchor(DuelPitchOutcome outcome, DuelPitchFlight? pitch)
        {
            // NotCaught（暴投・捕逸・振り逃げ。issue #525）はここに**含めない**＝-1 を返す。
            // 返り値がそのまま LaunchBall の「この球は捕られるか」なので、球はミットへ入らず抜ける。
            bool catcherCatches = outcome == DuelPitchOutcome.Take
                || outcome == DuelPitchOutcome.CalledStrike
                || outcome == DuelPitchOutcome.SwingMiss;
            if (!catcherCatches || _catcher == null || _catchClips == null) return -1;

            float px = pitch?.AimX ?? 0f;
            float py = pitch?.AimY ?? DefaultPlateY;
            float catcherX = CatcherBaseX;
            int best = -1;
            float bestCost = float.MaxValue;
            for (int i = 0; i < CatchAnchors.Length; i++)
            {
                if (_catchClips[i] == null) continue;
                var m = CatchAnchors[i].Mitt;
                float dx = px - (catcherX + m.x);
                float dy = py - m.y;
                float cost = Mathf.Sqrt(dx * dx + dy * dy) + (i == 0 ? 0f : CatchSelectionBias);
                if (cost < bestCost) { bestCost = cost; best = i; }
            }
            return best;
        }

        /// <summary>
        /// 横の立ち位置を狙いへ寄せる（issue #396 ①）。<see cref="SelectCatchAnchor"/> がクリップ（高さ＋
        /// 大きな左右のずれ）を狙いへ合わせるのに対し、こちらは捕手の体そのものの左右の寄りを狙いへ足す
        /// （実際の捕手も的をつけるとき軽く体ごと寄る）。常に較正の固定値（<see cref="CatcherBaseX"/>）を
        /// 基準にした絶対位置へ置く＝前の球の寄りを引きずらない。
        ///
        /// あわせてこの球の残差（実着弾−狙い＝コントロール誤差そのもの）を控えておく。<see cref="FixCatcherPose"/>
        /// がリリース後だけこれを体の寄せとして反映する（②）。狙いの無い球（合成列）は寄せも残差もゼロ
        /// ＝従来どおり中央固定のまま。
        /// </summary>
        private void StanceCatcherToAim(DuelPitchFlight? pitch)
        {
            float aimX = pitch?.AimX ?? 0f;
            _catcherResidual = pitch.HasValue
                ? new Vector2(pitch.Value.PlateX - pitch.Value.AimX, pitch.Value.PlateY - pitch.Value.AimY)
                : Vector2.zero;
            SetActorXZ(_catcher, CatcherBaseX + aimX, _cal.CatcherZ);
        }

        /// <summary>この球の残差（実着弾−狙い）[m]。x=左右, y=高さ。<see cref="StanceCatcherToAim"/> が控える。</summary>
        private Vector2 _catcherResidual;

        /// <summary>
        /// 残差の体寄せ専用の時計（リリースからの経過[s]。負=止まっている）。<see cref="_ballT"/> と同じ
        /// 刻みだが、球がミットの中で消えた後（<see cref="_ballT"/>=-1）も体の戻りが終わるまで生き続ける。
        /// 球の時計で駆動すると、捕球の瞬間から戻りが始まり**捕った球ごと体が中央へスライドする**
        /// （全身でフレーミングしているように見える）うえ、球の消滅と同時に駆動が死んで戻りが打ち切られる。
        /// </summary>
        private float _catcherShiftT = -1f;

        /// <summary>
        /// 持ち球を手の中心から掌側へ沈める量[m]。手の中心に置くと球（直径0.074）が手の甲側にも
        /// 半分はみ出し、センターカメラ（投手の背中越し）から「指の上に球が乗っている」ように見える
        /// （2026-08-05 監督指摘）。球半径弱ぶん掌側へ沈めると、甲と指が球のカメラ側を隠し
        /// 「握っている」絵になる。掌の向きは素体依存なので実測で採る（<see cref="PalmDirWorld"/>）。
        /// </summary>
        private const float BallPalmSinkM = 0.032f;

        /// <summary>
        /// ボールを投球する手へ貼り付ける（振りかぶり中は手の中にある）。<see cref="OnPitchWindup"/> から呼ぶ。
        ///
        /// 位置は design-18 §3.3.1 の掟どおり**手の中心＝手首→中指付け根の中点**を基準にし、そこから
        /// 掌側へ <see cref="BallPalmSinkM"/> 沈め、較正のオフセット
        /// （<see cref="DuelCalibration.BallInHandOffset"/>）を手ボーンローカルで足す。
        /// 手ボーンローカルで持つので、以後は手がどう動いても球は手の中（掌側）に留まる。
        /// </summary>
        private void HoldBallInHand()
        {
            if (_ball == null || _pitcher == null || _pitcher.Animator == null) return;
            var hand = _pitcher.Animator.GetBoneTransform(HumanBodyBones.RightHand);
            if (hand == null) return;
            var tip = _pitcher.Animator.GetBoneTransform(HumanBodyBones.RightMiddleProximal);
            // 手の中心（ワールド）→ 手ボーンローカル。指の付け根方向はボーンローカルでは動かないので
            // 1回求めれば以後のポーズでも手の中を指し続ける
            var centerWorld = tip != null ? Vector3.Lerp(hand.position, tip.position, 0.5f) : hand.position;
            _ballHand = hand;
            _ballHeldLocal = hand.InverseTransformPoint(centerWorld) + _cal.BallInHandOffset
                + hand.InverseTransformDirection(PalmDirWorld(_pitcher.Animator, hand, tip)) * BallPalmSinkM;
            _ballHeld = true;
            _ballInMitt = false;   // 前球がミットの中に残っていても投手の手へ戻す
            _ballT = -1f;
            _catcherShiftT = -1f;   // 前球の体寄せの戻り残りを持ち越さない（次の球の構えが基準）
            _ball.transform.SetParent(hand, false);
            _ball.transform.localPosition = _ballHeldLocal;
            _ball.SetActive(true);
        }

        /// <summary>
        /// 右手の掌の法線（ワールド・掌が向く側）。<see cref="AttachGlove"/> と同じ実測で組む:
        /// 縦軸=手首→中指付け根、横=人差し指付け根→小指付け根、**符号は指の丸まり側**（外積では
        /// 決まらず、視線で決めると取り違える＝グラブが甲側に付いた2026-07-29の教訓）。
        /// 左投手は鏡映親ノード下だが、丸まりも実測なので符号はそのまま正しい。
        /// 指ボーンが無い・丸まりが弱く符号が決められない素体では zero を返す（呼び出し側は沈め無し）。
        /// </summary>
        private static Vector3 PalmDirWorld(Animator a, Transform hand, Transform knuckle)
        {
            var index = a.GetBoneTransform(HumanBodyBones.RightIndexProximal);
            var little = a.GetBoneTransform(HumanBodyBones.RightLittleProximal);
            var tip = a.GetBoneTransform(HumanBodyBones.RightMiddleDistal);
            if (hand == null || knuckle == null || index == null || little == null || tip == null)
                return Vector3.zero;
            var up = knuckle.position - hand.position;
            up = up.sqrMagnitude > 1e-6f ? up.normalized : Vector3.up;
            var n = Vector3.Cross(up, index.position - little.position);
            n -= Vector3.Dot(n, up) * up;
            if (n.sqrMagnitude < 1e-8f) return Vector3.zero;
            n.Normalize();
            var curl = tip.position - knuckle.position;
            curl -= Vector3.Dot(curl, up) * up;
            if (curl.magnitude < 0.01f) return Vector3.zero;
            return Vector3.Dot(n, curl) < 0f ? -n : n;
        }

        /// <summary>
        /// ボールを手から放ち、この球の飛翔時間[s]を返す（アンカー逆算に使う。issue #332）。
        ///
        /// 発射点は**発射する瞬間のクリップ時刻**（<see cref="DuelCalibration.BallReleaseClipTime"/>＝手の
        /// 速度ピーク実測）における手の中の球位置。アンカーの <see cref="ReleaseTime"/> で採ると
        /// 手が既に振り下ろされていて腰から球が出る（2026-07-30 実測）。
        ///
        /// 到達点は**判定に使われた実着弾位置**（<c>PitchRecord.LocationX/Y</c> の写し）。engine の場と
        /// 対決ビューのステージは左右の符号が同じ（+x=一塁側=左打者側）なのでそのまま使う。
        /// 曲がりは engine の純関数 <c>PitchFlightPath</c> が持つ＝Unity側で弾道を積分し直さない
        /// （不変条件#1「物理層の変換式は一箇所」）。
        /// </summary>
        private float LaunchBall(DuelPitchFlight? pitch, int catchIndex, bool meetsBat)
        {
            float flight = pitch?.FlightSeconds ?? BallFlightSeconds;
            if (flight <= 0f) flight = BallFlightSeconds;
            if (_ball == null) return flight;

            _ballRelease = ReleasePointLocal();
            // コースが無い球（合成列）は「コースを主張しない」＝ゾーン中央へ運ぶだけ（issue #332 の判断(a)）
            _ballPlate = pitch.HasValue
                ? new Vector2(pitch.Value.PlateX, pitch.Value.PlateY)
                : new Vector2(0f, DefaultPlateY);
            _ballIvb = pitch?.InducedVerticalBreakM ?? 0f;
            _ballIhb = pitch?.InducedHorizontalBreakM ?? 0f;
            _ballFlight = flight;
            _ballT = 0f;
            _catcherShiftT = 0f;   // 体寄せの時計も同時に始める（戻りは球より長生き）
            _ballHeld = false;
            _ballInMitt = false;
            _ballInMittRemaining = 0f;
            _batted = _pendingBatted;   // 仕込むだけ＝接触（＝球の到達）まで乗り換えない
            _pendingBatted = null;
            _battedT = -1f;
            _trail.Clear();
            _trailNow = -1f;

            // 捕られる球は判定面（z=0）で止めず、選んだクリップの捕球面まで接線で延長する。
            // 捕球面は「そのクリップのミットの奥行き」＝ミットが待っている所まで運ぶ（実測0.19〜0.50m前）
            _ballWillBeCaught = catchIndex >= 0;
            float arrival = flight;
            if (_ballWillBeCaught)
            {
                float planeZ = _catcher.Go.transform.localPosition.z + CatchAnchors[catchIndex].Mitt.z;
                arrival = (float)KokoSim.Engine.Match.Timeline.Playback.PitchFlightPath.SecondsToDepth(ReleaseVector(), flight, planeZ);
                if (arrival < flight) arrival = flight;   // 捕球面が本塁より前（較正の異常値）なら延長しない
            }
            else if (meetsBat && ContactAheadM > 0f)
            {
                // 打つ球は**判定面より前で捌く**（下の ContactAheadM の項）。球は投手側(+z)から来るので
                // 前で当てる＝判定面より**手前の時刻**で止める。捕球面の延長と同じ式（深さ→時刻）を使う
                float t = (float)KokoSim.Engine.Match.Timeline.Playback.PitchFlightPath.SecondsToDepth(
                    ReleaseVector(), flight, ContactAheadM);
                if (t > 0f && t < flight) arrival = t;
            }
            _ballArrival = arrival;
            _ballCatchPoint = BallPointAt(arrival);

            _ball.transform.SetParent(_root.transform, false);   // 手から離す＝以後フルレートで飛ぶ
            _ball.SetActive(true);
            UpdateBall(0f);
            return flight;
        }

        /// <summary>この球のリリース点（engineのVector3Dへ）。</summary>
        private KokoSim.Engine.Core.Vector3D ReleaseVector()
            => new(_ballRelease.x, _ballRelease.y, _ballRelease.z);

        /// <summary>飛翔中の球の位置（ステージローカル）。判定面より奥は接線で延長する。</summary>
        private Vector3 BallPointAt(float t)
        {
            var p = KokoSim.Engine.Match.Timeline.Playback.PitchFlightPath.AtOrBeyond(ReleaseVector(),
                _ballPlate.x, _ballPlate.y, _ballFlight, _ballIvb, _ballIhb, t);
            return new Vector3((float)p.X, (float)p.Y, (float)p.Z);
        }

        /// <summary>コースの無い球を運ぶ先の高さ[m]（ストライクゾーンの中央 = (0.50+1.05)/2）。</summary>
        private const float DefaultPlateY = 0.775f;

        /// <summary>
        /// 発射点（ステージローカル）＝発射する瞬間のクリップ時刻における「手の中の球」の位置。
        /// 投手を一時的にその時刻へ送って実測し、呼び元がアンカー時刻へ送り直す前提。
        /// 手が取れない場合だけ一般的なリリース高へ落とす。
        /// </summary>
        private Vector3 ReleasePointLocal()
        {
            var fallback = new Vector3(0f, 1.85f, MoundDistance - 1.7f);
            if (_pitcher == null || _pitcher.Animator == null || _ball == null) return fallback;
            var hand = _ballHand ?? _pitcher.Animator.GetBoneTransform(HumanBodyBones.RightHand);
            if (hand == null) return fallback;
            // 貼り付け中ならその球の実位置がそのまま発射点（手の中から出る＝出所が構造的に一致）
            var world = _ballHeld ? _ball.transform.position : hand.TransformPoint(_ballHeldLocal);
            return _root.transform.InverseTransformPoint(world);
        }

        /// <summary>
        /// 飛翔中の球を進める。**フルレート**（12fps量子化の対象外。design-18 §4.5-②）。
        /// 手に貼り付いている間は何もしない（位置は親の手が運ぶ）。
        ///
        /// 捕られる球は捕球面（＝ミットの奥行き）まで運んだところで**ミットの子**へ移す。以後は球が
        /// ミットの吸収動作と一緒に引かれる＝12fpsのポーズと全レートの球のずれもここで閉じる。
        /// 捕られない球（打球・死球）は従来どおり判定面の少し先で畳む。
        /// </summary>
        private void UpdateBall(float deltaTime)
        {
            if (_ball == null) return;
            if (_battedT >= 0f)
            {
                // 守備ビューへ渡った後（_fieldPlay != null）は SetFieldTime が球の位置・残像を
                // 一本化して持つ（issue #398 ①）。ここは対決ビュー側の近似（_battedOffset のブレンド）
                // なので、渡った後も回し続けると同じ点が二重に積まれる／わずかにずれた点が混ざる
                if (_fieldPlay == null) UpdateBattedBall(deltaTime);
                return;
            }
            if (_ballHeld || _ballT < 0f) return;
            _ballT += deltaTime;

            if (_ballInMitt)
            {
                _ballInMittRemaining -= deltaTime;
                if (_ballInMittRemaining <= 0f)
                {
                    _ballT = -1f;
                    _ballInMitt = false;
                    _ball.transform.SetParent(_root.transform, true);
                    _ball.SetActive(false);
                }
                return;
            }

            if (_ballT >= _ballArrival && _ballWillBeCaught && TryPutBallInMitt()) return;

            // 接触＝球が判定面に着いた瞬間（打者のスイングはこの時刻にアンカーしてある）。
            // ここで engine の打球セグメントへ乗り換える＝球を消さずにそのまま飛んでいく
            if (_ballT >= _ballArrival && !_ballWillBeCaught && _batted != null)
            {
                BeginBattedBall(_ballT - _ballArrival);
                return;
            }

            if (_ballT > _ballArrival + BallLingerSeconds)
            {
                _ballT = -1f;
                _ball.SetActive(false);
                return;
            }
            _ball.transform.localPosition = BallPointAt(_ballT);
        }

        /// <summary>
        /// 球をミット（捕手の左手ボーン）の子へ移す。位置は**捕球面での実位置のまま**焼き付ける
        /// （ミット中心へ寄せ直さない＝IKでミットを球へ寄せてあるので、そのままで噛み合う）。
        /// </summary>
        private bool TryPutBallInMitt()
        {
            var hand = _catcher != null && _catcher.Animator != null
                ? _catcher.Animator.GetBoneTransform(HumanBodyBones.LeftHand) : null;
            if (hand == null) return false;
            _ball.transform.localPosition = _ballCatchPoint;
            _ball.transform.SetParent(hand, true);
            _ballInMitt = true;
            _ballInMittRemaining = CatcherLingerSeconds(_catchIsStrike);   // 判定表示が消えるまでミットの中に見せる
            return true;
        }

        /// <summary>
        /// 球を打球セグメントへ乗り換える（＝接触の瞬間）。<paramref name="overshoot"/> は接触を跨いだ
        /// このフレームぶんの端数[s]で、打球時計をそこから始める＝時間が飛ばず球も跳ばない。
        ///
        /// 打点（＝球が判定面に着いた位置）と、engine のセグメント始点（本塁=原点・高さ0）は食い違う。
        /// セグメント側は俯瞰の平面図用に「高さは種別ごとのプロファイル」で作られていて、滞空も
        /// ゴロも h(0)=0＝地面から始まるためで、そのまま乗せると球がバットから足元へ飛ぶ。
        /// 差は**打点からの自由落下**で解消する（左右・前後は同じ時間で線形に寄せる）:
        ///   ・ゴロは打点から落ちて地面を這う＝これがそのまま「バットから出て弾む」絵になる
        ///   ・フライは4秒の弧に0.8mが0.4秒だけ乗るだけ＝落下点も滞空時間も engine のまま動かない
        /// </summary>
        private void BeginBattedBall(float overshoot)
        {
            _battedDur = (float)(_batted.T1 - _batted.T0);
            var head = _batted.At(0.0);
            var contact = BallPointAt(_ballArrival);
            _battedOffset = new Vector3(
                contact.x - (float)head.X, contact.y - (float)head.H, contact.z - (float)head.Y);
            // 打点から地面までの自由落下時間。左右のずれ（±0.4m程度）もこの時間で解消する
            _battedFade = Mathf.Sqrt(2f * Mathf.Max(0.05f, Mathf.Abs(_battedOffset.y)) / 9.8f);
            _battedT = 0f;
            _trail.Clear();
            _ball.SetActive(true);
            UpdateBattedBall(Mathf.Max(0f, overshoot));
        }

        /// <summary>
        /// 打球を進める（フルレート）。守備ビューへ渡る（<see cref="_fieldPlay"/> が立つ）と
        /// <see cref="UpdateBall"/> 側でこの関数自体が呼ばれなくなる＝寿命は「渡るまでの保険」のまま
        /// （issue #398 ①。渡った後の追跡・残像は <see cref="SetFieldTime"/> に一本化）。
        /// </summary>
        private void UpdateBattedBall(float deltaTime)
        {
            _battedT += deltaTime;
            if (_battedT > BattedMaxSeconds) { HideBall(); return; }
            var p = BattedPointAt(_battedT);
            _ball.transform.localPosition = p;
            PushTrailPoint(p, _battedT, _cal.BattedTrailSeconds);
        }

        /// <summary>
        /// 打球の残像に1点積み、尾（<paramref name="trailSeconds"/> より前）を刈る（issue #398 ①）。
        /// <paramref name="now"/> は残像内で共通の時計（対決ビュー＝<see cref="_battedT"/> ／
        /// 守備ビュー＝プレー内の絶対時計）。<see cref="_trailNow"/> のコメントの通り、基準が
        /// 途中で変わっても差分計算だけなので壊れない。
        /// </summary>
        private void PushTrailPoint(Vector3 stageLocalPos, float now, float trailSeconds)
        {
            _trail.Add(new Vector4(stageLocalPos.x, stageLocalPos.y, stageLocalPos.z, now));
            TrimTrail(now, trailSeconds);
        }

        /// <summary>尾より古い点を捨てる（末尾の1点だけは残す＝線が根元で途切れない）。新しい点を積まない
        /// フレームでも呼べば、<paramref name="now"/> を進めるだけで残像が自然に消えていく
        /// （<see cref="_trailNow"/> もここで更新するので、描画側のフェード計算は常にこの時計を追う）。</summary>
        private void TrimTrail(float now, float trailSeconds)
        {
            _trailNow = now;
            float cut = now - Mathf.Max(0.02f, trailSeconds);
            int drop = 0;
            while (drop + 1 < _trail.Count && _trail[drop + 1].w < cut) drop++;
            if (drop > 0) _trail.RemoveRange(0, drop);
        }

        /// <summary>打球の位置（ステージローカル）。engine のセグメント＋打点合わせの補正。</summary>
        private Vector3 BattedPointAt(float t)
        {
            var b = _batted.At(Mathf.Clamp(t, 0f, _battedDur));
            float k = _battedFade > 0f ? Mathf.Max(0f, 1f - t / _battedFade) : 0f;   // 左右・前後は線形に解消
            // 高さは**足さずに、高い方を採る**。engine のプロファイルは h(0)=0 から始まる（滞空も
            // ゴロも同じ）ので、足すとゴロが自由落下と最初のバウンドの合計で 0.78→1.05m へ**上がって**
            // しまう（2026-07-31 実測）。max なら「打点から落ちる → プロファイルが追い越したらそちらへ
            // 乗る」となり、ゴロは落ちて弾み、フライは2コマで弧に乗る。落下点も滞空時間も engine のまま。
            float h = (float)b.H;
            float y = _battedOffset.y > 0f
                ? Mathf.Max(h, _battedOffset.y - 4.9f * t * t)   // 打点から落ちる／追い越されたら弧へ乗る
                : h + _battedOffset.y * k;                       // 既に打点の高さから始まる見本用
            return new Vector3(
                (float)b.X + _battedOffset.x * k,
                y,
                (float)b.Y + _battedOffset.z * k);
        }

        /// <summary>
        /// 対決ビュー側で打球を追跡しておく上限[秒]（保険）。F9 以前は「俯瞰への切替（接触+約0.55s）」
        /// でこれより先に畳まれていたが、現在は <c>CallMomentSeconds = _ballArrival</c>（接触の瞬間）で
        /// 守備ビューへ切り替わるため、通常は1フレームも経たずに <see cref="_fieldPlay"/> が立ち
        /// <see cref="UpdateBall"/> がこちらを呼ばなくなる（issue #398・付随不整合の是正）。
        /// この上限が効くのは、守備ビューへ渡らない異常系（rig 不可・Play なし等）だけ。
        /// </summary>
        private const float BattedMaxSeconds = 1.5f;

        /// <summary>到達後も球を置いておく秒数。捕球・ミートの瞬間を1コマ以上（12fps=83ms）見せるため。</summary>
        private const float BallLingerSeconds = 0.10f;


        /// <summary>
        /// 【開発ツール用】投手を**発射する瞬間の姿勢で静止**させ、球を手に持たせる（issue #332 の出所合わせ）。
        /// 動いている絵で位置を合わせるのは無理なので、止めた状態で <see cref="BallDiagForCapture"/> の
        /// 数値を見ながらスライダを回す＝スクショ往復が要らない。
        /// </summary>
        public void PoseAtBallRelease()
        {
            if (!Available || _pitcher == null) return;
            CancelFade(_pitcher);
            _cutCountdown = -1f;
            _umpireCallCountdown = -1f;
            SeekActor(_pitcher, _cal.BallReleaseClipTime);
            GroundFix(_pitcher);
            _pitcher.Holding = true;   // Tick で進めない（StepActor が Holding で降りる）
            HoldBallInHand();
        }

        /// <summary>
        /// 打球を見せ始めてからの経過[秒]（打球でなければ負）。俯瞰は**この続きから**再生する
        /// ＝対決ビューで見せたぶんを盤面がもう一度なぞらない（#330「投球が二重に見える」と同種）。
        /// </summary>
        public float BattedElapsedSeconds => _battedT;

        /// <summary>
        /// 打球が対決構図のビューポートから出たか（issue #481）。呼び出し側（<c>MatchLiveController</c>）は
        /// 接触後、これが立つまで守備ビューへの切替を据え置く＝「打球の行方を一瞬だけ見せる」
        /// （design-18 §4.5-④）。まだ打球でなければ（見せるものが無ければ）true＝待たせない。
        /// 保険は <see cref="BattedMaxSeconds"/>（1.5s。これより先は <see cref="UpdateBattedBall"/> が
        /// 自分で <see cref="HideBall"/> して畳む＝新しい据え置きは実測0.13〜0.22s程度なので十分長い）。
        /// </summary>
        public bool BattedOutOfFrame
        {
            get
            {
                if (_battedT < 0f || _camera == null) return true;
                var local = BattedPointAt(_battedT);
                var vp = _camera.WorldToViewportPoint(_root.transform.TransformPoint(local));
                bool inFrame = vp.z > 0f && vp.x >= 0f && vp.x <= 1f && vp.y >= 0f && vp.y <= 1f;
                return !inFrame;
            }
        }

        /// <summary>飛翔を打ち切って球を隠す（俯瞰へ切り替わる時＝盤面側が球を描くので二重に見えない。#330）。</summary>
        public void HideBall()
        {
            _ballT = -1f;
            _catcherShiftT = -1f;   // 俯瞰へ渡る＝対決ビューの芝居は打ち切り（体寄せの戻りも止める）
            _ballHeld = false;
            _ballInMitt = false;
            _battedT = -1f;
            _batted = null;
            _trail.Clear();
            _trailNow = -1f;
            if (_ball != null)
            {
                _ball.transform.SetParent(_root.transform, false);
                // ⚠ 見かけ倍率（守備ビューの可読性補正・最大12倍＝直径89cm）を必ず落とす。
                // 引きの絵ほど倍率が上がるので、**プレーの最後は常に最大**で終わる。落とさないと
                // 次の打席の対決ビューで**バランスボール大の球を投げ始める**（2026-08-01 実害）。
                // 隠す所を単一の出口にしてここで戻す＝補正を掛ける側（毎コマ）と対にする。
                _ball.transform.localScale = Vector3.one * BallRealDiameterM;
                _ball.SetActive(false);
            }
            if (_ballShadow != null) _ballShadow.SetActive(false);
        }

        /// <summary>一発物クリップへ差し替えて頭出しする（終了後は Actor.IdleClip へ自動復帰）。</summary>
        private static void SetActorClip(Actor a, AnimationClip clip, float seekTo, float endAt)
        {
            if (a == null || clip == null) return;
            CancelFade(a);
            if (a.Clip != clip)
            {
                if (a.Playable.IsValid()) a.Playable.Destroy();
                a.Playable = AnimationClipPlayable.Create(a.Graph, clip);
                SyncFootIk(a);
                a.Mixer.ConnectInput(1, a.Playable, 0);
                a.Mixer.SetInputWeight(1, 1f);
                a.Clip = clip;
            }
            a.EndAt = endAt;
            a.HoldSeconds = 0f;      // 新しいクリップへの差し替えは保留中のホールド予約を打ち消す
            a.HoldRemaining = 0f;
            SeekActor(a, Mathf.Max(0f, seekTo));
        }

        /// <summary>毎フレーム呼ぶ。ポーズは1/12秒たまるごとに進む（時間量子化）。</summary>
        public void Tick(float deltaTime)
        {
            if (!Available) return;

            // カットバック予約（アンカー同期のプリロール）。量子化と独立にプレー時間で数える
            if (_cutCountdown >= 0f)
            {
                _cutCountdown -= deltaTime;
                if (_cutCountdown < 0f)
                {
                    // フォロースルー姿勢から次球の投球開始位置へ戻る。ここも姿勢差が大きいので
                    // 頭出しだけだと跳ぶ（差し替え前からのハードカット）。同じクロスフェードで繋ぐ
                    CrossfadeActorTo(_pitcher, _cutClipTime, CrossfadeSeconds);
                    GroundFix(_pitcher);
                    HasCutToNextWindup = true;   // 次の1球の振りかぶりへ入った＝采配カードの受付締切
                }
            }

            // 球審ストライクコール予約（ボール到達後に発火）。量子化と独立にプレー時間で数える（#333）
            if (_umpireCallCountdown >= 0f)
            {
                _umpireCallCountdown -= deltaTime;
                if (_umpireCallCountdown < 0f)
                {
                    SetActorClip(_umpire, _pendingUmpireClip, _umpireCallStartAt, _umpireCallEndAt);
                    _umpire.Speed = UmpireCallPlaybackRate;   // SetActorClip の後（差し替えで持ち越さないため）
                }
            }

            // スイングの開始予約（頭出しでは間に合わない速い再生用。issue #368）。
            // 捕球・コールと同じくプレー時間で数える＝12fpsの量子化に丸められない
            if (_swingCountdown >= 0f)
            {
                _swingCountdown -= deltaTime;
                if (_swingCountdown < 0f && _swingPendingClip != null)
                    StartSwing(_swingPendingClip, _swingPendingAnchor, 0f, _swingPendingEndAt,
                        Mathf.Max(0.1f, _cal.SwingSpeed), _swingPendingHold);
            }

            // 捕球クリップの開始予約（球到達に捕球の瞬間を合わせる。issue #332 第2弾）。
            // 球審コールと同じくプレー時間で数える＝12fpsの量子化に丸められない
            if (_catchCountdown >= 0f)
            {
                _catchCountdown -= deltaTime;
                if (_catchCountdown < 0f && _catchIndex >= 0)
                {
                    // 捕球の瞬間（クリップ内 Moment）で打ち切り＋据え置き＝捕った形のまま
                    // 判定表示が消えるまで静止（ScheduleCatch の else 側と同じ作法。片方だけ変えない）
                    SetActorClip(_catcher, _catchClips[_catchIndex], 0f, CatchAnchors[_catchIndex].Moment);
                    _catcher.HoldSeconds = CatcherLingerSeconds(_catchIsStrike);
                    FixCatcherPose();   // 差し替え直後のポーズにも当てる（この同じフレームで球が着くことがある）
                }
            }

            // 後逸クリップの開始予約（issue #525）。捕球と同じくプレー時間で数える。
            // 打ち切り・据え置きは張らない＝抜けた球を追う続きをそのまま見せる（SchedulePassedBall の項）
            if (_passedBallCountdown >= 0f)
            {
                _passedBallCountdown -= deltaTime;
                if (_passedBallCountdown < 0f && _passedBallIndex >= 0)
                    SetActorClip(_catcher, _passedBallClips[_passedBallIndex], 0f, 0f);
            }

            // ボールはフルレート（design-18 §4.5-②「球筋の読みやすさはゲーム性なので量子化しない」）。
            // キャラのコマ打ちループより前に進める＝同フレームのポーズと球位置が同じ時刻を指す
            UpdateBall(deltaTime);

            // 残差の体寄せの時計（球の時計と別。捕球後の戻りは球がミットの中で消えた後まで続く）。
            // 戻り切ったら止める＝以後 ApplyCatcherResidualShift は書かない（従来の「球死後は素のまま」へ復帰）。
            // 猶予0.2sは「重み0の最終書き込みを最低1ポーズコマ確実に通す」ため（12fps=83ms×2＋余裕。
            // 端数で打ち切ると最後の書き込みが重み残りのまま止まり、止め際に数cmの段が出る）
            if (_catcherShiftT >= 0f)
            {
                _catcherShiftT += deltaTime;
                if (_catcherShiftT > _ballArrival + CatcherLingerSeconds(_catchIsStrike) + CatcherShiftReturnSeconds + 0.2f)
                    _catcherShiftT = -1f;
            }

            _acc += deltaTime;
            float step = 1f / PoseFps;
            bool stepped = false;
            while (_acc >= step)
            {
                _acc -= step;
                foreach (var a in _actors) StepActor(a, step);
                stepped = true;
                // ポーズが進んだ回数。**ポーズ補正IKは「進んだ回だけ」当てる**必要があり
                // （重み<1は今の位置からの補間＝二度当てると寄りすぎる）、呼び出し側が
                // 進んだかどうかを知る唯一の手がかりになる（守備の捕球IK＝F5）
                _poseGen++;
            }
            if (stepped)
            {
                GroundFix(_pitcher);   // 投球ループはルートモーションでマウンドを降りるためコマ毎に接地
                // 後逸クリップも**ルートモーション付き**なのでコマ毎に接地する（issue #525 のデグレ修正）。
                // 捕手はそれまで捕球5方向＝全部その場もの（足の上下が実測 0.075〜0.086m＝数mm）しか
                // 使っておらず、接地補正を一度も掛けていなかった。後逸を足した時点でその前提が崩れた:
                //   Passed_Ball_L 当てる瞬間 0.170（基準0.080＝**+0.09m 宙に浮く**）・ピーク 0.231
                //   L_S 0.052/0.309 ／ R 0.124/0.154 ／ R_S 0.169/0.178（いずれも最小 -0.28 前後）
                // 上だけ抑えると今度は足が地面下 0.29m へめり込むので、投手と同じ両方向の接地で解く。
                // 捕球中は掛けない＝#440/#466 で確立した「捕手の高さはルートで動かさない」を保つ
                // （その場ものには不要な補正で、掛けると据え置き中の微差を毎コマ拾う）。
                if (_passedBallIndex >= 0 && _catcher != null && _catcher.Animator != null
                    && _passedBallClips != null && _catcher.Clip == _passedBallClips[_passedBallIndex])
                {
                    GroundFix(_catcher);
                }
                // ポーズ補正は**そのコマでポーズを書き直した体にだけ**当てる（Actor.PoseAdvanced）。
                // 「コマが進んだ」と「ポーズを書き直した」は別物で、据え置き・恒久停止・間引きは
                // 進んでも書き直さない＝相対回転の補正が同じポーズへ積み上がる（issue #368 実害）
                if (_batter != null && _batter.PoseAdvanced)
                    FixBatterPose();   // 収録の破綻を骨へ当て直す（バット整列より前＝詰めた手から軸を採る）
                AlignBat();            // バットは骨から毎コマ組み直す純関数＝二度当てが無いので常に呼ぶ
                if (_catcher != null && _catcher.PoseAdvanced)
                    FixCatcherPose();  // ミットを実着弾点へ寄せる（5方向クリップの残差を吸収）
                SampleBatTrail();      // AlignBat の**後**＝組み上がったバットの端点を採る
            }
        }

        /// <summary>
        /// バットの端点をポーズ1コマぶん積む（<see cref="DrawBatTrail"/> の入力）。
        ///
        /// 積むのは<b>ポーズが進んだコマだけ</b>＝呼び出しは Tick の stepped ブロック1箇所に限る。
        /// フルレートで積むと同じ姿勢が何十個も入り、尾の秒数が「絵の枚数」でなく「フレーム数」で
        /// 決まってしまう（描画側の弧の刻みも二重に掛かる）。
        ///
        /// 出す・出さないは<b>角速度で決める</b>＝時刻の窓を作らない。12fpsの格子が狙った時刻に
        /// 乗るかは頭出しの位相次第で、乗らない結果だけ黙って壊れる（design-18 §4.5-⑩。
        /// 実測: 振り抜きの起点を時刻で焼く実装は空振りだけ一度も焼かれなかった）。
        /// 角度で見れば構えも据え置きも自動的に閾値を割るので、窓が要らない。
        /// </summary>
        private void SampleBatTrail()
        {
            _batTrailT += 1f / PoseFps;
            if (_bat == null || !_bat.activeSelf || _root == null)
            {
                // バットが消える区間（インプレーの走り出し＝BatDropTime）では**積むのを止めるだけ**。
                // 履歴は消さない＝手放した後も尾が残って消えていくのが自然（寿命では捨てる。
                // 捨てないと不可視の四角形を積み続ける）。
                // なお既定値では**この経路で絵は変わらない**: BatDropTime はクリップ2.60秒で、
                // 最後に積まれるコマ（振り抜き＝クリップ1.5前後）から実時間0.5秒以上あるのに対し
                // 尾は0.20秒しかないので、バットが消える頃には尾は消え切っている。
                // 効いてくるのは BatTrailSeconds を大きく伸ばしたときだけ
                _batTrailHasLast = false;
                TrimBatTrail();
                return;
            }

            var tr = _root.transform;
            var axis = (_bat.transform.rotation * Vector3.up).normalized;
            float half = Mathf.Max(0.1f, _cal.BatLengthM) * 0.5f;
            var center = _bat.transform.position;

            // スイング区間の武装/解除（issue #444）。角速度ゲートは構え・据え置きを除外するだけで、
            // **構えへ戻る速い動き**もバットが速く回るため区別できない。武装（_batTrailSwing）は
            // StartSwing で立ち、判定は毎コマ「クリップ時刻が振り抜きの打ち切り（Actor.EndAt＝
            // EffectiveSwingFinishSec、#415/#395 と同じ時刻源）より前か」の状態比較で行う
            // （「進みが届いたコマで一度だけ」型の1コマ副作用にしない＝design-18 §4.5-⑩。
            // 打ち切り後の据え置きは Time==EndAt で止まるので < が自然に外す）。
            // 武装外でも履歴は消さない＝振り終わりの尾は寿命で自然に消える（角速度スキップと同じ作法）。
            bool armed = _batTrailSwing && _batter != null
                && _batter.Time < (_batter.EndAt > 0f ? _batter.EndAt : float.MaxValue);
            if (!armed)
            {
#if UNITY_EDITOR
                // 旧実装（角速度ゲートのみ）なら積んでいたコマ＝ゲートが実際に効いた証拠を数える。
                if (_batTrailHasLast
                    && Vector3.Angle(_batTrailLastAxis, axis) >= Mathf.Max(0f, _cal.BatTrailMinDegPerFrame))
                    BatTrailSuppressedForCapture++;
#endif
                // 軸は追従させておく（再武装した最初のコマの角速度判定を直前コマ比で行うため）
                _batTrailLastAxis = axis;
                _batTrailHasLast = true;
                TrimBatTrail();
                return;
            }

            // 前サンプルからの回転が小さいコマは積まない（構え・据え置き）。
            // 「積まない」だけで履歴は保つので、振り終わりの尾はそのまま寿命で消えていく
            if (_batTrailHasLast
                && Vector3.Angle(_batTrailLastAxis, axis) < Mathf.Max(0f, _cal.BatTrailMinDegPerFrame))
            {
                TrimBatTrail();
                return;
            }
            _batTrailLastAxis = axis;
            _batTrailHasLast = true;
#if UNITY_EDITOR
            BatTrailSampledForCapture++;
#endif

            // ステージローカルで持つ（_trail と同じ。ステージは y=-500 へ退避しているので世界座標では持てない）
            var knob = tr.InverseTransformPoint(center - axis * half);
            var tip = tr.InverseTransformPoint(center + axis * half);
            _batTrailKnob.Add(new Vector4(knob.x, knob.y, knob.z, _batTrailT));
            _batTrailTip.Add(new Vector4(tip.x, tip.y, tip.z, _batTrailT));
            TrimBatTrail();
        }

        /// <summary>寿命を過ぎたサンプルを捨てる（両端は必ず同数・同時刻で動かす）。</summary>
        private void TrimBatTrail()
        {
            float cut = _batTrailT - Mathf.Max(0.02f, _cal.BatTrailSeconds);
            int drop = 0;
            while (drop < _batTrailKnob.Count && _batTrailKnob[drop].w < cut) drop++;
            if (drop <= 0) return;
            _batTrailKnob.RemoveRange(0, drop);
            _batTrailTip.RemoveRange(0, drop);
        }

        /// <summary>バットの軌跡を捨てる（打席の仕切り直し）。</summary>
        private void ClearBatTrail()
        {
            _batTrailKnob.Clear();
            _batTrailTip.Clear();
            _batTrailHasLast = false;
        }

        /// <summary>
        /// 捕球の瞬間の前後だけミットを球へ寄せる（design-18 §4.5「端数合わせは短時間IK」）。
        /// 狙点は**発射時に確定した捕球面での球位置**＝毎コマ球を追いかけない。ポーズは12fpsなので
        /// 球の現在位置を追うと1コマ（83ms＝球は3m進む）ぶん手前を掴もうとしてぶれる。
        ///
        /// 呼ぶのは**捕手のポーズを評価した直後だけ**（ポーズコマの更新後・クリップ差し替え直後）。
        /// 重み&lt;1のときは今の手の位置からの補間なので、同じポーズに二度当てると寄りすぎる。
        /// </summary>
        private void FixCatcherPose()
        {
            if (_catcher == null || _catcher.Animator == null) return;
            if (_catchIndex < 0) return;
            // 体寄せは専用の時計（_catcherShiftT）で駆動＝球がミットの中で消えた後も戻り切るまで焼き直す。
            // ミットIK（下）は球の時計のまま＝球が生きている間だけ実着弾点を掴む
            ApplyCatcherResidualShift();
            if (_ballT < 0f) return;
            float w = DuelCatcherPose.Weight(_ballT, _ballArrival);
            if (w <= 0f) return;
            DuelCatcherPose.ReachMitt(_catcher.Animator,
                _root.transform.TransformPoint(_ballCatchPoint), w);
        }

        /// <summary>
        /// リリース後、残差（実着弾−狙い＝コントロール誤差そのもの）の<b>左右成分だけ</b>捕手の体を
        /// 寄せる（issue #396 ②）。腕＋胴のIK（<see cref="DuelCatcherPose.ReachMitt"/>）だけでは
        /// 大きな横外れへ届かないため、体そのものを先に寄せておく＝実物の横っ跳び。
        /// 高さはルートで動かさない（#440/#466。脚IKが無いリグでYを平行移動すると
        /// めり込み・宙浮きになる）＝クリップ選択＋ミットIK＋胴アシスト側で吸収する。
        ///
        /// 駆動は<b>専用の時計</b>（<see cref="_catcherShiftT"/>。リリースからの経過秒＝フルレート・単調。
        /// 球の時計 <see cref="_ballT"/> はミットの中で消えると-1になり、捕球後の戻りを駆動できない）で、
        /// クリップ内時刻では駆動しない（design-18 §4.5-③の掟。捕球クリップの差し替えコマに
        /// 引っかかると重みが飛ぶ）。毎回<see cref="Actor.BasePos"/>からの絶対オフセットとして焼き直す
        /// ＝クリップ差し替え直後（<see cref="RewindActor"/>が位置をBasePosへ戻した直後）に呼ばれても
        /// 正しく乗る。捕球クリップは「立ち位置＋固定のミット相対位置」で実測してある
        /// （<see cref="CatchAnchors"/>）＝ルートモーションはそもそも無視できる前提のクリップなので、
        /// 絶対オフセットで焼いても収録の動きを消さない。
        /// </summary>
        private void ApplyCatcherResidualShift()
        {
            if (_catcherShiftT < 0f) return;   // 時計が止まっている＝寄せも戻りも完了（素の位置のまま触らない）
            float w = CatcherShiftWeight(_catcherShiftT, _ballArrival, CatcherLingerSeconds(_catchIsStrike));
            // 高さの残差はルートで吸収しない（issue #440 / #466）。残差はσ=(30−0.22×Control)cm＝
            // 制球の悪い投手では0.2〜0.4m級で、そのままYの平行移動にすると
            //   下外れ → 体ごと地面へめり込む（#440）／上外れ → 足ごと宙に浮く（#466。
            //   #464 が捕球後の据え置きを入れてから約1秒静止で見えるようになった）。
            // 実物の捕手は脚で沈む・伸びるが、このリグに脚IKは無い＝高さは捕球クリップ選択
            // （SelectCatchAnchor）とミットIK（DuelCatcherPose.ReachMitt。胴アシストは下方向だけ45°）に
            // 全部任せ、ルートの体寄せは左右（横っ跳び）専用にする。上クランプで高めのミットずれは
            // 0.020m（CatcherClampShooter 実測 2026-08-04）＝腕＋胴14°で届く。伸び上がりの上乗せは不要。
            var offset = new Vector3(_catcherResidual.x * w, 0f, 0f);
            _catcher.Go.transform.localPosition = _catcher.BasePos + offset;
        }

        /// <summary>
        /// 残差の体寄せの台形重み。<paramref name="ballTime"/> はリリースからの経過秒（0起点。
        /// <see cref="_catcherShiftT"/>＝球より長生きする専用の時計）、<paramref name="settleAt"/> は
        /// 球がミット面へ届く時刻（<see cref="_ballArrival"/>）、<paramref name="holdSeconds"/> は
        /// 捕球後の据え置き窓（<see cref="CatcherLingerSeconds(bool)"/>＝判定表示が消えるまで）。
        /// リリースから<see cref="CatcherShiftLeadSeconds"/>かけて寄り切り、**据え置き窓の間は動かず**、
        /// 判定表示が消えてから<see cref="CatcherShiftReturnSeconds"/>かけて狙いの構え（残差ゼロ）へ戻る。
        /// 捕球の瞬間から戻すと、ミットの子になった球ごと体が中央へスライドして
        /// 「全身でフレーミング」に見える（監督報告 2026-08-04。大外れほど瞬間移動に見える）。
        /// </summary>
        private static float CatcherShiftWeight(float ballTime, float settleAt, float holdSeconds)
        {
            if (ballTime <= 0f) return 0f;
            if (ballTime < CatcherShiftLeadSeconds) return Mathf.InverseLerp(0f, CatcherShiftLeadSeconds, ballTime);
            float holdUntil = settleAt + holdSeconds;
            if (ballTime <= holdUntil) return 1f;
            return 1f - Mathf.InverseLerp(holdUntil, holdUntil + CatcherShiftReturnSeconds, ballTime);
        }

        /// <summary>残差の体寄せがフルに乗るまでの時間[s]（issue #396 ②「リリース後0.2〜0.3秒かけて」）。</summary>
        private const float CatcherShiftLeadSeconds = 0.25f;

        /// <summary>
        /// 1球ごとの判定オーバーレイ（ストライク/ボール等）の表示時間[s]。正典は MatchLiveController の
        /// <c>pitchCallHoldSeconds</c>（直列化）で、<c>NewDuelRig</c> が生成時にここへ流し込む＝
        /// この既定値は撮影ハーネス・キャリブレーション等、リグを単体で組む場面のフォールバック。
        /// 表示の長さと捕手の据え置き（<see cref="CatcherLingerSeconds(bool)"/>）を同じ値から出すための入口
        /// なので、コントローラ側だけ・ここだけを変えない。
        /// </summary>
        public float PitchCallHoldSeconds { get; set; } = 0.85f;

        /// <summary>
        /// 捕球からの捕手の据え置き窓[s]＝判定表示が消えるまで。<paramref name="isStrike"/>（見逃し・空振り＝
        /// 球審のコールの絵がある）は「コールの間（<see cref="UmpireCallReactSeconds"/>＋
        /// <see cref="UmpireCallAnchorSeconds"/>÷<see cref="UmpireCallPlaybackRate"/>）＋表示
        /// <see cref="PitchCallHoldSeconds"/>」。ボール（コールの絵が無い＝issue #527 C。
        /// <see cref="CallMomentSeconds"/>と同じ理由でコールの間を待たない）は「反応
        /// <see cref="UmpireCallReactSeconds"/>＋表示<see cref="PitchCallHoldSeconds"/>」。
        /// どちらも「テロップが出てから消えるまで」の意味を保つ＝<see cref="CallMomentSeconds"/>と対で動かす。
        /// この間、捕手は捕った形のまま動かない（捕球クリップの据え置き＋体寄せの据え置き）し、球もミットの中に
        /// 見せ続ける（監督指示 2026-08-05「判定表示が消えるまで捕球位置から動かない」）。この3つ（コール・
        /// 体寄せ・球の据え置き）を同じ窓に縛るのは #464 の教訓＝球が見えている間に体が動くと、ミットの子の
        /// 球ごと中央へ引き戻す「全身フレーミング」になる。片方だけ変えない。
        /// </summary>
        private float CatcherLingerSeconds(bool isStrike)
            => isStrike
                ? UmpireCallReactSeconds + UmpireCallAnchorSeconds / UmpireCallPlaybackRate + PitchCallHoldSeconds
                : UmpireCallReactSeconds + PitchCallHoldSeconds;

        /// <summary>球が消えてから狙いの構え（基準位置）へ戻るまでの時間[s]。12fpsで5〜6コマ＝
        /// 捕った所からすっと構え直す速さ。短くすると大外れの球で瞬間移動に見える（監督報告 2026-08-04）。</summary>
        private const float CatcherShiftReturnSeconds = 0.45f;

        /// <summary>
        /// RTをカラム実寸由来のサイズへ張り替える（DuelViewElementから）。同寸なら何もしない。
        /// 縦横比はカラム追従だが、極端な縦長/横長は構図が壊れるためクランプする。
        /// 水平視野角は基準（16:9で垂直2.9°）固定＝縦に長い分は上下に描画範囲が広がるだけで構図は切れない。
        /// </summary>
        // ---- 開発ツール（撮影ハーネス・打撃フォーム確認窓）用の入口 ----
        // 実機の絵作りは常にこのrigを通す＝ツールと本番でポーズ・装備がずれない（design-18 §3.3の教訓）。

        /// <summary>Edit modeで1枚描く。Play中はカメラが自動で回るので不要。</summary>
        public void RenderNow()
        {
            // Edit modeの単発描画はスキンが**前のポーズのまま**焼かれる（骨は動いているのに絵が変わらない
            // ＝つまみが1手遅れて見える。2026-07-29 実害: 指の握り込みが絵に出なかった）。
            // 描画ごとにスキン行列を計算し直させる。Playのループ描画では起きないのでここだけで足りる
            // RTが外から破棄されていたら作り直す（無いまま描くと絵が出ないだけで例外も出ない）
            if (Available && Target == null) EnsureSize(_rtW, _rtH);
            if (!_forcedSkinUpdate && _root != null)
            {
                var smrs = _root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                for (int i = 0; i < smrs.Length; i++) smrs[i].forceMatrixRecalculationPerRender = true;
                _forcedSkinUpdate = true;
            }
            if (_camera != null) _camera.Render();
        }

        private bool _forcedSkinUpdate;

        /// <summary>打者のAnimator（骨を実測するため）。</summary>
        public Animator BatterAnimator => _batter != null ? _batter.Animator : null;

        /// <summary>ステージ根（FramePreview はここのローカル座標で受ける）。</summary>
        public Transform StageRoot => _root != null ? _root.transform : null;

        /// <summary>打者のクリップ内時刻[秒]（構え0〜スイング〜走り出し）。</summary>
        public float BatterClipTime => _batter != null ? _batter.Time : 0f;

        /// <summary>打者のクリップ長[秒]。</summary>
        public float BatterClipLength => _batter != null && _batter.Clip != null ? _batter.Clip.length : 0f;

        /// <summary>打者を指定時刻へ頭出しする（フォーム確認窓のコマ送り・コマ戻し）。</summary>
        public void SeekBatter(float clipTime)
        {
            if (_batter == null) return;
            SeekActor(_batter, clipTime);
            FixBatterPose();
            AlignBat();
        }

        /// <summary>投手の頭（打者の視線の狙点）。</summary>
        public Vector3 PitcherHeadPoint
        {
            get
            {
                var h = _pitcher != null && _pitcher.Animator != null
                    ? _pitcher.Animator.GetBoneTransform(HumanBodyBones.Head) : null;
                return h != null ? h.position
                    : (_root != null ? _root.transform.position : Vector3.zero)
                      + new Vector3(0f, 1.6f, MoundDistance);
            }
        }

        /// <summary>
        /// 打撃フォームの実測値（目測で判定しないため。design-18 §3.3）。
        /// 手首間隔[m] / 左右の手首の曲げ[度]（180=まっすぐ）/ 拳の向きの左右差[度] /
        /// 顔→投手のずれ[度] / 視線の仰角[度] / 前腕から見た左肘の向き[度]。
        /// </summary>
        public string BatterPoseDiag()
        {
            var a = BatterAnimator;
            if (a == null || _batter == null) return "-";
            var lh = a.GetBoneTransform(HumanBodyBones.LeftHand);
            var rh = a.GetBoneTransform(HumanBodyBones.RightHand);
            var ls = a.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            var le = a.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            var head = a.GetBoneTransform(HumanBodyBones.Head);
            if (lh == null || rh == null || ls == null || le == null || head == null) return "-";
            var face = head.TransformDirection(_batter.HeadFwdLocal);
            var toP = PitcherHeadPoint - head.position;
            var faceH = new Vector3(face.x, 0f, face.z).normalized;
            var toPH = new Vector3(toP.x, 0f, toP.z).normalized;
            var axis = (lh.position - ls.position).normalized;
            var pole = Vector3.ProjectOnPlane(le.position - ls.position, axis).normalized;
            var down = Vector3.ProjectOnPlane(Vector3.down, axis).normalized;
            var lk = a.GetBoneTransform(HumanBodyBones.LeftMiddleProximal);
            var rk = a.GetBoneTransform(HumanBodyBones.RightMiddleProximal);
            var re = a.GetBoneTransform(HumanBodyBones.RightLowerArm);
            float lFlex = 0f, rFlex = 0f, knuckleGap = 0f, lPalm = 0f, rPalm = 0f;
            if (lk != null && rk != null && re != null)
            {
                lFlex = 180f - Vector3.Angle(lh.position - le.position, lk.position - lh.position);
                rFlex = 180f - Vector3.Angle(rh.position - re.position, rk.position - rh.position);
                knuckleGap = Vector3.Angle(lk.position - lh.position, rk.position - rh.position);
                // 掌の法線と握り線の成す角（90°=掌がバットを巻いている）。ひねりの効きはここでしか見えない
                var grip = (lh.position - rh.position).normalized;
                lPalm = PalmAngle(a, lh, lk, grip, HumanBodyBones.LeftIndexProximal,
                    HumanBodyBones.LeftLittleProximal);
                rPalm = PalmAngle(a, rh, rk, grip, HumanBodyBones.RightIndexProximal,
                    HumanBodyBones.RightLittleProximal);
            }
            return string.Format(
                "握り: 間隔 {0:F3}m ／ 手首 左{1:F0}° 右{2:F0}° ／ 拳差 {3:F0}° ／ 掌⊥線 左{4:F0}° 右{5:F0}°（90=巻けている）\n"
                + "頭・腕: 顔→投手 {6:F1}° ／ 仰角 {7:F1}° ／ 左肘 {8:F0}°",
                Vector3.Distance(lh.position, rh.position), lFlex, rFlex, knuckleGap, lPalm, rPalm,
                Vector3.SignedAngle(faceH, toPH, Vector3.up),
                Mathf.Asin(Mathf.Clamp(face.y, -1f, 1f)) * Mathf.Rad2Deg,
                Vector3.SignedAngle(down, pole, axis));
        }

        private static float PalmAngle(Animator a, Transform hand, Transform knuckle, Vector3 grip,
            HumanBodyBones index, HumanBodyBones little)
        {
            var ix = a.GetBoneTransform(index);
            var li = a.GetBoneTransform(little);
            if (ix == null || li == null) return 0f;
            var n = Vector3.Cross(knuckle.position - hand.position, ix.position - li.position).normalized;
            return n.sqrMagnitude < 0.5f ? 0f : Vector3.Angle(n, grip);
        }

        public void EnsureSize(int width, int height)
        {
            if (!Available) return;
            width = Mathf.Clamp(width, 160, 2048);
            height = Mathf.Clamp(height, 90, 2048);
            _rtW = width; _rtH = height;   // 破棄されたRTを作り直すときの寸法（下の RenderNow）
            // 縦横比クランプ: 4:3より縦長・2.4:1より横長にはしない（壁の高さ・構図の担保範囲）。
            // ただし横の上限は**試合の構図を守るため**のものなので、FramePreview で構図を組み直した
            // プレビューには効かせない（効かせると欄の右端に緑の帯が残る。2026-07-29 実測）
            height = Mathf.Min(height, Mathf.FloorToInt(width * 0.75f));
            width = Mathf.Min(width, Mathf.FloorToInt(height * (_previewFrameH > 0f ? 8f : 2.4f)));
            if (Target != null && Target.width == width && Target.height == height) return;

            if (_camera != null) _camera.targetTexture = null;
            if (Target != null) { Target.Release(); Object.DestroyImmediate(Target); }
            Target = CreateRt(width, height);
            if (_colorRt != null)
            {
                _colorRt.Release(); Object.DestroyImmediate(_colorRt);
                _colorRt = CreateRt(width, height);   // ポスト前RTはTargetと常に同寸
            }
            if (_camera != null)
            {
                _camera.targetTexture = _colorRt != null ? _colorRt : Target;
                ApplyFov();
            }
        }

        /// <summary>ステージカメラの描画完了直後にポスタライズを当てて Target へ落とす（毎フレーム）。</summary>
        private void OnEndCameraRendering(UnityEngine.Rendering.ScriptableRenderContext ctx, Camera cam)
        {
            if (cam != _camera || _postMat == null || _colorRt == null || Target == null) return;
            if (PosterizeLevels < 2f)
            {
                Graphics.Blit(_colorRt, Target);
                DrawStrikeZone();
                DrawBatTrail();
                DrawBattedTrail();
                return;
            }
            _postMat.SetFloat("_PosterLevels", PosterizeLevels);
            Graphics.Blit(_colorRt, Target, _postMat, 0);
            DrawStrikeZone();   // ポスタライズの**後**。前に置くと薄さが量子化に潰される（下の説明）
            DrawBatTrail();     // 打球の残像より**先**＝接触の瞬間は打球の線を上に出したい
            DrawBattedTrail();
        }

        /// <summary>
        /// 判定に使われている実際のゾーン（<c>engine</c> の <see cref="KokoSim.Engine.Match.Field.StrikeZone"/>）。
        /// 表示だけ別の寸法を持つと「絵が嘘をつく」ので、寸法はここから採って較正には持たない。
        /// engine 側は全打者共通の固定矩形（打者ごとの身長差は入っていない）＝表示もそれに従う。
        /// </summary>
        private static readonly KokoSim.Engine.Match.Field.StrikeZone Zone = new();

        /// <summary>
        /// ストライクゾーンの枠（と、ごく薄い面）を本塁面 z=0 に重ねる。
        ///
        /// **ポスタライズの後に GL で直接引く**。ポスト前にシーン内の半透明として置くと、6段の量子化が
        /// 「同じ段に丸められて消える」か「まるごと1段ずれてベタ板になる」かの二択になり、
        /// 「うっすら」が絵として存在できない（2026-07-31）。ここなら薄いアルファがそのまま出る。
        ///
        /// 太さは画素でなく**実寸[m]**で持つ＝遠近でちゃんと細くなるし、RTの寸法を変えても比が崩れない
        /// （3Dの構図は画角でなく実寸で指定する、と同じ理由。design-18 §3.5.1）。
        /// </summary>
        private void DrawStrikeZone()
        {
            if (_cal.ZoneShow < 0.5f || _root == null || _camera == null) return;
            if (_zoneMat == null)
            {
                var sh = Shader.Find("KokoSim/DuelStageZone");
                if (sh == null) return;
                _zoneMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            }

            float hw = (float)Zone.HalfWidthM;
            float bot = (float)Zone.BottomM;
            float top = (float)Zone.TopM;
            float t = Mathf.Clamp(_cal.ZoneLineM, 0.002f, Mathf.Min(hw, (top - bot) * 0.5f));
            var tr = _root.transform;

            // 色はリニア空間へ自分で落とす（掟: 配列・即時モードは自動変換が効かない）。アルファは変換しない
            var baseCol = _cal.ZoneColor;
            var line = baseCol.linear; line.a = Mathf.Clamp01(_cal.ZoneLineAlpha);
            var fill = baseCol.linear; fill.a = Mathf.Clamp01(_cal.ZoneFillAlpha);

            var prev = RenderTexture.active;
            RenderTexture.active = Target;
            GL.PushMatrix();
            // 射影は**カメラの物をそのまま**渡す。GL.LoadProjectionMatrix が platform 変換を内部で
            // 掛けるので、GL.GetGPUProjectionMatrix を通すと二重変換になり上下が反転する。
            // ゾーンは画面の縦中央（注視点の高さ＝0.8m）に居るため反転しても4px程度しか動かず絵で
            // 気づけない＝打球の残像を足して初めて露見した（2026-07-31）
            GL.LoadProjectionMatrix(_camera.projectionMatrix);
            GL.modelview = _camera.worldToCameraMatrix;
            _zoneMat.SetPass(0);
            GL.Begin(GL.QUADS);
            if (fill.a > 0.001f)
            {
                GL.Color(fill);
                ZoneQuad(tr, -hw, bot, hw, top);
            }
            if (line.a > 0.001f)
            {
                GL.Color(line);
                ZoneQuad(tr, -hw, top - t, hw, top);   // 上
                ZoneQuad(tr, -hw, bot, hw, bot + t);   // 下
                ZoneQuad(tr, -hw, bot, -hw + t, top);  // 左（三塁側）
                ZoneQuad(tr, hw - t, bot, hw, top);    // 右（一塁側）
            }
            GL.End();
            GL.PopMatrix();
            RenderTexture.active = prev;
        }

        /// <summary>
        /// 打球の残像。ゾーンと同じくポスタライズの後に GL で引く。
        ///
        /// **線は世界の中に置く**（画面に貼らない）＝カメラから遠い所は細く、手前へ来るほど太い。
        /// だから球が枠の外へ抜けても、根元（打点のあたり）の線は枠に残って消えていく＝
        /// 「上へ急な線＝フライ／低く横へ＝ゴロ・ライナー」が3〜6コマでも読める。
        ///
        /// GL.LINES は太さを持てない（1画素固定＝遠近が出ず、望遠では点線に割れる）ので、
        /// 隣り合う点のあいだをカメラの方を向いた四角形で埋める。頭が太く濃く、尾へ向けて0まで絞る。
        /// </summary>
        private void DrawBattedTrail()
        {
            if (_cal.BattedShow < 0.5f || _trailNow < 0f || _trail.Count < 2) return;
            if (_root == null || _camera == null) return;
            if (_zoneMat == null)
            {
                var sh = Shader.Find("KokoSim/DuelStageZone");
                if (sh == null) return;
                _zoneMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            }

            // 守備ビュー（引きの構図）は対決ビューより数十倍広い実寸を映すので、対決ビュー向けに
            // 詰めた尾の長さ・太さ（0.28s/0.045m＝本塁付近で約10px）のままだと1px未満の点になる
            // （issue #398 ①）。フィールド専用の値へ差し替える（FieldMaxZoom 等と同じ「絵で決める」枠）
            bool inField = _fieldPlay != null;
            float span = Mathf.Max(0.02f, inField ? FieldBattedTrailSeconds : _cal.BattedTrailSeconds);
            float halfW = Mathf.Max(0.001f, inField ? FieldBattedTrailWidthM : _cal.BattedTrailWidthM) * 0.5f;
            // 色はリニア空間へ自分で落とす（掟: 即時モードは自動変換が効かない）。アルファは変換しない
            var baseCol = _cal.BattedTrailColor.linear;
            float headA = Mathf.Clamp01(inField ? FieldBattedTrailAlpha : _cal.BattedTrailAlpha);
            var tr = _root.transform;
            var eye = _camera.transform.position;

            var prev = RenderTexture.active;
            RenderTexture.active = Target;
            GL.PushMatrix();
            // 射影は**カメラの物をそのまま**渡す。GL.LoadProjectionMatrix が platform 変換を内部で
            // 掛けるので、GL.GetGPUProjectionMatrix を通すと二重変換になり上下が反転する。
            // ゾーンは画面の縦中央（注視点の高さ＝0.8m）に居るため反転しても4px程度しか動かず絵で
            // 気づけない＝打球の残像を足して初めて露見した（2026-07-31）
            GL.LoadProjectionMatrix(_camera.projectionMatrix);
            GL.modelview = _camera.worldToCameraMatrix;
            _zoneMat.SetPass(0);
            GL.Begin(GL.QUADS);
            for (int i = 0; i + 1 < _trail.Count; i++)
            {
                var a = tr.TransformPoint(new Vector3(_trail[i].x, _trail[i].y, _trail[i].z));
                var b = tr.TransformPoint(new Vector3(_trail[i + 1].x, _trail[i + 1].y, _trail[i + 1].z));
                var dir = b - a;
                if (dir.sqrMagnitude < 1e-10f) continue;
                // 視線と線分の両方に直交する向き＝どの角度から見ても同じ太さに見える
                var side = Vector3.Cross(dir, a - eye);
                if (side.sqrMagnitude < 1e-10f) continue;
                side = side.normalized;

                float ua = Mathf.Clamp01(1f - (_trailNow - _trail[i].w) / span);       // 0=尾 / 1=頭
                float ub = Mathf.Clamp01(1f - (_trailNow - _trail[i + 1].w) / span);
                var ca = baseCol; ca.a = headA * ua * ua;   // 濃さは二乗で落とす＝尾が長く伸びて見える
                var cb = baseCol; cb.a = headA * ub * ub;
                var sa = side * (halfW * ua);
                var sb = side * (halfW * ub);
                GL.Color(ca); GL.Vertex(a - sa);
                GL.Color(ca); GL.Vertex(a + sa);
                GL.Color(cb); GL.Vertex(b + sb);
                GL.Color(cb); GL.Vertex(b - sb);
            }
            GL.End();
            GL.PopMatrix();
            RenderTexture.active = prev;
        }

        /// <summary>
        /// バットの軌跡。ゾーン・打球残像と同じくポスタライズの後に GL で引く。
        ///
        /// <b>なぜ要るか</b>: ポーズは12fpsで、スイングのピーク区間は隣り合う2枚の絵の間で手が
        /// 175°回る（2026-08-01 実測）。これは「カクカク」ではなく回転方向が判別できない領域
        /// （ワゴンホイール効果）で、実際バットが1〜2コマ消えて見える。SwingSpeed を上げても
        /// サンプル数が減るだけで悪化する（100→124°/コマ）。手描きアニメが速い動きを12fpsで
        /// 見せる流儀（ブレ絵＋動線）に倣い、<b>コマ間をバットが掃いた面と先端の線で埋める</b>。
        ///
        /// <b>弧の再構成</b>: サンプル間を1枚の四角形で結ぶと、175°の区間では扇が粗すぎて
        /// 「円盤」に見える。バットは<b>剛体の棒</b>なので、中間姿勢は骨を評価し直さずに
        /// 幾何だけで復元できる（軸を Slerp・握り点を Lerp）＝体は12fpsのまま、掃いた面だけが
        /// 滑らかな弧になる。追加のポーズ評価はゼロ。
        ///
        /// 面と線で役割を分ける: <b>面＝速さ</b>（薄く。濃いと円盤に見える）／
        /// <b>先端の線＝軌道の形</b>（濃く。太さは実寸[m]＝遠近で細くなる）。
        /// </summary>
        private void DrawBatTrail()
        {
            if (_cal.BatTrailShow < 0.5f || _batTrailKnob.Count < 2) return;
            if (_root == null || _camera == null) return;
            if (_zoneMat == null)
            {
                var sh = Shader.Find("KokoSim/DuelStageZone");
                if (sh == null) return;
                _zoneMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            }

            float span = Mathf.Max(0.02f, _cal.BatTrailSeconds);
            float arcStep = Mathf.Clamp(_cal.BatTrailArcStepDeg, 1f, 90f);
            // 色はリニア空間へ自分で落とす（掟: 即時モードは自動変換が効かない）。アルファは変換しない
            var baseCol = _cal.BatTrailColor.linear;
            float fillA = Mathf.Clamp01(_cal.BatTrailFillAlpha);
            float edgeA = Mathf.Clamp01(_cal.BatTrailEdgeAlpha);
            float edgeHalf = Mathf.Max(0.001f, _cal.BatTrailEdgeWidthM) * 0.5f;
            var tr = _root.transform;
            var eye = _camera.transform.position;

            var prev = RenderTexture.active;
            RenderTexture.active = Target;
            GL.PushMatrix();
            // 射影は**カメラの物をそのまま**渡す（GL.GetGPUProjectionMatrix を通すと二重変換で上下反転。
            // DrawStrikeZone の項に経緯）
            GL.LoadProjectionMatrix(_camera.projectionMatrix);
            GL.modelview = _camera.worldToCameraMatrix;
            _zoneMat.SetPass(0);

            GL.Begin(GL.QUADS);
            for (int i = 0; i + 1 < _batTrailKnob.Count; i++)
            {
                var k0 = tr.TransformPoint(V3(_batTrailKnob[i]));
                var t0 = tr.TransformPoint(V3(_batTrailTip[i]));
                var k1 = tr.TransformPoint(V3(_batTrailKnob[i + 1]));
                var t1 = tr.TransformPoint(V3(_batTrailTip[i + 1]));
                float u0 = Mathf.Clamp01(1f - (_batTrailT - _batTrailKnob[i].w) / span);       // 0=尾 / 1=頭
                float u1 = Mathf.Clamp01(1f - (_batTrailT - _batTrailKnob[i + 1].w) / span);

                // ---- 剛体としての中間姿勢を復元して弧を滑らかにする ----
                var a0 = (t0 - k0).normalized;
                var a1 = (t1 - k1).normalized;
                float sweep = Vector3.Angle(a0, a1);
                int sub = Mathf.Clamp(Mathf.CeilToInt(sweep / arcStep), 1, 64);
                float half0 = (t0 - k0).magnitude * 0.5f;
                float half1 = (t1 - k1).magnitude * 0.5f;
                var c0 = (k0 + t0) * 0.5f;
                var c1 = (k1 + t1) * 0.5f;

                var pk = k0; var pt = t0; float pu = u0;
                for (int s = 1; s <= sub; s++)
                {
                    float f = (float)s / sub;
                    var ax = Vector3.Slerp(a0, a1, f);
                    var ct = Vector3.Lerp(c0, c1, f);
                    float hl = Mathf.Lerp(half0, half1, f);
                    var nk = ct - ax * hl;
                    var nt = ct + ax * hl;
                    float nu = Mathf.Lerp(u0, u1, f);

                    // 面は「棒が通った四角形」＝ノブ端と先端をそれぞれ繋ぐ
                    var ca = baseCol; ca.a = fillA * pu * pu;   // 濃さは二乗で落とす＝尾が長く伸びて見える
                    var cb = baseCol; cb.a = fillA * nu * nu;
                    GL.Color(ca); GL.Vertex(pk);
                    GL.Color(ca); GL.Vertex(pt);
                    GL.Color(cb); GL.Vertex(nt);
                    GL.Color(cb); GL.Vertex(nk);

                    // 先端の線は打球残像と同じカメラ向きリボン（GL.LINES は1画素固定で望遠では点線に割れる）
                    if (edgeA > 0.001f)
                    {
                        var dir = nt - pt;
                        if (dir.sqrMagnitude > 1e-10f)
                        {
                            var side = Vector3.Cross(dir, pt - eye);
                            if (side.sqrMagnitude > 1e-10f)
                            {
                                side = side.normalized;
                                var ea = baseCol; ea.a = edgeA * pu * pu;
                                var eb = baseCol; eb.a = edgeA * nu * nu;
                                var sa = side * (edgeHalf * pu);
                                var sb = side * (edgeHalf * nu);
                                GL.Color(ea); GL.Vertex(pt - sa);
                                GL.Color(ea); GL.Vertex(pt + sa);
                                GL.Color(eb); GL.Vertex(nt + sb);
                                GL.Color(eb); GL.Vertex(nt - sb);
                            }
                        }
                    }
                    pk = nk; pt = nt; pu = nu;
                }
            }
            GL.End();
            GL.PopMatrix();
            RenderTexture.active = prev;
        }

        private static Vector3 V3(Vector4 v) => new Vector3(v.x, v.y, v.z);

        /// <summary>本塁面（ステージローカル z=0）の矩形を1枚のQUADとして積む。</summary>
        private static void ZoneQuad(Transform tr, float x0, float y0, float x1, float y1)
        {
            GL.Vertex(tr.TransformPoint(new Vector3(x0, y0, 0f)));
            GL.Vertex(tr.TransformPoint(new Vector3(x1, y0, 0f)));
            GL.Vertex(tr.TransformPoint(new Vector3(x1, y1, 0f)));
            GL.Vertex(tr.TransformPoint(new Vector3(x0, y1, 0f)));
        }

        private static RenderTexture CreateRt(int width, int height)
        {
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
            { name = "DuelStageLiveRT", filterMode = FilterMode.Point };
            // ステージ本体（HideAndDontSave）はドメインリロードを跨いで生き残るのに、RTだけ既定だと
            // 破棄されて**絵が消える**（rigは生きているので窓は組み直さず、原因が見えない。2026-07-29 実害）
            rt.hideFlags = HideFlags.HideAndDontSave;
            rt.Create();
            return rt;
        }

        private int _rtW = BaseWidth, _rtH = BaseHeight;

        /// <summary>水平視野角を基準構図（垂直2.9°@16:9）で固定し、現RT縦横比から垂直FOVを逆算する。</summary>
        /// <summary>
        /// 【プレビュー用】1人だけを直立で立たせる（チーム設定画面の配色確認）。
        ///
        /// 立たせるのは<b>投手</b>。打者はヘルメット（暗紺固定）で帽子が見えず、捕手は防具で
        /// ユニが隠れるため、配色の対象（上・ズボン・アンダー・ソックス・帽子）が全部見えるのは投手だけ。
        /// 他の3人とバットは消す＝配色以外の情報を画から追い出す。
        /// クリップは購入パックの直立ループへ差し替える（投球モーションのままだと絵が動きすぎる）。
        /// </summary>
        /// <returns>直立クリップが無くて組めなければ false（呼び元はそのままの絵で続けてよい）。</returns>
        public bool PosePreviewStanding()
        {
            if (!Available || _pitcher == null) return false;
            var clip = LoadClip(OutfielderIdleClipName);
            if (clip == null) return false;

            if (_batter != null) _batter.Go.SetActive(false);
            if (_catcher != null) _catcher.Go.SetActive(false);
            if (_umpire != null) _umpire.Go.SetActive(false);
            if (_bat != null) _bat.SetActive(false);

            _pitcher.HoldAtEnd = false;   // 投球用の「投げ切って止まる」を解除してループさせる
            _pitcher.EndAt = 0f;
            _pitcher.DefaultEndAt = 0f;   // 投球クリップ用の打ち切りもここでは要らない（末尾までループさせる）
            _pitcher.IdleClip = clip;
            _pitcher.IdleEndAt = 0f;
            SwapClip(_pitcher, clip);
            StepActor(_pitcher, 0.001f);
            GroundFix(_pitcher);
            return true;
        }

        /// <summary>再生中のクリップを差し替える（Playable を作り直す。SpawnActor の組み立てと同じ形）。</summary>
        private static void SwapClip(Actor a, AnimationClip clip)
        {
            if (a == null || !a.Graph.IsValid()) return;
            CancelFade(a);
            a.Clip = clip;
            a.Time = 0f;
            a.Holding = false;
            a.HoldRemaining = 0f;
            if (a.Playable.IsValid()) a.Playable.Destroy();
            a.Playable = AnimationClipPlayable.Create(a.Graph, clip);
            SyncFootIk(a);
            a.Mixer.ConnectInput(1, a.Playable, 0);
            a.Mixer.SetInputWeight(1, 1f);
        }

        /// <summary>
        /// 【プレビュー用】カメラを任意の位置・注視点へ置く（ステージ原点ローカル・m）。
        ///
        /// <see cref="ApplyCalibration"/> のカメラは「マウンド後方から本塁を見る」構図に固定されていて
        /// <b>横へ回れない</b>（YawDeg は x をずらすだけ）。打者を正面から見るような別構図が要る
        /// チーム設定画面のために、構図だけを直接指定できる入口を分けてある。
        /// 対決ビュー本体はこれを呼ばない＝§7-1 の構図は不変。
        /// </summary>
        /// <param name="frameHeightM">
        /// 注視点の距離で画面の縦に収めたい実寸[m]。角度でなく寸法で指定するのは、
        /// 欄の縦横比が変わっても被写体の大きさを変えないため（カメラは水平画角を保つ実装なので、
        /// 画角を固定すると横長の欄では上下が切れる）。
        /// </param>
        public void FramePreview(Vector3 camLocalPos, Vector3 lookLocalPos, float frameHeightM)
        {
            if (_camera == null) return;
            _camera.transform.localPosition = camLocalPos;
            var look = _root.transform.position + lookLocalPos;
            _camera.transform.LookAt(look);
            _previewFrameH = frameHeightM;
            _previewDist = Vector3.Distance(_camera.transform.position, look);
            ApplyFov();
        }

        /// <summary>
        /// プレビュー枠を解除して試合の構図（FovDeg）へ戻す。
        /// <see cref="FramePreview"/> で組んだ枠は画角を上書きしたまま残るので、
        /// ApplyCalibration だけ呼んでも構図が戻らない（2026-07-29 実害: 望遠のはずが球場全景になる）。
        ///
        /// **`_fovBaseDeg` 自体の復元が必要（2026-08-02・issue #395）。** <see cref="ApplyFov"/> は
        /// 寄り枠がある間 `_fovBaseDeg` を寄り枠から計算し直して上書きする。旧実装は `_previewFrameH`
        /// を0にするだけで `_fovBaseDeg` を戻さないため、上書きされた広角（実測 約31°）がそのまま
        /// 残っていた（試合の構図は望遠 FovDeg=2.9° なので画角だけ10倍広い＝全景になる）。さらに
        /// <see cref="ApplyCalibration"/> は `_fovBaseDeg = cal.FovDeg` を入れた直後に ApplyFov を呼ぶため、
        /// 寄り枠が残っている状態で先に ApplyCalibration を呼ぶと入れた値がその場で潰される＝呼ぶ順で
        /// 結果が変わっていた。ここで較正値から復元すれば、呼び出し側の順序に依らなくなる。
        /// </summary>
        public void ClearPreviewFrame()
        {
            _previewFrameH = 0f;
            _previewDist = 0f;
            _fovBaseDeg = _cal.FovDeg;
            if (_camera != null) ApplyFov();
        }

        /// <summary>プレビュー枠（縦に収めたい実寸[m]と注視距離[m]）。0＝試合の構図＝FovDeg をそのまま使う。</summary>
        private float _previewFrameH;
        private float _previewDist;

        private void ApplyFov()
        {
            float aspect = Target.width / (float)Target.height;
            if (_previewFrameH > 0f && _previewDist > 0.01f)
            {
                // 収めたい高さ[m]→実効の垂直画角→16:9基準へ戻す（RTが変わるたびに引き直す）
                float halfV = Mathf.Atan(_previewFrameH * 0.5f / _previewDist);
                _fovBaseDeg = 2f * Mathf.Atan(Mathf.Tan(halfV) * aspect / (16f / 9f)) * Mathf.Rad2Deg;
            }
            float halfH = Mathf.Tan(_fovBaseDeg * 0.5f * Mathf.Deg2Rad) * (16f / 9f);
            _camera.fieldOfView = 2f * Mathf.Atan(halfH / aspect) * Mathf.Rad2Deg;
        }

        public void Dispose()
        {
            foreach (var a in _actors)
            {
                if (a.Graph.IsValid()) a.Graph.Destroy();
            }
            _actors.Clear();
            foreach (var m in _materials) if (m != null) Object.DestroyImmediate(m);
            _materials.Clear();
            foreach (var m in _meshes) if (m != null) Object.DestroyImmediate(m);
            _meshes.Clear();
            if (_postMat != null)
            {
                UnityEngine.Rendering.RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
                Object.DestroyImmediate(_postMat); _postMat = null;
            }
            if (_zoneMat != null) { Object.DestroyImmediate(_zoneMat); _zoneMat = null; }
            if (_camera != null) _camera.targetTexture = null;
            if (_colorRt != null) { _colorRt.Release(); Object.DestroyImmediate(_colorRt); _colorRt = null; }
            if (Target != null) { Target.Release(); Object.DestroyImmediate(Target); Target = null; }
            if (_root != null) { Object.DestroyImmediate(_root); _root = null; }
            Available = false;
            if (Live == this) Live = null;
        }

        // ---- ステージ ---------------------------------------------------------

        // ---- 球場の最小追加（S1・打球処理の3D化 F1） ----------------------------
        // 監督決定は「球場は今のまま（最低限）」。ここで足すのは**動きの検証に必要な最小限**だけ:
        //   ① 地面の広さ … 従来は 100m四方・中心z=+9 ＝ z=59m で尽きる。両翼95m・中堅118mの打球が
        //      板の外（背景色）へ抜けて行方が追えない
        //   ② 塁とファウルライン … 無いと「一塁へ送球した」の一塁が画面に存在しない
        // 内野の土・外野フェンス・スタンドは**やらない**（決定どおり）。
        //
        // 対決構図（R-comp60）への非侵襲は幾何で保証してある:
        //   ・地面の拡張ぶん（z<-41）はバックネット書き割り（z=-16・34×8m）の裏＝画面に出ない
        //   ・対決構図で地面が見えるのは z∈[-52,+12] のごく細い帯。塁は最も近い本塁側でも
        //     一塁(19.4,19.4)＝帯の外、二塁(0,38.8)も外
        //   ・ファウルラインは**本塁の土円（半径3.5m）の外から**引くので帯に入らない
        // ＝ F1 の受け入れは「対決構図の画素差分 0」。破れたらこの前提のどれかが崩れている。

        /// <summary>
        /// ファウルラインを引き始める本塁からの距離[m]。
        /// 実物は本塁から引くが、**対決構図（R-comp60）の右下隅に一塁側の線が食い込む**
        /// （5m 始まりで実測63画素・緑地に白＝はっきり見える異物。2026-08-01 A/B画素差分で検出）。
        /// 対決ビューは監督の指示外なので、線は構図の外＝本塁から 8m の所から引く。
        /// 代償は守備カメラで本塁まわり8mだけ線が無いこと（全長95mに対して小さい）。
        /// </summary>
        private const float FoulLineFromM = 8f;
        /// <summary>ファウルラインの長さ[m]（両翼＝ファウルポールまで）。</summary>
        private const float FoulLineToM = 95f;

        private void BuildGround(int layer)
        {
            // 焼いた球場メッシュがあればそれ一本（形の単一ソースは tools/stadium-gen.py）。
            // 芝・内野ダート・ウォーニングトラック・フェンス・塁・ファウルライン・マウンド・本塁プレート・
            // 打席・バックネット・スタンド・スコアボード・照明塔まで全部そちらに入っている。
            // ⚠ 下の手続き生成は**焼き忘れ用のフォールバック**として残してあるだけ。寸法を直すときは
            //   両方に手を入れない＝ tools/stadium-gen.py を直して焼き直す（二重ソースを作らない）。
            if (BuildStadiumMesh(layer)) return;

            // 中堅118m・両翼95mを板の上に収める（旧: scale10・中心z=9 ＝ z∈[-41,59]）。
            // Plane はスケール1で10m四方なので 26 ＝ 260m四方。中心z=+60 で z∈[-70,+190]・x∈[-130,+130]
            Primitive(PrimitiveType.Plane, "ground", new Vector3(0f, 0f, 60f), new Vector3(26f, 1f, 26f),
                new Color(0.30f, 0.55f, 0.30f), layer);
            // フェア芝・刈り分け・内野ダート・ウォーニングトラック・フェンス（S2/S3）。
            // 本塁の土円もここで引き直すので、旧 dirt-home（半径3.5mの円柱）は畳んだ
            BuildFieldSurface(layer);
            Primitive(PrimitiveType.Cylinder, "mound", new Vector3(0f, MoundHeight * 0.5f, MoundDistance),
                new Vector3(5.4f, MoundHeight * 0.5f, 5.4f), new Color(0.63f, 0.47f, 0.33f), layer);
            Primitive(PrimitiveType.Cube, "home-plate", new Vector3(0f, 0.012f, 0f), new Vector3(0.43f, 0.02f, 0.43f),
                Color.white, layer);
            Primitive(PrimitiveType.Cube, "batter-box-L", new Vector3(-0.9f, 0.011f, 0f), new Vector3(1.2f, 0.012f, 1.8f),
                new Color(0.75f, 0.68f, 0.58f), layer);
            Primitive(PrimitiveType.Cube, "batter-box-R", new Vector3(0.9f, 0.011f, 0f), new Vector3(1.2f, 0.012f, 1.8f),
                new Color(0.75f, 0.68f, 0.58f), layer);
            // バックネット書き割り（単色の暗インク壁）。地面プレーンの尽きた先が背景色で抜けるのを塞ぐ。
            // 本実装のスタジアム背景（スタンド/ネット）に置き換わるまでの仮（2026-07-26 構図リフレームD採用時）
            Primitive(PrimitiveType.Cube, "backstop-wall", new Vector3(0f, 4f, -16f), new Vector3(34f, 8f, 0.5f),
                new Color(0.12f, 0.20f, 0.16f), layer);

            BuildBasesAndFoulLines(layer);
        }

        /// <summary>
        /// 塁3枚とファウルライン2本（S1）。座標は engine の共通ソース
        /// <see cref="KokoSim.Engine.Match.Field.FieldDiagramGeometry"/> から採る＝**Unity 側に塁の表を複製しない**
        /// （2D俯瞰・メンバー画面と同じ地理を使う。ずれると「3Dでは一塁に見えるが2Dでは違う所」になる）。
        /// engine は (X=一塁側, Y=センター) の2Dなので、ステージの (x, 0, z=Y) へ写す（+x=一塁側は共通・変換不要）。
        /// </summary>
        private void BuildBasesAndFoulLines(int layer)
        {
            var white = new Color(0.88f, 0.88f, 0.84f);

            // 塁は一辺0.38m。実物の塁は辺がファウルライン（＝45°）に沿うので yaw=45°
            AddBase("base-first", KokoSim.Engine.Match.Field.FieldDiagramGeometry.First, white, layer);
            AddBase("base-second", KokoSim.Engine.Match.Field.FieldDiagramGeometry.Second, white, layer);
            AddBase("base-third", KokoSim.Engine.Match.Field.FieldDiagramGeometry.Third, white, layer);

            // ファウルライン。Cube の長軸は +z なので、yaw=±45° で (±1,0,1)/√2 方向へ倒す。
            // 本塁からの距離 FoulLineFromM〜FoulLineToM を線分の実長として置く（本塁の土円の中は引かない）
            float mid = (FoulLineFromM + FoulLineToM) * 0.5f;
            float len = FoulLineToM - FoulLineFromM;
            float d = mid / Mathf.Sqrt(2f);   // 45°方向に距離 mid 進んだ点の x・z 成分
            Primitive(PrimitiveType.Cube, "foul-line-right", new Vector3(d, 0.010f, d),
                new Vector3(0.12f, 0.02f, len), white, layer, 45f);
            Primitive(PrimitiveType.Cube, "foul-line-left", new Vector3(-d, 0.010f, d),
                new Vector3(0.12f, 0.02f, len), white, layer, -45f);
        }

        private void AddBase(string name, (double X, double Y) p, Color color, int layer)
        {
            Primitive(PrimitiveType.Cube, name, new Vector3((float)p.X, 0.012f, (float)p.Y),
                new Vector3(0.38f, 0.02f, 0.38f), color, layer, 45f);
        }

        /// <param name="yawDeg">Y軸まわりの回転[度]。既定0＝無回転（従来の呼び出しは全部これ）。
        /// ファウルライン・塁のように45°の菱形へ載せる部品で使う。</param>
        private GameObject Primitive(PrimitiveType type, string name, Vector3 localPos, Vector3 localScale, Color color,
            int layer, float yawDeg = 0f)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(_root.transform, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.Euler(0f, yawDeg, 0f);
            go.transform.localScale = localScale;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.GetComponent<MeshRenderer>().sharedMaterial = FlatMaterial(color);
            go.layer = layer;
            return go;
        }

        private Material FlatMaterial(Color color)
        {
            // シーンライト非依存の自前Lambert（Assets/DuelSpike/DuelStageProxyLambert.shader）
            var mat = new Material(Shader.Find("KokoSim/DuelStageProxyLambert")) { hideFlags = HideFlags.HideAndDontSave };
            mat.SetColor("_BaseColor", color);
            _materials.Add(mat);
            return mat;
        }

        /// <summary>着衣メッシュの領域塗り分けマテリアル（_RegionColors は Properties 宣言不可＝配列セット）。</summary>
        private Material RegionMaterial(UniformPalette pal)
        {
            var mat = FlatMaterial(Color.white);
            mat.SetFloat("_UseRegions", 1f);
            ApplyRegionColors(mat, pal, 1f);
            return mat;
        }

        /// <summary>
        /// 領域色と縦縞をマテリアルへ流し込む（生成時と、攻守交代での塗り替え <see cref="SetTeamColors"/> の共通処理）。
        /// </summary>
        /// <param name="objectScale">
        /// アクターのワールドスケール。縞はオブジェクト空間で刻む（ワールドで刻むと体が縞の中を滑る）ので、
        /// データ側の「本/m」をオブジェクト空間の本数へ換算するのに要る。
        /// </param>
        private static void ApplyRegionColors(Material mat, UniformPalette pal, float objectScale)
        {
            var colors = new Color[8];
            colors[(int)DuelUniformMeshBuilder.Region.Shirt] = pal.Shirt;
            colors[(int)DuelUniformMeshBuilder.Region.Pants] = pal.Pants;
            colors[(int)DuelUniformMeshBuilder.Region.Undershirt] = pal.Undershirt;
            colors[(int)DuelUniformMeshBuilder.Region.Socks] = pal.Socks;
            colors[(int)DuelUniformMeshBuilder.Region.Skin] = pal.Skin;
            colors[(int)DuelUniformMeshBuilder.Region.Shoes] = pal.Shoes;
            colors[(int)DuelUniformMeshBuilder.Region.Cap] = pal.Cap;
            colors[(int)DuelUniformMeshBuilder.Region.Belt] = pal.Belt;
            // **ガンマ→リニア変換を自分でやる**。プロジェクトはリニア色空間で、Properties に宣言した
            // Color（_BaseColor・_StripeColor）は SetColor が自動変換してくれるが、_RegionColors は
            // 配列＝Properties に宣言できず SetColorArray が素通しする。素通しすると sRGB値を
            // リニア値として解釈するので**暗い色ほど持ち上がる**（黒 0.10 が画面上は中間グレー）。
            // 縞色だけ自動変換される非対称もここで消える（2026-07-29 監督指摘「黒色が灰色に見える」）
            if (QualitySettings.activeColorSpace == ColorSpace.Linear)
                for (int i = 0; i < colors.Length; i++) colors[i] = colors[i].linear;
            mat.SetColorArray("_RegionColors", colors);

            mat.SetFloat("_StripeOn", pal.StripeOn ? 1f : 0f);
            mat.SetColor("_StripeColor", pal.StripeColor);
            mat.SetFloat("_StripeFreq", pal.StripeFreqPerMeter * objectScale);
            // 線の太さは比率でなく**実寸**で決める（実物のピンストライプは間隔に関係なく3mm前後の細線。
            // 比率固定にすると間隔を広げたとき線まで太くなり、縞でなく色帯になる）
            mat.SetFloat("_StripeDuty",
                Mathf.Clamp(StripeLineWidthM * pal.StripeFreqPerMeter, 0.02f, 0.5f));
        }

        // ---- 素体 -------------------------------------------------------------

        /// <param name="rootMotion">
        /// クリップのルートモーションを transform へ効かせるか。既定 true＝対決4体の現行挙動
        /// （AAクリップは踏み込み・降下をルートモーションで持つので捨てると宙に浮く）。
        /// 守備・走者は位置を engine のタイムラインで置く＝走行ループでは false（その場走り）にし、
        /// ダイビング・スライディング・走り込み捕球の区間だけ true に戻す。
        /// </param>
        /// <param name="tightBounds">
        /// true にすると <c>updateWhenOffscreen</c> を切って固定バウンズで描く。
        /// 既定 false＝実ポーズ追従（現行）。実ポーズ追従は**毎フレーム（60fps）フルスキニングして
        /// バウンズを引き直す**ので、体数が増えるとこれが最大のコストになる。
        /// 箱は ApplyMirror と同じ ±5m の巨大箱（ローカル・素体の足元原点基準）。
        /// </param>
        /// <param name="umpireBody">
        /// true で球審専用の着衣メッシュ（恰幅Stout＋スラックス。<see cref="UmpireMeshAsset"/>）を使う。
        /// U2素体のときだけ意味を持つ（vendorBody とは排他）。
        /// </param>
        private Actor SpawnActor(GameObject bodyPrefab, AnimationClip clip, Vector3 pos, float yawDeg,
            UniformPalette pal, float groundY, int layer, bool vendorBody = false,
            bool rootMotion = true, bool tightBounds = false, bool umpireBody = false)
        {
            var go = Object.Instantiate(bodyPrefab);
            go.name = "body-" + clip.name;
            go.transform.SetParent(_root.transform, false);

            // 領域色で塗れる状態にする。素体2系統で作りが違う:
            //   購入素体 … 帽子・上衣・ズボン等がもともと別メッシュ＝領域IDつきベイク資産へ差し替えるだけ
            //   U2素体   … 素体1枚に着衣メッシュ（DuelUniformBaker のベイク資産）を被せ、目・眉は消す
            //              （顔なし方針。従来は肌と同色マテリアルで偶然隠れていただけ＝領域色化で露出する）
            // どちらも bindposes/boneWeights は原本のままなのでボーン配列・Animatorはそのまま効く
            bool regionReady;
            if (vendorBody)
            {
                regionReady = ApplyVendorRegionMeshes(go);
            }
            else
            {
                var uniformMesh = umpireBody ? LoadUmpireMesh() : LoadUniformMesh();
                foreach (var smr0 in go.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    if (smr0.name == BodyMeshName && uniformMesh != null) smr0.sharedMesh = uniformMesh;
                    else if (smr0.name != BodyMeshName) smr0.gameObject.SetActive(false);
                }
                regionReady = uniformMesh != null;
            }

            var rs = go.GetComponentsInChildren<Renderer>();
            var b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            float scale = TargetHeight / Mathf.Max(0.01f, b.size.y);
            go.transform.localScale = Vector3.one * scale;
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(0f, yawDeg, 0f);

            // UV1の領域IDで塗り分け、領域資産が揃わない時だけ肌色一色へ落とす
            var mat = regionReady ? RegionMaterial(pal) : FlatMaterial(pal.Skin);
            if (regionReady) ApplyRegionColors(mat, pal, scale);   // 縞の刻みはスケール確定後でないと合わない
            foreach (var r in rs) r.sharedMaterial = mat;
            foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (tightBounds)
                {
                    // 固定バウンズ（ApplyMirror と同じ±5m巨大箱＝空間の取り違えを無効化する箱）。
                    // updateWhenOffscreen を切るのは毎フレームのフルスキニングを止めるため
                    smr.updateWhenOffscreen = false;
                    smr.localBounds = new Bounds(Vector3.zero, Vector3.one * 10f);
                }
                else
                {
                    smr.updateWhenOffscreen = true;   // バウンズをバインド箱でなく実ポーズ追従に
                }
            }
            SetLayerRecursive(go, layer);

            var animator = go.GetComponentInChildren<Animator>();
            animator.applyRootMotion = rootMotion; // AAクリップは踏み込み・降下をルートモーションで持つ
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            var actor = new Actor
            {
                Go = go,
                Animator = animator,
                Clip = clip,
                BasePos = go.transform.localPosition,
                BaseRot = go.transform.localRotation,
                GroundY = groundY,
                RegionMat = regionReady ? mat : null,   // 攻守交代の塗り替え（SetTeamColors）用
                BodyScale = scale,
            };
            var lf = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            var rf = animator.GetBoneTransform(HumanBodyBones.RightFoot);
            if (lf != null && rf != null)
                actor.AnkleOffset = Mathf.Min(lf.position.y, rf.position.y) - go.transform.position.y;

            // 頭蓋の上・前方向をボーンローカルで実測（AttachGear用）。クリップ評価前＝素立ちTポーズは
            // 頭が水平・顔がルート正面と保証される唯一のポーズ。頭ボーンのローカル軸は素体依存で
            // 信用できないため、「Tポーズのワールド上向き／ルート正面」を逆変換して持ち、
            // 装着時に現ポーズへ順変換して使う。前方向も実測するのは、視線ターゲットの水平投影で
            // 前を決めると頭が傾いた姿勢で枠がロールし「帽子の斜め被り」になるため（2026-07-28実害）
            var headBone = animator.GetBoneTransform(HumanBodyBones.Head);
            actor.HeadUpLocal = headBone != null
                ? headBone.InverseTransformDirection(Vector3.up) : Vector3.up;
            // バインドポーズの顔向きは素体依存で符号が逆になる:
            //   UBC素体   … 顔がルート **-Z** 向き（+Zで測るとキャップが後ろ被りになる実測 2026-07-28）
            //   購入素体  … 顔がルート **+Z** 向き（2026-07-29 実測。正面レンダで確認）
            // 間違えると被り物が180°回って後ろ被りになるだけで、エラーにはならず絵でしか気づけない
            var faceFwd = vendorBody ? go.transform.forward : -go.transform.forward;
            actor.HeadFwdLocal = headBone != null
                ? headBone.InverseTransformDirection(faceFwd) : Vector3.forward;

            actor.Graph = PlayableGraph.Create("duel-" + clip.name);
            actor.Output = AnimationPlayableOutput.Create(actor.Graph, "out", animator);
            // Output直下は常に2入力ミキサー。in1=現行クリップ（重み1）／in0=クロスフェードの退場ポーズ。
            // フェードしていない間は in0 未接続なので、素で Playable を直結していた頃と同じ絵になる
            actor.Mixer = AnimationMixerPlayable.Create(actor.Graph, 2);
            actor.Playable = AnimationClipPlayable.Create(actor.Graph, clip);
            SyncFootIk(actor);
            actor.Mixer.ConnectInput(1, actor.Playable, 0);
            actor.Mixer.SetInputWeight(1, 1f);
            actor.Output.SetSourcePlayable(actor.Mixer);

            _actors.Add(actor);
            return actor;
        }

        /// <summary>
        /// クリップの頭出し（アンカー同期用）。ルートモーションはデルタ蓄積なので、
        /// スポーン姿勢へ戻してから0→目標時刻を60fps刻みで前進させる（スパイクのSample()と同じ方式）。
        /// </summary>
        /// <summary>
        /// 【撮影ハーネス・プローブ用】打者の (クリップ内時刻[秒], 再生速度[倍])。
        /// 速度を入れると「絵が違う」だけでは頭出しのずれか速度そのものかを切り分けられないので、
        /// 目測でなくこの実測窓で判定する。
        /// </summary>
        public Vector2 BatterClock => _batter == null
            ? new Vector2(-1f, -1f)
            : new Vector2(_batter.Time, _batter.Speed);

        /// <summary>
        /// 【撮影ハーネス・プローブ用】打者がいま振り切りポーズで据え置き中か。
        /// クリップ内時刻は12fps量子化で毎フレーム止まって見えるので、「時刻が動かない」では
        /// 据え置きを判定できない（実測窓を外して据え置きでない所を撮る事故になる）。
        /// </summary>
        public bool BatterHolding => _batter != null && _batter.HoldRemaining > 0f;

        private static void SeekActor(Actor a, float clipTime)
        {
            if (a == null) return;
            CancelFade(a);
            a.Holding = false;
            RewindActor(a);
            float target = Mathf.Clamp(clipTime, 0f, a.Clip.length - 0.001f);
            int steps = Mathf.Max(1, Mathf.CeilToInt(target * 60f));
            float dt = target / steps;
            for (int i = 0; i < steps; i++) EvaluateWithMirrorFix(a, dt);
            a.Time = target;
            a.PoseAdvanced = true;   // 頭出しもポーズを書き直す＝補正を当て直す回
        }

        /// <summary>
        /// 現在のポーズを退場させながら clipTime へ繋ぐ（セット姿勢→投球の切り口用）。
        ///
        /// クリップ内部の緩急は触らない＝頭出し位置と短いクロスフェードだけで合わせる（design-18 §4.5の掟）。
        ///
        /// **フェード中は applyRootMotion を切る**。退場側は Pause してあるが、それで
        /// 「ルートモーション寄与ゼロ」にはならない——ミキサーが transform へ流すのは
        /// 両入力の**ルート姿勢を重みで混ぜた結果の差分**なので、重みが動いている間は
        /// 退場側（フォロースルーのルート姿勢）へ引っぱられて巨大なデルタが注入される。
        /// 実測（2026-07-30 DuelRootMotionProbe・切り口1.75s→3.0sを12fpsで再生）:
        ///   フェード無し    dPos=(-0.31,-0.58) dYaw=-107.4°
        ///   フェード有り    dPos=( 0.06,-0.02) dYaw=+124.0°  ← 231°ずれ・1コマ目だけで98°振れる
        ///   フェード有り＋凍結 dPos=(-0.32,-0.57) dYaw=-108.8°
        /// 凍結すると 0.25秒ぶんの前進（実測0.016m・1.4°）を捨てるが、切り口1.75〜2.0sは
        /// 踏み込み前でほとんど動かないので実害は無い。縦は `GroundFix` が毎コマ直す。
        ///
        /// 手順の順序が重要: 退場させたい時刻を控える → 先に Seek（SeekActor は CancelFade を通るので、
        /// 退場側を先に繋ぐと捨てられてしまう） → Seek 後に退場ポーズを作って重みを in0=1 へ倒す。
        /// </summary>
        /// <summary>
        /// **別クリップへ**差し替えながら現在の姿勢を退場させる（構え→スイングの切り口）。
        /// <see cref="CrossfadeActorTo"/> は同じクリップ内の飛びを繋ぐもので、クリップ自体が変わる
        /// ケースには使えない（退場側の Playable は差し替え前のクリップから作る必要がある）。
        ///
        /// 順序が要点: <see cref="SetActorClip"/> は頭で <see cref="CancelFade"/> を呼ぶので、
        /// **差し替えを済ませてから**退場側を繋ぐ。AnimationClip 自体は参照が生きているので後から使える。
        /// </summary>
        private static void CrossfadeActorToClip(Actor a, AnimationClip clip, float seekTo, float endAt,
            float seconds)
        {
            if (a == null || clip == null || a.Go == null || !a.Graph.IsValid()) return;
            var fromClip = a.Clip;
            float fromTime = fromClip == null ? 0f : Mathf.Clamp(a.Time, 0f, fromClip.length - 0.001f);
            SetActorClip(a, clip, seekTo, endAt);
            if (seconds <= 0f || fromClip == null || fromClip == clip) return;

            a.FadeFrom = AnimationClipPlayable.Create(a.Graph, fromClip);
            a.FadeFromClip = fromClip;
            SyncFootIk(a);
            a.FadeFrom.SetTime(fromTime);
            a.FadeFrom.Pause();
            a.Mixer.ConnectInput(0, a.FadeFrom, 0);
            a.Mixer.SetInputWeight(0, 1f);
            a.Mixer.SetInputWeight(1, 0f);
            a.FadeDuration = seconds;
            a.FadeRemaining = seconds;
            a.Animator.applyRootMotion = false;   // 混合ルート姿勢の差分が乗る（掟。CancelFadeで戻す）
        }

        private static void CrossfadeActorTo(Actor a, float clipTime, float seconds)
        {
            if (a == null || a.Go == null || !a.Graph.IsValid() || a.Clip == null) return;
            float fromTime = Mathf.Clamp(a.Time, 0f, a.Clip.length - 0.001f);
            var fromClip = a.Clip;   // 同一クリップ内の飛びなので家系は変わらないが、SyncBatterYaw向けに揃えておく
            SeekActor(a, clipTime);          // 退場側は未接続のまま＝Seek中の評価に混ざらない
            if (seconds <= 0f) return;

            a.FadeFrom = AnimationClipPlayable.Create(a.Graph, fromClip);
            a.FadeFromClip = fromClip;
            SyncFootIk(a);
            a.FadeFrom.SetTime(fromTime);
            a.FadeFrom.Pause();
            a.Mixer.ConnectInput(0, a.FadeFrom, 0);
            a.Mixer.SetInputWeight(0, 1f);
            a.Mixer.SetInputWeight(1, 0f);
            a.FadeDuration = seconds;
            a.FadeRemaining = seconds;
            a.Animator.applyRootMotion = false;   // ↑の理由。CancelFade で戻す
        }

        /// <summary>
        /// フェードを畳んで in1 単独（重み1）へ戻す。退場側 Playable は破棄する。
        /// ルートモーションを戻す前に、退場側を外した状態で Evaluate(0) して
        /// アニメータ内部のルート基準を「混ざっていない素のポーズ」へ張り直す
        /// （張り直さないと再開1コマ目に混合ポーズとの差分がゴミとして入る）。
        /// </summary>
        private static void CancelFade(Actor a)
        {
            if (a == null || !a.Graph.IsValid()) return;
            a.FadeRemaining = 0f;
            a.FadeDuration = 0f;
            bool wasFading = a.FadeFrom.IsValid();
            if (wasFading)
            {
                a.Mixer.DisconnectInput(0);
                a.FadeFrom.Destroy();
                a.FadeFromClip = null;
            }
            if (a.Mixer.IsValid()) a.Mixer.SetInputWeight(1, 1f);
            if (wasFading && a.Animator != null)
            {
                a.Graph.Evaluate(0f);
                a.Animator.applyRootMotion = true;
            }
        }

        /// <summary>
        /// クリップ先頭へ巻き戻す。常設グラフでの SetTime ジャンプは「直前時刻→0」のルートモーション差分を
        /// transformへ注入する（毎回使い捨てグラフのスパイクSample()では出ない罠）ため、
        /// 巻き戻しの間だけ applyRootMotion を切ってポーズだけ打ち直す。
        /// </summary>
        private static void RewindActor(Actor a)
        {
            a.Go.transform.localPosition = a.BasePos;
            a.Go.transform.localRotation = a.BaseRot;
            bool rm = a.Animator.applyRootMotion;
            a.Animator.applyRootMotion = false;
            a.Playable.SetTime(0);
            a.Graph.Evaluate(0f);
            a.Animator.applyRootMotion = rm;
            a.PoseAdvanced = true;   // 巻き戻しもポーズを書き直す＝補正を当て直す回
        }

        /// <summary>
        /// 1体を1ステップ進める。クリップ末尾でスポーン姿勢へ戻し、HoldAtEnd なら頭姿勢のまま停止
        /// （投手＝次のアンカー同期まで待つ）、それ以外はループ（ルートモーションのドリフト対策の基準戻し込み）。
        /// HoldSeconds が設定されている場合は、末尾到達時にそのポーズのまま HoldSeconds 秒だけ据え置いてから
        /// 待機へ戻す（空振りの振り切りポーズ用。design-18の掟によりクリップ内部の緩急は変えず、
        /// 単に再生を一時停止するだけ）。
        /// </summary>
        private void StepActor(Actor a, float step)
        {
            // Play停止・ドメインリロードは HideAndDontSave の素体を先に壊すので、Tick が
            // 生き残っている間だけ例外が出る。呼び元（Update・撮影ハーネス）に後始末を強いるより
            // ここで黙って降りる方が安全（2026-07-29 プレビュー撮影で実害）
            if (a == null || a.Go == null) return;
            a.PoseAdvanced = false;   // ポーズを書き直した経路だけが true を立てる（補正の二度当て防止）
            if (!a.Enabled) return;   // 打席中の野手など＝ポーズを進めない（N体化の間引き）
            // 役ごとの固定間隔で間引く（0=毎コマ＝現行）。溜めた分は次の更新へ持ち越して丸ごと進める＝
            // クリップ内時刻は実時間とずれない（コマ数だけが減る）
            if (a.PoseInterval > 0f)
            {
                a.PoseAccum += step;
                if (a.PoseAccum < a.PoseInterval) return;
                step = a.PoseAccum;
                a.PoseAccum = 0f;
            }
            if (a.Holding) return;
            if (a.HoldRemaining > 0f)
            {
                a.HoldRemaining -= step;
                if (a.HoldRemaining > 0f) return;
                FinishOneShot(a);
                return;
            }
            float before = a.Time;
            a.Time += step * a.Speed;
            float end = a.EndAt > 0f && a.EndAt < a.Clip.length ? a.EndAt : a.Clip.length;
            if (a.Time >= end)
            {
                if (a.HoldSeconds > 0f)
                {
                    // 据え置くポーズは**打ち切り時刻ちょうど**を評価して焼く。行き過ぎたぶんを
                    // 戻さずに前のコマで固めると、止め所に12fpsの格子が乗るかが頭出しの位相次第になり、
                    // 乗らない速度でだけ手前のコマで固まる（design-18 §4.5-⑩ の「格子に乗るかは位相次第で、
                    // 乗らない結果だけ黙って壊れる」そのもの）。
                    // 実害 2026-08-01: SwingFinishSec=1.63 に対し、SwingSpeed=1.4 は 1.617 で止まる（狙いどおり）
                    // のに 2.0 は格子が 1.513→1.680 と跨ぐため 1.513 で固まり、胴の回りが8°足りず
                    // バットが巻き付き切らない位置で停止した（＝振り終わりが中途半端に見えた）。
                    float rest = end - before;
                    if (rest > 1e-4f)
                    {
                        EvaluateWithMirrorFix(a, rest);
                        a.PoseAdvanced = true;
                    }
                    a.Time = end;
                    a.HoldRemaining = a.HoldSeconds;
                    a.HoldSeconds = 0f;
                    return;   // 以降はこのポーズのまま据え置き（Evaluateしない＝ポーズが進まない）
                }
                FinishOneShot(a);
                return;
            }
            // クロスフェードの重み送り（評価の直前に倒す）。12fpsで3コマしかないので smoothstep で角を取る
            if (a.FadeRemaining > 0f)
            {
                a.FadeRemaining -= step;
                float u = a.FadeDuration <= 0f ? 1f : Mathf.Clamp01(1f - a.FadeRemaining / a.FadeDuration);
                float w = u * u * (3f - 2f * u);
                a.Mixer.SetInputWeight(0, 1f - w);
                a.Mixer.SetInputWeight(1, w);
                if (a.FadeRemaining <= 0f) CancelFade(a);
            }
            // **速度は Evaluate 側にも掛ける**。`a.Time` だけ speed 倍にして Evaluate を等速で回すと、
            // 記録上のクリップ時刻だけが先行してポーズが置いていかれる（2026-07-31 実害）。
            // SwingSpeed=2.0 で実測 0.42 クリップ秒ぶん遅れ、球の到達（0.45s）に対して接触ポーズが
            // 0.18秒遅れて出る＝「振りが遅くて球と合っていない」。倍率を上げても、それまでは
            // 「クリップを早く打ち切る」だけでポーズは常に1倍速だった。
            // グラフはアクター別なので、他の役（Speed=1）の見え方は変わらない
            EvaluateWithMirrorFix(a, step * a.Speed);
            a.PoseAdvanced = true;
            // 打者の待機ループは自作打撃クリップの構え区間にわずかな水平ルートモーションが乗っており、
            // 毎コマ蓄積すると打席内で左右にドリフトして見える（#291）。待機中（構え区間ループ中）だけ
            // 基準位置のx/zへ毎コマ戻す。
            // ※ 条件は `a == _batter` の identity 判定だった。守備・走者のN体化で StepActor が
            //   4体以外も回すようになるため、役割ではなく**アクター自身のフラグ**へ移した（絵は不変）
            if (a.RebaseXZWhileIdle && a.Clip == a.IdleClip && a.EndAt == a.IdleEndAt)
            {
                var lp = a.Go.transform.localPosition;
                lp.x = a.BasePos.x;
                lp.z = a.BasePos.z;
                a.Go.transform.localPosition = lp;
            }
            // スイング区間（EndAt!=IdleEndAt）も同じクリップに横のルートモーションが乗っており、
            // 頭出し直後（RebaseActorXZ）で一度戻すだけでは以降が素通しでスイングと同時に右へ
            // ステップして見える（issue #446）。z（投手方向の踏み込み）は残したいので、こちらは
            // x だけ毎コマ基準へ戻す（z は触らない）
            else if (a.RebaseXWhileSwinging && a.Clip == a.IdleClip && a.EndAt != a.IdleEndAt)
            {
                var lp = a.Go.transform.localPosition;
                lp.x = a.BasePos.x;
                a.Go.transform.localPosition = lp;
            }
        }

        /// <summary>末尾（またはホールド終了）到達時の後始末: 待機ループへ戻す、または恒久停止（投手）。</summary>
        private void FinishOneShot(Actor a)
        {
            // フェード中に末尾へ達しても畳んでから戻す（畳まないと applyRootMotion が切れたままになる）
            CancelFade(a);
            // 打者の一発物（スイング）が待機へ戻る＝バット軌跡の武装を解除（issue #444）。
            // 構えとスイングが同じクリップ（既定）では復帰後の Time/EndAt が待機区間の値になり、
            // クリップ時刻の比較だけでは戻り動作を区別できないため、ここで明示的に下ろす
            if (a == _batter) _batTrailSwing = false;
            a.Time = 0f;
            a.EndAt = a.DefaultEndAt;   // 投手は購入クリップ6.00sの尻尾を毎回切り落とす（既定0=末尾まで）
            a.Speed = 1f;               // 一発物の速度指定は持ち越さない（待機は常に収録の速さ）
            if (a.IdleClip != null && a.Clip != a.IdleClip)
            {
                // 打者の構えを別クリップにしていると、フォロー→構えもクリップの切り口になる。
                // 同じクリップ内の巻き戻し（既定）はこの分岐に入らないので絵は変わらない。
                // 捕手は捕球ポーズで据え置いた後にここへ来る＝止めた姿勢と待機（構え）の頭が
                // 離れているので、ハードカットだと跳ぶ。構え直しもクロスフェードで作る
                if ((a == _batter && a.IdleClip == _batterOwnIdle) || a == _catcher)
                    CrossfadeActorToClip(a, a.IdleClip, 0f, a.IdleEndAt, CrossfadeSeconds);
                else
                    SetActorClip(a, a.IdleClip, 0f, a.IdleEndAt);   // 一発物（結果分岐）→待機ループへ復帰
                return;
            }
            RewindActor(a);
            // 待機が「1本のクリップの一区間だけ」の場合はループのたびに区間を張り直す。
            // 自作打撃クリップは構え→スイング→走り出しが1本なので、戻さないと待機中に振ってしまう
            if (a.Clip == a.IdleClip) a.EndAt = a.IdleEndAt;
            if (a.HoldAtEnd) a.Holding = true;
        }

        /// <summary>低い方の足ボーンが接地面＋足首オフセットに来るよう縦だけ合わせる（ステージローカル）。</summary>
        private void GroundFix(Actor a)
        {
            var lf = a.Animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            var rf = a.Animator.GetBoneTransform(HumanBodyBones.RightFoot);
            if (lf == null || rf == null) return;
            float footLocalY = Mathf.Min(lf.position.y, rf.position.y) - _root.transform.position.y;
            a.Go.transform.localPosition += new Vector3(0f, (a.GroundY + a.AnkleOffset) - footLocalY, 0f);
        }

        // ---- 小道具（§3.3。両手整列バット＋手ボーン子ミット） ------------------

        /// <summary>
        /// バットの断面（軸方向t=0がノブ端・1がバレル端／半径[m]）。実物84cm級の寸法。
        /// 一様円柱だとグリップが直径5.5cmの寸胴になり、拳が回り込めず「握れていない」絵になる
        /// （2026-07-29 寄り検証）。design-18 §3.3 の「先細り円柱」をこの断面で満たす。
        /// </summary>
        private static readonly Vector2[] BatProfile =
        {
            new(0.000f, 0.024f),   // ノブ端
            new(0.025f, 0.025f),   // ノブ
            new(0.045f, 0.016f),   // グリップ（実物のハンドルはø3.2cm前後）
            new(0.450f, 0.017f),
            new(0.620f, 0.027f),   // テーパー
            new(0.800f, 0.033f),   // バレル
            new(0.965f, 0.033f),
            new(1.000f, 0.026f),   // 先端の丸め
        };

        private const float BatHalfLength = 0.425f;   // 全長0.85m。AlignBat の位置計算もこの値を使う
        private const int BatSides = 8;               // ピクセル化後はシルエットだけなので8角で足りる

        private GameObject SpawnBat(int layer)
        {
            var bat = new GameObject("prop-bat");
            bat.transform.SetParent(_root.transform, false);
            var batMesh = BuildBatMesh();
            _meshes.Add(batMesh);
            bat.AddComponent<MeshFilter>().sharedMesh = batMesh;
            bat.AddComponent<MeshRenderer>().sharedMaterial = FlatMaterial(new Color(0.78f, 0.62f, 0.42f));
            bat.layer = layer;
            return bat;
        }

        /// <summary>BatProfile を軸まわりに回した実寸メッシュ（軸+Y・原点が中央・localScaleは1のまま使う）。</summary>
        private static Mesh BuildBatMesh()
        {
            int rings = BatProfile.Length;
            var verts = new Vector3[rings * BatSides];
            var tris = new List<int>((rings - 1) * BatSides * 6 + (BatSides - 2) * 6);
            for (int r = 0; r < rings; r++)
            {
                float y = Mathf.Lerp(-BatHalfLength, BatHalfLength, BatProfile[r].x);
                float rad = BatProfile[r].y;
                for (int s = 0; s < BatSides; s++)
                {
                    float a = s * Mathf.PI * 2f / BatSides;
                    verts[r * BatSides + s] = new Vector3(Mathf.Cos(a) * rad, y, Mathf.Sin(a) * rad);
                }
            }
            for (int r = 0; r < rings - 1; r++)
                for (int s = 0; s < BatSides; s++)
                {
                    int s2 = (s + 1) % BatSides;
                    int a = r * BatSides + s, b = r * BatSides + s2;
                    int c = (r + 1) * BatSides + s, d = (r + 1) * BatSides + s2;
                    tris.Add(a); tris.Add(c); tris.Add(b);
                    tris.Add(b); tris.Add(c); tris.Add(d);
                }
            // 両端の蓋（扇状）。開いたままだと内側が抜けて見える
            for (int s = 1; s < BatSides - 1; s++)
            {
                tris.Add(0); tris.Add(s); tris.Add(s + 1);
                int top = (rings - 1) * BatSides;
                tris.Add(top); tris.Add(top + s + 1); tris.Add(top + s);
            }
            var mesh = new Mesh { name = "prop-bat (generated)", hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = verts;
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>生成した使い捨てメッシュ（HideAndDontSave はドメインリロードを跨いで残るのでDisposeで明示破棄する）。</summary>
        private readonly List<Mesh> _meshes = new();

        // 片手フォロースルー判定: 両手ボーン間隔がグリップ間隔を明確に超えたら上の手が離れたとみなす。
        // 素体UBCの両手ボーン間隔は「両手で握っていても」構えで~0.23mある（RunHandGapProbe実測）ため、
        // 判定はスイング系クリップに限定する（構え/見逃し/死球は常に両手整列＝素の見た目を信じる）
        // 片手フォロースルー判定・仰角・ノブ出し量は DuelCalibration（BatHandsApartM /
        // BatStanceElevDeg / BatFollowElevDeg / BatGripBelowM）へ移した。旧既定と経緯:
        //   0.28m  … 両手で握っていても構えで手ボーン間隔が0.23mあった収録に対する閾値
        //   仰角60°… 手→手の実測線が寝てバットが水平に見えた（監督指摘 2026-07-28）ための決め打ち
        //   仰角35°… 振り切りで実測線がカメラ軸を向き、棒が点に潰れて消える絵への対処
        // いずれも**両手が開いていた頃の補正**で、握りを直した今は実測線をそのまま使える（BatAxisFromGrip）
        // 走り出しからバットを放った扱いで非表示。**2026-07-31 以降は発火しない**＝全スイングを
        // SwingFinishSec(1.63) で打ち切るようにしたので、打者が走り出す所まで再生されなくなった。
        // 走り出しを見せる作りに戻すときのために残してある（design-18 §4.5-⑭）
        private const float BatDropTime = BatterRunStart;
        private bool _batOneHand;       // いま片手追従中か（診断用）
        private bool _batGoodValid;     // _batLocal* が「両手で持てていた見た目」を保持しているか
        private Vector3 _batLocalPos;
        private Quaternion _batLocalRot;
        private bool _batHeldValid;           // バットの向きを構えで焼いてあるか
        private Vector3 _batHeldAxis;         // 構えでの向きそのもの。テイクバック中はここで止める
        // 予約したスイングの接触時刻[クリップ秒]（SwingBatAxis の終点）。既定を入れてあるのは
        // 打撃フォーム確認窓のように **OnPitchReleased を通さずクリップを直接送る**使い方のため
        // （0だとバットが構えの向きのまま固まり、軌道が一切出ない）
        private float _swingAnchorClip = SwingHitContact;

        private static float Smooth(float u) => u * u * (3f - 2f * u);

        /// <summary>
        /// 掌中心（ワールド）。手ボーンの原点は**手首**なので、そのまま結ぶとバットが手首の上を横切り
        /// 「握れていない」絵になる（design-18 §3.3.1）。中指付け根との補間で掌の中へ通す。
        /// </summary>
        private Vector3 Fist(Transform wrist, HumanBodyBones knuckle)
        {
            var k = _batter.Animator.GetBoneTransform(knuckle);
            return k != null ? Vector3.Lerp(wrist.position, k.position, 0.65f) : wrist.position;
        }

        /// <summary>この球はバットに当たるか（ミート・ファウル）。空振りは当てない＝向けてはいけない。</summary>
        private bool _batAimsBall;

#if UNITY_EDITOR
        /// <summary>【実測プローブ用】接触のクリップ時刻を差し替える（負＝既定のまま）。</summary>
        internal static float SwingAnchorOverrideForCapture = -1f;
#endif

        /// <summary>
        /// 振り下ろしに使う長さを**ポーズのコマ数**で持つ（秒でなく）。収録の振り下ろしは 0.11 クリップ秒＝
        /// 12fpsで**ちょうど1コマ**しかないので、秒で窓を切ると「どのコマにいくつ乗るか」が飛翔時間ごとに
        /// 変わり、接触のコマが 0.9 までしか寄らず芯が 0.108m 足りない（2026-07-31 実測）といったことが起きる。
        /// コマ数で持てば ①接触のコマは必ず 1.0（＝芯が打点に乗る） ②その1コマ前は必ず中間 が保証される。
        /// 2コマ＝中間が1枚。実時間では 2/12×1.4… ではなく 2/12＝0.167秒＝実物の振り下ろしと同程度。
        /// </summary>
        private const float BatSweepFrames = 2f;

        /// <summary>
        /// 振り抜きで振り下ろしと同じ面をさらに回す角度[度]と、そのコマ数。
        ///
        /// 120°/2コマ（v19以前）だと、バットは本塁側(+x)から投手側(+z)へ**方位で103°**回ったところで
        /// 止まり、フォロースルーが途中で固まる（2026-07-31 監督指摘「フォロースルーだからバットが逆だね／
        /// 頭の横まで振りぬく」）。実物は接触の後も背中側(-x)を通って捕手側(-z)＝**頭の横**まで回る。
        /// 面の法線がバット軸と直交していないぶん方位の進みは指定角より小さい（実測 120°指定で103°）。
        /// 時間も実物に合わせる: 接触から振り切りまでは実時間で 0.2〜0.3秒＝再生1.4倍で 2.4〜3.6コマ。
        ///
        /// **3→1.6 へ変更（2026-08-02。<see cref="SwingFinishSec"/> の 1.433 化に合わせる）。**
        /// 250°を1.6コマで回すので1コマあたり156°（旧83°）＝振り抜きのバットは今より飛んで見えるが、
        /// 実時間0.13秒の区間なので採用可と判断済み。issue #395
        /// </summary>
        private const float BatWrapDeg = 250f;
        private const float BatWrapFrames = 1.6f;

#if UNITY_EDITOR
        /// <summary>【実測プローブ用】<see cref="SwingFinishSec"/> を差し替える（負＝既定のまま）。</summary>
        internal static float SwingFinishOverrideForCapture = -1f;
        /// <summary>【実測プローブ用】<see cref="BatWrapFrames"/> を差し替える（負＝既定のまま）。</summary>
        internal static float BatWrapFramesOverrideForCapture = -1f;
#endif

        private static float EffectiveSwingFinishSec()
        {
            float v = SwingFinishSec;
#if UNITY_EDITOR
            if (SwingFinishOverrideForCapture > 0f) v = SwingFinishOverrideForCapture;
#endif
            return v;
        }

        private static float EffectiveBatWrapFrames()
        {
            float v = BatWrapFrames;
#if UNITY_EDITOR
            if (BatWrapFramesOverrideForCapture > 0f) v = BatWrapFramesOverrideForCapture;
#endif
            return v;
        }

        /// <summary>
        /// タメ（振り下ろしの途中で芯が捕手側へ寝る形）の仰角[度]。芯が握りより少し上に来る量。
        /// トップ軸→打点軸を素直に Slerp すると**最短の弧**になり、芯が本塁の上を横切る
        /// ＝バットが遠回りする（実測 2026-07-31: clip 0.87 で芯 x=+0.06 ＝本塁を越え、握りとほぼ同じ高さの
        /// 水平な棒になる）。実物は一度後ろ（捕手側）へ寝てから走るので、その姿勢を制御点として挟む。
        /// </summary>
        private const float BatLagElevDeg = 12f;

        /// <summary>
        /// 手が打点へこれだけ寄ったら振り下ろしを始める[m]（握り→打点が 腕+この値 になった時点で0）。
        /// 進みを**クリップ時刻でなく手の寄り**で測るための帯。収録の腕は接触の手前でほとんど動かない
        /// （実測 clip 0.75→0.95 で握りの移動 0.10m）ので、時刻で回すと**手が止まったままバットだけ回る**
        /// ＝「手の動きにバットがついてこない／遅れて出る」に見える（2026-07-31 監督指摘・横から）。
        /// </summary>
        /// **帯の幅は手の速さに合わせて実測で決める**。接触を判定面の前へ出した（ContactAheadM）ことで
        /// 接触が腕の全速区間に入り、握りは1コマで 0.20m 進むようになった＝旧値 0.20 では帯を1コマで
        /// 通り抜けて s が 0.11→1 に飛ぶ（2026-07-31 実測）。0.35 で 0.10→0.58→1 の3段になる。
        /// 経緯: 0.12（1コマに集中）→ 0.20（接触1.00のとき3段）→ 0.35（接触1.05）→ **0.55（接触1.17・v19）**。
        /// 0.55 は「手の寄りでなくコマ数が効き始める値」＝ここから先は広げても絵が変わらない
        /// （実測の芯の高さ: 0.35→1.63/1.51/0.93/0.81、**0.55→1.63/1.41/0.91/0.81**、0.75→0.55と同一）。
        /// この帯は駆動そのものでなく「手より先に回らない」ための安全弁なので、効かなくなる所まで広げてよい。
        private const float BatSweepReachM = 0.55f;

#if UNITY_EDITOR
        /// <summary>【実測プローブ用】上の帯の差し替え（負＝既定）。</summary>
        internal static float SweepReachOverrideForCapture = -1f;
#endif
        private static float SweepReach =>
#if UNITY_EDITOR
            SweepReachOverrideForCapture > 0f ? SweepReachOverrideForCapture :
#endif
            BatSweepReachM;

        /// <summary>空振りで球の下を通す量[m]。0だと**真ん中の球を空振りしたとき当たったように見える**。</summary>
        private const float BatMissUnderM = 0.20f;

        /// <summary>球が居ないときのスイング面の通り道の高さ[m]（ステージローカル）。</summary>
        private const float BatMissSweepHeightM = 0.75f;

        private bool _batWrapValid;      // 接触時の軸と面の法線を焼いてあるか（振り抜きの起点）
        private Vector3 _batWrapFrom, _batWrapNormal;
        private bool _batRestValid;      // 巻き付き終わりの姿勢を体の中へ焼いてあるか（④の起点）
        private Vector3 _batRestLocalAxis;   // その姿勢の軸（胸ローカル）

        /// <summary>
        /// 打撃クリップ中のバットの向きを**自分で描く**（2026-07-31 監督指摘「バットの軌道がおかしい」）。
        ///
        /// 収録の手の回転はバット軸の情報を持っていない（実測: トップ→接触で 上向き0.69→真上0.99→
        /// 0.87→0.74→**接触の瞬間は捕手向き0.18**）。道具を持たない前提の振りなので当然で、
        /// これに追従させる限り軌道にはならない。よって幾何で描く（当てにいく①〜③まで）:
        ///   ① トップ保持 … 構えで焼いた軸のまま（テイクバック。手が引かれるぶん平行移動するだけ）
        ///   ② 振り下ろし … トップ軸→打点へ向けた軸を Slerp。**大円＝1つの面内の回転**なので
        ///                   これがそのままスイング面になり、芯は単調に落ちる
        ///   ③ 振り抜き   … 接触時の軸から巻き付き軸（逆ヨー×仰角）へ回し続ける
        ///   ④ 惰性       … 巻き付きの後は手の中で剛体（①〜③はワールドの向きなので、
        ///                   式が定数になった瞬間バットが空間に釘付けになる）
        ///
        /// ②の終点を「実際の打点」に取るので、コース（高め/低め/内外角）に絵が自動で追従し、
        /// 接触の瞬間だけ別途IKで当てにいく必要が無くなる（旧 AimBatAtBall を吸収）。
        /// 空振りは打点でなく<see cref="BatMissSweepHeightM"/>の高さの本塁上を通す＝高め/低めの球は
        /// 自然に振り下／振り上げになる。
        /// </summary>
        private Vector3 SwingBatAxis(Vector3 grip, Transform restFrame)
        {
            float t = _batter.Time;
            float contact = _swingAnchorClip;
            if (contact <= 0f) return _batHeldAxis;
            float frame = Mathf.Max(0.001f, _batter.Speed / PoseFps);   // ポーズ1コマ[クリップ秒]

            // 狙点は**判定に使われた実着弾位置**（design-18 §4.5-③の掟）。
            // 空振りは**判定面**（_ballFlight＝本塁上）の位置の下を通す。固定の高さで振ると
            // 真ん中の球を空振りしたときバットが球に乗って「当たったのに空振り」に見える
            var target = _ballT < 0f
                ? new Vector3(0f, BatMissSweepHeightM, 0f)
                : _batAimsBall
                    ? BallPointAt(_ballArrival)
                    : BallPointAt(_ballFlight) + Vector3.down * BatMissUnderM;
            var toTarget = _root.transform.TransformPoint(target) - grip;
            float dist = toTarget.magnitude;
            var aim = dist > 1e-3f ? toTarget / dist : _batHeldAxis;
            var lag = LagAxis(aim);

            // 接触の**前後**半コマは②に含める（＝映るコマのうち接触に一番近い1枚を必ず打点へ乗せる）。
            // `t <= contact` だけだと、ポーズの12fps格子が接触の直後に落ちた結果では③に入り、
            // 巻き付きの起点が**1コマ前の狙い**（＝手がまだ手前に居たときの向き）になる。
            // 実測 2026-07-31: 格子が 1.0567/1.1733 に落ちる位相で、起点が 1.17 の狙い(1.00,-0.06,-0.03)
            // でなく 1.05 の狙い(0.82,-0.11,0.56)＝**33°ずれた**まま振り抜きが始まっていた。
            if (t <= contact + 0.5f * frame)
            {
                // ② 振り下ろし。
                // 進みは2つの小さい方で決める:
                //   ・コマ数 … 接触の瞬間に映るコマが必ず1.0に届くことを保証する（0.5コマ寄せ）
                //   ・手の寄り … 収録の腕が動き出すまでバットも動かない（＝手より先に回らない）
                float frames = (contact - t) / frame;
                float lever = BatLeverM();
                float uTime = (BatSweepFrames - (frames - 0.5f)) / BatSweepFrames;
                float reach = SweepReach;
                float uHand = (lever + reach - dist) / reach;
                float raw = Mathf.Min(uTime, uHand);
                if (frames <= 0.5f) raw = 1f;   // 接触の瞬間に映るコマは必ず寄せ切る
                float s = Smooth(Mathf.Clamp01(raw));
                // 振り抜き（③）の起点＝**接触の軸と面**を毎コマ焼き直しておく。
                // s が 1 に届いたコマでだけ焼く実装だと、**焼き損ねる結果がある**:
                // ポーズは12fpsの格子なので、接触の直前半コマに格子が乗るかは
                // 「頭出し位置と接触時刻の位相」次第で、乗らなければ ③ が起点を持たないまま
                // 構えの軸を返し続ける。実測 2026-07-31（空振り・接触1.00）: clip 1.03 以降
                // 軸が (-0.14,0.79,-0.60) に固まって最後まで1度も動かない＝監督が見た「固まったバット」。
                // 起点は s に依らず幾何で決まる（s=1 の3次ベジエは厳密に aim）ので毎コマ焼いて構わない。
                _batWrapFrom = aim;
                // 面はタメ→打点で取る（トップ→打点だと遠回りの面になり、振り抜きが逆へ回る）
                var n0 = Vector3.Cross(lag, aim);
                _batWrapNormal = n0.sqrMagnitude > 1e-6f ? n0.normalized : Vector3.up;
                _batWrapValid = true;
                if (s <= 0.001f) return _batHeldAxis;   // ① トップ保持

                // トップ→**タメ**→打点の3点で回す（最短の弧＝遠回りを避ける）。
                // 制御点のタメを二重に置いた球面3次ベジエ（de Casteljau）＝端点は厳密に一致する
                var b1 = Vector3.Slerp(Vector3.Slerp(_batHeldAxis, lag, s), lag, s);
                var b2 = Vector3.Slerp(lag, Vector3.Slerp(lag, aim, s), s);
                return Vector3.Slerp(b1, b2, s).normalized;
            }

            // ③ 振り抜き。接触時の軸を同じ面内でさらに回し続ける（起点も面も焼いてあるので暴れない）。
            // ②のコマが1枚も無かったとき（頭出しが接触より後 等）だけ、ここで今の握りから焼く
            if (!_batWrapValid)
            {
                _batWrapFrom = aim;
                var n1 = Vector3.Cross(lag, aim);
                _batWrapNormal = n1.sqrMagnitude > 1e-6f ? n1.normalized : Vector3.up;
                _batWrapValid = true;
            }
            float wrapEnd = contact + BatWrapFrames * frame;
            if (t < wrapEnd || restFrame == null)
            {
                float w = Smooth(Mathf.InverseLerp(contact, wrapEnd, t));
                return (Quaternion.AngleAxis(BatWrapDeg * w, _batWrapNormal) * _batWrapFrom).normalized;
            }

            // ④ 巻き付きの後は「**体**の中で剛体」へ渡す（2026-07-31 監督指摘の続き）。
            // ①〜③はどれも**ワールドの向き**を組み立てているので、巻き付きが終わった時点で
            // 式が定数になり、以後バットの向きがワールドに釘付けになる。位置は握りに追従するので、
            // 体が回って走り出しても**バットだけ空間に固定されたまま平行移動する**（実測 2026-07-31:
            // clip 1.30/1.60/1.90/2.20/2.50 で 芯−握り がビット一致 (-0.09,0.48,-0.37)）。
            // 対決ビューで実際に映るのは 空振り=1.28〜2.60（0.94秒）／インプレー=1.28〜約1.84 なので、
            // どの結果でも必ず目に入る区間。
            //
            // 焼き先は**手ではなく胸**。惰性区間だけは収録の手に預けてよさそうに見えるが、実測すると
            // フォロースルーの左手は1コマ（12fps）で **175°→138°→79°** 回っており（前腕の回内。
            // 道具を持たない前提の振りなので当然）、バットがそれを拾うと clip 1.38〜1.85 で
            // **真下（y=-0.74〜-0.90）＝自分の足元**を向く。監督が見た症状そのもの。
            // 同じ区間の胸は 46°→26°→9°→11° と桁が違い、これは振り切りで体が回り続けるぶん＝
            // 実物のフォロースルー（バットが背中側に留まったまま体が回る）と同じ動き。
            // ①〜③で収録の手の回転を使わなかったのと同じ理由が、惰性区間にもそのまま効く。
            //
            // **④へ入る最初のコマで「巻き終わり（w=1）」の向きを焼く（2026-08-02・issue #395）。**
            // 旧実装は③の最中に毎コマ _batRestLocalAxis を焼き直しており、④はその「最後に評価した
            // コマ」の中途半端な角度（w<1）をそのまま返していた。ポーズは12fpsの格子なので、
            // 巻き付き（wrapEnd）が打ち切り（SwingFinishSec）より前に終わる結果（例: 空振り）では、
            // ③最後のサンプルが wrapEnd に届く前のコマになり、④へ渡ったバットが途中の向きのまま
            // 凍っていた。正しい受け渡しは w=1 の完成形を④の最初のコマで一度だけ焼くこと。
            if (!_batRestValid)
            {
                var wrapEndAxis = (Quaternion.AngleAxis(BatWrapDeg, _batWrapNormal) * _batWrapFrom).normalized;
                _batRestLocalAxis = restFrame.InverseTransformDirection(wrapEndAxis);
                _batRestValid = true;
            }
            return restFrame.TransformDirection(_batRestLocalAxis).normalized;
        }

        /// <summary>
        /// 惰性区間（SwingBatAxis ④）でバットの向きを預ける骨。胸→無ければ上胸→腰の順。
        /// 素体によっては Chest が無い（オプション骨）ので必ず落とし先を用意する。
        /// </summary>
        private Transform RestFrameBone()
        {
            var a = _batter != null ? _batter.Animator : null;
            if (a == null) return null;
            return a.GetBoneTransform(HumanBodyBones.Chest)
                ?? a.GetBoneTransform(HumanBodyBones.UpperChest)
                ?? a.GetBoneTransform(HumanBodyBones.Hips);
        }

        /// <summary>芯（バレル端から手元側へこれだけ入った所）[m]。実物のスイートスポットの位置。
        /// ⚠ ここは**製品コード**（AlignBat が使う）。実測窓の中に置くと Player ビルドで落ちる。</summary>
        private const float BatSweetInsetM = 0.15f;

        /// <summary>握り→芯の長さ[m]。バットの原点は握り点から軸方向へ寄せてあるので2段の和になる。</summary>
        private float BatLeverM()
        {
            float half = Mathf.Max(0.1f, _cal.BatLengthM) * 0.5f;
            return (half - _cal.BatGripBelowM) + (half - BatSweetInsetM);
        }

        /// <summary>
        /// タメの軸（芯が捕手側へ寝て、握りより <see cref="BatLagElevDeg"/> だけ上）。
        /// 水平成分は打点方向と直交する2つのうち**捕手側（-z）**を選ぶ＝左右打ちのどちらでも
        /// 「体の後ろ」になる（打者の向きを見なくてよい）。
        /// </summary>
        private static Vector3 LagAxis(Vector3 aim)
        {
            var h = new Vector3(aim.x, 0f, aim.z);
            if (h.sqrMagnitude < 1e-6f) return aim;
            h.Normalize();
            var lag = new Vector3(h.z, 0f, -h.x);
            if (lag.z > 0f) lag = -lag;
            float el = BatLagElevDeg * Mathf.Deg2Rad;
            return (lag * Mathf.Cos(el) + Vector3.up * Mathf.Sin(el)).normalized;
        }

        /// <summary>握り点（両掌中心の中点・ワールド）。<see cref="AlignBat"/> と実測プローブの単一ソース。</summary>
        private Vector3 BatGripWorld()
        {
            var lhT = _batter?.Animator != null ? _batter.Animator.GetBoneTransform(HumanBodyBones.LeftHand) : null;
            var rhT = _batter?.Animator != null ? _batter.Animator.GetBoneTransform(HumanBodyBones.RightHand) : null;
            if (lhT == null || rhT == null) return Vector3.zero;
            return (Fist(lhT, HumanBodyBones.LeftMiddleProximal)
                  + Fist(rhT, HumanBodyBones.RightMiddleProximal)) * 0.5f;
        }

        private void AlignBat()
        {
            if (_bat == null || _batter == null) return;
            var lhT = _batter.Animator.GetBoneTransform(HumanBodyBones.LeftHand);
            var rhT = _batter.Animator.GetBoneTransform(HumanBodyBones.RightHand);
            if (lhT == null || rhT == null) return;

            // インプレーの走り出し以降はバットを放った扱い（実物はトスするが小道具は消すのが最も自然）。
            // 放すのは「クリップを走り出しの先まで流す＝実際に走る」インプレー（EndAt=0）だけ。
            // 空振り・ファールは EndAt=BatterRunStart で打ち切る＝Timeが3.25に達した停止ポーズでも
            // 打者は走らないので持ったまま（旧実装は空振りの振り切りホールドでバットが消えていた）
            if (_batter.Time >= BatDropTime && _batter.EndAt <= 0f)
            {
                _bat.SetActive(false);
                return;
            }
            _bat.SetActive(true);

            var lh = Fist(lhT, HumanBodyBones.LeftMiddleProximal);
            var rh = Fist(rhT, HumanBodyBones.RightMiddleProximal);
            // クリップ内時刻での区間判定は打撃クリップ限定（死球クリップは常に構え扱い＝バットは手元で立てたまま）
            bool battingClip = _batter.Clip == _swingMissClip;
            // 片手判定を許すのはスイング区間だけ。自作打撃クリップは構えもスイングも同じ1本なので
            // クリップの同一性では区別できず、クリップ内時刻で見る（構えは BatterIdleEnd 以下）
            bool swinging = battingClip && _batter.Time >= BatterSwingStart;
            _batOneHand = swinging && _batGoodValid && Vector3.Distance(lh, rh) > _cal.BatHandsApartM;
            if (!_batOneHand)
            {
                // 両手保持: 握り線（掌中心→掌中心）を基準に整列（§3.3の実測知見＝手ボーン軸は使えない）。
                // 実測線が信用できるのはスイング中（握り線が振り面に沿う）だけ:
                //   構え     = 合成軸（ヨー実測 × 仰角60°）で立てる（実測線は拳が横に開いて寝る）
                //   振り出し = ±0.15sで実測軸へブレンド
                //   振り抜き = 面通過+0.05〜0.25sで逆ヨー×仰角35°の巻き付き軸へブレンド
                //             （実測線は投手方向≒カメラ軸を向き、棒が点に潰れて消えて見える）
                var d = (rh - lh).normalized;
                var horiz = new Vector3(d.x, 0f, d.z);
                Vector3 axis;
                if (horiz.sqrMagnitude < 1e-6f)
                {
                    axis = d;
                }
                else
                {
                    Vector3 Composed(Vector3 yawDir, float elevationDeg)
                    {
                        float el = elevationDeg * Mathf.Deg2Rad;
                        return (yawDir.normalized * Mathf.Cos(el) + Vector3.up * Mathf.Sin(el)).normalized;
                    }
                    var stance = Composed(horiz, _cal.BatStanceElevDeg);
                    if (!battingClip || _batter.Time < BatterSwingStart - 0.15f)
                    {
                        axis = stance;
                    }
                    else
                    {
                        float inSwing = Mathf.InverseLerp(BatterSwingStart - 0.15f, BatterSwingStart + 0.15f, _batter.Time);
                        float wrapped = Mathf.InverseLerp(SwingMissPlane + 0.05f, SwingMissPlane + 0.25f, _batter.Time);
                        var wrap = Composed(-horiz, _cal.BatFollowElevDeg);
                        axis = Vector3.Slerp(Vector3.Slerp(stance, d, inSwing), wrap, wrapped).normalized;
                    }
                    // 握りが直った今は実測線（ノブ側の手→バレル側の手）がそのままバット軸。
                    // 合成軸は「両手が23cm開いて拳が積み重ならない」収録を前提にした補正なので、
                    // 手を積み重ねた状態では水平成分が消えてバットが真上を向く（実測 axis=(0,1,0)）
                    // 自作構えは両手が23cm開いて実測線が寝る＝クリップ別の値を使う
                    float axisFromGrip = BatterOnOwnIdle ? _cal.IdleBatAxisFromGrip : _cal.BatAxisFromGrip;
                    if (axisFromGrip > 0f)
                        axis = Vector3.Slerp(axis, d, Mathf.Clamp01(axisFromGrip)).normalized;
                }

                // ---- 打撃クリップの間は握り線から向きを採らない（2026-07-31 監督指摘）----
                // 実測線の基線は両手の間隔＝`GripGapM` 0.10m しかないので、掌中心の推定が1cmずれると
                // 軸が5.7°回る。実測の実害は2箇所:
                //   ・振り出し: 1コマ（12fps）で軸が102°飛ぶ（clip 0.79 担いだ形 → clip 0.90 ほぼ真下）
                //   ・テイクバック: 手はほぼ動いていない（握りの移動0.15m）のに、バットだけ
                //     clip 0.67→0.79 で**ヨー180°**回る（プレート側→反対側）＝「あちこち動く」の正体
                //
                // 代わりに「**構えのループで焼いた**手の中でのバットの向き」を打撃クリップの間ずっと
                // 剛体で運ぶ。実際にバットを握っているのと同じ関係＝**振っている最中に握り直さない**ので、
                // バットは手ボーンの回転ぶんしか動かない（暴れる差分を一切見ない）。
                // テイクバックは手が引かれるぶんだけ引けてトップが保たれる。
                // 位置（握り点）は両掌の**中点**なので平均されていて暴れない＝そのまま使う。
                // 片手フォロースルーで既に使っている「最後の見た目を焼いて手に追従させる」のと同じ流儀。
                // テイクバックは**体に対して**バットを止める（＝トップの位置を保つ。監督要望
                // 「構えから少し引くくらいでトップを保って欲しい」）。手に対して剛体にするだけでは、
                // 収録の手首のコックにバットが付いていって1コマ39〜69°動く（2026-07-31 実測）。
                // 体に固定すれば、テイクバック中のバットは**手が引かれるぶん平行移動するだけ**になる。
                // 振り出し（BatterSwingStart）からは手に対する剛体へ短く渡す＝スイングは収録の手に従う。
                // 止める先は**体の向きでもなく、焼いた向きそのもの**にする。体基準だと、
                //   ①構え(119°)→スイング(93.8°)のヨー補間 ②ルートモーションの根の回転
                // が乗ってテイクバックの前後で50〜61°動いた（clip 0.61→0.72 の間だけ0°だった。実測）。
                // 向きを凍結すれば、テイクバック中のバットは**手が引かれるぶん平行移動するだけ**になる。
                //
                // 振り出し以降を「収録の手に渡す」のも**やめた**（2026-07-31 第2弾）。手だけで運んだ軸を
                // 実測すると、トップから接触までに 上向き0.69→**真上0.99**→0.87→0.74→接触の瞬間は
                // **捕手向き(0.18)** と暴れており、肝心の接触の姿勢が「芯が捕手を向く」＝軌道にならない。
                // 収録の手首は道具を持たない前提の振りなので、そもそもバット軸の情報を持っていない。
                bool onIdleLoop = _batter.Clip == _batter.IdleClip;
                if (onIdleLoop)
                {
                    _batHeldAxis = axis;
                    _batHeldValid = true;
                    _batWrapValid = false;   // 次のスイングへ巻き付きの起点を持ち越さない
                    _batRestValid = false;
                }
                var grip = (lh + rh) * 0.5f;
                if (!onIdleLoop && battingClip && _batHeldValid) axis = SwingBatAxis(grip, RestFrameBone());
                // スイング面を**バット単体**へ掛ける（SwingPlaneBatOnly。腕は収録のまま動かさない）。
                // 傾きは握り点まわりの回転＝ノブは手に残るので離れない。持ち上げは平行移動なので
                // その量だけバットが手から浮く（承知の上で使うつまみ）。掛ける窓は FixGrip と共有＝
                // 別々に持つと腕とバットが違う時刻で動き出す
                float batOnly = battingClip
                    ? Mathf.Clamp01(_cal.SwingPlaneBatOnly) * DuelBatterPose.SwingWindow(_cal, _batter.Time)
                    : 0f;
                if (batOnly > 0.001f)
                {
                    if (Mathf.Abs(_cal.SwingPlaneTiltDeg) > 0.01f)
                    {
                        // axis はノブ→バレル。n=Cross(水平axis, up) まわりの正回転でバレル端が上がる
                        var h2 = new Vector3(axis.x, 0f, axis.z);
                        if (h2.sqrMagnitude > 1e-6f)
                        {
                            var n = Vector3.Cross(h2, Vector3.up).normalized;
                            axis = (Quaternion.AngleAxis(_cal.SwingPlaneTiltDeg * batOnly, n) * axis).normalized;
                        }
                    }
                    grip += Vector3.up * (_cal.SwingLiftM * batOnly);
                }
                float halfLen = Mathf.Max(0.1f, _cal.BatLengthM) * 0.5f;
                _bat.transform.localScale = new Vector3(1f, halfLen / BatHalfLength, 1f);
                _bat.transform.rotation = Quaternion.FromToRotation(Vector3.up, axis);
                _bat.transform.position = grip + axis * (halfLen - _cal.BatGripBelowM);
                // 片手期の追従用に「最後に両手で持てていた見た目」を毎回焼き直す。
                // 「離れた瞬間に初回焼き付け」だと、クリップ切替直後は直前クリップ末尾の残骸姿勢を
                // 拾ってゴミオフセットが固定される（構えでバットが地面に刺さる事故の真因）
                _batLocalPos = lhT.InverseTransformPoint(_bat.transform.position);
                _batLocalRot = Quaternion.Inverse(lhT.rotation) * _bat.transform.rotation;
                _batGoodValid = true;
                return;
            }

            // 片手フォロースルー（右打者は振り抜きで右手が離れる）: 最後の両手整列時の見た目のまま
            // 下の手（左手）ボーンに追従させる。ボーン軸の向きを仮定しない＝焼き付けオフセットなので
            // 素体が変わっても壊れない
            _bat.transform.position = lhT.TransformPoint(_batLocalPos);
            _bat.transform.rotation = lhT.rotation * _batLocalRot;
        }

        // グラブの寸法[m]（幅・高さ・厚み）と色。捕手ミットは丸く大きい、野手グラブはやや縦長で薄い。
        // 真円だと「胸に貼った円盤」に見えるので、縦をわずかに勝たせて楕円にする（2026-07-29 寄り検証）
        private static readonly Vector3 MittSize = new(0.25f, 0.27f, 0.12f);
        private static readonly Vector3 GloveSize = new(0.23f, 0.26f, 0.10f);
        private static readonly Color LeatherColor = new(0.45f, 0.27f, 0.15f);

        /// <summary>
        /// 手からポケット面へ出す量[m]（コンテナ+Z＝掌の法線）。実物のグラブは掌の側へ張り出す。
        /// **手がポケットの空洞に入らないだけ出す**のが要点。生成メッシュの+Z面は開いたポケット＝
        /// えぐれた空洞なので、寄せすぎると指がそこから前へ突き抜けて見える（2026-07-29 監督指摘）。
        /// 実測（グラブ座標系）: 指先 z≈+0.035〜+0.046・手の甲 z≈-0.010。
        /// 中心を +0.035 に置くと奥行0.125（±0.0625）の中へ手が丸ごと収まる。
        /// </summary>
        private const float GloveForward = 0.035f;

        /// <summary>
        /// 手の長さ（手首→指先）を中指付け根までの距離から見積もる倍率。
        /// 指先ボーンは Humanoid 必須でないので、実測できる手首→中指付け根で代用する。
        /// </summary>
        private const float HandLengthFromKnuckle = 1.85f;

        /// <summary>
        /// グラブ・ミットを利き手でない側へ装着する。**呼び出しは初期ポーズ評価後**（AttachGearと同じ）。
        ///
        /// 位置: 手ボーンの原点は**手首**なので、そのまま子にすると球が手首に刺さって
        /// 「腕の先に肉団子」になる。手首→指先の中点（＝手の中心）へグラブの中心を合わせる。
        /// 向き: ボーン軸は素体依存で信用できない（バット・被り物と同じ教訓）ので、**手の実測**で
        /// 座標系を組む。縦軸=手首→中指付け根、正面=掌の法線（人差し指付け根→小指付け根 と縦軸の外積）。
        /// 法線の符号だけは幾何では決まらないので、焼き付けポーズで掌が相手を向く事実を使って
        /// 視線ターゲット方向との内積で選ぶ。
        ///
        /// **正面を視線方向そのものにしてはいけない**: グラブの薄い軸(Z=0.115m)が視線に固定されるので、
        /// 掌が相手を向かないポーズ（立ち姿・投球前後）では手が薄い板を突き抜けて指が外へ出る
        /// （2026-07-29 監督指摘「左手が少し見えてる」の真因）。掌の法線に張れば、どの姿勢でも
        /// グラブの広い面（0.245×0.295m）が手のひらの面と重なる。
        /// </summary>
        private void AttachGlove(Actor actor, HumanBodyBones handBone, HumanBodyBones knuckleBone,
            Vector3 gazeLocal, Vector3 size, int layer, Mesh mesh = null)
        {
            if (actor == null || actor.Animator == null) return;
            var hand = actor.Animator.GetBoneTransform(handBone);
            if (hand == null) return;
            bool left = handBone == HumanBodyBones.LeftHand;
            var knuckle = actor.Animator.GetBoneTransform(knuckleBone);
            var index = actor.Animator.GetBoneTransform(
                left ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal);
            var little = actor.Animator.GetBoneTransform(
                left ? HumanBodyBones.LeftLittleProximal : HumanBodyBones.RightLittleProximal);

            var up = knuckle != null ? knuckle.position - hand.position : hand.up;
            up = up.sqrMagnitude > 1e-6f ? up.normalized : Vector3.up;
            var target = _root.transform.TransformPoint(gazeLocal);
            var gaze = target - hand.position;
            gaze -= Vector3.Dot(gaze, up) * up;   // 縦軸と直交化
            gaze = gaze.sqrMagnitude > 1e-6f ? gaze.normalized : Vector3.forward;

            // 掌の法線。軸は手のひらを横断する向きと縦軸の外積で得られるが、**符号は外積では決まらない**
            // （左右で手系が入れ替わる）。符号は「指が丸まっていく側＝掌側」を実測して決める。
            // 視線との内積で決めていた版は符号を取り違え、グラブが手の甲側に付いて指が丸見えだった
            // （2026-07-29 実測: 指先が全部 z=-0.035〜-0.046 なのにグラブ中心は +0.050）
            var tip = actor.Animator.GetBoneTransform(
                left ? HumanBodyBones.LeftMiddleDistal : HumanBodyBones.RightMiddleDistal);
            var curl = Vector3.zero;
            if (tip != null && knuckle != null)
            {
                curl = tip.position - knuckle.position;
                curl -= Vector3.Dot(curl, up) * up;
            }
            var fwd = gaze;
            if (index != null && little != null)
            {
                var n = Vector3.Cross(up, index.position - little.position);
                n -= Vector3.Dot(n, up) * up;
                if (n.sqrMagnitude > 1e-8f)
                {
                    n.Normalize();
                    // 指の丸まりが十分あればそれで符号を決め、無ければ（指が真っ直ぐな素体）視線で代用する
                    float sign = curl.magnitude > 0.01f ? Vector3.Dot(n, curl) : Vector3.Dot(n, gaze);
                    fwd = sign < 0f ? -n : n;
                }
            }
            var rot = Quaternion.LookRotation(fwd, up);
            var c = GearContainer(hand, "gear-glove", rot);
            // コンテナ原点＝手首なので、手の中心（手首→指先の中点）へグラブの中心を置く＝手が
            // 一番肉のある所に収まる。掌に合わせると手が踵側の薄い所へ寄って甲がはみ出す
            // （2026-07-29 実測: 縦寸の1/3も上げると投球ポーズで手の甲が丸見えになった）
            var lp = Vector3.zero;
            if (knuckle != null)
                lp.y = (knuckle.position - hand.position).magnitude * HandLengthFromKnuckle * 0.5f;
            else
                lp.y = (mesh != null ? mesh.bounds.size.y : size.y) * 0.25f;
            lp.z += GloveForward;   // 掌の上に重ねると手に飲まれる＝ポケットぶん前へ出す
            if (mesh != null)
                GearMeshPart(c, mesh, "prop-glove", lp, Quaternion.identity, LeatherColor, layer);
            else
                GearPart(c, PrimitiveType.Sphere, "prop-glove", lp, size, LeatherColor, layer);
        }

        // ---- 被り物・防具（design-18 §3.3。役割ごとに出し分け＝打者ヘルメット/守備帽子） --------

        /// <summary>役割別の装備セット。高校野球のヘルメットは両耳フラップ付き＝帽子とシルエットで判別できる。</summary>
        private enum GearKind { Cap, Helmet, CatcherGear, UmpireGear }

        // 捕手の被り物の素の装着位置[m]（gear-headコンテナ＝頭ボーン原点・+Y頭蓋上・+Z顔前）。
        // キャリブレーションの CatcherHelmetOffset / CatcherMaskOffset はこれに**加算**される
        private static readonly Vector3 CatcherHelmetBase = new(0f, 0.125f, -0.015f);
        private static readonly Vector3 CatcherMaskBase = new(0f, 0.090f, 0.075f);

        /// <summary>
        /// 球審マスク（捕手と同じ生成メッシュ）の装着位置[m]。起点は捕手と同値だが、
        /// U2素体（恰幅型）の頭に合わせて独立に調整できるよう定数を分ける（捕手側を巻き込まない）。
        /// </summary>
        private static readonly Vector3 UmpireMaskBase = new(0f, 0.090f, 0.075f);

        /// <summary>胸プロテクターの装着位置[m]（gear-chestコンテナ＝胸ボーン原点・+Z体前）。</summary>
        private static readonly Vector3 ChestMeshBase = new(0f, 0f, 0.085f);

        /// <summary>打者ヘルメットの被り角の回転中心（殻 helmet-shell の中心。AttachGear の配置と一致させる）。</summary>
        private static readonly Vector3 HelmetPivot = new(0f, 0.13f, 0.02f);

        /// <summary>
        /// 装備をボーンへ装着する。呼び出しは**初期ポーズ評価後**（コンストラクタ末尾のStepActor後）限定。
        ///
        /// 向きの決め方: ボーンのローカル軸もTポーズの正面も素体・クリップ依存で信用できない
        /// （バットの実装知見＋2026-07-28実測: Tポーズ正面=yaw仮定で焼くと打者のつばが後頭部を向いた）。
        /// そのため**初期ポーズで「視線ターゲット」（投手→本塁、他3者→マウンド）への水平方向を実測**し、
        /// そのワールド姿勢をボーンの子へ焼き付ける。野球のポーズは構えで必ず相手を見るので、
        /// 顔の向き＝視線方向が成り立つ。以後はボーン追従でお辞儀・しゃがみにも自然に付いてくる。
        ///
        /// 寸法はUBC頭部の実測（2026-07-28: 頭ボーン基準で幅0.18・奥行き0.22・頭頂+0.21）から。
        /// コンテナはlossyScale逆数でワールドスケール1に戻してあるので、子パーツは実寸(m)で置く。
        /// </summary>
        private void AttachGear(Actor actor, GearKind kind, Vector3 gazeLocal, Color accent, int layer)
        {
            var head = actor.Animator.GetBoneTransform(HumanBodyBones.Head);
            var dark = new Color(0.13f, 0.13f, 0.15f);
            var target = _root.transform.TransformPoint(gazeLocal);
            // 装着ボーンから視線ターゲットへの水平方向（つば・プロテクター正面。上下向きはボーン追従に任せる）
            Quaternion GazeRot(Transform bone)
            {
                var d = target - bone.position;
                d.y = 0f;
                return Quaternion.LookRotation(d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.forward);
            }
            // 頭専用: 上方向をワールド上でなく頭蓋実測（SpawnActorのTポーズ計測）に合わせる。
            // GazeRotのワールド上仮定は焼き付けポーズで頭が水平なとき（捕手・球審）しか成立せず、
            // 頭が傾く姿勢で焼く投手（リリース）・打者（構え）は被り物が頭蓋に対して斜めに接着されて
            // 「帽子が尖る／ヘルメットが後頭部へずれて髪に見える」実害が出た（2026-07-28 スクショ）
            Quaternion SkullRot(Transform bone)
            {
                var up = bone.TransformDirection(actor.HeadUpLocal);
                up = up.sqrMagnitude > 1e-6f ? up.normalized : Vector3.up;
                // 前も素立ち実測（顔正面）を現ポーズへ順変換して使う。旧実装の
                // 「視線ターゲットへの水平方向」は、ベイク姿勢で頭が傾いていると up と直交化した
                // 枠がロールし、つばが顔を斜めに横切る「斜め被り」になった（2026-07-28 投手で実害）
                var d = bone.TransformDirection(actor.HeadFwdLocal);
                d = d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.forward;
                var fwd = d - Vector3.Dot(d, up) * up;
                fwd = fwd.sqrMagnitude > 1e-6f ? fwd.normalized : d;
                return Quaternion.LookRotation(fwd, up);
            }

            if (head != null)
            {
                var c = GearContainer(head, "gear-head", SkullRot(head));
                // 帽子・ヘルメットは装着位置を較正できるようオフセット枠を1段挟む。コンテナは実寸空間なので
                // 枠の localPosition がそのまま[m]（x=左右/y=頭蓋上/z=顔前）。ApplyCalibration が流し込む
                var mount = c;
                if (kind == GearKind.Cap || kind == GearKind.Helmet)
                {
                    mount = new GameObject("gear-offset");
                    mount.transform.SetParent(c.transform, false);
                    if (kind == GearKind.Cap) _capMount = mount.transform;
                    else _helmetMount = mount.transform;
                }
                switch (kind)
                {
                    case GearKind.Cap:
                        // 生成メッシュ装備（較正済み: クラウン中心=原点・つば+Z）。無ければ従来プリミティブ。
                        // 位置決めの学び（2026-07-28監督2往復）: メッシュクラウンは深い（縁まで0.11）ので
                        // 「拡大＋下げ」は顔まで飲み込む。正解は**眉上（縁y≈0.08）まで持ち上げ**、
                        // 後頭部の露出はわずかな後ろ寄せだけで拾う
                        if (DuelGearMeshes.Cap() != null)
                        {
                            GearMeshPart(mount, DuelGearMeshes.Cap(), "cap-mesh",
                                new Vector3(0f, 0.185f, -0.025f), Quaternion.identity, accent, layer);
                        }
                        else
                        {
                            // 実測頭寸に被せる: クラウンは頭頂(+0.21)を覆い、側頭±0.09を包む
                            GearPart(mount, PrimitiveType.Sphere, "cap-crown", new Vector3(0f, 0.155f, 0.01f),
                                new Vector3(0.21f, 0.15f, 0.23f), accent, layer);
                            GearPart(mount, PrimitiveType.Cube, "cap-brim", new Vector3(0f, 0.125f, 0.145f),
                                new Vector3(0.17f, 0.012f, 0.09f), accent, layer);
                        }
                        break;
                    case GearKind.Helmet:
                        // ヘルメットはAIメッシュ不採用（2026-07-28確定）: 生成殻は開口・付属タブの向きが
                        // 制御できず、頭が開口を突き抜け＋タブが後ろ髪に見える「おかっぱ」事故になった
                        // （helm-closeup-0.png）。マスク（FaceCage）と同じ結論で、殻＋つば＋両耳フラップの
                        // **構造がある装備はプリミティブ構成**で作る。
                        // 帽子より低く深く被る（額は眉まで覆い、顔だけ出す）。初版は帽子と同じ高さに置いて
                        // 額が露出し「赤い髪」に見えた → 中心を下げ縦径を拡大
                        GearPart(mount, PrimitiveType.Sphere, "helmet-shell", new Vector3(0f, 0.13f, 0.02f),
                            new Vector3(0.24f, 0.20f, 0.26f), accent, layer);
                        GearPart(mount, PrimitiveType.Cube, "helmet-brim", new Vector3(0f, 0.12f, 0.16f),
                            new Vector3(0.17f, 0.012f, 0.07f), accent, layer);
                        GearPart(mount, PrimitiveType.Sphere, "helmet-ear-l", new Vector3(-0.11f, 0.05f, 0.02f),
                            new Vector3(0.035f, 0.11f, 0.10f), accent, layer);
                        GearPart(mount, PrimitiveType.Sphere, "helmet-ear-r", new Vector3(0.11f, 0.05f, 0.02f),
                            new Vector3(0.035f, 0.11f, 0.10f), accent, layer);
                        break;
                    case GearKind.CatcherGear:
                        // 日本の高校野球の捕手は米国式フルフェイスではなく「捕手用ヘルメット＋独立マスク」の
                        // 2点構成（2026-07-28監督指摘＋実物写真）。生成メッシュはその2点をそのまま持つ。
                        //
                        // 2026-07-28 に一度「AI生成マスクは顔を塞ぐのでプリミティブのケージが正解」と結論したが、
                        // これは**テキスト生成の結論**だった。実物写真を渡した画像生成（Meshy）では隙間の抜けた
                        // ケージが出た（2026-07-29・私の見立てが外れた）ので、実メッシュへ差し替える。
                        // ヘルメットは通気孔の穴でCollapseが頭打ちになるため voxel remesh 前処理が要る
                        // （tools/aigear-decimate.py のコメント参照）。
                        var cHelmet = DuelGearMeshes.CatcherHelmet();
                        var cMask = DuelGearMeshes.CatcherMask();
                        if (cHelmet != null && cMask != null)
                        {
                            _cHelmetMount = OffsetMount(c, "cmask-helm-offset");
                            _cMaskMount = OffsetMount(c, "cmask-cage-offset");
                            GearMeshPart(_cHelmetMount.gameObject, cHelmet, "cmask-helm",
                                CatcherHelmetBase, Quaternion.identity, accent, layer);
                            // ケージは黒（実物は金属の艶消し黒）。殻のチーム色と分けることで
                            // 「被り物＋防具」の2点に読める
                            GearMeshPart(_cMaskMount.gameObject, cMask, "cmask-cage",
                                CatcherMaskBase, Quaternion.identity, dark, layer);
                        }
                        else
                        {
                            GearPart(c, PrimitiveType.Sphere, "cmask-shell", new Vector3(0f, 0.145f, -0.02f),
                                new Vector3(0.23f, 0.18f, 0.22f), accent, layer);
                            FaceCage(c, "cmask", accent, layer);
                        }
                        break;
                    case GearKind.UmpireGear:
                        // 球審: 帽子型クラウン＋マスク。マスクは捕手と同じ生成メッシュ（2026-08-05 監督指示。
                        // クリーン化＝960×540・ポスタライズOFFで自作ケージバーの粗が見えるようになったため）。
                        // 生成メッシュが無い環境（gitignore資産の未導入）では従来のプリミティブケージへ落ちる
                        GearPart(c, PrimitiveType.Sphere, "umask-crown", new Vector3(0f, 0.155f, -0.01f),
                            new Vector3(0.21f, 0.15f, 0.21f), dark, layer);
                        var uMask = DuelGearMeshes.CatcherMask();
                        if (uMask != null)
                            GearMeshPart(c, uMask, "umask-cage", UmpireMaskBase, Quaternion.identity, dark, layer);
                        else
                            FaceCage(c, "umask", dark, layer);
                        break;
                }
            }

            if (kind == GearKind.CatcherGear || kind == GearKind.UmpireGear)
            {
                // 胸プロテクター（UpperChestはHumanoidマッピングで欠けることがある→Chest→Spineへ落ちる）
                var chest = actor.Animator.GetBoneTransform(HumanBodyBones.UpperChest);
                if (chest == null) chest = actor.Animator.GetBoneTransform(HumanBodyBones.Chest);
                if (chest == null) chest = actor.Animator.GetBoneTransform(HumanBodyBones.Spine);
                if (chest != null)
                {
                    var c = GearContainer(chest, "gear-chest", GazeRot(chest));
                    bool catcherGear = kind == GearKind.CatcherGear;
                    // 実物のプロテクターは横一列のパッド（ロール）が段々に積まれ、間に溝・上に肩当て・
                    // 首元に切り欠きがある。箱1枚だと「板」に見える（2026-07-28 監督指摘「丸みと窪みが無い」）ため、
                    // 横倒しカプセルを縦に積む＝Lambert陰影がロールの丸みと段間の溝を描く。
                    // 行の意味: (y, z, 幅W, 管径D)。腹へ向けて幅を絞り、中段をわずかに前へ出して体に沿う凸を作る。
                    // Capsuleの軸はローカルYなので z90°回転で横倒し、scale=(D, W/2, D)
                    var rows = catcherGear
                        ? new[]
                        {
                            new Vector4(0.065f, 0.100f, 0.30f, 0.085f),
                            new Vector4(-0.015f, 0.105f, 0.30f, 0.085f),
                            new Vector4(-0.090f, 0.100f, 0.26f, 0.080f),
                            new Vector4(-0.155f, 0.090f, 0.22f, 0.075f),
                        }
                        : new[]   // 球審は胸だけの薄型3段
                        {
                            new Vector4(0.050f, 0.085f, 0.24f, 0.070f),
                            new Vector4(-0.015f, 0.090f, 0.24f, 0.070f),
                            new Vector4(-0.075f, 0.085f, 0.21f, 0.065f),
                        };
                    // 生成メッシュ（Meshy画像生成・較正済み: 縦0.44m・肩当て込み・パッド面+Z）。
                    // 首元まで上げて着せる。球審は薄型プリミティブ維持（服の下に着る前提のシルエット）
                    bool chestMesh = catcherGear && DuelGearMeshes.Chest() != null;
                    if (chestMesh)
                    {
                        // 高さは**肩ボーン（UpperArm頭）の実測**へメッシュ上端（肩当ての天面）を合わせる。
                        // コンテナのボーンは UpperChest/Chest/Spine のどれに落ちるかが素体依存で、
                        // 原点の高さが揃わない＝固定オフセットだと腰まで下がる（2026-07-29 実害）。
                        // 旧実装が肩当てだけ肩ボーン実測で置いていたのと同じ理由
                        var chestGear = DuelGearMeshes.Chest();
                        float shoulderY = 0f; int shoulderN = 0;
                        foreach (var armBone in new[] { HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm })
                        {
                            var arm = actor.Animator.GetBoneTransform(armBone);
                            if (arm == null) continue;
                            shoulderY += c.transform.InverseTransformPoint(arm.position).y;
                            shoulderN++;
                        }
                        float top = shoulderN > 0
                            ? shoulderY / shoulderN + 0.045f    // 肩の稜線の少し上
                            : chestGear.bounds.extents.y;       // 肩ボーンが無い素体は素の中心置き
                        _cChestMount = OffsetMount(c, "chest-offset");
                        GearMeshPart(_cChestMount.gameObject, chestGear, "chest-mesh",
                            new Vector3(0f, top - chestGear.bounds.extents.y, ChestMeshBase.z),
                            Quaternion.identity, accent, layer);
                    }
                    else
                    {
                    for (int i = 0; i < rows.Length; i++)
                        GearPart(c, PrimitiveType.Capsule, "chest-pad-" + i,
                            new Vector3(0f, rows[i].x, rows[i].y),
                            new Vector3(rows[i].w, rows[i].z * 0.5f, rows[i].w),
                            accent, layer, new Vector3(0f, 0f, 90f));
                    }
                    // 肩当て: 肩の稜線の**上**に載せ、前後に跨がせる（前面だけに置くと「前掛け」に見える＝
                    // 2026-07-28監督指摘「肩の上を通って背中でパチンとするもの」）。左右の間＝首元の切り欠き。
                    // 位置は固定値でなく**肩ボーン（UpperArm頭）の実測**から: 固定値(±0.105)は内側すぎて
                    // 「襟の飾り」に見えた（スクショ実害）。素体・ポーズが変わってもずれない。
                    //
                    // ただし**生成メッシュの胸当ては肩当てを自前で持っている**ので、その時は載せない
                    // （2026-07-29。旧bpy製は胴だけの板だったため別置きが必要だった）。二重に置くと
                    // 肩が膨れる
                    if (!chestMesh)
                    {
                    var shoulderScale = catcherGear
                        ? new Vector3(0.115f, 0.055f, 0.175f)
                        : new Vector3(0.095f, 0.048f, 0.155f);
                    foreach (var (armBone, side) in new[]
                        { (HumanBodyBones.LeftUpperArm, "l"), (HumanBodyBones.RightUpperArm, "r") })
                    {
                        var arm = actor.Animator.GetBoneTransform(armBone);
                        if (arm == null) continue;
                        var lp = c.transform.InverseTransformPoint(arm.position);
                        lp.y += 0.055f;                              // 肩ボーン頭の少し上＝稜線
                        lp.x -= Mathf.Sign(lp.x) * 0.02f;            // 気持ち内側（腕の付け根に被せる）
                        lp.z += 0.005f;
                        GearPart(c, PrimitiveType.Sphere, "chest-shoulder-" + side,
                            lp, shoulderScale, accent, layer);
                    }
                    }
                    // 背面ストラップ: 肩から背中へ降りる縦帯2本＋背中の横バンド（バックルで留まる装具の記号）。
                    // 濃色の細帯にして「体の一部」ではなくベルトに読ませる
                    GearPart(c, PrimitiveType.Capsule, "strap-back-l",
                        new Vector3(-0.075f, 0.055f, -0.095f), new Vector3(0.030f, 0.095f, 0.030f), dark, layer);
                    GearPart(c, PrimitiveType.Capsule, "strap-back-r",
                        new Vector3(0.075f, 0.055f, -0.095f), new Vector3(0.030f, 0.095f, 0.030f), dark, layer);
                    GearPart(c, PrimitiveType.Cube, "strap-back-band",
                        new Vector3(0f, -0.035f, -0.100f), new Vector3(0.24f, 0.035f, 0.015f), dark, layer);
                }
            }

            if (kind == GearKind.CatcherGear)
            {
                // レガース。箱1枚は「板」に見える（2026-07-28監督指摘）ため、実物の構造どおり
                // 膝皿ドーム＋脛ロール3段＋足首ロールに分解する（胸プロテクターと同じ段積みの読み）。
                // 配置は「膝（calfボーン頭）→足首（footボーン頭）」の実測軸に沿わせる＝
                // しゃがみで脛が斜めでもロール列が脛から浮かない
                var legs = new[]
                {
                    (HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot),
                    (HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot),
                };
                foreach (var (calfBone, footBone) in legs)
                {
                    var calf = actor.Animator.GetBoneTransform(calfBone);
                    if (calf == null) continue;
                    var c = GearContainer(calf, "gear-leg", GazeRot(calf));
                    var foot = actor.Animator.GetBoneTransform(footBone);
                    // 膝→足首ベクトル（コンテナローカル）。footが無い素体はほぼ直下へフォールバック
                    var shin = foot != null
                        ? c.transform.InverseTransformPoint(foot.position)
                        : new Vector3(0f, -0.42f, 0f);
                    var fwd = new Vector3(0f, 0f, 0.065f);   // 脛の前面へのオフセット（コンテナ+Z=視線方向）
                    var legMesh = DuelGearMeshes.LegGuard();
                    if (legMesh != null)
                    {
                        // 生成メッシュ（bpy製・膝が+Y・較正済み）を膝→足首の実測軸に沿わせ、長さを合わせる
                        float len = shin.magnitude > 0.05f ? shin.magnitude : 0.42f;
                        float s = len * 1.05f / Mathf.Max(0.05f, legMesh.bounds.size.y);
                        GearMeshPart(c, legMesh, "leg-mesh", shin * 0.42f + fwd,
                            Quaternion.FromToRotation(Vector3.down, shin.normalized), accent, layer, s);
                    }
                    else
                    {
                    GearPart(c, PrimitiveType.Sphere, "leg-knee", shin * 0.04f + fwd + new Vector3(0f, 0f, 0.012f),
                        new Vector3(0.115f, 0.100f, 0.075f), accent, layer);
                    foreach (var t in new[] { 0.32f, 0.52f, 0.72f })
                        GearPart(c, PrimitiveType.Capsule, "leg-shin-" + t,
                            shin * t + fwd, new Vector3(0.055f, 0.055f, 0.055f), accent, layer,
                            new Vector3(0f, 0f, 90f));
                    GearPart(c, PrimitiveType.Capsule, "leg-ankle", shin * 0.90f + fwd * 0.9f,
                        new Vector3(0.048f, 0.045f, 0.048f), accent, layer, new Vector3(0f, 0f, 90f));
                    }
                }
            }
        }

        /// <summary>装備の位置オフセット枠（gear-head直下・実寸[m]）。ApplyCalibration が localPosition を流し込む。</summary>
        private static Transform OffsetMount(GameObject parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            return go.transform;
        }

        /// <summary>
        /// 装備コンテナ: ボーンの子に置き、素体の正規化スケールを打ち消して（lossyScale逆数）
        /// 実寸(m)でパーツを置ける空間を作る。ワールド姿勢はキャラ正面（yaw）に焼き付ける。
        /// </summary>
        private static GameObject GearContainer(Transform bone, string name, Quaternion worldRot)
        {
            var c = new GameObject(name);
            c.transform.SetParent(bone, false);
            var s = bone.lossyScale;
            c.transform.localScale = new Vector3(1f / s.x, 1f / s.y, 1f / s.z);
            c.transform.position = bone.position;
            c.transform.rotation = worldRot;
            return c;
        }

        private GameObject GearPart(GameObject container, PrimitiveType type, string name,
            Vector3 localPos, Vector3 localScale, Color color, int layer, Vector3 localEuler = default)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(container.transform, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.Euler(localEuler);
            go.transform.localScale = localScale;
            go.GetComponent<MeshRenderer>().sharedMaterial = FlatMaterial(color);
            go.layer = layer;
            return go;
        }

        /// <summary>
        /// 顔前ケージ（マスクの記号）。実物のマスクは顔を塞がず細いバーの隙間から視線が通る
        /// （2026-07-28 監督指摘＋実物写真）。横バー4本＋左右レール＋額・顎パッドで
        /// 「覆わずに守る」を作る。バーはピクセル化で格子の気配に落ち、肌色（顔）が残る。
        /// </summary>
        private void FaceCage(GameObject container, string prefix, Color pad, int layer)
        {
            var dark = new Color(0.13f, 0.13f, 0.15f);
            GearPart(container, PrimitiveType.Cube, prefix + "-pad-brow", new Vector3(0f, 0.125f, 0.10f),
                new Vector3(0.15f, 0.028f, 0.035f), pad, layer);
            GearPart(container, PrimitiveType.Cube, prefix + "-pad-chin", new Vector3(0f, -0.065f, 0.095f),
                new Vector3(0.11f, 0.030f, 0.035f), pad, layer);
            // バーは平面グリッドでなく**顔の丸みに沿って左右へ回り込む**（2026-07-28監督指摘
            // 「死角になってしまっている。実際は顔の形に合わせて丸みがある」）。
            // 中央＋左右ウィングの3分割アークにすると、どの視線角でも必ずどこかの隙間が抜ける
            foreach (var y in new[] { 0.085f, 0.04f, -0.005f, -0.05f })
            {
                GearPart(container, PrimitiveType.Cube, prefix + "-bar" + y, new Vector3(0f, y, 0.125f),
                    new Vector3(0.10f, 0.010f, 0.010f), dark, layer);
                GearPart(container, PrimitiveType.Cube, prefix + "-barL" + y, new Vector3(-0.072f, y, 0.103f),
                    new Vector3(0.062f, 0.010f, 0.010f), dark, layer, new Vector3(0f, 42f, 0f));
                GearPart(container, PrimitiveType.Cube, prefix + "-barR" + y, new Vector3(0.072f, y, 0.103f),
                    new Vector3(0.062f, 0.010f, 0.010f), dark, layer, new Vector3(0f, -42f, 0f));
            }
            // 縦レールもアークの折れ目（頬の位置）へ後退させる
            GearPart(container, PrimitiveType.Cube, prefix + "-rail-l", new Vector3(-0.092f, 0.02f, 0.082f),
                new Vector3(0.010f, 0.19f, 0.010f), dark, layer);
            GearPart(container, PrimitiveType.Cube, prefix + "-rail-r", new Vector3(0.092f, 0.02f, 0.082f),
                new Vector3(0.010f, 0.19f, 0.010f), dark, layer);
        }

        /// <summary>較正済み装備メッシュ（DuelGearMeshes）をコンテナ実寸空間へ置く。scale は一様倍率。</summary>
        private GameObject GearMeshPart(GameObject container, Mesh mesh, string name,
            Vector3 localPos, Quaternion localRot, Color color, int layer, float scale = 1f)
        {
            var go = new GameObject(name);
            go.transform.SetParent(container.transform, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = localRot;
            go.transform.localScale = Vector3.one * scale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = FlatMaterial(color);
            go.layer = layer;
            return go;
        }

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform) SetLayerRecursive(child.gameObject, layer);
        }

        // ---- 左右ミラー（issue #337・design-18 §3.4）--------------------------------
        // ⚠ **エディタ限定にしない**。左打者・左投手はゲームの中身であって開発用の観測窓ではない。
        //    2026-08-01 の最初の Player ビルドで、この一式が録画ハーネスの #if UNITY_EDITOR の
        //    中に紛れていて `_batterMirror` が未定義＝ビルドが通らないことが分かった
        //    （エディタでは常に定義済みなので tools/check-unity-compile.sh でも出ない）。
        // 左打者・左投手は「x=0面（投球線を含む鉛直面）の鏡映親ノード」で作る。
        // 素体GO自身の localScale.x=-1 は**回転より内側**で効くため、ヨー付きの素体では
        // 左右でなく前後の鏡になる（投手ヨー270°でスパイク実測）。無回転の親を反転すると
        // ワールドの左右鏡になり、立ち位置（BatterX -0.71→+0.71）・ヨー・骨・ボーン子の装備・
        // グラブのメッシュまで一括で映る＝道具のアタッチ先ボーン反転（旧§3.4の注意書き）は不要。
        // グラブは左手ボーンのまま視覚上は右手側へ移り、メッシュも鏡映＝左投手用の右手グラブになる。
        // 捕手・球審は反転しない（実物の捕手ミットは常に左手）。並びだけ打者と反対側へ寄せる（§7-1）。
        private Transform _batterMirror, _pitcherMirror;
        private bool _batterLeft, _pitcherLeft;

        /// <summary>現在の打者の側の符号（右打者=+1／左打者=-1）。捕手・球審の x オフセットに掛ける。</summary>
        private float BatterSideSign => _batterLeft ? -1f : 1f;

        /// <summary>
        /// 捕手の較正上の基準x（issue #396 前の唯一の値）。<see cref="StanceCatcherToAim"/> が
        /// 投球ごとに立ち位置を動かすため、以後は「今どこにいるか」ではなく常にこの固定値を基準にする。
        /// </summary>
        private float CatcherBaseX => _cal.CatcherX * BatterSideSign;

        /// <summary>
        /// 鏡映親ノードを1枚挟む（生成時に両対象へ常設。恒等スケールの間は描画へ一切影響しない＝
        /// 右打者・右投手の絵は従来とビット一致）。
        /// </summary>
        private Transform WrapInMirrorNode(Actor a, string name)
        {
            if (a == null) return null;
            var node = new GameObject(name);
            node.transform.SetParent(_root.transform, false);   // 恒等＝x=0面の鏡の枠
            a.Go.transform.SetParent(node.transform, true);     // ローカル姿勢は数値そのまま
            return node.transform;
        }

        /// <summary>
        /// 打者・投手の左右を差し替える（打席開始・対決ビュー再表示時に MatchLive 側から呼ぶ）。
        /// スイッチヒッターの「どちらの打席に立つか」は呼び出し側で解決して bool で渡す。
        /// </summary>
        public void SetHandedness(bool batterLeft, bool pitcherLeft)
        {
            if (!Available) return;
            if (_batterMirror != null && _batterLeft != batterLeft)
            {
                _batterLeft = batterLeft;
                ApplyMirror(_batterMirror, _batter, batterLeft);
            }
            if (_pitcherMirror != null && _pitcherLeft != pitcherLeft)
            {
                _pitcherLeft = pitcherLeft;
                ApplyMirror(_pitcherMirror, _pitcher, pitcherLeft);
            }
            // 本塁組の並び（§7-1「本塁組の左右の並びは打者の左右打席で入れ替わる」）:
            // 球審のスロットは常に打者と反対側の肩越し。捕手のわずかな寄りも合わせて映す
            SetActorXZ(_catcher, _cal.CatcherX * BatterSideSign, _cal.CatcherZ);
            SetActorXZ(_umpire, _cal.UmpireX * BatterSideSign, _cal.UmpireZ);
        }

        private static void ApplyMirror(Transform node, Actor a, bool on)
        {
            node.localScale = new Vector3(on ? -1f : 1f, 1f, 1f);
            if (a == null) return;
            // Humanoid Foot IK はミラー中OFF（SyncFootIk のコメント参照。両脚が頭の高さへ折り畳まれる）
            a.Mirrored = on;
            SyncFootIk(a);
            // 負スケール祖先の下では SMR の自動バウンズが狂い、寄りのカメラで一部メッシュ（購入素体の
            // ズボン等）がフラスタムカリングで消える（スパイク実測: 遠景は出るが近接で脚が消える）。
            // ミラー中だけ固定バウンズへ切り替えてカリングを黙らせる。箱は「バインド箱の3倍」ではダメ:
            // sharedMesh.bounds はメッシュ空間、localBounds はルートボーン空間で、空間を取り違えたまま
            // 拡大しても置き場所がずれたまま＝打者のズボンだけカメラ錐台から外れて脚が消えた（受け入れ撮影で実害。
            // 投手は偶々重なって描けるので気づけない）。ルートボーン（腰）から全身は高々2m弱なので、
            // 原点±5mの巨大箱にして空間の取り違え自体を無効化する（対象は対決ビューの数体だけ＝カリング損は無視できる）
            foreach (var smr in a.Go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (on)
                {
                    smr.updateWhenOffscreen = false;
                    smr.localBounds = new Bounds(Vector3.zero, Vector3.one * 10f);
                }
                else
                {
                    smr.updateWhenOffscreen = true;   // 非ミラーは従来どおり実ポーズ追従（SpawnActorの既定）
                }
            }
            // 剛体メッシュ（帽子・靴・ソックス・グラブ等のボーン直付け）は負スケールで巻き順が裏返り、
            // 閉じたメッシュは全面裏向き＝完全不可視になる（受け入れ撮影で実害: 左打者の靴・ソックスが消えた）。
            // ミラー中のアクターだけ両面描画（_CullMode=0）へ切り替える。シェーダ側は SV_IsFrontFace で
            // 裏面の法線を反転して陰影を保つ。マテリアルは全てアクター毎のインスタンス
            // （FlatMaterial / RegionMaterial / GearPart）なので他アクターへは波及しない
            foreach (var r in a.Go.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                    if (m != null && m.HasProperty("_CullMode"))
                        m.SetFloat("_CullMode", on ? 0f : 2f);
        }

        /// <summary>
        /// 飛翔中の球の実測（issue #332）。絵ではカメラの狙い外しと配管ミス（符号違い・単位違い）が
        /// 区別できないので、位置と捕手ミット・打者の頭との距離を数値で残す。
        /// </summary>
#if UNITY_EDITOR
        // ---- 【開発用・エディタ限定】録画ハーネス（DuelViewRecorder）向け観測窓 ----

        /// <summary>
        /// グラブの装着結果を実測で返す（絵の目測でなく数値で確かめるため。2026-07-29 追加）。
        /// 寄りカメラの狙いを外すと「付いていない」のか「見えていない」のか切り分けられないので、
        /// 掌の位置とグラブ実体の位置を直接突き合わせる。
        /// </summary>
        /// <summary>
        /// 【開発ツール用】ボールの実測。キャリブレーション窓が毎フレーム表示する＝スライダを動かしながら
        /// 数値を0へ寄せられる（絵を撮らずに合わせる）。Editorアセンブリから呼ぶので public。
        /// </summary>
        /// <summary>
        /// 打球の実測（2026-07-31）。「枠から出た」のか「そもそも出ていない」のかは絵では区別できない
        /// （どちらも球が写らない）ので、打球時計・位置・**ビューポート座標**・残像の点数を数値で残す。
        /// </summary>
        public string BattedDiagForCapture()
        {
            if (_battedT < 0f) return "batted=なし";
            var local = BattedPointAt(_battedT);
            var vp = _camera.WorldToViewportPoint(_root.transform.TransformPoint(local));
            bool inFrame = vp.z > 0f && vp.x >= 0f && vp.x <= 1f && vp.y >= 0f && vp.y <= 1f;
            return "batted t=" + _battedT.ToString("F3") + "s"
                + " 位置=" + local.ToString("F2")
                + " 画面=(" + vp.x.ToString("F3") + "," + vp.y.ToString("F3") + ")"
                + (inFrame ? " 枠内" : " 枠外")
                + " 残像=" + _trail.Count + "点";
        }

        public string BallDiagForCapture()
        {
            if (_ball == null) return "ball=なし";
            if (!_ball.activeSelf) return "ball=非表示";
            // 貼り付け中は「投球する手の中心からのずれ」が知りたい値（これを0に寄せる）
            if (_ballHeld)
            {
                var hand = _ballHand;
                var tip = _pitcher != null && _pitcher.Animator != null
                    ? _pitcher.Animator.GetBoneTransform(HumanBodyBones.RightMiddleProximal) : null;
                if (hand == null) return "ball=手に貼り付け（手ボーン未解決）";
                var center = tip != null ? Vector3.Lerp(hand.position, tip.position, 0.5f) : hand.position;
                return "ball=手に貼り付け 手の中心からのずれ="
                    + Vector3.Distance(_ball.transform.position, center).ToString("F4") + "m"
                    + " 手首から=" + Vector3.Distance(_ball.transform.position, hand.position).ToString("F4") + "m"
                    + " 球の高さ=" + _root.transform.InverseTransformPoint(_ball.transform.position).y.ToString("F3") + "m";
            }
            if (_ballT < 0f) return "ball=停止";
            var p = _root.transform.InverseTransformPoint(_ball.transform.position);
            string mitt = "-", head = "-";
            if (_catcher != null && _catcher.Animator != null)
                mitt = Vector3.Distance(
                    _root.transform.InverseTransformPoint(DuelCatcherPose.MittCenter(_catcher.Animator)),
                    p).ToString("F3");
            var headT = _batter != null && _batter.Animator != null
                ? _batter.Animator.GetBoneTransform(HumanBodyBones.Head) : null;
            if (headT != null)
                head = Vector3.Distance(_root.transform.InverseTransformPoint(headT.position), p).ToString("F3");
            return "ball pos=" + p.ToString("F3") + " t=" + _ballT.ToString("F3")
                + " 判定面=" + _ballFlight.ToString("F3") + " 捕球面=" + _ballArrival.ToString("F3")
                + (_ballInMitt ? " [ミットの中]" : "")
                + " ミット中心まで=" + mitt + "m 打者頭まで=" + head + "m";
        }

        /// <summary>
        /// 【開発ツール用】球心とミット中心の距離[m]。球が飛んでいない間は負。
        /// ハーネスが毎フレーム走査して**最接近**を採るため（コマ撮りの5枚では捕球の瞬間を跨ぐ）。
        /// </summary>
        public float BallToMittMeters()
        {
            if (_ball == null || !_ball.activeSelf || _ballHeld || _ballT < 0f) return -1f;
            if (_catcher == null || _catcher.Animator == null) return -1f;
            return Vector3.Distance(_ball.transform.position, DuelCatcherPose.MittCenter(_catcher.Animator));
        }

        /// <summary>
        /// 【開発ツール用】捕球の実測（issue #332 第2弾）。選んだクリップ・捕球の瞬間・ミットIKの重み・
        /// ミット中心と狙点のずれを返す。**目測で判定しない**ための単一ソース（受け入れはずれ≦0.12m
        /// ＝ミットの半径）。キャリブレーション窓が毎フレーム表示する。
        /// </summary>
#if UNITY_EDITOR
        /// <summary>【実測プローブ用】捕手ルートの BasePos からのオフセット（ステージローカル）。
        /// 残差の体寄せ（issue #396②/#440）の実測に使う。y&lt;0＝地面へ沈んでいる。</summary>
        public Vector3 CatcherRootOffsetForCapture()
            => _catcher == null || _catcher.Go == null
                ? Vector3.zero
                : _catcher.Go.transform.localPosition - _catcher.BasePos;

        /// <summary>【実測プローブ用】捕球からの据え置き窓[s]（<see cref="CatcherLingerSeconds(bool)"/>）。
        /// 直近の球の判定（ストライク系か）に連動する＝CatcherClampShooter が測定窓を組むのに使う
        /// （私有 const の写しを持たせない）。</summary>
        public float CatcherLingerSecondsForCapture => CatcherLingerSeconds(_catchIsStrike);
#endif

        public string CatchDiagForCapture()
        {
            // 後逸（issue #525）は捕球と同じ枠で読めるようにする＝「捕らない」と「後逸を出している」は
            // 絵では区別しづらいので、どのクリップをいつ当てているかを実測の出口に出す（design-18 §3.3）
            if (_passedBallIndex >= 0)
            {
                var pb = PassedBallAnchors[_passedBallIndex];
                bool pbRunning = _passedBallClips != null && _catcher != null
                    && _catcher.Clip == _passedBallClips[_passedBallIndex];
                return "後逸=" + pb.Clip.Replace("AA_Baseball_Catcher_Passed_Ball_", "")
                    + " 抜ける瞬間=" + pb.Moment.ToString("F2") + "s"
                    + " クリップ=" + (pbRunning && _catcher != null ? _catcher.Time.ToString("F2") + "s" : "待機")
                    + (_passedBallCountdown >= 0f ? " 開始まで=" + _passedBallCountdown.ToString("F2") + "s" : "")
                    + " 球はミット外（捕球なし）";
            }
            if (_catchIndex < 0) return "捕球=なし（この球は捕手が捕らない分岐）";
            var a = CatchAnchors[_catchIndex];
            string name = a.Clip.Replace("AA_Baseball_Catcher_Catching_", "");
            if (_catcher == null || _catcher.Animator == null) return "捕球=" + name + "（捕手なし）";
            bool running = _catchClips != null && _catcher.Clip == _catchClips[_catchIndex];
            float w = _ballT >= 0f ? DuelCatcherPose.Weight(_ballT, _ballArrival) : 0f;
            var center = _root.transform.InverseTransformPoint(DuelCatcherPose.MittCenter(_catcher.Animator));
            return "捕球=" + name + " 捕球の瞬間=" + a.Moment.ToString("F2") + "s"
                + " クリップ=" + (running ? _catcher.Time.ToString("F2") + "s" : "待機")
                + (_catchCountdown >= 0f ? " 開始まで=" + _catchCountdown.ToString("F2") + "s" : "")
                + " IK重み=" + w.ToString("F2")
                + " ミット中心=" + center.ToString("F3")
                + " 狙点=" + _ballCatchPoint.ToString("F3")
                + " ずれ=" + Vector3.Distance(center, _ballCatchPoint).ToString("F3") + "m";
        }

        internal string GloveDiagForCapture()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var (a, name) in new[] { (_catcher, "catcher"), (_pitcher, "pitcher") })
            {
                if (a == null || a.Animator == null) { sb.AppendLine(name + "\tno-actor"); continue; }
                var hand = a.Animator.GetBoneTransform(HumanBodyBones.LeftHand);
                var knuckle = a.Animator.GetBoneTransform(HumanBodyBones.LeftMiddleProximal);
                Transform glove = null;
                foreach (var t in a.Go.GetComponentsInChildren<Transform>(true))
                    if (t.name == "prop-glove") { glove = t; break; }
                if (hand == null || glove == null)
                {
                    sb.AppendLine(name + "\thand=" + (hand != null) + "\tglove=" + (glove != null) + "\t★欠落");
                    continue;
                }
                var palm = knuckle != null ? Vector3.Lerp(hand.position, knuckle.position, 0.65f) : hand.position;
                var local = _root.transform.InverseTransformPoint(glove.position);
                sb.AppendLine(name
                    + "\t掌=" + _root.transform.InverseTransformPoint(palm).ToString("F3")
                    + "\tグラブ=" + local.ToString("F3")
                    + "\t掌からの距離=" + Vector3.Distance(palm, glove.position).ToString("F3") + "m"
                    + "\t実寸=" + glove.lossyScale.ToString("F3"));
            }
            return sb.ToString();
        }

        /// <summary>
        /// 【診断用】投手の掌・グラブ・腰のステージローカル座標を返す（寄りカットの狙いを実測で決める）。
        /// 見つからない要素は Vector3.zero。
        /// </summary>
        internal void PreviewProbeForCapture(out Vector3 palm, out Vector3 glove, out Vector3 hips,
            out Quaternion gloveRot, out string handInGlove)
        {
            palm = glove = hips = Vector3.zero;
            gloveRot = Quaternion.identity;
            handInGlove = "-";
            if (_pitcher == null || _pitcher.Animator == null) return;
            var hand = _pitcher.Animator.GetBoneTransform(HumanBodyBones.LeftHand);
            var knuckle = _pitcher.Animator.GetBoneTransform(HumanBodyBones.LeftMiddleProximal);
            var hipBone = _pitcher.Animator.GetBoneTransform(HumanBodyBones.Hips);
            if (hand != null)
            {
                var p = knuckle != null ? Vector3.Lerp(hand.position, knuckle.position, 0.65f) : hand.position;
                palm = _root.transform.InverseTransformPoint(p);
            }
            if (hipBone != null) hips = _root.transform.InverseTransformPoint(hipBone.position);
            Transform container = null, prop = null;
            foreach (var t in _pitcher.Go.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "gear-glove") container = t;
                else if (t.name == "prop-glove") prop = t;
            }
            if (prop != null)
            {
                glove = _root.transform.InverseTransformPoint(prop.position);
                gloveRot = Quaternion.Inverse(_root.transform.rotation) * prop.rotation;
            }
            // 手の各関節を**グラブの座標系**で見る（+Y=指の向き / +Z=掌の法線）。
            // 斜めの絵では前後左右が分離できないので、はみ出しの向きはここで数値に出す
            if (container != null && prop != null)
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("グラブ中心=").Append(container.InverseTransformPoint(prop.position).ToString("F3"));
                foreach (var (b, name) in new[]
                {
                    (HumanBodyBones.LeftHand, "手首"),
                    (HumanBodyBones.LeftMiddleProximal, "中指付根"),
                    (HumanBodyBones.LeftMiddleDistal, "中指先"),
                    (HumanBodyBones.LeftThumbDistal, "親指先"),
                    (HumanBodyBones.LeftIndexDistal, "人差指先"),
                })
                {
                    var t = _pitcher.Animator.GetBoneTransform(b);
                    sb.Append(" ").Append(name).Append('=')
                      .Append(t == null ? "なし" : container.InverseTransformPoint(t.position).ToString("F3"));
                }
                handInGlove = sb.ToString();
            }
        }

        /// <summary>バット追従の内部状態をTSV1行で返す（clip / clipT / 両手間隔 / 片手フラグ / バット表示）。</summary>
        /// <summary>
        /// 「バットが球に当たって見えるか」の実測（2026-07-31 監督指摘「スイングしてるとこがあまり見えない」）。
        /// アンカー <see cref="SwingHitContact"/> は**右手の速度ピーク**で採ってあるが、バットはしなって
        /// 芯が手より遅れて来るので、両者は一致しない可能性がある。絵では「当たったように見えない」としか
        /// 分からないので、芯と打点・芯と球の距離を数値で出して当たり判定の瞬間を特定する。
        /// </summary>
        internal string BatContactDiagForCapture()
        {
            if (_bat == null || _batter == null || _root == null) return "bat=なし";
            if (!_bat.activeSelf) return "bat=非表示 clip=" + _batter.Time.ToString("F2");
            var tr = _root.transform;
            var axis = (_bat.transform.rotation * Vector3.up).normalized;
            float half = Mathf.Max(0.1f, _cal.BatLengthM) * 0.5f;
            var sweet = tr.InverseTransformPoint(_bat.transform.position + axis * (half - BatSweetInsetM));
            var contact = BallPointAt(_ballArrival);
            // 握り（掌中心の中点）と、そこから芯までの腕の長さ。再校正を掃引でなく幾何で解くのに要る＝
            // 「握りから打点までの距離」が「握りから芯までの長さ」を超えていたら、
            // バットを回すだけでは**原理的に届かない**（手を動かすしかない）ことがこの2つで分かる
            var grip = tr.InverseTransformPoint(BatGripWorld());
            float lever = Vector3.Distance(grip, sweet);
            string toBall = "-";
            if (_ball != null && _ball.activeSelf)
                toBall = Vector3.Distance(sweet, tr.InverseTransformPoint(_ball.transform.position)).ToString("F3");
            return "clip=" + _batter.Time.ToString("F2")
                + " 芯=" + sweet.ToString("F2")
                + " 握り=" + grip.ToString("F2")
                + " 腕=" + lever.ToString("F3")
                + " 握り→打点=" + Vector3.Distance(grip, contact).ToString("F3")
                + " 打点まで=" + Vector3.Distance(sweet, contact).ToString("F3") + "m"
                + " 球まで=" + toBall + "m";
        }

#if UNITY_EDITOR
        /// <summary>【実測プローブ用】握り点（両掌中心の中点・ワールド）。</summary>
        internal Vector3 BatGripWorldForCapture => BatGripWorld();

        /// <summary>【実測プローブ用】打点（判定に使われた実着弾位置・ステージローカル）。</summary>
        internal Vector3 ContactPointLocalForCapture => BallPointAt(_ballArrival);

        /// <summary>【実測プローブ用】バット軸（ノブ→バレル）をステージローカルで返す（非表示なら0）。</summary>
        internal Vector3 BatAxisLocalForCapture
            => (_bat == null || _root == null || !_bat.activeSelf) ? Vector3.zero
             : _root.transform.InverseTransformDirection((_bat.transform.rotation * Vector3.up).normalized);
#endif

        internal string BatDiagForCapture()
        {
            if (_batter == null || _batter.Animator == null) return "-\t0\t-1\tFalse\tFalse";
            var lh = _batter.Animator.GetBoneTransform(HumanBodyBones.LeftHand);
            var rh = _batter.Animator.GetBoneTransform(HumanBodyBones.RightHand);
            float gap = (lh != null && rh != null) ? Vector3.Distance(lh.position, rh.position) : -1f;
            string clip = _batter.Clip != null ? _batter.Clip.name : "-";
            bool batOn = _bat != null && _bat.activeSelf;
            return clip + "\t" + _batter.Time.ToString("F2") + "\t" + gap.ToString("F3")
                + "\t" + _batOneHand + "\t" + batOn;
        }

        /// <summary>
        /// 打者の足元の実測（clipT / 根の基準位置からのずれ[m] / 両足のステージローカルXZ / 腰XZ）。
        /// 「スイングで横へステップして見える」は**ルートモーションの水平成分**か**踏み出しそのもの**の
        /// どちらかだが、絵では区別できない（どちらも体が横へ動く）。根と足を別々に出せば、
        /// 根が動いていれば前者、根が止まったまま足だけ動いていれば後者と切り分けられる。
        /// 左右の符号: ステージローカル **+x=一塁側**（右打者は -x 側に立つ＝-xへ動く＝本塁から離れる）。
        /// </summary>
        internal string BatterRootDiagForCapture()
        {
            if (_batter == null || _batter.Animator == null || _root == null) return "-";
            var tr = _root.transform;
            var d = _batter.Go.transform.localPosition - _batter.BasePos;
            var lf = _batter.Animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            var rf = _batter.Animator.GetBoneTransform(HumanBodyBones.RightFoot);
            var hip = _batter.Animator.GetBoneTransform(HumanBodyBones.Hips);
            string F(Transform t) => t == null ? "-" : XZ(tr.InverseTransformPoint(t.position));
            return "clip=" + _batter.Time.ToString("F2")
                + " 根Δ=" + d.x.ToString("F3") + "," + d.z.ToString("F3")
                + " 左足=" + F(lf) + " 右足=" + F(rf) + " 腰=" + F(hip);
        }

        private static string XZ(Vector3 p)
            => "(" + p.x.ToString("F2") + "," + p.z.ToString("F2") + ")";

        /// <summary>
        /// 投手のルート状態をTSV1行で返す（clipT / 立ち向きのBaseRotからのずれ[度] / 立ち位置のBasePosからのずれ[m] /
        /// applyRootMotion / フェード中）。ルートモーションの積分は絵では「向きが少し変」としか見えず
        /// 目測で切り分けられないので、必ずこの実測で判定する（design-18 §7-2）。
        /// </summary>
        internal string PitcherDiagForCapture()
        {
            if (_pitcher == null || _pitcher.Go == null) return "0\t0\t0\t0\t-\t-";
            var t = _pitcher.Go.transform;
            float dYaw = Mathf.DeltaAngle(_pitcher.BaseRot.eulerAngles.y, t.localRotation.eulerAngles.y);
            var d = t.localPosition - _pitcher.BasePos;
            return _pitcher.Time.ToString("F2") + "\t" + dYaw.ToString("F1") + "\t"
                + d.x.ToString("F3") + "\t" + d.z.ToString("F3") + "\t"
                + (_pitcher.Animator != null && _pitcher.Animator.applyRootMotion) + "\t"
                + (_pitcher.FadeRemaining > 0f);
        }

        /// <summary>
        /// 球審の実測ログ（issue #353）。クリップ名・クリップ内時刻・右手の高さ[m]。
        /// 「捕球の後に腕が出ているか」は絵の目測でなくこの列で判定する
        /// （コールの腕伸ばしはクリップ内 <see cref="UmpireCallAnchorSeconds"/>）。
        /// </summary>
        internal string UmpireDiagForCapture()
        {
            if (_umpire == null || _umpire.Animator == null) return "-\t0\t0";
            var rh = _umpire.Animator.GetBoneTransform(HumanBodyBones.RightHand);
            float y = rh != null ? rh.position.y - _umpire.Go.transform.position.y : -1f;
            string clip = _umpire.Clip != null ? _umpire.Clip.name : "-";
            return clip + "\t" + _umpire.Time.ToString("F2") + "\t" + y.ToString("F3");
        }

        /// <summary>球審の右手ボーンのRT上ピクセル座標（球審中心クロップの決定用。issue #353）。</summary>
        internal Vector2 UmpireScreenPxForCapture()
        {
            if (_camera == null || _umpire == null || _umpire.Animator == null) return new Vector2(-1f, -1f);
            var rh = _umpire.Animator.GetBoneTransform(HumanBodyBones.RightHand);
            var p = rh != null ? rh.position : _umpire.Go.transform.position + Vector3.up;
            var sp = _camera.WorldToScreenPoint(p);
            return new Vector2(sp.x, sp.y);
        }

        /// <summary>打者の左手ボーンのRT上ピクセル座標（クロップ中心の決定用）。</summary>
        internal Vector2 BatterScreenPxForCapture()
        {
            if (_camera == null || _batter == null || _batter.Animator == null) return new Vector2(-1f, -1f);
            var lh = _batter.Animator.GetBoneTransform(HumanBodyBones.LeftHand);
            var p = lh != null ? lh.position : _batter.Go.transform.position + Vector3.up;
            var sp = _camera.WorldToScreenPoint(p);
            return new Vector2(sp.x, sp.y);
        }
#endif

        // ---- アセットロード ------------------------------------------------------
        //
        // 二段構え（F8）:
        //   エディタ … 従来どおり AssetDatabase を先に見る。**索引のベイク忘れで手元が止まらない**ように
        //              するのが目的で、コード側の名前表を足した直後でも絵は出る
        //   ビルド   … AssetDatabase が無いので索引（DuelAssetCatalog）だけ。索引も無ければ
        //              Available=false へ落ちる＝購入資産が無い環境の従来挙動と同じ
        // 索引の中身の鮮度は DuelAssetCatalogBaker の検証メニューが見る。

        /// <summary>
        /// 【開発用】AssetDatabase を使わず**索引だけ**で組む（＝Player ビルドと同じ経路）。
        /// エディタのロードは AssetDatabase を先に見るので、索引が壊れていても手元では絵が出る。
        /// ビルドを作らずにビルド経路を確かめるための唯一の入口。
        /// </summary>
        public static bool ForceCatalogOnly;

        private static DuelAssetCatalog _catalog;
        private static bool _catalogTried;

        /// <summary>ビルド同梱の索引（無ければ null）。Resources から1回だけ引く。</summary>
        internal static DuelAssetCatalog Catalog
        {
            get
            {
                if (_catalogTried) return _catalog;
                _catalogTried = true;
                _catalog = Resources.Load<DuelAssetCatalog>(DuelAssetCatalog.ResourceName);
                return _catalog;
            }
        }

        /// <summary>【開発用】体型比較撮影向けに素体FBXを差し替える（null=既定のSuperhero Male）。
        /// 素体を替える時は対応する着衣ベイク資産（UniformMeshOverrideForCapture）も揃えること＝
        /// ベイクは bindposes/boneWeights を素体からコピーするため、素体と着衣メッシュは必ず同源で組。</summary>
        internal static string BodyFbxOverrideForCapture;

        private static GameObject LoadBody()
        {
#if UNITY_EDITOR
            if (!ForceCatalogOnly)
            {
                var fromDb = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                    BodyFbxOverrideForCapture ?? BodyFbx);
                if (fromDb != null) return fromDb;
            }
#endif
            var cat = Catalog;
            return cat != null ? cat.FallbackBody : null;
        }

        /// <summary>子孫から名前一致のRendererを引いて表示を切り替える（見つからなければ何もしない）。</summary>
        private static void SetChildActive(GameObject root, string rendererName, bool active)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                if (r.name == rendererName) r.gameObject.SetActive(active);
        }

        /// <summary>
        /// 購入素体。gitignore配下なので手元に無い環境がある＝null を正常系として扱い、
        /// 呼び出し側でU2素体へ落とす（警告も出さない。無いのが異常ではない）。
        /// </summary>
        private static GameObject LoadVendorBody()
        {
#if UNITY_EDITOR
            if (!ForceCatalogOnly)
            {
                var fromDb = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(VendorBodyFbx);
                if (fromDb != null) return fromDb;
            }
#endif
            var cat = Catalog;
            return cat != null ? cat.VendorBody : null;
        }

        /// <summary>
        /// 購入素体のメッシュを領域IDつき（DuelVendorRegionBaker のベイク資産）へ差し替える。
        /// 眼球は消す（のっぺらぼう方針＝design-08。顔テクスチャは領域色で塗り潰されるので放っておいても
        /// 顔は出ないが、眼球は別メッシュなので明示的に落とす）。
        /// 1枚でも領域資産が欠けたら false＝呼び出し側は肌色一色のフォールバックへ倒す
        /// （半分だけ領域色という中途半端な絵を出さない）。
        /// </summary>
        private static bool ApplyVendorRegionMeshes(GameObject go)
        {
#if UNITY_EDITOR
            bool all = true;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r.name.EndsWith("Eye_Ball_Geo"))
                {
                    r.gameObject.SetActive(false);
                    continue;
                }
                var mesh = ForceCatalogOnly ? null : UnityEditor.AssetDatabase.LoadAssetAtPath<Mesh>(
                    VendorRegionMeshDir + r.name + VendorRegionMeshSuffix);
                if (mesh == null && Catalog != null) mesh = Catalog.FindRegionMesh(r.name);
                if (mesh == null) { all = false; continue; }
                var smr = r as SkinnedMeshRenderer;
                if (smr != null) smr.sharedMesh = mesh;
                else
                {
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf != null) mf.sharedMesh = mesh;
                }
            }
            if (!all)
                Debug.LogWarning("[DuelStageRig] vendor region meshes missing ("
                    + VendorRegionMeshDir + ") - run KokoSim.EditorTools.DuelVendorRegionBaker.BakeAll()");
            return all;
#else
            // ビルドでは索引だけ（AssetDatabase が無い）。1枚でも欠けたら肌色一色へ倒す
            var cat = Catalog;
            if (cat == null) return false;
            bool ok = true;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r.name.EndsWith("Eye_Ball_Geo")) { r.gameObject.SetActive(false); continue; }
                var mesh = cat.FindRegionMesh(r.name);
                if (mesh == null) { ok = false; continue; }
                var smr = r as SkinnedMeshRenderer;
                if (smr != null) smr.sharedMesh = mesh;
                else
                {
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf != null) mf.sharedMesh = mesh;
                }
            }
            return ok;
#endif
        }

        /// <summary>
        /// 着衣メッシュ（DuelUniformBaker のベイク資産）。素体と同じくエディタ限定ロード。
        /// 無ければ null＝素体のまま出す（警告のみ・対決ビューは止めない）。
        /// </summary>
        /// <summary>【開発用】比較シート撮影向けに着衣メッシュ資産を差し替える（null=既定のSlim0）。</summary>
        internal static string UniformMeshOverrideForCapture;

        private static Mesh LoadUniformMesh()
        {
#if UNITY_EDITOR
            if (!ForceCatalogOnly)
            {
                var path = UniformMeshOverrideForCapture ?? UniformMeshAsset;
                var mesh = UnityEditor.AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (mesh != null) return mesh;
                Debug.LogWarning("[DuelStageRig] uniform mesh not baked (" + path
                    + ") - run KokoSim.EditorTools.DuelUniformBaker.BakeAll()");
            }
#endif
            var cat = Catalog;
            return cat != null ? cat.UniformMesh : null;
        }

        /// <summary>
        /// 球審専用の着衣メッシュ（恰幅Stout）。読み方は <see cref="LoadUniformMesh"/> と同型で、
        /// 無いときだけ選手用（Slim1）へ落として痩せた球審のまま出す（対決ビューは止めない）。
        /// </summary>
        private static Mesh LoadUmpireMesh()
        {
#if UNITY_EDITOR
            if (!ForceCatalogOnly)
            {
                var mesh = UnityEditor.AssetDatabase.LoadAssetAtPath<Mesh>(
                    UniformMeshOverrideForCapture ?? UmpireMeshAsset);
                if (mesh != null) return mesh;
                Debug.LogWarning("[DuelStageRig] umpire mesh not baked (" + UmpireMeshAsset
                    + ") - run KokoSim.EditorTools.DuelUniformBaker.BakeAll()");
                return LoadUniformMesh();
            }
#endif
            var cat = Catalog;
            if (cat != null && cat.UmpireUniformMesh != null) return cat.UmpireUniformMesh;
            return LoadUniformMesh();
        }

        /// <summary>
        /// 供給元FBX（ClipFbxSources）を順に見てクリップを名前引きする。プローブ側も同じ入口を使う。
        /// FBXにはプレビュー複製（__preview__接頭辞）が同居するが、完全一致なので拾わない。
        /// </summary>
        public static AnimationClip LoadClip(string clipName)
        {
#if UNITY_EDITOR
            if (!ForceCatalogOnly)
            {
                foreach (var fbx in ClipFbxSources)
                {
                    foreach (var a in UnityEditor.AssetDatabase.LoadAllAssetsAtPath(fbx))
                    {
                        var c = a as AnimationClip;
                        if (c != null && c.name == clipName) return c;
                    }
                }
            }
#endif
            var cat = Catalog;
            return cat != null ? cat.FindClip(clipName) : null;
        }
    }
}
