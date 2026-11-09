using System;
using System.Linq;
using System.Threading.Tasks;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 判定用の弾道特徴量テーブル（<see cref="TrajectoryFeatureTable"/>）を起動直後に背景で作っておく。
    /// 表は「空気密度の節点×回転軸9種」で最大27枚、1枚 Mono で約1.9秒かかる。遅延生成のままだと
    /// 最初の試合（自校戦・裏試合の先読み）がこの構築を丸ごと待つ。天候（気温）の全季節ぶんの密度を覆う。
    /// 決定論には関係しない（表は決定論的な純構築・どの順で作っても中身は同じ）。
    /// </summary>
    public static class TrajectoryTableWarmup
    {
        private static Task _task;

        public static void Start()
        {
            if (_task != null) return;
            _task = Task.Run(() =>
            {
                try
                {
                    var ctx = new GameContext();
                    var tilts = ctx.Pitching.PitchAxis.Select(a => a.TiltDeg).Append(0.0);
                    // 全季節の気温範囲（春の下限〜夏の上限）を密度へ写す。
                    var min = WeatherModel.AirDensityAt(
                        WeatherModel.Summer.MaxTemperatureC, ctx.Aerodynamics.AirDensity, WeatherModel.Summer);
                    var max = WeatherModel.AirDensityAt(
                        WeatherModel.Spring.MinTemperatureC, ctx.Aerodynamics.AirDensity, WeatherModel.Spring);
                    TrajectoryFeatureTable.Prewarm(ctx.Aerodynamics, ctx.Mound, tilts, min, max,
                        maxParallelism: Math.Max(1, Math.Min(4, Environment.ProcessorCount - 2)));
                }
                catch (Exception e) { UnityEngine.Debug.LogException(e); }
            });
        }
    }
}
