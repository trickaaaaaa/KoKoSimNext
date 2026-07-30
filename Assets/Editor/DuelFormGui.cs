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
            EditorGUILayout.LabelField("スイング面（ど真ん中を打つ高さに）", EditorStyles.miniBoldLabel);
            // 2026-07-31: バットの軌道は「トップ軸→実際の打点」を回す幾何（SwingBatAxis）へ移した。
            // 下の3つはその**後段**に掛かるので、回すと接触の瞬間に芯が打点から外れる（実測0で一致）。
            EditorGUILayout.HelpBox(
                "軌道は打点へ向けて自動で回る（トップ→打点→巻き付き）。下の3つは後段の上乗せなので、"
                + "0以外にすると接触の瞬間に芯が球から外れる。既定は3つとも0。",
                MessageType.None);
            // 符号は幾何で決まる（＋でバレル端が上がる。DuelStageRig.AlignBat の n=Cross(水平axis,up)）。
            // 上限は±90°＝バットを真上／真下まで倒せる（これ以上は意味が反転する）
            cal.SwingPlaneTiltDeg = EditorGUILayout.Slider("バットの傾き（＋バレルが上がる）",
                cal.SwingPlaneTiltDeg, -90f, 90f);
            cal.SwingLiftM = EditorGUILayout.Slider("持ち上げる [m]", cal.SwingLiftM, -0.20f, 0.40f);
            cal.SwingPlaneBatOnly = EditorGUILayout.Slider("バットだけ動かす（0=腕も追う）",
                cal.SwingPlaneBatOnly, 0f, 1f);
            EditorGUILayout.LabelField(" ",
                "スイング中だけ効く。1=腕は収録のまま／傾きは握り点で回る＝手から離れない",
                EditorStyles.miniLabel);
            if (cal.SwingPlaneBatOnly > 0.5f && Mathf.Abs(cal.SwingLiftM) > 0.02f)
            {
                // 平行移動なので、バットだけ動かす設定では必ずこのぶん手から浮く
                EditorGUILayout.LabelField(" ",
                    string.Format("持ち上げはバットが手から{0:F2}m浮く（高さは傾きで作るのが安全）",
                        Mathf.Abs(cal.SwingLiftM)), EditorStyles.miniLabel);
            }

            EditorGUILayout.LabelField("打球の行方（接触〜俯瞰へ渡るまでの一瞬）", EditorStyles.miniBoldLabel);
            bool batted = EditorGUILayout.ToggleLeft("残像の線を出す", cal.BattedShow >= 0.5f);
            cal.BattedShow = batted ? 1f : 0f;
            using (new EditorGUI.DisabledScope(!batted))
            using (new EditorGUI.IndentLevelScope())
            {
                cal.BattedTrailSeconds = EditorGUILayout.Slider("尾の長さ [秒]", cal.BattedTrailSeconds, 0.05f, 0.60f);
                cal.BattedTrailWidthM = EditorGUILayout.Slider("太さ [m]", cal.BattedTrailWidthM, 0.01f, 0.40f);
                cal.BattedTrailAlpha = EditorGUILayout.Slider("濃さ", cal.BattedTrailAlpha, 0f, 1f);
                cal.BattedTrailColor = EditorGUILayout.ColorField("色", cal.BattedTrailColor);
                EditorGUILayout.LabelField(" ", "球は0.10〜0.22秒で枠外へ抜ける。線が残るぶんだけ角度が読める",
                    EditorStyles.miniLabel);
            }

            EditorGUILayout.LabelField("ストライクゾーン", EditorStyles.miniBoldLabel);
            bool zone = EditorGUILayout.ToggleLeft("うっすら出す", cal.ZoneShow >= 0.5f);
            cal.ZoneShow = zone ? 1f : 0f;
            using (new EditorGUI.DisabledScope(!zone))
            using (new EditorGUI.IndentLevelScope())
            {
                cal.ZoneLineAlpha = EditorGUILayout.Slider("枠の濃さ", cal.ZoneLineAlpha, 0f, 1f);
                cal.ZoneFillAlpha = EditorGUILayout.Slider("面の濃さ", cal.ZoneFillAlpha, 0f, 0.5f);
                cal.ZoneLineM = EditorGUILayout.Slider("枠の太さ [m]", cal.ZoneLineM, 0.002f, 0.05f);
                cal.ZoneColor = EditorGUILayout.ColorField("色", cal.ZoneColor);
                EditorGUILayout.LabelField(" ", "矩形の寸法は engine の判定値そのまま（表示では変えない）",
                    EditorStyles.miniLabel);
            }

            EditorGUILayout.LabelField("構え（待機ループ）", EditorStyles.miniBoldLabel);
            bool ownIdle = EditorGUILayout.ToggleLeft(
                "構えだけ自作モーションを使う（スイングは差し替えない）", cal.IdleFromOwnClip >= 0.5f);
            cal.IdleFromOwnClip = ownIdle ? 1f : 0f;
            using (new EditorGUI.DisabledScope(!ownIdle))
            using (new EditorGUI.IndentLevelScope())
            {
                cal.IdleYawDeg = EditorGUILayout.Slider("構えのヨー [度]", cal.IdleYawDeg, 0f, 180f);
                cal.IdleBatAxisFromGrip = EditorGUILayout.Slider("構えのバット軸を握り線から採る",
                    cal.IdleBatAxisFromGrip, 0f, 1f);
                EditorGUILayout.LabelField(" ", "素の向きが約30°違うので立ち向きはクリップ別に持つ",
                    EditorStyles.miniLabel);
            }

            EditorGUILayout.LabelField("立ち向き（スイング）", EditorStyles.miniBoldLabel);
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
            cal.SwingSpeed = EditorGUILayout.Slider("再生速度 [倍]", cal.SwingSpeed, 0.5f, 3f);
            // 振り出し(clip 0.90)→接触(1.12) は 0.22 クリップ秒。実物は 0.15〜0.20s なので 1.1〜1.5 が実寸相当
            EditorGUILayout.LabelField(" ",
                string.Format("振り出し→接触 {0:F3}秒（実物0.15〜0.20秒）／ポーズ12fpsで {1:F1}コマ",
                    0.22f / Mathf.Max(0.1f, cal.SwingSpeed), 0.22f / Mathf.Max(0.1f, cal.SwingSpeed) * 12f),
                EditorStyles.miniLabel);

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
                if (GUILayout.Button("フライ"))
                {
                    rig.OnPitchWindup();
                    rig.OnPitchReleased(0f, DuelPitchOutcome.InPlayContact, null, SampleBatted("センターフライ"));
                }
                if (GUILayout.Button("ゴロ"))
                {
                    rig.OnPitchWindup();
                    rig.OnPitchReleased(0f, DuelPitchOutcome.InPlayContact, null, SampleBatted("ゴロ"));
                }
            }
        }

        /// <summary>
        /// つまみを回しながら打球を確かめるための見本（engine の <c>PlaybackSamples</c> ＝俯瞰のモック
        /// 再生に使っている実データ）。ここで自前の弾道を作ると、絵を追い込む相手が本番と別物になる。
        /// </summary>
        private static KokoSim.Engine.Match.Timeline.Playback.PlaybackBallSegment SampleBatted(string nameContains)
        {
            foreach (var play in KokoSim.Engine.Match.Timeline.Playback.PlaybackSamples.All)
            {
                if (play.Name == null || !play.Name.Contains(nameContains)) continue;
                foreach (var s in play.Ball)
                    if (!(s is KokoSim.Engine.Match.Timeline.Playback.PitchSegment)) return s;
            }
            return null;
        }
    }
}
