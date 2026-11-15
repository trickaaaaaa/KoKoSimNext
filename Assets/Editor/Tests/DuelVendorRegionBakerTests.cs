using NUnit.Framework;
using UnityEngine;
using KokoSim.EditorTools;
using Builder = KokoSim.Unity.Match.DuelUniformMeshBuilder;
using Region = KokoSim.Unity.Match.DuelUniformMeshBuilder.Region;

namespace KokoSim.EditorTests
{
    /// <summary>
    /// 購入素体の領域ベイク（<see cref="DuelVendorRegionBaker"/>）の坊主頭の髪（issue #916）。
    /// 購入素体は gitignore＝クローン先に無いので、合成のメッシュとテクスチャで規則だけを固定する。
    /// </summary>
    public class DuelVendorRegionBakerTests
    {
        private static readonly Color Hair = new(0.30f, 0.27f, 0.24f);   // 描かれた短髪（輝度≈0.27）
        private static readonly Color Blur = new(0.45f, 0.40f, 0.36f);   // 生え際のぼかし（輝度≈0.40）
        private static readonly Color Skin = new(0.68f, 0.53f, 0.43f);   // 肌（輝度≈0.56）
        private static readonly Color Lip = new(0.45f, 0.25f, 0.22f);    // 唇（暗いが赤い）

        [Test]
        public void HairRegion_DecodesToNine_AboveOne()
        {
            // 刻み幅 /8 のまま 1 を超える側へ伸ばす（design-18 §3.5.1）。シェーダは int(UV1.x*8) で引く
            float u = Builder.RegionUv((int)Region.Hair);
            Assert.That(u, Is.GreaterThan(1f));
            Assert.That((int)(u * 8f), Is.EqualTo(9));
            Assert.That(Builder.RegionCount, Is.EqualTo(10));
        }

        [Test]
        public void HairWeight_FollowsLuma_AndBlursTheHairline()
        {
            float hair = DuelVendorRegionBaker.HairWeight(Hair, 0.5f, 0.6f, 0.9f);
            float blur = DuelVendorRegionBaker.HairWeight(Blur, 0.5f, 0.6f, 0.9f);
            float skin = DuelVendorRegionBaker.HairWeight(Skin, 0.5f, 0.6f, 0.9f);
            Assert.That(hair, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(skin, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(blur, Is.GreaterThan(0.05f).And.LessThan(0.95f), "生え際はぼかす（監督決定）");
        }

        [Test]
        public void HairWeight_ExcludesMouthIsland_Lips_AndBrows()
        {
            // 歯・舌の島
            Assert.That(DuelVendorRegionBaker.HairWeight(Hair, 0.9f, 0.6f, 0.9f), Is.EqualTo(0f));
            // 唇・眼窩（暗いが赤い）は顔の前でだけ除く
            Assert.That(DuelVendorRegionBaker.HairWeight(Lip, 0.5f, 0.3f, 0.5f), Is.EqualTo(0f));
            // 後頭部の赤茶の髪（赤/緑1.3超）は髪のまま＝襟足の正中に肌の筋を通さない
            Assert.That(DuelVendorRegionBaker.HairWeight(Lip, 0.5f, 0.9f, 0.5f), Is.EqualTo(1f).Within(1e-4f));
            // 眉（前方・額の生え際より低い）
            Assert.That(DuelVendorRegionBaker.HairWeight(Hair, 0.5f, 0.1f, 0.6f), Is.EqualTo(0f));
            // 同じ高さでも前方でなければ髪（耳上・後頭部）
            Assert.That(DuelVendorRegionBaker.HairWeight(Hair, 0.5f, 0.6f, 0.6f), Is.EqualTo(1f).Within(1e-4f));
            // 前方でも額の生え際より上なら髪
            Assert.That(DuelVendorRegionBaker.HairWeight(Hair, 0.5f, 0.1f, 0.8f), Is.EqualTo(1f).Within(1e-4f));
        }

        [Test]
        public void ClassifyHead_PutsAllHeadVerticesInHair_AndKeepsHandsSkin()
        {
            // テクスチャ: u<0.3 髪 / 0.3〜0.6 生え際 / u≥0.6 肌（輝度だけで分かれる＝実物の顔テクスチャと同じ）
            var tex = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < 64; x++)
                {
                    float u = (x + 0.5f) / 64f;
                    tex.SetPixel(x, y, u < 0.3f ? Hair : u < 0.6f ? Blur : Skin);
                }
            tex.Apply();

            // 頭部（サブメッシュ0）: 後頭部寄り（y大）・高い所に、u を髪→肌へ並べた溶接の帯
            // 手（サブメッシュ1）: 別の頂点
            var verts = new[]
            {
                new Vector3(0f, 1f, 1.0f), new Vector3(0f, 1f, 0.9f), new Vector3(0.1f, 0f, 0.0f), new Vector3(0.1f, 1f, 1.0f),
                new Vector3(2f, 0f, 0f), new Vector3(2.1f, 0f, 0f), new Vector3(2f, 0.1f, 0f),
            };
            var uvs = new[]
            {
                new Vector2(0.10f, 0.5f), new Vector2(0.45f, 0.5f), new Vector2(0.90f, 0.5f), new Vector2(0.10f, 0.9f),
                new Vector2(0.10f, 0.5f), new Vector2(0.10f, 0.5f), new Vector2(0.10f, 0.5f),
            };
            var mesh = new Mesh { vertices = verts, uv = uvs, subMeshCount = 2 };
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            mesh.SetTriangles(new[] { 4, 5, 6 }, 1);

            var region = DuelVendorRegionBaker.ClassifyHead(mesh, 0, tex, out var w, out _);

            for (int i = 0; i < 4; i++)
                Assert.That(region[i], Is.EqualTo((int)Region.Hair), "頭部は全部 Hair＝生え際で領域IDが補間されない");
            for (int i = 4; i < 7; i++)
            {
                Assert.That(region[i], Is.EqualTo((int)Region.Skin), "同居している手は肌のまま");
                Assert.That(w[i], Is.EqualTo(0f));
            }
            Assert.That(w[0], Is.EqualTo(1f).Within(1e-3f), "髪");
            Assert.That(w[1], Is.GreaterThan(0.05f).And.LessThan(0.95f), "生え際はぼかす");
            Assert.That(w[2], Is.EqualTo(0f).Within(1e-3f), "肌");
            Assert.That(w[3], Is.EqualTo(0f), "歯・舌の島は髪にしない");

            Object.DestroyImmediate(mesh);
            Object.DestroyImmediate(tex);
        }

        [Test]
        public void ClassifyHead_VertexSharedWithHands_FallsToSkin()
        {
            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) tex.SetPixel(x, y, Hair);
            tex.Apply();
            var mesh = new Mesh
            {
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.one },
                uv = new[] { Vector2.one * 0.5f, Vector2.one * 0.5f, Vector2.one * 0.5f, Vector2.one * 0.5f },
                subMeshCount = 2,
            };
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            mesh.SetTriangles(new[] { 2, 1, 3 }, 1);

            var region = DuelVendorRegionBaker.ClassifyHead(mesh, 0, tex, out var w, out _);

            Assert.That(region[0], Is.EqualTo((int)Region.Hair));
            Assert.That(region[1], Is.EqualTo((int)Region.Skin), "手と共有＝手に髪の色を乗せない");
            Assert.That(region[2], Is.EqualTo((int)Region.Skin));
            Assert.That(w[1], Is.EqualTo(0f));

            Object.DestroyImmediate(mesh);
            Object.DestroyImmediate(tex);
        }

        // ---- のっぺらぼう（FlattenFace） ----

        /// <summary>なめらかな顔面（左右対称の放物面）。</summary>
        private static float SmoothFace(float x, float z) => -0.10f + 4f * x * x + 1.5f * z * z;

        [Test]
        public void FitFaceSurface_TrimsNoseAndSockets_RecoversSmoothFace()
        {
            // 格子の顔面に、前へ出る「鼻」と奥へ凹む「眼窩」を足す。刈り込みで両方が外れ、元の面が出ること
            var pts = new System.Collections.Generic.List<Vector3>();
            for (int i = -6; i <= 6; i++)
                for (int k = -8; k <= 10; k++)
                {
                    float x = i * 0.01f, z = k * 0.01f;
                    float y = SmoothFace(x, z);
                    if (Mathf.Abs(x) < 0.012f && z > -0.02f && z < 0.03f) y -= 0.02f;            // 鼻（前＝-y）
                    if (Mathf.Abs(Mathf.Abs(x) - 0.03f) < 0.011f && Mathf.Abs(z - 0.05f) < 0.011f) y += 0.008f;   // 眼窩
                    pts.Add(new Vector3(x, y, z));
                }
            var coef = DuelVendorRegionBaker.FitFaceSurface(pts, 0.06f, 0.08f, 0.0025f, 4, out int kept);

            Assert.That(coef, Is.Not.Null);
            Assert.That(kept, Is.LessThan(pts.Count), "鼻・眼窩は外れ値として刈られる");
            foreach (var (x, z) in new[] { (0f, 0f), (0.03f, 0.05f), (-0.04f, -0.06f), (0.05f, 0.09f) })
                Assert.That(DuelVendorRegionBaker.EvalFace(coef, x / 0.06f, z / 0.08f),
                    Is.EqualTo(SmoothFace(x, z)).Within(0.001f), $"({x},{z}) で目鼻の跡が残っている");
        }

        [Test]
        public void FaceMask_FullInsideWindow_ZeroOutside_Continuous()
        {
            Assert.That(DuelVendorRegionBaker.FaceMask(0f, 0f), Is.EqualTo(1f));
            Assert.That(DuelVendorRegionBaker.FaceMask(DuelVendorRegionBaker.FaceFlatHalfWidth, 0f), Is.EqualTo(1f));
            Assert.That(DuelVendorRegionBaker.FaceMask(DuelVendorRegionBaker.FaceFadeHalfWidth, 0f), Is.EqualTo(0f));
            Assert.That(DuelVendorRegionBaker.FaceMask(0f, DuelVendorRegionBaker.FaceFadeTop), Is.EqualTo(0f));
            Assert.That(DuelVendorRegionBaker.FaceMask(0f, DuelVendorRegionBaker.FaceFadeBottom), Is.EqualTo(0f));
            // 縁で段を作らない: 細かく刻んで隣同士の差が小さい
            float prev = 1f;
            for (float dx = 0f; dx <= 0.08f; dx += 0.0005f)
            {
                float w = DuelVendorRegionBaker.FaceMask(dx, 0f);
                Assert.That(Mathf.Abs(w - prev), Is.LessThan(0.06f));
                prev = w;
            }
        }

        [Test]
        public void FlattenFace_RemovesNose_KeepsBackOfHead_AndDropsBlendShapes()
        {
            // 前面（-y）に鼻のある格子＋後頭部の点。鼻先は前面の最前点＝窓の基準
            var verts = new System.Collections.Generic.List<Vector3>();
            for (int i = -6; i <= 6; i++)
                for (int k = -8; k <= 10; k++)
                {
                    float x = i * 0.01f, z = k * 0.01f;
                    float y = SmoothFace(x, z);
                    if (i == 0 && k == 0) y -= 0.02f;   // 鼻先
                    verts.Add(new Vector3(x, y, 1.6f + z));
                }
            int back = verts.Count;
            verts.Add(new Vector3(0f, 0.10f, 1.6f));   // 後頭部（同じ x,z）
            int nose = 6 * 19 + 8;
            var tris = new System.Collections.Generic.List<int>();
            for (int i = 0; i < 12; i++)
                for (int k = 0; k < 18; k++)
                {
                    int a = i * 19 + k;
                    tris.AddRange(new[] { a, a + 1, a + 19, a + 1, a + 20, a + 19 });
                }
            tris.AddRange(new[] { back, 0, 1 });
            var normals = new Vector3[verts.Count];
            for (int i = 0; i < normals.Length; i++) normals[i] = Vector3.down;
            normals[back] = Vector3.up;
            var mesh = new Mesh { vertices = verts.ToArray(), normals = normals, uv = new Vector2[verts.Count] };
            mesh.SetTriangles(tris, 0);
            var delta = new Vector3[verts.Count];
            mesh.AddBlendShapeFrame("Smile", 100f, delta, null, null);

            DuelVendorRegionBaker.FlattenFace(mesh, 0);

            var v = mesh.vertices;
            Assert.That(v[nose].y, Is.EqualTo(SmoothFace(0f, 0f)).Within(0.002f), "鼻が面へ均される");
            Assert.That(v[back], Is.EqualTo(new Vector3(0f, 0.10f, 1.6f)), "後頭部は動かさない");
            Assert.That(mesh.normals[nose].y, Is.LessThan(-0.99f), "鼻先の法線は面の法線（正面）");
            Assert.That(mesh.blendShapeCount, Is.EqualTo(0), "表情で目鼻口が戻らない");
            Object.DestroyImmediate(mesh);
        }
    }
}
