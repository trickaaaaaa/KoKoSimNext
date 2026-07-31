using System.Collections.Generic;
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

        [SerializeField] private ulong gameSeed = 20260718UL;
        [SerializeField] private string awayName = "北都大付属";
        [SerializeField] private string homeName = "桜丘";
        [Tooltip("タイムラインを持たない打席（三振・四球）の表示時間[秒]。")]
        [SerializeField] private float noPlayHoldSeconds = 1.2f;
        [Tooltip("投球フェーズの1球あたり表示時間[秒]（×0.5/×2/×4速度に連動）。投球飛翔0.45秒より長くし、着弾後の一呼吸を確保する（issue #135）。")]
        [SerializeField] private float pitchIntervalSeconds = 1.0f;
        [Tooltip("打席の頭に置く采配の間[秒]（設計書15 §8.3-①）。打者を見てサインを出す間合い。倍速で一緒に縮む。")]
        [SerializeField] private float paHeadDwellSeconds = 0.6f;
        [Tooltip("対決ビュー表示中のリリース間隔[秒]（design-18 §7-2）。投球（セットポジション）1.3秒＋フォロースルー1.5秒が切り貼りなしで収まる長さにする。判定表示のタイミングは pitchIntervalSeconds のまま＝次の投球だけ遅れる。")]
        [SerializeField] private float duelPitchIntervalSeconds = DuelStageRig.DefaultPitchInterval;
        [Tooltip("1球ごとの判定オーバーレイ（ストライク/ボール/ファール＋球速球種）の表示時間[秒]。")]
        [SerializeField] private float pitchCallHoldSeconds = 0.35f;
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
        // カウントは ScoreBug の数字マス（ランプではない）。塁は ScoreBug の菱形。
        private Label _sbugBalls, _sbugStrikes, _sbugOuts;
        private VisualElement[] _bases;
        private int _pitchIdx;
        private float _pitchClock;
        private bool _inPitchPhase;   // 投球フェーズ（B/S点灯）中か。false なら打球フェーズ/静止。
        private bool _pitchBallVisible;   // 投球フェーズで今の1球の投球軌道を流しているか（#5）。
        private bool _pitchCallDone;      // 今の1球の判定表示＋カウント更新を済ませたか（対決ビュー中は次リリースと分離）。
        private bool _pitchFirstPending;  // 対決ビューの初球ワインドアップ待ち（時計が0に達したらBeginPitchBall(0)）。
        // ファウル打球の俯瞰表示（issue #313）。非nullの間＝今の1球がファウル打球の再生中で、値＝着地まで
        // 保持すべき最短表示秒（次の1球へ切り替える interval をこの秒数まで延ばす）。
        private float? _foulFlightHoldSeconds;
        // BeginPitchBall が仕込んだ「この1球のファウル打球タイムライン」。pitchIntervalSeconds 到達時に
        // Update() 側で表示へ差し替える（投球セグメントの二重表示を避けるため, issue #313）。
        private PlaybackPlay _pendingFoulFlight;

        // 対決ビューの1球ペーシング（フル振りかぶり再生）を使うか（design-18 §7-2）。
        private bool DuelPacingActive => _duelOn && _duelRig != null && _duelRig.Available;

        // 1球ごとの判定オーバーレイ（issue #6）。判定＋球速球種を盤面上に大きく短時間だけ出す。
        // 球速・球種は実1球記録がある打席だけ（合成投球列は null＝出さない）。表示専用。
        private VisualElement _pitchCall;
        private Label _pitchCallJudge, _pitchCallDetail;
        private float _pitchCallClock;
        private bool _pitchCallOn;

        // 得点時の大型スコアオーバーレイ（issue #60）。加点前スコア→1点ずつの加算アニメ→加点後スコアを
        // 自前クロックで演出する（PitchCallと同じ「Update()の専用クロックで寿命管理」方式）。表示専用。
        private VisualElement _scoreCall;
        private Label _scoreCallOwnName, _scoreCallOwnNum, _scoreCallOppNum, _scoreCallOppName;
        private float _scoreCallClock;
        private bool _scoreCallOn;
        private int _scoreCallOwnScore, _scoreCallOppScore, _scoreCallRunsRemaining;
        private bool _scoreCallOwnScoring;

        // 交代ポップアップ（issue #209 表現）。継投・投野交代・野手登板が起きた瞬間に数秒だけ盤面へ浮かせる。
        // PitchCall/ScoreCall と同じ「Update() の専用クロックで寿命管理」方式。表示専用。
        [SerializeField] private float subCallHoldSeconds = 2.6f;
        private VisualElement _subCall;
        private Label _subCallTag, _subCallText;
        private float _subCallClock;
        private bool _subCallOn;
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
            _sbugBalls = _root.Q<Label>("sbug-balls");
            _sbugStrikes = _root.Q<Label>("sbug-strikes");
            _sbugOuts = _root.Q<Label>("sbug-outs");
            _bases = new[] { _root.Q<VisualElement>("sbug-base-1"), _root.Q<VisualElement>("sbug-base-2"), _root.Q<VisualElement>("sbug-base-3") };

            _pitchCall = _root.Q<VisualElement>("pitch-call");
            _pitchCallJudge = _root.Q<Label>("pitch-call-judge");
            _pitchCallDetail = _root.Q<Label>("pitch-call-detail");

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
            if (!CaptureMode) StartAutoRun();
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
            _windowClock = _windowHold = _windowCarry = 0f;
            _displayedLogCount = 0;
            _prog = null;
            _homeTeam = null;
            _t = 0;
            _speed = 1f;
            _pitchIdx = 0;
            _pitchClock = 0f;
            _inPitchPhase = false;
            _pitchBallVisible = false;
            _pitchCallClock = 0f;
            _pitchCallOn = false;
            _scoreCallClock = 0f;
            _scoreCallOn = false;
            _subCallClock = 0f;
            _subCallOn = false;
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
            // RosterTeamBuilder.Build は控えを設定しないため、代打采配のデモ用に控えを付与する。
            home = home with { Bench = BuildBench() };
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
                    GroupLabel = "打撃", OnSelect = () => _pitchBattingChoice = PitchBattingOverride.ForceSwing,
                    OnClear = () => _pitchBattingChoice = null },
                new SignPad.SignDef { Id = "batting-take", Label = "待て", Icon = "待", Offense = true, Group = "batting",
                    GroupLabel = "打撃", OnSelect = () => _pitchBattingChoice = PitchBattingOverride.ForceTake,
                    OnClear = () => _pitchBattingChoice = null },
                // ── 攻撃: 仕掛け（1手・走者が要る）──
                new SignPad.SignDef { Id = "batting-bunt", Label = "バント", Icon = "犠", Offense = true, Group = "batting",
                    GroupLabel = "打撃", EnabledIf = () => RunnerOnAnyBase,
                    OnSelect = () => _pitchBattingChoice = PitchBattingOverride.Bunt,
                    OnClear = () => _pitchBattingChoice = null },
                new SignPad.SignDef { Id = "batting-squeeze", Label = "スクイズ", Icon = "決", Offense = true, Group = "batting",
                    GroupLabel = "打撃", EnabledIf = () => RunnerOnThird,
                    OnSelect = () => _pitchBattingChoice = PitchBattingOverride.Squeeze,
                    OnClear = () => _pitchBattingChoice = null },
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

        // 守備の常時方針は4項目まとめて1本の窓口へ渡すので、各カードは自分の項目だけ差し替えて押し直す。
        // null を渡す＝その項目の解除（委任采配の判断に戻る）。
        private void PushDefPolicy(PitchPolicy? v) { _defPolicy = v; PushDefense(); }
        private void PushDefGear(PitcherGear? v) { _defGear = v; PushDefense(); }
        private void PushDefInfield(DefenseDepth? v) { _defInfield = v; PushDefense(); }
        private void PushDefOutfield(DefenseDepth? v) { _defOutfield = v; PushDefense(); }

        private void PushDefense()
            => _prog?.SetStandingDefense(_managerIsAway, _defPolicy, _defGear, _defInfield, _defOutfield);

        // これから投げる1球へ、①の「1手」だけを予約する（無指示=null なら従来と完全一致）。
        // ②の方針は押した時点で入っているのでここでは触らない。
        private void PushPitchTacticsChoice()
        {
            // 自校が攻撃していない球へは攻撃の1手を渡さない。渡すと自校の予約として残り、
            // **次に自校が攻撃する回の初球で不意に発動する**。
            var offense = _atBat != null
                ? _atBat.IsTop == _managerIsAway              // 1球進行: いま止まっている窓の攻守
                : _prog.IsTeamOnOffense(_managerIsAway);      // 一括経路（撮影ハーネス）
            _prog.SetPitchBattingOverride(_managerIsAway, offense ? _pitchBattingChoice : null);
            _prog.SetOffensiveSign(_managerIsAway, offense ? _signChoice : null);
            _prog.SetStealAttempt(_managerIsAway, offense ? _stealChoice : null);
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
            _inPitchPhase = true;
            _stage = LiveStage.Pitch;
            var carry = _windowCarry;   // 前の球・窓で余った端数は持ち越す（球間の間合いが倍速でずれないように）
            _windowCarry = _windowClock = _windowHold = 0f;

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
            var isFielder = kind != KokoSim.Engine.Match.Game.PitchingChangeKind.BullpenRelief;
            _subCallTag.text = kind == KokoSim.Engine.Match.Game.PitchingChangeKind.BullpenRelief ? "投手交代"
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
                case KokoSim.Engine.Match.Game.PitchingChangeKind.FielderToMound:
                    return half + " " + c.InPitcherName + " が登板（" + c.OutPitcherName + " → 退く）";
                default:
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
        // （ファウルは pitchIntervalSeconds 到達時に Update() 側で差し替える, issue #313）。
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
                _duelRig.OnPitchReleased(isLast ? -1f : duelPitchIntervalSeconds, outcome,
                    FlightOf(pt), BattedOf(pt, isLast));
            }
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
        /// </summary>
        private static DuelPitchFlight? FlightOf(KokoSim.Engine.Match.Timeline.Playback.PitchToken pt)
        {
            if (pt.LocationX is not { } x || pt.LocationY is not { } y) return null;
            var tr = pt.Trajectory;
            return new DuelPitchFlight(
                (float)x, (float)y,
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
                // 対決ビューが打球を見せていた秒数は SetDuelViewAuto(false)→HideBall で消えるので先に採る
                var shown = wasDuelOn ? (_duelRig?.BattedElapsedSeconds ?? -1f) : -1f;
                SetDuelViewAuto(false);   // インプレー打球は打った瞬間から俯瞰で行方を見せる（issue #312）
                _view.SetPlay(_current.Play);
                // 対決ビューから来た場合は投球が二重に見えないよう接触の瞬間から再生する（issue #330）。
                // さらに対決ビューで打球を見せたぶんは飛ばす（同じ0.5秒を二度描かない。2026-07-31）。
                // 俯瞰固定（手動OFF）中は対決ビュー側で1球も見せていないので、従来どおり投球から流す。
                if (wasDuelOn) _t = ContactMomentOf(_current.Play, shown);
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

        private void Update()
        {
            // ×4速時は酔い防止のため自動で全景固定へ（Issue #119 完了条件）。毎フレーム同期するだけで十分軽量。
            _view.CameraWorkEnabled = _cameraWorkWanted && _speed < 4f;
            // 判定オーバーレイ（#59）の表示窓はプレー時間軸で数えるので、倍速を渡して実時間の見え方を保つ（#236 症状A）。
            _view.PlaybackSpeed = _speed;

            // 対決ビューはポーズを12fps量子化で進める（タイム中断中は盤面と同様に凍結）。
            if (!_paused && _duelOn) _duelRig?.Tick(Time.deltaTime * _speed);

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
            if (_pitchCallOn)
            {
                _pitchCallClock += Time.deltaTime * _speed;
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

            // 采配窓: 打者を映したまま指示を待つ。打席の頭だけ短い間を置き（設計書15 §8.3-①）、
            // 打席の途中は球間そのものが窓なので待たずに次の1球へ抜ける。倍速で間も一緒に縮む。
            if (_stage == LiveStage.Window)
            {
                _windowClock += Time.deltaTime * _speed;
                if (_windowClock < _windowHold) return;
                _windowCarry = _windowClock - _windowHold;
                ThrowFromWindow();
                return;
            }

            if (_stage != LiveStage.Pitch && _stage != LiveStage.Play) return;

            if (_inPitchPhase)
            {
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
                // 判定＋カウント更新はリリース+pitchIntervalSeconds（従来タイミング）。
                // 次のリリースだけ対決ビュー中は duelPitchIntervalSeconds まで遅らせ、フル振りかぶりを収める。
                if (!_pitchCallDone && _pitchClock >= pitchIntervalSeconds)
                {
                    _pitchCallDone = true;
                    var q = _paQueue[_pitchIdx];
                    var pt = q.Token;
                    SetCount(pt.BallsAfter, pt.StrikesAfter);
                    // 1球ごとの観測があるときは、その球を投げ終えた実状態でアウト・塁も動かす
                    //（打席途中の盗塁・牽制死がその場で絵に出る。合成投球列には無いので触らない）。
                    if (q.Live != null)
                    {
                        SetOuts(q.Live.Outs);
                        SetBases(q.Live.BaseFirst, q.Live.BaseSecond, q.Live.BaseThird);
                    }
                    ShowPitchCall(pt);
                    if (IsTerminalPitch(_pitchIdx))
                    {
                        EnterBattedBallOrHold();   // 打席を終わらせた球は判定直後に打球再生／結果保持へ
                        return;
                    }
                    if (_pendingFoulFlight != null)
                    {
                        // ファウルでも打球は飛ぶので、打った瞬間から俯瞰へ切り替えて行方を見せる（issue #313）。
                        // 復帰（対決ビューへ）は打球が地面へ着いてから＝次の1球のワインドアップに合わせる。
                        var wasDuelOn = _duelOn;
                        var shownFoul = wasDuelOn ? (_duelRig?.BattedElapsedSeconds ?? -1f) : -1f;
                        SetDuelViewAuto(false);
                        _view.SetPlay(_pendingFoulFlight);
                        _foulFlightHoldSeconds = (float)_pendingFoulFlight.Dur;
                        _pitchBallVisible = true;
                        // 対決ビューから来た場合は接触の瞬間から（投球セグメントの二重表示を避ける,
                        // issue #330）＋対決ビューで見せた打球のぶんは飛ばす。
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

                    if (_foulFlightHoldSeconds.HasValue)
                    {
                        _foulFlightHoldSeconds = null;
                        SetDuelViewAuto(true);   // ファウル打球が地面へ着いたら対決ビューへ復帰（issue #313）
                    }
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

        // 再生を打ち切る時刻。アウトで走者も得点も無く「次のプレイに発展しようがない」場合は、
        // 決着＋最後の送球直後で切って次打席へ（野手が定位置へ戻る余韻を長々見せない）。
        private double PlayEndTime()
        {
            var play = _current.Play;
            var noRunnersAfter = !_current.BaseFirstAfter && !_current.BaseSecondAfter && !_current.BaseThirdAfter;
            var deadOut = _current.Result == KokoSim.Engine.Match.AtBat.PlateAppearanceResult.InPlayOut
                          && _current.RunsScored == 0 && noRunnersAfter;

            var end = play.Dur;
            if (deadOut)
            {
                var lastEvent = play.ResAt;
                foreach (var seg in play.Ball) if (seg.T1 > lastEvent) lastEvent = seg.T1;
                end = System.Math.Min(play.Dur, lastEvent + 0.6);
            }

            // 判定オーバーレイ（#59）の表示窓 [T1, T1+hold) を必ず含める（issue #236 症状A）。
            // 内野ゴロの打者走者は「送球到達＝アウト成立」より後に一塁へ着く（T1＞ResAt）ので、
            // 早切りだけだと最頻のアウトが一度も出ない。Dur でクランプしない（SetTime は t の純関数で、
            // Dur を超えた時刻を評価しても最終フレームを保持するだけ）。
            var judged = PlaybackEvaluator.LastJudgementTime(play);
            if (judged.HasValue) end = System.Math.Max(end, judged.Value + _view.JudgementHoldSeconds);
            return end;
        }

        private string NextPrompt() =>
            "采配を選んで次の打席へ（" + _current.Inning + "回" + (_current.IsTop ? "表" : "裏") + "）";

        private void RenderReplayFrame()
        {
            var play = _current.Play;
            _view.SetTime(_t);
            if (_caption != null) _caption.text = PlaybackEvaluator.CaptionAt(play, _t);
            var resolved = _t >= play.ResAt;
            if (_result != null)
            {
                _result.text = play.Result;
                _result.style.display = resolved ? DisplayStyle.Flex : DisplayStyle.None;
            }
            UpdateScoreboard(_current, resolved);
        }

        // ── ラインスコア（LineScorePanel。設計書16 §4-2） ──
        // 数値はすべてエンジンの観測データ（MatchLiveSnapshot.AwayLine/HomeLine）から引き、UI側で得点を組み立てない。
        // resolved=false（打席の演出中）は掲示せず、確定した瞬間に1回だけ掲げる＝実物の掲示板と同じ運用。
        private void UpdateScoreboard(LivePlateAppearance item, bool resolved)
        {
            if (!resolved) { _lineScorePosted = false; return; }
            if (_lineScorePosted) return;
            _lineScorePosted = true;
            PostLineScore(item.Inning, item.IsTop, finished: false);
            if (item.RunsScored > 0) BeginScoreCall(item);
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
        // ScoreBug の左袖はランプではなく数字なので、点灯トグルではなく数字を差し替える。
        // 表示は必ず掲示板の範囲へ丸める: 四球の最終球は BallsAfter=4、三振の最終球は StrikesAfter=3 で
        // 渡ってくるが、実物の掲示板に 4 ボールは出ない（打席が終わって 0-0 に戻る）。
        // ランプ時代は玉数が足りず自然に頭打ちだったので、数字にした今は明示的に丸める。
        private void SetCount(int balls, int strikes)
        {
            if (_sbugBalls != null) _sbugBalls.text = Mathf.Clamp(balls, 0, 3).ToString();
            if (_sbugStrikes != null) _sbugStrikes.text = Mathf.Clamp(strikes, 0, 2).ToString();
        }

        private void SetOuts(int outs)
        {
            if (_sbugOuts != null) _sbugOuts.text = Mathf.Clamp(outs, 0, 2).ToString();
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
            _pitchCallJudge.text = judge;
            _pitchCallJudge.style.display = judge.Length == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            _pitchCallJudge.EnableInClassList("pitch-call__judge--strike", IsStrikeCall(pt.Kind));
            _pitchCallJudge.EnableInClassList("pitch-call__judge--ball", pt.Kind == KokoSim.Engine.Match.AtBat.PitchKind.Ball);

            var detail = "";
            if (_pitchCallDetail != null)
            {
                detail = PitchDetailText(pt);
                _pitchCallDetail.text = detail;
                _pitchCallDetail.style.display = detail.Length == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            }

            if (judge.Length == 0 && detail.Length == 0) { HidePitchCall(); return; }

            _pitchCall.style.display = DisplayStyle.Flex;
            _pitchCallOn = true;
            _pitchCallClock = 0f;
        }

        private void HidePitchCall()
        {
            _pitchCallOn = false;
            _pitchCallClock = 0f;
            if (_pitchCall != null) _pitchCall.style.display = DisplayStyle.None;
        }

        // ── 得点時の大型スコアオーバーレイ（issue #60） ──
        // 並びは自校－相手校固定（先攻/後攻に依存しない）。加点前スコアを表示してから、得点数ぶん
        // 1点ずつ加算する演出を挟み、最終スコアで一呼吸おいて自動的に消える。加算タイミングは各走者の
        // 本塁生還時刻ではなく「判定確定後に等間隔で連続再生」（PlaybackRunに生還/アウトの意味フラグが
        // 無く走者ごとの生還検知には新規実装が要るため、描画専用スコープで低リスクな方を選んだ）。
        private void BeginScoreCall(LivePlateAppearance item)
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

            if (_scoreCallOwnName != null) _scoreCallOwnName.text = ownIsAway ? _awayDisp : _homeDisp;
            if (_scoreCallOppName != null) _scoreCallOppName.text = ownIsAway ? _homeDisp : _awayDisp;
            RefreshScoreCallLabels();
            _scoreCall.style.display = DisplayStyle.Flex;
        }

        private void AdvanceScoreCall(float dt)
        {
            _scoreCallClock += dt;
            if (_scoreCallRunsRemaining > 0)
            {
                if (_scoreCallClock < scoreCallStepSeconds) return;
                _scoreCallClock -= scoreCallStepSeconds;
                if (_scoreCallOwnScoring) _scoreCallOwnScore++; else _scoreCallOppScore++;
                _scoreCallRunsRemaining--;
                RefreshScoreCallLabels();
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

        // 判定の内訳（見逃し/空振り）＋球種＋球速。球種・球速は実1球記録がある打席だけ出す。
        private static string PitchDetailText(PitchToken pt)
        {
            var note = pt.Kind == KokoSim.Engine.Match.AtBat.PitchKind.CalledStrike ? "見逃し"
                : pt.Kind == KokoSim.Engine.Match.AtBat.PitchKind.SwingingStrike ? "空振り" : "";
            var pitch = pt.PitchType.HasValue ? PitchJp(pt.PitchType.Value) : "";
            var velo = pt.VelocityKmh.HasValue
                ? Mathf.RoundToInt((float)pt.VelocityKmh.Value).ToString() + "km/h" : "";

            var s = note;
            if (pitch.Length > 0) s = s.Length > 0 ? s + "　" + pitch : pitch;
            if (velo.Length > 0) s = s.Length > 0 ? s + " " + velo : velo;
            return s;
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
            }
            _duelImg.Rig = _duelRig;   // カラム実寸に合わせたRT適応リサイズの結線
            _duelImg.Bind(_duelRig.Target);
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
            // 1球進行では采配窓の対戦（＝いまの打者・投手）が単一ソース。一括経路では確定打席から引く。
            Handedness bats, throws;
            if (_atBat is { } at) { bats = at.BatterBats; throws = at.PitcherThrows; }
            else if (_current != null) { bats = _current.BatterBats; throws = _current.PitcherThrows; }
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
            _duelBtn?.EnableInClassList("chip-btn--on", _duelOn);
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
                // 控え（ベンチ入り＝背番号ありのメンバのみ。ベンチ外は出さない）。野手控え＋ブルペンを
                // 背番号の昇順で1リストに並べる（種別で分けず、上から番号順）。
                var benchRows = new List<(int Number, VisualElement Row)>();
                foreach (var b in team.Bench) if (b.Number > 0) benchRows.Add((b.Number, BuildBenchRow(b, mirror)));
                foreach (var bp in team.Bullpen) if (bp.Number > 0) benchRows.Add((bp.Number, BuildBenchPitcherRow(bp, mirror)));
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
            row.Add(MakeLabel(p.Pitches + "球", "mlineup-pitcher__stat"));
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

            _hudPitcherSlot.Add(HudPanel(BuildHudPitcher(defPitcher, mu)));
            _hudBatterSlot.Add(HudPanel(BuildHudBatter(batterToday, mu, offenseIsTop)));
        }

        private static VisualElement HudPanel(VisualElement block)
        {
            var panel = new VisualElement { pickingMode = PickingMode.Ignore };
            panel.AddToClassList("mhud-panel");
            panel.Add(block);
            return panel;
        }

        private VisualElement BuildHudPitcher(LivePitcherToday def, Matchup m)
        {
            var block = new VisualElement();
            block.AddToClassList("mup-block");
            block.AddToClassList("mup-block--pitcher");

            // 1行目＝登板順・背番号・名前・学年（左右投は3Dの絵で分かるので出さない）。
            var line1 = MakeRow("mup-line");
            var relief = MakeLabel(MatchLiveStatsProvider.ReliefLabel(def.ReliefOrder), "mup-role");
            if (def.ReliefOrder > 1) relief.AddToClassList("mup-role--relief");
            line1.Add(relief);
            line1.Add(MakeLabel(NumText(m.PitcherNumber), "num-badge", "num-badge--sm", "f-num"));
            line1.Add(MakeLabel(m.PitcherName, "mup-name"));
            if (def.Grade > 0) line1.Add(MakeLabel(def.Grade + "年", "mup-grade"));
            block.Add(line1);

            // 2行目＝通算防御率・本日の奪三振・球数。球数は独立行にすると縦4行になって嵩むので
            // 同じ「今日の投球」の並びへ寄せた（2026-07-31 ユーザー判断）。
            var line2 = MakeRow("mup-line");
            line2.Add(MakeLabel("防 " + MatchLiveStatsProvider.Era(_statsProvider.CareerPitching(m.PitcherSourceId))
                                    + "　奪三振 " + def.StrikeOuts, "mup-career"));
            line2.Add(MakeLabel("球数", "mup-pitches__lab", "mup-pitches__lab--gap"));
            var pc = MakeLabel(def.Pitches.ToString(), "mup-pitches", "f-num");
            if (def.Pitches > 100) pc.AddToClassList("mup-pitches--alert");
            else if (def.Pitches > 80) pc.AddToClassList("mup-pitches--warn");
            line2.Add(pc);
            line2.Add(MakeLabel("球", "mup-pitches__lab"));
            block.Add(line2);

            block.Add(BuildStaminaBar(def.StaminaRemaining));
            return block;
        }

        // スタミナ残量のバー（継投の目安）。球数の数字だけでなく形でも「そろそろ」を出す。
        // 色は残量で落とす: 6割超=良好 / 3〜6割=注意 / 3割未満=警告（盤面ビューの状態コード）。
        private static VisualElement BuildStaminaBar(double remaining)
        {
            var row = MakeRow("mup-line", "stam");
            row.Add(MakeLabel("スタミナ", "stam__lab"));

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
            row.Add(MakeLabel(Mathf.RoundToInt((float)(pct * 100.0)) + "%", "stam__pct", "f-num"));
            return row;
        }

        private VisualElement BuildHudBatter(LiveBatterSlot batterToday, Matchup m, bool offenseIsTop)
        {
            var block = new VisualElement();
            block.AddToClassList("mup-block");
            block.AddToClassList("mup-block--batter");

            // 1行目＝放送と同じ並び「4番 三塁 [5] 中村 陽介 2年 (調子)」。左右打は3Dの絵で分かるので出さない。
            var line1 = MakeRow("mup-line");
            line1.Add(MakeLabel(m.BatterOrder.ToString(), "mup-order", "f-num"));
            line1.Add(MakeLabel("番", "mup-order__unit"));
            var pos = MatchLiveStatsProvider.PosJp(batterToday?.Position ?? m.BatterPosition);
            if (pos.Length > 0) line1.Add(MakeLabel(pos, "mup-pos"));
            line1.Add(MakeLabel(NumText(m.BatterNumber), "num-badge", "num-badge--sm", "f-num"));
            line1.Add(MakeLabel(m.BatterName, "mup-name"));
            if (batterToday is { Grade: > 0 }) line1.Add(MakeLabel(batterToday.Grade + "年", "mup-grade"));
            var face = new ConditionFace();
            face.AddToClassList("mup-cond");
            face.Set(_statsProvider.ConditionOf(m.BatterSourceId, m.BatterConditionValue, m.BatterName));
            line1.Add(face);
            block.Add(line1);

            var b = _statsProvider.CareerBatting(m.BatterSourceId);
            var career = b == null
                ? "通算 —"
                : MatchLiveStatsProvider.Avg(b) + " / " + b.HomeRuns + "本 / " + b.Rbi + "点";
            var line2 = MakeRow("mup-line");
            line2.Add(MakeLabel(career, "mup-career"));
            block.Add(line2);

            var pa = BuildTodayPlateAppearances(m, offenseIsTop);
            if (pa != null) block.Add(pa);
            return block;
        }

        /// <summary>
        /// 本日の打撃成績（この打者がここまでに終えた打席の略記を並べる）。出塁した打席だけ点灯させる。
        /// <b>まだ絵にしていない打席は必ず外す</b>（<see cref="_displayedLogCount"/> までしか見ない）——
        /// 入れると、盤面で再生する前に結果を先に見せてしまう。
        /// </summary>
        private VisualElement BuildTodayPlateAppearances(Matchup m, bool offenseIsTop)
        {
            var log = _prog?.PlayLog;
            if (log == null) return null;
            var upTo = Mathf.Min(_displayedLogCount, log.Count);
            if (upTo <= 0) return null;

            var row = MakeRow("mup-line", "mup-pa-row");
            row.Add(MakeLabel("打席", "mup-pa__lab"));
            var any = false;
            for (var i = 0; i < upTo; i++)
            {
                var e = log[i];
                // 同じ打者の打席だけ拾う。代打で打順枠の中身が入れ替わるので名前まで見る
                // （BoxScoreGrid.Build が使うキーと同じ組み合わせ）。
                if (e.IsTop != offenseIsTop || e.BatterOrder != m.BatterOrder
                    || e.BatterName != m.BatterName) continue;
                var chip = MakeLabel(MatchLiveStatsProvider.ScorebookAbbr(e), "mup-pa");
                if (MatchLiveStatsProvider.Reached(e.Result)) chip.AddToClassList("mup-pa--on");
                row.Add(chip);
                any = true;
            }
            return any ? row : null;
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

        /// <summary>スクショ用: いま止まっている采配窓の局面（回・表裏・カウント・塁）。無ければ null。</summary>
        public LiveAtBat CurrentAtBatForCapture => _atBat;

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

        /// <summary>スクショ用: 自校の控え先頭（代打候補）の名前。</summary>
        public string HomeBenchZeroName =>
            _homeTeam != null && _homeTeam.Bench.Count > 0 ? _homeTeam.Bench[0].Name : "";

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
            for (var s = 0.0; s < _current.Play.ResAt + 0.4; s += 0.03) _view.SetTime(s);
            _t = _current.Play.ResAt + 0.4;
            RenderReplayFrame();
        }
    }
}
