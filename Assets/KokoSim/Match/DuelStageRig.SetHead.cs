using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// セット姿勢で待つ投手の首（issue #912）と、投球中もその形を保つ首（issue #973）。
    /// </summary>
    public sealed partial class DuelStageRig
    {
        /// <summary>
        /// 「セット姿勢で待っている」とみなす投球クリップ内時刻の上限[秒]。待ちは常にクリップ0秒で静止しているので
        /// （生成直後・打席の頭の <see cref="ResetPitcherToSet"/>・最後の1球の後・終局）、0秒ちょうどを拾えれば足りる。
        /// 投球は <see cref="DeliveryStartTime"/>（1.75秒）から再生するので、投球中にこの窓へ入ることはない。
        /// </summary>
        private const float SetPoseWindowEnd = 0.05f;

        /// <summary>投球クリップ（セット姿勢の判定用。プレビューで直立ループへ差し替えた投手は対象外）。</summary>
        private AnimationClip _pitchClip;

        /// <summary>首の補正の効き（0〜1）。セットで待っている間と投球中（リリースまで）は1へ、それ以外は0へ、<see cref="CrossfadeSeconds"/>で寄せる。</summary>
        private float _setHeadW;

        /// <summary>
        /// 顔の向きの行き先を「その時点の収録のひねり」から「セットで止まっていたときのひねり」へ寄せる割合（0〜1）。
        /// 投球中も首の角度（胴に対するひねり）をセットのまま保つ（issue #973・2026-11-15 監督指摘:
        /// 当初の「顔が捕手を向くひねり」は胴が回るぶん首が本塁側へ回りすぎて見えた）。
        /// </summary>
        private float _headLookW;

        /// <summary>セットで止まっていたときの頭のひねり[度]（<see cref="TryHeadTurn"/> の測り方）。セットの間に毎回控える。</summary>
        private float _setTurn;
        private bool _setTurnReady;

        /// <summary>
        /// セットで止まっていたときの「胴→捕手の角度のうち、首でひねっていた割合」（右投手の実測 −61°÷−92°≈0.66）。
        /// 投球中はこの割合のまま首をひねる。胴に対するひねり（度）をそのまま保つと、胴が約180°回るフィニッシュで
        /// 顔が二塁側（中継カメラ側）を向き、背中越しに顔が見えた（2026-11-15 監督指摘・実測: リリース時 胴→捕手 +93°）。
        /// 割合で保てば顔は常に胴と捕手の間にあり、背中側へは回らない。
        /// </summary>
        private float _setTurnRatio;
        private bool _setRatioReady;

        /// <summary>【診断用】最後に当てたときの収録のひねり・行き先のひねり[度]。</summary>
        private float _diagRawTurn, _diagTargetTurn;

        /// <summary>
        /// リリース後に首を収録のフォロースルーへ返す時間[秒]。胴が一気に回るリリース直後に 0.25秒（<see cref="CrossfadeSeconds"/>）で
        /// 返すと、保っていたセットのひねりと収録のひねりの差が一度に出て頭が振られて見えた（2026-11-15 監督指摘）。
        /// 胴の回転に紛れるよう長めにほどく。
        /// </summary>
        private const float PostReleaseReturnSeconds = 0.6f;

        /// <summary>顔が向く先（捕手の頭の高さ）の高さ[m]。</summary>
        private const float HeadLookHeightM = 1.0f;

        /// <summary>補正を最後に当てたときの効き（静止中に効きが動いたら評価し直して当て直すため）。</summary>
        private float _setHeadApplied = -1f, _setGloveApplied = -1f;

        /// <summary>投球の切り口（<see cref="DeliveryStartTime"/>）での首・頭のローカル回転（生成時に実測）。</summary>
        private Quaternion _deliveryNeckLocal, _deliveryHeadLocal;
        private bool _deliveryHeadReady;

        /// <summary>投手がいまセット姿勢（投球クリップの頭）にいるか。セット補正（首・グラブ）の効きの行き先。</summary>
        private bool PitcherAtSet =>
            _pitcher != null && _pitcher.Clip != null && _pitcher.Clip == _pitchClip
            && _pitcher.Time <= SetPoseWindowEnd;

        /// <summary>
        /// 投球の切り口の首・頭の姿勢を控える（生成時に1回）。<see cref="SeekActor"/> で切り口へ頭出しした直後に呼ぶ。
        /// 骨のローカル回転は鏡映を外した空間で評価した値（<see cref="EvaluateGraph"/>）なので、左投手もこの値でよい。
        /// </summary>
        private void CaptureDeliveryHeadPose()
        {
            if (_pitcher == null) return;
            _pitchClip = _pitcher.Clip;
            var anim = _pitcher.Animator;
            var neck = anim != null ? anim.GetBoneTransform(HumanBodyBones.Neck) : null;
            var head = anim != null ? anim.GetBoneTransform(HumanBodyBones.Head) : null;
            if (neck == null || head == null) return;
            _deliveryNeckLocal = neck.localRotation;
            _deliveryHeadLocal = head.localRotation;
            _deliveryHeadReady = true;
        }

        /// <summary>首の補正の効きをプレー時間で進める（Tick の頭で毎フレーム）。駆動はクリップ内時刻の窓（状態）。</summary>
        private void AdvanceSetHeadWeight(float dt)
        {
            // セットで待つ間（issue #912）に加え、投球の頭出しからリリース（<see cref="ReleaseTime"/>）まで保つ（issue #973）。
            // リリースを過ぎたら PostReleaseReturnSeconds かけて収録のフォロースルーへ返す
            bool inDelivery = _pitcher != null && _pitcher.Clip != null && _pitcher.Clip == _pitchClip
                && _pitcher.Time <= ReleaseTime;
            bool afterRelease = !inDelivery && _pitcher != null && _pitcher.Clip == _pitchClip;
            float fadeSeconds = afterRelease ? PostReleaseReturnSeconds : CrossfadeSeconds;
            _setHeadW = Mathf.MoveTowards(_setHeadW, inDelivery ? 1f : 0f, dt / fadeSeconds);
            // 入る側（セット・投球）は補正をポーズの切り替えと一緒に効かせる（下の SetFixEnterProgress）
            if (inDelivery) _setHeadW = Mathf.Max(_setHeadW, SetFixEnterProgress);
            if (PitcherAtSet) _headLookW = Mathf.Min(_headLookW, 1f - SetFixEnterProgress);
            _headLookW = Mathf.MoveTowards(_headLookW, PitcherAtSet ? 0f : 1f, dt / CrossfadeSeconds);
        }

        /// <summary>
        /// 投手のポーズがセット（または次球の投球）へ入るときの切り替えの進み（0〜1。補正の効きの下限に使う）。
        /// 補正の効きを独立に0.25秒かけて上げると、最後の1球の後のように**ポーズが一瞬でセットへ戻る**ときは
        /// 補正前のうつむいた首・寝かせたグラブが先に映り、そこから首とグラブをぐいっと起こす動きが見えた
        /// （2026-11-15 監督指摘「次のセットは最初から完璧な首とグラブの角度で入ってほしい」）。
        /// ポーズが一瞬で切り替わる（クロスフェード無し）なら1＝補正も即座に満額。クロスフェード中はその進み
        /// （u。ミキサーの重みは smoothstep(u)）＝セットの形と補正が同じ速さで寄る。
        /// </summary>
        private float SetFixEnterProgress
        {
            get
            {
                if (_pitcher == null) return 0f;
                if (_pitcher.FadeRemaining <= 0f || _pitcher.FadeDuration <= 1e-4f) return 1f;
                return Mathf.Clamp01(1f - _pitcher.FadeRemaining / _pitcher.FadeDuration);
            }
        }

        /// <summary>
        /// セット姿勢の首・頭を**直立のまま捕手を見る形**へ直す。ポーズを評価した直後だけ呼ぶ。
        ///
        /// 購入クリップの0秒（セット姿勢）は、真横（三塁側）を向いた胴から本塁を見る収録で、首が切り口より
        /// 首28°・頭39°ずれている（ローカル回転の差。2026-11-14 実測・issue #912）。胴に対しては
        /// 本塁側へ61°ひねって左肩へ8°傾き、12°うつむく（切り口は11°・右へ2.5°・16°）。0.75秒（振りかぶりの
        /// 途中）からは切り口との差が首4°・頭2°以内に収まる＝**曲がって見えるのはセットで待っている間だけ**で、
        /// 投球へ入ると直る。待ちは0秒で静止するので、試合開始直後・打席の頭（采配窓の間）に長く映っていた。
        ///
        /// 直し方（2026-11-14 監督決定「捕手を見る形」）: **傾きとうつむきだけ消し、本塁側へのひねりは残す**。
        /// ①首・頭を切り口の姿勢へ寄せる（首と頭の折れをまっすぐにする）→ ②頭の上方向を背骨の軸へ起こす（傾き・うつむき0）
        /// → ③頭の上方向の軸まわりにだけ、収録のひねりとの差を首で回し戻す。上方向の軸まわりの回転は頭の上方向を
        /// 動かさない＝③は②で消した傾き・うつむきを戻さない。
        /// 角度はすべて首の親（胸）のローカル空間で測って回す＝鏡映親（左投手）の下でも同じ式で効く
        /// （ワールドの回転を負スケールの下で直接書くと手系が裏返る）。クリップ自体は改変しない（部位の補正窓）。
        /// </summary>
        private void FixPitcherSetHead()
        {
            // 効きの下限（ポーズの切り替えの進み）は、いま評価したポーズで取り直す。Tick の頭で進めた値は
            // ポーズを進める前の状態（一瞬でセットへ戻ったコマでは0のまま）で、補正前の首が1コマ映る
            AdvanceSetHeadWeight(0f);
            _setHeadApplied = _setHeadW;
            if (_setHeadW <= 0f || !_deliveryHeadReady || _pitcher == null || _pitcher.Animator == null) return;
            var anim = _pitcher.Animator;
            var neck = anim.GetBoneTransform(HumanBodyBones.Neck);
            var head = anim.GetBoneTransform(HumanBodyBones.Head);
            if (neck == null || head == null || neck.parent == null) return;
            float w = _setHeadW * _setHeadW * (3f - 2f * _setHeadW);
            bool haveTurn = TryHeadTurn(out float rawTurn, out _);
            neck.localRotation = Quaternion.Slerp(neck.localRotation, _deliveryNeckLocal, w);
            head.localRotation = Quaternion.Slerp(head.localRotation, _deliveryHeadLocal, w);
            if (!haveTurn) return;
            // 切り口の姿勢でも頭は胸に対して19°うつむき・2.6°傾いている（そのまま使うとうつむきが収録の16°より深くなる）。
            // 頭の上方向を背骨の軸へ最短で起こす＝傾き・うつむきを0にする（首の付け根で回す）
            {
                var up0 = neck.parent.InverseTransformDirection(head.TransformDirection(_pitcher.HeadUpLocal));
                var spine = neck.parent.InverseTransformPoint(neck.position).normalized;
                var lift = Quaternion.FromToRotation(up0, spine);
                neck.localRotation = Quaternion.Slerp(Quaternion.identity, lift, w) * neck.localRotation;
            }
            if (!TryHeadTurn(out float uprightTurn0, out _)) return;
            // 投球中は収録のひねりでなく、セットで止まっていたときのひねりのまま（首の角度を胴に対して保つ）
            bool haveLook = TryLookTurn(out float lookTurn);
            if (PitcherAtSet)
            {
                _setTurn = rawTurn; _setTurnReady = true;
                if (haveLook && Mathf.Abs(lookTurn) > 10f) { _setTurnRatio = Mathf.Clamp01(rawTurn / lookTurn); _setRatioReady = true; }
            }
            float wantTurn = rawTurn;
            if (_headLookW > 0f && _setTurnReady)
            {
                // 胴→捕手の角度のうちセットと同じ割合だけひねる（顔が背中側へ回らない）
                float holdTurn = haveLook && _setRatioReady ? _setTurnRatio * lookTurn : _setTurn;
                wantTurn = Mathf.LerpAngle(rawTurn, holdTurn, _headLookW);
            }
            float targetTurn = Mathf.LerpAngle(uprightTurn0, wantTurn, w);
            _diagRawTurn = rawTurn; _diagTargetTurn = targetTurn;
            // ひねりは**頭の上方向の軸まわり**で戻す。背骨の軸で回すと、うつむいた頭（切り口で19°）が横へ振られて
            // 傾きが−8°→−14°へ悪化した（2026-11-14 実測）。頭の上方向まわりなら上方向は動かない＝傾き・うつむきは
            // ①のまま保たれる。ひねりは背骨に垂直な面で測るので軸が少しずれる＝2回寄せて合わせる
            for (int iter = 0; iter < 2; iter++)
            {
                if (!TryHeadTurn(out float turnNow, out _)) return;
                var up = neck.parent.InverseTransformDirection(head.TransformDirection(_pitcher.HeadUpLocal));
                float delta = Mathf.DeltaAngle(turnNow, targetTurn);
                neck.localRotation = Quaternion.AngleAxis(delta, up) * neck.localRotation;
            }
        }

        /// <summary>
        /// 頭のひねり[度]＝胴の正面に対する顔の向きを、背骨の軸まわりで測った角度。首の親（胸）のローカル空間で測る。
        /// <paramref name="spine"/> は同じ空間の背骨の軸（胸→首）。
        /// </summary>
        private bool TryHeadTurn(out float turn, out Vector3 spine)
        {
            turn = 0f;
            spine = Vector3.up;
            var anim = _pitcher.Animator;
            var neck = anim.GetBoneTransform(HumanBodyBones.Neck);
            var head = anim.GetBoneTransform(HumanBodyBones.Head);
            var lu = anim.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            var ru = anim.GetBoneTransform(HumanBodyBones.RightUpperArm);
            if (neck == null || head == null || lu == null || ru == null || neck.parent == null) return false;
            var frame = neck.parent;
            spine = frame.InverseTransformPoint(neck.position).normalized;   // 胸の原点→首
            if (spine.sqrMagnitude < 1e-6f) return false;
            var shoulder = Vector3.ProjectOnPlane(
                frame.InverseTransformPoint(ru.position) - frame.InverseTransformPoint(lu.position), spine);
            var chestFwd = Vector3.Cross(shoulder, spine);
            var face = Vector3.ProjectOnPlane(
                frame.InverseTransformDirection(head.TransformDirection(_pitcher.HeadFwdLocal)), spine);
            if (chestFwd.sqrMagnitude < 1e-8f || face.sqrMagnitude < 1e-8f) return false;
            turn = Vector3.SignedAngle(chestFwd, face, spine);
            return true;
        }

        /// <summary>
        /// 顔が捕手を向くときの頭のひねり[度]（<see cref="TryHeadTurn"/> と同じ測り方＝胴の正面に対する、背骨の軸まわりの角度）。
        /// 向く先のワールド位置を首の親のローカル空間へ引くので、左投手（鏡映親）でも同じ式で効く。
        /// </summary>
        private bool TryLookTurn(out float turn)
        {
            turn = 0f;
            if (_catcher == null || _catcher.Go == null) return false;
            var anim = _pitcher.Animator;
            var neck = anim.GetBoneTransform(HumanBodyBones.Neck);
            var head = anim.GetBoneTransform(HumanBodyBones.Head);
            var lu = anim.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            var ru = anim.GetBoneTransform(HumanBodyBones.RightUpperArm);
            if (neck == null || head == null || lu == null || ru == null || neck.parent == null) return false;
            var frame = neck.parent;
            var spine = frame.InverseTransformPoint(neck.position).normalized;
            if (spine.sqrMagnitude < 1e-6f) return false;
            var shoulder = Vector3.ProjectOnPlane(
                frame.InverseTransformPoint(ru.position) - frame.InverseTransformPoint(lu.position), spine);
            var chestFwd = Vector3.Cross(shoulder, spine);
            var target = _catcher.Go.transform.position + new Vector3(0f, HeadLookHeightM, 0f);
            var toTarget = Vector3.ProjectOnPlane(
                frame.InverseTransformPoint(target) - frame.InverseTransformPoint(head.position), spine);
            if (chestFwd.sqrMagnitude < 1e-8f || toTarget.sqrMagnitude < 1e-8f) return false;
            turn = Vector3.SignedAngle(chestFwd, toTarget, spine);
            return true;
        }

        /// <summary>
        /// 静止中（<see cref="Actor.Holding"/>＝生成直後・最後の1球の後）はポーズを評価しないので、補正の効きが
        /// 動いても絵に出ない。効きが前回当てたときから動いていたら、同じ時刻を評価し直して当て直させる
        /// （時刻は進めない＝ルートモーションは乗らない）。
        /// </summary>
        private void RefreshHeldPitcherForSetFixes()
        {
            if (_pitcher == null || !_pitcher.Holding || _pitcher.PoseAdvanced) return;
            if (Mathf.Approximately(_setHeadW, _setHeadApplied) && Mathf.Approximately(_setGloveW, _setGloveApplied)) return;
            EvaluateGraph(_pitcher, 0f);
            _pitcher.PoseAdvanced = true;
        }

        /// <summary>【診断用】首・頭の、投球の切り口とのローカル回転の差[度]（issue #912）。</summary>
        internal string SetHeadDiagForCapture()
        {
            if (!_deliveryHeadReady || _pitcher == null || _pitcher.Animator == null) return "no pitcher";
            var anim = _pitcher.Animator;
            var neck = anim.GetBoneTransform(HumanBodyBones.Neck);
            var head = anim.GetBoneTransform(HumanBodyBones.Head);
            // 胴に対する頭: ひねり（背骨まわり）／傾き（肩の向き）／うつむき（胸の前へ）。首の親のローカルで測る
            string pose = "";
            if (TryHeadTurn(out float turn, out var spine))
            {
                var frame = neck.parent;
                var lu = anim.GetBoneTransform(HumanBodyBones.LeftUpperArm);
                var ru = anim.GetBoneTransform(HumanBodyBones.RightUpperArm);
                var shoulder = Vector3.ProjectOnPlane(
                    frame.InverseTransformPoint(ru.position) - frame.InverseTransformPoint(lu.position), spine).normalized;
                var chestFwd = Vector3.Cross(shoulder, spine).normalized;
                var up = frame.InverseTransformDirection(head.TransformDirection(_pitcher.HeadUpLocal));
                float tilt = Mathf.Atan2(Vector3.Dot(up, shoulder), Vector3.Dot(up, spine)) * Mathf.Rad2Deg;
                float nod = Mathf.Atan2(Vector3.Dot(up, chestFwd), Vector3.Dot(up, spine)) * Mathf.Rad2Deg;
                pose = " raw=" + _diagRawTurn.ToString("F1") + " tgt=" + _diagTargetTurn.ToString("F1") + " turn=" + turn.ToString("F1") + " tilt=" + tilt.ToString("F1") + " nod=" + nod.ToString("F1");
            }
            if (TryLookTurn(out float lookTurn)) pose += " look=" + lookTurn.ToString("F1");
            return "clipT=" + _pitcher.Time.ToString("F2") + " w=" + _setHeadW.ToString("F2")
                + " neckΔ=" + Quaternion.Angle(neck.localRotation, _deliveryNeckLocal).ToString("F1")
                + " headΔ=" + Quaternion.Angle(head.localRotation, _deliveryHeadLocal).ToString("F1") + pose;
        }
    }
}
