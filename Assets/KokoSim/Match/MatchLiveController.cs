using System.Collections.Generic;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using KokoSim.Unity.Components;   // 部品辞書（LineScorePanel）
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
            public int ManagerTacticalSense = 70;
            public System.Action<GameResult> OnComplete;
        }

        /// <summary>次に MatchLive がアクティブ化されたとき消費するライブ観戦要求（一度使ったらクリア）。</summary>
        public static LiveMatchRequest Pending;

        [SerializeField] private ulong gameSeed = 20260718UL;
        [SerializeField] private string awayName = "北都大付属";
        [SerializeField] private string homeName = "桜丘";
        [Tooltip("タイムラインを持たない打席（三振・四球）の表示時間[秒]。")]
        [SerializeField] private float noPlayHoldSeconds = 1.2f;
        [Tooltip("投球フェーズの1球あたり表示時間[秒]（×2/×4速度に連動）。")]
        [SerializeField] private float pitchIntervalSeconds = 0.45f;
        [Tooltip("1球ごとの判定オーバーレイ（ストライク/ボール/ファール＋球速球種）の表示時間[秒]。")]
        [SerializeField] private float pitchCallHoldSeconds = 0.35f;
        [Tooltip("自校（後攻 home）の采配能力（委任AIの強さ・スキップ時に使用）。")]
        [SerializeField] private int managerTacticalSense = 70;

        // ライブ観戦（大会フロー）モードの状態。デモ生成モードでは false / null。
        private bool _managerIsAway;
        private System.Action<GameResult> _onComplete;
        private GameResult _finalResult;
        private Button _backHome;

        private VisualElement _root;
        private Match2DPlaybackElement _view;
        private Label _caption, _result, _batter;

        // ラインスコアを描き直したかのフラグ。掲示板は「プレーが確定してから点を掲げる」（実物の運用と同じ）
        // ので、打席の演出が resolved になった瞬間に1回だけ更新する（毎フレーム Snapshot を組み直さない）。
        private bool _lineScorePosted;
        private Button _nextPa, _subOpen, _skip;
        // 選手交代モーダル（設計書09 §6 / issue #22）。代打・代走・投手交代・守備交代・DH解除の入口。
        private MatchSubstitutionPanel _subPanel;
        private readonly List<Button> _speedButtons = new();
        // 速度ボタンのハンドラ（ラムダのため OnDisable で外せるよう保持する）。
        private readonly List<(Button Button, System.Action Handler)> _speedHandlers = new();

        // 3カラム（左=自校 / 右=相手）＋マッチアップHUD＋実況履歴。表示専用（数値はエンジン集計 Snapshot から引く）。
        private VisualElement _leftLineup, _rightLineup, _leftPitcher, _rightPitcher, _hudHost, _capHistHost;
        private Label _leftTeam, _rightTeam;

        // 1球采配ショートカット（設計書15 Phase C-3, Q12-2: 全球で常駐）。ITacticsBrain を経由しない
        // プレイヤーの手動指示。次に解決される打席の最初の1球にだけ効き、Advance() のたび自動で選択解除する
        // （エンジン側の「1球指示は消費後クリア」と表示を一致させる＝実は1球限りだと分かる）。
        private VisualElement _ptBattingSeg, _ptDefenseSeg;
        private PitchBattingOverride? _pitchBattingChoice;
        private PitchPolicy? _pitchPolicyChoice;
        private MatchLiveStatsProvider _statsProvider;
        private string _awayDisp = "先攻", _homeDisp = "後攻";
        private readonly List<string> _capHist = new();   // 実況履歴（直近3件）

        private MatchProgression _prog;
        private KokoSim.Engine.Match.Game.Team _homeTeam;
        private LivePlateAppearance _current;
        private double _t;
        private float _speed = 1f;
        private bool _replaying;   // 再生中（true）か采配窓待ち（false）か
        private bool _gameOver;

        // 中継風カウント（BSO）＋塁ダイヤ（#4）。投球フェーズで B/S を1球ずつ点灯させる。
        private VisualElement[] _ballDots, _strikeDots, _outDots, _bases;
        private int _pitchIdx;
        private float _pitchClock;
        private bool _inPitchPhase;   // 投球フェーズ（B/S点灯）中か。false なら打球フェーズ/静止。
        private bool _pitchBallVisible;   // 投球フェーズで今の1球の投球軌道を流しているか（#5）。

        // 1球ごとの判定オーバーレイ（issue #6）。判定＋球速球種を盤面上に大きく短時間だけ出す。
        // 球速・球種は実1球記録がある打席だけ（合成投球列は null＝出さない）。表示専用。
        private VisualElement _pitchCall;
        private Label _pitchCallJudge, _pitchCallDetail;
        private float _pitchCallClock;
        private bool _pitchCallOn;

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

            _leftLineup = _root.Q<VisualElement>("left-lineup");
            _rightLineup = _root.Q<VisualElement>("right-lineup");
            _leftPitcher = _root.Q<VisualElement>("left-pitcher");
            _rightPitcher = _root.Q<VisualElement>("right-pitcher");
            _hudHost = _root.Q<VisualElement>("hud-host");
            _capHistHost = _root.Q<VisualElement>("caption-history");
            _leftTeam = _root.Q<Label>("left-team");
            _rightTeam = _root.Q<Label>("right-team");
            _statsProvider = new MatchLiveStatsProvider();

            _caption = _root.Q<Label>("caption");
            _result = _root.Q<Label>("result-chip");
            _batter = _root.Q<Label>("batter");

            _nextPa = _root.Q<Button>("next-pa");
            if (_nextPa != null) _nextPa.clicked += OnNextPa;
            _subOpen = _root.Q<Button>("sub-open");
            if (_subOpen != null) _subOpen.clicked += OnOpenSubstitution;
            _skip = _root.Q<Button>("skip");
            if (_skip != null) _skip.clicked += OnSkip;
            _backHome = _root.Q<Button>("back-home");
            if (_backHome != null) _backHome.clicked += OnBackHome;
            WireSpeed("spd-1", 1f); WireSpeed("spd-2", 2f); WireSpeed("spd-4", 4f);
            WirePitchTactics();

            // B/S/O ランプと塁ダイヤは LineScorePanel（部品辞書）の右袖。1球ごとに動くのでここから直接トグルする。
            _ballDots = new[] { _root.Q<VisualElement>("lsc-b1"), _root.Q<VisualElement>("lsc-b2"), _root.Q<VisualElement>("lsc-b3") };
            _strikeDots = new[] { _root.Q<VisualElement>("lsc-s1"), _root.Q<VisualElement>("lsc-s2") };
            _outDots = new[] { _root.Q<VisualElement>("lsc-o1"), _root.Q<VisualElement>("lsc-o2") };
            _bases = new[] { _root.Q<VisualElement>("lsc-base-1"), _root.Q<VisualElement>("lsc-base-2"), _root.Q<VisualElement>("lsc-base-3") };

            _pitchCall = _root.Q<VisualElement>("pitch-call");
            _pitchCallJudge = _root.Q<Label>("pitch-call-judge");
            _pitchCallDetail = _root.Q<Label>("pitch-call-detail");

            ResetPerMatchVisuals();
            _subPanel = new MatchSubstitutionPanel(_root, OnSubstitutionApplied);
            _subPanel.Bind();
            BuildGame();
            _subPanel.SetMatch(_prog, _managerIsAway);
            // 試合開始前の掲示板（校名と空の升目）を先に掲げる。掲げないと最初の打席が確定するまで枠が出ない。
            PostLineScore(1, isTop: true, finished: false);
            SetBackHomeVisible(false);
            EnterTacticsWindow("試合開始。采配を選んで打席へ。");
            RefreshPanel();   // 初期スタメン列（今日の成績は0・現打者/HUDは打席開始で点灯）
            _view.SetResting(false, false, false);   // 打席開始前も守備陣を定位置に表示（盤面を空にしない）
        }

        // 画面を離れるときにハンドラを外す（OnEnable が毎回登録するため、外さないと往復のたび多重登録になり
        // 1クリックで複数打席進んでしまう）。
        private void OnDisable()
        {
            if (_nextPa != null) _nextPa.clicked -= OnNextPa;
            if (_subOpen != null) _subOpen.clicked -= OnOpenSubstitution;
            _subPanel?.Close();
            if (_skip != null) _skip.clicked -= OnSkip;
            if (_backHome != null) _backHome.clicked -= OnBackHome;
            foreach (var (b, h) in _speedHandlers) b.clicked -= h;
            _speedHandlers.Clear();
            _speedButtons.Clear();
        }

        // 試合ごとに初期化する進行状態（試合をまたいだ持ち越し禁止）。要素クエリ前に呼べるフィールドだけを扱う。
        private void ResetPerMatchState()
        {
            _gameOver = false;
            _replaying = false;
            _finalResult = null;
            _onComplete = null;
            _current = null;
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
            _pitchBattingChoice = null;
            _pitchPolicyChoice = null;
            _capHist.Clear();
            _speedButtons.Clear();
            _speedHandlers.Clear();
        }

        // 試合ごとに初期化する表示（前試合の実況履歴・BSO・結果チップを残さない）。要素クエリ後に呼ぶ。
        private void ResetPerMatchVisuals()
        {
            _capHistHost?.Clear();
            ClearSegHighlight(_ptBattingSeg);
            ClearSegHighlight(_ptDefenseSeg);
            _lineScorePosted = false;
            SetCount(0, 0);
            SetOuts(0);
            SetBases(false, false, false);
            HidePitchCall();
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
                _homeTeam = null;   // 実試合は進行体が保持（代打候補名の外部参照は使わない）
                _awayDisp = req.AwayName ?? awayName;
                _homeDisp = req.HomeName ?? homeName;
                // 掲示板の校名はエンジンの観測データ（LiveLineScore.Name）から出るのでここでは埋めない。
                SetColumnTitles();
                return;
            }

            // デモ/スクショ用の自前生成（本番フローでは通らない）。
            _managerIsAway = false;
            _onComplete = null;
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
            // 観戦する試合だけ CaptureTimelines=true（裏試合は GameEngine を通らずコストゼロ）。
            var ctx = new GameContext { CaptureTimelines = true };
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

        // ── 采配窓（打席前・再生停止中） ──
        private void EnterTacticsWindow(string note)
        {
            _replaying = false;
            if (_caption != null) _caption.text = note;
            var canAct = !_gameOver;
            SetEnabled(_nextPa, canAct);
            SetEnabled(_subOpen, canAct);
            SetEnabled(_skip, canAct);
        }

        private void OnNextPa()
        {
            if (_gameOver) return;
            ResolveAndReplayNext();
        }

        // 選手交代（設計書09 §6）。攻撃中／守備中で出せる選択肢はモーダル側が出し分ける。
        // 自校が先攻(away)か後攻(home)かは _managerIsAway。
        private void OnOpenSubstitution()
        {
            if (_gameOver || _prog == null) return;
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
            if (_gameOver) return;
            var result = _prog.SkipDelegateToAi(_managerIsAway, managerTacticalSense);
            _view.SetPlay(null);
            if (_caption != null) _caption.text = "以降を委任して試合終了。";
            EndGame(result);
        }

        // 終局処理（自然終了・スキップ委任 共通）。結果を確定表示し、ライブ観戦なら試合結果画面へ渡す。
        private void EndGame(GameResult result)
        {
            _gameOver = true;
            _replaying = false;
            _finalResult = result;
            UpdateScoreboardFinal(result);
            if (_result != null) { _result.text = FinalResultText(result); _result.style.display = DisplayStyle.Flex; }
            EnterTacticsWindow("試合終了。");
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
        private void WirePitchTactics()
        {
            _ptBattingSeg = _root.Q<VisualElement>("pt-batting");
            _ptDefenseSeg = _root.Q<VisualElement>("pt-defense");
            WireSegCell("pt-batting-swing", _ptBattingSeg, () => _pitchBattingChoice = PitchBattingOverride.ForceSwing);
            WireSegCell("pt-batting-take", _ptBattingSeg, () => _pitchBattingChoice = PitchBattingOverride.ForceTake);
            WireSegCell("pt-batting-auto", _ptBattingSeg, () => _pitchBattingChoice = null);
            WireSegCell("pt-defense-break", _ptDefenseSeg, () => _pitchPolicyChoice = PitchPolicy.BreakingHeavy);
            WireSegCell("pt-defense-zone", _ptDefenseSeg, () => _pitchPolicyChoice = PitchPolicy.ControlFirst);
            WireSegCell("pt-defense-auto", _ptDefenseSeg, () => _pitchPolicyChoice = null);
        }

        private void WireSegCell(string name, VisualElement seg, System.Action onPick)
        {
            var cell = _root.Q<Label>(name);
            if (cell == null || seg == null) return;
            cell.pickingMode = PickingMode.Position;
            cell.RegisterCallback<ClickEvent>(_ =>
            {
                onPick();
                foreach (var c in seg.Children()) c.EnableInClassList("seg__cell--on", c == cell);
            });
        }

        // 次に Advance() する打席の最初の1球へ、現在の選択を予約する（無指示=null なら従来と完全一致）。
        private void PushPitchTacticsChoice()
        {
            _prog.SetPitchBattingOverride(_managerIsAway, _pitchBattingChoice);
            _prog.SetPitchDefenseOverride(!_managerIsAway, _pitchPolicyChoice, null);
        }

        // 1球指示は消費後に自動解除（Q12-3: 次球は方針に復帰）。表示もそれに合わせて既定(無点灯)へ戻す。
        private void ResetPitchTacticsChoice()
        {
            _pitchBattingChoice = null;
            _pitchPolicyChoice = null;
            ClearSegHighlight(_ptBattingSeg);
            ClearSegHighlight(_ptDefenseSeg);
        }

        private static void ClearSegHighlight(VisualElement seg)
        {
            if (seg == null) return;
            foreach (var c in seg.Children()) c.RemoveFromClassList("seg__cell--on");
        }

        private void ResolveAndReplayNext()
        {
            PushPitchTacticsChoice();
            if (!_prog.Advance())
            {
                ResetPitchTacticsChoice();
                EndGame(_prog.BuildResult());
                return;
            }
            ResetPitchTacticsChoice();

            _current = _prog.Current;
            _t = 0;
            UpdateScoreboard(_current, resolved: false);
            SetText("batter", _current.BatterName);
            RefreshPanel();   // 打席確定＝今日の成績・現打者ハイライト・マッチアップHUD を更新

            // BSO 初期化: アウト＝打席前、塁ダイヤ＝打席前、カウント＝0-0。結果チップは投球フェーズ中は隠す。
            SetOuts(_current.OutsBefore);
            SetBases(_current.BaseFirstBefore, _current.BaseSecondBefore, _current.BaseThirdBefore);
            SetCount(0, 0);
            if (_result != null) _result.style.display = DisplayStyle.None;

            // 投球フェーズ（B/S を1球ずつ点灯）。盤面は静止で待つ。
            _pitchIdx = 0;
            _pitchClock = 0f;
            HidePitchCall();
            // Play==null（三振・四球）でも守備陣＋走者を静止表示する（打席前の塁状況）。
            if (_current.Play != null) _view.SetPlay(_current.Play);
            else _view.SetResting(_current.BaseFirstBefore, _current.BaseSecondBefore, _current.BaseThirdBefore);
            _replaying = true;
            if (_caption != null) _caption.text = _current.BatterName + " 打席";

            var pitches = _current.PitchSeq?.Pitches;
            _inPitchPhase = pitches != null && pitches.Count > 0;
            if (_inPitchPhase) BeginPitchBall(0);        // 1球目の投球軌道を盤面へ（#5）
            else EnterBattedBallOrHold();                // 投球列が空でも先へ進む

            SetEnabled(_nextPa, false);
            SetEnabled(_subOpen, false);
            SetEnabled(_skip, false);
        }

        // 投球フェーズ: index 球目の投球軌道（マウンド→本塁）を盤面へ流す（#5）。
        // 捕手からの返球は描画しない。打球になる最後の1球だけは、続く打球タイムラインが同じ投球
        // セグメントを先頭に持つので二重に描かず、静止表示のまま打球フェーズへ渡す。
        private void BeginPitchBall(int index)
        {
            var last = _current.PitchSeq.Pitches.Count - 1;
            _pitchBallVisible = _current.Play == null || index < last;
            if (_pitchBallVisible)
                _view.SetPlay(PitchPlaybackFactory.PitchOnly(
                    _current.BaseFirstBefore, _current.BaseSecondBefore, _current.BaseThirdBefore));
            else
                _view.SetResting(_current.BaseFirstBefore, _current.BaseSecondBefore, _current.BaseThirdBefore);
        }

        // 投球フェーズ終了→打球再生（Play あり）または結果保持（三振・四球）。
        private void EnterBattedBallOrHold()
        {
            _inPitchPhase = false;
            _pitchBallVisible = false;
            _t = 0;
            if (_current.Play != null)
            {
                _view.SetPlay(_current.Play);
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

        // 打席解決: 塁ダイヤを結果へ更新してから采配窓へ。
        private void FinishPaView()
        {
            HidePitchCall();
            SetBases(_current.BaseFirstAfter, _current.BaseSecondAfter, _current.BaseThirdAfter);
            PushHistory(HistoryLine(_current));   // プレー確定ごとに実況履歴へ1件積む
            EnterTacticsWindow(NextPrompt());
        }

        private void Update()
        {
            // 判定オーバーレイは自前の寿命で消す（打球再生へ移った直後の1球分もそのまま見せる）。
            if (_pitchCallOn)
            {
                _pitchCallClock += Time.deltaTime * _speed;
                if (_pitchCallClock >= pitchCallHoldSeconds) HidePitchCall();
            }

            if (!_replaying || _current == null) return;

            if (_inPitchPhase)
            {
                _pitchClock += Time.deltaTime * _speed;
                // 1球ぶんの投球軌道を毎フレーム進める（0.45s で本塁到達。以降は到達点で保持）。
                if (_pitchBallVisible) _view.SetTime(_pitchClock);
                var pitches = _current.PitchSeq.Pitches;
                while (_inPitchPhase && _pitchClock >= pitchIntervalSeconds)
                {
                    _pitchClock -= pitchIntervalSeconds;
                    var pt = pitches[_pitchIdx];
                    SetCount(pt.BallsAfter, pt.StrikesAfter);
                    ShowPitchCall(pt);
                    _pitchIdx++;
                    if (_pitchIdx >= pitches.Count) EnterBattedBallOrHold();
                    else BeginPitchBall(_pitchIdx);   // 次の1球の投球軌道を頭から流す
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
            if (!deadOut) return play.Dur;

            var lastEvent = play.ResAt;
            foreach (var seg in play.Ball) if (seg.T1 > lastEvent) lastEvent = seg.T1;
            return System.Math.Min(play.Dur, lastEvent + 0.6);
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
        }

        // ── 中継風カウント（BSO）＋塁ダイヤ ──
        private static void SetDots(VisualElement[] dots, int n)
        {
            if (dots == null) return;
            for (var i = 0; i < dots.Length; i++)
                dots[i]?.EnableInClassList("is-on", i < n);
        }

        private void SetCount(int balls, int strikes)
        {
            SetDots(_ballDots, balls);
            SetDots(_strikeDots, strikes);
        }

        private void SetOuts(int outs) => SetDots(_outDots, outs);

        // ── 1球ごとの判定オーバーレイ（issue #6） ──
        // 判定（ストライク/ボール/ファール…）を盤面上に大きく出し、実1球記録がある打席では球速・球種を併記する。
        // 合成投球列（実記録なし・PitchSequenceSynthesizer）は球種・球速を持たないので判定だけを出す。
        private void ShowPitchCall(PitchToken pt)
        {
            if (_pitchCall == null || _pitchCallJudge == null) return;

            _pitchCallJudge.text = PitchJudgeJp(pt.Kind);
            _pitchCallJudge.EnableInClassList("pitch-call__judge--strike", IsStrikeCall(pt.Kind));
            _pitchCallJudge.EnableInClassList("pitch-call__judge--ball", pt.Kind == KokoSim.Engine.Match.AtBat.PitchKind.Ball);

            if (_pitchCallDetail != null)
            {
                var detail = PitchDetailText(pt);
                _pitchCallDetail.text = detail;
                _pitchCallDetail.style.display = detail.Length == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            }

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

        private static bool IsStrikeCall(KokoSim.Engine.Match.AtBat.PitchKind k) =>
            k == KokoSim.Engine.Match.AtBat.PitchKind.CalledStrike || k == KokoSim.Engine.Match.AtBat.PitchKind.SwingingStrike;

        private static string PitchJudgeJp(KokoSim.Engine.Match.AtBat.PitchKind k)
        {
            switch (k)
            {
                case KokoSim.Engine.Match.AtBat.PitchKind.Ball: return "ボール";
                case KokoSim.Engine.Match.AtBat.PitchKind.CalledStrike: return "ストライク";
                case KokoSim.Engine.Match.AtBat.PitchKind.SwingingStrike: return "ストライク";
                case KokoSim.Engine.Match.AtBat.PitchKind.Foul: return "ファール";
                case KokoSim.Engine.Match.AtBat.PitchKind.InPlay: return "打った";
                case KokoSim.Engine.Match.AtBat.PitchKind.HitByPitch: return "死球";
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

        private static void SetEnabled(Button b, bool on) { if (b != null) b.SetEnabled(on); }

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
            var offenseIsOwn = snap.OffenseIsTop == ownIsAway;   // 攻撃側が自校か
            var order = snap.CurrentBatterOrder;

            // 左右とも同じ並び（打順→守備→名前→背番号→調子→今日）。ミラーはしない。
            BuildColumn(_leftLineup, _leftPitcher, own, mirror: false, highlightOrder: offenseIsOwn ? order : 0);
            BuildColumn(_rightLineup, _rightPitcher, opp, mirror: false, highlightOrder: offenseIsOwn ? 0 : order);
            RefreshHud(snap);
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

        // 並び: 打順 / 守備位置 / 名前(フルネーム) / 背番号 / 調子 / 今日の成績（右列は row-reverse でミラー）。
        private VisualElement BuildLineupRow(LiveBatterSlot s, bool mirror, bool atBat)
        {
            var row = new VisualElement();
            row.AddToClassList("mlineup-row");
            if (mirror) row.AddToClassList("mlineup-row--mirror");
            if (atBat) row.AddToClassList("mlineup-row--at-bat");

            row.Add(MakeLabel(s.Order.ToString(), "mlineup-row__ord"));
            row.Add(MakeLabel(MatchLiveStatsProvider.PosAbbr(s.Position), "mlineup-row__pos"));
            row.Add(MakeLabel(s.Name, "mlineup-row__name"));   // フルネーム
            if (!string.IsNullOrEmpty(s.ReplacedName))
                row.Add(MakeLabel("←" + s.ReplacedName, "mlineup-row__replaced"));
            row.Add(MakeLabel(NumText(s.Number), "num-badge", "num-badge--sm"));
            var face = new ConditionFace();
            face.AddToClassList("mlineup-row__cond");
            face.Set(_statsProvider.ConditionOf(s.SourceId));   // 相手校(null)は描かない
            row.Add(face);
            row.Add(MakeLabel(MatchLiveStatsProvider.TodayLine(s.AtBats, s.Hits, s.Rbi), "mlineup-row__today"));
            return row;
        }

        // 控え1行（背番号・名前フル・調子）。守備位置は出さない。代打・代走・守備固めの候補。
        private VisualElement BuildBenchRow(LiveBatterSlot s, bool mirror)
        {
            var row = new VisualElement();
            row.AddToClassList("mlineup-benchrow");
            if (mirror) row.AddToClassList("mlineup-row--mirror");
            row.Add(MakeLabel(NumText(s.Number), "num-badge", "num-badge--sm"));
            row.Add(MakeLabel(s.Name, "mlineup-row__name"));
            var face = new ConditionFace();
            face.AddToClassList("mlineup-row__cond");
            face.Set(_statsProvider.ConditionOf(s.SourceId));
            row.Add(face);
            return row;
        }

        // 控え投手1行（背番号・名前フル・調子）。
        private VisualElement BuildBenchPitcherRow(LivePitcherToday p, bool mirror)
        {
            var row = new VisualElement();
            row.AddToClassList("mlineup-benchrow");
            if (mirror) row.AddToClassList("mlineup-row--mirror");
            row.Add(MakeLabel(NumText(p.Number), "num-badge", "num-badge--sm"));
            row.Add(MakeLabel(p.Name, "mlineup-row__name"));
            var face = new ConditionFace();
            face.AddToClassList("mlineup-row__cond");
            face.Set(_statsProvider.ConditionOf(p.SourceId));
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

        // ── マッチアップHUD（盤面上に画面座標固定・左下=投手/右下=打者） ──
        private void RefreshHud(MatchLiveSnapshot snap)
        {
            if (_hudHost == null) return;
            _hudHost.Clear();
            if (_current == null) return;   // 打席開始前は空（トークンを隠さない）

            var defPitcher = _current.IsTop ? snap.Home.Pitcher : snap.Away.Pitcher; // 守備側投手
            var offense = _current.IsTop ? snap.Away.Lineup : snap.Home.Lineup;
            LiveBatterSlot batterToday = _current.BatterOrder >= 1 && _current.BatterOrder <= offense.Count
                ? offense[_current.BatterOrder - 1] : null;

            _hudHost.Add(HudCorner("mhud-corner--left", BuildHudPitcher(defPitcher)));
            _hudHost.Add(HudCorner("mhud-corner--right", BuildHudBatter(batterToday)));
        }

        private static VisualElement HudCorner(string sideClass, VisualElement block)
        {
            var corner = new VisualElement();
            corner.AddToClassList("mhud-corner");
            corner.AddToClassList(sideClass);
            var panel = new VisualElement();
            panel.AddToClassList("mhud-panel");
            panel.Add(block);
            corner.Add(panel);
            return corner;
        }

        private VisualElement BuildHudPitcher(LivePitcherToday def)
        {
            var block = new VisualElement();
            block.AddToClassList("mup-block");
            block.AddToClassList("mup-block--pitcher");

            var line1 = MakeRow("mup-line");
            line1.Add(MakeLabel(NumText(_current.PitcherNumber), "num-badge", "num-badge--sm"));
            line1.Add(MakeLabel(_current.PitcherName, "mup-name"));
            line1.Add(MakeLabel(MatchLiveStatsProvider.ThrowsLabel(_current.PitcherThrows), "mup-hand"));
            block.Add(line1);

            var line2 = MakeRow("mup-line");
            line2.Add(MakeLabel("防 " + MatchLiveStatsProvider.Era(_statsProvider.CareerPitching(_current.PitcherSourceId))
                                    + "　奪三振 " + def.StrikeOuts, "mup-career"));
            block.Add(line2);

            var line3 = MakeRow("mup-line");
            line3.Add(MakeLabel("球数", "mup-pitches__lab"));
            var pc = MakeLabel(def.Pitches.ToString(), "mup-pitches");
            if (def.Pitches > 100) pc.AddToClassList("mup-pitches--alert");
            else if (def.Pitches > 80) pc.AddToClassList("mup-pitches--warn");
            line3.Add(pc);
            line3.Add(MakeLabel("球", "mup-pitches__lab"));
            block.Add(line3);
            return block;
        }

        private VisualElement BuildHudBatter(LiveBatterSlot batterToday)
        {
            var block = new VisualElement();
            block.AddToClassList("mup-block");
            block.AddToClassList("mup-block--batter");

            var line1 = MakeRow("mup-line");
            line1.Add(MakeLabel(NumText(_current.BatterNumber), "num-badge", "num-badge--sm"));
            line1.Add(MakeLabel(_current.BatterName, "mup-name"));
            line1.Add(MakeLabel(MatchLiveStatsProvider.BatsLabel(_current.BatterBats), "mup-hand"));
            var face = new ConditionFace();
            face.AddToClassList("mup-cond");
            face.Set(_statsProvider.ConditionOf(_current.BatterSourceId));
            line1.Add(face);
            block.Add(line1);

            var b = _statsProvider.CareerBatting(_current.BatterSourceId);
            var career = b == null
                ? "通算 —"
                : MatchLiveStatsProvider.Avg(b) + " / " + b.HomeRuns + "本 / " + b.Rbi + "点 / —盗";
            var line2 = MakeRow("mup-line");
            line2.Add(MakeLabel(career, "mup-career"));
            block.Add(line2);

            var line3 = MakeRow("mup-line");
            line3.Add(MakeLabel("今日", "mup-today__lab"));
            var t = batterToday != null
                ? MatchLiveStatsProvider.TodayLine(batterToday.AtBats, batterToday.Hits, batterToday.Rbi)
                : "0-0";
            line3.Add(MakeLabel(t, "mup-today"));
            block.Add(line3);
            return block;
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

        private static VisualElement MakeRow(string cls)
        {
            var v = new VisualElement { pickingMode = PickingMode.Ignore };
            v.AddToClassList(cls);
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
            var name = v switch
            {
                PitchBattingOverride.ForceSwing => "pt-batting-swing",
                PitchBattingOverride.ForceTake => "pt-batting-take",
                _ => "pt-batting-auto",
            };
            if (_ptBattingSeg == null) return;
            foreach (var c in _ptBattingSeg.Children()) c.EnableInClassList("seg__cell--on", c.name == name);
        }

        /// <summary>スクショ用: 1球采配ショートカットをクリックしたのと同じ状態にする（配球側）。</summary>
        public void SelectPitchDefenseForCapture(PitchPolicy? v)
        {
            _pitchPolicyChoice = v;
            var name = v switch
            {
                PitchPolicy.BreakingHeavy => "pt-defense-break",
                PitchPolicy.ControlFirst => "pt-defense-zone",
                _ => "pt-defense-auto",
            };
            if (_ptDefenseSeg == null) return;
            foreach (var c in _ptDefenseSeg.Children()) c.EnableInClassList("seg__cell--on", c.name == name);
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

        /// <summary>スクショ用: 選手交代モーダルを開く（局面に合う種別で開く）。</summary>
        public void OpenSubstitutionForCapture() => _subPanel?.Open();

        /// <summary>スクショ用: 自校が今「攻撃側」か（交代モーダルの出し分け確認用）。</summary>
        public bool ManagerOnOffenseForCapture
            => _prog != null && _prog.SubstitutionOptions(_managerIsAway).IsOffense;

        /// <summary>スクショ用: 今このタイミングで代走を出せるか（塁上に走者がいる攻撃中）。</summary>
        public bool ManagerCanPinchRunForCapture
            => _prog != null && _prog.SubstitutionOptions(_managerIsAway).CanPinchRun;

        /// <summary>スクショ用: 選手交代モーダルを閉じる。</summary>
        public void CloseSubstitutionForCapture() => _subPanel?.Close();

        /// <summary>スクショ用: 交代種別タブを選ぶ（0=代打 1=代走 2=投手交代 3=守備交代 4=DH解除）。</summary>
        public void SelectSubstitutionKindForCapture(int index) => _subPanel?.SelectKindForCapture(index);

        /// <summary>スクショ用: 自校の控え先頭（代打候補）の名前。</summary>
        public string HomeBenchZeroName =>
            _homeTeam != null && _homeTeam.Bench.Count > 0 ? _homeTeam.Bench[0].Name : "";

        /// <summary>スクショ用: ライブ観戦の「ホームへ戻る」を実行する（終局後のみ有効）。</summary>
        public void BackHomeForCapture() => OnBackHome();

        /// <summary>スクショ用: 「ホームへ戻る」ボタンが表示中か（終局＋ライブ観戦モードで true）。</summary>
        public bool BackHomeVisible => _backHome != null && _backHome.style.display.value == DisplayStyle.Flex;

        /// <summary>スクショ用: 現在ロード中の打席を任意時刻 t で静止表示する（守備の動き確認用）。</summary>
        public void SeekForCapture(double t)
        {
            _replaying = false;
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
            _replaying = false;
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
