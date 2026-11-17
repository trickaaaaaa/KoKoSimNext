using System.Collections.Generic;
using KokoSim.Unity.Match;
using NUnit.Framework;
using UnityEngine;

namespace KokoSim.EditorTests
{
    /// <summary>
    /// 対決ビューの「絵でしか気づけない」実害の見張り（.claude/rules/3d-character.md の掟を検査にしたもの）。
    /// どれも壊れても例外もログも出ず、絵が少し変になるだけだった。素体・装備が無い環境（購入資産の無い端末）では
    /// リグが組めないので Ignore する＝失敗ではなく「この端末では測れない」。
    /// 回し方: Unity の Test Runner（EditMode）か MCP の run_tests。
    ///
    /// ここに置くのは「実装を壊したら赤くなる」ことを確かめた検査だけ（2026-11-17 に1本ずつ壊して確認）。
    /// 捕手の接地（issue #607）は入れていない: 接地補正が毎コマ最後に掛かるので、測った時点の浮きは
    /// 基準が汚れていても常に 0 になり、不具合を戻しても赤くならなかった。#607 は CatcherFloatProbe の実測で見る。
    /// </summary>
    public class DuelStageGuardTests
    {
        private DuelStageRig _rig;

        /// <summary>
        /// テストごとに組み直す。1つのリグを使い回すと、別のテストがリグを組んで片付けたときに
        /// こちらの役者まで消えた（2026-11-17 実測）＝テスト同士を独立にする。
        /// </summary>
        [SetUp]
        public void BuildRig()
        {
            _rig = new DuelStageRig();
            if (!_rig.Available)
            {
                _rig.Dispose();
                _rig = null;
                return;
            }
            _rig.EnsureSize(320, 180);
            // 構えまで流す（素立ちのTポーズのままだと「顔の向き」が意味を持たない）
            float step = 1f / DuelStageRig.PoseFps;
            for (int i = 0; i < 24; i++) _rig.Tick(step);
        }

        [TearDown]
        public void DisposeRig()
        {
            _rig?.Dispose();
            _rig = null;
        }

        private DuelStageRig Rig
        {
            get
            {
                if (_rig == null) Assert.Ignore("DuelStageRig が組めない（素体/クリップ未導入の端末）");
                return _rig;
            }
        }

        // ---- 顔の向き（素体ごとにバインドポーズの顔向きの符号が違う。design-18 §3.2.1） ----

        /// <summary>
        /// 構えの役者の顔が、相手のほうを向いていること。顔の前（HeadFwdLocal）の符号を
        /// 取り違えると、被り物は顔の前に合わせて付くので**帽子ごと後ろを向く**（後ろ被り）。
        /// 被り物と顔の前を比べても両方一緒に裏返るので検出できない＝相手の位置と比べる。
        /// 符号は素体ごとに違う: 捕手・投手は購入素体（+Z）、球審は U2 素体（-Z）＝両方の分岐を1人ずつ見る。
        /// 打者は入れない: 「投手を見る」首の補正が顔の前を投手へ向け直すので、符号を取り違えても
        /// この測り方では投手を向いたままになる（顔の実物だけが後ろを向く。2026-11-17 に壊して確かめた）。
        /// </summary>
        [TestCase(2, 1, 0.5f, TestName = "Catcher_FacesPitcher")]
        [TestCase(1, 2, 0.5f, TestName = "Pitcher_FacesCatcher")]
        [TestCase(4, 1, 0.5f, TestName = "Umpire_FacesPitcher")]
        public void Actor_FacesItsOpponent(int who, int toward, float minDot)
        {
            Assert.That(Rig.HeadFrameForCapture(who, out var center, out var fwd, out _), "頭の枠が取れない");
            Assert.That(Rig.HeadFrameForCapture(toward, out var target, out _, out _), "相手の頭の枠が取れない");
            var to = target - center;
            to.y = 0f;
            fwd.y = 0f;
            float dot = Vector3.Dot(fwd.normalized, to.normalized);
            Assert.That(dot, Is.GreaterThan(minDot),
                $"顔が相手を向いていない（水平の内積 {dot:F2}。負なら後ろ向き＝素体の顔向きの符号の取り違え）");
        }

        // ---- グラブをはめた手を描かない（HideGloveHand。issue #913） ----

        /// <summary>
        /// グラブ・ミットをはめた手（左手とその子孫の骨）に重み0.5以上の頂点が、どの三角形にも使われていないこと。
        /// 体メッシュが読めないと**エラーも出ずに手が残る**（2026-11-14 実害）。
        /// </summary>
        [TestCase(2, TestName = "Catcher_MittHandIsHidden")]
        [TestCase(1, TestName = "Pitcher_GloveHandIsHidden")]
        public void GloveHand_IsNotDrawn(int who)
        {
            var animator = Rig.ActorAnimatorForCapture(who);
            Assert.That(animator, Is.Not.Null, "役者が組めていない");
            var hand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
            Assert.That(hand, Is.Not.Null, "左手の骨が無い");

            int handVerts = 0, drawnHandTris = 0;
            foreach (var smr in animator.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = smr.sharedMesh;
                if (mesh == null || !mesh.isReadable) continue;
                var isHand = HandVertices(mesh, smr.bones, hand, out int count);
                if (count == 0) continue;
                handVerts += count;
                var tris = mesh.triangles;
                for (int i = 0; i < tris.Length; i += 3)
                    if (isHand[tris[i]] || isHand[tris[i + 1]] || isHand[tris[i + 2]]) drawnHandTris++;
            }
            Assert.That(handVerts, Is.GreaterThan(0), "手の頂点が1つも見つからない（検査が空振りしている）");
            Assert.That(drawnHandTris, Is.Zero, $"グラブをはめた手の三角形が {drawnHandTris} 枚描かれている");
        }

        private static bool[] HandVertices(Mesh mesh, Transform[] bones, Transform hand, out int count)
        {
            count = 0;
            var result = new bool[mesh.vertexCount];
            if (bones == null || bones.Length == 0) return result;
            var isHandBone = new bool[bones.Length];
            for (int i = 0; i < bones.Length; i++)
                isHandBone[i] = bones[i] != null && (bones[i] == hand || bones[i].IsChildOf(hand));
            var weights = mesh.boneWeights;
            if (weights == null || weights.Length != mesh.vertexCount) return result;
            for (int v = 0; v < weights.Length; v++)
            {
                var w = weights[v];
                float sum = 0f;
                if (w.boneIndex0 < isHandBone.Length && isHandBone[w.boneIndex0]) sum += w.weight0;
                if (w.boneIndex1 < isHandBone.Length && isHandBone[w.boneIndex1]) sum += w.weight1;
                if (w.boneIndex2 < isHandBone.Length && isHandBone[w.boneIndex2]) sum += w.weight2;
                if (w.boneIndex3 < isHandBone.Length && isHandBone[w.boneIndex3]) sum += w.weight3;
                if (sum >= 0.5f) { result[v] = true; count++; }
            }
            return result;
        }

        // ---- 配列で渡す色のガンマ→リニア変換（design-18 §3.5.1） ----

        /// <summary>
        /// 領域色の配列（_RegionColors）がリニアへ変換して渡されていること。SetColorArray は自動変換しないので、
        /// 素通しすると暗い色ほど持ち上がり、黒（ベルトの #23242A）が中間グレーに見える（2026-07-29 実害）。
        /// ベルトは全役割・全校で固定の黒なので、どのマテリアルでも同じ値になるはず。
        /// </summary>
        [Test]
        public void RegionColors_ArePassedInLinearSpace()
        {
            var black = new Color(0x23 / 255f, 0x24 / 255f, 0x2A / 255f);
            var expected = QualitySettings.activeColorSpace == ColorSpace.Linear ? black.linear : black;
            int belt = (int)DuelUniformMeshBuilder.Region.Belt;
            int checkedMaterials = 0;
            for (int who = 0; who <= 2; who++)
            {
                var animator = Rig.ActorAnimatorForCapture(who);
                if (animator == null) continue;
                foreach (var r in animator.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    // 配列は Properties に宣言できないので HasProperty では引けない＝配列そのものの有無で見分ける
                    var colors = m.GetColorArray("_RegionColors");
                    if (colors == null || colors.Length <= belt) continue;
                    checkedMaterials++;
                    var c = colors[belt];
                    Assert.That(c.r, Is.EqualTo(expected.r).Within(0.002f), $"ベルトの赤が変換されていない（{m.name}）");
                    Assert.That(c.g, Is.EqualTo(expected.g).Within(0.002f), $"ベルトの緑が変換されていない（{m.name}）");
                    Assert.That(c.b, Is.EqualTo(expected.b).Within(0.002f), $"ベルトの青が変換されていない（{m.name}）");
                }
            }
            Assert.That(checkedMaterials, Is.GreaterThan(0), "領域塗り分けのマテリアルが1つも見つからない（検査が空振りしている）");
        }

        // ---- 装備メッシュは1サブメッシュ（DuelGearMeshes.LoadMeters。design-18 §3.3.2） ----

        /// <summary>
        /// まとめる処理そのもの。今ある装備ファイルは取り込み前のデシメートで1サブメッシュになっていて、
        /// 下の getter の検査だけでは、まとめる処理が消えても気づけない（2026-11-17 に壊して確かめた）。
        /// </summary>
        [Test]
        public void MergeSubmeshes_JoinsAllTrianglesIntoOne()
        {
            var mesh = new Mesh
            {
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward, Vector3.one },
                subMeshCount = 2,
            };
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            mesh.SetTriangles(new[] { 1, 3, 4, 2, 3, 4 }, 1);
            try
            {
                DuelGearMeshes.MergeSubmeshes(mesh);
                Assert.That(mesh.subMeshCount, Is.EqualTo(1));
                Assert.That(mesh.GetTriangles(0).Length, Is.EqualTo(9), "2つ目のサブメッシュの三角形が落ちた");
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        /// <summary>
        /// 装備は1マテリアルで描くので、2つ目以降のサブメッシュは**黙って描かれない**
        /// （購入グラブのウェブが丸ごと消えていた。2026-11-14 実害）。読めた装備はすべて1サブメッシュであること。
        /// </summary>
        [Test]
        public void GearMeshes_AreSingleSubmesh()
        {
            var gear = new Dictionary<string, Mesh>
            {
                ["Cap"] = DuelGearMeshes.Cap(),
                ["CatcherMask"] = DuelGearMeshes.CatcherMask(),
                ["CatcherHelmet"] = DuelGearMeshes.CatcherHelmet(),
                ["BatterHelmet"] = DuelGearMeshes.BatterHelmet(),
                ["Chest"] = DuelGearMeshes.Chest(),
                ["Glove"] = DuelGearMeshes.Glove(),
                ["Mitt"] = DuelGearMeshes.Mitt(),
                ["LegGuard"] = DuelGearMeshes.LegGuard(),
            };
            int loaded = 0;
            foreach (var kv in gear)
            {
                if (kv.Value == null) continue;
                loaded++;
                Assert.That(kv.Value.subMeshCount, Is.EqualTo(1), $"{kv.Key} が {kv.Value.subMeshCount} サブメッシュのまま");
            }
            Assert.That(loaded, Is.GreaterThan(0), "装備メッシュが1つも読めない（検査が空振りしている）");
        }

        // ---- インプレー打球の据え置き（issue #481 / #1099） ----

        /// <summary>
        /// 打球を仕込んだ球が、接触の直前（まだ打球へ乗り換えていない）にあるとき、
        /// <see cref="DuelStageRig.BattedOutOfFrame"/> は「画面外」を返してはいけない（issue #1099）。
        /// 呼び出し側の時計（<c>MatchLiveController._pitchClock</c>）は rig の球の時計より
        /// 1フレーム未満だけ先に進むので、判定のフレームでは接触前であることがある。
        /// そこで「画面外」と返すと据え置きが始まらず、打球を1コマも描かずに守備ビューへ切り替わる。
        /// </summary>
        [Test]
        public void BattedOutOfFrame_IsFalse_WhileBattedBallAwaitsContact()
        {
            var rig = Rig;
            // センター返しのゴロ（本塁→中堅。打者の正面を転がる＝しばらく画面に残る打球）
            var grounder = new KokoSim.Engine.Match.Timeline.Playback.RollSegment(
                t0: 0, x0: 0, y0: 0, dx: 0, dy: 1, v0: 30, a: -6, dur: 2.0, hop: 0.3);
            rig.OnPitchReleased(-1f, DuelPitchOutcome.InPlayContact, null, grounder);

            // 接触（＝判定の瞬間）のわずか手前まで進める
            float contact = rig.CallMomentSeconds;
            float dt = 1f / 120f;
            float t = 0f;
            while (t + dt < contact - 0.002f) { rig.Tick(dt); t += dt; }

            Assert.That(rig.BattedElapsedSeconds, Is.LessThan(0f), "前提: まだ打球へ乗り換えていない");
            Assert.That(rig.BattedOutOfFrame, Is.False,
                "接触前の打球を「もう画面外」と扱うと、据え置きが始まらず打球が1コマも描かれない");

            // 接触を越えれば打球が始まり、打点は画面の中＝ここでも「画面内」
            rig.Tick(0.02f);
            Assert.That(rig.BattedElapsedSeconds, Is.GreaterThanOrEqualTo(0f), "前提: 接触を越えて打球が始まった");
            Assert.That(rig.BattedOutOfFrame, Is.False, "打った直後の打球は画面の中にある");
        }

        /// <summary>打球の無い球（見送り）は見せるものが無い＝待たせない（従来どおり「画面外」扱い）。</summary>
        [Test]
        public void BattedOutOfFrame_IsTrue_WhenNoBattedBall()
        {
            var rig = Rig;
            rig.OnPitchReleased(-1f, DuelPitchOutcome.Take, null, null);
            rig.Tick(0.1f);
            Assert.That(rig.BattedOutOfFrame, Is.True);
        }
    }
}
