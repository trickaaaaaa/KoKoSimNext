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

        // ---- ビルド同梱の索引に載せるクリップの一覧（RequiredClipNames）の漏れ ----

        /// <summary>
        /// リグが持つクリップ名の表（<c>…ClipName</c>・<c>…ClipNames</c>・<c>Name</c>/<c>Clip</c> を持つ表や1件物）が
        /// すべて <see cref="DuelStageRig.RequiredClipNames"/> に載っていること。漏れたクリップはビルド同梱の索引に入らず、
        /// <b>ビルドでだけ</b>読めない（エディタは索引を通さず読むので絵では気づけない）。実害: 振り返り・背走・
        /// 横歩き・走り出し・ジャンピングキャッチ・はじく・捕逸の10表が漏れていて、ブラウザ版で外野手が
        /// 待機の姿勢のまま滑っていた（#1323）。新しい表を足したら一覧にも足す。
        /// </summary>
        [Test]
        public void EveryClipTableOfTheRig_IsInTheCatalogList()
        {
            var required = new HashSet<string>(DuelStageRig.RequiredClipNames());
            var missing = new List<string>();
            int seen = 0;
            const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            foreach (var f in typeof(DuelStageRig).GetFields(F))
            {
                if (f.Name == "ClipFbxSources") continue;   // クリップ名ではなく供給元 FBX の置き場所
                object v;
                try { v = f.GetValue(null); } catch { continue; }
                if (v == null) continue;
                foreach (var name in ClipNamesIn(f.Name, v))
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    seen++;
                    if (!required.Contains(name)) missing.Add(f.Name + ": " + name);
                }
            }
            Assert.That(seen, Is.GreaterThan(50), "クリップ名の表を拾えていない（検査が空振りしている）");
            Assert.That(missing, Is.Empty, "ビルド同梱の索引から漏れるクリップ（RequiredClipNames に足す）");
        }

        private static IEnumerable<string> ClipNamesIn(string fieldName, object v)
        {
            if (v is string s) { if (fieldName.EndsWith("ClipName")) yield return s; yield break; }
            if (v is string[] arr) { if (fieldName.Contains("Clip")) foreach (var n in arr) yield return n; yield break; }
            if (v is System.Array a)
            {
                var member = ClipMember(a.GetType().GetElementType());
                if (member == null) yield break;
                foreach (var e in a) yield return (string)member.GetValue(e);
                yield break;
            }
            var t = v.GetType();
            if (t.IsPrimitive || t.IsEnum || t.Namespace == null || !t.Namespace.StartsWith("KokoSim")) yield break;
            var one = ClipMember(t);
            if (one != null) yield return (string)one.GetValue(v);
        }

        /// <summary>表の要素のうちクリップ名を持つ欄（Name か Clip の文字列）。</summary>
        private static System.Reflection.FieldInfo ClipMember(System.Type t)
        {
            var m = t.GetField("Name") ?? t.GetField("Clip");
            return m != null && m.FieldType == typeof(string) ? m : null;
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

        /// <summary>
        /// ビルドと同じ経路（索引だけ）で、リポジトリにある装備がすべて読めること。装備のロードが
        /// エディタ限定だった間、ビルドではグラブ・ミット・マスク・レガースが全部プリミティブに落ちていた
        /// （ブラウザ版で発覚・#1323。エディタの絵では気づけない）。索引は gitignore（購入資産の参照を含む）なので、
        /// 無い環境では検査しない。
        /// </summary>
        [Test]
        public void GearMeshes_LoadFromCatalogLikeABuild()
        {
            Assume.That(DuelStageRig.Catalog, Is.Not.Null, "ビルド同梱の索引が無い環境（購入資産なし）");
            bool prev = DuelTuning.ForceCatalogOnly;
            DuelTuning.ForceCatalogOnly = true;
            DuelGearMeshes.ClearCache();
            try
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
                foreach (var kv in gear)
                    Assert.That(kv.Value, Is.Not.Null,
                        $"{kv.Key} が索引から読めない＝ビルドでプリミティブに落ちる（KokoSim/Duel/F8 ビルド同梱の索引を焼く）");
            }
            finally
            {
                DuelTuning.ForceCatalogOnly = prev;
                DuelGearMeshes.ClearCache();
            }
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

        /// <summary>
        /// 対決ビューのファウルの打球は、打点から離れていく一方で、途中で打点の側へ戻ってこない（issue #1074「跳ね返る」）。
        /// engine が作るファウルの再生（BuildFoulFlight）の打球区間をそのまま乗せ、水平距離の推移を測る。
        /// </summary>
        [TestCase(30f, 2f, 60f)]     // 一塁側への低い当たり（転がり）
        [TestCase(30f, 2f, -60f)]    // 三塁側への低い当たり
        [TestCase(25f, -5f, 100f)]   // 後ろへのボテボテ
        [TestCase(30f, 10f, 150f)]   // 真後ろへのライナー
        [TestCase(28f, 35f, 120f)]   // 後ろへの飛球
        [TestCase(20f, 5f, 50f)]     // ライン際の弱い当たり
        public void FoulBall_NeverComesBackTowardTheBat(float exitMps, float launchDeg, float bearingDeg)
        {
            var rig = Rig;
            var ball = new KokoSim.Engine.Match.Batting.BattedBall
                { ExitVelocityMps = exitMps, LaunchAngleDeg = launchDeg, BearingDeg = bearingDeg };
            var tl = KokoSim.Engine.Match.Timeline.TimelineBuilder.BuildFoulFlight(ball,
                new KokoSim.Engine.Match.Pitching.Aerodynamics(), new KokoSim.Engine.Match.Fielding.FieldingCoefficients(),
                new KokoSim.Engine.Match.Field.FieldGeometry(), false, false, false);
            var play = KokoSim.Engine.Match.Timeline.Playback.PlayTimelineAdapter.ToPlaybackPlay(tl);
            rig.OnPitchReleased(-1f, DuelPitchOutcome.FoulContact, null, play.Ball[1]);
            // 接触まで進める（打点合わせの補正は接触の瞬間に決まる）
            for (int i = 0; i < 400 && rig.BattedElapsedSeconds < 0f; i++) rig.Tick(1f / 240f);
            Assert.That(rig.BattedElapsedSeconds, Is.GreaterThanOrEqualTo(0f), "前提: 打球になった");

            var start = rig.BattedPointForCapture(0f).Value;
            float prev = 0f, worst = 0f, worstAt = 0f;
            for (float t = 0f; t <= 1.5f; t += 1f / 120f)
            {
                var p = rig.BattedPointForCapture(t).Value;
                float d = new Vector2(p.x - start.x, p.z - start.z).magnitude;
                if (prev - d > worst) { worst = prev - d; worstAt = t; }
                prev = Mathf.Max(prev, d);
            }
            Assert.That(worst, Is.LessThan(0.05f),
                $"打球が打点の側へ {worst:F2}m 戻った（t={worstAt:F2}s）");
        }

        /// <summary>
        /// 同じ見張りを初速・角度・方向の格子で掃く（issue #1074「ファウルが画面内で跳ね返る」）。打球の進む向きに対して
        /// 逆向きに動くコマが無いこと、物理の着地よりずっと早く地面に着いて跳ね上がらないこと
        /// （上がっていく低いライナーを「転がり」で描いていて、打者の目の前で地面に当たり 2m 近く跳ね上がっていた）。
        /// </summary>
        [Test]
        public void FoulBall_Sweep_NeverMovesAgainstItsDirection()
        {
            var rig = Rig;
            var aero = new KokoSim.Engine.Match.Pitching.Aerodynamics();
            var co = new KokoSim.Engine.Match.Fielding.FieldingCoefficients();
            var fg = new KokoSim.Engine.Match.Field.FieldGeometry();
            var bad = new System.Text.StringBuilder();
            var heights = new System.Text.StringBuilder();
            int n = 0;
            foreach (var ev in new[] { 15f, 25f, 35f, 45f })
            foreach (var la in new[] { -15f, -5f, 2f, 8f, 15f, 30f, 50f, 70f })
            foreach (var br in new[] { 46f, 55f, 70f, 90f, 120f, 150f, 175f, -46f, -55f, -70f, -90f, -120f, -150f })
            {
                var ball = new KokoSim.Engine.Match.Batting.BattedBall { ExitVelocityMps = ev, LaunchAngleDeg = la, BearingDeg = br };
                var tl = KokoSim.Engine.Match.Timeline.TimelineBuilder.BuildFoulFlight(ball, aero, co, fg, false, false, false);
                var play = KokoSim.Engine.Match.Timeline.Playback.PlayTimelineAdapter.ToPlaybackPlay(tl);
                rig.OnPitchReleased(-1f, DuelPitchOutcome.FoulContact, null, play.Ball[1]);
                for (int i = 0; i < 400 && rig.BattedElapsedSeconds < 0f; i++) rig.Tick(1f / 240f);
                if (rig.BattedElapsedSeconds < 0f) continue;
                var p0 = rig.BattedPointForCapture(0f).Value;
                var pEnd = rig.BattedPointForCapture(1.5f).Value;
                var dir = new Vector2(pEnd.x - p0.x, pEnd.z - p0.z);
                if (dir.magnitude < 0.01f) continue;
                dir.Normalize();
                var prev = p0;
                float worst = 0f, worstT = 0f, minY = p0.y, rebound = 0f, reboundT = 0f, groundAt = float.NaN;
                for (float t = 1f / 120f; t <= 1.5f; t += 1f / 120f)
                {
                    var p = rig.BattedPointForCapture(t).Value;
                    float along = Vector2.Dot(new Vector2(p.x - prev.x, p.z - prev.z), dir);
                    if (-along > worst) { worst = -along; worstT = t; }
                    minY = Mathf.Min(minY, p.y);
                    if (p.y < 0.12f && float.IsNaN(groundAt)) groundAt = t;
                    if (!float.IsNaN(groundAt) && p.y - minY > rebound) { rebound = p.y - minY; reboundT = t; }
                    prev = p;
                }
                n++;
                // 物理の打球が地面に着く時刻より、絵の球がずっと早く地面に着いて跳ね上がる＝打者の目の前で
                // 地面に当たって跳ね返って見える（物理は上がっていくライナーなのに「転がり」で描いていた）
                float hang = (float)(play.Ball[1].T1 - play.Ball[1].T0);
                if (!float.IsNaN(groundAt) && hang > 0.2f && groundAt < hang * 0.8f && rebound > 0.25f)
                    heights.AppendLine($"ev={ev} la={la} br={br}: 物理の着地 {hang:F2}s より早い {groundAt:F2}s に地面へ着き {rebound:F2}m 跳ね上がる");
                if (worst > 0.02f) bad.AppendLine($"ev={ev} la={la} br={br}: 逆向きに {worst:F3}m/コマ（t={worstT:F2}s）");
            }
            Debug.Log($"[1074] ファウル {n} 本を検査\n{bad}\n[1074-h]\n{heights}");
            Assert.That(n, Is.GreaterThan(100), "前提: 検査できた本数（全件スキップでも緑にならないように）");
            Assert.That(bad.Length, Is.EqualTo(0), bad.ToString());
            Assert.That(heights.Length, Is.EqualTo(0), "空中で跳ね返る打球:\n" + heights);
        }

        /// <summary>
        /// 一度画面の外へ出たファウルの打球が、見せている間（最長1.5秒）に画面の中へ戻ってこないこと
        /// （issue #1074「高く上がったファウルが画面端で跳ね返る」の再現探し）。
        /// </summary>
        [Test]
        public void FoulBall_Sweep_DoesNotReenterTheFrame()
        {
            var rig = Rig;
            var aero = new KokoSim.Engine.Match.Pitching.Aerodynamics();
            var co = new KokoSim.Engine.Match.Fielding.FieldingCoefficients();
            var fg = new KokoSim.Engine.Match.Field.FieldGeometry();
            var bad = new System.Text.StringBuilder();
            int checkedCount = 0;
            foreach (var ev in new[] { 15f, 22f, 30f, 38f, 45f })
            foreach (var la in new[] { -15f, -5f, 2f, 8f, 15f, 25f, 35f, 50f, 65f, 80f })
            foreach (var br in new[] { 46f, 55f, 70f, 90f, 120f, 150f, 175f, -46f, -55f, -70f, -90f, -120f, -150f })
            {
                var ball = new KokoSim.Engine.Match.Batting.BattedBall { ExitVelocityMps = ev, LaunchAngleDeg = la, BearingDeg = br };
                var tl = KokoSim.Engine.Match.Timeline.TimelineBuilder.BuildFoulFlight(ball, aero, co, fg, false, false, false);
                var play = KokoSim.Engine.Match.Timeline.Playback.PlayTimelineAdapter.ToPlaybackPlay(tl);
                rig.OnPitchReleased(-1f, DuelPitchOutcome.FoulContact, null, play.Ball[1]);
                for (int i = 0; i < 400 && rig.BattedElapsedSeconds < 0f; i++) rig.Tick(1f / 240f);
                if (rig.BattedElapsedSeconds < 0f) continue;
                checkedCount++;
                // 実際に描かれる球（rig を進めた結果）で見る。消えた球（null）は「画面の外」と同じ
                bool wasOut = false;
                for (float t = 0f; t <= 1.5f; t += 1f / 60f)
                {
                    rig.Tick(1f / 60f);
                    var vpn = rig.BallViewportForCapture;
                    bool inFrame = vpn is { } vp && vp.z > 0f && vp.x >= 0f && vp.x <= 1f && vp.y >= 0f && vp.y <= 1f;
                    if (!inFrame) wasOut = true;
                    else if (wasOut)
                    {
                        bad.AppendLine($"ev={ev} la={la} br={br}: t={t:F2}s で画面へ戻る（vp=({vpn.Value.x:F2},{vpn.Value.y:F2})）");
                        break;
                    }
                }
                rig.HideBall();
            }
            Assert.That(checkedCount, Is.GreaterThan(100), "前提: 検査できた本数（全件スキップでも緑にならないように）");
            Assert.That(bad.Length, Is.EqualTo(0), "画面へ戻るファウル:\n" + bad);
        }

        /// <summary>ファウルを実際のリリースと同じ形（本塁面の実着弾位置つき）で流し、打球へ乗り換わるまで進める。</summary>
        private bool LaunchFoul(DuelStageRig rig, float ev, float la, float br, float plateX, float plateY,
            KokoSim.Engine.Match.Fielding.FoulChase chase = null)
        {
            var ball = new KokoSim.Engine.Match.Batting.BattedBall { ExitVelocityMps = ev, LaunchAngleDeg = la, BearingDeg = br };
            var tl = KokoSim.Engine.Match.Timeline.TimelineBuilder.BuildFoulFlight(ball,
                new KokoSim.Engine.Match.Pitching.Aerodynamics(), new KokoSim.Engine.Match.Fielding.FieldingCoefficients(),
                new KokoSim.Engine.Match.Field.FieldGeometry(), false, false, false, chase);
            var play = KokoSim.Engine.Match.Timeline.Playback.PlayTimelineAdapter.ToPlaybackPlay(tl);
            var pitch = new DuelPitchFlight(plateX, plateY, plateX, plateY, 0.5f, 0.05f, 0.2f, 140f);
            rig.OnPitchReleased(-1f, DuelPitchOutcome.FoulContact, pitch, play.Ball[1]);
            for (int i = 0; i < 400 && rig.BattedElapsedSeconds < 0f; i++) rig.Tick(1f / 240f);
            return rig.BattedElapsedSeconds >= 0f;
        }

        /// <summary>打球の水平の動きのうち、全体の進む向きと逆へ戻った距離の合計[m]（1.5秒・120fps）。</summary>
        private static float BackwardTravel(DuelStageRig rig, out float worstAt)
        {
            var p0 = rig.BattedPointForCapture(0f).Value;
            var pEnd = rig.BattedPointForCapture(1.5f).Value;
            worstAt = 0f;
            var dir = new Vector2(pEnd.x - p0.x, pEnd.z - p0.z);
            if (dir.magnitude < 0.01f) return 0f;
            dir.Normalize();
            var prev = p0;
            float sum = 0f, worst = 0f;
            for (float t = 1f / 120f; t <= 1.5f; t += 1f / 120f)
            {
                var p = rig.BattedPointForCapture(t).Value;
                float along = Vector2.Dot(new Vector2(p.x - prev.x, p.z - prev.z), dir);
                if (along < 0f) { sum -= along; if (-along > worst) { worst = -along; worstAt = t; } }
                prev = p;
            }
            return sum;
        }

        /// <summary>
        /// 実際の試合に出たファウル（2026-11-23・100試合・1834本の打球つきファウルから、戻りが大きかった当たりと
        /// 守備ビューで見せる2種）を、打点の実位置つきで流して、打球が着地点で止まったあとに引き戻されないこと（issue #1543）。
        /// 叩きつけた速いファウルは engine のセグメントが打点合わせの補正（自由落下時間）より先に終わり、
        /// 静止した球を残りの補正が打球の進む向きと逆へ最大0.4m引き戻していた（画面の中で「跳ね返る」）。
        /// 格子（#1074）は打点が本塁の真上（x=0）固定で、補正が0に近かったので掃けていなかった。
        /// </summary>
        [TestCase(38.5f, -22.1f, 49.7f, 0.46f, 0.70f, TestName = "FoulRebound_GrounderFirstBase")]
        [TestCase(28.0f, -51.7f, -54.4f, -0.39f, 0.76f, TestName = "FoulRebound_PoundedThirdBase")]
        [TestCase(38.5f, -16.0f, -45.9f, -0.36f, 0.61f, TestName = "FoulRebound_LineHugger")]
        [TestCase(40.9f, -7.3f, -61.1f, -0.50f, 0.89f, TestName = "FoulRebound_HardChopLeavesFrame")]
        [TestCase(35.4f, -36.6f, 49.2f, 0.18f, 0.96f, TestName = "FoulRebound_IntoDirt")]
        [TestCase(42f, 25f, 47f, 0.3f, 0.8f, TestName = "FoulRebound_PoleFoulBigFoul")]
        public void FoulBall_RealGameCases_DoNotDragBackAfterStopping(float ev, float la, float br, float px, float py)
        {
            var rig = Rig;
            Assert.That(LaunchFoul(rig, ev, la, br, px, py), "前提: 打球になった");
            float back = BackwardTravel(rig, out float at);
            Assert.That(back, Is.LessThan(0.02f), $"打球が進む向きと逆へ合計 {back:F2}m 戻った（最大のコマ t={at:F2}s）");
        }

        /// <summary>
        /// 同じ見張りを打点の位置（左右）も振った格子で掃く（issue #1543）。#1074 の格子は打点を本塁の真上に固定していて、
        /// 打点合わせの補正が効く条件を掃いていなかった。ゴロ性（叩きつけ）を厚めに取る。
        /// </summary>
        [Test]
        public void FoulBall_Sweep_PlateOffsets_DoNotDragBackAfterStopping()
        {
            var rig = Rig;
            var bad = new System.Text.StringBuilder();
            int n = 0;
            foreach (var ev in new[] { 20f, 30f, 40f })
            foreach (var la in new[] { -50f, -35f, -22f, -10f, -3f, 5f, 20f, 45f })
            foreach (var br in new[] { 46f, 60f, 90f, 150f, -46f, -60f, -90f, -150f })
            foreach (var px in new[] { -0.4f, 0f, 0.4f })
            {
                if (!LaunchFoul(rig, ev, la, br, px, 0.75f)) { rig.HideBall(); continue; }
                n++;
                float back = BackwardTravel(rig, out float at);
                if (back > 0.02f) bad.AppendLine($"ev={ev} la={la} br={br} plateX={px}: 逆向きに合計 {back:F2}m（t={at:F2}s）");
                rig.HideBall();
            }
            Assert.That(n, Is.GreaterThan(400), "前提: 掃けた本数");
            Assert.That(bad.Length, Is.EqualTo(0), "止まったあとに引き戻されるファウル:\n" + bad);
        }

        /// <summary>
        /// 守備ビューで見せるファウル（追ったフライ）の対決ビュー側の打球区間も、戻らないこと（issue #1543 の穴2）。
        /// </summary>
        [TestCase(25f, 60f, 100f, 0.1f)]
        [TestCase(22f, 70f, -120f, -0.3f)]
        [TestCase(30f, 50f, 135f, 0.35f)]
        public void FoulBall_ChasedFly_DoesNotDragBack(float ev, float la, float br, float px)
        {
            var rig = Rig;
            var chase = new KokoSim.Engine.Match.Fielding.FoulChase(
                KokoSim.Engine.Match.Field.FieldPosition.Catcher, 0.0, -1.0, 4.6, 4.2);
            Assert.That(LaunchFoul(rig, ev, la, br, px, 0.75f, chase), "前提: 打球になった");
            float back = BackwardTravel(rig, out float at);
            Assert.That(back, Is.LessThan(0.1f), $"追ったフライの打球が逆へ合計 {back:F2}m 戻った（t={at:F2}s）");
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

        // ---- 守備ビューから戻った投手のルートモーション（2026-11-24 実害） ----

        /// <summary>
        /// 打球プレー（守備ビュー）を流して対決ビューへ戻したら、投手のルートモーションが入っていること。
        /// 守備ビューは捕球・走りのために役者のルートモーションを切る。戻し忘れると、次の打席から投手の
        /// 踏み込みと体の回転（リリース後に約150°）が抜け、セットの向き（三塁側）のまま横へ倒れ込んで投げる。
        /// </summary>
        [Test]
        public void Pitcher_RootMotionIsBackOn_AfterFieldPlay()
        {
            var rig = Rig;
            var pitcher = rig.ActorAnimatorForCapture(1);
            Assert.That(pitcher, Is.Not.Null, "前提: 投手がいる");
            Assert.That(pitcher.applyRootMotion, Is.True, "前提: 対決ビューの投手はルートモーションで踏み込む");

            var play = PitcherFieldsPlay();
            rig.BeginFieldPlay(play, 0f);
            float step = 1f / DuelStageRig.PoseFps;
            bool cutDuringPlay = false;
            for (float t = 0f; t < 4.5f; t += step)
            {
                rig.SetFieldTime(t);   // 試合と同じく守備ビューの時計を進める（MatchLiveController.RenderReplayFrame）
                rig.Tick(step);
                cutDuringPlay |= !pitcher.applyRootMotion;
            }
            Assert.That(cutDuringPlay, Is.True, "前提: 投手が捕った（捕球の収録がルートモーションを切った）");
            rig.EndFieldPlay();

            Assert.That(pitcher.applyRootMotion, Is.True,
                "守備ビューから戻った投手のルートモーションが切れたまま＝次の打席から横を向いて投げる");
        }

        /// <summary>投手前のゴロを投手が自分で捕る打球（捕球の収録が役者のルートモーションを切る）。</summary>
        private static KokoSim.Engine.Match.Timeline.Playback.PlaybackPlay PitcherFieldsPlay()
        {
            var p = KokoSim.Engine.Match.Field.FieldPosition.Pitcher;
            var pt = new KokoSim.Engine.Match.Timeline.TimelinePoint(1.0, 14.0);
            var tl = new KokoSim.Engine.Match.Timeline.PlayTimeline
            {
                Ball = new List<KokoSim.Engine.Match.Timeline.BallSegment>
                {
                    new KokoSim.Engine.Match.Timeline.BallSegment
                    {
                        Kind = KokoSim.Engine.Match.Timeline.BallSegmentKind.Roll, T0 = 0.85, T1 = 1.6,
                        From = new KokoSim.Engine.Match.Timeline.TimelinePoint(0, 0), To = pt,
                    },
                },
                Moves = new List<KokoSim.Engine.Match.Timeline.FielderMove>
                {
                    new KokoSim.Engine.Match.Timeline.FielderMove
                    {
                        Role = p, T0 = 1.0, T1 = 1.6, To = pt,
                        Task = KokoSim.Engine.Match.Timeline.FielderTask.FieldBall,
                    },
                },
                FielderStarts = new Dictionary<KokoSim.Engine.Match.Field.FieldPosition, KokoSim.Engine.Match.Timeline.TimelinePoint>
                {
                    [p] = new KokoSim.Engine.Match.Timeline.TimelinePoint(0, 17.0),
                },
                Result = "テスト", Duration = 4.0, ResolvedAt = 4.0,
            };
            return KokoSim.Engine.Match.Timeline.Playback.PlayTimelineAdapter.ToPlaybackPlay(tl);
        }

        // ---- 外野手が走りながら掬った後の送球（issue #1552） ----

        /// <summary>
        /// 走りながら掬った外野手は、engine の運びが止まってから送球クリップを流す（足を据えた収録の間に位置が動くと
        /// 体が地面を滑る）。流し始め〜リリースの間の engine の位置が動かないこと。
        /// </summary>
        [TestCase(4.98f)]   // 握り替え 1.18s（深い位置の収録のリリース 1.36s より短い＝速回しで収める）
        [TestCase(4.40f)]   // 握り替え 0.60s（守備の上手い野手＝頭を捨てる分も出る）
        [TestCase(5.80f)]   // 握り替え 2.00s（窓が十分ある）
        public void OutfieldRunThroughThrow_StartsOnlyAfterTheCarryStops(float release)
        {
            var rig = Rig;
            var play = RunThroughPlay(3.8, release);
            rig.BeginFieldPlay(play, 0f);
            var plan = rig.ThrowPlanForTest();
            Assert.That(plan.Length, Is.GreaterThan(0), "前提: 送球の予定が組まれた");

            var e = plan[0];
            Assert.That(e.From, Is.EqualTo(KokoSim.Engine.Match.Field.FieldPosition.LeftField));
            var p0 = KokoSim.Engine.Match.Timeline.Playback.PlaybackEvaluator.FielderAt(play, e.From, e.TFire).Pos;
            var p1 = KokoSim.Engine.Match.Timeline.Playback.PlaybackEvaluator.FielderAt(play, e.From, e.TRelease).Pos;
            float moved = new Vector2((float)(p1.X - p0.X), (float)(p1.Y - p0.Y)).magnitude;
            Assert.That(moved, Is.LessThan(0.01f),
                $"送球クリップを流している間（{e.TFire:F2}〜{e.TRelease:F2}s）に engine の位置が {moved:F2}m 動く＝体が滑る");
        }

        // ---- 内野手がライナーをノーバウンドで捕った後の滑り（issue #1724） ----

        /// <summary>
        /// 低く強いライナーを内野手が正面で捕るとき、絵の捕球点は engine の野手の位置から届く範囲に収まること。
        /// 捕球の時刻を「グラブの高さを通る時刻」へ遡らせすぎると、捕球点が打った直後の上がり際まで本塁寄りへ数m
        /// ずれ、体をそこまで寄せて捕ってから保持の後に平行移動で戻す＝足を動かさずに滑る（体の寄せ＝0なら滑らない）。
        /// </summary>
        [TestCase(46f, 1.31f, 0.94)]   // 実測した強いライナー（打ち出し高さ1.0m・頂点1.3m）
        [TestCase(34f, 1.8f, 1.52)]    // 少し高く長い滞空
        public void InfielderLineDriveCatch_DoesNotShiftTheBodyToAFarCatchPoint(float exit, float apex, double hang)
        {
            var rig = Rig;
            var ss = KokoSim.Engine.Match.Field.FieldPosition.Shortstop;
            var pt = new KokoSim.Engine.Match.Timeline.TimelinePoint(-9.2, 37.1);
            var tl = new KokoSim.Engine.Match.Timeline.PlayTimeline
            {
                Ball = new List<KokoSim.Engine.Match.Timeline.BallSegment>
                {
                    new KokoSim.Engine.Match.Timeline.BallSegment
                    {
                        Kind = KokoSim.Engine.Match.Timeline.BallSegmentKind.Flight, T0 = 0.85, T1 = 0.85 + hang,
                        From = new KokoSim.Engine.Match.Timeline.TimelinePoint(0, 0), To = pt,
                        ApexHeightM = apex, LaunchHeightM = 1.0,
                    },
                },
                FielderStarts = new Dictionary<KokoSim.Engine.Match.Field.FieldPosition, KokoSim.Engine.Match.Timeline.TimelinePoint>
                {
                    [ss] = pt,
                },
                Result = "テスト", Duration = 5.0, ResolvedAt = 5.0,
            };
            rig.BeginFieldPlay(KokoSim.Engine.Match.Timeline.Playback.PlayTimelineAdapter.ToPlaybackPlay(tl), 0f);
            var plan = rig.CatchPlanForTest();
            Assert.That(plan.Length, Is.GreaterThan(0), "前提: 捕球の予定が組まれた");
            Assert.That(plan[0].Pos, Is.EqualTo(ss));
            Assert.That(plan[0].BodyShiftM, Is.LessThan(0.3f),
                $"体を {plan[0].BodyShiftM:F2}m 寄せて捕る＝捕った後に元の位置へ平行移動で戻る（足を据えたまま滑る）");
            var d = new Vector2(plan[0].Point.x - (float)pt.X, plan[0].Point.z - (float)pt.Z).magnitude;
            Assert.That(d, Is.LessThan(1.2f), $"捕球点が engine の野手の位置から {d:F2}m 離れている");
        }

        // ---- 塁審は帽子だけ（マスク・胸当てが無い。issue #1740） ----

        /// <summary>
        /// 塁審の頭には帽子（ucap-*）だけが付き、球審用のマスク（umask-*）・捕手用の防具（cmask-*）・胸当て（chest-*）が
        /// 付かないこと。塁審は球審と同じ U2素体＋着衣なので、選手向けの帽子メッシュを被せると額が突き抜けて顔を覆い、
        /// マスクを被って見えた（絵でしか気づけない・例外もログも出ない）。
        /// </summary>
        [Test]
        public void BaseUmpire_WearsACapOnly()
        {
            var rig = Rig;
            var tl = new KokoSim.Engine.Match.Timeline.PlayTimeline
            {
                Ball = new List<KokoSim.Engine.Match.Timeline.BallSegment>
                {
                    new KokoSim.Engine.Match.Timeline.BallSegment
                    {
                        Kind = KokoSim.Engine.Match.Timeline.BallSegmentKind.Flight, T0 = 0.85, T1 = 2.0,
                        From = new KokoSim.Engine.Match.Timeline.TimelinePoint(0, 0),
                        To = new KokoSim.Engine.Match.Timeline.TimelinePoint(-9.2, 37.1),
                        ApexHeightM = 1.8, LaunchHeightM = 1.0,
                    },
                },
                FielderStarts = new Dictionary<KokoSim.Engine.Match.Field.FieldPosition, KokoSim.Engine.Match.Timeline.TimelinePoint>
                {
                    [KokoSim.Engine.Match.Field.FieldPosition.Shortstop] = new KokoSim.Engine.Match.Timeline.TimelinePoint(-9.2, 37.1),
                },
                Result = "テスト", Duration = 5.0, ResolvedAt = 5.0,
            };
            rig.BeginFieldPlay(KokoSim.Engine.Match.Timeline.Playback.PlayTimelineAdapter.ToPlaybackPlay(tl), 0f);
            var gear = rig.BaseUmpireHeadGearForTest();
            Assert.That(gear, Is.Not.Empty, "塁審が組めていない（検査が空振りしている）");
            Assert.That(gear, Has.Some.StartsWith("ucap-"), "塁審に帽子が付いていない");
            Assert.That(gear, Has.None.StartsWith("umask").Or.StartsWith("cmask").Or.StartsWith("chest"),
                "塁審にマスク・胸当てが付いている: " + string.Join(",", gear));
        }

        // ---- バントのあとの打者走者の足の滑り（issue #1734） ----

        /// <summary>
        /// 本塁→一塁のレッグ1本だけの打者走者（バント・振り逃げ。engine は内野ゴロでしか駆け抜けのレッグを積まない）は、
        /// 一塁に着くまで走りの段で流れ（足の運びと進みが合う）、塁を通ってから駆け抜けの減速クリップへ移ること。
        /// 駆け抜けのレッグを補わないと、本塁→一塁の全編を減速クリップ（全長2.67s）で流す＝走り→歩きの足の運びのまま
        /// 約4秒で27mを進み、足が地面を滑る（診断は走者の毎コマの段: -2＝駆け抜けクリップ）。
        /// </summary>
        [Test]
        public void BuntBatterRunner_RunsOnTheLadderToFirst_ThenOverruns()
        {
            var rig = Rig;
            var tl = new KokoSim.Engine.Match.Timeline.PlayTimeline
            {
                Ball = new List<KokoSim.Engine.Match.Timeline.BallSegment>
                {
                    new KokoSim.Engine.Match.Timeline.BallSegment
                    {
                        Kind = KokoSim.Engine.Match.Timeline.BallSegmentKind.Roll, T0 = 0.85, T1 = 1.6,
                        From = new KokoSim.Engine.Match.Timeline.TimelinePoint(0, 0),
                        To = new KokoSim.Engine.Match.Timeline.TimelinePoint(0, 4),
                    },
                },
                Runners = new List<KokoSim.Engine.Match.Timeline.RunnerLeg>
                {
                    new KokoSim.Engine.Match.Timeline.RunnerLeg
                    {
                        Label = "打", T0 = 0.85, T1 = 5.32,
                        From = new KokoSim.Engine.Match.Timeline.TimelinePoint(0, 0),
                        To = new KokoSim.Engine.Match.Timeline.TimelinePoint(
                            KokoSim.Engine.Match.Field.FieldDiagramGeometry.First.X,
                            KokoSim.Engine.Match.Field.FieldDiagramGeometry.First.Y),
                        Force = true,
                    },
                },
                Result = "テスト", Duration = 7.0, ResolvedAt = 7.0,
            };
            rig.BeginFieldPlay(KokoSim.Engine.Match.Timeline.Playback.PlayTimelineAdapter.ToPlaybackPlay(tl), 0f);
            float step = 1f / DuelStageRig.PoseFps;
            for (float t = 0f; t < 6.4f; t += step)
            {
                rig.SetFieldTime(t);
                rig.Tick(step);
            }

            int onLadder = 0, overrun = 0;
            float slipMin = float.MaxValue, slipMax = 0f;
            foreach (var line in rig.RunnerDiagForCapture().Split('\n'))
            {
                var f = line.Split('\t');
                if (f.Length != 6 || f[0] != "打" || !float.TryParse(f[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ft)) continue;
                int ladder = int.Parse(f[3]);
                if (ladder == -2) { overrun++; Assert.That(ft, Is.GreaterThan(5.3f), $"塁に着く前（t={ft:F2}s）から駆け抜けクリップを流している"); }
                if (ladder < 0) continue;
                onLadder++;
                float slip = float.Parse(f[5], System.Globalization.CultureInfo.InvariantCulture);
                slipMin = Mathf.Min(slipMin, slip);
                slipMax = Mathf.Max(slipMax, slip);
            }
            Assert.That(onLadder, Is.GreaterThan(30), "一塁へ走る間、走りの段で流れていない（全編を駆け抜けクリップで流している）");
            Assert.That(overrun, Is.GreaterThan(5), "塁を通ってから駆け抜けクリップへ移っていない");
            Assert.That(slipMin, Is.GreaterThan(0.87f), $"足が地面に対して空回りしている（滑りの比 {slipMin:F2}）");
            Assert.That(slipMax, Is.LessThan(1.15f), $"足より体が速く進んでいる（滑りの比 {slipMax:F2}）");
        }

        private static KokoSim.Engine.Match.Timeline.Playback.PlaybackPlay RunThroughPlay(double fieldedAt, double release)
        {
            var fieldPt = new KokoSim.Engine.Match.Timeline.TimelinePoint(-18.5, 71.7);
            var lf = KokoSim.Engine.Match.Field.FieldPosition.LeftField;
            var tl = new KokoSim.Engine.Match.Timeline.PlayTimeline
            {
                Ball = new List<KokoSim.Engine.Match.Timeline.BallSegment>
                {
                    new KokoSim.Engine.Match.Timeline.BallSegment
                    {
                        Kind = KokoSim.Engine.Match.Timeline.BallSegmentKind.Roll, T0 = 0.85, T1 = fieldedAt,
                        From = new KokoSim.Engine.Match.Timeline.TimelinePoint(0, 0), To = fieldPt,
                    },
                },
                Moves = new List<KokoSim.Engine.Match.Timeline.FielderMove>
                {
                    new KokoSim.Engine.Match.Timeline.FielderMove
                    {
                        Role = lf, T0 = 1.2, T1 = fieldedAt, To = fieldPt,
                        Task = KokoSim.Engine.Match.Timeline.FielderTask.FieldBall,
                    },
                },
                FielderStarts = new Dictionary<KokoSim.Engine.Match.Field.FieldPosition, KokoSim.Engine.Match.Timeline.TimelinePoint>
                {
                    [lf] = new KokoSim.Engine.Match.Timeline.TimelinePoint(-28, 80),
                },
                Result = "テスト", Duration = 8.0, ResolvedAt = 8.0,
            };
            var rt = KokoSim.Engine.Match.Timeline.TimelineBuilder.ApplyOutfieldRunThrough(tl, lf, fieldPt, fieldedAt, release);
            Assert.That(rt.HasValue, "前提: 走りながら掬う移動が付く");
            var ball = new List<KokoSim.Engine.Match.Timeline.BallSegment>(tl.Ball)
            {
                new KokoSim.Engine.Match.Timeline.BallSegment
                {
                    Kind = KokoSim.Engine.Match.Timeline.BallSegmentKind.Throw, T0 = release, T1 = release + 2.0,
                    From = rt.Value.ThrowFrom, To = new KokoSim.Engine.Match.Timeline.TimelinePoint(0, 38.8),
                    FromRole = lf, ToRole = KokoSim.Engine.Match.Field.FieldPosition.Shortstop,
                },
            };
            tl = rt.Value.Timeline with { Ball = ball };
            return KokoSim.Engine.Match.Timeline.Playback.PlayTimelineAdapter.ToPlaybackPlay(tl);
        }
    }
}
