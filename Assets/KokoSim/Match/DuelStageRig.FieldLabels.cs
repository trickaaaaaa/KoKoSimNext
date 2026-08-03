using System;
using System.Collections.Generic;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Timeline.Playback;
using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 打球処理中（守備の引き）の頭上フルネームラベル向けデータ（issue #393）。画面内の野手・走者へ、
    /// 画面空間の固定サイズで氏名を出すため、ここは「誰が・どこに（ビューポート正規化座標）・
    /// どれだけ優先か」だけを計算する。フォント・地色・重なりの実レイアウトは呼び出し側
    /// （MatchLiveController、UI Toolkit）の責務＝RTへ焼くとドット荒れするため（design-18 §4.5）。
    /// </summary>
    public sealed partial class DuelStageRig
    {
        private DuelFieldIdentity _fieldIdentity = new(new Dictionary<FieldPosition, DuelPlayerIdentity>());

        /// <summary>
        /// 守備側の守備位置→選手（issue #393）。打球プレー開始時に呼び出し側が固定する
        /// （試合中に交代で変わるため、BeginFieldPlay のたびに渡し直す前提）。
        /// </summary>
        public void SetFieldIdentity(DuelFieldIdentity identity)
        {
            if (identity != null) _fieldIdentity = identity;
        }

        /// <summary>頭上ラベル1件。UI Toolkit 座標への変換・見た目（書体/地色/縁取り）は呼び出し側の責務。</summary>
        public readonly struct FieldLabel
        {
            public readonly string Name;
            public readonly bool IsRunner;
            /// <summary>ビューポート正規化座標（x:0=左..1=右, y:0=下..1=上）。</summary>
            public readonly Vector2 Viewport01;

            public FieldLabel(string name, bool isRunner, Vector2 viewport01)
            {
                Name = name;
                IsRunner = isRunner;
                Viewport01 = viewport01;
            }
        }

        /// <summary>
        /// 現在画面内に映っている野手・走者のラベル候補を、優先順位の高い順に最大 <paramref name="maxCount"/> 件返す
        /// （issue #393・重なりの間引き案＝打球/送球経路の関与者を優先）。走者は常に優先（②）し、
        /// 守備側だけを間引く。打球プレー中でなければ空。カメラの後ろに回った役者は含めない。
        /// </summary>
        public IReadOnlyList<FieldLabel> CurrentFieldLabels(int maxCount)
        {
            if (_fieldPlay == null || !Available || _camera == null) return Array.Empty<FieldLabel>();
            var t = _fieldT;
            var candidates = new List<(string Name, bool IsRunner, Vector3 World, float Priority)>();

            foreach (var pos in AllFieldPositions)
            {
                var a = FielderActor(pos);
                if (a?.Go == null || !a.Enabled || !a.Go.activeInHierarchy) continue;
                if (!_fieldIdentity.Defense.TryGetValue(pos, out var id)) continue;
                var world = a.Go.transform.position + Vector3.up * TargetHeight;
                candidates.Add((id.Name, false, world, FielderLabelPriority(pos, t)));
            }
            for (var i = 0; i < _runnerCast.Count && i < _fieldPlay.Runners.Count; i++)
            {
                var a = _runnerCast[i];
                if (a?.Go == null || !a.Enabled || !a.Go.activeInHierarchy) continue;
                var runner = _fieldPlay.Runners[i];
                if (runner.Identity is not { } id) continue;
                if (PlaybackEvaluator.RunnerAt(runner, t) == null) continue;   // 憤死/封殺で消えた走者は出さない
                var world = a.Go.transform.position + Vector3.up * TargetHeight;
                candidates.Add((id.Name, true, world, -1f));   // 走者は常に最優先（守備側だけを間引く）
            }

            candidates.Sort((x, y) => x.Priority.CompareTo(y.Priority));
            var result = new List<FieldLabel>(Math.Min(maxCount, candidates.Count));
            foreach (var c in candidates)
            {
                if (result.Count >= maxCount) break;
                var sp = _camera.WorldToViewportPoint(c.World);
                if (sp.z <= 0f) continue;   // カメラの後ろ
                result.Add(new FieldLabel(c.Name, c.IsRunner, new Vector2(sp.x, sp.y)));
            }
            return result;
        }

        /// <summary>
        /// 守備位置1つの注目度（issue #393・間引き案。小さいほど優先）。
        /// 0=現在のボールセグメントの投げ手/受け手、1=現在打球へ寄っている最中、
        /// それ以外はボールとの距離が近いほど優先（2+距離[m]×0.01）。
        /// </summary>
        private float FielderLabelPriority(FieldPosition pos, double t)
        {
            if (_fieldPlay == null) return 100f;
            foreach (var seg in _fieldPlay.Ball)
            {
                if (t < seg.T0 || t > seg.T1) continue;
                if (seg.FromRole == pos || seg.ToRole == pos) return 0f;
            }
            if (_fieldPlay.Moves.TryGetValue(pos, out var moves))
            {
                foreach (var m in moves)
                    if (t >= m.T0 && t <= m.T1) return 1f;
            }

            var ball = PlaybackEvaluator.BallAt(_fieldPlay, t);
            if (ball == null) return 50f;
            var (fp, _) = PlaybackEvaluator.FielderAt(_fieldPlay, pos, t);
            var dx = fp.X - ball.Value.X;
            var dy = fp.Y - ball.Value.Y;
            return 2f + (float)Math.Sqrt(dx * dx + dy * dy) * 0.01f;
        }
    }
}
