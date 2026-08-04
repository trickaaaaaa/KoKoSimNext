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
    /// （issue #393 技術メモ）。向きは適用時に１回だけ実測して焼き付ける方式で、正面は視線でなく
    /// **体の実測**（左右上腕ジョイントの水平軸から。issue #453: 視線基準だとセットポジションの投手で
    /// 背面が横腹へ回る）＝**背面へ焼く**ので実測した正面向きを180°反転して使う
    /// （背番号の面を視線に固定しない, issue #393）。
    /// </summary>
    public sealed partial class DuelStageRig
    {
        private GameObject _pitcherNumberGo, _catcherNumberGo, _batterNumberGo;
        private Mesh _numberQuadMesh;
        private readonly Dictionary<int, Texture2D> _numberTextures = new();
        private readonly List<Texture2D> _numberTexturesOwned = new();

        private const float NumberQuadWidthM = 0.16f;
        private const float NumberQuadHeightM = 0.11f;
        // 胸ボーン原点から背面デカールまでの奥行き[m]。**素体と役（ポーズ）で背中の深さが違う**ので
        // 素体別×役別に持つ（issue #453: U2 だけで校正した一律 0.105 は購入素体では背中の内側＝
        // Zテストで1画素も描かれない。夜間 worktree には購入素体が無く、検証がサイレントに U2 で通った。
        // さらに実測すると U2 でも打席頭のセット/構えポーズでは 0.105 は3役とも内側だった）。
        // 値は BackNumberDiagForCapture の実測（打席頭の既定ポーズ・体正面軸コンテナでの数字域の
        // 背面最深[m]。2026-08-04）＋約1.5cmのクリアランス:
        //   購入素体: 投手0.105 / 捕手0.193(上衣。尻は0.270で数字域下端に入る) / 打者0.113(上衣。尻0.173)
        //   U2素体:   投手0.111 / 捕手0.273(単一メッシュ=尻込み) / 打者0.175(単一メッシュ=尻込み)
        // 捕手・打者は前傾で尻が上衣より深いが、尻まで避けると浮きすぎるので**上衣基準**とし、
        // 水平真後ろから見た数字下端の欠けは許容する（そもそも既定構図で捕手の背は映らない）。
        // U2 は単一メッシュで上衣だけ分離できないぶんを絵で確認して購入素体より少し厚めに置く。
        private const float NumberBackPitcherVendorM = 0.120f;
        private const float NumberBackCatcherVendorM = 0.205f;
        private const float NumberBackBatterVendorM = 0.130f;
        private const float NumberBackPitcherU2M = 0.125f;
        private const float NumberBackCatcherU2M = 0.205f;
        private const float NumberBackBatterU2M = 0.140f;
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
            ApplyBackNumber(ref _pitcherNumberGo, _pitcher, pitcherNumber, home,
                NumberBackPitcherVendorM, NumberBackPitcherU2M);
            ApplyBackNumber(ref _catcherNumberGo, _catcher, catcherNumber, mound,
                NumberBackCatcherVendorM, NumberBackCatcherU2M);
            ApplyBackNumber(ref _batterNumberGo, _batter, batterNumber, mound,
                NumberBackBatterVendorM, NumberBackBatterU2M);
        }

        /// <summary>3人分の背番号デカールを剥がす（打球プレー開始時。守備の絵に焼き番号を持ち込まない）。</summary>
        private void ClearBackNumbers() => SetBackNumbers(null, null, null);

        private void ApplyBackNumber(ref GameObject slot, Actor actor, int? number, Vector3 gazeLocal,
            float vendorOffsetM, float u2OffsetM)
        {
            if (slot != null) { Object.DestroyImmediate(slot); slot = null; }
            if (number is not { } n || actor?.Go == null || actor.Animator == null) return;

            var chest = actor.Animator.GetBoneTransform(HumanBodyBones.UpperChest);
            if (chest == null) chest = actor.Animator.GetBoneTransform(HumanBodyBones.Chest);
            if (chest == null) chest = actor.Animator.GetBoneTransform(HumanBodyBones.Spine);
            if (chest == null) return;

            // 正面は視線方向でなく**体の実測**（左右上腕ジョイントの水平軸×上＝胸の正面。issue #453）。
            // 視線で作ると、セットポジションの投手のように体が視線へ正対しないポーズで
            // 背面が横腹の縫い目側へ回り込み、面が体表を斜めに横切って数字が断片化する（実害・寄り実測）。
            // 上腕ジョイント位置は腕を振っても動かない胴のランドマークなのでポーズに追従して安定。
            // 鏡映中はボーンの左右が視覚と逆＝符号も反転する（design-18 §3.4 の鏡映親方式）。
            // 背中に貼るので実測正面を180反転し、コンテナのローカル-Zに置く（GearContainer は+Zを正面に焼く規約）。
            var lUp = actor.Animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            var rUp = actor.Animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
            var d = Vector3.zero;
            if (lUp != null && rUp != null)
            {
                var lateral = rUp.position - lUp.position;
                lateral.y = 0f;
                d = Vector3.Cross(lateral, Vector3.up);   // 左→右肩軸×上＝正面（水平）
                if (actor.Mirrored) d = -d;
            }
            if (d.sqrMagnitude <= 1e-6f)
            {
                // フォールバック: 従来どおり視線ターゲットへの水平方向（肩軸が取れない素体向け）
                d = _root.transform.TransformPoint(gazeLocal) - chest.position;
                d.y = 0f;
            }
            var faceRot = Quaternion.LookRotation(d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.forward);
            var container = GearContainer(chest, "gear-back-number", faceRot);

            var go = new GameObject("back-number-" + n);
            go.transform.SetParent(container.transform, false);
            var backOffset = actor.VendorBody ? vendorOffsetM : u2OffsetM;
            go.transform.localPosition = new Vector3(0f, NumberHeightOffsetM, -backOffset);
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

        // ── 撮影ハーネス用（issue #453）。実機配線そのままで奥行きを実測する ──

        /// <summary>撮影ハーネス用: 3役ともデカールのコンテナが立っているか（打席頭の適用待ちに使う）。</summary>
        internal bool HasBackNumbersForCapture =>
            _pitcherNumberGo != null && _catcherNumberGo != null && _batterNumberGo != null;

        /// <summary>撮影ハーネス用: 役ごとのデカールコンテナ（"pitcher"/"catcher"/"batter"。無ければ null）。
        /// コンテナの +Z が体の正面（視線方向）、デカールは -Z 側＝寄りカメラは -Z 側から狙う。</summary>
        internal Transform BackNumberContainerForCapture(string role) => role switch
        {
            "pitcher" => _pitcherNumberGo != null ? _pitcherNumberGo.transform : null,
            "catcher" => _catcherNumberGo != null ? _catcherNumberGo.transform : null,
            "batter" => _batterNumberGo != null ? _batterNumberGo.transform : null,
            _ => null,
        };

        /// <summary>
        /// 【診断用】背番号デカールの奥行きを実測する（issue #453）。役ごとに、体のスキンメッシュを
        /// 現ポーズでベイクし、**数字が実際に占める領域**（グリフ＋縁取り≒|x|≦6cm・y=高さ±4.5cm。
        /// クワッド全面で測ると透明マージンの外周が腕・尻など別部位を拾って過大になる=2026-08-04実測）の
        /// 背面最深点（コンテナ -Z 方向の最大到達）をメッシュ別に測り、現在のデカール奥行きと並べて返す。
        /// オフセットの正典値はこの実測から決める（目測でスクショを追い込まない。design-18 §3.3）。
        /// GearContainer はスケールをワールドへ正規化済みなので値はそのまま実寸[m]。
        /// 全高はベイク座標系の検算（TargetHeight とかけ離れていたら BakeMesh の座標解釈を疑う）。
        /// 注意: ワールド変換が位置+回転だけ＝**鏡映中（負スケール）の素体は正しく測れない**。
        /// 右投げ・右打ちの打席頭で測ること。
        /// </summary>
        internal string BackNumberDiagForCapture()
        {
            var sb = new System.Text.StringBuilder();
            const float digitHalfW = 0.06f;    // 2桁グリフ＋縁取りの実幅（12px/15px × 0.16m ≒ 0.128m）の約半分
            const float digitHalfH = 0.045f;   // グリフ高＋縁取り（9px/9px × 0.11m）の約半分。少し内側で測る
            foreach (var (a, name) in new[] { (_pitcher, "pitcher"), (_catcher, "catcher"), (_batter, "batter") })
            {
                var container = BackNumberContainerForCapture(name);
                if (a?.Animator == null || container == null)
                {
                    sb.AppendLine(name + "\tno-actor-or-container");
                    continue;
                }
                var quad = container.childCount > 0 ? container.GetChild(0) : null;
                sb.Append(name)
                    .Append("\t素体=").Append(a.VendorBody ? "購入" : "U2")
                    .Append("\tデカール奥行き=").Append(quad != null ? (-quad.localPosition.z).ToString("F3") + "m" : "-");
                float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
                var baked = new Mesh();
                foreach (var smr in a.Go.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    if (!smr.gameObject.activeInHierarchy || !smr.enabled) continue;
                    baked.Clear();
                    smr.BakeMesh(baked, true);   // useScale=true＝スケール込み。ワールドへは位置+回転だけ
                    var verts = baked.vertices;
                    float maxDepth = float.NegativeInfinity;
                    int hit = 0;
                    for (var i = 0; i < verts.Length; i++)
                    {
                        var w = smr.transform.position + smr.transform.rotation * verts[i];
                        if (w.y < minY) minY = w.y;
                        if (w.y > maxY) maxY = w.y;
                        var lp = container.InverseTransformPoint(w);
                        if (lp.z >= 0f) continue;                          // 体の前面側は対象外
                        if (Mathf.Abs(lp.x) > digitHalfW) continue;
                        if (Mathf.Abs(lp.y - NumberHeightOffsetM) > digitHalfH) continue;
                        hit++;
                        if (-lp.z > maxDepth) maxDepth = -lp.z;
                    }
                    if (hit > 0) sb.Append("\t").Append(smr.name).Append("=").Append(maxDepth.ToString("F3")).Append("m/").Append(hit);
                }
                Object.DestroyImmediate(baked);
                sb.Append("\t全高=").Append((maxY - minY).ToString("F2")).Append("m").AppendLine();
            }
            return sb.ToString();
        }
    }
}
