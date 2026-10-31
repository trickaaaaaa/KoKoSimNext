using System.Collections.Generic;
using System.Globalization;
using System.IO;
using KokoSim.Unity.Match;
using UnityEngine;

namespace KokoSim.UnityEditorTools
{
    /// <summary>
    /// 観客を座席に撒いて <see cref="DuelStadiumMesh"/> の部位（<c>crowd_0..2</c>）として焼く（issue #705）。
    ///
    /// <para>座れる場所は <c>tools/stadium-gen.py</c> が書く <c>data/stadiums/&lt;name&gt;.seats.tsv</c>
    /// （座席の列の一覧）から取る＝スタンドの寸法をここで持ち直さない。ここが決めるのは
    /// 人の形・服の色・どの席が埋まるかだけ。</para>
    ///
    /// <para>決定論: 席ごとの抽選は (列の番号, 席の番号) の整数ハッシュ（splitmix64）だけで決める。
    /// <c>System.Random</c> や <c>string.GetHashCode</c> は使わない＝焼くたびに同じ客の入り。</para>
    ///
    /// <para>形は「胴＋頭」の人形（2026-10-30 監督決定。箱1つの色ドット案は寄りの絵で積み木に見えて不採用）。
    /// 正面は肌一色ののっぺらぼう（設計書08）、頭の天面だけ髪か帽子。</para>
    ///
    /// <para>密度は3層に分けて焼く。<c>crowd_0</c> だけ＝まばら、0+1＝半分、0+1+2＝満員。
    /// 実行時は層の表示を切り替えるだけ（作り直さない）。</para>
    /// </summary>
    public static class DuelCrowdBaker
    {
        public const int Layers = 3;
        public const string PartPrefix = "crowd_";

        /// <summary>席の間隔[m]（列に沿って）。</summary>
        private const float SeatPitch = 0.52f;
        /// <summary>満員でも空けておく割合（通路際・ばらけ）。</summary>
        private const float AlwaysEmpty = 0.08f;

        // 服の色（sRGB）。重みは「白・紺・灰が多く、原色は少し」。地方球場の夏の客席の見た目で選ぶ
        private static readonly (Color c, float w)[] Shirts =
        {
            (Hex(0xE2E2DC), 0.26f), (Hex(0x2C3A5C), 0.15f), (Hex(0x8A8A86), 0.12f),
            (Hex(0x2A2A2C), 0.08f), (Hex(0x7FA6CC), 0.10f), (Hex(0xB8483A), 0.08f),
            (Hex(0xD9B23A), 0.07f), (Hex(0x5E8C5A), 0.06f), (Hex(0xC98AA0), 0.04f),
            (Hex(0xE08A3A), 0.04f),
        };
        private static readonly Color Skin = Hex(0xC9956E);
        private static readonly Color Hair = Hex(0x2A2522);
        private static readonly (Color c, float w)[] Caps =
        {
            (Hex(0xE2E2DC), 0.5f), (Hex(0x2C3A5C), 0.3f), (Hex(0xB8483A), 0.2f),
        };
        private const float CapShare = 0.28f;

        private struct Row
        {
            public string Group;
            public float T0, T1, R0, R1, Y, Tread, BackIn;
            public int RowIndex;
        }

        /// <summary>座席TSVを読んで、層ごとの部位を返す。TSV が無ければ空。</summary>
        public static List<DuelStadiumMesh.Part> Build(string seatsTsvPath, out int people, out int triangles)
        {
            people = 0;
            triangles = 0;
            var parts = new List<DuelStadiumMesh.Part>();
            if (!File.Exists(seatsTsvPath)) return parts;

            var rows = Read(seatsTsvPath);
            var builders = new MeshBuilder[Layers];
            for (var i = 0; i < Layers; i++) builders[i] = new MeshBuilder();

            for (var ri = 0; ri < rows.Count; ri++)
            {
                var r = rows[ri];
                var dT = Mathf.DeltaAngle(r.T0 * Mathf.Rad2Deg, r.T1 * Mathf.Rad2Deg) * Mathf.Deg2Rad;
                var rFront = 0.5f * (r.R0 + r.R1);
                var len = Mathf.Abs(dT) * (rFront + r.Tread * 0.5f);
                var n = Mathf.FloorToInt(len / SeatPitch);
                if (n <= 0) continue;
                var outfield = r.Group.StartsWith("stand_outfield");
                var backstop = r.Group == "stand_backstop";
                for (var s = 0; s < n; s++)
                {
                    var h = Hash((ulong)ri, (ulong)s);
                    var u = U01(h, 0);
                    if (u < AlwaysEmpty) continue;
                    // 埋まる順: ネット裏 → 内野 → 外野。層の境目はこのスコアの三等分
                    var bias = backstop ? -0.18f : outfield ? 0.22f : 0f;
                    var score = Mathf.Clamp01((u - AlwaysEmpty) / (1f - AlwaysEmpty) + bias);
                    var layer = Mathf.Min(Layers - 1, (int)(score * Layers));

                    var f = (s + 0.5f) / n;
                    var th = r.T0 + dT * f;
                    var rBack = Mathf.Lerp(r.R0, r.R1, f) + r.Tread - r.BackIn;   // 背もたれの前面
                    Place(builders[layer], th, rBack, r.Y, h);
                    people++;
                }
            }

            for (var i = 0; i < Layers; i++)
            {
                var part = builders[i].ToPart(PartPrefix + i);
                if (part == null) continue;
                triangles += part.Mesh.triangles.Length / 3;
                parts.Add(part);
            }
            return parts;
        }

        /// <summary>1人ぶん。th=方位、rBack=背もたれ前面の半径、y=踏面の高さ。顔は本塁側を向く。</summary>
        private static void Place(MeshBuilder b, float th, float rBack, float y, ulong h)
        {
            var radial = new Vector3(Mathf.Sin(th), 0f, Mathf.Cos(th));      // 外向き
            var tangent = new Vector3(Mathf.Cos(th), 0f, -Mathf.Sin(th));
            // ばらけ（体格・前のめり・左右のずれ）。揃いすぎると壁紙に見える
            var jx = (U01(h, 1) - 0.5f) * 0.10f;
            var size = 0.92f + U01(h, 2) * 0.16f;
            var shirt = Pick(Shirts, U01(h, 3));
            var basePos = radial * rBack + tangent * jx;

            // 胴（座った上半身）: 踏面+0.40（座面）〜+0.98
            var torsoH = 0.58f * size;
            var torso = basePos - radial * 0.17f + Vector3.up * (y + 0.40f + torsoH * 0.5f);
            b.Box(torso, radial, tangent, 0.42f * size, torsoH, 0.26f, shirt, shirt);
            // 頭（のっぺらぼう: 正面は肌一色。天面だけ髪か帽子）
            var head = 0.21f * size;
            var hc = basePos - radial * 0.19f + Vector3.up * (y + 0.40f + torsoH + 0.03f + head * 0.5f);
            var top = U01(h, 4) < CapShare ? Pick(Caps, U01(h, 5)) : Hair;
            b.Box(hc, radial, tangent, head, head, head, Skin, top);
        }

        private static List<Row> Read(string path)
        {
            var list = new List<Row>();
            foreach (var line in File.ReadAllLines(path))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var t = line.Split('\t');
                if (t.Length < 9) continue;
                list.Add(new Row
                {
                    Group = t[0], T0 = F(t[1]), T1 = F(t[2]), R0 = F(t[3]), R1 = F(t[4]),
                    Y = F(t[5]), Tread = F(t[6]), RowIndex = int.Parse(t[7], CultureInfo.InvariantCulture),
                    BackIn = F(t[8]),
                });
            }
            return list;
        }

        private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        private static Color Hex(int rgb) =>
            new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);

        private static Color Pick((Color c, float w)[] table, float u)
        {
            var sum = 0f;
            foreach (var e in table) sum += e.w;
            var x = u * sum;
            foreach (var e in table)
            {
                if (x < e.w) return e.c;
                x -= e.w;
            }
            return table[table.Length - 1].c;
        }

        private static ulong Hash(ulong a, ulong b) => Mix(Mix(a * 0x9E3779B97F4A7C15UL + 705UL) ^ (b + 0x632BE59BD9B4E019UL));

        private static ulong Mix(ulong z)
        {
            z += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>同じ席から用途ごとに別の一様乱数を取る（k=用途の番号）。</summary>
        private static float U01(ulong h, int k) => (Mix(h + (ulong)k * 0xD1B54A32D192ED03UL) >> 40) / (float)(1UL << 24);

        /// <summary>色ごとのサブメッシュに三角形を積む。面ごとに法線を割る（平面ランバート）。</summary>
        private sealed class MeshBuilder
        {
            private readonly List<Vector3> _v = new();
            private readonly List<Vector3> _n = new();
            private readonly List<Color> _colors = new();
            private readonly List<List<int>> _subs = new();

            /// <summary>
            /// 本塁側を向いた箱。正面・左右・天面だけ（背面と底は写らないので省く）。
            /// radial=外向き・tangent=横。<paramref name="topColor"/> は天面の色。
            /// </summary>
            public void Box(Vector3 c, Vector3 radial, Vector3 tangent, float w, float hgt, float d, Color body, Color topColor)
            {
                var hx = tangent * (w * 0.5f);
                var hy = Vector3.up * (hgt * 0.5f);
                var hz = radial * (d * 0.5f);
                var front = c - hz;          // 本塁側の面の中心
                Quad(front - hx - hy, front + hx - hy, front + hx + hy, front - hx + hy, -radial, body);
                Quad(c - hx - hz - hy, c - hx + hz - hy, c - hx + hz + hy, c - hx - hz + hy, -tangent, body);
                Quad(c + hx - hz - hy, c + hx + hz - hy, c + hx + hz + hy, c + hx - hz + hy, tangent, body);
                Quad(c - hx - hz + hy, c + hx - hz + hy, c + hx + hz + hy, c - hx + hz + hy, Vector3.up, topColor);
            }

            private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Color color)
            {
                Tri(a, b, c, normal, color);
                Tri(a, c, d, normal, color);
            }

            /// <summary>生成スクリプトの tri() と同じ約束: (b−a)×(c−a) が normal を向くよう巻き順をそろえる。</summary>
            private void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 normal, Color color)
            {
                if (Vector3.Dot(Vector3.Cross(b - a, c - a), normal) < 0f) (b, c) = (c, b);
                var sub = Sub(color);
                var i = _v.Count;
                _v.Add(a); _v.Add(b); _v.Add(c);
                _n.Add(normal); _n.Add(normal); _n.Add(normal);
                sub.Add(i); sub.Add(i + 1); sub.Add(i + 2);
            }

            private List<int> Sub(Color color)
            {
                var k = _colors.IndexOf(color);
                if (k >= 0) return _subs[k];
                _colors.Add(color);
                var l = new List<int>();
                _subs.Add(l);
                return l;
            }

            public DuelStadiumMesh.Part ToPart(string name)
            {
                if (_v.Count == 0) return null;
                var mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                mesh.SetVertices(_v);
                mesh.SetNormals(_n);
                mesh.subMeshCount = _subs.Count;
                for (var i = 0; i < _subs.Count; i++) mesh.SetTriangles(_subs[i], i);
                mesh.RecalculateBounds();
                return new DuelStadiumMesh.Part { Name = name, Mesh = mesh, Colors = _colors.ToArray() };
            }
        }
    }
}
