using System.Collections.Generic;
using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 打席中（対決の寄り）の背番号デカール（issue #393）。投手・捕手・打者の背中に背番号を焼く。
    /// 守備の7人（捕手を除く）には焼かない＝呼び出し側が守備位置別に配らない。
    ///
    /// 数字は購入フォントでなくランタイム生成の5x7ドットマトリクス（電光掲示板と同じ発光風の意匠,
    /// design-16）。UV1 が領域ID/縞座標で満杯（DuelStageProxyLambert）のため、既存のユニフォーム
    /// メッシュへ載せる余地が無く、別メッシュ（薄板のデカール）を背骨/胸ボーンへ付ける
    /// （issue #393 技術メモ）。向きは AttachGear と同じ「視線ターゲットへの水平方向」を１回だけ実測して
    /// 焼き付ける方式（投手は視線が本塁、打者/捕手は視線がマウンド）＝視線に正対させる用途とは逆に、
    /// **背面へ焼く**ので実測した正面向きを180°反転して使う（背番号の面を視線に固定しない, issue #393）。
    /// </summary>
    public sealed partial class DuelStageRig
    {
        private GameObject _pitcherNumberGo, _catcherNumberGo, _batterNumberGo;
        private Mesh _numberQuadMesh;
        private readonly Dictionary<int, Texture2D> _numberTextures = new();
        private readonly List<Texture2D> _numberTexturesOwned = new();

        private const float NumberQuadWidthM = 0.16f;
        private const float NumberQuadHeightM = 0.11f;
        // 胸ボーン原点から背面デカールまでの奥行き[m]。ChestMeshBase.z(+0.085=体前)の符号違いに近い値
        // （背は胸よりわずかに厚みがある分だけ絶対値を大きく取る）。実測はスクショで追い込む。
        private const float NumberBackOffsetM = 0.105f;
        private const float NumberHeightOffsetM = -0.06f;   // 胸ボーン原点からの高さ（実測で首元寄りだったため下げる）

        /// <summary>
        /// 打席中の背番号デカールを張り替える。3人とも毎回作り直す（人数が少ないので使い回さず、
        /// 交代直後の番号の取りこぼしを構造的に防ぐ）。null を渡した役は無地のまま（守備の7人用）。
        /// 打球プレー中（<see cref="FieldPlayActive"/>）は呼ばない＝<see cref="BeginFieldPlay"/> が
        /// 3人とも <c>SetBackNumbers(null,null,null)</c> 相当で剥がす。
        /// </summary>
        public void SetBackNumbers(int? pitcherNumber, int? catcherNumber, int? batterNumber)
        {
            if (!Available) return;
            var home = Vector3.zero;
            var mound = new Vector3(0f, MoundHeight, MoundDistance);
            ApplyBackNumber(ref _pitcherNumberGo, _pitcher, pitcherNumber, home);
            ApplyBackNumber(ref _catcherNumberGo, _catcher, catcherNumber, mound);
            ApplyBackNumber(ref _batterNumberGo, _batter, batterNumber, mound);
        }

        /// <summary>3人分の背番号デカールを剥がす（打球プレー開始時。守備の絵に焼き番号を持ち込まない）。</summary>
        private void ClearBackNumbers() => SetBackNumbers(null, null, null);

        private void ApplyBackNumber(ref GameObject slot, Actor actor, int? number, Vector3 gazeLocal)
        {
            if (slot != null) { Object.DestroyImmediate(slot); slot = null; }
            if (number is not { } n || actor?.Go == null || actor.Animator == null) return;

            var chest = actor.Animator.GetBoneTransform(HumanBodyBones.UpperChest);
            if (chest == null) chest = actor.Animator.GetBoneTransform(HumanBodyBones.Chest);
            if (chest == null) chest = actor.Animator.GetBoneTransform(HumanBodyBones.Spine);
            if (chest == null) return;

            // AttachGear の GazeRot と同じ実測（視線ターゲットへの水平方向＝正面）。背中に貼るので
            // 90ではなく180反転させ、コンテナのローカル-Zに置く（GearContainer は+Zを正面に焼く規約）。
            var target = _root.transform.TransformPoint(gazeLocal);
            var d = target - chest.position;
            d.y = 0f;
            var faceRot = Quaternion.LookRotation(d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.forward);
            var container = GearContainer(chest, "gear-back-number", faceRot);

            var go = new GameObject("back-number-" + n);
            go.transform.SetParent(container.transform, false);
            go.transform.localPosition = new Vector3(0f, NumberHeightOffsetM, -NumberBackOffsetM);
            // コンテナの-Z（背面）から見て正しく読める向きへ180反転（面を視線に固定しない＝体に貼ったまま）。
            go.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            go.transform.localScale = Vector3.one;
            go.AddComponent<MeshFilter>().sharedMesh = NumberQuadMesh();
            var mat = new Material(Shader.Find("KokoSim/DuelStageDecalUnlit")) { hideFlags = HideFlags.HideAndDontSave };
            mat.SetTexture("_MainTex", NumberTexture(n));
            _materials.Add(mat);
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            go.layer = actor.Go.layer;

            slot = container;
        }

        private Mesh NumberQuadMesh()
        {
            if (_numberQuadMesh != null) return _numberQuadMesh;
            float hw = NumberQuadWidthM * 0.5f, hh = NumberQuadHeightM * 0.5f;
            var mesh = new Mesh { name = "back-number-quad" };
            mesh.vertices = new[]
            {
                new Vector3(-hw, -hh, 0f), new Vector3(hw, -hh, 0f),
                new Vector3(hw, hh, 0f), new Vector3(-hw, hh, 0f),
            };
            mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            mesh.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();
            _numberQuadMesh = mesh;
            _meshes.Add(mesh);
            return mesh;
        }

        private Texture2D NumberTexture(int number)
        {
            if (_numberTextures.TryGetValue(number, out var cached) && cached != null) return cached;
            var tex = BuildNumberTexture(number);
            _numberTextures[number] = tex;
            _numberTexturesOwned.Add(tex);
            return tex;
        }

        // ── 5x7ドットマトリクス数字（電光掲示板の意匠を踏襲, design-16）。行は上から。 ──
        private static readonly string[][] DigitGlyphs5X7 =
        {
            new[] { "01110", "10001", "10011", "10101", "11001", "10001", "01110" }, // 0
            new[] { "00100", "01100", "00100", "00100", "00100", "00100", "01110" }, // 1
            new[] { "01110", "10001", "00001", "00010", "00100", "01000", "11111" }, // 2
            new[] { "11111", "00010", "00100", "00010", "00001", "10001", "01110" }, // 3
            new[] { "00010", "00110", "01010", "10010", "11111", "00010", "00010" }, // 4
            new[] { "11111", "10000", "11110", "00001", "00001", "10001", "01110" }, // 5
            new[] { "00110", "01000", "10000", "11110", "10001", "10001", "01110" }, // 6
            new[] { "11111", "00001", "00010", "00100", "01000", "01000", "01000" }, // 7
            new[] { "01110", "10001", "10001", "01110", "10001", "10001", "01110" }, // 8
            new[] { "01110", "10001", "10001", "01111", "00001", "00010", "01100" }, // 9
        };

        private const int GlyphW = 5, GlyphH = 7, GlyphGap = 1, TexMarginX = 2, TexMarginY = 1;

        /// <summary>
        /// 数字テクスチャを都度生成する純関数寄りのヘルパ（Unity依存の Texture2D のみ純関数から外れる）。
        /// 白地の数字＋濃色の縁取り（背景は透明）＝ユニフォームの地色が薄くても濃くても読める
        /// （実況テロップと同じ発想でコントラストを縁取りに持たせる, design-16）。
        /// </summary>
        private static Texture2D BuildNumberTexture(int number)
        {
            number = Mathf.Clamp(number, 0, 99);
            var digits = number >= 10 ? new[] { number / 10, number % 10 } : new[] { number };
            int texW = TexMarginX * 2 + GlyphW * digits.Length + GlyphGap * (digits.Length - 1);
            int texH = TexMarginY * 2 + GlyphH;

            var on = new bool[texW, texH];
            for (var d = 0; d < digits.Length; d++)
            {
                var x0 = TexMarginX + d * (GlyphW + GlyphGap);
                var glyph = DigitGlyphs5X7[digits[d]];
                for (var row = 0; row < GlyphH; row++)
                for (var col = 0; col < GlyphW; col++)
                {
                    if (glyph[row][col] != '1') continue;
                    // テクスチャ座標は下端が y=0。グリフの行0は上端なので縦を反転して書く。
                    on[x0 + col, TexMarginY + (GlyphH - 1 - row)] = true;
                }
            }

            var pixels = new Color32[texW * texH];
            var fg = new Color32(255, 255, 255, 255);
            var outline = new Color32(26, 26, 26, 255);
            var clear = new Color32(0, 0, 0, 0);
            for (var y = 0; y < texH; y++)
            for (var x = 0; x < texW; x++)
            {
                pixels[y * texW + x] = on[x, y] ? fg
                    : HasForegroundNeighbor(on, x, y, texW, texH) ? outline
                    : clear;
            }

            var tex = new Texture2D(texW, texH, TextureFormat.RGBA32, mipChain: false, linear: false)
            {
                name = "back-number-tex-" + number,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            tex.SetPixels32(pixels);
            tex.Apply(false, true);
            return tex;
        }

        private static bool HasForegroundNeighbor(bool[,] on, int x, int y, int w, int h)
        {
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                var nx = x + dx;
                var ny = y + dy;
                if (nx >= 0 && nx < w && ny >= 0 && ny < h && on[nx, ny]) return true;
            }
            return false;
        }
    }
}
