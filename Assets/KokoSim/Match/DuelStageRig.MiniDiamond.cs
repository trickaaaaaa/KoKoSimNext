using System;
using System.Collections.Generic;
using System.Text;
using KokoSim.Engine.Match.Timeline.Playback;
using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 守備フォーカス中の小ダイヤモンド（issue #595）へ渡す走者位置。
    ///
    /// <para>守備カメラは球を追ってパン・ズームするので、枠外へ出た走者は<b>原理的に映らない</b>
    /// ＝「三塁で止まったのか本塁を狙ったのか」がカメラ側では解けない。画面固定のオーバーレイで補うために、
    /// ここは<b>今この瞬間の走者の居場所[m]</b>だけを返す。図法（箱への写像）は engine の
    /// <c>MiniDiamondProjection</c>、描画は <see cref="MiniDiamondElement"/> の責務。</para>
    ///
    /// <para><b>3Dの走者と同じ時刻・同じ単一ソース</b>: 位置は <c>PlaybackEvaluator.RunnerAt</c> を
    /// 現在の再生時刻 <c>_fieldT</c> で引く。3D の <c>StepRunnerMotion</c> が走者を置くのに使っているのと
    /// <b>同じ関数・同じ時刻</b>なので、補間をUI側に書き起こさない＝ミニダイヤと3Dの走者がずれない。</para>
    ///
    /// <para>⚠ <b>消え方だけは3Dと違う</b>（2026-08-12 監督指示）。<c>HideAt</c>（＝憤死/封殺の瞬間）を
    /// <b>効かせる</b>＝アウトになった走者はその時点でドットが消える。3Dで <c>respectHideAt: false</c> なのは
    /// 「選手が空中で消える」のを避けるためで、丸トークンにその制約は無い。むしろ残すと
    /// <b>まだ生きている走者と区別が付かない</b>＝この図の目的（誰がどこまで進んだか）を潰す。
    /// これは2D俯瞰（<c>Match2DPlaybackElement</c>）が最初から採っている扱いと同じ。</para>
    /// </summary>
    public sealed partial class DuelStageRig
    {
        /// <summary>走者ドット1つ。球場座標[m]（本塁=原点・+Y=二塁方向・+X=一塁側）のまま返す。</summary>
        public readonly struct RunnerDot
        {
            /// <summary>球場座標[m]。</summary>
            public readonly Vector2 FieldM;

            /// <summary>打者走者か（"打"）。図では区別しないが、実測ログの読み分けに使う。</summary>
            public readonly bool IsBatter;

            public RunnerDot(Vector2 fieldM, bool isBatter)
            {
                FieldM = fieldM;
                IsBatter = isBatter;
            }
        }

        /// <summary>
        /// 現在の再生時刻の走者位置[m]。守備フォーカス中でなければ空。
        /// <b>3Dで実際に立っている走者</b>のうち、<b>まだアウトになっていない</b>ものだけを返す
        /// （役者枠 <c>MaxRunners</c> を超える走者は3Dにも居ない）。
        /// </summary>
        public IReadOnlyList<RunnerDot> CurrentRunnerDots()
        {
            if (_fieldPlay?.Runners == null) return Array.Empty<RunnerDot>();
            var n = Math.Min(_runnerCast.Count, _fieldPlay.Runners.Count);
            if (n <= 0) return Array.Empty<RunnerDot>();

            var dots = new List<RunnerDot>(n);
            for (var i = 0; i < n; i++)
            {
                var a = _runnerCast[i];
                if (a?.Go == null || !a.Enabled || !a.Go.activeInHierarchy) continue;   // 3D側と枠を揃える
                var r = _fieldPlay.Runners[i];
                if (!MiniDiamondXZ(_fieldPlay, r, _fieldT, out var xz)) continue;
                dots.Add(new RunnerDot(xz, r.Label == "打"));
            }
            return dots;
        }

        /// <summary>
        /// ミニダイヤに描く走者の位置[m]。<c>RunnerXZ</c>（3D用）と<b>消え方だけ</b>が違う:
        /// アウトになった走者は <see cref="PlaybackEvaluator.IsRunnerOut"/> で false を返して<b>消える</b>
        /// （塁で決着したアウトも飛球の捕球アウトも同じ口で拾う）。
        /// レッグが始まる前は engine が null を返すが、そこは「まだ動いていないだけ」なので
        /// 最初のレッグの起点＝居る塁に置く（3D側と同じ扱い）。
        /// </summary>
        private static bool MiniDiamondXZ(PlaybackPlay play, PlaybackRunner r, float t, out Vector2 xz)
        {
            xz = Vector2.zero;
            if (r == null || PlaybackEvaluator.IsRunnerOut(play, r, t)) return false;
            var p = PlaybackEvaluator.RunnerAt(r, t, respectHideAt: false);
            if (p.HasValue) { xz = new Vector2((float)p.Value.X, (float)p.Value.Y); return true; }
            if (r.Segs == null || r.Segs.Count == 0) return false;
            var f = r.Segs[0].From;
            xz = new Vector2((float)f.X, (float)f.Y);
            return true;
        }

        /// <summary>
        /// 【撮影ハーネス用】ミニダイヤの走者ドットと3Dの走者の突き合わせ（issue #595 の完了条件）。
        /// <c>差</c> は「ドットの言う位置 → 3Dの役者が実際に立っている位置」の距離[m]で、
        /// 同じ補間を同じ <c>_fieldT</c> で引いている限り <b>0.000 のはず</b>
        /// （3Dは <c>StepRunnerMotion</c> が同じ値で <c>PlaceRunner</c> する）。
        /// ここが 0 でない＝どちらかが別の時刻・別の補間を見ている合図。
        /// アウトになってドットを消した走者は <c>差</c> の代わりに「消えた」と出す
        /// （3Dの役者は残り続けるので、ここで突き合わせるとアウト後は必ず不一致になる）。
        /// </summary>
        public string MiniDiamondDiagForCapture()
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "t={0:F2}s\tplay={1}", _fieldT, _fieldPlay?.Name ?? "(none)"));
            sb.AppendLine("走者\tドット[m]\t3Dの役者[m]\t差[m]");

            if (_fieldPlay?.Runners == null) return sb.ToString();
            var n = Math.Min(_runnerCast.Count, _fieldPlay.Runners.Count);
            for (var i = 0; i < n; i++)
            {
                var a = _runnerCast[i];
                if (a?.Go == null || !a.Enabled || !a.Go.activeInHierarchy) continue;
                var r = _fieldPlay.Runners[i];
                var lp = a.Go.transform.localPosition;
                if (!MiniDiamondXZ(_fieldPlay, r, _fieldT, out var xz))
                {
                    sb.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "{0}\t—\t({1:F2}, {2:F2})\t消えた", r.Label, lp.x, lp.z));
                    continue;
                }
                var d = new Vector2(lp.x - xz.x, lp.z - xz.y).magnitude;
                sb.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0}\t({1:F2}, {2:F2})\t({3:F2}, {4:F2})\t{5:F3}",
                    r.Label, xz.x, xz.y, lp.x, lp.z, d));
            }
            return sb.ToString();
        }
    }
}
