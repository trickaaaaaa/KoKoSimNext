using System.Collections.Generic;
using System.Text;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Timeline.Playback;
using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 打球処理の3D化（守備・捕球・送球・走塁）。対決4体の較正の巣（DuelStageRig.cs）を触らずに
    /// 足していくための partial 側。
    ///
    /// <b>骨格</b>: 位置は engine が唯一の真値。<see cref="PlaybackEvaluator"/> の
    /// FielderAt/RunnerAt/BallAt を引いて stage-local へ写すだけで、Unity が作るのは
    /// 「その位置でどんな芝居をしているか」だけ。こうすると 2D俯瞰と3Dの絵が原理的に一致し、
    /// 決定論にも帯にも触らない（不変条件#1/#2）。
    /// </summary>
    public sealed partial class DuelStageRig
    {
        // ---- クリップ ---------------------------------------------------------
        //
        // 素の向きは実測（DuelMotionSpike.RunFielderFacingProbe・2026-08-01）:
        //   Defender_Standing_Idle_Loop_Type01  chest=(-0.008, 1.000)  ＝ +Z
        //   Defender_Crouch_Idle_Loop_Type01    chest=(-0.005, 1.000)  ＝ +Z
        //   Offense_Crouch_Idle_Loop_Type01     chest=(-0.012, 1.000)  ＝ +Z
        // 対決4体（打者/捕手/球審）と同じ規約。投手クリップだけが −X で例外。
        // ⚠ クリップを足したら必ずプローブで測ってから yaw を決める（design-18 §7-3 の掟）。

        /// <summary>内野の待機（ゴロに備えた低い構え）。</summary>
        public const string InfielderIdleClipName = "AA_Baseball_Defender_Crouch_Idle_Loop_Type01";

        /// <summary>外野の待機（直立）。遠くて数pxなので低い構えを持たせても読めない。</summary>
        public const string OutfielderIdleClipName = "AA_Baseball_Defender_Standing_Idle_Loop_Type01";

        /// <summary>守備・走塁クリップの素の胸の向き（実測 +Z）。yaw の逆算はここを基準にする。</summary>
        private static readonly Vector3 FieldClipBaseFacing = Vector3.forward;

        /// <summary>投手・捕手を除いた「足す7人」。P/C は対決4体の <c>_pitcher</c>/<c>_catcher</c> を兼用する。</summary>
        private static readonly FieldPosition[] AddedFieldPositions =
        {
            FieldPosition.FirstBase, FieldPosition.SecondBase, FieldPosition.ThirdBase,
            FieldPosition.Shortstop, FieldPosition.LeftField, FieldPosition.CenterField,
            FieldPosition.RightField,
        };

        private readonly Dictionary<FieldPosition, Actor> _fieldCast = new();
        private bool _fieldCastVisible;

        /// <summary>守備陣（投手・捕手を含む9人）を役割で引く。未生成なら空。</summary>
        private Actor FielderActor(FieldPosition pos)
        {
            if (pos == FieldPosition.Pitcher) return _pitcher;
            if (pos == FieldPosition.Catcher) return _catcher;
            Actor a;
            return _fieldCast.TryGetValue(pos, out a) ? a : null;
        }

        /// <summary>
        /// stage-local の位置(x,z)から「本塁を向く」yaw[度]を出す。
        /// yaw θ は素体の +Z を (sinθ, cosθ) へ回すので、本塁方向 normalize(-x,-z) に等しくなる
        /// θ = atan2(-x, -z)。クリップの素の向きが +Z であることが前提
        /// （<see cref="FieldClipBaseFacing"/>・実測済み）。
        /// </summary>
        private static float YawFacingHome(float x, float z)
        {
            if (Mathf.Abs(x) < 1e-4f && Mathf.Abs(z) < 1e-4f) return 0f;
            return Mathf.Atan2(-x, -z) * Mathf.Rad2Deg;
        }

        private static bool IsOutfield(FieldPosition p) =>
            p == FieldPosition.LeftField || p == FieldPosition.CenterField || p == FieldPosition.RightField;

        /// <summary>
        /// 守備の7人を生成する（2回目以降は何もしない）。
        ///
        /// 打席中は対決構図（望遠2.9°＝本塁で幅7m）の**フレーム外**なので既定で伏せておくが、
        /// 生成そのものは打球が出る前に済ませる＝打球の瞬間に7体 Instantiate は間に合わない。
        /// </summary>
        public void EnsureFieldCast()
        {
            if (!Available || _fieldCast.Count > 0) return;

            var vendor = LoadVendorBody();
            bool useVendor = vendor != null;
            var bodyPrefab = useVendor ? vendor : LoadBody();
            if (bodyPrefab == null) return;
            int layer = LayerMask.NameToLayer(LayerName);
            if (layer < 0) return;

            var infieldClip = LoadClip(InfielderIdleClipName);
            var outfieldClip = LoadClip(OutfielderIdleClipName);
            if (infieldClip == null || outfieldClip == null)
            {
                Debug.LogWarning("[DuelStage] 守備の待機クリップが無いので守備陣を組まない");
                return;
            }

            foreach (var pos in AddedFieldPositions)
            {
                PlaybackVec home;
                if (!PlaybackEvaluator.DefaultPositions.TryGetValue(pos, out home)) continue;
                float x = (float)home.X, z = (float)home.Y;   // engine の +Y=センター → stage の +z
                var clip = IsOutfield(pos) ? outfieldClip : infieldClip;

                // rootMotion=false … 位置は engine のタイムラインで置く（クリップに動かさせない）
                // tightBounds=true … updateWhenOffscreen の実ポーズ追従は毎フレームのフルスキニング。
                //                    体数が増えるとこれが最大のコストになる
                var a = SpawnActor(bodyPrefab, clip, new Vector3(x, 0f, z), YawFacingHome(x, z),
                    _defenseColors, 0f, layer, useVendor, rootMotion: false, tightBounds: true);
                a.Go.name = "fielder-" + pos;
                a.IdleClip = clip;

                // 初期ポーズを1コマ評価してから装備を焼く（AttachGear/AttachGlove は視線の実測が要る）
                StepActor(a, 0.001f);
                GroundFix(a);
                if (!useVendor) AttachGear(a, GearKind.Cap, Vector3.zero, _defenseColors.Cap, layer);
                CollectTeamMats(a, _defenseColors.Cap);
                // グラブは投手と同じ左手・同じメッシュ。視線ターゲットは本塁（＝ステージ原点）
                AttachGlove(a, HumanBodyBones.LeftHand, HumanBodyBones.LeftMiddleProximal,
                    Vector3.zero, GloveSize, layer, DuelGearMeshes.Glove());

                _fieldCast[pos] = a;
            }

            SetFieldCastVisible(_fieldCastVisible);
        }

        /// <summary>
        /// 守備7人の表示を切り替える。伏せている間は <see cref="Actor.Enabled"/> も落として
        /// <c>StepActor</c> ごと飛ばす（ポーズ評価もスキニングも走らない）。
        /// </summary>
        public void SetFieldCastVisible(bool on)
        {
            _fieldCastVisible = on;
            foreach (var kv in _fieldCast)
            {
                var a = kv.Value;
                if (a == null || a.Go == null) continue;
                a.Go.SetActive(on);
                a.Enabled = on;
            }
        }

        /// <summary>守備7人を定位置（engine の <c>DefaultPositions</c>）へ戻す。</summary>
        public void PlaceFieldCastAtDefault()
        {
            foreach (var kv in _fieldCast)
            {
                PlaybackVec home;
                if (!PlaybackEvaluator.DefaultPositions.TryGetValue(kv.Key, out home)) continue;
                PlaceFielder(kv.Value, (float)home.X, (float)home.Y);
            }
        }

        /// <summary>
        /// 野手を stage-local の (x,z) へ置き、本塁を向かせる。
        /// 縦は <see cref="GroundFix"/> が骨の実測で解く（クリップごとに根の高さが違うため）。
        /// </summary>
        private void PlaceFielder(Actor a, float x, float z)
        {
            if (a == null || a.Go == null) return;
            var lp = a.Go.transform.localPosition;
            a.Go.transform.localPosition = new Vector3(x, lp.y, z);
            a.Go.transform.localRotation = Quaternion.Euler(0f, YawFacingHome(x, z), 0f);
            a.BasePos = a.Go.transform.localPosition;
            a.BaseRot = a.Go.transform.localRotation;
            GroundFix(a);
        }

        // ---- 守備カメラ ---------------------------------------------------------
        //
        // **既存の打球追随カメラ（engine CameraPlanBuilder・issue #119）をそのまま使う。**
        // あれが返す `CameraViewport(Cx, Cy, ZoomMult)` は「フィールド上の注視点とズーム」なので、
        // 3Dでは注視点 (Cx, AimY, Cy)・縦に収めたい実寸 155.2/ZoomMult へそのまま写る。
        // 演出の判断（投球区間は固定フレーム／送球は始終点を収める／打球は0.6秒先読みで追う／
        // 決着に絡む走塁レッグはボールと目標塁の中点／ResAt+0.9sで全景へ戻す）と
        // レート制限（ズーム2.0/s・パン45m/s＝酔い防止）が丸ごと効く。
        //
        // **カメラは平行移動しない**＝中継の三脚カメラと同じくパン・チルト・ズームだけ。
        // 視差が出ないので酔わず、station の切り替えは対決構図→守備カメラの1回だけで済む
        // （対決カメラは z=+78.44 から −z を向くので外野が背後にあり、パンでは届かない）。

        /// <summary>
        /// 守備カメラの据え位置（stage-local[m]）。本塁後方の高所。
        ///
        /// ⚠ z は **> −16**（バックネット書き割り z=−16・34×8m より手前）。
        /// 奥へ引くと壁が画面下半分を黒く塞ぐ（2026-08-01 実害）。
        ///
        /// ⚠ **高さが効く**。低いと視線が地面とほぼ平行になり、「縦に収めたい実寸」を詰めても
        /// 画面の大半が遠景の芝で埋まって被写体が埋もれる（枠の高さは視線に直交して測るのに、
        /// 地面は視線とほぼ平行だから＝frameH 25.9m の枠が奥行き100m超の地面を映す）。
        /// **絵で決める値**なので比較撮影のために差し替えられるようにしてある。
        /// </summary>
        public static Vector3 FieldStation = new(0f, 24f, -14f);

        /// <summary>注視点の高さ[m]。地面でなく腰の高さに取ると、寄ったときに人が下に切れない。</summary>
        private const float FieldAimY = 1.0f;

        /// <summary>
        /// ZoomMult=1（全景）で縦に収まる実寸[m]。engine 側のデザインキャンバス
        /// （DesignH=520 / S=3.35 px/m）と同じ定義＝2Dの全景と同じ広さから始まる。
        /// **この3定数は engine の CameraPlanBuilder と同期している**（片方だけ変えない）。
        /// </summary>
        private const float FieldWideHeightM = 520f / 3.35f;   // ≒155.2

        /// <summary>
        /// 守備カメラの寄りの深さ。2D俯瞰の既定（MaxZoom=3.4＝縦45.6m）では野手が十数pxにしかならない。
        /// 外野フライで FitBox が求めるズームは実測5.75で上限に頭打ちされていた（engine テストで固定）。
        /// **絵で決める値**なので、比較撮影のために差し替えられるようにしてある。
        /// </summary>
        public static float FieldMaxZoom = 6f;

        private System.Collections.Generic.IReadOnlyList<CameraKeyframe> _fieldPlan;

        /// <summary>engine の追随カメラのプランを、この rig の守備カメラ用に組む。</summary>
        private void BuildFieldCameraPlan(PlaybackPlay play)
        {
            _fieldPlan = play == null ? null : CameraPlanBuilder.Build(play,
                new CameraFraming(FieldMaxZoom, CameraFraming.Default.MarginM, CameraFraming.Default.PitchZoom));
        }

        /// <summary>
        /// 時刻 t の視野を守備カメラへ流す。位置は据え置きで、向き（LookAt）と画角だけが動く。
        /// </summary>
        private void ApplyFieldCamera(float t)
        {
            if (_fieldPlan == null || _fieldPlan.Count == 0) return;
            var vp = CameraEvaluator.ViewportAt(_fieldPlan, t);
            float zoom = (float)System.Math.Max(vp.ZoomMult, 0.01);
            FramePreview(FieldStation,
                new Vector3((float)vp.Cx, FieldAimY, (float)vp.Cy),
                FieldWideHeightM / zoom);
        }

        // ---- 打球プレーの再生 ---------------------------------------------------

        private PlaybackPlay _fieldPlay;
        private float _fieldT;

        /// <summary>打球プレーを再生中か（守備カメラが効いているか）。</summary>
        public bool FieldPlayActive => _fieldPlay != null;

        /// <summary>
        /// 打球プレーの再生を始める。<paramref name="t0"/> は対決ビューで既に見せたぶんの続き
        /// （<c>MatchLiveController.ContactMomentOf</c> と同じ時計）。
        /// </summary>
        public void BeginFieldPlay(PlaybackPlay play, float t0)
        {
            if (!Available || play == null) return;
            EnsureFieldCast();
            SetFieldCastVisible(true);
            _fieldPlay = play;
            BuildFieldCameraPlan(play);
            SetFieldTime(t0);
        }

        /// <summary>打球プレーの再生をやめ、対決ビューの構図へ戻す。</summary>
        public void EndFieldPlay()
        {
            _fieldPlay = null;
            _fieldPlan = null;
            SetFieldCastVisible(false);
            HideBall();
            ClearPreviewFrame();
            ApplyCalibration(_cal);   // ⚠ 画角だけでなくカメラの位置・向きも戻す（ClearPreviewFrame は画角のみ）
        }

        /// <summary>
        /// 時刻 t のフレームを作る。**ボール・カメラはフルレート**（ポーズだけが12fps量子化＝design-18 §4.5-②）。
        /// 位置は engine の評価値を写すだけで、Unity 側で軌道を積分し直さない（不変条件#1）。
        /// </summary>
        public void SetFieldTime(float t)
        {
            if (_fieldPlay == null) return;
            _fieldT = t;

            // 野手・走者の位置（F3 時点では移動クリップが無いので位置だけ＝滑って見える。走りは F4）
            foreach (var kv in _fieldCast)
            {
                var p = PlaybackEvaluator.FielderAt(_fieldPlay, kv.Key, t);
                PlaceFielder(kv.Value, (float)p.Pos.X, (float)p.Pos.Y);
            }

            // ボール
            var ball = PlaybackEvaluator.BallAt(_fieldPlay, t);
            if (_ball != null)
            {
                if (ball.HasValue)
                {
                    _ball.transform.SetParent(_root.transform, false);
                    _ball.transform.localPosition =
                        new Vector3((float)ball.Value.X, (float)ball.Value.H, (float)ball.Value.Y);
                    _ball.SetActive(true);
                }
                else
                {
                    _ball.SetActive(false);
                }
            }

            ApplyFieldCamera(t);
            if (_ball != null && _ball.activeSelf) ApplyBallReadability();
        }

        /// <summary>
        /// 球が画面上で最低 <see cref="BallMinScreenPx"/> になるまで**見かけの大きさだけ**上げる
        /// （監督決定 2026-08-01）。硬式球7.4cmは縦24mの構図で0.8px＝実質見えない。
        /// **位置・軌道は engine の真値のまま**で、触るのはスケールだけ＝当たり所も帯も動かない。
        /// 寄った構図では 1.0 に戻る（近いところで膨らませない）。
        /// </summary>
        private void ApplyBallReadability()
        {
            if (_camera == null || _ball == null) return;
            var p = _ball.transform.position;
            var a = _camera.WorldToScreenPoint(p);
            var b = _camera.WorldToScreenPoint(p + _camera.transform.up * BallRealDiameterM);
            if (a.z <= 0f) return;
            float px = Mathf.Abs(b.y - a.y);
            float k = px > 0.001f ? Mathf.Max(1f, BallMinScreenPx / px) : 1f;
            _ball.transform.localScale = Vector3.one * (BallRealDiameterM * Mathf.Min(k, BallMaxScaleGain));
        }

        /// <summary>硬式球の実寸[m]。</summary>
        private const float BallRealDiameterM = 0.074f;
        /// <summary>画面上で保証する球の直径[px]。読める下限として監督が採った値。</summary>
        private const float BallMinScreenPx = 3f;
        /// <summary>見かけ倍率の上限（引きの絵で球が人より大きくならないための歯止め）。</summary>
        private const float BallMaxScaleGain = 12f;

        // ---- 実測窓（撮影ハーネス用） -------------------------------------------

        /// <summary>
        /// 【撮影ハーネス用】守備9人の「engine が言う位置」と「実際に置かれた位置」をTSVで返す。
        ///
        /// この差は**絵では絶対に読めない**（どちらも野手が立っているだけ）ので、
        /// 位置駆動の受け入れは必ずこの実測で採る。写しているだけなので差は 0.000 のはず。
        /// 投手・捕手だけは対決ステージ側の立ち位置（マウンド z=18.44・捕手 z=-1.0）で置かれており、
        /// engine の定位置（17.4 / -2.2）と**構造的にずれる**＝この行の差が 0 でないのは正常。
        /// </summary>
        public string FielderDiagForCapture()
        {
            var sb = new StringBuilder();
            sb.AppendLine("pos\tengineX\tengineZ\tactualX\tactualZ\tdiffM\tpx\tclip\tyaw");
            foreach (FieldPosition pos in System.Enum.GetValues(typeof(FieldPosition)))
            {
                if (pos == FieldPosition.DesignatedHitter) continue;
                var a = FielderActor(pos);
                PlaybackVec home;
                if (!PlaybackEvaluator.DefaultPositions.TryGetValue(pos, out home)) continue;
                if (a == null || a.Go == null)
                {
                    sb.AppendLine(pos + "\t" + home.X.ToString("F2") + "\t" + home.Y.ToString("F2") + "\t(未生成)");
                    continue;
                }
                var lp = a.Go.transform.localPosition;
                float dx = lp.x - (float)home.X, dz = lp.z - (float)home.Y;
                sb.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0}\t{1:F2}\t{2:F2}\t{3:F2}\t{4:F2}\t{5:F3}\t{6}\t{7}\t{8:F1}",
                    pos, home.X, home.Y, lp.x, lp.z, Mathf.Sqrt(dx * dx + dz * dz),
                    ScreenHeightPx(a), a.Clip != null ? a.Clip.name : "(null)",
                    a.Go.transform.localEulerAngles.y));
            }
            return sb.ToString();
        }

        /// <summary>
        /// 【撮影ハーネス用】打球プレー1コマの実測を1行のTSVで返す。
        /// 「打球の行方が読めるか」は絵で見るが、**なぜ読めないのか**（枠が広すぎる／球が小さすぎる／
        /// 追えていない）は絵では切り分けられないので、枠の実寸と画面pxを並べて出す。
        /// </summary>
        public string FieldFrameDiagForCapture(float t)
        {
            var ball = _fieldPlay == null ? null : PlaybackEvaluator.BallAt(_fieldPlay, t);
            float frameH = -1f;
            if (_fieldPlan != null && _fieldPlan.Count > 0)
                frameH = FieldWideHeightM / (float)System.Math.Max(CameraEvaluator.ViewportAt(_fieldPlan, t).ZoomMult, 0.01);

            // **注視点にいる人**の大きさ＝この構図の読みやすさそのもの。
            // 特定の役（中堅手など）で測ると、その役がプレーに絡まない時に意味のない数になる。
            int aimPx = frameH > 0.01f && Target != null
                ? Mathf.RoundToInt(TargetHeight / frameH * Target.height) : -1;

            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0}\t{1:F1}\t{2:F2}\t{3}\t{4}\t{5}\t{6:F1}\t{7}\t{8}",
                _fieldPlay != null ? _fieldPlay.Name : "(none)", FieldMaxZoom, t,
                ball.HasValue ? ball.Value.X.ToString("F1") : "-",
                ball.HasValue ? ball.Value.Y.ToString("F1") : "-",
                ball.HasValue ? ball.Value.H.ToString("F1") : "-",
                frameH, aimPx, BallScreenPx());
        }

        /// <summary>スケール補正後の球が画面上で何pxか（補正が効いているかの裏取り）。</summary>
        private int BallScreenPx()
        {
            if (_camera == null || _ball == null || !_ball.activeSelf) return -1;
            var p = _ball.transform.position;
            var a = _camera.WorldToScreenPoint(p);
            var b = _camera.WorldToScreenPoint(p + _camera.transform.up * _ball.transform.localScale.y);
            if (a.z <= 0f) return -1;
            return Mathf.RoundToInt(Mathf.Abs(b.y - a.y));
        }

        /// <summary>
        /// その構図で「立位1.75mの人」が RT 上で何pxになるかを実測する。
        /// 捕球の芝居が読めるかの判断に直結する数だが目測では出せないので実測で持つ。
        /// カメラの背後（＝映らない）なら -1。
        ///
        /// ⚠ **レンダラのバウンズでは測れない**。守備陣は <c>tightBounds</c> で
        /// ±5m の固定箱に差し替えてある（毎フレームのフルスキニングを止めるため）ので、
        /// 包含AABBを投影すると桁が1つ違う値が出る（実測207px＝実際の約13倍。2026-08-01）。
        /// 足元と頭上の2点だけを投影する＝バウンズの作りに依存しない。
        /// 姿勢（内野のクラウチ等）は反映しない公称値で、役をまたいで比べられるのが利点。
        /// </summary>
        private int ScreenHeightPx(Actor a)
        {
            if (_camera == null || a == null || a.Go == null) return -1;
            var foot = a.Go.transform.position;
            var head = foot + Vector3.up * TargetHeight;
            var p0 = _camera.WorldToScreenPoint(foot);
            var p1 = _camera.WorldToScreenPoint(head);
            if (p0.z <= 0f || p1.z <= 0f) return -1;
            return Mathf.RoundToInt(Mathf.Abs(p1.y - p0.y));
        }
    }
}
