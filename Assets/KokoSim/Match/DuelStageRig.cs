using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace KokoSim.Unity.Match
{
    /// <summary>対決ビューへ渡す1球の結果分類（design-18 §7-2 第2弾）。エンジンのPitchKindから写像する。</summary>
    public enum DuelPitchOutcome
    {
        Take,           // ボール見送り（打者は待機のまま・捕手が捕球）
        CalledStrike,   // 見逃しストライク（Batter_Strike＋捕球）
        SwingMiss,      // 空振り（Swing_Miss＋捕球）
        FoulContact,    // ファール（Swing_Hitting・走り出し前でカット・捕球なし）
        InPlayContact,  // インプレー（Swing_Hittingフル＝走り出しまで・捕球なし）
        HitByPitch,     // 死球（Hit_ByPitch・捕球なし）
    }

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

        /// <summary>
        /// クリップ供給元（先勝ち＝同名なら先の要素が勝つ）。自作モーション（Assets/Motions＝Rokoko収録・
        /// コミット可）を先に見て、無ければ購入パック（Assets/Animo＝再配布NG・gitignore）へ落ちる。
        /// プローブ（DuelMotionSpike / DuelMotionCatalog）も同じ入口を通す＝実測と本番でクリップがずれない。
        /// </summary>
        public static readonly string[] ClipFbxSources =
        {
            "Assets/Motions/Duel/Pitcher_Pitching_01.fbx",
            "Assets/Motions/Duel/Batter_Batting_01.fbx",
            "Assets/Animo/AA_Basic_Baseball_Movement_Animations_Package/Animations/AA_Basic_Baseball_Movement_Animations_Package.fbx",
        };

        /// <summary>投手の投球クリップ名。自作Rokokoモーション（Assets/Motions/Duel）に置き換え済み。</summary>
        public const string PitcherClipName = "Pitcher_Pitching_01";

        /// <summary>
        /// 投手クリップを本塁向き（−Z）へ揃えるyaw[度]。
        /// 収録モーキャップはセット姿勢の向きと実際に踏み込んで投げる方向が一致しない（本クリップは約68°ずれ）。
        /// 見えるべきは「打者へ投げている」ことなので、`RunFacingProbe` のセット姿勢基準（180°）ではなく
        /// `RunRootPathProbe` の踏み込みベクトル基準を採る（2026-07-27実測: t=3.5sの変位(1.23,-0.49)→+56〜68°）。
        /// </summary>
        public const float PitcherYaw = 236f;

        /// <summary>
        /// 打者のクリップ名。自作Rokokoモーション（Assets/Motions/Duel）に置き換え済み。
        /// **構え→スイング→走り出しが1本**に入っているため、待機と一発物で同じクリップを区間違いで使う。
        /// 素の向きはAA打者クリップと同じ+Z（RunFacingProbe実測 chest=82°/hips=103°→ yaw 90 のまま）。
        /// </summary>
        public const string BatterClipName = "Batter_Batting_01";

        // 打者クリップ内の実測アンカー（RunClipSpeedProbe 0.05s刻み・右手速度 2026-07-27）:
        // 0.0-1.7s=構え（ほぼ静止） / 1.7-2.8s=予備動作 / 2.85-3.15s=スイング（ピーク10.0m/s @3.05s） /
        // 3.25s-=走り出し。待機は構え区間だけをループさせ、スイングは頭出しで呼ぶ。
        private const float BatterIdleEnd = 1.70f;     // 待機ループの折り返し（ここから先を待機中に見せない）
        private const float BatterSwingStart = 2.80f;  // ここから先が「振っている」区間（バット片手判定の許可域）
        private const float BatterRunStart = 3.25f;    // 走り出し。ファール・空振りはここで打ち切って構えへ戻す

        public RenderTexture Target { get; private set; }
        public bool Available { get; private set; }

        private GameObject _root;
        private Camera _camera;
        private GameObject _bat;
        private readonly List<Actor> _actors = new();
        private Actor _pitcher, _batter, _catcher;
        private float _acc;

        // 結果分岐の一発物クリップ（design-18 §7-2 第2弾。無いものはnull＝その分岐は待機のまま）
        private AnimationClip _swingMissClip, _swingHitClip, _strikeTakeClip, _hbpClip, _catchClip;

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
            public AnimationPlayableOutput Output;   // クリップ差し替え（結果分岐の一発物）用
            public AnimationClip IdleClip;           // 一発物の終了後に戻る待機ループ（null=戻らない）
            public float IdleEndAt;                  // 待機ループの折り返し時刻（0=クリップ末尾）。
                                                     // 構えとスイングが同じクリップの打者は、ループのたびに張り直す必要がある
            public float EndAt;                      // 一発物の早期終了時刻（0=クリップ末尾まで）
        }

        public DuelStageRig()
        {
            int layer = LayerMask.NameToLayer(LayerName);
            var body = LoadBody();
            var pitchClip = LoadClip(PitcherClipName);
            var batClip = LoadClip(BatterClipName);
            var catClip = LoadClip("AA_Baseball_Catcher_Idle");
            var umpClip = LoadClip("AA_Baseball_Umpire_Crouch_Idle");
            if (layer < 0 || body == null || pitchClip == null || batClip == null || catClip == null || umpClip == null)
            {
                Debug.LogWarning("[DuelStageRig] assets unavailable (layer/body/clips) - duel view disabled");
                return;
            }

            // 残骸掃除: HideAndDontSave はPlay終了・ドメインリロードを跨いで生き残るため、
            // Disposeが走らず孤児化した過去のステージが同座標に重なって二重に映る（2026-07-26実害）。
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (go != null && go.name == "~DuelStageLive" && go.transform.parent == null
                    && (go.hideFlags & HideFlags.HideAndDontSave) != 0)
                    Object.DestroyImmediate(go);
            }

            _root = new GameObject("~DuelStageLive") { hideFlags = HideFlags.HideAndDontSave };
            _root.transform.position = StageOrigin;
            BuildGround(layer);

            // yawはクリップごとの素の向き実測（DuelMotionSpike.RunFacingProbe）から:
            // 投手クリップ（自作Rokoko）は素で+Z向き＝PitcherYaw、脇3クリップ（購入AA）も素で+Z向き
            _pitcher = SpawnActor(body, pitchClip, new Vector3(0f, MoundHeight, MoundDistance), PitcherYaw,
                new Color(0.92f, 0.92f, 0.90f), MoundHeight, layer);
            _pitcher.HoldAtEnd = true;   // 投手は投げ切ったらセット姿勢で次のアンカー同期を待つ（勝手にループしない）
            _batter = SpawnActor(body, batClip, new Vector3(-0.9f, 0f, 0f), 90f,
                new Color(0.16f, 0.20f, 0.38f), 0f, layer);
            _batter.IdleClip = batClip;
            _batter.IdleEndAt = BatterIdleEnd;
            _batter.EndAt = BatterIdleEnd;   // 生成直後の初回ループでスイングまで流さない
            _catcher = SpawnActor(body, catClip, new Vector3(0f, 0f, -1.0f), 0f,
                new Color(0.25f, 0.27f, 0.30f), 0f, layer);
            _catcher.IdleClip = catClip;
            SpawnActor(body, umpClip, new Vector3(-0.35f, 0f, -1.7f), 0f,
                new Color(0.12f, 0.12f, 0.13f), 0f, layer);

            // 結果分岐の一発物（§7-2第2弾）。素の向きは打者系/捕手系とも+Z（RunFacingProbe実測の規約どおり）。
            // 空振り/ミートとも自作打撃クリップの同じスイング区間を使う（収録が1本なので分岐を持てない）。
            // 差は打ち切り位置だけ＝空振り・ファールは走り出し前で構えへ戻す（ScheduleReactions）
            _swingMissClip = batClip;
            _swingHitClip = batClip;
            _strikeTakeClip = LoadClip("AA_Baseball_Batter_Strike");
            _hbpClip = LoadClip("AA_Baseball_Batter_Hit_ByPitch_Type01");
            _catchClip = LoadClip("AA_Baseball_Catcher_Catching_C");

            _bat = SpawnBat(layer);
            AttachMitt(_catcher, layer);

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
        // リリースのアンカー時刻は右手速度ピーク実測。自作Rokokoクリップ（4.27s）では
        // RunClipSpeedProbe(0.05s刻み) の 3.05s＝13.7m/s がピーク（旧AAクリップは3.00s＝8.5m/s）。
        // 収録の頭にあった静止1.2sはインポータのfirstFrame=36で落としてあり、その結果
        // リリース時刻が旧クリップとほぼ同値に揃っている＝打席の間合いは据え置き。
        // 本実装ではAnimationEventの注釈付けに置き換える（design-18 §7-2）。
        // MatchLiveController が対決ビュー中の1球周期（duelPitchIntervalSeconds）の基準にも使う。
        public const float ReleaseTime = 3.05f;
        private const float FollowSeconds = 1.2f;   // リリース後に見せるフォロースルーの長さ（クリップ末尾まで実測1.22s）
        private float _cutCountdown = -1f;   // 次のカットバックまでの残り（プレー時間秒）。負=予約なし
        private float _cutClipTime;

        /// <summary>
        /// 打席頭のワインドアップ入口。クリップ先頭（セット姿勢）から等速で再生を始める。
        /// 呼び出し側が投球時計を −ReleaseTime から始めれば、時計0＝リリースにそのまま一致する。
        /// </summary>
        public void OnPitchWindup()
        {
            if (!Available || _pitcher == null) return;
            _cutCountdown = -1f;
            SeekActor(_pitcher, 0f);
            GroundFix(_pitcher);
            // 打席頭は打者・捕手を待機へ戻す（前打席の一発物が残っていても仕切り直す）
            if (_batter != null && _batter.IdleClip != null) SetActorClip(_batter, _batter.IdleClip, 0f, _batter.IdleEndAt);
            if (_catcher != null && _catcher.IdleClip != null) SetActorClip(_catcher, _catcher.IdleClip, 0f, _catcher.IdleEndAt);
        }

        // 結果分岐のアンカー（スイングはType02をRunClipSpeedProbeで実測 2026-07-27。値はクリップ内秒）:
        // 空振り=スイング面通過1.2s（右手速度ピーク6.7m/s） / ミート=当たり1.25s（ピーク1.3sの直前） /
        // 見逃し=振り返り2.0s / 死球=のけぞり1.3s / 捕球=ミット提示0.4s（冒頭0.15sで構えて保持する作り）。
        // 盤面の投球飛翔は0.45s（MatchLiveのSetTime基準）＝リリース時に「アンカー−0.45」へ頭出しすると
        // ボール到達とアンカーが一致する。
        private const float BallFlightSeconds = 0.45f;
        private const float SwingMissPlane = 3.05f;    // 自作打撃クリップ: 右手速度ピーク10.0m/s（2026-07-27実測）
        private const float SwingHitContact = 3.05f;   // 同上。空振りとミートで収録が分かれていないため同値
        private const float StrikeReact = 2.0f;
        private const float HbpReact = 1.3f;
        private const float CatchMoment = 0.4f;

        /// <summary>
        /// 盤面の「ボールが手を離れた瞬間」（BeginPitchBall）に呼ぶアンカー同期の入口。
        /// 投手クリップをリリース時刻へスナップし、次の1球があれば（nextReleaseIn=リリース間隔）
        /// フォロースルーを FollowSeconds 見せてから次の振りかぶり位置へ切り替える予約を入れる。
        /// 間隔が振りかぶり＋フォローに足りない場合だけプリロールを削って間に合わせる。
        /// あわせて1球の結果（outcome）に応じた打者・捕手の一発物をボール到達アンカーで頭出しする。
        /// クリップ内部の緩急は改変しない＝再生開始位置の逆算だけで合わせる（設計書18の掟）。
        /// </summary>
        public void OnPitchReleased(float nextReleaseIn, DuelPitchOutcome outcome)
        {
            if (!Available || _pitcher == null) return;
            SeekActor(_pitcher, ReleaseTime);
            GroundFix(_pitcher);
            if (nextReleaseIn > 0f)
            {
                float preRoll = Mathf.Clamp(nextReleaseIn - FollowSeconds, 0.5f, ReleaseTime);
                _cutCountdown = nextReleaseIn - preRoll;
                _cutClipTime = ReleaseTime - preRoll;
            }
            else
            {
                _cutCountdown = -1f;   // 最後の1球: フォロースルーを最後まで→クリップ末尾でセット姿勢
            }
            ScheduleReactions(outcome);
        }

        /// <summary>打者・捕手の結果分岐（§7-2第2弾）。クリップが無い分岐は待機のまま何もしない。</summary>
        private void ScheduleReactions(DuelPitchOutcome outcome)
        {
            switch (outcome)
            {
                case DuelPitchOutcome.SwingMiss:
                    // 空振り専用の収録が無いので同じスイングを使い、走り出し前で打ち切って構えへ戻す
                    SetActorClip(_batter, _swingMissClip, SwingMissPlane - BallFlightSeconds, BatterRunStart);
                    break;
                case DuelPitchOutcome.FoulContact:
                    // 走り出し（実測3.25s）前でカットして打席へ戻る
                    SetActorClip(_batter, _swingHitClip, SwingHitContact - BallFlightSeconds, BatterRunStart);
                    break;
                case DuelPitchOutcome.InPlayContact:
                    SetActorClip(_batter, _swingHitClip, SwingHitContact - BallFlightSeconds, 0f);
                    break;
                case DuelPitchOutcome.CalledStrike:
                    SetActorClip(_batter, _strikeTakeClip, StrikeReact - BallFlightSeconds, 3.2f);
                    break;
                case DuelPitchOutcome.HitByPitch:
                    SetActorClip(_batter, _hbpClip, HbpReact - BallFlightSeconds, 4.2f);
                    break;
            }
            bool catcherCatches = outcome == DuelPitchOutcome.Take
                || outcome == DuelPitchOutcome.CalledStrike
                || outcome == DuelPitchOutcome.SwingMiss;
            if (catcherCatches)
                SetActorClip(_catcher, _catchClip, Mathf.Max(0f, CatchMoment - BallFlightSeconds), 0f);
        }

        /// <summary>一発物クリップへ差し替えて頭出しする（終了後は Actor.IdleClip へ自動復帰）。</summary>
        private static void SetActorClip(Actor a, AnimationClip clip, float seekTo, float endAt)
        {
            if (a == null || clip == null) return;
            if (a.Clip != clip)
            {
                if (a.Playable.IsValid()) a.Playable.Destroy();
                a.Playable = AnimationClipPlayable.Create(a.Graph, clip);
                a.Output.SetSourcePlayable(a.Playable);
                a.Clip = clip;
            }
            a.EndAt = endAt;
            SeekActor(a, Mathf.Max(0f, seekTo));
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
            actor.Output = AnimationPlayableOutput.Create(actor.Graph, "out", animator);
            actor.Playable = AnimationClipPlayable.Create(actor.Graph, clip);
            actor.Output.SetSourcePlayable(actor.Playable);

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
            float end = a.EndAt > 0f && a.EndAt < a.Clip.length ? a.EndAt : a.Clip.length;
            if (a.Time >= end)
            {
                a.Time = 0f;
                a.EndAt = 0f;
                if (a.IdleClip != null && a.Clip != a.IdleClip)
                {
                    SetActorClip(a, a.IdleClip, 0f, a.IdleEndAt);   // 一発物（結果分岐）→待機ループへ復帰
                    return;
                }
                RewindActor(a);
                // 待機が「1本のクリップの一区間だけ」の場合はループのたびに区間を張り直す。
                // 自作打撃クリップは構え→スイング→走り出しが1本なので、戻さないと待機中に振ってしまう
                if (a.Clip == a.IdleClip) a.EndAt = a.IdleEndAt;
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

        // 片手フォロースルー判定: 両手ボーン間隔がグリップ間隔を明確に超えたら上の手が離れたとみなす。
        // 素体UBCの両手ボーン間隔は「両手で握っていても」構えで~0.23mある（RunHandGapProbe実測）ため、
        // 判定はスイング系クリップに限定する（構え/見逃し/死球は常に両手整列＝素の見た目を信じる）
        private const float HandsApartThreshold = 0.28f;
        private const float BatDropTime = BatterRunStart;   // 走り出し（実測3.25s）からバットを放った扱いで非表示
        private bool _batOneHand;       // いま片手追従中か（診断用）
        private bool _batGoodValid;     // _batLocal* が「両手で持てていた見た目」を保持しているか
        private Vector3 _batLocalPos;
        private Quaternion _batLocalRot;

        private void AlignBat()
        {
            if (_bat == null || _batter == null) return;
            var lhT = _batter.Animator.GetBoneTransform(HumanBodyBones.LeftHand);
            var rhT = _batter.Animator.GetBoneTransform(HumanBodyBones.RightHand);
            if (lhT == null || rhT == null) return;

            // インプレーの走り出し以降はバットを放った扱い（実物はトスするが小道具は消すのが最も自然）
            if (_batter.Time >= BatDropTime)
            {
                _bat.SetActive(false);
                return;
            }
            _bat.SetActive(true);

            var lh = lhT.position;
            var rh = rhT.position;
            // 片手判定を許すのはスイング区間だけ。自作打撃クリップは構えもスイングも同じ1本なので
            // クリップの同一性では区別できず、クリップ内時刻で見る（構えは BatterIdleEnd 以下）
            bool swinging = _batter.Time >= BatterSwingStart;
            _batOneHand = swinging && _batGoodValid && Vector3.Distance(lh, rh) > HandsApartThreshold;
            if (!_batOneHand)
            {
                // 両手保持: 両手ボーンを結ぶ線に整列（§3.3の実測知見＝手ボーン軸は素体依存で使えない）
                var axis = (rh - lh).normalized;
                _bat.transform.rotation = Quaternion.FromToRotation(Vector3.up, axis);
                _bat.transform.position = lh + axis * (0.425f - 0.10f);
                // 片手期の追従用に「最後に両手で持てていた見た目」を毎回焼き直す。
                // 「離れた瞬間に初回焼き付け」だと、クリップ切替直後は直前クリップ末尾の残骸姿勢を
                // 拾ってゴミオフセットが固定される（構えでバットが地面に刺さる事故の真因）
                _batLocalPos = lhT.InverseTransformPoint(_bat.transform.position);
                _batLocalRot = Quaternion.Inverse(lhT.rotation) * _bat.transform.rotation;
                _batGoodValid = true;
                return;
            }

            // 片手フォロースルー（右打者は振り抜きで右手が離れる）: 最後の両手整列時の見た目のまま
            // 下の手（左手）ボーンに追従させる。ボーン軸の向きを仮定しない＝焼き付けオフセットなので
            // 素体が変わっても壊れない
            _bat.transform.position = lhT.TransformPoint(_batLocalPos);
            _bat.transform.rotation = lhT.rotation * _batLocalRot;
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

#if UNITY_EDITOR
        // ---- 【開発用・エディタ限定】録画ハーネス（DuelViewRecorder）向け観測窓 ----

        /// <summary>バット追従の内部状態をTSV1行で返す（clip / clipT / 両手間隔 / 片手フラグ / バット表示）。</summary>
        internal string BatDiagForCapture()
        {
            if (_batter == null || _batter.Animator == null) return "-\t0\t-1\tFalse\tFalse";
            var lh = _batter.Animator.GetBoneTransform(HumanBodyBones.LeftHand);
            var rh = _batter.Animator.GetBoneTransform(HumanBodyBones.RightHand);
            float gap = (lh != null && rh != null) ? Vector3.Distance(lh.position, rh.position) : -1f;
            string clip = _batter.Clip != null ? _batter.Clip.name : "-";
            bool batOn = _bat != null && _bat.activeSelf;
            return clip + "\t" + _batter.Time.ToString("F2") + "\t" + gap.ToString("F3")
                + "\t" + _batOneHand + "\t" + batOn;
        }

        /// <summary>打者の左手ボーンのRT上ピクセル座標（クロップ中心の決定用）。</summary>
        internal Vector2 BatterScreenPxForCapture()
        {
            if (_camera == null || _batter == null || _batter.Animator == null) return new Vector2(-1f, -1f);
            var lh = _batter.Animator.GetBoneTransform(HumanBodyBones.LeftHand);
            var p = lh != null ? lh.position : _batter.Go.transform.position + Vector3.up;
            var sp = _camera.WorldToScreenPoint(p);
            return new Vector2(sp.x, sp.y);
        }
#endif

        // ---- アセットロード（エディタ限定・ビルド同梱は本実装時に決める） --------

        private static GameObject LoadBody()
        {
#if UNITY_EDITOR
            return UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(BodyFbx);
#else
            return null;
#endif
        }

        /// <summary>
        /// 供給元FBX（ClipFbxSources）を順に見てクリップを名前引きする。プローブ側も同じ入口を使う。
        /// FBXにはプレビュー複製（__preview__接頭辞）が同居するが、完全一致なので拾わない。
        /// </summary>
        public static AnimationClip LoadClip(string clipName)
        {
#if UNITY_EDITOR
            foreach (var fbx in ClipFbxSources)
            {
                foreach (var a in UnityEditor.AssetDatabase.LoadAllAssetsAtPath(fbx))
                {
                    var c = a as AnimationClip;
                    if (c != null && c.name == clipName) return c;
                }
            }
#endif
            return null;
        }
    }
}
