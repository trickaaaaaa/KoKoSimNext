using System.Linq;
using KokoSim.Config;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Config;

/// <summary>
/// data/coefficients.yaml → エンジン設定オブジェクトの読込検証（不変条件#1/#4 のデータ駆動を担保）。
/// </summary>
public sealed class CoefficientsLoaderTests
{
    [Fact]
    public void ParsesInlineYaml()
    {
        const string yaml = """
            version: 1
            aerodynamics:
              drag_coefficient: 0.33
              lift_coefficient_per_spin_factor: 0.9
            mound:
              release_to_plate_distance_m: 16.5
            pitching:
              control_to_sigma:
                intercept_cm: 28.0
                slope_cm_per_point: 0.20
                min_sigma_cm: 3.0
            """;

        var bundle = CoefficientsLoader.Parse(yaml);

        Assert.Equal(1, bundle.Version);
        Assert.Equal(0.33, bundle.Aerodynamics.DragCoefficient, 6);
        Assert.Equal(0.9, bundle.Aerodynamics.LiftCoefficientPerSpinFactor, 6);
        Assert.Equal(16.5, bundle.Mound.ReleaseToPlateDistanceM, 6);
        Assert.Equal(28.0, bundle.Pitching.ControlSigmaInterceptCm, 6);
        Assert.Equal(0.20, bundle.Pitching.ControlSigmaSlopeCmPerPoint, 6);
        Assert.Equal(0.03, bundle.Pitching.ControlSigmaMeters(140), 6); // 下限 3cm
    }

    [Fact]
    public void ParsesPersistentRoster()
    {
        const string yaml = """
            version: 1
            persistent_roster:
              pitchers_per_cohort: 4
              freshman_gap: 9.5
              phenom_spike_rate_per_school_year: 0.002
              phenom_main_min: 88
              phenom_support_max: 84
            """;

        var bundle = CoefficientsLoader.Parse(yaml);

        Assert.Equal(4, bundle.PersistentRoster.PitchersPerCohort);
        Assert.Equal(9.5, bundle.PersistentRoster.FreshmanGap, 6);
        Assert.Equal(0.002, bundle.PersistentRoster.Phenom.SpikeRatePerSchoolYear, 6);
        Assert.Equal(88, bundle.PersistentRoster.Phenom.MainMin);
        Assert.Equal(84, bundle.PersistentRoster.Phenom.SupportMax);
    }

    [Fact]
    public void ParsesFieldersChoiceProb()
    {
        const string yaml = """
            version: 1
            baserunning:
              fielders_choice_prob: 0.07
            """;

        var bundle = CoefficientsLoader.Parse(yaml);

        Assert.Equal(0.07, bundle.Baserunning.FieldersChoiceProb, 6);
    }

    [Fact]
    public void ParsesTrainingTimeBudget()
    {
        const string yaml = """
            version: 1
            training:
              reference_week_minutes: 360
              default_budget_minutes: 420
            """;

        var bundle = CoefficientsLoader.Parse(yaml);

        Assert.Equal(360, bundle.Training.ReferenceWeekMinutes);
        Assert.Equal(420, bundle.Training.DefaultBudgetMinutes);
    }

    [Fact]
    public void OmittedSections_FallBackToDefaults()
    {
        var bundle = CoefficientsLoader.Parse("version: 1");

        Assert.Equal(new Aerodynamics().DragCoefficient, bundle.Aerodynamics.DragCoefficient);
        Assert.Equal(new MoundGeometry().ReleaseToPlateDistanceM, bundle.Mound.ReleaseToPlateDistanceM);
    }

    [Fact]
    public void LoadsRepositoryCoefficientsFile()
    {
        var path = FindDataFile("coefficients.yaml");
        var bundle = CoefficientsLoader.LoadFromFile(path);

        Assert.Equal(1, bundle.Version);
        // リポジトリの初期値（設計書02 §1.2）
        Assert.Equal(0.35, bundle.Aerodynamics.DragCoefficient, 6);
        Assert.Equal(16.74, bundle.Mound.ReleaseToPlateDistanceM, 6);
        Assert.Equal(30.0, bundle.Pitching.ControlSigmaInterceptCm, 6);
        Assert.Equal(0.19, bundle.Pitching.ControlSigmaMeters(50), 6);

        // 読み込んだ係数で 145km/h・2200rpm を解くと妥当な弾道になる
        var traj = PitchSimulator.Simulate(
            new PitchSpec { SpeedKmh = 145, SpinRadPerSec = PitchSpec.BackspinFromRpm(2200) },
            bundle.Aerodynamics, bundle.Mound);
        Assert.InRange(traj.InducedVerticalBreakM, 0.30, 0.55);

        // 性格（設計書01 §1.1）: 8タイプが読み込まれ、代表値が反映される。
        var spawnable = bundle.Personalities.Profiles.Where(p => p.SpawnWeight > 0).ToList();
        Assert.Equal(8, spawnable.Count);
        Assert.Equal(1.10, bundle.Personalities.Profile(Personality.HonorStudent).CoachingReceptivity, 6);
        Assert.Equal(-0.06, bundle.Personalities.Profile(Personality.Genius).BuntSuccessBonus, 6);

        // 本塁クロスプレー係数（F2, 設計書12 §3）が YAML から実際に束縛される
        // （IgnoreUnmatchedProperties のため綴り違いは黙って既定値になる＝ここで実値を固定して検出する）。
        Assert.Equal(2.00, bundle.Baserunning.HomeSuccessBias, 6);
        Assert.Equal(0.25, bundle.Baserunning.HomeMarginScale, 6);
        Assert.Equal(32.0, bundle.Baserunning.RelayThrowSpeedMps, 6);
        Assert.Equal(60.0, bundle.Baserunning.CutoffDistanceThresholdM, 6);
        Assert.Equal(0.06, bundle.Baserunning.FieldersChoiceProb, 6); // 野選（FC, design-14 P1-1）帯再校正済み値（2026-07-17）

        // 本塁送り判定の采配閾値（F2, 設計書12 §3/§4）も YAML から束縛される。
        Assert.Equal(0.50, bundle.Tactics.SendHomeMinSuccess, 6);
        Assert.Equal(0.30, bundle.Tactics.SendHomeTwoOutRelax, 6);
        Assert.Equal(0.50, bundle.Tactics.SendHomeAggressionSpan, 6);

        // 練習試合（設計書03 §週ターン③）の係数も YAML から束縛される。
        Assert.Equal(1.0, bundle.PracticeMatch.Cost, 6);
        Assert.Equal(0.80, bundle.PracticeMatch.BaseAccept, 6);
        Assert.Equal(0.20, bundle.PracticeMatch.TierGapPenalty, 6);
        Assert.Equal(0.40, bundle.PracticeMatch.FameWeight, 6);
        Assert.Equal(1.55, bundle.Baserunning.HomeGrounderStartDelaySeconds, 6); // G1 ゴロ走者遅延

        // ライナー併殺（G2, 設計書12 §4）の係数も YAML から束縛される。
        Assert.Equal(1.00, bundle.Baserunning.LinerCommitCapSeconds, 6);
        Assert.Equal(7.25, bundle.Baserunning.LinerReferenceSprintSpeedMps, 6);
        Assert.Equal(0.30, bundle.Baserunning.DoubledOffTransferSeconds, 6);
        Assert.Equal(0.0, bundle.Baserunning.DoubledOffSuccessBias, 6);

        // トランスファーの守備(Fielding)紐づけ（Issue #36, design-02 §1.2）も YAML から束縛される。
        Assert.Equal(0.003, bundle.Fielding.TransferFieldingSlope, 6);
        Assert.Equal(0.15, bundle.Fielding.TransferSecondsFloor, 6);
        Assert.Equal(0.003, bundle.Baserunning.TransferFieldingSlope, 6);
        Assert.Equal(0.15, bundle.Baserunning.TransferSecondsFloor, 6);

        // 守備の読み/ピッチアウト（G3, 設計書12 §5）の係数も YAML から束縛される。
        Assert.Equal(0.42, bundle.Baserunning.StealExpectednessIntercept, 6);
        Assert.Equal(0.50, bundle.Baserunning.StealReadIntercept, 6);
        Assert.Equal(0.55, bundle.Baserunning.MaxPitchoutProb, 6);
        Assert.Equal(0.35, bundle.Baserunning.PitchoutDefenseBonusSeconds, 6);

        // ギャンブル始動の采配（G3b, 設計書12 §5）も YAML から束縛される。
        Assert.Equal(0.82, bundle.Tactics.GambleStartMaxSuccess, 6);
        Assert.Equal(0.40, bundle.Tactics.GambleStartProb, 6);
        Assert.Equal(4, bundle.EnemyAi.GambleStartMinTier);
        Assert.Equal(1.8, bundle.EnemyAi.SmallBallGambleStartFactor, 6);

        // 相手校の調子観測（誤認モデル, issue #47）も YAML から束縛される。
        Assert.Equal(0.65, bundle.Form.ObserveSigmaBase, 6);
        Assert.Equal(-0.0063, bundle.Form.ObserveSigmaPerTalentEye, 6);
        Assert.Equal(0.02, bundle.Form.ObserveSigmaMin, 6);

        // チーム総合力6指標の重み（設計決定 2026-07-18）も YAML から束縛される。
        Assert.Equal(0.28, bundle.TeamStrength.PitchingWeight, 6);
        Assert.Equal(0.24, bundle.TeamStrength.BattingWeight, 6);
        Assert.Equal(0.08, bundle.TeamStrength.MentalWeight, 6);
        Assert.Equal(0.50, bundle.TeamStrength.AceWeight, 6);
        Assert.Equal(9, bundle.TeamStrength.LineupSize);
        Assert.Equal(1.037, bundle.TeamStrength.OverallScale, 6);   // リーグ標準化（③）
        Assert.Equal(-6.6, bundle.TeamStrength.OverallOffset, 6);

        // 試合間の回復モデル（issue #41）も YAML から束縛される。
        Assert.Equal(7.0, bundle.PitchRecovery.FullRecoveryDays, 6);
        Assert.Equal(100.0, bundle.PitchRecovery.ReferencePitches, 6);
        Assert.Equal(0.5, bundle.PitchRecovery.MaxReductionFraction, 6);
    }

    /// <summary>テスト実行ディレクトリから上へ辿って data/ 配下のファイルを探す。</summary>
    private static string FindDataFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "data", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"data/{fileName} がリポジトリ内に見つかりません。");
    }
}
