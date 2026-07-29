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
    /// 対決ビューの絵作りパラメータ一式（カメラ構図＋空色＋シェーダのライト）。既定値は確定構図
    /// R-comp60（design-18 §7-1・2026-07-26改定）と DuelStageProxyLambert のシェーダ既定に一致し、
    /// 既定のままなら従来のハードコード時代と同じ絵になる。
    /// DuelCalibrationWindow（Assets/Editor）がPlay中にスライダで差し替え、確定値は Default へ焼き戻す運用。
    /// </summary>
    [System.Serializable]
    public struct DuelCalibration
    {
        /// <summary>スキーマ版。旧版のJSON・シリアライズ値は新項目が0埋めで来るため、Upgradeで既定へ補完する。</summary>
        public int Version;
        public float Dist;       // 注視点からの後退距離[m]（望遠圧縮の強さ。カメラz = マウンド + Dist）
        public float AimY;       // 注視点の高さ[m]
        public float AimZ;       // 注視点のz[m]（本塁=0・正=マウンド側）
        public float PitchDeg;   // 俯角[度]
        public float YawDeg;     // 横角[度]（正=既定と同じ側＝カメラx負）
        public float FovDeg;     // 16:9時の垂直FOV[度]（ApplyFovが縦横比から逆算する基準）
        public Color Sky;        // カメラ背景色（夏空）
        public float Ambient;    // シェーダ _Ambient（環境光の下限）
        public Vector3 LightDir; // シェーダ _LightDir（ワールド・面へ向かう向き）
        // 立ち位置[m]（ステージローカル。本塁=原点・+z=マウンド。yは接地系が持つため対象外）。
        // 投手はプレート位置がルール寸法（18.44m）なので較正対象にしない
        public float BatterX, BatterZ;
        public float CatcherX, CatcherZ;
        public float UmpireX, UmpireZ;
        // 被り物の装着オフセット[m]（gear-headコンテナ＝実寸・頭蓋基準。x=左右/y=頭蓋上/z=顔前）
        public Vector3 CapOffset;             // 投手の帽子
        public Vector3 HelmetOffset;          // 打者のヘルメット
        public Vector3 CatcherHelmetOffset;   // 捕手のヘルメット（殻）
        public Vector3 CatcherMaskOffset;     // 捕手のマスク（ケージ）
        // 胸プロテクター[m]（gear-chestコンテナ＝胸ボーン原点・+Z体前）。yは肩ボーン実測に載る補正ぶん
        public Vector3 CatcherChestOffset;
        /// <summary>
        /// 打者ヘルメットの被り角[度]（x=前後の傾き/y=ヨー/z=左右の傾き）。
        /// 回転の中心は**殻の中心**であって枠の原点（頭ボーン＝首の付け根）ではない。
        /// 首で回すと角度を変えるたびにヘルメットが振り出されて位置まで動き、
        /// 「向きだけ直す」ができないため（ApplyCalibration が中心を保つ補正を掛ける）。
        /// </summary>
        public Vector3 HelmetEuler;

        private const int CurrentVersion = 5;

        public static DuelCalibration Default => new DuelCalibration
        {
            Version = CurrentVersion,
            Dist = 60f,
            AimY = 0.8f,
            AimZ = 1f,
            // 俯角4.5°・横角3.8°: 旧ハードコードのオフセット係数（0.0782 / 0.0668）を角度へ逆算した値
            PitchDeg = Mathf.Atan(0.0782f) * Mathf.Rad2Deg,
            YawDeg = Mathf.Atan(0.0668f) * Mathf.Rad2Deg,
            FovDeg = 2.9f,
            Sky = new Color(0.55f, 0.78f, 0.90f),
            Ambient = 0.45f,
            LightDir = new Vector3(-0.35f, -0.85f, -0.4f),
            BatterX = -0.9f, BatterZ = 0f,
            CatcherX = 0f, CatcherZ = -1.0f,
            UmpireX = 0.35f, UmpireZ = -2.0f,   // #291のスロット位置
            CapOffset = Vector3.zero,
            HelmetOffset = Vector3.zero,
            CatcherHelmetOffset = Vector3.zero,
            CatcherMaskOffset = Vector3.zero,
            CatcherChestOffset = Vector3.zero,
            HelmetEuler = Vector3.zero,
        };

        /// <summary>
        /// 旧版の値を現行スキーマへ補完する。0埋めのままだと打者が本塁上（x=0）に立つ等の
        /// 壊れた絵になるため、読み込み・適用の入口で必ず通す。
        ///
        /// **版ごとに「その版で増えた項目だけ」を埋める**こと。全項目を一括で既定へ戻すと、
        /// 版を上げるたびに調整済みのJSONが白紙に戻る（ツールでライブ調整→JSONへ書き出す運用なので
        /// これは実害）。新項目を足すときは CurrentVersion を上げて、下に1ブロック足す。
        /// </summary>
        public static DuelCalibration Upgrade(DuelCalibration c)
        {
            if (c.Version >= CurrentVersion) return c;
            var d = Default;
            if (c.Version < 2)   // v2: 立ち位置と被り物オフセットを追加
            {
                c.BatterX = d.BatterX; c.BatterZ = d.BatterZ;
                c.CatcherX = d.CatcherX; c.CatcherZ = d.CatcherZ;
                c.UmpireX = d.UmpireX; c.UmpireZ = d.UmpireZ;
                c.CapOffset = d.CapOffset;
                c.HelmetOffset = d.HelmetOffset;
            }
            if (c.Version < 3)   // v3: 捕手のヘルメット・マスクを分離
            {
                c.CatcherHelmetOffset = d.CatcherHelmetOffset;
                c.CatcherMaskOffset = d.CatcherMaskOffset;
            }
            if (c.Version < 4)   // v4: 胸プロテクターを生成メッシュへ差し替え
                c.CatcherChestOffset = d.CatcherChestOffset;
            if (c.Version < 5)   // v5: 打者ヘルメットの被り角
                c.HelmetEuler = d.HelmetEuler;
            c.Version = CurrentVersion;
            return c;
        }
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
        /// 着衣メッシュ（DuelUniformBaker が焼いた U2 資産。design-18 §3.5）。
        /// 痩せ強度は Slim1（中＝がっしり型。肩・胴×0.90/上腕×0.84/腿×0.92）＝2026-07-28 比較シートで監督選定。
        /// 無ければ素体のまま出す（着衣は表示品質の話であり対決ビュー自体は止めない）。
        /// </summary>
        private const string UniformMeshAsset = "Assets/DuelSpike/Generated/Body_Uniform_Slim1.asset";
        private const string BodyMeshName = "SuperHero_Male";

        /// <summary>
        /// 購入素体（School Baseball Player 0001。design-18 §3.1 で再配布NG＝gitignore）。
        /// 投手・打者・捕手に使う。無い環境（他端末・CI）では自動でU2素体へ落ちる＝対決ビューは止めない。
        /// 球審だけはU2素体のまま残す（2026-07-29 決定）: 購入素体のシルエットは野球のユニフォームそのもので、
        /// 紺に塗っても審判のスラックス・短つば帽にはならないため。素体2系統の併存を許容する。
        /// </summary>
        private const string VendorBodyFbx =
            "Assets/DuelSpike/Vendor/SchoolBaseballPlayer0001/School_Baseball_Player_0001.fbx";

        /// <summary>DuelVendorRegionBaker が焼いた領域IDつきメッシュ（UV1に領域ID）。</summary>
        private const string VendorRegionMeshDir = "Assets/DuelSpike/Vendor/Generated/";
        private const string VendorRegionMeshSuffix = "_Regions.asset";

        /// <summary>購入素体の帽子（独立メッシュ）。打者はこれを消してヘルメットを付ける。</summary>
        private const string VendorCapMeshName = "A_Players_0001_Cap_0001";

        /// <summary>
        /// 役割別の領域配色（DuelUniformMeshBuilder.Region の並びで _RegionColors[8] に流し込む）。
        /// 実データはエンジンの <see cref="KokoSim.Engine.Nation.TeamColors"/>（data/team-colors.yaml 由来）で、
        /// ここは Unity の Color へ移した表現＋肌・シューズの固定色。
        ///
        /// <b>両校とも自校の配色で描く</b>（実物準拠・2026-07-29 監督決定）。旧実装は守備=白／打者=薄グレーの
        /// 定数で「輪郭が重なっても見分く」ことを保証していたが、実データ化でその保証は無くなった
        /// ＝紺同士の対戦では両者が似て見える。承知の上の仕様。
        /// </summary>
        private readonly struct UniformPalette
        {
            public readonly Color Shirt, Pants, Undershirt, Socks, Skin, Shoes, Cap, Belt;
            public readonly bool StripeOn;
            public readonly Color StripeColor;
            /// <summary>縞の本数[本/m]。シェーダへはアクターのスケールを掛けて渡す（RegionMaterial）。</summary>
            public readonly float StripeFreqPerMeter;

            public UniformPalette(Color shirt, Color pants, Color undershirt, Color socks, Color cap,
                bool stripeOn = false, Color stripeColor = default, float stripeFreqPerMeter = 0f)
            {
                Shirt = shirt;
                Pants = pants;
                Undershirt = undershirt;
                Socks = socks;
                Cap = cap;
                Skin = new Color(0.85f, 0.68f, 0.55f);
                Shoes = new Color(0.15f, 0.15f, 0.16f);
                // ベルトは固定の黒革（高校球児は黒ベルトが多数派）。靴と同じ暗さだと腰で消えるので、
                // 白ユニに対して**輪郭が立つ**ぶんだけ靴より濃くする
                Belt = new Color(0.10f, 0.09f, 0.09f);
                StripeOn = stripeOn;
                StripeColor = stripeColor;
                StripeFreqPerMeter = stripeFreqPerMeter;
            }

            /// <summary>エンジンの確定配色（6領域＋縞）から作る。つばは帽子と1メッシュなので使わない。</summary>
            public static UniformPalette From(KokoSim.Engine.Nation.TeamColors c) => new(
                Rgb(c.Shirt), Rgb(c.Pants), Rgb(c.Undershirt), Rgb(c.Socks), Rgb(c.CapCrown),
                c.Stripe.On, Rgb(c.Stripe.Color), (float)c.Stripe.FreqPerMeter);

            private static Color Rgb(KokoSim.Engine.Nation.RgbColor c)
                => new(c.R / 255f, c.G / 255f, c.B / 255f);

            /// <summary>チーム色が届かない場面（スクショ・デモ・engine未接続）の既定＝白ユニ＋紺。</summary>
            public static UniformPalette Default => new(
                new Color(0.93f, 0.92f, 0.88f), new Color(0.93f, 0.92f, 0.88f),
                new Color(0.16f, 0.20f, 0.38f), new Color(0.16f, 0.20f, 0.38f),
                new Color(0.16f, 0.20f, 0.38f));

            /// <summary>球審: 紺シャツ＋グレースラックス（実物の球審装束）。チーム色の対象外。</summary>
            public static UniformPalette Umpire => new(
                new Color(0.14f, 0.16f, 0.22f), new Color(0.38f, 0.39f, 0.42f),
                new Color(0.14f, 0.16f, 0.22f), new Color(0.14f, 0.16f, 0.22f),
                new Color(0.14f, 0.16f, 0.22f));
        }

        /// <summary>縦縞の線の太さ[m]（実物のピンストライプは間隔に依らず3mm前後の細線）。</summary>
        private const float StripeLineWidthM = 0.0035f;

        // 守備側（投手・捕手）と攻撃側（打者）の配色。役割は固定で、攻守交代で入れ替わるのは
        // 「どちらの学校の色か」の方＝SetTeamColors で塗り替える（アクターは作り直さない）
        private UniformPalette _defenseColors = UniformPalette.Default;
        private UniformPalette _offenseColors = UniformPalette.Default;

        /// <summary>
        /// 生成前に一度だけ渡す初期配色（ステージ構築時に読む）。null なら既定（白＋紺）。
        /// 構築後の変更は <see cref="SetTeamColors"/> を使う。
        /// </summary>
        public static (KokoSim.Engine.Nation.TeamColors Defense, KokoSim.Engine.Nation.TeamColors Offense)? InitialTeamColors { get; set; }

        /// <summary>
        /// 守備側・攻撃側の配色を差し替える（攻守交代のたびに呼ぶ）。素体とクリップは作り直さず
        /// マテリアルだけ塗り替える＝進行中のポーズ・アンカー同期を壊さない。
        /// </summary>
        public void SetTeamColors(KokoSim.Engine.Nation.TeamColors defense, KokoSim.Engine.Nation.TeamColors offense)
        {
            if (defense == null || offense == null) return;
            _defenseColors = UniformPalette.From(defense);
            _offenseColors = UniformPalette.From(offense);

            Repaint(_pitcher, _defenseColors);
            Repaint(_catcher, _defenseColors);
            Repaint(_batter, _offenseColors);   // 打者ヘルメットは暗紺固定＝チーム色の装備は無い
        }

        private static void Repaint(Actor a, UniformPalette pal)
        {
            if (a == null) return;
            if (a.RegionMat != null) ApplyRegionColors(a.RegionMat, pal, a.BodyScale);
            foreach (var m in a.TeamMats) if (m != null) m.SetColor("_BaseColor", pal.Cap);
        }

        /// <summary>
        /// 装着直後に「チーム色で塗った装備」のマテリアルを拾っておく（攻守交代で塗り替える対象）。
        /// 装備はプリミティブの寄せ集めで、同じ呼び出しの中に固定色（ケージの黒・パッド）も混ざるため、
        /// <b>色の一致で選ぶ</b>。装着時点の accent と厳密に同じ Color を入れているので取りこぼさない。
        /// </summary>
        private static void CollectTeamMats(Actor a, Color accent)
        {
            if (a == null || a.Go == null) return;
            foreach (var r in a.Go.GetComponentsInChildren<Renderer>(true))
            {
                var m = r.sharedMaterial;
                // 装備は必ず FlatMaterial（自前シェーダ）なので、_UseRegions を持たないマテリアル
                // （素体に残る URP Lit 等）は対象外。HasProperty で弾かないと毎フレーム警告が出る
                if (m == null || !m.HasProperty("_BaseColor") || !m.HasProperty("_UseRegions")) continue;
                // 素体の領域マテリアルは _BaseColor が白のダミー＝ここで拾ってはいけない
                // （チーム色に白を選ばれた瞬間に素体まで塗り替え対象になる）。RegionMat 側で塗り替わる
                if (m.GetFloat("_UseRegions") > 0.5f) continue;
                if (m.GetColor("_BaseColor") == accent && !a.TeamMats.Contains(m)) a.TeamMats.Add(m);
            }
        }

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

        /// <summary>
        /// 色数制限（ポスタライズ）の段数。0または1で無効＝素通し。
        /// 監督選定（2026-07-28・絵柄マトリクス）: **6段・輪郭なし** を採用既定とする。
        /// 実装は DuelStagePixelPost の Pass0 をカメラ出力→Target のブリット1枚で常時適用。
        /// </summary>
        public static float PosterizeLevels = 6f;

        private RenderTexture _colorRt;   // ポスト前のカメラ出力（ポスタライズ有効時のみ生成）
        private Material _postMat;
        public bool Available { get; private set; }

        private GameObject _root;
        private Camera _camera;
        private GameObject _bat;
        private readonly List<Actor> _actors = new();
        // 生成した全マテリアル（HideAndDontSave はドメインリロードを跨いで残るため、Disposeで明示破棄する。
        // 従来も漏れていたが、領域色でマテリアル数が増えるのを機に修正）
        private readonly List<Material> _materials = new();
        private Actor _pitcher, _batter, _catcher, _umpire;
        private float _acc;
        private float _fovBaseDeg = 2.9f;   // 16:9時の垂直FOV（ApplyCalibrationが上書き。既定=R-comp60）
        private Transform _capMount, _helmetMount;   // 被り物の位置オフセット枠（gear-head直下・実寸[m]）
        private Transform _cHelmetMount, _cMaskMount;   // 捕手のヘルメット・マスク（同上）
        private Transform _cChestMount;                 // 捕手の胸プロテクター（gear-chest直下）

        // 結果分岐の一発物クリップ（design-18 §7-2 第2弾。無いものはnull＝その分岐は待機のまま）
        private AnimationClip _swingMissClip, _swingHitClip, _hbpClip, _catchClip;

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
            public float HoldSeconds;                // 末尾到達時、待機へ戻る前にそのポーズで止める秒数（0=即戻る）
            public float HoldRemaining;               // ↑の残り時間（>0の間はポーズ据え置き＝Graph.Evaluateしない）
            public Material RegionMat;               // 領域塗り分けマテリアル（攻守交代の塗り替え用。一色フォールバック時はnull）
            public float BodyScale;                  // ワールドスケール（縞の刻みをオブジェクト空間へ換算するのに要る）
            public readonly List<Material> TeamMats = new();  // チーム色で塗った装備のマテリアル（帽子・捕手ヘルメット殻）
            public Vector3 HeadUpLocal;              // 頭蓋の上方向（頭ボーンローカル）。素立ち＝頭が水平な唯一のポーズで実測
            public Vector3 HeadFwdLocal;             // 頭蓋の前方向（同上）。視線ターゲット由来の水平前だとロールする（斜め被り）ため実測で持つ
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

            // チーム配色は素体を作る前に確定させる（SpawnActor がマテリアルを焼くため）。
            // 未指定＝スクショ・デモ経路は既定（白＋紺）で立つ
            if (InitialTeamColors is { } tc)
            {
                _defenseColors = UniformPalette.From(tc.Defense);
                _offenseColors = UniformPalette.From(tc.Offense);
            }

            // 立ち位置・装備位置は絵作りパラメータ（DuelCalibration.Default が単一ソース。
            // 球審の#291スロット位置の経緯コメントも Default 側の値に対応する）。
            // スポーン位置から較正値で置く＝装備の視線ベイク（AttachGear）が較正後の位置で走る
            var cal0 = DuelCalibration.Upgrade(CalibrationOverride ?? DuelCalibration.Default);

            // yawはクリップごとの素の向き実測（DuelMotionSpike.RunFacingProbe）から:
            // 投手クリップ（自作Rokoko）は素で+Z向き＝PitcherYaw、脇3クリップ（購入AA）も素で+Z向き
            // 投手・打者・捕手は購入素体（無い環境ではU2素体へ自動フォールバック）。球審だけはU2素体のまま。
            var vendor = LoadVendorBody();
            bool useVendor = vendor != null;
            var playerBody = useVendor ? vendor : body;

            _pitcher = SpawnActor(playerBody, pitchClip, new Vector3(0f, MoundHeight, MoundDistance), PitcherYaw,
                _defenseColors, MoundHeight, layer, useVendor);
            _pitcher.HoldAtEnd = true;   // 投手は投げ切ったらセット姿勢で次のアンカー同期を待つ（勝手にループしない）
            _batter = SpawnActor(playerBody, batClip, new Vector3(cal0.BatterX, 0f, cal0.BatterZ), 90f,
                _offenseColors, 0f, layer, useVendor);
            // 購入素体は帽子を素で持っている。打者はヘルメットを被せるので、その帽子を落とす
            if (useVendor) SetChildActive(_batter.Go, VendorCapMeshName, false);
            _batter.IdleClip = batClip;
            _batter.IdleEndAt = BatterIdleEnd;
            _batter.EndAt = BatterIdleEnd;   // 生成直後の初回ループでスイングまで流さない
            _catcher = SpawnActor(playerBody, catClip, new Vector3(cal0.CatcherX, 0f, cal0.CatcherZ), 0f,
                _defenseColors, 0f, layer, useVendor);
            // 捕手も素の帽子を落とす。日本の高校野球の捕手は「捕手用ヘルメット＋マスク」で、
            // 帽子の上にマスクを重ねる形ではない（帽子を残すと頭が二重に膨らむ 2026-07-29 監督指摘）。
            // ヘルメットとマスクは CatcherGear 側（殻＋ケージバー）が受け持つ
            if (useVendor) SetChildActive(_catcher.Go, VendorCapMeshName, false);
            _catcher.IdleClip = catClip;
            // 球審は捕手の真後ろ・打者（x=-0.9側）から遠い肩越しに覗く「スロット」位置（#291）。
            // 旧値(-0.35,-1.7)は捕手とのz間隔0.7mしか無く、しゃがみ姿勢同士の胴回りでめり込んで見えていた。
            // z間隔を1.0mへ広げ、x offsetは打者と反対側（+x）へ倒して基準構図R-comp60から見て
            // 捕手の背後に自然に収まるようにする。
            _umpire = SpawnActor(body, umpClip, new Vector3(cal0.UmpireX, 0f, cal0.UmpireZ), 0f,
                UniformPalette.Umpire, 0f, layer);

            // 結果分岐の一発物（§7-2第2弾）。素の向きは打者系/捕手系とも+Z（RunFacingProbe実測の規約どおり）。
            // 空振り/ミートとも自作打撃クリップの同じスイング区間を使う（収録が1本なので分岐を持てない）。
            // 差は打ち切り位置だけ＝空振り・ファールは走り出し前で構えへ戻す（ScheduleReactions）
            _swingMissClip = batClip;
            _swingHitClip = batClip;
            _hbpClip = LoadClip("AA_Baseball_Batter_Hit_ByPitch_Type01");
            _catchClip = LoadClip("AA_Baseball_Catcher_Catching_C");

            _bat = SpawnBat(layer);

            Target = CreateRt(BaseWidth, BaseHeight);

            var camGo = new GameObject("StageCamera");
            camGo.transform.SetParent(_root.transform, false);
            _camera = camGo.AddComponent<Camera>();
            _camera.cullingMask = 1 << layer;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.nearClipPlane = 0.1f;
            _camera.farClipPlane = 200f;
            _camera.targetTexture = Target;

            // ポスタライズ常設（監督選定 2026-07-28: 6段・輪郭なし）。カメラは中間RTへ描き、
            // カメラ描画完了フックで Pass0 を当てて Target へ落とす。シェーダ欠落時は従来どおり素通し
            var postShader = Shader.Find("KokoSim/DuelStagePixelPost");
            if (postShader != null)
            {
                _postMat = new Material(postShader) { hideFlags = HideFlags.HideAndDontSave };
                _colorRt = CreateRt(BaseWidth, BaseHeight);
                _camera.targetTexture = _colorRt;
                UnityEngine.Rendering.RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
            }

            // 初期ポーズを1回打っておく（トグル直後の素立ちTポーズを見せない）
            foreach (var a in _actors) StepActor(a, 0.001f);
            GroundFix(_pitcher);
            AlignBat();

            // 装備（design-18 §3.3: 打者=ヘルメット/守備=帽子）。初期ポーズ評価後に
            // 視線ターゲット（投手→本塁、他3者→マウンド）から向きを実測して焼き付ける（AttachGear仕様）。
            // 投手だけはセット姿勢の顔が投球方向と約68°ずれる（PitcherYawの経緯と同じ収録特性）ため、
            // 顔が確実に本塁を向くリリース時刻の姿勢で焼き、初期姿勢へ巻き戻す
            var mound = new Vector3(0f, MoundHeight, MoundDistance);
            SeekActor(_pitcher, ReleaseTime);
            // 購入素体は帽子を素で持っている（頭ボーン直付けの剛体メッシュ）ので生成帽子は載せない＝二重帽子を避ける
            if (!useVendor)
                AttachGear(_pitcher, GearKind.Cap, Vector3.zero, _defenseColors.Cap, layer);
            SeekActor(_pitcher, 0.001f);
            GroundFix(_pitcher);
            // ヘルメットはチーム色でなく暗紺: 実際の高校野球のヘルメットは黒/紺が大半で、
            // チーム色（えんじ等）だと肌の顔と並んで「髪」に見える（2026-07-28 スクショで実害）
            // ヘルメットの紺は壁(0.12,0.20,0.16)と同輝度だと頭が背景に溶けて「頭が無い」絵になる
            // （2026-07-28監督指摘）。ポスタライズ6段で2段ぶん明るい紺にして輪郭を立てる
            AttachGear(_batter, GearKind.Helmet, mound, new Color(0.28f, 0.31f, 0.45f), layer);
            AttachGear(_catcher, GearKind.CatcherGear, mound, _defenseColors.Cap, layer);
            AttachGear(_umpire, GearKind.UmpireGear, mound, new Color(0.13f, 0.13f, 0.15f), layer);

            // 攻守交代で塗り替える装備を拾っておく（投手の帽子＝U2経路のみ／捕手のヘルメット殻）。
            // 購入素体の帽子は素体メッシュの領域なので、ここではなく RegionMat 側で替わる
            CollectTeamMats(_pitcher, _defenseColors.Cap);
            CollectTeamMats(_catcher, _defenseColors.Cap);

            // グラブ（被り物と同じく初期ポーズ評価後＝視線実測が要るため）。利き手でない側に着ける。
            // 投手は 2026-07-29 まで**グラブを持っていなかった**（AttachMitt が捕手専用だった）
            // 捕手ミットは野手グラブと別メッシュ（指の区切りが無く縁が厚い＝流用すると捕手に見えない）
            AttachGlove(_catcher, HumanBodyBones.LeftHand, HumanBodyBones.LeftMiddleProximal,
                mound, MittSize, layer, DuelGearMeshes.Mitt());
            AttachGlove(_pitcher, HumanBodyBones.LeftHand, HumanBodyBones.LeftMiddleProximal,
                Vector3.zero, GloveSize, layer, DuelGearMeshes.Glove());

            // 絵作り一式（構図R-comp60・夏空・ライト・被り物オフセット）。数値の実体は
            // DuelCalibration.Default に集約し、キャリブレーションツール（DuelCalibrationWindow）が
            // 上書き中ならその値で作る。全マテリアル・装備が出揃った後に呼ぶ＝漏れなく反映する
            ApplyCalibration(cal0);

            Available = true;
            Live = this;
        }

        /// <summary>生きている対決ステージ（Play中に対決ビューを開いている間だけ非null）。キャリブレーションツールの入口。</summary>
        public static DuelStageRig Live { get; private set; }

        /// <summary>
        /// キャリブレーションツールの上書き値（null=既定）。設定中はrigを作り直しても
        /// （打席送り・RebuildDuelRigForCapture）調整中の見た目が維持される。
        /// </summary>
        public static DuelCalibration? CalibrationOverride;

        /// <summary>
        /// 絵作りパラメータを反映する（生成時と、キャリブレーションツールのスライダ操作から毎回）。
        /// カメラ位置は「注視点(0, AimY, AimZ)から俯角・横角の方向へ後退」の逆算で、
        /// 既定値なら従来のハードコード定数（-dz*0.0668 / +dz*0.0782）と同じ位置になる。
        /// </summary>
        public void ApplyCalibration(DuelCalibration cal)
        {
            if (_camera == null) return;
            cal = DuelCalibration.Upgrade(cal);
            _fovBaseDeg = cal.FovDeg;
            _camera.backgroundColor = cal.Sky;
            float camZ = MoundDistance + cal.Dist;
            float dz = camZ - cal.AimZ;
            _camera.transform.localPosition = new Vector3(
                -dz * Mathf.Tan(cal.YawDeg * Mathf.Deg2Rad),
                cal.AimY + dz * Mathf.Tan(cal.PitchDeg * Mathf.Deg2Rad),
                camZ);
            _camera.transform.LookAt(_root.transform.position + new Vector3(0f, cal.AimY, cal.AimZ));
            ApplyFov();
            foreach (var m in _materials)
            {
                if (m == null) continue;
                m.SetFloat("_Ambient", cal.Ambient);
                m.SetVector("_LightDir", cal.LightDir);
            }
            // 立ち位置（x/zのみ・yは接地系）。装備の視線ベイクはスポーン時の位置のままなので、
            // ライブ移動中はつば等の向きがわずかに古い＝焼き戻し後の rig 再生成で正しくベイクされる
            SetActorXZ(_batter, cal.BatterX, cal.BatterZ);
            SetActorXZ(_catcher, cal.CatcherX, cal.CatcherZ);
            SetActorXZ(_umpire, cal.UmpireX, cal.UmpireZ);
            if (_capMount != null) _capMount.localPosition = cal.CapOffset;
            if (_helmetMount != null)
            {
                // 被り角は**殻の中心**を軸に回す。枠の原点（頭ボーン＝首の付け根）で回すと
                // 角度を1度いじるたびにヘルメットが弧を描いて位置まで動き、
                // 「位置は決まったので向きだけ直す」ができない。中心を保つぶんを位置で打ち消す
                var helmetRot = Quaternion.Euler(cal.HelmetEuler);
                _helmetMount.localRotation = helmetRot;
                _helmetMount.localPosition =
                    cal.HelmetOffset + HelmetPivot - helmetRot * HelmetPivot;
            }
            if (_cHelmetMount != null) _cHelmetMount.localPosition = cal.CatcherHelmetOffset;
            if (_cMaskMount != null) _cMaskMount.localPosition = cal.CatcherMaskOffset;
            if (_cChestMount != null) _cChestMount.localPosition = cal.CatcherChestOffset;
        }

        /// <summary>
        /// 立ち位置のx/zだけ差し替える。現在位置とBasePosの両方へ同じ差分を足す＝
        /// 巻き戻し（RewindActor）・待機ドリフト補正（StepActor）の基準ごと動かす。
        /// </summary>
        private static void SetActorXZ(Actor a, float x, float z)
        {
            if (a == null) return;
            var delta = new Vector3(x - a.BasePos.x, 0f, z - a.BasePos.z);
            if (delta.sqrMagnitude < 1e-10f) return;
            a.BasePos += delta;
            a.Go.transform.localPosition += delta;
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
        private const float HbpReact = 1.3f;
        // 空振り後、振り切りポーズで据え置く秒数（#291。目安0.6〜1.0sの中間・duelPitchIntervalSeconds=4.5sに余裕で収まる）
        private const float SwingMissHoldSeconds = 0.8f;
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
                    _batter.HoldSeconds = SwingMissHoldSeconds;   // 振り切りポーズでしばらく止めてから待機へ戻す
                    break;
                case DuelPitchOutcome.FoulContact:
                    // 走り出し（実測3.25s）前でカットして打席へ戻る
                    SetActorClip(_batter, _swingHitClip, SwingHitContact - BallFlightSeconds, BatterRunStart);
                    break;
                case DuelPitchOutcome.InPlayContact:
                    SetActorClip(_batter, _swingHitClip, SwingHitContact - BallFlightSeconds, 0f);
                    break;
                // CalledStrike（見逃し）は打者を待機のままにする＝ここで何もしない（#291）。
                // 捕手の捕球は下の catcherCatches に含めたまま残す
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
            a.HoldSeconds = 0f;      // 新しいクリップへの差し替えは保留中のホールド予約を打ち消す
            a.HoldRemaining = 0f;
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
            // 縦横比クランプ: 4:3より縦長・2.4:1より横長にはしない（壁の高さ・構図の担保範囲）。
            // ただし横の上限は**試合の構図を守るため**のものなので、FramePreview で構図を組み直した
            // プレビューには効かせない（効かせると欄の右端に緑の帯が残る。2026-07-29 実測）
            height = Mathf.Min(height, Mathf.FloorToInt(width * 0.75f));
            width = Mathf.Min(width, Mathf.FloorToInt(height * (_previewFrameH > 0f ? 8f : 2.4f)));
            if (Target != null && Target.width == width && Target.height == height) return;

            if (_camera != null) _camera.targetTexture = null;
            if (Target != null) { Target.Release(); Object.DestroyImmediate(Target); }
            Target = CreateRt(width, height);
            if (_colorRt != null)
            {
                _colorRt.Release(); Object.DestroyImmediate(_colorRt);
                _colorRt = CreateRt(width, height);   // ポスト前RTはTargetと常に同寸
            }
            if (_camera != null)
            {
                _camera.targetTexture = _colorRt != null ? _colorRt : Target;
                ApplyFov();
            }
        }

        /// <summary>ステージカメラの描画完了直後にポスタライズを当てて Target へ落とす（毎フレーム）。</summary>
        private void OnEndCameraRendering(UnityEngine.Rendering.ScriptableRenderContext ctx, Camera cam)
        {
            if (cam != _camera || _postMat == null || _colorRt == null || Target == null) return;
            if (PosterizeLevels < 2f)
            {
                Graphics.Blit(_colorRt, Target);
                return;
            }
            _postMat.SetFloat("_PosterLevels", PosterizeLevels);
            Graphics.Blit(_colorRt, Target, _postMat, 0);
        }

        private static RenderTexture CreateRt(int width, int height)
        {
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
            { name = "DuelStageLiveRT", filterMode = FilterMode.Point };
            rt.Create();
            return rt;
        }

        /// <summary>水平視野角を基準構図（垂直2.9°@16:9）で固定し、現RT縦横比から垂直FOVを逆算する。</summary>
        /// <summary>
        /// 【プレビュー用】1人だけを直立で立たせる（チーム設定画面の配色確認）。
        ///
        /// 立たせるのは<b>投手</b>。打者はヘルメット（暗紺固定）で帽子が見えず、捕手は防具で
        /// ユニが隠れるため、配色の対象（上・ズボン・アンダー・ソックス・帽子）が全部見えるのは投手だけ。
        /// 他の3人とバットは消す＝配色以外の情報を画から追い出す。
        /// クリップは購入パックの直立ループへ差し替える（投球モーションのままだと絵が動きすぎる）。
        /// </summary>
        /// <returns>直立クリップが無くて組めなければ false（呼び元はそのままの絵で続けてよい）。</returns>
        public bool PosePreviewStanding()
        {
            if (!Available || _pitcher == null) return false;
            var clip = LoadClip("AA_Baseball_Defender_Standing_Idle_Loop_Type01");
            if (clip == null) return false;

            if (_batter != null) _batter.Go.SetActive(false);
            if (_catcher != null) _catcher.Go.SetActive(false);
            if (_umpire != null) _umpire.Go.SetActive(false);
            if (_bat != null) _bat.SetActive(false);

            _pitcher.HoldAtEnd = false;   // 投球用の「投げ切って止まる」を解除してループさせる
            _pitcher.EndAt = 0f;
            _pitcher.IdleClip = clip;
            _pitcher.IdleEndAt = 0f;
            SwapClip(_pitcher, clip);
            StepActor(_pitcher, 0.001f);
            GroundFix(_pitcher);
            return true;
        }

        /// <summary>再生中のクリップを差し替える（Playable を作り直す。SpawnActor の組み立てと同じ形）。</summary>
        private static void SwapClip(Actor a, AnimationClip clip)
        {
            if (a == null || !a.Graph.IsValid()) return;
            a.Clip = clip;
            a.Time = 0f;
            a.Holding = false;
            a.HoldRemaining = 0f;
            if (a.Playable.IsValid()) a.Playable.Destroy();
            a.Playable = AnimationClipPlayable.Create(a.Graph, clip);
            a.Output.SetSourcePlayable(a.Playable);
        }

        /// <summary>
        /// 【プレビュー用】カメラを任意の位置・注視点へ置く（ステージ原点ローカル・m）。
        ///
        /// <see cref="ApplyCalibration"/> のカメラは「マウンド後方から本塁を見る」構図に固定されていて
        /// <b>横へ回れない</b>（YawDeg は x をずらすだけ）。打者を正面から見るような別構図が要る
        /// チーム設定画面のために、構図だけを直接指定できる入口を分けてある。
        /// 対決ビュー本体はこれを呼ばない＝§7-1 の構図は不変。
        /// </summary>
        /// <param name="frameHeightM">
        /// 注視点の距離で画面の縦に収めたい実寸[m]。角度でなく寸法で指定するのは、
        /// 欄の縦横比が変わっても被写体の大きさを変えないため（カメラは水平画角を保つ実装なので、
        /// 画角を固定すると横長の欄では上下が切れる）。
        /// </param>
        public void FramePreview(Vector3 camLocalPos, Vector3 lookLocalPos, float frameHeightM)
        {
            if (_camera == null) return;
            _camera.transform.localPosition = camLocalPos;
            var look = _root.transform.position + lookLocalPos;
            _camera.transform.LookAt(look);
            _previewFrameH = frameHeightM;
            _previewDist = Vector3.Distance(_camera.transform.position, look);
            ApplyFov();
        }

        /// <summary>プレビュー枠（縦に収めたい実寸[m]と注視距離[m]）。0＝試合の構図＝FovDeg をそのまま使う。</summary>
        private float _previewFrameH;
        private float _previewDist;

        private void ApplyFov()
        {
            float aspect = Target.width / (float)Target.height;
            if (_previewFrameH > 0f && _previewDist > 0.01f)
            {
                // 収めたい高さ[m]→実効の垂直画角→16:9基準へ戻す（RTが変わるたびに引き直す）
                float halfV = Mathf.Atan(_previewFrameH * 0.5f / _previewDist);
                _fovBaseDeg = 2f * Mathf.Atan(Mathf.Tan(halfV) * aspect / (16f / 9f)) * Mathf.Rad2Deg;
            }
            float halfH = Mathf.Tan(_fovBaseDeg * 0.5f * Mathf.Deg2Rad) * (16f / 9f);
            _camera.fieldOfView = 2f * Mathf.Atan(halfH / aspect) * Mathf.Rad2Deg;
        }

        public void Dispose()
        {
            foreach (var a in _actors)
            {
                if (a.Graph.IsValid()) a.Graph.Destroy();
            }
            _actors.Clear();
            foreach (var m in _materials) if (m != null) Object.DestroyImmediate(m);
            _materials.Clear();
            foreach (var m in _meshes) if (m != null) Object.DestroyImmediate(m);
            _meshes.Clear();
            if (_postMat != null)
            {
                UnityEngine.Rendering.RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
                Object.DestroyImmediate(_postMat); _postMat = null;
            }
            if (_camera != null) _camera.targetTexture = null;
            if (_colorRt != null) { _colorRt.Release(); Object.DestroyImmediate(_colorRt); _colorRt = null; }
            if (Target != null) { Target.Release(); Object.DestroyImmediate(Target); Target = null; }
            if (_root != null) { Object.DestroyImmediate(_root); _root = null; }
            Available = false;
            if (Live == this) Live = null;
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

        private Material FlatMaterial(Color color)
        {
            // シーンライト非依存の自前Lambert（Assets/DuelSpike/DuelStageProxyLambert.shader）
            var mat = new Material(Shader.Find("KokoSim/DuelStageProxyLambert")) { hideFlags = HideFlags.HideAndDontSave };
            mat.SetColor("_BaseColor", color);
            _materials.Add(mat);
            return mat;
        }

        /// <summary>着衣メッシュの領域塗り分けマテリアル（_RegionColors は Properties 宣言不可＝配列セット）。</summary>
        private Material RegionMaterial(UniformPalette pal)
        {
            var mat = FlatMaterial(Color.white);
            mat.SetFloat("_UseRegions", 1f);
            ApplyRegionColors(mat, pal, 1f);
            return mat;
        }

        /// <summary>
        /// 領域色と縦縞をマテリアルへ流し込む（生成時と、攻守交代での塗り替え <see cref="SetTeamColors"/> の共通処理）。
        /// </summary>
        /// <param name="objectScale">
        /// アクターのワールドスケール。縞はオブジェクト空間で刻む（ワールドで刻むと体が縞の中を滑る）ので、
        /// データ側の「本/m」をオブジェクト空間の本数へ換算するのに要る。
        /// </param>
        private static void ApplyRegionColors(Material mat, UniformPalette pal, float objectScale)
        {
            var colors = new Color[8];
            colors[(int)DuelUniformMeshBuilder.Region.Shirt] = pal.Shirt;
            colors[(int)DuelUniformMeshBuilder.Region.Pants] = pal.Pants;
            colors[(int)DuelUniformMeshBuilder.Region.Undershirt] = pal.Undershirt;
            colors[(int)DuelUniformMeshBuilder.Region.Socks] = pal.Socks;
            colors[(int)DuelUniformMeshBuilder.Region.Skin] = pal.Skin;
            colors[(int)DuelUniformMeshBuilder.Region.Shoes] = pal.Shoes;
            colors[(int)DuelUniformMeshBuilder.Region.Cap] = pal.Cap;
            colors[(int)DuelUniformMeshBuilder.Region.Belt] = pal.Belt;
            mat.SetColorArray("_RegionColors", colors);

            mat.SetFloat("_StripeOn", pal.StripeOn ? 1f : 0f);
            mat.SetColor("_StripeColor", pal.StripeColor);
            mat.SetFloat("_StripeFreq", pal.StripeFreqPerMeter * objectScale);
            // 線の太さは比率でなく**実寸**で決める（実物のピンストライプは間隔に関係なく3mm前後の細線。
            // 比率固定にすると間隔を広げたとき線まで太くなり、縞でなく色帯になる）
            mat.SetFloat("_StripeDuty",
                Mathf.Clamp(StripeLineWidthM * pal.StripeFreqPerMeter, 0.02f, 0.5f));
        }

        // ---- 素体 -------------------------------------------------------------

        private Actor SpawnActor(GameObject bodyPrefab, AnimationClip clip, Vector3 pos, float yawDeg,
            UniformPalette pal, float groundY, int layer, bool vendorBody = false)
        {
            var go = Object.Instantiate(bodyPrefab);
            go.name = "body-" + clip.name;
            go.transform.SetParent(_root.transform, false);

            // 領域色で塗れる状態にする。素体2系統で作りが違う:
            //   購入素体 … 帽子・上衣・ズボン等がもともと別メッシュ＝領域IDつきベイク資産へ差し替えるだけ
            //   U2素体   … 素体1枚に着衣メッシュ（DuelUniformBaker のベイク資産）を被せ、目・眉は消す
            //              （顔なし方針。従来は肌と同色マテリアルで偶然隠れていただけ＝領域色化で露出する）
            // どちらも bindposes/boneWeights は原本のままなのでボーン配列・Animatorはそのまま効く
            bool regionReady;
            if (vendorBody)
            {
                regionReady = ApplyVendorRegionMeshes(go);
            }
            else
            {
                var uniformMesh = LoadUniformMesh();
                foreach (var smr0 in go.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    if (smr0.name == BodyMeshName && uniformMesh != null) smr0.sharedMesh = uniformMesh;
                    else if (smr0.name != BodyMeshName) smr0.gameObject.SetActive(false);
                }
                regionReady = uniformMesh != null;
            }

            var rs = go.GetComponentsInChildren<Renderer>();
            var b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            float scale = TargetHeight / Mathf.Max(0.01f, b.size.y);
            go.transform.localScale = Vector3.one * scale;
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(0f, yawDeg, 0f);

            // UV1の領域IDで塗り分け、領域資産が揃わない時だけ肌色一色へ落とす
            var mat = regionReady ? RegionMaterial(pal) : FlatMaterial(pal.Skin);
            if (regionReady) ApplyRegionColors(mat, pal, scale);   // 縞の刻みはスケール確定後でないと合わない
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
                RegionMat = regionReady ? mat : null,   // 攻守交代の塗り替え（SetTeamColors）用
                BodyScale = scale,
            };
            var lf = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            var rf = animator.GetBoneTransform(HumanBodyBones.RightFoot);
            if (lf != null && rf != null)
                actor.AnkleOffset = Mathf.Min(lf.position.y, rf.position.y) - go.transform.position.y;

            // 頭蓋の上・前方向をボーンローカルで実測（AttachGear用）。クリップ評価前＝素立ちTポーズは
            // 頭が水平・顔がルート正面と保証される唯一のポーズ。頭ボーンのローカル軸は素体依存で
            // 信用できないため、「Tポーズのワールド上向き／ルート正面」を逆変換して持ち、
            // 装着時に現ポーズへ順変換して使う。前方向も実測するのは、視線ターゲットの水平投影で
            // 前を決めると頭が傾いた姿勢で枠がロールし「帽子の斜め被り」になるため（2026-07-28実害）
            var headBone = animator.GetBoneTransform(HumanBodyBones.Head);
            actor.HeadUpLocal = headBone != null
                ? headBone.InverseTransformDirection(Vector3.up) : Vector3.up;
            // バインドポーズの顔向きは素体依存で符号が逆になる:
            //   UBC素体   … 顔がルート **-Z** 向き（+Zで測るとキャップが後ろ被りになる実測 2026-07-28）
            //   購入素体  … 顔がルート **+Z** 向き（2026-07-29 実測。正面レンダで確認）
            // 間違えると被り物が180°回って後ろ被りになるだけで、エラーにはならず絵でしか気づけない
            var faceFwd = vendorBody ? go.transform.forward : -go.transform.forward;
            actor.HeadFwdLocal = headBone != null
                ? headBone.InverseTransformDirection(faceFwd) : Vector3.forward;

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
        /// HoldSeconds が設定されている場合は、末尾到達時にそのポーズのまま HoldSeconds 秒だけ据え置いてから
        /// 待機へ戻す（空振りの振り切りポーズ用。design-18の掟によりクリップ内部の緩急は変えず、
        /// 単に再生を一時停止するだけ）。
        /// </summary>
        private void StepActor(Actor a, float step)
        {
            // Play停止・ドメインリロードは HideAndDontSave の素体を先に壊すので、Tick が
            // 生き残っている間だけ例外が出る。呼び元（Update・撮影ハーネス）に後始末を強いるより
            // ここで黙って降りる方が安全（2026-07-29 プレビュー撮影で実害）
            if (a == null || a.Go == null) return;
            if (a.Holding) return;
            if (a.HoldRemaining > 0f)
            {
                a.HoldRemaining -= step;
                if (a.HoldRemaining > 0f) return;
                FinishOneShot(a);
                return;
            }
            a.Time += step;
            float end = a.EndAt > 0f && a.EndAt < a.Clip.length ? a.EndAt : a.Clip.length;
            if (a.Time >= end)
            {
                if (a.HoldSeconds > 0f)
                {
                    a.HoldRemaining = a.HoldSeconds;
                    a.HoldSeconds = 0f;
                    return;   // 振り切りポーズのまま据え置き（Graph.Evaluateしない＝ポーズが進まない）
                }
                FinishOneShot(a);
                return;
            }
            a.Graph.Evaluate(step);
            // 打者の待機ループは自作打撃クリップの構え区間にわずかな水平ルートモーションが乗っており、
            // 毎コマ蓄積すると打席内で左右にドリフトして見える（#291）。待機中（構え区間ループ中）だけ
            // 基準位置のx/zへ毎コマ戻す。スイング区間（EndAt!=IdleEndAt）は踏み込みの見た目を残すため触らない
            if (a == _batter && a.Clip == a.IdleClip && a.EndAt == a.IdleEndAt)
            {
                var lp = a.Go.transform.localPosition;
                lp.x = a.BasePos.x;
                lp.z = a.BasePos.z;
                a.Go.transform.localPosition = lp;
            }
        }

        /// <summary>末尾（またはホールド終了）到達時の後始末: 待機ループへ戻す、または恒久停止（投手）。</summary>
        private void FinishOneShot(Actor a)
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

        /// <summary>
        /// バットの断面（軸方向t=0がノブ端・1がバレル端／半径[m]）。実物84cm級の寸法。
        /// 一様円柱だとグリップが直径5.5cmの寸胴になり、拳が回り込めず「握れていない」絵になる
        /// （2026-07-29 寄り検証）。design-18 §3.3 の「先細り円柱」をこの断面で満たす。
        /// </summary>
        private static readonly Vector2[] BatProfile =
        {
            new(0.000f, 0.024f),   // ノブ端
            new(0.025f, 0.025f),   // ノブ
            new(0.045f, 0.016f),   // グリップ（実物のハンドルはø3.2cm前後）
            new(0.450f, 0.017f),
            new(0.620f, 0.027f),   // テーパー
            new(0.800f, 0.033f),   // バレル
            new(0.965f, 0.033f),
            new(1.000f, 0.026f),   // 先端の丸め
        };

        private const float BatHalfLength = 0.425f;   // 全長0.85m。AlignBat の位置計算もこの値を使う
        private const int BatSides = 8;               // ピクセル化後はシルエットだけなので8角で足りる

        private GameObject SpawnBat(int layer)
        {
            var bat = new GameObject("prop-bat");
            bat.transform.SetParent(_root.transform, false);
            var batMesh = BuildBatMesh();
            _meshes.Add(batMesh);
            bat.AddComponent<MeshFilter>().sharedMesh = batMesh;
            bat.AddComponent<MeshRenderer>().sharedMaterial = FlatMaterial(new Color(0.78f, 0.62f, 0.42f));
            bat.layer = layer;
            return bat;
        }

        /// <summary>BatProfile を軸まわりに回した実寸メッシュ（軸+Y・原点が中央・localScaleは1のまま使う）。</summary>
        private static Mesh BuildBatMesh()
        {
            int rings = BatProfile.Length;
            var verts = new Vector3[rings * BatSides];
            var tris = new List<int>((rings - 1) * BatSides * 6 + (BatSides - 2) * 6);
            for (int r = 0; r < rings; r++)
            {
                float y = Mathf.Lerp(-BatHalfLength, BatHalfLength, BatProfile[r].x);
                float rad = BatProfile[r].y;
                for (int s = 0; s < BatSides; s++)
                {
                    float a = s * Mathf.PI * 2f / BatSides;
                    verts[r * BatSides + s] = new Vector3(Mathf.Cos(a) * rad, y, Mathf.Sin(a) * rad);
                }
            }
            for (int r = 0; r < rings - 1; r++)
                for (int s = 0; s < BatSides; s++)
                {
                    int s2 = (s + 1) % BatSides;
                    int a = r * BatSides + s, b = r * BatSides + s2;
                    int c = (r + 1) * BatSides + s, d = (r + 1) * BatSides + s2;
                    tris.Add(a); tris.Add(c); tris.Add(b);
                    tris.Add(b); tris.Add(c); tris.Add(d);
                }
            // 両端の蓋（扇状）。開いたままだと内側が抜けて見える
            for (int s = 1; s < BatSides - 1; s++)
            {
                tris.Add(0); tris.Add(s); tris.Add(s + 1);
                int top = (rings - 1) * BatSides;
                tris.Add(top); tris.Add(top + s + 1); tris.Add(top + s);
            }
            var mesh = new Mesh { name = "prop-bat (generated)", hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = verts;
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>生成した使い捨てメッシュ（HideAndDontSave はドメインリロードを跨いで残るのでDisposeで明示破棄する）。</summary>
        private readonly List<Mesh> _meshes = new();

        // 片手フォロースルー判定: 両手ボーン間隔がグリップ間隔を明確に超えたら上の手が離れたとみなす。
        // 素体UBCの両手ボーン間隔は「両手で握っていても」構えで~0.23mある（RunHandGapProbe実測）ため、
        // 判定はスイング系クリップに限定する（構え/見逃し/死球は常に両手整列＝素の見た目を信じる）
        private const float HandsApartThreshold = 0.28f;
        private const float BatDropTime = BatterRunStart;   // 走り出し（実測3.25s）からバットを放った扱いで非表示
        // 構えのバット仰角（水平から）。手→手の実測線はこの素体だと拳が積み重ならず横に開くため
        // 構えでは軸が寝てバットが水平に見える（監督指摘 2026-07-28）→ ヨーだけ実測から拾い、仰角は立てる
        private const float StanceBatElevationDeg = 60f;
        // 振り抜き後の巻き付き仰角。振り切りポーズの手→手実測線はほぼ投手方向＝カメラ軸を向き、
        // 円柱が数pxの点に潰れて「バットが消えた」絵になる（2026-07-28 プローブ実測）。
        // 実物はバレルがリード肩越しに捕手側へ巻き付くので、逆ヨー×この仰角の合成軸で表す
        private const float FollowBatElevationDeg = 35f;
        private const float GripBelowHalf = 0.15f;   // グリップ中点からノブ端までの距離（実物: 拳2個+ノブ余り）
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

            // インプレーの走り出し以降はバットを放った扱い（実物はトスするが小道具は消すのが最も自然）。
            // 放すのは「クリップを走り出しの先まで流す＝実際に走る」インプレー（EndAt=0）だけ。
            // 空振り・ファールは EndAt=BatterRunStart で打ち切る＝Timeが3.25に達した停止ポーズでも
            // 打者は走らないので持ったまま（旧実装は空振りの振り切りホールドでバットが消えていた）
            if (_batter.Time >= BatDropTime && _batter.EndAt <= 0f)
            {
                _bat.SetActive(false);
                return;
            }
            _bat.SetActive(true);

            // 手ボーンの原点は手首＝そのまま結ぶとバットが手首の上を横切り「握れていない」絵になる。
            // 中指付け根との補間で掌中心を推定し、握り線を拳の中へ通す
            Vector3 Fist(Transform wrist, HumanBodyBones knuckle)
            {
                var k = _batter.Animator.GetBoneTransform(knuckle);
                return k != null ? Vector3.Lerp(wrist.position, k.position, 0.65f) : wrist.position;
            }
            var lh = Fist(lhT, HumanBodyBones.LeftMiddleProximal);
            var rh = Fist(rhT, HumanBodyBones.RightMiddleProximal);
            // クリップ内時刻での区間判定は打撃クリップ限定（死球クリップは常に構え扱い＝バットは手元で立てたまま）
            bool battingClip = _batter.Clip == _swingMissClip;
            // 片手判定を許すのはスイング区間だけ。自作打撃クリップは構えもスイングも同じ1本なので
            // クリップの同一性では区別できず、クリップ内時刻で見る（構えは BatterIdleEnd 以下）
            bool swinging = battingClip && _batter.Time >= BatterSwingStart;
            _batOneHand = swinging && _batGoodValid && Vector3.Distance(lh, rh) > HandsApartThreshold;
            if (!_batOneHand)
            {
                // 両手保持: 握り線（掌中心→掌中心）を基準に整列（§3.3の実測知見＝手ボーン軸は使えない）。
                // 実測線が信用できるのはスイング中（握り線が振り面に沿う）だけ:
                //   構え     = 合成軸（ヨー実測 × 仰角60°）で立てる（実測線は拳が横に開いて寝る）
                //   振り出し = ±0.15sで実測軸へブレンド
                //   振り抜き = 面通過+0.05〜0.25sで逆ヨー×仰角35°の巻き付き軸へブレンド
                //             （実測線は投手方向≒カメラ軸を向き、棒が点に潰れて消えて見える）
                var d = (rh - lh).normalized;
                var horiz = new Vector3(d.x, 0f, d.z);
                Vector3 axis;
                if (horiz.sqrMagnitude < 1e-6f)
                {
                    axis = d;
                }
                else
                {
                    Vector3 Composed(Vector3 yawDir, float elevationDeg)
                    {
                        float el = elevationDeg * Mathf.Deg2Rad;
                        return (yawDir.normalized * Mathf.Cos(el) + Vector3.up * Mathf.Sin(el)).normalized;
                    }
                    var stance = Composed(horiz, StanceBatElevationDeg);
                    if (!battingClip || _batter.Time < BatterSwingStart - 0.15f)
                    {
                        axis = stance;
                    }
                    else
                    {
                        float inSwing = Mathf.InverseLerp(BatterSwingStart - 0.15f, BatterSwingStart + 0.15f, _batter.Time);
                        float wrapped = Mathf.InverseLerp(SwingMissPlane + 0.05f, SwingMissPlane + 0.25f, _batter.Time);
                        var wrap = Composed(-horiz, FollowBatElevationDeg);
                        axis = Vector3.Slerp(Vector3.Slerp(stance, d, inSwing), wrap, wrapped).normalized;
                    }
                }
                var grip = (lh + rh) * 0.5f;
                _bat.transform.rotation = Quaternion.FromToRotation(Vector3.up, axis);
                _bat.transform.position = grip + axis * (BatHalfLength - GripBelowHalf);
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

        // グラブの寸法[m]（幅・高さ・厚み）と色。捕手ミットは丸く大きい、野手グラブはやや縦長で薄い。
        // 真円だと「胸に貼った円盤」に見えるので、縦をわずかに勝たせて楕円にする（2026-07-29 寄り検証）
        private static readonly Vector3 MittSize = new(0.25f, 0.27f, 0.12f);
        private static readonly Vector3 GloveSize = new(0.23f, 0.26f, 0.10f);
        private static readonly Color LeatherColor = new(0.45f, 0.27f, 0.15f);

        /// <summary>手からポケット面へ出す量[m]（コンテナ+Z＝掌の法線）。実物のグラブは掌の側へ張り出す。</summary>
        private const float GloveForward = 0.025f;

        /// <summary>
        /// 手の長さ（手首→指先）を中指付け根までの距離から見積もる倍率。
        /// 指先ボーンは Humanoid 必須でないので、実測できる手首→中指付け根で代用する。
        /// </summary>
        private const float HandLengthFromKnuckle = 1.85f;

        /// <summary>
        /// グラブ・ミットを利き手でない側へ装着する。**呼び出しは初期ポーズ評価後**（AttachGearと同じ）。
        ///
        /// 位置: 手ボーンの原点は**手首**なので、そのまま子にすると球が手首に刺さって
        /// 「腕の先に肉団子」になる。手首→指先の中点（＝手の中心）へグラブの中心を合わせる。
        /// 向き: ボーン軸は素体依存で信用できない（バット・被り物と同じ教訓）ので、**手の実測**で
        /// 座標系を組む。縦軸=手首→中指付け根、正面=掌の法線（人差し指付け根→小指付け根 と縦軸の外積）。
        /// 法線の符号だけは幾何では決まらないので、焼き付けポーズで掌が相手を向く事実を使って
        /// 視線ターゲット方向との内積で選ぶ。
        ///
        /// **正面を視線方向そのものにしてはいけない**: グラブの薄い軸(Z=0.115m)が視線に固定されるので、
        /// 掌が相手を向かないポーズ（立ち姿・投球前後）では手が薄い板を突き抜けて指が外へ出る
        /// （2026-07-29 監督指摘「左手が少し見えてる」の真因）。掌の法線に張れば、どの姿勢でも
        /// グラブの広い面（0.245×0.295m）が手のひらの面と重なる。
        /// </summary>
        private void AttachGlove(Actor actor, HumanBodyBones handBone, HumanBodyBones knuckleBone,
            Vector3 gazeLocal, Vector3 size, int layer, Mesh mesh = null)
        {
            if (actor == null || actor.Animator == null) return;
            var hand = actor.Animator.GetBoneTransform(handBone);
            if (hand == null) return;
            bool left = handBone == HumanBodyBones.LeftHand;
            var knuckle = actor.Animator.GetBoneTransform(knuckleBone);
            var index = actor.Animator.GetBoneTransform(
                left ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal);
            var little = actor.Animator.GetBoneTransform(
                left ? HumanBodyBones.LeftLittleProximal : HumanBodyBones.RightLittleProximal);

            var up = knuckle != null ? knuckle.position - hand.position : hand.up;
            up = up.sqrMagnitude > 1e-6f ? up.normalized : Vector3.up;
            var target = _root.transform.TransformPoint(gazeLocal);
            var gaze = target - hand.position;
            gaze -= Vector3.Dot(gaze, up) * up;   // 縦軸と直交化
            gaze = gaze.sqrMagnitude > 1e-6f ? gaze.normalized : Vector3.forward;

            var fwd = gaze;
            if (index != null && little != null)
            {
                var across = index.position - little.position;
                var n = Vector3.Cross(up, across);
                n -= Vector3.Dot(n, up) * up;
                if (n.sqrMagnitude > 1e-8f)
                {
                    n.Normalize();
                    fwd = Vector3.Dot(n, gaze) < 0f ? -n : n;   // 掌が相手を向く側を正面に採る
                }
            }
            var rot = Quaternion.LookRotation(fwd, up);
            var c = GearContainer(hand, "gear-glove", rot);
            // コンテナ原点＝手首なので、手の中心（手首→指先の中点）へグラブの中心を置く＝手が
            // 一番肉のある所に収まる。掌に合わせると手が踵側の薄い所へ寄って甲がはみ出す
            // （2026-07-29 実測: 縦寸の1/3も上げると投球ポーズで手の甲が丸見えになった）
            var lp = Vector3.zero;
            if (knuckle != null)
                lp.y = (knuckle.position - hand.position).magnitude * HandLengthFromKnuckle * 0.5f;
            else
                lp.y = (mesh != null ? mesh.bounds.size.y : size.y) * 0.25f;
            lp.z += GloveForward;   // 掌の上に重ねると手に飲まれる＝ポケットぶん前へ出す
            if (mesh != null)
                GearMeshPart(c, mesh, "prop-glove", lp, Quaternion.identity, LeatherColor, layer);
            else
                GearPart(c, PrimitiveType.Sphere, "prop-glove", lp, size, LeatherColor, layer);
        }

        // ---- 被り物・防具（design-18 §3.3。役割ごとに出し分け＝打者ヘルメット/守備帽子） --------

        /// <summary>役割別の装備セット。高校野球のヘルメットは両耳フラップ付き＝帽子とシルエットで判別できる。</summary>
        private enum GearKind { Cap, Helmet, CatcherGear, UmpireGear }

        // 捕手の被り物の素の装着位置[m]（gear-headコンテナ＝頭ボーン原点・+Y頭蓋上・+Z顔前）。
        // キャリブレーションの CatcherHelmetOffset / CatcherMaskOffset はこれに**加算**される
        private static readonly Vector3 CatcherHelmetBase = new(0f, 0.125f, -0.015f);
        private static readonly Vector3 CatcherMaskBase = new(0f, 0.090f, 0.075f);

        /// <summary>胸プロテクターの装着位置[m]（gear-chestコンテナ＝胸ボーン原点・+Z体前）。</summary>
        private static readonly Vector3 ChestMeshBase = new(0f, 0f, 0.085f);

        /// <summary>打者ヘルメットの被り角の回転中心（殻 helmet-shell の中心。AttachGear の配置と一致させる）。</summary>
        private static readonly Vector3 HelmetPivot = new(0f, 0.13f, 0.02f);

        /// <summary>
        /// 装備をボーンへ装着する。呼び出しは**初期ポーズ評価後**（コンストラクタ末尾のStepActor後）限定。
        ///
        /// 向きの決め方: ボーンのローカル軸もTポーズの正面も素体・クリップ依存で信用できない
        /// （バットの実装知見＋2026-07-28実測: Tポーズ正面=yaw仮定で焼くと打者のつばが後頭部を向いた）。
        /// そのため**初期ポーズで「視線ターゲット」（投手→本塁、他3者→マウンド）への水平方向を実測**し、
        /// そのワールド姿勢をボーンの子へ焼き付ける。野球のポーズは構えで必ず相手を見るので、
        /// 顔の向き＝視線方向が成り立つ。以後はボーン追従でお辞儀・しゃがみにも自然に付いてくる。
        ///
        /// 寸法はUBC頭部の実測（2026-07-28: 頭ボーン基準で幅0.18・奥行き0.22・頭頂+0.21）から。
        /// コンテナはlossyScale逆数でワールドスケール1に戻してあるので、子パーツは実寸(m)で置く。
        /// </summary>
        private void AttachGear(Actor actor, GearKind kind, Vector3 gazeLocal, Color accent, int layer)
        {
            var head = actor.Animator.GetBoneTransform(HumanBodyBones.Head);
            var dark = new Color(0.13f, 0.13f, 0.15f);
            var target = _root.transform.TransformPoint(gazeLocal);
            // 装着ボーンから視線ターゲットへの水平方向（つば・プロテクター正面。上下向きはボーン追従に任せる）
            Quaternion GazeRot(Transform bone)
            {
                var d = target - bone.position;
                d.y = 0f;
                return Quaternion.LookRotation(d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.forward);
            }
            // 頭専用: 上方向をワールド上でなく頭蓋実測（SpawnActorのTポーズ計測）に合わせる。
            // GazeRotのワールド上仮定は焼き付けポーズで頭が水平なとき（捕手・球審）しか成立せず、
            // 頭が傾く姿勢で焼く投手（リリース）・打者（構え）は被り物が頭蓋に対して斜めに接着されて
            // 「帽子が尖る／ヘルメットが後頭部へずれて髪に見える」実害が出た（2026-07-28 スクショ）
            Quaternion SkullRot(Transform bone)
            {
                var up = bone.TransformDirection(actor.HeadUpLocal);
                up = up.sqrMagnitude > 1e-6f ? up.normalized : Vector3.up;
                // 前も素立ち実測（顔正面）を現ポーズへ順変換して使う。旧実装の
                // 「視線ターゲットへの水平方向」は、ベイク姿勢で頭が傾いていると up と直交化した
                // 枠がロールし、つばが顔を斜めに横切る「斜め被り」になった（2026-07-28 投手で実害）
                var d = bone.TransformDirection(actor.HeadFwdLocal);
                d = d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.forward;
                var fwd = d - Vector3.Dot(d, up) * up;
                fwd = fwd.sqrMagnitude > 1e-6f ? fwd.normalized : d;
                return Quaternion.LookRotation(fwd, up);
            }

            if (head != null)
            {
                var c = GearContainer(head, "gear-head", SkullRot(head));
                // 帽子・ヘルメットは装着位置を較正できるようオフセット枠を1段挟む。コンテナは実寸空間なので
                // 枠の localPosition がそのまま[m]（x=左右/y=頭蓋上/z=顔前）。ApplyCalibration が流し込む
                var mount = c;
                if (kind == GearKind.Cap || kind == GearKind.Helmet)
                {
                    mount = new GameObject("gear-offset");
                    mount.transform.SetParent(c.transform, false);
                    if (kind == GearKind.Cap) _capMount = mount.transform;
                    else _helmetMount = mount.transform;
                }
                switch (kind)
                {
                    case GearKind.Cap:
                        // 生成メッシュ装備（較正済み: クラウン中心=原点・つば+Z）。無ければ従来プリミティブ。
                        // 位置決めの学び（2026-07-28監督2往復）: メッシュクラウンは深い（縁まで0.11）ので
                        // 「拡大＋下げ」は顔まで飲み込む。正解は**眉上（縁y≈0.08）まで持ち上げ**、
                        // 後頭部の露出はわずかな後ろ寄せだけで拾う
                        if (DuelGearMeshes.Cap() != null)
                        {
                            GearMeshPart(mount, DuelGearMeshes.Cap(), "cap-mesh",
                                new Vector3(0f, 0.185f, -0.025f), Quaternion.identity, accent, layer);
                        }
                        else
                        {
                            // 実測頭寸に被せる: クラウンは頭頂(+0.21)を覆い、側頭±0.09を包む
                            GearPart(mount, PrimitiveType.Sphere, "cap-crown", new Vector3(0f, 0.155f, 0.01f),
                                new Vector3(0.21f, 0.15f, 0.23f), accent, layer);
                            GearPart(mount, PrimitiveType.Cube, "cap-brim", new Vector3(0f, 0.125f, 0.145f),
                                new Vector3(0.17f, 0.012f, 0.09f), accent, layer);
                        }
                        break;
                    case GearKind.Helmet:
                        // ヘルメットはAIメッシュ不採用（2026-07-28確定）: 生成殻は開口・付属タブの向きが
                        // 制御できず、頭が開口を突き抜け＋タブが後ろ髪に見える「おかっぱ」事故になった
                        // （helm-closeup-0.png）。マスク（FaceCage）と同じ結論で、殻＋つば＋両耳フラップの
                        // **構造がある装備はプリミティブ構成**で作る。
                        // 帽子より低く深く被る（額は眉まで覆い、顔だけ出す）。初版は帽子と同じ高さに置いて
                        // 額が露出し「赤い髪」に見えた → 中心を下げ縦径を拡大
                        GearPart(mount, PrimitiveType.Sphere, "helmet-shell", new Vector3(0f, 0.13f, 0.02f),
                            new Vector3(0.24f, 0.20f, 0.26f), accent, layer);
                        GearPart(mount, PrimitiveType.Cube, "helmet-brim", new Vector3(0f, 0.12f, 0.16f),
                            new Vector3(0.17f, 0.012f, 0.07f), accent, layer);
                        GearPart(mount, PrimitiveType.Sphere, "helmet-ear-l", new Vector3(-0.11f, 0.05f, 0.02f),
                            new Vector3(0.035f, 0.11f, 0.10f), accent, layer);
                        GearPart(mount, PrimitiveType.Sphere, "helmet-ear-r", new Vector3(0.11f, 0.05f, 0.02f),
                            new Vector3(0.035f, 0.11f, 0.10f), accent, layer);
                        break;
                    case GearKind.CatcherGear:
                        // 日本の高校野球の捕手は米国式フルフェイスではなく「捕手用ヘルメット＋独立マスク」の
                        // 2点構成（2026-07-28監督指摘＋実物写真）。生成メッシュはその2点をそのまま持つ。
                        //
                        // 2026-07-28 に一度「AI生成マスクは顔を塞ぐのでプリミティブのケージが正解」と結論したが、
                        // これは**テキスト生成の結論**だった。実物写真を渡した画像生成（Meshy）では隙間の抜けた
                        // ケージが出た（2026-07-29・私の見立てが外れた）ので、実メッシュへ差し替える。
                        // ヘルメットは通気孔の穴でCollapseが頭打ちになるため voxel remesh 前処理が要る
                        // （tools/aigear-decimate.py のコメント参照）。
                        var cHelmet = DuelGearMeshes.CatcherHelmet();
                        var cMask = DuelGearMeshes.CatcherMask();
                        if (cHelmet != null && cMask != null)
                        {
                            _cHelmetMount = OffsetMount(c, "cmask-helm-offset");
                            _cMaskMount = OffsetMount(c, "cmask-cage-offset");
                            GearMeshPart(_cHelmetMount.gameObject, cHelmet, "cmask-helm",
                                CatcherHelmetBase, Quaternion.identity, accent, layer);
                            // ケージは黒（実物は金属の艶消し黒）。殻のチーム色と分けることで
                            // 「被り物＋防具」の2点に読める
                            GearMeshPart(_cMaskMount.gameObject, cMask, "cmask-cage",
                                CatcherMaskBase, Quaternion.identity, dark, layer);
                        }
                        else
                        {
                            GearPart(c, PrimitiveType.Sphere, "cmask-shell", new Vector3(0f, 0.145f, -0.02f),
                                new Vector3(0.23f, 0.18f, 0.22f), accent, layer);
                            FaceCage(c, "cmask", accent, layer);
                        }
                        break;
                    case GearKind.UmpireGear:
                        // 球審: 帽子型クラウン＋ケージバー（顔は見せる）
                        GearPart(c, PrimitiveType.Sphere, "umask-crown", new Vector3(0f, 0.155f, -0.01f),
                            new Vector3(0.21f, 0.15f, 0.21f), dark, layer);
                        FaceCage(c, "umask", dark, layer);
                        break;
                }
            }

            if (kind == GearKind.CatcherGear || kind == GearKind.UmpireGear)
            {
                // 胸プロテクター（UpperChestはHumanoidマッピングで欠けることがある→Chest→Spineへ落ちる）
                var chest = actor.Animator.GetBoneTransform(HumanBodyBones.UpperChest);
                if (chest == null) chest = actor.Animator.GetBoneTransform(HumanBodyBones.Chest);
                if (chest == null) chest = actor.Animator.GetBoneTransform(HumanBodyBones.Spine);
                if (chest != null)
                {
                    var c = GearContainer(chest, "gear-chest", GazeRot(chest));
                    bool catcherGear = kind == GearKind.CatcherGear;
                    // 実物のプロテクターは横一列のパッド（ロール）が段々に積まれ、間に溝・上に肩当て・
                    // 首元に切り欠きがある。箱1枚だと「板」に見える（2026-07-28 監督指摘「丸みと窪みが無い」）ため、
                    // 横倒しカプセルを縦に積む＝Lambert陰影がロールの丸みと段間の溝を描く。
                    // 行の意味: (y, z, 幅W, 管径D)。腹へ向けて幅を絞り、中段をわずかに前へ出して体に沿う凸を作る。
                    // Capsuleの軸はローカルYなので z90°回転で横倒し、scale=(D, W/2, D)
                    var rows = catcherGear
                        ? new[]
                        {
                            new Vector4(0.065f, 0.100f, 0.30f, 0.085f),
                            new Vector4(-0.015f, 0.105f, 0.30f, 0.085f),
                            new Vector4(-0.090f, 0.100f, 0.26f, 0.080f),
                            new Vector4(-0.155f, 0.090f, 0.22f, 0.075f),
                        }
                        : new[]   // 球審は胸だけの薄型3段
                        {
                            new Vector4(0.050f, 0.085f, 0.24f, 0.070f),
                            new Vector4(-0.015f, 0.090f, 0.24f, 0.070f),
                            new Vector4(-0.075f, 0.085f, 0.21f, 0.065f),
                        };
                    // 生成メッシュ（Meshy画像生成・較正済み: 縦0.44m・肩当て込み・パッド面+Z）。
                    // 首元まで上げて着せる。球審は薄型プリミティブ維持（服の下に着る前提のシルエット）
                    bool chestMesh = catcherGear && DuelGearMeshes.Chest() != null;
                    if (chestMesh)
                    {
                        // 高さは**肩ボーン（UpperArm頭）の実測**へメッシュ上端（肩当ての天面）を合わせる。
                        // コンテナのボーンは UpperChest/Chest/Spine のどれに落ちるかが素体依存で、
                        // 原点の高さが揃わない＝固定オフセットだと腰まで下がる（2026-07-29 実害）。
                        // 旧実装が肩当てだけ肩ボーン実測で置いていたのと同じ理由
                        var chestGear = DuelGearMeshes.Chest();
                        float shoulderY = 0f; int shoulderN = 0;
                        foreach (var armBone in new[] { HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm })
                        {
                            var arm = actor.Animator.GetBoneTransform(armBone);
                            if (arm == null) continue;
                            shoulderY += c.transform.InverseTransformPoint(arm.position).y;
                            shoulderN++;
                        }
                        float top = shoulderN > 0
                            ? shoulderY / shoulderN + 0.045f    // 肩の稜線の少し上
                            : chestGear.bounds.extents.y;       // 肩ボーンが無い素体は素の中心置き
                        _cChestMount = OffsetMount(c, "chest-offset");
                        GearMeshPart(_cChestMount.gameObject, chestGear, "chest-mesh",
                            new Vector3(0f, top - chestGear.bounds.extents.y, ChestMeshBase.z),
                            Quaternion.identity, accent, layer);
                    }
                    else
                    {
                    for (int i = 0; i < rows.Length; i++)
                        GearPart(c, PrimitiveType.Capsule, "chest-pad-" + i,
                            new Vector3(0f, rows[i].x, rows[i].y),
                            new Vector3(rows[i].w, rows[i].z * 0.5f, rows[i].w),
                            accent, layer, new Vector3(0f, 0f, 90f));
                    }
                    // 肩当て: 肩の稜線の**上**に載せ、前後に跨がせる（前面だけに置くと「前掛け」に見える＝
                    // 2026-07-28監督指摘「肩の上を通って背中でパチンとするもの」）。左右の間＝首元の切り欠き。
                    // 位置は固定値でなく**肩ボーン（UpperArm頭）の実測**から: 固定値(±0.105)は内側すぎて
                    // 「襟の飾り」に見えた（スクショ実害）。素体・ポーズが変わってもずれない。
                    //
                    // ただし**生成メッシュの胸当ては肩当てを自前で持っている**ので、その時は載せない
                    // （2026-07-29。旧bpy製は胴だけの板だったため別置きが必要だった）。二重に置くと
                    // 肩が膨れる
                    if (!chestMesh)
                    {
                    var shoulderScale = catcherGear
                        ? new Vector3(0.115f, 0.055f, 0.175f)
                        : new Vector3(0.095f, 0.048f, 0.155f);
                    foreach (var (armBone, side) in new[]
                        { (HumanBodyBones.LeftUpperArm, "l"), (HumanBodyBones.RightUpperArm, "r") })
                    {
                        var arm = actor.Animator.GetBoneTransform(armBone);
                        if (arm == null) continue;
                        var lp = c.transform.InverseTransformPoint(arm.position);
                        lp.y += 0.055f;                              // 肩ボーン頭の少し上＝稜線
                        lp.x -= Mathf.Sign(lp.x) * 0.02f;            // 気持ち内側（腕の付け根に被せる）
                        lp.z += 0.005f;
                        GearPart(c, PrimitiveType.Sphere, "chest-shoulder-" + side,
                            lp, shoulderScale, accent, layer);
                    }
                    }
                    // 背面ストラップ: 肩から背中へ降りる縦帯2本＋背中の横バンド（バックルで留まる装具の記号）。
                    // 濃色の細帯にして「体の一部」ではなくベルトに読ませる
                    GearPart(c, PrimitiveType.Capsule, "strap-back-l",
                        new Vector3(-0.075f, 0.055f, -0.095f), new Vector3(0.030f, 0.095f, 0.030f), dark, layer);
                    GearPart(c, PrimitiveType.Capsule, "strap-back-r",
                        new Vector3(0.075f, 0.055f, -0.095f), new Vector3(0.030f, 0.095f, 0.030f), dark, layer);
                    GearPart(c, PrimitiveType.Cube, "strap-back-band",
                        new Vector3(0f, -0.035f, -0.100f), new Vector3(0.24f, 0.035f, 0.015f), dark, layer);
                }
            }

            if (kind == GearKind.CatcherGear)
            {
                // レガース。箱1枚は「板」に見える（2026-07-28監督指摘）ため、実物の構造どおり
                // 膝皿ドーム＋脛ロール3段＋足首ロールに分解する（胸プロテクターと同じ段積みの読み）。
                // 配置は「膝（calfボーン頭）→足首（footボーン頭）」の実測軸に沿わせる＝
                // しゃがみで脛が斜めでもロール列が脛から浮かない
                var legs = new[]
                {
                    (HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot),
                    (HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot),
                };
                foreach (var (calfBone, footBone) in legs)
                {
                    var calf = actor.Animator.GetBoneTransform(calfBone);
                    if (calf == null) continue;
                    var c = GearContainer(calf, "gear-leg", GazeRot(calf));
                    var foot = actor.Animator.GetBoneTransform(footBone);
                    // 膝→足首ベクトル（コンテナローカル）。footが無い素体はほぼ直下へフォールバック
                    var shin = foot != null
                        ? c.transform.InverseTransformPoint(foot.position)
                        : new Vector3(0f, -0.42f, 0f);
                    var fwd = new Vector3(0f, 0f, 0.065f);   // 脛の前面へのオフセット（コンテナ+Z=視線方向）
                    var legMesh = DuelGearMeshes.LegGuard();
                    if (legMesh != null)
                    {
                        // 生成メッシュ（bpy製・膝が+Y・較正済み）を膝→足首の実測軸に沿わせ、長さを合わせる
                        float len = shin.magnitude > 0.05f ? shin.magnitude : 0.42f;
                        float s = len * 1.05f / Mathf.Max(0.05f, legMesh.bounds.size.y);
                        GearMeshPart(c, legMesh, "leg-mesh", shin * 0.42f + fwd,
                            Quaternion.FromToRotation(Vector3.down, shin.normalized), accent, layer, s);
                    }
                    else
                    {
                    GearPart(c, PrimitiveType.Sphere, "leg-knee", shin * 0.04f + fwd + new Vector3(0f, 0f, 0.012f),
                        new Vector3(0.115f, 0.100f, 0.075f), accent, layer);
                    foreach (var t in new[] { 0.32f, 0.52f, 0.72f })
                        GearPart(c, PrimitiveType.Capsule, "leg-shin-" + t,
                            shin * t + fwd, new Vector3(0.055f, 0.055f, 0.055f), accent, layer,
                            new Vector3(0f, 0f, 90f));
                    GearPart(c, PrimitiveType.Capsule, "leg-ankle", shin * 0.90f + fwd * 0.9f,
                        new Vector3(0.048f, 0.045f, 0.048f), accent, layer, new Vector3(0f, 0f, 90f));
                    }
                }
            }
        }

        /// <summary>装備の位置オフセット枠（gear-head直下・実寸[m]）。ApplyCalibration が localPosition を流し込む。</summary>
        private static Transform OffsetMount(GameObject parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            return go.transform;
        }

        /// <summary>
        /// 装備コンテナ: ボーンの子に置き、素体の正規化スケールを打ち消して（lossyScale逆数）
        /// 実寸(m)でパーツを置ける空間を作る。ワールド姿勢はキャラ正面（yaw）に焼き付ける。
        /// </summary>
        private static GameObject GearContainer(Transform bone, string name, Quaternion worldRot)
        {
            var c = new GameObject(name);
            c.transform.SetParent(bone, false);
            var s = bone.lossyScale;
            c.transform.localScale = new Vector3(1f / s.x, 1f / s.y, 1f / s.z);
            c.transform.position = bone.position;
            c.transform.rotation = worldRot;
            return c;
        }

        private GameObject GearPart(GameObject container, PrimitiveType type, string name,
            Vector3 localPos, Vector3 localScale, Color color, int layer, Vector3 localEuler = default)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(container.transform, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.Euler(localEuler);
            go.transform.localScale = localScale;
            go.GetComponent<MeshRenderer>().sharedMaterial = FlatMaterial(color);
            go.layer = layer;
            return go;
        }

        /// <summary>
        /// 顔前ケージ（マスクの記号）。実物のマスクは顔を塞がず細いバーの隙間から視線が通る
        /// （2026-07-28 監督指摘＋実物写真）。横バー4本＋左右レール＋額・顎パッドで
        /// 「覆わずに守る」を作る。バーはピクセル化で格子の気配に落ち、肌色（顔）が残る。
        /// </summary>
        private void FaceCage(GameObject container, string prefix, Color pad, int layer)
        {
            var dark = new Color(0.13f, 0.13f, 0.15f);
            GearPart(container, PrimitiveType.Cube, prefix + "-pad-brow", new Vector3(0f, 0.125f, 0.10f),
                new Vector3(0.15f, 0.028f, 0.035f), pad, layer);
            GearPart(container, PrimitiveType.Cube, prefix + "-pad-chin", new Vector3(0f, -0.065f, 0.095f),
                new Vector3(0.11f, 0.030f, 0.035f), pad, layer);
            // バーは平面グリッドでなく**顔の丸みに沿って左右へ回り込む**（2026-07-28監督指摘
            // 「死角になってしまっている。実際は顔の形に合わせて丸みがある」）。
            // 中央＋左右ウィングの3分割アークにすると、どの視線角でも必ずどこかの隙間が抜ける
            foreach (var y in new[] { 0.085f, 0.04f, -0.005f, -0.05f })
            {
                GearPart(container, PrimitiveType.Cube, prefix + "-bar" + y, new Vector3(0f, y, 0.125f),
                    new Vector3(0.10f, 0.010f, 0.010f), dark, layer);
                GearPart(container, PrimitiveType.Cube, prefix + "-barL" + y, new Vector3(-0.072f, y, 0.103f),
                    new Vector3(0.062f, 0.010f, 0.010f), dark, layer, new Vector3(0f, 42f, 0f));
                GearPart(container, PrimitiveType.Cube, prefix + "-barR" + y, new Vector3(0.072f, y, 0.103f),
                    new Vector3(0.062f, 0.010f, 0.010f), dark, layer, new Vector3(0f, -42f, 0f));
            }
            // 縦レールもアークの折れ目（頬の位置）へ後退させる
            GearPart(container, PrimitiveType.Cube, prefix + "-rail-l", new Vector3(-0.092f, 0.02f, 0.082f),
                new Vector3(0.010f, 0.19f, 0.010f), dark, layer);
            GearPart(container, PrimitiveType.Cube, prefix + "-rail-r", new Vector3(0.092f, 0.02f, 0.082f),
                new Vector3(0.010f, 0.19f, 0.010f), dark, layer);
        }

        /// <summary>較正済み装備メッシュ（DuelGearMeshes）をコンテナ実寸空間へ置く。scale は一様倍率。</summary>
        private GameObject GearMeshPart(GameObject container, Mesh mesh, string name,
            Vector3 localPos, Quaternion localRot, Color color, int layer, float scale = 1f)
        {
            var go = new GameObject(name);
            go.transform.SetParent(container.transform, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = localRot;
            go.transform.localScale = Vector3.one * scale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = FlatMaterial(color);
            go.layer = layer;
            return go;
        }

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform) SetLayerRecursive(child.gameObject, layer);
        }

#if UNITY_EDITOR
        // ---- 【開発用・エディタ限定】録画ハーネス（DuelViewRecorder）向け観測窓 ----

        /// <summary>
        /// グラブの装着結果を実測で返す（絵の目測でなく数値で確かめるため。2026-07-29 追加）。
        /// 寄りカメラの狙いを外すと「付いていない」のか「見えていない」のか切り分けられないので、
        /// 掌の位置とグラブ実体の位置を直接突き合わせる。
        /// </summary>
        internal string GloveDiagForCapture()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var (a, name) in new[] { (_catcher, "catcher"), (_pitcher, "pitcher") })
            {
                if (a == null || a.Animator == null) { sb.AppendLine(name + "\tno-actor"); continue; }
                var hand = a.Animator.GetBoneTransform(HumanBodyBones.LeftHand);
                var knuckle = a.Animator.GetBoneTransform(HumanBodyBones.LeftMiddleProximal);
                Transform glove = null;
                foreach (var t in a.Go.GetComponentsInChildren<Transform>(true))
                    if (t.name == "prop-glove") { glove = t; break; }
                if (hand == null || glove == null)
                {
                    sb.AppendLine(name + "\thand=" + (hand != null) + "\tglove=" + (glove != null) + "\t★欠落");
                    continue;
                }
                var palm = knuckle != null ? Vector3.Lerp(hand.position, knuckle.position, 0.65f) : hand.position;
                var local = _root.transform.InverseTransformPoint(glove.position);
                sb.AppendLine(name
                    + "\t掌=" + _root.transform.InverseTransformPoint(palm).ToString("F3")
                    + "\tグラブ=" + local.ToString("F3")
                    + "\t掌からの距離=" + Vector3.Distance(palm, glove.position).ToString("F3") + "m"
                    + "\t実寸=" + glove.lossyScale.ToString("F3"));
            }
            return sb.ToString();
        }

        /// <summary>
        /// 【診断用】投手の掌・グラブ・腰のステージローカル座標を返す（寄りカットの狙いを実測で決める）。
        /// 見つからない要素は Vector3.zero。
        /// </summary>
        internal void PreviewProbeForCapture(out Vector3 palm, out Vector3 glove, out Vector3 hips,
            out Quaternion gloveRot)
        {
            palm = glove = hips = Vector3.zero;
            gloveRot = Quaternion.identity;
            if (_pitcher == null || _pitcher.Animator == null) return;
            var hand = _pitcher.Animator.GetBoneTransform(HumanBodyBones.LeftHand);
            var knuckle = _pitcher.Animator.GetBoneTransform(HumanBodyBones.LeftMiddleProximal);
            var hipBone = _pitcher.Animator.GetBoneTransform(HumanBodyBones.Hips);
            if (hand != null)
            {
                var p = knuckle != null ? Vector3.Lerp(hand.position, knuckle.position, 0.65f) : hand.position;
                palm = _root.transform.InverseTransformPoint(p);
            }
            if (hipBone != null) hips = _root.transform.InverseTransformPoint(hipBone.position);
            foreach (var t in _pitcher.Go.GetComponentsInChildren<Transform>(true))
                if (t.name == "prop-glove")
                {
                    glove = _root.transform.InverseTransformPoint(t.position);
                    gloveRot = Quaternion.Inverse(_root.transform.rotation) * t.rotation;
                    break;
                }
        }

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

        /// <summary>【開発用】体型比較撮影向けに素体FBXを差し替える（null=既定のSuperhero Male）。
        /// 素体を替える時は対応する着衣ベイク資産（UniformMeshOverrideForCapture）も揃えること＝
        /// ベイクは bindposes/boneWeights を素体からコピーするため、素体と着衣メッシュは必ず同源で組。</summary>
        internal static string BodyFbxOverrideForCapture;

        private static GameObject LoadBody()
        {
#if UNITY_EDITOR
            return UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(BodyFbxOverrideForCapture ?? BodyFbx);
#else
            return null;
#endif
        }

        /// <summary>子孫から名前一致のRendererを引いて表示を切り替える（見つからなければ何もしない）。</summary>
        private static void SetChildActive(GameObject root, string rendererName, bool active)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                if (r.name == rendererName) r.gameObject.SetActive(active);
        }

        /// <summary>
        /// 購入素体。gitignore配下なので手元に無い環境がある＝null を正常系として扱い、
        /// 呼び出し側でU2素体へ落とす（警告も出さない。無いのが異常ではない）。
        /// </summary>
        private static GameObject LoadVendorBody()
        {
#if UNITY_EDITOR
            return UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(VendorBodyFbx);
#else
            return null;
#endif
        }

        /// <summary>
        /// 購入素体のメッシュを領域IDつき（DuelVendorRegionBaker のベイク資産）へ差し替える。
        /// 眼球は消す（のっぺらぼう方針＝design-08。顔テクスチャは領域色で塗り潰されるので放っておいても
        /// 顔は出ないが、眼球は別メッシュなので明示的に落とす）。
        /// 1枚でも領域資産が欠けたら false＝呼び出し側は肌色一色のフォールバックへ倒す
        /// （半分だけ領域色という中途半端な絵を出さない）。
        /// </summary>
        private static bool ApplyVendorRegionMeshes(GameObject go)
        {
#if UNITY_EDITOR
            bool all = true;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r.name.EndsWith("Eye_Ball_Geo"))
                {
                    r.gameObject.SetActive(false);
                    continue;
                }
                var mesh = UnityEditor.AssetDatabase.LoadAssetAtPath<Mesh>(
                    VendorRegionMeshDir + r.name + VendorRegionMeshSuffix);
                if (mesh == null) { all = false; continue; }
                var smr = r as SkinnedMeshRenderer;
                if (smr != null) smr.sharedMesh = mesh;
                else
                {
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf != null) mf.sharedMesh = mesh;
                }
            }
            if (!all)
                Debug.LogWarning("[DuelStageRig] vendor region meshes missing ("
                    + VendorRegionMeshDir + ") - run KokoSim.EditorTools.DuelVendorRegionBaker.BakeAll()");
            return all;
#else
            return false;
#endif
        }

        /// <summary>
        /// 着衣メッシュ（DuelUniformBaker のベイク資産）。素体と同じくエディタ限定ロード。
        /// 無ければ null＝素体のまま出す（警告のみ・対決ビューは止めない）。
        /// </summary>
        /// <summary>【開発用】比較シート撮影向けに着衣メッシュ資産を差し替える（null=既定のSlim0）。</summary>
        internal static string UniformMeshOverrideForCapture;

        private static Mesh LoadUniformMesh()
        {
#if UNITY_EDITOR
            var path = UniformMeshOverrideForCapture ?? UniformMeshAsset;
            var mesh = UnityEditor.AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (mesh == null)
                Debug.LogWarning("[DuelStageRig] uniform mesh not baked (" + path
                    + ") - run KokoSim.EditorTools.DuelUniformBaker.BakeAll()");
            return mesh;
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
