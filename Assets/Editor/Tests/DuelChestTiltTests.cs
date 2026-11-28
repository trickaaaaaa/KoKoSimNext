using KokoSim.Unity.Match;
using NUnit.Framework;
using UnityEngine;

namespace KokoSim.EditorTests
{
    /// <summary>
    /// 捕手の胸プロテクターの向きが、土台（胸ボーン）の倒れに左右されず背骨の前傾だけで決まること（issue #1791）。
    /// 守備伝令で立って話す待機は胸ボーンが約49°前傾しており、以前は起きた背筋の前で胸当てが首のまわりへ浮いた。
    /// 合成の骨組みで測る＝素体・装備が無い端末でも回る。
    /// </summary>
    public class DuelChestTiltTests
    {
        private GameObject _root;

        [TearDown]
        public void Cleanup()
        {
            if (_root != null) Object.DestroyImmediate(_root);
        }

        private static Transform Node(string name, Transform parent, Vector3 pos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            return go.transform;
        }

        private DuelChestTilt Build(Vector3 neckPos, float chestBonePitchDeg)
        {
            _root = new GameObject("tilt-root");
            var hips = Node("hips", _root.transform, new Vector3(0f, 0.9f, 0f));
            var neck = Node("neck", _root.transform, neckPos);
            var ls = Node("ls", _root.transform, new Vector3(-0.2f, neckPos.y - 0.1f, neckPos.z));
            var rs = Node("rs", _root.transform, new Vector3(0.2f, neckPos.y - 0.1f, neckPos.z));
            var container = Node("container", _root.transform, new Vector3(0f, 1.2f, 0f));
            container.rotation = Quaternion.Euler(chestBonePitchDeg, 0f, 0f);
            var mount = Node("mount", container, container.position);
            var tilt = mount.gameObject.AddComponent<DuelChestTilt>();
            tilt.Hips = hips; tilt.Neck = neck; tilt.LeftShoulder = ls; tilt.RightShoulder = rs;
            tilt.Container = container;
            tilt.MeshTopLocalY = 0.22f;
            return tilt;
        }

        [Test]
        public void UprightSpine_StaysUpright_EvenWhenTheChestBoneIsPitchedForward()
        {
            var tilt = Build(new Vector3(0f, 1.5f, 0f), 49f);
            tilt.Apply();
            Assert.That(Vector3.Angle(tilt.transform.up, Vector3.up), Is.LessThan(1f),
                "立った背筋なのに胸当てが胸ボーンの前傾ぶん倒れている（首のまわりに浮く）");
        }

        [Test]
        public void LeaningSpine_TiltsByTheSpineLean_NotByTheChestBone()
        {
            // 背骨を前へ45°倒す。胸ボーンはほとんど倒れていない（しゃがんだ姿勢）
            var tilt = Build(new Vector3(0f, 0.9f + 0.6f * Mathf.Cos(Mathf.Deg2Rad * 45f), 0.6f * Mathf.Sin(Mathf.Deg2Rad * 45f)), 0.8f);
            tilt.Apply();
            Assert.That(Vector3.Angle(tilt.transform.up, Vector3.up), Is.EqualTo(45f).Within(1.5f));
        }

        [Test]
        public void MeshTop_FollowsTheShoulderLine_InAnyPose()
        {
            var tilt = Build(new Vector3(0f, 1.5f, 0f), 49f);
            tilt.Apply();
            var top = tilt.transform.TransformPoint(new Vector3(0f, tilt.MeshTopLocalY, 0f));
            var shoulder = (tilt.LeftShoulder.position + tilt.RightShoulder.position) * 0.5f + Vector3.up * tilt.ShoulderLift;
            Assert.That(Vector3.Distance(top, shoulder), Is.LessThan(0.005f));
        }
    }
}
