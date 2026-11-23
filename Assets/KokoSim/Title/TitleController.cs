using KokoSim.Unity.Shell;   // ScreenRouter
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Title
{
    /// <summary>
    /// タイトル画面（issue #482・B案 球場バック型）。起動直後の入口。
    /// 「はじめから」→ 県選択画面（日本地図・issue #1410）→ 新規ゲーム設定画面（issue #585。新設校の校名・配色をそこで決める）へ。「つづきから」はセーブが1件も無ければ非活性、あればセーブスロット選択画面
    /// （issue #508）へ遷移する。「終了」は Editor / Player の両方で意図どおり動くよう分岐する。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class TitleController : MonoBehaviour
    {
        private Button _newGame;
        private Button _quit;
        private Button _continueBtn;

        private void OnEnable()
        {
            var root = GetComponent<UIDocument>().rootVisualElement;
            _newGame = root.Q<Button>("ts-new-game");
            _quit = root.Q<Button>("ts-quit");
            _continueBtn = root.Q<Button>("ts-continue");

            if (_newGame != null) _newGame.clicked += OnNewGame;
            if (_quit != null) _quit.clicked += OnQuit;
            // ブラウザ版はタブを閉じれば終わる＝「終了」を出さない（issue #1534）。
            if (_quit != null) _quit.style.display = BuildPlatform.IsBrowser ? DisplayStyle.None : DisplayStyle.Flex;
            if (_continueBtn != null) _continueBtn.clicked += OnContinue;

            // セーブが1件も無ければ押せない見た目にする（issue #508）。
            _continueBtn?.SetEnabled(SaveSlots.AnySaveExists());

            // 版表記は Player Settings の Version（bundleVersion）を正典にする（issue #1310）。
            var version = root.Q<Label>("ts-version");
            if (version != null) version.text = "v" + Application.version;

            // デバッグ試合（issue #905）は開発用ビルドだけに出す。UXML では隠してあり、製品ビルドでは一切触らない。
#if KOKOSIM_DEBUG || UNITY_EDITOR || DEVELOPMENT_BUILD
            _debugMatch = root.Q<Button>("ts-debug-match");
            if (_debugMatch != null)
            {
                _debugMatch.style.display = DisplayStyle.Flex;
                _debugMatch.clicked -= OnDebugMatch;
                _debugMatch.clicked += OnDebugMatch;
            }
#endif
        }

#if KOKOSIM_DEBUG || UNITY_EDITOR || DEVELOPMENT_BUILD
        private Button _debugMatch;

        private static void OnDebugMatch()
            => ScreenRouter.Instance?.ShowDeferred(Unity.Debugging.DebugMatchSetupController.ScreenName);
#endif

        private void OnDisable()
        {
            if (_newGame != null) _newGame.clicked -= OnNewGame;
            if (_quit != null) _quit.clicked -= OnQuit;
            if (_continueBtn != null) _continueBtn.clicked -= OnContinue;
#if KOKOSIM_DEBUG || UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_debugMatch != null) _debugMatch.clicked -= OnDebugMatch;
#endif
        }

        // クリック配信中に同期 Show すると UITK のイベント木が壊れるため、遷移は必ず遅延切替を使う
        // （ScreenRouter.cs 冒頭コメント参照）。
        // 「はじめから」は県選択画面（日本地図・issue #1410）へ。県を選ぶと新規ゲーム設定画面（issue #585）へ進み、
        // 新設校（校名・配色）をそこで作ってからホームへ渡す＝ここでホームへ直行しない。
        private static void OnNewGame() => ScreenRouter.Instance?.ShowDeferred("PrefectureSelect");

        private static void OnContinue()
        {
            SaveSlots.Entry = SaveSlotsEntry.Title;   // ホームのメニューから開いた直後の使い回しを防ぐ（issue #509）
            ScreenRouter.Instance?.ShowDeferred("SaveSlots");
        }

        private static void OnQuit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
