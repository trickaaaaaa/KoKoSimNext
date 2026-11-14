using UnityEngine;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// セット姿勢で待つ投手の首（issue #912）。
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

        /// <summary>首の補正の効き（0〜1）。セットで待っている間は1へ、投球へ入ったら0へ、<see cref="CrossfadeSeconds"/>で寄せる。</summary>
        private float _setHeadW;

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
            float target = PitcherAtSet ? 1f : 0f;
            _setHeadW = Mathf.MoveTowards(_setHeadW, target, dt / CrossfadeSeconds);
        }

        /// <summary>
        /// セット姿勢の首・頭を、投球の切り口（1.75秒）と同じ姿勢へ寄せる。ポーズを評価した直後だけ呼ぶ。
        ///
        /// 購入クリップの0秒（セット姿勢）は、真横（三塁側）を向いた胴から本塁を見る収録で、首が切り口より
        /// 首28°・頭39°ずれている（ローカル回転の差。2026-11-14 実測・issue #912）。胴に対しては
        /// 本塁側へ61°ひねって左肩へ8°傾き、12°うつむく（切り口は11°・右へ2.5°・16°）。0.75秒（振りかぶりの
        /// 途中）からは切り口との差が首4°・頭2°以内に収まる＝**曲がって見えるのはセットで待っている間だけ**で、
        /// 投球へ入ると直る。待ちは0秒で静止するので、試合開始直後・打席の頭（采配窓の間）に長く映っていた。
        ///
        /// 補正は「切り口の首・頭をそのまま使う」＝投球へ入る瞬間（0秒→1.75秒のクロスフェード）にも首が振れない。
        /// クリップ自体は改変しない（部位の補正窓。CLAUDE.md の掟）。
        /// </summary>
        private void FixPitcherSetHead()
        {
            _setHeadApplied = _setHeadW;
            if (_setHeadW <= 0f || !_deliveryHeadReady || _pitcher == null || _pitcher.Animator == null) return;
            var anim = _pitcher.Animator;
            var neck = anim.GetBoneTransform(HumanBodyBones.Neck);
            var head = anim.GetBoneTransform(HumanBodyBones.Head);
            if (neck == null || head == null) return;
            float w = _setHeadW * _setHeadW * (3f - 2f * _setHeadW);
            neck.localRotation = Quaternion.Slerp(neck.localRotation, _deliveryNeckLocal, w);
            head.localRotation = Quaternion.Slerp(head.localRotation, _deliveryHeadLocal, w);
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
            return "clipT=" + _pitcher.Time.ToString("F2") + " w=" + _setHeadW.ToString("F2")
                + " neckΔ=" + Quaternion.Angle(neck.localRotation, _deliveryNeckLocal).ToString("F1")
                + " headΔ=" + Quaternion.Angle(head.localRotation, _deliveryHeadLocal).ToString("F1");
        }
    }
}
