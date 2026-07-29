using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 対決ビューのRT表示要素（design-18 §7-1 A案）。カラム実寸（物理ピクセル）からRT解像度と
    /// 整数スケールを決めて額縁なしのほぼ全面表示にする: 論理幅480を基準ドット密度として
    /// 収まる最大の整数倍 s=floor(幅px/480) を選び、RT=実寸/s（端数ぶんの余白は各辺 s-1px 以下）。
    /// フル画面幅では従来の 480×270×4 に収束する。縦横比はRT側がカラムに追従（縦に長い分は
    /// DuelStageRig が描画範囲を上下に広げるだけで構図は切れない）。
    /// 滲みの主因は非整数 scaledPixelsPerPoint（panel-scale-crispness知見）なので、
    /// ポイントでなく物理ピクセル基準で倍率を決める。
    /// </summary>
    public sealed class DuelViewElement : Image
    {
        /// <summary>適応リサイズの相手。MatchLiveController が生成時に結線する。</summary>
        internal DuelStageRig Rig;

        public DuelViewElement()
        {
            scaleMode = ScaleMode.StretchToFill;   // サイズはこちらで整数倍に固定するため引き伸ばしで良い
            RegisterCallback<AttachToPanelEvent>(_ => parent?.RegisterCallback<GeometryChangedEvent>(OnParentGeometry));
            RegisterCallback<DetachFromPanelEvent>(_ => parent?.UnregisterCallback<GeometryChangedEvent>(OnParentGeometry));
        }

        public void Bind(Texture texture)
        {
            image = texture;
            Snap();
        }

        /// <summary>
        /// リグを結線して表示する（適応リサイズ込み）。試合以外の用途（チーム設定のプレビュー等）が
        /// <see cref="Rig"/> と <see cref="Bind"/> を別々に触らずに済むようにまとめてある。
        /// </summary>
        public void BindRig(DuelStageRig rig)
        {
            Rig = rig;
            style.flexGrow = 1f;
            Bind(rig != null ? rig.Target : null);
        }

        private void OnParentGeometry(GeometryChangedEvent _) => Snap();

        internal void Snap()
        {
            if (parent == null || panel == null) return;
            float sppp = panel.scaledPixelsPerPoint;
            if (sppp <= 0f) return;
            float wPx = parent.resolvedStyle.width * sppp;
            float hPx = parent.resolvedStyle.height * sppp;
            if (float.IsNaN(wPx) || wPx <= 0f || float.IsNaN(hPx) || hPx <= 0f) return;

            if (Rig != null && Rig.Available)
            {
                int scale = Mathf.Max(1, Mathf.FloorToInt(wPx / DuelStageRig.BaseWidth));
                int rtW = Mathf.FloorToInt(wPx / scale);
                int rtH = Mathf.FloorToInt(hPx / scale);
                Rig.EnsureSize(rtW, rtH);
                rtW = Rig.Target.width;   // 縦横比クランプ後の実寸で表示サイズを決める
                rtH = Rig.Target.height;
                image = Rig.Target;
                style.width = rtW * scale / sppp;
                style.height = rtH * scale / sppp;
                return;
            }

            // リグ未結線時のフォールバック（固定480×270の整数倍スナップ）
            int s = Mathf.Max(1, Mathf.FloorToInt(
                Mathf.Min(wPx / DuelStageRig.BaseWidth, hPx / DuelStageRig.BaseHeight)));
            style.width = DuelStageRig.BaseWidth * s / sppp;
            style.height = DuelStageRig.BaseHeight * s / sppp;
        }
    }
}
