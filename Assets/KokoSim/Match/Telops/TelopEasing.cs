using System;

namespace KokoSim.Unity.Match.Telops
{
    /// <summary>
    /// 試合テロップ（issue #1179）のイージング。UI ToolkitのUSS transitionには任意の
    /// cubic-bezierが無い（既存の「出現/消失」演出が ease-out-back で代用しているのと同じ事情）ので、
    /// モックのCSS keyframesと同じ値をコードで評価する。
    /// </summary>
    public static class TelopEasing
    {
        public static double Linear(double t) => Math.Clamp(t, 0.0, 1.0);

        /// <summary>イージングを指定しない「フェードイン/アウト」用（ease-in-outの簡易版）。</summary>
        public static double EaseInOut(double t)
        {
            t = Math.Clamp(t, 0.0, 1.0);
            return t * t * (3.0 - 2.0 * t);
        }

        public static double InQuad(double t)
        {
            t = Math.Clamp(t, 0.0, 1.0);
            return t * t;
        }

        /// <summary>cubic-bezier(0.16, 1, 0.3, 1)（プレイボール/ゲームセットの入り）。</summary>
        public static double OutExpo(double t) => CubicBezier(0.16, 1.0, 0.3, 1.0, t);

        /// <summary>cubic-bezier(0.7, 0, 0.84, 0)（プレイボール/ゲームセットの抜け）。</summary>
        public static double InExpo(double t) => CubicBezier(0.7, 0.0, 0.84, 0.0, t);

        /// <summary>cubic-bezier(0.34, 1.56, 0.64, 1)（判定テロップの出現。既存の is-enter と同じ跳ね方）。</summary>
        public static double OutBack(double t) => CubicBezier(0.34, 1.56, 0.64, 1.0, t);

        /// <summary>
        /// CSSのcubic-bezier(x1,y1,x2,y2)と同じ補間（P0=(0,0), P3=(1,1)固定）。
        /// tはx軸の進捗[0,1]。x(u)=tとなるuをNewton-Raphsonで解き、y(u)を返す（ブラウザ実装と同じ近似）。
        /// </summary>
        public static double CubicBezier(double x1, double y1, double x2, double y2, double t)
        {
            if (t <= 0.0) return 0.0;
            if (t >= 1.0) return 1.0;
            var u = t;
            for (var i = 0; i < 8; i++)
            {
                var x = SampleCurve(u, x1, x2) - t;
                var dx = SampleCurveDerivative(u, x1, x2);
                if (Math.Abs(dx) < 1e-6) break;
                u -= x / dx;
                u = Math.Clamp(u, 0.0, 1.0);
            }
            return SampleCurve(u, y1, y2);
        }

        private static double SampleCurve(double u, double a1, double a2)
        {
            var v = 1.0 - u;
            return 3 * v * v * u * a1 + 3 * v * u * u * a2 + u * u * u;
        }

        private static double SampleCurveDerivative(double u, double a1, double a2)
        {
            var v = 1.0 - u;
            return 3 * v * v * a1 + 6 * v * u * (a2 - a1) + 3 * u * u * (1 - a2);
        }

        /// <summary>
        /// 区間[t0,t1]内でのeased補間値をfrom→toで返す（t&lt;t0はfrom、t&gt;t1はtoにクランプ）。
        /// 判定/プレイボール各タイムラインの「0.00→0.65でxを+1800から0へ」のような表をそのまま書くための小道具。
        /// </summary>
        public static double Segment(double t, double t0, double t1, double from, double to, Func<double, double> ease)
        {
            if (t1 <= t0) return to;
            var frac = Math.Clamp((t - t0) / (t1 - t0), 0.0, 1.0);
            return from + (to - from) * ease(frac);
        }
    }
}
