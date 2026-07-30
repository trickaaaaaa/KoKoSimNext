using KokoSim.Unity.Match;
using UnityEditor;
using UnityEngine;

namespace KokoSim.EditorTools
{
    /// <summary>
    /// 打撃フォーム補正（<see cref="DuelBatterPose"/>）のつまみ描画。
    /// キャリブレーション窓と打撃フォーム確認窓の**両方から同じ物を出す**ための単一実装
    /// （2つに書くと片方だけ項目が増えて食い違う）。
    /// </summary>
    public static class DuelFormGui
    {
        public static void DrawBattingForm(ref DuelCalibration cal)
        {
            EditorGUILayout.LabelField("立ち向き", EditorStyles.miniBoldLabel);
            cal.BatterYawDeg = EditorGUILayout.Slider("ヨー [度]", cal.BatterYawDeg, 0f, 180f);

            EditorGUILayout.LabelField("握り（両手にかかる）", EditorStyles.miniBoldLabel);
            cal.GripGapM = EditorGUILayout.Slider("両手の間隔 [m]", cal.GripGapM, 0.04f, 0.30f);
            cal.GripPullBias = EditorGUILayout.Slider("寄せの配分（−左手を残す／＋右手を残す）",
                cal.GripPullBias, -1f, 1f);

            // 収録の破綻は左右対称でない（手首の折れ 左143°/右155°・拳の向きは65〜72°食い違う）ので
            // 左右別に持つ。上下の呼び名は右打者基準＝ミラー再生（左打者）では入れ替わる
            DrawHand("左手（右打者=下・ノブ側）",
                ref cal.GripElbowPoleDeg, ref cal.GripReachGuard, ref cal.GripReachGuardSwing,
                ref cal.GripWristFlexDeg, ref cal.GripWristFlexStrength,
                ref cal.GripWristTwistDeg, ref cal.GripWristSideDeg, ref cal.GripFingerCurl,
                ref cal.GripForearmRollDeg);
            DrawHand("右手（右打者=上・バレル側）",
                ref cal.GripElbowPoleDegTop, ref cal.GripReachGuardTop, ref cal.GripReachGuardSwingTop,
                ref cal.GripWristFlexDegTop, ref cal.GripWristFlexStrengthTop,
                ref cal.GripWristTwistDegTop, ref cal.GripWristSideDegTop, ref cal.GripFingerCurlTop,
                ref cal.GripForearmRollDegTop);

            EditorGUILayout.LabelField("頭（投手を見続ける）", EditorStyles.miniBoldLabel);
            cal.HeadStrength = EditorGUILayout.Slider("固定の強さ", cal.HeadStrength, 0f, 1f);
            cal.HeadYawOffsetDeg = EditorGUILayout.Slider("狙いのヨー [度]", cal.HeadYawOffsetDeg, -45f, 45f);
            cal.HeadElevDeg = EditorGUILayout.Slider("視線の仰角 [度]", cal.HeadElevDeg, -30f, 30f);
            cal.HeadNeckShare = EditorGUILayout.Slider("首が受け持つ割合", cal.HeadNeckShare, 0f, 1f);
            cal.HeadUntilSec = EditorGUILayout.Slider("見続ける [秒]", cal.HeadUntilSec, 0f, 3.4f);

            EditorGUILayout.LabelField("バット", EditorStyles.miniBoldLabel);
            cal.BatAxisFromGrip = EditorGUILayout.Slider("軸を握り線から採る", cal.BatAxisFromGrip, 0f, 1f);
            cal.BatGripBelowM = EditorGUILayout.Slider("ノブの出し量 [m]", cal.BatGripBelowM, 0f, 0.35f);
            cal.BatLengthM = EditorGUILayout.Slider("全長 [m]", cal.BatLengthM, 0.6f, 1.1f);
            cal.BatHandsApartM = EditorGUILayout.Slider("片手へ移る間隔 [m]", cal.BatHandsApartM, 0.1f, 0.6f);
            using (new EditorGUI.DisabledScope(cal.BatAxisFromGrip >= 0.999f))
            {
                cal.BatStanceElevDeg = EditorGUILayout.Slider("合成軸 構えの仰角 [度]", cal.BatStanceElevDeg, 0f, 90f);
                cal.BatFollowElevDeg = EditorGUILayout.Slider("合成軸 振り抜きの仰角 [度]", cal.BatFollowElevDeg, 0f, 90f);
            }

            EditorGUILayout.LabelField("脚（体を残す）", EditorStyles.miniBoldLabel);
            cal.KneeDeg = EditorGUILayout.Slider("前膝を入れる [度]", cal.KneeDeg, -60f, 60f);
            cal.KneeFromSec = EditorGUILayout.Slider("効き始め [秒]", cal.KneeFromSec, 0f, 3.4f);
            cal.KneeFullSec = EditorGUILayout.Slider("全開 [秒]", cal.KneeFullSec, 0f, 3.4f);
            cal.KneeToSec = EditorGUILayout.Slider("戻し切り [秒]", cal.KneeToSec, 0f, 3.4f);

            EditorGUILayout.LabelField("振りの速さ", EditorStyles.miniBoldLabel);
            cal.SwingSpeed = EditorGUILayout.Slider("再生速度 [倍]", cal.SwingSpeed, 0.5f, 2f);
            EditorGUILayout.LabelField(" ", "ポーズは12fps＝この倍率ぶんコマが減る", EditorStyles.miniLabel);

            EditorGUILayout.LabelField("胴（開きを抑える）", EditorStyles.miniBoldLabel);
            cal.TorsoCloseDeg = EditorGUILayout.Slider("胴を戻す [度]", cal.TorsoCloseDeg, -45f, 45f);
            cal.TorsoFromSec = EditorGUILayout.Slider("効き始め [秒]", cal.TorsoFromSec, 0f, 3.4f);
            cal.TorsoFullSec = EditorGUILayout.Slider("全開 [秒]", cal.TorsoFullSec, 0f, 3.4f);
            cal.TorsoToSec = EditorGUILayout.Slider("戻し切り [秒]", cal.TorsoToSec, 0f, 3.4f);
        }

        /// <summary>片手ぶんのつまみ。左右で同じ並びを出す（片方だけ項目が増えると比べられなくなる）。</summary>
        private static void DrawHand(string title, ref float elbowPole, ref float guard, ref float guardSwing,
            ref float wristFlex, ref float wristStrength, ref float wristTwist, ref float wristSide,
            ref float fingerCurl, ref float forearmRoll)
        {
            EditorGUILayout.LabelField(title, EditorStyles.miniBoldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                elbowPole = EditorGUILayout.Slider("肘の向き [度]", elbowPole, -90f, 90f);
                guard = EditorGUILayout.Slider("腕の伸び上限", guard, 0.7f, 1f);
                guardSwing = EditorGUILayout.Slider("同 スイング中", guardSwing, 0.7f, 1f);
                forearmRoll = EditorGUILayout.Slider("前腕のひねり [度]", forearmRoll, -180f, 180f);
                wristFlex = EditorGUILayout.Slider("手首 曲げ [度]", wristFlex, 90f, 180f);
                wristStrength = EditorGUILayout.Slider("　└ 戻す強さ", wristStrength, 0f, 1f);
                wristTwist = EditorGUILayout.Slider("手首 ひねり [度]", wristTwist, -90f, 90f);
                wristSide = EditorGUILayout.Slider("手首 横振り [度]", wristSide, -60f, 60f);
                fingerCurl = EditorGUILayout.Slider("指の握り込み", fingerCurl, 0f, 1f);
            }
        }

        /// <summary>結果分岐を流すボタン列（構えのループだけでは膝・スイング中の握りが見えないため）。</summary>
        public static void DrawSwingButtons(DuelStageRig rig)
        {
            using (new EditorGUI.DisabledScope(rig == null || !rig.Available))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("構えへ戻す")) rig.OnPitchWindup();
                if (GUILayout.Button("空振り"))
                {
                    rig.OnPitchWindup();
                    rig.OnPitchReleased(0f, DuelPitchOutcome.SwingMiss);
                }
                if (GUILayout.Button("打つ"))
                {
                    rig.OnPitchWindup();
                    rig.OnPitchReleased(0f, DuelPitchOutcome.InPlayContact);
                }
            }
        }
    }
}
