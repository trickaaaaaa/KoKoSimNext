using System.Collections.Generic;
using KokoSim.Engine.Match.AtBat;   // PlateAppearanceResult（打席パネルの色分け）
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;   // PitcherGear（とばしていけ）
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using KokoSim.Unity.Components;   // 部品辞書（LineScorePanel / ScoreBug）
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 試合ライブ進行（設計書06 §3.4 詳細モード＋設計書09 采配）。実試合を「打席単位で解決→再生→采配窓→次」
    /// で進める。エンジンは <see cref="MatchProgression"/>（GameEngine.Steps を1打席ずつ引く）で駆動し、
    /// 各打席のタイムラインを <see cref="Match2DPlaybackElement"/> で再生する。描画部品は再利用するが、
    /// 7サンプルの再生ハーネス（MatchDetailController/PlaybackSamples）には干渉しない。
    ///
    /// 采配窓は「次の打席へ（采配なし）」＋「選手交代」（代打・代走・投手交代・守備交代・DH解除を
    /// <see cref="MatchSubstitutionPanel"/> のモーダルで出し分け）＋「スキップ（委任）」（残りを委任AIへ）。
    /// サイン・伝令の本UIは設計書09準拠の別タスク。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class MatchLiveController : MonoBehaviour
    {
        /// <summary>
        /// 大会/タウンフローからのライブ観戦要求（試合開始→観戦画面の受け渡し）。ScreenRouter が MatchLive を
        /// SetActive(true) する前にこれをセットしておくと、OnEnable が自前生成ではなくこの実試合を観戦する。
        /// 観戦の終局で <see cref="OnComplete"/>(GameResult) を呼び、呼び出し側が大会へ結果を戻して画面遷移する。
        /// null のときは従来どおり自前の固定シード試合（デモ・スクショ用）を生成する。
        /// </summary>
        public sealed class LiveMatchRequest
        {
            public MatchProgression Progression;
            public bool ManagerIsAway;
            public string AwayName;
            public string HomeName;
            /// <summary>
            /// 両校のユニフォーム配色（設計書18 §3.5.1）。対決ビューの塗り分けに使う表示専用データで、
            /// 未設定なら既定（白＋紺）で描く。<b>両校とも自校の色を着る</b>ので、同系色同士の対戦では
            /// 見分けが付きにくい（実物準拠・2026-07-29 監督決定）。
            /// </summary>
            public KokoSim.Engine.Nation.TeamColors AwayColors;
            public KokoSim.Engine.Nation.TeamColors HomeColors;
            public int ManagerTacticalSense = 70;
            /// <summary>
            /// 勝者が要る文脈か（大会戦＝true）。結果画面で延長上限の引き分けを「引き分け（再試合）」と
            /// 明示するかに使う（issue #253）。練習試合は引き分けで終わるので false（issue #269）。
            /// </summary>
            public bool RematchOnTie = true;
            public System.Action<GameResult> OnComplete;
        }

        /// <summary>次に MatchLive がアクティブ化されたとき消費するライブ観戦要求（一度使ったらクリア）。</summary>
        public static LiveMatchRequest Pending;

        /// <summary>
        /// スクショ用シューター（<see cref="AdvanceForCapture"/> で手動駆動する）が真にする。真の間は
        /// 自動連続進行（issue #173）を止め、従来どおり1打席ずつ手動で解決させる。シューターは MatchLive を
        /// アクティブ化する前にこれを立てること（OnEnable の自動開始を抑止するため）。
        /// </summary>
        public static bool CaptureMode;

#if UNITY_EDITOR
        /// <summary>
        /// 【開発用・エディタ限定】試合係数の差し替え口。牽制・暴投・捕逸・振り逃げは実確率が 0.05〜0.001 で、
        /// 素の試合を流しても絵の検証に必要な回数が出ない。撮影ハーネスだけがここへ確率を上げた
        /// <see cref="GameContext"/> を返す関数を挿す。既定 null＝恒等＝本番の乱数順・統計帯には一切触れない。
        /// </summary>
        public static System.Func<GameContext, GameContext> CaptureContextOverride;
#endif

        [SerializeField] private ulong gameSeed = 20260718UL;
        [SerializeField] private string awayName = "北都大付属";
        [SerializeField] private string homeName = "桜丘";
        [Tooltip("タイムラインを持たない打席（三振・四球）の表示時間[秒]。")]
        [SerializeField] private float noPlayHoldSeconds = 1.2f;
        [Tooltip("俯瞰（2D）で1球を見せる時間[秒]（×0.5/×2/×4速度に連動）。盤面の投球飛翔0.45秒より長くし、着弾後の一呼吸を確保する（issue #135）。対決ビュー中の判定時刻はこの値ではなく球審のコール（DuelStageRig.CallMomentSeconds）で決まる。")]
        [SerializeField] private float pitchIntervalSeconds = 1.0f;
        [Tooltip("打席の頭に置く采配の間[秒]（設計書15 §8.3-①）。打者を見てサインを出す間合い。倍速で一緒に縮む。1.0だと采配カードの受付が短すぎたため1.8へ延長（issue #493）。")]
        [SerializeField] private float paHeadDwellSeconds = 1.8f;
        [Tooltip("対決ビュー表示中のリリース間隔[秒]（design-18 §7-2）。投球（セットポジション）1.3秒＋フォロースルー1.5秒が切り貼りなしで収まる長さにする。判定表示のタイミングは球審のコールに従うので、この値は次の投球までの間だけを決める。3.2だと采配カードの受付締切（次球の投手モーション開始＝この値−1.30秒）が短すぎたため3.6へ延長（issue #493）。")]
        [SerializeField] private float duelPitchIntervalSeconds = DuelStageRig.DefaultPitchInterval;
        [Tooltip("1球ごとの判定オーバーレイ（ストライク/ボール/ファール＋球速球種）の表示時間[秒]。0.85=監督確定 2026-08-05（0.35は短い）。対決ビューの捕手はこの表示が消えるまで捕球位置で静止する（NewDuelRig が rig へ流し込む）。")]
        [SerializeField] private float pitchCallHoldSeconds = 0.85f;
        [Tooltip("得点オーバーレイ（issue #60）の1点あたりの加算間隔[秒]（加点前の一時停止にも使う）。")]
        [SerializeField] private float scoreCallStepSeconds = 0.5f;
        [Tooltip("得点オーバーレイが最終スコアに達してから消えるまでの表示時間[秒]。")]
        [SerializeField] private float scoreCallFinalHoldSeconds = 1.1f;
        [Tooltip("自校（後攻 home）の采配能力（委任AIの強さ・スキップ時に使用）。")]
        [SerializeField] private int managerTacticalSense = 70;

        // ライブ観戦（大会フロー）モードの状態。デモ生成モードでは false / null。
        private bool _managerIsAway;
        // 両校の配色（設計書18 §3.5.1）。攻守交代のたびに守備側/攻撃側へ割り当て直す（DuelStageRig.SetTeamColors）。
        private KokoSim.Engine.Nation.TeamColors _awayColors;
        private KokoSim.Engine.Nation.TeamColors _homeColors;
        private bool? _duelColorsOffenseIsTop;   // 対決ビューへ最後に流した攻守（null=未適用）
        private System.Action<GameResult> _onComplete;
        private GameResult _finalResult;
        private Button _backHome;

        private VisualElement _root;
        private Match2DPlaybackElement _view;
        private Label _caption, _result, _batter;

        // ラインスコアを描き直したかのフラグ。掲示板は「プレーが確定してから点を掲げる」（実物の運用と同じ）
        // ので、打席の演出が resolved になった瞬間に1回だけ更新する（毎フレーム Snapshot を組み直さない）。
        private bool _lineScorePosted;
        // クリック中断ポップアップ（issue #289・案P1）。盤面クリック＝演出だけを止める自由中断で、
        // エンジンには触れない（CallTimeout を呼ばない＝伝令・動揺解除は動かさない）。
        // 伝令・選手交代・スキップ・再開はこのポップアップに集約する。
        // 旧「タイム（伝令）中断」（issue #173）との2種別は、交代が伝令不要になったことで
        // ボタン可否の差が消えたため 1本の _paused に統合した（伝令は中断中に押せる1アクションへ降格）。
        private VisualElement _pauseModal;
        private Label _pmSituation;
        private Button _pmTimeout, _pmSub, _pmSkip, _pmResume;
        private bool _timeoutSentThisPause;   // 1中断につき伝令1回（連打で複数消費させない）
        // 自動連続進行の中断中フラグ（ポップアップ表示〜再開の間 true）。盤面・自動進行の両方を止める。
        private bool _paused;
        // 自動進行を開始したか（OnEnable の一度だけ。スクショ時＝CaptureMode は開始しない）。
        private bool _autoStarted;
        // タイムライン駆動カメラワーク（design-06/12・Issue #119）の観戦設定トグル。既定オフ＝現行の固定全景。
        // ×4速時は酔い防止のため強制的に全景固定へ戻す（スキップは委任でその場で試合が終わるため対象外）。
        private Button _camWork;
        private bool _cameraWorkWanted;
        // 対決ビュー（design-18 §7-1 A案）。既定の開始ビュー（issue #312）で、インプレー打球の間だけ
        // 俯瞰へ自動で切り替わる。実体は DuelStageRig（UBC素体のループ芝居・12fps量子化）。
        private Button _duelBtn;
        private bool _duelOn;
        // ユーザーが手動で対決ビューをOFF（俯瞰固定）にしたか。true の間は自動切替（打球/次打席）を止める。
        // 再度手動でONにすると自動切替も復帰する（issue #312）。
        private bool _duelManualOff;
        private DuelStageRig _duelRig;
        private DuelViewElement _duelImg;
        private VisualElement _duelHost, _fieldHost;
        // 打球処理中の頭上フルネームラベル（issue #393）。_duelImg の子＝RTの表示矩形とローカル座標が
        // 一致する（DuelViewElement.Snap が RT の整数倍スケール表示に合わせて自身の箱を作る）ので、
        // ビューポート正規化座標をそのまま _duelImg の実寸へ掛けるだけで済む。
        private VisualElement _duelLabelHost;
        private readonly List<Label> _duelFieldLabelPool = new();
        private const int MaxDuelFieldLabels = 6;
        // 選手交代モーダル（設計書09 §6 / issue #22）。代打・代走・投手交代・守備交代・DH解除の入口。
        private MatchSubstitutionPanel _subPanel;
        private readonly List<Button> _speedButtons = new();
        // 速度ボタンのハンドラ（ラムダのため OnDisable で外せるよう保持する）。
        private readonly List<(Button Button, System.Action Handler)> _speedHandlers = new();

        // 3カラム（左=自校 / 右=相手）＋マッチアップHUD。表示専用（数値はエンジン集計 Snapshot から引く）。
        // 左下コーナーは「上=投手HUD／下=指示パネル」の常設スタック（issue #289）。HUD はスロットの
        // 中身だけを打席ごとに入れ替え、SignPad は入れ替えない（選択状態・攻守フェーズを保持する）。
        private VisualElement _leftLineup, _rightLineup, _leftPitcher, _rightPitcher, _hudHost, _capHistHost;
        private VisualElement _hudPitcherSlot, _hudBatterSlot;
        private Label _leftTeam, _rightTeam;

        // 1球采配（設計書15 Phase C-3 → issue #289 で SignPad 化）。ITacticsBrain を経由しない
        // プレイヤーの手動指示。次に解決される打席の最初の1球にだけ効き、Advance() のたび自動で選択解除する
        // （エンジン側の「1球指示は消費後クリア」と表示を一致させる＝実は1球限りだと分かる）。
        // 攻撃中は打撃指示だけ・守備中は配球指示だけを出す（SetPhase）。定義は BuildSignPad に集約。
        private TacticsCardDeck _signPad;   // 采配指示の手札（旧 SignPad の置き換え。API は同型）
        // ①勝負の1手＝次の打席へ押し込んで自動解除される選択。
        private PitchBattingOverride? _pitchBattingChoice;
        private OffensiveSign? _signChoice;
        private StartType? _stealChoice;
        // ②方針＝押した瞬間にエンジンへ入り、解除するまで残る選択（表示の点灯もそのまま）。
        private BattingApproach _approach = BattingApproach.Normal;
        private PitchPolicy? _defPolicy;
        private PitcherGear? _defGear;
        private DefenseDepth? _defInfield;
        private DefenseDepth? _defOutfield;
        private MatchLiveStatsProvider _statsProvider;
        private string _awayDisp = "先攻", _homeDisp = "後攻";
        // 勝者が要る文脈か（大会戦=true／練習試合=false, issue #269）。結果画面の引き分け表記に渡す。
        private bool _rematchOnTie = true;
        private readonly List<string> _capHist = new();   // 実況履歴（直近3件）

        private MatchProgression _prog;
        private KokoSim.Engine.Match.Game.Team _homeTeam;
        private LivePlateAppearance _current;
        private double _t;
        private float _speed = 1f;
        private bool _gameOver;

        // ── 1球進行（設計書15 §8）──
        // 画面は「采配窓 → 打者を出す →〈間〉→ サインを押し込む → 1球投げる → その球を描く → 次の窓」で回る。
        // 窓はエンジンが実際に止まる場所（MatchProgression.AdvancePitch が Pitch を返した地点）そのもので、
        // UI 側が勝手に作る間合いではない＝押した指示は必ず「次の1球」に効く。
        private enum LiveStage
        {
            Idle,     // 試合開始前／進行を起こしていない
            Window,   // 采配窓で待っている（指示を受け付ける）
            Pitch,    // 1球を描いている
            Play,     // 打球再生／結果の据え置き
            Over,     // 終局
        }

        private LiveStage _stage = LiveStage.Idle;
        /// <summary>いま止まっている采配窓の局面（これから投げる1球）。打席の頭でも埋まる。</summary>
        private LiveAtBat _atBat;
        /// <summary>
        /// 次に開く采配窓。エンジンは「1球投げた結果」と「次の窓」を同時に返すので、球を描き終えるまで
        /// ここで預かる（先に <see cref="_atBat"/> へ入れるとカウントが判定より先に動いてネタバレになる）。
        /// </summary>
        private LiveAtBat _atBatNext;
        // いま映している半回（イニング間画面の差し込み判定。エンジンに半回終了のイベントは無い）。
        private int _shownInning;
        private bool _shownIsTop, _shownAnyHalf;
        /// <summary>この打席で描く投球列。1球進行では投げるたび1件ずつ伸び、一括経路では最初から全件入る。</summary>
        private readonly List<QueuedPitch> _paQueue = new();
        /// <summary>この打席が確定済みか（＝<see cref="_current"/> がいま描いている打席を指しているか）。</summary>
        private bool _paConfirmed;
        private float _windowClock, _windowHold, _windowCarry;
        /// <summary>再生し終えた打席数（本日成績の略記列はここまでしか見ない＝再生前の結果を出さない）。</summary>
        private int _displayedLogCount;

        /// <summary>描く1球ぶんの材料。塁は「その球を投げる時点」（＝打席途中の盗塁を絵に載せるため）。</summary>
        private readonly struct QueuedPitch
        {
            public readonly PitchToken Token;
            public readonly bool First, Second, Third;
            /// <summary>1球ごとの観測（一括経路＝合成投球列では null）。アウト・塁の即時反映に使う。</summary>
            public readonly LivePitch Live;

            public QueuedPitch(PitchToken token, bool first, bool second, bool third, LivePitch live)
            {
                Token = token; First = first; Second = second; Third = third; Live = live;
            }
        }

        // 中継風カウント（BSO）＋塁ダイヤ（#4）。投球フェーズで B/S を1球ずつ点灯させる。
        // カウントは ScoreBug の点灯ランプ（B=3 / S=2 / O=2。issue #479 で数字マスから再改定）。
        // 配列の長さがそのまま上限＝実物の掲示板と同じで 4 ボール目・3 ストライク目は点かない。
        private VisualElement[] _lampsB, _lampsS, _lampsO;
        private VisualElement[] _bases;
        private int _pitchIdx;
        private float _pitchClock;
        private bool _inPitchPhase;   // 投球フェーズ（B/S点灯）中か。false なら打球フェーズ/静止。
        private bool _pitchBallVisible;   // 投球フェーズで今の1球の投球軌道を流しているか（#5）。
        private bool _pitchCallDone;      // 今の1球の判定表示＋カウント更新を済ませたか（対決ビュー中は次リリースと分離）。
        private bool _pitchFirstPending;  // 対決ビューの初球ワインドアップ待ち（時計が0に達したらBeginPitchBall(0)）。
        // 今の1球の判定を出す時刻[秒]（リリース起点）。BeginPitchBall で1球ごとに確定する＝
        // 対決ビューは球ごとに飛翔時間が違うので定数では合わない（DuelStageRig.CallMomentSeconds の項）。
        private float _pitchCallAt;
        // ファウル打球の俯瞰表示（issue #313）。非nullの間＝今の1球がファウル打球の再生中で、値＝着地まで
        // 保持すべき最短表示秒（次の1球へ切り替える interval をこの秒数まで延ばす）。
        private float? _foulFlightHoldSeconds;
        // BeginPitchBall が仕込んだ「この1球のファウル打球タイムライン」。判定を出す時刻（_pitchCallAt）に
        // Update() 側で表示へ差し替える（投球セグメントの二重表示を避けるため, issue #313）。
        private PlaybackPlay _pendingFoulFlight;

        // インプレー打球は接触直後に守備ビューへ切り替えず、対決構図のまま球がビューポートから出るまで
        // 据え置く（design-18 §4.5-④・issue #481）。_battedHoldClock は対決ビューを離れられない異常系
        // （デバッグ操作で _duelOn が途中で切られた等）向けの保険で、DuelStageRig.BattedMaxSeconds（1.5s）
        // と同じ値を上限にする（あちらより長く待たない＝二重の安全網にしない）。
        private bool _battedHoldActive;
        private float _battedHoldClock;
        private const float BattedHoldSafetyCapSeconds = 1.5f;

        // 打席途中の盗塁の再生（issue #485）。非nullの間＝盗塁プレー（走者が走る→捕手が投げる→塁で判定）を
        // 守備ビューで見せている最中。打球の据え置きと違い、終わったら打席の続きへ戻す（＝次打席へは進めない）。
        // 再生は打球（_current.Play）と同じ経路（DuelStageRig.BeginFieldPlay＋Match2DPlaybackElement）を通す。
        private PlaybackPlay _stealPlay;
        private double _stealEndAt;

        // 打席途中の牽制の再生（issue #486）。盗塁と同型だが、判定結果（アウト/セーフ）に関わらず
        // 牽制球が投じられた球は必ず再生する（分岐させない、2026-08-05 issue決定）。同じ球に
        // 盗塁も解決していれば（牽制セーフの直後に走塁勝負が続く場合）、終わったら盗塁の再生へ連結する
        // （FinishPickoffView）。再生経路は盗塁と共有（EnterStealPlay と同型）。
        private PlaybackPlay _pickoffPlay;
        private double _pickoffEndAt;

        // 打席途中の暴投・捕逸の再生（issue #487）。盗塁・牽制と同型だが、この球そのものの結果
        // （キャッチャーが後逸した）として起きるため、盗塁・牽制（この球の前）より後にチェックする
        // （＝この球の判定が出た直後）。再生経路は盗塁・牽制と共有（EnterStealPlay と同型）。
        private PlaybackPlay _batteryMissPlay;
        private double _batteryMissEndAt;

        // 打席途中の振り逃げの再生（issue #488）。暴投・捕逸と同型だが、三振確定後の後処理として
        // 判定されるため、盗塁・牽制・暴投/捕逸のいずれよりも後にチェックする（＝この球の最後の連結先）。
        // 再生経路は盗塁・牽制・暴投/捕逸と共有（EnterStealPlay と同型）。
        private PlaybackPlay _dropThirdStrikePlay;
        private double _dropThirdStrikeEndAt;

        // 対決ビューの1球ペーシング（フル振りかぶり再生）を使うか（design-18 §7-2）。
        private bool DuelPacingActive => _duelOn && _duelRig != null && _duelRig.Available;

        // 1球ごとの判定オーバーレイ（issue #6）。判定＋球速球種を盤面上に大きく短時間だけ出す。
        // 球速・球種は実1球記録がある打席だけ（合成投球列は null＝出さない）。表示専用。
        private VisualElement _pitchCall;
        private Label _pitchCallJudge, _pitchCallJudgeShadow;
        private VisualElement _pitchCallDetail;
        private Label _pitchCallNote, _pitchCallPitchType, _pitchCallSpeed, _pitchCallUnit;
        private float _pitchCallClock;
        private bool _pitchCallOn;
        // 出現/消失の演出（issue #479）。USS の transition は display の切替をまたいで走らないので、
        // 「表示にしてから次フレームで is-enter を外す」で出現を、「is-enter を戻してから畳む」で消失を出す。
        // pitchCallHoldSeconds はこの出現・消失を含めた**総寿命**として扱う＝対決ビューの捕手据え置き
        // （DuelStageRig.PitchCallHoldSeconds に直列化）と1バイトも食い違わせない。
        private const float CallFadeSeconds = 0.16f;

        // 得点時の大型スコアオーバーレイ（issue #60）。加点前スコア→1点ずつの加算アニメ→加点後スコアを
        // 自前クロックで演出する（PitchCallと同じ「Update()の専用クロックで寿命管理」方式）。表示専用。
        private VisualElement _scoreCall;
        private Label _scoreCallOwnName, _scoreCallOwnNum, _scoreCallOppNum, _scoreCallOppName;
        private float _scoreCallClock;
        private bool _scoreCallOn;
        private int _scoreCallOwnScore, _scoreCallOppScore, _scoreCallRunsRemaining;
        private bool _scoreCallOwnScoring;
        private bool _scoreCallPlayDriven;   // true=加算はプレー時計（本塁到達）駆動（issue #438）。false=旧・等間隔（キャプチャ用）

        // ── 表示同期（issue #438）: 結果テキスト・ラインスコア・大型スコアの表示時刻（プレー時計）──
        // 時刻の計算は engine の純関数 PlaybackDisplaySync が単一ソース。プレー開始時に前計算する。
        private double _resultShowAt;                // 結果テキスト＝打者がセーフで生きる結果は到達レッグT1
        private double _lineScorePostAt;             // ラインスコア掲示＝最後の生還走者の本塁到達後
        private System.Collections.Generic.IReadOnlyList<double> _runScoreShowAt
            = System.Array.Empty<double>();          // 大型スコア+1の時刻（各走者の本塁到達T1・昇順）
        private int _runScoreShown;                  // 発火済みの本数

        // 交代ポップアップ（issue #209 表現）。継投・投野交代・野手登板が起きた瞬間に数秒だけ盤面へ浮かせる。
        // PitchCall/ScoreCall と同じ「Update() の専用クロックで寿命管理」方式。表示専用。
        [SerializeField] private float subCallHoldSeconds = 2.6f;
        private VisualElement _subCall;
        private Label _subCallTag, _subCallText;
        private float _subCallClock;
        private bool _subCallOn;

        // 試合開始/終了テロップ「プレイボール」「ゲームセット」（issue #442）。器は判定テロップ
        // （judgement-call。#59/#354）を流用し、新規の見た目部品は作らない。寿命は PitchCall と同じ
        // 「Update() の専用クロック」方式だが、判定オーバーレイ（#236）と同じく実時間で数える
        // （倍速でも見え方を一定にする）。表示専用＝engine には触らない。
        // 判定テロップ（0.6s）より +1.5s 長く掲げる（2026-08-05 監督指示「プレイボールの表示をもっと長く」。
        // ゲームセットも同じ器＝同じ長さになる）。⚠ シーンの MatchLive が直列化値を持つ＝両方変える。
        [SerializeField] private float gameTelopHoldSeconds = (float)Match2DPlaybackElement.JudgementHoldRealSeconds + 1.5f;
        private VisualElement _gameTelopHost;
        private Label _gameTelopLabel, _gameTelopLabelShadow, _gameTelopDetail;
        private float _gameTelopClock;
        private bool _gameTelopOn;
        // 終局後「ゲームセット」を掲げる予約。得点テロップ（#60）の演出が済むのを Update 側で待つ。
        private bool _gameSetPending;
        private int _subLogCount;   // 既に取り込んだ継投ログ件数（_prog.PitchingChanges との差分検知）

        // イニング間のスコア画面（試合画面改修）。攻守が入れ替わった瞬間、次の打席の再生を保留して
        // ラインスコアを大写しにする。寿命は他のオーバーレイと同じく Update() の専用クロックだが、
        // これだけは「進行を止めて待つ」点が違う（_paused と同じく Update 冒頭で抜ける）。
        [SerializeField] private float inningBreakHoldSeconds = 4f;
        private VisualElement _inningBreak;
        private Label _ibCaption;
        private float _inningBreakClock;
        private bool _inningBreakOn;
        /// <summary>イニング間画面が明けたら実行する続き（保留していた采配窓を開く）。</summary>
        private System.Action _afterInningBreak;

        private void OnEnable()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;

            // ScreenRouter は同じ GameObject を SetActive で付け外しするだけなので、このコンポーネントは
            // 試合をまたいで生存する。試合ごとの状態は必ずここで初期化する（前試合の _gameOver 等の持ち越しで
            // 2試合目の操作ボタンが全無効になる不具合を防ぐ）。
            ResetPerMatchState();

            var host = _root.Q<VisualElement>("field-host");
            host?.Clear();   // 前回の盤面要素を残さない（OnEnable ごとに積み上がるのを防ぐ）
            _view = new Match2DPlaybackElement();
            _view.SetColumnFraming(true);   // 全景の3カラム中央＝列内を使い切る既定ビューポート
            host?.Add(_view);
            _fieldHost = host;

            // 判定（アウト/セーフ/フェア/ホームラン）の器は盤面の外＝俯瞰と3Dの両方にかかる層へ。
            // 中身を書くのは Match2DPlaybackElement のまま（表示窓・文言・色クラスの単一ソース）。
            var judgeHost = _root.Q<VisualElement>("judgement-call");
            var judgeLabel = _root.Q<Label>("judgement-judge");
            var judgeShadow = _root.Q<Label>("judgement-judge-sh");
            var judgeDetail = _root.Q<Label>("judgement-detail");
            if (judgeHost != null && judgeLabel != null && judgeDetail != null)
                _view.SetJudgementHost(judgeHost, judgeLabel, judgeShadow, judgeDetail);
            // 試合開始/終了テロップ（issue #442）も同じ器へ載せる（掲げるのは「最初のプレーの前」と
            // 「終局後」だけ＝プレー時刻の純関数で描く Match2DPlaybackElement と時間帯が重ならない）。
            _gameTelopHost = judgeHost;
            _gameTelopLabel = judgeLabel;
            _gameTelopLabelShadow = judgeShadow;
            _gameTelopDetail = judgeDetail;
            // 判定テキストの字送りは盤面の実幅から出す（USS に vw / clamp が無い。issue #479）。
            RegisterCallTextMetrics(judgeHost, judgeLabel, judgeShadow);

            _leftLineup = _root.Q<VisualElement>("left-lineup");
            _rightLineup = _root.Q<VisualElement>("right-lineup");
            _leftPitcher = _root.Q<VisualElement>("left-pitcher");
            _rightPitcher = _root.Q<VisualElement>("right-pitcher");
            _hudHost = _root.Q<VisualElement>("hud-host");
            BuildHudCorners();   // 右上=打者HUD／右下=投手HUD の常設コーナー
            _capHistHost = _root.Q<VisualElement>("caption-history");
            _leftTeam = _root.Q<Label>("left-team");
            _rightTeam = _root.Q<Label>("right-team");
            _statsProvider = new MatchLiveStatsProvider();

            // 実況バー（caption/result/batter）は issue #289 で撤去済み＝クエリは null を返し、
            // 各所の書き込みは null ガードで素通りする（実況ロジック自体は _capHist に残す）。
            _caption = _root.Q<Label>("caption");
            _result = _root.Q<Label>("result-chip");
            _batter = _root.Q<Label>("batter");

            // クリック中断ポップアップ（issue #289）。盤面クリックの検出はルートで拾い、
            // 残置した操作列・指示パネル・モーダル内のクリックは除外する（IsPauseExempt）。
            _pauseModal = _root.Q<VisualElement>("pause-modal");
            _pmSituation = _root.Q<Label>("pm-situation");
            _pmTimeout = _root.Q<Button>("pm-timeout");
            if (_pmTimeout != null) _pmTimeout.clicked += OnSendTimeout;
            _pmSub = _root.Q<Button>("pm-sub");
            if (_pmSub != null) _pmSub.clicked += OnOpenSubstitution;
            _pmSkip = _root.Q<Button>("pm-skip");
            if (_pmSkip != null) _pmSkip.clicked += OnSkip;
            _pmResume = _root.Q<Button>("pm-resume");
            if (_pmResume != null) _pmResume.clicked += OnResume;
            _pauseModal?.RegisterCallback<PointerDownEvent>(OnPauseScrimDown);
            _root.RegisterCallback<PointerDownEvent>(OnRootPointerDown);
            _backHome = _root.Q<Button>("back-home");
            if (_backHome != null) _backHome.clicked += OnBackHome;
            WireSpeed("spd-05", 0.5f); WireSpeed("spd-1", 1f); WireSpeed("spd-2", 2f); WireSpeed("spd-4", 4f);
            _camWork = _root.Q<Button>("cam-work");
            if (_camWork != null) _camWork.clicked += OnToggleCameraWork;
            ApplyCameraWorkState();
            _duelHost = _root.Q<VisualElement>("duel-host");
            _duelHost?.Clear();   // OnEnable ごとの積み上がり防止（field-host と同じ運用）
            _duelImg = null;
            _duelBtn = _root.Q<Button>("duel-view");
            if (_duelBtn != null) _duelBtn.clicked += OnToggleDuelView;
            InitDuelViewDefault();   // 既定=対決ビュー（issue #312）。Rig不可なら俯瞰へフォールバック
            ApplyDuelViewState();

            // 采配の手札は盤面の外（列の下）。打席をまたいで作り直さないのでここで1度だけ生成する。
            var tacticsHost = _root.Q<VisualElement>("tactics-host");
            if (tacticsHost != null)
            {
                tacticsHost.Clear();
                _signPad = new TacticsCardDeck();
                tacticsHost.Add(_signPad);
            }
            WirePitchTactics();

            // B/S/O と塁ダイヤは ScoreBug（部品辞書）の中。1球ごとに動くのでここから直接駆動する。
            _lampsB = new[] { _root.Q<VisualElement>("sbug-lamp-b0"), _root.Q<VisualElement>("sbug-lamp-b1"), _root.Q<VisualElement>("sbug-lamp-b2") };
            _lampsS = new[] { _root.Q<VisualElement>("sbug-lamp-s0"), _root.Q<VisualElement>("sbug-lamp-s1") };
            _lampsO = new[] { _root.Q<VisualElement>("sbug-lamp-o0"), _root.Q<VisualElement>("sbug-lamp-o1") };
            _bases = new[] { _root.Q<VisualElement>("sbug-base-1"), _root.Q<VisualElement>("sbug-base-2"), _root.Q<VisualElement>("sbug-base-3") };

            _pitchCall = _root.Q<VisualElement>("pitch-call");
            _pitchCallJudge = _root.Q<Label>("pitch-call-judge");
            _pitchCallJudgeShadow = _root.Q<Label>("pitch-call-judge-sh");
            _pitchCallDetail = _root.Q<VisualElement>("pitch-call-detail");
            _pitchCallNote = _root.Q<Label>("pitch-call-note");
            _pitchCallPitchType = _root.Q<Label>("pitch-call-ptype");
            _pitchCallSpeed = _root.Q<Label>("pitch-call-speed");
            _pitchCallUnit = _root.Q<Label>("pitch-call-unit");
            RegisterCallTextMetrics(_pitchCall, _pitchCallJudge, _pitchCallJudgeShadow);

            _scoreCall = _root.Q<VisualElement>("score-call");
            _scoreCallOwnName = _root.Q<Label>("score-call-own-name");
            _scoreCallOwnNum = _root.Q<Label>("score-call-own-num");
            _scoreCallOwnNum?.AddToClassList("f-num-bd");
            _scoreCallOppNum = _root.Q<Label>("score-call-opp-num");
            _scoreCallOppNum?.AddToClassList("f-num-bd");
            _scoreCallOppName = _root.Q<Label>("score-call-opp-name");
            _subCall = _root.Q<VisualElement>("sub-call");
            _subCallTag = _root.Q<Label>("sub-call-tag");
            _subCallText = _root.Q<Label>("sub-call-text");

            _inningBreak = _root.Q<VisualElement>("inning-break");
            _ibCaption = _root.Q<Label>("ib-caption");
            _inningBreak?.RegisterCallback<PointerDownEvent>(OnInningBreakDown);

            ResetPerMatchVisuals();
            _subPanel = new MatchSubstitutionPanel(_root, OnSubstitutionApplied);
            _subPanel.Bind();
            BuildGame();
            _subPanel.SetMatch(_prog, _managerIsAway);
            // 指示パネルの初期フェーズ（1打席目の攻守）。以降は采配窓ごとに OpenWindow が追従させる。
            if (_prog != null) _signPad?.SetPhase(_prog.IsTeamOnOffense(_managerIsAway));
            // 試合開始前の掲示板（校名と空の升目）を先に掲げる。掲げないと最初の打席が確定するまで枠が出ない。
            PostLineScore(1, isTop: true, finished: false);
            SetBackHomeVisible(false);
            if (_caption != null) _caption.text = "試合開始。";
            UpdateControlState();
            RefreshPanel();   // 初期スタメン列（今日の成績は0・現打者/HUDは打席開始で点灯）
            _view.SetResting(false, false, false);   // 打席開始前も守備陣を定位置に表示（盤面を空にしない）

            // 自動連続進行（issue #173）: 打席頭で止めず終局まで流す。スクショ時（CaptureMode）は開始せず、
            // シューターの AdvanceForCapture で従来どおり1打席ずつ手動駆動する。
            // 「プレイボール」テロップ（issue #442）は試合の入り口＝ここにしか無いので、最初のプレーの
            // 前に一度だけ出る（攻守交代・2打席目以降では出ない）。初打席の采配窓は表示中カウントを
            // 止めて待つ（Update の Window 分岐）＝寿命が必ず窓の中で尽きてから初球が始まる。
            // スクショ時は ShowGameTelopForCapture で出す。
            if (!CaptureMode)
            {
                ShowGameTelop("プレイボール");
                StartAutoRun();
            }
        }

        // 画面を離れるときにハンドラを外す（OnEnable が毎回登録するため、外さないと往復のたび多重登録になり
        // 1クリックで複数打席進んでしまう）。
        private void OnDisable()
        {
            if (_pmTimeout != null) _pmTimeout.clicked -= OnSendTimeout;
            if (_pmResume != null) _pmResume.clicked -= OnResume;
            if (_pmSub != null) _pmSub.clicked -= OnOpenSubstitution;
            if (_pmSkip != null) _pmSkip.clicked -= OnSkip;
            _pauseModal?.UnregisterCallback<PointerDownEvent>(OnPauseScrimDown);
            _inningBreak?.UnregisterCallback<PointerDownEvent>(OnInningBreakDown);
            _root?.UnregisterCallback<PointerDownEvent>(OnRootPointerDown);
            _subPanel?.Close();
            if (_backHome != null) _backHome.clicked -= OnBackHome;
            if (_camWork != null) _camWork.clicked -= OnToggleCameraWork;
            if (_duelBtn != null) _duelBtn.clicked -= OnToggleDuelView;
            _duelRig?.Dispose();
            _duelRig = null;
            _duelImg = null;
            foreach (var (b, h) in _speedHandlers) b.clicked -= h;
            _speedHandlers.Clear();
            _speedButtons.Clear();
        }

        // 試合ごとに初期化する進行状態（試合をまたいだ持ち越し禁止）。要素クエリ前に呼べるフィールドだけを扱う。
        private void ResetPerMatchState()
        {
            _gameOver = false;
            _stage = LiveStage.Idle;
            _paused = false;
            _timeoutSentThisPause = false;
            _autoStarted = false;
            _finalResult = null;
            _onComplete = null;
            _current = null;
            _atBat = null;
            _atBatNext = null;
            _afterInningBreak = null;
            _shownInning = 0;
            _shownIsTop = false;
            _shownAnyHalf = false;
            _paQueue.Clear();
            _paConfirmed = false;
            _stealPlay = null;       // 打席途中の盗塁再生（issue #485）は試合をまたがない
            _pickoffPlay = null;     // 打席途中の牽制再生（issue #486）も同様
            _batteryMissPlay = null; // 打席途中の暴投・捕逸再生（issue #487）も同様
            _dropThirdStrikePlay = null; // 打席途中の振り逃げ再生（issue #488）も同様
            _windowClock = _windowHold = _windowCarry = 0f;
            _displayedLogCount = 0;
            _prog = null;
            _homeTeam = null;
            _t = 0;
            _speed = 1f;
            _pitchIdx = 0;
            _pitchClock = 0f;
            _pitchCallAt = pitchIntervalSeconds;
            _inPitchPhase = false;
            _pitchBallVisible = false;
            _pitchCallClock = 0f;
            _pitchCallOn = false;
            _scoreCallClock = 0f;
            _scoreCallOn = false;
            _subCallClock = 0f;
            _subCallOn = false;
            _gameTelopClock = 0f;
            _gameTelopOn = false;
            _gameSetPending = false;
            _inningBreakClock = 0f;
            _inningBreakOn = false;
            _subLogCount = 0;
            _pitchBattingChoice = null;
            _signChoice = null;
            _stealChoice = null;
            // 方針カードは試合をまたがない（前の試合の前進守備が次の試合に残らないよう明示的に戻す）。
            _approach = BattingApproach.Normal;
            _defPolicy = null;
            _defGear = null;
            _defInfield = null;
            _defOutfield = null;
            _capHist.Clear();
            _speedButtons.Clear();
            _speedHandlers.Clear();
            _cameraWorkWanted = false;
            _duelOn = false;
            _duelManualOff = false;
        }

        // 試合ごとに初期化する表示（前試合の実況履歴・BSO・結果チップを残さない）。要素クエリ後に呼ぶ。
        private void ResetPerMatchVisuals()
        {
            _capHistHost?.Clear();
            _signPad?.ClearSelection();
            SetVisible(_pauseModal, false);   // 前試合の中断ポップアップを持ち越さない
            SetVisible(_inningBreak, false);  // 前試合のイニング間画面を持ち越さない
            _lineScorePosted = false;
            SetCount(0, 0);
            SetOuts(0);
            SetBases(false, false, false);
            HidePitchCall();
            HideScoreCall();
            HideSubCall();
            foreach (var sb in _speedButtons) sb.EnableInClassList("chip-btn--on", sb.name == "spd-1");
            if (_result != null) _result.style.display = DisplayStyle.None;
        }

        private void BuildGame()
        {
            // 大会/タウンフローからの実試合を観戦するライブ要求があればそれを消費する（一度きり）。
            var req = Pending;
            Pending = null;
            if (req?.Progression != null)
            {
                _prog = req.Progression;
                _managerIsAway = req.ManagerIsAway;
                _onComplete = req.OnComplete;
                managerTacticalSense = req.ManagerTacticalSense;
                _rematchOnTie = req.RematchOnTie;
                _homeTeam = null;   // 実試合は進行体が保持（代打候補名の外部参照は使わない）
                _awayDisp = req.AwayName ?? awayName;
                _homeDisp = req.HomeName ?? homeName;
                _awayColors = req.AwayColors;
                _homeColors = req.HomeColors;
                // 掲示板の校名はエンジンの観測データ（LiveLineScore.Name）から出るのでここでは埋めない。
                SetColumnTitles();
                return;
            }

            // デモ/スクショ用の自前生成（本番フローでは通らない）。
            _managerIsAway = false;
            _onComplete = null;
            _rematchOnTie = true;
            // 委任AIの采配値も成長した監督の実値を使う（旧ハードコード70を廃す, issue #171）。
            managerTacticalSense = KokoSim.Unity.Shell.ManagerService.TacticalSenseForAi;
            // デモの相手校（away＝右列）は本番と同じ StrengthTeamFactory 生成（氏名＋控え8人つき）で観戦する。
            var awayRng = new KokoSim.Engine.Core.Xoshiro256Random(gameSeed ^ 0x1234ABCDUL);
            var away = KokoSim.Engine.Nation.StrengthTeamFactory.Create(58, awayName, awayRng);
            // デモの自校（home＝左列）は実部員（RosterService）から組み、背番号・調子・通算の join を実機同様に見せる。
            var home = RosterTeamBuilder.Build(RosterService.Active, homeName);
            // RosterTeamBuilder.Build が積む控え（投手枠2枚）へ、代打采配のデモ用の控えを足す。
            // 控えは1本の名簿なので上書きせず Merge する（上書きすると継投先が消える。issue #381）。
            home = home with
            {
                Reserves = KokoSim.Engine.Match.Game.TeamReserves.Merge(home.Reserves, BuildBench()),
            };
            _homeTeam = home;
            _awayDisp = awayName;
            _homeDisp = homeName;
            SetColumnTitles();
            // 観戦する試合だけ CaptureTimelines/CapturePitches=true（裏試合は GameEngine を通らずコストゼロ）。
            // CapturePitches は1球進行（設計書15 §8）の供給口。どちらも乱数中立＝結果・digest・帯は不変。
            var ctx = new GameContext { CaptureTimelines = true, CapturePitches = true };
#if KOKOSIM_DEBUG || UNITY_EDITOR || DEVELOPMENT_BUILD
            // デバッグHUD（設計書17 §5, F3）が開いているときだけ観測を差し込む。閉じていれば恒等＝従来と完全一致。
            ctx = Unity.Debugging.DebugTraceHub.AttachTo(ctx);
#endif
#if UNITY_EDITOR
            // 撮影ハーネス（稀プレーの絵の検証）だけが挿す差し替え口。既定 null＝恒等。
            if (CaptureContextOverride != null) ctx = CaptureContextOverride(ctx);
#endif
            _prog = new MatchProgression(away, home, ctx, gameSeed);
        }

        // 列見出し（左=自校 / 右=相手校）。監督が先攻(away)か後攻(home)かで名前を割り当てる。
        private void SetColumnTitles()
        {
            var own = _managerIsAway ? _awayDisp : _homeDisp;
            var opp = _managerIsAway ? _homeDisp : _awayDisp;
            if (_leftTeam != null) _leftTeam.text = own;
            if (_rightTeam != null) _rightTeam.text = opp;
        }

        // 代打采配デモ用の控え（識別しやすい名前）。本番は登録メンバー（設計書06 §3.3b）から供給する。
        // 背番号16/17＝RosterTeamBuilder が積む控え投手と重ならない番号（Merge の並びが安定する）。
        private static IReadOnlyList<KokoSim.Engine.Players.Player> BuildBench()
        {
            KokoSim.Engine.Players.Player Ph(string name, KokoSim.Engine.Match.Field.FieldPosition pos, int number, int contact) =>
                new()
                {
                    Position = pos, Name = name, UniformNumber = number,
                    Contact = contact, Power = 55, LaunchTendency = 50, Discipline = 55,
                    Speed = 55, ArmStrength = 50, Fielding = 50, Catching = 50,
                };
            return new[]
            {
                Ph("代打 一郎", KokoSim.Engine.Match.Field.FieldPosition.FirstBase, 16, 68),
                Ph("代走 次郎", KokoSim.Engine.Match.Field.FieldPosition.SecondBase, 17, 50),
            };
        }

        private static IReadOnlyList<DevelopingPlayer> BuildRoster(ulong seed, double initMean)
        {
            var rng = new KokoSim.Engine.Core.Xoshiro256Random(seed);
            var coeff = new RosterCoefficients { InitLevelMean = initMean };
            var list = new List<DevelopingPlayer>();
            for (var grade = 1; grade <= 3; grade++)
                foreach (var p in ProspectGenerator.Intake(grade, coeff, rng))
                {
                    p.Grade = grade;
                    list.Add(p);
                }
            return list;
        }

        // ── 1球進行のポンプ（設計書15 §8・issue #173 の自動連続進行を1球単位へ移したもの）──
        // 「采配窓 →〈間〉→ 1球 → 描く → 次の窓」を連鎖し、打席が確定した球だけ打球再生へ寄り道する。
        // プレイヤーは盤面クリックで任意に中断し、采配を差し込んでから「再開」で戻る。
        //
        // エンジン呼び出しはメインスレッド同期。旧・打席単位では1回の Advance() が打席まるごと（打球解決＋
        // タイムライン生成）を回すので背景スレッド（BackgroundGameOp, issue #160）へ逃がしていたが、
        // 1球単位では1回の AdvancePitch() が投球1球ぶんしか進まない＝1フレームに収まる。加えて
        // BackgroundGameOp は同時1本の共有スロットなので、1球ごとに占有すると交代・伝令の可否判定
        //（issue #278 の守り）が常時「解決中」になってしまう。
        //
        // 実測（MatchLivePitchPumpProbe・2026-08-01）: 1試合371歩のうち温まった後は平均1.23ms・最大11.7ms、
        // 打席を確定する歩だけでも平均2.24ms（1フレーム=16.7ms）。設計書15 §8.3-⑤ の「要実測」の答え。
        // 最初の20歩だけ合計1.4秒かかるが、これは JIT と係数の遅延ロードで、実ゲームでは MatchLive を
        // 開く前に裏試合が大量に走って払い終えている。
        //
        // これにより「背景スレッドが進行体を握ったままメインが触る」競合（issue #278）は構造的に消える
        // ＝ポンプ側で BackgroundGameOp.Running を見張る必要は無い（見張ると、他画面の背景処理中に
        // 打席の切れ目で止まったまま誰も起こさない、という別の詰まりを作る）。

        // 最初の采配窓を起こす（OnEnable から1回だけ・スクショ時は呼ばない）。
        private void StartAutoRun()
        {
            if (_autoStarted) return;
            _autoStarted = true;
            AdvanceToNextWindow();
        }

        /// <summary>
        /// 次の采配窓まで進める（指示は押し込まない）。打席が終わった直後・イニング間の明けから呼ぶ。
        /// この境界では1球も投げられないので、選択中のカードを消費してはいけない。
        /// </summary>
        private void AdvanceToNextWindow() => PumpEngine(commitTactics: false);

        /// <summary>
        /// 采配窓を1つ抜ける＝選ばれている「1手」をこの球へ押し込み、1球投げさせる。
        /// 窓の間（＝球間）が終わったときだけ呼ぶ。
        /// </summary>
        private void ThrowFromWindow() => PumpEngine(commitTactics: true);

        private void PumpEngine(bool commitTactics)
        {
            if (_gameOver || _paused || _inningBreakOn || CaptureMode) return;
            if (_prog == null) return;
            if (_subPanel != null && _subPanel.IsOpen) return;   // 交代モーダルを開いている間は待つ

            if (commitTactics) PushPitchTacticsChoice();
            var before = _prog.LastPitch;
            var result = _prog.AdvancePitch();
            // 窓を1つ抜けた＝1球投げ終わっている（打席の頭・半回の変わり目では投げていない＝参照が変わらない）。
            var thrown = ReferenceEquals(_prog.LastPitch, before) ? null : _prog.LastPitch;
            if (commitTactics) ResetPitchTacticsChoice();   // ①の1手は1球で消費＝消灯

            DispatchStep(result, thrown);
            // 直前の1歩で起きた継投をポップアップ＋実況履歴へ（issue #209 表現）。新着が無ければ何もしない。
            DrainSubstitutionEvents();
        }

        private void DispatchStep(AdvancePitchResult result, LivePitch thrown)
        {
            if (result == AdvancePitchResult.Finished)
            {
                EndGame(_prog.BuildResult());
                return;
            }

            if (result == AdvancePitchResult.PlateAppearance)
            {
                // 打席が確定した。<b>打席を終わらせた1球はここから描く</b>——インプレーの打球は確定して
                // 初めて手に入る（BattedOf が _current.Play を読む）ので、順序は入れ替えられない。
                _current = _prog.Current;
                _paConfirmed = true;
                if (thrown != null) { EnqueueThrownPitch(thrown, terminal: true); return; }
                // 1球も投げずに確定した打席（敬遠・場面注入）。1球供給が無いので合成投球列で描く。
                // 采配窓を1つも通らないので、半回の変わり目の判定もここで自前に行う。
                if (!CaptureMode && _shownAnyHalf
                    && (_current.IsTop != _shownIsTop || _current.Inning != _shownInning))
                {
                    var ei = _shownInning;
                    var et = _shownIsTop;
                    _shownInning = _current.Inning;
                    _shownIsTop = _current.IsTop;
                    _windowCarry = 0f;
                    BeginInningBreak(ei, et, _current.Inning, _current.IsTop, BeginPaFromSequence);
                    return;
                }
                _shownInning = _current.Inning;
                _shownIsTop = _current.IsTop;
                _shownAnyHalf = true;
                BeginPaFromSequence();
                return;
            }

            // まだ打席の途中、または次の打席（半回）の頭。
            var at = _prog.CurrentAtBat;
            if (thrown != null)
            {
                // いま投げ終わった球を描く。次の窓（＝この at）はその球が描き終わってから開ける
                //（先に開けるとカウントが判定より先に動いてネタバレになる）。
                _atBatNext = at;
                EnqueueThrownPitch(thrown, terminal: false);
                return;
            }
            OpenWindowOrBreak(at);
        }

        /// <summary>投げ終えた1球を投球列へ積み、その球の描画を始める。</summary>
        private void EnqueueThrownPitch(LivePitch live, bool terminal)
        {
            // 塁は「この球を投げる時点」＝直前の采配窓の状態（打席途中の盗塁を絵に載せるため）。
            var b = _atBat;
            _paQueue.Add(new QueuedPitch(live.Pitch,
                b?.BaseFirst ?? false, b?.BaseSecond ?? false, b?.BaseThird ?? false, live));
            BeginPitchRender(_paQueue.Count - 1);
            if (terminal) _atBatNext = null;
        }

        /// <summary>
        /// 采配窓を開く。半回が変わっていたらイニング間画面を先に挟む（明けたらこの窓を開く）。
        /// 半回の変化はエンジンにイベントが無いので、直前に映していた半回との比較で見る。
        /// </summary>
        private void OpenWindowOrBreak(LiveAtBat at)
        {
            if (at == null) { _stage = LiveStage.Idle; return; }
            if (!CaptureMode && _shownAnyHalf && (at.IsTop != _shownIsTop || at.Inning != _shownInning))
            {
                var endedInning = _shownInning;
                var endedIsTop = _shownIsTop;
                _shownInning = at.Inning;
                _shownIsTop = at.IsTop;
                _windowCarry = 0f;
                BeginInningBreak(endedInning, endedIsTop, at.Inning, at.IsTop, () => OpenWindow(at));
                return;
            }
            OpenWindow(at);
        }

        /// <summary>
        /// 采配窓を開く: いまの対戦を画面へ出し、指示を受け付ける状態にする。
        /// 打席の頭だけ短い間を置く（監督が打者を見てサインを出す間合い。設計書15 §8.3-①）。
        /// 打席の途中は球間そのものが窓なので、追加の間は置かない。
        /// </summary>
        private void OpenWindow(LiveAtBat at)
        {
            _atBat = at;
            _atBatNext = null;
            _shownInning = at.Inning;
            _shownIsTop = at.IsTop;
            _shownAnyHalf = true;

            var head = at.PitchNumberInPa <= 1;
            if (head)
            {
                // 新しい打席の頭＝前の打席の後片付け。投球列と確定フラグをここで落とす。
                _paQueue.Clear();
                _pitchIdx = 0;
                _paConfirmed = false;
                _current = null;
                _lineScorePosted = false;   // 掲示板は次のプレーが確定した瞬間に掲げ直す
                SetDuelViewAuto(true);   // 次打席の開始で対決ビューへ復帰（issue #312。手動OFF中は据え置き）
                // 開けたら**その場で**仕切り直す。窓の間（paHeadDwellSeconds）を挟むようになったので、
                // 前打席の振りが凍ったまま残っていると新しい打者が球も無しに振り抜く
                // （2026-08-01「幽霊空振り」。DuelStageRig.ResetForNewPlateAppearance の項）。
                _duelRig?.ResetForNewPlateAppearance();
                if (_result != null) _result.style.display = DisplayStyle.None;
            }

            ApplyWindowSituation();
            if (head) ApplyDuelHandedness();   // この打席の左右で立たせ替える（継投後の利き腕もここで追従。issue #337）
            if (_caption != null && head) _caption.text = at.BatterName + " 打席";

            _stage = LiveStage.Window;
            _windowClock = _windowCarry;
            _windowCarry = 0f;
            _windowHold = head ? paHeadDwellSeconds : 0f;
            _inPitchPhase = false;
            UpdateControlState();
        }

        /// <summary>窓の局面（カウント・アウト・塁・対戦HUD・カードの攻守と可否）を画面へ反映する。</summary>
        private void ApplyWindowSituation()
        {
            if (_atBat == null) return;
            SetCount(_atBat.Balls, _atBat.Strikes);
            SetOuts(_atBat.Outs);
            SetBases(_atBat.BaseFirst, _atBat.BaseSecond, _atBat.BaseThird);
            RefreshPanel();                                       // HUD（打者・投手・球数）とスタメン列
            _signPad?.SetPhase(_atBat.IsTop == _managerIsAway);   // 攻撃中は打撃指示だけ／守備中は配球指示だけ
            _signPad?.RefreshEnabled();                           // 走者の有無でバント/盗塁の可否が変わる
        }

        /// <summary>
        /// 采配カードの受付ゲート（issue #493）。実際にエンジンへ指示が反映される締切（<see cref="ThrowFromWindow"/>
        /// が読む瞬間）は変えない——対決ビューで<b>次の1球の投手モーションが始まった</b>ら、見た目にも
        /// 押せなくする。俯瞰（デバッグ2D）は振りかぶりが無いので対象外＝常時受付のまま（球間そのものが窓）。
        /// 采配窓（打席の頭の間・打席途中の球間）は常に開け直す＝投球フェーズを抜けた瞬間に自動で戻る。
        /// </summary>
        private void UpdateTacticsAcceptance()
        {
            var accepting = !(_inPitchPhase && DuelPacingActive && (_duelRig?.HasCutToNextWindup ?? false));
            _signPad?.SetAcceptingInput(accepting);
        }

        // ── クリック中断（issue #289） ──
        // 盤面のどこをクリックしても進行を一時停止してポップアップを開く。中断そのものはエンジンに
        // 一切触れない（CallTimeout を呼ばない＝伝令は減らない・動揺解除も走らない）。
        // 再生中なら進行段階（_stage）はそのまま（Update が _paused で凍結する）。再開すると凍結地点から
        // 続きが流れ、その打席の後処理（FinishPaView）も正しく走る。
        private void OnRootPointerDown(PointerDownEvent evt)
        {
            if (CaptureMode) return;   // スクショハーネス中は中断を出さない（バッチ撮影を暗幕で潰さない）
            if (_gameOver || _paused || _prog == null) return;
            if (_subPanel != null && _subPanel.IsOpen) return;
            if (IsPauseExempt(evt.target as VisualElement)) return;
            OpenPauseMenu();
        }

        // 中断クリックの対象外＝押した操作の実行と中断が二重反応しない場所。
        // ボタン全般（速度・カメラ・対決ビュー等）／指示パネル（SignPad）／モーダル内。
        private static bool IsPauseExempt(VisualElement e)
        {
            for (var v = e; v != null; v = v.parent)
            {
                if (v is Button) return true;
                if (v.ClassListContains("tcards")) return true;   // 采配の手札。押した指示と中断が二重反応しない
                if (v.ClassListContains("modal-scrim")) return true;
                if (v.ClassListContains("inning-break")) return true;   // クリック＝スキップ。中断は開かない
            }
            return false;
        }

        // 暗幕（カードの外）クリックは「再開」と同じ＝もう1クリックで即戻れる。
        private void OnPauseScrimDown(PointerDownEvent evt)
        {
            if (evt.target == _pauseModal) OnResume();
        }

        private void OpenPauseMenu()
        {
            _paused = true;
            _timeoutSentThisPause = false;
            UpdatePauseMenu();
            SetVisible(_pauseModal, true);
        }

        // ポップアップ内ボタンの活性と表示。打席解決中（BackgroundGameOp）はエンジンを触る操作
        //（伝令・交代・スキップ）を伏せ、解決完了時の UpdateControlState 経由で復帰する（issue #278 と同じ守り）。
        private void UpdatePauseMenu()
        {
            if (_prog == null) return;
            if (_pmSituation != null) _pmSituation.text = PauseSituationText();
            var resolving = BackgroundGameOp.Running;
            if (_pmTimeout != null)
            {
                var off = _prog.OffenseTimeoutsLeft(_managerIsAway);
                var def = _prog.DefenseTimeoutsLeft(_managerIsAway);
                _pmTimeout.text = _timeoutSentThisPause
                    ? "📣 伝令を送った"
                    : "📣 伝令を送る\n攻" + off + " / 守" + def;
                SetEnabled(_pmTimeout,
                    !resolving && !_timeoutSentThisPause && _prog.CanCallTimeout(_managerIsAway));
            }
            SetEnabled(_pmSub, !resolving);
            SetEnabled(_pmSkip, !resolving);
        }

        // 局面サマリ（ポップアップ見出し）。いま映している局面だけを出す＝解決済みスコアのネタバレを
        // しない（掲示板・盤面の見た目と一致させる）。1球進行中は采配窓の局面がそのまま「いま」。
        private string PauseSituationText()
        {
            if (_atBat is { } at)
            {
                var h = at.Inning + "回" + (at.IsTop ? "表" : "裏");
                var o = at.Outs == 0 ? "無死" : at.Outs + "死";
                var r = RunnersText(at.BaseFirst, at.BaseSecond, at.BaseThird);
                return h + " " + o + " " + r + "　" + _awayDisp + " " + at.AwayScore + " - " + at.HomeScore + " " + _homeDisp;
            }
            if (_current == null) return _awayDisp + "　－　" + _homeDisp;
            var half = _current.Inning + "回" + (_current.IsTop ? "表" : "裏");
            var outs = _current.OutsBefore == 0 ? "無死" : _current.OutsBefore + "死";
            var runners = RunnersText(_current.BaseFirstBefore, _current.BaseSecondBefore, _current.BaseThirdBefore);
            // 打席前スコアへ巻き戻す（LivePlateAppearance のスコアは解決後。BeginScoreCall と同じ導出）。
            var awayBefore = _current.IsTop ? _current.AwayScore - _current.RunsScored : _current.AwayScore;
            var homeBefore = _current.IsTop ? _current.HomeScore : _current.HomeScore - _current.RunsScored;
            return half + " " + outs + " " + runners + "　" + _awayDisp + " " + awayBefore + " - " + homeBefore + " " + _homeDisp;
        }

        private static string RunnersText(bool first, bool second, bool third)
        {
            if (!first && !second && !third) return "走者なし";
            if (first && second && third) return "満塁";
            var s = "";
            if (first) s += "一";
            if (second) s += (s.Length > 0 ? "・" : "") + "二";
            if (third) s += (s.Length > 0 ? "・" : "") + "三";
            return s + "塁";
        }

        // 「伝令を送る」（ポップアップ内）: ここで初めて伝令を1回消費する（クリック中断自体は消費しない）。
        // 攻守はいまの表裏×自校の先後で自動判定（攻撃中＝攻撃伝令／守備中＝守備伝令）。残り0では発動しない。
        // 効果は次に解決される打席から効く（エンジンの決定論経路を通す＝帯・digest不変。設計書09 §3）。
        private void OnSendTimeout()
        {
            if (_gameOver || !_paused || _prog == null) return;
            if (BackgroundGameOp.Running) return;   // 解決中は打席境界が未確定なので受け付けない
            if (_timeoutSentThisPause) return;      // 1中断につき1回
            var kind = _prog.CallTimeout(_managerIsAway);
            if (kind == MatchProgression.PlayerTimeoutResult.Unavailable) return;
            _timeoutSentThisPause = true;
            UpdatePauseMenu();   // 残数と送信済み表示を反映
        }

        // 「再開」: 中断を解いて自動連続進行へ戻る。暗幕クリックでも呼ばれる（OnPauseScrimDown）。
        private void OnResume()
        {
            if (_gameOver || !_paused) return;
            _subPanel?.Close();
            SetVisible(_pauseModal, false);
            _paused = false;
            UpdateControlState();
            // 再生・投球・采配窓の途中で止めていたなら Update が続きを流す。どこにも居ない（打席の
            // 切れ目で止めていた）場合だけ、次の采配窓を取りに行く。
            if (_stage == LiveStage.Idle) AdvanceToNextWindow();
        }

        // 選手交代（設計書09 §6）。issue #289 で伝令の消費とは切り離した＝中断中ならいつでも開ける。
        // 交代モーダルは pause-modal の上に重なり、閉じるとポップアップへ戻る。
        // 攻撃中／守備中で出せる選択肢はモーダル側が出し分ける。自校が先攻(away)か後攻(home)かは _managerIsAway。
        private void OnOpenSubstitution()
        {
            if (_gameOver || _prog == null || !_paused) return;
            _subPanel?.Open();
        }

        // 交代確定後（即時・取り消し不可）。該当スロットを即入替（退いた選手名を薄く併記）。
        private void OnSubstitutionApplied()
        {
            RefreshPanel();
            if (_caption != null) _caption.text = "選手交代を行った。";
        }

        private void OnSkip()
        {
            if (_gameOver || _prog == null) return;
            // 解決中は打席境界が未確定＝進行体を背景スレッドと奪い合う（issue #278）。「タイム」と同じ扱いで弾く。
            // ボタン自体も UpdateControlState で伏せているが、二重に守る（押下配信は1フレーム遅れうる）。
            if (BackgroundGameOp.Running) return;
            _subPanel?.Close();
            SetVisible(_pauseModal, false);
            _paused = false;
            var result = _prog.SkipDelegateToAi(_managerIsAway, managerTacticalSense);
            _view.SetPlay(null);
            EndGame(result);
            if (_caption != null) _caption.text = "以降を委任して試合終了。";
        }

        // 終局処理（自然終了・スキップ委任 共通）。結果を確定表示し、ライブ観戦なら試合結果画面へ渡す。
        private void EndGame(GameResult result)
        {
            EndFieldPlay();   // 打球の途中で終局しても守備陣・走者を場に置き去りにしない
            _view.HideJudgement();
            _gameOver = true;
            _stage = LiveStage.Over;
            _inPitchPhase = false;
            _paused = false;
            // 中断ポップアップを開いたまま背景解決が終局に達した場合も閉じる（結果画面へ暗幕を残さない）。
            SetVisible(_pauseModal, false);
            _finalResult = result;
            UpdateScoreboardFinal(result);
            if (_result != null) { _result.text = FinalResultText(result); _result.style.display = DisplayStyle.Flex; }
            if (_caption != null) _caption.text = "試合終了。";
            UpdateControlState();
            // 「ゲームセット」テロップ（issue #442）: 掲げるのは Update 側＝得点テロップ（#60）の
            // 演出が済んでから（サヨナラ等の得点絡みの決着で重ねない）。結果画面への受け渡しも
            // テロップを見せ切ってから（FinishGameEndHandOff）。9回決着・サヨナラ・委任（OnSkip）の
            // どの終了経路もここを通るので、経路ごとの分岐は持たない。
            _gameSetPending = true;
        }

        // 終局処理の後半＝結果画面への受け渡し。「ゲームセット」を見せ切ってから呼ぶ（issue #442）。
        private void FinishGameEndHandOff()
        {
            if (!HandOffToResultScreen()) SetBackHomeVisible(_onComplete != null);
        }

        /// <summary>
        /// 試合結果画面（issue #13）へ自動遷移する。大会への結果反映（OnComplete）は結果画面の「閉じる」へ
        /// そのまま預ける＝従来「戻る」ボタンが担っていた後処理を1つ先の画面へ移すだけで、大会の進行は不変。
        /// ルータ不在／デモ観戦（OnComplete なし）のときは遷移せず、従来どおり「戻る」導線に任せる。
        /// </summary>
        private bool HandOffToResultScreen()
        {
            var router = KokoSim.Unity.Shell.ScreenRouter.Instance;
            if (router == null || _onComplete == null) return false;

            var cb = _onComplete;
            var final = _finalResult;
            _onComplete = null;
            SetBackHomeVisible(false);
            KokoSim.Unity.MatchResult.MatchResultController.Pending =
                new KokoSim.Unity.MatchResult.MatchResultController.MatchResultRequest
                {
                    Result = final,
                    ManagerIsAway = _managerIsAway,
                    AwayName = _awayDisp,
                    HomeName = _homeDisp,
                    // 大会戦の観戦＝勝者が要る文脈。延長上限の引き分けは再試合で決着するため「引き分け（再試合）」と
                    // 明示（issue #253）。練習試合は引き分けで終わるので false（issue #269）。
                    RematchOnTie = _rematchOnTie,
                    OnClose = () => cb(final),
                };
            // 終局はクリック配信中（委任スキップ）にも起きるため、同期 Show は使わず必ず遅延切替にする。
            router.ShowDeferred("MatchResult");
            return true;
        }

        // ライブ観戦（大会フロー）: 結果を大会へ戻して画面遷移する。呼び出し側の OnComplete が遷移を担う。
        private void OnBackHome()
        {
            if (!_gameOver) return;
            var cb = _onComplete;
            _onComplete = null;
            SetBackHomeVisible(false);
            cb?.Invoke(_finalResult);
        }

        // ── 1球采配ショートカット（設計書15 Phase C-3） ──
        // 采配カードの定義（定義配列駆動＝種類を増やすときはここへ1件足すだけ）。
        //
        // 寿命が2種類ある（2026-07-31 ユーザー判断）。
        //  ①勝負の1手（バント/スクイズ/盗塁/エンドラン/積極的に/待て）＝次に解決される打席へ1回だけ効き、
        //    消費したら自動で消灯する。選択はいったん choice フィールドへ溜め、Advance の直前に押し込む。
        //  ②方針（打撃姿勢4種・守備6種）＝解除するまで続く。押した瞬間にエンジンへ入れっぱなしにし、
        //    もう一度押すと解除（トグル）。既定カード「任せ/通常」は置かない。
        // 群（Group）が違うカードは同時に点く（例: 前進守備＋コントロール重視＋とばしていけ）。
        private void WirePitchTactics()
        {
            _signPad?.SetDefs(new[]
            {
                // ── 攻撃: 打ち方（1手）──  Icon はカードの絵の位置に敷く1文字（将来ここへ実際の絵が入る）。
                new SignPad.SignDef { Id = "batting-swing", Label = "積極的に", Icon = "打", Offense = true, Group = "batting",
                    GroupLabel = "打撃", OnSelect = () => { _pitchBattingChoice = PitchBattingOverride.ForceSwing; PrepareBuntStance(false); },
                    OnClear = () => _pitchBattingChoice = null },
                new SignPad.SignDef { Id = "batting-take", Label = "待て", Icon = "待", Offense = true, Group = "batting",
                    GroupLabel = "打撃", OnSelect = () => { _pitchBattingChoice = PitchBattingOverride.ForceTake; PrepareBuntStance(false); },
                    OnClear = () => _pitchBattingChoice = null },
                // ── 攻撃: 仕掛け（1手・走者が要る）──
                // ⚠ バント・スクイズは**押した瞬間**に構えの差し替えも始める（issue #490）。
                // 構えへの移行クリップは 1.10秒かかるのに対し、采配を押し込む PushPitchTactics から
                // リリースまでは実測 0.3秒しかない（クリップ内時刻を実測: リリース時点で
                // Idle_To_Bunt が t=0.00＝始まったばかり）。窓の滞在時間ぶんを丸ごと使わないと
                // 着弾までに構えへ入り切らない。
                new SignPad.SignDef { Id = "batting-bunt", Label = "バント", Icon = "犠", Offense = true, Group = "batting",
                    GroupLabel = "打撃", EnabledIf = () => RunnerOnAnyBase,
                    OnSelect = () => { _pitchBattingChoice = PitchBattingOverride.Bunt; PrepareBuntStance(true); },
                    OnClear = () => { _pitchBattingChoice = null; PrepareBuntStance(false); } },
                new SignPad.SignDef { Id = "batting-squeeze", Label = "スクイズ", Icon = "決", Offense = true, Group = "batting",
                    GroupLabel = "打撃", EnabledIf = () => RunnerOnThird,
                    OnSelect = () => { _pitchBattingChoice = PitchBattingOverride.Squeeze; PrepareBuntStance(true); },
                    OnClear = () => { _pitchBattingChoice = null; PrepareBuntStance(false); } },
                new SignPad.SignDef { Id = "sign-hitrun", Label = "エンドラン", Icon = "走", Offense = true, Group = "batting",
                    GroupLabel = "打撃", EnabledIf = () => RunnerOnFirst,
                    OnSelect = () => _signChoice = OffensiveSign.HitAndRun,
                    OnClear = () => _signChoice = null },
                new SignPad.SignDef { Id = "run-steal", Label = "盗塁", Icon = "盗", Offense = true, Group = "run",
                    GroupLabel = "走塁", EnabledIf = () => RunnerOnFirst,
                    OnSelect = () => _stealChoice = StartType.Gamble,
                    OnClear = () => _stealChoice = null },
                // ── 攻撃: 構え（方針・解除するまで続く）──
                new SignPad.SignDef { Id = "app-work", Label = "待球策", Icon = "粘", Offense = true, Group = "approach",
                    GroupLabel = "姿勢", OnSelect = () => PushApproach(BattingApproach.WorkCount),
                    OnClear = () => PushApproach(BattingApproach.Normal) },
                new SignPad.SignDef { Id = "app-ground", Label = "転がせ", Icon = "転", Offense = true, Group = "approach",
                    GroupLabel = "姿勢", OnSelect = () => PushApproach(BattingApproach.GroundBall),
                    OnClear = () => PushApproach(BattingApproach.Normal) },
                new SignPad.SignDef { Id = "app-slug", Label = "長打狙い", Icon = "長", Offense = true, Group = "approach",
                    GroupLabel = "姿勢", OnSelect = () => PushApproach(BattingApproach.Slug),
                    OnClear = () => PushApproach(BattingApproach.Normal) },
                new SignPad.SignDef { Id = "app-put", Label = "当てに行け", Icon = "当", Offense = true, Group = "approach",
                    GroupLabel = "姿勢", OnSelect = () => PushApproach(BattingApproach.PutInPlay),
                    OnClear = () => PushApproach(BattingApproach.Normal) },

                // ── 守備（すべて方針・解除するまで続く）──
                new SignPad.SignDef { Id = "def-infield-in", Label = "前進守備", Icon = "前", Offense = false, Group = "infield",
                    GroupLabel = "陣形", OnSelect = () => PushDefInfield(DefenseDepth.In), OnClear = () => PushDefInfield(null) },
                new SignPad.SignDef { Id = "def-outfield-deep", Label = "長打警戒", Icon = "深", Offense = false, Group = "outfield",
                    GroupLabel = "陣形", OnSelect = () => PushDefOutfield(DefenseDepth.Deep), OnClear = () => PushDefOutfield(null) },
                new SignPad.SignDef { Id = "policy-fast", Label = "直球中心", Icon = "直", Offense = false, Group = "policy",
                    GroupLabel = "配球", OnSelect = () => PushDefPolicy(PitchPolicy.FastballHeavy), OnClear = () => PushDefPolicy(null) },
                new SignPad.SignDef { Id = "policy-break", Label = "変化球中心", Icon = "変", Offense = false, Group = "policy",
                    GroupLabel = "配球", OnSelect = () => PushDefPolicy(PitchPolicy.BreakingHeavy), OnClear = () => PushDefPolicy(null) },
                new SignPad.SignDef { Id = "policy-zone", Label = "コントロール重視", Icon = "制", Offense = false, Group = "policy",
                    GroupLabel = "配球", OnSelect = () => PushDefPolicy(PitchPolicy.ControlFirst), OnClear = () => PushDefPolicy(null) },
                new SignPad.SignDef { Id = "gear-push", Label = "とばしていけ", Icon = "飛", Offense = false, Group = "gear",
                    GroupLabel = "ギア", OnSelect = () => PushDefGear(PitcherGear.Push), OnClear = () => PushDefGear(null) },
            });
        }

        // 「選べるか」の局面条件。1球進行では指示が**次の1球**に効くので、いま止まっている采配窓の塁を見る
        //（＝画面に映っている塁そのもの）。打席単位だった頃は「次の打席の塁」を先読みするしかなく、
        // カードの出方が打席の結果を先に漏らしていた（走者が出るのを見る前にバントが押せた）。
        private bool RunnerOnFirst => _atBat is { BaseFirst: true };
        private bool RunnerOnThird => _atBat is { BaseThird: true };
        private bool RunnerOnAnyBase =>
            _atBat is { BaseFirst: true } or { BaseSecond: true } or { BaseThird: true };

        // ② 方針カード: 押した瞬間にエンジンへ入れて、解除するまでそのままにする。
        private void PushApproach(BattingApproach approach)
        {
            _approach = approach;
            _prog?.SetBattingApproach(_managerIsAway, approach);
        }

        // 守備の方針（配球/ギア/陣形）は4項目まとめて1本の窓口へ渡すので、各カードは自分の項目だけ差し替えて押し直す。
        // null を渡す＝その項目の解除（委任采配の判断に戻る）。issue #492 で「打席頭で凍結」から「1球の口」へ移した。
        private void PushDefPolicy(PitchPolicy? v) { _defPolicy = v; PushDefense(); }
        private void PushDefGear(PitcherGear? v) { _defGear = v; PushDefense(); }
        private void PushDefInfield(DefenseDepth? v) { _defInfield = v; PushDefense(); }
        private void PushDefOutfield(DefenseDepth? v) { _defOutfield = v; PushDefense(); }

        // 守備方針を1球の口へ即予約する（issue #492）。押した次の1球から効き、以降は PushPitchTacticsChoice が
        // 采配窓ごとに再予約して「解除するまで続く」寿命を維持する。自校が守備側の窓でだけ載せる
        //（攻撃中に押しても相手の守備には入れない）。SetPitchDefenseOverride は「攻撃側」を引数に取り、守備＝その逆へ載る。
        private void PushDefense()
        {
            if (_prog == null || ManagerIsOnOffense()) return;   // 攻撃中は相手守備＝brain のまま触らない
            var offenseSide = _atBat != null ? _atBat.IsTop : !_managerIsAway;
            _prog.SetPitchDefenseOverride(offenseSide, _defPolicy, _defGear, _defInfield, _defOutfield);
        }

        // いま止まっている窓（または一括経路）で自校が攻撃側か。
        private bool ManagerIsOnOffense()
            => _atBat != null ? _atBat.IsTop == _managerIsAway : _prog.IsTeamOnOffense(_managerIsAway);

        // これから投げる1球へ、①攻撃の「1手」と②守備方針（配球/ギア/陣形）を予約する。
        // 攻撃の1手は無指示=null なら従来と完全一致。守備方針は「解除するまで続く」ので、自校が守備の窓では
        // 現在の選択を毎窓ここで再予約して寿命を保つ（issue #492: 1球の口は消費後に自動でnullへ戻るため）。
        private void PushPitchTacticsChoice()
        {
            // 自校が攻撃していない球へは攻撃の1手を渡さない。渡すと自校の予約として残り、
            // **次に自校が攻撃する回の初球で不意に発動する**。
            var offense = _atBat != null
                ? _atBat.IsTop == _managerIsAway              // 1球進行: いま止まっている窓の攻守
                : _prog.IsTeamOnOffense(_managerIsAway);      // 一括経路（撮影ハーネス）
            _prog.SetPitchBattingOverride(_managerIsAway, offense ? _pitchBattingChoice : null);
            // ⚠ ここで構えを**下ろさない**（issue #490 実機確認 2026-08-06）。1手カードは押し込んだ後に
            // 自動解除される（ResetPitchTacticsChoice）ので、この経路から「札が無い＝構えを解く」を
            // 流すと、札を押した瞬間に始めた移行が投球の直前で巻き戻り、リリース時の再指定で
            // t=0 から始まり直す＝前倒しが丸ごと無駄になる（実測: リリース時点で Idle_To_Bunt が
            // 毎回 t=0.00 のままだった）。構えの真偽はその球の指示を持つ OnPitchReleased が決める。
            _prog.SetOffensiveSign(_managerIsAway, offense ? _signChoice : null);
            _prog.SetStealAttempt(_managerIsAway, offense ? _stealChoice : null);
            // 守備方針の再予約（自校が守備の窓のみ）。攻撃中は相手守備＝brain のまま触らない。
            if (!offense)
            {
                var offenseSide = _atBat != null ? _atBat.IsTop : !_managerIsAway;
                _prog.SetPitchDefenseOverride(offenseSide, _defPolicy, _defGear, _defInfield, _defOutfield);
            }
        }

        // ①の1手は消費後に自動解除（Q12-3: 次球は方針に復帰）。②の方針カードは点けたまま残す。
        private void ResetPitchTacticsChoice()
        {
            _pitchBattingChoice = null;
            _signChoice = null;
            _stealChoice = null;
            _signPad?.ClearSelection("batting", "run");
        }

        // ── 打席一括経路（撮影ハーネス＋1球供給が無い打席）──
        // 1球ずつ止まらずに打席まるごと解決し、合成/実記録の投球列を頭から流す。1球進行と違って
        // 「これから投げる球」の窓が無いので、指示は打席の頭でまとめて押し込む（従来の挙動）。

        private void ResolveAndReplayNext()
        {
            PushPitchTacticsChoice();
            var advanced = _prog.Advance();
            ResetPitchTacticsChoice();
            if (!advanced) { EndGame(_prog.BuildResult()); return; }
            _current = _prog.Current;
            _paConfirmed = true;
            // HUD の対戦は 1球進行と同じ供給口から引く（Advance() でも各投球の窓で更新されている）。
            _atBat = _prog.CurrentAtBat;
            // 一括経路は1打席まるごとロードするので、ログ末尾＝これから描く打席。本日成績はその手前まで。
            _displayedLogCount = Mathf.Max(0, _prog.PlayLog.Count - 1);
            BeginPaFromSequence();
            DrainSubstitutionEvents();
            UpdateControlState();
        }

        /// <summary>確定済みの打席（<see cref="_current"/>）を、その投球列で頭から描き始める。</summary>
        private void BeginPaFromSequence()
        {
            _paQueue.Clear();
            var pitches = _current.PitchSeq?.Pitches;
            if (pitches != null)
                foreach (var pt in pitches)
                    _paQueue.Add(new QueuedPitch(pt, _current.BaseFirstBefore, _current.BaseSecondBefore,
                        _current.BaseThirdBefore, null));

            _t = 0;
            _lineScorePosted = false;
            SetDuelViewAuto(true);   // 打席の開始で対決ビューへ復帰（issue #312。手動OFF中は据え置き）
            // 1球も投げない打席（敬遠）はこの後 OnPitchWindup を通らないので、ここでも仕切り直す。
            _duelRig?.ResetForNewPlateAppearance();
            ApplyDuelHandedness();
            RefreshPanel();
            _signPad?.SetPhase(_current.IsTop == _managerIsAway);
            _signPad?.RefreshEnabled();

            // BSO 初期化: アウト＝打席前、塁ダイヤ＝打席前、カウント＝0-0。結果チップは投球フェーズ中は隠す。
            SetOuts(_current.OutsBefore);
            SetBases(_current.BaseFirstBefore, _current.BaseSecondBefore, _current.BaseThirdBefore);
            SetCount(0, 0);
            if (_result != null) _result.style.display = DisplayStyle.None;
            HidePitchCall();
            HideScoreCall();
            if (_caption != null) _caption.text = _current.BatterName + " 打席";

            if (_paQueue.Count > 0) BeginPitchRender(0);
            else { _stage = LiveStage.Play; EnterBattedBallOrHold(); }
        }

        // ── 1球の描画 ──

        /// <summary>
        /// 投球列の index 球目を描き始める。打席の最初の球だけ、対決ビューでは振りかぶりから見せるため
        /// 時計を実尺ぶん巻き戻して待つ（design-18 §7-2。2球目以降は前の球の OnPitchReleased が予約済み）。
        /// </summary>
        private void BeginPitchRender(int index)
        {
            _pitchIdx = index;
            _pitchCallDone = false;
            _battedHoldActive = false;
            _inPitchPhase = true;
            _stage = LiveStage.Pitch;
            var carry = _windowCarry;   // 前の球・窓で余った端数は持ち越す（球間の間合いが倍速でずれないように）
            _windowCarry = _windowClock = _windowHold = 0f;

            // バント/スクイズの構えは**投球の描画に入る前**に入れる（issue #490 実機確認 2026-08-06）。
            // 構えへの移行は 1.10秒、リリース〜着弾は 0.45秒なので、解決後に確定する
            // PitchToken.BattingSign を待つと squaring の途中で球が来る（実測: リリース時点で
            // 移行クリップが毎回 t=0.00）。engine の予約読み口（PendingPitchBattingOverride）は
            // 投球前に読める＝振りかぶり 1.30秒を助走に使える。
            // 読める中でいちばん早い指示は**この球のトークン**（解決済みなので既に入っている。
            // 実測 2026-08-06: BeginPitchRender の時点で token=Bunt）。予約（engine の読み口）は
            // まだ消費前の球にしか無いので論理和で拾う。
            // 猶予は打席の初球だけ振りかぶりぶん（1.30秒）あり、2球目以降はほぼ0＝rig 側が
            // 移行クリップを通すか直接構えるかを猶予で選ぶ。
            var buntNow = (index >= 0 && index < _paQueue.Count && IsBuntSign(_paQueue[index].Token))
                          || PendingBuntSign();
            PrepareBuntStance(buntNow,
                index == 0 && DuelPacingActive ? DuelStageRig.WindupSeconds : 0f);

            if (index == 0 && DuelPacingActive)
            {
                // セットポジション投球なのでクリップ先頭からではなく DeliveryStartTime から流れる＝
                // 巻き戻すのは ReleaseTime ではなく WindupSeconds。
                _pitchClock = -DuelStageRig.WindupSeconds + carry;
                _pitchFirstPending = true;
                _duelRig.OnPitchWindup();
                return;
            }
            _pitchClock = carry;
            _pitchFirstPending = false;
            BeginPitchBall(index);
        }

        // ── イニング間のスコア画面（試合画面改修）──
        // 攻守が入れ替わった瞬間に挟む。次の采配窓は EndInningBreak まで保留する。
        // ここは「1球ぶんの描画が終わった後・次の窓を開く前」なので、再生も進行も走っていない
        // ＝二重進行は構造的に起きない。
        private void BeginInningBreak(int endedInning, bool endedIsTop, int nextInning, bool nextIsTop,
            System.Action afterBreak)
        {
            if (_inningBreak == null) { afterBreak?.Invoke(); return; }

            HidePitchCall();
            HideScoreCall();   // 得点直後の攻守交代でテロップが暗幕に重ならないよう先に畳む

            // スコアバグは新しい半回の頭へ戻す（0-0・無死・走者なし）。
            SetCount(0, 0);
            SetOuts(0);
            SetBases(false, false, false);

            if (_ibCaption != null)
                _ibCaption.text = endedInning + "回" + (endedIsTop ? "表" : "裏") + " 終了";
            // 大写しにするラインスコアは「次の回」を指した状態で掲げる。
            PostLineScore(nextInning, nextIsTop, finished: false);

            _stage = LiveStage.Idle;
            _inPitchPhase = false;
            _afterInningBreak = afterBreak;
            _inningBreakOn = true;
            _inningBreakClock = 0f;
            SetVisible(_inningBreak, true);
        }

        // 自動で明ける（倍速中は表示時間も一緒に縮む）／クリックで即明ける、の共通出口。
        private void EndInningBreak()
        {
            if (!_inningBreakOn) return;
            _inningBreakOn = false;
            _inningBreakClock = 0f;
            SetVisible(_inningBreak, false);
            var next = _afterInningBreak;
            _afterInningBreak = null;
            next?.Invoke();   // 保留していた次の采配窓をここで開く
        }

        // 暗幕クリック＝スキップ。StopPropagation で中断ポップアップと二重反応させない
        //（保険として IsPauseExempt にも inning-break を入れてある）。
        private void OnInningBreakDown(PointerDownEvent evt)
        {
            if (!_inningBreakOn) return;
            EndInningBreak();
            evt.StopPropagation();
        }

        // ── 交代ポップアップ（issue #209 表現）──
        // 直前の進行で増えた継投ログ（_prog.PitchingChanges）を取り込み、実況履歴に1行ずつ残しつつ
        // 最新の1件を数秒のポップアップで盤面へ浮かせる。観測データの表示のみ＝試合結果に影響しない。
        private void DrainSubstitutionEvents()
        {
            var log = _prog?.PitchingChanges;
            if (log == null) return;
            KokoSim.Engine.Match.Game.PitchingChangeEvent latest = null;
            for (var i = _subLogCount; i < log.Count; i++)
            {
                var e = log[i];
                PushHistory("⇄ " + SubEventText(e));
                latest = e;
            }
            _subLogCount = log.Count;
            if (latest != null) BeginSubCall(latest);
        }

        private void BeginSubCall(KokoSim.Engine.Match.Game.PitchingChangeEvent e)
        {
            if (_subCall == null || _subCallTag == null || _subCallText == null) return;
            // 交代は「今起きたこと」を示す確定オーバーレイ。直前の球判定・得点演出が残っていると
            // 上下に重なって読みづらいので、他の一過性オーバーレイを先に畳んでから前面に出す。
            HidePitchCall();
            HideScoreCall();
            var kind = e.Change.Kind;
            // 実況の区別は「控えから出たか／守備に就いていた者が回ったか（＝守備に穴が開くか）」（issue #381）。
            // 出てきた控えが投手枠かどうかでは分けない＝控えからの登板はすべて「投手交代」。
            var isFielder = kind != KokoSim.Engine.Match.Game.PitchingChangeKind.ReserveRelief;
            _subCallTag.text = kind == KokoSim.Engine.Match.Game.PitchingChangeKind.ReserveRelief ? "投手交代"
                : kind == KokoSim.Engine.Match.Game.PitchingChangeKind.TwoWaySwap ? "投野交代" : "野手登板";
            _subCallTag.EnableInClassList("sub-call__tag--fielder", isFielder);   // 野手系だけアンバー点灯（一過性強調）
            _subCallText.text = SubEventText(e);
            _subCall.style.display = DisplayStyle.Flex;
            _subCallOn = true;
            _subCallClock = 0f;
        }

        private void HideSubCall()
        {
            _subCallOn = false;
            _subCallClock = 0f;
            if (_subCall != null) _subCall.style.display = DisplayStyle.None;
        }

        // 交代1件のテキスト（種別で出し分け）。backfill 先は「◯◯へ」で守備位置を添える。
        private static string SubEventText(KokoSim.Engine.Match.Game.PitchingChangeEvent e)
        {
            var half = e.Inning + "回" + (e.IsTopHalf ? "表" : "裏");
            var c = e.Change;
            switch (c.Kind)
            {
                case KokoSim.Engine.Match.Game.PitchingChangeKind.TwoWaySwap:
                    return half + " " + c.InPitcherName + " が登板／" + c.OutPitcherName + " は" + PosJp(c.BackfillPosition) + "へ";
                case KokoSim.Engine.Match.Game.PitchingChangeKind.FielderReliefWithBackfill:
                    return half + " " + c.InPitcherName + " が登板／" + PosJp(c.BackfillPosition) + "は" + c.BackfillName;
                default:
                    // 控えからの登板（ReserveRelief）。守備に穴が開かないので交代の形だけを出す（issue #381）。
                    return half + " " + c.OutPitcherName + " → " + c.InPitcherName;
            }
        }

        private static string PosJp(KokoSim.Engine.Match.Field.FieldPosition? pos) => pos switch
        {
            KokoSim.Engine.Match.Field.FieldPosition.Pitcher => "投",
            KokoSim.Engine.Match.Field.FieldPosition.Catcher => "捕",
            KokoSim.Engine.Match.Field.FieldPosition.FirstBase => "一塁",
            KokoSim.Engine.Match.Field.FieldPosition.SecondBase => "二塁",
            KokoSim.Engine.Match.Field.FieldPosition.ThirdBase => "三塁",
            KokoSim.Engine.Match.Field.FieldPosition.Shortstop => "遊撃",
            KokoSim.Engine.Match.Field.FieldPosition.LeftField => "左翼",
            KokoSim.Engine.Match.Field.FieldPosition.CenterField => "中堅",
            KokoSim.Engine.Match.Field.FieldPosition.RightField => "右翼",
            _ => "守備",
        };

        // 対決ビュー→俯瞰の切替アンカー（issue #330）: 打球/ファウル飛球のタイムラインは必ず先頭に
        // 投球セグメントを持つ（TimelineBuilder）。対決ビューは既にリリース〜ミート/ファウル接触を
        // 演じ終えているので、俯瞰はその投球セグメントの終端（＝接触の瞬間）から再生する。
        // タイムライン自身の T1 から取り、エンジンの ContactAt 定数は複製しない（単一ソース）。
        // キャプションは実況キャプション列から接触直後の1件を拾う＝「ピッチャー、投げた」から
        // 始まらず、打った/ファウルの実況から出る。
        /// <param name="shownSeconds">
        /// 対決ビューが接触後に打球を見せていた秒数（見せていなければ負）。俯瞰はその**続き**から
        /// 始める＝同じ0.5秒を二度描かない。キャプションは <c>CaptionAt</c> が「t以前の最後の1件」を
        /// 拾うので、飛ばした先でも「打った」の実況から出る（2026-07-31）。
        /// </param>
        /// <summary>
        /// 対決ビューの打者をバントの構えへ入れる/戻す（issue #490）。**札を押した瞬間**に呼ぶのが本命で、
        /// 構えへの移行クリップ 1.10秒ぶんの助走を窓の滞在時間から取る。表示専用＝判定・帯には無関係。
        /// </summary>
        /// <summary>
        /// 次の1球に予約済みのバント・スクイズ指示があるか（issue #490）。engine の
        /// <see cref="MatchProgression.PendingPitchBattingOverride"/> を読むだけ＝乱数も状態も動かさない。
        /// 敵AIのサインは解決の途中で決まるのでここには出ない（先読みは乱数順を動かす）。
        /// </summary>
        private bool PendingBuntSign()
        {
            if (_prog == null) return false;
            var offenseIsAway = _atBat?.IsTop ?? _prog.IsTeamOnOffense(true);
            return _prog.PendingPitchBattingOverride(offenseIsAway)
                is PitchBattingOverride.Bunt or PitchBattingOverride.SafetyBunt
                or PitchBattingOverride.Squeeze;
        }

        private void PrepareBuntStance(bool bunting, float leadSeconds = 0f)
        {
            if (!DuelPacingActive) return;
            _duelRig?.PrepareBatterBunt(bunting, leadSeconds);
        }

        /// <summary>その球にバント・セーフティバント・スクイズの指示が出ているか（issue #490）。表示専用。</summary>
        private static bool IsBuntSign(PitchToken pt)
            => pt.BattingSign is KokoSim.Engine.Match.Tactics.PitchBattingOverride.Bunt
                or KokoSim.Engine.Match.Tactics.PitchBattingOverride.SafetyBunt
                or KokoSim.Engine.Match.Tactics.PitchBattingOverride.Squeeze;

        /// <summary>
        /// 打席途中プレー（issue #485〜#488）を守備ビューで見せ始める時刻。投球区間を持たないプレーは 0
        /// （＝頭から全部見せる）。
        ///
        /// 投球そのものは対決ビューで見せ終わっているので、守備ビューは**その続き**から始める
        /// （打球が <see cref="ContactMomentOf"/> で接触から始めるのと同型）。ただし返すのは投球の
        /// 終わりではなく**次に球が動き出す時刻**（盗塁なら捕手の送球開始）。
        ///
        /// 投球の終わりで切ると、そこは「捕手が握り替えている 0.6秒」＝どの球区間にも属さない隙間で、
        /// カメラプランは見るものが無いので全景を返す。実測（2026-08-06）: 開始を投球の終わり 1.40s に
        /// しても頭は枠64.7m＝人18px のまま変わらなかった。次の区間の頭で切れば、その瞬間から
        /// 送球の枠に入る＝寄った絵で始まる。
        /// </summary>
        private static double FieldCutMomentOf(PlaybackPlay play)
        {
            if (play == null || play.Ball.Count == 0) return 0;
            var head = play.Ball[0];
            var isPitch = head is PitchSegment || (head is EndpointBallSegment eb && eb.Kind == BallSegmentKind.Pitch);
            if (!isPitch) return 0;
            // 投球の次に球が動き出す時刻（無ければ投球の終わり＝球の出番がもう無いプレー）。
            var next = double.PositiveInfinity;
            for (var i = 1; i < play.Ball.Count; i++)
                if (play.Ball[i].T0 < next) next = play.Ball[i].T0;
            return double.IsFinite(next) ? System.Math.Max(next, head.T1) : head.T1;
        }

        private static double ContactMomentOf(PlaybackPlay play, double shownSeconds = -1)
        {
            if (play == null || play.Ball.Count == 0) return 0;
            var head = play.Ball[0];
            var isPitch = head is PitchSegment || (head is EndpointBallSegment eb && eb.Kind == BallSegmentKind.Pitch);
            if (!isPitch) return 0;

            var t = head.T1;
            double? nextCaption = null;
            foreach (var cap in play.Caps)
                if (cap.T > t && (!nextCaption.HasValue || cap.T < nextCaption.Value)) nextCaption = cap.T;
            var start = nextCaption ?? t;
            return shownSeconds > 0 ? System.Math.Max(start, t + shownSeconds) : start;
        }

        // 投球フェーズ: index 球目の投球軌道（マウンド→本塁）を盤面へ流す（#5）。
        // 捕手からの返球は描画しない。打球になる最後の1球・ファウル打球データを持つ1球は、続く
        // タイムラインが同じ投球セグメントを先頭に持つので二重に描かず、静止表示のまま渡す
        // （ファウルは判定を出す時刻に Update() 側で差し替える, issue #313）。
        private void BeginPitchBall(int index)
        {
            var q = _paQueue[index];
            var pt = q.Token;
            var isLast = IsTerminalPitch(index);
            var play = _paConfirmed ? _current.Play : null;
            _pendingFoulFlight = pt.Kind == KokoSim.Engine.Match.AtBat.PitchKind.Foul ? pt.FoulFlight : null;

            _pitchBallVisible = _pendingFoulFlight == null && !(isLast && play != null);
            if (_pitchBallVisible)
                _view.SetPlay(PitchPlaybackFactory.PitchOnly(q.First, q.Second, q.Third));
            else
                _view.SetResting(q.First, q.Second, q.Third);

            // 対決ビューのアンカー同期（design-18 §7-2）: この瞬間＝リリース。次の1球があればリリース間隔を渡して
            // フォロースルー→次の振りかぶりの切り替えを予約し、1球の結果で打者・捕手の一発物を分岐する（第2弾）
            if (_duelOn && _duelRig != null)
            {
                var outcome = pt.Kind switch
                {
                    KokoSim.Engine.Match.AtBat.PitchKind.CalledStrike => DuelPitchOutcome.CalledStrike,
                    KokoSim.Engine.Match.AtBat.PitchKind.SwingingStrike => DuelPitchOutcome.SwingMiss,
                    KokoSim.Engine.Match.AtBat.PitchKind.Foul => DuelPitchOutcome.FoulContact,
                    KokoSim.Engine.Match.AtBat.PitchKind.InPlay => DuelPitchOutcome.InPlayContact,
                    KokoSim.Engine.Match.AtBat.PitchKind.HitByPitch => DuelPitchOutcome.HitByPitch,
                    _ => DuelPitchOutcome.Take,
                };
                // バント/セーフティバント/スクイズの指示が出ている球（issue #490）: 打者の構え・
                // 接触/空振りをバント用クリップへ差し替える（表示専用＝判定には無関係）。
                // 構えの予約は BeginPitchRender で済ませてある（間に合わせるため）。ここは一発物
                // （当てる／空振り）の差し替えと、予約が漏れた経路への保険を兼ねる。
                var isBuntAttempt = IsBuntSign(pt);
                BuntAttemptForCapture = isBuntAttempt;
                // 構えは予約と実指示の論理和。実指示だけで下ろすと、指示の無い球のリリースが
                // 予約済みの構えを巻き戻す（issue #490）。敵AIのバントは予約に出ないので、
                // ここで初めて真になる＝従来どおりリリース時からの切り替えになる。
                PrepareBuntStance(isBuntAttempt || PendingBuntSign());
                _duelRig.OnPitchReleased(isLast ? -1f : duelPitchIntervalSeconds, outcome,
                    FlightOf(pt), BattedOf(pt, isLast), isBuntAttempt);
            }

            // この1球の判定を出す時刻。俯瞰は盤面の投球尺（0.45秒固定）に合わせた pitchIntervalSeconds だが、
            // 対決ビューの球は実速度で飛ぶので**球ごとに違う**＝球審のコールに合わせる。定数のままだと
            // 捕る前に「ストライク」が出る（2026-08-01 実測・DuelStageRig.CallMomentSeconds の項）。
            _pitchCallAt = DuelPacingActive ? _duelRig.CallMomentSeconds : pitchIntervalSeconds;
        }

        /// <summary>
        /// この球で打席が終わるか。<b>打席が確定していないうちは常に false</b>——1球進行では投げた球が
        /// 打席を終わらせたかどうかは、その球と一緒にエンジンが打席を返してくるかで分かる（先読みしない）。
        /// </summary>
        private bool IsTerminalPitch(int index) => _paConfirmed && index == _paQueue.Count - 1;

        /// <summary>
        /// 1球のコース・弾道を対決ビューへ渡す形に写す（issue #332）。実データが揃っていない球は null
        /// ＝合成列に架空のコースを主張させない（<c>PitchSequenceSynthesizer</c> の契約）。
        /// 弾道は <c>CaptureTimelines</c> の試合だけ載るので、無い場合はコースだけ使い飛翔時間は既定へ落とす。
        /// 狙い位置（issue #396）は実着弾位置と同じ球にしか載らない（同じ <c>PitchRecord</c> 由来で必ず対）
        /// ため、欠けていた場合だけ実着弾位置へ落とす（=旧挙動と同じ「狙い＝実着弾」の縮退）。
        /// </summary>
        private static DuelPitchFlight? FlightOf(KokoSim.Engine.Match.Timeline.Playback.PitchToken pt)
        {
            if (pt.LocationX is not { } x || pt.LocationY is not { } y) return null;
            var tr = pt.Trajectory;
            return new DuelPitchFlight(
                (float)x, (float)y,
                (float)(pt.AimX ?? x), (float)(pt.AimY ?? y),
                (float)(tr?.FlightTimeSeconds ?? 0.0),         // 0 は rig 側で既定（BallFlightSeconds）へ落ちる
                (float)(tr?.InducedVerticalBreakM ?? 0.0),
                (float)(tr?.InducedHorizontalBreakM ?? 0.0));
        }

        /// <summary>
        /// この1球の打球を対決ビューへ渡す（2026-07-31）。渡すのは**俯瞰がこの後すぐ描くのと同じ
        /// セグメント**＝行き先が2つの絵で食い違わない（打球の物理は engine が単一ソース）。
        /// インプレー（打席を終わらせる最後の1球）とファウル打球の両方が対象。
        /// </summary>
        private KokoSim.Engine.Match.Timeline.Playback.PlaybackBallSegment BattedOf(
            KokoSim.Engine.Match.Timeline.Playback.PitchToken pt, bool isLast)
        {
            var play = pt.Kind switch
            {
                KokoSim.Engine.Match.AtBat.PitchKind.Foul => pt.FoulFlight,
                KokoSim.Engine.Match.AtBat.PitchKind.InPlay => isLast ? _current.Play : null,
                _ => null,
            };
            if (play == null) return null;
            // 打球タイムラインの先頭は必ず投球セグメント（TimelineBuilder）。その次が打球
            foreach (var s in play.Ball)
            {
                if (s is PitchSegment) continue;
                if (s is EndpointBallSegment eb && eb.Kind == BallSegmentKind.Pitch) continue;
                return s;
            }
            return null;
        }

        // 投球フェーズ終了→打球再生（Play あり）または結果保持（三振・四球）。
        private void EnterBattedBallOrHold()
        {
            _inPitchPhase = false;
            _pitchBallVisible = false;
            _stage = LiveStage.Play;
            _windowCarry = 0f;   // 打球再生を挟むので球間の端数は持ち越さない（次は打席の頭の間から）
            _t = 0;
            if (_current.Play != null)
            {
                var wasDuelOn = _duelOn;
                // 対決ビューが打球を見せていた秒数は BeginFieldPlay→打球表示の畳みで消えるので先に採る
                var shown = wasDuelOn ? (_duelRig?.BattedElapsedSeconds ?? -1f) : -1f;
                _view.SetPlay(_current.Play);
                PrepareDisplayTimings(_current);   // 表示同期の時刻を前計算（issue #438）
                // 対決ビューから来た場合は投球が二重に見えないよう接触の瞬間から再生する（issue #330）。
                // さらに対決ビューで打球を見せたぶんは飛ばす（同じ0.5秒を二度描かない。2026-07-31）。
                // 俯瞰固定（手動OFF）中は対決ビュー側で1球も見せていないので、従来どおり投球から流す。
                if (wasDuelOn) _t = ContactMomentOf(_current.Play, shown);
                // 打球は3Dの守備ビューで見せる（打球処理の3D化 F9）。俯瞰は同じ時計で並走させたままにして、
                // トグルを叩いた瞬間に同じ場面へ切り替わるようにする（＝2Dはデバッグ用の別窓であって、別経路ではない）。
                BeginFieldPlay((float)_t);
                RenderReplayFrame();
            }
            else
            {
                // 守備陣＋走者を静止表示（四球なら押し出し反映＝結果後の塁状況）。盤面を空にしない。
                _view.SetResting(_current.BaseFirstAfter, _current.BaseSecondAfter, _current.BaseThirdAfter);
                _t = -noPlayHoldSeconds; // 経過で采配窓へ
                if (_caption != null) _caption.text = _current.BatterName + "　" + ResultJp(_current.Result);
                if (_result != null) { _result.text = ResultJp(_current.Result); _result.style.display = DisplayStyle.Flex; }
            }
        }

        // 打席の再生が終わった: 塁ダイヤを結果へ更新してから、中断中でなければ次の采配窓を開けに行く。
        private void FinishPaView()
        {
            EndFieldPlay();   // 守備陣・走者を伏せて対決の構図へ戻す（＝次打席への唯一のカット）
            _view.HideJudgement();   // 器は盤面の外＝ビュー切替では消えない（打ち切りに据え置きを添えるのと同型）
            HidePitchCall();
            HideScoreCall();
            SetBases(_current.BaseFirstAfter, _current.BaseSecondAfter, _current.BaseThirdAfter);
            PushHistory(HistoryLine(_current));   // プレー確定ごとに実況履歴へ1件積む
            // ここまでが「絵にした打席」＝本日成績の略記列はここまでしか見ない（再生前の結果を出さない）。
            _displayedLogCount = _prog?.PlayLog.Count ?? _displayedLogCount;
            _stage = LiveStage.Idle;
            if (_gameOver) return;
            if (_caption != null) _caption.text = NextPrompt();
            AdvanceToNextWindow();   // タイム中断中・スクショ時は内部で待つ（起こさない）
        }

        // ── 打席途中の盗塁の3D再生（issue #485） ──
        // 打球の EnterBattedBallOrHold と同型（守備ビューを開いて再生し、終わったら対決構図へ戻す）だが、
        // 盗塁は打席を終わらせないので、終端では FinishPaView（次打席へ）ではなく打席の続きへ戻す。
        // ここが牽制（#486）・暴投/捕逸（#487）・振り逃げ（#488）が乗る「打席途中プレーの再生経路」の土台。

        /// <summary>盗塁プレーを守備ビューで再生し始める。走者・捕手送球・判定は engine の <see cref="PlaybackPlay"/>
        /// （<c>BuildSteal</c> の出力）が単一ソース＝打球と同じ経路で描く。</summary>
        private void EnterStealPlay(PlaybackPlay steal)
        {
            _inPitchPhase = false;
            _pitchBallVisible = false;
            _stage = LiveStage.Play;
            _windowCarry = 0f;
            _stealPlay = steal;
            // 投球は対決ビューで見せ終わっている＝その続きから再生する（頭が全景にならない）。
            _t = FieldCutMomentOf(steal);
            _stealEndAt = StealPlayEndTime(steal);
            _view.SetPlay(steal);
            // 3Dの守備ビュー（打球と同じ経路。BeginFieldPlay は rig 未導入なら内部で何もしない）。
            if (_duelRig != null && _duelRig.Available)
            {
                // 守備側の頭上ラベル用の同定。盗塁は打席途中なので _current は同じ半回の直前の打席
                // （走者が居る＝必ず先行打席がある）＝その IsTop で守備チームを引く。
                if (_prog != null && _current != null)
                    _duelRig.SetFieldIdentity(_prog.CurrentDefenseIdentity(_current.IsTop));
                _duelRig.BeginFieldPlay(steal, (float)_t);
            }
            RenderStealFrame();
        }

        /// <summary>盗塁再生の1フレーム（打球の <see cref="RenderReplayFrame"/> の最小版。結果テキスト・
        /// ラインスコア・得点テロップは打席の産物なので触らない＝盤面の実況キャプションだけ進める）。</summary>
        private void RenderStealFrame()
        {
            if (_stealPlay == null) return;
            _view.SetTime(_t);
            if (FieldPlayActive) _duelRig.SetFieldTime((float)_t);
            if (_caption != null) _caption.text = PlaybackEvaluator.CaptionAt(_stealPlay, _t);
        }

        /// <summary>盗塁再生の打ち切り時刻。<see cref="PlayEndTime"/> と同じ規則（動く走者レッグ・球・判定の
        /// 最後＋余韻）だが、打席の表示同期（結果テキスト・得点）は無いので盗塁プレー単体から出す。</summary>
        private double StealPlayEndTime(PlaybackPlay play)
        {
            var lastEvent = play.ResAt;
            foreach (var seg in play.Ball) if (seg.T1 > lastEvent) lastEvent = seg.T1;
            foreach (var r in play.Runners)
                foreach (var s in r.Segs)
                {
                    if (s.From.X == s.To.X && s.From.Y == s.To.Y) continue;   // その場に居るだけの走者は数えない
                    if (s.T1 > lastEvent) lastEvent = s.T1;
                }
            var end = System.Math.Min(play.Dur, lastEvent + PlayTailSeconds);
            var judged = PlaybackEvaluator.LastJudgementTime(play);
            if (judged.HasValue) end = System.Math.Max(end, judged.Value + _view.JudgementHoldSeconds);
            return end;
        }

        /// <summary>盗塁再生が終わった: 対決構図へ戻し、盗塁が動かした塁・アウトを反映してから打席の続きへ。
        /// 同じ球で暴投・捕逸も解決していれば（issue #487。走者ありの実球は盗塁企図の後にも投げられる）、
        /// 盤面を動かす前にその再生へ連結する（先に塁を動かすと二重に見える）。暴投・捕逸が無く振り逃げ
        /// だけ解決していれば（issue #488。盗塁成功で一塁が空いた直後に三振＋振り逃げが続く稀な複合）、
        /// そちらへ連結する。</summary>
        private void FinishStealView()
        {
            EndFieldPlay();          // 守備陣・走者を伏せて対決の構図へ戻す（＝打席への唯一のカット）
            _view.HideJudgement();
            _stealPlay = null;
            var q = _paQueue[_pitchIdx];
            if (q.Live?.BatteryMissPlay is { } batteryMissPlay)
            {
                EnterBatteryMissPlay(batteryMissPlay);
                return;
            }
            if (q.Live?.DropThirdStrikePlay is { } dropThirdStrikePlay)
            {
                EnterDropThirdStrikePlay(dropThirdStrikePlay);
                return;
            }
            // 盗塁が動かした塁・アウトはここで反映＝走者が走り着いてから盤面のダイヤ・アウトを動かす
            // （先に動かすと走る前に着いて見える）。1球観測が無い経路（合成投球列）には盗塁は載らない。
            if (q.Live != null)
            {
                SetOuts(q.Live.Outs);
                SetBases(q.Live.BaseFirst, q.Live.BaseSecond, q.Live.BaseThird);
            }
            // 打席の続きへ（打球の FinishPaView と違い次打席へは進めない）。この球が打席を終わらせていれば
            // 続けて打球再生／結果保持へ入る（盗塁のあと同じ球で三振・四球・インプレーが起きる稀な複合）。
            _foulFlightHoldSeconds = null;
            _windowCarry = 0f;
            _pitchCallDone = false;
            _inPitchPhase = false;
            if (IsTerminalPitch(_pitchIdx)) { _stage = LiveStage.Play; EnterBattedBallOrHold(); return; }
            if (_pitchIdx + 1 < _paQueue.Count) { BeginPitchRender(_pitchIdx + 1); return; }
            if (_atBatNext != null) { OpenWindowOrBreak(_atBatNext); return; }
            _stage = LiveStage.Idle;
            AdvanceToNextWindow();
        }

        // ── 打席途中の牽制の3D再生（issue #486） ──
        // EnterStealPlay 系（issue #485）と同型。判定結果（アウト/セーフ）に関わらず、牽制球が
        // 投じられた球は必ずこの経路で再生する（分岐させない・独自の間引きをしない、2026-08-05 issue決定）。

        /// <summary>牽制プレーを守備ビューで再生し始める。投手の送球・走者の帰塁・判定は engine の
        /// <see cref="PlaybackPlay"/>（<c>BuildPickoff</c> の出力）が単一ソース＝盗塁・打球と同じ経路で描く。</summary>
        private void EnterPickoffPlay(PlaybackPlay pickoff)
        {
            _inPitchPhase = false;
            _pitchBallVisible = false;
            _stage = LiveStage.Play;
            _windowCarry = 0f;
            _pickoffPlay = pickoff;
            // 投球は対決ビューで見せ終わっている＝その続きから再生する（頭が全景にならない）。
            _t = FieldCutMomentOf(pickoff);
            _pickoffEndAt = PickoffPlayEndTime(pickoff);
            _view.SetPlay(pickoff);
            if (_duelRig != null && _duelRig.Available)
            {
                if (_prog != null && _current != null)
                    _duelRig.SetFieldIdentity(_prog.CurrentDefenseIdentity(_current.IsTop));
                _duelRig.BeginFieldPlay(pickoff, (float)_t);
            }
            RenderPickoffFrame();
        }

        /// <summary>牽制再生の1フレーム（<see cref="RenderStealFrame"/> と同型）。</summary>
        private void RenderPickoffFrame()
        {
            if (_pickoffPlay == null) return;
            _view.SetTime(_t);
            if (FieldPlayActive) _duelRig.SetFieldTime((float)_t);
            if (_caption != null) _caption.text = PlaybackEvaluator.CaptionAt(_pickoffPlay, _t);
        }

        /// <summary>牽制再生の打ち切り時刻（<see cref="StealPlayEndTime"/> と同じ規則）。</summary>
        private double PickoffPlayEndTime(PlaybackPlay play)
        {
            var lastEvent = play.ResAt;
            foreach (var seg in play.Ball) if (seg.T1 > lastEvent) lastEvent = seg.T1;
            foreach (var r in play.Runners)
                foreach (var s in r.Segs)
                {
                    if (s.From.X == s.To.X && s.From.Y == s.To.Y) continue;
                    if (s.T1 > lastEvent) lastEvent = s.T1;
                }
            var end = System.Math.Min(play.Dur, lastEvent + PlayTailSeconds);
            var judged = PlaybackEvaluator.LastJudgementTime(play);
            if (judged.HasValue) end = System.Math.Max(end, judged.Value + _view.JudgementHoldSeconds);
            return end;
        }

        /// <summary>牽制再生が終わった: 対決構図へ戻す。同じ球で盗塁企図の勝負に続いていれば
        /// （牽制セーフの直後）盤面を動かす前に盗塁の再生へ連結する（先に塁を動かすと二重に見える）。
        /// 盗塁が続かず暴投・捕逸だけ解決していれば（issue #487）そちらへ、それも無く振り逃げだけ
        /// 解決していれば（issue #488）そちらへ連結する。どれも続きが無ければ塁・アウトを反映して
        /// 打席の続きへ戻る。</summary>
        private void FinishPickoffView()
        {
            EndFieldPlay();
            _view.HideJudgement();
            _pickoffPlay = null;
            var q = _paQueue[_pitchIdx];
            if (q.Live?.StealPlay is { } stealPlay)
            {
                EnterStealPlay(stealPlay);
                return;
            }
            if (q.Live?.BatteryMissPlay is { } batteryMissPlay)
            {
                EnterBatteryMissPlay(batteryMissPlay);
                return;
            }
            if (q.Live?.DropThirdStrikePlay is { } dropThirdStrikePlay)
            {
                EnterDropThirdStrikePlay(dropThirdStrikePlay);
                return;
            }
            if (q.Live != null)
            {
                SetOuts(q.Live.Outs);
                SetBases(q.Live.BaseFirst, q.Live.BaseSecond, q.Live.BaseThird);
            }
            _foulFlightHoldSeconds = null;
            _windowCarry = 0f;
            _pitchCallDone = false;
            _inPitchPhase = false;
            if (IsTerminalPitch(_pitchIdx)) { _stage = LiveStage.Play; EnterBattedBallOrHold(); return; }
            if (_pitchIdx + 1 < _paQueue.Count) { BeginPitchRender(_pitchIdx + 1); return; }
            if (_atBatNext != null) { OpenWindowOrBreak(_atBatNext); return; }
            _stage = LiveStage.Idle;
            AdvanceToNextWindow();
        }

        // ── 打席途中の暴投・捕逸の3D再生（issue #487） ──
        // EnterPickoffPlay 系（issue #486）と同型。盗塁・牽制と違い「この球の前」ではなく
        // 「この球そのものの結果」として起きるため、盗塁・牽制の再生（あれば）が終わった後に連結する
        // （FinishPickoffView／FinishStealView 参照）。同じ球で振り逃げも解決していれば
        // （issue #488。三振確定後の後処理として判定される）、続けてそちらへ連結する（FinishBatteryMissView）。

        /// <summary>暴投・捕逸のプレーを守備ビューで再生し始める。ボールの後逸・捕手の返球・走者の進塁は
        /// engine の <see cref="PlaybackPlay"/>（<c>BuildBatteryMiss</c> の出力）が単一ソース＝
        /// 盗塁・牽制・打球と同じ経路で描く。</summary>
        private void EnterBatteryMissPlay(PlaybackPlay batteryMiss)
        {
            _inPitchPhase = false;
            _pitchBallVisible = false;
            _stage = LiveStage.Play;
            _windowCarry = 0f;
            _batteryMissPlay = batteryMiss;
            // 投球は対決ビューで見せ終わっている＝その続きから再生する（頭が全景にならない）。
            _t = FieldCutMomentOf(batteryMiss);
            _batteryMissEndAt = BatteryMissPlayEndTime(batteryMiss);
            _view.SetPlay(batteryMiss);
            if (_duelRig != null && _duelRig.Available)
            {
                if (_prog != null && _current != null)
                    _duelRig.SetFieldIdentity(_prog.CurrentDefenseIdentity(_current.IsTop));
                _duelRig.BeginFieldPlay(batteryMiss, (float)_t);
            }
            RenderBatteryMissFrame();
        }

        /// <summary>暴投・捕逸再生の1フレーム（<see cref="RenderPickoffFrame"/> と同型）。</summary>
        private void RenderBatteryMissFrame()
        {
            if (_batteryMissPlay == null) return;
            _view.SetTime(_t);
            if (FieldPlayActive) _duelRig.SetFieldTime((float)_t);
            if (_caption != null) _caption.text = PlaybackEvaluator.CaptionAt(_batteryMissPlay, _t);
        }

        /// <summary>暴投・捕逸再生の打ち切り時刻（<see cref="PickoffPlayEndTime"/> と同じ規則）。</summary>
        private double BatteryMissPlayEndTime(PlaybackPlay play)
        {
            var lastEvent = play.ResAt;
            foreach (var seg in play.Ball) if (seg.T1 > lastEvent) lastEvent = seg.T1;
            foreach (var r in play.Runners)
                foreach (var s in r.Segs)
                {
                    if (s.From.X == s.To.X && s.From.Y == s.To.Y) continue;
                    if (s.T1 > lastEvent) lastEvent = s.T1;
                }
            var end = System.Math.Min(play.Dur, lastEvent + PlayTailSeconds);
            var judged = PlaybackEvaluator.LastJudgementTime(play);
            if (judged.HasValue) end = System.Math.Max(end, judged.Value + _view.JudgementHoldSeconds);
            return end;
        }

        /// <summary>暴投・捕逸再生が終わった: 対決構図へ戻す。同じ球で振り逃げも解決していれば
        /// （issue #488）盤面を動かす前にその再生へ連結する（先に塁を動かすと二重に見える）。
        /// 続きが無ければ塁・アウトを反映して打席の続きへ戻る。</summary>
        private void FinishBatteryMissView()
        {
            EndFieldPlay();
            _view.HideJudgement();
            _batteryMissPlay = null;
            var q = _paQueue[_pitchIdx];
            if (q.Live?.DropThirdStrikePlay is { } dropThirdStrikePlay)
            {
                EnterDropThirdStrikePlay(dropThirdStrikePlay);
                return;
            }
            if (q.Live != null)
            {
                SetOuts(q.Live.Outs);
                SetBases(q.Live.BaseFirst, q.Live.BaseSecond, q.Live.BaseThird);
            }
            _foulFlightHoldSeconds = null;
            _windowCarry = 0f;
            _pitchCallDone = false;
            _inPitchPhase = false;
            if (IsTerminalPitch(_pitchIdx)) { _stage = LiveStage.Play; EnterBattedBallOrHold(); return; }
            if (_pitchIdx + 1 < _paQueue.Count) { BeginPitchRender(_pitchIdx + 1); return; }
            if (_atBatNext != null) { OpenWindowOrBreak(_atBatNext); return; }
            _stage = LiveStage.Idle;
            AdvanceToNextWindow();
        }

        // ── 打席途中の振り逃げの3D再生（issue #488） ──
        // EnterBatteryMissPlay 系（issue #487）と同型。盗塁・牽制・暴投/捕逸のいずれとも異なり
        // 三振確定後の後処理（イニング後処理の前）として判定されるため、これらの再生（あれば）が
        // すべて終わった後に連結する（FinishBatteryMissView 参照）＝この球で最後にチェックする事象。

        /// <summary>振り逃げのプレーを守備ビューで再生し始める。捕手の落球・打者走者の一塁到達・
        /// 一塁への送球は engine の <see cref="PlaybackPlay"/>（<c>BuildDropThirdStrike</c> の出力）が
        /// 単一ソース＝盗塁・牽制・暴投/捕逸・打球と同じ経路で描く。</summary>
        private void EnterDropThirdStrikePlay(PlaybackPlay dropThirdStrike)
        {
            _inPitchPhase = false;
            _pitchBallVisible = false;
            _stage = LiveStage.Play;
            _windowCarry = 0f;
            _dropThirdStrikePlay = dropThirdStrike;
            // 投球は対決ビューで見せ終わっている＝その続きから再生する（頭が全景にならない）。
            _t = FieldCutMomentOf(dropThirdStrike);
            _dropThirdStrikeEndAt = DropThirdStrikePlayEndTime(dropThirdStrike);
            _view.SetPlay(dropThirdStrike);
            if (_duelRig != null && _duelRig.Available)
            {
                if (_prog != null && _current != null)
                    _duelRig.SetFieldIdentity(_prog.CurrentDefenseIdentity(_current.IsTop));
                _duelRig.BeginFieldPlay(dropThirdStrike, (float)_t);
            }
            RenderDropThirdStrikeFrame();
        }

        /// <summary>振り逃げ再生の1フレーム（<see cref="RenderBatteryMissFrame"/> と同型）。</summary>
        private void RenderDropThirdStrikeFrame()
        {
            if (_dropThirdStrikePlay == null) return;
            _view.SetTime(_t);
            if (FieldPlayActive) _duelRig.SetFieldTime((float)_t);
            if (_caption != null) _caption.text = PlaybackEvaluator.CaptionAt(_dropThirdStrikePlay, _t);
        }

        /// <summary>振り逃げ再生の打ち切り時刻（<see cref="BatteryMissPlayEndTime"/> と同じ規則）。</summary>
        private double DropThirdStrikePlayEndTime(PlaybackPlay play)
        {
            var lastEvent = play.ResAt;
            foreach (var seg in play.Ball) if (seg.T1 > lastEvent) lastEvent = seg.T1;
            foreach (var r in play.Runners)
                foreach (var s in r.Segs)
                {
                    if (s.From.X == s.To.X && s.From.Y == s.To.Y) continue;
                    if (s.T1 > lastEvent) lastEvent = s.T1;
                }
            var end = System.Math.Min(play.Dur, lastEvent + PlayTailSeconds);
            var judged = PlaybackEvaluator.LastJudgementTime(play);
            if (judged.HasValue) end = System.Math.Max(end, judged.Value + _view.JudgementHoldSeconds);
            return end;
        }

        /// <summary>振り逃げ再生が終わった: 対決構図へ戻し、出塁後の塁・アウトを反映してから打席の続きへ。
        /// 三振確定後の後処理として起きる（＝この球の最後の事象）ので、これ以上連結するプレーは無い。</summary>
        private void FinishDropThirdStrikeView()
        {
            EndFieldPlay();
            _view.HideJudgement();
            _dropThirdStrikePlay = null;
            var q = _paQueue[_pitchIdx];
            if (q.Live != null)
            {
                SetOuts(q.Live.Outs);
                SetBases(q.Live.BaseFirst, q.Live.BaseSecond, q.Live.BaseThird);
            }
            _foulFlightHoldSeconds = null;
            _windowCarry = 0f;
            _pitchCallDone = false;
            _inPitchPhase = false;
            if (IsTerminalPitch(_pitchIdx)) { _stage = LiveStage.Play; EnterBattedBallOrHold(); return; }
            if (_pitchIdx + 1 < _paQueue.Count) { BeginPitchRender(_pitchIdx + 1); return; }
            if (_atBatNext != null) { OpenWindowOrBreak(_atBatNext); return; }
            _stage = LiveStage.Idle;
            AdvanceToNextWindow();
        }

        private void Update()
        {
            // ×4速時は酔い防止のため自動で全景固定へ（Issue #119 完了条件）。毎フレーム同期するだけで十分軽量。
            _view.CameraWorkEnabled = _cameraWorkWanted && _speed < 4f;
            // 判定オーバーレイ（#59）の表示窓はプレー時間軸で数えるので、倍速を渡して実時間の見え方を保つ（#236 症状A）。
            _view.PlaybackSpeed = _speed;

            // 対決ビューはポーズを12fps量子化で進める（タイム中断中は盤面と同様に凍結）。
            // 打球プレー中は俯瞰を出していても回す＝トグルで切り替えた瞬間に3Dが止まっていない（F9）。
            if (!_paused && (_duelOn || FieldPlayActive)) _duelRig?.Tick(Time.deltaTime * _speed);
            RefreshDuelFieldLabels();   // 頭上フルネームラベル（issue #393）。ポーズ中も現在位置のまま表示を保つ
            RefreshHudVisibility();     // 打球プレー中はマッチアップHUDを引っ込める（下の説明を参照）
            UpdateTacticsAcceptance();  // 采配カードの受付ゲート（issue #493）。投手が次球の振りかぶりへ入ったら締め切る

            // タイム中断中（issue #173）は盤面・オーバーレイ・自動進行のすべてを凍結する。再開で続きから流れる。
            if (_paused) return;

            // イニング間はここで抜ける＝再生系を動かさない。倍速を掛けた時計で数えるので、
            // ×4速なら表示時間も1/4になる（速度設定と間合いが食い違わない）。
            // タイム中断中はこの手前で return しているので、クロックも一緒に凍る。
            if (_inningBreakOn)
            {
                _inningBreakClock += Time.deltaTime * _speed;
                if (_inningBreakClock >= inningBreakHoldSeconds) EndInningBreak();
                return;
            }

            // 判定オーバーレイは自前の寿命で消す（打球再生へ移った直後の1球分もそのまま見せる）。
            // pitchCallHoldSeconds は出現・消失を含めた総寿命なので、畳む 0.16 秒前に is-enter を戻して
            // 縮みながら消えるところまでを窓の中で終わらせる（対決ビューの捕手据え置きと窓を揃えるため）。
            if (_pitchCallOn)
            {
                _pitchCallClock += Time.deltaTime * _speed;
                if (_pitchCallClock >= pitchCallHoldSeconds - CallFadeSeconds)
                    _pitchCall?.AddToClassList("is-enter");
                if (_pitchCallClock >= pitchCallHoldSeconds) HidePitchCall();
            }
            // 得点オーバーレイも自前の寿命で加算演出→消去まで進める（issue #60）。
            if (_scoreCallOn) AdvanceScoreCall(Time.deltaTime * _speed);
            // 交代ポップアップも自前の寿命で消す（issue #209 表現）。
            if (_subCallOn)
            {
                _subCallClock += Time.deltaTime * _speed;
                if (_subCallClock >= subCallHoldSeconds) HideSubCall();
            }
            // 試合開始/終了テロップ（issue #442）。判定オーバーレイ（#236）と同じく実時間で数える
            // ＝倍速でも見え方を一定にする。「ゲームセット」は得点テロップの演出が済んでから掲げる。
            if (_gameSetPending && !_scoreCallOn)
            {
                _gameSetPending = false;
                ShowGameTelop("ゲームセット");
                // 器が見つからない画面でも結果画面への受け渡しだけは必ず行う（遷移を人質にしない）。
                if (!_gameTelopOn) FinishGameEndHandOff();
            }
            if (_gameTelopOn)
            {
                _gameTelopClock += Time.deltaTime;
                if (_gameTelopClock >= gameTelopHoldSeconds)
                {
                    // プレー再生が器を先取りしていたら畳まない（判定の表示中に消さない）。フラグだけ下ろす。
                    if (_stage == LiveStage.Pitch || _stage == LiveStage.Play) { _gameTelopOn = false; _gameTelopClock = 0f; }
                    else HideGameTelop();
                    if (_gameOver) FinishGameEndHandOff();
                }
            }

            // 采配窓: 打者を映したまま指示を待つ。打席の頭だけ短い間を置き（設計書15 §8.3-①）、
            // 打席の途中は球間そのものが窓なので待たずに次の1球へ抜ける。倍速で間も一緒に縮む。
            if (_stage == LiveStage.Window)
            {
                // 「プレイボール」（issue #442）が掲がっている間は初球を投げさせない＝テロップの寿命が
                // 必ず窓の中で尽きてから投球が始まる。器は判定テロップと共用なので、投球フェーズへ
                // 入ってから寿命が切れると「畳めず次の判定表示まで残留する」（寿命を2.1sへ延ばした
                // 2026-08-05 に顕在化する並び。従来は寿命0.6s＜窓1.0sで構造的に起きなかった）
                if (_gameTelopOn) return;
                _windowClock += Time.deltaTime * _speed;
                if (_windowClock < _windowHold) return;
                _windowCarry = _windowClock - _windowHold;
                ThrowFromWindow();
                return;
            }

            if (_stage != LiveStage.Pitch && _stage != LiveStage.Play) return;

            if (_inPitchPhase)
            {
                // インプレー打球の据え置き中（issue #481）。_duelRig.Tick は上でこのフレームも既に
                // 回っている＝対決構図のまま球と残像は進み続ける。ここは「いつ守備ビューへ切り替えるか」
                // だけを毎フレーム見る。ビューポートから出たら（または保険の秒数を使い切ったら）進める。
                if (_battedHoldActive)
                {
                    _battedHoldClock += Time.deltaTime * _speed;
                    if ((_duelRig?.BattedOutOfFrame ?? true) || _battedHoldClock >= BattedHoldSafetyCapSeconds)
                    {
                        _battedHoldActive = false;
                        EnterBattedBallOrHold();
                    }
                    return;
                }
                _pitchClock += Time.deltaTime * _speed;
                // 対決ビュー中は打席の初球をワインドアップから見せる＝時計が負の間はリリース前（design-18 §7-2）
                if (_pitchClock < 0f) return;
                if (_pitchFirstPending)
                {
                    _pitchFirstPending = false;
                    BeginPitchBall(_pitchIdx);
                }
                // 1球ぶんの投球軌道（またはファウル打球の再生）を毎フレーム進める。
                if (_pitchBallVisible) _view.SetTime(_pitchClock);
                // 判定＋カウント更新はリリース+_pitchCallAt（俯瞰=盤面の投球尺／対決ビュー=球審のコール）。
                // 次のリリースだけ対決ビュー中は duelPitchIntervalSeconds まで遅らせ、フル振りかぶりを収める。
                if (!_pitchCallDone && _pitchClock >= _pitchCallAt)
                {
                    _pitchCallDone = true;
                    var q = _paQueue[_pitchIdx];
                    var pt = q.Token;
                    SetCount(pt.BallsAfter, pt.StrikesAfter);
                    ShowPitchCall(pt);
                    // 打席途中の牽制（issue #486）: この球の前に牽制球が投じられていれば、判定結果
                    // （アウト/セーフ）に関わらず守備ビューで再生する（分岐させない）。盗塁より先に
                    // 解決する事象なので盗塁より先にチェックする（同じ球に両方載ることがある＝牽制セーフの
                    // 直後に走塁勝負が続く場合。その連結は FinishPickoffView が担う）。
                    if (q.Live?.PickoffPlay is { } pickoffPlay)
                    {
                        EnterPickoffPlay(pickoffPlay);
                        return;
                    }
                    // 打席途中の盗塁（issue #485）: この球の前に盗塁が解決していれば、守備ビューで
                    // 「走者が走る→捕手が投げる→塁で判定」を見せてから塁・アウトを動かす（先に動かすと
                    // 走る前に着いて見える）。再生が終わったら打席の続きへ戻る（次打席へは進めない）。
                    if (q.Live?.StealPlay is { } stealPlay)
                    {
                        EnterStealPlay(stealPlay);
                        return;
                    }
                    // 打席途中の暴投・捕逸（issue #487）: 盗塁・牽制と違い「この球の前」ではなく
                    // 「この球そのものの結果」として起きるため、盗塁・牽制の判定を終えたこの位置でチェックする。
                    if (q.Live?.BatteryMissPlay is { } batteryMissPlay)
                    {
                        EnterBatteryMissPlay(batteryMissPlay);
                        return;
                    }
                    // 振り逃げ（issue #488）: 三振確定後の後処理として判定されるため、盗塁・牽制・
                    // 暴投/捕逸のいずれよりも後にチェックする（＝この球で最後に見る事象）。
                    if (q.Live?.DropThirdStrikePlay is { } dropThirdStrikePlay)
                    {
                        EnterDropThirdStrikePlay(dropThirdStrikePlay);
                        return;
                    }
                    // 1球ごとの観測があるときは、その球を投げ終えた実状態でアウト・塁も動かす
                    //（打席途中の牽制死等がその場で絵に出る。合成投球列には無いので触らない）。
                    if (q.Live != null)
                    {
                        SetOuts(q.Live.Outs);
                        SetBases(q.Live.BaseFirst, q.Live.BaseSecond, q.Live.BaseThird);
                    }
                    if (IsTerminalPitch(_pitchIdx))
                    {
                        // インプレー打球は対決構図のまま球がビューポートから出るまで据え置く
                        // （design-18 §4.5-④・issue #481）。三振・四球等（Play無し）は見せるものが
                        // 無いので即座に結果保持へ入る。
                        if (_current.Play != null && DuelPacingActive && !(_duelRig?.BattedOutOfFrame ?? true))
                        {
                            _battedHoldActive = true;
                            _battedHoldClock = 0f;
                            return;
                        }
                        EnterBattedBallOrHold();   // 打席を終わらせた球は判定直後に打球再生／結果保持へ
                        return;
                    }
                    if (_pendingFoulFlight != null)
                    {
                        // ファウルは**守備が一切解決されない**（BuildFoulFlight は打者走者も野手の動きも
                        // 送球も作らない）＝守備ビューへ切り替える意味がない。打球の行方は対決ビュー側の
                        // 打球表示が接触からそのまま見せている（issue #313 の目的はこれで満たされる）。
                        // F9 以前はここで俯瞰へ切り替えていた。今は**構図を動かさず**、俯瞰（デバッグ）は
                        // 同じ時計で並走させるだけ。
                        var wasDuelOn = _duelOn;
                        var shownFoul = wasDuelOn ? (_duelRig?.BattedElapsedSeconds ?? -1f) : -1f;
                        _view.SetPlay(_pendingFoulFlight);
                        _foulFlightHoldSeconds = (float)_pendingFoulFlight.Dur;
                        _pitchBallVisible = true;
                        // 対決ビューから来た場合は接触の瞬間から（投球セグメントの二重表示を避ける,
                        // issue #330）＋対決ビューで見せた打球のぶんは飛ばす＝**対決ビューが今出している
                        // 位置に俯瞰を合わせる**（両者は同じ実時間で進むので以降もずれない）。
                        // 俯瞰固定中は対決ビュー側で見せていないので投球から流す。
                        _pitchClock = wasDuelOn ? (float)ContactMomentOf(_pendingFoulFlight, shownFoul) : 0f;
                        _view.SetTime(_pitchClock);
                        _pendingFoulFlight = null;
                    }
                }
                if (_pitchCallDone)
                {
                    var interval = DuelPacingActive ? duelPitchIntervalSeconds : pitchIntervalSeconds;
                    if (_foulFlightHoldSeconds is { } hold) interval = Mathf.Max(interval, hold);
                    if (_pitchClock < interval) return;

                    // ファウルは対決の構図から離れないので、着地しても戻す先が無い（F9 以前はここで
                    // 対決ビューへ復帰させていた）。間合いの延長ぶんを畳むだけ。
                    _foulFlightHoldSeconds = null;
                    _windowCarry = _pitchClock - interval;   // 端数は次の間合いへ持ち越す
                    if (_windowCarry < 0f) _windowCarry = 0f;
                    _pitchCallDone = false;
                    _inPitchPhase = false;

                    // まだ投球列に続きがある＝一括経路（合成投球列）。1球進行では窓を1つ開けて次の球を待つ。
                    if (_pitchIdx + 1 < _paQueue.Count) { BeginPitchRender(_pitchIdx + 1); return; }
                    if (_atBatNext != null) { OpenWindowOrBreak(_atBatNext); return; }
                    // 1球供給も次の窓も無い＝打席が確定しないまま半回が終わった等。次の窓を取りに行く。
                    _stage = LiveStage.Idle;
                    AdvanceToNextWindow();
                }
                return;
            }

            _t += Time.deltaTime * _speed;
            // 打席途中の牽制の再生（issue #486）: 盗塁より先に解決する事象なので盗塁より先にチェックする。
            if (_pickoffPlay != null)
            {
                RenderPickoffFrame();
                if (_t > _pickoffEndAt + 0.3) FinishPickoffView();
                return;
            }
            // 打席途中の盗塁の再生（issue #485）: 打球（_current.Play）とは別プレーなので専用に回し、
            // 終わったら FinishPaView（次打席へ）ではなく打席の続きへ戻す（FinishStealView）。
            if (_stealPlay != null)
            {
                RenderStealFrame();
                if (_t > _stealEndAt + 0.3) FinishStealView();
                return;
            }
            // 打席途中の暴投・捕逸の再生（issue #487）: 盗塁・牽制の再生（あれば）が終わった後に連結される
            // （FinishPickoffView／FinishStealView）ので、盗塁より後にチェックする。
            if (_batteryMissPlay != null)
            {
                RenderBatteryMissFrame();
                if (_t > _batteryMissEndAt + 0.3) FinishBatteryMissView();
                return;
            }
            // 打席途中の振り逃げの再生（issue #488）: 盗塁・牽制・暴投/捕逸の再生（あれば）が
            // すべて終わった後に連結されるので、最後にチェックする。
            if (_dropThirdStrikePlay != null)
            {
                RenderDropThirdStrikeFrame();
                if (_t > _dropThirdStrikeEndAt + 0.3) FinishDropThirdStrikeView();
                return;
            }
            if (_current.Play != null)
            {
                RenderReplayFrame();
                if (_t > PlayEndTime() + 0.3) FinishPaView();
            }
            else if (_t >= 0)
            {
                FinishPaView();
            }
        }

        /// <summary>再生を打ち切ってから次打席へ移るまでの余韻[秒]。</summary>
        private const double PlayTailSeconds = 0.6;

        /// <summary>
        /// 再生を打ち切る時刻。**「まだ見るものがある」最後の瞬間＋余韻**で切る。
        ///
        /// <b>なぜ engine の <c>Dur</c> をそのまま使わないか</b>: タイムラインの長さは
        /// <c>max(決着, 打者走者の一塁到達) + 1.2</c> と<b>野手が定位置へ戻り終わる時刻</b>の大きいほうで、
        /// 長打では後者が勝つ。本塁打は柵越えの後、フェンス際まで行った野手が定位置へ戻るまでを
        /// 尺に含むので **決着から 9.5 秒**あり、その間に起きるのは野手の復帰だけ（打者走者は
        /// 「ダイヤモンド一周の簡略で一塁まで」なので塁上で止まっている）。
        /// 2Dの俯瞰では盤面が収束していく画として読めたが、3Dの守備カメラは全景（縦155m）へ引くので
        /// **4pxの点が数個ゆっくり動くだけの9.5秒**になる（監督決定 2026-08-01・打球処理の3D化 ②）。
        ///
        /// 以前はこの早切りを「アウトで走者も得点も無い」場合だけに掛けていた（deadOut）。
        /// 走者レッグを見るようにしたので場合分けは要らなくなり、長打にも同じ規則が効く。
        /// </summary>
        private double PlayEndTime()
        {
            var play = _current.Play;

            // 「まだ見るもの」＝決着／球が動き終わる／**実際に動く**走者レッグの終わり。
            // ⚠ 動かない走者のレッグを数えないこと。塁上で止まる走者は AppendHeldRunners が
            // T1=Duration で張るので、除かないと必ず Dur に戻って1秒も縮まらない。
            var lastEvent = play.ResAt;
            foreach (var seg in play.Ball) if (seg.T1 > lastEvent) lastEvent = seg.T1;
            foreach (var r in play.Runners)
                foreach (var s in r.Segs)
                {
                    if (s.From.X == s.To.X && s.From.Y == s.To.Y) continue;   // その場に居るだけ
                    if (s.T1 > lastEvent) lastEvent = s.T1;
                }
            var end = System.Math.Min(play.Dur, lastEvent + PlayTailSeconds);

            // 判定オーバーレイ（#59）の表示窓 [T1, T1+hold) を必ず含める（issue #236 症状A）。
            // 内野ゴロの打者走者は「送球到達＝アウト成立」より後に一塁へ着く（T1＞ResAt）ので、
            // 早切りだけだと最頻のアウトが一度も出ない。Dur でクランプしない（SetTime は t の純関数で、
            // Dur を超えた時刻を評価しても最終フレームを保持するだけ）。
            var judged = PlaybackEvaluator.LastJudgementTime(play);
            if (judged.HasValue) end = System.Math.Max(end, judged.Value + _view.JudgementHoldSeconds);

            // ⚠ 得点テロップ（#60）の寿命も含める。FinishPaView が HideScoreCall するので、
            // 含めないと**加算の途中でテロップごと消える**（本塁打は必ずここを通る＝早切りの直撃）。
            // 加算は各走者の本塁到達（issue #438）なので、最後の到達＋最終ホールドまで見せ切る。
            // ⚠ 非有限は無視（#208 の教訓: ResAt=+∞ のプレーで end=∞ になると再生が終わらない）。
            if (_runScoreShowAt.Count > 0 && double.IsFinite(_runScoreShowAt[_runScoreShowAt.Count - 1]))
                end = System.Math.Max(end, _runScoreShowAt[_runScoreShowAt.Count - 1] + scoreCallFinalHoldSeconds);

            // 結果テキスト（issue #438: 打者セーフは到達で出す）も一呼吸見せてから切る。
            if (double.IsFinite(_resultShowAt))
                end = System.Math.Max(end, _resultShowAt + PlayTailSeconds);

            return end;
        }

        private string NextPrompt() =>
            "采配を選んで次の打席へ（" + _current.Inning + "回" + (_current.IsTop ? "表" : "裏") + "）";

        private void RenderReplayFrame()
        {
            var play = _current.Play;
            _view.SetTime(_t);
            // 3Dの守備ビューは同じ時計で並走させる（位置の真値は engine 側の同じ評価器＝両者の絵は
            // 原理的に一致する。不変条件#1）。俯瞰を出していても回すのは Tick と同じ理由。
            if (FieldPlayActive) _duelRig.SetFieldTime((float)_t);
            if (_caption != null) _caption.text = PlaybackEvaluator.CaptionAt(play, _t);

            // 結果テキスト（issue #438）: 内部の決着（ResAt）でなく表示用の時刻で出す＝
            // 打者がセーフで生きる結果は到達の絵と同時。アウト・柵越えは従来どおり確定の瞬間。
            if (_result != null)
            {
                _result.text = play.Result;
                _result.style.display = _t >= _resultShowAt ? DisplayStyle.Flex : DisplayStyle.None;
            }

            // ラインスコア（issue #438）: 最後の生還走者が本塁に帰ってから掲げる（得点の先出しをしない）。
            if (_t >= _lineScorePostAt)
            {
                if (!_lineScorePosted)
                {
                    _lineScorePosted = true;
                    PostLineScore(_current.Inning, _current.IsTop, finished: false);
                }
            }
            else
            {
                _lineScorePosted = false;   // 巻き戻し（キャプチャのシーク）は掲示前へ戻す＝旧 resolved=false と同型
            }

            // 大型スコア（issue #438）: 各走者の本塁到達（プレー時計）で1点ずつ。器は到達の1拍前に
            // 加点前スコアで開く（#60 の「前スコア→加算」の演出順は保つ）。
            if (_runScoreShowAt.Count > 0 && _runScoreShown < _runScoreShowAt.Count)
            {
                if (!_scoreCallOn && _runScoreShown == 0 && _t >= _runScoreShowAt[0] - scoreCallStepSeconds)
                    BeginScoreCall(_current, playDriven: true);
                while (_scoreCallOn && _scoreCallPlayDriven
                       && _runScoreShown < _runScoreShowAt.Count && _t >= _runScoreShowAt[_runScoreShown])
                {
                    StepScoreCallOnce();
                    _scoreCallClock = 0f;   // 最後の+1から最終ホールドを数え直す
                    _runScoreShown++;
                }
            }
        }

        /// <summary>
        /// 表示同期の時刻（issue #438）を前計算する。計算は engine の純関数
        /// <see cref="PlaybackDisplaySync"/> が単一ソース（テストもそちら）。
        /// </summary>
        private void PrepareDisplayTimings(LivePlateAppearance item)
        {
            _runScoreShown = 0;
            var play = item?.Play;
            if (play == null)
            {
                _resultShowAt = 0;
                _lineScorePostAt = 0;
                _runScoreShowAt = System.Array.Empty<double>();
                return;
            }
            _resultShowAt = PlaybackDisplaySync.ResultShowTime(play);
            _runScoreShowAt = PlaybackDisplaySync.RunScoreTimes(play, item.RunsScored, scoreCallStepSeconds);
            _lineScorePostAt = _runScoreShowAt.Count > 0
                ? System.Math.Max(_resultShowAt, _runScoreShowAt[_runScoreShowAt.Count - 1])
                : _resultShowAt;
        }

        // ── ラインスコア（LineScorePanel。設計書16 §4-2） ──
        // 数値はすべてエンジンの観測データ（MatchLiveSnapshot.AwayLine/HomeLine）から引き、UI側で得点を組み立てない。
        // 通常再生の掲示は RenderReplayFrame が表示同期の時刻（issue #438）で行う。ここは
        // **キャプチャ専用**（FreezeCurrentAtResult）＝結果時点へ一気に飛ぶので即時に掲げ、
        // 得点コールも旧・等間隔（実時間）で回す。
        private void UpdateScoreboard(LivePlateAppearance item, bool resolved)
        {
            if (!resolved) { _lineScorePosted = false; return; }
            if (_lineScorePosted) return;
            _lineScorePosted = true;
            PostLineScore(item.Inning, item.IsTop, finished: false);
            if (item.RunsScored > 0) BeginScoreCall(item, playDriven: false);
        }

        private void UpdateScoreboardFinal(GameResult r)
        {
            _lineScorePosted = true;
            PostLineScore(r.InningsPlayed, isTop: false, finished: true);
        }

        private void PostLineScore(int inning, bool isTop, bool finished)
        {
            if (_prog == null) return;
            var snap = _prog.Snapshot();
            LineScorePanel.Fill(_root, snap.AwayLine, snap.HomeLine, inning, isTop, finished);
            ScoreBug.Fill(_root, snap.AwayLine, snap.HomeLine, inning, isTop, finished);
        }

        // ── 中継風カウント（BSO）＋塁ダイヤ ──
        // ScoreBug の左袖は点灯ランプ（issue #479）。玉数が上限を兼ねる＝四球の最終球は BallsAfter=4、
        // 三振の最終球は StrikesAfter=3 で渡ってくるが、玉が3個/2個しか無いので自然に頭打ちになる
        // （実物の掲示板に 4 ボールは出ない。打席が終わって 0-0 に戻る）。数字方式のときだけ必要だった
        // 明示的な Clamp はここでは不要。
        private void SetCount(int balls, int strikes)
        {
            SetLampRow(_lampsB, balls);
            SetLampRow(_lampsS, strikes);
        }

        private void SetOuts(int outs) => SetLampRow(_lampsO, outs);

        /// <summary>1行ぶんのランプを左から <paramref name="lit"/> 個だけ点ける（グローは USS が追従する）。</summary>
        private static void SetLampRow(VisualElement[] lamps, int lit)
        {
            if (lamps == null) return;
            for (var i = 0; i < lamps.Length; i++) lamps[i]?.EnableInClassList("is-on", i < lit);
        }

        /// <summary>
        /// 判定テキストの字送りを器の実幅に追従させる（issue #479）。「盤面幅の10%・30〜64pxクランプ」は
        /// USS では書けない（vw も clamp も無い）ので、実幅が決まる/変わるたびに入れ直す。
        /// </summary>
        private static void RegisterCallTextMetrics(VisualElement host, Label face, Label shadow)
        {
            if (host == null || face == null) return;
            host.RegisterCallback<GeometryChangedEvent>(_ =>
                UiComponents.ApplyCallTextMetrics(host.resolvedStyle.width, face, shadow));
        }

        // ── 1球ごとの判定オーバーレイ（issue #6） ──
        // 判定（ストライク/ボール/ファール…）を盤面上に大きく出し、実1球記録がある打席では球速・球種を併記する。
        // 合成投球列（実記録なし・PitchSequenceSynthesizer）は球種・球速を持たないので判定だけを出す。
        // インプレーは判定文言を持たない（打球が飛ぶのは盤面で見えており、決着後はアウト/セーフと
        // 結果ラベルが別途出る＝冗長。打球の飛び際に文字を被せない）。何を打たれたかを残すため
        // 球種・球速の下段だけを出す。上下どちらも空なら器ごと出さない。
        private void ShowPitchCall(PitchToken pt)
        {
            if (_pitchCall == null || _pitchCallJudge == null) return;

            var judge = PitchJudgeJp(pt);
            UiComponents.SetCallText(_pitchCallJudge, _pitchCallJudgeShadow, judge);
            var judgeDisplay = judge.Length == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            _pitchCallJudge.style.display = judgeDisplay;
            if (_pitchCallJudgeShadow != null) _pitchCallJudgeShadow.style.display = judgeDisplay;
            // 色は「ストライク系＝見逃し/空振り/ファール/三振」でひとまとまり（参照モックの色分け）。
            // IsStrikeCall はカウント上のストライク判定なのでファールを含まない＝ここで足す。
            _pitchCallJudge.EnableInClassList("pitch-call__judge--strike",
                IsStrikeCall(pt.Kind) || pt.Kind == KokoSim.Engine.Match.AtBat.PitchKind.Foul);
            _pitchCallJudge.EnableInClassList("pitch-call__judge--ball", pt.Kind == KokoSim.Engine.Match.AtBat.PitchKind.Ball);

            // 計器表示（球種・球速）。実1球記録がある打席だけ値が入る＝合成投球列では丸ごと畳む。
            var note = PitchNoteJp(pt);
            var ptype = pt.PitchType.HasValue ? PitchJp(pt.PitchType.Value) : "";
            var speed = pt.VelocityKmh.HasValue ? Mathf.RoundToInt((float)pt.VelocityKmh.Value).ToString() : "";
            SetOptionalLabel(_pitchCallNote, note);
            SetOptionalLabel(_pitchCallPitchType, ptype);
            SetOptionalLabel(_pitchCallSpeed, speed);
            // 単位は球速があるときだけ（"km/h" が単独で浮かないように）。
            if (_pitchCallUnit != null)
                _pitchCallUnit.style.display = speed.Length == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            var hasReadout = note.Length > 0 || ptype.Length > 0 || speed.Length > 0;
            if (_pitchCallDetail != null)
                _pitchCallDetail.style.display = hasReadout ? DisplayStyle.Flex : DisplayStyle.None;

            if (judge.Length == 0 && !hasReadout) { HidePitchCall(); return; }

            ShowCallHost(_pitchCall);
            _pitchCallOn = true;
            _pitchCallClock = 0f;
        }

        private void HidePitchCall()
        {
            _pitchCallOn = false;
            _pitchCallClock = 0f;
            if (_pitchCall != null)
            {
                _pitchCall.RemoveFromClassList("is-enter");
                _pitchCall.style.display = DisplayStyle.None;
            }
        }

        /// <summary>
        /// 判定オーバーレイを跳ねさせながら出す（issue #479）。USS の transition は「前のフレームで
        /// 解決済みのスタイル」との差分で走るので、is-enter を外すのは**スタイル解決を1回挟んだ後**で
        /// なければならない。
        ///
        /// ⚠ <c>ExecuteLater(0)</c> 1回では足りない（2026-08-05 に A/B で実測）。スケジューラは
        /// そのフレームのスタイル解決より前に回るため、付けて外すのが同じ解決パスに入り、
        /// **開始値 0.84 が一度も記録されない＝跳ねずにいきなり出る**。二段にすると確実に次フレームへ送れる
        /// （ミリ秒の決め打ちを避けるのはパネルのフレームレートに依存させないため）。
        ///
        /// ⚠ 効いているかは**静止画では絶対に判定できない**。跳ねてもいなくても「出た後」の絵は同一なので、
        /// 立ち上がりを毎フレーム実測して確かめる（BoardLampShooter の anim ログ。二段での実測は
        /// 33ms→scale 0.93 / 66ms→1.006 の山 / 117ms→1.0、単段では全フレーム 1.0 で平坦）。
        /// ログを <c>Vector3.ToString()</c> で出すと小数2桁に丸められて山（1.006）が「1.00」に化けるので、
        /// **必ず成分（scale.value.x）で見る**。
        /// </summary>
        private static void ShowCallHost(VisualElement host)
        {
            if (host == null) return;
            host.AddToClassList("is-enter");
            host.style.display = DisplayStyle.Flex;
            host.schedule.Execute(() =>
                host.schedule.Execute(() => host.RemoveFromClassList("is-enter")).ExecuteLater(0)
            ).ExecuteLater(0);
        }

        /// <summary>空文字なら畳む（空ラベルが margin だけ残して並びをずらすのを防ぐ）。</summary>
        private static void SetOptionalLabel(Label label, string text)
        {
            if (label == null) return;
            label.text = text;
            label.style.display = text.Length == 0 ? DisplayStyle.None : DisplayStyle.Flex;
        }

        // ── 得点時の大型スコアオーバーレイ（issue #60） ──
        // 並びは自校－相手校固定（先攻/後攻に依存しない）。加点前スコアを表示してから、得点数ぶん
        // 1点ずつ加算する演出を挟み、最終スコアで一呼吸おいて自動的に消える。
        // 加算タイミング（issue #438）: 通常再生は**各走者の本塁到達**（プレー時計・RenderReplayFrame が
        // PlaybackDisplaySync.RunScoreTimes で駆動）。キャプチャ（結果時点へ一気に飛ぶ）だけ
        // 旧・等間隔（実時間）で回す＝playDriven で切り替える。
        private void BeginScoreCall(LivePlateAppearance item, bool playDriven)
        {
            if (_scoreCall == null || _scoreCallOwnNum == null || _scoreCallOppNum == null) return;

            var ownIsAway = _managerIsAway;
            var awayBefore = item.IsTop ? item.AwayScore - item.RunsScored : item.AwayScore;
            var homeBefore = item.IsTop ? item.HomeScore : item.HomeScore - item.RunsScored;
            _scoreCallOwnScore = ownIsAway ? awayBefore : homeBefore;
            _scoreCallOppScore = ownIsAway ? homeBefore : awayBefore;
            _scoreCallOwnScoring = item.IsTop == ownIsAway;
            _scoreCallRunsRemaining = item.RunsScored;
            _scoreCallClock = 0f;
            _scoreCallOn = true;
            _scoreCallPlayDriven = playDriven;

            if (_scoreCallOwnName != null) _scoreCallOwnName.text = ownIsAway ? _awayDisp : _homeDisp;
            if (_scoreCallOppName != null) _scoreCallOppName.text = ownIsAway ? _homeDisp : _awayDisp;
            RefreshScoreCallLabels();
            _scoreCall.style.display = DisplayStyle.Flex;
        }

        /// <summary>+1を1回ぶん進める（表示だけ。加算時刻の判断は呼び元が持つ）。</summary>
        private void StepScoreCallOnce()
        {
            if (_scoreCallOwnScoring) _scoreCallOwnScore++; else _scoreCallOppScore++;
            _scoreCallRunsRemaining--;
            RefreshScoreCallLabels();
        }

        private void AdvanceScoreCall(float dt)
        {
            _scoreCallClock += dt;
            if (_scoreCallRunsRemaining > 0)
            {
                // プレー時計駆動（issue #438）: 加算は RenderReplayFrame が本塁到達で行う。
                // ここで実時間の等間隔を回すと二重加算になる。
                if (_scoreCallPlayDriven) return;
                if (_scoreCallClock < scoreCallStepSeconds) return;
                _scoreCallClock -= scoreCallStepSeconds;
                StepScoreCallOnce();
            }
            else if (_scoreCallClock >= scoreCallFinalHoldSeconds)
            {
                HideScoreCall();
            }
        }

        private void RefreshScoreCallLabels()
        {
            if (_scoreCallOwnNum != null) _scoreCallOwnNum.text = _scoreCallOwnScore.ToString();
            if (_scoreCallOppNum != null) _scoreCallOppNum.text = _scoreCallOppScore.ToString();
        }

        private void HideScoreCall()
        {
            _scoreCallOn = false;
            _scoreCallClock = 0f;
            _scoreCallRunsRemaining = 0;
            if (_scoreCall != null) _scoreCall.style.display = DisplayStyle.None;
        }

        // ── 試合開始/終了テロップ「プレイボール」「ゲームセット」（issue #442） ──
        // 器は判定テロップ（judgement-call）を流用する。中身は普段 Match2DPlaybackElement が
        // プレー時刻の純関数で描く＝プレー再生が始まると自然に上書き・消灯される側なので、
        // 掲げるのは「最初のプレーの前」と「終局後」だけに限る（時間帯が重ならない）。
        private void ShowGameTelop(string text)
        {
            if (_gameTelopHost == null || _gameTelopLabel == null) return;
            UiComponents.SetCallText(_gameTelopLabel, _gameTelopLabelShadow, text);
            // 直前の判定の色クラスを引き継がない（素の書体・色で出す）。
            _gameTelopLabel.EnableInClassList("pitch-call__judge--out", false);
            _gameTelopLabel.EnableInClassList("pitch-call__judge--safe", false);
            _gameTelopLabel.EnableInClassList("pitch-call__judge--fair", false);
            _gameTelopLabel.EnableInClassList("pitch-call__judge--homerun", false);
            if (_gameTelopDetail != null)
            {
                _gameTelopDetail.text = "";
                _gameTelopDetail.style.display = DisplayStyle.None;
            }
            ShowCallHost(_gameTelopHost);
            _gameTelopClock = 0f;
            _gameTelopOn = true;
        }

        private void HideGameTelop()
        {
            _gameTelopOn = false;
            _gameTelopClock = 0f;
            if (_gameTelopHost != null) _gameTelopHost.style.display = DisplayStyle.None;
        }

        /// <summary>スクショ用: 試合開始/終了テロップを強制表示する（寿命管理は通常と同じ）。</summary>
        public void ShowGameTelopForCapture(bool gameSet) => ShowGameTelop(gameSet ? "ゲームセット" : "プレイボール");

        /// <summary>
        /// スクショ用: 任意の1球で判定オーバーレイを出す（issue #479 の受け入れ）。
        /// 文言・色・計器表示の分岐を撮影側に持たせないため、**本番と同じ ShowPitchCall を通す**。
        /// </summary>
        public void ShowPitchCallForCapture(PitchToken pt)
        {
            ShowPitchCall(pt);
            SetCount(pt.BallsAfter, pt.StrikesAfter);
        }

        /// <summary>スクショ用: 判定オーバーレイを畳む（ランプだけを撮るとき）。</summary>
        public void HidePitchCallForCapture() => HidePitchCall();

        /// <summary>スクショ用: BSOランプを任意のカウントで点ける（issue #479 の受け入れ）。</summary>
        public void SetCountForCapture(int balls, int strikes, int outs)
        {
            SetCount(balls, strikes);
            SetOuts(outs);
        }

        private static bool IsStrikeCall(KokoSim.Engine.Match.AtBat.PitchKind k) =>
            k == KokoSim.Engine.Match.AtBat.PitchKind.CalledStrike || k == KokoSim.Engine.Match.AtBat.PitchKind.SwingingStrike;

        // 四球成立の瞬間（4球目）は「ボール」ではなく「フォアボール」を表示する（issue #61）。
        // 3ストライク目も同様に「ストライク」ではなく「三振」を表示する（issue #329）。
        private static string PitchJudgeJp(PitchToken pt)
        {
            switch (pt.Kind)
            {
                case KokoSim.Engine.Match.AtBat.PitchKind.Ball: return pt.BallsAfter >= 4 ? "フォアボール" : "ボール";
                case KokoSim.Engine.Match.AtBat.PitchKind.CalledStrike: return pt.StrikesAfter >= 3 ? "三振" : "ストライク";
                case KokoSim.Engine.Match.AtBat.PitchKind.SwingingStrike: return pt.StrikesAfter >= 3 ? "三振" : "ストライク";
                case KokoSim.Engine.Match.AtBat.PitchKind.Foul: return "ファール";
                case KokoSim.Engine.Match.AtBat.PitchKind.InPlay: return "";   // 打球再生と結果ラベルに任せる
                case KokoSim.Engine.Match.AtBat.PitchKind.HitByPitch: return "デッドボール";
                default: return "";
            }
        }

        // 判定の内訳（見逃し/空振り）＝計器表示の左端に添える補助情報。
        // 球種・球速は数字と和文を混植しないよう別ラベルへ分けたので、ここでは組み立てない（issue #479）。
        private static string PitchNoteJp(PitchToken pt)
        {
            return pt.Kind == KokoSim.Engine.Match.AtBat.PitchKind.CalledStrike ? "見逃し"
                : pt.Kind == KokoSim.Engine.Match.AtBat.PitchKind.SwingingStrike ? "空振り" : "";
        }

        private static string PitchJp(PitchType t)
        {
            switch (t)
            {
                case PitchType.Fastball: return "ストレート";
                case PitchType.TwoSeam: return "ツーシーム";
                case PitchType.Cutter: return "カットボール";
                case PitchType.Slider: return "スライダー";
                case PitchType.Curve: return "カーブ";
                case PitchType.Fork: return "フォーク";
                case PitchType.Changeup: return "チェンジアップ";
                case PitchType.Shuuto: return "シュート";
                case PitchType.Sinker: return "シンカー";
                default: return t.ToString();
            }
        }

        private void SetBases(bool first, bool second, bool third)
        {
            _bases?[0]?.EnableInClassList("is-on", first);   // base-1（一塁）
            _bases?[1]?.EnableInClassList("is-on", second);  // base-2（二塁）
            _bases?[2]?.EnableInClassList("is-on", third);   // base-3（三塁）
        }

        // ── 速度・補助 ──
        private void WireSpeed(string name, float speed)
        {
            var b = _root.Q<Button>(name);
            if (b == null) return;
            _speedButtons.Add(b);
            System.Action handler = () =>
            {
                _speed = speed;
                foreach (var sb in _speedButtons) sb.EnableInClassList("chip-btn--on", sb == b);
            };
            b.clicked += handler;
            _speedHandlers.Add((b, handler));
        }

        // カメラ演出トグル（Issue #119）。押すたびオン/オフ、既定オフ。
        private void OnToggleCameraWork()
        {
            _cameraWorkWanted = !_cameraWorkWanted;
            ApplyCameraWorkState();
        }

        private void ApplyCameraWorkState()
        {
            _camWork?.EnableInClassList("chip-btn--on", _cameraWorkWanted);
        }

        // 試合開始の既定ビュー（issue #312）: 対決ビューから始める。DuelStageRig が使えない環境
        // （3Dアセット未導入・生成失敗）では従来どおり俯瞰にフォールバックする。
        /// <summary>
        /// 対決ビューを生成する唯一の入口。<b>配色は生成前に渡す</b>＝SpawnActor がマテリアルを焼くので、
        /// 後から SetTeamColors するだけだと最初の数フレームが既定色（白＋紺）で映る。
        /// </summary>
        private DuelStageRig NewDuelRig()
        {
            var offenseIsTop = _prog?.Snapshot().OffenseIsTop ?? true;
            DuelStageRig.InitialTeamColors = _awayColors != null && _homeColors != null
                ? (offenseIsTop ? _homeColors : _awayColors, offenseIsTop ? _awayColors : _homeColors)
                : ((KokoSim.Engine.Nation.TeamColors, KokoSim.Engine.Nation.TeamColors)?)null;
            _duelColorsOffenseIsTop = _awayColors != null && _homeColors != null ? offenseIsTop : (bool?)null;
            var rig = new DuelStageRig();
            DuelStageRig.InitialTeamColors = null;   // 静的な受け渡し窓は使ったら必ず閉じる
            // 判定表示の長さを流し込む＝捕手の据え置き（捕球位置で静止する窓）が表示と同じ時刻に終わる
            rig.PitchCallHoldSeconds = pitchCallHoldSeconds;
            return rig;
        }

        private void InitDuelViewDefault()
        {
            _duelRig ??= NewDuelRig();
            if (!_duelRig.Available) { _duelOn = false; return; }
            EnsureDuelImg();
            _duelOn = true;
        }

        private void EnsureDuelImg()
        {
            if (_duelImg == null)
            {
                _duelImg = new DuelViewElement();
                _duelHost?.Add(_duelImg);
                // _duelImg を作り直したら旧ラベルホストは**孤児**（旧Imgの子のまま panel の外）。
                // 参照だけ生き残ると下の null ガードが再装着を飛ばし、以後 RefreshDuelFieldLabels が
                // panel の外の孤児へ書き続けて**実機で頭上ラベルが一枚も出ない**
                // （issue #454 実測: OnEnable の _duelHost.Clear() 後、host.panel=null・プールには
                // 正しい氏名6件・絵はゼロ。夜間検証は enable 1回きりの環境で通ってしまった）。
                _duelLabelHost = null;
                _duelFieldLabelPool.Clear();
            }
            _duelImg.Rig = _duelRig;   // カラム実寸に合わせたRT適応リサイズの結線
            _duelImg.Bind(_duelRig.Target);
            if (_duelLabelHost == null)
            {
                // _duelImg（Image）の子にする＝RTの表示矩形をそのままローカル座標系として使える
                // （DuelViewElement.Snap が自身の箱をRTの整数倍スケールぴったりに作るため）。
                _duelLabelHost = new VisualElement { pickingMode = PickingMode.Ignore };
                _duelLabelHost.style.position = Position.Absolute;
                _duelLabelHost.style.left = 0; _duelLabelHost.style.top = 0;
                _duelLabelHost.style.right = 0; _duelLabelHost.style.bottom = 0;
                _duelImg.Add(_duelLabelHost);
            }
        }

        // 対決ビューの手動トグル。アセット未導入環境では何もしない（警告ログのみ）。手動でOFFにした試合中は
        // 打球/次打席の自動切替も止め、再度手動でONにすると自動切替が復帰する（issue #312）。
        private void OnToggleDuelView()
        {
            if (!_duelOn)
            {
                _duelRig ??= NewDuelRig();
                if (!_duelRig.Available) return;
                EnsureDuelImg();
                ApplyDuelHandedness();   // 打席の途中で開いても現打席の左右で立たせる（issue #337）
            }
            _duelOn = !_duelOn;
            _duelManualOff = !_duelOn;
            ApplyDuelViewState();
        }

        // ── 打球プレーの3D再生（打球処理の3D化 F9） ──
        // 表示のON/OFF（＝俯瞰トグル）とは独立に動かす。3Dは常に走らせ、俯瞰も同じ時計で並走させる＝
        // トグルは「どちらを画面に出すか」だけを決める。途中で切り替えても場面が飛ばない。
        // Rig が使えない環境（3Dアセット未導入）では何も起きず、従来どおり俯瞰だけが残る。

        /// <summary>3Dの守備ビューが打球プレーを再生中か。</summary>
        private bool FieldPlayActive => _duelRig != null && _duelRig.Available && _duelRig.FieldPlayActive;

        private void BeginFieldPlay(float t0)
        {
            if (_duelRig == null || !_duelRig.Available || _current?.Play == null) return;
            // 頭上フルネームラベル用（issue #393）: 守備位置→選手をプレー開始時点で固定する
            // （交代・投手交代で変わるため、この打球プレーの間はこの対応で通す）。
            if (_prog != null) _duelRig.SetFieldIdentity(_prog.CurrentDefenseIdentity(_current.IsTop));
            _duelRig.BeginFieldPlay(_current.Play, t0);
        }

        private void EndFieldPlay()
        {
            if (FieldPlayActive) _duelRig.EndFieldPlay();
        }

        /// <summary>
        /// マッチアップHUDの出し入れ。<b>打球プレー中（3Dの守備ビュー）は出さない</b>——帯は打席の
        /// 対決を読むための計器で、守備が始まったら見るのは打球と野手であり、頭上フルネームラベル
        /// （<see cref="RefreshDuelFieldLabels"/>）と役割が重なるうえ Z 順で上に乗って隠してしまう
        /// （設計書18 §7-1 の既知の限界）。中身は消さず表示だけ落とす＝打席へ戻ったときに
        /// 組み直しが要らない。
        /// </summary>
        private void RefreshHudVisibility()
        {
            if (_hudHost == null) return;
            var show = !FieldPlayActive;
            var want = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (_hudHost.style.display != want) _hudHost.style.display = want;
        }

        /// <summary>
        /// 頭上フルネームラベルの張り替え（issue #393）。打球プレー中だけ、画面内の野手・走者の
        /// 頭上に氏名チップを置く。打席中（対決の寄り）は出さない＝寄りの絵は背番号の担当。
        /// プールを使い回し、余った枠は隠すだけ（毎フレーム Add/Remove しない）。
        /// </summary>
        private void RefreshDuelFieldLabels()
        {
            if (_duelLabelHost == null) return;
            if (!FieldPlayActive)
            {
                foreach (var l in _duelFieldLabelPool) l.style.display = DisplayStyle.None;
                return;
            }
            var labels = _duelRig.CurrentFieldLabels(MaxDuelFieldLabels);
            var w = _duelImg.resolvedStyle.width;
            var h = _duelImg.resolvedStyle.height;
            if (float.IsNaN(w) || float.IsNaN(h) || w <= 0f || h <= 0f) return;   // レイアウト未確定の初回フレーム

            for (var i = 0; i < labels.Count; i++)
            {
                var lbl = DuelFieldLabelSlot(i);
                var l = labels[i];
                lbl.text = l.Name;
                lbl.EnableInClassList("duel-field-label--runner", l.IsRunner);
                lbl.style.display = DisplayStyle.Flex;
                lbl.style.left = l.Viewport01.x * w;
                lbl.style.top = (1f - l.Viewport01.y) * h;
            }
            for (var i = labels.Count; i < _duelFieldLabelPool.Count; i++)
                _duelFieldLabelPool[i].style.display = DisplayStyle.None;
        }

        private Label DuelFieldLabelSlot(int index)
        {
            while (_duelFieldLabelPool.Count <= index)
            {
                var l = new Label { pickingMode = PickingMode.Ignore };
                l.AddToClassList("duel-field-label");
                l.AddToClassList("f-display");   // 人名は明朝（書体3役ルール, design-16）
                _duelLabelHost.Add(l);
                _duelFieldLabelPool.Add(l);
            }
            return _duelFieldLabelPool[index];
        }

        // 打席進行に連動した自動切替（次打席開始→対決 / インプレー打球→俯瞰）。手動OFF中は何もしない。
        private void SetDuelViewAuto(bool on)
        {
            if (_duelManualOff || _duelOn == on) return;
            if (on)
            {
                _duelRig ??= NewDuelRig();
                if (!_duelRig.Available) return;
                EnsureDuelImg();
                ApplyDuelHandedness();   // rig を作り直した直後は右/右に戻っているため（issue #337）
            }
            _duelOn = on;
            ApplyDuelViewState();
        }

        /// <summary>
        /// 現打席の左右（打者の打席・投手の利き腕）を対決ビューへ流し込む（issue #337）。
        /// 左右のデータは PlayLogEntry 由来で HUD（左打/左投バッジ）と同じ単一ソース＝絵と表示が食い違わない。
        /// スイッチヒッターは対戦投手の逆に立つ（右投手→左打席／左投手→右打席）。
        /// engine 側は Switch を Left 扱いのままなので、左投手との対戦では「絵は右打席／内部の引っ張りは
        /// 左打者」と食い違うが、engine の追随は issue #337 のスコープ外（本文に明記あり）。
        /// </summary>
        private void ApplyDuelHandedness()
        {
            if (_duelRig == null || !_duelRig.Available) return;
            // ⚠ 打球プレー中は触らない。SetHandedness は捕手・球審を打席の定位置へ置き直し、投手の
            // ミラー親を裏返す＝**守備として走っている最中の投手・捕手を打席の較正へ引き戻す**。
            // 打球の途中で俯瞰トグルを叩いたときだけ通る道で、絵にしか出ない（F9）。
            // 左右は次の打席の頭（OpenWindow / BeginPa）で必ず当て直すので、飛ばして困らない。
            if (FieldPlayActive) return;
            // 1球進行では采配窓の対戦（＝いまの打者・投手）が単一ソース。一括経路では確定打席から引く。
            Handedness bats, throws;
            if (_atBat is { } at)
            {
                bats = at.BatterBats; throws = at.PitcherThrows;
            }
            else if (_current != null)
            {
                bats = _current.BatterBats; throws = _current.PitcherThrows;
            }
            else return;
            var pitcherLeft = throws == Handedness.Left;
            var batterLeft = bats == Handedness.Left || (bats == Handedness.Switch && !pitcherLeft);
            _duelRig.SetHandedness(batterLeft, pitcherLeft);
        }

        /// <summary>撮影ハーネス用（エディタ限定運用）: 対決ビューのトグルを外から叩く。</summary>
        internal void ToggleDuelViewForCapture() => OnToggleDuelView();

        /// <summary>撮影ハーネス用: 対決ビューが表示中か（打席送りで試合が替わるとリセットされるため確認用）。</summary>
        internal bool DuelViewOnForCapture => _duelOn;

        /// <summary>撮影ハーネス用: 対決ステージ（録画ハーネスがRTと診断値を直接読む）。</summary>
        internal DuelStageRig DuelRigForCapture => _duelRig;

        /// <summary>
        /// 撮影ハーネス用: 対決ビュー中のリリース間隔を差し替える（間合いの比較録画用。issue #353）。
        /// SerializeField の既定を変えずに1回の録画だけ別の間合いで回すための入口。
        /// </summary>
        internal void SetDuelPitchIntervalForCapture(float seconds)
        {
            if (seconds > 0f) duelPitchIntervalSeconds = seconds;
        }

        /// <summary>
        /// 撮影ハーネス用: 対決ステージを作り直す（着衣メッシュの差し替え比較=痩せ3段シート用。
        /// DuelStageRig.UniformMeshOverrideForCapture を変えてから呼ぶ）。表示中なら新RTを結線し直す。
        /// </summary>
        internal void RebuildDuelRigForCapture()
        {
            _duelRig?.Dispose();
            _duelRig = NewDuelRig();
            if (_duelOn && _duelRig.Available && _duelImg != null)
            {
                _duelImg.Rig = _duelRig;
                _duelImg.Bind(_duelRig.Target);
            }
        }

        /// <summary>撮影ハーネス用: 現在ロード中の打席の再生を頭からやり直す（対決ビューの狙い撃ち撮影用）。</summary>
        internal void RestartCurrentPaForCapture()
        {
            if (_current == null) return;
            BeginPaFromSequence();
        }

        private void ApplyDuelViewState()
        {
            if (_fieldHost != null) _fieldHost.style.display = _duelOn ? DisplayStyle.None : DisplayStyle.Flex;
            if (_duelHost != null) _duelHost.style.display = _duelOn ? DisplayStyle.Flex : DisplayStyle.None;
            // トグルの意味は「2D俯瞰（デバッグ）を出すか」＝3Dが既定なので点灯は反転（F9）。
            _duelBtn?.EnableInClassList("chip-btn--on", !_duelOn);
            // 俯瞰へ渡すときは飛翔中の球を畳む（issue #332）。畳まないと、対決ビューを閉じた後も
            // 次に開いた瞬間に古い球が本塁前に残って見える（#330 の「投球が二重に見える」と同種の事故）
            if (!_duelOn) _duelRig?.HideBall();
        }

        private static void SetEnabled(Button b, bool on) { if (b != null) b.SetEnabled(on); }

        private static void SetVisible(VisualElement e, bool on)
        {
            if (e != null) e.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // ── 操作状態（issue #289 で操作列をポップアップへ集約） ──
        // 残置した操作列（速度・カメラ・対決ビュー）は常時有効＝状態を持たない。
        // 打席解決中の守り（issue #278: 進行体を背景スレッドが握っている間はエンジンを触る操作を伏せる）は
        // ポップアップ側のボタン活性（UpdatePauseMenu）が引き継ぐ。采配窓を開いた時（OpenWindow/EndGame）に
        // ここが呼ばれるので、開いていれば表示を復帰させる。
        private void UpdateControlState()
        {
            if (_paused) UpdatePauseMenu();
        }

        private void SetBackHomeVisible(bool on)
        {
            if (_backHome != null) _backHome.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void SetText(string name, string text)
        {
            var l = _root.Q<Label>(name);
            if (l != null) l.text = text;
        }

        private string FinalResultText(GameResult r)
        {
            if (r.Tied) return "引き分け " + r.AwayRuns + "-" + r.HomeRuns;
            var managerWon = _managerIsAway ? !r.HomeWon : r.HomeWon;
            return (managerWon ? "自校の勝ち " : "自校の負け ") + r.AwayRuns + "-" + r.HomeRuns;
        }

        private static string ResultJp(KokoSim.Engine.Match.AtBat.PlateAppearanceResult r)
        {
            switch (r)
            {
                case KokoSim.Engine.Match.AtBat.PlateAppearanceResult.Strikeout: return "三振";
                case KokoSim.Engine.Match.AtBat.PlateAppearanceResult.Walk: return "四球";
                case KokoSim.Engine.Match.AtBat.PlateAppearanceResult.HitByPitch: return "死球";
                default: return "凡退";
            }
        }

        // ══ 3カラム（スタメン列）＋マッチアップHUD＋実況履歴 ══
        // 表示専用。数値はすべてエンジンの Snapshot（成績集計）から引く（UI再計算禁止・不変条件）。
        // 左列=自校 / 右列=相手校（ミラー）。現打者ハイライトは攻撃側の列を移り、HUDは左=投手/右=打者で固定。

        private void RefreshPanel()
        {
            if (_prog == null || _statsProvider == null) return;
            var snap = _prog.Snapshot();
            var ownIsAway = _managerIsAway;
            var own = ownIsAway ? snap.Away : snap.Home;
            var opp = ownIsAway ? snap.Home : snap.Away;
            // 攻守・現打者は「いま止まっている采配窓」から引く。Snapshot 側は確定した打席を指すので、
            // 1球進行では1打席ぶん遅れる（打席の頭でハイライトが前の打者に残る）。
            var offenseIsTop = _atBat?.IsTop ?? snap.OffenseIsTop;
            var offenseIsOwn = offenseIsTop == ownIsAway;   // 攻撃側が自校か
            var order = _atBat?.BatterOrder ?? snap.CurrentBatterOrder;

            // 左右とも同じ並び（打順→守備→名前→背番号→調子→今日）。ミラーはしない。
            BuildColumn(_leftLineup, _leftPitcher, own, mirror: false, highlightOrder: offenseIsOwn ? order : 0);
            BuildColumn(_rightLineup, _rightPitcher, opp, mirror: false, highlightOrder: offenseIsOwn ? 0 : order);
            RefreshHud(snap, offenseIsTop, order);
            ApplyDuelTeamColors(offenseIsTop);
        }

        /// <summary>
        /// 対決ビューの配色を攻守に合わせる。投手・捕手＝守備側の学校、打者＝攻撃側の学校。
        /// 役割（誰が投げて誰が打つか）は固定で、攻守交代で入れ替わるのは<b>どちらの学校の色か</b>。
        /// 塗り替えはマテリアルだけ（素体・クリップは作り直さない）ので進行中に呼んで良い。
        /// </summary>
        private void ApplyDuelTeamColors(bool offenseIsTop)
        {
            if (_duelRig == null || !_duelRig.Available) return;
            if (_awayColors == null || _homeColors == null) return;
            if (_duelColorsOffenseIsTop == offenseIsTop) return;   // 半回が変わった時だけ塗る
            _duelColorsOffenseIsTop = offenseIsTop;
            var offense = offenseIsTop ? _awayColors : _homeColors;
            var defense = offenseIsTop ? _homeColors : _awayColors;
            _duelRig.SetTeamColors(defense, offense);
        }

        private void BuildColumn(VisualElement lineupHost, VisualElement pitcherHost,
            LiveTeamSnapshot team, bool mirror, int highlightOrder)
        {
            if (lineupHost != null)
            {
                lineupHost.Clear();
                foreach (var s in team.Lineup)
                    lineupHost.Add(BuildLineupRow(s, mirror, atBat: s.Order == highlightOrder));
            }
            if (pitcherHost != null)
            {
                pitcherHost.Clear();
                pitcherHost.Add(BuildPitcherRow(team.Pitcher, mirror));
                // 控え（ベンチ入り＝背番号ありのメンバのみ。ベンチ外は出さない）。控えは1本の名簿なので
                // そのまま背番号の昇順で並べる（issue #381）。投手として置かれている控えだけ行の作りを
                // 投手行に替える＝並びは分けず、見せる数字だけ役割に合わせる。
                var benchRows = new List<(int Number, VisualElement Row)>();
                foreach (var b in team.Reserves)
                {
                    if (b.Number <= 0) continue;
                    var asPitcher = FindReservePitcher(team, b.Number);
                    benchRows.Add((b.Number, asPitcher is null
                        ? BuildBenchRow(b, mirror)
                        : BuildBenchPitcherRow(asPitcher, mirror)));
                }
                if (benchRows.Count > 0)
                {
                    benchRows.Sort((a, c) => a.Number.CompareTo(c.Number));
                    var head = MakeLabel("控え", "mlineup-subhead");
                    if (mirror) head.AddToClassList("mlineup-subhead--right");
                    pitcherHost.Add(head);
                    foreach (var r in benchRows) pitcherHost.Add(r.Row);
                }
            }
        }

        // 並び: 打順 / 守備位置 / 名前(フルネーム) / 背番号 / 調子（右列は row-reverse でミラー）。
        // 名前セルはライブ観戦だけ固定幅にする（mlineup-row--compact。試合前画面と共有クラスのため
        // 画面ローカルの直書きにせずモディファイアで限定。issue #297）。
        private VisualElement BuildLineupRow(LiveBatterSlot s, bool mirror, bool atBat)
        {
            var row = new VisualElement();
            row.AddToClassList("mlineup-row");
            row.AddToClassList("mlineup-row--compact");
            if (mirror) row.AddToClassList("mlineup-row--mirror");
            if (atBat) row.AddToClassList("mlineup-row--at-bat");

            row.Add(MakeLabel(s.Order.ToString(), "mlineup-row__ord", "f-num"));
            row.Add(MakeLabel(MatchLiveStatsProvider.PosAbbr(s.Position), "mlineup-row__pos"));
            row.Add(MakeLabel(s.Name, "mlineup-row__name"));   // フルネーム
            if (!string.IsNullOrEmpty(s.ReplacedName))
                row.Add(MakeLabel("←" + s.ReplacedName, "mlineup-row__replaced"));
            row.Add(MakeLabel(NumText(s.Number), "num-badge", "num-badge--sm", "f-num"));
            var face = new ConditionFace();
            face.AddToClassList("mlineup-row__cond");
            face.Set(_statsProvider.ConditionOf(s.SourceId, s.ConditionValue, s.Name));
            row.Add(face);
            return row;
        }

        /// <summary>
        /// その控えが投手として置かれているか（issue #381）。控えは1本の名簿で、
        /// <c>ReservePitchers</c> はそのうち投手枠の者を投手行の材料つきで別に持つ観測データ。
        /// 突き合わせは背番号（ベンチ入りは1〜20で一意）。置かれていなければ null。
        /// </summary>
        private static LivePitcherToday FindReservePitcher(LiveTeamSnapshot team, int number)
        {
            foreach (var p in team.ReservePitchers) if (p.Number == number) return p;
            return null;
        }

        // 控え1行（背番号・名前フル・調子）。守備位置は出さない。代打・代走・守備固めの候補。
        private VisualElement BuildBenchRow(LiveBatterSlot s, bool mirror)
        {
            var row = new VisualElement();
            row.AddToClassList("mlineup-benchrow");
            if (mirror) row.AddToClassList("mlineup-row--mirror");
            row.Add(MakeLabel(NumText(s.Number), "num-badge", "num-badge--sm", "f-num"));
            row.Add(MakeLabel(s.Name, "mlineup-row__name"));
            var face = new ConditionFace();
            face.AddToClassList("mlineup-row__cond");
            face.Set(_statsProvider.ConditionOf(s.SourceId, s.ConditionValue, s.Name));
            row.Add(face);
            return row;
        }

        // 控え投手1行（背番号・名前フル・調子）。
        private VisualElement BuildBenchPitcherRow(LivePitcherToday p, bool mirror)
        {
            var row = new VisualElement();
            row.AddToClassList("mlineup-benchrow");
            if (mirror) row.AddToClassList("mlineup-row--mirror");
            row.Add(MakeLabel(NumText(p.Number), "num-badge", "num-badge--sm", "f-num"));
            row.Add(MakeLabel(p.Name, "mlineup-row__name"));
            var face = new ConditionFace();
            face.AddToClassList("mlineup-row__cond");
            face.Set(_statsProvider.ConditionOf(p.SourceId, p.ConditionValue, p.Name));
            row.Add(face);
            return row;
        }

        private VisualElement BuildPitcherRow(LivePitcherToday p, bool mirror)
        {
            var row = new VisualElement();
            row.AddToClassList("mlineup-pitcher");
            if (mirror) row.AddToClassList("mlineup-pitcher--mirror");
            row.Add(MakeLabel("投", "mlineup-pitcher__tag"));
            row.Add(MakeLabel(p.Name, "mlineup-pitcher__name"));
            // 確定球数＋進行中の打席ぶん（issue #397。LiveLineScore.PendingRuns と同型）。
            row.Add(MakeLabel((p.Pitches + p.PendingPitches) + "球", "mlineup-pitcher__stat"));
            row.Add(MakeLabel(InningsText(p.Outs) + "回", "mlineup-pitcher__stat"));
            row.Add(MakeLabel("失" + p.Runs, "mlineup-pitcher__stat"));
            return row;
        }

        // ── マッチアップHUD（盤面上に画面座標固定・右上=打者/右下=投手） ──
        // コーナーは OnEnable で常設し（BuildHudCorners）、打席ごとに HUD スロットの中身だけを
        // 入れ替える。采配の手札（TacticsCardDeck）は盤面の外＝列の下に置き、作り直さない
        //（選択状態と攻守フェーズを打席をまたいで保持するため。issue #289 からの引き継ぎ）。
        private void BuildHudCorners()
        {
            if (_hudHost == null) return;
            _hudHost.Clear();

            // 投手は右下。
            var bottomRight = new VisualElement { pickingMode = PickingMode.Ignore };
            bottomRight.AddToClassList("mhud-corner");
            bottomRight.AddToClassList("mhud-corner--right");
            _hudPitcherSlot = new VisualElement { pickingMode = PickingMode.Ignore };
            bottomRight.Add(_hudPitcherSlot);
            _hudHost.Add(bottomRight);

            // 打者は右上＝盤面の打者に近い側へ寄せる（投手は右下に残す）。
            var topRight = new VisualElement { pickingMode = PickingMode.Ignore };
            topRight.AddToClassList("mhud-corner");
            topRight.AddToClassList("mhud-corner--topright");
            _hudBatterSlot = new VisualElement { pickingMode = PickingMode.Ignore };
            topRight.Add(_hudBatterSlot);
            _hudHost.Add(topRight);
        }

        /// <summary>
        /// HUD が映す対戦（打者・投手の氏名/背番号/調子）。1球進行では采配窓（<see cref="LiveAtBat"/>）が
        /// 単一ソース——打席の頭で、まだ1球も投げていないうちから打者を出すため。1球供給が無い経路
        /// （撮影ハーネス・観測ゲート未設定）では確定した打席から引く。
        /// </summary>
        private readonly struct Matchup
        {
            public readonly int BatterOrder, BatterNumber, PitcherNumber;
            public readonly string BatterName, PitcherName;
            public readonly int? BatterSourceId, PitcherSourceId;
            public readonly double BatterConditionValue;
            public readonly FieldPosition BatterPosition;

            public Matchup(LiveAtBat a)
            {
                BatterOrder = a.BatterOrder; BatterNumber = a.BatterNumber; PitcherNumber = a.PitcherNumber;
                BatterName = a.BatterName; PitcherName = a.PitcherName;
                BatterSourceId = a.BatterSourceId; PitcherSourceId = a.PitcherSourceId;
                BatterConditionValue = a.BatterConditionValue; BatterPosition = a.BatterPosition;
            }

            public Matchup(LivePlateAppearance p)
            {
                BatterOrder = p.BatterOrder; BatterNumber = p.BatterNumber; PitcherNumber = p.PitcherNumber;
                BatterName = p.BatterName; PitcherName = p.PitcherName;
                BatterSourceId = p.BatterSourceId; PitcherSourceId = p.PitcherSourceId;
                BatterConditionValue = p.BatterConditionValue; BatterPosition = p.BatterPosition;
            }
        }

        private void RefreshHud(MatchLiveSnapshot snap, bool offenseIsTop, int batterOrder)
        {
            if (_hudPitcherSlot == null || _hudBatterSlot == null) return;
            _hudPitcherSlot.Clear();
            _hudBatterSlot.Clear();
            // 打席開始前はHUDだけ空（指示パネルは常設のまま）。
            var m = _atBat != null ? new Matchup(_atBat) : _current != null ? new Matchup(_current) : (Matchup?)null;
            if (m is not { } mu) return;

            var defPitcher = offenseIsTop ? snap.Home.Pitcher : snap.Away.Pitcher; // 守備側投手
            var offense = offenseIsTop ? snap.Away.Lineup : snap.Home.Lineup;
            LiveBatterSlot batterToday = batterOrder >= 1 && batterOrder <= offense.Count
                ? offense[batterOrder - 1] : null;

            // 名前行のチップの地色＝その選手のチーム色（帽子クラウン＝アンダーシャツと同じ「チーム色」）。
            // 試合中不変なので観測データには載せず LiveMatchRequest の静的経路から来る（設計書18 §3.5.1）。
            var offenseColor = TeamChipColor(offenseIsTop ? _awayColors : _homeColors);
            var defenseColor = TeamChipColor(offenseIsTop ? _homeColors : _awayColors);

            _hudPitcherSlot.Add(BuildHudPitcher(defPitcher, mu, defenseColor));
            _hudBatterSlot.Add(BuildHudBatter(batterToday, mu, offenseIsTop, offenseColor));
        }

        /// <summary>
        /// チーム色チップの地色。配色が来ていない経路（撮影ハーネス等）では帯に馴染む灰へ落とす
        /// ——透明にすると守備位置だけ地無しになって行の並びが崩れる。
        /// </summary>
        private static Color TeamChipColor(KokoSim.Engine.Nation.TeamColors colors)
        {
            if (colors == null) return new Color(1f, 1f, 1f, 0.22f);
            var c = colors.CapCrown;
            return new Color32(c.R, c.G, c.B, 255);
        }

        private VisualElement BuildHudPitcher(LivePitcherToday def, Matchup m, Color teamColor)
        {
            var band = HudBand();

            // 1段目＝登板順・背番号・名前・学年・球数。打者HUDと同一の行構造（1つ目が白地黒字のチップ、
            // 次が素の白文字の背番号、その次が名前）で、右端に球数を置く。
            // 登板順は「N番手」が数字＋和文の混植なので、ReliefLabel の文字列ではなく数値から組んで
            // Label を分ける（書体3役ルール・設計書16 §2。Oswald は欧文専用）。
            var idn = HudIdRow();
            idn.Add(def.ReliefOrder > 1
                ? HudChip(def.ReliefOrder.ToString(), "番手", "mhud-cp--key", numeric: true)
                : HudChip("先発", null, "mhud-cp--key"));
            var pNum = NumText(m.PitcherNumber);
            if (pNum.Length > 0) idn.Add(HudNumber(pNum));
            idn.Add(HudName(m.PitcherName));
            if (def.Grade > 0) idn.Add(HudChip(def.Grade.ToString(), "年", "mhud-cp--dim", numeric: true));

            // 球数は名前行の右端＝継投判断の主指標なので、スタッツ行のセルに埋もれさせず単体で立てる。
            // 確定球数＋進行中の打席ぶん（issue #397）。据え置きを解消し1球ごとに動かす。
            var totalPitches = def.Pitches + def.PendingPitches;
            // 警告色は絶対球数でなく「目安投球数に対する残量」で出す（issue #408）。生成スタミナの水準を
            // 上げて平均球数が100前後になったため、絶対 80/100 だと健康な先発が常時 alert になる。スタミナバー
            // （BuildStaminaBar）と同じ残量帯（<0.6=注意 / <0.3=警告）で数字も落とし、数字とバーの色を一致させる。
            var pcMod = def.StaminaRemaining < 0.3 ? "mhud-pc__n--alert"
                : def.StaminaRemaining < 0.6 ? "mhud-pc__n--warn" : null;
            idn.Add(MakeRow("mhud-spacer"));
            idn.Add(HudPitchCount(totalPitches.ToString(), pcMod));
            band.Add(idn);

            // 2段目＝通算防御率・本日の奪三振・与四死球の等幅3セル（打者HUDと同構造）。奪三振と与四死球は
            // 対で見て初めて「今日の出来」になるので隣接させる（issue #407）。単位は見出しへ逃がしてあるので
            // 値は数字だけ＝Oswald（.f-num）が素直に効く（設計書16 §2）。
            // 防御率は通算＋本日の合算（issue #449。通算なしは本日分のみ、登板なしかつ本日アウト0のみ「—」）。
            var era = MatchLiveStatsProvider.LiveEra(_statsProvider.CareerPitching(m.PitcherSourceId), def.EarnedRuns, def.Outs);
            var stats = HudStatsRow();
            stats.Add(HudCell("防御率", era, first: true));
            stats.Add(HudCell("奪三振", def.StrikeOuts.ToString()));
            stats.Add(HudCell("与四死球", (def.Walks + def.HitByPitch).ToString()));
            band.Add(stats);

            // 3段目＝スタミナ。帯の最下段として帯と同幅で組み込む（別要素として浮かせない）。
            band.Add(BuildStaminaBar(def.StaminaRemaining));
            return band;
        }

        // スタミナ残量のバー（継投の目安）。球数の数字だけでなく形でも「そろそろ」を出す。
        // 色は残量で落とす: 6割超=良好 / 3〜6割=注意 / 3割未満=警告（盤面ビューの状態コード）。
        // 文字（見出し・％）は持たない＝残量は形と色だけで読ませ、数字は名前行の球数に集約する。
        private static VisualElement BuildStaminaBar(double remaining)
        {
            var row = MakeRow("mhud-stam");

            var track = new VisualElement { pickingMode = PickingMode.Ignore };
            track.AddToClassList("stam__track");
            var fill = new VisualElement { pickingMode = PickingMode.Ignore };
            fill.AddToClassList("stam__fill");
            var pct = remaining < 0.0 ? 0.0 : remaining > 1.0 ? 1.0 : remaining;
            fill.style.width = new Length((float)(pct * 100.0), LengthUnit.Percent);
            if (pct < 0.3) fill.AddToClassList("stam__fill--low");
            else if (pct < 0.6) fill.AddToClassList("stam__fill--mid");
            track.Add(fill);
            // 目盛は3等分（残り2/3・1/3の位置）。色の変わり目と同じ場所に置いて読み替えを不要にする。
            foreach (var at in new[] { 33.333f, 66.667f })
            {
                var tick = new VisualElement { pickingMode = PickingMode.Ignore };
                tick.AddToClassList("stam__tick");
                tick.style.left = new Length(at, LengthUnit.Percent);
                track.Add(tick);
            }
            row.Add(track);
            return row;
        }

        private VisualElement BuildHudBatter(LiveBatterSlot batterToday, Matchup m, bool offenseIsTop, Color teamColor)
        {
            var band = HudBand();

            // 1段目＝放送と同じ並び「4番 三塁 5 中村 陽介 2年 (調子)」。左右打は3Dの絵で分かるので出さない。
            var idn = HudIdRow();
            idn.Add(HudChip(m.BatterOrder.ToString(), "番", "mhud-cp--key", numeric: true));
            var pos = MatchLiveStatsProvider.PosJp(batterToday?.Position ?? m.BatterPosition);
            if (pos.Length > 0) idn.Add(HudTeamChip(pos, teamColor));
            var bNum = NumText(m.BatterNumber);
            if (bNum.Length > 0) idn.Add(HudNumber(bNum));
            idn.Add(HudName(m.BatterName));
            if (batterToday is { Grade: > 0 }) idn.Add(HudChip(batterToday.Grade.ToString(), "年", "mhud-cp--dim", numeric: true));
            // 顔は名前行の右端＝学年チップの外側。spacer で押し出す。
            idn.Add(MakeRow("mhud-spacer"));
            var face = new ConditionFace();
            face.AddToClassList("mhud-face");
            face.Set(_statsProvider.ConditionOf(m.BatterSourceId, m.BatterConditionValue, m.BatterName));
            idn.Add(face);
            band.Add(idn);

            // 2段目＝成績。打率は通算＋本日の合算（issue #449。通算なしは本日分のみ、通算なしかつ
            // 本日打数0のみ「—」）。本塁打・打点は通算のみ（issue範囲外・据え置き）＝等幅セルに割ると
            // 同格の3項目に見えるが、意味の違いはここに残す。
            var b = _statsProvider.CareerBatting(m.BatterSourceId);
            var stats = HudStatsRow();
            stats.Add(HudCell("打率", MatchLiveStatsProvider.LiveAvg(b, batterToday?.AtBats ?? 0, batterToday?.Hits ?? 0), first: true));
            stats.Add(HudCell("本塁打", b == null ? "—" : b.HomeRuns.ToString()));
            stats.Add(HudCell("打点", b == null ? "—" : b.Rbi.ToString()));
            band.Add(stats);

            // 3段目＝本日の打席。まだ結果が無い1打席目は段ごと出ない＝帯は2段のまま。
            var pas = BuildTodayPlateAppearances(m, offenseIsTop);
            if (pas != null) band.Add(pas);
            return band;
        }

        /// <summary>
        /// 本日の打撃成績（この打者がここまでに終えた打席の略記を左から順にパネルで並べる）。
        /// <b>まだ絵にしていない打席は必ず外す</b>（<see cref="_displayedLogCount"/> までしか見ない）——
        /// 入れると、盤面で再生する前に結果を先に見せてしまう。落としても例外もテスト失敗も出ない。
        /// 1件も無ければ null を返す＝段を作らない（1打席目の帯は2段のまま）。
        /// </summary>
        private VisualElement BuildTodayPlateAppearances(Matchup m, bool offenseIsTop)
        {
            var log = _prog?.PlayLog;
            if (log == null) return null;
            var upTo = Mathf.Min(_displayedLogCount, log.Count);
            if (upTo <= 0) return null;

            var row = MakeRow("mhud-pas");
            var any = false;
            for (var i = 0; i < upTo; i++)
            {
                var e = log[i];
                // 同じ打者の打席だけ拾う。代打で打順枠の中身が入れ替わるので名前まで見る
                // （BoxScoreGrid.Build が使うキーと同じ組み合わせ）。
                if (e.IsTop != offenseIsTop || e.BatterOrder != m.BatterOrder
                    || e.BatterName != m.BatterName) continue;
                var pa = MakeRow("mhud-pa", PaKindMod(e));
                // 略記は和文なので .f-num は付けない（Oswald は欧文専用）。
                pa.Add(MakeLabel(MatchLiveStatsProvider.ScorebookAbbr(e), "mhud-pa__t"));
                row.Add(pa);
                any = true;
            }
            return any ? row : null;
        }

        /// <summary>
        /// 打席パネルの色（結果の種類コード）。安打系=桜／四球・バント系=アンバー／アウト=空青。
        /// 犠飛は「バント」ではないが記録上は犠打と同じ生産的アウトなので黄側へ、失策出塁は
        /// 「出塁したが安打ではない」ので四球側へ寄せてある（3分類に収まらない2つの置き場）。
        /// </summary>
        private static string PaKindMod(PlayLogEntry e)
        {
            if (e.Result.IsHit()) return "mhud-pa--hit";
            if (e.Result is PlateAppearanceResult.Walk or PlateAppearanceResult.HitByPitch
                or PlateAppearanceResult.ReachedOnError) return "mhud-pa--walk";
            if (e.IsSacBunt || e.IsSacFly) return "mhud-pa--walk";
            return "mhud-pa--out";
        }

        // ── 実況履歴（プレー確定ごとに直近3件を薄く積む省スペース型） ──
        private void PushHistory(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            _capHist.Add(text);
            while (_capHist.Count > 3) _capHist.RemoveAt(0);
            if (_capHistHost == null) return;
            _capHistHost.Clear();
            foreach (var t in _capHist)
                _capHistHost.Add(MakeLabel(t, "mlive-caphist__line"));
        }

        private static string HistoryLine(LivePlateAppearance pa)
        {
            var half = pa.Inning + "回" + (pa.IsTop ? "表" : "裏");
            var res = pa.Play != null ? pa.Play.Result : ResultJp(pa.Result);
            return half + " " + pa.BatterName + "　" + res;
        }

        // ── 生成ヘルパ ──
        private static Label MakeLabel(string text, params string[] classes)
        {
            var l = new Label(text) { pickingMode = PickingMode.Ignore };
            foreach (var c in classes) l.AddToClassList(c);
            return l;
        }

        private static VisualElement MakeRow(params string[] classes)
        {
            var v = new VisualElement { pickingMode = PickingMode.Ignore };
            foreach (var c in classes) v.AddToClassList(c);
            return v;
        }

        // ── マッチアップHUD（黒半透明の一体帯）の生成 ──────────────────────────
        // 見た目（地・区切り線・級数・余白）は部品辞書（components.uss の .mhud-*）が持つ。
        // ここが決めるのは「どのデータをどの段のどのセルに置くか」だけ。
        // **地を持つのは帯コンテナ1枚だけ**で、段もセルも背景を持たない（区切りは border のみ・隙間なし）。
        // MakeLabel / MakeRow の上に積むのは pickingMode=Ignore を無料で継承するため
        //（HUDは盤面の絵の上に乗る非対話オーバーレイなので、1要素でも拾うとクリック中断が効かなくなる）。

        /// <summary>帯コンテナ（縦1本）。打者・投手とも同じ器で、幅はコーナーが決める＝両者同幅。</summary>
        private static VisualElement HudBand() => MakeRow("mhud-band");

        /// <summary>1段目＝名前行。</summary>
        private static VisualElement HudIdRow() => MakeRow("mhud-idrow");

        /// <summary>2段目＝スタッツ行（等幅セルを隙間なしで並べる）。</summary>
        private static VisualElement HudStatsRow() => MakeRow("mhud-stats");

        /// <summary>
        /// 名前行のチップ。<paramref name="unit"/> がある場合は数字と和文単位で Label を分ける
        /// ——Oswald（.f-num）は欧文専用で和文グリフを持たないため（書体3役ルール・設計書16 §2）。
        /// </summary>
        private static VisualElement HudChip(string text, string unit, string mod, bool numeric = false)
        {
            var chip = MakeRow("mhud-cp");
            if (mod != null) chip.AddToClassList(mod);
            var t = MakeLabel(text, "mhud-cp__t");
            if (numeric) t.AddToClassList("f-num");
            chip.Add(t);
            if (!string.IsNullOrEmpty(unit)) chip.Add(MakeLabel(unit, "mhud-cp__u"));
            return chip;
        }

        /// <summary>
        /// チーム色地のチップ（打者=守備位置／投手=背番号）。地色は校ごとに変わるので
        /// USS では宣言できず、ここでインラインで入れる（値は data/team-colors.yaml 由来・乱数不使用）。
        /// </summary>
        private static VisualElement HudTeamChip(string text, Color bg)
        {
            var chip = HudChip(text, null, "mhud-cp--team", numeric: IsNumText(text));
            chip.style.backgroundColor = bg;
            return chip;
        }

        /// <summary>
        /// 帯の中で最も大きい要素＝選手名。人名は明朝なので、書体registryに新クラスを足さず
        /// <c>.f-display</c> を要素へ直接付ける（UI-BUILD-METHOD の新規コード方針）。
        /// 姓名間の全角空白は半角へ落として詰める（字間は USS の letter-spacing 側）。
        /// </summary>
        private static VisualElement HudName(string name) =>
            MakeLabel((name ?? "").Replace('　', ' '), "mhud-nm", "f-display");

        /// <summary>背番号。チップにせず素の白文字で名前の直前に置く（打者・投手で共通）。</summary>
        private static VisualElement HudNumber(string digits) => MakeLabel(digits, "mhud-no", "f-num");

        /// <summary>
        /// 球数「6球」。数字と和文単位で Label を分ける（Oswald は欧文専用）。見出しは持たない。
        /// 数字は太字＝学年チップより目立たせる（継投判断の主指標）。
        /// </summary>
        private static VisualElement HudPitchCount(string digits, string mod)
        {
            var box = MakeRow("mhud-pc");
            var n = MakeLabel(digits, "mhud-pc__n", "f-num-bd");
            if (mod != null) n.AddToClassList(mod);
            box.Add(n);
            box.Add(MakeLabel("球", "mhud-pc__u"));
            return box;
        }

        /// <summary>
        /// スタッツ行の1セル（ラベル上・数値下の縦積み）。単位はラベルへ逃がしてあるので
        /// 値は数字だけになり、Oswald が素直に効く（UI原則③）。
        /// </summary>
        private static VisualElement HudCell(string lab, string val, bool first = false, string valMod = null)
        {
            var cell = MakeRow("mhud-cell");
            // セル間の区切りは上下15%ずつ短い細片で引く（border では詰められない）。先頭セルには付けない
            // ＝帯の左端に落ちて「枠の縁」に見えてしまうため。
            if (!first) cell.Add(MakeRow("mhud-cell__div"));
            cell.Add(MakeLabel(lab, "mhud-cell__lab"));
            var v = MakeLabel(val, "mhud-cell__val");
            // 該当なしの「—」に .f-num を付けると欠字になる（Oswald は ASCII しか収録していない）。
            // 数字のときだけ載せる＝UiComponents の IsNumericCell と同じ作法。
            if (IsNumText(val)) v.AddToClassList("f-num");
            if (valMod != null) v.AddToClassList(valMod);
            cell.Add(v);
            return cell;
        }

        /// <summary>Oswald（.f-num）に載せてよい文字列か。数字・小数点・符号・% だけなら真。</summary>
        private static bool IsNumText(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (var c in s)
                if (!char.IsDigit(c) && c != '.' && c != '-' && c != '+' && c != '%') return false;
            return true;
        }

        // 背番号テキスト（0＝番号なしは空表示）。
        private static string NumText(int n) => n > 0 ? n.ToString() : "";

        // アウト数 → 投球回テキスト（n / n 1/3 / n 2/3）。
        private static string InningsText(int outs)
        {
            var whole = outs / 3;
            var frac = outs % 3;
            return frac == 0 ? whole.ToString() : whole + " " + frac + "/3";
        }

        // ── スクショ用（決定論シーク不要：進行状態を外部から操作するフックだけ提供） ──
        public MatchProgression Progression => _prog;
        public LivePlateAppearance CurrentPa => _current;
        public bool IsGameOver => _gameOver;

        // ── 打球プレーの3D再生を外から覗く窓（打球処理の3D化 F9 の受け入れ用） ──
        /// <summary>打球再生の時計[秒]。俯瞰・3D・判定オーバーレイを同時に駆動している唯一の値。</summary>
        public double PlayClockForCapture => _t;

        /// <summary>3Dの守備ビューが走っているか。</summary>
        public bool FieldPlayActiveForCapture => FieldPlayActive;

        /// <summary>
        /// 判定オーバーレイが**画面に出ているか**。
        /// ⚠ 「<c>JudgementAt</c> が値を返すか」ではなく<b>要素の表示状態</b>を見ること。
        /// F9 で壊れ得るのは探索ではなく<b>器の置き場所</b>（盤面の中に置いたままだと3Dでは隠れる）で、
        /// 純関数の側を見ても壊れていることが分からない。
        /// </summary>
        public bool JudgementVisibleForCapture =>
            _root?.Q<VisualElement>("judgement-call")?.resolvedStyle.display == DisplayStyle.Flex;

        /// <summary>判定オーバーレイの文言（出ていなければ空）。</summary>
        public string JudgementTextForCapture =>
            JudgementVisibleForCapture ? (_root?.Q<Label>("judgement-judge")?.text ?? "") : "";

        /// <summary>スクショ用: 1球采配ショートカットをクリックしたのと同じ状態にする（打撃側）。</summary>
        public void SelectPitchBattingForCapture(PitchBattingOverride? v)
        {
            _pitchBattingChoice = v;
            var id = v switch
            {
                PitchBattingOverride.ForceSwing => "batting-swing",
                PitchBattingOverride.ForceTake => "batting-take",
                PitchBattingOverride.Bunt => "batting-bunt",
                PitchBattingOverride.Squeeze => "batting-squeeze",
                _ => null,
            };
            // 既定カード（任せ）は廃止＝解除は「点いているカードをもう一度押す」。null 指定は消灯だけ行う。
            if (id == null) _signPad?.ClearSelection("batting");
            else _signPad?.Select(id);   // OnSelect 経由で choice も入る（クリックと同経路。直接代入は保険）
        }

        /// <summary>スクショ用: 采配カードをクリックしたのと同じ状態にする（配球側）。null で解除。</summary>
        public void SelectPitchDefenseForCapture(PitchPolicy? v)
        {
            var id = v switch
            {
                PitchPolicy.FastballHeavy => "policy-fast",
                PitchPolicy.BreakingHeavy => "policy-break",
                PitchPolicy.ControlFirst => "policy-zone",
                _ => null,
            };
            if (id == null) { PushDefPolicy(null); _signPad?.ClearSelection("policy"); }
            else _signPad?.Select(id);
        }

        /// <summary>スクショ用: 采配カードをホバー拡大の状態にする（実際のカーソルは要らない）。null で解除。</summary>
        public void SetTacticsHotForCapture(string id) => _signPad?.SetHotForCapture(id);

        /// <summary>スクショ用: 采配カードをIDで押す（クリックと同経路＝OnSelect も通る）。
        /// <see cref="SelectPitchBattingForCapture"/> が扱わない群（走塁・仕掛け）を押すのに使う。</summary>
        public void SelectSignForCapture(string id) => _signPad?.Select(id);

        /// <summary>スクショ用: 打席途中プレー（issue #485〜#488）を再生中なら種類名、そうでなければ null。
        /// 打球（<see cref="FieldPlayActiveForCapture"/>）とは別経路なので別の口で見る。</summary>
        public string MidPaPlayForCapture =>
            _stealPlay != null ? "steal"
            : _pickoffPlay != null ? "pickoff"
            : _batteryMissPlay != null ? "battery-miss"
            : _dropThirdStrikePlay != null ? "drop-third"
            : null;

        /// <summary>スクショ用: 直前の球にバント・スクイズの指示が出ていたか（issue #490 の絵の検証用）。</summary>
        public bool BuntAttemptForCapture { get; private set; }

        /// <summary>
        /// スクショ用: チーム配色を差し込む。撮影ハーネスの試合は <see cref="LiveMatchRequest"/> を通らないので
        /// 配色が null のまま＝HUDのチーム色チップが灰色のフォールバックで写ってしまい、実物と違う絵になる。
        /// 既に配色が入っている経路（実試合）では**上書きしない**。
        /// </summary>
        public void FillTeamColorsForCapture(KokoSim.Engine.Nation.TeamColors away, KokoSim.Engine.Nation.TeamColors home)
        {
            if (_awayColors == null) _awayColors = away;
            if (_homeColors == null) _homeColors = home;
        }

        /// <summary>スクショ用: クリック中断ポップアップを強制表示する（クリックと同経路）。</summary>
        public void OpenPauseMenuForCapture() => OpenPauseMenu();

        /// <summary>
        /// スクショ用: 1球進行そのものを凍らせる（中断ポップアップは出さない＝暗幕で絵を潰さない）。
        /// <see cref="CaptureMode"/> は自動進行ごと止めてしまい采配窓を再現できないので、
        /// 「実進行のまま任意のコマで止める」入口を別に用意する。
        /// </summary>
        public void FreezeLiveForCapture(bool on) => _paused = on;

        /// <summary>
        /// スクショ用: いま<b>打席の頭の采配窓</b>で止まっているか（まだ1球も投げていない＝設計書15 §8 の肝）。
        /// この状態で撮った絵が「打者を見てからサインを出せる」ことの証拠になる。
        /// </summary>
        public bool AtPaHeadWindowForCapture => _stage == LiveStage.Window && _atBat is { PitchNumberInPa: 1 };

        /// <summary>スクショ用: いま采配窓で止まっているか（打席の途中＝球間を含む）。</summary>
        public bool AtWindowForCapture => _stage == LiveStage.Window;

        /// <summary>診断用: 采配カードが受付中か（issue #493）。false＝投手が次球の振りかぶりへ入り締め切っている。</summary>
        public bool TacticsAcceptingInputForCapture => _signPad?.IsAcceptingInputForCapture ?? true;

        /// <summary>診断用: 対決ビューの投手が次球の振りかぶりへ既に切り替わったか（issue #493）。</summary>
        public bool DuelHasCutToNextWindupForCapture => _duelRig?.HasCutToNextWindup ?? false;

        /// <summary>スクショ用: いま止まっている采配窓の局面（回・表裏・カウント・塁）。無ければ null。</summary>
        public LiveAtBat CurrentAtBatForCapture => _atBat;

        /// <summary>スクショ用: 現在のライブ観戦スナップショット（HUD/スタメン列の数値の元, issue #397）。</summary>
        public MatchLiveSnapshot SnapshotForCapture() => _prog?.Snapshot();

        /// <summary>スクショ用: 次の打席を1つ解決してロードする（Update 非依存）。試合終了なら false。</summary>
        public bool AdvanceForCapture()
        {
            if (_gameOver) return false;
            ResolveAndReplayNext();
            return !_gameOver;
        }

        /// <summary>スクショ用: 自校（後攻 home）へ代打を送る。</summary>
        public bool RequestPinchHitHome()
        {
            if (_gameOver || !_prog.PinchHitUpcoming(offenseIsAway: false, benchIndex: 0)) return false;
            RefreshPanel();
            return true;
        }

        /// <summary>スクショ用: 選手交代モーダルを開く（局面に合う種別で開く）。</summary>
        public void OpenSubstitutionForCapture() => _subPanel?.Open();

        /// <summary>スクショ用: 交代ポップアップ（issue #209）を任意の継投ログで強制表示する。</summary>
        public void ForceSubCallForCapture(KokoSim.Engine.Match.Game.PitchingChangeEvent e) => BeginSubCall(e);

        /// <summary>スクショ用: 自校が今「攻撃側」か（交代モーダルの出し分け確認用）。</summary>
        public bool ManagerOnOffenseForCapture
            => _prog != null && _prog.SubstitutionOptions(_managerIsAway).IsOffense;

        /// <summary>スクショ用: 今このタイミングで代走を出せるか（塁上に走者がいる攻撃中）。</summary>
        public bool ManagerCanPinchRunForCapture
            => _prog != null && _prog.SubstitutionOptions(_managerIsAway).CanPinchRun;

        /// <summary>スクショ用: 選手交代モーダルを閉じる。</summary>
        public void CloseSubstitutionForCapture() => _subPanel?.Close();

        /// <summary>スクショ用: 攻撃タブを選ぶ（0=代打 1=代走）。守備中はタブ無し＝守備の割当2ペイン（issue #256）。</summary>
        public void SelectSubstitutionKindForCapture(int index) => _subPanel?.SelectKindForCapture(index);

        /// <summary>スクショ用: 守備の割当枠を守備位置で選ぶ（issue #452 プレビュー撮影用）。</summary>
        public void PickSubstitutionSlotForCapture(KokoSim.Engine.Match.Field.FieldPosition pos) => _subPanel?.PickSlotForCapture(pos);

        /// <summary>スクショ用: 控え一覧の n 番目を選ぶ（行内プレビューを出す。issue #452）。</summary>
        public void PickSubstitutionCandidateForCapture(int index) => _subPanel?.PickCandidateForCapture(index);

        /// <summary>スクショ用: 代走に出す走者を選ぶ（負値＝先頭の走者。issue #452）。</summary>
        public void PickSubstitutionRunnerForCapture(int baseIndex) => _subPanel?.PickRunnerForCapture(baseIndex);

        /// <summary>スクショ用: 自校の控え先頭（代打候補）の名前。</summary>
        public string HomeBenchZeroName =>
            _homeTeam != null && _homeTeam.Reserves.Count > 0 ? _homeTeam.Reserves[0].Name : "";

        /// <summary>スクショ用: ライブ観戦の「ホームへ戻る」を実行する（終局後のみ有効）。</summary>
        public void BackHomeForCapture() => OnBackHome();

        /// <summary>
        /// スクショ用: イニング間のスコア画面を強制的に出す。
        /// CaptureMode では実進行が半回をまたいでも自動では出さない（撮影を暗幕で潰さないため）ので、
        /// 撮りたいときだけこれで開く。<see cref="HideInningBreakForCapture"/> で閉じる。
        /// </summary>
        public void ShowInningBreakForCapture()
        {
            if (_inningBreak == null || _current == null) return;
            if (_ibCaption != null)
            {
                // 直前に終わった半回＝いまの打席のひとつ前。表裏を反転して見出しにする。
                var inning = _current.IsTop ? _current.Inning - 1 : _current.Inning;
                if (inning < 1) inning = 1;
                _ibCaption.text = inning + "回" + (_current.IsTop ? "裏" : "表") + " 終了";
            }
            PostLineScore(_current.Inning, _current.IsTop, finished: false);
            SetVisible(_inningBreak, true);
        }

        /// <summary>スクショ用: イニング間のスコア画面を閉じる。</summary>
        public void HideInningBreakForCapture() => SetVisible(_inningBreak, false);

        /// <summary>スクショ用: 「ホームへ戻る」ボタンが表示中か（終局＋ライブ観戦モードで true）。</summary>
        public bool BackHomeVisible => _backHome != null && _backHome.style.display.value == DisplayStyle.Flex;

        /// <summary>スクショ用: 現在ロード中の打席を任意時刻 t で静止表示する（守備の動き確認用）。</summary>
        public void SeekForCapture(double t)
        {
            _stage = LiveStage.Idle;
            _inPitchPhase = false;
            if (_current?.Play == null) return;
            SetOuts(_current.OutsBefore);
            SetBases(_current.BaseFirstBefore, _current.BaseSecondBefore, _current.BaseThirdBefore);
            _view.SetPlay(_current.Play);
            for (var s = 0.0; s < t; s += 0.03) _view.SetTime(s);
            _t = t;
            _view.SetTime(t);
            if (_caption != null) _caption.text = PlaybackEvaluator.CaptionAt(_current.Play, t);
        }

        /// <summary>スクショ用: 現在打席の再生尺（秒）。守備の動きを尺の途中で撮るのに使う。</summary>
        public double CurrentPlayDuration => _current?.Play?.Dur ?? 0.0;

        /// <summary>スクショ用: 現在ロード中の打席を結果時点で静止表示する。</summary>
        public void FreezeCurrentAtResult()
        {
            _stage = LiveStage.Idle;
            _inPitchPhase = false;
            if (_current == null) return;

            // BSO を解決状態へ: 最終カウント＋アウト（打席前）＋塁ダイヤ（結果）。
            var pitches = _current.PitchSeq?.Pitches;
            if (pitches != null && pitches.Count > 0)
            {
                var last = pitches[pitches.Count - 1];
                SetCount(last.BallsAfter, last.StrikesAfter);
            }
            SetOuts(_current.OutsBefore);
            SetBases(_current.BaseFirstAfter, _current.BaseSecondAfter, _current.BaseThirdAfter);
            // 掲示（ラインスコア＋ScoreBug）も結果時点へ。Play なしの打席は下で早期 return するので、
            // ここで掲げないと三振・四球で終わった打席のスクショだけ回と得点が前のまま写る。
            UpdateScoreboard(_current, resolved: true);

            // Play なし（三振・四球）は結果時点の塁状況で守備陣＋走者を静止表示（EnterBattedBallOrHold と同じ後状態）。
            if (_current.Play == null)
            {
                _view.SetResting(_current.BaseFirstAfter, _current.BaseSecondAfter, _current.BaseThirdAfter);
                return;
            }
            _view.SetPlay(_current.Play);
            PrepareDisplayTimings(_current);
            // 「結果時点」＝表示同期後は結果テキストが出る時刻（打者セーフは到達）まで進める（issue #438）。
            var freezeAt = System.Math.Max(_current.Play.ResAt, _resultShowAt) + 0.4;
            for (var s = 0.0; s < freezeAt; s += 0.03) _view.SetTime(s);
            _t = freezeAt;
            RenderReplayFrame();
        }
    }
}
