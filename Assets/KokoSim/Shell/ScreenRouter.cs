using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 画面フロー（設計書06 §4）。ナビバーのクリックで画面を切り替える軽量ルーター。
    /// 切替は各画面 GameObject の SetActive で行う（＝パネルには常に1画面の UIDocument ツリーだけが載る）。
    /// 複数 UIDocument が同一 PanelSettings に同居すると描画競合でパネルごと表示されなくなるため、
    /// display 切替ではなく SetActive で確実に1ツリーに保つ。各画面が自前のナビ（該当項目ハイライト済み）を持つ。
    /// </summary>
    public sealed class ScreenRouter : MonoBehaviour
    {
        // ナビ項目 name → 画面 GameObject 名。未マップの項目（大会・スカウト…）はクリックしても無反応。
        // MatchScreen: 試合はカレンダー進行（週送り）から遷移する設計のため、ナビの「試合」タブは撤去済み
        // （設計書06 §3.4: ハブ＆スポークのスポーク＝遷移先画面）。ここに残すのは休眠画面として
        // 起動時に確実に SetActive(false) にするためだけ（外すと描画競合でパネルが消える）。nav-match の
        // UI要素はどの画面にも無いので、配線ループでは no-op になりクリック導線は生じない。
        private static readonly (string Nav, string Go)[] Map =
        {
            ("nav-home", "HomeDashboard"),
            ("nav-players", "PlayerList"),
            ("nav-member", "MemberSetting"),
            ("nav-training", "TrainingPlan"),
            ("nav-practice", "PracticeMatch"),
            ("nav-match", "MatchScreen"),
            ("nav-tournament", "TournamentPreview"),
        };

        // タブに現れない従属画面（一覧→詳細／試合前スタメン設定など）。ナビ項目は持たないが Show 対象＋
        // 起動時 SetActive(false) 管理に含める。LineupSetting は大会の試合開始フローからのみ遷移する。
        // MatchPreview は試合開始フロー（スタメンOK→対戦カード→試合開始）の中継画面。
        // MatchLive は試合開始フロー（対戦カード→ホーム→ライブ観戦）からのみ遷移する実試合の2D俯瞰観戦画面。
        // MatchResult はその出口（ライブ観戦の終局で自動遷移するボックススコア画面。閉じるとホームへ戻る）。
        private static readonly string[] ExtraScreens =
            { "PlayerDetail", "LineupSetting", "TeamStrength", "MatchPreview", "MatchLive", "MatchResult" };

        private const string DefaultScreen = "HomeDashboard";

        // ホーム以外で「今週を進める」を持つ画面。ここで引退週に入ったらホームの主将指名モーダルへ回送する。
        private static readonly string[] WeekAdvancers = { "TrainingPlan", "TournamentPreview", "PracticeMatch" };

        /// <summary>プログラム遷移用（試合開始→スタメン設定→ホームなど、ナビを介さない画面切替）。</summary>
        public static ScreenRouter Instance { get; private set; }

        private readonly Dictionary<string, GameObject> _screens = new Dictionary<string, GameObject>();

        // ナビクリックで即 Show せず、次フレームの Update で切り替える遅延先。
        // Show は SetActive で画面GameObjectを付け替えるが、これをクリックの ClickEvent 配信中に
        // 同期実行すると「配信中パネルを SetActive(false) する」ことになり UITK のイベント木が壊れて
        // 全画面が非アクティブ化する（＝盤面が真っ黒に落ちる）。遅延させて配信外で切り替える。
        // 累積登録された複数ハンドラも同じ文字列を代入するだけになり無害化される。
        private string _pending;

        // 現在アクティブな画面 GameObject 名（回送の判定に使う）。
        private string _current;

        private void Awake() => Instance = this;

        // 遅延した画面切替をイベント配信外（次フレーム）で処理する。
        private void Update()
        {
            // 新チーム発足（夏の3年引退の翌週）の主将指名は、どの画面で週を進めても
            // ホームの指名モーダルへ回送する（導線を1箇所に集約する, 設計書09 §8）。
            // 回送元は「週を進められる画面」だけに限る（試合中フローの画面から引き剥がさない）。
            if (_pending == null && NewTeamService.Pending && System.Array.IndexOf(WeekAdvancers, _current) >= 0)
                _pending = DefaultScreen;

            if (_pending == null) return;
            var target = _pending;
            _pending = null;
            Show(target);
        }

        private void Start()
        {
            // UITK パネルのスケールを起動時にコードで確定させる（アセット任せにしない）。
            EnforcePanelScale();

            // 画面GameObjectを取得する。非アクティブでも確実に拾う（GameObject.Find はアクティブしか
            // 見つけられないため、起動時にアクティブでない画面が _screens に入らず、そのタブへ切り替えると
            // 「他画面を非アクティブ化 → 目的画面は辞書に無く TryGetValue 失敗 → return」で全画面が
            // 消える＝盤面が真っ黒に落ちる。シーンルートを非アクティブ込みで走査して確実に登録する）。
            foreach (var (_, goName) in Map)
            {
                var go = FindInScene(goName);
                if (go != null) _screens[goName] = go;
            }
            foreach (var goName in ExtraScreens)
            {
                var go = FindInScene(goName);
                if (go != null) _screens[goName] = go;
            }
            Show(DefaultScreen);
        }

        // 非アクティブな GameObject も含めてロード済みシーンから名前で探す（GameObject.Find の代替）。
        private static GameObject FindInScene(string name)
        {
            for (var s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    // 自身と子孫（非アクティブ込み）を走査。
                    var all = root.GetComponentsInChildren<Transform>(true);
                    foreach (var t in all)
                        if (t.gameObject.name == name) return t.gameObject;
                }
            }
            return null;
        }

        /// <summary>
        /// UITK パネルのスケール指定を起動時にコードで確定させる。
        /// PanelSettings アセットの m_ScaleMode はエディタのインポートキャッシュと desync しやすく、
        /// ConstantPhysicalSize のまま読み込まれると scaledPixelsPerPoint が DPI 依存の非整数
        /// （例: Screen.dpi 257 / referenceDpi 96 = 2.68）になり、論理解像度が 1600→約598px に潰れて
        /// ヘッダー等のレイアウトが破綻し文字も滲む。ここで必ず ScaleWithScreenSize / 1600x900 へ揃え、
        /// 決定論的に整数スケール運用（ウィンドウ=参照解像度の整数倍で ppp=1.0/2.0）へ寄せる。
        /// 将来の「画面解像度から最寄り整数倍(1.0/2.0)を選び、余りを余白/レイアウトで吸収」処理も
        /// この一箇所に集約する。全画面が同一 PanelSettings を共有するため1つ設定すれば足りる。
        /// </summary>
        private void EnforcePanelScale()
        {
            foreach (var (_, goName) in Map)
            {
                var go = FindInScene(goName);
                var doc = go != null ? go.GetComponent<UIDocument>() : null;
                var ps = doc != null ? doc.panelSettings : null;
                if (ps == null) continue;
                ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                ps.referenceResolution = new Vector2Int(1600, 900);
                ps.match = 0.5f;
                return;
            }
        }

        /// <summary>
        /// 次フレーム（Update）で画面を切り替える。<see cref="Show"/> や OnEnable の内側から呼んでも
        /// 「Show のネスト（配信中の SetActive 切替）」を避けられる＝全画面非アクティブ落ちを防ぐ。
        /// 例: HomeDashboard.OnEnable（＝別の Show の内側で走る）から MatchLive へ遷移する場合はこれを使う。
        /// </summary>
        public void ShowDeferred(string target) => _pending = target;

        /// <summary>指定画面のみをアクティブにし、そのナビにクリックを配線する。</summary>
        public void Show(string target)
        {
            foreach (var kv in _screens)
                kv.Value.SetActive(kv.Key == target);
            _current = target;

            if (!_screens.TryGetValue(target, out var go)) return;
            var doc = go.GetComponent<UIDocument>();
            var root = doc != null ? doc.rootVisualElement : null;
            if (root == null) return;

            // SetActive(true) で UIDocument がツリーを再構築するたびにナビを配線し直し、
            // 共通トップバー（Components/TopBar.uxml）のアクティブタブ強調も付け直す。
            foreach (var (navName, goName) in Map)
            {
                var item = root.Q<VisualElement>(navName);
                if (item == null) continue;
                item.EnableInClassList("nav__item--on", goName == target);   // 現在画面のタブを強調
                var next = goName; // クロージャ捕捉
                // 即 Show せず遅延（再入防止）。Update が次フレームで実際の切替を行う。
                item.RegisterCallback<ClickEvent>(_ => _pending = next);
            }
        }
    }
}
