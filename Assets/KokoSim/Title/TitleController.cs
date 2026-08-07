using KokoSim.Unity.Shell;   // ScreenRouter
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Title
{
    /// <summary>
    /// タイトル画面（issue #482・B案 球場バック型）。起動直後の入口。
    /// 「はじめから」→ ホームへ（部員ロスター等は各シェルサービスの遅延初期化に任せる。新規状態を
    /// ここで作らない）。「つづきから」はセーブが1件も無ければ非活性、あればセーブスロット選択画面
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
            if (_continueBtn != null) _continueBtn.clicked += OnContinue;

            // セーブが1件も無ければ押せない見た目にする（issue #508）。
            _continueBtn?.SetEnabled(SaveSlots.AnySaveExists());
        }

        private void OnDisable()
        {
            if (_newGame != null) _newGame.clicked -= OnNewGame;
            if (_quit != null) _quit.clicked -= OnQuit;
            if (_continueBtn != null) _continueBtn.clicked -= OnContinue;
        }

        // クリック配信中に同期 Show すると UITK のイベント木が壊れるため、遷移は必ず遅延切替を使う
        // （ScreenRouter.cs 冒頭コメント参照）。
        private static void OnNewGame() => ScreenRouter.Instance?.ShowDeferred("HomeDashboard");

        private static void OnContinue() => ScreenRouter.Instance?.ShowDeferred("SaveSlots");

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
