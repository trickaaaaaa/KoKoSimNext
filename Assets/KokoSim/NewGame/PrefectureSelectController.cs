using KokoSim.Engine.Nation;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.NewGame
{
    /// <summary>
    /// 県選択画面（issue #1410）。タイトルの「はじめから」→ここで日本地図から本拠地の県を選ぶ → 新規ゲーム設定画面。
    /// 乗せた県を墨で強調して名前を出し、クリックで決定。北海道と東京は押すとその県だけの拡大図
    /// （北北海道/南北海道・東東京/西東京）に替わり、そこから区画を選ぶ。拡大図から全国地図へ戻れる。
    ///
    /// 決めるのは「県＋（北海道・東京なら）区画」だけ。校の作成と <c>SaveGameIO.StartNewSchool</c> は設定画面の1経路のまま
    /// （<see cref="NewGameSelection"/> で初期値を渡す）。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class PrefectureSelectController : MonoBehaviour
    {
        public const string ScreenName = "PrefectureSelect";

        private VisualElement _root;
        private JapanMapElement _map;
        private Label _title;
        private Button _back;
        private Button _zoomBack;
        private int? _zoomPrefecture;   // 拡大図の県（null＝全国図）

        private void OnEnable()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;
            _title = _root.Q<Label>("pms-title");
            _back = _root.Q<Button>("pms-back");
            _zoomBack = _root.Q<Button>("pms-zoom-back");

            var host = _root.Q<VisualElement>("pms-map");
            host.Clear();
            _map = new JapanMapElement();
            _map.style.flexGrow = 1f;
            host.Add(_map);
            _map.Picked += OnPicked;

            if (_back != null) _back.clicked += OnBack;
            if (_zoomBack != null) _zoomBack.clicked += OnZoomBack;

            ShowNational();
        }

        private void OnDisable()
        {
            if (_map != null) _map.Picked -= OnPicked;
            if (_back != null) _back.clicked -= OnBack;
            if (_zoomBack != null) _zoomBack.clicked -= OnZoomBack;
        }

        private void ShowNational()
        {
            _zoomPrefecture = null;
            var map = JapanMapProvider.Map;
            if (map != null) _map.SetView(map.National);
            if (_title != null) _title.text = "本拠地を選ぶ";
            if (_zoomBack != null) _zoomBack.style.display = DisplayStyle.None;
            if (_back != null) _back.style.display = DisplayStyle.Flex;
        }

        private void ShowZoom(int prefectureId, MapView zoom)
        {
            _zoomPrefecture = prefectureId;
            _map.SetView(zoom);
            if (_title != null) _title.text = PrefectureNames.JapaneseOf(prefectureId);
            if (_zoomBack != null) _zoomBack.style.display = DisplayStyle.Flex;
            if (_back != null) _back.style.display = DisplayStyle.None;
        }

        private void OnPicked(MapFeature f)
        {
            if (_zoomPrefecture == null)
            {
                var zoom = JapanMapProvider.Map?.ZoomOf(f.PrefectureId);
                if (zoom != null) { ShowZoom(f.PrefectureId, zoom); return; }
                Choose(f.PrefectureId, 0);
                return;
            }
            Choose(f.PrefectureId, f.Split ?? 0);
        }

        /// <summary>選んだ県・区画を設定画面へ渡す（<see cref="NewGameSelection"/>）。</summary>
        internal void Choose(int prefectureId, int districtChoice)
        {
            NewGameSelection.Set(prefectureId, districtChoice);
            // クリック配信中に同期 Show すると UITK のイベント木が壊れる（ScreenRouter.cs 冒頭コメント）。
            ScreenRouter.Instance?.ShowDeferred("NewGameSetup");
        }

        private static void OnBack() => ScreenRouter.Instance?.ShowDeferred("TitleScreen");

        private void OnZoomBack() => ShowNational();

#if UNITY_EDITOR
        /// <summary>【スクショ・検証用】指定の県を押したのと同じ処理（座標を介さず、地図の当たり判定の結果を使う）。</summary>
        internal void PickForCapture(int prefectureId) => PickForCapture(prefectureId, null);

        internal void PickForCapture(int prefectureId, int split) => PickForCapture(prefectureId, (int?)split);

        private void PickForCapture(int prefectureId, int? split)
        {
            var view = _map.View;
            foreach (var f in view.Features)
            {
                if (f.PrefectureId != prefectureId) continue;
                if (split.HasValue && f.Split != split) continue;
                OnPicked(f);
                return;
            }
            Debug.LogError("[県選択] 見つからない: " + prefectureId + "/" + split);
        }

        /// <summary>【スクショ・検証用】区画の内側の点に乗せた状態にする（図の座標 → 要素のローカル座標へ直して通常の乗せる処理に流す）。</summary>
        internal void HoverForCapture(int prefectureId) => _map.HoverForCapture(prefectureId, null);

        internal void HoverForCapture(int prefectureId, int split) => _map.HoverForCapture(prefectureId, split);

        /// <summary>【スクショ・検証用】全国図へ戻す。</summary>
        internal void ResetForCapture() => ShowNational();
#endif
    }
}
