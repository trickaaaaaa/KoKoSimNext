using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 投手の「動揺」を絵で見せる（issue #1178）。動揺しているあいだ投手の頭上で渦巻きが回り、
    /// 動揺中の毎球、投球動作に入る時に頭の左右から汗の粒が短く飛ぶ。HUD には文字を出さない。
    ///
    /// <para><b>ポスタライズの後に GL で引く</b>（打球の残像・バットの軌跡と同じ。<see cref="DrawRattledFx"/>）。
    /// 世界座標の頭ボーンから組むので、左投手の鏡映親ノード（負スケール）の下でも右投手と同じ式で正しい位置に出る
    /// （素体を触らない＝モーションのクリップ・速さにも触れない。設計書18 §3.4）。</para>
    ///
    /// <para>形は毎球同じで乱数を使わない。大きさは<b>画面の画素で下限</b>を持たせる＝投手はカメラから遠く、
    /// 実寸どおりだと数画素に潰れて渦巻きと読めない（<see cref="MinSwirlRadiusPx"/>）。</para>
    /// </summary>
    public sealed partial class DuelStageRig
    {
        /// <summary>渦巻きの外半径[m]（下限の画素に満たないときはこの比のまま拡大する）。</summary>
        private const float SwirlRadiusM = 0.14f;
        /// <summary>渦巻きの外半径の画素下限（試合ビューのRT上）。ピクセル化の後でも渦巻きと読める大きさ。</summary>
        private const float MinSwirlRadiusPx = 17f;
        /// <summary>頭ボーンから渦巻きの中心までの高さ[m]（帽子の上に浮かせる）。</summary>
        private const float SwirlLiftM = 0.42f;
        private const float SwirlTurns = 1.75f;
        /// <summary>渦巻きが1周する秒数（ゆっくり回す）。</summary>
        private const float SwirlSpinSeconds = 2.2f;
        /// <summary>渦巻きの面をカメラへ向ける度合い（0=真水平／1=画面に正対）。水平のままだと真横から見て線に潰れる。</summary>
        public static float RattledSwirlFaceCamera = 0.55f;
        private const float SwirlFadeSeconds = 0.15f;

        /// <summary>汗が飛ぶ秒数（0.3秒前後）。</summary>
        private const float SweatSeconds = 0.36f;
        private const float SweatDropRadiusM = 0.035f;
        private const int SweatDropsPerSide = 3;

        private static readonly Color RattledInk = new Color(0.137f, 0.141f, 0.165f);   // パレットの黒 #23242A
        private static readonly Color SwirlPaper = new Color(0.98f, 0.95f, 0.84f);
        private static readonly Color SweatBlue = new Color(0.66f, 0.88f, 1f);

        private bool _pitcherRattled;
        private float _swirlAlpha;       // 0..1（出る・消えるを短くフェード）
        private float _swirlClock;       // 回転の位相用[s]
        private float _sweatT = -1f;     // 汗の経過[s]。負＝飛んでいない

        /// <summary>動揺の表示の有無（現投手）。観測口 <c>LiveTeamSnapshot.PitcherRattled</c> から呼び出し側が流す。</summary>
        public void SetPitcherRattled(bool rattled)
        {
            if (_pitcherRattled == rattled) return;
            _pitcherRattled = rattled;
            if (!rattled) _sweatT = -1f;
        }

        /// <summary>撮影・確認用: いま渦巻きを出す状態か。</summary>
        public bool PitcherRattledShown => _pitcherRattled;

        /// <summary>投球動作に入る瞬間に呼ぶ。動揺中だけ汗を飛ばす（毎球同じ形）。</summary>
        private void BeginPitcherSweat()
        {
            _sweatT = _pitcherRattled ? 0f : -1f;
        }

        private void ResetPitcherSweat() => _sweatT = -1f;

        private void AdvanceRattledFx(float deltaTime)
        {
            var target = _pitcherRattled ? 1f : 0f;
            _swirlAlpha = Mathf.MoveTowards(_swirlAlpha, target, deltaTime / SwirlFadeSeconds);
            if (_swirlAlpha > 0f) _swirlClock += deltaTime;
            if (_sweatT >= 0f)
            {
                _sweatT += deltaTime;
                if (_sweatT > SweatSeconds + 0.12f) _sweatT = -1f;
            }
        }

        /// <summary>頭上の渦巻きと投球前の汗を、ポスタライズ後の Target へ引く。</summary>
        private void DrawRattledFx()
        {
            if (_swirlAlpha <= 0f && _sweatT < 0f) return;
            if (_fieldPlay != null || _root == null || _camera == null || Target == null) return;
            if (_pitcher == null || _pitcher.Animator == null || _pitcher.Go == null || !_pitcher.Go.activeInHierarchy) return;
            var head = _pitcher.Animator.GetBoneTransform(HumanBodyBones.Head);
            if (head == null) return;

            var camT = _camera.transform;
            var eye = camT.position;
            var headPos = head.position;
            float depth = Vector3.Dot(headPos - eye, camT.forward);
            if (depth < 0.5f) return;

            if (_zoneMat == null)
            {
                var sh = Shader.Find("KokoSim/DuelStageZone");
                if (sh == null) return;
                _zoneMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            }

            // 1画素あたりの実寸[m]。外半径が画素下限に満たなければ記号ぜんたいを同じ比で拡大する
            float metersPerPx = 2f * depth * Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad) / Target.height;
            float k = Mathf.Max(1f, MinSwirlRadiusPx * metersPerPx / SwirlRadiusM);

            var prev = RenderTexture.active;
            RenderTexture.active = Target;
            GL.PushMatrix();
            // 射影はカメラの物をそのまま渡す（GL.GetGPUProjectionMatrix を通すと二重変換で上下反転。DrawStrikeZone の項）
            GL.LoadProjectionMatrix(_camera.projectionMatrix);
            GL.modelview = _camera.worldToCameraMatrix;
            _zoneMat.SetPass(0);

            if (_swirlAlpha > 0f) DrawSwirl(headPos, eye, k);
            if (_sweatT >= 0f) DrawSweat(headPos, camT, k);

            GL.PopMatrix();
            RenderTexture.active = prev;
        }

        // 色はリニア空間へ自分で落とす（掟: 即時モードは自動変換が効かない）。アルファは変換しない
        private static Color FxColor(Color c, float a)
        {
            var l = c.linear;
            l.a = a;
            return l;
        }

        private void DrawSwirl(Vector3 headPos, Vector3 eye, float k)
        {
            var center = headPos + Vector3.up * (SwirlLiftM * k);
            var toEye = (eye - center).normalized;
            var normal = Vector3.Slerp(Vector3.up, toEye, Mathf.Clamp01(RattledSwirlFaceCamera)).normalized;
            // 面内の基底。回転は面の法線まわり（水平にゆっくり回る渦巻き）
            var u = Vector3.Cross(normal, Mathf.Abs(Vector3.Dot(normal, Vector3.right)) > 0.9f ? Vector3.forward : Vector3.right).normalized;
            var v = Vector3.Cross(normal, u);
            float spin = _swirlClock / SwirlSpinSeconds * Mathf.PI * 2f;

            const int N = 56;
            var pts = new Vector3[N + 1];
            for (int i = 0; i <= N; i++)
            {
                float t = i / (float)N;
                float ang = t * SwirlTurns * Mathf.PI * 2f + spin;
                float r = Mathf.Lerp(0.18f, 1f, t) * SwirlRadiusM * k;   // 中心から外へ巻く
                pts[i] = center + (u * Mathf.Cos(ang) + v * Mathf.Sin(ang)) * r;
            }

            // 縁（ink）を太く、芯（紙色）を細く重ねる＝背景が芝でも空でも渦巻きと読める
            DrawRibbon(pts, eye, 0.020f * k, FxColor(RattledInk, _swirlAlpha));
            DrawRibbon(pts, eye, 0.0095f * k, FxColor(SwirlPaper, _swirlAlpha));
        }

        private static void DrawRibbon(Vector3[] pts, Vector3 eye, float halfW, Color col)
        {
            GL.Begin(GL.QUADS);
            GL.Color(col);
            for (int i = 0; i + 1 < pts.Length; i++)
            {
                var a = pts[i];
                var b = pts[i + 1];
                var dir = b - a;
                if (dir.sqrMagnitude < 1e-12f) continue;
                // 視線と線分の両方に直交する向き＝どの角度から見ても同じ太さ
                var side = Vector3.Cross(dir, a - eye);
                if (side.sqrMagnitude < 1e-12f) continue;
                side = side.normalized * halfW;
                GL.Vertex(a - side);
                GL.Vertex(a + side);
                GL.Vertex(b + side);
                GL.Vertex(b - side);
            }
            GL.End();
        }

        // 汗: 頭の左右から外へ・上へ放物線。粒ごとに出だしの速さと遅れを変えて毎球同じ形（乱数なし）
        private void DrawSweat(Vector3 headPos, Transform camT, float k)
        {
            var right = camT.right;
            var up = camT.up;
            GL.Begin(GL.QUADS);
            for (int pass = 0; pass < 2; pass++)   // 0=縁（ink・少し大きく）／1=芯
            {
                for (int side = -1; side <= 1; side += 2)
                {
                    for (int i = 0; i < SweatDropsPerSide; i++)
                    {
                        float delay = i * 0.06f;
                        float t = _sweatT - delay;
                        if (t < 0f) continue;
                        float life = SweatSeconds - delay;
                        float u = t / life;
                        if (u >= 1f) continue;
                        float outward = (0.35f + 0.30f * i) * side;   // m/s（画面の左右）
                        float rise = 1.05f - 0.35f * i;               // m/s（上向きの初速）
                        const float gravity = 3.2f;
                        var pos = headPos
                            + right * ((0.10f * side + outward * t) * k)
                            + up * ((0.18f + rise * t - 0.5f * gravity * t * t) * k);
                        var vel = right * outward + up * (rise - gravity * t);
                        var dir = new Vector2(Vector3.Dot(vel, right), Vector3.Dot(vel, up)).normalized;
                        float a = u < 0.7f ? 1f : Mathf.Clamp01((1f - u) / 0.3f);
                        float r = SweatDropRadiusM * k * (pass == 0 ? 1.45f : 1f);
                        GL.Color(FxColor(pass == 0 ? RattledInk : SweatBlue, a));
                        DropQuad(pos, right, up, dir, r);
                    }
                }
            }
            GL.End();
        }

        // 涙形（進行方向に尖った菱形）。dir は画面（右,上）内の進行方向
        private static void DropQuad(Vector3 c, Vector3 right, Vector3 up, Vector2 dir, float r)
        {
            var f = right * dir.x + up * dir.y;
            var s = right * -dir.y + up * dir.x;   // 面内で f に直交
            GL.Vertex(c + f * (r * 1.5f));
            GL.Vertex(c + s * (r * 0.95f));
            GL.Vertex(c - f * (r * 0.9f));
            GL.Vertex(c - s * (r * 0.95f));
        }
    }
}
