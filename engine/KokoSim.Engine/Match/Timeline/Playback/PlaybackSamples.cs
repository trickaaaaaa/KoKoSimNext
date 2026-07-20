using System.Collections.Generic;
using KokoSim.Engine.Match.Field;

namespace KokoSim.Engine.Match.Timeline.Playback;

/// <summary>
/// docs/design/mock-match-2d-view.html の PLAYS 配列（7プレー）の忠実移植。
/// 式・定数・データを一切変えずに写経（設計者Claude指示 Step 1）。
/// エンジン側ゴールデンテストと Unity 側2Dビューの両方がこの単一ソースを再生する。
/// </summary>
public static class PlaybackSamples
{
    // 固定ジオメトリ（mock: HOME/FIRST/SECOND/THIRD）。塁座標は共通ソース FieldDiagramGeometry を参照。
    private static readonly PlaybackVec Home = Vec2Of(FieldDiagramGeometry.Home);
    private static readonly PlaybackVec First = Vec2Of(FieldDiagramGeometry.First);
    private static readonly PlaybackVec Second = Vec2Of(FieldDiagramGeometry.Second);
    private static readonly PlaybackVec Third = Vec2Of(FieldDiagramGeometry.Third);
    private static PlaybackVec Vec2Of((double X, double Y) t) => new(t.X, t.Y);

    // ── 短縮ビルダ（mock: pitchSeg/arcSeg/rollSeg/throwSeg/move/run のエイリアス） ──
    private static PitchSegment Pitch(double t0) => new(t0);
    private static ArcSegment Arc(double t0, double x0, double y0, double h0, double vx, double vy, double vz, double? tEnd = null)
        => new(t0, x0, y0, h0, vx, vy, vz, tEnd);
    private static RollSegment Roll(double t0, double x0, double y0, double dx, double dy, double v0, double a, double dur, double hop)
        => new(t0, x0, y0, dx, dy, v0, a, dur, hop);
    private static ThrowSegment Throw(double t0, PlaybackVec from, PlaybackVec to, double spd)
        => new(t0, from, to, spd);
    private static PlaybackVec V(double x, double y) => new(x, y);
    private static PlaybackMove Move(double t0, double t1, PlaybackVec to) => new(t0, t1, to);
    private static PlaybackRun Run(double t0, double t1, PlaybackVec from, PlaybackVec to) => new(t0, t1, from, to);
    private static PlaybackCaption Cap(double t, string text) => new(t, text);

    private static Dictionary<FieldPosition, IReadOnlyList<PlaybackMove>> Moves(
        params (FieldPosition Pos, PlaybackMove[] Segs)[] entries)
    {
        var d = new Dictionary<FieldPosition, IReadOnlyList<PlaybackMove>>();
        foreach (var (pos, segs) in entries) d[pos] = segs;
        return d;
    }

    // 位置エイリアス（mock の 投捕一二三遊左中右）。
    private const FieldPosition P = FieldPosition.Pitcher;
    private const FieldPosition C = FieldPosition.Catcher;
    private const FieldPosition B1 = FieldPosition.FirstBase;
    private const FieldPosition B2 = FieldPosition.SecondBase;
    private const FieldPosition B3 = FieldPosition.ThirdBase;
    private const FieldPosition SS = FieldPosition.Shortstop;
    private const FieldPosition LF = FieldPosition.LeftField;
    private const FieldPosition CF = FieldPosition.CenterField;
    private const FieldPosition RF = FieldPosition.RightField;

    /// <summary>7プレー（mock: PLAYS）。</summary>
    public static readonly IReadOnlyList<PlaybackPlay> All = new[]
    {
        // ① レフト前ヒット
        new PlaybackPlay
        {
            Name = "① レフト前ヒット", Dur = 6.3, Result = "H  レフト前", ResAt = 3.90,
            Ball = new PlaybackBallSegment[]
            {
                Pitch(0.40),
                Arc(0.85, 0, 0.6, 1.0, -14.75, 31.63, 8.7),
                Roll(2.73, -27.7, 60.1, -0.4226, 0.9063, 15, -6, 1.12, 0.5),
                Throw(4.25, V(-33.2, 71.9), V(0.5, 38.8), 30),
            },
            Moves = Moves(
                (LF, new[] { Move(1.20, 3.85, V(-33.2, 71.9)) }),
                (B2, new[] { Move(1.50, 3.50, V(0.5, 38.8)) })),
            Runners = new[]
            {
                new PlaybackRunner { Segs = new[] { Run(0.95, 5.15, Home, First) } },
            },
            Caps = new[]
            {
                Cap(0.05, "ピッチャー、振りかぶって…"), Cap(0.86, "カキーン！ 鋭い打球がレフトへ"),
                Cap(2.75, "ワンバウンド、レフトが前へチャージ"), Cap(3.90, "レフト前ヒット！ 打者は一塁へ"),
            },
        },

        // ② センターフライ
        new PlaybackPlay
        {
            Name = "② センターフライ", Dur = 5.6, Result = "F8  中飛", ResAt = 4.47,
            Ball = new PlaybackBallSegment[]
            {
                Pitch(0.40),
                Arc(0.85, 0, 0.6, 1.0, 1.31, 25.07, 18.2, 3.60),
            },
            Moves = Moves(
                (CF, new[] { Move(1.30, 4.35, V(4.7, 91.0)) })),
            Runners = new[]
            {
                new PlaybackRunner { Segs = new[] { Run(0.95, 4.45, Home, First) }, HideAt = 4.75 },
            },
            Caps = new[]
            {
                Cap(0.05, "ピッチャー、投げた"), Cap(0.86, "打ち上げた！ センターへ高いフライ"),
                Cap(2.60, "センター、落下点へ入る"), Cap(4.47, "キャッチ。バッターアウト！"),
            },
        },

        // ③ ショートゴロ 6-3
        new PlaybackPlay
        {
            Name = "③ ショートゴロ 6-3", Dur = 5.4, Result = "6-3  遊ゴロ", ResAt = 3.55,
            Ball = new PlaybackBallSegment[]
            {
                Pitch(0.40),
                Roll(0.85, 0, 0.6, -0.2079, 0.9781, 30, -5, 1.33, 0.6),
                Throw(2.55, V(-7.5, 35.8), V(19.6, 19.6), 32),
            },
            Moves = Moves(
                (SS, new[] { Move(1.05, 2.18, V(-7.5, 35.8)) }),
                (B1, new[] { Move(1.20, 2.60, V(19.6, 19.6)) })),
            Runners = new[]
            {
                new PlaybackRunner { Segs = new[] { Run(0.95, 5.10, Home, First) }, HideAt = 4.00 },
            },
            Caps = new[]
            {
                Cap(0.05, "ピッチャー、投げた"), Cap(0.86, "叩きつけた！ ショートの左へ"),
                Cap(2.20, "ショート、回り込んで捕った"), Cap(2.58, "一塁へ送球…"), Cap(3.55, "アウト！ 6-3"),
            },
        },

        // ④ ショートがファンブル！
        new PlaybackPlay
        {
            Name = "④ ショートがファンブル！", Dur = 5.9, Result = "E6  失策", ResAt = 4.63,
            Ball = new PlaybackBallSegment[]
            {
                Pitch(0.40),
                Roll(0.85, 0, 0.6, -0.2079, 0.9781, 30, -5, 1.33, 0.6),
                Arc(2.18, -7.5, 35.8, 0.6, 1.2, -1.5, 2.4),
                Throw(3.60, V(-6.7, 34.8), V(19.6, 19.6), 30),
            },
            Moves = Moves(
                (SS, new[] { Move(1.05, 2.18, V(-7.5, 35.8)), Move(2.30, 3.40, V(-6.7, 34.8)) }),
                (B1, new[] { Move(1.20, 2.60, V(19.6, 19.6)) })),
            Runners = new[]
            {
                new PlaybackRunner { Segs = new[] { Run(0.95, 4.55, Home, First) } },
            },
            Caps = new[]
            {
                Cap(0.05, "ピッチャー、投げた"), Cap(0.86, "叩きつけた！ ショート正面"),
                Cap(2.20, "捕り損ねた！ お手玉している…"), Cap(3.62, "拾って一塁へ、間に合うか——"),
                Cap(4.63, "セーフ！ 記録はエラー（E6）"),
            },
        },

        // ⑤ 6-4-3 ダブルプレー
        new PlaybackPlay
        {
            Name = "⑤ 6-4-3 ダブルプレー", Dur = 5.6, Result = "6-4-3  併殺", ResAt = 4.16,
            Ball = new PlaybackBallSegment[]
            {
                Pitch(0.40),
                Roll(0.85, 0, 0.6, -0.292, 0.956, 32, -5, 1.29, 0.55),
                Throw(2.45, V(-10.8, 36.1), V(0.4, 38.8), 25),
                Throw(3.26, V(0.4, 38.8), V(19.6, 19.6), 31),
            },
            Moves = Moves(
                (SS, new[] { Move(1.00, 2.14, V(-10.8, 36.1)) }),
                (B2, new[] { Move(1.00, 2.80, V(0.4, 38.8)) }),
                (B1, new[] { Move(1.20, 2.60, V(19.6, 19.6)) }),
                (CF, new[] { Move(1.30, 3.40, V(2, 62)) }),
                (RF, new[] { Move(1.50, 4.10, V(29, 13)) })),
            Runners = new[]
            {
                new PlaybackRunner { Label = "走", Segs = new[] { Run(0.0, 0.85, First, First), Run(0.85, 4.35, First, Second) }, HideAt = 3.25 },
                new PlaybackRunner { Label = "打", Segs = new[] { Run(0.95, 5.15, Home, First) }, HideAt = 4.55 },
            },
            Caps = new[]
            {
                Cap(0.05, "一塁に走者。ピッチャー投げた"), Cap(0.86, "ゴロだ！ ショート正面——併殺コースか"),
                Cap(2.16, "ショート捕って二塁へトス"), Cap(2.93, "一つアウト！ 二塁手が反転、一塁へ転送——"),
                Cap(4.16, "ダブルプレー成立！！ 6-4-3"),
            },
        },

        // ⑥ 右中間破る長打→中継バックホーム
        new PlaybackPlay
        {
            Name = "⑥ 右中間破る長打→中継バックホーム", Dur = 10.8, Result = "9-4-2  本塁憤死", ResAt = 9.30,
            Ball = new PlaybackBallSegment[]
            {
                Pitch(0.40),
                Arc(0.85, 0, 0.6, 1.0, 12.31, 33.84, 8.0),
                Roll(2.60, 21.6, 59.9, 0.342, 0.940, 15, -4, 2.90, 0.4),
                Throw(6.10, V(30.7, 85.0), V(20, 55), 31),
                Throw(7.50, V(20, 55), V(0, 1.0), 33),
            },
            Moves = Moves(
                (RF, new[] { Move(1.20, 5.50, V(30.7, 85.0)) }),
                (CF, new[] { Move(1.30, 5.90, V(25.5, 90)) }),
                (B2, new[] { Move(1.30, 5.00, V(20, 55)) }),
                (SS, new[] { Move(1.40, 3.20, V(0.4, 38.8)) }),
                (B1, new[] { Move(1.30, 2.80, V(19.6, 19.6)) }),
                (P, new[] { Move(6.30, 8.30, V(-2.5, -7.5)) })),
            Runners = new[]
            {
                new PlaybackRunner
                {
                    Label = "走",
                    Segs = new[]
                    {
                        Run(0.0, 0.85, First, First), Run(0.85, 3.85, First, Second),
                        Run(3.85, 6.85, Second, Third), Run(6.85, 9.85, Third, Home),
                    },
                    HideAt = 9.75,
                },
                new PlaybackRunner
                {
                    Label = "打",
                    Segs = new[] { Run(0.95, 4.55, Home, First), Run(4.55, 7.90, First, Second) },
                },
            },
            Caps = new[]
            {
                Cap(0.05, "一塁に走者"), Cap(0.86, "痛烈！ 右中間を破った！"),
                Cap(2.65, "一塁走者は二塁を蹴った。センターがカバーに走る"),
                Cap(5.55, "ライトが追いつく。セカンドが中継に出た"),
                Cap(7.52, "三塁を回った！ バックホーム——！！"),
                Cap(9.30, "本塁タッチアウト！ 中継プレーが刺した！"),
            },
        },

        // ⑦ 一・二塁から送りバント
        new PlaybackPlay
        {
            Name = "⑦ 一・二塁から送りバント", Dur = 5.4, Result = "犠打  成功", ResAt = 3.95,
            Ball = new PlaybackBallSegment[]
            {
                Pitch(0.40),
                Roll(0.85, 0, 0.6, 0.342, 0.940, 7, -2.5, 1.60, 0.15),
                Throw(2.85, V(2.7, 8.1), V(19.6, 19.6), 28),
            },
            Moves = Moves(
                (B1, new[] { Move(1.00, 2.45, V(2.7, 8.1)) }),
                (P, new[] { Move(1.00, 2.20, V(1.8, 11.5)) }),
                (C, new[] { Move(1.10, 2.00, V(0.6, 1.8)) }),
                (B2, new[] { Move(1.00, 2.90, V(19.6, 19.6)) }),
                (SS, new[] { Move(1.00, 2.80, V(0.2, 38.8)) }),
                (B3, new[] { Move(1.00, 1.80, V(-19.4, 19.4)) }),
                (RF, new[] { Move(1.30, 3.60, V(29.5, 14)) })),
            Runners = new[]
            {
                new PlaybackRunner { Label = "走", Segs = new[] { Run(0.0, 0.90, Second, Second), Run(0.90, 4.40, Second, Third) } },
                new PlaybackRunner { Label = "走", Segs = new[] { Run(0.0, 0.90, First, First), Run(0.90, 4.40, First, Second) } },
                new PlaybackRunner { Label = "打", Segs = new[] { Run(1.05, 5.25, Home, First) }, HideAt = 3.95 },
            },
            Caps = new[]
            {
                Cap(0.05, "一・二塁。バッターはバントの構え"), Cap(0.86, "転がした——一塁側！"),
                Cap(1.60, "ファーストが猛チャージ、セカンドが一塁ベースカバーへ"),
                Cap(2.88, "一塁送球、バッターアウト"), Cap(3.95, "送りバント成功。走者は二・三塁に進んだ"),
            },
        },
    };
}
