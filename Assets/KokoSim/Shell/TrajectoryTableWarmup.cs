using System;
using System.Threading.Tasks;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 判定用の弾道特徴量テーブル（<see cref="TrajectoryFeatureTable"/>）を起動直後に背景で作っておく。
    /// 表は「空気密度の節点×回転軸の節点7つ（0〜180°・30°刻み。回転軸はキレで連続に動く, issue #911）」で最大21枚。遅延生成のままだと
    /// 最初の試合（自校戦・裏試合の先読み）がこの構築を丸ごと待つ。天候（気温）の全月ぶんの密度を覆う。
    /// 決定論には関係しない（表は決定論的な純構築・どの順で作っても中身は同じ）。
    /// </summary>
    public static class TrajectoryTableWarmup
    {
        private static Task _task;

        public static void Start()
        {
            if (_task != null) return;
            _task = BackgroundWork.Run(() =>
            {
                try
                {
                    var ctx = new GameContext();
                    // 1年の気温範囲（12か月の表の最低〜最高）を密度へ写す。
                    var min = WeatherModel.AirDensityAt(
                        WeatherModel.HottestTemperatureC, ctx.Aerodynamics.AirDensity, WeatherModel.Base);
                    var max = WeatherModel.AirDensityAt(
                        WeatherModel.CoolestTemperatureC, ctx.Aerodynamics.AirDensity, WeatherModel.Base);
                    TrajectoryFeatureTable.Prewarm(ctx.Aerodynamics, ctx.Mound, min, max,
                        maxParallelism: BackgroundWork.Parallelism(Math.Min(4, Environment.ProcessorCount - 2)));
                }
                catch (Exception e) { UnityEngine.Debug.LogException(e); }
            });
        }
    }
}
