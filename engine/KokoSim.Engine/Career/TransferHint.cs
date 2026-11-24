namespace KokoSim.Engine.Career;

/// <summary>転任判定の前に学校がどう見ているか（信頼度を数字で見せない代わりの匂わせの段。issue #1606）。</summary>
public enum TransferHintTier
{
    Low,
    Mid,
    High,
}

/// <summary>
/// 年度末の転任判定の前に出す校長との会話の出し分け（設計書04 §1.3「判定前の匂わせ」）。
/// 乱数を引かない純関数＝判定の結果にも乱数列にも触れない。
/// </summary>
public static class TransferHint
{
    /// <summary>
    /// 会話を出す年なら信頼度の段、出さない年は null。出すのは「教員監督で、この年度末に残留の抽選がある」年だけ:
    /// 教員であること／総括（3月）で在任年数が1つ増えた時点で最低在任年数に届く／フリー化資格を満たしていない
    /// （満たすと抽選ではなくフリー化の選択になる）。<br/>
    /// 段の切れ目は残留確率のロジスティックの z（<see cref="ManagerTransferModel.RetainZ"/>）で ±1:
    /// 中点±傾き（既定は信頼度 70 以上＝High、50 未満＝Low、間＝Mid）。
    /// </summary>
    public static TransferHintTier? TierFor(Manager manager, CareerCoefficients c)
    {
        if (manager.Status != ManagerStatus.Teacher) return null;
        if (manager.YearsAtSchool + 1 < c.MinTenureYears) return null;
        if (manager.KoshienAppearances >= c.FreeKoshienThreshold || manager.Fame >= c.FreeFameThreshold) return null;

        var z = ManagerTransferModel.RetainZ(manager.Trust, c);
        if (z >= 1.0) return TransferHintTier.High;
        if (z < -1.0) return TransferHintTier.Low;
        return TransferHintTier.Mid;
    }
}
