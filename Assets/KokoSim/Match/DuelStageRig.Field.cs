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
                _fieldIdleClip[pos] = clip;
            }

            // 投手・捕手も打球中は守備陣の一員として動く（engine の Moves に入る）。
            // 待機は対決用のクリップ（投球・捕手の構え）ではなく守備の待機を使い、
            // プレーが終わったら EndFieldPlay が対決の状態ごと戻す
            _fieldIdleClip[FieldPosition.Pitcher] = outfieldClip;
            _fieldIdleClip[FieldPosition.Catcher] = infieldClip;

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
        /// 野手を stage-local の (x,z) へ置く。yaw を渡さなければ本塁を向く。
        /// 縦は <see cref="GroundFix"/> が骨の実測で解く（クリップごとに根の高さが違うため）。
        /// </summary>
        private void PlaceFielder(Actor a, float x, float z, float? yawDeg = null)
        {
            if (a == null || a.Go == null) return;
            var lp = a.Go.transform.localPosition;
            a.Go.transform.localPosition = new Vector3(x, lp.y, z);
            a.Go.transform.localRotation = Quaternion.Euler(0f, yawDeg ?? YawFacingHome(x, z), 0f);
            a.BasePos = a.Go.transform.localPosition;
            a.BaseRot = a.Go.transform.localRotation;
            a.GroundY = FieldGroundY(x, z);   // マウンドの上だけ持ち上がる（投手が降りたら地面へ）
            GroundFix(a);
        }

        /// <summary>
        /// その地点の接地高[m]。マウンドの上だけ <see cref="MoundHeight"/> で、縁で地面へ落ちる。
        ///
        /// 投手の <c>GroundY</c> は生成時にマウンド高で焼かれている。動かすようになると
        /// **マウンドを降りても 25cm 浮いたまま**になる（枠10mの構図で14px＝はっきり見える）。
        /// 位置で決まる量なので毎コマ引き直す＝「特定のコマでだけ」の分岐にしない（design-18 §4.5-⑩）。
        /// </summary>
        private static float FieldGroundY(float x, float z)
        {
            float r = Mathf.Sqrt(x * x + (z - MoundDistance) * (z - MoundDistance));
            return MoundHeight * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(2.0f, 2.7f, r)));
        }

        // ---- 走る（F4） ---------------------------------------------------------
        //
        // engine は位置しか返さない（`FielderAt` は easeIO で塁間を補間するだけ）。
        // Unity 側の仕事は「その位置へ置く」ことではなく「**その位置の動き方に足を合わせる**」こと。
        // 合わせないと、空中を滑るか、足だけ空回りする。

        /// <summary>
        /// 走行クリップの実測値（<c>out/duel-stage/locomotion.tsv</c>・
        /// <c>DuelMotionSpike.RunLocomotionProbe</c>）。**目分量の値を置かない**。
        /// </summary>
        private readonly struct LocoClip
        {
            /// <summary>クリップ名。</summary>
            public readonly string Name;
            /// <summary>1ループの前進量 ÷ 尺 [m/s]。<c>Speed = v_engine / VClip</c> の分母。</summary>
            public readonly float VClip;
            /// <summary>
            /// クリップのローカル座標での**進行方向**[度]（+Z を 0 とする）。
            ///
            /// ⚠ **体の向きではない**。モーキャップは収録中に横へ流れており、実測では
            /// 走りループが −9.6°、全力が −12.9° ずれている。体の向きを速度ベクトルへ合わせると
            /// **全員がその角度ぶんカニ歩きする**（絵では「なんとなく斜め」としか見えない）。
            /// 合わせるのは進行方向のほう。
            /// </summary>
            public readonly float MoveDirDeg;

            public LocoClip(string name, float vClip, float moveDirDeg)
            {
                Name = name; VClip = vClip; MoveDirDeg = moveDirDeg;
            }
        }

        /// <summary>速さの段。遅い順（同じ誤差なら遅い段を採る＝走らせすぎない）。</summary>
        private static readonly LocoClip[] LocoLadder =
        {
            new LocoClip("AA_Baseball_Defender_Movement_Walk_F_Loop", 0.73f, -4.5f),
            new LocoClip("AA_Baseball_Defender_Movement_Run_Loop", 4.28f, -9.6f),
            new LocoClip("AA_Baseball_Defender_Movement_Sprint_Loop", 5.11f, -12.9f),
        };

        /// <summary>再生速度の許容幅。外れると足の運びが目に見えて嘘になる。</summary>
        private const float LocoSpeedMin = 0.6f, LocoSpeedMax = 1.45f;

        /// <summary>これ未満は待機（走らせない）[m/s]。easeIO の出入りで毎回0を通るので要る。</summary>
        private const float FielderIdleSpeedMps = 0.35f;

        /// <summary>向き変えの上限[度/秒]。無いと1コマで振り向いてコマ落ちに見える。</summary>
        private const float FielderYawRateDegPerSec = 540f;

        /// <summary>速度を採る中央差分の刻み[秒]。</summary>
        private const float LocoSampleDt = 1f / 60f;

        /// <summary>立ち位置の不連続を吸収する時間[秒]（下の <see cref="FielderMotion.Entry"/>）。</summary>
        private const float EntryFadeSeconds = 0.3f;

        private struct FielderMotion
        {
            /// <summary>いま流している段（-1=待機）。</summary>
            public int Ladder;
            /// <summary>いまの yaw[度]（レート制限の積分値）。</summary>
            public float Yaw;
            public bool Started;
            /// <summary>
            /// プレー開始時の「実際の立ち位置 − engine の言う立ち位置」[m]。
            ///
            /// 対決ステージの投手 z=18.44・捕手 z=−1.0 に対し engine の定位置は 17.4／−2.2 で、
            /// **構造的に 1.347m／1.200m ずれている**（F2 実測）。差をそのまま無視すると
            /// 打球の瞬間に2人だけワープする。0.3秒かけて 0 へ落として吸収する。
            /// </summary>
            public Vector2 Entry;
        }

        private readonly Dictionary<FieldPosition, FielderMotion> _loco = new();

        /// <summary>走行中に <c>IdleClip</c> を差し替えるので、本来の待機クリップを控えておく。</summary>
        private readonly Dictionary<FieldPosition, AnimationClip> _fieldIdleClip = new();

        /// <summary>守備の9人（engine が動かす対象）。</summary>
        private static readonly FieldPosition[] AllFieldPositions =
        {
            FieldPosition.Pitcher, FieldPosition.Catcher, FieldPosition.FirstBase,
            FieldPosition.SecondBase, FieldPosition.ThirdBase, FieldPosition.Shortstop,
            FieldPosition.LeftField, FieldPosition.CenterField, FieldPosition.RightField,
        };

        /// <summary>
        /// 時刻 t の野手1人を「engine の位置」へ置き、その動きに足と向きを合わせる。
        /// </summary>
        /// <param name="dt">前回の <c>SetFieldTime</c> からの経過[秒]（向き変えのレート制限に使う）。</param>
        private void StepFielderMotion(FieldPosition pos, Actor a, float t, float dt)
        {
            if (a == null || a.Go == null) return;
            FielderMotion st;
            if (!_loco.TryGetValue(pos, out st)) st = new FielderMotion { Ladder = -1 };
            float prevYaw = st.Yaw;
            bool hadYaw = st.Started;

            var cur = PlaybackEvaluator.FielderAt(_fieldPlay, pos, t);

            // 速度は中央差分。engine は位置しか返さないので、速さも向きもここで採るしかない
            float ta = Mathf.Max(0f, t - LocoSampleDt);
            float tb = t + LocoSampleDt;
            var pa = PlaybackEvaluator.FielderAt(_fieldPlay, pos, ta);
            var pb = PlaybackEvaluator.FielderAt(_fieldPlay, pos, tb);
            float span = Mathf.Max(1e-4f, tb - ta);
            float vx = (float)(pb.Pos.X - pa.Pos.X) / span;
            float vz = (float)(pb.Pos.Y - pa.Pos.Y) / span;
            float speed = Mathf.Sqrt(vx * vx + vz * vz);

            // 段を選ぶ。クランプ後にどれだけ嘘が残るかで比べる（比なので対数で見る）
            int want = -1;
            float mul = 1f;
            if (speed >= FielderIdleSpeedMps)
            {
                float best = float.MaxValue;
                for (int i = 0; i < LocoLadder.Length; i++)
                {
                    float m = Mathf.Clamp(speed / LocoLadder[i].VClip, LocoSpeedMin, LocoSpeedMax);
                    float err = Mathf.Abs(Mathf.Log(speed / (LocoLadder[i].VClip * m)));
                    if (err < best - 1e-4f) { best = err; want = i; mul = m; }
                }
                // 足と地面の食い違い。1.0=一致 / >1=地面のほうが速い（前へ滑る）/
                // <1=足のほうが速い（後ろへ滑る＝ムーンウォーク）。**絵で見えるのはこの比**であって
                // 「クランプに当たったコマ数」ではない（当たっても比が1に近ければ見えない）
                if (dt > 0f)
                {
                    _locoMoveFrames++;
                    float ratio = speed / Mathf.Max(0.01f, LocoLadder[want].VClip * mul);
                    _locoSlipMax = Mathf.Max(_locoSlipMax, ratio);
                    _locoSlipMin = Mathf.Min(_locoSlipMin, ratio);
                    if (ratio > 1.15f || ratio < 0.87f) _locoClampFrames++;
                }
            }

            // 向き。走っている間は**進行方向**をエンジンの速度ベクトルへ合わせる（体の向きではない）
            float targetYaw = want < 0
                ? YawFacingHome((float)cur.Pos.X, (float)cur.Pos.Y)
                : Mathf.Atan2(vx, vz) * Mathf.Rad2Deg - LocoLadder[want].MoveDirDeg;
            st.Yaw = st.Started
                ? Mathf.MoveTowardsAngle(st.Yaw, targetYaw, FielderYawRateDegPerSec * Mathf.Max(0f, dt))
                : targetYaw;
            st.Started = true;

            // クリップの差し替え。**位相を保って移る**（0へ戻すと足が跳ぶ）
            if (want != st.Ladder)
            {
                var clip = want < 0 ? IdleClipFor(pos) : LoadClip(LocoLadder[want].Name);
                if (clip != null && clip != a.Clip)
                {
                    float phase = a.Clip != null && a.Clip.length > 0.01f
                        ? Mathf.Repeat(a.Time / a.Clip.length, 1f) : 0f;
                    SetActorClip(a, clip, phase * clip.length, 0f);
                    a.IdleClip = clip;   // ループさせる（FinishOneShot がここへ戻る）
                    a.IdleEndAt = 0f;
                }
                st.Ladder = want;
            }
            // ⚠ Speed は SetActorClip では戻らない。毎コマ入れ直す（差し替えの前後で持ち越さない）
            a.Speed = want < 0 ? 1f : mul;

            // 立ち位置の不連続を 0 へ落としながら engine の位置へ置く
            float fade = EntryFadeSeconds <= 0f ? 0f
                : Mathf.Clamp01(1f - (t - _fieldT0) / EntryFadeSeconds);
            PlaceFielder(a, (float)cur.Pos.X + st.Entry.x * fade,
                (float)cur.Pos.Y + st.Entry.y * fade, st.Yaw);

            // 受け入れの実測。**どちらも絵では読めない**（位置のずれは「野手が立っている」だけ、
            // 向きの飛びは1コマなので静止画に残らない）ので、通しながら最悪値を溜める
            if (dt > 0f && t > _fieldT0 + EntryFadeSeconds)
            {
                var lp = a.Go.transform.localPosition;
                _locoMaxPosErr = Mathf.Max(_locoMaxPosErr,
                    new Vector2(lp.x - (float)cur.Pos.X, lp.z - (float)cur.Pos.Y).magnitude);
            }
            if (dt > 0f && hadYaw)
                _locoMaxYawStep = Mathf.Max(_locoMaxYawStep, Mathf.Abs(Mathf.DeltaAngle(prevYaw, st.Yaw)));
            _locoMaxSpeed = Mathf.Max(_locoMaxSpeed, speed);

            _loco[pos] = st;
        }

        private float _locoMaxPosErr, _locoMaxYawStep, _locoMaxSpeed;
        private int _locoMoveFrames, _locoClampFrames;
        private float _locoSlipMax, _locoSlipMin;

        /// <summary>
        /// 【撮影ハーネス用】走りの受け入れ実測（プレー開始からの最悪値）。
        ///
        /// <c>posErr</c> は「engine の言う位置」と実際に置いた位置の差＝**写しているだけなので 0.000 のはず**。
        /// 0 でなければ位置を engine 以外の何かが動かしている（＝不変条件#1 違反）。
        /// <c>yawStep</c> は1コマの向き変化＝レート制限が効いているかどうか。
        /// <c>vMax</c> は engine が野手に要求する最大の速さ＝段の選び方が妥当かの根拠。
        /// **どれも絵からは読めない**（立っている人と、1コマだけ飛んだ向き）。
        /// </summary>
        public string FielderRunDiagForCapture() => string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "posErrMax={0:F3}m\tyawStepMax={1:F1}deg\tvMax={2:F2}m/s\tslip={3:F2}〜{4:F2}\t目立つ={5}/{6}コマ",
            _locoMaxPosErr, _locoMaxYawStep, _locoMaxSpeed,
            _locoSlipMin > 1e6f ? 1f : _locoSlipMin, _locoSlipMax, _locoClampFrames, _locoMoveFrames);

        /// <summary>その役の本来の待機クリップ（走行中は <c>IdleClip</c> が差し替わっているため別に持つ）。</summary>
        private AnimationClip IdleClipFor(FieldPosition pos)
        {
            AnimationClip c;
            return _fieldIdleClip.TryGetValue(pos, out c) ? c : null;
        }

        // ---- 球場の面（内野の土・刈り分け・フェンス。S2/S3・監督決定 2026-08-01） -----
        //
        // 一様な芝だけだと、寄った守備カメラの絵が「緑の虚無に立つ人」になり、
        // 大きさの基準も向きも読めない（F3 の撮影で判明）。2D俯瞰が描いている
        // フェア芝／刈り込みストライプ／内野ダート／ウォーニングトラック／フェンス弧を
        // **同じ幾何・同じ色**で3Dへ起こす（Match2DPlaybackElement.DrawField と対応）。
        // 弧の式は engine の FieldDiagramGeometry が単一ソース＝2Dと3Dで形がずれない。

        private const float FenceWingM = 95f;    // 両翼（2D の _wingM と同値）
        private const float FenceCenterM = 118f; // 中堅（同 _centerM）
        private const float FenceHeightM = 3.2f;
        private const float WarningTrackM = 3.5f;
        private const float HomeCircleM = 4.3f;
        private const float FoulQuarterRad = Mathf.PI / 4f;   // フェア領域は ±45°

        // 高さの積み順（本塁プレート0.012・打席0.011 より下に収める）
        private const float YFair = 0.002f;
        private const float YStripe = 0.003f;
        private const float YWarning = 0.004f;
        private const float YDirt = 0.005f;
        private const float YHomeCircle = 0.006f;

        // ⚠ **2D俯瞰のパレットをそのまま持ち込めない**（2026-08-01 実測）。
        // 対決ビューはポスタライズ6段のポストを通すので、出力は 38/84/128/171/214 付近の段に丸められる。
        //   ・2Dの clay 0xA26E44 は緑が1段落ちて (171,84,84)＝**ピンク**に見える
        //     （マウンドの既存色 (0.63,0.47,0.33) は (171,128,84) の正しい茶）
        //   ・2Dの芝 0x4F7C3B と刈り込み 0x578646 は**同じ段に丸められて縞が完全に消える**
        //     （明暗どちらも実測 (84,128,38)）
        // ＝色は「2Dと同じ見た目」でなく「**段に乗る**」ことを基準に選ぶ。幾何だけ2Dと共有する。

        /// <summary>内野ダート・ウォーニングトラック。マウンドの既存色と同値＝段に乗る茶。</summary>
        private static readonly Color ClayColor = new(0.63f, 0.47f, 0.33f);
        /// <summary>フェア芝（刈り込みの暗側）。素の地面より1段暗い緑。</summary>
        private static readonly Color FairTurfColor = new(0.30f, 0.50f, 0.24f);
        /// <summary>刈り込みの明側。緑を1段上へ跨がせないと縞が消える。</summary>
        private static readonly Color StripeTurfColor = new(0.36f, 0.64f, 0.30f);
        /// <summary>外野フェンス。</summary>
        private static readonly Color FenceColor = new(0.14f, 0.26f, 0.18f);
        /// <summary>フェンスの外側を塞ぐ暗インク壁（バックネット書き割りと同じ思想）。</summary>
        private static readonly Color BackdropColor = new(0.12f, 0.20f, 0.16f);

        /// <summary>本塁からの距離[m]（方位角θ・+Z=センターから+X側が正）。2Dの FencePoint と同式。</summary>
        private static float FenceR(float th) =>
            (float)FieldDiagramGeometry.FenceRadius(th, FenceWingM, FenceCenterM);

        /// <summary>
        /// 球場の面を組む。<c>BuildGround</c> の後（＝地面プレーンの上）に重ねる。
        /// </summary>
        private void BuildFieldSurface(int layer)
        {
            // ② フェア芝（本塁→フェンス弧の扇）
            AddFan("turf-fair", th => FenceR(th), YFair, FairTurfColor, layer);

            // ④ 放射ストライプ（刈り込み）。±45°を8ウェッジに割り、奇数番だけ明側で重ね塗り
            for (int w = 1; w < 8; w += 2)
            {
                float t0 = -FoulQuarterRad + FoulQuarterRad * 2f * w / 8f;
                float t1 = -FoulQuarterRad + FoulQuarterRad * 2f * (w + 1) / 8f;
                AddFan("turf-stripe-" + w, th => FenceR(th), YStripe, StripeTurfColor, layer, t0, t1, 8);
            }

            // ウォーニングトラック（フェンス手前の土帯）
            AddBand("warning-track", th => FenceR(th) - WarningTrackM, th => FenceR(th),
                YWarning, ClayColor, layer);

            // ⑤ 内野ダート扇（甲子園様式＝内野は全面ダート）。境界弧はマウンド中心のグラスライン
            AddFan("infield-dirt", th => (float)FieldDiagramGeometry.InfieldDirtRadius(th),
                YDirt, ClayColor, layer);

            // 本塁の土円（既存の dirt-home より一回り大きい2D基準の半径へ）
            AddFan("home-circle", _ => HomeCircleM, YHomeCircle, ClayColor, layer,
                -Mathf.PI, Mathf.PI, 48);

            // 外野フェンス（弧に沿った垂直の帯）＋その外側の暗インク壁（世界の果てを塞ぐ）
            AddWall("fence", th => FenceR(th), 0f, FenceHeightM, FenceColor, layer);
            // 壁は「フェンスの向こうを塞ぐ」だけの書き割り。高すぎると画面上端を黒く占領する
            AddWall("fence-backdrop", th => FenceR(th) + 3f, 0f, 7f, BackdropColor, layer);
        }

        private delegate float RadiusAt(float thetaRad);

        /// <summary>本塁を中心とした扇（θ範囲・半径関数）の平らなメッシュ。</summary>
        private void AddFan(string name, RadiusAt r, float y, Color color, int layer,
            float th0 = -FoulQuarterRad, float th1 = FoulQuarterRad, int seg = 48)
        {
            var verts = new Vector3[seg + 2];
            var tris = new int[seg * 3];
            verts[0] = Vector3.zero;
            for (int i = 0; i <= seg; i++)
            {
                float th = Mathf.Lerp(th0, th1, i / (float)seg);
                float rad = r(th);
                verts[i + 1] = new Vector3(rad * Mathf.Sin(th), 0f, rad * Mathf.Cos(th));
            }
            for (int i = 0; i < seg; i++)
            {
                tris[i * 3] = 0; tris[i * 3 + 1] = i + 1; tris[i * 3 + 2] = i + 2;
            }
            AddSurface(name, verts, tris, y, color, layer);
        }

        /// <summary>2つの半径関数に挟まれた帯（ウォーニングトラック等）。</summary>
        private void AddBand(string name, RadiusAt rIn, RadiusAt rOut, float y, Color color, int layer,
            float th0 = -FoulQuarterRad, float th1 = FoulQuarterRad, int seg = 48)
        {
            var verts = new Vector3[(seg + 1) * 2];
            var tris = new int[seg * 6];
            for (int i = 0; i <= seg; i++)
            {
                float th = Mathf.Lerp(th0, th1, i / (float)seg);
                float s = Mathf.Sin(th), c = Mathf.Cos(th);
                verts[i * 2] = new Vector3(rIn(th) * s, 0f, rIn(th) * c);
                verts[i * 2 + 1] = new Vector3(rOut(th) * s, 0f, rOut(th) * c);
            }
            for (int i = 0; i < seg; i++)
            {
                int b = i * 2, t = i * 6;
                tris[t] = b; tris[t + 1] = b + 1; tris[t + 2] = b + 3;
                tris[t + 3] = b; tris[t + 4] = b + 3; tris[t + 5] = b + 2;
            }
            AddSurface(name, verts, tris, y, color, layer);
        }

        /// <summary>弧に沿った垂直の帯（フェンス・背景壁）。内側から見えるよう両面で作る。</summary>
        private void AddWall(string name, RadiusAt r, float y0, float y1, Color color, int layer,
            float th0 = -FoulQuarterRad - 0.12f, float th1 = FoulQuarterRad + 0.12f, int seg = 48)
        {
            var verts = new Vector3[(seg + 1) * 2];
            var tris = new int[seg * 12];   // 表裏の2枚
            for (int i = 0; i <= seg; i++)
            {
                float th = Mathf.Lerp(th0, th1, i / (float)seg);
                float rad = r(th);
                float s = Mathf.Sin(th), c = Mathf.Cos(th);
                verts[i * 2] = new Vector3(rad * s, y0, rad * c);
                verts[i * 2 + 1] = new Vector3(rad * s, y1, rad * c);
            }
            for (int i = 0; i < seg; i++)
            {
                int b = i * 2, t = i * 12;
                tris[t] = b; tris[t + 1] = b + 1; tris[t + 2] = b + 3;
                tris[t + 3] = b; tris[t + 4] = b + 3; tris[t + 5] = b + 2;
                // 裏面（巻き順を逆に）。片面だとカメラ側から抜けて見える
                tris[t + 6] = b; tris[t + 7] = b + 3; tris[t + 8] = b + 1;
                tris[t + 9] = b; tris[t + 10] = b + 2; tris[t + 11] = b + 3;
            }
            AddSurface(name, verts, tris, 0f, color, layer);
        }

        private void AddSurface(string name, Vector3[] verts, int[] tris, float y, Color color, int layer)
        {
            var mesh = new Mesh { name = name };
            // ⚠ RecalculateNormals は呼ばない（design-18 の掟）。平面なので法線は一定＝上向きで足りる
            mesh.vertices = verts;
            mesh.triangles = tris;
            var normals = new Vector3[verts.Length];
            for (int i = 0; i < normals.Length; i++) normals[i] = Vector3.up;
            mesh.normals = normals;
            mesh.RecalculateBounds();
            _meshes.Add(mesh);

            var go = new GameObject(name);
            go.transform.SetParent(_root.transform, false);
            go.transform.localPosition = new Vector3(0f, y, 0f);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = FlatMaterial(color);
            go.layer = layer;
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
        /// 守備カメラの据え位置（stage-local[m]）。本塁後方に**2台**据える（監督決定 2026-08-01・案D）。
        ///
        /// 高さは絵に直結する。低いほど視線が寝て選手を横から見る＝立体が出るが、
        /// 遠くの被写体では背後のフェンス（3.2m）と暗幕（7m）が画面の半分を埋める
        /// （寄った枠は縦10m級なので、90m先へ向けると縦の画角は6.6°しかない）。
        /// 高くすると外野の背景に芝と空が入る代わりに、内野が見下ろしになる。
        /// **足して2で割れない**ので内野用と外野用に分け、被写体の深さで切り替える。
        ///
        /// ⚠ z は **> −16**（バックネット書き割り z=−16・34×8m より手前）。
        /// 奥へ引くと壁が画面下半分を黒く塞ぐ（2026-08-01 実害）。
        /// </summary>
        public static Vector3 FieldStationInfield = new(0f, 10f, -14f);

        /// <inheritdoc cref="FieldStationInfield"/>
        public static Vector3 FieldStationOutfield = new(0f, 24f, -14f);

        /// <summary>
        /// 外野の据え位置へ切り替える被写体の深さ[m]（本塁からの距離）。
        /// 二塁が 38.8m なので、内野のプレーが全部おさまる外側に置く。
        /// </summary>
        private const float StationSwitchDepthM = 45f;

        /// <summary>
        /// 切り替え判定の先読み[s]。**今どこかでなく、これからどこへ行くか**で決める。
        /// これが無いと外野フライで「打球が45mを通過した瞬間」に切れる＝飛んでいる最中のカットになる。
        /// 先読みしておけば打った直後（対決→守備のカットと同時）に外野側で始まるので、
        /// 実質カットが増えない。
        /// </summary>
        private const float StationLeadSeconds = 1.2f;

        /// <summary>最小滞在[s]。これより短い切り替えは前の据え位置を延長して潰す（チカチカ防止）。</summary>
        private const float StationMinDwellSeconds = 0.8f;

        private const float StationSampleStepSeconds = 0.05f;

        /// <summary>注視点の高さの下限[m]。地面でなく腰の高さに取ると、寄ったときに人が下に切れない。</summary>
        private const float FieldAimYMin = 1.0f;

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
        public static float FieldMaxZoom = 40f;

        /// <summary>
        /// 被写体まわりに残す余白[m]。**寄りの深さを決めているのは実はこちら**で、<see cref="FieldMaxZoom"/> ではない。
        ///
        /// <c>FitBox</c> は箱の縦横に 2×Margin を足してからズームを決めるので、箱が点まで縮んでも
        /// <c>zoom ≤ 155.2/(2×Margin)</c> ＝ **frameH は 2×Margin より小さくならない**。
        /// 既定の 6.0m では frameH の下限が 12m（＝上限 12.9 倍）で、これより上に MaxZoom を上げても
        /// 1mm も寄らない。**絵で決める値**なので比較撮影のために差し替えられるようにしてある。
        /// </summary>
        public static float FieldMarginM = 2f;

        /// <summary>
        /// 送球区間で球を追うか。2D俯瞰の既定は「始点と終点を両方収める」＝送球の距離ぶん必ず引くので、
        /// 6-3 の一塁到達や中継バックホームという**そのプレーで一番見たい瞬間が一番引いた絵になる**
        /// （実測 frameH 20m / 56m）。3Dでは中継カメラと同じく球を追う側にする。
        /// </summary>
        public static bool FieldThrowFollowsBall = true;

        private System.Collections.Generic.IReadOnlyList<CameraKeyframe> _fieldPlan;

        /// <summary>engine の追随カメラのプランを、この rig の守備カメラ用に組む。</summary>
        private void BuildFieldCameraPlan(PlaybackPlay play)
        {
            _fieldPlan = play == null ? null : CameraPlanBuilder.Build(play,
                new CameraFraming(FieldMaxZoom, FieldMarginM, CameraFraming.Default.PitchZoom,
                    FieldThrowFollowsBall));
        }

        /// <summary>
        /// 時刻 t の構図（注視点と縦に収める実寸）を求める。
        ///
        /// 水平（<c>Cx,Cy</c>）とズームは engine の追随カメラのプランそのもの。**高さだけは 2D の俯瞰に
        /// 存在しない＝プランが持っていない**ので、ここで足す。無いと寄った瞬間にフライが画面の上へ抜ける
        /// （枠 19m の構図に対し打球の頂点は 30m 級＝1枠ぶん以上うわを通る）。
        /// 足し方はプランと同型＝**同じ箱（今の球と先読み先の球）を縦にも取る**だけで、
        /// 新しい演出判断を持ち込まない。球が無い区間（保持中・全景復帰後）は下限の腰の高さに落ちる。
        /// </summary>
        private void ComputeFieldFrame(float t, out Vector3 aim, out float frameH)
        {
            var vp = CameraEvaluator.ViewportAt(_fieldPlan, t);
            float zoom = (float)System.Math.Max(vp.ZoomMult, 0.01);
            frameH = FieldWideHeightM / zoom;

            float aimY = FieldAimYMin;
            var b0 = PlaybackEvaluator.BallAt(_fieldPlay, t);
            if (b0.HasValue)
            {
                var b1 = PlaybackEvaluator.BallAt(_fieldPlay, t + (float)CameraPlanBuilder.LookaheadSeconds);
                float h0 = (float)b0.Value.H;
                float h1 = b1.HasValue ? (float)b1.Value.H : h0;
                aimY = Mathf.Max(FieldAimYMin, (h0 + h1) * 0.5f);
                frameH = Mathf.Max(frameH, Mathf.Abs(h0 - h1) + FieldMarginM * 2f);
            }
            aim = new Vector3((float)vp.Cx, aimY, (float)vp.Cy);
        }

        /// <summary>
        /// 据え位置の切り替え計画。<c>(切り替わる時刻, 外野側か)</c> の列で、先頭は必ず T=0。
        /// **再生前に一度だけ組む**（毎フレームの判定にしない）＝どの時刻から再生を始めても
        /// 同じ絵になり、閾値付近で往復してもチカチカしない。
        /// </summary>
        private readonly System.Collections.Generic.List<(float T, bool Outfield)> _fieldStationCuts
            = new System.Collections.Generic.List<(float, bool)>();

        /// <param name="t0">
        /// 守備カメラが映り始める時刻（＝対決ビューから切り替わる打球の瞬間）。
        /// **これより前は判定に入れない**。打球が出るのは t≈0.5s なので、0 から数えると
        /// 「カットした直後に0.4秒だけ内野カメラが挟まってすぐ外野へ飛ぶ」＝チラつきになる。
        /// </param>
        private void BuildFieldStationPlan(PlaybackPlay play, float t0)
        {
            _fieldStationCuts.Clear();
            if (play == null || _fieldPlan == null || _fieldPlan.Count == 0) return;

            // 決着（ResAt）までで決め、そのあとは据え置く。全景へ戻る区間まで判定に入れると
            // 「引きの中心は常に外野寄り（WideCy=69m）」なので、内野のプレーが終わるたびに
            // 最後の最後で外野側へ切り替わる＝要らないカットが1回増える。
            float end = (float)System.Math.Min(play.Dur, 120.0);
            if (double.IsFinite(play.ResAt)) end = Mathf.Min(end, (float)play.ResAt);
            float start = Mathf.Clamp(t0, 0f, end);
            if (end <= start) { _fieldStationCuts.Add((start, false)); return; }

            var runs = new System.Collections.Generic.List<(float T0, float T1, bool O)>();
            bool cur = false;
            float runStart = start;
            bool started = false;
            for (float t = start; ; t += StationSampleStepSeconds)
            {
                if (t > end) t = end;
                float lead = Mathf.Min(t + StationLeadSeconds, end);
                bool o = CameraEvaluator.ViewportAt(_fieldPlan, lead).Cy > StationSwitchDepthM;
                if (!started) { cur = o; runStart = t; started = true; }
                else if (o != cur) { runs.Add((runStart, t, cur)); cur = o; runStart = t; }
                if (t >= end) break;
            }
            runs.Add((runStart, end, cur));

            // 先頭の短い区間は**次のほうへ譲る**（後ろの短い区間は前を延長して潰すのと逆向き）。
            // 頭だけは「前」が無いので、同じ扱いにすると必ず1コマぶんのチラつきが残る。
            int i0 = 0;
            while (i0 + 1 < runs.Count && runs[i0].T1 - runs[i0].T0 < StationMinDwellSeconds) i0++;

            for (int i = i0; i < runs.Count; i++)
            {
                var r = runs[i];
                if (_fieldStationCuts.Count == 0) { _fieldStationCuts.Add((start, r.O)); continue; }
                if (r.T1 - r.T0 < StationMinDwellSeconds) continue;   // 短すぎ＝前を延長
                if (r.O == _fieldStationCuts[_fieldStationCuts.Count - 1].Outfield) continue;
                _fieldStationCuts.Add((r.T0, r.O));
            }
        }

        /// <summary>時刻 t に使う据え位置。</summary>
        private Vector3 FieldStationAt(float t)
        {
            bool outfield = false;
            for (int i = 0; i < _fieldStationCuts.Count; i++)
            {
                if (_fieldStationCuts[i].T > t) break;
                outfield = _fieldStationCuts[i].Outfield;
            }
            return outfield ? FieldStationOutfield : FieldStationInfield;
        }

        /// <summary>
        /// 時刻 t の視野を守備カメラへ流す。据え位置は切り替え計画から採り、
        /// 同じ据え位置でいる間は**位置が動かない**（向きと画角だけが動く＝中継の三脚と同じ）。
        /// </summary>
        private void ApplyFieldCamera(float t)
        {
            if (_fieldPlan == null || _fieldPlan.Count == 0) return;
            Vector3 aim;
            float frameH;
            ComputeFieldFrame(t, out aim, out frameH);
            FramePreview(FieldStationAt(t), aim, frameH);
        }

        // ---- 打球プレーの再生 ---------------------------------------------------

        private PlaybackPlay _fieldPlay;
        private float _fieldT;
        private float _fieldT0;

        /// <summary>
        /// 打球プレー中だけ差し替える投手・捕手の状態。対決の状態機械（セット姿勢で待つ・
        /// フォロースルーで打ち切る等）をそのまま踏むと戻せなくなるので、丸ごと控えて戻す。
        /// </summary>
        private struct DuelActorSnapshot
        {
            public AnimationClip Clip, IdleClip;
            public float Time, Speed, EndAt, DefaultEndAt, IdleEndAt, GroundY;
            public bool HoldAtEnd, Holding, Valid;
            public Vector3 BasePos;
            public Quaternion BaseRot;
        }

        private DuelActorSnapshot _pitcherSnap, _catcherSnap;

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
            _fieldT0 = t0;
            _pitcherSnap = Snapshot(_pitcher);
            _catcherSnap = Snapshot(_catcher);

            // 立ち位置の不連続を控える。engine の言う定位置と実際の立ち位置の差を
            // **プレーの頭で1回だけ**採り、0.3秒かけて 0 へ落とす（投手1.347m・捕手1.200m）
            _loco.Clear();
            _locoMaxPosErr = _locoMaxYawStep = _locoMaxSpeed = 0f;
            _locoMoveFrames = _locoClampFrames = 0;
            _locoSlipMax = 0f; _locoSlipMin = float.MaxValue;
            foreach (var pos in AllFieldPositions)
            {
                var a = FielderActor(pos);
                if (a == null || a.Go == null) continue;
                var want = PlaybackEvaluator.FielderAt(play, pos, t0);
                var lp = a.Go.transform.localPosition;
                _loco[pos] = new FielderMotion
                {
                    Ladder = -1,
                    Entry = new Vector2(lp.x - (float)want.Pos.X, lp.z - (float)want.Pos.Y),
                };
            }

            BuildFieldCameraPlan(play);
            BuildFieldStationPlan(play, t0);   // ⚠ プランを組んだ後（据え位置の判定が視野プランを引く）
            _fieldT = t0;
            SetFieldTime(t0);
        }

        /// <summary>打球プレーの再生をやめ、対決ビューの構図へ戻す。</summary>
        public void EndFieldPlay()
        {
            _fieldPlay = null;
            _fieldPlan = null;
            _fieldStationCuts.Clear();
            _loco.Clear();
            // 走行クリップのまま伏せると、次の打席の頭で走っている投手が出る
            Restore(_pitcher, _pitcherSnap);
            Restore(_catcher, _catcherSnap);
            foreach (var kv in _fieldCast)
            {
                var idle = IdleClipFor(kv.Key);
                if (idle == null || kv.Value == null) continue;
                SetActorClip(kv.Value, idle, 0f, 0f);
                kv.Value.IdleClip = idle;
                kv.Value.Speed = 1f;
            }
            PlaceFieldCastAtDefault();
            SetFieldCastVisible(false);
            HideBall();
            ClearPreviewFrame();
            ApplyCalibration(_cal);   // ⚠ 画角だけでなくカメラの位置・向きも戻す（ClearPreviewFrame は画角のみ）
        }

        private static DuelActorSnapshot Snapshot(Actor a)
        {
            if (a == null || a.Go == null) return default;
            return new DuelActorSnapshot
            {
                Valid = true,
                Clip = a.Clip, IdleClip = a.IdleClip, Time = a.Time, Speed = a.Speed,
                EndAt = a.EndAt, DefaultEndAt = a.DefaultEndAt, IdleEndAt = a.IdleEndAt,
                GroundY = a.GroundY, HoldAtEnd = a.HoldAtEnd, Holding = a.Holding,
                BasePos = a.Go.transform.localPosition, BaseRot = a.Go.transform.localRotation,
            };
        }

        private void Restore(Actor a, DuelActorSnapshot s)
        {
            if (a == null || a.Go == null || !s.Valid) return;
            a.Go.transform.localPosition = s.BasePos;
            a.Go.transform.localRotation = s.BaseRot;
            a.BasePos = s.BasePos;
            a.BaseRot = s.BaseRot;
            a.GroundY = s.GroundY;
            a.IdleClip = s.IdleClip;
            a.IdleEndAt = s.IdleEndAt;
            a.DefaultEndAt = s.DefaultEndAt;
            if (s.Clip != null) SetActorClip(a, s.Clip, s.Time, s.EndAt);
            a.Speed = s.Speed;          // ⚠ SetActorClip の後（差し替えで戻らない）
            a.HoldAtEnd = s.HoldAtEnd;
            a.Holding = s.Holding;
            GroundFix(a);
        }

        /// <summary>
        /// 時刻 t のフレームを作る。**ボール・カメラはフルレート**（ポーズだけが12fps量子化＝design-18 §4.5-②）。
        /// 位置は engine の評価値を写すだけで、Unity 側で軌道を積分し直さない（不変条件#1）。
        /// </summary>
        public void SetFieldTime(float t)
        {
            if (_fieldPlay == null) return;
            float dt = t - _fieldT;
            _fieldT = t;

            // 守備9人（投手・捕手を含む）。位置は engine の真値、足と向きは Unity が合わせる
            foreach (var pos in AllFieldPositions)
                StepFielderMotion(pos, FielderActor(pos), t, dt);

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
            sb.AppendLine("pos\tengineX\tengineZ\tactualX\tactualZ\tdiffM\tv_mps\tspeedMul\tpx\tclip\tyaw");
            foreach (var pos in AllFieldPositions)
            {
                var a = FielderActor(pos);
                // 再生中は「その時刻に engine が言う位置」、それ以外は定位置と比べる
                PlaybackVec want;
                float v = 0f;
                if (_fieldPlay != null)
                {
                    want = PlaybackEvaluator.FielderAt(_fieldPlay, pos, _fieldT).Pos;
                    float ta = Mathf.Max(0f, _fieldT - LocoSampleDt), tb = _fieldT + LocoSampleDt;
                    var pa = PlaybackEvaluator.FielderAt(_fieldPlay, pos, ta).Pos;
                    var pb = PlaybackEvaluator.FielderAt(_fieldPlay, pos, tb).Pos;
                    v = new Vector2((float)(pb.X - pa.X), (float)(pb.Y - pa.Y)).magnitude
                        / Mathf.Max(1e-4f, tb - ta);
                }
                else if (!PlaybackEvaluator.DefaultPositions.TryGetValue(pos, out want)) continue;

                if (a == null || a.Go == null)
                {
                    sb.AppendLine(pos + "\t" + want.X.ToString("F2") + "\t" + want.Y.ToString("F2") + "\t(未生成)");
                    continue;
                }
                var lp = a.Go.transform.localPosition;
                float dx = lp.x - (float)want.X, dz = lp.z - (float)want.Y;
                sb.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0}\t{1:F2}\t{2:F2}\t{3:F2}\t{4:F2}\t{5:F3}\t{6:F2}\t{7:F2}\t{8}\t{9}\t{10:F1}",
                    pos, want.X, want.Y, lp.x, lp.z, Mathf.Sqrt(dx * dx + dz * dz), v, a.Speed,
                    ScreenHeightPx(a),
                    a.Clip != null ? a.Clip.name.Replace("AA_Baseball_Defender_Movement_", "") : "(null)",
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
            var aim = Vector3.zero;
            if (_fieldPlan != null && _fieldPlan.Count > 0) ComputeFieldFrame(t, out aim, out frameH);

            // **注視点にいる人**の大きさ＝この構図の読みやすさそのもの。
            // 特定の役（中堅手など）で測ると、その役がプレーに絡まない時に意味のない数になる。
            int aimPx = frameH > 0.01f && Target != null
                ? Mathf.RoundToInt(TargetHeight / frameH * Target.height) : -1;

            // 球が枠の中か（0〜1で画面内）。**寄るほど「枠の外に置き去り」が起きる**が、
            // 絵では「球が写っていない」としか見えず、追えていないのか小さすぎるのかを切り分けられない。
            float bu = -1f, bv = -1f;
            if (_camera != null && _ball != null && _ball.activeSelf && Target != null)
            {
                var sp = _camera.WorldToScreenPoint(_ball.transform.position);
                if (sp.z > 0f) { bu = sp.x / Target.width; bv = sp.y / Target.height; }
            }

            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0}\t{1:F1}\t{2:F1}\t{3:F2}\t{4}\t{5}\t{6}\t{7:F1}\t{8:F1}\t{9}\t{10}\t{11:F2}\t{12:F2}\t{13:F0}",
                _fieldPlay != null ? _fieldPlay.Name : "(none)", FieldMaxZoom, FieldMarginM, t,
                ball.HasValue ? ball.Value.X.ToString("F1") : "-",
                ball.HasValue ? ball.Value.Y.ToString("F1") : "-",
                ball.HasValue ? ball.Value.H.ToString("F1") : "-",
                frameH, aim.y, aimPx, BallScreenPx(), bu, bv, FieldStationAt(t).y);
        }

        /// <summary>
        /// 【撮影ハーネス用】据え位置の切り替え計画を1行で返す（例 <c>"0.00:内野 3.05:外野"</c>）。
        ///
        /// **カットは1コマの絵では見えない**（切り替わった後の絵しか残らない）ので、
        /// 「何回・いつ切れるか」は必ずこの実測で採る。要らないカットが増えていないかの唯一の窓。
        /// </summary>
        public string FieldStationPlanForCapture()
        {
            if (_fieldStationCuts.Count == 0) return "(none)";
            var sb = new StringBuilder();
            for (int i = 0; i < _fieldStationCuts.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(_fieldStationCuts[i].T.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append(_fieldStationCuts[i].Outfield ? ":外野" : ":内野");
            }
            return sb.ToString();
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
