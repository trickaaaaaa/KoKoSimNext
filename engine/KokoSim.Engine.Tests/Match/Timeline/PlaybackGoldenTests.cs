using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Timeline.Playback;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 試合2D俯瞰ビュー再生モデル（PlaybackModel/PlaybackSamples）が、
/// docs/design/mock-match-2d-view.html の JS 補間関数と固定時刻で同値になることを保証する
/// ゴールデンテスト（設計者Claude指示 Step 1「JSとC#で同値＝移植完了の定義」）。
///
/// 期待値は node で JS の ballAt/fielderAt/runnerAt を原文ママ実行して採取した固定スナップショット。
/// 各プレー3時点以上・ボール(x,y,h)＋動いている代表野手＋全走者を照合する。許容誤差 1e-4。
/// </summary>
public sealed class PlaybackGoldenTests
{
    private const double Tol = 1e-4;

    // ── 照合ヘルパ（golden 生成器と同じ引数順） ──
    private static void Ball(int pi, double t, double x, double y, double h)
    {
        var b = PlaybackEvaluator.BallAt(PlaybackSamples.All[pi], t);
        Assert.True(b.HasValue, $"play{pi} t={t}: ボールが null");
        Assert.Equal(x, b.Value.X, Tol);
        Assert.Equal(y, b.Value.Y, Tol);
        Assert.Equal(h, b.Value.H, Tol);
    }

    private static void BallNull(int pi, double t)
        => Assert.False(PlaybackEvaluator.BallAt(PlaybackSamples.All[pi], t).HasValue, $"play{pi} t={t}: ボールは null のはず");

    private static void Fld(int pi, double t, FieldPosition key, double x, double y, bool moving)
    {
        var (pos, mv) = PlaybackEvaluator.FielderAt(PlaybackSamples.All[pi], key, t);
        Assert.Equal(x, pos.X, Tol);
        Assert.Equal(y, pos.Y, Tol);
        Assert.Equal(moving, mv);
    }

    private static void Run(int pi, double t, int runnerIndex, double x, double y)
    {
        var pos = PlaybackEvaluator.RunnerAt(PlaybackSamples.All[pi].Runners[runnerIndex], t);
        Assert.True(pos.HasValue, $"play{pi} t={t} runner{runnerIndex}: 走者が null");
        Assert.Equal(x, pos.Value.X, Tol);
        Assert.Equal(y, pos.Value.Y, Tol);
    }

    [Fact]
    public void Golden_MatchesMockInterpolation()
    {
        // 0: ① レフト前ヒット  (H  レフト前)
        Ball(0, 1.4, -8.1125, 17.9965, 4.30275);
        Fld(0, 1.4, FieldPosition.LeftField, -35.954561, 75.933465, moving: true);
        Run(0, 1.4, 0, 2.078571, 2.078571);
        Ball(0, 3.8, -33.031226, 71.533246, 0.015247);
        Fld(0, 3.8, FieldPosition.LeftField, -33.202953, 71.904324, moving: true);
        Run(0, 3.8, 0, 13.164286, 13.164286);
        Ball(0, 5.4, -8.58669, 47.72491, 5.834399);
        Run(0, 5.4, 0, 19.4, 19.4);

        // 1: ② センターフライ  (F8  中飛)
        Ball(1, 1.4, 0.7205, 14.3885, 9.52775);
        Fld(1, 1.4, FieldPosition.CenterField, 0.014826, 91.996846, moving: true);
        Run(1, 1.4, 0, 2.494286, 2.494286);
        Ball(1, 3, 2.8165, 54.5005, 17.47975);
        Fld(1, 3, FieldPosition.CenterField, 2.752733, 91.414312, moving: true);
        Run(1, 3, 0, 11.362857, 11.362857);
        Ball(1, 4.6, 4.716, 90.852, 3.016);
        Run(1, 4.6, 0, 19.4, 19.4);

        // 2: ③ ショートゴロ 6-3  (6-3  遊ゴロ)
        Ball(2, 1.4, -3.273126, 15.998962, 0.043823);
        Fld(2, 1.4, FieldPosition.FirstBase, 21.867055, 25.645481, moving: true);
        Run(2, 1.4, 0, 2.103614, 2.103614);
        Ball(2, 3, 4.859955, 28.411392, 5.181044);
        Run(2, 3, 0, 9.583133, 9.583133);
        Ball(2, 4.6, 19.6, 19.6, 1.7);

        // 3: ④ ショートがファンブル！  (E6  失策)
        Ball(3, 1.4, -3.273126, 15.998962, 0.043823);
        Fld(3, 1.4, FieldPosition.FirstBase, 21.867055, 25.645481, moving: true);
        Run(3, 1.4, 0, 2.425, 2.425);
        Ball(3, 3, -6.693589, 34.791986, 0);
        Fld(3, 3, FieldPosition.Shortstop, -6.940421, 35.100526, moving: true);
        Run(3, 3, 0, 11.047222, 11.047222);
        Ball(3, 4.6, 19.274051, 19.788381, 1.865247);
        Run(3, 4.6, 0, 19.4, 19.4);

        // 4: ⑤ 6-4-3 ダブルプレー  (6-4-3  併殺)
        Ball(4, 1.4, -4.918375, 16.702625, 0.040171);
        Fld(4, 1.4, FieldPosition.FirstBase, 21.867055, 25.645481, moving: true);
        Run(4, 1.4, 0, 16.351429, 22.448571);
        Run(4, 1.4, 1, 2.078571, 2.078571);
        Ball(4, 3, 0.4, 38.8, 1.7);
        Fld(4, 3, FieldPosition.CenterField, 1.809956, 64.850664, moving: true);
        Run(4, 3, 0, 7.482857, 31.317143);
        Run(4, 3, 1, 9.469048, 9.469048);
        Ball(4, 4.6, 19.6, 19.6, 1.7);

        // 5: ⑥ 右中間破る長打→中継バックホーム  (9-4-2  本塁憤死)
        Ball(5, 1.4, 6.7705, 19.212, 3.91775);
        Fld(5, 1.4, FieldPosition.FirstBase, 21.969422, 25.918459, moving: true);
        Run(5, 1.4, 0, 15.843333, 22.956667);
        Run(5, 1.4, 1, 2.425, 2.425);
        Ball(5, 5.4, 30.60144, 84.6408, 0.007044);
        Fld(5, 5.4, FieldPosition.CenterField, 24.661667, 90.065752, moving: true);
        Run(5, 5.4, 0, -10.023333, 28.776667);
        Run(5, 5.4, 1, 14.477612, 24.322388);
        Ball(5, 9.4, 0, 1, 1.7);
        Run(5, 9.4, 0, -2.91, 2.91);
        Run(5, 9.4, 1, 0, 38.8);

        // 6: ⑦ 一・二塁から送りバント  (犠打  成功)
        Ball(6, 1.4, 1.187381, 3.863562, 0.010956);
        Fld(6, 1.4, FieldPosition.Pitcher, 0.466667, 15.87037, moving: true);
        Run(6, 1.4, 0, -2.771429, 36.028571);
        Run(6, 1.4, 1, 16.628571, 22.171429);
        Run(6, 1.4, 2, 1.616667, 1.616667);
        Ball(6, 3, 6.172327, 10.462826, 3.183135);
        Fld(6, 3, FieldPosition.RightField, 30.596244, 24.456481, moving: true);
        Run(6, 3, 0, -11.64, 27.16);
        Run(6, 3, 1, 7.76, 31.04);
        Run(6, 3, 2, 9.007143, 9.007143);
        Ball(6, 4.6, 19.6, 19.6, 1.7);
        Run(6, 4.6, 0, -19.4, 19.4);
        Run(6, 4.6, 1, 0, 38.8);
    }

    [Fact]
    public void Caption_LatestThresholdWins()
    {
        var p = PlaybackSamples.All[0];
        Assert.Equal("", PlaybackEvaluator.CaptionAt(p, 0.0));
        Assert.Equal("ピッチャー、振りかぶって…", PlaybackEvaluator.CaptionAt(p, 0.5));
        Assert.Equal("カキーン！ 鋭い打球がレフトへ", PlaybackEvaluator.CaptionAt(p, 2.0));
        Assert.Equal("レフト前ヒット！ 打者は一塁へ", PlaybackEvaluator.CaptionAt(p, 6.0));
    }

    [Fact]
    public void Runner_HidesAfterHideAt()
    {
        // ② センターフライ: 打者走者は hideAt=4.75 で消える。
        var runner = PlaybackSamples.All[1].Runners[0];
        Assert.NotNull(PlaybackEvaluator.RunnerAt(runner, 4.7));
        Assert.Null(PlaybackEvaluator.RunnerAt(runner, 4.8));
    }

    [Fact]
    public void Ball_HeldBeforePitchAndBetweenSegments()
    {
        var p = PlaybackSamples.All[0];
        // 投球前（t < 0.40）はボール null（mock: ballAt が held=null を返す）。
        Assert.Null(PlaybackEvaluator.BallAt(p, 0.2));
        // 全プレー終了後もボールは最後の保持位置を返す（held）。
        Assert.NotNull(PlaybackEvaluator.BallAt(p, 6.2));
    }
}
