using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Field;

/// <summary>
/// 送球逸れの回収レース（design-12 §3.8）の純関数。逸れ幅→着弾点→回収ロス（受け手自身 vs バックアップの近い方）。
/// </summary>
public sealed class ThrowRecoveryTests
{
    [Fact]
    public void BackupRole_MatchesRealBaseballConvention()
    {
        Assert.Equal(FieldPosition.RightField, ThrowRecovery.BackupRole(1));
        Assert.Equal(FieldPosition.CenterField, ThrowRecovery.BackupRole(2));
        Assert.Equal(FieldPosition.LeftField, ThrowRecovery.BackupRole(3));
        Assert.Equal(FieldPosition.Pitcher, ThrowRecovery.BackupRole(4));
    }

    [Fact]
    public void LandingPoint_ExtendsBeyondTargetAlongThrowLine()
    {
        var origin = new Vector3D(0, 0, 0);
        var target = new Vector3D(0, 0, 27.43); // 本塁→一塁方向を単純化した直線
        var landing = ThrowRecovery.LandingPoint(origin, target, scatterM: 5.0);

        // 送球方向(+Z)へさらに5m延長した点になる。
        Assert.Equal(0.0, landing.X, 6);
        Assert.Equal(32.43, landing.Z, 6);
        Assert.Equal(5.0, (landing - target).Length, 6);
    }

    [Fact]
    public void RecoveryPenaltySeconds_ZeroScatter_IsJustReactionDelay()
    {
        // 逸れ幅0＝受け手はその場でグラブに収まる。回収ロスは反応遅延だけ。
        var target = new Vector3D(17.0, 0, 24.0);
        var backup = new Vector3D(37.6, 0, 70.6);
        var penalty = ThrowRecovery.RecoveryPenaltySeconds(
            scatterM: 0.0, landingPoint: target, backupPosition: backup,
            selfSprintSpeedMps: 7.0, backupSprintSpeedMps: 7.0, backupThrowSpeedMps: 30.0, reactionSeconds: 0.30);
        Assert.Equal(0.30, penalty, 6);
    }

    [Fact]
    public void RecoveryPenaltySeconds_SmallScatter_SelfRecoveryWins()
    {
        // 受け手はすでに塁に立っているため起点が近く、バックアップ（遠い定位置から走る）より
        // 小さい逸れ幅では必ず速い＝minは受け手側の時間と一致する。
        var scatterM = 0.5;
        var target = new Vector3D(17.0, 0, 24.0);
        var landing = target + new Vector3D(0, 0, scatterM);
        var backup = new Vector3D(37.6, 0, 70.6); // 遠い定位置
        var penalty = ThrowRecovery.RecoveryPenaltySeconds(
            scatterM, landing, backup, selfSprintSpeedMps: 7.0, backupSprintSpeedMps: 7.0,
            backupThrowSpeedMps: 30.0, reactionSeconds: 0.30);
        var selfOnly = scatterM / 7.0 + 0.30;
        Assert.Equal(selfOnly, penalty, 6);
    }

    [Fact]
    public void RecoveryPenaltySeconds_ScatterNearBackupPosition_BackupWinsInstead()
    {
        // 逸れ幅がバックアップ（投手）の定位置あたりまで達すると、バックアップは着弾点のほぼ真横に
        // 立っている＝受け手（逸れ幅ぶん延々と追いかける）より速く処理できる＝minがバックアップ側に切り替わる。
        var target = new Vector3D(0, 0, 0); // 本塁
        var backup = FieldGeometry.StandardPosition(FieldPosition.Pitcher); // 本塁から18.44m
        var scatterM = backup.Z; // 逸れ幅＝投手の定位置あたり
        var landing = target + new Vector3D(0, 0, scatterM);
        var backupPos = new Vector3D(backup.X, 0, backup.Z);
        var penalty = ThrowRecovery.RecoveryPenaltySeconds(
            scatterM, landing, backupPos, selfSprintSpeedMps: 7.0, backupSprintSpeedMps: 7.0,
            backupThrowSpeedMps: 30.0, reactionSeconds: 0.30);
        var selfOnly = scatterM / 7.0 + 0.30;
        Assert.True(penalty < selfOnly, $"逸れ幅が投手の定位置付近なのにバックアップが選ばれていない: penalty={penalty} selfOnly={selfOnly}");
    }

    [Fact]
    public void RecoveryPenaltySeconds_VeryLargeScatter_SelfEventuallyWinsAgain_BecauseBackupMustThrowBack()
    {
        // バックアップは拾った後さらに塁へ返球が要る（距離＝逸れ幅ぶん）。逸れ幅が定位置よりずっと
        // 遠くまで伸びると、受け手（返球不要＝直接タッチ）のほうが結局速くなる。
        var target = new Vector3D(0, 0, 0);
        var backup = FieldGeometry.StandardPosition(FieldPosition.Pitcher);
        var scatterM = 300.0; // 定位置(18.44m)よりはるかに遠く
        var landing = target + new Vector3D(0, 0, scatterM);
        var backupPos = new Vector3D(backup.X, 0, backup.Z);
        var penalty = ThrowRecovery.RecoveryPenaltySeconds(
            scatterM, landing, backupPos, selfSprintSpeedMps: 7.0, backupSprintSpeedMps: 7.0,
            backupThrowSpeedMps: 30.0, reactionSeconds: 0.30);
        var selfOnly = scatterM / 7.0 + 0.30;
        Assert.Equal(selfOnly, penalty, 6);
    }
}
