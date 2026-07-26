using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 対決ビューの実体（design-18 §7-1 A案・配線スパイク段階）。
    /// DuelStage レイヤーに簡易ステージ＋素体4体（UBC）を組み、StageCamera（確定構図R）が
    /// RT 480×270・Point へ毎フレーム描画する。キャラのポーズ更新だけ 12fps に量子化（§4.2 確定値）、
    /// カメラ・RTはフルレート。ステージは主カメラに映り込まないよう y=-500 へ退避して置き、
    /// HideAndDontSave でシーンを汚さない。
    ///
    /// 素体FBXとモーション（Assets/Animo＝再配布NG・gitignore）はエディタ実行時のみ AssetDatabase から
    /// 引く。ビルドへの同梱方法は本実装時に決める（この段階では Available=false で何もしない）。
    /// 表示内容は当面のループ芝居（投手=投球ループ／打者・捕手・審判=待機）。エンジンのpitch進行との
    /// 同期は §7-2 アンカー同期の実装時に接続する。
    /// </summary>
    public sealed class DuelStageRig : System.IDisposable
    {
        /// <summary>基準ドット密度（16:9・全画面時の確定解像度）。実RTはカラム実寸から適応する（EnsureSize）。</summary>
        public const int BaseWidth = 480;
        public const int BaseHeight = 270;

        private const string LayerName = "DuelStage";
        private const float MoundDistance = 18.44f;
        private const float MoundHeight = 0.254f;
        private const float TargetHeight = 1.75f;
        private const float PoseFps = 12f;   // §4.2 確定（2026-07-26 監督判定）
        private static readonly Vector3 StageOrigin = new(0f, -500f, 0f);

        private const string BodyFbx = "Assets/DuelSpike/UBC/SuperheroMale.fbx";
        private const string ClipFbx = "Assets/Animo/AA_Basic_Baseball_Movement_Animations_Package/Animations/AA_Basic_Baseball_Movement_Animations_Package.fbx";

        public RenderTexture Target { get; private set; }
        public bool Available { get; private set; }

        private GameObject _root;
        private Camera _camera;
        private GameObject _bat;
        private readonly List<Actor> _actors = new();
        private Actor _pitcher, _batter;
        private float _acc;

        private sealed class Actor
        {
            public GameObject Go;
            public Animator Animator;
            public PlayableGraph Graph;
            public AnimationClipPlayable Playable;
            public AnimationClip Clip;
            public float Time;
            public Vector3 BasePos;
            public Quaternion BaseRot;
            public float GroundY;
            public float AnkleOffset;
            public bool HoldAtEnd;   // クリップ末尾で頭姿勢のまま停止する（投手＝アンカー同期の合間はセット姿勢で待つ）
            public bool Holding;
        }

        public DuelStageRig()
        {
            int layer = LayerMask.NameToLayer(LayerName);
            var body = LoadBody();
            var pitchClip = LoadClip("AA_Baseball_Pitcher_Pitching_type01");
            var batClip = LoadClip("AA_Baseball_Batter_Idle_Loop");
            var catClip = LoadClip("AA_Baseball_Catcher_Idle");
            var umpClip = LoadClip("AA_Baseball_Umpire_Crouch_Idle");
            if (layer < 0 || body == null || pitchClip == null || batClip == null || catClip == null || umpClip == null)
            {
                Debug.LogWarning("[DuelStageRig] assets unavailable (layer/body/clips) - duel view disabled");
                return;
            }

            _root = new GameObject("~DuelStageLive") { hideFlags = HideFlags.HideAndDontSave };
            _root.transform.position = StageOrigin;
            BuildGround(layer);

            // yawはクリップごとの素の向き実測（DuelMotionSpike.RunFacingProbe 2026-07-26）から:
            // 投手クリップだけ前方向−X（ルートモーション実測）、脇3クリップは素で+Z向き
            _pitcher = SpawnActor(body, pitchClip, new Vector3(0f, MoundHeight, MoundDistance), -90f,
                new Color(0.92f, 0.92f, 0.90f), MoundHeight, layer);
            _pitcher.HoldAtEnd = true;   // 投手は投げ切ったらセット姿勢で次のアンカー同期を待つ（勝手にループしない）
            _batter = SpawnActor(body, batClip, new Vector3(-0.9f, 0f, 0f), 90f,
                new Color(0.16f, 0.20f, 0.38f), 0f, layer);
            var catcher = SpawnActor(body, catClip, new Vector3(0f, 0f, -1.0f), 0f,
                new Color(0.25f, 0.27f, 0.30f), 0f, layer);
            SpawnActor(body, umpClip, new Vector3(-0.35f, 0f, -1.7f), 0f,
                new Color(0.12f, 0.12f, 0.13f), 0f, layer);

            _bat = SpawnBat(layer);
            AttachMitt(catcher, layer);

            Target = CreateRt(BaseWidth, BaseHeight);

            var camGo = new GameObject("StageCamera");
            camGo.transform.SetParent(_root.transform, false);
            _camera = camGo.AddComponent<Camera>();
            _camera.cullingMask = 1 << layer;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.55f, 0.78f, 0.90f); // 夏空（盤面トーン連動は絵作り本実装で）
            _camera.nearClipPlane = 0.1f;
            _camera.farClipPlane = 200f;
            // 確定構図 R-comp60（design-18 §7-1・2026-07-26改定＝望遠圧縮）。
            // 俯角4.5°・横角3.8°は旧Rのまま、後退60m+FOV2.9°で投手/打者の遠近差を圧縮する
            const float dist = 60f;
            const float aimY = 0.8f;
            float camZ = MoundDistance + dist;
            float dz = camZ - 1f;
            camGo.transform.localPosition = new Vector3(-dz * 0.0668f, aimY + dz * 0.0782f, camZ);
            camGo.transform.LookAt(_root.transform.position + new Vector3(0f, aimY, 1f));
            ApplyFov();
            _camera.targetTexture = Target;

            // 初期ポーズを1回打っておく（トグル直後の素立ちTポーズを見せない）
            foreach (var a in _actors) StepActor(a, 0.001f);
            GroundFix(_pitcher);
            AlignBat();

            Available = true;
        }

        // ── §7-2 アンカー同期（第1弾＝投手のみ） ──────────────────────────
        // リリースのアンカー時刻は右手速度ピーク実測（RunPitcherProbe: t=0.50 × 6.00s＝8.5m/s ピーク）。
        // 本実装ではAnimationEventの注釈付けに置き換える（design-18 §7-2）。
        private const float ReleaseTime = 3.0f;
        private float _cutCountdown = -1f;   // 次のカットバックまでの残り（プレー時間秒）。負=予約なし
        private float _cutClipTime;

        /// <summary>
        /// 盤面の「ボールが手を離れた瞬間」（BeginPitchBall）に呼ぶアンカー同期の入口。
        /// 投手クリップをリリース時刻へスナップし、次の1球があれば（nextReleaseIn=1球周期）
        /// フォロースルー後にリリースのプリロール位置へカットバックする予約を入れる。
        /// クリップ内部の緩急は改変しない＝再生開始位置の逆算だけで合わせる（設計書18の掟）。
        /// </summary>
        public void OnPitchReleased(float nextReleaseIn)
        {
            if (!Available || _pitcher == null) return;
            SeekActor(_pitcher, ReleaseTime);
            GroundFix(_pitcher);
            if (nextReleaseIn > 0f)
            {
                float preRoll = Mathf.Min(ReleaseTime, nextReleaseIn * 0.6f);
                _cutCountdown = nextReleaseIn - preRoll;
                _cutClipTime = ReleaseTime - preRoll;
            }
            else
            {
                _cutCountdown = -1f;   // 最後の1球: フォロースルーを最後まで→クリップ末尾でセット姿勢
            }
        }

        /// <summary>毎フレーム呼ぶ。ポーズは1/12秒たまるごとに進む（時間量子化）。</summary>
        public void Tick(float deltaTime)
        {
            if (!Available) return;

            // カットバック予約（アンカー同期のプリロール）。量子化と独立にプレー時間で数える
            if (_cutCountdown >= 0f)
            {
                _cutCountdown -= deltaTime;
                if (_cutCountdown < 0f)
                {
                    SeekActor(_pitcher, _cutClipTime);
                    GroundFix(_pitcher);
                }
            }

            _acc += deltaTime;
            float step = 1f / PoseFps;
            bool stepped = false;
            while (_acc >= step)
            {
                _acc -= step;
                foreach (var a in _actors) StepActor(a, step);
                stepped = true;
            }
            if (stepped)
            {
                GroundFix(_pitcher);   // 投球ループはルートモーションでマウンドを降りるためコマ毎に接地
                AlignBat();
            }
        }

        /// <summary>
        /// RTをカラム実寸由来のサイズへ張り替える（DuelViewElementから）。同寸なら何もしない。
        /// 縦横比はカラム追従だが、極端な縦長/横長は構図が壊れるためクランプする。
        /// 水平視野角は基準（16:9で垂直2.9°）固定＝縦に長い分は上下に描画範囲が広がるだけで構図は切れない。
        /// </summary>
        public void EnsureSize(int width, int height)
        {
            if (!Available) return;
            width = Mathf.Clamp(width, 160, 2048);
            height = Mathf.Clamp(height, 90, 2048);
            // 縦横比クランプ: 4:3より縦長・2.4:1より横長にはしない（壁の高さ・構図の担保範囲）
            height = Mathf.Min(height, Mathf.FloorToInt(width * 0.75f));
            width = Mathf.Min(width, Mathf.FloorToInt(height * 2.4f));
            if (Target != null && Target.width == width && Target.height == height) return;

            if (_camera != null) _camera.targetTexture = null;
            if (Target != null) { Target.Release(); Object.DestroyImmediate(Target); }
            Target = CreateRt(width, height);
            if (_camera != null)
            {
                _camera.targetTexture = Target;
                ApplyFov();
            }
        }

        private static RenderTexture CreateRt(int width, int height)
        {
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
            { name = "DuelStageLiveRT", filterMode = FilterMode.Point };
            rt.Create();
            return rt;
        }

        /// <summary>水平視野角を基準構図（垂直2.9°@16:9）で固定し、現RT縦横比から垂直FOVを逆算する。</summary>
        private void ApplyFov()
        {
            float halfH = Mathf.Tan(2.9f * 0.5f * Mathf.Deg2Rad) * (16f / 9f);
            float aspect = Target.width / (float)Target.height;
            _camera.fieldOfView = 2f * Mathf.Atan(halfH / aspect) * Mathf.Rad2Deg;
        }

        public void Dispose()
        {
            foreach (var a in _actors)
            {
                if (a.Graph.IsValid()) a.Graph.Destroy();
            }
            _actors.Clear();
            if (_camera != null) _camera.targetTexture = null;
            if (Target != null) { Target.Release(); Object.DestroyImmediate(Target); Target = null; }
            if (_root != null) { Object.DestroyImmediate(_root); _root = null; }
            Available = false;
        }

        // ---- ステージ ---------------------------------------------------------

        private void BuildGround(int layer)
        {
            Primitive(PrimitiveType.Plane, "ground", new Vector3(0f, 0f, 9f), new Vector3(10f, 1f, 10f),
                new Color(0.30f, 0.55f, 0.30f), layer);
            Primitive(PrimitiveType.Cylinder, "dirt-home", new Vector3(0f, 0.005f, 0f), new Vector3(7f, 0.005f, 7f),
                new Color(0.63f, 0.47f, 0.33f), layer);
            Primitive(PrimitiveType.Cylinder, "mound", new Vector3(0f, MoundHeight * 0.5f, MoundDistance),
                new Vector3(5.4f, MoundHeight * 0.5f, 5.4f), new Color(0.63f, 0.47f, 0.33f), layer);
            Primitive(PrimitiveType.Cube, "home-plate", new Vector3(0f, 0.012f, 0f), new Vector3(0.43f, 0.02f, 0.43f),
                Color.white, layer);
            Primitive(PrimitiveType.Cube, "batter-box-L", new Vector3(-0.9f, 0.011f, 0f), new Vector3(1.2f, 0.012f, 1.8f),
                new Color(0.75f, 0.68f, 0.58f), layer);
            Primitive(PrimitiveType.Cube, "batter-box-R", new Vector3(0.9f, 0.011f, 0f), new Vector3(1.2f, 0.012f, 1.8f),
                new Color(0.75f, 0.68f, 0.58f), layer);
            // バックネット書き割り（単色の暗インク壁）。地面プレーンの尽きた先が背景色で抜けるのを塞ぐ。
            // 本実装のスタジアム背景（スタンド/ネット）に置き換わるまでの仮（2026-07-26 構図リフレームD採用時）
            Primitive(PrimitiveType.Cube, "backstop-wall", new Vector3(0f, 4f, -16f), new Vector3(34f, 8f, 0.5f),
                new Color(0.12f, 0.20f, 0.16f), layer);
        }

        private GameObject Primitive(PrimitiveType type, string name, Vector3 localPos, Vector3 localScale, Color color, int layer)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(_root.transform, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = localScale;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.GetComponent<MeshRenderer>().sharedMaterial = FlatMaterial(color);
            go.layer = layer;
            return go;
        }

        private static Material FlatMaterial(Color color)
        {
            // シーンライト非依存の自前Lambert（Assets/DuelSpike/DuelStageProxyLambert.shader）
            var mat = new Material(Shader.Find("KokoSim/DuelStageProxyLambert")) { hideFlags = HideFlags.HideAndDontSave };
            mat.SetColor("_BaseColor", color);
            return mat;
        }

        // ---- 素体 -------------------------------------------------------------

        private Actor SpawnActor(GameObject bodyPrefab, AnimationClip clip, Vector3 pos, float yawDeg, Color color,
            float groundY, int layer)
        {
            var go = Object.Instantiate(bodyPrefab);
            go.name = "body-" + clip.name;
            go.transform.SetParent(_root.transform, false);

            var rs = go.GetComponentsInChildren<Renderer>();
            var b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            float scale = TargetHeight / Mathf.Max(0.01f, b.size.y);
            go.transform.localScale = Vector3.one * scale;
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(0f, yawDeg, 0f);

            var mat = FlatMaterial(color);
            foreach (var r in rs) r.sharedMaterial = mat;
            foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>())
                smr.updateWhenOffscreen = true;   // バウンズをバインド箱でなく実ポーズ追従に
            SetLayerRecursive(go, layer);

            var animator = go.GetComponentInChildren<Animator>();
            animator.applyRootMotion = true;      // AAクリップは踏み込み・降下をルートモーションで持つ
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            var actor = new Actor
            {
                Go = go,
                Animator = animator,
                Clip = clip,
                BasePos = go.transform.localPosition,
                BaseRot = go.transform.localRotation,
                GroundY = groundY,
            };
            var lf = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            var rf = animator.GetBoneTransform(HumanBodyBones.RightFoot);
            if (lf != null && rf != null)
                actor.AnkleOffset = Mathf.Min(lf.position.y, rf.position.y) - go.transform.position.y;

            actor.Graph = PlayableGraph.Create("duel-" + clip.name);
            var output = AnimationPlayableOutput.Create(actor.Graph, "out", animator);
            actor.Playable = AnimationClipPlayable.Create(actor.Graph, clip);
            output.SetSourcePlayable(actor.Playable);

            _actors.Add(actor);
            return actor;
        }

        /// <summary>
        /// クリップの頭出し（アンカー同期用）。ルートモーションはデルタ蓄積なので、
        /// スポーン姿勢へ戻してから0→目標時刻を60fps刻みで前進させる（スパイクのSample()と同じ方式）。
        /// </summary>
        private static void SeekActor(Actor a, float clipTime)
        {
            if (a == null) return;
            a.Holding = false;
            RewindActor(a);
            float target = Mathf.Clamp(clipTime, 0f, a.Clip.length - 0.001f);
            int steps = Mathf.Max(1, Mathf.CeilToInt(target * 60f));
            float dt = target / steps;
            for (int i = 0; i < steps; i++) a.Graph.Evaluate(dt);
            a.Time = target;
        }

        /// <summary>
        /// クリップ先頭へ巻き戻す。常設グラフでの SetTime ジャンプは「直前時刻→0」のルートモーション差分を
        /// transformへ注入する（毎回使い捨てグラフのスパイクSample()では出ない罠）ため、
        /// 巻き戻しの間だけ applyRootMotion を切ってポーズだけ打ち直す。
        /// </summary>
        private static void RewindActor(Actor a)
        {
            a.Go.transform.localPosition = a.BasePos;
            a.Go.transform.localRotation = a.BaseRot;
            bool rm = a.Animator.applyRootMotion;
            a.Animator.applyRootMotion = false;
            a.Playable.SetTime(0);
            a.Graph.Evaluate(0f);
            a.Animator.applyRootMotion = rm;
        }

        /// <summary>
        /// 1体を1ステップ進める。クリップ末尾でスポーン姿勢へ戻し、HoldAtEnd なら頭姿勢のまま停止
        /// （投手＝次のアンカー同期まで待つ）、それ以外はループ（ルートモーションのドリフト対策の基準戻し込み）。
        /// </summary>
        private void StepActor(Actor a, float step)
        {
            if (a.Holding) return;
            a.Time += step;
            if (a.Time >= a.Clip.length)
            {
                a.Time = 0f;
                RewindActor(a);
                if (a.HoldAtEnd) a.Holding = true;
                return;
            }
            a.Graph.Evaluate(step);
        }

        /// <summary>低い方の足ボーンが接地面＋足首オフセットに来るよう縦だけ合わせる（ステージローカル）。</summary>
        private void GroundFix(Actor a)
        {
            var lf = a.Animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            var rf = a.Animator.GetBoneTransform(HumanBodyBones.RightFoot);
            if (lf == null || rf == null) return;
            float footLocalY = Mathf.Min(lf.position.y, rf.position.y) - _root.transform.position.y;
            a.Go.transform.localPosition += new Vector3(0f, (a.GroundY + a.AnkleOffset) - footLocalY, 0f);
        }

        // ---- 小道具（§3.3。両手整列バット＋手ボーン子ミット） ------------------

        private GameObject SpawnBat(int layer)
        {
            var bat = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            bat.name = "prop-bat";
            Object.DestroyImmediate(bat.GetComponent<Collider>());
            bat.transform.SetParent(_root.transform, false);
            bat.transform.localScale = new Vector3(0.055f, 0.425f, 0.055f);
            bat.GetComponent<MeshRenderer>().sharedMaterial = FlatMaterial(new Color(0.78f, 0.62f, 0.42f));
            bat.layer = layer;
            return bat;
        }

        private void AlignBat()
        {
            if (_bat == null || _batter == null) return;
            var rh = _batter.Animator.GetBoneTransform(HumanBodyBones.RightHand).position;
            var lh = _batter.Animator.GetBoneTransform(HumanBodyBones.LeftHand).position;
            var axis = (rh - lh).normalized;
            _bat.transform.rotation = Quaternion.FromToRotation(Vector3.up, axis);
            _bat.transform.position = lh + axis * (0.425f - 0.10f);
        }

        private void AttachMitt(Actor catcher, int layer)
        {
            var hand = catcher.Animator.GetBoneTransform(HumanBodyBones.LeftHand);
            if (hand == null) return;
            var mitt = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            mitt.name = "prop-mitt";
            Object.DestroyImmediate(mitt.GetComponent<Collider>());
            mitt.transform.SetParent(hand, false);
            var inv = new Vector3(1f / hand.lossyScale.x, 1f / hand.lossyScale.y, 1f / hand.lossyScale.z);
            mitt.transform.localScale = Vector3.Scale(new Vector3(0.26f, 0.24f, 0.15f), inv);
            mitt.GetComponent<MeshRenderer>().sharedMaterial = FlatMaterial(new Color(0.45f, 0.27f, 0.15f));
            mitt.layer = layer;
        }

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform) SetLayerRecursive(child.gameObject, layer);
        }

        // ---- アセットロード（エディタ限定・ビルド同梱は本実装時に決める） --------

        private static GameObject LoadBody()
        {
#if UNITY_EDITOR
            return UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(BodyFbx);
#else
            return null;
#endif
        }

        private static AnimationClip LoadClip(string clipName)
        {
#if UNITY_EDITOR
            foreach (var a in UnityEditor.AssetDatabase.LoadAllAssetsAtPath(ClipFbx))
            {
                var c = a as AnimationClip;
                if (c != null && c.name == clipName) return c;
            }
#endif
            return null;
        }
    }
}
