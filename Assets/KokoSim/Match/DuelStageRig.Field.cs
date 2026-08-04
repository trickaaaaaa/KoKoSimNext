using System.Collections.Generic;
using System.Text;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Timeline;
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
                var home = FieldHomeSpot(pos);
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

            EnsureRunnerCast(bodyPrefab, useVendor, layer);
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

        /// <summary>
        /// 守備の定位置（<see cref="FieldGeometry.StandardPosition"/>）。再生中の <c>Starts</c> と同じ表で、
        /// **判定が距離と時間を出しているのがこれ**。<c>PlaybackEvaluator.DefaultPositions</c> は
        /// HTMLモックの DEF 配列の写経で最大10m（中堅）ずれる＝実試合の配置に使わない。
        /// </summary>
        private static PlaybackVec FieldHomeSpot(FieldPosition pos)
        {
            var (x, z) = FieldGeometry.StandardPosition(pos);
            return new PlaybackVec(x, z);
        }

        /// <summary>守備7人を定位置へ戻す。</summary>
        public void PlaceFieldCastAtDefault()
        {
            foreach (var kv in _fieldCast)
            {
                var home = FieldHomeSpot(kv.Key);
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

        /// <summary>投手板まわりの平坦部の半径[m]。マウンドの断面はここまで水平。</summary>
        private const float MoundFlatM = 0.9f;
        /// <summary>マウンドが地面に接する半径[m]。18フィート＝**直径**なので半径は2.74m。</summary>
        private const float MoundBaseM = 2.74f;

        /// <summary>
        /// その地点の接地高[m]。マウンドの上だけ <see cref="MoundHeight"/> で、縁で地面へ落ちる。
        ///
        /// 投手の <c>GroundY</c> は生成時にマウンド高で焼かれている。動かすようになると
        /// **マウンドを降りても 25cm 浮いたまま**になる（枠10mの構図で14px＝はっきり見える）。
        /// 位置で決まる量なので毎コマ引き直す＝「特定のコマでだけ」の分岐にしない（design-18 §4.5-⑩）。
        ///
        /// ⚠ **この式は球場メッシュのマウンドの断面と同じ形でなければならない**
        /// （<c>tools/stadium-gen.py</c> の <c>mound_y()</c>）。別の形を持つと投手が斜面に浮くか
        /// めり込む。旧値（平坦部2.0m・接地2.7m）は残り0.7mで25cm落ちる＝20°の崖で、
        /// 25cm しかない起伏が「もっこりした皿」に見えていた（2026-08-01 監督指摘）。
        /// </summary>
        private static float FieldGroundY(float x, float z)
        {
            float r = Mathf.Sqrt(x * x + (z - MoundDistance) * (z - MoundDistance));
            return MoundHeight * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(MoundFlatM, MoundBaseM, r)));
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

            /// <summary>捕球クリップを流し終える時刻（プレーの時計）。この間は走りの段が触らない。</summary>
            public float CatchUntil;

            /// <summary>いま結び付いている捕球予定の添字（-1=無し）。グラブIKの狙点を引くのに使う。</summary>
            public int CatchEvent;
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

            // 捕球クリップを流している間は、走りの段も向きも触らない（クリップの芝居を奪わない）。
            // 位置だけは engine の真値へ置き続ける＝捕球点と体がずれない
            if (t < st.CatchUntil)
            {
                PlaceFielder(a, (float)cur.Pos.X, (float)cur.Pos.Y, st.Yaw);
                _loco[pos] = st;
                return;
            }
            if (st.CatchEvent >= 0 && t >= st.CatchUntil)
            {
                st.CatchEvent = -1;
                st.Ladder = -1;   // 走り／待機の管理へ戻す
            }

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

        // ---- 捕る（F5） ---------------------------------------------------------
        //
        // 捕手で動いている三点セット（アンカー表＋開始の逆算＋短時間IK）をそのまま野手へ広げる。
        // 新しい発明はしない。違うのは**選択の軸**だけで、捕手はコース（着弾点）、野手は
        // 球の高さと近づき方で選ぶ。

        /// <summary>近づき方。クリップの収録がこの6通りに分かれている。</summary>
        private enum CatchApproach { Still, Forward, Back, Left, Right }

        /// <summary>
        /// 野手の捕球クリップ1本ぶんのアンカー（実測 2026-08-01・0.02s刻み・
        /// <c>DuelMotionSpike.RunFielderCatchProbe</c>／<c>out/duel-stage/fieldcatch.tsv</c>）。
        ///
        /// <see cref="Moment"/> は「構えからグラブが最も動いた瞬間」＝運び終わり／伸び切り。
        /// ⚠ **腰→グラブの距離では測れない**。腕を下ろした構えも前へ伸ばした捕球姿勢も同じ 0.56m で、
        /// 方向が変わるだけで距離が変わらない（初回の実測で <c>Catching_TypeF</c> を
        /// 「最初から伸び切っている」と誤判定した）。構えからの**移動量**で採る。
        ///
        /// <see cref="GloveY"/> は捕球の瞬間のグラブの**足元からの高さ**[m]。立ち位置にも yaw にも
        /// 依らないので、球の高さと直接突き合わせられる＝選択の主軸になる。
        /// </summary>
        private readonly struct FieldCatchAnchor
        {
            public readonly string Clip;
            public readonly float Moment;
            public readonly float GloveY;
            /// <summary>
            /// 捕球の瞬間のグラブの**水平位置**（**アクターのローカル**・右/前[m]）。
            /// 体をどちらへ向ければグラブが球の来る方を向くかを出すのに使う。
            ///
            /// ⚠ 軸は肩の線から作る解剖学的な前後左右**ではなく**、立ち位置と yaw を与える
            /// アクターのローカル座標で採る。収録は捕球までに体を回しており、解剖学的な軸で採った表で
            /// yaw を決めると回した角度ぶんグラブが球からずれる（2026-08-01 実測: 残差 0.026m → 0.165m）。
            /// **絵ではカメラの狙い外しと区別できない**ので実測でしか気づけない。
            /// </summary>
            public readonly float GloveX, GloveZ;
            public readonly CatchApproach Approach;

            public FieldCatchAnchor(string clip, float moment, float gloveY,
                float gloveX, float gloveZ, CatchApproach approach)
            {
                Clip = clip; Moment = moment; GloveY = gloveY;
                GloveX = gloveX; GloveZ = gloveZ; Approach = approach;
            }
        }

        /// <summary>
        /// 捕球クリップ表。<c>gy</c> の実測がそのまま高さの帯になっている
        /// （低め 0.20〜0.30 ／ 胸 1.07〜1.26 ／ 頭上 1.50〜1.74）。
        /// </summary>
        private static readonly FieldCatchAnchor[] FieldCatchAnchors =
        {
            // その場で捕る（低め→頭上まで帯を埋める。捕手と同じで**高さの主軸**はここ）
            new("AA_Baseball_Defender_Catching_TypeF", 0.64f, 0.227f, -0.104f, 0.207f, CatchApproach.Still),
            new("AA_Baseball_Defender_1st_baseman_Catching", 0.40f, 1.070f, -0.119f, 0.756f, CatchApproach.Still),
            new("AA_Baseball_Defender_3rd_baseman_Catching", 0.46f, 1.088f, -0.691f, 0.336f, CatchApproach.Still),
            new("AA_Baseball_Defender_Catching_TypeR", 0.42f, 1.235f, -0.010f, 0.766f, CatchApproach.Still),
            new("AA_Baseball_Defender_Catching_TypeL", 0.48f, 1.262f, -0.666f, 0.416f, CatchApproach.Still),
            new("AA_Baseball_Defender_Catching_Jumping_Catch", 1.08f, 1.665f, -0.064f, 0.203f, CatchApproach.Still),
            new("AA_Baseball_Defender_Catching_TypeU", 0.98f, 1.753f, -0.209f, 0.036f, CatchApproach.Still),
            // 前へ走り込む
            new("AA_Baseball_Defender_Movement_Run_To_Catching_Type02", 0.76f, 0.253f, -0.020f, 0.110f, CatchApproach.Forward),
            new("AA_Baseball_Defender_Movement_Run_To_Catching_Type01", 0.72f, 1.548f, -0.213f, 0.102f, CatchApproach.Forward),
            // 後ろへ下がる（低い球で下がる収録は無いので1本）
            new("AA_Baseball_Defender_Movement_Run_Back_To_Catching", 1.00f, 1.479f, -0.084f, 0.302f, CatchApproach.Back),
            // 左右
            new("AA_Baseball_Defender_Movement_Shuffle_L_Catching", 0.34f, 0.249f, -0.408f, 0.170f, CatchApproach.Left),
            new("AA_Baseball_Defender_Movement_Run_L90_To_Catching_Type01", 0.70f, 1.594f, -0.213f, 0.231f, CatchApproach.Left),
            new("AA_Baseball_Defender_Movement_Shuffle_R_Catching", 0.84f, 0.194f, -0.482f, 0.158f, CatchApproach.Right),
            new("AA_Baseball_Defender_Movement_Run_R90_To_Catching_Type01", 0.84f, 1.483f, -0.394f, 0.277f, CatchApproach.Right),
        };

        /// <summary>
        /// 近づき方を切り替える速さ[m/s]。これ未満は「その場」。
        /// <see cref="FielderIdleSpeedMps"/> と別に持つのは、走りの段の切替と捕球クリップの選択が
        /// **同じ閾値である必要はない**ため（歩き程度で近づくならその場の捕球で足りる）。
        /// </summary>
        private const float CatchApproachSpeedMps = 1.2f;

        // 捕手の <c>CatchSelectionBias</c>（帯の境目でクリップが上下に飛ぶのを止める下駄）に当たるものは
        // ここには要らない。捕手は毎球コースから選び直すが、野手の選択は**再生前に1回だけ**なので
        // 揺れようがない。予定表にした副作用として、この種のチャタリングが構造的に消えている。

        /// <summary>捕った形を見せる据え置き[s]（打ち切りには必ず据え置きを添える＝design-18 §4.5-⑭）。</summary>
        private const float CatchHoldSeconds = 0.35f;

        /// <summary>
        /// 捕球点を収録のグラブ位置より何割ぶん内側へ置くか（腕に残す余裕）。
        ///
        /// 収録のグラブ位置は**その姿勢での**伸び切りなので、そこを狙点にすると腕に余裕が無い。
        /// ポーズは12fpsなので実際に描かれるコマは狙いの瞬間から最大83ms離れており（グラブは
        /// その間に 0.2〜0.38m 動く）、伸び切った狙点へは**届かないまま**になる
        /// （実測: 余裕なしで残差 0.074〜0.113m）。<c>DuelCatcherPose.ReachGuard</c>=0.98 が
        /// 肘のロック防止に余裕を残しているのと同じ理由で、こちらはコマの位相ぶんを吸収する。
        /// </summary>
        private const float CatchReachSlack = 0.85f;

        /// <summary>
        /// 捕球クリップを選ぶ**純関数**（乱数を引かない＝同じプレーは常に同じ絵）。
        /// 主軸は球の高さ、副軸は近づき方。残差はグラブIKが詰めるので、ここは「系統が合っていれば良い」。
        /// </summary>
        private static int SelectFieldCatch(float ballHeightM, CatchApproach approach)
        {
            int best = -1;
            float bestErr = float.MaxValue;
            for (int i = 0; i < FieldCatchAnchors.Length; i++)
            {
                var c = FieldCatchAnchors[i];
                // 近づき方が合わないものは高さが近くても選ばない（合う収録が無ければ後で「その場」へ落とす）
                if (c.Approach != approach) continue;
                float err = Mathf.Abs(c.GloveY - ballHeightM);
                if (err < bestErr) { bestErr = err; best = i; }
            }
            if (best >= 0) return best;

            // その近づき方の収録が無い（＝後ろへ下がる低い球など）。その場の系統から高さで選ぶ
            for (int i = 0; i < FieldCatchAnchors.Length; i++)
            {
                if (FieldCatchAnchors[i].Approach != CatchApproach.Still) continue;
                float err = Mathf.Abs(FieldCatchAnchors[i].GloveY - ballHeightM);
                if (err < bestErr) { bestErr = err; best = i; }
            }
            return best;
        }

        /// <summary>
        /// 捕球の予定。**再生前に全部組む**（毎コマ「いま捕るか」を判定しない）＝
        /// どの時刻から再生を始めても同じ絵になり、12fpsの位相で漏れることもない（design-18 §4.5-⑩）。
        /// </summary>
        private struct FieldCatchEvent
        {
            public FieldPosition Pos;
            public float TCatch;      // 球が着く時刻（プレーの時計）
            public float TStart;      // クリップを始める時刻＝TCatch − Moment（負なら頭を捨てて始める）
            public int Anchor;
            public Vector3 Point;     // 捕球点（**予定時に1回だけ確定**。毎コマ球を追わない）
            public float HoldUntil;   // 捕球点に球を留めておく終わり（次のセグメントが始まるまで）
            public float FielderGapM; // 捕球の瞬間の「野手の立ち位置 → 球の終点」の水平距離（実測用）
            public float PulledM;     // 捕球点を engine の終点から**軌跡に沿って**遡らせた量[m]
            public bool Fired;
        }

        private readonly List<FieldCatchEvent> _catchEvents = new();

        /// <summary>捕球点と実際のグラブの差の最悪値（受け入れの実測）。</summary>
        private float _catchGloveErrMax;
        private float _catchWristErrMax, _catchMittErrMax;
        private int _catchCount;

        /// <summary>
        /// 捕球後・保持中（IKの重みが落ちた後の素の吸収動作も含む）のグラブ→捕球点の差の最悪値。
        /// 球はこの間ミット中心をそのまま描く（<see cref="CatchGloveNow"/>）ので、これが大きいほど
        /// 「球が野手から離れて見える」量そのもの（issue #399 ①）。
        /// </summary>
        private float _catchHoldDriftMax;
        private int _catchHoldCount;

        /// <summary>
        /// このプレーの捕球を全部洗い出して予定表にする。
        ///
        /// 打球（滞空・転がり・バウンド）は engine に「誰が捕るか」が無いので**終点に一番近い野手**を
        /// 捕り手とみなす。打球の終点は必ず誰かが処理する点なので、この推定は打球については素直に当たる。
        /// **送球の受け手だけは推定しない**（<c>ToRole</c>＝seam E1）。併殺のように二塁の周りへ
        /// 遊撃手・二塁手・走者が同時に集まる場面では最近傍が取り違え、「受けていない野手が捕る」
        /// という形で絵に出るのに、原因は絵からは切り分けられない。
        /// </summary>
        private void BuildFieldCatchPlan(PlaybackPlay play)
        {
            _catchEvents.Clear();
            _catchGloveErrMax = _catchWristErrMax = _catchMittErrMax = 0f;
            _catchCount = 0;
            _catchHoldDriftMax = 0f;
            _catchHoldCount = 0;
            if (play == null || play.Ball == null) return;

            foreach (var seg in play.Ball)
            {
                // engine 実出力（EndpointBallSegment＋Kind）と、2D俯瞰の忠実移植モデル
                // （ArcSegment/RollSegment/ThrowSegment の固定4型）の両方を拾う（#258 と同じ二重対応）
                var eb = seg as EndpointBallSegment;
                bool isThrow;
                if (eb != null)
                {
                    isThrow = eb.Kind == BallSegmentKind.Throw;
                    if (!isThrow && eb.Kind != BallSegmentKind.Flight && eb.Kind != BallSegmentKind.Roll
                        && eb.Kind != BallSegmentKind.Bounce) continue;
                }
                else
                {
                    isThrow = seg is ThrowSegment;
                    if (!isThrow && !(seg is ArcSegment) && !(seg is RollSegment)) continue;
                }

                float tc = (float)seg.T1;
                var end = seg.At(seg.T1 - seg.T0);

                // 受け手。送球は engine が知っている（E1）＝推定しない。無ければ従来どおり最近傍へ落とす
                FieldPosition who;
                float bestD;
                if (isThrow && seg.ToRole.HasValue)
                {
                    who = seg.ToRole.Value;
                    var p = PlaybackEvaluator.FielderAt(play, who, tc).Pos;
                    bestD = new Vector2((float)(p.X - end.X), (float)(p.Y - end.Y)).magnitude;
                }
                else
                {
                    who = FieldPosition.Pitcher;
                    bestD = float.MaxValue;
                    foreach (var pos in AllFieldPositions)
                    {
                        var p = PlaybackEvaluator.FielderAt(play, pos, tc).Pos;
                        float d = new Vector2((float)(p.X - end.X), (float)(p.Y - end.Y)).magnitude;
                        if (d < bestD) { bestD = d; who = pos; }
                    }
                    // 終点に誰も居なければ（フェンス際・本塁打）捕球ではない。
                    // ⚠ 役が判っている送球はこの足切りをしない＝engine が「この人が受ける」と
                    //    言っている以上、離れていても受けに行くのが正しい
                    if (bestD > CatchOwnerRadiusM) continue;
                }

                // 近づき方は**クリップが始まる少し前**の速度で決める（捕る瞬間は easeIO で0に落ちている）
                var app = ApproachAt(play, who, tc - 0.4f, end);
                float h = isThrow ? ThrowCatchHeightM
                    : CatchHeightFor(eb != null ? eb.Kind
                        : (seg is RollSegment ? BallSegmentKind.Roll : BallSegmentKind.Flight));
                int anchor = SelectFieldCatch(h, app);
                if (anchor < 0) continue;

                // 次のセグメントが始まるまでは球を捕球点に留める（engine の保持位置は高さ0のため）
                float holdUntil = (float)play.Dur;
                foreach (var nxt in play.Ball)
                    if (nxt.T0 >= seg.T1 - 1e-4 && nxt.T0 < holdUntil) holdUntil = (float)nxt.T0;

                // 捕球点を**収録のグラブが居る距離**へ合わせる。engine の終点は「球が止まる点」で
                // あって「捕る点」ではなく、実測では**両側に**破綻していた:
                //   ・打球 … 野手と終点が 0.51m 離れる（収録のグラブは 0.11m）。腕は肩から0.55mなので
                //             IK が伸び切っても届かず「グラブの横で球が止まる」
                //   ・送球 … 終点が受け手の**真上**（距離 0.00m。収録のグラブは 0.77m 前）。
                //             グラブを胸の中へ畳むことになる
                // どちらも「グラブの居る距離で捕る」で解ける。嘘の大きさは両側とも
                // |野手との距離 − グラブの距離| に収まる（打点を投手側へ出す既存の作法と同型）。
                var an = FieldCatchAnchors[anchor];
                float reach = new Vector2(an.GloveX, an.GloveZ).magnitude * CatchReachSlack;
                var fp = PlaybackEvaluator.FielderAt(play, who, tc).Pos;
                // 高さは**選んだ収録のグラブの高さそのもの**にする。`h` は「その種別ならこのくらいの高さ」
                // という**選ぶための**値で、選び終わった後まで使うと収録との差だけ IK に無理をさせる
                // （実測: 滞空 h=1.40 に対し Catching_TypeL のグラブは 1.262 ＝ 0.138 差。収録の腕は
                // 既に伸び切っているので上げようがなく、残差 0.143m がまるごとこれだった。
                // **絵ではカメラの狙い外しと区別できない**＝内訳を測って初めて判る）。
                var pt = new Vector3((float)end.X, an.GloveY, (float)end.Y);
                float pulled = 0f;
                var gap = new Vector2((float)(end.X - fp.X), (float)(end.Y - fp.Y));
                if (reach > 0.01f && gap.magnitude > reach)
                {
                    // 遠い＝届かない。野手へ真っ直ぐ寄せる
                    var g2 = new Vector2((float)fp.X, (float)fp.Y) + gap.normalized * reach;
                    pulled = (new Vector2(pt.x, pt.z) - g2).magnitude;
                    pt = new Vector3(g2.x, an.GloveY, g2.y);
                }
                else if (reach > 0.01f)
                {
                    // 近すぎ＝畳むことになる。**球が来た向きへ遡って**離す＝球は自分の線から
                    // 外れない（横へ飛ばない）。XYは全種別とも直線なので二次方程式で厳密に出る
                    var back = seg.At(System.Math.Max(0.0, (seg.T1 - seg.T0) - 0.1));
                    var dir = new Vector2((float)(end.X - back.X), (float)(end.Y - back.Y));
                    if (dir.sqrMagnitude > 1e-8f)
                    {
                        var d = dir.normalized;
                        float aDot = Vector2.Dot(gap, d);
                        // gap < reach なので判別式は必ず正・大きいほうの根が「遡る量」
                        float s = aDot + Mathf.Sqrt(Mathf.Max(0f,
                            aDot * aDot - (gap.sqrMagnitude - reach * reach)));
                        if (s > 0f)
                        {
                            var g2 = new Vector2((float)end.X, (float)end.Y) - d * s;
                            pulled = s;
                            pt = new Vector3(g2.x, an.GloveY, g2.y);
                        }
                    }
                }

                _catchEvents.Add(new FieldCatchEvent
                {
                    Pos = who,
                    TCatch = tc,
                    TStart = tc - FieldCatchAnchors[anchor].Moment,
                    Anchor = anchor,
                    Point = pt,
                    HoldUntil = holdUntil,
                    FielderGapM = bestD,
                    PulledM = pulled,
                });
            }
        }

        /// <summary>終点からこれ以上離れていたら「誰も捕っていない」[m]。</summary>
        private const float CatchOwnerRadiusM = 2.5f;

        /// <summary>
        /// 捕球の高さ[m]。**engine のタイムラインは「その球がどの高さで捕られたか」を持たない**。
        ///
        /// 実測（2026-08-01）:
        ///   実出力（<c>EndpointBallSegment</c>）の滞空は <c>apex·4u(1−u)</c> ＝ **端点で必ず 0** ＝
        ///     フライを地面で捕ることになる
        ///   モック（<c>PlaybackSamples</c>）の <c>ArcSegment</c> は authored な tEnd で切るので
        ///     逆に **3.02m** で終わる＝身長1.75mでは跳んでも届かない
        /// どちらも「捕球の高さ」ではない。2D俯瞰は高さを描かないので表に出ていなかった。
        ///
        /// 3Dでは**種別から決める**。ここが <b>球の描画位置・グラブIKの狙点・捕球クリップの選択</b> の
        /// 3つを出す唯一の値になる（別々に持つとずれる＝design-18 §4.5-⑨ と同じ掟）。
        /// 水平（XZ）は engine の真値のまま。動かすのは高さだけ。
        /// </summary>
        private static float CatchHeightFor(BallSegmentKind kind)
        {
            switch (kind)
            {
                case BallSegmentKind.Roll: return 0.25f;    // ゴロを捕る＝低い構え
                case BallSegmentKind.Bounce: return 0.85f;  // 弾んだ球＝腰
                default: return 1.40f;                      // 滞空を捕る＝胸から顔の高さ
            }
        }

        /// <summary>
        /// 送球を受ける高さ[m]。塁上の捕球なので胸の帯に置く＝一塁手・三塁手の収録
        /// （実測 gy=1.070/1.088）が選ばれる。engine の送球区間は端点で 1.7m だが、
        /// それは山なりの弦の高さであって「受ける高さ」ではない（打球と同じ事情）。
        /// </summary>
        private const float ThrowCatchHeightM = 1.15f;

        /// <summary>捕球点へ球を寄せ始める時間[s]（捕球の手前）。</summary>
        private const float CatchBallBlendSeconds = 0.30f;

        /// <summary>時刻 t での近づき方（球へ向かって走っているか・横か・後ろか）。</summary>
        private static CatchApproach ApproachAt(PlaybackPlay play, FieldPosition pos, float t, PlaybackBall end)
        {
            float ta = Mathf.Max(0f, t - LocoSampleDt), tb = t + LocoSampleDt;
            var pa = PlaybackEvaluator.FielderAt(play, pos, ta).Pos;
            var pb = PlaybackEvaluator.FielderAt(play, pos, tb).Pos;
            var v = new Vector2((float)(pb.X - pa.X), (float)(pb.Y - pa.Y)) / Mathf.Max(1e-4f, tb - ta);
            if (v.magnitude < CatchApproachSpeedMps) return CatchApproach.Still;

            var here = PlaybackEvaluator.FielderAt(play, pos, t).Pos;
            var toBall = new Vector2((float)(end.X - here.X), (float)(end.Y - here.Y));
            if (toBall.sqrMagnitude < 1e-6f) return CatchApproach.Still;
            float ang = Vector2.SignedAngle(v.normalized, toBall.normalized);
            if (Mathf.Abs(ang) < 45f) return CatchApproach.Forward;
            if (Mathf.Abs(ang) > 135f) return CatchApproach.Back;
            // Vector2.SignedAngle は反時計回りが正。フィールドの (x,z) では正＝球が左手側
            return ang > 0f ? CatchApproach.Left : CatchApproach.Right;
        }

        /// <summary>
        /// 予定の時刻が来た捕球クリップを流す。**絶対時刻で判定する**（カウントダウンを持たない）＝
        /// 途中の時刻から再生を始めても同じ絵になる。頭出しは <c>t − TStart</c> ぶん。
        /// </summary>
        private void FireDueCatches(float t)
        {
            for (int i = 0; i < _catchEvents.Count; i++)
            {
                var e = _catchEvents[i];
                if (e.Fired || t < e.TStart) continue;
                var a = FielderActor(e.Pos);
                var clip = LoadClip(FieldCatchAnchors[e.Anchor].Clip);
                if (a != null && clip != null)
                {
                    // ⚠ 頭出しは Max(0,…) で潰さない。TStart が未来なら**まだ来ていない**ので
                    //    上の t < TStart で降りており、ここへ来る時点で t − TStart ≥ 0 が保証されている
                    SetActorClip(a, clip, t - e.TStart, 0f);
                    a.Speed = 1f;                       // 走りの倍率を持ち越さない
                    a.HoldSeconds = CatchHoldSeconds;   // 捕った形を見せてから待機へ戻る
                    FielderMotion st;
                    if (_loco.TryGetValue(e.Pos, out st))
                    {
                        st.Ladder = int.MinValue;       // 走りの段の管理から外す（クリップを奪わせない）
                        st.CatchUntil = e.TCatch + CatchHoldSeconds;
                        st.CatchEvent = i;
                        // **グラブが球の来る方を向くように体を回す**。走る向きを速度ベクトルへ
                        // 合わせたのと同じ考え方で、今度は合わせる対象がグラブになる。
                        // これが無いと、腕（肩から0.55m）では届かない所へ球があるまま IK が伸び切る
                        // （実測: 野手と球が 0.51m 離れた場面で残差 0.48m）。
                        // ⚠ しきい値は**向きが数値的に決まる下限**まで下げる。0.15 にしていたら
                        //    走り込み捕球（グラブが体の真下＝オフセット 0.112m）が丸ごと外れ、
                        //    狙点だけ「野手→球」の向きに置かれてグラブは走った向きのまま＝残差 0.094m。
                        //    引き寄せた狙点はグラブの居場所そのものなので、**向きを合わせて初めて一致する**
                        var an = FieldCatchAnchors[e.Anchor];
                        var gh = new Vector2(an.GloveX, an.GloveZ);
                        if (gh.magnitude > 0.05f)
                        {
                            var here = PlaybackEvaluator.FielderAt(_fieldPlay, e.Pos, e.TCatch).Pos;
                            var toBall = new Vector2(e.Point.x - (float)here.X, e.Point.z - (float)here.Y);
                            if (toBall.sqrMagnitude > 1e-6f)
                                st.Yaw = Mathf.Atan2(toBall.x, toBall.y) * Mathf.Rad2Deg
                                         - Mathf.Atan2(gh.x, gh.y) * Mathf.Rad2Deg;
                        }
                        _loco[e.Pos] = st;
                        PlaceFielder(a, (float)PlaybackEvaluator.FielderAt(_fieldPlay, e.Pos, t).Pos.X,
                            (float)PlaybackEvaluator.FielderAt(_fieldPlay, e.Pos, t).Pos.Y, st.Yaw);
                    }
                }
                e.Fired = true;
                _catchEvents[i] = e;
            }
        }

        /// <summary>
        /// 捕球の前後だけグラブを球へ寄せる（捕手の <c>FixCatcherPose</c> と同型）。
        ///
        /// ⚠ **ポーズを評価した直後だけ**当てる。重み&lt;1 は「今の手の位置からの補間」なので、
        /// 同じポーズに二度当てると寄りすぎる。12fps のコマが進んだ回だけ呼ぶこと。
        /// ⚠ 狙点は予定時に確定した1点で、毎コマ球を追わない（12fpsで球は1コマ3m進む）。
        /// </summary>
        private void FixFielderCatchPose(float t)
        {
            foreach (var kv in _loco)
            {
                var st = kv.Value;
                if (st.CatchEvent < 0 || st.CatchEvent >= _catchEvents.Count) continue;
                var e = _catchEvents[st.CatchEvent];
                var a = FielderActor(kv.Key);
                if (a == null || a.Animator == null) continue;

                float w = DuelCatcherPose.Weight(t, e.TCatch);
                if (w > 0f) DuelCatcherPose.ReachMitt(a.Animator, _root.transform.TransformPoint(e.Point), w);

                // ⚠ 測るのは**ミット中心**（手首→中指付け根の中点）。手ボーンの原点は手首なので、
                //    そこで測ると常にミットの厚みぶん外れた値が出る（捕手の受け入れと同じ単一ソース）
                // 計測は**捕球の直前のコマだけ**。捕球の後は重みが release で落ちていく途中なので、
                // その区間を混ぜると「IK が外れていく過程」を受け入れ値として拾ってしまう
                if (t <= e.TCatch + 1e-4f && t > e.TCatch - 1f / PoseFps)
                {
                    var target = _root.transform.TransformPoint(e.Point);
                    float d = Vector3.Distance(DuelCatcherPose.MittCenter(a.Animator), target);
                    _catchGloveErrMax = Mathf.Max(_catchGloveErrMax, d);
                    // 残差の内訳。**この2つを分けないと直しようがない**:
                    //   手首 … IK が狙いの手首位置へ届いたか（届かない＝腕の長さ/胴の助けの問題）
                    //   ミット … 手首→ミット中心のずれが IK 後に回ったか（＝1回では収束しない性質の問題）
                    var wrist = a.Animator.GetBoneTransform(HumanBodyBones.LeftHand);
                    if (wrist != null)
                    {
                        _catchWristErrMax = Mathf.Max(_catchWristErrMax,
                            Vector3.Distance(wrist.position, target - (DuelCatcherPose.MittCenter(a.Animator) - wrist.position)));
                        _catchMittErrMax = Mathf.Max(_catchMittErrMax,
                            (DuelCatcherPose.MittCenter(a.Animator) - wrist.position).magnitude);
                    }
                    _catchCount++;
                }
                // 捕球後・保持中（IKの重みが release で落ちきった後も含む）。ボールは今やこの
                // ミット中心をそのまま描く（design-18 §4.5-③・issue #399 ①）ので、ここが捕球点から
                // 離れるほど「素の吸収動作でグラブ＝球が離れて見える」量そのもの。捕球直前の実測
                // （0.025m級）とは別枠で最悪値を持つ＝「捕球の一瞬だけ緑」を防ぐ
                else if (t > e.TCatch + 1e-4f && t <= e.TCatch + CatchHoldSeconds)
                {
                    var target = _root.transform.TransformPoint(e.Point);
                    float d = Vector3.Distance(DuelCatcherPose.MittCenter(a.Animator), target);
                    _catchHoldDriftMax = Mathf.Max(_catchHoldDriftMax, d);
                    _catchHoldCount++;
                }
            }
        }

        /// <summary>
        /// 【撮影ハーネス用】捕球の受け入れ実測。グラブ（手ボーン）と捕球点の距離。
        /// 捕手の実績は 0.007〜0.039m。**絵ではカメラの狙い外しと区別できない**ので実測で採る。
        /// </summary>
        public string FieldCatchDiagForCapture()
        {
            var sb = new StringBuilder();
            sb.AppendLine("捕球予定=" + _catchEvents.Count + "件\tグラブ→球の最悪="
                + _catchGloveErrMax.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                + "m\t内訳 手首="
                + _catchWristErrMax.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                + "m ミット厚="
                + _catchMittErrMax.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                + "m\t計測コマ=" + _catchCount);
            sb.AppendLine("保持中（捕球後〜" + CatchHoldSeconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                + "s）のグラブ→捕球点の最悪="
                + _catchHoldDriftMax.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                + "m\t計測コマ=" + _catchHoldCount);
            sb.AppendLine("pos\ttCatch\ttStart\tcatchH\t野手との距離\t手前へ\tclip");
            foreach (var e in _catchEvents)
                sb.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0}\t{1:F2}\t{2:F2}\t{3:F2}\t{4:F2}\t{5:F2}\t{6}", e.Pos, e.TCatch, e.TStart,
                    e.Point.y, e.FielderGapM, e.PulledM,
                    FieldCatchAnchors[e.Anchor].Clip.Replace("AA_Baseball_Defender_", "")));
            return sb.ToString();
        }

        // ---- 投げる／受ける（F6） -----------------------------------------------
        //
        // 捕球（F5）と同じ三点セット（アンカー表＋開始の逆算＋予定表）だが、**アンカーの位置が違う**。
        // 捕球はクリップの終盤に球が着いて終わるのに対し、送球はクリップの途中で球が出ていき、
        // その後にフォロースルーが続く。engine が持つのは出ていく時刻（Throw の T0）だけなので、
        // クリップの頭は「T0 − リリースのアンカー」で逆算する。

        /// <summary>
        /// 送球クリップ1本ぶんのアンカー（実測 2026-08-01・0.02s刻み・
        /// <c>DuelMotionSpike.RunThrowProbe</c>／<c>out/duel-stage/throw.tsv</c>）。
        ///
        /// <see cref="Release"/> は**利き手の速さのピーク**＝投手の
        /// <c>DuelCalibration.BallReleaseClipTime</c>（2.97＝「手の速度ピーク実測」）と同じ定義。
        /// 定義を役ごとに変えると、同じ「投げる」なのに球の出所が役ごとにずれる。
        /// 実測では手が最も前へ出た時刻とも 1〜2 コマ（0.02〜0.04s）しか違わなかった。
        /// </summary>
        private readonly struct FieldThrowAnchor
        {
            public readonly string Clip;
            public readonly float Release;
            /// <summary>リリースの瞬間の手の**足元からの高さ**[m]（球が出ていく高さ）。</summary>
            public readonly float HandY;
            /// <summary>
            /// 同じ瞬間の手の**水平位置**（腰基準・右/前[m]）。体をどちらへ向ければ
            /// 手が的の方を向くかを出すのに使う（F5 のグラブと同型）。
            /// </summary>
            public readonly float HandX, HandZ;
            /// <summary>この収録を使う送球距離の上限[m]。</summary>
            public readonly float MaxDistM;

            public FieldThrowAnchor(string clip, float release, float handY,
                float handX, float handZ, float maxDistM)
            {
                Clip = clip; Release = release; HandY = handY;
                HandX = handX; HandZ = handZ; MaxDistM = maxDistM;
            }
        }

        /// <summary>
        /// 送球クリップ表（近い順）。実測の手の速さが**そのまま強度の段**になっている
        /// （トス 4.5 ／ 速い横手 12.9 ／ 本格的な上手 18.9 m/s）＝距離で選べば見た目の強さが合う。
        ///
        /// ⚠ **投げる向きを収録の手の速度から採ってはいけない**。実測ではリリース時の手の速度が
        /// 横69°・下55°（type01）を向いており、これは既に振り下ろしへ入っているため。
        /// 「フォロースルーの手首から道具の向きを採ってはいけない」（design-18 §4.5-⑩）と同じ罠で、
        /// 向きは幾何（野手→的）から出し、手からは**時刻と位置だけ**を採る。
        /// </summary>
        private static readonly FieldThrowAnchor[] FieldThrowAnchors =
        {
            new("AA_Baseball_Defender_Pass_the_ball_type02", 0.68f, 0.765f, 0.247f, 0.466f, 15f),
            new("AA_Baseball_Defender_Pass_the_ball_type01", 0.76f, 1.172f, 0.361f, 0.718f, 45f),
            new("AA_Baseball_Defender_Pass_the_ball_type03", 1.36f, 1.436f, -0.122f, 0.655f, float.MaxValue),
        };

        /// <summary>捕手の送球（盗塁の二塁送球）。しゃがみから立ちながら投げる専用収録。</summary>
        private static readonly FieldThrowAnchor CatcherThrowAnchor =
            new("AA_Baseball_Catcher_Throwing", 1.16f, 1.390f, 0.336f, 0.380f, float.MaxValue);

        /// <summary>フォロースルーを見せる長さ[s]（リリースから先）。</summary>
        private const float ThrowFollowSeconds = 0.55f;

        /// <summary>投げ終わりの据え置き[s]（打ち切りには必ず据え置きを添える＝design-18 §4.5-⑭）。</summary>
        private const float ThrowHoldSeconds = 0.30f;

        /// <summary>
        /// リリース後、球を「手の位置」から「engine の送球線」へ寄せ切るまで[s]。
        ///
        /// engine の Throw は始点が**野手の地面位置**・高さ 1.7m の山なりで、手とは一致しない。
        /// 差をその場で消すと球が瞬間移動するので、<c>BattedPointAt</c> の打点合わせと同じ
        /// オフセット線形フェードで 0 へ落とす。
        /// </summary>
        private const float ThrowHandoffSeconds = 0.15f;

        /// <summary>
        /// 送球の予定。捕球と同じく**再生前に全部組む**（毎コマ「いま投げるか」を判定しない）。
        /// </summary>
        private struct FieldThrowEvent
        {
            public FieldPosition From;
            public FieldPosition To;
            public float TRelease;         // 球が手を離れる時刻（＝engine の Throw の T0）
            public float TStart;           // クリップの頭＝TRelease − Release（**負でも潰さない**）
            public float TFire;            // 実際に流し始める時刻（捕るより前には投げられない）
            public FieldThrowAnchor Anchor;
            public float Yaw;              // 投げる体の向き[度]
            public float AimErrDeg;        // 体の向きと「野手→的」の方角の差[度]（実測用。0が正しい）
            public Vector3 SegStart;       // engine の送球区間の始点（stage-local）
            public Vector3 SegEnd;         // 同・終点（横ずれの実測に使う）
            public Vector3 HandAtRelease;  // 球が手を離れる点（幾何解。保持中は実測の手で上書き）
            public float DistM;
            public bool Fired;
        }

        private readonly List<FieldThrowEvent> _throwEvents = new();

        /// <summary>
        /// 送球クリップを選ぶ**純関数**（乱数を引かない）。主軸は距離＝実測の手の速さの段。
        /// 捕手だけは専用収録（しゃがみから立って投げる）を使う。
        /// </summary>
        private static FieldThrowAnchor SelectFieldThrow(FieldPosition pos, float distM)
        {
            if (pos == FieldPosition.Catcher) return CatcherThrowAnchor;
            for (int i = 0; i < FieldThrowAnchors.Length; i++)
                if (distM <= FieldThrowAnchors[i].MaxDistM) return FieldThrowAnchors[i];
            return FieldThrowAnchors[FieldThrowAnchors.Length - 1];
        }

        /// <summary>このプレーの送球を全部洗い出して予定表にする。</summary>
        private void BuildFieldThrowPlan(PlaybackPlay play)
        {
            _throwEvents.Clear();
            _throwWindupMin = float.MaxValue;
            _throwHeadMin = float.MaxValue;
            _throwLateralMax = 0f;
            _throwAimErrMax = 0f;
            if (play == null || play.Ball == null) return;

            foreach (var seg in play.Ball)
            {
                var eb = seg as EndpointBallSegment;
                bool isThrow = eb != null ? eb.Kind == BallSegmentKind.Throw : seg is ThrowSegment;
                if (!isThrow) continue;

                float tr = (float)seg.T0;
                var from = seg.At(0.0);
                var to = seg.At(seg.T1 - seg.T0);

                // 投げ手は engine が知っている（E1）。無ければ始点に一番近い野手へ落とす
                FieldPosition who = seg.FromRole ?? NearestFielder(play, tr, from.X, from.Y);
                float dist = new Vector2((float)(to.X - from.X), (float)(to.Y - from.Y)).magnitude;
                var an = SelectFieldThrow(who, dist);

                // 体の向き＝**的を向ける**。F5（グラブを球へ向ける）とは意味が違う: グラブは球の
                // 位置に居なければならないのに対し、送球は球が的へ飛んでいけばよく、手が的への
                // 直線上に居る必要はない。以前は「リリースの手が的を向く」よう体ごとオフセット
                // していたが（an.HandX/HandZ ぶん引く式）、収録ごとの手の水平位置（0.12〜0.36m）
                // がそのまま体の向きのずれ（実測10〜42°）になっていた（issue #399 ③）。
                // 手のオフセットで生じる横ずれは ThrowHandoffSeconds の寄せで吸収する（下）
                var fp = PlaybackEvaluator.FielderAt(play, who, tr).Pos;
                var toTarget = new Vector2((float)(to.X - fp.X), (float)(to.Y - fp.Y));
                float yaw = toTarget.sqrMagnitude > 1e-6f
                    ? Mathf.Atan2(toTarget.x, toTarget.y) * Mathf.Rad2Deg
                    : YawFacingHome((float)fp.X, (float)fp.Y);
                float aimErrDeg = toTarget.sqrMagnitude > 1e-6f
                    ? Mathf.Abs(Mathf.DeltaAngle(yaw, Mathf.Atan2(toTarget.x, toTarget.y) * Mathf.Rad2Deg))
                    : 0f;
                _throwAimErrMax = Mathf.Max(_throwAimErrMax, aimErrDeg);

                // 球が手を離れる点の**幾何解**（どの時刻から再生を始めても同じ値）。
                // 保持中は実測の手で上書きするが、途中から再生した場合はこれが使われる
                var off = Quaternion.Euler(0f, yaw, 0f) * new Vector3(an.HandX, 0f, an.HandZ);
                var hand = new Vector3((float)fp.X + off.x,
                    FieldGroundY((float)fp.X, (float)fp.Y) + an.HandY, (float)fp.Y + off.z);

                float tStart = tr - an.Release;
                // ⚠ 捕ってからでないと投げられない。ただし**アンカーの側は動かさない**＝
                //    頭出し量を増やして（クリップの頭を捨てて）リリースの時刻を守る。
                //    ここで tStart 自体を持ち上げると球が手を離れる時刻がずれる
                float tFire = Mathf.Max(tStart, CatchTimeOf(who, tr));

                _throwEvents.Add(new FieldThrowEvent
                {
                    From = who,
                    To = seg.ToRole ?? NearestFielder(play, (float)seg.T1, to.X, to.Y),
                    TRelease = tr, TStart = tStart, TFire = tFire, Anchor = an, Yaw = yaw,
                    AimErrDeg = aimErrDeg,
                    SegStart = new Vector3((float)from.X, (float)from.H, (float)from.Y),
                    SegEnd = new Vector3((float)to.X, (float)to.H, (float)to.Y),
                    HandAtRelease = hand, DistM = dist,
                });
            }
        }

        /// <summary>その点に一番近い野手（E1 が無いときのフォールバック）。</summary>
        private static FieldPosition NearestFielder(PlaybackPlay play, float t, double x, double y)
        {
            FieldPosition best = FieldPosition.Pitcher;
            float bestD = float.MaxValue;
            foreach (var pos in AllFieldPositions)
            {
                var p = PlaybackEvaluator.FielderAt(play, pos, t).Pos;
                float d = new Vector2((float)(p.X - x), (float)(p.Y - y)).magnitude;
                if (d < bestD) { bestD = d; best = pos; }
            }
            return best;
        }

        /// <summary>その野手が <paramref name="beforeT"/> より前に球を手にした時刻（無ければ 0）。</summary>
        private float CatchTimeOf(FieldPosition pos, float beforeT)
        {
            float t = 0f;
            foreach (var e in _catchEvents)
                if (e.Pos == pos && e.TCatch <= beforeT + 1e-4f && e.TCatch > t) t = e.TCatch;
            return t;
        }

        /// <summary>
        /// 予定の時刻が来た送球クリップを流す。捕球と同じく**絶対時刻で判定する**
        /// （カウントダウンを持たない＝途中の時刻から再生を始めても同じ絵になる）。
        /// </summary>
        private void FireDueThrows(float t)
        {
            for (int i = 0; i < _throwEvents.Count; i++)
            {
                var e = _throwEvents[i];
                if (e.Fired || t < e.TFire) continue;
                var a = FielderActor(e.From);
                var clip = LoadClip(e.Anchor.Clip);
                if (a != null && clip != null)
                {
                    // 頭出し。TFire ≥ TStart は上の予定で保証済み＝ここで Max(0,…) を挟まない
                    float head = t - e.TStart;
                    SetActorClip(a, clip, head, e.Anchor.Release + ThrowFollowSeconds);
                    a.Speed = 1f;                        // 走りの倍率を持ち越さない
                    a.HoldSeconds = ThrowHoldSeconds;    // 投げ終わりの形を見せてから待機へ戻る
                    FielderMotion st;
                    if (_loco.TryGetValue(e.From, out st))
                    {
                        st.Ladder = int.MinValue;        // 走りの段の管理から外す
                        st.CatchEvent = -1;              // グラブIKを切る（捕球の重みが振りかぶりへ食い込まない）
                        st.CatchUntil = e.TRelease + ThrowFollowSeconds + ThrowHoldSeconds;
                        st.Yaw = e.Yaw;
                        _loco[e.From] = st;
                        var p = PlaybackEvaluator.FielderAt(_fieldPlay, e.From, t).Pos;
                        PlaceFielder(a, (float)p.X, (float)p.Y, st.Yaw);
                    }
                    _throwHeadMin = Mathf.Min(_throwHeadMin, head);
                    _throwWindupMin = Mathf.Min(_throwWindupMin, e.TRelease - e.TFire);
                }
                e.Fired = true;
                _throwEvents[i] = e;
            }
        }

        /// <summary>
        /// 球の位置（stage-local）。engine の真値を基本に、**手に持っている間**と
        /// **手を離れた直後**だけ差し替える（design-15 の3区間）。
        ///   ① 手の中 … 捕ってから投げるまで＝投げ手の手ボーンの子と同じ位置
        ///   ② 寄せ  … リリース直後、手と engine の送球線の差を <see cref="ThrowHandoffSeconds"/> で 0 へ
        ///   ③ 受け  … 捕球点（F5）
        /// </summary>
        private Vector3 FieldBallPoint(Vector3 enginePos, float t)
        {
            for (int i = 0; i < _throwEvents.Count; i++)
            {
                var e = _throwEvents[i];
                if (t >= e.TFire && t < e.TRelease)
                {
                    // ⚠ 球は**描かれている手**に付ける（アンカー時刻の幾何解ではなく）。ポーズは12fpsで
                    //    手は 12.9m/s 出るので、1コマぶん（83ms）で 1m 動く＝幾何解に貼ると
                    //    リリースの瞬間に球が手から1m飛ぶ
                    var h = ThrowHandNow(e);
                    e.HandAtRelease = h;
                    _throwEvents[i] = e;
                    return h;
                }
                if (t >= e.TRelease && t < e.TRelease + ThrowHandoffSeconds)
                {
                    // 横ずれ＝球が手を離れた点の、送球線に対する直交成分。体の向きを的へ合わせて
                    // いるので 0 に近いはず（大きいと「投げた向きと球の行き先が折れて」見える）
                    var line = new Vector2(e.SegEnd.x - e.SegStart.x, e.SegEnd.z - e.SegStart.z);
                    var d = new Vector2(e.HandAtRelease.x - e.SegStart.x, e.HandAtRelease.z - e.SegStart.z);
                    if (line.sqrMagnitude > 1e-6f)
                    {
                        var n = line.normalized;
                        _throwLateralMax = Mathf.Max(_throwLateralMax, Mathf.Abs(d.x * n.y - d.y * n.x));
                    }
                    float u = Mathf.InverseLerp(e.TRelease, e.TRelease + ThrowHandoffSeconds, t);
                    float w = 1f - u * u * (3f - 2f * u);
                    return enginePos + (e.HandAtRelease - e.SegStart) * w;
                }
            }
            return BlendToCatchPoint(enginePos, t);
        }

        /// <summary>いま描かれている利き手の中の球の位置（stage-local）。</summary>
        private Vector3 ThrowHandNow(FieldThrowEvent e)
        {
            var a = FielderActor(e.From);
            if (a == null || a.Animator == null) return e.HandAtRelease;
            var hand = a.Animator.GetBoneTransform(HumanBodyBones.RightHand);
            if (hand == null) return e.HandAtRelease;
            // 手の中心＝手首→中指付け根の中点。手ボーンの原点は手首なので、そのままだと球が手首に刺さる
            // （design-18 §3.3.1・HoldBallInHand と同じ作法）
            var tip = a.Animator.GetBoneTransform(HumanBodyBones.RightMiddleProximal);
            var w = tip != null ? Vector3.Lerp(hand.position, tip.position, 0.5f) : hand.position;
            return _root.transform.InverseTransformPoint(w);
        }

        private float _throwWindupMin, _throwHeadMin, _throwLateralMax, _throwAimErrMax;

        /// <summary>
        /// 【撮影ハーネス用】送球の受け入れ実測。
        ///
        /// <c>頭出し</c> は「クリップの頭をどれだけ捨てたか」[s]。**負になってはいけない**
        /// （負＝球を持つ前に投げ終わっている＝design-18 §4.5-③ の実害と同型）。
        /// <c>見せる振り</c> は投げ始めてから球が出ていくまで[s]＝0 だとクリップが一瞬で飛ぶ。
        /// <c>横ずれ</c> は球が手を離れた点と engine の送球線の**横方向**の差[m]＝
        /// 「投げた向きと球の飛ぶ向きが折れて見える量」。体の向きを的へ合わせているので 0 に近いはず。
        /// <c>向きの差</c> は体の向きと「野手→的」の方角の差[度]（issue #399 ③）。体は的そのものを
        /// 向けているので 0 のはず＝横ずれと違い ThrowHandoffSeconds で吸収する対象ではない。
        /// **どれも絵では読めない**（どれも「野手が投げている」だけ）。
        /// </summary>
        public string FieldThrowDiagForCapture()
        {
            var sb = new StringBuilder();
            sb.AppendLine("送球予定=" + _throwEvents.Count + "件\t頭出しの最小="
                + (_throwHeadMin > 1e6f ? 0f : _throwHeadMin).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                + "s\t見せる振りの最小="
                + (_throwWindupMin > 1e6f ? 0f : _throwWindupMin).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                + "s\t横ずれの最悪="
                + _throwLateralMax.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                + "m\t向きの差の最悪="
                + _throwAimErrMax.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "°");
            sb.AppendLine("from\tto\ttRelease\ttStart\ttFire\tdistM\thandY\taimErrDeg\tclip");
            foreach (var e in _throwEvents)
                sb.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0}\t{1}\t{2:F2}\t{3:F2}\t{4:F2}\t{5:F1}\t{6:F2}\t{7:F1}\t{8}",
                    e.From, e.To, e.TRelease, e.TStart, e.TFire, e.DistM, e.HandAtRelease.y, e.AimErrDeg,
                    e.Anchor.Clip.Replace("AA_Baseball_Defender_", "").Replace("AA_Baseball_", "")));
            return sb.ToString();
        }

        // ---- 走者（F7） ---------------------------------------------------------
        //
        // 守備と同じ「位置は engine・芝居は Unity」。違いは2つだけ:
        //   ・走者は engine の <c>RunnerAt</c> が**線形補間**（野手の easeIO と違って速度が一定）
        //   ・塁に着いたら止まる＝到達の演技が要る（走り抜け／スライディング成功／失敗）

        /// <summary>同時に出せる走者の上限（三塁まで＋打者走者）。</summary>
        private const int MaxRunners = 4;

        /// <summary>
        /// 走者の走行クリップ。歩き・走りは攻撃側の収録、全力だけ守備側の収録を借りる。
        ///
        /// ⚠ 借りるのは**実測がそう言っている**から。engine のモックは本塁→一塁に 6.5〜9.1 m/s を
        /// 要求するが、攻撃側の走りループは 3.98 m/s しか無く、倍率が 1.64〜2.29 ＝ F4 で決めた
        /// 上限 1.45 を大きく超える（足が目に見えて空回りする）。全力ループ 5.11 m/s なら 1.28〜1.79 で、
        /// 少なくとも段の中では一番マシになる。素体もリグも同じで、全力疾走に攻守の区別は無い。
        /// </summary>
        private static readonly LocoClip[] RunnerLadder =
        {
            new LocoClip("AA_Baseball_Offense_Base_Walking", 1.14f, -1f),
            new LocoClip("AA_Baseball_Offense_Base_Running", 3.98f, -17f),
            new LocoClip("AA_Baseball_Defender_Movement_Sprint_Loop", 5.11f, -12.9f),
        };

        /// <summary>走者の待機（塁上でのリード）。</summary>
        private const string RunnerIdleClipName = "AA_Baseball_Offense_Crouch_Idle_Loop_Type01";

        /// <summary>
        /// 塁に着く演技1本ぶんのアンカー（実測 2026-08-01・<c>DuelMotionSpike.RunRunnerArriveProbe</c>／
        /// <c>out/duel-stage/runner.tsv</c>）。<see cref="Touch"/> は engine の走者レッグの終端（T1）へ合わせる瞬間。
        ///
        /// ⚠ **その瞬間の定義はクリップごとに違う**（捕球で2種・守備の走り込みで3種あったのと同じ）。
        ///   走り抜け・スライディング成功 … 地面の高さの手足が最も前へ出た時刻（＝塁を踏む/触る）
        ///   スライディング失敗           … 腰が最も低くなった時刻（＝滑り込みの接地）。
        ///     この収録は「触る」のが 2.06s と遅く、それはタッチされた後に塁へ手を伸ばし直す芝居で、
        ///     そこを T1 に合わせると滑り込みが1.5秒も早く終わってしまう
        /// ⚠ Type02 は採らない。前置きが長く（触るまで 1.34s・腰が沈むまで 4.30s）、走者レッグより
        ///   長い頭出しが要る場面が出る。Type01 は 0.58〜0.62s で収まる。
        /// </summary>
        private readonly struct RunnerArriveAnchor
        {
            public readonly string Clip;
            public readonly float Touch;
            /// <summary>その瞬間に塁へ触れている手足の**アクターローカル**水平位置（右/前[m]）。</summary>
            public readonly float TouchX, TouchZ;

            public RunnerArriveAnchor(string clip, float touch, float touchX, float touchZ)
            {
                Clip = clip; Touch = touch; TouchX = touchX; TouchZ = touchZ;
            }
        }

        private static readonly RunnerArriveAnchor ArriveRunThrough =
            new("AA_Baseball_Offense_Running_To_Base", 0.68f, 0.405f, 0.582f);
        private static readonly RunnerArriveAnchor ArriveSlideSafe =
            new("AA_Baseball_Offense_Base_Sliding_Type01_Succ", 0.62f, 0.602f, 0.447f);
        private static readonly RunnerArriveAnchor ArriveSlideOut =
            new("AA_Baseball_Offense_Base_Sliding_Type01_Fail", 0.58f, -0.003f, 0.530f);

        /// <summary>到達の演技を見せる長さ[s]（アンカーから先）。</summary>
        private const float ArriveFollowSeconds = 1.10f;

        /// <summary>到達後の据え置き[s]（打ち切りには必ず据え置きを添える＝design-18 §4.5-⑭）。</summary>
        private const float ArriveHoldSeconds = 0.40f;

        private readonly List<Actor> _runnerCast = new();

        private struct RunnerMotion
        {
            public int Ladder;
            public float Yaw;
            public bool Started;
            /// <summary>到達クリップを流し終える時刻（この間は走りの段も向きも触らない）。</summary>
            public float ArriveUntil;
            public bool ArriveFired;
        }

        private readonly List<RunnerMotion> _runnerMotion = new();
        private float _runnerMaxPosErr, _runnerMaxSpeed;
        private float _runnerSlipMax, _runnerSlipMin;
        private float _runnerHeadMin;

        /// <summary>走者プールを生成する（2回目以降は何もしない）。装備は帽子だけ＝グラブは持たせない。</summary>
        private void EnsureRunnerCast(GameObject bodyPrefab, bool useVendor, int layer)
        {
            if (_runnerCast.Count > 0 || bodyPrefab == null) return;
            var idle = LoadClip(RunnerIdleClipName);
            if (idle == null)
            {
                Debug.LogWarning("[DuelStage] 走者の待機クリップが無いので走者を組まない");
                return;
            }
            for (int i = 0; i < MaxRunners; i++)
            {
                var a = SpawnActor(bodyPrefab, idle, Vector3.zero, 0f,
                    _offenseColors, 0f, layer, useVendor, rootMotion: false, tightBounds: true);
                a.Go.name = "runner-" + i;
                a.IdleClip = idle;
                StepActor(a, 0.001f);
                GroundFix(a);
                if (!useVendor) AttachGear(a, GearKind.Cap, Vector3.zero, _offenseColors.Cap, layer);
                CollectTeamMats(a, _offenseColors.Cap);
                a.Go.SetActive(false);
                a.Enabled = false;
                _runnerCast.Add(a);
            }
        }

        /// <summary>そのプレーに出る走者だけ有効にする（出ない枠はポーズ評価ごと止める）。</summary>
        private void SetRunnerCastForPlay(PlaybackPlay play)
        {
            _runnerMotion.Clear();
            int n = play?.Runners?.Count ?? 0;
            for (int i = 0; i < _runnerCast.Count; i++)
            {
                bool on = i < n;
                var a = _runnerCast[i];
                if (a?.Go == null) continue;
                a.Go.SetActive(on);
                a.Enabled = on;
                if (on) { a.Speed = 1f; a.HoldSeconds = 0f; }
            }
            for (int i = 0; i < n; i++) _runnerMotion.Add(new RunnerMotion { Ladder = -1, Yaw = 0f });
        }

        /// <summary>
        /// 時刻 t の走者位置（stage-local の x,z）。engine の補間が単一ソース。
        ///
        /// ⚠ <c>HideAt</c> は効かせない。あれは2D俯瞰が「アウトになった丸トークンを盤面から消す」ための
        /// 都合で、3Dでそのまま使うと**選手が空中で消える**。アウトかどうかは判定オーバーレイ
        /// （Issue #328）だけで伝える＝到達の演技（駆け抜け/スライディング）はアウト/セーフを問わず
        /// 際どさ（closeCall）だけで選ぶ（Issue #417。旧「アウトは滑り込み失敗の演技で見せる」設計は
        /// オーナー未指示のため廃止）。一塁到達だけは際どさに関係なく常に駆け抜け
        /// （オーバーランは失格にならないルール）。
        /// レッグが始まる前は最初のレッグの起点に立たせる（engine は null を返す＝2Dでは描かない区間）。
        /// </summary>
        private static bool RunnerXZ(PlaybackRunner r, float t, out Vector2 xz)
        {
            var p = PlaybackEvaluator.RunnerAt(r, t, respectHideAt: false);
            if (p.HasValue) { xz = new Vector2((float)p.Value.X, (float)p.Value.Y); return true; }
            if (r?.Segs != null && r.Segs.Count > 0)
            {
                var f = r.Segs[0].From;
                xz = new Vector2((float)f.X, (float)f.Y);
                return true;
            }
            xz = Vector2.zero;
            return false;
        }

        /// <summary>この走者が最後に止まる時刻と、その塁でどうなったか。</summary>
        private static bool RunnerFinal(PlaybackRunner r, out float t1, out bool outAtEnd, out bool closeCall)
        {
            t1 = 0f; outAtEnd = false; closeCall = false;
            if (r?.Segs == null || r.Segs.Count == 0) return false;
            var last = r.Segs[r.Segs.Count - 1];
            // 塁上で動かないレッグ（待機）は到達ではない
            if (new Vector2((float)(last.To.X - last.From.X), (float)(last.To.Y - last.From.Y)).sqrMagnitude < 0.25f)
                return false;
            t1 = (float)last.T1;
            outAtEnd = last.OutAtEnd;
            closeCall = last.CloseCall;
            return true;
        }

        /// <summary>
        /// 打者走者（本塁→一塁）が一塁に留まるレッグか。一塁到達はアウト/セーフ/際どさに関わらず
        /// 常に駆け抜け（オーバーランは失格にならないルール）。長打で二塁以降へ続くレッグは
        /// 最終レッグの終点が一塁でなくなるため対象外＝最終レッグの終点だけで判定する（Issue #417）。
        /// </summary>
        private static bool IsBatterArrivingAtFirst(PlaybackRunner r)
        {
            if (r?.Label != "打" || r.Segs == null || r.Segs.Count == 0) return false;
            var to = r.Segs[r.Segs.Count - 1].To;
            var dx = (float)(to.X - FieldDiagramGeometry.First.X);
            var dz = (float)(to.Y - FieldDiagramGeometry.First.Y);
            return dx * dx + dz * dz < 4f; // 2m以内＝丸め誤差を吸収しつつ二塁・三塁とは混同しない
        }

        /// <summary>
        /// 到達の演技を選ぶ**純関数**（乱数を引かない）。アウトかどうかは演技を変えない＝
        /// アウトは判定オーバーレイ（Issue #328）で読ませる（Issue #417）。
        /// </summary>
        private static RunnerArriveAnchor SelectArrive(bool firstBaseArrival, bool closeCall)
            => !firstBaseArrival && closeCall ? ArriveSlideSafe : ArriveRunThrough;

        /// <summary>走者1人を時刻 t へ進める（守備の <see cref="StepFielderMotion"/> と同じ作り）。</summary>
        private void StepRunnerMotion(int idx, float t, float dt)
        {
            if (idx >= _runnerCast.Count || idx >= _runnerMotion.Count) return;
            var a = _runnerCast[idx];
            var r = _fieldPlay.Runners[idx];
            if (a == null || a.Go == null || !a.Enabled) return;
            var st = _runnerMotion[idx];

            Vector2 cur;
            if (!RunnerXZ(r, t, out cur)) return;

            // 到達の予定。**絶対時刻で判定する**（途中の時刻から再生しても同じ絵）
            float t1;
            bool closeCall;
            if (!st.ArriveFired && RunnerFinal(r, out t1, out _, out closeCall))
            {
                var an = SelectArrive(IsBatterArrivingAtFirst(r), closeCall);
                float tStart = t1 - an.Touch;
                if (t >= tStart)
                {
                    var clip = LoadClip(an.Clip);
                    if (clip != null)
                    {
                        // 頭出し。**負になってはいけない**（負＝塁に触る芝居が着く前に終わっている）
                        _runnerHeadMin = Mathf.Min(_runnerHeadMin, t - tStart);
                        SetActorClip(a, clip, t - tStart, an.Touch + ArriveFollowSeconds);
                        a.Speed = 1f;
                        a.HoldSeconds = ArriveHoldSeconds;
                        st.Ladder = int.MinValue;
                        st.ArriveUntil = t1 + ArriveFollowSeconds + ArriveHoldSeconds;
                        // 塁へ触れる手足が塁の方を向くように体を回す（F5 のグラブ・F6 の手と同型）
                        var th = new Vector2(an.TouchX, an.TouchZ);
                        var to = new Vector2((float)r.Segs[r.Segs.Count - 1].To.X,
                            (float)r.Segs[r.Segs.Count - 1].To.Y);
                        var from = new Vector2((float)r.Segs[r.Segs.Count - 1].From.X,
                            (float)r.Segs[r.Segs.Count - 1].From.Y);
                        var dir = to - from;
                        if (dir.sqrMagnitude > 1e-6f && th.magnitude > 0.05f)
                            st.Yaw = Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg
                                     - Mathf.Atan2(th.x, th.y) * Mathf.Rad2Deg;
                        st.Started = true;
                    }
                    st.ArriveFired = true;
                }
            }

            if (t < st.ArriveUntil)
            {
                // 到達の芝居の間は位置だけ engine の真値へ置き続ける
                PlaceRunner(a, cur.x, cur.y, st.Yaw);
                _runnerMotion[idx] = st;
                return;
            }

            // 速度は中央差分（engine は位置しか返さない）
            Vector2 pa, pb;
            float ta = Mathf.Max(0f, t - LocoSampleDt), tb = t + LocoSampleDt;
            if (!RunnerXZ(r, ta, out pa) || !RunnerXZ(r, tb, out pb)) { _runnerMotion[idx] = st; return; }
            var v = (pb - pa) / Mathf.Max(1e-4f, tb - ta);
            float speed = v.magnitude;

            int want = -1;
            float mul = 1f;
            if (speed >= FielderIdleSpeedMps)
            {
                float best = float.MaxValue;
                for (int i = 0; i < RunnerLadder.Length; i++)
                {
                    float m = Mathf.Clamp(speed / RunnerLadder[i].VClip, LocoSpeedMin, LocoSpeedMax);
                    float err = Mathf.Abs(Mathf.Log(speed / (RunnerLadder[i].VClip * m)));
                    if (err < best - 1e-4f) { best = err; want = i; mul = m; }
                }
                if (dt > 0f)
                {
                    float ratio = speed / Mathf.Max(0.01f, RunnerLadder[want].VClip * mul);
                    _runnerSlipMax = Mathf.Max(_runnerSlipMax, ratio);
                    _runnerSlipMin = Mathf.Min(_runnerSlipMin, ratio);
                }
            }

            float targetYaw = want < 0
                ? st.Yaw
                : Mathf.Atan2(v.x, v.y) * Mathf.Rad2Deg - RunnerLadder[want].MoveDirDeg;
            st.Yaw = st.Started
                ? Mathf.MoveTowardsAngle(st.Yaw, targetYaw, FielderYawRateDegPerSec * Mathf.Max(0f, dt))
                : targetYaw;
            st.Started = true;

            if (want != st.Ladder)
            {
                var clip = want < 0 ? LoadClip(RunnerIdleClipName) : LoadClip(RunnerLadder[want].Name);
                if (clip != null && clip != a.Clip)
                {
                    float phase = a.Clip != null && a.Clip.length > 0.01f
                        ? Mathf.Repeat(a.Time / a.Clip.length, 1f) : 0f;
                    SetActorClip(a, clip, phase * clip.length, 0f);
                    a.IdleClip = clip;
                    a.IdleEndAt = 0f;
                }
                st.Ladder = want;
            }
            a.Speed = want < 0 ? 1f : mul;   // ⚠ SetActorClip では戻らない（毎コマ入れ直す）

            PlaceRunner(a, cur.x, cur.y, st.Yaw);

            if (dt > 0f)
            {
                var lp = a.Go.transform.localPosition;
                _runnerMaxPosErr = Mathf.Max(_runnerMaxPosErr,
                    new Vector2(lp.x - cur.x, lp.z - cur.y).magnitude);
            }
            _runnerMaxSpeed = Mathf.Max(_runnerMaxSpeed, speed);
            _runnerMotion[idx] = st;
        }

        /// <summary>走者を置く（守備の <see cref="PlaceFielder"/> と同じ。接地はマウンド以外は平ら）。</summary>
        private void PlaceRunner(Actor a, float x, float z, float yawDeg)
        {
            if (a == null || a.Go == null) return;
            var lp = a.Go.transform.localPosition;
            a.Go.transform.localPosition = new Vector3(x, lp.y, z);
            a.Go.transform.localRotation = Quaternion.Euler(0f, yawDeg, 0f);
            a.BasePos = a.Go.transform.localPosition;
            a.BaseRot = a.Go.transform.localRotation;
            a.GroundY = FieldGroundY(x, z);
            GroundFix(a);
        }

        /// <summary>
        /// 【撮影ハーネス用】走者の受け入れ実測。
        ///
        /// <c>posErr</c> は engine の言う位置との差＝写しているだけなので 0.000 のはず。
        /// <c>着塁ずれ</c> は<b>レッグの終端時刻ちょうど</b>での「走者の位置 → その塁」の差。
        /// ⚠ ここを「最後に描いたコマ」で測ってはいけない。ポーズは12fpsなので最後のコマは
        /// 終端の最大83ms手前＝6.6m/s なら 0.55m 残っており、**サンプリングの粗さを
        /// 誤差として読んでしまう**（2026-08-01 実測でそう出た）。
        /// <c>頭出し</c> は到達クリップの頭をどれだけ捨てたか＝**負になってはいけない**。
        /// <c>slip</c> は足と地面の食い違い（1.0=一致 / &gt;1=前へ滑る）。
        /// </summary>
        public string RunnerDiagForCapture()
        {
            float arriveErr = 0f;
            if (_fieldPlay?.Runners != null)
            {
                foreach (var r in _fieldPlay.Runners)
                {
                    float t1;
                    bool o, c;
                    if (!RunnerFinal(r, out t1, out o, out c)) continue;
                    Vector2 at;
                    if (!RunnerXZ(r, t1, out at)) continue;
                    var to = r.Segs[r.Segs.Count - 1].To;
                    arriveErr = Mathf.Max(arriveErr,
                        new Vector2(at.x - (float)to.X, at.y - (float)to.Y).magnitude);
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "走者={0}人\tposErrMax={1:F3}m\t着塁ずれ={2:F3}m\t頭出しの最小={3:F2}s\tvMax={4:F2}m/s\tslip={5:F2}〜{6:F2}",
                _runnerMotion.Count, _runnerMaxPosErr, arriveErr,
                _runnerHeadMin > 1e6f ? 0f : _runnerHeadMin, _runnerMaxSpeed,
                _runnerSlipMin > 1e6f ? 1f : _runnerSlipMin, _runnerSlipMax));
            sb.AppendLine("走者\tt1\t結果\tclip\tHideAt");
            if (_fieldPlay?.Runners != null)
            {
                for (int i = 0; i < _fieldPlay.Runners.Count; i++)
                {
                    var r = _fieldPlay.Runners[i];
                    float t1;
                    bool o, c;
                    if (!RunnerFinal(r, out t1, out o, out c)) { sb.AppendLine(r.Label + "\t(移動なし)"); continue; }
                    var an = SelectArrive(IsBatterArrivingAtFirst(r), c);
                    sb.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "{0}\t{1:F2}\t{2}\t{3}\t{4}", r.Label, t1,
                        o ? "アウト" : c ? "際どいセーフ" : "セーフ",
                        an.Clip.Replace("AA_Baseball_Offense_", ""),
                        r.HideAt.HasValue ? r.HideAt.Value.ToString("F2") : "—"));
                }
            }
            return sb.ToString();
        }

        // ---- ビルド同梱のための名前表（F8） --------------------------------------

        /// <summary>
        /// このリグが使うクリップ名を全部返す。**名前の単一ソースはコード側の表のまま**で、
        /// ビルド同梱の索引（<see cref="DuelAssetCatalog"/>）はここを読んで自動で埋める。
        ///
        /// ⚠ クリップを使う所を足したら**必ずここへ足す**。足し忘れは
        /// エディタでは AssetDatabase が拾ってしまうので絵に出ず、**Player ビルドでだけ**
        /// そのクリップが消える（＝手元では絶対に気づけない類の壊れ方）。
        /// </summary>
        public static System.Collections.Generic.IEnumerable<string> RequiredClipNames()
        {
            // 対決4体
            yield return PitcherClipName;
            yield return BatterClipName;
            yield return BatterOwnIdleClipName;
            yield return CatcherIdleClipName;
            yield return UmpireIdleClipName;
            yield return UmpireStrikeClipName;
            yield return BatterHbpClipName;
            foreach (var c in CatchAnchors) yield return c.Clip;

            // 守備（F2・F4・F5・F6）
            yield return InfielderIdleClipName;
            yield return OutfielderIdleClipName;
            foreach (var l in LocoLadder) yield return l.Name;
            foreach (var a in FieldCatchAnchors) yield return a.Clip;
            foreach (var a in FieldThrowAnchors) yield return a.Clip;
            yield return CatcherThrowAnchor.Clip;

            // 走者（F7）
            yield return RunnerIdleClipName;
            foreach (var l in RunnerLadder) yield return l.Name;
            yield return ArriveRunThrough.Clip;
            yield return ArriveSlideSafe.Clip;
            yield return ArriveSlideOut.Clip;
        }

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

        /// <summary>球場メッシュが無くて旧球場へ落ちた警告は1回だけ（試合ごとに出すと埋もれる）。</summary>
        private static bool _stadiumFallbackWarned;

        /// <summary>
        /// 焼いた球場メッシュ（<see cref="DuelStadiumMesh"/>）で球場を組む。無ければ false を返し、
        /// 呼び出し側が下の手続き生成へ落ちる＝**資産を焼いていない環境でも絵は出る**
        /// （購入資産と違ってこれは生成物なので普段は必ず在るが、焼き忘れで手元が止まらないように）。
        ///
        /// メッシュは資産の一部なので <c>_meshes</c> へ入れない（入れると Dispose で
        /// **プロジェクトの資産を壊す**）。マテリアルだけは実行時生成なので従来どおり登録する。
        /// </summary>
        private bool BuildStadiumMesh(int layer)
        {
            var asset = Resources.Load<DuelStadiumMesh>(DuelStadiumMesh.ResourceName);
            if (asset == null || asset.Parts == null || asset.Parts.Length == 0)
            {
                // ⚠ 黙って落ちない。焼いた資産は Resources ごと gitignore なので、**clone 直後や
                //   焼き忘れでは必ずここを通る**。旧球場（手続き生成）は形も色も別物だが、
                //   絵を見ても「そういう球場」に見えるだけで気づけない
                if (!_stadiumFallbackWarned)
                {
                    _stadiumFallbackWarned = true;
                    Debug.LogWarning("球場メッシュが無いので手続き生成の旧球場へ落ちた。"
                                     + "メニュー「KokoSim/Duel/球場メッシュを焼く」で焼くこと");
                }
                return false;
            }

            // 色は17種しか無いのにサブメッシュは60超あるので、色で使い回す（毎回 new すると材質が60個できる）
            var byColor = new Dictionary<Color, Material>();
            foreach (var part in asset.Parts)
            {
                if (part == null || part.Mesh == null) continue;
                var go = new GameObject(part.Name);
                go.transform.SetParent(_root.transform, false);
                go.layer = layer;
                go.AddComponent<MeshFilter>().sharedMesh = part.Mesh;
                var mats = new Material[part.Mesh.subMeshCount];
                for (int i = 0; i < mats.Length; i++)
                {
                    var c = i < part.Colors.Length ? part.Colors[i] : Color.magenta;
                    if (!byColor.TryGetValue(c, out var mat))
                    {
                        mat = FlatMaterial(c);
                        byColor[c] = mat;
                    }
                    mats[i] = mat;
                }
                go.AddComponent<MeshRenderer>().sharedMaterials = mats;
            }
            return true;
        }

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
        /// 注視点（高さ）の追従速度上限[m/s]（issue #399②-2）。①（頂点・着地点基準）でセグメント中の
        /// 目標は一定になるが、セグメントの変わり目（接触→滞空／着地・捕球→保持）では目標が段差で変わる。
        /// ここへ即座に飛ぶと段差そのものが目に付くので、Mathf.MoveTowards で寄せる。
        /// 全景へ戻す「カット」（<see cref="_fieldCamCutT"/>）は対象外＝そこは既存どおり一気に切り替える。
        /// **絵で決める値**なので比較撮影のために差し替えられるようにしてある。
        /// </summary>
        public static float FieldAimYSpeedCapMPerSecond = 20f;

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
        /// 守備ビューの打球残像・尾の長さ[秒]（issue #398 ①）。対決ビュー向けの値
        /// （<see cref="DuelCalibration.BattedTrailSeconds"/>＝0.28s）は「接触から俯瞰へ渡るまで
        /// （旧・約0.55s）で消え切る」前提で詰めた長さで、いまの引きの構図（frameH 29〜65m）では
        /// 短すぎて「点」に近い。**絵で決める値**なので比較撮影のために差し替えられるようにしてある。
        /// </summary>
        public static float FieldBattedTrailSeconds = 0.45f;

        /// <summary>
        /// 守備ビューの打球残像・頭の太さ[m]（issue #398 ①）。対決ビュー向けの 0.045m は本塁付近の
        /// 寄った構図で約10pxになるよう詰めた値で、frameH 29m 級の構図では1px未満（実質見えない）。
        /// **絵で決める値**なので比較撮影のために差し替えられるようにしてある。
        /// </summary>
        public static float FieldBattedTrailWidthM = 0.35f;

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
        /// 時刻 t の構図（注視点と縦に収める実寸）を求める。**速度上限をかける前の生の目標値**
        /// （issue #399②-2の速度上限は <see cref="ApplyFieldCamera"/> 側の状態で別途かける）。
        ///
        /// 水平（<c>Cx,Cy</c>）とズームは engine の追随カメラのプランそのもの。**高さだけは 2D の俯瞰に
        /// 存在しない＝プランが持っていない**ので、ここで足す。無いと寄った瞬間にフライが画面の上へ抜ける
        /// （枠 19m の構図に対し打球の頂点は 30m 級＝1枠ぶん以上うわを通る）。
        ///
        /// 滞空中（<see cref="BallSegmentKind.Flight"/>）は**現在高度を追わない**（issue #399②-1）。
        /// 現在高度＋0.6秒先読みで決めると、上昇中はほぼ同じ高さへ貼り付き続けて上昇が絵から消える
        /// （ライナーに見える症状の主因）。代わりに**そのセグメントの頂点・着地点（0とApexHeightM）を
        /// 両端とする箱**を取る＝engine 側 FitBox の縦版で、セグメント中は枠が一定のまま
        /// 球がその中で弧を描いて見える。頂点は <c>apex·4u(1−u)</c>（u=0.5で最大）の性質から
        /// セグメント中央の値をそのまま読むだけで求まる（式を Unity 側で持ち直さない）。
        /// それ以外の区間（保持中・ゴロ・送球等）は従来どおり現在＋先読みの箱に落ちる。
        /// </summary>
        private void ComputeFieldFrame(float t, out Vector3 aim, out float frameH)
        {
            var vp = CameraEvaluator.ViewportAt(_fieldPlan, t);
            float zoom = (float)System.Math.Max(vp.ZoomMult, 0.01);
            frameH = FieldWideHeightM / zoom;

            float aimY = FieldAimYMin;
            var flight = FieldFlightSegmentAt(t);
            if (flight != null)
            {
                float apex = Mathf.Max(0f, (float)flight.At((flight.T1 - flight.T0) * 0.5).H);
                aimY = Mathf.Max(FieldAimYMin, apex * 0.5f);
                frameH = Mathf.Max(frameH, apex + FieldMarginM * 2f);
            }
            else
            {
                var b0 = PlaybackEvaluator.BallAt(_fieldPlay, t);
                if (b0.HasValue)
                {
                    var b1 = PlaybackEvaluator.BallAt(_fieldPlay, t + (float)CameraPlanBuilder.LookaheadSeconds);
                    float h0 = (float)b0.Value.H;
                    float h1 = b1.HasValue ? (float)b1.Value.H : h0;
                    aimY = Mathf.Max(FieldAimYMin, (h0 + h1) * 0.5f);
                    frameH = Mathf.Max(frameH, Mathf.Abs(h0 - h1) + FieldMarginM * 2f);
                }
            }
            aim = new Vector3((float)vp.Cx, aimY, (float)vp.Cy);
        }

        /// <summary>時刻 t を覆う滞空セグメント（外野フライ等）。無ければ null（issue #399②-1）。</summary>
        private EndpointBallSegment FieldFlightSegmentAt(float t)
        {
            if (_fieldPlay == null) return null;
            foreach (var s in _fieldPlay.Ball)
            {
                if (t < s.T0) return null;
                if (t <= s.T1) return s is EndpointBallSegment { Kind: BallSegmentKind.Flight } eb ? eb : null;
            }
            return null;
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
                else if (o != cur)
                {
                    float cutT = SnapStationCutOutsideFlight(play, t, start, end);
                    runs.Add((runStart, cutT, cur));
                    cur = o; runStart = cutT;
                }
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

        /// <summary>
        /// 据え位置カットの時刻を、滞空セグメント（外野フライ等）の外へ寄せる（issue #399②-3）。
        /// セグメントの途中で切ると、球がまだ弧を描いている最中にカメラの角度（内野／外野の据え位置）が
        /// 変わり、上昇／下降の連続性が絵から切れる。①（頂点・着地点基準）で枠自体は段差なしになった
        /// あとも、視点そのものが変わる据え位置カットは残る問題なので別立てで直す。
        /// 元の切り替え時刻に近いほう（頂点より前＝セグメント開始／着地の後＝セグメント終了）へ寄せ、
        /// ずれを最小にする。
        /// </summary>
        private static float SnapStationCutOutsideFlight(PlaybackPlay play, float t, float start, float end)
        {
            foreach (var s in play.Ball)
            {
                if (!(s is EndpointBallSegment { Kind: BallSegmentKind.Flight })) continue;
                if (t <= s.T0 || t >= s.T1) continue;   // セグメントの外＝そのまま
                float toStart = t - (float)s.T0, toEnd = (float)s.T1 - t;
                float snapped = toStart <= toEnd ? (float)s.T0 : (float)s.T1;
                return Mathf.Clamp(snapped, start, end);
            }
            return t;
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
        /// 全景へ戻し始める時刻（＝決着＋<c>ReturnToWideDelaySeconds</c>）と、プランの終端時刻。
        ///
        /// <b>何を直しているか</b>: プランはこの時刻から全景（<c>WideCy</c>=69m・縦155m）へ戻り始めるが、
        /// ズーム変化率の上限のせいで戻り切るのに約1.5秒かかる。2Dの俯瞰は全景に盤面が全部入るので
        /// 途中も絵として成立するが、寄っている3Dでは**球も決着の場所も枠の外**＝
        /// 「用のないところを見ている1.5秒」になる（F3 の申し送り）。
        ///
        /// <b>直し方は「据え置き」ではなく「カット」</b>。一度は据え置きで実装したが、実測で
        /// **走者が残るプレー2.7秒・本塁打9.5秒**のあいだ止まった枠がフェンス際を映し続けると分かった
        /// （凡打は決着+0.9秒でプレー自体が終わるので据え置きに入らず、短いプレーだけ見ていると気づけない）。
        /// 戻り先そのものは正しい＝**流れるのをやめて一気に切り替える**。中継のカットと同じで、
        /// 全景では走者が塁を回っているのが見える（F9 以前に俯瞰で見せていたものと同じ）。
        ///
        /// ⚠ 寄りのピークは決着の0.6秒後（同じくズーム変化率の上限）なので、決着ちょうどで切ると
        /// 一番寄った絵を捨てることになる。切るのは戻し始める瞬間で、そこまでは追わせる。
        /// </summary>
        private float _fieldCamCutT = float.PositiveInfinity;
        private float _fieldPlanEndT;

        /// <summary>
        /// カメラが使う時刻。全景へ戻し始める瞬間から先は**プランの終端**（＝レート制限が収束した後の
        /// 全景そのもの）を引く＝流れずに切り替わる。**実測の窓もこれを通す**。
        /// 素の t で測ると数値だけ滑らかに動き、カットになっているのか流れているのか区別できない。
        /// </summary>
        private float FieldCamTime(float t) => t < _fieldCamCutT ? t : _fieldPlanEndT;

        /// <summary>
        /// 注視点の高さの**表示値**（速度上限を通した後。issue #399②-2）。<see cref="FieldFrameDiagForCapture"/>
        /// はこれを読む＝実測が「画面に出ている枠そのもの」になる（<see cref="ComputeFieldFrame"/> は生の目標値）。
        /// </summary>
        private float _fieldAimY = FieldAimYMin;

        /// <summary>
        /// 時刻 t の視野を守備カメラへ流す。据え位置は切り替え計画から採り、
        /// 同じ据え位置でいる間は**位置が動かない**（向きと画角だけが動く＝中継の三脚と同じ）。
        /// </summary>
        /// <param name="dt">前フレームからの経過秒（注視点の高さの速度上限に使う）。</param>
        private void ApplyFieldCamera(float t, float dt)
        {
            if (_fieldPlan == null || _fieldPlan.Count == 0) return;
            float ct = FieldCamTime(t);
            Vector3 target;
            float frameH;
            ComputeFieldFrame(ct, out target, out frameH);

            // 全景への「カット」は水平・ズームと同じく一気に切り替える（据え置きにしない・上の申し送り）。
            // それ以外は段差へ即座に飛ばず、速度上限つきで寄せる（急上昇中は追いつかせない＝issue #399②-2）。
            _fieldAimY = t >= _fieldCamCutT
                ? target.y
                : Mathf.MoveTowards(_fieldAimY, target.y, FieldAimYSpeedCapMPerSecond * Mathf.Max(dt, 0f));

            FramePreview(FieldStationAt(ct), new Vector3(target.x, _fieldAimY, target.z), frameH);
        }

        // ---- 打球プレーの再生 ---------------------------------------------------

        private PlaybackPlay _fieldPlay;
        private float _fieldT;
        private float _fieldT0;
        private int _fieldPoseGen = -1;

        /// <summary>
        /// 打球プレー中だけ差し替える投手・捕手の状態。対決の状態機械（セット姿勢で待つ・
        /// フォロースルーで打ち切る等）をそのまま踏むと戻せなくなるので、丸ごと控えて戻す。
        /// </summary>
        private struct DuelActorSnapshot
        {
            public AnimationClip Clip, IdleClip;
            public float Time, Speed, EndAt, DefaultEndAt, IdleEndAt, GroundY;
            public bool HoldAtEnd, Holding, Valid;
            /// <summary>
            /// **立ち位置の基準**（<c>Actor.BasePos/BaseRot</c>）であって、控えた瞬間の transform ではない。
            /// 打球が出るのはリリースの約0.9秒後＝投手はフォロースルーの途中で、transform は
            /// ルートモーションぶん前へ出て回っている。そこを基準として戻すと、次の打席以降
            /// <c>RewindActor</c> が毎回その「振り終わりの向き」へ戻す＝**投手が変な向きで投げ始める**
            /// （2026-08-01 実害。F9 で初めて実際の打席の中を通って出た）。
            /// </summary>
            public Vector3 BasePos;
            public Quaternion BaseRot;
        }

        private DuelActorSnapshot _pitcherSnap, _catcherSnap;

        /// <summary>打球プレーを再生中か（守備カメラが効いているか）。</summary>
        public bool FieldPlayActive => _fieldPlay != null;

        /// <summary>
        /// 3Dの守備ビュー用に、決着後の定位置復帰レッグ（Backup/Cutoff の戻り・寄った内野手の戻り
        /// [Issue #336②]）を取り除いた <see cref="PlaybackPlay"/> を作る（issue #406①）。
        ///
        /// このレッグは TimelineBuilder が <c>Math.Max(resolvedAt, ...) + 0.3</c> でしか積まない
        /// ＝<c>T0 &gt;= play.ResAt</c> であることが唯一かつ確実な見分け方（<see cref="FielderTask"/> は
        /// <see cref="PlaybackMove"/> に運ばれておらず、ここでは読めない）。プレーは既に決まっているのに
        /// 野手が定位置へ歩いて戻るだけの絵を待たされる（テンポが悪い）のが症状で、直し方は
        /// 「エンジンのタイムラインからは消さない」こと＝2D俯瞰（デバッグ）はこのレッグを見せ続ける
        /// （"寄ったまま"を防ぐ元の目的は2Dでは今も有効。issue #406 の申し送り）。<see cref="EndFieldPlay"/>
        /// が <c>PlaceFieldCastAtDefault</c> で全員を定位置へ置き直すので、歩いて戻る絵を1コマも
        /// 描かなくても次の打席の配置は狂わない。
        /// </summary>
        private static PlaybackPlay StripPostResolutionReturnMoves(PlaybackPlay play)
        {
            if (play.Moves == null || play.Moves.Count == 0) return play;
            // エラー出塁（issue #439）は決着（ResAt=処理時刻）の後もプレーが続く＝こぼれ球・逸れた球を
            // 追いかけて拾うレッグ（T0 >= ResAt）が本編なので剥がさない。「決着後レッグ＝退屈な定位置復帰」
            // の前提はエラーでは成り立たない
            if (play.Judgements != null)
                foreach (var j in play.Judgements)
                    if (j.Kind == JudgementKind.Error) return play;
            var resAt = play.ResAt;
            var filtered = new Dictionary<FieldPosition, IReadOnlyList<PlaybackMove>>();
            var changed = false;
            foreach (var kv in play.Moves)
            {
                var segs = kv.Value;
                var kept = new List<PlaybackMove>(segs.Count);
                foreach (var m in segs)
                {
                    if (m.T0 >= resAt) { changed = true; continue; }
                    kept.Add(m);
                }
                if (kept.Count > 0) filtered[kv.Key] = kept;
            }
            return changed ? play with { Moves = filtered } : play;
        }

        /// <summary>
        /// 打球プレーの再生を始める。<paramref name="t0"/> は対決ビューで既に見せたぶんの続き
        /// （<c>MatchLiveController.ContactMomentOf</c> と同じ時計）。
        /// </summary>
        public void BeginFieldPlay(PlaybackPlay play, float t0)
        {
            if (!Available || play == null) return;
            play = StripPostResolutionReturnMoves(play);
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
                    CatchEvent = -1,
                    Entry = new Vector2(lp.x - (float)want.Pos.X, lp.z - (float)want.Pos.Y),
                };
            }

            // 打者は走者としてやり直す。打席の較正（バット・左打者のミラー親）を引きずったまま
            // 走らせるより、右利き前提の走者プールへバトンタッチするほうが安全（設計の既定路線）
            if (_batter?.Go != null) _batter.Go.SetActive(false);
            SetRunnerCastForPlay(play);
            _runnerMaxPosErr = _runnerMaxSpeed = 0f;
            _runnerHeadMin = float.MaxValue;
            _runnerSlipMax = 0f; _runnerSlipMin = float.MaxValue;

            BuildFieldCatchPlan(play);
            BuildFieldThrowPlan(play);         // ⚠ 捕球の後（投げ始めの下限に「その野手の捕球時刻」を使う）
            PlanErrorMark(play);               // びっくりマーク（issue #439 ②）
            BuildFieldCameraPlan(play);
            BuildFieldStationPlan(play, t0);   // ⚠ プランを組んだ後（据え位置の判定が視野プランを引く）
            // 全景へ戻し始める時刻は engine のプラン（BuildFieldCameraPlan＝CameraPlanBuilder.Build）と
            // 同じ CameraPlanBuilder.WideStartTime を単一ソースにする（issue #406②/#441。片方だけ
            // 変えるとプランと実カットがずれる）。死んだ球に走者レッグだけが残るプレー（本塁打の一周）
            // では着弾＋余韻へ前倒しされる＝カットも同じ瞬間に跳ぶので、プランの「全景への移動走行」
            // （3Dでは用のないところを見ている travel）は実カットに乗らない。
            var wideStart = CameraPlanBuilder.WideStartTime(play);
            _fieldCamCutT = double.IsFinite(wideStart) ? (float)wideStart : float.PositiveInfinity;
            // プランの終端はレート制限の収束余白（WideSettleBufferSeconds）ぶん後ろにあり、
            // そこを引けば「戻り切った全景」がそのまま出る＝Unity側で全景の値を持ち直さない
            _fieldPlanEndT = _fieldPlan != null && _fieldPlan.Count > 0
                ? (float)_fieldPlan[_fieldPlan.Count - 1].T : 0f;

            // 前のプレーの高さを引きずらない＝プレーの頭は速度上限を経由せず目標へ直接合わせる
            // （速度上限が効くのは次フレーム以降の段差だけ。issue #399②-2）
            if (_fieldPlan != null && _fieldPlan.Count > 0)
            {
                ComputeFieldFrame(FieldCamTime(t0), out var aim0, out _);
                _fieldAimY = aim0.y;
            }
            else _fieldAimY = FieldAimYMin;

            _fieldT = t0;
            SetFieldTime(t0);
        }

        /// <summary>
        /// 打球プレーの再生をやめ、対決ビューの構図へ戻す。
        /// ⚠ 走っていないときに呼んでも何もしない＝**投手・捕手を古い控えで上書きしない**
        /// （二度呼ぶと前のプレーの途中姿勢へ巻き戻る）。
        /// </summary>
        public void EndFieldPlay()
        {
            if (_fieldPlay == null) return;
            _fieldPlay = null;
            _fieldPlan = null;
            _fieldStationCuts.Clear();
            _loco.Clear();
            _catchEvents.Clear();
            _throwEvents.Clear();
            _runnerMotion.Clear();
            // びっくりマーク（issue #439 ②）はプレー打ち切りでも必ず消す（器は使い回す）
            if (_errorMark != null) _errorMark.SetActive(false);
            _errorMarkRole = null;
            _errorMarkAt = float.PositiveInfinity;
            foreach (var a in _runnerCast)
            {
                if (a?.Go == null) continue;
                a.Go.SetActive(false);
                a.Enabled = false;
            }
            if (_batter?.Go != null) _batter.Go.SetActive(true);
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
                // ⚠ transform ではなく基準を控える（上の項。フォロースルーの途中の姿勢を
                // 「立ち位置」として焼くと投手の向きが恒久的に狂う）
                BasePos = a.BasePos, BaseRot = a.BaseRot,
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

            // 捕球・送球の予定は**位置を置く前**に発火させる（クリップ差し替えは頭出しで位置を動かすため）。
            // 送球を後に置くのは、同じ野手が「捕る→投げる」と続くとき投げるほうが後勝ちであるべきだから
            FireDueCatches(t);
            FireDueThrows(t);

            // 守備9人（投手・捕手を含む）。位置は engine の真値、足と向きは Unity が合わせる
            foreach (var pos in AllFieldPositions)
                StepFielderMotion(pos, FielderActor(pos), t, dt);

            // 走者（そのプレーに出る人数だけ）
            for (int i = 0; i < _runnerMotion.Count; i++) StepRunnerMotion(i, t, dt);

            // グラブを球へ寄せるのは**ポーズが進んだ回だけ**。毎フレーム当てると、同じポーズに
            // 二度当たって寄りすぎる（重み<1 は今の手の位置からの補間だから。design-18 §4.5-③）
            if (_poseGen != _fieldPoseGen)
            {
                _fieldPoseGen = _poseGen;
                FixFielderCatchPose(t);
            }

            // ボール
            var ball = PlaybackEvaluator.BallAt(_fieldPlay, t);
            if (_ball != null)
            {
                if (ball.HasValue)
                {
                    var p = FieldBallPoint(new Vector3((float)ball.Value.X, (float)ball.Value.H, (float)ball.Value.Y), t);
                    _ball.transform.SetParent(_root.transform, false);
                    _ball.transform.localPosition = p;
                    _ball.SetActive(true);
                    // 守備ビューの残像はここで一本化する（issue #398 ①）。対決ビュー側の打球ステッパ
                    // （UpdateBattedBall）は _fieldPlay が立つと呼ばれなくなるので、二重に積む心配はない
                    PushTrailPoint(p, t, FieldBattedTrailSeconds);
                    UpdateBallShadow(p.x, p.z);
                }
                else
                {
                    _ball.SetActive(false);
                    if (_ballShadow != null) _ballShadow.SetActive(false);
                    // 球が消えた（捕球・失投等）後も尾は自然に消えるよう時計だけ進める。進めないと
                    // 尾の最後の点が刈られず、次にこの残像枠を使うプレーまで宙に浮いたまま残る
                    if (_trail.Count > 0) TrimTrail(t, FieldBattedTrailSeconds);
                }
            }

            ApplyFieldCamera(t, dt);
            // びっくりマーク（issue #439 ②）はカメラを動かした後＝このフレームの視点へ正対させる
            UpdateErrorMark(t);
            if (_ball != null && _ball.activeSelf) ApplyBallReadability();
        }

        // ── エラーのびっくりマーク（issue #439 ②）──
        // エラーした野手の頭上に「！」を出す。文字グリフに頼らず棒＋点の2箱で組む
        // （のっぺらぼう抽象方針 design-08 と同じ流儀）。表示はプレー時刻の純関数
        // （[エラー確定, +1.5s] の窓）で、位置・正対はフルレート追従＝ポーズ量子化の対象にしない
        // （ボール・カメラと同じ側。design-18 §4.5-②）。
        private GameObject _errorMark;
        private FieldPosition? _errorMarkRole;
        private float _errorMarkAt = float.PositiveInfinity;
        private const float ErrorMarkSeconds = 1.5f;
        private const float ErrorMarkAboveHeadM = 0.35f;
        /// <summary>マークの見た目の全高[m]（可読性スケールの基準。棒0.75+点=原点〜0.75）。</summary>
        private const float ErrorMarkHeightM = 0.75f;
        /// <summary>画面上で保証するマークの高さ[px]（球の BallMinScreenPx と同じ考え方の下限）。</summary>
        private const float ErrorMarkMinScreenPx = 28f;
        /// <summary>見かけ倍率の上限（全景で巨大な棒にならないための歯止め）。</summary>
        private const float ErrorMarkMaxScaleGain = 6f;

        /// <summary>このプレーのエラー確定時刻と「エラーした野手」を控える（BeginFieldPlay から）。</summary>
        private void PlanErrorMark(PlaybackPlay play)
        {
            _errorMarkRole = null;
            _errorMarkAt = float.PositiveInfinity;
            if (play?.Judgements == null) return;
            foreach (var j in play.Judgements)
                if (j.Kind == JudgementKind.Error) _errorMarkAt = (float)j.T;
            if (float.IsPositiveInfinity(_errorMarkAt) || play.Ball == null) return;
            // 「誰がエラーしたか」は TimelineBuilder が球側に載せる: 弾きロール（T0=エラー時刻）の
            // FromRole＝こぼした本人／受け手なし悪送球（Throw かつ ToBase なし）の FromRole＝投げた本人
            foreach (var s in play.Ball)
            {
                var eb = s as EndpointBallSegment;
                if (eb == null || !eb.FromRole.HasValue) continue;
                if ((eb.Kind == BallSegmentKind.Roll && Mathf.Abs((float)eb.T0 - _errorMarkAt) < 0.05f)
                    || (eb.Kind == BallSegmentKind.Throw && eb.ToBase == null))
                {
                    _errorMarkRole = eb.FromRole;
                }
            }
            if (_errorMarkRole == null) _errorMarkAt = float.PositiveInfinity;
        }

        private void UpdateErrorMark(float t)
        {
            var on = _errorMarkRole.HasValue && t >= _errorMarkAt && t <= _errorMarkAt + ErrorMarkSeconds;
            if (!on)
            {
                if (_errorMark != null && _errorMark.activeSelf) _errorMark.SetActive(false);
                return;
            }
            var a = FielderActor(_errorMarkRole.Value);
            if (a?.Go == null) return;
            EnsureErrorMark(a.Go.layer);
            var head = a.Animator != null ? a.Animator.GetBoneTransform(HumanBodyBones.Head) : null;
            var basePos = head != null ? head.position : a.Go.transform.position + Vector3.up * 1.7f;
            _errorMark.transform.position = basePos + Vector3.up * ErrorMarkAboveHeadM;
            if (_camera != null)
            {
                var fwd = _errorMark.transform.position - _camera.transform.position;
                if (fwd.sqrMagnitude > 1e-6f) _errorMark.transform.rotation = Quaternion.LookRotation(fwd);

                // 引きの構図でも読めるよう最低画面高を保証（球の可読性補正 ApplyBallReadability と同じ流儀
                // ＝位置は真値のまま・見かけの大きさだけ）。マークは原点から上側だけへ伸びる構成なので、
                // 拡大しても頭へ食い込まない
                var sa = _camera.WorldToScreenPoint(_errorMark.transform.position);
                var sb = _camera.WorldToScreenPoint(_errorMark.transform.position + _camera.transform.up * ErrorMarkHeightM);
                var scale = 1f;
                if (sa.z > 0f)
                {
                    var px = Mathf.Abs(sb.y - sa.y);
                    if (px > 0.001f) scale = Mathf.Min(Mathf.Max(1f, ErrorMarkMinScreenPx / px), ErrorMarkMaxScaleGain);
                }
                _errorMark.transform.localScale = Vector3.one * scale;
            }
            if (!_errorMark.activeSelf) _errorMark.SetActive(true);
        }

        private void EnsureErrorMark(int layer)
        {
            if (_errorMark != null) return;
            _errorMark = new GameObject("ErrorMark");
            _errorMark.transform.SetParent(_root.transform, false);
            _errorMark.layer = layer;
            // アンバーは tokens.uss --accent と同値（盤面ビューの色は状態コード＝注目。UI原則の適用範囲例外）。
            // FlatMaterial(_BaseColor)は宣言済みプロパティ＝SetColorのガンマ→リニアは自動で正しい
            // 原点（頭上のアンカー）から**上側だけ**へ伸びる配置＝可読性スケールで拡大しても頭へ食い込まない
            var amber = (Color)new Color32(0xF5, 0xC6, 0x4A, 0xFF);
            var bar = Primitive(PrimitiveType.Cube, "ErrorMarkBar",
                Vector3.zero, new Vector3(0.16f, 0.45f, 0.05f), amber, layer);
            bar.transform.SetParent(_errorMark.transform, false);
            bar.transform.localPosition = new Vector3(0f, 0.525f, 0f);
            var dot = Primitive(PrimitiveType.Cube, "ErrorMarkDot",
                Vector3.zero, new Vector3(0.16f, 0.14f, 0.05f), amber, layer);
            dot.transform.SetParent(_errorMark.transform, false);
            dot.transform.localPosition = new Vector3(0f, 0.15f, 0f);
            _errorMark.SetActive(false);
        }

        /// <summary>
        /// 捕球の直前から保持の終わりまで、球を**捕球点**へ寄せる。
        ///
        /// グラブの狙点と球の描画位置が別々だと、寄せたグラブの横で球が地面へ落ちる
        /// （engine の滞空は端点で高さ0へ戻るため）。**同じ1つの値から出す**のが掟
        /// （design-18 §4.5-⑨ の「投球の終わり・打球の始まり・バットの狙点」と同型）。
        /// 水平は engine の真値のまま＝判定にも帯にも触れない（動かすのは高さと、捕球後の
        /// 保持中だけ**描かれているグラブへの追従**）。
        /// </summary>
        private Vector3 BlendToCatchPoint(Vector3 enginePos, float t)
        {
            for (int i = 0; i < _catchEvents.Count; i++)
            {
                var e = _catchEvents[i];
                if (t > e.HoldUntil) continue;
                if (t >= e.TCatch) return CatchGloveNow(e);            // 捕ってから次の区間まではグラブに追従
                if (t < e.TCatch - CatchBallBlendSeconds) continue;
                float u = Mathf.InverseLerp(e.TCatch - CatchBallBlendSeconds, e.TCatch, t);
                return Vector3.Lerp(enginePos, e.Point, u * u * (3f - 2f * u));
            }
            return enginePos;
        }

        /// <summary>
        /// 捕球後・保持中の球の位置（stage-local）＝**描かれているグラブ（ミット中心）**。
        /// 送球の保持区間で既に「描かれている手」を使っている <see cref="ThrowHandNow"/> と同型
        /// （捕手の <c>TryPutBallInMitt</c>＝球をミットの子にするのと同じ意図。design-18 §4.5-③）。
        /// これが無いと、捕球後は空間固定の <see cref="FieldCatchEvent.Point"/> に球が置き去りになり、
        /// 待機へ戻っていくグラブから球だけが微妙にずれて見える（issue #399 ①）。
        /// アニメータが無い（未生成・破棄済み等）ときだけ、確定済みの捕球点へフォールバックする。
        /// </summary>
        private Vector3 CatchGloveNow(FieldCatchEvent e)
        {
            var a = FielderActor(e.Pos);
            if (a == null || a.Animator == null) return e.Point;
            var hand = a.Animator.GetBoneTransform(HumanBodyBones.LeftHand);
            if (hand == null) return e.Point;
            return _root.transform.InverseTransformPoint(DuelCatcherPose.MittCenter(a.Animator));
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

        /// <summary>
        /// 打球の影を球のXZ位置へ追従させる（守備ビュー限定・issue #401①・監督決定2026-08-03）。
        /// <see cref="ApplyBallReadability"/> の可読性スケールは掛けない＝影は常に実寸のまま地面に張り付き、
        /// 球だけが遠いほど拡大される。この「影に対して球がどれだけ浮いているか」が高さの手がかりになる
        /// （②可読性スケール自体の見直しは別issueで判断・同時変更しない）。
        /// </summary>
        private void UpdateBallShadow(float x, float z)
        {
            if (_ballShadow == null) return;
            _ballShadow.transform.localPosition = new Vector3(x, FieldGroundY(x, z) + BallShadowGroundOffsetM, z);
            _ballShadow.SetActive(true);
        }

        /// <summary>影の厚み[m]。薄い球を地面へ半分埋めて「楕円プリミティブ（ブロブ影）」の見た目にする。</summary>
        private const float BallShadowThicknessM = 0.01f;
        /// <summary>影を地面から持ち上げる高さ[m]。塁・打席と同じ z-fighting 回避の作法。</summary>
        private const float BallShadowGroundOffsetM = 0.006f;
        /// <summary>影の色。芝・土のどちらの上でも沈んで見えるよう暗くほぼ無彩色にする。</summary>
        private static readonly Color BallShadowColor = new Color(0.05f, 0.07f, 0.05f);

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
                else want = FieldHomeSpot(pos);

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
            // ⚠ 球は素の t、枠は**据え置き後の時刻**で採る（＝画面に出ている枠そのもの）。
            float ct = FieldCamTime(t);
            if (_fieldPlan != null && _fieldPlan.Count > 0)
            {
                ComputeFieldFrame(ct, out aim, out frameH);
                // 注視点の高さは速度上限を通した表示値に差し替える（生の目標値のままだと
                // 実測がカメラの実際の動きとずれる。issue #399②-2）
                aim.y = _fieldAimY;
            }

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
        /// 【撮影ハーネス用】<b>打球プレーから戻したあとの状態</b>を1行で返す。
        ///
        /// <b>なぜ要るか</b>: 守備ビューは対決ビューの持ち物（投手・捕手の立ち位置と向き、球の見かけ倍率、
        /// 打者の在／不在）を一時的に借りて書き換える。借りっぱなしの取りこぼしは
        /// **次の打席になって初めて絵に出る**うえ、「投手が変な向き」「球がやたら大きい」という
        /// 症状からは原因の箇所を切り分けられない（2026-08-01 に実際に2件出た）。
        /// プレーの前後でこの1行が**完全に一致する**ことを受け入れ条件にすれば、
        /// 新しい借り物が増えても同じ窓で捕まる。
        /// </summary>
        public string DuelRestoreDiagForCapture()
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            // ⚠ 横位置は**打者の側で割って**出す。捕手・球審は左右打席で寄る側が入れ替わる
            //（`CatcherX * BatterSideSign`）ので、生の x で比べると**左打者の打席が毎回「ずれ」に見える**
            // ＝本当の戻し漏れが埋もれる（実測で ±0.02 の往復として出た）。左右に依らない量で比べる。
            float side = BatterSideSign;
            System.Func<Actor, string> one = a =>
                a == null || a.Go == null ? "-"
                : string.Format(ci, "{0:F2},{1:F2}/{2:F1}", a.BasePos.x * side, a.BasePos.z, a.BaseRot.eulerAngles.y);
            return string.Format(ci, "投={0}\t捕={1}\t球={2:F3}\t打者={3}",
                one(_pitcher), one(_catcher),
                _ball != null ? _ball.transform.localScale.x : -1f,
                _batter != null && _batter.Go != null && _batter.Go.activeSelf ? "在" : "不在");
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
