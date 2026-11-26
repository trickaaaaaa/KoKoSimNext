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
            /// <summary>
            /// 両校の学校ID（issue #634）。成績の帰属キー (校ID, 選手ID) の校側で、通算成績を
            /// 自校ストアと全国ストアのどちらから引くかを決める。<b>試合中に変わらない同定情報</b>なので
            /// 観測データ（<see cref="MatchLiveSnapshot"/>）には載せず、チーム配色と同じ静的経路で渡す。
            /// 不明（デモ・練習試合の相手など）は <c>int.MinValue</c>＝どちらの校とも一致しない＝成績なし。
            /// </summary>
            public int AwaySchoolId = int.MinValue;
            public int HomeSchoolId = int.MinValue;
            public int ManagerTacticalSense = 70;
            /// <summary>
            /// 勝者が要る文脈か（大会戦＝true）。結果画面で延長上限の引き分けを「引き分け（再試合）」と
            /// 明示するかに使う（issue #253）。練習試合は引き分けで終わるので false（issue #269）。
            /// </summary>
            public bool RematchOnTie = true;
            /// <summary>
            /// 観客の入り（issue #705・表示専用）。大会と回戦から <see cref="KokoSim.Engine.Nation.Tournaments.CrowdTurnouts"/> で決める。
            /// 試合中に変わらないので配色と同じ静的経路で渡す（観測データには載せない）。
            /// </summary>
            public KokoSim.Engine.Nation.Tournaments.CrowdTurnout Crowd = KokoSim.Engine.Nation.Tournaments.CrowdTurnout.Full;
            /// <summary>
            /// アルプス席のブラスバンド応援を流すか（表示専用）。甲子園の試合だけ真（<see cref="KokoSim.Unity.Audio.MatchBrassBand"/>）。
            /// </summary>
            public bool BrassBand;
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

        /// <summary>
        /// 【開発用・エディタ限定】デモ試合の乱数シードの差し替え口。1試合に出るプレーの取り合わせは
        /// シードで決まるので、録画ハーネスが「1試合で揃わなかった種類」を別の試合で拾い直すために使う。
        /// 既定 null＝<c>gameSeed</c> のまま＝従来と完全一致。
        /// </summary>
        public static ulong? GameSeedOverrideForCapture;

        /// <summary>
        /// 【開発用・エディタ限定】デモ試合で監督が持つ側の差し替え口。バント・盗塁は**監督側の采配**なので、
        /// 監督側が塁に出ない試合では一度も撮れない（実測: 自校が3回で0-18のとき走者が出ずじまい）。
        /// 撮影ハーネスだけが強い側へ回って絵を作る。既定 null＝後攻(home)＝従来と完全一致。
        /// </summary>
        public static bool? ManagerIsAwayOverrideForCapture;

        /// <summary>
        /// 【開発用・エディタ限定】デモ試合で監督側の校IDを自校（<see cref="NationService.ManagerSchoolId"/>）にする口。
        /// デモは校IDを持たない＝両校とも「相手校」扱いになり、調子が全員推定表示になる。実試合と同じ
        /// 「自校＝真値・相手＝推定」の見え方を撮るときだけ真にする。既定 false＝従来と完全一致。
        /// </summary>
        public static bool ManagerSchoolIdForCapture;

        /// <summary>
        /// 【開発用・エディタ限定】デモ試合の自校（home）の組み方の差し替え口。デモは能力順の自動編成
        /// （<c>RosterTeamBuilder.Build</c>）で組むので**必ず非DH**になり、DH制でしか出ない操作
        /// （DH解除・DH下の投野交代）が絵にも一巡確認にも一度も出てこない（issue #452 の残件）。
        /// ハーネスだけが本番と同じ経路（スタメン設定→<c>LineupSpec</c>→<c>BuildFromLineup</c>）で組んだ
        /// チームを差す。既定 null＝従来と完全一致。
        /// </summary>
        public static System.Func<KokoSim.Engine.Match.Game.Team> HomeTeamOverrideForCapture;

        /// <summary>
        /// 【開発用・エディタ限定】デモ試合を <c>data/debug/scenarios.yaml</c> の場面から始める差し替え口（issue #647）。
        /// 場面ジャンプ（設計書17 §3.4）と同じ <c>ScenarioBuilder</c> で組み、シナリオの <c>force</c> を最初の打席へ予約する
        /// ＝撮りたい場面（ゲームセットの瞬間など）を確実に作る。"player" 側はデモの自校で解決する。既定 null＝従来と完全一致。
        /// </summary>
        public static string ScenarioIdForCapture;

        /// <summary>
        /// 【開発用・エディタ限定】<see cref="ScenarioIdForCapture"/> で始めた試合の最初の打席に予約する強制発動を、
        /// シナリオの <c>force</c> の代わりに使う（撮影レシピ <c>data/debug/captures.yaml</c> の <c>force</c>）。
        /// ForcedOutcome の enum 名。既定 null＝シナリオの指定どおり。
        /// </summary>
        public static string ForceOverrideForCapture;

        /// <summary>
        /// 【開発用・エディタ限定】場面ジャンプで始めた試合の校名（先攻・後攻）。撮影レシピの <c>teams</c>。
        /// 既定 null＝engine の仮名（「遠征校(C)」など）とデモの校名のまま＝従来と完全一致。
        /// 指定すると掲示板（engine の Team.Name）と左右の打順表の校名が同じ名前になり、デモ用の控え
        /// （「代打 一郎」など）も足さない＝製品と同じ見た目で撮れる（ストアのスクショ用）。
        /// </summary>
        public static string AwayNameForCapture, HomeNameForCapture;

        /// <summary>
        /// 【開発用・エディタ限定】場面ジャンプで始めた試合の配色（<c>TeamColorProvider.Palette.ColorsFor</c> に渡す校ID）。
        /// 撮影レシピの <c>colors</c>。既定 null＝配色なし（デモの既定のユニフォーム）＝従来と完全一致。
        /// </summary>
        public static int? AwayColorIdForCapture, HomeColorIdForCapture;

        /// <summary>
        /// 【開発用・エディタ限定】配色のヘルメットだけ差し替える色（撮影レシピの <c>helmet</c>・issue #1620）。
        /// 校IDからの配色は帽子と同色のヘルメットしか作れないので、別色の見え方はこれで撮る。null＝差し替えない。
        /// </summary>
        public static KokoSim.Engine.Nation.RgbColor? AwayHelmetForCapture, HomeHelmetForCapture;
#endif
        /// <summary>
        /// 【開発用】伝令が効いていなくても「制球↑」「ミート↑」を出す（issue #1177 の見た目の撮影用。
        /// 撮影レシピ <c>calm-badge-1177</c> の overrides で立てる）。既定 false＝従来と完全一致。
        /// </summary>
        public static bool CalmBadgeForceForCapture;

        /// <summary>撮影専用（issue #1178）: 対決ビューの投手を左投手として立たせる。既定 false＝従来と完全一致。</summary>
        public static bool PitcherLeftForceForCapture;

        /// <summary>撮影専用（issue #1178）: 監督の守備伝令を今送る（動揺が解けて渦巻きが消える絵）。<see cref="SendTimeout"/> と同じ流れ。</summary>
        public void DefenseTimeoutForCapture()
        {
            if (_prog == null) return;
            _prog.CallTimeout(_managerIsAway, null, ShownOffenseIsTop);
            _preStep = null;
            var snap = _prog.Snapshot();
            ApplyPitcherRattled(snap, _atBat?.IsTop ?? snap.OffenseIsTop);
        }

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
        [Tooltip("本塁到達の時刻が取れない得点の補完間隔[秒]（PlaybackDisplaySync.RunScoreTimes の保険）。得点オーバーレイは加算後スコアだけを出す（issue #643）。")]
        [SerializeField] private float scoreCallStepSeconds = 0.5f;
        [Tooltip("得点オーバーレイが最終スコアに達してから消えるまでの表示時間[秒]。")]
        [SerializeField] private float scoreCallFinalHoldSeconds = 1.1f;
        [Tooltip("自校（後攻 home）の采配能力（委任AIの強さ・スキップ時に使用）。")]
        [SerializeField] private int managerTacticalSense = 70;
        [Tooltip("高速試合モード（design-06 §3.4）の半イニング送り間隔[秒]（×1基準。既存の速度倍率 _speed で伸縮する）。演出（対決ビュー/2D俯瞰）を出さずスコアボードだけ進める観戦モード。")]
        [SerializeField] private float fastModeHalfInningSeconds = 1.0f;

        // ライブ観戦（大会フロー）モードの状態。デモ生成モードでは false / null。
        private bool _managerIsAway;
        // 観客の入り（issue #705）。対決ビューを組むときに DuelStageRig へ渡す
        private KokoSim.Engine.Nation.Tournaments.CrowdTurnout _crowd = KokoSim.Engine.Nation.Tournaments.CrowdTurnout.Full;

        // 両校の配色（設計書18 §3.5.1）。攻守交代のたびに守備側/攻撃側へ割り当て直す（DuelStageRig.SetTeamColors）。
        private KokoSim.Engine.Nation.TeamColors _awayColors;
        private KokoSim.Engine.Nation.TeamColors _homeColors;
        // 両校の学校ID（成績の帰属キーの校側, issue #634）。既定はどちらの校とも一致しない値＝成績なし。
        private int _awaySchoolId = int.MinValue;
        private int _homeSchoolId = int.MinValue;
        /// <summary>自校（監督のチーム）の校ID。</summary>
        private int OwnSchoolId => _managerIsAway ? _awaySchoolId : _homeSchoolId;
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
        // いま掲示板に出ている得点（issue #628）。「まだ掲げていない点」＝これとエンジン集計の差、が
        // 得点コール（issue #60）を何回回すかの単一ソース。打球・押し出し・打席途中の生還が別々に
        // 掲げるようになったので、各経路が自分の点数を数え直すと同じ点を二度祝う。
        private int _postedAwayRuns, _postedHomeRuns;
        // 打球の無い得点（押し出し）を掲げる時刻（打席の時計。issue score-display-before-home-touch）。
        // null＝予約なし。据え置きの途中で来るので、必ず FinishPaView までに掲げ切る。
        private double? _nonBattedScoreAt;
        // クリック中断ポップアップ（issue #289・案P1）。盤面クリック＝演出だけを止める自由中断で、
        // エンジンには触れない（CallTimeout を呼ばない＝伝令・動揺解除は動かさない）。
        // 伝令・選手交代・スキップ・再開はこのポップアップに集約する。
        // 旧「タイム（伝令）中断」（issue #173）との2種別は、交代が伝令不要になったことで
        // ボタン可否の差が消えたため 1本の _paused に統合した（伝令は中断中に押せる1アクションへ降格）。
        private VisualElement _pauseModal;
        private TimeoutPopupView _timeoutView;   // 値の流し込み（見た目は MatchLive.uss の tmo-*）
        private Button _pmTimeout, _pmSub, _pmSkip, _pmResume;
        // 申告敬遠（issue #1563）: タイムのメニューのカードと、押したあとの「はい／いいえ」確認（メニューと入れ替わる）。
        private Button _pmIbb, _pmIbbYes, _pmIbbNo;
        private VisualElement _pmIbbConfirm;
        // 伝令役の人選（issue #716・A案）: 「伝令を送る」でメニューと入れ替わる控え一覧。
        private VisualElement _pmMenu, _pmMessenger;
        private ScrollView _pmMessengerList;
        private Label _pmMessengerCap;
        private Button _pmMessengerBack;
        private bool _timeoutSentThisPause;   // 1中断につき伝令1回（連打で複数消費させない）
        // 自動連続進行の中断中フラグ（ポップアップ表示〜再開の間 true）。盤面・自動進行の両方を止める。
        private bool _paused;
        // 振りかぶり中（エンジンが1球先＝伝令・交代を受け付けられない間）に押されたタイム（issue #1530）。
        // その場で開くと画面が止まって預かった球が放されず、ボタンが全部伏せたまま戻らなかった。
        // 1球を見せ終えて次の采配窓が開いたところ（UpdateControlState）でかける＝タイムは投球の合間にかかる。
        private bool _pauseQueued;
        // 球数制限の強制交代パネル（OpenForcedPitcherChangePanel）が中断メニューを経由せず _paused を
        // 立てた印。中断メニューの「再開」は押せないので、Update() がパネルの Close を検知して自分で戻す。
        private bool _forcedSubPause;
        // 自動進行を開始したか（OnEnable の一度だけ。スクショ時＝CaptureMode は開始しない）。
        private bool _autoStarted;
        // 対決ビュー（design-18 §7-1 A案）。既定の開始ビュー（issue #312）で、インプレー打球の間だけ
        // 俯瞰へ自動で切り替わる。実体は DuelStageRig（UBC素体のループ芝居・12fps量子化）。
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
        private VisualElement _miniDiaHost;
        private MiniDiamondElement _miniDia;
        private VisualElement _plChartHost;              // 投球チャート（issue #1541）
        private PitchLocationChartElement _plChart;
        private const int MaxDuelFieldLabels = 6;
        // 場内放送（issue #919）の切り替え（操作列のスイッチ・#1193）の解除と、同じ打席で二度言わないための鍵。
        private System.Action _unbindAnnounce;
        private string _announcedKey;
        // 選手交代モーダル（設計書09 §6 / issue #22）。代打・代走・投手交代・守備交代・DH解除の入口。
        private MatchSubstitutionPanel _subPanel;
        // 選手能力ビューア（issue #602）。タイムから開く両校の名簿＝**読むだけ**でエンジンに触れない。
        private MatchScoutPanel _scoutPanel;
        private Button _pmScout;
        private readonly List<Button> _speedButtons = new();
        // 速度ボタンのハンドラ（ラムダのため OnDisable で外せるよう保持する）。
        private readonly List<(Button Button, System.Action Handler)> _speedHandlers = new();

        // 高速試合モード（design-06 §3.4）。演出を出さずスコアボードだけを実時間経過で半イニング送りする。
        // 速度ボタン列と同じ排他選択グループの一員（_speedButtons/_speedHandlers に相乗り）。
        private bool _fastMode;
        private double _fastModeClock;
        private VisualElement _fastModeBoard;

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
        // ③際どい所（旧名くさい所・issue #872）。押すとエンジンへ入り、ほかの構えと同じく解除するまで続く（issue #1473）。
        // 表示の点灯は窓ごとにエンジンの実状態へ合わせ直す（エンジン側で解けていれば消灯する）。
        private bool _pitchAroundOn;
        // バントシフト（issue #1473）＝構え。陣形と同じく、自校が守備の窓ごとに1球の口へ再予約して続ける。
        private bool _defBuntShift;
        // 守備のサイン（issue #1473）＝ウエスト。その1球へ押し込んで自動で解ける。申告敬遠は手札でなくタイムのメニューから（issue #1563）。
        private DefenseSign? _defSignChoice;
        private enum DefenseSign { Pitchout }
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

        // 画面に映っている半回の攻撃側が表か（映していない・高速試合中・終局は null＝エンジンの局面に従う）。
        // 3アウト目の打球を再生している間、エンジンは画面より先に次の半回へ進んでいる＝攻守の表示・伝令・交代タブ・
        // 守備側の名札は、エンジンの局面ではなくこの画面の半回で決める（issue #1246。さもないとアウトが先に分かる）。
        private bool? ShownOffenseIsTop => _shownAnyHalf && !_fastMode && !_gameOver ? _shownIsTop : (bool?)null;
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
        // この打席の投球列（_paQueue）のうち、判定を画面に出し終えた球の数（issue #1689）。球数の表示は
        // engine の球数から「投げたがまだ判定を見せていない球」を引いて出す＝BSO・投球チャートと同じ瞬間に1増える。
        private int _paPitchesCalled;
        // 守備側の現投手の球数を出している Label（HUD・スタメン列の投手行）。判定の瞬間に数字だけ書き換える（issue #1689）。
        private readonly List<Label> _defPitchCountLabels = new();
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
        // 伝令の3Dの場面（issue #1174/#1175）の打ち切り。通常は15秒前後で終わる＝その倍で切る。
        private const float TimeoutSceneSafetyCapSeconds = 30f;
        /// <summary>「打った瞬間」の本塁打で、対決ビューのまま打者を映して見送る秒数（issue #704。**絵で決める値**）。</summary>
        public static float NoDoubtWatchSeconds = 1.8f;

        private bool IsNoDoubtHomeRun => _current?.Play?.HomeRun == KokoSim.Engine.Match.Timeline.HomeRunStyle.NoDoubt;

        // 打席途中の盗塁の再生（issue #485）。非nullの間＝盗塁プレー（走者が走る→捕手が投げる→塁で判定）を
        // 守備ビューで見せている最中。打球の据え置きと違い、終わったら打席の続きへ戻す（＝次打席へは進めない）。
        // 再生は打球（_current.Play）と同じ経路（DuelStageRig.BeginFieldPlay＋Match2DPlaybackElement）を通す。
        private PlaybackPlay _stealPlay;
        private double _stealEndAt;
        // 捕手の送球を対決ビューの寄りで見せてからリリースで守備ビューへ引き渡す（issue #512）。
        // _stealReleaseAt = FieldCutMomentOf(steal)＝BeginFieldPlay へ渡す t0 と同じ定義（TRelease）。
        private double _stealReleaseAt;
        // 実際に守備ビューへ渡す時刻（issue #662）。捕手の送球はリリースより前（腕を横へ張り出す前）で渡す
        // ＝正面の構図だと張り出し→振りが「三塁へ横振り」に映るため。投手（牽制）・頭出しできない時はリリース。
        private double _stealHandoffAt;
        private bool _stealHandedOff;

        // 打席途中の牽制の再生（issue #486）。盗塁と同型だが、判定結果（アウト/セーフ）に関わらず
        // 牽制球が投じられた球は必ず再生する（分岐させない、2026-08-05 issue決定）。この球の**前**に
        // 再生する（issue #515。従来はこの球を描き切ってから再生していた＝順序が逆だった）。同じ球に
        // 盗塁も解決していても、盗塁企図は走者が投手の投球動作を見てスタートする＝牽制の直後には連結
        // せず、常にこの球を仕切り直して投げる側へ戻す（BeginPitchWindupOrBall）。盗塁は従来どおり
        // この球を投げ終えた後（_pitchCallDone）に再生する（issue #523。#515 で牽制と一緒に投球前へ
        // 連結してしまっていたのが誤りだった）。
        private PlaybackPlay _pickoffPlay;
        private double _pickoffEndAt;
        // 投手の牽制送球を対決ビューの寄りで見せてからリリースで守備ビューへ引き渡す（issue #512）。
        // _pickoffReleaseAt = FieldCutMomentOf(pickoff)＝BeginFieldPlay へ渡す t0 と同じ定義（TRelease）。
        private double _pickoffReleaseAt;
        private bool _pickoffHandedOff;

        // 打席途中の暴投・捕逸の再生（issue #487）。盗塁・牽制と同型だが、この球そのものの結果
        // （キャッチャーが後逸した）として起きるため、盗塁・牽制（この球の前）より後にチェックする
        // （＝この球の判定が出た直後）。再生経路は盗塁・牽制と共有（EnterStealPlay と同型）。
        private PlaybackPlay _batteryMissPlay;
        private double _batteryMissEndAt;

        // 守備ビューで見せるファウル（issue #1074）。あと一歩届かなかったファウルフライ・ポール際の大ファウルだけ
        // （判定は engine の FoulView.Classify＝PlaybackPlay.FoulView）。暴投・捕逸と同じく「この球そのものの結果」で、
        // 見せ終えたら打席の続きへ戻る。_battedHoldFoul は対決ビューで打球が画面から出るのを待っている間の控え。
        private PlaybackPlay _foulFieldPlay;
        private double _foulFieldEndAt;
        private PlaybackPlay _battedHoldFoul;

        // 打席途中のスクイズ不成立（ウエスト／バント空振り）で三塁走者が挟殺される再生（issue #1061）。
        // 暴投・捕逸と同型で「この球そのものの結果」として起きるため、盗塁の後・暴投/捕逸の前に連結する。
        private PlaybackPlay _squeezeRundownPlay;
        private double _squeezeRundownEndAt;

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
        // 判定テロップの画像面（issue #1179）。文字ラベル(_pitchCallJudge系)は display:none のまま残し、
        // 面はこのCallTelopViewが動かす。合計の寿命(pitchCallHoldSeconds)はそのまま・入り/出だけこの秒数。
        private KokoSim.Unity.Match.Telops.CallTelopView _pitchCallTelop;
        private const double PitchCallInSeconds = 0.12, PitchCallOutSeconds = 0.12;
        // 「走った！」の叫び吹き出し（issue #696）。走者の元の塁ごとに1つ（[0]=一塁…[2]=三塁）。
        // 投手が投球動作に入った瞬間に、その球でスタートを切った走者の塁の側へ出す（攻守とも）。
        private VisualElement _runnerGoHost;
        private readonly KokoSim.Unity.Components.BurstBalloon[] _runnerGo = new KokoSim.Unity.Components.BurstBalloon[3];
        private float _runnerGoClock = -1f;   // 残り表示秒（プレー時間）。負＝非表示
        // 「走った！」を出した球（同じ球で2回出さない。振りかぶりで出した球をリリースでまた出さない）。
        private LivePitch _runnerGoFor;

        // 振りかぶりで先に解決した次の1球（issue #696）。打席途中の球は、対決ビューで投手が次の振りかぶりへ
        // 入った瞬間（DuelStageRig.HasCutToNextWindup＝采配カードの受付締切）にエンジンへ投げさせ、
        // その結果をリリース（＝従来エンジンを呼んでいた采配窓）まで預かる。押し込む指示も締切も
        // 見た目どおりになり、走者のスタートが振りかぶりの頭で分かる。エンジンの呼び出し列は変わらない。
        private struct PreResolvedStep
        {
            public AdvancePitchResult Result;
            public LivePitch Thrown;
            /// <summary>解決する直前の観測。預かっている間の HUD はこれで描く（次の球の結果を先に出さない）。</summary>
            public MatchLiveSnapshot SnapshotBefore;
        }
        private PreResolvedStep? _preStep;
        // 打席途中の守備ビュー（盗塁・暴投捕逸・振り逃げ）から戻った直後の球は振りかぶりから見せる（issue #696）。
        // 戻った投手はセット姿勢で止まっている（DuelStageRig.EndFieldPlay）＝牽制の投げ直しと同じ扱い。
        // 振りかぶりが無いと「走った！」を出す投球動作の頭が無い（従来はセット姿勢からリリースへ跳んでいた）。
        private bool _windupAfterFieldPlay;
        /// <summary>エンジンが画面より1球先へ進んでいる（振りかぶりで解決済み・リリース前）。</summary>
        private bool EngineAheadOfScreen => _preStep.HasValue;
        [SerializeField] private float runnerGoHoldSeconds = 1.6f;
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
        private int _scoreCallOwnScore, _scoreCallOppScore;

        // ── 表示同期（issue #438）: 結果テキスト・ラインスコア・大型スコアの表示時刻（プレー時計）──
        // 時刻の計算は engine の純関数 PlaybackDisplaySync が単一ソース。プレー開始時に前計算する。
        private double _resultShowAt;                // 結果テキスト＝打者がセーフで生きる結果は到達レッグT1
        private double _lineScorePostAt;             // ラインスコア掲示＝最後の生還走者の本塁到達後
        private System.Collections.Generic.IReadOnlyList<double> _runScoreShowAt
            = System.Array.Empty<double>();          // 各走者の本塁到達T1（昇順）。大型スコアは最後の到達で開く（issue #643）
        private int _runScoreShown;                  // 発火済みの本数


        // 交代テロップ（issue #920）。交代を確定した瞬間（自校は交代パネルを閉じて再開した瞬間、AI は次の球の前）に
        // 進行を止めて盤面の下へ出す。止め方はイニング間画面（_inningBreakOn）と同じ型＝PumpEngine に入らせず
        // Update で早期に抜け、時間かクリックで明ける。同じ間合いの交代はチームごとに1枚（SubstitutionTelop）。
        // 高速試合モードでは出さない（実況履歴の1行だけ）。
        [SerializeField] private float subTelopHoldSeconds = 3f;
        private SubTelopView _subTelop;
        private readonly Queue<KokoSim.Engine.Match.Broadcast.TelopCard> _subTelopQueue = new();
        private float _subTelopClock;
        private bool _subTelopOn;
        // 守備伝令の場面（issue #1174）。伝令を送った中断で予約し、再開して球の合間に入ったら流す。
        // 予約の中身はテロップの1行（伝令役の背番号＋名前／記録員）。null＝予約なし
        private KokoSim.Engine.Match.Broadcast.TelopRow _timeoutScenePending;
        private bool _timeoutSceneOn;
        private bool _timeoutSceneOffense;  // 予約・流している場面が攻撃伝令（打者への耳打ち・issue #1175）か
        private bool _timeoutSceneRig;      // 3Dの場面を流しているか（false＝テロップだけ）
        private float _timeoutSceneClock;
        // 投球・打球の描画の途中で出したか（そのときは対決ビューの芝居も止める＝明けたら続きから）。
        private bool _subTelopFreezeRig;
        // テロップが明けたら次の采配窓を取りに行く（球数制限の強制交代から戻る途中で出した場合）。
        private bool _subTelopPumpAfter;

        // 試合開始/終了テロップ「プレイボール」「ゲームセット」（issue #442・画像化は #1179）。
        // 専用の全画面層（game-telop）を使う＝judgement-call（Calls画像11種専用になった）とは別の器。
        // 寿命は Update() の専用クロック方式のまま。issue #1179 で「実時間固定」→「再生速度で縮む」へ切替
        // （合計約4.0秒の振り付け＝プレイボール中は初球を投げさせない待ちもこの値に揃う）。
        // ⚠ シーンの MatchLive が直列化値を持つ＝両方変える。
        [SerializeField] private float gameTelopHoldSeconds = 4.0f;
        private VisualElement _gameTelopHost;
        private KokoSim.Unity.Match.Telops.GameTelopView _gameTelop;
        private float _gameTelopClock;
        private bool _gameTelopOn;
        private KokoSim.Unity.Match.Telops.HomeRunTelopView _homeRunTelop;
        // 終局後「ゲームセット」を掲げる予約。得点テロップ（#60）の演出が済むのを Update 側で待つ。
        private bool _gameSetPending;
        // サヨナラの場面（issue #1176）: 喜びを見せてから「ゲームセット」を掲げるまでの秒数（8割が輪に着いてから）。喜びが5種類になった（#1195）ので、バンザイが1往復見える長さまで延ばした
        [SerializeField] private float walkoffCelebrateHoldSeconds = 1.5f;
        // 人だかりが成立しない（喜ぶ人数が足りない・到着できない役者がいる）場面でも「ゲームセット」を出す上限[秒]（実時間）。
        // 通常は終局から10秒前後で人だかりができる＝その倍で切る。
        private const float WalkoffSceneSafetyCapSeconds = 20f;
        private float _walkoffSceneClock;
        private bool _walkoffSkip;          // 盤面クリックで場面を飛ばした（そのまま「ゲームセット」へ）
        private int _subLogCount;   // 既に取り込んだ交代ログ件数（_prog.Substitutions との差分検知。issue #920）

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

            // 判定（アウト/セーフ/フェア/エラー/ファール/フェア、issue #1179でCalls画像11種）の器は
            // 盤面の外＝俯瞰と3Dの両方にかかる層へ。中身を書くのは Match2DPlaybackElement のまま
            // （表示窓・文言→画像面・色クラスの単一ソース）。
            var judgeHost = _root.Q<VisualElement>("judgement-call");
            var judgeLabel = _root.Q<Label>("judgement-judge");
            var judgeShadow = _root.Q<Label>("judgement-judge-sh");
            var judgeDetail = _root.Q<Label>("judgement-detail");
            if (judgeHost != null && judgeLabel != null && judgeDetail != null)
                _view.SetJudgementHost(judgeHost, judgeLabel, judgeShadow, judgeDetail);

            // ホームラン（issue #1179 §3）は judgement-call を使わず専用の全画面層（homerun-telop）。
            var homerunRoot = _root.Q<VisualElement>("homerun-telop");
            if (homerunRoot != null)
            {
                _homeRunTelop = new KokoSim.Unity.Match.Telops.HomeRunTelopView(homerunRoot);
                _view.SetHomeRunHost(_homeRunTelop);
            }

            // プレイボール／ゲームセット（issue #442・画像化は #1179）も専用の全画面層（game-telop）。
            // judgement-call はもう使わない＝Calls画像11種専用になった。
            _gameTelopHost = _root.Q<VisualElement>("game-telop");
            if (_gameTelopHost != null) _gameTelop = new KokoSim.Unity.Match.Telops.GameTelopView(_gameTelopHost);

            // 1球の判定テロップの画像面（issue #1179）。文字ラベルは常時隠す（UXML側で display:none 済み）。
            var pitchCallImage = _root.Q<VisualElement>("pitch-call-image");
            if (pitchCallImage != null) _pitchCallTelop = new KokoSim.Unity.Match.Telops.CallTelopView(pitchCallImage);

            _leftLineup = _root.Q<VisualElement>("left-lineup");
            _rightLineup = _root.Q<VisualElement>("right-lineup");
            _leftPitcher = _root.Q<VisualElement>("left-pitcher");
            _rightPitcher = _root.Q<VisualElement>("right-pitcher");
            _hudHost = _root.Q<VisualElement>("hud-host");
            BuildHudCorners();   // 右上=打者HUD／右下=投手HUD の常設コーナー
            BuildMiniDiamond();  // 守備フォーカス中だけ出す走者の小ダイヤ（issue #595）
            BuildPitchLocationChart();  // 対決ビューの右の中ほどの投球チャート（issue #1541）
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
            _timeoutView = _pauseModal != null ? new TimeoutPopupView(_pauseModal) : null;
            _pmTimeout = _root.Q<Button>("pm-timeout");
            if (_pmTimeout != null) _pmTimeout.clicked += OnSendTimeout;
            _pmMenu = _root.Q<VisualElement>("pm-menu");
            _pmMessenger = _root.Q<VisualElement>("pm-messenger");
            _pmMessengerList = _root.Q<ScrollView>("pm-messenger-list");
            _pmMessengerCap = _root.Q<Label>("pm-messenger-cap");
            _pmMessengerBack = _root.Q<Button>("pm-messenger-back");
            if (_pmMessengerBack != null) _pmMessengerBack.clicked += CloseMessengerPicker;
            _pmSub = _root.Q<Button>("pm-sub");
            if (_pmSub != null) _pmSub.clicked += OnOpenSubstitution;
            _pmScout = _root.Q<Button>("pm-scout");
            if (_pmScout != null) _pmScout.clicked += OnOpenScout;
            _pmIbb = _root.Q<Button>("pm-ibb");
            if (_pmIbb != null) _pmIbb.clicked += OpenIntentionalWalkConfirm;
            _pmIbbConfirm = _root.Q<VisualElement>("pm-ibb-confirm");
            _pmIbbYes = _root.Q<Button>("pm-ibb-yes");
            if (_pmIbbYes != null) _pmIbbYes.clicked += OnConfirmIntentionalWalk;
            _pmIbbNo = _root.Q<Button>("pm-ibb-no");
            if (_pmIbbNo != null) _pmIbbNo.clicked += CloseMessengerPicker;
            _pmSkip = _root.Q<Button>("pm-skip");
            if (_pmSkip != null) _pmSkip.clicked += OnSkip;
            _pmResume = _root.Q<Button>("pm-resume");
            if (_pmResume != null) _pmResume.clicked += OnResume;
            _pauseModal?.RegisterCallback<PointerDownEvent>(OnPauseScrimDown);
            _root.RegisterCallback<PointerDownEvent>(OnRootPointerDown);
            _backHome = _root.Q<Button>("back-home");
            if (_backHome != null) _backHome.clicked += OnBackHome;
            WireSpeed("spd-05", 0.5f); WireSpeed("spd-1", 1f); WireSpeed("spd-2", 2f); WireSpeed("spd-4", 4f);
            WireFastMode("spd-fast");
            _fastModeBoard = _root.Q<VisualElement>("fastmode-board");
            if (_fastModeBoard != null) _fastModeBoard.style.display = DisplayStyle.None;
            _duelHost = _root.Q<VisualElement>("duel-host");
            _duelHost?.Clear();   // OnEnable ごとの積み上がり防止（field-host と同じ運用）
            _duelImg = null;
            _unbindAnnounce = KokoSim.Unity.Audio.MatchAnnouncer.BindSwitch(
                _root.Q<VisualElement>("announce"), _root.Q<VisualElement>("announce-switch"));
            // ⚠ 対決ステージの生成（InitDuelViewDefault）はここではなく BuildGame の**後ろ**。
            //    生成時に配色をマテリアルへ焼くので、両校の配色が入る前に立てると前試合の色で焼ける。

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

            _runnerGoHost = _root.Q<VisualElement>("runner-go-host");
            if (_runnerGoHost != null)
            {
                for (var b = 0; b < _runnerGo.Length; b++)
                {
                    var balloon = new KokoSim.Unity.Components.BurstBalloon("走った！");
                    balloon.AddToClassList("runner-go__burst");
                    balloon.AddToClassList("runner-go__burst--b" + (b + 1));
                    balloon.style.display = DisplayStyle.None;
                    _runnerGo[b] = balloon;
                    _runnerGoHost.Add(balloon);
                }
            }

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

            _inningBreak = _root.Q<VisualElement>("inning-break");
            _ibCaption = _root.Q<Label>("ib-caption");
            _inningBreak?.RegisterCallback<PointerDownEvent>(OnInningBreakDown);

            var subTelopHost = _root.Q<VisualElement>("sub-telop");
            _subTelop = subTelopHost != null ? new SubTelopView(subTelopHost) : null;
            subTelopHost?.RegisterCallback<PointerDownEvent>(OnSubTelopDown);

            ResetPerMatchVisuals();
            // 守備適性は Player に無く名簿側にあるので、通算成績と同じ join（SourceId）を注入する（issue #452）。
            // 交代パネルは自校の選手だけを扱う＝自校の校IDで引く（選手IDは校ごとに1から振る・issue #923）。
            // 校IDは試合ごとに変わるので、呼ばれた時点の値を読む。
            _subPanel = new MatchSubstitutionPanel(_root, OnSubstitutionApplied,
                sid => _statsProvider.AptitudeGradesOf(OwnSchoolId, sid),
                p => _statsProvider.PitchChartOf(OwnSchoolId, p),
                p => _statsProvider.ConditionOf(OwnSchoolId, p.SourceId, p.ConditionValue, p.Name));
            _subPanel.Bind();
            // 能力ビューア（issue #602/#923）。局面文はポップアップと同じ単一ソースを渡す。
            _scoutPanel = new MatchScoutPanel(_root, _statsProvider, PauseSituationText);
            _scoutPanel.Bind();
            BuildGame();
            // 対決ビューの生成は BuildGame の後（＝両校の配色と進行体が入った後）でなければならない。
            // NewDuelRig は生成時に配色をマテリアルへ焼き、以降の塗り替え（ApplyDuelTeamColors）は
            // 「半回が変わった時だけ」なので、誤った色で焼くと 1回表のあいだ直る機会が無い
            // （旧実装は要素クエリの直後に呼んでいたため、2試合目以降は**前試合の配色**で立ち、
            //  自校の先攻/後攻が前試合と反転していると投手と打者のユニフォームが入れ替わって見えた）。
            InitDuelViewDefault();   // 既定=対決ビュー（issue #312）。Rig不可なら俯瞰へフォールバック
            ApplyDuelViewState();
            // 強制交代（球数制限）は次の半回の守備への継投＝エンジンの局面で開く（画面はまだ前の半回を映している）。
            _subPanel.SetMatch(_prog, _managerIsAway, () => _forcedSubPause ? (bool?)null : ShownOffenseIsTop);
            _scoutPanel.SetMatch(_prog, _managerIsAway, _awayDisp, _homeDisp, _awaySchoolId, _homeSchoolId);
            // 指示パネルの初期フェーズ（1打席目の攻守）。以降は采配窓ごとに OpenWindow が追従させる。
            if (_prog != null) _signPad?.SetPhase(_prog.IsTeamOnOffense(_managerIsAway, ShownOffenseIsTop));
            // 試合開始前の掲示板（校名と空の升目）を先に掲げる。掲げないと最初の打席が確定するまで枠が出ない。
            // 場面ジャンプで途中の回から始めた試合は、その回・表裏で掲げる（1回表と決め打ちすると9回の場面でも「1回表」と出る）。
            var startPoint = _prog?.ScenarioStartPoint;
            PostLineScore(startPoint?.Inning ?? 1, isTop: startPoint?.IsTop ?? true, finished: false);
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
                ShowGameTelop(isGameSet: false);
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
            if (_pmScout != null) _pmScout.clicked -= OnOpenScout;
            if (_pmIbb != null) _pmIbb.clicked -= OpenIntentionalWalkConfirm;
            if (_pmIbbYes != null) _pmIbbYes.clicked -= OnConfirmIntentionalWalk;
            if (_pmIbbNo != null) _pmIbbNo.clicked -= CloseMessengerPicker;
            if (_pmMessengerBack != null) _pmMessengerBack.clicked -= CloseMessengerPicker;
            if (_pmSkip != null) _pmSkip.clicked -= OnSkip;
            _pauseModal?.UnregisterCallback<PointerDownEvent>(OnPauseScrimDown);
            _inningBreak?.UnregisterCallback<PointerDownEvent>(OnInningBreakDown);
            _subTelop?.Host.UnregisterCallback<PointerDownEvent>(OnSubTelopDown);
            _root?.UnregisterCallback<PointerDownEvent>(OnRootPointerDown);
            _subPanel?.Close();
            _subPanel?.Unbind();
            _scoutPanel?.Close();
            _scoutPanel?.Unbind();
            if (_backHome != null) _backHome.clicked -= OnBackHome;
            _unbindAnnounce?.Invoke();
            _unbindAnnounce = null;
            KokoSim.Unity.Audio.MatchAnnouncer.Stop();
            _duelRig?.Dispose();
            _duelRig = null;
            _duelImg = null;
            KokoSim.Unity.Audio.MatchBrassBand.End(immediate: true);
            foreach (var (b, h) in _speedHandlers) b.clicked -= h;
            _speedHandlers.Clear();
            _speedButtons.Clear();
        }

        /// <summary>
        /// 打席途中の再生状態（盗塁・牽制・暴投捕逸・スクイズ挟殺・振り逃げ・守備ビューのファウル、打球の据え置き、
        /// 待たせていた押し出しの掲示、投球フェーズ）を全部畳む。Update の再生分岐は先頭の非nullを再生するので、
        /// 1つでも取り残すと次の打席が前の打席のプレーを再生する。後始末（EndFieldPlay・判定の消灯・塁/アウト/得点の反映）は
        /// 呼び出し側の仕事＝ここはフィールドを戻すだけ。
        /// </summary>
        private void ClearMidPaPlayState()
        {
            _stealPlay = null;
            _pickoffPlay = null;
            _batteryMissPlay = null;
            _foulFieldPlay = null;
            _squeezeRundownPlay = null;
            _dropThirdStrikePlay = null;
            _battedHoldActive = false;
            _battedHoldFoul = null;
            _pendingFoulFlight = null;
            _foulFlightHoldSeconds = null;
            _nonBattedScoreAt = null;
            _inPitchPhase = false;
            _pitchCallDone = false;
            _pitchBallVisible = false;
        }

        // 試合ごとに初期化する進行状態（試合をまたいだ持ち越し禁止）。要素クエリ前に呼べるフィールドだけを扱う。
        private void ResetPerMatchState()
        {
            _gameOver = false;
            _stage = LiveStage.Idle;
            _paused = false;
            _forcedSubPause = false;     // 強制交代パネルを開いたまま離れても次の試合へ持ち越さない
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
            _postedAwayRuns = _postedHomeRuns = 0;   // 掲示済み得点の基準（issue #628）も試合をまたがない
            _paQueue.Clear();
            _paPitchesCalled = 0;
            _plChart?.ClearMarks();      // 投球チャートも試合をまたがない（issue #1541）
            _paConfirmed = false;
            ClearMidPaPlayState();       // 打席途中の再生は試合をまたがない
            _preStep = null;             // 振りかぶりで先に解決した球（issue #696）も試合をまたがない
            _windupAfterFieldPlay = false;
            _runnerGoFor = null;
            _windowClock = _windowHold = _windowCarry = 0f;
            _displayedLogCount = 0;
            _prog = null;
            _homeTeam = null;
            _t = 0;
            _speed = 1f;
            _fastMode = false;
            _fastModeClock = 0;
            _pitchIdx = 0;
            _pitchClock = 0f;
            _pitchCallAt = pitchIntervalSeconds;
            _inPitchPhase = false;
            _pitchBallVisible = false;
            _pitchCallClock = 0f;
            HideRunnerGo();
            _pitchCallOn = false;
            _scoreCallClock = 0f;
            _scoreCallOn = false;
            _gameTelopClock = 0f;
            _gameTelopOn = false;
            _gameSetPending = false;
            _walkoffSkip = false;
            _walkoffSceneClock = 0f;
            _duelRig?.EndWalkoffScene();   // 前の試合のサヨナラの場面を持ち越さない（issue #1176）
            _inningBreakClock = 0f;
            _inningBreakOn = false;
            _subLogCount = 0;
            _subTelopQueue.Clear();
            _subTelopClock = 0f;
            _subTelopOn = false;
            _subTelopFreezeRig = false;
            _subTelopPumpAfter = false;
            _timeoutScenePending = null;
            _timeoutSceneOn = false;
            _announcedKey = null;
            _pitchBattingChoice = null;
            _signChoice = null;
            _stealChoice = null;
            _pitchAroundOn = false;
            _defSignChoice = null;
            _defBuntShift = false;
            // 方針カードは試合をまたがない（前の試合の前進守備が次の試合に残らないよう明示的に戻す）。
            _approach = BattingApproach.Normal;
            _defPolicy = null;
            _defGear = null;
            _defInfield = null;
            _defOutfield = null;
            _capHist.Clear();
            _speedButtons.Clear();
            _speedHandlers.Clear();
            _duelOn = false;
            _duelManualOff = false;
            // 配色と「最後に対決ビューへ流した攻守」も試合をまたがない。残すと次の試合の対決ステージが
            // 前試合の色で立ち、塗り替えガード（ApplyDuelTeamColors）も同じ値で素通りして直らない。
            _awayColors = null;
            _homeColors = null;
            _duelColorsOffenseIsTop = null;
        }

        // 試合ごとに初期化する表示（前試合の実況履歴・BSO・結果チップを残さない）。要素クエリ後に呼ぶ。
        private void ResetPerMatchVisuals()
        {
            _capHistHost?.Clear();
            _signPad?.ClearSelection();
            SetVisible(_pauseModal, false);   // 前試合の中断ポップアップを持ち越さない
            SetVisible(_inningBreak, false);  // 前試合のイニング間画面を持ち越さない
            _subTelop?.Hide();                // 前試合の交代テロップも持ち越さない
            _lineScorePosted = false;
            SetCount(0, 0);
            SetOuts(0);
            SetBases(false, false, false);
            HidePitchCall();
            HideScoreCall();
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
                _awaySchoolId = req.AwaySchoolId;
                _homeSchoolId = req.HomeSchoolId;
                _crowd = req.Crowd;
                KokoSim.Unity.Audio.MatchBrassBand.Begin(req.BrassBand, req.AwaySchoolId, req.HomeSchoolId);
                KokoSim.Unity.Audio.MatchSfx.IsKoshienMatch = req.BrassBand;   // 応援が鳴る＝甲子園
                // 掲示板の校名はエンジンの観測データ（LiveLineScore.Name）から出るのでここでは埋めない。
                SetColumnTitles();
                return;
            }

            // デモ/スクショ用の自前生成（本番フローでは通らない）。
            _managerIsAway = false;
            // 校IDは持たない＝どちらの校とも一致しない＝成績は「—」（issue #634）。
            // 直前の実試合の値が残ると、デモの選手に他校の成績が付く。
            _awaySchoolId = int.MinValue;
            _homeSchoolId = int.MinValue;
#if UNITY_EDITOR
            if (ManagerIsAwayOverrideForCapture is { } mia) _managerIsAway = mia;
            if (ManagerSchoolIdForCapture)
            {
                if (_managerIsAway) _awaySchoolId = NationService.ManagerSchoolId;
                else _homeSchoolId = NationService.ManagerSchoolId;
            }
#endif
            _onComplete = null;
            _rematchOnTie = true;
            _crowd = KokoSim.Engine.Nation.Tournaments.CrowdTurnout.Full;   // デモは満員で見せる
            KokoSim.Unity.Audio.MatchBrassBand.Begin(false, _awaySchoolId, _homeSchoolId);
            KokoSim.Unity.Audio.MatchSfx.IsKoshienMatch = false;
            // 委任AIの采配値も成長した監督の実値を使う（旧ハードコード70を廃す, issue #171）。
            managerTacticalSense = KokoSim.Unity.Shell.ManagerService.TacticalSenseForAi;
            // デモの相手校（away＝右列）は本番と同じ StrengthTeamFactory 生成（氏名＋控え8人つき）で観戦する。
            var awayRng = new KokoSim.Engine.Core.Xoshiro256Random(gameSeed ^ 0x1234ABCDUL);
            var away = KokoSim.Engine.Nation.StrengthTeamFactory.Create(58, awayName, awayRng);
            // デモの自校（home＝左列）は実部員（RosterService）から組み、背番号・調子・通算の join を実機同様に見せる。
            var home = RosterTeamBuilder.Build(RosterService.Active, homeName);
#if UNITY_EDITOR
            if (HomeTeamOverrideForCapture != null) home = HomeTeamOverrideForCapture();
#endif
            // RosterTeamBuilder.Build が積む控え（投手枠2枚）へ、代打采配のデモ用の控えを足す。
            // 控えは1本の名簿なので上書きせず Merge する（上書きすると継投先が消える。issue #381）。
            // 撮影で校名を指定したとき（製品と同じ見た目で撮る）は、識別用の仮名の控えを足さない。
            var demoBench = true;
#if UNITY_EDITOR
            if (AwayNameForCapture != null || HomeNameForCapture != null) demoBench = false;
#endif
            home = home with
            {
                Reserves = demoBench
                    ? KokoSim.Engine.Match.Game.TeamReserves.Merge(home.Reserves, BuildBench())
                    : home.Reserves,
                ManagerTacticalSense = managerTacticalSense,   // 伝令の効き目（issue #714）
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
            var seed = gameSeed;
#if UNITY_EDITOR
            if (GameSeedOverrideForCapture is { } s) seed = s;
#endif
#if UNITY_EDITOR
            if (!string.IsNullOrEmpty(ScenarioIdForCapture)
                && Unity.Debugging.DebugScenarios.Catalog.TryGet(ScenarioIdForCapture, out var scenario))
            {
                var built = KokoSim.Engine.Debugging.ScenarioBuilder.Build(scenario, ctx, seed, home);
                var awayTeam = built.Away;
                var homeTeam = built.Home;
                // 撮影の校名・配色（既定 null＝従来どおり）。校名は engine の Team.Name（掲示板）と打順表の両方へ。
                // 名前を変えるだけ＝乱数・結果は変わらない。
                if (AwayNameForCapture != null) { awayTeam = awayTeam with { Name = AwayNameForCapture }; _awayDisp = AwayNameForCapture; }
                if (HomeNameForCapture != null) { homeTeam = homeTeam with { Name = HomeNameForCapture }; _homeDisp = HomeNameForCapture; }
                if (AwayColorIdForCapture is { } ac) _awayColors = TeamColorProvider.Palette.ColorsFor(ac);
                if (HomeColorIdForCapture is { } hc) _homeColors = TeamColorProvider.Palette.ColorsFor(hc);
                if (AwayHelmetForCapture is { } ah && _awayColors != null) _awayColors = _awayColors with { Helmet = ah };
                if (HomeHelmetForCapture is { } hh && _homeColors != null) _homeColors = _homeColors with { Helmet = hh };
                SetColumnTitles();
                _homeTeam = homeTeam;
                _prog = new MatchProgression(awayTeam, homeTeam, built.Ctx, built.Seed, built.Start);
                var forceName = string.IsNullOrEmpty(ForceOverrideForCapture) ? scenario.Force : ForceOverrideForCapture;
                if (KokoSim.Engine.Debugging.ForcedOutcomes.TryParse(forceName, out var force)) _prog.ForceNext(force);
                return;
            }
#endif
            _prog = new MatchProgression(away, home, ctx, seed);
        }

        // 列見出し（左=自校 / 右=相手校）。監督が先攻(away)か後攻(home)かで名前を割り当て、
        // 校名の横のチップに先攻/後攻を書く（issue #1193）。
        private void SetColumnTitles()
        {
            var own = _managerIsAway ? _awayDisp : _homeDisp;
            var opp = _managerIsAway ? _homeDisp : _awayDisp;
            if (_leftTeam != null) _leftTeam.text = own;
            if (_rightTeam != null) _rightTeam.text = opp;
            var ownSide = _root?.Q<Label>("left-side-text");
            var oppSide = _root?.Q<Label>("right-side-text");
            if (ownSide != null) ownSide.text = _managerIsAway ? "先攻" : "後攻";
            if (oppSide != null) oppSide.text = _managerIsAway ? "後攻" : "先攻";
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
            if (_gameOver || _paused || _inningBreakOn || _subTelopOn || _timeoutSceneOn || CaptureMode) return;
            if (_prog == null) return;
            if (_subPanel != null && _subPanel.IsOpen) return;   // 交代モーダルを開いている間は待つ
            if (_scoutPanel != null && _scoutPanel.IsOpen) return;   // 能力ビューアも同じ（issue #602）

            // 振りかぶりで先に解決した球（issue #696）があれば、エンジンは呼ばずにそれを描く
            //（指示はその時点で押し込み済み・消灯済み）。
            if (_preStep is { } pre)
            {
                _preStep = null;
                DispatchStep(pre.Result, pre.Thrown);
                DrainSubstitutionEvents();
                return;
            }

            // 窓で止まっているエンジンへ AdvancePitch を呼ぶと窓を開かずに1球投げてしまう（高速試合モードを
            // 窓の途中で出入りした・イニング間で入って出た、など）。投げさせずに同じ窓を開け直す。
            if (!commitTactics && _prog.IsAtPitchWindow && _prog.CurrentAtBat is { } window)
            {
                OpenWindowOrBreak(window);
                return;
            }

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

        /// <summary>
        /// 打席途中の次の1球を、投手が振りかぶりへ入った瞬間に解決しておく（issue #696）。
        /// 解決するのは「同じ打席の続きの球を、対決ビューの間合いで、そのまま投げる」ときだけ。
        /// 守備ビューへ寄り道する球・打席を終わらせた球・半回が変わる窓・球数制限の交代待ち・
        /// モーダル表示中は従来どおり采配窓（＝リリース）で解決する。
        /// </summary>
        private void TryPreResolveNextPitch()
        {
            // タイムを待たせている間は先に解決しない（先に解決するとエンジンが画面より先にいる＝次の窓でもタイムが開けず、
            // 打席の途中で申告敬遠などを押せない。issue #1563）。
            if (_pauseQueued) return;
            if (_preStep.HasValue || _prog == null || _gameOver || _fastMode || CaptureMode) return;
            if (!DuelPacingActive || !(_duelRig?.HasCutToNextWindup ?? false)) return;
            if (_pitchIdx + 1 < _paQueue.Count || IsTerminalPitch(_pitchIdx)) return;
            var next = _atBatNext;
            if (next == null || _atBat == null || next.PitchNumberInPa <= 1) return;
            if (next.IsTop != _atBat.IsTop || next.Inning != _atBat.Inning) return;
            var live = _paQueue[_pitchIdx].Live;
            if (live == null || live.StealPlay != null || live.BatteryMissPlay != null
                || live.SqueezeRundownPlay != null || live.DropThirdStrikePlay != null) return;
            if (_prog.PendingForcedPitcherChange) return;
            if (_subPanel != null && _subPanel.IsOpen) return;
            if (_scoutPanel != null && _scoutPanel.IsOpen) return;

            var snapBefore = _prog.Snapshot();
            PushPitchTacticsChoice();
            var before = _prog.LastPitch;
            var result = _prog.AdvancePitch();
            var thrown = ReferenceEquals(_prog.LastPitch, before) ? null : _prog.LastPitch;
            ResetPitchTacticsChoice();
            _preStep = new PreResolvedStep { Result = result, Thrown = thrown, SnapshotBefore = snapBefore };
            ShowRunnerGo(thrown);
        }

        /// <summary>
        /// 打席が確定した＝「1打席」持続の指示を解除する。エンドラン指示は設計書09 §1.2 のとおり打席の確定で解く。
        /// 際どい所は構え（解除するまで続く・issue #1473）なのでここでは解かない。
        /// </summary>
        private void ClearPerPaTacticsChoices()
        {
            if (_signChoice != null) { _signChoice = null; _signPad?.ClearSelection("batting"); }
            if (_defSignChoice != null) { _defSignChoice = null; _signPad?.ClearSelection("defsign"); }
        }

        private void DispatchStep(AdvancePitchResult result, LivePitch thrown)
        {
            if (result == AdvancePitchResult.Finished)
            {
                // 打席の途中で決着したサヨナラ（暴投・捕逸・本盗・重盗）は、決着の球を投げ終えたその歩で終局が返る。
                // その球（と進塁の再生）を描き切ってから終局へ進む＝再生の終わりの AdvanceToNextWindow が改めて Finished を受ける。
                if (thrown != null && !_fastMode && _prog != null && _shownAnyHalf)
                {
                    EnqueueThrownPitch(thrown, terminal: false);
                    return;
                }
                EndGame(_prog.BuildResult());
                return;
            }

            // 週間球数制限に達した自校投手がいる（MatchProgression.PendingForcedPitcherChange）。
            // 疲労だけでは起きない＝プレイヤーが自分で気づいて代える（要件どおりシステムからは何も言わない）。
            if (result == AdvancePitchResult.AwaitingForcedPitcherChange)
            {
                OpenForcedPitcherChangePanel();
                return;
            }

            if (result == AdvancePitchResult.PlateAppearance)
            {
                ClearPerPaTacticsChoices();
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
                // カウントの途中で申告敬遠した打席（issue #1473）: この打席の球はもう描いてある＝頭から描き直さず結果へ。
                if (_paQueue.Count > 0) { EnterBattedBallOrHold(); return; }
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
                _paPitchesCalled = 0;
                _plChart?.ClearMarks();   // 投球チャートは今の打席の球だけ（issue #1541）
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
            // この打席の左右で立たせ替える（継投後の利き腕もここで追従。issue #337）。打席の途中の窓でも、
            // 投球・打球の描画中に交代して窓の対戦と左右が食い違っていれば当て直す（issue #802。窓で止まって
            // いない交代は OnSubstitutionApplied で当て直さない＝動作の途中で鏡映を裏返さないため、ここが受け皿）。
            if (head || DuelHandednessStale()) ApplyDuelHandedness();
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
            // 際どい所の点灯はエンジンの実状態に合わせる（エンジン側で解けていたら消灯する）。
            if (_pitchAroundOn && _prog != null && !_prog.PitchAroundOn(_managerIsAway))
            {
                _pitchAroundOn = false;
                _signPad?.ClearSelection("pitcharound");
            }
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
            // サヨナラの場面（issue #1176）の盤面クリック＝飛ばしてそのまま「ゲームセット」へ
            if (!_paused && (_duelRig?.WalkoffSceneArmedOrActive ?? false) && !IsPauseExempt(evt.target as VisualElement))
            {
                SkipWalkoffScene();
                return;
            }
            if (_gameOver || _paused || _prog == null) return;
            if (_subPanel != null && _subPanel.IsOpen) return;
            if (_scoutPanel != null && _scoutPanel.IsOpen) return;
            if (IsPauseExempt(evt.target as VisualElement)) return;
            // 交代テロップ中の盤面クリック＝テロップを明ける（issue #920。中断は開かない）
            if (_subTelopOn) { EndSubTelop(); return; }
            // 守備伝令の場面（issue #1174）の盤面クリック＝場面を飛ばす（すぐ定位置へ戻して進行を再開）
            if (_timeoutSceneOn) { EndTimeoutScene(); return; }
            // プレイボール／ホームランのテロップ中の盤面クリック＝テロップを明ける（中断は開かない）。
            // タイム中はテロップの時計が止まるので、開くとテロップが残ったままになる（ゲームセットは
            // 上の _gameOver で弾かれる）。
            if (_gameTelopOn) { HideGameTelop(); return; }
            if (_homeRunTelop?.IsPlaying ?? false) { _homeRunTelop.Hide(); return; }
            if (EngineAheadOfScreen) { _pauseQueued = true; return; }   // 投げ終わってからかける（issue #1530）
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
                if (v.ClassListContains("mlive-announce")) return true;   // 場内放送のスイッチ（Buttonでない＝issue #1193）
                if (v.ClassListContains("modal-scrim")) return true;
                if (v.ClassListContains("inning-break")) return true;   // クリック＝スキップ。中断は開かない
                if (v.ClassListContains("sub-telop")) return true;      // 交代テロップも同じ（issue #920）
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
            CloseMessengerPicker();
            UpdatePauseMenu();
            SetVisible(_pauseModal, true);
        }

        // ポップアップの中身（状況・対戦・伝令の残り）とボタンの活性。エンジンを触る操作（伝令・交代・スキップ）は、
        // 他の背景処理（BackgroundGameOp）が走っている間と、エンジンが画面より先にいる間（EngineAheadOfScreen）は伏せ、
        // 完了時の UpdateControlState 経由で復帰する。1球進行そのものは背景スレッドでなくメインスレッド同期なので、
        // 試合の進行で Running が立つことは無い＝立つのは他の背景処理（週送り・試合後処理など）が静的状態を使っている間の予防（issue #278 の名残）。
        private void UpdatePauseMenu()
        {
            if (_prog == null) return;
            _timeoutView?.Render(BuildTimeoutModel());
        }

        /// <summary>
        /// タイムのポップアップの ViewModel を組む。値はすべてエンジンの観測（采配窓 <see cref="LiveAtBat"/>・
        /// スナップショット・伝令の残り/枠）と Shell の単一ソース（通算成績・調子の誤認）から引き、ここでは並べるだけ。
        /// 状況は「いま映している局面」＝解決済みの得点を先に見せない（<see cref="PauseSituationText"/> と同じ導出）。
        /// </summary>
        private TimeoutPopupModel BuildTimeoutModel()
        {
            // 振りかぶりで先に解決した球を預かっている間（issue #696）も、エンジンが画面より先にいる
            // ＝伝令・交代・能力ビューアは「投げ終わった後」の状態に効く／見えてしまうので伏せる。
            var resolving = BackgroundGameOp.Running || EngineAheadOfScreen;
            var m = new TimeoutPopupModel
            {
                AwayName = _awayDisp,
                HomeName = _homeDisp,
                ManagerIsAway = _managerIsAway,
                ManagerOnOffense = _prog.IsTeamOnOffense(_managerIsAway, ShownOffenseIsTop),
                TimeoutCap = _prog.TimeoutsPerPeriod,
                OffenseTimeoutsLeft = _prog.OffenseTimeoutsLeft(_managerIsAway),
                DefenseTimeoutsLeft = _prog.DefenseTimeoutsLeft(_managerIsAway),
                TimeoutSent = _timeoutSentThisPause,
                TimeoutEnabled = !resolving && !_timeoutSentThisPause && _prog.CanCallTimeout(_managerIsAway, ShownOffenseIsTop),
                ActionsEnabled = !resolving,
            };
            // 申告敬遠は自校が守備のときだけ。打席が確定して結果を見せている間は出さない（押すと次の打者への敬遠になってしまう）。
            m.IntentionalWalkVisible = !m.ManagerOnOffense;
            m.IntentionalWalkEnabled = IntentionalWalkAvailable;

            if (_atBat is { } at)
            {
                m.Inning = at.Inning; m.IsTop = at.IsTop; m.Outs = at.Outs;
                m.AwayScore = at.AwayScore; m.HomeScore = at.HomeScore;
            }
            else if (_current is { } cur)
            {
                m.Inning = cur.Inning; m.IsTop = cur.IsTop; m.Outs = cur.OutsBefore;
                // 打席前スコアへ巻き戻す（LivePlateAppearance のスコアは解決後。BeginScoreCall と同じ導出）。
                m.AwayScore = cur.IsTop ? cur.AwayScore - cur.RunsScored : cur.AwayScore;
                m.HomeScore = cur.IsTop ? cur.HomeScore : cur.HomeScore - cur.RunsScored;
            }
            else
            {
                m.Inning = 1; m.IsTop = true;
            }
            ShownBases(out m.OnFirst, out m.OnSecond, out m.OnThird);

            if (_statsProvider == null) return m;
            var mu = _atBat != null ? new Matchup(_atBat) : _current != null ? new Matchup(_current) : (Matchup?)null;
            if (mu is not { } who) return m;

            // 対戦は HUD と同じ源（RefreshPanel）＝画面に映っている打者・投手。
            var snap = _preStep is { } pre ? pre.SnapshotBefore : _prog.Snapshot();
            var offenseIsTop = _atBat?.IsTop ?? snap.OffenseIsTop;
            var offense = offenseIsTop ? snap.Away.Lineup : snap.Home.Lineup;
            var def = offenseIsTop ? snap.Home.Pitcher : snap.Away.Pitcher;
            var offenseSchoolId = offenseIsTop ? _awaySchoolId : _homeSchoolId;
            var defenseSchoolId = offenseIsTop ? _homeSchoolId : _awaySchoolId;
            var slot = who.BatterOrder >= 1 && who.BatterOrder <= offense.Count ? offense[who.BatterOrder - 1] : null;

            var career = _statsProvider.CareerBatting(offenseSchoolId, who.BatterSourceId);
            m.Batter = new TimeoutPopupModel.PlayerLine
            {
                Number = NumText(who.BatterNumber),
                Name = who.BatterName,
                Detail = TimeoutPopupModel.BatterDetail(who.BatterOrder, slot?.Position ?? who.BatterPosition,
                    slot?.Grade ?? 0, slot?.Throws ?? Handedness.Right, slot?.Bats ?? who.BatterBats),
                Stats = new List<(string, string)>
                {
                    ("打率", MatchLiveStatsProvider.LiveAvg(career, slot?.AtBats ?? 0, slot?.Hits ?? 0)),
                    ("本", career == null ? "—" : career.HomeRuns.ToString()),
                },
                Condition = _statsProvider.ConditionOf(offenseSchoolId, who.BatterSourceId, who.BatterConditionValue, who.BatterName),
                ConditionEstimated = _statsProvider.IsConditionEstimated(offenseSchoolId),
            };
            if (def != null)
            {
                m.Pitcher = new TimeoutPopupModel.PlayerLine
                {
                    Number = NumText(def.Number),
                    Name = def.Name,
                    Detail = TimeoutPopupModel.PitcherDetail(def.Grade, def.Throws, def.Bats),
                    // 球数だけ（目安投球数・ゲージは出さない）。確定球数＋進行中の打席ぶん（issue #397）。
                    Stats = new List<(string, string)> { ("球数", ShownPitchTotal(def).ToString()) },
                    Condition = _statsProvider.ConditionOf(defenseSchoolId, def.SourceId, def.ConditionValue, def.Name),
                    ConditionEstimated = _statsProvider.IsConditionEstimated(defenseSchoolId),
                };
            }
            return m;
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

        // 「伝令を送る」（ポップアップ内）: 控えが居れば伝令役の一覧へ入れ替える（issue #716・A案）。
        // 控えが0人なら記録員が走る扱いで、そのまま1押しで送る（伝令役の係数は中立）。
        // 攻守はいまの表裏×自校の先後で自動判定（攻撃中＝攻撃伝令／守備中＝守備伝令）。残り0では発動しない。
        private void OnSendTimeout()
        {
            if (!CanSendTimeoutNow()) return;
            var cands = _prog.TimeoutMessengerCandidates(_managerIsAway);
            if (cands.Count == 0) { SendTimeout(null); return; }
            OpenMessengerPicker(cands);
        }

        private bool CanSendTimeoutNow()
        {
            if (_gameOver || !_paused || _prog == null) return false;
            if (BackgroundGameOp.Running || EngineAheadOfScreen) return false;   // 他の背景処理中、または振りかぶりで解決済みの球を預かっている間は受け付けない
            return !_timeoutSentThisPause;          // 1中断につき1回
        }

        // ここで初めて伝令を1回消費する（クリック中断自体は消費しない）。効果は次に解決される打席から効く
        //（エンジンの決定論経路を通す・乱数を引かない。設計書09 §3）。messengerIndex＝控えの添字（null＝記録員）。
        private void SendTimeout(int? messengerIndex)
        {
            if (!CanSendTimeoutNow()) return;
            // 伝令役は送る前に控える（テロップの名前。engine の候補の並び＝messengerIndex の添字）
            var reserves = _prog.TimeoutMessengerCandidates(_managerIsAway);
            var messenger = messengerIndex is int mi && mi >= 0 && mi < reserves.Count ? reserves[mi] : null;
            var kind = _prog.CallTimeout(_managerIsAway, messengerIndex, ShownOffenseIsTop);
            CloseMessengerPicker();
            if (kind == MatchProgression.PlayerTimeoutResult.Unavailable) return;
            _timeoutSentThisPause = true;
            // 守備伝令は「マウンドに集まる」場面（issue #1174）、攻撃伝令は「打者に耳打ちする」場面（issue #1175）を
            // 予約する。高速試合モードでは出さない（実況の1行だけ）
            if (!_fastMode && (kind == MatchProgression.PlayerTimeoutResult.Defense
                               || kind == MatchProgression.PlayerTimeoutResult.Offense))
            {
                _timeoutScenePending = TimeoutTelopRow(messenger);
                _timeoutSceneOffense = kind == MatchProgression.PlayerTimeoutResult.Offense;
            }
            if (_prog != null) ApplyPitcherRattled(_prog.Snapshot(), _atBat?.IsTop ?? _prog.Snapshot().OffenseIsTop);   // 守備伝令で動揺が解けた絵を即座に（issue #1178）
            UpdatePauseMenu();   // 残数と送信済み表示を反映
        }

        // 伝令役の一覧（背番号順）。行＝背番号・名前・学年・性格だけ（効き目の数字や状態は出さない＝監督方針）。
        private void OpenMessengerPicker(IReadOnlyList<Player> cands)
        {
            if (_pmMessenger == null || _pmMessengerList == null) { SendTimeout(null); return; }
            var onOffense = _prog.IsTeamOnOffense(_managerIsAway, ShownOffenseIsTop);
            var left = onOffense ? _prog.OffenseTimeoutsLeft(_managerIsAway) : _prog.DefenseTimeoutsLeft(_managerIsAway);
            if (_pmMessengerCap != null)
                _pmMessengerCap.text = (onOffense ? "打者へ" : "マウンドへ") + "、誰を伝令に？（残り" + left + "回）";
            _pmMessengerList.Clear();
            var order = new List<int>();
            for (var i = 0; i < cands.Count; i++) order.Add(i);
            order.Sort((a, b) =>
            {
                int Key(Player p) => p.UniformNumber > 0 ? p.UniformNumber : int.MaxValue;
                var c = Key(cands[a]).CompareTo(Key(cands[b]));
                return c != 0 ? c : a.CompareTo(b);
            });
            foreach (var i in order)
            {
                var p = cands[i];
                var idx = i;
                var row = new VisualElement();
                row.AddToClassList("bench-row");
                row.Add(MakeLabel(NumText(p.UniformNumber), "num-badge", "num-badge--sm", "f-num"));
                row.Add(MakeLabel(p.Name, "bench-row__name"));
                if (p.Grade > 0)
                {
                    var g = UiComponents.NumUnit(p.Grade.ToString(), "年", false, "bench-row__tag");
                    g.pickingMode = PickingMode.Ignore;
                    row.Add(g);
                }
                if (p.Personality != Personality.Normal)
                    row.Add(MakeLabel(Personalities.DisplayName(p.Personality), "bench-row__tag"));
                row.RegisterCallback<PointerDownEvent>(e => { e.StopPropagation(); SendTimeout(idx); });
                _pmMessengerList.Add(row);
            }
            SetVisible(_pmMenu, false);
            SetVisible(_pmMessenger, true);
        }

        // 伝令役の人選・申告敬遠の確認（どちらもメニューと入れ替わる）を畳んでメニューへ戻る。
        private void CloseMessengerPicker()
        {
            SetVisible(_pmMessenger, false);
            SetVisible(_pmIbbConfirm, false);
            SetVisible(_pmMenu, true);
        }

        // 申告敬遠を押せるか: 自校が守備で、打席の途中（まだ結果の出ていない打者）の、次の1球を投げる前。
        // 伝令の残りには左右されない（実際のルールでも申告敬遠は伝令とは別の操作）。
        private bool IntentionalWalkAvailable
            => _prog != null && !_gameOver && _paused && !_paConfirmed && _atBat != null
               && !BackgroundGameOp.Running && !EngineAheadOfScreen
               && !_prog.IsTeamOnOffense(_managerIsAway, ShownOffenseIsTop);

        private void OpenIntentionalWalkConfirm()
        {
            if (!IntentionalWalkAvailable || _pmIbbConfirm == null) return;
            SetVisible(_pmMenu, false);
            SetVisible(_pmIbbConfirm, true);
        }

        // 「はい」: 申告敬遠を予約してタイムを閉じる。次の窓で投球なしの四球になる（engine の SetIntentionalWalk は
        // 打席の頭にもカウントの途中にも対応）。伝令は消費しない。
        private void OnConfirmIntentionalWalk()
        {
            if (!IntentionalWalkAvailable) { CloseMessengerPicker(); return; }
            _prog.SetIntentionalWalk(_managerIsAway);
            OnResume();
        }

        // 「再開」: 中断を解いて自動連続進行へ戻る。暗幕クリックでも呼ばれる（OnPauseScrimDown）。
        private void OnResume()
        {
            if (_gameOver || !_paused) return;
            _subPanel?.Close();
            _scoutPanel?.Close();
            // 打席解決中などで交代パネルを畳めなかった＝出たまま再開すると進行だけ止まる。畳めるまで再開しない。
            if (_subPanel != null && _subPanel.IsOpen) return;
            CloseMessengerPicker();
            SetVisible(_pauseModal, false);
            _paused = false;
            UpdateControlState();
            // 中断中に確定した交代は、再開したその場でテロップにして止める（issue #920。次の球を投げる前）。
            // テロップが明けたら EndSubTelop が続きを起こす。
            DrainSubstitutionEvents();
            // 再生・投球・采配窓の途中で止めていたなら Update が続きを流す。どこにも居ない（打席の
            // 切れ目で止めていた）場合だけ、次の采配窓を取りに行く。
            // 高速試合モード中は TickFastMode が自分で続きを進める＝ここで1球進行の入口を起こさない
            //（起こすと高速中に1球ぶん通常の進行が走り、強制交代が出ると高速側では解けない _paused が残る）。
            if (_stage == LiveStage.Idle && !_fastMode) AdvanceToNextWindow();
        }

        // 選手交代（設計書09 §6）。issue #289 で伝令の消費とは切り離した＝中断中ならいつでも開ける。
        // 交代モーダルは pause-modal の上に重なり、閉じるとポップアップへ戻る。
        // 攻撃中／守備中で出せる選択肢はモーダル側が出し分ける。自校が先攻(away)か後攻(home)かは _managerIsAway。
        private void OnOpenSubstitution()
        {
            if (_gameOver || _prog == null || !_paused) return;
            if (EngineAheadOfScreen) return;   // 振りかぶりで解決済みの球を預かっている（issue #696）
            _subPanel?.Open();
        }

        /// <summary>
        /// 週間球数制限に達した自校投手がいて、プレイヤーの人選待ちになった
        /// （<see cref="MatchProgression.PendingForcedPitcherChange"/>）。中断メニューを経由せず、
        /// 交代パネルを直接・強制的に開く。<see cref="MatchSubstitutionPanel.Close"/> 側が
        /// 交代するまで閉じさせないので、これ以降の自動進行は既存の IsOpen ガードで自然に止まる。
        /// </summary>
        private void OpenForcedPitcherChangePanel()
        {
            if (_gameOver || _prog == null || _subPanel == null || _subPanel.IsOpen) return;
            _paused = true;
            _forcedSubPause = true;
            _subPanel.Open();
        }

        // 選手能力ビューア（issue #602）。中断中ならいつでも開ける＝**読むだけ**なので
        // 伝令を消費せず乱数も引かない（開閉が試合結果に影響しない＝決定論不変）。
        // 他の背景処理中（BackgroundGameOp）や、エンジンが画面より先にいる間（EngineAheadOfScreen）は、
        // 進行体の可変状態には触れないが、交代・伝令と同じ守りでボタンを伏せておく
        //（読んだ観測が「いまの盤面」とずれて見えるため。issue #278 と同じ考え方）。
        private void OnOpenScout()
        {
            if (_gameOver || _prog == null || !_paused) return;
            if (BackgroundGameOp.Running || EngineAheadOfScreen) return;
            _scoutPanel?.Open();
        }

        // 交代の確定後（パネル側が確定のたびに呼ぶ。「全部やめる」なら取り消しでも呼ばれる）。該当スロットを即入替（退いた選手名を薄く併記）。
        private void OnSubstitutionApplied()
        {
            // 采配窓で止まっているなら、窓の対戦（打者・投手）を engine から引き直す。engine は交代の直後に
            // LiveAtBat を今の実体へ差し替え（MatchProgression.RecordSubstitution）、次の球からその選手で
            // 解決する（打席の頭なら束縛前・途中なら束縛し直し）。引き直さないと HUD の打者・対決ビューの
            // 利き腕がこの打席のあいだ古いまま＝「交代したのに前の投手が投げている」に見える（2026-08-15）。
            if (_stage == LiveStage.Window && _atBat != null && _prog?.CurrentAtBat is { } at)
            {
                // 打席の頭で代打を送った＝前の打者の放送は止めて、新しい打者で言い直す（issue #919）。
                if (_announcedKey != null && AnnouncementKeyOf(at) != _announcedKey)
                    KokoSim.Unity.Audio.MatchAnnouncer.Stop();
                _atBat = at;
                ApplyWindowSituation();     // HUD・スタメン列・カードの可否
                ApplyDuelHandedness();      // 継投後の利き腕・代打の左右で立たせ替える（issue #337 と同じ入口）
            }
            else RefreshPanel();
            // 投げ終えた球を描いている最中に交代すると、次の窓（_atBatNext）は交代前の観測のまま先に掴まれている。
            // engine は CurrentAtBat を差し替え済みなので、引き直さないと窓が開いた時に前の打者の左右・HUDで出る
            // （issue #677。窓で止まっている場合は上の枝で足りる）。
            if (_atBatNext != null && _prog?.CurrentAtBat is { } next) _atBatNext = next;
            if (_caption != null) _caption.text = "選手交代を行った。";
        }

        private void OnSkip()
        {
            if (_gameOver || _prog == null) return;
            // 他の背景処理（BackgroundGameOp）が走っている間は engine の共有状態を奪い合わないよう弾く（issue #278 の名残）。
            // 1球進行自体は背景スレッドで走らないので、試合の進行中にここへ来ることは通常無い。「タイム」と同じ扱い。
            // ボタン自体も UpdateControlState で伏せているが、二重に守る（押下配信は1フレーム遅れうる）。
            if (BackgroundGameOp.Running) return;
            _subPanel?.Close();
            _scoutPanel?.Close();
            SetVisible(_pauseModal, false);
            _paused = false;
            var result = _prog.SkipDelegateToAi(_managerIsAway, managerTacticalSense);
            _view.SetPlay(null);
            EndGame(result, allowWalkoffScene: false);   // 委任で終わったらサヨナラの場面は出さない（issue #1176）
            if (_caption != null) _caption.text = "以降を委任して試合終了。";
        }

        // 終局処理（自然終了・スキップ委任・高速試合モード中の終局 共通）。結果を確定表示し、
        // ライブ観戦なら試合結果画面へ渡す。
        private void EndGame(GameResult result, bool allowWalkoffScene = true)
        {
            // 高速試合モード中に終局した場合はここで抜ける（_prog.IsFinished なので OpenWindowOrBreak は
            // 何もしない＝安全）。抜けておかないと通常ループが止まったままで「ゲームセット」テロップ等の
            // Update() 側の後処理（_gameSetPending の消化）が一生走らない。
            // 高速試合モード・委任で終わったときはサヨナラの場面を出さない（issue #1176）
            bool walkoffScene = allowWalkoffScene && !_fastMode && BeginWalkoffAtGameEnd();
            if (!walkoffScene) _duelRig?.EndWalkoffScene();   // 予約だけ残っていたら畳む（委任・高速）
            SetFastMode(false);
            if (!walkoffScene)
            {
                EndFieldPlay();   // 打球の途中で終局しても守備陣・走者を場に置き去りにしない
                // 対決ビューの役者をその場で止める（issue #647）。EndFieldPlay は投手を投球クリップの途中へ戻すので、
                // 止めないと終わった試合で投げ終わりの余韻が再生され続ける。⚠ EndFieldPlay の後（戻された姿勢から畳む）
                if (_duelRig != null && _duelRig.Available) _duelRig.FreezeForGameEnd();
            }
            _view.HideJudgement();
            KokoSim.Unity.Audio.MatchBrassBand.End(immediate: false);
            KokoSim.Unity.Audio.MatchAnnouncer.Stop();
            _gameOver = true;
            _stage = LiveStage.Over;
            _inPitchPhase = false;
            _paused = false;
            // 中断ポップアップを開いたまま背景解決が終局に達した場合も閉じる（結果画面へ暗幕を残さない）。
            SetVisible(_pauseModal, false);
            _subPanel?.Close();
            _subPanel?.ForceHide();   // 解決中で取り消せなくても結果画面へ交代パネルを残さない
            _scoutPanel?.Close();
            // ⚠ 終局の受け渡し（_gameSetPending → FinishGameEndHandOff）は Update の中で、
            //    _paused / _inningBreakOn の早期 return より**後ろ**にある。つまり終局の瞬間に
            //    立っていた演出フラグは、そのまま結果画面への遷移を人質に取る。演出は畳めばよいだけ
            //    なので、終局ではここで全部下ろしてから _gameSetPending を立てる
            //    （実害: 得点コールが残って試合が終わらなかった 2026-08-14。同じ形が回間の暗幕にもある）。
            //    ⚠ EndInningBreak は使わない＝あれは保留していた**次の采配窓を開く**のが仕事で、
            //      終局後に呼ぶと終わった試合の打席を描き始める。ここは暗幕を畳むだけにする。
            _inningBreakOn = false;
            _inningBreakClock = 0f;
            _afterInningBreak = null;
            SetVisible(_inningBreak, false);
            ClearSubTelop();   // 交代テロップも同じ（明けるのを待つと結果画面への遷移を人質に取る）
            ClearTimeoutScene();
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
                // ── 攻撃: 打ち方（1手）──  Art はカードの絵（Assets/UI/Images/signs）。Icon は絵が無いときに敷く1文字。
                new SignDef { Id = "batting-swing", Label = "積極的に", Icon = "打", Art = "swing", Offense = true, Group = "batting",
                    GroupLabel = "打撃", OnSelect = () => { _pitchBattingChoice = PitchBattingOverride.ForceSwing; PrepareBuntStance(false); },
                    OnClear = () => _pitchBattingChoice = null },
                new SignDef { Id = "batting-take", Label = "待て", Icon = "待", Art = "take", Offense = true, Group = "batting",
                    GroupLabel = "打撃", OnSelect = () => { _pitchBattingChoice = PitchBattingOverride.ForceTake; PrepareBuntStance(false); },
                    OnClear = () => _pitchBattingChoice = null },
                // ── 攻撃: 仕掛け（1手・走者が要る）──
                // ⚠ バント・スクイズは**押した瞬間**に構えの差し替えも始める（issue #490）。
                // 構えへの移行クリップは 1.10秒かかるのに対し、采配を押し込む PushPitchTactics から
                // リリースまでは実測 0.3秒しかない（クリップ内時刻を実測: リリース時点で
                // Idle_To_Bunt が t=0.00＝始まったばかり）。窓の滞在時間ぶんを丸ごと使わないと
                // 着弾までに構えへ入り切らない。
                new SignDef { Id = "batting-bunt", Label = "バント", Icon = "犠", Art = "bunt", Offense = true, Group = "batting",
                    GroupLabel = "打撃", EnabledIf = () => RunnerOnAnyBase,
                    OnSelect = () => { _pitchBattingChoice = PitchBattingOverride.Bunt; PrepareBuntStance(true); },
                    OnClear = () => { _pitchBattingChoice = null; PrepareBuntStance(false); } },
                // セーフティバント（issue #1473）: 走力で内野安打を狙う＝走者が居なくても出せる。
                // 構えは前倒しせず、投げるまでは打つ構えのまま**リリースの直前に構え直す**（不意を突く絵。issue #1658）。
                new SignDef { Id = "batting-safety", Label = "セーフティバント", CardLabel = "セーフティ\nバント", Icon = "セ", Art = "safety", Offense = true, Group = "batting",
                    GroupLabel = "打撃",
                    OnSelect = () => { _pitchBattingChoice = PitchBattingOverride.SafetyBunt; PrepareBuntStance(false); },
                    OnClear = () => { _pitchBattingChoice = null; PrepareBuntStance(false); } },
                new SignDef { Id = "batting-squeeze", Label = "スクイズ", CardLabel = "スクイズ", Icon = "決", Art = "squeeze", Offense = true, Group = "batting",
                    GroupLabel = "打撃", EnabledIf = () => RunnerOnThird,
                    OnSelect = () =>
                    {
                        _pitchBattingChoice = PitchBattingOverride.Squeeze;
                        PrepareBuntStance(true);
                    },
                    OnClear = () =>
                    {
                        _pitchBattingChoice = null;
                        PrepareBuntStance(false);
                        _signPad?.RefreshEnabled();
                    } },
                new SignDef { Id = "sign-hitrun", Label = "エンドラン", CardLabel = "エンドラン", Icon = "走", Art = "hitrun", Offense = true, Group = "batting",
                    // 盗塁と同じ球に押せない（engine は盗塁で一塁走者を先に動かすので、エンドランが効かなくなる・issue #1263）。
                    GroupLabel = "打撃", EnabledIf = () => EndrunAvailable && _stealChoice == null,
                    OnSelect = () => { _signChoice = OffensiveSign.HitAndRun; _signPad?.RefreshEnabled(); },
                    OnClear = () => { _signChoice = null; _signPad?.RefreshEnabled(); } },
                // 狙う塁は塁状況で一意に決まる（一塁→二盗／二塁のみ→三盗。三塁のみは盗塁不可・issue #788）ので、
                // カードは1枚のままラベルだけ書き換える（issue #622・案A）。
                new SignDef { Id = "run-steal", Label = "盗塁", Icon = "盗", Art = "steal", Offense = true, Group = "run",
                    GroupLabel = "走塁", LabelOf = () => StealCardLabel,
                    EnabledIf = () => StealTargetNow != null && _signChoice != OffensiveSign.HitAndRun,
                    OnSelect = () => { _stealChoice = StartType.Gamble; _signPad?.RefreshEnabled(); },
                    OnClear = () => { _stealChoice = null; _signPad?.RefreshEnabled(); } },
                // ── 攻撃: 構え（方針・解除するまで続く）──
                new SignDef { Id = "app-work", Stance = true, Label = "待球策", Icon = "粘", Art = "work", Offense = true, Group = "approach",
                    GroupLabel = "姿勢", OnSelect = () => PushApproach(BattingApproach.WorkCount),
                    OnClear = () => PushApproach(BattingApproach.Normal) },
                new SignDef { Id = "app-ground", Stance = true, Label = "転がせ", Icon = "転", Art = "ground", Offense = true, Group = "approach",
                    GroupLabel = "姿勢", OnSelect = () => PushApproach(BattingApproach.GroundBall),
                    OnClear = () => PushApproach(BattingApproach.Normal) },
                new SignDef { Id = "app-slug", Stance = true, Label = "長打狙い", Icon = "長", Art = "slug", Offense = true, Group = "approach",
                    GroupLabel = "姿勢", OnSelect = () => PushApproach(BattingApproach.Slug),
                    OnClear = () => PushApproach(BattingApproach.Normal) },
                new SignDef { Id = "app-put", Stance = true, Label = "当てに行け", Icon = "当", Art = "put", Offense = true, Group = "approach",
                    GroupLabel = "姿勢", OnSelect = () => PushApproach(BattingApproach.PutInPlay),
                    OnClear = () => PushApproach(BattingApproach.Normal) },

                // ── 守備・左＝サイン（その1球・使うと解ける。issue #1473）──
                // ウエスト: 次の1球を外す。走者が居ないとき（2026-11-23 オーナー判断）と3ボール（外すと四球。エンジンと同じ規則）は消灯する。
                new SignDef { Id = "def-pitchout", Label = "ウエスト", CardLabel = "ウエスト", Icon = "外", Art = "pitchout", Offense = false, Group = "defsign",
                    GroupLabel = "サイン", EnabledIf = () => PitchoutAvailable,
                    OnSelect = () => _defSignChoice = DefenseSign.Pitchout, OnClear = () => _defSignChoice = null },

                // ── 守備・右＝構え（解除するまで続く）──
                new SignDef { Id = "def-infield-in", Label = "前進守備", Icon = "前", Art = "infield-in", Offense = false, Stance = true, Group = "infield",
                    GroupLabel = "陣形", OnSelect = () => PushDefInfield(DefenseDepth.In), OnClear = () => PushDefInfield(null) },
                // バントシフト（issue #1473）: 一・三塁手のチャージ。前進守備と重ねられる（一・三塁はシフトが優先＝AlignmentTactics）。
                new SignDef { Id = "def-buntshift", Label = "バントシフト", CardLabel = "バント\nシフト", Icon = "シ", Art = "buntshift", Offense = false, Stance = true, Group = "buntshift",
                    GroupLabel = "陣形", OnSelect = () => PushDefBuntShift(true), OnClear = () => PushDefBuntShift(false) },
                new SignDef { Id = "def-outfield-deep", Label = "長打警戒", Icon = "深", Art = "outfield-deep", Offense = false, Stance = true, Group = "outfield",
                    GroupLabel = "陣形", OnSelect = () => PushDefOutfield(DefenseDepth.Deep), OnClear = () => PushDefOutfield(null) },
                new SignDef { Id = "policy-fast", Label = "直球中心", Icon = "直", Art = "fastball", Offense = false, Stance = true, Group = "policy",
                    GroupLabel = "配球", OnSelect = () => PushDefPolicy(PitchPolicy.FastballHeavy), OnClear = () => PushDefPolicy(null) },
                new SignDef { Id = "policy-break", Label = "変化球中心", Icon = "変", Art = "breaking", Offense = false, Stance = true, Group = "policy",
                    GroupLabel = "配球", OnSelect = () => PushDefPolicy(PitchPolicy.BreakingHeavy), OnClear = () => PushDefPolicy(null) },
                new SignDef { Id = "policy-zone", Label = "コントロール重視", CardLabel = "コントロール\n重視", Icon = "制", Art = "control", Offense = false, Stance = true, Group = "policy",
                    GroupLabel = "配球", OnSelect = () => PushDefPolicy(PitchPolicy.ControlFirst), OnClear = () => PushDefPolicy(null) },
                // 際どい所（issue #872・旧名くさい所）: ゾーンの際を突き、甘い球を投げない（四球も辞さない）。
                // 2026-11-23 オーナー判断（issue #1473）で名前を「際どい所」に変え、ほかの構えと同じく解除するまで続く。
                new SignDef { Id = "def-pitch-around", Label = "際どい所", CardLabel = "際どい所", Icon = "際", Art = "pitcharound", Offense = false, Stance = true, Group = "pitcharound",
                    GroupLabel = "打席", OnSelect = () => PushPitchAround(true), OnClear = () => PushPitchAround(false) },
                new SignDef { Id = "gear-push", Label = "とばしていけ", Icon = "飛", Art = "push", Offense = false, Stance = true, Group = "gear",
                    GroupLabel = "ギア", OnSelect = () => PushDefGear(PitcherGear.Push), OnClear = () => PushDefGear(null) },
            });
        }

        // 「選べるか」の局面条件。1球進行では指示が**次の1球**に効くので、いま止まっている采配窓の塁を見る
        //（＝画面に映っている塁そのもの）。打席単位だった頃は「次の打席の塁」を先読みするしかなく、
        // カードの出方が打席の結果を先に漏らしていた（走者が出るのを見る前にバントが押せた）。
        private bool RunnerOnFirst => _atBat is { BaseFirst: true };
        private bool RunnerOnThird => _atBat is { BaseThird: true };
        // エンドランが押せるか（issue #1234・2026-11-24 オーナー判断）: 一塁に走者が居て、満塁でない。
        // 満塁は押し出しで全員が進む場面＝エンドランを掛ける意味が無いので選ばせない（engine も満塁では効かせない）。
        private bool EndrunAvailable =>
            _atBat != null && HitAndRunRules.IsAvailable(_atBat.BaseFirst, _atBat.BaseSecond, _atBat.BaseThird);
        private bool RunnerOnAnyBase =>
            _atBat is { BaseFirst: true } or { BaseSecond: true } or { BaseThird: true };

        // ウエストが押せるか（issue #1473）: 走者が居て、3ボールでない（3ボールで外すと四球＝StealReadModel が単一ソース）。
        private bool PitchoutAvailable => RunnerOnAnyBase && StealReadModel.CanPitchout(_atBat.Balls);

        /// <summary>
        /// いまの塁状況で単独盗塁が狙える塁（issue #622。null＝どの塁も狙えない＝カードは消灯）。
        /// 判定式は engine の <see cref="StealTargets"/> が単一ソース＝「押せたのに engine が
        /// 解決しない指示」を構造的に作らない。狙う塁は塁状況で一意に決まるので選ばせない。
        /// </summary>
        /// <para>単独の本盗は採用しない（issue #788）＝三塁のみ在塁では null（カードは消灯）。
        /// 本盗とスクイズの排他（issue #619）もこれで不要になった。</para>
        private StealTarget? StealTargetNow => _atBat == null
            ? null
            : StealTargets.FromBases(_atBat.BaseFirst, _atBat.BaseSecond, _atBat.BaseThird);

        /// <summary>盗塁カードの表示名（狙う塁で書き換える。案A・2026-08-14 監督選択）。</summary>
        private string StealCardLabel => StealTargetNow switch
        {
            StealTarget.Third => "三盗",
            _ => "盗塁",
        };

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
        private void PushDefBuntShift(bool on) { _defBuntShift = on; PushDefense(); }

        // バントシフトは「敷く／敷かない（brain の判断のまま）」の2値＝外しているときは null を渡す。
        private bool? DefBuntShiftOrNull => _defBuntShift ? true : (bool?)null;

        // 際どい所は構え＝解除するまで続く（issue #1473）。押した次の1球から効く。自校が守備側の窓でだけ入れる。
        private void PushPitchAround(bool on)
        {
            _pitchAroundOn = on;
            if (_prog == null || ManagerIsOnOffense()) return;
            _prog.SetPitchAround(_managerIsAway, on);
        }

        // 守備方針を1球の口へ即予約する（issue #492）。押した次の1球から効き、以降は PushPitchTacticsChoice が
        // 采配窓ごとに再予約して「解除するまで続く」寿命を維持する。自校が守備側の窓でだけ載せる
        //（攻撃中に押しても相手の守備には入れない）。SetPitchDefenseOverride は「攻撃側」を引数に取り、守備＝その逆へ載る。
        private void PushDefense()
        {
            if (_prog == null || ManagerIsOnOffense()) return;   // 攻撃中は相手守備＝brain のまま触らない
            var offenseSide = _atBat != null ? _atBat.IsTop : !_managerIsAway;
            _prog.SetPitchDefenseOverride(offenseSide, _defPolicy, _defGear, _defInfield, _defOutfield, DefBuntShiftOrNull);
        }

        // いま止まっている窓（または一括経路）で自校が攻撃側か。
        private bool ManagerIsOnOffense()
            => _atBat != null ? _atBat.IsTop == _managerIsAway : _prog.IsTeamOnOffense(_managerIsAway);

        // これから投げる1球へ、①攻撃の「1手」と②守備方針（配球/ギア/陣形）を予約する。
        // 攻撃の1手は無指示=null なら従来と完全一致。守備方針は「解除するまで続く」ので、自校が守備の窓では
        // 現在の選択を毎窓ここで再予約して寿命を保つ（issue #492: 1球の口は消費後に自動でnullへ戻るため）。
        private void PushPitchTacticsChoice()
        {
            DropImpossiblePitchChoices();
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
            // 狙う塁は塁状況から導く（issue #622）。無指示のときは既定（二盗）を渡す＝engine の
            // 消費順・乱数列は無指示時とまったく同じ（不変条件#2）。
            _prog.SetStealAttempt(_managerIsAway, offense ? _stealChoice : null,
                StealTargetNow ?? StealTarget.Second);
            // 守備方針の再予約（自校が守備の窓のみ）。攻撃中は相手守備＝brain のまま触らない。
            if (!offense)
            {
                var offenseSide = _atBat != null ? _atBat.IsTop : !_managerIsAway;
                _prog.SetPitchDefenseOverride(offenseSide, _defPolicy, _defGear, _defInfield, _defOutfield, DefBuntShiftOrNull);
                // 守備のサイン（issue #1473）: 選んでいる1手だけをこの球へ押し込む（押していなければ何も積まない）。
                if (_defSignChoice == DefenseSign.Pitchout) _prog.SetPitchout(_managerIsAway);
            }
        }

        // issue #619: 押し込む直前に局面条件を採り直し、満たさなくなった「1手」は engine へ渡さず点灯も落とす。
        // 窓の中では塁は変わらない（ApplyWindowSituation の RefreshEnabled で足りる）が、打席一括経路には
        // 窓のスナップショットが無く、前の球の選択が残ったまま別の塁状況へ押し込まれうる。
        // 本命の防御は engine 側（三塁が空ならスクイズは無指示へ落ちる）で、ここはその表示側の整合＝
        // 「押せてしまった指示が黙って別物として効く」のを防ぐ。構えは下ろさない（issue #490: この経路から
        // 構えを解くと、札を押した瞬間に始めた移行が投球直前で巻き戻る）。
        private void DropImpossiblePitchChoices()
        {
            var dropBatting = false;
            if (_pitchBattingChoice == PitchBattingOverride.Squeeze && !RunnerOnThird)
            {
                _pitchBattingChoice = null;
                dropBatting = true;
            }
            else if (_pitchBattingChoice == PitchBattingOverride.Bunt && !RunnerOnAnyBase)
            {
                _pitchBattingChoice = null;
                dropBatting = true;
            }
            if (_signChoice == OffensiveSign.HitAndRun && !EndrunAvailable)
            {
                _signChoice = null;
                dropBatting = true;
            }
            if (_defSignChoice == DefenseSign.Pitchout && _atBat != null && !PitchoutAvailable)
            {
                _defSignChoice = null;
                _signPad?.ClearSelection("defsign");
            }
            var dropRun = _stealChoice != null && StealTargetNow == null;
            if (dropRun) _stealChoice = null;

            if (dropBatting && dropRun) _signPad?.ClearSelection("batting", "run");
            else if (dropBatting) _signPad?.ClearSelection("batting");
            else if (dropRun) _signPad?.ClearSelection("run");
        }

        // ①の1手は消費後に自動解除（Q12-3: 次球は方針に復帰）。②の方針カードは点けたまま残す。
        // エンドラン（_signChoice）だけは設計書09 §1.2の通り「1打席」効く指示なので、ここでは解除しない
        // （2026-08-06 issue #492 でここに巻き込まれ「1球」に縮んでいた退行を修正。PA確定時に
        // DispatchStep / ResolveAndReplayNext が明示的に解除する）。
        private void ResetPitchTacticsChoice()
        {
            _pitchBattingChoice = null;
            _stealChoice = null;
            // 守備のサイン（ウエスト）も1球で解ける。
            if (_defSignChoice != null) { _defSignChoice = null; _signPad?.ClearSelection("defsign"); }
            if (_signChoice == null) _signPad?.ClearSelection("batting", "run");
            else _signPad?.ClearSelection("run");
        }

        // ── 打席一括経路（撮影ハーネス＋1球供給が無い打席）──
        // 1球ずつ止まらずに打席まるごと解決し、合成/実記録の投球列を頭から流す。1球進行と違って
        // 「これから投げる球」の窓が無いので、指示は打席の頭でまとめて押し込む（従来の挙動）。

        private void ResolveAndReplayNext()
        {
            PushPitchTacticsChoice();
            var advanced = _prog.Advance();
            _signChoice = null;   // 一括経路は1回のAdvanceで打席全体を解決する＝エンドランもここで解除
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
            _paPitchesCalled = 0;
            _plChart?.ClearMarks();   // 投球チャートは今の打席の球だけ（issue #1541）
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
        /// 投球列の index 球目を描き始める。
        ///
        /// 打席途中の牽制（issue #486/#515）: この球の前に牽制球が投じられていれば、engine の意味づけ
        /// （「この球の前」）どおり、この球の振りかぶりに入る**前**に守備ビューで再生する。従来はここで
        /// 素通りして投球を描き切ってから（判定後の _pitchCallDone で）牽制をチェックしていたため、
        /// 投手が打者へ投げ切ってから一塁の絵へハードカットしていた＝順序が逆だった（issue #515）。
        /// 前の球のチップが残ったまま牽制の絵に重なると混乱するので先に畳む。牽制が終わったら
        /// <see cref="FinishPickoffView"/> が同じ球を <see cref="BeginPitchWindupOrBall"/> で仕切り直して
        /// 投げる（同じ球に盗塁企図が続いていても、走者は投球動作を見てスタートするため投球後まで
        /// 連結しない＝issue #523）。
        /// </summary>
        private void BeginPitchRender(int index)
        {
            if (index >= 0 && index < _paQueue.Count && _paQueue[index].Live?.PickoffPlay is { } pickoffPlay)
            {
                _pitchIdx = index;
                HidePitchCall();   // 直前の球の球種チップを残さない（issue #515）
                EnterPickoffPlay(pickoffPlay);
                return;
            }
            BeginPitchWindupOrBall(index, forceWindup: false);
        }

        /// <summary>
        /// index 球目の振りかぶり（または継続球のリリース直結）を実際に描き始める。打席の最初の球だけ、
        /// 対決ビューでは振りかぶりから見せるため時計を実尺ぶん巻き戻して待つ（design-18 §7-2。
        /// 2球目以降は前の球の OnPitchReleased が予約済み）。
        ///
        /// <paramref name="forceWindup"/>: 牽制（issue #515）の直後にこの球を投げ直す場合は index が
        /// 0でなくても振りかぶりから見せる。3D守備ビューへ逃げていた間、対決ビューの投手は
        /// 逃げた瞬間のポーズで凍っている（<c>DuelStageRig.EndFieldPlay</c> の Restore）ので、
        /// 前の球のフォロースルーの「続き」として carry では繋がず、必ずセット〜振りかぶりへ
        /// 仕切り直す（<c>DuelStageRig.OnPitchWindup</c> が内部で <c>ResetForNewPlateAppearance</c> を呼ぶ）。
        /// </summary>
        private void BeginPitchWindupOrBall(int index, bool forceWindup)
        {
            // 「走った！」（issue #696）: この球でスタートを切った走者がいれば、投球動作の始まりで出す。
            // 打席の初球（と牽制後の投げ直し）はここが振りかぶりの頭。打席途中の球は振りかぶりの頭で
            // 先に解決して出し済み（TryPreResolveNextPitch）＝同じ球は ShowRunnerGo が二度出さない。
            // 先に解決できなかった球（守備ビュー帰り等）だけ、ここ＝リリースで出る。
            ShowRunnerGo(index >= 0 && index < _paQueue.Count ? _paQueue[index].Live : null);
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
            // セーフティバントだけは構えを前倒ししない（リリース直前の構え直しを rig へ予約する。issue #1658）
            var safetyNow = (index >= 0 && index < _paQueue.Count && IsSafetyBuntSign(_paQueue[index].Token))
                            || PendingSafetyBuntSign();
            var afterField = _windupAfterFieldPlay;
            _windupAfterFieldPlay = false;
            // 打席の途中でも、投手がセット姿勢で止まっていれば振りかぶりから（継投で左右を当て直した窓の後。
            // issue #818）。前の球の予約は当て直しのときに捨ててあるので、予約頼みでは投げ始めない
            var awaitsWindup = _duelRig != null && _duelRig.Available && _duelRig.PitcherAwaitsWindup;
            var useWindup = (index == 0 || forceWindup || afterField || awaitsWindup) && DuelPacingActive;
            PrepareBuntStance(buntNow && !safetyNow, useWindup ? DuelStageRig.WindupSeconds : 0f);

            if (useWindup)
            {
                // セットポジション投球なのでクリップ先頭からではなく DeliveryStartTime から流れる＝
                // 巻き戻すのは ReleaseTime ではなく WindupSeconds。
                _pitchClock = -DuelStageRig.WindupSeconds + carry;
                _pitchFirstPending = true;
                _duelRig.OnPitchWindup();
                if (safetyNow) ScheduleSafetySquare(DuelStageRig.WindupSeconds);   // OnPitchWindup が打者を構えへ戻すので、その後に予約する
                return;
            }
            _pitchClock = carry;
            _pitchFirstPending = false;
            if (safetyNow) ScheduleSafetySquare(0f);
            BeginPitchBall(index);
        }

        // ── 場内放送（issue #919）──
        // 打席の頭の采配窓で「4番、キャッチャー、田中くん。」（代打は「バッター、田中くん。」）。何を言うかは
        // engine の PaAnnouncement が決め、ここは打順表のいまの位置を渡して鳴らすだけ。×4・高速試合モード・
        // オフでは流さない。素材が無ければ黙って鳴らさない（その場合は待たない）。

        /// <summary>
        /// 打席の頭なら放送を始め（同じ打席では一度だけ）、放送中は真を返す＝1球目を待たせる。
        /// </summary>
        private bool HoldForAnnouncement()
        {
            var at = _atBat;
            if (at == null || at.PitchNumberInPa > 1 || _prog == null || CaptureMode) return false;
            if (!KokoSim.Unity.Audio.MatchAnnouncer.Enabled || _fastMode || _speed >= 4f) return false;
            var key = AnnouncementKeyOf(at);
            if (key != _announcedKey)
            {
                _announcedKey = key;
                // 守備位置は打順表のいまの位置（DHの枠は DH）。LiveAtBat.BatterPosition は DH の打者でも
                // 本来の守備位置のままなので使わない。
                var snap = _prog.Snapshot();
                var lineup = at.IsTop ? snap.Away.Lineup : snap.Home.Lineup;
                var order = at.BatterOrder;
                var pos = order >= 1 && order <= lineup.Count ? lineup[order - 1].Position : at.BatterPosition;
                KokoSim.Unity.Audio.MatchAnnouncer.Play(
                    KokoSim.Engine.Match.Broadcast.PaAnnouncement.Clips(order, pos, at.BatterName, at.BatterIsPinchHitter));
            }
            return KokoSim.Unity.Audio.MatchAnnouncer.IsPlaying;
        }

        /// <summary>同じ打席で二度言わない鍵（回・表裏・打順・打席数＋打者）。</summary>
        private string AnnouncementKeyOf(LiveAtBat at)
            => KokoSim.Engine.Match.Broadcast.PaAnnouncement.Key(
                at.Inning, at.IsTop, at.BatterOrder, _prog?.PlayLog.Count ?? 0, at.BatterSourceId, at.BatterName);

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
            KokoSim.Unity.Audio.MatchBrassBand.Rest();   // 攻守交代の間は応援も止む（明けた最初の塁表示で再開）

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

        // ── 交代テロップ（issue #920。旧・交代ポップアップ issue #209 の置き換え）──
        // 直前の進行で増えた交代ログ（_prog.Substitutions＝代打・代走・守備交代・継投・DH解除）を取り込み、
        // 実況履歴に1行ずつ残しつつ、同じ間合いの交代をチームごとに1枚のテロップにして進行を止める。
        // 高速試合モード中は履歴だけ（止めない）。観測データの表示のみ＝試合結果に影響しない。
        private void DrainSubstitutionEvents()
        {
            var log = _prog?.Substitutions;
            if (log == null) return;
            // 交代の下書きを取り消すと engine は再生し直す＝ログが短くなることがある。
            if (_subLogCount > log.Count) _subLogCount = log.Count;
            if (_subLogCount == log.Count) return;
            var fresh = new List<KokoSim.Engine.Match.Game.SubstitutionEvent>();
            for (var i = _subLogCount; i < log.Count; i++)
            {
                fresh.Add(log[i]);
                PushHistory("⇄ " + KokoSim.Engine.Match.Broadcast.SubstitutionTelop.HistoryLine(log[i]));
            }
            _subLogCount = log.Count;
            if (_fastMode || _gameOver || CaptureMode || _subTelop == null) return;
            foreach (var card in KokoSim.Engine.Match.Broadcast.SubstitutionTelop.Build(fresh)) _subTelopQueue.Enqueue(card);
            TryBeginSubTelop();
        }

        /// <summary>
        /// 溜まっている交代テロップを1枚出す。イニング間画面・中断・高速試合モードの間は待つ
        /// （イニング間画面が明けた後の Update で出る＝新しい半回の最初の球の前）。
        /// </summary>
        private void TryBeginSubTelop()
        {
            if (_subTelopOn || _subTelopQueue.Count == 0 || _subTelop == null) return;
            if (_inningBreakOn || _paused || _fastMode || _gameOver) return;
            // 伝令の場面が先（issue #1174「伝令の場面 → 交代テロップ」の順）。場面が明けたら EndTimeoutScene が呼び直す
            if (_timeoutSceneOn || _timeoutScenePending != null) return;
            var card = _subTelopQueue.Dequeue();
            // 判定・得点・旧ポップアップが残っていると重なって読みづらいので先に畳む。
            HidePitchCall();
            HideScoreCall();
            _subTelopFreezeRig = _stage == LiveStage.Pitch || _stage == LiveStage.Play;
            _subTelop.Show(card, card.TeamIsAway ? _awayDisp : _homeDisp, PitcherHudClearance());
            _subTelopOn = true;
            _subTelopClock = 0f;
        }

        /// <summary>
        /// 盤面右下の投手HUD（.mhud-corner--right）の上端までの高さ＋余白。テロップをこれより上に出す（issue #920・
        /// 2026-11-14 監督決定）。HUD が隠れている・まだ測れないときは -1（USS の既定位置のまま）。
        /// </summary>
        private float PitcherHudClearance()
        {
            var corner = _hudPitcherSlot?.parent;
            if (corner == null || corner.resolvedStyle.display == DisplayStyle.None) return -1f;
            var h = corner.layout.height;
            if (float.IsNaN(h) || h <= 0f) return -1f;
            return h + 8f;   // --sp-2 ぶん離す
        }

        // 時間で明ける／クリックで明ける、の共通出口。次の1枚があれば続けて出す。
        private void EndSubTelop()
        {
            if (!_subTelopOn) return;
            _subTelopOn = false;
            _subTelopClock = 0f;
            _subTelopFreezeRig = false;
            _subTelop?.Hide();
            TryBeginSubTelop();
            if (_subTelopOn) return;
            // 打席の切れ目で止めていた（どこにも居ない）なら、次の采配窓を取りに行く。窓・再生の途中なら Update が続きを流す。
            var pump = _subTelopPumpAfter;
            _subTelopPumpAfter = false;
            if (_gameOver || _paused) return;
            if (pump || _stage == LiveStage.Idle) AdvanceToNextWindow();
        }

        // テロップのクリック＝その場で明ける。StopPropagation で中断ポップアップと二重反応させない。
        private void OnSubTelopDown(PointerDownEvent evt)
        {
            // 伝令の場面もこの器（sub-telop）を借りて出す＝帯を押したら場面を飛ばす（盤面クリックと同じ）。
            if (_timeoutSceneOn) { EndTimeoutScene(); evt.StopPropagation(); return; }
            if (!_subTelopOn) return;
            EndSubTelop();
            evt.StopPropagation();
        }

        // ── 守備伝令の場面（issue #1174） ──

        /// <summary>伝令役のテロップの1行（背番号＋名前。控えが0人で記録員が走ったら「記録員」）。守備位置は出さない。</summary>
        private static KokoSim.Engine.Match.Broadcast.TelopRow TimeoutTelopRow(Player messenger) =>
            messenger != null
                ? new KokoSim.Engine.Match.Broadcast.TelopRow("伝令", "", messenger.UniformNumber, messenger.Name, "", null)
                : new KokoSim.Engine.Match.Broadcast.TelopRow("伝令", "", 0, "記録員", "", null);

        /// <summary>
        /// 予約した伝令の場面を始める。球の合間（采配窓・切れ目）に限る＝投球・打球の途中なら終わるまで待つ。
        /// 3Dが使えないとき（俯瞰表示など）はテロップだけを出す。
        /// </summary>
        private void TryBeginTimeoutScene()
        {
            if (_timeoutSceneOn || _timeoutScenePending == null) return;
            if (_gameOver || _paused || _fastMode || _inningBreakOn || _gameTelopOn) return;
            if (_stage != LiveStage.Window && _stage != LiveStage.Idle) return;
            if (FieldPlayActive || _subTelopOn) return;
            // 送った側の半回でなくなっていたら（守備伝令の直後に3アウト目を取った等）捨てずに預かる。
            // 伝令の効き目は次のその側の半回へ持ち越される＝その半回の窓で場面を流す（issue #1246）。
            // 判定はエンジンの局面（3アウトで次の半回の攻守）＝半回が閉じた後の切れ目では流さない。
            if (_prog == null || _prog.IsTeamOnOffense(_managerIsAway) != _timeoutSceneOffense) return;
            var row = _timeoutScenePending;
            _timeoutScenePending = null;
            HidePitchCall();
            HideScoreCall();
            bool bench1B = DuelStageRig.BenchIsFirstBaseSide(_managerIsAway);
            _timeoutSceneRig = DuelPacingActive && (_timeoutSceneOffense
                ? _duelRig.BeginOffenseTimeoutScene(bench1B)
                : _duelRig.BeginTimeoutScene(bench1B));
            var card = new KokoSim.Engine.Match.Broadcast.TelopCard(_managerIsAway, new[] { row });
            _subTelop?.Show(card, _managerIsAway ? _awayDisp : _homeDisp, PitcherHudClearance());
            _timeoutSceneOn = true;
            _timeoutSceneClock = 0f;
        }

        /// <summary>場面を畳んで進行へ戻す（散り終えた・クリックで飛ばした）。交代テロップが待っていれば続けて出す。</summary>
        private void EndTimeoutScene()
        {
            if (!_timeoutSceneOn) return;
            _timeoutSceneOn = false;
            _timeoutSceneClock = 0f;
            _duelRig?.EndTimeoutScene();
            _subTelop?.Hide();
            TryBeginSubTelop();
            if (_subTelopOn) return;
            if (_gameOver || _paused) return;
            if (_stage == LiveStage.Idle) AdvanceToNextWindow();
        }

        /// <summary>伝令の場面を予約ごと畳む（高速試合モードへ入る・終局）。進行は起こさない。</summary>
        private void ClearTimeoutScene()
        {
            _timeoutScenePending = null;
            if (!_timeoutSceneOn) return;
            _timeoutSceneOn = false;
            _timeoutSceneClock = 0f;
            _duelRig?.EndTimeoutScene();
            _subTelop?.Hide();
        }

        /// <summary>
        /// 撮影専用（issue #1174/#1175）: 中断→伝令役を選んで送る（守備中なら守備伝令・攻撃中なら攻撃伝令）→再開、を画面の操作と同じ順で行う。
        /// <paramref name="messengerIndex"/>＝控えの添字（負＝記録員）。送れなかった（他の背景処理中・エンジンが先行中など）ら false。
        /// </summary>
        internal bool SendTimeoutViaMenuForCapture(int messengerIndex)
        {
            if (_prog == null || _gameOver || _paused) return false;
            if (BackgroundGameOp.Running || EngineAheadOfScreen) return false;
            OpenPauseMenu();
            SendTimeout(messengerIndex >= 0 ? messengerIndex : (int?)null);
            bool sent = _timeoutSentThisPause;
            OnResume();
            return sent;
        }

        // ── サヨナラの場面（issue #1176） ──

        /// <summary>
        /// いまの打席がサヨナラの決着（規定の回以降の裏に、後攻がこの打席で勝ち越した）か。点は掲示板にまだ出していない
        /// 分で数える（<see cref="RunsNotYetPosted"/> と同じ基準）。<paramref name="awayRuns"/>＝相手（先攻）の最終得点。
        /// ⚠ <c>IsFinished</c> では見ない: 打球の再生を始める時点では、engine の終局の印はまだ立っていない
        /// （打席の後処理で立つ。2026-11-21 実測）。勝ち越した瞬間に終わる規則（GameEngine の終局判定と同じ）で決める。
        /// </summary>
        private bool IsWalkoffNow(out int awayRuns)
        {
            awayRuns = 0;
            if (_current == null || _current.IsTop || _prog == null || _current.Inning < _prog.RegulationInnings) return false;
            var snap = _prog.Snapshot();
            awayRuns = snap.AwayLine.Runs;
            return snap.HomeLine.Runs > awayRuns && _postedHomeRuns <= awayRuns;
        }

        /// <summary>
        /// 打球の再生を始めた直後に呼ぶ。サヨナラの決着なら、勝ち越しの走者（本塁打は打者）の生還の時刻で場面へ入る予約を
        /// 3Dへ渡す。時刻は得点テロップと同じ表示同期の単一ソース（<see cref="PrepareDisplayTimings"/> の生還時刻）。
        /// </summary>
        private void TryArmWalkoff()
        {
            if (_fastMode || !DuelPacingActive || _current?.Play == null || _runScoreShowAt.Count == 0) return;
            if (!IsWalkoffNow(out var awayRuns)) return;
            // 本塁打は打者の生還を待つ（監督決定）＝最後の生還。それ以外は勝ち越しの1点（同点からの次の1点）
            int k = _current.Play.HomeRun != null
                ? _runScoreShowAt.Count - 1
                : Mathf.Clamp(awayRuns - _postedHomeRuns, 0, _runScoreShowAt.Count - 1);
            var tc = _runScoreShowAt[k];
            if (!double.IsFinite(tc)) return;
            _duelRig.ArmWalkoff((float)tc, _prog.BenchEntryCount(teamIsAway: false), DuelStageRig.BenchIsFirstBaseSide(false));
        }

        /// <summary>
        /// 終局（<see cref="EndGame"/>）でサヨナラの場面を流すか決めて始める。予約済み（打球のあるサヨナラ）ならその場で
        /// 始め、再生の無いサヨナラ（押し出し・死球など）は守備の定位置から始める。流すなら true。
        /// </summary>
        private bool BeginWalkoffAtGameEnd()
        {
            if (_duelRig == null || !_duelRig.Available) return false;
            if (_duelRig.WalkoffSceneArmedOrActive)
            {
                _duelRig.StartArmedWalkoffNow();
                return _duelRig.WalkoffSceneActive;
            }
            if (!DuelPacingActive || !IsWalkoffAtGameEnd()) return false;
            // 塁の絵は決着の前の配置: 打席が確定していれば打席の頭の塁、打席の途中で決着した（暴投・捕逸・本盗・重盗）なら直近の窓の塁
            var onBase = _paConfirmed && _current != null
                ? new[] { _current.BaseFirstBefore, _current.BaseSecondBefore, _current.BaseThirdBefore }
                : new[] { _atBat?.BaseFirst ?? false, _atBat?.BaseSecond ?? false, _atBat?.BaseThird ?? false };
            return _duelRig.BeginWalkoffWithoutPlay(_prog.BenchEntryCount(teamIsAway: false), onBase,
                DuelStageRig.BenchIsFirstBaseSide(false));
        }

        /// <summary>
        /// 終局の時点でサヨナラの決着か（規定の回以降の裏に後攻が勝ち越して終わった）。<see cref="IsWalkoffNow"/> と違い、
        /// 点を掲示板へ出したかには依らない＝押し出し・死球は終局までに掲示済み、暴投・捕逸・重盗は再生の終わりで掲示済みになる。
        /// 裏に後攻がリードで終わる試合は、その裏の攻撃中に勝ち越した場合しかない（先にリードなら裏は行わない）。
        /// </summary>
        private bool IsWalkoffAtGameEnd()
        {
            if (_prog == null || !_shownAnyHalf || _shownIsTop || _shownInning < _prog.RegulationInnings) return false;
            var snap = _prog.Snapshot();
            return snap.HomeLine.Runs > snap.AwayLine.Runs;
        }

        /// <summary>「ゲームセット」を掲げてよいか＝サヨナラの場面が無い・飛ばした・人だかりができてから所定の秒数たった。</summary>
        private bool WalkoffReadyForGameSet()
        {
            if (_walkoffSkip || _duelRig == null || !_duelRig.WalkoffSceneActive) return true;
            if (_walkoffSceneClock >= WalkoffSceneSafetyCapSeconds) return true;
            return _duelRig.WalkoffCelebrateSeconds >= walkoffCelebrateHoldSeconds;
        }

        /// <summary>
        /// サヨナラの場面を飛ばす（盤面クリック）。場面はそのまま続け、「ゲームセット」を待たせない。打球の再生中なら
        /// 再生を終わりまで飛ばす（再生の後始末で予約していた場面がその場で始まる）。
        /// </summary>
        private void SkipWalkoffScene()
        {
            _walkoffSkip = true;
            if (!_gameOver && _stage == LiveStage.Play && _current?.Play != null)
                _t = System.Math.Max(_t, PlayEndTime() + FinishGraceSeconds + 1e-3);
        }

        /// <summary>撮影専用（issue #1176）: サヨナラで喜ぶはずの人数（勝った側＝後攻のベンチ入り）。</summary>
        internal int WalkoffExpectedCountForCapture => _prog?.BenchEntryCount(teamIsAway: false) ?? -1;

        /// <summary>撮影専用（issue #1174）: 伝令の場面を流しているか。</summary>
        internal bool TimeoutSceneOnForCapture => _timeoutSceneOn;

        /// <summary>交代テロップを全部畳む（高速試合モードへ入る・終局）。溜まっている分も捨てる。</summary>
        private void ClearSubTelop()
        {
            _subTelopQueue.Clear();
            _subTelopOn = false;
            _subTelopClock = 0f;
            _subTelopFreezeRig = false;
            _subTelopPumpAfter = false;
            _subTelop?.Hide();
        }

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

        /// <summary>
        /// 対決ビューの打者をバントの構えへ入れる/戻す（issue #490）。**札を押した瞬間**に呼ぶのが本命で、
        /// 構えへの移行クリップ 1.10秒ぶんの助走を窓の滞在時間から取る。表示専用＝判定・帯には無関係。
        /// </summary>
        private void PrepareBuntStance(bool bunting, float leadSeconds = 0f)
        {
            if (!DuelPacingActive) return;
            _duelRig?.PrepareBatterBunt(bunting, leadSeconds);
        }

        private bool PendingSafetyBuntSign()
        {
            if (_prog == null) return false;
            var offenseIsAway = _atBat?.IsTop ?? _prog.IsTeamOnOffense(true);
            return _prog.PendingPitchBattingOverride(offenseIsAway) is PitchBattingOverride.SafetyBunt;
        }

        private static bool IsSafetyBuntSign(PitchToken pt)
            => pt.BattingSign is KokoSim.Engine.Match.Tactics.PitchBattingOverride.SafetyBunt;

        /// <summary>セーフティバントの構え直しをリリースの手前に予約する（issue #1658）。表示専用。</summary>
        private void ScheduleSafetySquare(float releaseIn)
        {
            if (!DuelPacingActive) return;
            _duelRig?.ScheduleBatterSafetySquare(releaseIn);
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
                // 捕手が捕り損ねる球（issue #525）は PitchKind より優先して倒す。engine が着弾コースで
                // 抽選しているので、この球に暴投・捕逸／振り逃げのプレーが載っている＝**捕られない球**。
                // 倒す対象は捕手が捕る3分類だけ＝打者が触った球（ファウル・インプレー・死球）は
                // そもそも捕手が捕らないので既存の意味を変えない（issue の「追加であって置き換えではない」）。
                bool catcherMisses = q.Live?.BatteryMissPlay != null || q.Live?.DropThirdStrikePlay != null;
                if (catcherMisses && (outcome == DuelPitchOutcome.Take
                    || outcome == DuelPitchOutcome.CalledStrike
                    || outcome == DuelPitchOutcome.SwingMiss))
                {
                    outcome = DuelPitchOutcome.NotCaught;
                }
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
                // 見逃し三振（issue #533）: nextReleaseIn<0（＝最後の球）からは推測しない――盗塁死等で
                // イニングが終わる打席は最後の球が見逃しストライクでも三振ではない。打席結果
                // （_paConfirmed済みの pt.StrikesAfter）から直接判定して明示的に渡す。
                var isCalledStrikeout = _paConfirmed
                    && pt.Kind == KokoSim.Engine.Match.AtBat.PitchKind.CalledStrike && pt.StrikesAfter >= 3;
                // 捕り損ねる球で打者が振ったか（issue #525）。分類は捕手側の事情しか表せないので、
                // 振り逃げ（＝ほぼ空振り三振）のスイングが消えないよう別に渡す
                bool batterSwung = pt.Kind == KokoSim.Engine.Match.AtBat.PitchKind.SwingingStrike;
                _duelRig.OnPitchReleased(isLast ? -1f : duelPitchIntervalSeconds, outcome,
                    FlightOf(pt), BattedOf(pt, isLast), isBuntAttempt, isCalledStrikeout, batterSwung);
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
                (float)(tr?.InducedHorizontalBreakM ?? 0.0),
                (float)(pt.VelocityKmh ?? 0.0));
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
                TryArmWalkoff();   // サヨナラの決着なら、勝ち越しの走者の生還で場面へ入る予約（issue #1176）
                RenderReplayFrame();
            }
            else
            {
                // 守備陣＋走者を静止表示（四球なら押し出し反映＝結果後の塁状況）。盤面を空にしない。
                _view.SetResting(_current.BaseFirstAfter, _current.BaseSecondAfter, _current.BaseThirdAfter);
                // 見逃し三振の専用コール（issue #533）はStrike_Type01より長く、既定の据え置きでは
                // クリップが自然終了する前に次打席へ切り替わって消える恐れがある（実装メモ5節）。
                // 対決ビューがその球で予約した最低据え置き（PendingResultHoldSeconds。通常球は0）を
                // 底上げに使う＝他の全ての「打球無し」結果（三振・四球等）は従来どおり変わらない。
                var hold = DuelPacingActive
                    ? Mathf.Max(noPlayHoldSeconds, _duelRig?.PendingResultHoldSeconds ?? 0f)
                    : noPlayHoldSeconds;
                // 掲示板はここで掲げる（issue #628）。ただし押し出しの得点は「走者が歩いて還る一拍」を
                // 待ってから掲げる（issue score-display-before-home-touch）。絵が無いぶん、結果テロップと
                // 同時に加算すると走者がまだ塁上に居るのに点だけが増えて見える。間の長さは engine が単一ソース。
                var pendingRuns = RunsNotYetPosted(_current.IsTop);
                var scoreDelay = (float)PlaybackDisplaySync.NonBattedScoreDelaySeconds;
                if (pendingRuns > 0)
                {
                    // 得点コールが終わるまで据え置きを伸ばす。伸ばさないと FinishPaView の HideScoreCall が
                    // 掲げている途中でコールを消す（既定 1.2 秒 < 待ち 1.8 + 最終ホールド 1.1 秒）。
                    hold = Mathf.Max(hold, scoreDelay + scoreCallFinalHoldSeconds);
                }
                else
                {
                    PostResolvedScore(_current.Inning, _current.IsTop);   // 点が動かない結果は待つ理由がない
                }
                _t = -hold; // 経過で采配窓へ
                // 待ってから掲げる予約（点が入ったときだけ）。実時間でなく打席の時計で数えるので倍速に追従する。
                _nonBattedScoreAt = pendingRuns > 0 ? _t + scoreDelay : (double?)null;
                var noPlayResultJp = ResultJp(_current);
                if (_caption != null) _caption.text = _current.BatterName + "　" + noPlayResultJp;
                if (_result != null) { _result.text = noPlayResultJp; _result.style.display = DisplayStyle.Flex; }
            }
        }

        // 打席の再生が終わった: 塁ダイヤを結果へ更新してから、中断中でなければ次の采配窓を開けに行く。
        private void FinishPaView()
        {
            FlushPendingNonBattedScore();   // 待たせていた押し出しの点を落とさない（据え置きが縮む経路の保険）
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

        // ── 打席途中プレーの共通部品（盗塁・牽制・暴投/捕逸・ファウル守備・挟殺・振り逃げ） ──

        /// <summary>打席途中プレーの再生に入る共通の頭: 投球の表示を畳んでプレー段へ。</summary>
        private void BeginMidPaPlayStage()
        {
            _inPitchPhase = false;
            _pitchBallVisible = false;
            _stage = LiveStage.Play;
            _windowCarry = 0f;
        }

        /// <summary>守備ビューを開いて <paramref name="t"/> から再生する。守備側の頭上ラベル用の同定は、
        /// 打席途中で <c>_current</c> が空なので画面の半回（<c>_shownIsTop</c>）で守備チームを引く。
        /// <paramref name="endDuelThrow"/> は対決ビューへ直乗せしていた送球クリップ（issue #512）を畳んでから渡す場合。</summary>
        private void BeginMidPaFieldPlay(PlaybackPlay play, float t, bool endDuelThrow = false)
        {
            if (_duelRig == null || !_duelRig.Available) return;
            if (_prog != null && _shownAnyHalf)
                _duelRig.SetFieldIdentity(_prog.CurrentDefenseIdentity(_shownIsTop));
            if (endDuelThrow) _duelRig.EndDuelThrow();
            _duelRig.BeginFieldPlay(play, t);
        }

        /// <summary>打席途中プレーの再生が終わった共通の後始末: 守備陣・走者を伏せて対決の構図へ戻す。
        /// <paramref name="windupNext"/>＝次の球は振りかぶりから（issue #696。牽制は投げ直しで別に強制する）。</summary>
        private void EndMidPaFieldView(bool windupNext = true)
        {
            EndFieldPlay();
            if (windupNext) _windupAfterFieldPlay = true;
            _view.HideJudgement();
        }

        /// <summary>この球の事象が動かした塁・アウトと、打席途中に入った点（issue #628）を盤面へ反映する。
        /// 走者が走り着いてから動かす（先に動かすと走る前に着いて見える）。1球観測が無い経路（合成投球列）では何もしない。</summary>
        private void ReflectMidPaBoard(QueuedPitch q)
        {
            if (q.Live == null) return;
            SetOuts(q.Live.Outs);
            SetBases(q.Live.BaseFirst, q.Live.BaseSecond, q.Live.BaseThird);
            PostMidPaScore(q.Live);
        }

        /// <summary>打席の続きへ（打球の <see cref="FinishPaView"/> と違い次打席へは進めない）。
        /// この球が打席を終わらせていれば打球再生／結果保持へ入る。ファウルだけは終端球にならない。</summary>
        private void ResumeAtBatAfterMidPaPlay(bool terminalPitchEntersBattedBall = true)
        {
            _foulFlightHoldSeconds = null;
            _windowCarry = 0f;
            _pitchCallDone = false;
            _inPitchPhase = false;
            if (terminalPitchEntersBattedBall && IsTerminalPitch(_pitchIdx)) { _stage = LiveStage.Play; EnterBattedBallOrHold(); return; }
            if (_pitchIdx + 1 < _paQueue.Count) { BeginPitchRender(_pitchIdx + 1); return; }
            if (_atBatNext != null) { OpenWindowOrBreak(_atBatNext); return; }
            _stage = LiveStage.Idle;
            AdvanceToNextWindow();
        }

        // ── 打席途中の盗塁の3D再生（issue #485） ──
        // 打球の EnterBattedBallOrHold と同型（守備ビューを開いて再生し、終わったら対決構図へ戻す）だが、
        // 盗塁は打席を終わらせないので、終端では FinishPaView（次打席へ）ではなく打席の続きへ戻す。
        // ここが牽制（#486）・暴投/捕逸（#487）・振り逃げ（#488）が乗る「打席途中プレーの再生経路」の土台。

        /// <summary>盗塁プレーを守備ビューで再生し始める。走者・捕手送球・判定は engine の <see cref="PlaybackPlay"/>
        /// （<c>BuildSteal</c> の出力）が単一ソース＝打球と同じ経路で描く。</summary>
        private void EnterStealPlay(PlaybackPlay steal)
        {
            BeginMidPaPlayStage();
            _stealPlay = steal;
            // 投球は対決ビューで見せ終わっている＝その続きから再生する（頭が全景にならない）。
            _stealReleaseAt = FieldCutMomentOf(steal);
            _stealEndAt = MidPaPlayEndTime(steal);
            _view.SetPlay(steal);
            // 対決ビュー据え置きのまま捕手へ送球クリップを直乗せする（issue #512）。頭出しできれば
            // その時刻（TFire）から再生し、リリース（_stealReleaseAt）に達したら守備ビューへ引き渡す
            // （RenderStealFrame）。頭出しできない（rig 未導入・クリップ無し等）なら従来どおり即座に
            // 守備ビューへ切り替える。
            _stealHandedOff = false;
            _stealHandoffAt = _stealReleaseAt;
            if (_duelRig != null && _duelRig.Available && _duelRig.TryBeginDuelThrow(steal, out var fireAt))
            {
                _t = fireAt;
                var handoff = _duelRig.DuelThrowHandoffAt;
                if (float.IsFinite(handoff)) _stealHandoffAt = System.Math.Min(_stealReleaseAt, handoff);
            }
            else
            {
                _t = _stealReleaseAt;
                HandOffStealToFieldView();
            }
            RenderStealFrame();
        }

        /// <summary>捕手の送球を対決ビューの寄りで見せ終わり、守備ビューへ引き渡す（issue #512）。
        /// 送球予定を頭出しできなかった場合は <see cref="EnterStealPlay"/> から即座に呼ばれる。
        /// 守備ビューは**渡した時刻から**再生する（捕手はリリース前に渡す＝issue #662。守備ビュー側の
        /// 捕手の送球予定は同じ BuildThrowEvent から組むので、途中から入っても続きが流れる）。</summary>
        private void HandOffStealToFieldView()
        {
            _stealHandedOff = true;
            BeginMidPaFieldPlay(_stealPlay, (float)System.Math.Min(_t, _stealReleaseAt), endDuelThrow: true);
        }

        /// <summary>盗塁再生の1フレーム（打球の <see cref="RenderReplayFrame"/> の最小版。結果テキスト・
        /// ラインスコア・得点テロップは打席の産物なので触らない＝盤面の実況キャプションだけ進める）。</summary>
        private void RenderStealFrame()
        {
            if (_stealPlay == null) return;
            _view.SetTime(_t);
            if (!_stealHandedOff && _t >= _stealHandoffAt) HandOffStealToFieldView();
            if (FieldPlayActive) _duelRig.SetFieldTime((float)_t);
            else _duelRig?.StepDuelThrow((float)_t);
            if (_caption != null) _caption.text = PlaybackEvaluator.CaptionAt(_stealPlay, _t);
        }

        /// <summary>打席途中プレー（盗塁・牽制・暴投/捕逸・挟殺・振り逃げ）の打ち切り時刻。<see cref="PlayEndTime"/> と
        /// 同じ規則（動く走者レッグ・球・判定の最後＋余韻）だが、打席の表示同期（結果テキスト・得点）は無いのでプレー単体から出す。</summary>
        private double MidPaPlayEndTime(PlaybackPlay play)
        {
            var lastEvent = play.ResAt;
            foreach (var seg in play.Ball) if (seg.T1 > lastEvent) lastEvent = seg.T1;
            foreach (var r in play.Runners)
                foreach (var s in r.Segs)
                {
                    if (PlaybackDisplaySync.IsStationaryLeg(s)) continue;   // その場に居るだけの走者は数えない
                    if (s.T1 > lastEvent) lastEvent = s.T1;
                }
            var end = System.Math.Min(play.Dur, lastEvent + PlayTailSeconds);
            var judged = PlaybackEvaluator.LastJudgementEndTime(play, _view.JudgementHoldSeconds);
            if (judged.HasValue) end = System.Math.Max(end, judged.Value);
            return ClampBeforeWideCameraCut(end, play);
        }

        /// <summary>盗塁再生が終わった（この球そのものを描き切った後・issue #523で牽制からの連結を
        /// 廃止したため常にこの経路）: 対決構図へ戻し、盗塁が動かした塁・アウトを反映してから打席の
        /// 続きへ。同じ球で暴投・捕逸も解決していれば（issue #487。走者ありの実球は盗塁企図の後にも
        /// 投げられる）、盤面を動かす前にその再生へ連結する（先に塁を動かすと二重に見える）。
        /// 暴投・捕逸が無く振り逃げだけ解決していれば（issue #488。盗塁成功で一塁が空いた直後に
        /// 三振＋振り逃げが続く稀な複合）、そちらへ連結する。</summary>
        private void FinishStealView()
        {
            EndMidPaFieldView();   // 守備陣・走者を伏せて対決の構図へ戻す（＝打席への唯一のカット）
            _stealPlay = null;
            var q = _paQueue[_pitchIdx];
            if (q.Live?.SqueezeRundownPlay is { } squeezeRundownPlay)
            {
                EnterSqueezeRundownPlay(squeezeRundownPlay);
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
            // 盗塁が動かした塁・アウトはここで反映＝走者が走り着いてから盤面のダイヤ・アウトを動かす
            // （先に動かすと走る前に着いて見える）。1球観測が無い経路（合成投球列）には盗塁は載らない。
            ReflectMidPaBoard(q);
            // 打席の続きへ（打球の FinishPaView と違い次打席へは進めない）。この球が打席を終わらせていれば
            // 続けて打球再生／結果保持へ入る（盗塁のあと同じ球で三振・四球・インプレーが起きる稀な複合）。
            ResumeAtBatAfterMidPaPlay();
        }

        // ── 打席途中の牽制の3D再生（issue #486） ──
        // EnterStealPlay 系（issue #485）と同型。判定結果（アウト/セーフ）に関わらず、牽制球が
        // 投じられた球は必ずこの経路で再生する（分岐させない・独自の間引きをしない、2026-08-05 issue決定）。

        /// <summary>牽制プレーを守備ビューで再生し始める。投手の送球・走者の帰塁・判定は engine の
        /// <see cref="PlaybackPlay"/>（<c>BuildPickoff</c> の出力）が単一ソース＝盗塁・打球と同じ経路で描く。</summary>
        private void EnterPickoffPlay(PlaybackPlay pickoff)
        {
            BeginMidPaPlayStage();
            _pickoffPlay = pickoff;
            // 投球は対決ビューで見せ終わっている＝その続きから再生する（頭が全景にならない）。
            _pickoffReleaseAt = FieldCutMomentOf(pickoff);
            _pickoffEndAt = MidPaPlayEndTime(pickoff);
            _view.SetPlay(pickoff);
            // 対決ビュー据え置きのまま投手へ牽制送球クリップを直乗せする（issue #512。EnterStealPlay
            // と同型）。頭出しできなければ従来どおり即座に守備ビューへ切り替える。
            _pickoffHandedOff = false;
            if (_duelRig != null && _duelRig.Available && _duelRig.TryBeginDuelThrow(pickoff, out var fireAt))
            {
                _t = fireAt;
            }
            else
            {
                _t = _pickoffReleaseAt;
                HandOffPickoffToFieldView();
            }
            RenderPickoffFrame();
        }

        /// <summary>投手の牽制送球を対決ビューの寄りで見せ終わり、守備ビューへ引き渡す（issue #512）。</summary>
        private void HandOffPickoffToFieldView()
        {
            _pickoffHandedOff = true;
            BeginMidPaFieldPlay(_pickoffPlay, (float)_pickoffReleaseAt, endDuelThrow: true);
        }

        /// <summary>牽制再生の1フレーム（<see cref="RenderStealFrame"/> と同型）。</summary>
        private void RenderPickoffFrame()
        {
            if (_pickoffPlay == null) return;
            _view.SetTime(_t);
            if (!_pickoffHandedOff && _t >= _pickoffReleaseAt) HandOffPickoffToFieldView();
            if (FieldPlayActive) _duelRig.SetFieldTime((float)_t);
            else _duelRig?.StepDuelThrow((float)_t);
            if (_caption != null) _caption.text = PlaybackEvaluator.CaptionAt(_pickoffPlay, _t);
        }

        /// <summary>牽制再生が終わった: 対決構図へ戻し、この球を仕切り直して投げる
        /// （<see cref="BeginPitchWindupOrBall"/>）。牽制は「この球の前」の事象（issue #515）だが、
        /// 盗塁企図は走者が投手の投球動作を見てスタートするプレーなので、牽制セーフの直後であっても
        /// ここでは連結しない——投球を挟んでから、従来どおり投げ終わった時点（_pitchCallDone）で
        /// チェックされる（issue #523。牽制と一緒に投球前へ連結していたため、捕手がまだ受けていない
        /// 球で送球する順序になっていた）。</summary>
        private void FinishPickoffView()
        {
            EndMidPaFieldView(windupNext: false);
            _pickoffPlay = null;
            BeginPitchWindupOrBall(_pitchIdx, forceWindup: true);
        }

        // ── 打席途中の暴投・捕逸の3D再生（issue #487） ──
        // EnterPickoffPlay 系（issue #486）と同型。盗塁・牽制と違い「この球の前」ではなく
        // 「この球そのものの結果」として起きるため、この球を実際に投げ終えた後（_pitchCallDone）で
        // チェックする。牽制は投球前に既に再生済み（issue #515）。盗塁は同じ_pitchCallDoneブロック内で
        // 直前にチェックされ、乗っていれば先にそちらへ連結する（issue #523・#485）ため、ここに
        // 辿り着く時点で盗塁も解決済み。同じ球で振り逃げも解決していれば（issue #488。三振確定後の
        // 後処理として判定される）、続けてそちらへ連結する（FinishBatteryMissView）。

        /// <summary>暴投・捕逸のプレーを守備ビューで再生し始める。ボールの後逸・捕手の返球・走者の進塁は
        /// engine の <see cref="PlaybackPlay"/>（<c>BuildBatteryMiss</c> の出力）が単一ソース＝
        /// 盗塁・牽制・打球と同じ経路で描く。</summary>
        private void EnterBatteryMissPlay(PlaybackPlay batteryMiss)
        {
            BeginMidPaPlayStage();
            _batteryMissPlay = batteryMiss;
            // 投球は対決ビューで見せ終わっている＝その続きから再生する（頭が全景にならない）。
            _t = FieldCutMomentOf(batteryMiss);
            _batteryMissEndAt = MidPaPlayEndTime(batteryMiss);
            _view.SetPlay(batteryMiss);
            BeginMidPaFieldPlay(batteryMiss, (float)_t);
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

        /// <summary>暴投・捕逸再生が終わった: 対決構図へ戻す。同じ球で振り逃げも解決していれば
        /// （issue #488）盤面を動かす前にその再生へ連結する（先に塁を動かすと二重に見える）。
        /// 続きが無ければ塁・アウトを反映して打席の続きへ戻る。</summary>
        private void FinishBatteryMissView()
        {
            EndMidPaFieldView();
            _batteryMissPlay = null;
            var q = _paQueue[_pitchIdx];
            if (q.Live?.DropThirdStrikePlay is { } dropThirdStrikePlay)
            {
                EnterDropThirdStrikePlay(dropThirdStrikePlay);
                return;
            }
            ReflectMidPaBoard(q);
            ResumeAtBatAfterMidPaPlay();
        }

        // ── 守備ビューで見せるファウル（issue #1074） ──
        // 暴投・捕逸（EnterBatteryMissPlay）と同型。違いは始まり方だけで、ファウルは打球なので
        // インプレーと同じく「対決ビューで見せた打球の続き」から再生する（ContactMomentOf）。

        private void EnterFoulFieldPlay(PlaybackPlay foul)
        {
            BeginMidPaPlayStage();
            // 対決ビューが打球を見せていた秒数は BeginFieldPlay で消えるので先に採る（同じ区間を二度描かない）
            var shown = _duelRig?.BattedElapsedSeconds ?? -1f;
            _foulFieldPlay = foul;
            _t = ContactMomentOf(foul, shown);
            _foulFieldEndAt = FoulFieldEndTime(foul);
            _view.SetPlay(foul);
            BeginMidPaFieldPlay(foul, (float)_t);
            RenderFoulFieldFrame();
        }

        private void RenderFoulFieldFrame()
        {
            if (_foulFieldPlay == null) return;
            _view.SetTime(_t);
            if (FieldPlayActive) _duelRig.SetFieldTime((float)_t);
            if (_caption != null) _caption.text = PlaybackEvaluator.CaptionAt(_foulFieldPlay, _t);
        }

        /// <summary>ファウルの再生を打ち切る時刻＝球が落ちる／追った野手が着くの遅いほう＋余韻。</summary>
        private double FoulFieldEndTime(PlaybackPlay play)
        {
            var lastEvent = play.ResAt;
            foreach (var seg in play.Ball) if (seg.T1 > lastEvent) lastEvent = seg.T1;
            if (play.Moves != null) foreach (var kv in play.Moves)
                foreach (var m in kv.Value)
                    if (m.T1 > lastEvent) lastEvent = m.T1;
            var end = System.Math.Min(play.Dur, lastEvent + PlayTailSeconds);
            // 「ファール」の判定（issue #1134）が消え終わるまでは切らない（Dur でクランプ）
            var judged = PlaybackEvaluator.LastJudgementEndTime(play, _view.JudgementHoldSeconds);
            return judged.HasValue ? System.Math.Min(play.Dur, System.Math.Max(end, judged.Value)) : end;
        }

        /// <summary>ファウルの再生が終わった: 対決構図へ戻し、次の球を振りかぶりから投げる（打席の続き）。</summary>
        private void FinishFoulFieldView()
        {
            EndMidPaFieldView();
            _foulFieldPlay = null;
            ResumeAtBatAfterMidPaPlay(terminalPitchEntersBattedBall: false);
        }

        // ── 打席途中のスクイズ挟殺の3D再生（issue #1061） ──
        // EnterBatteryMissPlay 系（issue #487）と同型。三塁走者が三本間で挟まれてタッチアウトになる絵は
        // engine の <see cref="PlaybackPlay"/>（<c>BuildSqueezeRundown</c> の出力）が単一ソース。
        // 同じ球で暴投・捕逸／振り逃げも解決していれば、盤面を動かす前にその再生へ連結する。

        private void EnterSqueezeRundownPlay(PlaybackPlay rundown)
        {
            BeginMidPaPlayStage();
            _squeezeRundownPlay = rundown;
            // 投球は対決ビューで見せ終わっている＝その続きから再生する（頭が全景にならない）。
            _t = FieldCutMomentOf(rundown);
            _squeezeRundownEndAt = MidPaPlayEndTime(rundown);
            _view.SetPlay(rundown);
            BeginMidPaFieldPlay(rundown, (float)_t);
            RenderSqueezeRundownFrame();
        }

        private void RenderSqueezeRundownFrame()
        {
            if (_squeezeRundownPlay == null) return;
            _view.SetTime(_t);
            if (FieldPlayActive) _duelRig.SetFieldTime((float)_t);
            if (_caption != null) _caption.text = PlaybackEvaluator.CaptionAt(_squeezeRundownPlay, _t);
        }

        /// <summary>挟殺再生が終わった: 対決構図へ戻し、挟殺後の塁・アウトを反映してから打席の続きへ
        /// （<see cref="FinishBatteryMissView"/> と同型。3アウト目なら次の窓が攻守交代を開く）。</summary>
        private void FinishSqueezeRundownView()
        {
            EndMidPaFieldView();
            _squeezeRundownPlay = null;
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
            ReflectMidPaBoard(q);
            ResumeAtBatAfterMidPaPlay();
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
            BeginMidPaPlayStage();
            _dropThirdStrikePlay = dropThirdStrike;
            // 投球は対決ビューで見せ終わっている＝その続きから再生する（頭が全景にならない）。
            _t = FieldCutMomentOf(dropThirdStrike);
            _dropThirdStrikeEndAt = MidPaPlayEndTime(dropThirdStrike);
            _view.SetPlay(dropThirdStrike);
            BeginMidPaFieldPlay(dropThirdStrike, (float)_t);
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

        /// <summary>振り逃げ再生が終わった: 対決構図へ戻し、出塁後の塁・アウトを反映してから打席の続きへ。
        /// 三振確定後の後処理として起きる（＝この球の最後の事象）ので、これ以上連結するプレーは無い。</summary>
        private void FinishDropThirdStrikeView()
        {
            EndMidPaFieldView();
            _dropThirdStrikePlay = null;
            var q = _paQueue[_pitchIdx];
            ReflectMidPaBoard(q);
            ResumeAtBatAfterMidPaPlay();
        }

        private void Update()
        {
            // 高速試合モード中は演出系（対決ビュー/2D俯瞰/HUD各種）を一切更新しない別ループへ分岐する。
            if (_fastMode) { TickFastMode(); return; }

            // 2D俯瞰のカメラワーク（Issue #119）は使わない＝固定全景。切り替えボタンは issue #1193 で撤去した。
            _view.CameraWorkEnabled = false;
            // 判定オーバーレイ（#59）の表示窓はプレー時間軸で数えるので、倍速を渡して実時間の見え方を保つ（#236 症状A）。
            _view.PlaybackSpeed = _speed;
            _view.CallsSuppressed = _gameTelopOn;   // プレイボール/ゲームセット中は判定テロップを出さない（issue #1179）

            // 対決ビューはポーズを12fps量子化で進める（タイム中断中は盤面と同様に凍結）。
            // 打球プレー中は俯瞰を出していても回す＝トグルで切り替えた瞬間に3Dが止まっていない（F9）。
            // 投球・打球の途中で出した交代テロップ（issue #920）の間も止める＝明けたら同じ場面から続く。
            if (!_paused && !(_subTelopOn && _subTelopFreezeRig)
                && (_duelOn || FieldPlayActive || (_duelRig?.WalkoffSceneActive ?? false)))
                _duelRig?.Tick(Time.deltaTime * _speed);
            RefreshDuelFieldLabels();   // 頭上フルネームラベル（issue #393）。ポーズ中も現在位置のまま表示を保つ
            RefreshMiniDiamond();       // 走者の小ダイヤ（issue #595）。同じくポーズ中も現在位置のまま残す
            RefreshPitchLocationChart(); // 投球チャート（issue #1541）。対決ビュー中だけ出す
            RefreshHudVisibility();     // 打球プレー中はマッチアップHUDを引っ込める（下の説明を参照）
            UpdateTacticsAcceptance();  // 采配カードの受付ゲート（issue #493）。投手が次球の振りかぶりへ入ったら締め切る

            // 強制交代パネル（球数制限）が閉じた＝継投が確定した。中断メニューを経由していないので
            // 「再開」ボタンを押しようがない＝ここで自分から中断を解いて自動進行へ戻す。
            if (_forcedSubPause && (_subPanel == null || !_subPanel.IsOpen))
            {
                _forcedSubPause = false;
                _paused = false;
                DrainSubstitutionEvents();   // 継投の交代テロップ（issue #920）。出したら明けてから進む
                if (_subTelopOn) _subTelopPumpAfter = true;
                else AdvanceToNextWindow();
                return;
            }

            // ホームラン（issue #1179 §3）。再生速度では縮めない＝実時間(deltaTime)のまま進める。
            // 中断・イニング間・伝令の場面・交代テロップの待ちより**前**で回す＝それらの最中も実時間で消える
            //（後ろで回すと、強制交代のパネル等の上に全画面の暗幕ごと凍って残っていた）。
            // 高速試合モード中はこのUpdate()自体に入らない（TickFastModeへ分岐・切替時に畳む）。
            _homeRunTelop?.Tick(Time.deltaTime);

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

            // 守備伝令の場面（issue #1174）。交代テロップより先に流し、終わるまで進行を止めて待つ。
            if (_timeoutScenePending != null && !_timeoutSceneOn) TryBeginTimeoutScene();
            if (_timeoutSceneOn)
            {
                _timeoutSceneClock += Time.deltaTime * _speed;
                // 3Dの場面はリグの「散り終えた」で終わるが、役者が欠ける等で終わりが来ない場合に備えて
                // 上限を置く（集まる＋話す4秒＋散る2.6秒で通常は15秒前後）。上限が無いと自動進行が止まる。
                bool done = _timeoutSceneRig
                    ? (_duelRig == null || !_duelRig.TimeoutSceneActive || _duelRig.TimeoutSceneDone
                       || _timeoutSceneClock >= TimeoutSceneSafetyCapSeconds)
                    : _timeoutSceneClock >= subTelopHoldSeconds;
                if (done) EndTimeoutScene();
                return;
            }

            // 交代テロップ（issue #920）。イニング間画面と同じく進行を止めて待つ。溜まっていれば次の1枚を出す。
            if (!_subTelopOn) TryBeginSubTelop();
            if (_subTelopOn)
            {
                _subTelopClock += Time.deltaTime * _speed;
                if (_subTelopClock >= subTelopHoldSeconds) EndSubTelop();
                return;
            }

            // 判定オーバーレイは自前の寿命で消す。pitchCallHoldSeconds は出現・消失を含めた総寿命なので、
            // 畳む 0.16 秒前に is-enter を戻して縮みながら消えるところまでを窓の中で終わらせる
            // （対決ビューの捕手据え置きと窓を揃えるため）。
            // ただし3Dの守備ビューに切り替わったらその場で消す（2026-11-03 監督報告「サードが見えない」:
            // 盤面の真ん中に残った球種・球速が、三塁線を向いたカメラの三塁手にちょうど重なっていた）。
            if (_pitchCallOn && FieldPlayActive) HidePitchCall();
            if (_pitchCallOn)
            {
                _pitchCallClock += Time.deltaTime * _speed;
                _pitchCallTelop?.Tick(_pitchCallClock, pitchCallHoldSeconds, PitchCallInSeconds, PitchCallOutSeconds);
                if (_pitchCallClock >= pitchCallHoldSeconds - CallFadeSeconds)
                    _pitchCall?.AddToClassList("is-enter");
                if (_pitchCallClock >= pitchCallHoldSeconds) HidePitchCall();
            }
            if (_runnerGoClock >= 0f)
            {
                _runnerGoClock -= Time.deltaTime * _speed;
                if (_runnerGoClock < 0f) HideRunnerGo();
            }
            // 得点オーバーレイも自前の寿命で加算演出→消去まで進める（issue #60）。
            if (_scoreCallOn) AdvanceScoreCall(Time.deltaTime * _speed);
            // 試合開始/終了テロップ（issue #442・画像化は #1179）。「ゲームセット」は得点テロップと
            // サヨナラのホームラン演出（#1176・ホームランは打ち切らない）の両方が済んでから掲げる。
            // 寿命は再生速度で縮む（issue #1179「判定のように速度へ追従」）。
            if (_gameSetPending && _duelRig != null && _duelRig.WalkoffSceneActive) _walkoffSceneClock += Time.deltaTime;
            if (_gameSetPending && !_scoreCallOn && !(_homeRunTelop?.IsPlaying ?? false) && WalkoffReadyForGameSet())
            {
                _gameSetPending = false;
                ShowGameTelop(isGameSet: true);
                // 器が見つからない画面でも結果画面への受け渡しだけは必ず行う（遷移を人質にしない）。
                if (!_gameTelopOn) FinishGameEndHandOff();
            }
            if (_gameTelopOn)
            {
                _gameTelopClock += Time.deltaTime * _speed;
                _gameTelop?.Tick(_gameTelopClock);
                if (_gameTelopClock >= gameTelopHoldSeconds)
                {
                    // 投球/プレー中に寿命が切れたらフラグだけ下ろし、器は畳まない。旧・判定テロップと器を共用していた
                    // 時代の分岐（プレー再生が器を先取りしていたら判定の表示中に消さない）の名残。いまは専用の game-telop 層で
                    // 判定とは別の器になり、窓では _gameTelopOn の間は初球を投げさせない（下）ので、ここへ来ることは通常無いはず。
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
                // 必ず窓の中で尽きてから投球が始まる。旧実装は器を判定テロップと共用していたので、投球フェーズへ
                // 入ってから寿命が切れると「畳めず次の判定表示まで残留する」ことがあった（寿命を2.1sへ延ばした
                // 2026-08-05 に顕在化）。いまは専用の game-telop 層（issue #1179）だが、初球待ちはこの値に揃えたまま。
                if (_gameTelopOn) return;
                // 打席の頭の場内放送（issue #919）。打者を見る間と並行して流し、終わるまで1球目を待たせる。
                var announcing = HoldForAnnouncement();
                _windowClock += Time.deltaTime * _speed;
                if (_windowClock < _windowHold) return;
                if (announcing) { _windowClock = _windowHold; return; }   // 待った分を次の球へ持ち越さない
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
                    // 守備ビューで見せるファウル（issue #1074）もインプレーと同じく、球が画面から出たら切り替える
                    if (_battedHoldFoul != null)
                    {
                        if ((_duelRig?.BattedOutOfFrame ?? true) || _battedHoldClock >= BattedHoldSafetyCapSeconds)
                        {
                            _battedHoldActive = false;
                            var foul = _battedHoldFoul;
                            _battedHoldFoul = null;
                            EnterFoulFieldPlay(foul);
                        }
                        return;
                    }
                    // 「打った瞬間」の本塁打は、球が画面から出ても打者を映したまま見送る（issue #704。
                    // 監督決定: 当たりの違いはカメラだけで見せる）。守備ビューへはこの秒数が経ってから切り替える
                    if (IsNoDoubtHomeRun)
                    {
                        if (_battedHoldClock >= NoDoubtWatchSeconds)
                        {
                            _battedHoldActive = false;
                            EnterBattedBallOrHold();
                        }
                        return;
                    }
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
                    AddPitchLocationMark(pt, _pitchIdx + 1);   // 投球チャート（issue #1541）。判定と同時に印を打つ
                    // 球数もこの瞬間に1増やす（issue #1689）。HUD 全体は組み直さない＝打球の結果（今日の成績）を先に出さない。
                    _paPitchesCalled = System.Math.Max(_paPitchesCalled, _pitchIdx + 1);
                    UpdateShownPitchCounts();
                    // 打席途中の牽制（issue #486/#515）はもうここでは見ない――この球の**前**の事象なので
                    // BeginPitchRender の頭（この球の振りかぶりに入る前）で既に再生済み。
                    // 打席途中の盗塁（issue #485）: この球を実際に投げ終えた今、守備ビューで
                    // 「走者が走る→捕手が投げる→塁で判定」を見せてから塁・アウトを動かす（先に動かすと
                    // 走る前に着いて見える）。再生が終わったら打席の続きへ戻る（次打席へは進めない）。
                    // 同じ球に牽制が乗っていても（牽制セーフの直後の盗塁企図）ここで初めて再生する
                    // （issue #523。走者は投手の投球動作を見てスタートするため、投球より前には走らない）。
                    if (q.Live?.StealPlay is { } stealPlay)
                    {
                        EnterStealPlay(stealPlay);
                        return;
                    }
                    // スクイズ不成立の挟殺（issue #1061）: 暴投・捕逸と同じく「この球そのものの結果」。
                    // 球の落ち先（捕手が捕る）から始まる挟殺を先に見せ、同じ球で暴投・捕逸も起きていれば続けて連結する。
                    if (q.Live?.SqueezeRundownPlay is { } squeezeRundownPlay)
                    {
                        EnterSqueezeRundownPlay(squeezeRundownPlay);
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
                        PostMidPaScore(q.Live);   // 打席途中に入った点もここで掲げる（issue #628）
                    }
                    if (IsTerminalPitch(_pitchIdx))
                    {
                        // インプレー打球は対決構図のまま球がビューポートから出るまで据え置く
                        // （design-18 §4.5-④・issue #481）。三振・四球等（Play無し）は見せるものが
                        // 無いので即座に結果保持へ入る。
                        // 「打った瞬間」の本塁打は球が画面の外でも据え置く＝打者を映したまま見送る（issue #704）
                        if (_current.Play != null && DuelPacingActive
                            && (IsNoDoubtHomeRun || !(_duelRig?.BattedOutOfFrame ?? true)))
                        {
                            _battedHoldActive = true;
                            _battedHoldClock = 0f;
                            // 見送っている間は振り抜いた形のまま止める（既定の据え置き0.8sで構えへ戻ると
                            // 「次の球を待つ打者」に見える。issue #704）
                            if (IsNoDoubtHomeRun) _duelRig?.HoldBatterFinish(NoDoubtWatchSeconds);
                            return;
                        }
                        EnterBattedBallOrHold();   // 打席を終わらせた球は判定直後に打球再生／結果保持へ
                        return;
                    }
                    // 見どころのあるファウル（issue #1074: あと一歩届かなかったファウルフライ・ポール際の大ファウル）は
                    // 守備ビューで見せる。インプレーと同じく、打球が対決ビューの画面から出るまで据え置いてから切り替える。
                    // 俯瞰固定（対決ビューを使わない）中は従来どおり盤面で流すだけ。
                    if (_pendingFoulFlight != null && _pendingFoulFlight.FoulView != KokoSim.Engine.Match.Fielding.FoulViewKind.None
                        && DuelPacingActive && _duelRig != null && _duelRig.Available)
                    {
                        var foul = _pendingFoulFlight;
                        _pendingFoulFlight = null;
                        if (!_duelRig.BattedOutOfFrame)
                        {
                            _battedHoldActive = true;
                            _battedHoldClock = 0f;
                            _battedHoldFoul = foul;
                            return;
                        }
                        EnterFoulFieldPlay(foul);
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
                        // 投手はリリースの時点で「duelPitchIntervalSeconds 後に次を投げる」前提で振りかぶりを
                        // 予約済み。下で間合いを打球の飛行時間まで延ばし、時計も接触時刻へ巻き戻すので、
                        // 次のリリースまでの残りが変わった分だけ予約もずらす（issue #770）。
                        var releaseRemainBefore = duelPitchIntervalSeconds - _pitchClock;
                        // 「ファール」の判定は守備ビューで見せるための物（issue #1134）。ここは対決の構図のまま
                        // 球審コール（ShowPitchCall）が出る経路なので、外して二重に出さない。
                        _view.SetPlay(_pendingFoulFlight with { Judgements = System.Array.Empty<PlaybackJudgement>() });
                        _foulFlightHoldSeconds = (float)_pendingFoulFlight.Dur;
                        _pitchBallVisible = true;
                        // 対決ビューから来た場合は接触の瞬間から（投球セグメントの二重表示を避ける,
                        // issue #330）＋対決ビューで見せた打球のぶんは飛ばす＝**対決ビューが今出している
                        // 位置に俯瞰を合わせる**（両者は同じ実時間で進むので以降もずれない）。
                        // 俯瞰固定中は対決ビュー側で見せていないので投球から流す。
                        _pitchClock = wasDuelOn ? (float)ContactMomentOf(_pendingFoulFlight, shownFoul) : 0f;
                        _view.SetTime(_pitchClock);
                        _pendingFoulFlight = null;
                        if (DuelPacingActive && !FoulReleaseShiftDisabledForCapture)
                        {
                            var releaseRemainAfter = Mathf.Max(duelPitchIntervalSeconds, _foulFlightHoldSeconds.Value)
                                                     - _pitchClock;
                            _duelRig?.ShiftNextRelease(releaseRemainAfter - releaseRemainBefore);
                        }
                    }
                }
                if (_pitchCallDone)
                {
                    TryPreResolveNextPitch();   // 次の球の振りかぶりに入ったら、その球を先に解決（issue #696）
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
                if (_t > _pickoffEndAt + FinishGraceSeconds) FinishPickoffView();
                return;
            }
            // 打席途中の盗塁の再生（issue #485）: 打球（_current.Play）とは別プレーなので専用に回し、
            // 終わったら FinishPaView（次打席へ）ではなく打席の続きへ戻す（FinishStealView）。
            if (_stealPlay != null)
            {
                RenderStealFrame();
                if (_t > _stealEndAt + FinishGraceSeconds) FinishStealView();
                return;
            }
            // 打席途中のスクイズ挟殺の再生（issue #1061）: 盗塁の再生（あれば）が終わった後に連結される。
            if (_squeezeRundownPlay != null)
            {
                RenderSqueezeRundownFrame();
                if (_t > _squeezeRundownEndAt + FinishGraceSeconds) FinishSqueezeRundownView();
                return;
            }
            // 打席途中の暴投・捕逸の再生（issue #487）: 盗塁の再生（あれば）が終わった後に連結される
            // （FinishStealView）ので、盗塁より後にチェックする。
            if (_batteryMissPlay != null)
            {
                RenderBatteryMissFrame();
                if (_t > _batteryMissEndAt + FinishGraceSeconds) FinishBatteryMissView();
                return;
            }
            // 守備ビューで見せるファウル（issue #1074）
            if (_foulFieldPlay != null)
            {
                RenderFoulFieldFrame();
                if (_t > _foulFieldEndAt + FinishGraceSeconds) FinishFoulFieldView();
                return;
            }
            // 打席途中の振り逃げの再生（issue #488）: 盗塁・牽制・暴投/捕逸の再生（あれば）が
            // すべて終わった後に連結されるので、最後にチェックする。
            if (_dropThirdStrikePlay != null)
            {
                RenderDropThirdStrikeFrame();
                if (_t > _dropThirdStrikeEndAt + FinishGraceSeconds) FinishDropThirdStrikeView();
                return;
            }
            if (_current.Play != null)
            {
                RenderReplayFrame();
                if (_t > PlayEndTime() + FinishGraceSeconds) FinishPaView();
            }
            else
            {
                // 押し出しの得点は据え置きの途中で掲げる（issue score-display-before-home-touch）。
                if (_nonBattedScoreAt is { } at && _t >= at) FlushPendingNonBattedScore();
                if (_t >= 0) FinishPaView();
            }
        }

        /// <summary>
        /// 待たせていた「打球の無い得点」を掲げる（issue score-display-before-home-touch）。
        /// 据え置きの途中で来るのが通常だが、スキップ等で打席が畳まれる経路でも点を落とさないよう
        /// <see cref="FinishPaView"/> からも呼ぶ（二度掲げないよう予約は必ず消す）。
        /// </summary>
        private void FlushPendingNonBattedScore()
        {
            if (_nonBattedScoreAt is null) return;
            _nonBattedScoreAt = null;
            if (_current != null) PostResolvedScore(_current.Inning, _current.IsTop);
        }

        /// <summary>再生を打ち切ってから次打席へ移るまでの余韻[秒]。</summary>
        private const double PlayTailSeconds = 0.6;

        /// <summary>再生打ち切り判定（<c>_t &gt; XxxEndAt + FinishGraceSeconds</c>）の余韻[秒]。
        /// <see cref="ClampBeforeWideCameraCut"/> 参照。</summary>
        private const double FinishGraceSeconds = 0.3;

        /// <summary>
        /// 打席途中プレー（盗塁・牽制・暴投捕逸・振り逃げ）の打ち切り時刻を、3D守備カメラが
        /// 全景へハードカットする時刻（<see cref="CameraPlanBuilder.WideStartTime"/>）より
        /// 確実に手前で止める（issue #522）。
        ///
        /// <see cref="MidPaPlayEndTime"/> 等は元々 <c>lastEvent + PlayTailSeconds(0.6)</c> 基準だが、
        /// カメラのハードカットは <c>lastEvent + CameraPlanBuilder.ReturnToWideDelaySeconds(0.9)</c>。
        /// 打ち切り判定に使う余韻 <see cref="FinishGraceSeconds"/>(0.3) を足すと
        /// <c>0.6+0.3=0.9</c> でカット時刻とちょうど一致してしまい、判定が発火する前に
        /// レンダリング（<c>RenderStealFrame</c> 等は判定より前に呼ばれる）が1コマ、
        /// カット後の全景（縦155m級）を描いてしまう（実測: 枠が10.5m→147.3mへ1コマで飛ぶ）。
        /// 打ち切りをハードカットより確実に手前で終わらせることで、そのコマを再生の外へ出す。
        ///
        /// <para>⚠ 全景ハードカットは廃止した（issue field-view-cut-back-sooner ④・
        /// <see cref="DuelTuning.FieldReturnsToWide"/>）ので、この clamp が守っていた「全景の1コマ」
        /// はもう出ない。それでも残しているのは<b>打席途中プレーの尺そのもの</b>が
        /// この値で決まっているため（外すと盗塁・牽制の再生が伸びる＝この issue が頼まれていない変更に
        /// なる）。旧挙動（FieldReturnsToWide=true）へ戻したときの保険も兼ねる。</para>
        /// </summary>
        private static double ClampBeforeWideCameraCut(double end, PlaybackPlay play)
        {
            var wideStart = CameraPlanBuilder.WideStartTime(play);
            return double.IsFinite(wideStart) ? System.Math.Min(end, wideStart - FinishGraceSeconds) : end;
        }

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
            // 規則は engine の純関数（PlaybackDisplaySync.LastActionTime）が単一ソース＝
            // 凡打の打者走者の駆け抜けを待たない扱い（issue field-view-cut-back-sooner ①）も含む。
            var lastEvent = PlaybackDisplaySync.LastActionTime(play);
            var end = System.Math.Min(play.Dur, lastEvent + PlayTailSeconds);

            // 判定オーバーレイ（#59）の表示窓 [T1, T1+hold) を必ず含める（issue #236 症状A）。
            // 内野ゴロの打者走者は「送球到達＝アウト成立」より後に一塁へ着く（T1＞ResAt）ので、
            // 早切りだけだと最頻のアウトが一度も出ない。Dur でクランプしない（SetTime は t の純関数で、
            // Dur を超えた時刻を評価しても最終フレームを保持するだけ）。
            var judged = PlaybackEvaluator.LastJudgementEndTime(play, _view.JudgementHoldSeconds);
            if (judged.HasValue) end = System.Math.Max(end, judged.Value);

            // ⚠ 得点テロップ（#60）の寿命も含める。FinishPaView が HideScoreCall するので、
            // 含めないと**掲げた直後にテロップごと消える**（本塁打は必ずここを通る＝早切りの直撃）。
            // テロップは最後の走者の本塁到達（issue #438/#643）で開くので、そこから最終ホールドまで見せ切る。
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

            // 大型スコア（issue #438/#643）: **最後の得点走者の本塁到達**（プレー時計）で、加算後の
            // 最終スコアのまま開く。加点前スコアを先に見せると「1-1→2-1」が2点入ったように読める（#643）。
            if (_runScoreShowAt.Count > 0 && _runScoreShown < _runScoreShowAt.Count
                && _t >= _runScoreShowAt[_runScoreShowAt.Count - 1])
            {
                BeginScoreCall(_current.IsTop, _runScoreShowAt.Count);
                _runScoreShown = _runScoreShowAt.Count;
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
            // 掲げる点は打席の得点（RunsScored）でなく「まだ掲示板に出していない点」で数える（issue #628）。
            // ①打席途中に暴投・本盗で入って既に掲げた点を二度祝わない ②RunsScored に乗らない得点
            //（暴投での生還・重盗の生還。MatchProgression のコメント参照）を取りこぼさない。
            _runScoreShowAt = PlaybackDisplaySync.RunScoreTimes(play, RunsNotYetPosted(item.IsTop), scoreCallStepSeconds, item.OutsBefore);
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
            // 掲げる前に「まだ出していない点」を数える（PostLineScore が基準を更新するため, issue #628）。
            var gained = RunsNotYetPosted(item.IsTop);
            _lineScorePosted = true;
            PostLineScore(item.Inning, item.IsTop, finished: false);
            BeginScoreCall(item.IsTop, gained);
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
            // lsc-root は LineScorePanel テンプレートの内部名なので、fastmode-board 追加後は
            // inning-break のインスタンスと同名が2つ並ぶ。_root の丸ごと検索（単一マッチ）だと
            // ツリー順で先に来る側しか埋まらず、もう一方が空のまま表示される（2026-08-25 実害）。
            // 各インスタンスの器（自分のサブツリーだけ）を渡して個別に埋める。
            // 球場名は engine の観測データ（GameContext.StadiumName）そのもの＝実際にその試合が開催された
            // 球場（Stadium）の名前（PR #883 レビュー指摘。大会タイトルの流用を撤回）。天気と同じく
            // ここで抽選し直さない（issue #803）。球場割当が未配線の大会（地区・神宮）・練習試合は null＝空欄。
            var venueName = snap.StadiumName ?? "";
            LineScorePanel.Fill(_inningBreak, snap.AwayLine, snap.HomeLine, inning, isTop, finished, venueName, snap.Weather);
            LineScorePanel.Fill(_fastModeBoard, snap.AwayLine, snap.HomeLine, inning, isTop, finished, venueName, snap.Weather);
            ScoreBug.Fill(_root, snap.AwayLine, snap.HomeLine, inning, isTop, finished);
            // 掲げた点を控える（issue #628）。次に掲げるときの「まだ出していない点」の基準になる。
            _postedAwayRuns = snap.AwayLine.Runs;
            _postedHomeRuns = snap.HomeLine.Runs;
        }

        /// <summary>まだ掲示板へ出していない得点（攻撃側ぶん）。負にはしない。</summary>
        private int RunsNotYetPosted(bool isTop)
        {
            if (_prog == null) return 0;
            var snap = _prog.Snapshot();
            var gained = isTop ? snap.AwayLine.Runs - _postedAwayRuns : snap.HomeLine.Runs - _postedHomeRuns;
            return Mathf.Max(0, gained);
        }

        /// <summary>
        /// 打球の無い打席確定（四球・死球・三振＝押し出しはここ）で掲示板を掲げる（issue #628）。
        /// 走者の到達アニメが無いので待つ理由がなく、結果テロップと同じ瞬間に出す。
        /// 打球のある経路の掲示時刻（#438「最後の走者が本塁へ帰ってから」）には触らない。
        /// </summary>
        /// <returns>この掲示で新たに出した得点（＝据え置きを伸ばすかの判断に使う）。</returns>
        private int PostResolvedScore(int inning, bool isTop)
        {
            if (_prog == null || _lineScorePosted) return 0;
            var gained = RunsNotYetPosted(isTop);
            _lineScorePosted = true;
            PostLineScore(inning, isTop, finished: false);
            // 得点コール（issue #60）も出す（監督決定 2026-08-13）。走者の到達アニメが無いので
            // 掲示の瞬間に最終スコアで開く。
            if (gained > 0) { HidePitchCall(); BeginScoreCall(isTop, gained); }
            return gained;
        }

        /// <summary>
        /// 打席途中のプレー（本盗の生還・暴投/捕逸での生還）で入った点を、その再生の終わりで掲げる（issue #628）。
        ///
        /// <para>エンジンの集計（<c>Snapshot</c>）は打席まるごと解決済みなので、そのまま掲げると
        /// <b>同じ打席の後の得点まで先に出る</b>（#438 で直した得点の先出しの再発）。1球観測が持つ
        /// 「その球の時点の得点」と集計が一致するときだけ掲げる＝残りがあるなら打球側の掲示に任せる。</para>
        /// </summary>
        private void PostMidPaScore(LivePitch live)
        {
            if (_prog == null || live == null) return;
            var snap = _prog.Snapshot();
            if (live.AwayScore != snap.AwayLine.Runs || live.HomeScore != snap.HomeLine.Runs) return;
            var gained = RunsNotYetPosted(_shownIsTop);
            if (gained <= 0) return;
            PostLineScore(_shownInning, _shownIsTop, finished: false);
            HidePitchCall();
            BeginScoreCall(_shownIsTop, gained);
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
            if (_pitchCall == null) return;
            // プレイボール／ゲームセット／ホームランは打ち切らない＝その間に来た判定は捨てる（issue #1179）。
            if (_gameTelopOn || (_homeRunTelop?.IsPlaying ?? false)) { HidePitchCall(); return; }

            // 画像テロップ（issue #1179）。1プレー1枚。
            var callClass = PitchCallClassOf(pt);

            // 計器表示（球種・球速）。実1球記録がある打席だけ値が入る＝合成投球列では丸ごと畳む。
            var note = PitchNoteJp(pt);
            // 外した球（ウエスト・issue #1472 ついで）は球種名を出さない＝球種は配球の乱数で決まった本来の球のままなので。
            var ptype = pt.Pitchout ? "ウエスト"
                : pt.PitchType.HasValue ? PitchTypeLabels.Jp(pt.PitchType.Value) : "";
            // ウエストは球速も出さない（オーナー判断 2026-11-23）＝本来の球の球速なので。単位ごと畳む。
            var speed = !pt.Pitchout && pt.VelocityKmh.HasValue ? Mathf.RoundToInt((float)pt.VelocityKmh.Value).ToString() : "";
            SetOptionalLabel(_pitchCallNote, note);
            SetOptionalLabel(_pitchCallPitchType, ptype);
            SetOptionalLabel(_pitchCallSpeed, speed);
            // 単位は球速があるときだけ（"km/h" が単独で浮かないように）。
            if (_pitchCallUnit != null)
                _pitchCallUnit.style.display = speed.Length == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            var hasReadout = note.Length > 0 || ptype.Length > 0 || speed.Length > 0;
            if (_pitchCallDetail != null)
                _pitchCallDetail.style.display = hasReadout ? DisplayStyle.Flex : DisplayStyle.None;

            if (callClass == null && !hasReadout) { HidePitchCall(); return; }

            if (callClass != null)
            {
                _pitchCallTelop?.SetFace(callClass);
                _pitchCallTelop?.Tick(0.0, pitchCallHoldSeconds, PitchCallInSeconds, PitchCallOutSeconds);
            }
            else
            {
                _pitchCallTelop?.Hide();
            }

            ShowCallHost(_pitchCall);
            _pitchCallOn = true;
            _pitchCallClock = 0f;
        }

        /// <summary>
        /// 1球の判定（球審コール）テロップの画像面クラス（tl-call-xxx）を返す（issue #1179）。四球成立の瞬間（4球目）は
        /// 「フォアボール」、3ストライク目は「三振」の画像にする。どれにも当たらない球種は null（出さない）。
        /// 打球プレーの判定の画像面は Match2DPlaybackElement.UpdateJudgement 内の switch が持つ（こちらは1球判定だけ）。
        /// </summary>
        private static string PitchCallClassOf(PitchToken pt)
        {
            switch (pt.Kind)
            {
                case PitchKind.Ball: return pt.BallsAfter >= 4 ? "tl-call-walk" : "tl-call-ball";
                case PitchKind.CalledStrike: return pt.StrikesAfter >= 3 ? "tl-call-strikeout" : "tl-call-strike";
                case PitchKind.SwingingStrike: return pt.StrikesAfter >= 3 ? "tl-call-strikeout" : "tl-call-strike";
                case PitchKind.Foul: return pt.StrikesAfter >= 3 ? "tl-call-strikeout" : "tl-call-foul";
                case PitchKind.HitByPitch: return "tl-call-hitbypitch";
                default: return null;
            }
        }

        /// <summary>
        /// 「走った！」の吹き出しを、この球でスタートを切った走者の塁の側に出す（issue #696）。
        /// 読むのは engine の観測 <see cref="LivePitch.RunnersStarted"/> だけ＝盗塁・エンドラン・スクイズを
        /// 表示側で見分けない。走者がいなければ何もしない（前の吹き出しも残さない）。
        /// </summary>
        private void ShowRunnerGo(LivePitch live)
        {
            if (live != null && ReferenceEquals(live, _runnerGoFor)) return;   // 振りかぶりで出し済み
            _runnerGoFor = live;
            HideRunnerGo();
            if (_fastMode || live == null || live.RunnersStarted == 0) return;
            var any = false;
            for (var b = 0; b < _runnerGo.Length; b++)
            {
                if (_runnerGo[b] == null || !live.RunnerStarted(b + 1)) continue;
                _runnerGo[b].style.display = DisplayStyle.Flex;
                any = true;
            }
            if (any) _runnerGoClock = runnerGoHoldSeconds;
        }

        private void HideRunnerGo()
        {
            _runnerGoClock = -1f;
            foreach (var b in _runnerGo)
                if (b != null) b.style.display = DisplayStyle.None;
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
        // 並びは自校－相手校固定（先攻/後攻に依存しない）。**加算後の最終スコアだけ**を掲げ、
        // 一呼吸おいて自動的に消える（issue #643）。加点前スコア→+1 の加算演出は、1-1 から 2-1 への
        // 変化が「2点入った」ように読めるので撤去した。
        // 開く時刻（issue #438）: 通常再生は**最後の得点走者の本塁到達**（プレー時計・RenderReplayFrame が
        // PlaybackDisplaySync.RunScoreTimes の最後の時刻で駆動）。打球の無い得点・打席途中の得点・
        // キャプチャ（結果時点へ一気に飛ぶ）は掲示の瞬間に開く。
        /// <summary>
        /// 得点コール（issue #60）を始める。<paramref name="runs"/> は<b>これから掲げる点</b>だけ
        /// （issue #628）。打席の得点（<c>RunsScored</c>）で数えると、暴投での生還のように打席に
        /// 紐づかない点や、打席途中に既に掲げた点を二度祝う。スコアはエンジン集計から加点前を逆算し、
        /// その点を足した最終値を出す。
        /// </summary>
        private void BeginScoreCall(bool isTop, int runs)
        {
            if (_scoreCall == null || _scoreCallOwnNum == null || _scoreCallOppNum == null) return;
            if (_prog == null || runs <= 0) return;

            var ownIsAway = _managerIsAway;
            var snap = _prog.Snapshot();
            var awayBefore = isTop ? snap.AwayLine.Runs - runs : snap.AwayLine.Runs;
            var homeBefore = isTop ? snap.HomeLine.Runs : snap.HomeLine.Runs - runs;
            var awayAfter = isTop ? awayBefore + runs : awayBefore;
            var homeAfter = isTop ? homeBefore : homeBefore + runs;
            _scoreCallOwnScore = ownIsAway ? awayAfter : homeAfter;
            _scoreCallOppScore = ownIsAway ? homeAfter : awayAfter;
            _scoreCallClock = 0f;
            _scoreCallOn = true;
            if (!_fastMode)
            {
                KokoSim.Unity.Audio.MatchBrassBand.Scored(isTop);
                KokoSim.Unity.Audio.MatchSfx.PlayCheer();
            }

            if (_scoreCallOwnName != null) _scoreCallOwnName.text = ownIsAway ? _awayDisp : _homeDisp;
            if (_scoreCallOppName != null) _scoreCallOppName.text = ownIsAway ? _homeDisp : _awayDisp;
            RefreshScoreCallLabels();
            _scoreCall.style.display = DisplayStyle.Flex;
        }

        private void AdvanceScoreCall(float dt)
        {
            _scoreCallClock += dt;
            if (_scoreCallClock >= scoreCallFinalHoldSeconds) HideScoreCall();
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
            if (_scoreCall != null) _scoreCall.style.display = DisplayStyle.None;
        }

        // ── 試合開始/終了テロップ「プレイボール」「ゲームセット」（issue #442） ──
        // 器は専用の全画面層（game-telop・issue #1179）。当初（issue #442）は判定テロップ（judgement-call）を流用していたが、
        // いまは別の器で、掲げている間は判定テロップ側を CallsSuppressed で出さない（時間帯が重ならないよう
        // 「最初のプレーの前」と「終局後」だけに掲げる）。
        /// <summary>プレイボール（false）／ゲームセット（true）を掲げる（issue #1179）。
        /// 動きはGameTelopViewが毎フレーム(Tick)付ける＝ここでは変数切替と表示だけ。</summary>
        private void ShowGameTelop(bool isGameSet)
        {
            if (_gameTelopHost == null || _gameTelop == null) return;
            _gameTelop.Play(isGameSet, KokoSim.Unity.Shell.SchoolAccent.ForManager());
            _gameTelopHost.style.display = DisplayStyle.Flex;
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
        public void ShowGameTelopForCapture(bool gameSet) => ShowGameTelop(gameSet);

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

        /// <summary>
        /// スクショ用: 判定テロップ（Calls画像11種）を1枚だけ強制表示する（issue #1179）。
        /// "tl-call-strike" 等のクラス名で指定する（1球判定は <see cref="PitchCallClassOf"/>・打球プレーの判定は
        /// Match2DPlaybackElement.UpdateJudgement 内の switch が実戦で使う値そのまま。文言→画像面の対応を撮影側に持たせない）。
        /// </summary>
        public void ShowJudgementCallForCapture(string callClass) => _view?.ShowCallForCapture(callClass);

        /// <summary>スクショ用: 判定テロップ（Calls画像）を畳む。</summary>
        public void HideJudgementCallForCapture() => _view?.HideJudgement();

        /// <summary>スクショ用: ホームランテロップ（約3.2秒の専用アニメ）を強制再生する（issue #1179）。</summary>
        public void ShowHomeRunTelopForCapture(double distanceM) => _homeRunTelop?.Play(distanceM);

        /// <summary>スクショ用: ホームランテロップを畳む。</summary>
        public void HideHomeRunTelopForCapture() => _homeRunTelop?.Hide();

        private static bool IsStrikeCall(KokoSim.Engine.Match.AtBat.PitchKind k) =>
            k == KokoSim.Engine.Match.AtBat.PitchKind.CalledStrike || k == KokoSim.Engine.Match.AtBat.PitchKind.SwingingStrike;

        // 判定の内訳（見逃し/空振り）＝計器表示の左端に添える補助情報。
        // 球種・球速は数字と和文を混植しないよう別ラベルへ分けたので、ここでは組み立てない（issue #479）。
        private static string PitchNoteJp(PitchToken pt)
        {
            return pt.Kind == KokoSim.Engine.Match.AtBat.PitchKind.CalledStrike ? "見逃し"
                : pt.Kind == KokoSim.Engine.Match.AtBat.PitchKind.SwingingStrike ? "空振り" : "";
        }


        // いま掲示板（ScoreBug の塁ダイヤ）に出している塁。タイムのパネルはこれと同じ値を出す（issue #1534 項目8）。
        private bool _shownBaseFirst, _shownBaseSecond, _shownBaseThird;

        private void SetBases(bool first, bool second, bool third)
        {
            _shownBaseFirst = first; _shownBaseSecond = second; _shownBaseThird = third;
            _bases?[0]?.EnableInClassList("is-on", first);   // base-1（一塁）
            _bases?[1]?.EnableInClassList("is-on", second);  // base-2（二塁）
            _bases?[2]?.EnableInClassList("is-on", third);   // base-3（三塁）
            // 応援曲は塁の表示に合わせて切り替える（得点圏に走者＝チャンスの曲）。
            // 試合が始まる前・攻守交代の間・高速試合モード中・終局後は鳴らさない。
            if (_shownAnyHalf && !_inningBreakOn && !_fastMode && !_gameOver)
                KokoSim.Unity.Audio.MatchBrassBand.SetOffense(_shownIsTop, second || third);
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
                if (speed >= 4f) KokoSim.Unity.Audio.MatchAnnouncer.Stop();   // ×4では場内放送を流さない（issue #919）
                SetFastMode(false);   // 数値速度を選び直す＝高速試合モードの解除操作を兼ねる（専用の「戻る」ボタンは作らない）
                foreach (var sb in _speedButtons) sb.EnableInClassList("chip-btn--on", sb == b);
            };
            b.clicked += handler;
            _speedHandlers.Add((b, handler));
        }

        // 高速試合モードの入口（design-06 §3.4）。速度ボタンと同じ排他選択グループの一員として登録する
        // （_speedButtons への相乗り＝OnDisable の一括解除もそのまま効く）。_speed 自体は変更しない
        // （直前の速度倍率のまま「半イニング送りの間隔」にも流用する＝新しいペース系統を増やさない）。
        private void WireFastMode(string name)
        {
            var b = _root.Q<Button>(name);
            if (b == null) return;
            _speedButtons.Add(b);
            System.Action handler = () =>
            {
                // 終局後は高速へ入れない。入るとゲームセットのテロップを畳んだうえ TickFastMode が _gameOver で
                // 止まり、結果画面への受け渡し（FinishGameEndHandOff）を起こすものが無くなって詰む。
                if (_gameOver) return;
                SetFastMode(true);
                foreach (var sb in _speedButtons) sb.EnableInClassList("chip-btn--on", sb == b);
            };
            b.clicked += handler;
            _speedHandlers.Add((b, handler));
        }

        /// <summary>
        /// 高速試合モードの切替。ON: 対決ビュー/2D俯瞰を隠して大型スコアボードへ差し替える。
        /// OFF: 表示を戻し、<see cref="AdvanceToNextWindow"/>（打席頭のウィンドウを開く既存の入口そのもの。
        /// 試合開始時の <see cref="StartAutoRun"/> と同じ経路）で1球進行ループへ戻す。
        ///
        /// <para><b>罠</b>: <see cref="MatchProgression.CurrentAtBat"/> は「<see cref="MatchProgression.AdvancePitch"/>
        /// が Pitch を返した直後に読む」契約（同プロパティのXMLコメント）で、<see cref="MatchProgression.Advance"/>
        /// 系（<see cref="TickFastMode"/> が使う <c>AdvanceUntilInningEnd</c>）の直後に読むと打席途中の値を
        /// 拾うことがある（実測: 2026-08-24、高速モード中に「3番 2-2」のような未確定値が出た）。
        /// 復帰時は必ずこの経路（内部で <c>AdvancePitch</c> を1回呼んで窓を張り直す）を通す。</para>
        /// </summary>
        private void SetFastMode(bool on)
        {
            if (_fastMode == on) return;
            _fastMode = on;
            _fastModeClock = 0;
            if (_fastModeBoard != null) _fastModeBoard.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
            var tacticsHost = _root?.Q<VisualElement>("tactics-host");
            if (tacticsHost != null) tacticsHost.style.display = on ? DisplayStyle.None : DisplayStyle.Flex;
            if (on)
            {
                KokoSim.Unity.Audio.MatchBrassBand.Rest();   // 高速試合モードは音を出さない
                KokoSim.Unity.Audio.MatchAnnouncer.Stop();   // 場内放送も（issue #919）
                _duelRig?.SetPitcherRattled(false);          // 高速試合モードは動揺の絵も出さない（issue #1178）
                // 振りかぶりで先に解決した球（issue #696）はエンジン側では投げ終わっている＝描かずに捨てる。
                _preStep = null;
                HideRunnerGo();
                if (_fieldHost != null) _fieldHost.style.display = DisplayStyle.None;
                if (_duelHost != null) _duelHost.style.display = DisplayStyle.None;
                _plChart?.ClearMarks();
                if (_plChartHost != null) _plChartHost.style.display = DisplayStyle.None;
                // 演出系の一過性オーバーレイ（プレイボール等のテロップ・判定・得点コール・交代ポップアップ・
                // イニング間画面）は自前の実時間タイマーで消える設計だが、そのタイマーは高速試合モードの
                // Update() 分岐では回さない。ここで畳んでおかないと消えるきっかけを永遠に失い、
                // 盤面に張り付いたまま高速試合モードの間じゅう残る（EndGame の EndFieldPlay と同じ理由）。
                // サヨナラの予約は先に畳む（EndFieldPlay が予約した場面をその場で始めてしまい、以後 Tick されず凍る）。
                _duelRig?.EndWalkoffScene();
                _duelRig?.EndDuelThrow();   // 盗塁・牽制の送球を対決ビューで見せている最中（守備ビューへ渡す前）に入った場合
                EndFieldPlay();
                // 打席途中の再生状態も戻す。残すと戻った後の次の打席が前の打席の盗塁等を再生する。
                ClearMidPaPlayState();
                _view.HideJudgement();
                HideGameTelop();
                HidePitchCall();
                _homeRunTelop?.Hide();
                    if (_scoreCallOn) { _scoreCallOn = false; if (_scoreCall != null) _scoreCall.style.display = DisplayStyle.None; }
                if (_inningBreakOn) { _inningBreakOn = false; _inningBreakClock = 0f; _afterInningBreak = null; SetVisible(_inningBreak, false); }
                ClearSubTelop();   // 交代テロップも畳む（高速中の交代は実況履歴の1行だけ。issue #920）
                ClearTimeoutScene();   // 伝令の場面も（issue #1174）
                PostFastModeBoard();
            }
            else
            {
                ApplyDuelViewState();   // 対決ビュー/俯瞰の表示を _duelOn の状態へ戻す
                if (_prog != null && !_prog.IsFinished)
                {
                    _stage = LiveStage.Idle;
                    // 高速中に打席が進んだなら、窓で選んでいた1手は別の場面のもの＝押し込まず消す。
                    // 窓で止まったままなら（すぐ戻した）点灯はそのまま、同じ窓を開け直して次の球へ押し込む。
                    if (!_prog.IsAtPitchWindow) { ClearPerPaTacticsChoices(); ResetPitchTacticsChoice(); }
                    AdvanceToNextWindow();
                }
            }
        }

        /// <summary>
        /// 高速試合モードの進行（design-17 §6.2 向けに実装済みの早送りAPIをそのまま使う。結果は不変）。
        /// 実時間で <see cref="fastModeHalfInningSeconds"/>（×<see cref="_speed"/>）ごとに半イニング進め、
        /// スコアボードだけ更新する。試合終了は通常終了と同じ <see cref="EndGame"/> 経路に合流する。
        /// </summary>
        private void TickFastMode()
        {
            if (_gameOver || _paused || _prog == null) return;
            _fastModeClock += Time.deltaTime * _speed;
            if (_fastModeClock < fastModeHalfInningSeconds) return;
            _fastModeClock -= fastModeHalfInningSeconds;
            _prog.AdvanceUntilInningEnd();
            DrainSubstitutionEvents();   // 高速中の交代は実況履歴の1行だけ（止めない。issue #920）
            if (_prog.IsFinished) { EndGame(_prog.BuildResult()); return; }
            // 球数制限で投手交代待ちになった＝早送り中でも必ず一旦止めてプレイヤーに選ばせる。
            // 高速試合モードを解いて通常速度へ戻すと、直後の AdvanceToNextWindow が窓を開き直す
            // 過程で AwaitingForcedPitcherChange を検知し、交代パネルが自動的に開く。
            if (_prog.PendingForcedPitcherChange) { SetFastMode(false); return; }
            PostFastModeBoard();
        }

        /// <summary>
        /// 大型スコアボード＋左右スタメン列（現打者ハイライト）を今の進行状況へ更新する。BSO/塁は
        /// <see cref="MatchProgression.CurrentAtBat"/> が信頼できる区間（1球進行の窓）でしか安全に読めない
        /// （<see cref="SetFastMode"/> の罠コメント参照）ため、高速モード中は更新しない＝入る前の値のまま残る。
        /// </summary>
        private void PostFastModeBoard()
        {
            if (_prog?.Current is not { } current) return;   // まだ1打席も確定していない（滅多にない）
            _atBat = null;   // RefreshPanel を snap.OffenseIsTop/CurrentBatterOrder のフォールバックへ倒す
            PostLineScore(current.Inning, current.IsTop, finished: false);
            RefreshPanel();
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
            // issue #618: 片方でも判っていれば渡す（旧実装は両方揃わないと渡さず、自校の配色が常に null
            // だったせいで相手校の配色まで捨てていた）。判らない側は rig 側で既定色のまま残る。
            var anyColors = _awayColors != null || _homeColors != null;
            DuelStageRig.InitialTeamColors = anyColors
                ? (offenseIsTop ? _homeColors : _awayColors, offenseIsTop ? _awayColors : _homeColors)
                : ((KokoSim.Engine.Nation.TeamColors, KokoSim.Engine.Nation.TeamColors)?)null;
            _duelColorsOffenseIsTop = anyColors ? offenseIsTop : (bool?)null;
            DuelStageRig.CrowdLayersShown = (int)_crowd;   // 観客の入り（issue #705）。球場を組むときに効く
            // 球場寸法は判定と同じもの（issue #771）。県大会の決勝なら県営級（両翼100m）で描く
            DuelStageRig.InitialField = _prog?.Field;
            DuelStageRig.InitialStadiumId = _prog?.StadiumId;   // issue #905
            var rig = new DuelStageRig();
            DuelStageRig.InitialTeamColors = null;   // 静的な受け渡し窓は使ったら必ず閉じる
            DuelStageRig.InitialField = null;
            DuelStageRig.InitialStadiumId = null;
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
        // 画面のボタン（2D俯瞰（デバッグ））は issue #1193 で撤去＝今は撮影ハーネス（ToggleDuelViewForCapture）だけが叩く。
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
            // サヨナラの場面（issue #1176）は再生が終わっても畳まない＝場面が守備ビューの役者を引き継いで続く。
            // 再生を最後まで待たずに打ち切られたら、予約していた場面をその場で始める
            if (_duelRig != null && _duelRig.WalkoffSceneArmedOrActive) { _duelRig.StartArmedWalkoffNow(); return; }
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
        /// 走者の小ダイヤモンド（issue #595）を盤面の右下へ1つだけ作る。
        /// 頭上ラベルのホスト（<see cref="EnsureDuelImg"/>）と違って <c>_duelImg</c> の子にはしない
        /// ——頭上ラベルが RT の子なのは<b>ビューポート座標に紐づくから</b>（design-18 §3.6）で、
        /// 画面固定のこの図は同じ理由を持たない＝盤面ラッパの子（UXML の <c>minidia-host</c>）に置く。
        /// こうすると <c>_duelImg</c> を作り直しても孤児にならない（issue #454 の罠を踏まない）。
        /// </summary>
        private void BuildMiniDiamond()
        {
            _miniDiaHost = _root.Q<VisualElement>("minidia-host");
            if (_miniDiaHost == null) return;
            _miniDiaHost.pickingMode = PickingMode.Ignore;   // 盤面クリック（→中断ポップアップ）を塞がない
            _miniDiaHost.Clear();
            _miniDia = new MiniDiamondElement();
            _miniDiaHost.Add(_miniDia);
        }

        /// <summary>
        /// 走者の小ダイヤモンド（issue #595）の張り替え。守備カメラは球に寄って追随するので枠外の走者が
        /// 映らない＝「三塁で止まったのか本塁を狙ったのか」を画面固定の図で補う。
        ///
        /// <para>出すのは<b>守備フォーカス中だけ</b>（打席の寄りでは出さない＝塁の在/不在は ScoreBug の
        /// 塁ダイヤの担当で役割を重ねない）。<c>EndFieldPlay</c> で <c>FieldPlayActive</c> が落ちるので、
        /// 畳むのはここ1箇所で足りる（守備陣・走者を伏せる経路と同じ扱い）。</para>
        ///
        /// <para>2D俯瞰（デバッグトグル）を出している間も伏せる——あちらは走者も野手も打球も描いている＝
        /// 同じ情報を二重に出しても読む場所が増えるだけ。</para>
        /// </summary>
        private void RefreshMiniDiamond()
        {
            if (_miniDiaHost == null) return;
            var show = MiniDiamondShown;
            var want = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (_miniDiaHost.style.display != want) _miniDiaHost.style.display = want;
            if (show) _miniDia.SetRunners(_duelRig.CurrentRunnerDots());
        }

        /// <summary>走者の小ダイヤを出している（＝守備フォーカス中で対決ビュー）か。</summary>
        private bool MiniDiamondShown => FieldPlayActive && _duelOn;

        /// <summary>
        /// 投球チャート（issue #1541）を対決ビューの右の中ほどへ1つだけ作る（左下は投手にかぶる・2026-11-24 ユーザー決定）。小ダイヤと同じく画面固定の図なので、
        /// <c>_duelImg</c> の子にせず盤面ラッパの子（UXML の <c>plchart-host</c>）に置く。
        /// </summary>
        private void BuildPitchLocationChart()
        {
            _plChartHost = _root.Q<VisualElement>("plchart-host");
            if (_plChartHost == null) return;
            _plChartHost.pickingMode = PickingMode.Ignore;   // 盤面クリック（→中断ポップアップ）を塞がない
            _plChartHost.Clear();
            _plChart = new PitchLocationChartElement();
            _plChartHost.Add(_plChart);
        }

        /// <summary>
        /// 投球チャートの出し入れ。出すのは<b>対決ビュー中だけ</b>＝守備ビュー（打球処理・右下に小ダイヤが出る間）と
        /// 高速試合モード（対決ビューを出さない）では伏せる。印は打席の頭で消す（<see cref="BeginPaFromSequence"/> ほか）。
        /// </summary>
        private void RefreshPitchLocationChart()
        {
            if (_plChartHost == null) return;
            var show = PitchLocationChartShown;
            var want = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (_plChartHost.style.display != want) _plChartHost.style.display = want;
        }

        /// <summary>投球チャートを出している（＝対決ビューで、守備フォーカス中でも高速試合でもない）か。</summary>
        private bool PitchLocationChartShown => _duelOn && !FieldPlayActive && !_fastMode;

        /// <summary>
        /// 判定を出した1球の印を投球チャートへ打つ（issue #1541）。コースの無い球（合成の球＝<see cref="FlightOf"/> が
        /// null を返す球）は打たない＝架空のコースを描かない。投手の左右は対決ビューの絵と同じ入口から引く。
        /// </summary>
        private void AddPitchLocationMark(PitchToken pt, int number)
        {
            if (_plChart == null) return;
            if (pt.LocationX is not { } x || pt.LocationY is not { } y) return;
            TryDuelHandedness(out _, out var pitcherLeft);
            _plChart.AddMark(x, y, PitchLocationChartElement.ResultOf(pt.Kind), pt.PitchType,
                pitcherLeft ? Handedness.Left : Handedness.Right, number);
        }

        /// <summary>
        /// いま画面に出ている塁の状況（issue #1534 項目8）。タイムのパネルの塁ダイヤはここだけから引く。
        /// 小ダイヤを出している間（守備フォーカス中）は、その小ダイヤと同じ走者・同じ再生時刻の「最後に踏んだ塁」
        /// （<see cref="DuelStageRig.TryCurrentOccupiedBases"/>）。それ以外は ScoreBug の塁ダイヤに出している値。
        /// どちらも描いた分しか進まない＝解決済みの結果を先に見せない（<see cref="EngineAheadOfScreen"/> の約束のまま）。
        /// </summary>
        private void ShownBases(out bool first, out bool second, out bool third)
        {
            if (MiniDiamondShown && _duelRig.TryCurrentOccupiedBases(out first, out second, out third)) return;
            first = _shownBaseFirst; second = _shownBaseSecond; third = _shownBaseThird;
        }

        /// <summary>
        /// 頭上フルネームラベルの張り替え（issue #393）。打球プレー中だけ、画面内の野手・走者の
        /// 頭上に氏名チップを置く。打席中（対決の寄り）は出さない＝寄りの絵は背番号の担当。
        /// プールを使い回し、余った枠は隠すだけ（毎フレーム Add/Remove しない）。
        /// </summary>
        private void RefreshDuelFieldLabels()
        {
            if (_duelLabelHost == null) return;
            // サヨナラの場面（issue #1176）では名札を出さない＝人だかりに名前が重なって読めない
            if (!FieldPlayActive || (_duelRig?.WalkoffSceneActive ?? false))
            {
                foreach (var l in _duelFieldLabelPool) l.style.display = DisplayStyle.None;
                return;
            }
            // 重なりの間引き（issue #689）。優先順に並んだ候補を画面空間で置き、
            // ①判定テロップ（アウト/ダブルプレー等）の矩形に被るものは出さない
            // ②先に置いたラベルと重なるものは上へずらし、2段ずらしても重なるなら出さない。
            // 固定枠の内野（44m）は9人が小さく密集するので、頭上の名札が互いに被る。
            var labels = _duelRig.CurrentFieldLabels(MaxDuelFieldLabels * 2);
            var w = _duelImg.resolvedStyle.width;
            var h = _duelImg.resolvedStyle.height;
            if (float.IsNaN(w) || float.IsNaN(h) || w <= 0f || h <= 0f) return;   // レイアウト未確定の初回フレーム

            var blocked = new List<Rect>(2);
            // 判定テロップと、球種・球速のチップ（pitch-call）の両方を避ける。
            foreach (var hostName in new[] { "judgement-call", "pitch-call" })
            {
                var host = _root?.Q<VisualElement>(hostName);
                if (host == null || host.resolvedStyle.display != DisplayStyle.Flex) continue;
                foreach (var el in new VisualElement[] { host.Q<VisualElement>(className: "pitch-call__stack"),
                             host.Q<VisualElement>(className: "pitch-call__readout"), host.Q<Label>("judgement-detail") })
                {
                    if (el == null || el.resolvedStyle.display == DisplayStyle.None) continue;
                    var wb = el.worldBound;
                    if (float.IsNaN(wb.width) || wb.width <= 0f) continue;
                    blocked.Add(new Rect(_duelImg.WorldToLocal(wb.position), wb.size));
                }
            }

            var placed = new List<Rect>(MaxDuelFieldLabels);
            var shown = 0;
            for (var i = 0; i < labels.Count && shown < MaxDuelFieldLabels; i++)
            {
                var l = labels[i];
                var lbl = DuelFieldLabelSlot(shown);
                lbl.text = l.Name;
                var lw = lbl.resolvedStyle.width; var lh = lbl.resolvedStyle.height;
                if (float.IsNaN(lw) || lw <= 0f) lw = 16f * l.Name.Length + 16f;   // 初回は文字数からの概算
                if (float.IsNaN(lh) || lh <= 0f) lh = 24f;
                var x = l.Viewport01.x * w;
                var y = (1f - l.Viewport01.y) * h;
                var ok = false;
                for (var step = 0; step < 3 && !ok; step++)
                {
                    var r = new Rect(x - lw * 0.5f, y - lh - step * (lh + 2f), lw, lh);   // translate: -50% -100% と同じ原点
                    if (Overlaps(r, blocked) || Overlaps(r, placed)) continue;
                    placed.Add(r);
                    lbl.style.left = r.x + lw * 0.5f;
                    lbl.style.top = r.y + lh;
                    ok = true;
                }
                if (!ok) continue;
                lbl.EnableInClassList("duel-field-label--runner", l.IsRunner);
                lbl.style.display = DisplayStyle.Flex;
                shown++;
            }
            for (var i = shown; i < _duelFieldLabelPool.Count; i++)
                _duelFieldLabelPool[i].style.display = DisplayStyle.None;
        }

        private static bool Overlaps(Rect r, List<Rect> others)
        {
            for (var i = 0; i < others.Count; i++)
                if (r.Overlaps(others[i])) return true;
            return false;
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
        /// 打席側は engine の <see cref="HandednessExtensions.EffectiveBats"/> と同じ関数で決める（issue #1019）。
        /// </summary>
        private void ApplyDuelHandedness()
        {
            if (_duelRig == null || !_duelRig.Available) return;
            // ⚠ 打球プレー中は触らない。SetHandedness は捕手・球審を打席の定位置へ置き直し、投手の
            // ミラー親を裏返す＝**守備として走っている最中の投手・捕手を打席の較正へ引き戻す**。
            // 打球の途中で俯瞰トグルを叩いたときだけ通る道で、絵にしか出ない（F9）。
            // 左右は次の打席の頭（OpenWindow / BeginPa）で必ず当て直すので、飛ばして困らない。
            if (FieldPlayActive) return;
            if (!TryDuelHandedness(out var batterLeft, out var pitcherLeft)) return;
            _duelRig.SetHandedness(batterLeft, pitcherLeft);
            ApplyBatFinish();
            ApplyFielderHandedness();
        }

        /// <summary>現打者のバットの仕上げ（選手ID→銀/黒/金の決定論）。代打で打者が替われば色も替わる。</summary>
        private bool TryBatFinish(out KokoSim.Engine.Nation.BatFinish finish)
        {
            finish = KokoSim.Engine.Nation.BatFinish.Silver;
            if (_atBat is { } at)
                finish = KokoSim.Engine.Nation.BatFinishes.For(at.BatterSourceId, at.BatterName);
            else if (_current != null)
                finish = KokoSim.Engine.Nation.BatFinishes.For(_current.BatterSourceId, _current.BatterName);
            else return false;
            return true;
        }

        private void ApplyBatFinish()
        {
            if (TryBatFinish(out var finish)) _duelRig.SetBatFinish(finish);
        }

        /// <summary>
        /// いまの対戦で立たせるべき左右（打者が左打席か・投手が左投げか）。1球進行では采配窓の対戦
        /// （＝いまの打者・投手）が単一ソース。一括経路では確定打席から引く。対戦が無ければ false。
        /// スイッチヒッターは対戦投手の逆に立つ。
        /// </summary>
        private bool TryDuelHandedness(out bool batterLeft, out bool pitcherLeft)
        {
            batterLeft = pitcherLeft = false;
            Handedness bats, throws;
            if (_atBat is { } at)
            {
                bats = at.BatterBats; throws = at.PitcherThrows;
            }
            else if (_current != null)
            {
                bats = _current.BatterBats; throws = _current.PitcherThrows;
            }
            else return false;
            pitcherLeft = throws == Handedness.Left || PitcherLeftForceForCapture;
            batterLeft = bats.EffectiveBats(throws) == Handedness.Left;   // engine と同じ打席側（issue #1019）
            return true;
        }

        /// <summary>対決ビューの左右が、いまの対戦と食い違っているか（打席の途中の交代の追従, issue #802）。</summary>
        private bool DuelHandednessStale()
            => _duelRig != null && _duelRig.Available
               && TryDuelHandedness(out var batterLeft, out var pitcherLeft)
               && (_duelRig.HandednessDiffers(batterLeft, pitcherLeft)
                   || (TryBatFinish(out var finish) && _duelRig.BatFinishForCapture != finish));

        /// <summary>
        /// 守備側の各位置の選手の投げる手を3D守備ビューへ渡す（issue #795）。いま守っている9人の位置は
        /// 観測データの打順（交代反映済み）から引く。投手は <see cref="ApplyDuelHandedness"/> の側で済んでいる。
        /// </summary>
        private void ApplyFielderHandedness()
        {
            if (_duelRig == null || !_duelRig.Available || _prog == null) return;
            var snap = _prog.Snapshot();
            var defense = snap.OffenseIsTop ? snap.Home : snap.Away;
            var left = new System.Collections.Generic.List<KokoSim.Engine.Match.Field.FieldPosition>();
            foreach (var slot in defense.Lineup)
                if (slot.Throws == Handedness.Left) left.Add(slot.Position);
            _duelRig.SetFielderHandedness(left);
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
        /// DuelTuning.UniformMeshOverrideForCapture を変えてから呼ぶ）。表示中なら新RTを結線し直す。
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

        /// <summary>実測の比較用（issue #770）: 真にするとファウル後の振りかぶり予約の補正を切る（修正前の挙動）。</summary>
        internal static bool FoulReleaseShiftDisabledForCapture;

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
        // 他の背景処理中の守り（issue #278 の名残: 背景スレッドが共有状態を握っている間はエンジンを触る操作を伏せる）は
        // ポップアップ側のボタン活性（UpdatePauseMenu）が引き継ぐ。采配窓を開いた時（OpenWindow/EndGame）に
        // ここが呼ばれるので、開いていれば表示を復帰させる。
        private void UpdateControlState()
        {
            if (_pauseQueued && !_paused && !EngineAheadOfScreen)
            {
                _pauseQueued = false;
                if (!_gameOver && !BackgroundGameOp.Running) OpenPauseMenu();
                return;
            }
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

        private static string ResultJp(LivePlateAppearance pa) =>
            pa.IntentionalWalk ? "申告敬遠" : ResultJp(pa.Result);

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

        /// <summary>
        /// 守備側の現投手の、画面に出す球数（issue #1689）。engine の球数（確定＋進行中の打席ぶん）から、
        /// 投げたがまだ判定を見せていない球を引く。HUD は采配窓（＝次の球のリリース直前）でしか組み直さないため、
        /// engine の値をそのまま出すと BSO・投球チャートより1球遅れ、試合を終わらせた球は数えられないまま残っていた。
        /// </summary>
        private int ShownPitchTotal(LivePitcherToday p)
            => ShownPitchTotal(p.Pitches + p.PendingPitches, _paQueue.Count, _paPitchesCalled, _fastMode);

        /// <summary>
        /// 画面に出す球数の式（issue #1689・EditMode テストで固定）。<paramref name="engineTotal"/>＝engine の球数、
        /// <paramref name="queued"/>＝この打席で描く球の数（投球列）、<paramref name="called"/>＝そのうち判定を見せた数。
        /// 高速試合モードは投球を描かない＝引かない。
        /// </summary>
        public static int ShownPitchTotal(int engineTotal, int queued, int called, bool fastMode)
            => fastMode ? engineTotal : System.Math.Max(0, engineTotal - System.Math.Max(0, queued - called));

        /// <summary>判定を出した瞬間に、守備側の現投手の球数だけを書き換える（HUD 全体は組み直さない。issue #1689）。</summary>
        private void UpdateShownPitchCounts()
        {
            if (_prog == null || _defPitchCountLabels.Count == 0) return;
            var snap = _preStep is { } pre ? pre.SnapshotBefore : _prog.Snapshot();
            var offenseIsTop = _atBat?.IsTop ?? snap.OffenseIsTop;
            var def = offenseIsTop ? snap.Home.Pitcher : snap.Away.Pitcher;
            if (def == null) return;
            var text = ShownPitchTotal(def).ToString();
            foreach (var l in _defPitchCountLabels) l.text = text;
        }

        private void RefreshPanel()
        {
            if (_prog == null || _statsProvider == null) return;
            // 振りかぶりで先に解決した球を預かっている間は、解決前の観測で描く（球数・今日成績の先出し防止）。
            var snap = _preStep is { } pre ? pre.SnapshotBefore : _prog.Snapshot();
            var ownIsAway = _managerIsAway;
            var own = ownIsAway ? snap.Away : snap.Home;
            var opp = ownIsAway ? snap.Home : snap.Away;
            // 攻守・現打者は「いま止まっている采配窓」から引く。Snapshot 側は確定した打席を指すので、
            // 1球進行では1打席ぶん遅れる（打席の頭でハイライトが前の打者に残る）。
            var offenseIsTop = _atBat?.IsTop ?? snap.OffenseIsTop;
            var offenseIsOwn = offenseIsTop == ownIsAway;   // 攻撃側が自校か
            var order = _atBat?.BatterOrder ?? snap.CurrentBatterOrder;

            // 左右とも同じ並び（打順→守備→名前→背番号→調子→今日）。ミラーはしない。
            // 校ID＝調子の真値/誤認と通算成績の帰属先（issue #634）。配色と同じ静的経路から来る。
            var ownSchoolId = ownIsAway ? _awaySchoolId : _homeSchoolId;
            var oppSchoolId = ownIsAway ? _homeSchoolId : _awaySchoolId;
            _defPitchCountLabels.Clear();
            BuildColumn(_leftLineup, _leftPitcher, own, mirror: false, highlightOrder: offenseIsOwn ? order : 0,
                schoolId: ownSchoolId, defending: !offenseIsOwn);
            BuildColumn(_rightLineup, _rightPitcher, opp, mirror: false, highlightOrder: offenseIsOwn ? 0 : order,
                schoolId: oppSchoolId, defending: offenseIsOwn);
            RefreshHud(snap, offenseIsTop, order);
            ApplyDuelTeamColors(offenseIsTop);
            ApplyPitcherRattled(snap, offenseIsTop);
        }

        /// <summary>
        /// 対決ビューの投手へ動揺（頭上の渦巻き・投球前の汗）を流し込む（issue #1178）。観測口の守備側の値を読むだけ
        /// ＝試合結果に触れない。HUD に文字は出さない。窓の観測（解決前）から引くので、動揺が立つのは
        /// 「それを起こした打席」を見せ終えた次の窓から＝結果の先出しにならない。解ける瞬間
        /// （守備伝令・継投・イニングの替わり目・2アウト回復）も同じ窓で拾う。高速試合モードでは出さない。
        /// </summary>
        private void ApplyPitcherRattled(MatchLiveSnapshot snap, bool offenseIsTop)
        {
            if (_duelRig == null || !_duelRig.Available) return;
            var defense = offenseIsTop ? snap.Home : snap.Away;
            _duelRig.SetPitcherRattled(defense.PitcherRattled && !_fastMode);
        }

        /// <summary>
        /// 対決ビューの配色を攻守に合わせる。投手・捕手＝守備側の学校、打者＝攻撃側の学校。
        /// 役割（誰が投げて誰が打つか）は固定で、攻守交代で入れ替わるのは<b>どちらの学校の色か</b>。
        /// 塗り替えはマテリアルだけ（素体・クリップは作り直さない）ので進行中に呼んで良い。
        /// </summary>
        private void ApplyDuelTeamColors(bool offenseIsTop)
        {
            if (_duelRig == null || !_duelRig.Available) return;
            if (_awayColors == null && _homeColors == null) return;   // issue #618: 片方でも判れば塗る
            if (_duelColorsOffenseIsTop == offenseIsTop) return;   // 半回が変わった時だけ塗る
            _duelColorsOffenseIsTop = offenseIsTop;
            var offense = offenseIsTop ? _awayColors : _homeColors;
            var defense = offenseIsTop ? _homeColors : _awayColors;
            _duelRig.SetTeamColors(defense, offense);
        }

        // 左右のチーム欄（GameScreen.mock 準拠・2026-11-21）。左右とも同じ並び（ミラーしない）。
        // 打順の行 9 → 投手の行 → 控え（見出し「控え／N人」＋行）。色は固定（--ms-*）でスクールカラーは使わない。
        private void BuildColumn(VisualElement lineupHost, VisualElement pitcherHost,
            LiveTeamSnapshot team, bool mirror, int highlightOrder, int schoolId, bool defending)
        {
            if (lineupHost != null)
            {
                lineupHost.Clear();
                foreach (var s in team.Lineup)
                    lineupHost.Add(BuildLineupRow(s, atBat: s.Order == highlightOrder, schoolId));
            }
            if (pitcherHost != null)
            {
                pitcherHost.Clear();
                pitcherHost.Add(BuildPitcherRow(team.Pitcher, defending));
                // 控え（ベンチ入り＝背番号ありのメンバのみ。ベンチ外は出さない）。控えは1本の名簿なので
                // そのまま背番号の昇順で並べる（issue #381）。投手として置かれている控えも同じ行の作り
                //（背番号・名前・調子）で、材料だけ投手側の観測から引く。
                var benchRows = new List<(int Number, VisualElement Row)>();
                foreach (var b in team.Reserves)
                {
                    if (b.Number <= 0) continue;
                    var asPitcher = FindReservePitcher(team, b.Number);
                    benchRows.Add((b.Number, asPitcher is null
                        ? BuildBenchRow(b.Number, b.Name, b.SourceId, b.ConditionValue, schoolId)
                        : BuildBenchRow(asPitcher.Number, asPitcher.Name, asPitcher.SourceId, asPitcher.ConditionValue, schoolId)));
                }
                if (benchRows.Count > 0)
                {
                    benchRows.Sort((a, c) => a.Number.CompareTo(c.Number));
                    var head = new VisualElement();
                    head.AddToClassList("mside-bench-head");
                    head.Add(MakeLabel("控え", "mside-bench-head__t"));
                    head.Add(MakeLabel(benchRows.Count + "人", "mside-bench-head__t"));
                    pitcherHost.Add(head);
                    foreach (var r in benchRows) pitcherHost.Add(r.Row);
                }
            }
        }

        // 打順の行: 打順 | 守備位置チップ | 名前(フルネーム) | 背番号の箱 | 調子の顔。
        // 今の打者は行の地をベージュ・名前を山吹（枠は付けない）。
        private VisualElement BuildLineupRow(LiveBatterSlot s, bool atBat, int schoolId)
        {
            var row = new VisualElement();
            row.AddToClassList("mside-row");
            if (atBat) row.AddToClassList("mside-row--at-bat");

            row.Add(MakeLabel(s.Order.ToString(), "mside-row__ord"));
            row.Add(MakeLabel(MatchLiveStatsProvider.PosAbbr(s.Position), "mside-pos", PosChipClass(s.Position), "f-body-bd"));
            // 退いた選手（←旧選手名）は出さない＝名前の幅を食って入った選手の名前が切れるため。
            // 交代は交代テロップ（#920）と実況の履歴で見せる（issue #947・監督決定）。
            row.Add(MakeLabel(SideName(s.Name), "mside-row__name"));   // フルネーム
            row.Add(MakeLabel(NumText(s.Number), "mside-num", "f-body-bd"));
            var face = new ConditionFace();
            face.Set(_statsProvider.ConditionOf(schoolId, s.SourceId, s.ConditionValue, s.Name));
            row.Add(face);
            return row;
        }

        /// <summary>欄の選手名。姓名の間の全角空白を半角へ詰める（HUD の HudName と同じ扱い。字間は USS の letter-spacing 側）。</summary>
        private static string SideName(string name) => (name ?? "").Replace('　', ' ');

        /// <summary>守備位置チップの色クラス（投／捕／内野／外野／指の5系統）。</summary>
        private static string PosChipClass(FieldPosition pos) => pos switch
        {
            FieldPosition.Pitcher => "mside-pos--p",
            FieldPosition.Catcher => "mside-pos--c",
            FieldPosition.FirstBase or FieldPosition.SecondBase
                or FieldPosition.ThirdBase or FieldPosition.Shortstop => "mside-pos--if",
            FieldPosition.LeftField or FieldPosition.CenterField or FieldPosition.RightField => "mside-pos--of",
            _ => "mside-pos--dh",
        };

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

        // 控えの行: 背番号の箱 | 名前(フルネーム) | 調子の顔。守備位置は出さない。代打・代走・守備固めの候補。
        private VisualElement BuildBenchRow(int number, string name, int? sourceId, double conditionValue, int schoolId)
        {
            var row = new VisualElement();
            row.AddToClassList("mside-bench");
            row.Add(MakeLabel(NumText(number), "mside-bench__num", "f-num"));
            row.Add(MakeLabel(SideName(name), "mside-bench__name"));
            var face = new ConditionFace();
            face.Set(_statsProvider.ConditionOf(schoolId, sourceId, conditionValue, name));
            row.Add(face);
            return row;
        }

        // 投手の行: 空き | 「投」チップ | 名前 | 球数（N 球）。目立たせない＝地・枠なし。
        private VisualElement BuildPitcherRow(LivePitcherToday p, bool defending)
        {
            var row = new VisualElement();
            row.AddToClassList("mside-pitcher");
            var gap = new VisualElement();
            gap.AddToClassList("mside-pitcher__gap");
            row.Add(gap);
            row.Add(MakeLabel("投", "mside-pos", "mside-pos--p", "f-body-bd"));
            row.Add(MakeLabel(SideName(p.Name), "mside-pitcher__name"));
            // 確定球数＋進行中の打席ぶん（issue #397。LiveLineScore.PendingRuns と同型）。投球回・失点は出さない（issue #739）。
            var count = new VisualElement();
            count.AddToClassList("mside-pitcher__count");
            // 守備側の現投手は、投げたがまだ判定を見せていない球を引く（issue #1689）。
            var n = MakeLabel((defending ? ShownPitchTotal(p) : p.Pitches + p.PendingPitches).ToString(), "mside-pitcher__n", "f-num-bd");
            if (defending) _defPitchCountLabels.Add(n);
            count.Add(n);
            count.Add(MakeLabel("球", "mside-pitcher__u", "f-body"));
            row.Add(count);
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
            public readonly int BatterOrder, BatterNumber;
            public readonly string BatterName;
            public readonly int? BatterSourceId;
            public readonly double BatterConditionValue;
            public readonly FieldPosition BatterPosition;
            public readonly Handedness BatterBats;

            public Matchup(LiveAtBat a)
            {
                BatterBats = a.BatterBats;
                BatterOrder = a.BatterOrder; BatterNumber = a.BatterNumber;
                BatterName = a.BatterName;
                BatterSourceId = a.BatterSourceId;
                BatterConditionValue = a.BatterConditionValue; BatterPosition = a.BatterPosition;
            }

            public Matchup(LivePlateAppearance p)
            {
                BatterBats = p.BatterBats;
                BatterOrder = p.BatterOrder; BatterNumber = p.BatterNumber;
                BatterName = p.BatterName;
                BatterSourceId = p.BatterSourceId;
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

            // 校ID＝通算成績の帰属先（issue #634）。
            var offenseSchoolId = offenseIsTop ? _awaySchoolId : _homeSchoolId;
            var defenseSchoolId = offenseIsTop ? _homeSchoolId : _awaySchoolId;

            _hudPitcherSlot.Add(BuildHudPitcher(defPitcher, defenseSchoolId));
            _hudBatterSlot.Add(BuildHudBatter(batterToday, mu, offenseIsTop, offenseSchoolId));

            // 伝令が効いている間だけ、上がっている能力の名前に↑（issue #1177）。守備伝令＝投手の制球、攻撃伝令＝打者のミート。
            // 両校とも出す（相手校の伝令も相手の投手／打者の帯に出る）。数字・残り打席は出さない（監督決定 2026-11-19）。
            // 置き場所は帯の盤面側の辺＝投手は帯の上・打者は帯の下（案B・2026-11-19 監督決定）。帯の中身は変えない。
            var offTeam = offenseIsTop ? snap.Away : snap.Home;
            var defTeam = offenseIsTop ? snap.Home : snap.Away;
            if (defTeam.DefenseCalmActive || CalmBadgeForceForCapture)
                _hudPitcherSlot.Add(CalmBadge("制球", "mhud-calm--pitcher"));
            if (offTeam.OffenseCalmActive || CalmBadgeForceForCapture)
                _hudBatterSlot.Add(CalmBadge("ミート", "mhud-calm--batter"));
        }

        /// <summary>伝令が効いている間の「制球↑」「ミート↑」（issue #1177）。位置は USS のクラスで決める。</summary>
        private static VisualElement CalmBadge(string ability, string placement)
        {
            var badge = MakeRow("mhud-calm");
            badge.AddToClassList(placement);
            var t = MakeLabel(ability, "mhud-calm__t");
            t.AddToClassList("f-body");
            var up = MakeLabel("↑", "mhud-calm__up");
            up.AddToClassList("f-body");
            badge.Add(t);
            badge.Add(up);
            return badge;
        }

        private VisualElement BuildHudPitcher(LivePitcherToday def, int schoolId)
        {
            var band = HudBand();

            // 1段目＝登板順・背番号・名前・学年・球数。打者HUDと同一の行構造（1つ目が白地黒字のチップ、
            // 次が素の白文字の背番号、その次が名前）で、右端に球数を置く。
            // 登板順は「N番手」が数字＋和文の混植なので、ReliefLabel の文字列ではなく数値から組んで
            // Label を分ける（設計書16 §2 の作法。設計書22 で数字も Noto Sans JP になり分けは必須でなくなったが、既存の分割は残している）。
            // 背番号・名前は Matchup（打席頭で凍った打者側の事情）ではなく LivePitcherToday（常に現在の
            // 投手を指すライブソース）から引く。番手・球数・スタミナと同じソースにしないと、交代直後に
            // 存在しない選手の行が1打席分表示される（issue #528）。
            var idn = HudIdRow();
            idn.Add(def.ReliefOrder > 1
                ? HudChip(def.ReliefOrder.ToString(), "番手", "mhud-cp--key", numeric: true)
                : HudChip("先発", null, "mhud-cp--key"));
            var pNum = NumText(def.Number);
            if (pNum.Length > 0) idn.Add(HudNumber(pNum));
            idn.Add(HudName(def.Name));
            if (def.Grade > 0) idn.Add(HudChip(def.Grade.ToString(), "年", "mhud-cp--dim", numeric: true));

            // 球数は名前行の右端＝継投判断の主指標なので、スタッツ行のセルに埋もれさせず単体で立てる。
            // 確定球数＋進行中の打席ぶん（issue #397）。据え置きを解消し1球ごとに動かす。
            var totalPitches = ShownPitchTotal(def);   // 判定を見せた球まで（issue #1689）
            // 警告色は絶対球数でなく「目安投球数に対する残量」で出す（issue #408）。生成スタミナの水準を
            // 上げて平均球数が100前後になったため、絶対 80/100 だと健康な先発が常時 alert になる。スタミナバー
            // （BuildStaminaBar）と同じ残量帯（<0.6=注意 / <0.3=警告）で数字も落とし、数字とバーの色を一致させる。
            var pcMod = def.StaminaRemaining < 0.3 ? "mhud-pc__n--alert"
                : def.StaminaRemaining < 0.6 ? "mhud-pc__n--warn" : null;
            idn.Add(MakeRow("mhud-spacer"));
            // 球数は「今 / この試合で投げられる最大」の分母つき。分母は週間球数制限（設計書05 §1.3）の
            // 上限から持ち越しを引いた値＝試合開始時点で固定（目安投球数は出さない）。
            var pc = HudPitchCount(totalPitches.ToString(), pcMod, MaxPitchesThisGameText(def));
            if (pc.Q<Label>(className: "mhud-pc__n") is { } pcLabel) _defPitchCountLabels.Add(pcLabel);
            idn.Add(pc);
            // 調子の顔は打者HUDと右端を揃える＝球数のさらに右（issue #1053）。番手・球数と同じ
            // LivePitcherToday から引く＝継投直後も新しい投手の調子になる（issue #528 と同じ理由）。
            var face = new ConditionFace();
            face.AddToClassList("mhud-face");
            face.AddToClassList("mhud-face--after-pc");
            face.Set(_statsProvider.ConditionOf(schoolId, def.SourceId, def.ConditionValue, def.Name));
            idn.Add(face);
            band.Add(idn);

            // 2段目＝通算防御率・本日の奪三振・与四死球の等幅3セル（打者HUDと同構造）。奪三振と与四死球は
            // 対で見て初めて「今日の出来」になるので隣接させる（issue #407）。単位は見出しへ逃がしてあるので
            // 値は数字だけ＝.f-num の数字セルとして素直に載る（設計書16 §2。書体は設計書22 で Noto Sans JP）。
            // 防御率は通算＋本日の合算（issue #449。通算なしは本日分のみ、登板なしかつ本日アウト0のみ「—」）。
            var era = MatchLiveStatsProvider.LiveEra(
                _statsProvider.CareerPitching(schoolId, def.SourceId), def.EarnedRuns, def.Outs);
            var stats = HudStatsRow();
            stats.Add(HudCell("防御率", era, first: true));
            stats.Add(HudCell("奪三振", def.StrikeOuts.ToString()));
            stats.Add(HudCell("与四死球", (def.Walks + def.HitByPitch).ToString()));
            band.Add(stats);

            // 特殊能力（自校の選手だけ・issue #738 案A）。投手帯は投手の能力だけ（issue #781）。無ければ段ごと出さない。
            var pSkills = UiComponents.SkillChipsFor(_statsProvider.OwnSkills(schoolId, def.SourceId), 4, "mhud-skills",
                KokoSim.Engine.Players.SkillCategory.Pitching);
            if (pSkills != null) band.Add(pSkills);

            // 3段目＝スタミナ。帯の最下段として帯と同幅で組み込む（別要素として浮かせない）。
            band.Add(BuildStaminaBar(def.StaminaRemaining));
            return band;
        }

        /// <summary>
        /// 週間球数制限（設計書05 §1.3）の残り球数。制限なし（int.MaxValue）なら null＝表示しない。
        /// 持ち越し（<see cref="LivePitcherToday.WeeklyPitchesPrior"/>）＋当試合ぶんを引く。到達判定は
        /// <b>打席境界</b>なので実際は数球超える＝0でクランプして「打者を打ち終えたら交代」と読ませる。
        /// </summary>
        private static int? WeeklyPitchesLeft(LivePitcherToday def, int totalPitchesToday)
        {
            if (def == null || def.WeeklyPitchLimit == int.MaxValue) return null;
            var left = def.WeeklyPitchLimit - def.WeeklyPitchesPrior - totalPitchesToday;
            return left < 0 ? 0 : left;
        }

        /// <summary>
        /// 球数の分母＝この試合で投げられる最大球数（週間上限 − 持ち越し。0でクランプ）。
        /// 制限なし（int.MaxValue）なら空＝分母を出さない。
        /// </summary>
        private static string MaxPitchesThisGameText(LivePitcherToday def)
            => WeeklyPitchesLeft(def, 0)?.ToString();

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

        private VisualElement BuildHudBatter(LiveBatterSlot batterToday, Matchup m, bool offenseIsTop,
            int schoolId)
        {
            var band = HudBand();

            // 1段目＝放送と同じ並び「4番 三塁 5 中村 陽介 2年 (調子)」。左右打は3Dの絵で分かるので出さない。
            var idn = HudIdRow();
            idn.Add(HudChip(m.BatterOrder.ToString(), "番", "mhud-cp--key", numeric: true));
            var pos = MatchLiveStatsProvider.PosJp(batterToday?.Position ?? m.BatterPosition);
            if (pos.Length > 0) idn.Add(HudChip(pos, null, "mhud-cp--pos", numeric: false));
            var bNum = NumText(m.BatterNumber);
            if (bNum.Length > 0) idn.Add(HudNumber(bNum));
            idn.Add(HudName(m.BatterName));
            if (batterToday is { Grade: > 0 }) idn.Add(HudChip(batterToday.Grade.ToString(), "年", "mhud-cp--dim", numeric: true));
            // 顔は名前行の右端＝学年チップの外側。spacer で押し出す。
            idn.Add(MakeRow("mhud-spacer"));
            var face = new ConditionFace();
            face.AddToClassList("mhud-face");
            face.Set(_statsProvider.ConditionOf(schoolId, m.BatterSourceId, m.BatterConditionValue, m.BatterName));
            idn.Add(face);
            band.Add(idn);

            // 2段目＝成績。打率は通算＋本日の合算（issue #449。通算なしは本日分のみ、通算なしかつ
            // 本日打数0のみ「—」）。本塁打・打点は通算のみ（issue範囲外・据え置き）＝等幅セルに割ると
            // 同格の3項目に見えるが、意味の違いはここに残す。
            var b = _statsProvider.CareerBatting(schoolId, m.BatterSourceId);
            var stats = HudStatsRow();
            stats.Add(HudCell("打率", MatchLiveStatsProvider.LiveAvg(b, batterToday?.AtBats ?? 0, batterToday?.Hits ?? 0), first: true));
            stats.Add(HudCell("本塁打", b == null ? "—" : b.HomeRuns.ToString()));
            stats.Add(HudCell("打点", b == null ? "—" : b.Rbi.ToString()));
            band.Add(stats);

            // 特殊能力（自校の選手だけ・issue #738 案A＝成績の下にチップ段）。打者帯は打撃の能力だけ（issue #781）。
            // 無ければ段ごと出さない。
            var bSkills = UiComponents.SkillChipsFor(_statsProvider.OwnSkills(schoolId, m.BatterSourceId), 4, "mhud-skills",
                KokoSim.Engine.Players.SkillCategory.Batting);
            if (bSkills != null) band.Add(bSkills);

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
                // 略記は和文なので .f-num は付けない（数字セル用のクラス。書体は設計書22 以降どちらも Noto Sans JP）。
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
            var res = pa.Play != null ? pa.Play.Result : ResultJp(pa);
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
        /// ——設計書16 §2 の作法（当時は Oswald（.f-num）が欧文専用だったため）。設計書22 で数字も Noto Sans JP
        /// になり分けは必須でなくなったが、既存の分割は残している。
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
        /// 帯の中で最も大きい要素＝選手名。人名は明朝なので、書体registryに新クラスを足さず
        /// <c>.f-display</c> を要素へ直接付ける（UI-BUILD-METHOD の新規コード方針）。
        /// 姓名間の全角空白は半角へ落として詰める（字間は USS の letter-spacing 側）。
        /// </summary>
        private static VisualElement HudName(string name) =>
            MakeLabel((name ?? "").Replace('　', ' '), "mhud-nm", "f-display");

        /// <summary>背番号。チップにせず素の白文字で名前の直前に置く（打者・投手で共通）。</summary>
        private static VisualElement HudNumber(string digits) => MakeLabel(digits, "mhud-no", "f-num");

        /// <summary>
        /// 球数「6球」。数字と和文単位で Label を分ける（設計書16 §2 の作法・設計書22 以降は必須でなく既存の分割を残している）。見出しは持たない。
        /// 数字は太字＝学年チップより目立たせる（継投判断の主指標）。
        /// </summary>
        private static VisualElement HudPitchCount(string digits, string mod, string denominator)
        {
            var box = MakeRow("mhud-pc");
            var n = MakeLabel(digits, "mhud-pc__n", "f-num-bd");
            if (mod != null) n.AddToClassList(mod);
            box.Add(n);
            // 分母（実効目安投球数）。数字だけの Label なので .f-num の数字セルとして素直に載る（設計書16 §2）。
            if (!string.IsNullOrEmpty(denominator)) box.Add(MakeLabel("/ " + denominator, "mhud-pc__d", "f-num"));
            box.Add(MakeLabel("球", "mhud-pc__u"));
            return box;
        }

        /// <summary>
        /// スタッツ行の1セル（ラベル上・数値下の縦積み）。単位はラベルへ逃がしてあるので
        /// 値は数字だけになり、.f-num の数字セルとして素直に載る（UI原則③）。
        /// </summary>
        private static VisualElement HudCell(string lab, string val, bool first = false, string valMod = null)
        {
            var cell = MakeRow("mhud-cell");
            // セル間の区切りは上下15%ずつ短い細片で引く（border では詰められない）。先頭セルには付けない
            // ＝帯の左端に落ちて「枠の縁」に見えてしまうため。
            if (!first) cell.Add(MakeRow("mhud-cell__div"));
            cell.Add(MakeLabel(lab, "mhud-cell__lab"));
            var v = MakeLabel(val, "mhud-cell__val");
            // 該当なしの「—」に .f-num を付けると欠字になった（旧・Oswald は ASCII しか収録していなかった。設計書22 で Noto Sans JP になった今は載るが、作法は残している）。
            // 数字のときだけ載せる＝UiComponents の IsNumericCell と同じ作法。
            if (IsNumText(val)) v.AddToClassList("f-num");
            if (valMod != null) v.AddToClassList(valMod);
            cell.Add(v);
            return cell;
        }

        /// <summary>.f-num（数字セル）に載せてよい文字列か。数字・小数点・符号・% だけなら真。</summary>
        private static bool IsNumText(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (var c in s)
                if (!char.IsDigit(c) && c != '.' && c != '-' && c != '+' && c != '%') return false;
            return true;
        }

        // 背番号テキスト（0＝番号なしは空表示）。
        private static string NumText(int n) => n > 0 ? n.ToString() : "";

        // ── スクショ用（決定論シーク不要：進行状態を外部から操作するフックだけ提供） ──
        public MatchProgression Progression => _prog;
        public LivePlateAppearance CurrentPa => _current;
        public bool IsGameOver => _gameOver;

        /// <summary>ベンチマーク用（issue #1314）: 対決ビュー（投手と打者）を映しているか。偽＝打球の俯瞰（守備の場面）などへ出ている間。</summary>
        public bool DuelViewOnForBenchmark => _duelOn;

        // ── 打球プレーの3D再生を外から覗く窓（打球処理の3D化 F9 の受け入れ用） ──
        /// <summary>打球再生の時計[秒]。俯瞰・3D・判定オーバーレイを同時に駆動している唯一の値。</summary>
        public double PlayClockForCapture => _t;

        /// <summary>
        /// いま掲示板に出ている得点（"先攻-後攻"）。得点が<b>いつ</b>増えたかは絵の外側からは
        /// 分からない（掲示板の数字は撮った瞬間の値でしかない）ので、時刻と並べて記録するための窓
        /// （issue score-display-before-home-touch）。
        /// </summary>
        public string PostedScoreForCapture => _postedAwayRuns + "-" + _postedHomeRuns;

        /// <summary>3Dの守備ビューが走っているか。</summary>
        public bool FieldPlayActiveForCapture => FieldPlayActive;

        /// <summary>スクショ用: 守備ビューで再生中のプレー。打席途中のプレー（ファウル・盗塁・牽制・暴投/捕逸等）を
        /// 先に見て、無ければ打席の打球（<c>_current.Play</c>）。撮影の節目はこのプレーの時刻で解く。</summary>
        public PlaybackPlay PlayingForCapture =>
            _foulFieldPlay ?? _stealPlay ?? _pickoffPlay ?? _batteryMissPlay ?? _squeezeRundownPlay ?? _dropThirdStrikePlay
            ?? _current?.Play;

        /// <summary>スクショ用: <see cref="PlayingForCapture"/> の再生を閉じる時計[秒]（この時刻を過ぎたコマで閉じる）。
        /// 打席途中のプレーはプレーの長さより手前で閉じる（<c>MidPaPlayEndTime</c>）ので、撮影の節目「end」は
        /// <c>play.Dur</c> でなくこちらで置く。</summary>
        public double PlayCloseAtForCapture =>
            (_foulFieldPlay != null ? _foulFieldEndAt
            : _stealPlay != null ? _stealEndAt
            : _pickoffPlay != null ? _pickoffEndAt
            : _batteryMissPlay != null ? _batteryMissEndAt
            : _squeezeRundownPlay != null ? _squeezeRundownEndAt
            : _dropThirdStrikePlay != null ? _dropThirdStrikeEndAt
            : _current?.Play != null ? PlayEndTime() : 0) + FinishGraceSeconds;

        /// <summary>
        /// 判定オーバーレイが**画面に出ているか**。
        /// ⚠ 「<c>JudgementAt</c> が値を返すか」ではなく<b>要素の表示状態</b>を見ること。
        /// F9 で壊れ得るのは探索ではなく<b>器の置き場所</b>（盤面の中に置いたままだと3Dでは隠れる）で、
        /// 純関数の側を見ても壊れていることが分からない。
        /// </summary>
        public bool JudgementVisibleForCapture =>
            _root?.Q<VisualElement>("judgement-call")?.resolvedStyle.display == DisplayStyle.Flex;

        /// <summary>判定オーバーレイの画像面（tl-call-xxx。出ていなければ空）。</summary>
        public string JudgementFaceForCapture =>
            JudgementVisibleForCapture ? (_view?.JudgementFaceForCapture ?? "") : "";

        /// <summary>【撮影ハーネス用】走者の小ダイヤ（issue #595）が今出ているか。</summary>
        public bool MiniDiamondVisibleForCapture =>
            _miniDiaHost != null && _miniDiaHost.resolvedStyle.display == DisplayStyle.Flex;

        /// <summary>【撮影ハーネス用】小ダイヤと3Dの走者の突き合わせ（同じ時刻・同じ補間か）。</summary>
        public string MiniDiamondDiagForCapture =>
            _duelRig != null ? _duelRig.MiniDiamondDiagForCapture() : "(rig なし)";

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
                PitchBattingOverride.SafetyBunt => "batting-safety",
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

        /// <summary>
        /// 撮影用（issue #1473）: その采配カードがいまの局面で手札に出ていて押せるか。押せない札を黙って押すと
        /// 何も起きないまま撮れてしまうので、撮影レシピはこれで確かめてから押す。
        /// </summary>
        public bool TacticsCardSelectableForCapture(string id) => _signPad?.IsSelectableForCapture(id) ?? false;

        /// <summary>撮影用（issue #1473）: 「プレイボール」「ゲームセット」のテロップを掲げている最中か（撮影はこれが消えてから）。</summary>
        public bool GameTelopOnForCapture => _gameTelopOn;

        /// <summary>
        /// 撮影用（issue #1473）: いま止まっている采配窓の局面（回・表裏・アウト・カウント・塁・攻守）を1行で。
        /// 窓に入っていなければ null。撮った絵の局面を残し、「走者が居ない」等を絵と突き合わせる。
        /// </summary>
        public string WindowSituationForCapture => _atBat == null ? null
            : $"{_atBat.Inning}回{(_atBat.IsTop ? "表" : "裏")} {_atBat.Outs}死 {_atBat.Balls}-{_atBat.Strikes} "
              + $"塁[{(_atBat.BaseFirst ? "1" : "-")}{(_atBat.BaseSecond ? "2" : "-")}{(_atBat.BaseThird ? "3" : "-")}] "
              + $"球{_atBat.PitchNumberInPa} 自校={(_atBat.IsTop == _managerIsAway ? "攻撃" : "守備")}";

        /// <summary>スクショ用: 打席途中プレー（issue #485〜#488）を再生中なら種類名、そうでなければ null。
        /// 打球（<see cref="FieldPlayActiveForCapture"/>）とは別経路なので別の口で見る。</summary>
        public string MidPaPlayForCapture =>
            _stealPlay != null ? "steal"
            : _pickoffPlay != null ? "pickoff"
            : _batteryMissPlay != null ? "battery-miss"
            : _foulFieldPlay != null ? "foul"
            : _squeezeRundownPlay != null ? "squeeze-rundown"
            : _dropThirdStrikePlay != null ? "drop-third"
            : null;

        /// <summary>スクショ用: 再生中の打席途中プレーの結果テキスト（盗塁成功/盗塁死/牽制/牽制死/暴投・捕逸/振り逃げ）。
        /// 種類（<see cref="MidPaPlayForCapture"/>）だけでは盗塁の成否＝タッチアウトの有無が分からない。</summary>
        public string MidPaPlayResultForCapture =>
            _stealPlay?.Result ?? _pickoffPlay?.Result
            ?? _batteryMissPlay?.Result ?? _squeezeRundownPlay?.Result ?? _dropThirdStrikePlay?.Result;

        /// <summary>スクショ用: 1球進行のいまの段（Idle/Window/Pitch/Play/Over）。
        /// 録画ハーネスが「打席が確定した瞬間」を段の縁で拾うのに使う。</summary>
        public string StageNameForCapture => _stage.ToString();

        /// <summary>スクショ用: 直前の球にバント・スクイズの指示が出ていたか（issue #490 の絵の検証用）。</summary>
        public bool BuntAttemptForCapture { get; private set; }

        /// <summary>
        /// スクショ用: チーム配色を差し込む。撮影ハーネスの試合は <see cref="LiveMatchRequest"/> を通らないので
        /// 配色が null のまま＝対決ビューの選手が既定色（白＋紺）で写ってしまい、実物と違う絵になる。
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

        /// <summary>撮影用（issue #1563）: 盤面クリックでタイムを開く操作そのもの（エンジンが画面より先にいれば投げ終わるまで待ってから開く）。</summary>
        public void ClickToPauseForCapture()
        {
            _paused = false;
            if (EngineAheadOfScreen) _pauseQueued = true;
            else OpenPauseMenu();
        }

        /// <summary>検証用: いま高速試合モード中か（design-06 §3.4）。</summary>
        public bool FastModeForCapture => _fastMode;

        /// <summary>
        /// スクショ用: いま<b>打席の頭の采配窓</b>で止まっているか（まだ1球も投げていない＝設計書15 §8 の肝）。
        /// この状態で撮った絵が「打者を見てからサインを出せる」ことの証拠になる。
        /// </summary>
        public bool AtPaHeadWindowForCapture => _stage == LiveStage.Window && _atBat is { PitchNumberInPa: 1 };

        /// <summary>撮影用（issue #1563）: いま止まっている采配窓が「その打席の何球目」か（窓でなければ 0）と、その打者の名前。</summary>
        public int WindowPitchNumberForCapture => _atBat?.PitchNumberInPa ?? 0;
        public string WindowBatterForCapture => _atBat?.BatterName;

        /// <summary>撮影用（issue #1563）: 申告敬遠の確認を出している最中か・押せる状態か・メニューに出ているか。</summary>
        public bool IntentionalWalkConfirmOpenForCapture => _pmIbbConfirm != null && _pmIbbConfirm.resolvedStyle.display != DisplayStyle.None;

        /// <summary>撮影用（issue #1563）: 1コマぶんの状態（描いている打席の球数・描画中の球・自校の伝令の残り）を1行で。</summary>
        public string FrameStateForCapture()
        {
            var pa = _current;
            return $"stage={_stage} paConfirmed={_paConfirmed} queue={_paQueue.Count} pitchIdx={_pitchIdx} inPitch={_inPitchPhase} ball={_pitchBallVisible} "
                 + $"result={(pa == null ? "-" : pa.Result.ToString())} seq={(pa?.PitchSeq?.Pitches.Count.ToString() ?? "-")} "
                 + $"timeoutsLeft(off/def)={_prog?.OffenseTimeoutsLeft(_managerIsAway)}/{_prog?.DefenseTimeoutsLeft(_managerIsAway)}";
        }

        /// <summary>スクショ用: いま采配窓で止まっているか（打席の途中＝球間を含む）。</summary>
        public bool AtWindowForCapture => _stage == LiveStage.Window;
        /// <summary>スクショ用: 投球フェーズ（振りかぶり〜判定〜次の窓の手前）か。対決ビューのまま撮るレシピの起点。</summary>
        public bool InPitchPhaseForCapture => _inPitchPhase;

        /// <summary>
        /// 診断用: いま一塁に走者が居るか（盗塁カード <c>run-steal</c> の <c>EnabledIf</c> と同じ条件）。
        /// 撮影ハーネスが「盗塁が撮れなかったのは走者が一度も一塁に居なかったからか、居たのに
        /// 出なかったのか」を切り分けるのに使う。カードは無効時に押しても黙って何も起きないので、
        /// この口が無いと失敗が絵にもログにも残らない（issue #489: 押し続けたのに撮れず原因不明だった）。
        /// </summary>
        public bool RunnerOnFirstForCapture => RunnerOnFirst;

        /// <summary>
        /// 【撮影ハーネス用】次の打席の結果を強制する（設計書17 §6.1・<c>ForcedOutcome</c>）。
        ///
        /// 稀プレーの絵を撮るのに「確率を上げて起きるまで待つ」必要は無い＝該当ゲートの抽選を
        /// 1.0 へ寄せて1打席で出す。強制した試合は <c>GameResult.HasForcedOutcomes</c> が立って
        /// digest・統計から自動的に外れるので、本番の帯には触れない。
        ///
        /// ⚠ 走塁系（盗塁・牽制・重盗）は<b>走者一塁・二塁空き・その打席の初球</b>でしか効かない
        /// （<c>GameEngine</c> の <c>ForcesStealAttempt</c> の分岐）。打席の頭で予約すること。
        /// </summary>
        public void ForceNextForCapture(KokoSim.Engine.Debugging.ForcedOutcome outcome)
            => _prog?.ForceNext(outcome);

        /// <summary>【撮影ハーネス用】いま走塁系の強制が効く状況か（走者一塁・二塁空き）。</summary>
        public bool CanForceStealPlayForCapture
            => _atBat is { BaseFirst: true, BaseSecond: false };

        /// <summary>診断用: 采配カードが受付中か（issue #493）。false＝投手が次球の振りかぶりへ入り締め切っている。</summary>
        public bool TacticsAcceptingInputForCapture => _signPad?.IsAcceptingInputForCapture ?? true;

        /// <summary>診断用: 対決ビューの投手が次球の振りかぶりへ既に切り替わったか（issue #493）。</summary>
        public bool DuelHasCutToNextWindupForCapture => _duelRig?.HasCutToNextWindup ?? false;

        /// <summary>スクショ用: いま止まっている采配窓の局面（回・表裏・カウント・塁）。無ければ null。</summary>
        public LiveAtBat CurrentAtBatForCapture => _atBat;

        /// <summary>スクショ用: 現在のライブ観戦スナップショット（HUD/スタメン列の数値の元, issue #397）。</summary>
        public MatchLiveSnapshot SnapshotForCapture() => _prog?.Snapshot();

        /// <summary>
        /// スクショ用: 盗塁カードを押した状態にする（狙う塁は塁状況から決まる・issue #622）。
        /// 自校が攻撃している窓でだけ効く＝実際にカードを押せる条件と同じ。
        /// </summary>
        public bool SelectStealForCapture()
        {
            if (_atBat == null || StealTargetNow == null) return false;
            if (_atBat.IsTop != _managerIsAway) return false;   // 守備の窓では攻撃の1手を出さない
            _stealChoice = StartType.Gamble;
            _signPad?.RefreshEnabled();
            return true;
        }

        /// <summary>スクショ用: 盗塁の再生（走者→捕手送球→判定）が走っているか。</summary>
        public bool StealPlayActiveForCapture => _stealPlay != null;

        /// <summary>スクショ用: 再生速度（待ち時間を詰める。画面の×N と同じ口）。</summary>
        public void SetSpeedForCapture(float speed) => _speed = speed;

        /// <summary>
        /// スクショ用: 進行だけを止める（タイムの導線は通らない）。再生中の1コマを撮るには、
        /// <see cref="FreezeCurrentAtResult"/> のように打席結果へ飛ばしてしまうと**その瞬間が消える**。
        /// </summary>
        public void SetPausedForCapture(bool paused) => _paused = paused;

        /// <summary>スクショ用: 盗塁カードのいまの表示名と可否（issue #622）。</summary>
        public string StealCardLabelForCapture => StealCardLabel;

        public bool StealCardEnabledForCapture => StealTargetNow != null;

        /// <summary>スクショ用: いま窓に映っている塁（"一二三" の在塁だけを並べた文字列）。</summary>
        public string BasesForCapture
        {
            get
            {
                if (_atBat == null) return "(窓なし)";
                var s = (_atBat.BaseFirst ? "一" : "") + (_atBat.BaseSecond ? "二" : "")
                        + (_atBat.BaseThird ? "三" : "");
                return s.Length == 0 ? "走者なし" : s;
            }
        }

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

        /// <summary>スクショ用（issue #716）: タイムを開いて「伝令を送る」を押した状態（伝令役の一覧）にする。</summary>
        internal void OpenMessengerPickerForCapture() { OpenPauseMenu(); OnSendTimeout(); }
        /// <summary>タイムのポップアップの今の ViewModel（撮影ハーネスが状態を書き換えて <see cref="RenderTimeoutForCapture"/> へ渡す）。</summary>
        internal TimeoutPopupModel TimeoutModelForCapture() => _prog == null ? null : BuildTimeoutModel();
        /// <summary>表示だけを差し替える（エンジンに触れない）。アウト・走者・伝令0回などの見え方の確認用。</summary>
        internal void RenderTimeoutForCapture(TimeoutPopupModel m) => _timeoutView?.Render(m);
        internal VisualElement PauseModalForCapture => _pauseModal;
        /// <summary>スクショ用（issue #716）: 伝令役の一覧で i 番目（背番号順でなく控えの添字）を選んで送る。</summary>
        internal void PickMessengerForCapture(int i) => SendTimeout(i);
        /// <summary>スクショ用: 能力ビューアを開く／閉じる／タブを切り替える（issue #602 の受け入れ絵）。以下 Scout 系はその一式。</summary>
        public void OpenScoutForCapture() { _paused = true; SetVisible(_pauseModal, true); _scoutPanel?.Open(); }
        public void CloseScoutForCapture() => _scoutPanel?.Close();
        public void SelectScoutTabForCapture(int tab) => _scoutPanel?.SelectTabForCapture(tab);
        public void SelectScoutTeamForCapture(bool own) => _scoutPanel?.SelectTeamForCapture(own);
        public void HoverScoutRowForCapture(int index) => _scoutPanel?.HoverRowForCapture(index);
        public bool ScoutOpenForCapture => _scoutPanel != null && _scoutPanel.IsOpen;

        /// <summary>スクショ用: 選手交代モーダルを開く（局面に合う種別で開く）。</summary>
        public void OpenSubstitutionForCapture() => _subPanel?.Open();

        /// <summary>スクショ用: 自校が今「攻撃側」か（交代モーダルの出し分け確認用）。</summary>
        public bool ManagerOnOffenseForCapture
            => _prog != null && _prog.SubstitutionOptions(_managerIsAway, ShownOffenseIsTop).IsOffense;

        /// <summary>スクショ用: 今このタイミングで代走を出せるか（塁上に走者がいる攻撃中）。</summary>
        public bool ManagerCanPinchRunForCapture
            => _prog != null && _prog.SubstitutionOptions(_managerIsAway).CanPinchRun;

        /// <summary>スクショ用: 予約した交代を確定して閉じる（issue #718 以降、✕／やめる は予約の取り消し）。</summary>
        public void CloseSubstitutionForCapture() => _subPanel?.CommitForCapture();
        /// <summary>スクショ用（issue #718）: 予約をまとめて取り消して閉じる（「全部やめる」）。</summary>
        public void CancelSubstitutionForCapture() => _subPanel?.CancelAllForCapture();

        /// <summary>スクショ用: 攻撃タブを選ぶ（0=代打 1=代走）。守備中はタブ無し＝守備の割当2ペイン（issue #256）。</summary>
        public void SelectSubstitutionKindForCapture(int index) => _subPanel?.SelectKindForCapture(index);

        /// <summary>スクショ用: 守備の割当枠を守備位置で選ぶ（issue #452 プレビュー撮影用）。</summary>
        public void PickSubstitutionSlotForCapture(KokoSim.Engine.Match.Field.FieldPosition pos) => _subPanel?.PickSlotForCapture(pos);

        /// <summary>スクショ用: 控え一覧の n 番目を選ぶ（行内プレビューを出す。issue #452）。</summary>
        public void PickSubstitutionCandidateForCapture(int index) => _subPanel?.PickCandidateForCapture(index);

        /// <summary>スクショ用: 代走に出す走者を選ぶ（負値＝先頭の走者。issue #452）。</summary>
        public void PickSubstitutionRunnerForCapture(int baseIndex) => _subPanel?.PickRunnerForCapture(baseIndex);

        /// <summary>スクショ用: 指枠の「そのまま退かせる」を選ぶ（先に指枠を主選択しておくこと。issue #452）。</summary>
        public void PickSubstitutionDhWithdrawForCapture() => _subPanel?.PickDhWithdrawForCapture();

        /// <summary>スクショ用: 投手が絡む比較の「投球／打撃・守備」トグルを切り替える。</summary>
        public void ToggleSubstitutionCmpViewForCapture() => _subPanel?.ToggleCmpFielderViewForCapture();

        /// <summary>スクショ用: いまDH制が生きているか（DH解除は不可逆＝前後の突き合わせに使う。issue #452）。</summary>
        public bool UsesDhForCapture => _subPanel != null && _subPanel.UsesDhForCapture;

        /// <summary>スクショ用: 指枠に居る選手（非DH＝null）。</summary>
        public KokoSim.Engine.Players.Player DhForCapture => _subPanel?.DhForCapture;

        /// <summary>スクショ用: 投手の打順スロット（DH制は -1＝打順外）。DH解除で打順へ入ったかの証拠。</summary>
        public int PitcherSlotForCapture => _subPanel?.PitcherSlotForCapture ?? -2;

        /// <summary>スクショ用: 現在の打順9人（交代反映済み）。退場した先発は打順から消えることで確かめる。</summary>
        public System.Collections.Generic.IReadOnlyList<KokoSim.Engine.Players.Player> LineupForCapture
            => _subPanel?.LineupForCapture ?? System.Array.Empty<KokoSim.Engine.Players.Player>();

        /// <summary>スクショ用: 交代モーダルが開いているか（確定に失敗すると開いたまま残る）。</summary>
        public bool SubstitutionOpenForCapture => _subPanel != null && _subPanel.IsOpen;

        /// <summary>スクショ用: いまその守備位置に就いている選手（確定後の姿を engine から引き直す）。</summary>
        public KokoSim.Engine.Players.Player FielderForCapture(KokoSim.Engine.Match.Field.FieldPosition pos)
            => _subPanel?.FielderForCapture(pos);

        /// <summary>スクショ用: 残っている控えの人数（交代が成立したかの証拠）。</summary>
        public int BenchCountForCapture => _subPanel?.BenchCountForCapture ?? -1;

        /// <summary>スクショ用: 交代を確定する（ボタンと同じ経路。issue #452 の操作一巡確認用）。</summary>
        public void ApplySubstitutionForCapture() => _subPanel?.ApplyForCapture();

        /// <summary>スクショ用: 交代モーダルのフッター文（保留中の操作の要約・エラー理由）。</summary>
        public string SubstitutionNoteForCapture => _subPanel?.NoteForCapture ?? "";

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
