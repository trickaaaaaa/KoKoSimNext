#if UNITY_EDITOR
using UnityEngine;

namespace KokoSim.Unity.Match
{
    public sealed partial class DuelStageRig
    {
        /// <summary>打者ヘルメット中心と向き（正面・右）を stage-local で返す（寄りの絵の狙い用）。</summary>
        internal bool BatterHelmetFrameForCapture(out Vector3 center, out Vector3 fwd, out Vector3 right)
            => HelmetFrame(_helmetMount, out center, out fwd, out right);

        /// <summary>走者枠0を stage-local の位置へ立てて有効化し、そのヘルメットの中心と向きを返す。</summary>
        internal bool RunnerHelmetFrameForCapture(Vector3 localPos, out Vector3 center, out Vector3 fwd, out Vector3 right)
        {
            center = fwd = right = Vector3.zero;
            EnsureFieldCast();
            if (_runnerCast.Count == 0 || _runnerHelmetMounts.Count == 0) return false;
            var a = _runnerCast[0];
            a.Go.SetActive(true);
            a.Enabled = true;
            a.Go.transform.position = _root.transform.TransformPoint(localPos);
            StepActor(a, 0.001f);
            return HelmetFrame(_runnerHelmetMounts[0], out center, out fwd, out right);
        }

        /// <summary>
        /// 顔の寄りの狙い用（のっぺらぼうの確認）。0=打者 / 1=投手 / 2=捕手 / 3=走者枠0（RunnerHelmetFrameForCapture で立てた後）。
        /// 中心は頭ボーンから頭蓋の上へ 0.09m、向きは素立ちで実測した顔の前（Actor.HeadFwdLocal）。
        /// </summary>
        internal bool HeadFrameForCapture(int who, out Vector3 center, out Vector3 fwd, out Vector3 up)
        {
            center = fwd = up = Vector3.zero;
            var a = who == 0 ? _batter : who == 1 ? _pitcher : who == 2 ? _catcher
                : (_runnerCast.Count > 0 ? _runnerCast[0] : null);
            if (a == null || a.Animator == null) return false;
            var head = a.Animator.GetBoneTransform(HumanBodyBones.Head);
            if (head == null) return false;
            up = _root.transform.InverseTransformDirection(head.TransformDirection(a.HeadUpLocal)).normalized;
            fwd = _root.transform.InverseTransformDirection(head.TransformDirection(a.HeadFwdLocal)).normalized;
            center = _root.transform.InverseTransformPoint(head.position) + up * 0.09f;
            return true;
        }

        private bool HelmetFrame(Transform mount, out Vector3 center, out Vector3 fwd, out Vector3 right)
        {
            center = fwd = right = Vector3.zero;
            if (mount == null) return false;
            var c = mount.parent;
            center = _root.transform.InverseTransformPoint(c.TransformPoint(HelmetPivot));
            fwd = _root.transform.InverseTransformDirection(c.forward).normalized;
            right = _root.transform.InverseTransformDirection(c.right).normalized;
            return true;
        }
    }
}
#endif
