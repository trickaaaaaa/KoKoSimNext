using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Career;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Players;
using KokoSim.Engine.Practice;
using KokoSim.Engine.Season;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KokoSim.Config;

/// <summary>
/// data/coefficients.yaml をエンジンの設定オブジェクトへ変換した束。
/// batting/fielding セクションは Phase 1 では既定値（設計書02初期値）を用い、調整確定後に YAML へ展開する。
/// </summary>
public sealed record CoefficientsBundle
{
    public required int Version { get; init; }
    public required Aerodynamics Aerodynamics { get; init; }
    public required MoundGeometry Mound { get; init; }
    public required PitchingCoefficients Pitching { get; init; }
    public BattingCoefficients Batting { get; init; } = new();
    public FieldingCoefficients Fielding { get; init; } = new();
    public BaserunningCoefficients Baserunning { get; init; } = new();
    public FatigueCoefficients Fatigue { get; init; } = new();
    public PitchRecoveryCoefficients PitchRecovery { get; init; } = new();
    public KokoSim.Engine.Players.FormCoefficients Form { get; init; } = new();
    public KokoSim.Engine.Players.SkillCoefficients Skills { get; init; } = new();
    public KokoSim.Engine.Players.PersonalityCoefficients Personalities { get; init; } = new();
    public PressureCoefficients Pressure { get; init; } = new();
    public TacticsCoefficients Tactics { get; init; } = new();
    public EnemyAiCoefficients EnemyAi { get; init; } = new();
    public InjuryCoefficients Injury { get; init; } = new();
    public MatchInjuryCoefficients MatchInjury { get; init; } = new();
    public GrowthEventCoefficients Growth { get; init; } = new();
    public InsightCoefficients Insight { get; init; } = new();
    public ManagerGrowthCoefficients ManagerGrowth { get; init; } = new();
    public TrainingCoefficients Training { get; init; } = new();
    public RosterCoefficients Roster { get; init; } = new();
    public PersistentRosterCoefficients PersistentRoster { get; init; } = new();
    public TeamStrengthCoefficients TeamStrength { get; init; } = new();
    public NationCoefficients Nation { get; init; } = new();
    public CareerCoefficients Career { get; init; } = new();
    public TournamentSchedule Tournament { get; init; } = new();
    public PracticeMatchCoefficients PracticeMatch { get; init; } = new();
    /// <summary>施設カタログ（Issue #128）。未指定なら C# 既定（Unity実プレイと同値）。</summary>
    public FacilityCatalog Facilities { get; init; } = FacilityCatalog.Default;
}

/// <summary>
/// data/coefficients.yaml のローダ。IO はこの層に隔離する（不変条件#3）。
/// </summary>
public static class CoefficientsLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static CoefficientsBundle LoadFromFile(string path)
        => Parse(File.ReadAllText(path));

    public static CoefficientsBundle Parse(string yaml)
    {
        var dto = Deserializer.Deserialize<CoefficientsDto>(yaml)
            ?? throw new InvalidDataException("coefficients.yaml が空です。");
        return dto.ToBundle();
    }

    // --- YAML マッピング用 DTO（snake_case） ---

    private sealed class CoefficientsDto
    {
        public int Version { get; set; }
        public AeroDto? Aerodynamics { get; set; }
        public MoundDto? Mound { get; set; }
        public PitchingDto? Pitching { get; set; }
        public BattingDto? Batting { get; set; }
        public FieldingDto? Fielding { get; set; }
        public BaserunningDto? Baserunning { get; set; }
        public FatigueDto? Fatigue { get; set; }
        public PitchRecoveryDto? PitchRecovery { get; set; }
        public FormDto? Form { get; set; }
        public SkillsDto? Skills { get; set; }
        public PersonalitiesDto? Personalities { get; set; }
        public PressureDto? Pressure { get; set; }
        public TacticsDto? Tactics { get; set; }
        public EnemyAiDto? EnemyAi { get; set; }
        public InjuryDto? Injury { get; set; }
        public GrowthDto? Growth { get; set; }
        public InsightDto? Insight { get; set; }
        public ManagerGrowthDto? ManagerGrowth { get; set; }
        public TrainingDto? Training { get; set; }
        public RosterDto? Roster { get; set; }
        public PersistentRosterDto? PersistentRoster { get; set; }
        public TeamStrengthDto? TeamStrength { get; set; }
        public NationDto? Nation { get; set; }
        public CareerDto? Career { get; set; }
        public TournamentDto? Tournament { get; set; }
        public PracticeMatchDto? PracticeMatch { get; set; }
        public List<FacilityDefDto>? Facilities { get; set; }

        public CoefficientsBundle ToBundle() => new()
        {
            Version = Version,
            Aerodynamics = Aerodynamics?.ToModel() ?? new Aerodynamics(),
            Mound = Mound?.ToModel() ?? new MoundGeometry(),
            Pitching = Pitching?.ToModel() ?? new PitchingCoefficients(),
            Batting = Batting?.ToModel() ?? new BattingCoefficients(),
            Fielding = Fielding?.ToModel() ?? new FieldingCoefficients(),
            Baserunning = Baserunning?.ToModel() ?? new BaserunningCoefficients(),
            Fatigue = Fatigue?.ToModel() ?? new FatigueCoefficients(),
            PitchRecovery = PitchRecovery?.ToModel() ?? new PitchRecoveryCoefficients(),
            Form = Form?.ToModel() ?? new KokoSim.Engine.Players.FormCoefficients(),
            Skills = Skills?.ToModel() ?? new KokoSim.Engine.Players.SkillCoefficients(),
            Personalities = Personalities?.ToModel() ?? new KokoSim.Engine.Players.PersonalityCoefficients(),
            Pressure = Pressure?.ToModel() ?? new PressureCoefficients(),
            Tactics = Tactics?.ToModel() ?? new TacticsCoefficients(),
            EnemyAi = EnemyAi?.ToModel() ?? new EnemyAiCoefficients(),
            Injury = Injury?.ToModel() ?? new InjuryCoefficients(),
            MatchInjury = Injury?.ToMatchModel() ?? new MatchInjuryCoefficients(),
            Growth = Growth?.ToModel() ?? new GrowthEventCoefficients(),
            Insight = Insight?.ToModel() ?? new InsightCoefficients(),
            ManagerGrowth = ManagerGrowth?.ToModel() ?? new ManagerGrowthCoefficients(),
            Training = Training?.ToModel() ?? new TrainingCoefficients(),
            Roster = Roster?.ToModel() ?? new RosterCoefficients(),
            PersistentRoster = PersistentRoster?.ToModel() ?? new PersistentRosterCoefficients(),
            TeamStrength = TeamStrength?.ToModel() ?? new TeamStrengthCoefficients(),
            Nation = Nation?.ToModel() ?? new NationCoefficients(),
            Career = Career?.ToModel() ?? new CareerCoefficients(),
            Tournament = Tournament?.ToModel() ?? new TournamentSchedule(),
            PracticeMatch = PracticeMatch?.ToModel() ?? new PracticeMatchCoefficients(),
            Facilities = Facilities is null
                ? FacilityCatalog.Default
                : new FacilityCatalog { Facilities = Facilities.Select(f => f.ToModel()).ToList() },
        };
    }

    private sealed class FacilityDefDto
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public List<FacilityTierDtoNew>? Tiers { get; set; }

        public FacilityDef ToModel() => new()
        {
            Id = Id,
            Name = Name,
            Tiers = Tiers is null
                ? new[] { new FacilityUpgrade() }
                : Tiers.Select(t => t.ToModel()).ToList(),
        };
    }

    private sealed class FacilityTierDtoNew
    {
        public double Cost { get; set; }
        public double CoefAdd { get; set; }
        public int BudgetAdd { get; set; }

        public FacilityUpgrade ToModel() => new() { Cost = Cost, CoefAdd = CoefAdd, BudgetAdd = BudgetAdd };
    }

    private sealed class TournamentDto
    {
        private static readonly TournamentSchedule D = new();
        public int FirstRoundDay { get; set; } = D.FirstRoundDay;
        public int RoundGapDays { get; set; } = D.RoundGapDays;

        public TournamentSchedule ToModel() => new()
        {
            FirstRoundDay = FirstRoundDay,
            RoundGapDays = RoundGapDays,
        };
    }

    private sealed class PracticeMatchDto
    {
        private static readonly PracticeMatchCoefficients D = new();
        public double Cost { get; set; } = D.Cost;
        public double BaseAccept { get; set; } = D.BaseAccept;
        public double TierGapPenalty { get; set; } = D.TierGapPenalty;
        public double FameWeight { get; set; } = D.FameWeight;
        public double MinAccept { get; set; } = D.MinAccept;
        public double MaxAccept { get; set; } = D.MaxAccept;

        public PracticeMatchCoefficients ToModel() => new()
        {
            Cost = Cost,
            BaseAccept = BaseAccept,
            TierGapPenalty = TierGapPenalty,
            FameWeight = FameWeight,
            MinAccept = MinAccept,
            MaxAccept = MaxAccept,
        };
    }

    private sealed class AeroDto
    {
        public double BallMassKg { get; set; } = 0.145;
        public double BallRadiusM { get; set; } = 0.0365;
        public double AirDensity { get; set; } = 1.225;
        public double Gravity { get; set; } = 9.81;
        public double DragCoefficient { get; set; } = 0.35;
        public double LiftCoefficientPerSpinFactor { get; set; } = 0.80;
        public double MaxLiftCoefficient { get; set; } = 0.35;

        public Aerodynamics ToModel() => new()
        {
            BallMassKg = BallMassKg,
            BallRadiusM = BallRadiusM,
            AirDensity = AirDensity,
            Gravity = Gravity,
            DragCoefficient = DragCoefficient,
            LiftCoefficientPerSpinFactor = LiftCoefficientPerSpinFactor,
            MaxLiftCoefficient = MaxLiftCoefficient,
        };
    }

    private sealed class MoundDto
    {
        public double ReleaseToPlateDistanceM { get; set; } = 16.74;

        public MoundGeometry ToModel() => new()
        {
            ReleaseToPlateDistanceM = ReleaseToPlateDistanceM,
        };
    }

    private sealed class PitchingDto
    {
        private static readonly PitchingCoefficients D = new();
        public ControlToSigmaDto? ControlToSigma { get; set; }
        // 死球（HBP, design-14 未決F・2026-07-20）
        public double HbpBodyEdgeM { get; set; } = D.HbpBodyEdgeM;
        public double HbpBodyBottomM { get; set; } = D.HbpBodyBottomM;
        public double HbpBodyTopM { get; set; } = D.HbpBodyTopM;
        public double HbpDodgeProb { get; set; } = D.HbpDodgeProb;
        // 毎球の球速ばらつき（設計書02 §1.1d）
        public double VelocityDropMeanKmh { get; set; } = D.VelocityDropMeanKmh;
        public double VelocityDropSigmaKmh { get; set; } = D.VelocityDropSigmaKmh;
        public double VelocityDropMaxKmh { get; set; } = D.VelocityDropMaxKmh;
        // 配球（Phase 簡略）
        public double StraightShare { get; set; } = D.StraightShare;
        public double CatcherLeadStuffPerPoint { get; set; } = D.CatcherLeadStuffPerPoint;
        // 回転数マッピング（設計書15 §0.1 Q12-5, Phase B観測専用の暫定式）
        public double SpinRpmBase { get; set; } = D.SpinRpmBase;
        public double SpinRpmPerSharpness { get; set; } = D.SpinRpmPerSharpness;
        // 投手ギア（設計書02 §1.1f）
        public double GearPushVelocityBonusKmh { get; set; } = D.GearPushVelocityBonusKmh;
        public double GearPushStaminaFactor { get; set; } = D.GearPushStaminaFactor;
        public double GearCoastVelocityPenaltyKmh { get; set; } = D.GearCoastVelocityPenaltyKmh;
        public double GearCoastStaminaFactor { get; set; } = D.GearCoastStaminaFactor;

        public PitchingCoefficients ToModel()
        {
            var s = ControlToSigma ?? new ControlToSigmaDto();
            return new PitchingCoefficients
            {
                ControlSigmaInterceptCm = s.InterceptCm,
                ControlSigmaSlopeCmPerPoint = s.SlopeCmPerPoint,
                ControlSigmaMinCm = s.MinSigmaCm,
                HbpBodyEdgeM = HbpBodyEdgeM,
                HbpBodyBottomM = HbpBodyBottomM,
                HbpBodyTopM = HbpBodyTopM,
                HbpDodgeProb = HbpDodgeProb,
                VelocityDropMeanKmh = VelocityDropMeanKmh,
                VelocityDropSigmaKmh = VelocityDropSigmaKmh,
                VelocityDropMaxKmh = VelocityDropMaxKmh,
                StraightShare = StraightShare,
                CatcherLeadStuffPerPoint = CatcherLeadStuffPerPoint,
                SpinRpmBase = SpinRpmBase,
                SpinRpmPerSharpness = SpinRpmPerSharpness,
                GearPushVelocityBonusKmh = GearPushVelocityBonusKmh,
                GearPushStaminaFactor = GearPushStaminaFactor,
                GearCoastVelocityPenaltyKmh = GearCoastVelocityPenaltyKmh,
                GearCoastStaminaFactor = GearCoastStaminaFactor,
            };
        }
    }

    private sealed class ControlToSigmaDto
    {
        public double InterceptCm { get; set; } = 30.0;
        public double SlopeCmPerPoint { get; set; } = 0.22;
        public double MinSigmaCm { get; set; } = 2.0;
    }

    private sealed class BattingDto
    {
        private static readonly BattingCoefficients D = new();
        public double AimSigmaXM { get; set; } = D.AimSigmaXMeters;
        public double AimSigmaYM { get; set; } = D.AimSigmaYMeters;
        public double ZoneSwingBase { get; set; } = D.ZoneSwingBase;
        public double ChaseBase { get; set; } = D.ChaseBase;
        public double ChaseDisciplineSlope { get; set; } = D.ChaseDisciplineSlope;
        public double ZoneSwingDisciplineSlope { get; set; } = D.ZoneSwingDisciplineSlope;
        public double ChaseBreakSlope { get; set; } = D.ChaseBreakSlope;
        public double ChaseDistanceSlope { get; set; } = D.ChaseDistanceSlope;
        public double WhiffIntercept { get; set; } = D.WhiffIntercept;
        public double WhiffContactSlope { get; set; } = D.WhiffContactSlope;
        public double WhiffOutOfZonePenalty { get; set; } = D.WhiffOutOfZonePenalty;
        public double WhiffBreakSlope { get; set; } = D.WhiffBreakSlope;
        public double FoulShare { get; set; } = D.FoulShare;
        public double StuffBaseVelocityKmh { get; set; } = D.StuffBaseVelocityKmh;
        public double StuffPerKmh { get; set; } = D.StuffPerKmh;
        public double ExitVeloInterceptKmh { get; set; } = D.ExitVeloInterceptKmh;
        public double ExitVeloPerPower { get; set; } = D.ExitVeloPerPower;
        public double QualityMean { get; set; } = D.QualityMean;
        public double QualityContactSlope { get; set; } = D.QualityContactSlope;
        public double QualitySigma { get; set; } = D.QualitySigma;
        public double LaunchAngleAtLt1 { get; set; } = D.LaunchAngleAtLt1;
        public double LaunchAngleAtLt100 { get; set; } = D.LaunchAngleAtLt100;
        public double LaunchAngleSigma { get; set; } = D.LaunchAngleSigma;
        public double BearingSigma { get; set; } = D.BearingSigma;
        public double MinQualityVeloFactor { get; set; } = D.MinQualityVeloFactor;
        public double ContactQualityVelocityRefKmh { get; set; } = D.ContactQualityVelocityRefKmh;
        public double ContactQualityPerKmh { get; set; } = D.ContactQualityPerKmh;
        public double ContactQualityPerControl { get; set; } = D.ContactQualityPerControl;
        public double ContactQualityBreakRefM { get; set; } = D.ContactQualityBreakRefM;
        public double ContactQualityPerBreakM { get; set; } = D.ContactQualityPerBreakM;

        public BattingCoefficients ToModel() => new()
        {
            AimSigmaXMeters = AimSigmaXM,
            AimSigmaYMeters = AimSigmaYM,
            ZoneSwingBase = ZoneSwingBase,
            ChaseBase = ChaseBase,
            ChaseDisciplineSlope = ChaseDisciplineSlope,
            ZoneSwingDisciplineSlope = ZoneSwingDisciplineSlope,
            ChaseBreakSlope = ChaseBreakSlope,
            ChaseDistanceSlope = ChaseDistanceSlope,
            WhiffIntercept = WhiffIntercept,
            WhiffContactSlope = WhiffContactSlope,
            WhiffOutOfZonePenalty = WhiffOutOfZonePenalty,
            WhiffBreakSlope = WhiffBreakSlope,
            FoulShare = FoulShare,
            StuffBaseVelocityKmh = StuffBaseVelocityKmh,
            StuffPerKmh = StuffPerKmh,
            ExitVeloInterceptKmh = ExitVeloInterceptKmh,
            ExitVeloPerPower = ExitVeloPerPower,
            QualityMean = QualityMean,
            QualityContactSlope = QualityContactSlope,
            QualitySigma = QualitySigma,
            LaunchAngleAtLt1 = LaunchAngleAtLt1,
            LaunchAngleAtLt100 = LaunchAngleAtLt100,
            LaunchAngleSigma = LaunchAngleSigma,
            BearingSigma = BearingSigma,
            MinQualityVeloFactor = MinQualityVeloFactor,
            ContactQualityVelocityRefKmh = ContactQualityVelocityRefKmh,
            ContactQualityPerKmh = ContactQualityPerKmh,
            ContactQualityPerControl = ContactQualityPerControl,
            ContactQualityBreakRefM = ContactQualityBreakRefM,
            ContactQualityPerBreakM = ContactQualityPerBreakM,
        };
    }

    private sealed class FieldingDto
    {
        private static readonly FieldingCoefficients D = new();
        public double FlyApexThresholdM { get; set; } = D.FlyApexThresholdM;
        public double CatchReachFactor { get; set; } = D.CatchReachFactor;
        public double ThrowTransferSeconds { get; set; } = D.ThrowTransferSeconds;
        public double ThrowTransferFieldingSlope { get; set; } = D.ThrowTransferFieldingSlope;
        public double ThrowTransferFactorMin { get; set; } = D.ThrowTransferFactorMin;
        public double InfieldPlayOverheadSeconds { get; set; } = D.InfieldPlayOverheadSeconds;
        public double RunnerReactionSeconds { get; set; } = D.RunnerReactionSeconds;
        public double LeftBatterFirstStepBonusSeconds { get; set; } = D.LeftBatterFirstStepBonusSeconds;
        public double InfieldDepthM { get; set; } = D.InfieldDepthM;
        public double ForceOutMarginSeconds { get; set; } = D.ForceOutMarginSeconds;
        public double ErrorBaseProb { get; set; } = D.ErrorBaseProb;
        public double ErrorCatchingSlope { get; set; } = D.ErrorCatchingSlope;
        public double ErrorCatchingSlopeStrong { get; set; } = D.ErrorCatchingSlopeStrong;
        public double ErrorMinProb { get; set; } = D.ErrorMinProb;
        public double ErrorMaxProb { get; set; } = D.ErrorMaxProb;
        public double ThrowErrorBaseProb { get; set; } = D.ThrowErrorBaseProb;
        public double ThrowErrorAccuracySlope { get; set; } = D.ThrowErrorAccuracySlope;
        public double CatchReachFieldingSlope { get; set; } = D.CatchReachFieldingSlope;
        public double CatchReachCapSeconds { get; set; } = D.CatchReachCapSeconds;
        public double RollDecelMps2 { get; set; } = D.RollDecelMps2;
        public double RollRetentionFlat { get; set; } = D.RollRetentionFlat;
        public double RollRetentionSteep { get; set; } = D.RollRetentionSteep;
        public double FenceCaromSeconds { get; set; } = D.FenceCaromSeconds;
        public double OutfieldPickupSeconds { get; set; } = D.OutfieldPickupSeconds;
        public double ThrowDistanceDragPerM { get; set; } = D.ThrowDistanceDragPerM;
        public double RunningTopSpeedFactor { get; set; } = D.RunningTopSpeedFactor;
        public double BaseTurnSeconds { get; set; } = D.BaseTurnSeconds;
        public double ExtraBaseMarginSeconds { get; set; } = D.ExtraBaseMarginSeconds;
        public double AptitudeNeutral { get; set; } = D.AptitudeNeutral;
        public double AptitudeSlopePerPoint { get; set; } = D.AptitudeSlopePerPoint;
        public double AptitudeFactorMin { get; set; } = D.AptitudeFactorMin;
        public double AptitudeFactorMax { get; set; } = D.AptitudeFactorMax;

        public FieldingCoefficients ToModel() => new()
        {
            FlyApexThresholdM = FlyApexThresholdM,
            CatchReachFactor = CatchReachFactor,
            ThrowTransferSeconds = ThrowTransferSeconds,
            ThrowTransferFieldingSlope = ThrowTransferFieldingSlope,
            ThrowTransferFactorMin = ThrowTransferFactorMin,
            InfieldPlayOverheadSeconds = InfieldPlayOverheadSeconds,
            RunnerReactionSeconds = RunnerReactionSeconds,
            LeftBatterFirstStepBonusSeconds = LeftBatterFirstStepBonusSeconds,
            InfieldDepthM = InfieldDepthM,
            ForceOutMarginSeconds = ForceOutMarginSeconds,
            ErrorBaseProb = ErrorBaseProb,
            ErrorCatchingSlope = ErrorCatchingSlope,
            ErrorCatchingSlopeStrong = ErrorCatchingSlopeStrong,
            ErrorMinProb = ErrorMinProb,
            ErrorMaxProb = ErrorMaxProb,
            ThrowErrorBaseProb = ThrowErrorBaseProb,
            ThrowErrorAccuracySlope = ThrowErrorAccuracySlope,
            CatchReachFieldingSlope = CatchReachFieldingSlope,
            CatchReachCapSeconds = CatchReachCapSeconds,
            RollDecelMps2 = RollDecelMps2,
            RollRetentionFlat = RollRetentionFlat,
            RollRetentionSteep = RollRetentionSteep,
            FenceCaromSeconds = FenceCaromSeconds,
            OutfieldPickupSeconds = OutfieldPickupSeconds,
            ThrowDistanceDragPerM = ThrowDistanceDragPerM,
            RunningTopSpeedFactor = RunningTopSpeedFactor,
            BaseTurnSeconds = BaseTurnSeconds,
            ExtraBaseMarginSeconds = ExtraBaseMarginSeconds,
            AptitudeNeutral = AptitudeNeutral,
            AptitudeSlopePerPoint = AptitudeSlopePerPoint,
            AptitudeFactorMin = AptitudeFactorMin,
            AptitudeFactorMax = AptitudeFactorMax,
        };
    }

    private sealed class BaserunningDto
    {
        private static readonly BaserunningCoefficients D = new();
        public double FirstToThirdOnSingle { get; set; } = D.FirstToThirdOnSingle;
        public double SecondToHomeOnSingle { get; set; } = D.SecondToHomeOnSingle;
        public double FirstToHomeOnDouble { get; set; } = D.FirstToHomeOnDouble;
        public double SpeedSlope { get; set; } = D.SpeedSlope;
        public double SacFlyScoreProb { get; set; } = D.SacFlyScoreProb;
        public double ProductiveOutAdvanceProb { get; set; } = D.ProductiveOutAdvanceProb;
        public double DoublePlayProb { get; set; } = D.DoublePlayProb;
        public double FieldersChoiceProb { get; set; } = D.FieldersChoiceProb;
        public double ErrorExtraAdvanceProb { get; set; } = D.ErrorExtraAdvanceProb;
        public double ErrorExtraAdvanceAccuracySlope { get; set; } = D.ErrorExtraAdvanceAccuracySlope;
        public double DropThirdStrikeReachProb { get; set; } = D.DropThirdStrikeReachProb;
        public double DropThirdStrikeCatchingSlope { get; set; } = D.DropThirdStrikeCatchingSlope;
        // 暴投・パスボール（design-14 P2-8, 設計書15 Phase D-3）
        public double WildPitchProb { get; set; } = D.WildPitchProb;
        public double WildPitchControlSlope { get; set; } = D.WildPitchControlSlope;
        public double PassedBallProb { get; set; } = D.PassedBallProb;
        public double PassedBallCatchingSlope { get; set; } = D.PassedBallCatchingSlope;
        // 盗塁（設計書02 §4.2）
        public double StealLeadDistanceM { get; set; } = D.StealLeadDistanceM;
        public double CatchThrowDistanceM { get; set; } = D.CatchThrowDistanceM;
        public double StealReactionIntercept { get; set; } = D.StealReactionIntercept;
        public double StealReactionSlope { get; set; } = D.StealReactionSlope;
        public double PitcherQuickSeconds { get; set; } = D.PitcherQuickSeconds;
        public double PopTransferSeconds { get; set; } = D.PopTransferSeconds;
        public double TransferFieldingSlope { get; set; } = D.TransferFieldingSlope;
        public double TransferFactorMin { get; set; } = D.TransferFactorMin;
        public double TagSeconds { get; set; } = D.TagSeconds;
        public double StealMarginScale { get; set; } = D.StealMarginScale;
        public double StealSuccessBias { get; set; } = D.StealSuccessBias;
        public double CatchThrowToThirdDistanceM { get; set; } = D.CatchThrowToThirdDistanceM;
        public double StealThirdSuccessBias { get; set; } = D.StealThirdSuccessBias;
        public double StealHomeSuccessBias { get; set; } = D.StealHomeSuccessBias;
        public double DoubleStealThirdBreakProb { get; set; } = D.DoubleStealThirdBreakProb;
        public double PickoffBaseProb { get; set; } = D.PickoffBaseProb;
        public double PickoffRunnerLeadSlope { get; set; } = D.PickoffRunnerLeadSlope;
        public double PickoffPitcherSenseSlope { get; set; } = D.PickoffPitcherSenseSlope;
        public double PickoffMaxProb { get; set; } = D.PickoffMaxProb;
        // バント（設計書02 §4.3）
        public double BuntBase { get; set; } = D.BuntBase;
        public double BuntSkillSlope { get; set; } = D.BuntSkillSlope;
        public double BuntVelocityRefKmh { get; set; } = D.BuntVelocityRefKmh;
        public double BuntVelocityPenaltyPerKmh { get; set; } = D.BuntVelocityPenaltyPerKmh;
        public double BuntMissShare { get; set; } = D.BuntMissShare;
        public double BuntFoulShare { get; set; } = D.BuntFoulShare;
        public double BuntFoulSkillSlope { get; set; } = D.BuntFoulSkillSlope;
        public double BuntFoulFloor { get; set; } = D.BuntFoulFloor;
        public double BuntPopShare { get; set; } = D.BuntPopShare;
        public double BuntBaseDistanceM { get; set; } = D.BuntBaseDistanceM;
        public double BuntRunnerReactionSeconds { get; set; } = D.BuntRunnerReactionSeconds;
        public double BuntLeftFirstStepBonusSeconds { get; set; } = D.BuntLeftFirstStepBonusSeconds;
        public double SacrificeBuntSquareDelaySeconds { get; set; } = D.SacrificeBuntSquareDelaySeconds;
        public double SafetyBuntSquareDelaySeconds { get; set; } = D.SafetyBuntSquareDelaySeconds;
        public double BuntFieldThrowBaseSeconds { get; set; } = D.BuntFieldThrowBaseSeconds;
        public double BuntPlacementSlope { get; set; } = D.BuntPlacementSlope;
        public double BuntInfieldHitTimeScale { get; set; } = D.BuntInfieldHitTimeScale;
        // 本塁クロスプレー（バックホーム憤死, 設計書12 §3, F2）
        public double HomeRunnerReactionIntercept { get; set; } = D.HomeRunnerReactionIntercept;
        public double HomeRunnerReactionSlope { get; set; } = D.HomeRunnerReactionSlope;
        public double HomeLeadDistanceM { get; set; } = D.HomeLeadDistanceM;
        public double OutfieldTransferSeconds { get; set; } = D.OutfieldTransferSeconds;
        public double RelayTransferSeconds { get; set; } = D.RelayTransferSeconds;
        public double RelayThrowSpeedMps { get; set; } = D.RelayThrowSpeedMps;
        public double CutoffFractionFromHome { get; set; } = D.CutoffFractionFromHome;
        public double CutoffDistanceThresholdM { get; set; } = D.CutoffDistanceThresholdM;
        public double HomeTagSeconds { get; set; } = D.HomeTagSeconds;
        public double HomeMarginScale { get; set; } = D.HomeMarginScale;
        public double HomeSuccessBias { get; set; } = D.HomeSuccessBias;
        public double HomeGrounderStartDelaySeconds { get; set; } = D.HomeGrounderStartDelaySeconds;
        // 三塁への進塁レース（単打の一塁→三塁, Issue #89）
        public double ThirdTagSeconds { get; set; } = D.ThirdTagSeconds;
        public double ThirdMarginScale { get; set; } = D.ThirdMarginScale;
        public double ThirdSuccessBias { get; set; } = D.ThirdSuccessBias;
        // 犠飛のタッチアップ（Issue #90）
        public double TagUpMarginScale { get; set; } = D.TagUpMarginScale;
        public double TagUpSuccessBias { get; set; } = D.TagUpSuccessBias;
        public double CloseCallMarginSeconds { get; set; } = D.CloseCallMarginSeconds;
        // ライナー併殺（設計書12 §4, G2）
        public double LinerBreakReactionSeconds { get; set; } = D.LinerBreakReactionSeconds;
        public double LinerCommitCapSeconds { get; set; } = D.LinerCommitCapSeconds;
        public double LinerReferenceSprintSpeedMps { get; set; } = D.LinerReferenceSprintSpeedMps;
        public double DoubledOffReverseSeconds { get; set; } = D.DoubledOffReverseSeconds;
        public double DoubledOffTransferSeconds { get; set; } = D.DoubledOffTransferSeconds;
        public double DoubledOffTagSeconds { get; set; } = D.DoubledOffTagSeconds;
        public double DoubledOffMarginScale { get; set; } = D.DoubledOffMarginScale;
        public double DoubledOffSuccessBias { get; set; } = D.DoubledOffSuccessBias;
        // 守備の読み/ピッチアウト（設計書09 §1, 設計書12 §5, G3）
        public double StealExpectednessIntercept { get; set; } = D.StealExpectednessIntercept;
        public double StealExpectednessStealSlope { get; set; } = D.StealExpectednessStealSlope;
        public double GambleUnexpectednessReduction { get; set; } = D.GambleUnexpectednessReduction;
        public double StealReadIntercept { get; set; } = D.StealReadIntercept;
        public double StealReadCatcherLeadSlope { get; set; } = D.StealReadCatcherLeadSlope;
        public double StealReadPitcherSenseSlope { get; set; } = D.StealReadPitcherSenseSlope;
        public double MaxPitchoutProb { get; set; } = D.MaxPitchoutProb;
        public double PitchoutDefenseBonusSeconds { get; set; } = D.PitchoutDefenseBonusSeconds;
        public double GamblePitchoutExtraBonusSeconds { get; set; } = D.GamblePitchoutExtraBonusSeconds;
        public double GambleJumpBonusSeconds { get; set; } = D.GambleJumpBonusSeconds;

        public BaserunningCoefficients ToModel() => new()
        {
            FirstToThirdOnSingle = FirstToThirdOnSingle,
            SecondToHomeOnSingle = SecondToHomeOnSingle,
            FirstToHomeOnDouble = FirstToHomeOnDouble,
            SpeedSlope = SpeedSlope,
            SacFlyScoreProb = SacFlyScoreProb,
            ProductiveOutAdvanceProb = ProductiveOutAdvanceProb,
            DoublePlayProb = DoublePlayProb,
            FieldersChoiceProb = FieldersChoiceProb,
            ErrorExtraAdvanceProb = ErrorExtraAdvanceProb,
            ErrorExtraAdvanceAccuracySlope = ErrorExtraAdvanceAccuracySlope,
            DropThirdStrikeReachProb = DropThirdStrikeReachProb,
            DropThirdStrikeCatchingSlope = DropThirdStrikeCatchingSlope,
            WildPitchProb = WildPitchProb,
            WildPitchControlSlope = WildPitchControlSlope,
            PassedBallProb = PassedBallProb,
            PassedBallCatchingSlope = PassedBallCatchingSlope,
            StealLeadDistanceM = StealLeadDistanceM,
            CatchThrowDistanceM = CatchThrowDistanceM,
            StealReactionIntercept = StealReactionIntercept,
            StealReactionSlope = StealReactionSlope,
            PitcherQuickSeconds = PitcherQuickSeconds,
            PopTransferSeconds = PopTransferSeconds,
            TransferFieldingSlope = TransferFieldingSlope,
            TransferFactorMin = TransferFactorMin,
            TagSeconds = TagSeconds,
            StealMarginScale = StealMarginScale,
            StealSuccessBias = StealSuccessBias,
            CatchThrowToThirdDistanceM = CatchThrowToThirdDistanceM,
            StealThirdSuccessBias = StealThirdSuccessBias,
            StealHomeSuccessBias = StealHomeSuccessBias,
            DoubleStealThirdBreakProb = DoubleStealThirdBreakProb,
            PickoffBaseProb = PickoffBaseProb,
            PickoffRunnerLeadSlope = PickoffRunnerLeadSlope,
            PickoffPitcherSenseSlope = PickoffPitcherSenseSlope,
            PickoffMaxProb = PickoffMaxProb,
            BuntBase = BuntBase,
            BuntSkillSlope = BuntSkillSlope,
            BuntVelocityRefKmh = BuntVelocityRefKmh,
            BuntVelocityPenaltyPerKmh = BuntVelocityPenaltyPerKmh,
            BuntMissShare = BuntMissShare,
            BuntFoulShare = BuntFoulShare,
            BuntFoulSkillSlope = BuntFoulSkillSlope,
            BuntFoulFloor = BuntFoulFloor,
            BuntPopShare = BuntPopShare,
            BuntBaseDistanceM = BuntBaseDistanceM,
            BuntRunnerReactionSeconds = BuntRunnerReactionSeconds,
            BuntLeftFirstStepBonusSeconds = BuntLeftFirstStepBonusSeconds,
            SacrificeBuntSquareDelaySeconds = SacrificeBuntSquareDelaySeconds,
            SafetyBuntSquareDelaySeconds = SafetyBuntSquareDelaySeconds,
            BuntFieldThrowBaseSeconds = BuntFieldThrowBaseSeconds,
            BuntPlacementSlope = BuntPlacementSlope,
            BuntInfieldHitTimeScale = BuntInfieldHitTimeScale,
            HomeRunnerReactionIntercept = HomeRunnerReactionIntercept,
            HomeRunnerReactionSlope = HomeRunnerReactionSlope,
            HomeLeadDistanceM = HomeLeadDistanceM,
            OutfieldTransferSeconds = OutfieldTransferSeconds,
            RelayTransferSeconds = RelayTransferSeconds,
            RelayThrowSpeedMps = RelayThrowSpeedMps,
            CutoffFractionFromHome = CutoffFractionFromHome,
            CutoffDistanceThresholdM = CutoffDistanceThresholdM,
            HomeTagSeconds = HomeTagSeconds,
            HomeMarginScale = HomeMarginScale,
            HomeSuccessBias = HomeSuccessBias,
            HomeGrounderStartDelaySeconds = HomeGrounderStartDelaySeconds,
            ThirdTagSeconds = ThirdTagSeconds,
            ThirdMarginScale = ThirdMarginScale,
            ThirdSuccessBias = ThirdSuccessBias,
            TagUpMarginScale = TagUpMarginScale,
            TagUpSuccessBias = TagUpSuccessBias,
            CloseCallMarginSeconds = CloseCallMarginSeconds,
            LinerBreakReactionSeconds = LinerBreakReactionSeconds,
            LinerCommitCapSeconds = LinerCommitCapSeconds,
            LinerReferenceSprintSpeedMps = LinerReferenceSprintSpeedMps,
            DoubledOffReverseSeconds = DoubledOffReverseSeconds,
            DoubledOffTransferSeconds = DoubledOffTransferSeconds,
            DoubledOffTagSeconds = DoubledOffTagSeconds,
            DoubledOffMarginScale = DoubledOffMarginScale,
            DoubledOffSuccessBias = DoubledOffSuccessBias,
            StealExpectednessIntercept = StealExpectednessIntercept,
            StealExpectednessStealSlope = StealExpectednessStealSlope,
            GambleUnexpectednessReduction = GambleUnexpectednessReduction,
            StealReadIntercept = StealReadIntercept,
            StealReadCatcherLeadSlope = StealReadCatcherLeadSlope,
            StealReadPitcherSenseSlope = StealReadPitcherSenseSlope,
            MaxPitchoutProb = MaxPitchoutProb,
            PitchoutDefenseBonusSeconds = PitchoutDefenseBonusSeconds,
            GamblePitchoutExtraBonusSeconds = GamblePitchoutExtraBonusSeconds,
            GambleJumpBonusSeconds = GambleJumpBonusSeconds,
        };
    }

    private sealed class FatigueDto
    {
        private static readonly FatigueCoefficients D = new();
        public double VelocityDropPerOverPitch { get; set; } = D.VelocityDropPerOverPitch;
        public double ControlDropPerOverPitch { get; set; } = D.ControlDropPerOverPitch;
        public double RelievePitchMargin { get; set; } = D.RelievePitchMargin;
        public double HardCapPitches { get; set; } = D.HardCapPitches;

        public FatigueCoefficients ToModel() => new()
        {
            VelocityDropPerOverPitch = VelocityDropPerOverPitch,
            ControlDropPerOverPitch = ControlDropPerOverPitch,
            RelievePitchMargin = RelievePitchMargin,
            HardCapPitches = HardCapPitches,
        };
    }

    private sealed class PitchRecoveryDto
    {
        private static readonly PitchRecoveryCoefficients D = new();
        public double FullRecoveryDays { get; set; } = D.FullRecoveryDays;
        public double ReferencePitches { get; set; } = D.ReferencePitches;
        public double MaxReductionFraction { get; set; } = D.MaxReductionFraction;

        public PitchRecoveryCoefficients ToModel() => new()
        {
            FullRecoveryDays = FullRecoveryDays,
            ReferencePitches = ReferencePitches,
            MaxReductionFraction = MaxReductionFraction,
        };
    }

    private sealed class FormDto
    {
        private static readonly KokoSim.Engine.Players.FormCoefficients D = new();
        public double ContactPerStep { get; set; } = D.ContactPerStep;
        public double PowerPerStep { get; set; } = D.PowerPerStep;
        public double ControlPerStep { get; set; } = D.ControlPerStep;
        public double VelocityPerStepKmh { get; set; } = D.VelocityPerStepKmh;
        public double SharpnessPerStep { get; set; } = D.SharpnessPerStep;
        public double DayFormBaseSigma { get; set; } = D.DayFormBaseSigma;
        public double DayFormClamp { get; set; } = D.DayFormClamp;
        public double DayFormSpikeProb { get; set; } = D.DayFormSpikeProb;
        public double DayFormSpikeMin { get; set; } = D.DayFormSpikeMin;
        public double DayFormSpikeMax { get; set; } = D.DayFormSpikeMax;
        public double ContactPerDayForm { get; set; } = D.ContactPerDayForm;
        public double PowerPerDayForm { get; set; } = D.PowerPerDayForm;
        public double ControlPerDayForm { get; set; } = D.ControlPerDayForm;
        public double VelocityPerDayFormKmh { get; set; } = D.VelocityPerDayFormKmh;
        public double SharpnessPerDayForm { get; set; } = D.SharpnessPerDayForm;
        public double WeeklyPersistence { get; set; } = D.WeeklyPersistence;
        public double WeeklySigma { get; set; } = D.WeeklySigma;
        public double ObserveSigmaBase { get; set; } = D.ObserveSigmaBase;
        public double ObserveSigmaPerTalentEye { get; set; } = D.ObserveSigmaPerTalentEye;
        public double ObserveSigmaMin { get; set; } = D.ObserveSigmaMin;
        public double MatchHitBonus { get; set; } = D.MatchHitBonus;
        public double MatchHomeRunBonus { get; set; } = D.MatchHomeRunBonus;
        public double MatchHitlessMinAtBats { get; set; } = D.MatchHitlessMinAtBats;
        public double MatchHitlessPenalty { get; set; } = D.MatchHitlessPenalty;
        public double MatchQualityStartMinOuts { get; set; } = D.MatchQualityStartMinOuts;
        public double MatchQualityStartMaxRuns { get; set; } = D.MatchQualityStartMaxRuns;
        public double MatchQualityStartBonus { get; set; } = D.MatchQualityStartBonus;
        public double MatchRockedRunsThreshold { get; set; } = D.MatchRockedRunsThreshold;
        public double MatchRockedPenalty { get; set; } = D.MatchRockedPenalty;
        public double MatchBlowoutLossMargin { get; set; } = D.MatchBlowoutLossMargin;
        public double MatchBlowoutLossPenalty { get; set; } = D.MatchBlowoutLossPenalty;

        public KokoSim.Engine.Players.FormCoefficients ToModel() => new()
        {
            ContactPerStep = ContactPerStep,
            PowerPerStep = PowerPerStep,
            ControlPerStep = ControlPerStep,
            VelocityPerStepKmh = VelocityPerStepKmh,
            SharpnessPerStep = SharpnessPerStep,
            DayFormBaseSigma = DayFormBaseSigma,
            DayFormClamp = DayFormClamp,
            DayFormSpikeProb = DayFormSpikeProb,
            DayFormSpikeMin = DayFormSpikeMin,
            DayFormSpikeMax = DayFormSpikeMax,
            ContactPerDayForm = ContactPerDayForm,
            PowerPerDayForm = PowerPerDayForm,
            ControlPerDayForm = ControlPerDayForm,
            VelocityPerDayFormKmh = VelocityPerDayFormKmh,
            SharpnessPerDayForm = SharpnessPerDayForm,
            WeeklyPersistence = WeeklyPersistence,
            WeeklySigma = WeeklySigma,
            ObserveSigmaBase = ObserveSigmaBase,
            ObserveSigmaPerTalentEye = ObserveSigmaPerTalentEye,
            ObserveSigmaMin = ObserveSigmaMin,
            MatchHitBonus = MatchHitBonus,
            MatchHomeRunBonus = MatchHomeRunBonus,
            MatchHitlessMinAtBats = MatchHitlessMinAtBats,
            MatchHitlessPenalty = MatchHitlessPenalty,
            MatchQualityStartMinOuts = MatchQualityStartMinOuts,
            MatchQualityStartMaxRuns = MatchQualityStartMaxRuns,
            MatchQualityStartBonus = MatchQualityStartBonus,
            MatchRockedRunsThreshold = MatchRockedRunsThreshold,
            MatchRockedPenalty = MatchRockedPenalty,
            MatchBlowoutLossMargin = MatchBlowoutLossMargin,
            MatchBlowoutLossPenalty = MatchBlowoutLossPenalty,
        };
    }

    private sealed class InjuryDto
    {
        private static readonly InjuryCoefficients D = new();
        public double WeeklyBaseProb { get; set; } = D.WeeklyBaseProb;
        public double ResistanceSlope { get; set; } = D.ResistanceSlope;
        public double MinorShare { get; set; } = D.MinorShare;
        public double ModerateShare { get; set; } = D.ModerateShare;
        public int MinorRecoveryWeeks { get; set; } = D.MinorRecoveryWeeks;
        public int ModerateRecoveryWeeks { get; set; } = D.ModerateRecoveryWeeks;
        public int SevereRecoveryWeeks { get; set; } = D.SevereRecoveryWeeks;
        public double MinorPerformanceFactor { get; set; } = D.MinorPerformanceFactor;
        public double ModeratePerformanceFactor { get; set; } = D.ModeratePerformanceFactor;
        public double SeverePerformanceFactor { get; set; } = D.SeverePerformanceFactor;
        public double PlayThroughWorsenProb { get; set; } = D.PlayThroughWorsenProb;
        public int WorsenExtraWeeks { get; set; } = D.WorsenExtraWeeks;
        public int PlayThroughExtraWeeks { get; set; } = D.PlayThroughExtraWeeks;
        public double MedicalRecoveryFactor { get; set; } = D.MedicalRecoveryFactor;

        // --- 試合中の場面駆動（同じ injury: ブロックから MatchInjuryCoefficients も組む） ---
        private static readonly MatchInjuryCoefficients M = new();
        public double MatchHitByPitchProb { get; set; } = M.HitByPitchProb;
        public double MatchHomeCollisionProb { get; set; } = M.HomeCollisionProb;
        public double MatchHomeCollisionCatcherShare { get; set; } = M.HomeCollisionCatcherShare;
        public double MatchFenceCrashProb { get; set; } = M.FenceCrashProb;
        public double MatchFenceCrashMarginM { get; set; } = M.FenceCrashMarginM;
        public double MatchSlidingProb { get; set; } = M.SlidingProb;
        public double MatchOveruseProb { get; set; } = M.OveruseProb;
        public double MatchOveruseOverPitches { get; set; } = M.OveruseOverPitches;

        /// <summary>怪我耐性の掛け方は週次と共通（resistance_slope を共有する）。</summary>
        public MatchInjuryCoefficients ToMatchModel() => new()
        {
            HitByPitchProb = MatchHitByPitchProb,
            HomeCollisionProb = MatchHomeCollisionProb,
            HomeCollisionCatcherShare = MatchHomeCollisionCatcherShare,
            FenceCrashProb = MatchFenceCrashProb,
            FenceCrashMarginM = MatchFenceCrashMarginM,
            SlidingProb = MatchSlidingProb,
            OveruseProb = MatchOveruseProb,
            OveruseOverPitches = MatchOveruseOverPitches,
            ResistanceSlope = ResistanceSlope,
        };

        public InjuryCoefficients ToModel() => new()
        {
            WeeklyBaseProb = WeeklyBaseProb,
            ResistanceSlope = ResistanceSlope,
            MinorShare = MinorShare,
            ModerateShare = ModerateShare,
            MinorRecoveryWeeks = MinorRecoveryWeeks,
            ModerateRecoveryWeeks = ModerateRecoveryWeeks,
            SevereRecoveryWeeks = SevereRecoveryWeeks,
            MinorPerformanceFactor = MinorPerformanceFactor,
            ModeratePerformanceFactor = ModeratePerformanceFactor,
            SeverePerformanceFactor = SeverePerformanceFactor,
            PlayThroughWorsenProb = PlayThroughWorsenProb,
            WorsenExtraWeeks = WorsenExtraWeeks,
            PlayThroughExtraWeeks = PlayThroughExtraWeeks,
            MedicalRecoveryFactor = MedicalRecoveryFactor,
        };
    }

    private sealed class GrowthDto
    {
        private static readonly GrowthEventCoefficients D = new();
        public double AwakeningWeeklyProb { get; set; } = D.AwakeningWeeklyProb;
        public double AwakeningGrowthThreshold { get; set; } = D.AwakeningGrowthThreshold;
        public double AwakeningConditionMin { get; set; } = D.AwakeningConditionMin;
        public int AwakeningGain { get; set; } = D.AwakeningGain;
        public double AwakeningConditionBoost { get; set; } = D.AwakeningConditionBoost;
        public double BreakthroughWeeklyProb { get; set; } = D.BreakthroughWeeklyProb;
        public int BreakthroughGain { get; set; } = D.BreakthroughGain;
        public double SlumpWeeklyProb { get; set; } = D.SlumpWeeklyProb;
        public double SlumpConditionMax { get; set; } = D.SlumpConditionMax;
        public int SlumpWeeksMin { get; set; } = D.SlumpWeeksMin;
        public int SlumpWeeksMax { get; set; } = D.SlumpWeeksMax;
        public double SlumpPerformanceFactor { get; set; } = D.SlumpPerformanceFactor;
        public double SlumpConditionDrop { get; set; } = D.SlumpConditionDrop;
        public double PlateauCapProximity { get; set; } = D.PlateauCapProximity;
        public double PlateauWeeklyProb { get; set; } = D.PlateauWeeklyProb;
        public double PlateauGrowthFactor { get; set; } = D.PlateauGrowthFactor;
        public double YipsWeeklyProb { get; set; } = D.YipsWeeklyProb;
        public int YipsAbilityDrop { get; set; } = D.YipsAbilityDrop;
        public double YipsOvercomeWeeklyProb { get; set; } = D.YipsOvercomeWeeklyProb;

        public GrowthEventCoefficients ToModel() => new()
        {
            AwakeningWeeklyProb = AwakeningWeeklyProb,
            AwakeningGrowthThreshold = AwakeningGrowthThreshold,
            AwakeningConditionMin = AwakeningConditionMin,
            AwakeningGain = AwakeningGain,
            AwakeningConditionBoost = AwakeningConditionBoost,
            BreakthroughWeeklyProb = BreakthroughWeeklyProb,
            BreakthroughGain = BreakthroughGain,
            SlumpWeeklyProb = SlumpWeeklyProb,
            SlumpConditionMax = SlumpConditionMax,
            SlumpWeeksMin = SlumpWeeksMin,
            SlumpWeeksMax = SlumpWeeksMax,
            SlumpPerformanceFactor = SlumpPerformanceFactor,
            SlumpConditionDrop = SlumpConditionDrop,
            PlateauCapProximity = PlateauCapProximity,
            PlateauWeeklyProb = PlateauWeeklyProb,
            PlateauGrowthFactor = PlateauGrowthFactor,
            YipsWeeklyProb = YipsWeeklyProb,
            YipsAbilityDrop = YipsAbilityDrop,
            YipsOvercomeWeeklyProb = YipsOvercomeWeeklyProb,
        };
    }

    private sealed class InsightDto
    {
        private static readonly InsightCoefficients D = new();
        public double BaseWeeklyProb { get; set; } = D.BaseWeeklyProb;
        public double ProbPerTalentEye { get; set; } = D.ProbPerTalentEye;
        public double AccuracyBase { get; set; } = D.AccuracyBase;
        public double AccuracyPerTalentEye { get; set; } = D.AccuracyPerTalentEye;
        public double AccuracyMax { get; set; } = D.AccuracyMax;

        public InsightCoefficients ToModel() => new()
        {
            BaseWeeklyProb = BaseWeeklyProb,
            ProbPerTalentEye = ProbPerTalentEye,
            AccuracyBase = AccuracyBase,
            AccuracyPerTalentEye = AccuracyPerTalentEye,
            AccuracyMax = AccuracyMax,
        };
    }

    private sealed class ManagerGrowthDto
    {
        private static readonly ManagerGrowthCoefficients D = new();
        public double KoshienTacticalBoost { get; set; } = D.KoshienTacticalBoost;
        public double FamousRivalProb { get; set; } = D.FamousRivalProb;
        public double FamousRivalBoost { get; set; } = D.FamousRivalBoost;
        public double BigLossProb { get; set; } = D.BigLossProb;
        public double BigLossBoost { get; set; } = D.BigLossBoost;
        public double SeminarProb { get; set; } = D.SeminarProb;
        public double SeminarBoost { get; set; } = D.SeminarBoost;
        public double SeminarCost { get; set; } = D.SeminarCost;
        public double ProObVisitBaseProb { get; set; } = D.ProObVisitBaseProb;
        public double ProObVisitFameSlope { get; set; } = D.ProObVisitFameSlope;
        public double ProObVisitBoost { get; set; } = D.ProObVisitBoost;
        public double MentorProb { get; set; } = D.MentorProb;
        public double MentorBoost { get; set; } = D.MentorBoost;
        public double Cap { get; set; } = D.Cap;

        public ManagerGrowthCoefficients ToModel() => new()
        {
            KoshienTacticalBoost = KoshienTacticalBoost,
            FamousRivalProb = FamousRivalProb,
            FamousRivalBoost = FamousRivalBoost,
            BigLossProb = BigLossProb,
            BigLossBoost = BigLossBoost,
            SeminarProb = SeminarProb,
            SeminarBoost = SeminarBoost,
            SeminarCost = SeminarCost,
            ProObVisitBaseProb = ProObVisitBaseProb,
            ProObVisitFameSlope = ProObVisitFameSlope,
            ProObVisitBoost = ProObVisitBoost,
            MentorProb = MentorProb,
            MentorBoost = MentorBoost,
            Cap = Cap,
        };
    }

    private sealed class TrainingDto
    {
        private static readonly TrainingCoefficients D = new();
        public double BaseMainExp { get; set; } = D.BaseMainExp;
        public double SubFactor { get; set; } = D.SubFactor;
        public double FacilityCoef { get; set; } = D.FacilityCoef;
        public double CoachingLevel { get; set; } = D.CoachingLevel;
        public double CoachingSlope { get; set; } = D.CoachingSlope;
        public int IndividualCoachingSlots { get; set; } = D.IndividualCoachingSlots;
        public double IndividualCoachingBonusScale { get; set; } = D.IndividualCoachingBonusScale;
        public List<FacilityTierDto>? FacilityTiers { get; set; }
        public double LevelUpBase { get; set; } = D.LevelUpBase;
        public double LevelUpGrowth { get; set; } = D.LevelUpGrowth;
        public double AptitudeRequiredExpFactor { get; set; } = D.AptitudeRequiredExpFactor;
        public TrainabilityDto? Trainability { get; set; }
        public double SummerCampMult { get; set; } = D.SummerCampMult;
        public double WinterCampMult { get; set; } = D.WinterCampMult;
        public double TournamentPracticeMult { get; set; } = D.TournamentPracticeMult;
        public int ReferenceWeekMinutes { get; set; } = D.ReferenceWeekMinutes;
        public int DefaultBudgetMinutes { get; set; } = D.DefaultBudgetMinutes;
        public double MatchMentalExp { get; set; } = D.MatchMentalExp;
        public double MatchLeadExp { get; set; } = D.MatchLeadExp;
        public double MatchBaserunningExp { get; set; } = D.MatchBaserunningExp;

        public TrainingCoefficients ToModel() => new()
        {
            BaseMainExp = BaseMainExp,
            SubFactor = SubFactor,
            FacilityCoef = FacilityCoef,
            CoachingLevel = CoachingLevel,
            CoachingSlope = CoachingSlope,
            IndividualCoachingSlots = IndividualCoachingSlots,
            IndividualCoachingBonusScale = IndividualCoachingBonusScale,
            FacilityTiers = FacilityTiers is null
                ? new TrainingCoefficients().FacilityTiers
                : FacilityTiers.Select(t => t.ToModel()).ToList(),
            LevelUpBase = LevelUpBase,
            LevelUpGrowth = LevelUpGrowth,
            AptitudeRequiredExpFactor = AptitudeRequiredExpFactor,
            Trainability = Trainability?.ToModel() ?? new TrainabilityCoefficients(),
            SummerCampMult = SummerCampMult,
            WinterCampMult = WinterCampMult,
            TournamentPracticeMult = TournamentPracticeMult,
            ReferenceWeekMinutes = ReferenceWeekMinutes,
            DefaultBudgetMinutes = DefaultBudgetMinutes,
            MatchMentalExp = MatchMentalExp,
            MatchLeadExp = MatchLeadExp,
            MatchBaserunningExp = MatchBaserunningExp,
        };
    }

    private sealed class FacilityTierDto
    {
        private static readonly FacilityTier D = new();
        public double Coef { get; set; } = D.Coef;
        public int BudgetMinutes { get; set; } = D.BudgetMinutes;

        public FacilityTier ToModel() => new() { Coef = Coef, BudgetMinutes = BudgetMinutes };
    }

    private sealed class TrainabilityDto
    {
        private static readonly TrainabilityCoefficients D = new();
        public double Contact { get; set; } = D.Contact;
        public double Power { get; set; } = D.Power;
        public double LaunchTendency { get; set; } = D.LaunchTendency;
        public double Discipline { get; set; } = D.Discipline;
        public double Speed { get; set; } = D.Speed;
        public double ArmStrength { get; set; } = D.ArmStrength;
        public double Fielding { get; set; } = D.Fielding;
        public double Catching { get; set; } = D.Catching;
        public double Velocity { get; set; } = D.Velocity;
        public double Control { get; set; } = D.Control;
        public double Stamina { get; set; } = D.Stamina;
        public double PitchRank { get; set; } = D.PitchRank;
        public double Bunt { get; set; } = D.Bunt;
        public double Steal { get; set; } = D.Steal;
        public double Baserunning { get; set; } = D.Baserunning;
        public double ThrowAccuracy { get; set; } = D.ThrowAccuracy;

        public TrainabilityCoefficients ToModel() => new()
        {
            Contact = Contact,
            Power = Power,
            LaunchTendency = LaunchTendency,
            Discipline = Discipline,
            Speed = Speed,
            ArmStrength = ArmStrength,
            Fielding = Fielding,
            Catching = Catching,
            Velocity = Velocity,
            Control = Control,
            Stamina = Stamina,
            PitchRank = PitchRank,
            Bunt = Bunt,
            Steal = Steal,
            Baserunning = Baserunning,
            ThrowAccuracy = ThrowAccuracy,
        };
    }

    private sealed class SkillsDto
    {
        private static readonly KokoSim.Engine.Players.SkillCoefficients D = new();
        public double SlowStarterBatContactPerPa { get; set; } = D.SlowStarterBatContactPerPa;
        public double SlowStarterBatMaxBonus { get; set; } = D.SlowStarterBatMaxBonus;
        public double SprayBearingFactor { get; set; } = D.SprayBearingFactor;
        public double FirstPitchSwingProb { get; set; } = D.FirstPitchSwingProb;
        public double GrinderFoulFactor { get; set; } = D.GrinderFoulFactor;
        public double StreakyVarianceFactor { get; set; } = D.StreakyVarianceFactor;
        public double SlowStarterPitchControlPerBf { get; set; } = D.SlowStarterPitchControlPerBf;
        public double SlowStarterPitchMaxBonus { get; set; } = D.SlowStarterPitchMaxBonus;
        public int SecondTimeThroughBattersFaced { get; set; } = D.SecondTimeThroughBattersFaced;
        public double SecondTimeThroughControlPenalty { get; set; } = D.SecondTimeThroughControlPenalty;
        public double SecondTimeThroughStuffPenalty { get; set; } = D.SecondTimeThroughStuffPenalty;
        public double EffectivelyWildControlPenalty { get; set; } = D.EffectivelyWildControlPenalty;
        public double EffectivelyWildStuffBonus { get; set; } = D.EffectivelyWildStuffBonus;
        public double DeceptiveBallStuffBonus { get; set; } = D.DeceptiveBallStuffBonus;
        public double DoublePlayArtistBonus { get; set; } = D.DoublePlayArtistBonus;
        public int MasterCatcherLeadBonus { get; set; } = D.MasterCatcherLeadBonus;
        public double SpiritualPillarCaptainFactor { get; set; } = D.SpiritualPillarCaptainFactor;
        public double DiligentExpFactor { get; set; } = D.DiligentExpFactor;
        public double LazyExpFactor { get; set; } = D.LazyExpFactor;
        public double DurableInjuryFactor { get; set; } = D.DurableInjuryFactor;
        public double InjuryProneInjuryFactor { get; set; } = D.InjuryProneInjuryFactor;
        public double MonsterContactBonus { get; set; } = D.MonsterContactBonus;
        public double MonsterPowerBonus { get; set; } = D.MonsterPowerBonus;
        public double MonsterControlBonus { get; set; } = D.MonsterControlBonus;
        public double MonsterStuffBonus { get; set; } = D.MonsterStuffBonus;
        public int MaxSkillsPerPlayer { get; set; } = D.MaxSkillsPerPlayer;
        public double CommonSkillProb { get; set; } = D.CommonSkillProb;
        public double HiddenShare { get; set; } = D.HiddenShare;
        public double MarqueeSkillProb { get; set; } = D.MarqueeSkillProb;
        public int PillarLeadershipThreshold { get; set; } = D.PillarLeadershipThreshold;
        public double PillarBonusProb { get; set; } = D.PillarBonusProb;

        public KokoSim.Engine.Players.SkillCoefficients ToModel() => new()
        {
            SlowStarterBatContactPerPa = SlowStarterBatContactPerPa,
            SlowStarterBatMaxBonus = SlowStarterBatMaxBonus,
            SprayBearingFactor = SprayBearingFactor,
            FirstPitchSwingProb = FirstPitchSwingProb,
            GrinderFoulFactor = GrinderFoulFactor,
            StreakyVarianceFactor = StreakyVarianceFactor,
            SlowStarterPitchControlPerBf = SlowStarterPitchControlPerBf,
            SlowStarterPitchMaxBonus = SlowStarterPitchMaxBonus,
            SecondTimeThroughBattersFaced = SecondTimeThroughBattersFaced,
            SecondTimeThroughControlPenalty = SecondTimeThroughControlPenalty,
            SecondTimeThroughStuffPenalty = SecondTimeThroughStuffPenalty,
            EffectivelyWildControlPenalty = EffectivelyWildControlPenalty,
            EffectivelyWildStuffBonus = EffectivelyWildStuffBonus,
            DeceptiveBallStuffBonus = DeceptiveBallStuffBonus,
            DoublePlayArtistBonus = DoublePlayArtistBonus,
            MasterCatcherLeadBonus = MasterCatcherLeadBonus,
            SpiritualPillarCaptainFactor = SpiritualPillarCaptainFactor,
            DiligentExpFactor = DiligentExpFactor,
            LazyExpFactor = LazyExpFactor,
            DurableInjuryFactor = DurableInjuryFactor,
            InjuryProneInjuryFactor = InjuryProneInjuryFactor,
            MonsterContactBonus = MonsterContactBonus,
            MonsterPowerBonus = MonsterPowerBonus,
            MonsterControlBonus = MonsterControlBonus,
            MonsterStuffBonus = MonsterStuffBonus,
            MaxSkillsPerPlayer = MaxSkillsPerPlayer,
            CommonSkillProb = CommonSkillProb,
            HiddenShare = HiddenShare,
            MarqueeSkillProb = MarqueeSkillProb,
            PillarLeadershipThreshold = PillarLeadershipThreshold,
            PillarBonusProb = PillarBonusProb,
        };
    }

    private sealed class PersonalitiesDto
    {
        private static readonly KokoSim.Engine.Players.PersonalityCoefficients D = new();
        public double FactorJitterSd { get; set; } = D.FactorJitterSd;
        public double FactorMin { get; set; } = D.FactorMin;
        public double FactorMax { get; set; } = D.FactorMax;
        public List<PersonalityProfileDto>? Profiles { get; set; }

        public KokoSim.Engine.Players.PersonalityCoefficients ToModel()
        {
            // profiles 未指定なら既定表を維持（部分上書きは全置換方式: 定義するなら全タイプ列挙する）。
            var profiles = Profiles is { Count: > 0 }
                ? Profiles.Select(x => x.ToModel()).ToList()
                : new KokoSim.Engine.Players.PersonalityCoefficients().Profiles;
            return new KokoSim.Engine.Players.PersonalityCoefficients
            {
                FactorJitterSd = FactorJitterSd,
                FactorMin = FactorMin,
                FactorMax = FactorMax,
                Profiles = profiles,
            };
        }
    }

    private sealed class PersonalityProfileDto
    {
        public string Type { get; set; } = "Normal";
        public double SpawnWeight { get; set; }
        public double CoachingReceptivity { get; set; } = 1.0;
        public double SelfGrowthFactor { get; set; } = 1.0;
        public double BuntSuccessBonus { get; set; }
        public double ChanceHitFactor { get; set; } = 1.0;
        public double LeadershipMeanOffset { get; set; }

        public KokoSim.Engine.Players.PersonalityProfile ToModel() => new()
        {
            Type = Enum.TryParse<KokoSim.Engine.Players.Personality>(Type, ignoreCase: true, out var t)
                ? t : KokoSim.Engine.Players.Personality.Normal,
            SpawnWeight = SpawnWeight,
            CoachingReceptivity = CoachingReceptivity,
            SelfGrowthFactor = SelfGrowthFactor,
            BuntSuccessBonus = BuntSuccessBonus,
            ChanceHitFactor = ChanceHitFactor,
            LeadershipMeanOffset = LeadershipMeanOffset,
        };
    }

    private sealed class PressureDto
    {
        private static readonly PressureCoefficients D = new();
        public int LateInningFrom { get; set; } = D.LateInningFrom;
        public int CloseScoreDiff { get; set; } = D.CloseScoreDiff;
        public int RispPoint { get; set; } = D.RispPoint;
        public int BasesLoadedPoint { get; set; } = D.BasesLoadedPoint;
        public int RetirementPoint { get; set; } = D.RetirementPoint;
        public int MaxIndex { get; set; } = D.MaxIndex;
        public double MultiplierSlope { get; set; } = D.MultiplierSlope;
        public double FatigueNegativeAmplify { get; set; } = D.FatigueNegativeAmplify;

        public PressureCoefficients ToModel() => new()
        {
            LateInningFrom = LateInningFrom,
            CloseScoreDiff = CloseScoreDiff,
            RispPoint = RispPoint,
            BasesLoadedPoint = BasesLoadedPoint,
            RetirementPoint = RetirementPoint,
            MaxIndex = MaxIndex,
            MultiplierSlope = MultiplierSlope,
            FatigueNegativeAmplify = FatigueNegativeAmplify,
        };
    }

    private sealed class TacticsDto
    {
        private static readonly TacticsCoefficients D = new();
        // 攻撃サイン判断
        public int SacBuntFromInning { get; set; } = D.SacBuntFromInning;
        public double SacBuntProb { get; set; } = D.SacBuntProb;
        public double SacBuntTieBreakProb { get; set; } = D.SacBuntTieBreakProb;
        public int SacBuntMaxPower { get; set; } = D.SacBuntMaxPower;
        public int SacBuntMinSkill { get; set; } = D.SacBuntMinSkill;
        public int SacBuntMaxBehind { get; set; } = D.SacBuntMaxBehind;
        public int SacBuntMaxAhead { get; set; } = D.SacBuntMaxAhead;
        public int SqueezeFromInning { get; set; } = D.SqueezeFromInning;
        public double SqueezeProb { get; set; } = D.SqueezeProb;
        public int SqueezeMaxDiffAbs { get; set; } = D.SqueezeMaxDiffAbs;
        public int SqueezeMinBunt { get; set; } = D.SqueezeMinBunt;
        public double StealMinSuccess { get; set; } = D.StealMinSuccess;
        public double StealProb { get; set; } = D.StealProb;
        // 三盗・本盗（issue #67）
        public double StealThirdMinSuccess { get; set; } = D.StealThirdMinSuccess;
        public double StealThirdProb { get; set; } = D.StealThirdProb;
        public int StealThirdMaxOuts { get; set; } = D.StealThirdMaxOuts;
        public int StealThirdMaxDiffAbs { get; set; } = D.StealThirdMaxDiffAbs;
        public double StealHomeMinSuccess { get; set; } = D.StealHomeMinSuccess;
        public double StealHomeProb { get; set; } = D.StealHomeProb;
        public int StealHomeMaxOuts { get; set; } = D.StealHomeMaxOuts;
        public int StealHomeMaxDiffAbs { get; set; } = D.StealHomeMaxDiffAbs;
        public double HitAndRunProb { get; set; } = D.HitAndRunProb;
        public int HitAndRunMinContact { get; set; } = D.HitAndRunMinContact;
        public int HitAndRunMaxPower { get; set; } = D.HitAndRunMaxPower;
        public double GambleStartMaxSuccess { get; set; } = D.GambleStartMaxSuccess;
        public double GambleStartProb { get; set; } = D.GambleStartProb;
        public int TakeMaxControl { get; set; } = D.TakeMaxControl;
        public double TakeProb { get; set; } = D.TakeProb;
        public double SendHomeMinSuccess { get; set; } = D.SendHomeMinSuccess;
        public double SendHomeTwoOutRelax { get; set; } = D.SendHomeTwoOutRelax;
        public double SendHomeAggressionSpan { get; set; } = D.SendHomeAggressionSpan;
        // 三塁への送り判定（単打の一塁→三塁, Issue #89）
        public double SendThirdMinSuccess { get; set; } = D.SendThirdMinSuccess;
        public double SendThirdTwoOutRelax { get; set; } = D.SendThirdTwoOutRelax;
        public double SendThirdAggressionSpan { get; set; } = D.SendThirdAggressionSpan;
        // 守備指示判断
        public double BuntShiftProb { get; set; } = D.BuntShiftProb;
        public int InfieldInFromInning { get; set; } = D.InfieldInFromInning;
        public int InfieldInMaxLead { get; set; } = D.InfieldInMaxLead;
        public int OutfieldDeepMinPower { get; set; } = D.OutfieldDeepMinPower;
        public int ControlFirstMaxControl { get; set; } = D.ControlFirstMaxControl;
        public int KeepLowMinPower { get; set; } = D.KeepLowMinPower;
        public int GearPushInningsLeft { get; set; } = D.GearPushInningsLeft;
        public int GearPushMaxDiffAbs { get; set; } = D.GearPushMaxDiffAbs;
        public int GearCoastMinLead { get; set; } = D.GearCoastMinLead;
        // 敬遠（design-14 P1-3）
        public int IntentionalWalkMinPower { get; set; } = D.IntentionalWalkMinPower;
        public int IntentionalWalkFromInning { get; set; } = D.IntentionalWalkFromInning;
        public int IntentionalWalkMaxDiffAbs { get; set; } = D.IntentionalWalkMaxDiffAbs;
        public double IntentionalWalkProb { get; set; } = D.IntentionalWalkProb;
        // 伝令判断
        public int DefenseTimeoutMinPressure { get; set; } = D.DefenseTimeoutMinPressure;
        public int OffenseTimeoutMinPressure { get; set; } = D.OffenseTimeoutMinPressure;
        public int OffenseTimeoutMaxMental { get; set; } = D.OffenseTimeoutMaxMental;
        // 選手交代判断（設計書09 §6, C-2）
        public int PinchHitFromInning { get; set; } = D.PinchHitFromInning;
        public int PinchHitContactCeiling { get; set; } = D.PinchHitContactCeiling;
        public int PinchHitImprovement { get; set; } = D.PinchHitImprovement;
        public int PinchHitMinDiff { get; set; } = D.PinchHitMinDiff;
        public int PinchHitMaxDiff { get; set; } = D.PinchHitMaxDiff;
        public int PinchRunFromInning { get; set; } = D.PinchRunFromInning;
        public int PinchRunSpeedCeiling { get; set; } = D.PinchRunSpeedCeiling;
        public int PinchRunImprovement { get; set; } = D.PinchRunImprovement;
        public int PinchRunMinDiff { get; set; } = D.PinchRunMinDiff;
        public int DefensiveSubFromInning { get; set; } = D.DefensiveSubFromInning;
        public int DefensiveSubMinLead { get; set; } = D.DefensiveSubMinLead;
        public int DefensiveSubFieldingCeiling { get; set; } = D.DefensiveSubFieldingCeiling;
        public int DefensiveSubImprovement { get; set; } = D.DefensiveSubImprovement;
        // サイン効果
        public double HitAndRunContactBoost { get; set; } = D.HitAndRunContactBoost;
        public double HitAndRunPowerPenalty { get; set; } = D.HitAndRunPowerPenalty;
        public double HitAndRunLaunchPenalty { get; set; } = D.HitAndRunLaunchPenalty;
        public double HitAndRunCaughtPenalty { get; set; } = D.HitAndRunCaughtPenalty;
        public double BusterVsShiftBonus { get; set; } = D.BusterVsShiftBonus;
        public double BusterPenalty { get; set; } = D.BusterPenalty;
        public double SqueezeReadBase { get; set; } = D.SqueezeReadBase;
        public double SqueezeReadPerLead { get; set; } = D.SqueezeReadPerLead;
        public double SqueezeReadObviousBonus { get; set; } = D.SqueezeReadObviousBonus;
        // 陣形の初期守備位置
        public double InfieldInFactor { get; set; } = D.InfieldInFactor;
        public double InfieldDeepFactor { get; set; } = D.InfieldDeepFactor;
        public double OutfieldInFactor { get; set; } = D.OutfieldInFactor;
        public double OutfieldDeepFactor { get; set; } = D.OutfieldDeepFactor;
        public double BuntShiftCornerChargeFactor { get; set; } = D.BuntShiftCornerChargeFactor;
        // 配球方針の重み
        public double FastballHeavyShareDelta { get; set; } = D.FastballHeavyShareDelta;
        public double BreakingHeavyShareDelta { get; set; } = D.BreakingHeavyShareDelta;
        public double ControlFirstAimSigmaFactor { get; set; } = D.ControlFirstAimSigmaFactor;
        public double KeepLowAimYOffsetM { get; set; } = D.KeepLowAimYOffsetM;
        public double InsideAimXOffsetM { get; set; } = D.InsideAimXOffsetM;
        // 伝令効果・動揺・主将
        public double TimeoutMitigation { get; set; } = D.TimeoutMitigation;
        public int TimeoutDurationPa { get; set; } = D.TimeoutDurationPa;
        public int RattledConsecutiveBaserunners { get; set; } = D.RattledConsecutiveBaserunners;
        public double RattledNegativeAmplify { get; set; } = D.RattledNegativeAmplify;
        public int RattledThresholdMentalOffset { get; set; } = D.RattledThresholdMentalOffset;
        public int RattledRecoveryOuts { get; set; } = D.RattledRecoveryOuts;
        public double CaptainMitigationPerPower { get; set; } = D.CaptainMitigationPerPower;
        public double CaptainBenchFactor { get; set; } = D.CaptainBenchFactor;
        // 1球采配（設計書15 §2.3, Phase C）
        public double PitchTacticsTwoStrikeForceSwingProb { get; set; } = D.PitchTacticsTwoStrikeForceSwingProb;
        public int PitchTacticsTwoStrikeMinContact { get; set; } = D.PitchTacticsTwoStrikeMinContact;
        public double PitchTacticsThreeZeroTakeProb { get; set; } = D.PitchTacticsThreeZeroTakeProb;
        public int PitchTacticsThreeZeroSwingAwayMinPower { get; set; } = D.PitchTacticsThreeZeroSwingAwayMinPower;
        public int PitchTacticsThreeZeroSwingAwayFromInning { get; set; } = D.PitchTacticsThreeZeroSwingAwayFromInning;
        public int PitchTacticsThreeZeroSwingAwayMaxDiffAbs { get; set; } = D.PitchTacticsThreeZeroSwingAwayMaxDiffAbs;
        public double PitchTacticsPutAwayProb { get; set; } = D.PitchTacticsPutAwayProb;
        public int PitchTacticsControlFirstMinBalls { get; set; } = D.PitchTacticsControlFirstMinBalls;

        public TacticsCoefficients ToModel() => new()
        {
            SacBuntFromInning = SacBuntFromInning,
            SacBuntProb = SacBuntProb,
            SacBuntTieBreakProb = SacBuntTieBreakProb,
            SacBuntMaxPower = SacBuntMaxPower,
            SacBuntMinSkill = SacBuntMinSkill,
            SacBuntMaxBehind = SacBuntMaxBehind,
            SacBuntMaxAhead = SacBuntMaxAhead,
            SqueezeFromInning = SqueezeFromInning,
            SqueezeProb = SqueezeProb,
            SqueezeMaxDiffAbs = SqueezeMaxDiffAbs,
            SqueezeMinBunt = SqueezeMinBunt,
            StealMinSuccess = StealMinSuccess,
            StealProb = StealProb,
            StealThirdMinSuccess = StealThirdMinSuccess,
            StealThirdProb = StealThirdProb,
            StealThirdMaxOuts = StealThirdMaxOuts,
            StealThirdMaxDiffAbs = StealThirdMaxDiffAbs,
            StealHomeMinSuccess = StealHomeMinSuccess,
            StealHomeProb = StealHomeProb,
            StealHomeMaxOuts = StealHomeMaxOuts,
            StealHomeMaxDiffAbs = StealHomeMaxDiffAbs,
            HitAndRunProb = HitAndRunProb,
            HitAndRunMinContact = HitAndRunMinContact,
            HitAndRunMaxPower = HitAndRunMaxPower,
            GambleStartMaxSuccess = GambleStartMaxSuccess,
            GambleStartProb = GambleStartProb,
            TakeMaxControl = TakeMaxControl,
            TakeProb = TakeProb,
            SendHomeMinSuccess = SendHomeMinSuccess,
            SendHomeTwoOutRelax = SendHomeTwoOutRelax,
            SendHomeAggressionSpan = SendHomeAggressionSpan,
            SendThirdMinSuccess = SendThirdMinSuccess,
            SendThirdTwoOutRelax = SendThirdTwoOutRelax,
            SendThirdAggressionSpan = SendThirdAggressionSpan,
            BuntShiftProb = BuntShiftProb,
            InfieldInFromInning = InfieldInFromInning,
            InfieldInMaxLead = InfieldInMaxLead,
            OutfieldDeepMinPower = OutfieldDeepMinPower,
            ControlFirstMaxControl = ControlFirstMaxControl,
            KeepLowMinPower = KeepLowMinPower,
            GearPushInningsLeft = GearPushInningsLeft,
            GearPushMaxDiffAbs = GearPushMaxDiffAbs,
            GearCoastMinLead = GearCoastMinLead,
            IntentionalWalkMinPower = IntentionalWalkMinPower,
            IntentionalWalkFromInning = IntentionalWalkFromInning,
            IntentionalWalkMaxDiffAbs = IntentionalWalkMaxDiffAbs,
            IntentionalWalkProb = IntentionalWalkProb,
            DefenseTimeoutMinPressure = DefenseTimeoutMinPressure,
            OffenseTimeoutMinPressure = OffenseTimeoutMinPressure,
            OffenseTimeoutMaxMental = OffenseTimeoutMaxMental,
            PinchHitFromInning = PinchHitFromInning,
            PinchHitContactCeiling = PinchHitContactCeiling,
            PinchHitImprovement = PinchHitImprovement,
            PinchHitMinDiff = PinchHitMinDiff,
            PinchHitMaxDiff = PinchHitMaxDiff,
            PinchRunFromInning = PinchRunFromInning,
            PinchRunSpeedCeiling = PinchRunSpeedCeiling,
            PinchRunImprovement = PinchRunImprovement,
            PinchRunMinDiff = PinchRunMinDiff,
            DefensiveSubFromInning = DefensiveSubFromInning,
            DefensiveSubMinLead = DefensiveSubMinLead,
            DefensiveSubFieldingCeiling = DefensiveSubFieldingCeiling,
            DefensiveSubImprovement = DefensiveSubImprovement,
            HitAndRunContactBoost = HitAndRunContactBoost,
            HitAndRunPowerPenalty = HitAndRunPowerPenalty,
            HitAndRunLaunchPenalty = HitAndRunLaunchPenalty,
            HitAndRunCaughtPenalty = HitAndRunCaughtPenalty,
            BusterVsShiftBonus = BusterVsShiftBonus,
            BusterPenalty = BusterPenalty,
            SqueezeReadBase = SqueezeReadBase,
            SqueezeReadPerLead = SqueezeReadPerLead,
            SqueezeReadObviousBonus = SqueezeReadObviousBonus,
            InfieldInFactor = InfieldInFactor,
            InfieldDeepFactor = InfieldDeepFactor,
            OutfieldInFactor = OutfieldInFactor,
            OutfieldDeepFactor = OutfieldDeepFactor,
            BuntShiftCornerChargeFactor = BuntShiftCornerChargeFactor,
            FastballHeavyShareDelta = FastballHeavyShareDelta,
            BreakingHeavyShareDelta = BreakingHeavyShareDelta,
            ControlFirstAimSigmaFactor = ControlFirstAimSigmaFactor,
            KeepLowAimYOffsetM = KeepLowAimYOffsetM,
            InsideAimXOffsetM = InsideAimXOffsetM,
            TimeoutMitigation = TimeoutMitigation,
            TimeoutDurationPa = TimeoutDurationPa,
            RattledConsecutiveBaserunners = RattledConsecutiveBaserunners,
            RattledNegativeAmplify = RattledNegativeAmplify,
            RattledThresholdMentalOffset = RattledThresholdMentalOffset,
            RattledRecoveryOuts = RattledRecoveryOuts,
            CaptainMitigationPerPower = CaptainMitigationPerPower,
            CaptainBenchFactor = CaptainBenchFactor,
            PitchTacticsTwoStrikeForceSwingProb = PitchTacticsTwoStrikeForceSwingProb,
            PitchTacticsTwoStrikeMinContact = PitchTacticsTwoStrikeMinContact,
            PitchTacticsThreeZeroTakeProb = PitchTacticsThreeZeroTakeProb,
            PitchTacticsThreeZeroSwingAwayMinPower = PitchTacticsThreeZeroSwingAwayMinPower,
            PitchTacticsThreeZeroSwingAwayFromInning = PitchTacticsThreeZeroSwingAwayFromInning,
            PitchTacticsThreeZeroSwingAwayMaxDiffAbs = PitchTacticsThreeZeroSwingAwayMaxDiffAbs,
            PitchTacticsPutAwayProb = PitchTacticsPutAwayProb,
            PitchTacticsControlFirstMinBalls = PitchTacticsControlFirstMinBalls,
        };
    }

    private sealed class EnemyAiDto
    {
        private static readonly EnemyAiCoefficients D = new();
        public double OptimalBase { get; set; } = D.OptimalBase;
        public double OptimalPerSense { get; set; } = D.OptimalPerSense;
        public double OptimalFloor { get; set; } = D.OptimalFloor;
        public double OptimalCap { get; set; } = D.OptimalCap;
        public double RecklessOnMissProb { get; set; } = D.RecklessOnMissProb;
        public int SafetyBuntMinTier { get; set; } = D.SafetyBuntMinTier;
        public int StealMinTier { get; set; } = D.StealMinTier;
        public int StealThirdMinTier { get; set; } = D.StealThirdMinTier;
        public int StealHomeMinTier { get; set; } = D.StealHomeMinTier;
        public int GambleStartMinTier { get; set; } = D.GambleStartMinTier;
        public int SqueezeMinTier { get; set; } = D.SqueezeMinTier;
        public int HitAndRunMinTier { get; set; } = D.HitAndRunMinTier;
        public int BusterMinTier { get; set; } = D.BusterMinTier;
        public int DepthMinTier { get; set; } = D.DepthMinTier;
        public int BuntShiftMinTier { get; set; } = D.BuntShiftMinTier;
        public int AdvancedPolicyMinTier { get; set; } = D.AdvancedPolicyMinTier;
        public int InsidePolicyMinTier { get; set; } = D.InsidePolicyMinTier;
        public int GearMinTier { get; set; } = D.GearMinTier;
        public int TimeoutMinTier { get; set; } = D.TimeoutMinTier;
        public int PinchHitMinTier { get; set; } = D.PinchHitMinTier;
        public int PinchRunMinTier { get; set; } = D.PinchRunMinTier;
        public int DefensiveSubMinTier { get; set; } = D.DefensiveSubMinTier;
        public int PitchTacticsMinTier { get; set; } = D.PitchTacticsMinTier;
        public double SmallBallStealFactor { get; set; } = D.SmallBallStealFactor;
        public double SmallBallBuntFactor { get; set; } = D.SmallBallBuntFactor;
        public int SmallBallBuntInningEarlier { get; set; } = D.SmallBallBuntInningEarlier;
        public double SmallBallHitAndRunFactor { get; set; } = D.SmallBallHitAndRunFactor;
        public double SmallBallStealMinSuccessRelax { get; set; } = D.SmallBallStealMinSuccessRelax;
        public double SmallBallGambleStartFactor { get; set; } = D.SmallBallGambleStartFactor;
        public double PowerBuntFactor { get; set; } = D.PowerBuntFactor;
        public double PowerTakeFactor { get; set; } = D.PowerTakeFactor;
        public double PowerSqueezeFactor { get; set; } = D.PowerSqueezeFactor;
        public double DefensiveBuntFactor { get; set; } = D.DefensiveBuntFactor;
        public double DefensiveShiftFactor { get; set; } = D.DefensiveShiftFactor;
        public int DefensiveCoastLeadEarlier { get; set; } = D.DefensiveCoastLeadEarlier;
        public int DefensiveSubInningEarlier { get; set; } = D.DefensiveSubInningEarlier;
        public int SmallBallPinchRunInningEarlier { get; set; } = D.SmallBallPinchRunInningEarlier;
        public double AceDependentDefenseTimeoutKeepProb { get; set; } = D.AceDependentDefenseTimeoutKeepProb;
        // ④ 監督傾向層（issue #55）
        public double BuntHeavyBuntFactor { get; set; } = D.BuntHeavyBuntFactor;
        public int BuntHeavyBuntInningEarlier { get; set; } = D.BuntHeavyBuntInningEarlier;
        public double RunAndGunStealFactor { get; set; } = D.RunAndGunStealFactor;
        public double RunAndGunHitAndRunFactor { get; set; } = D.RunAndGunHitAndRunFactor;
        public double RunAndGunStealMinSuccessRelax { get; set; } = D.RunAndGunStealMinSuccessRelax;
        public double AceOveruseRelieveMarginFactor { get; set; } = D.AceOveruseRelieveMarginFactor;
        public double AceOveruseHardCapAdd { get; set; } = D.AceOveruseHardCapAdd;
        public double QuickHookRelieveMarginFactor { get; set; } = D.QuickHookRelieveMarginFactor;
        public int QuickHookDefensiveSubInningEarlier { get; set; } = D.QuickHookDefensiveSubInningEarlier;
        public int AggressivePinchHitInningEarlier { get; set; } = D.AggressivePinchHitInningEarlier;
        public int AggressivePinchHitCeilingRelax { get; set; } = D.AggressivePinchHitCeilingRelax;
        public int AggressivePinchHitImprovementRelax { get; set; } = D.AggressivePinchHitImprovementRelax;
        public double PromoterConditionWeight { get; set; } = D.PromoterConditionWeight;
        public int PromoterMinConditionStep { get; set; } = D.PromoterMinConditionStep;
        public double SqueezeLoverSqueezeFactor { get; set; } = D.SqueezeLoverSqueezeFactor;
        public int AggressiveGearPushInningsMore { get; set; } = D.AggressiveGearPushInningsMore;
        public int AggressiveGearPushDiffMore { get; set; } = D.AggressiveGearPushDiffMore;
        public int AggressiveGearCoastLeadLater { get; set; } = D.AggressiveGearCoastLeadLater;
        public double CautiousIntentionalWalkProb { get; set; } = D.CautiousIntentionalWalkProb;
        public int CautiousIntentionalWalkMinPowerRelax { get; set; } = D.CautiousIntentionalWalkMinPowerRelax;
        // ⑤ エース温存層（issue #42）
        public int AceRestMinTier { get; set; } = D.AceRestMinTier;
        public double AceRestBase { get; set; } = D.AceRestBase;
        public double AceRestTierGapWeight { get; set; } = D.AceRestTierGapWeight;
        public double AceRestRoundsRemainingWeight { get; set; } = D.AceRestRoundsRemainingWeight;
        public double AceRestFatigueWeight { get; set; } = D.AceRestFatigueWeight;
        public int AceRestFatigueWindowDays { get; set; } = D.AceRestFatigueWindowDays;
        public double AceRestFatigueReferencePitches { get; set; } = D.AceRestFatigueReferencePitches;
        public double AceRestFloor { get; set; } = D.AceRestFloor;
        public double AceRestCap { get; set; } = D.AceRestCap;
        public double AceRestAceDependentFactor { get; set; } = D.AceRestAceDependentFactor;
        public double AceRestDefensiveMindedFactor { get; set; } = D.AceRestDefensiveMindedFactor;
        public double AceRestTotalBaseballFactor { get; set; } = D.AceRestTotalBaseballFactor;

        public EnemyAiCoefficients ToModel() => new()
        {
            OptimalBase = OptimalBase,
            OptimalPerSense = OptimalPerSense,
            OptimalFloor = OptimalFloor,
            OptimalCap = OptimalCap,
            RecklessOnMissProb = RecklessOnMissProb,
            SafetyBuntMinTier = SafetyBuntMinTier,
            StealMinTier = StealMinTier,
            StealThirdMinTier = StealThirdMinTier,
            StealHomeMinTier = StealHomeMinTier,
            GambleStartMinTier = GambleStartMinTier,
            SqueezeMinTier = SqueezeMinTier,
            HitAndRunMinTier = HitAndRunMinTier,
            BusterMinTier = BusterMinTier,
            DepthMinTier = DepthMinTier,
            BuntShiftMinTier = BuntShiftMinTier,
            AdvancedPolicyMinTier = AdvancedPolicyMinTier,
            InsidePolicyMinTier = InsidePolicyMinTier,
            GearMinTier = GearMinTier,
            TimeoutMinTier = TimeoutMinTier,
            PinchHitMinTier = PinchHitMinTier,
            PinchRunMinTier = PinchRunMinTier,
            DefensiveSubMinTier = DefensiveSubMinTier,
            PitchTacticsMinTier = PitchTacticsMinTier,
            SmallBallStealFactor = SmallBallStealFactor,
            SmallBallBuntFactor = SmallBallBuntFactor,
            SmallBallBuntInningEarlier = SmallBallBuntInningEarlier,
            SmallBallHitAndRunFactor = SmallBallHitAndRunFactor,
            SmallBallStealMinSuccessRelax = SmallBallStealMinSuccessRelax,
            SmallBallGambleStartFactor = SmallBallGambleStartFactor,
            PowerBuntFactor = PowerBuntFactor,
            PowerTakeFactor = PowerTakeFactor,
            PowerSqueezeFactor = PowerSqueezeFactor,
            DefensiveBuntFactor = DefensiveBuntFactor,
            DefensiveShiftFactor = DefensiveShiftFactor,
            DefensiveCoastLeadEarlier = DefensiveCoastLeadEarlier,
            DefensiveSubInningEarlier = DefensiveSubInningEarlier,
            SmallBallPinchRunInningEarlier = SmallBallPinchRunInningEarlier,
            AceDependentDefenseTimeoutKeepProb = AceDependentDefenseTimeoutKeepProb,
            BuntHeavyBuntFactor = BuntHeavyBuntFactor,
            BuntHeavyBuntInningEarlier = BuntHeavyBuntInningEarlier,
            RunAndGunStealFactor = RunAndGunStealFactor,
            RunAndGunHitAndRunFactor = RunAndGunHitAndRunFactor,
            RunAndGunStealMinSuccessRelax = RunAndGunStealMinSuccessRelax,
            AceOveruseRelieveMarginFactor = AceOveruseRelieveMarginFactor,
            AceOveruseHardCapAdd = AceOveruseHardCapAdd,
            QuickHookRelieveMarginFactor = QuickHookRelieveMarginFactor,
            QuickHookDefensiveSubInningEarlier = QuickHookDefensiveSubInningEarlier,
            AggressivePinchHitInningEarlier = AggressivePinchHitInningEarlier,
            AggressivePinchHitCeilingRelax = AggressivePinchHitCeilingRelax,
            AggressivePinchHitImprovementRelax = AggressivePinchHitImprovementRelax,
            PromoterConditionWeight = PromoterConditionWeight,
            PromoterMinConditionStep = PromoterMinConditionStep,
            SqueezeLoverSqueezeFactor = SqueezeLoverSqueezeFactor,
            AggressiveGearPushInningsMore = AggressiveGearPushInningsMore,
            AggressiveGearPushDiffMore = AggressiveGearPushDiffMore,
            AggressiveGearCoastLeadLater = AggressiveGearCoastLeadLater,
            CautiousIntentionalWalkProb = CautiousIntentionalWalkProb,
            CautiousIntentionalWalkMinPowerRelax = CautiousIntentionalWalkMinPowerRelax,
            AceRestMinTier = AceRestMinTier,
            AceRestBase = AceRestBase,
            AceRestTierGapWeight = AceRestTierGapWeight,
            AceRestRoundsRemainingWeight = AceRestRoundsRemainingWeight,
            AceRestFatigueWeight = AceRestFatigueWeight,
            AceRestFatigueWindowDays = AceRestFatigueWindowDays,
            AceRestFatigueReferencePitches = AceRestFatigueReferencePitches,
            AceRestFloor = AceRestFloor,
            AceRestCap = AceRestCap,
            AceRestAceDependentFactor = AceRestAceDependentFactor,
            AceRestDefensiveMindedFactor = AceRestDefensiveMindedFactor,
            AceRestTotalBaseballFactor = AceRestTotalBaseballFactor,
        };
    }

    private sealed class RosterDto
    {
        private static readonly RosterCoefficients D = new();
        public double IntakeMean { get; set; } = D.IntakeMean;
        public double IntakeSd { get; set; } = D.IntakeSd;
        public int IntakeMin { get; set; } = D.IntakeMin;
        public int IntakeMax { get; set; } = D.IntakeMax;
        public double PitcherShare { get; set; } = D.PitcherShare;
        // 総合力（名声由来）
        public double TalentCenterDefault { get; set; } = D.TalentCenterDefault;
        public double TalentSd { get; set; } = D.TalentSd;
        public double TalentMin { get; set; } = D.TalentMin;
        public double TalentMax { get; set; } = D.TalentMax;
        // 凸凹配分
        public double ConcentrationMean { get; set; } = D.ConcentrationMean;
        public double ConcentrationSd { get; set; } = D.ConcentrationSd;
        public int SpecialtyFloor { get; set; } = D.SpecialtyFloor;
        public double OffSpecialtyFactor { get; set; } = D.OffSpecialtyFactor;
        public int OffSpecialtyFloor { get; set; } = D.OffSpecialtyFloor;
        // 運動能力ベースライン（名声独立）
        public double PhysicalMean { get; set; } = D.PhysicalMean;
        public double PhysicalSd { get; set; } = D.PhysicalSd;
        // 精神力・統率傾向（設計書02 §3 / 09 §8）
        public double MentalMean { get; set; } = D.MentalMean;
        public double MentalSd { get; set; } = D.MentalSd;
        public double LeadershipMean { get; set; } = D.LeadershipMean;
        public double LeadershipSd { get; set; } = D.LeadershipSd;
        public double LeadMean { get; set; } = D.LeadMean;
        public double LeadSd { get; set; } = D.LeadSd;
        public double LeadMentalCorr { get; set; } = D.LeadMentalCorr;
        public double LeadCapMentalCorr { get; set; } = D.LeadCapMentalCorr;
        // 投打の利き
        public double ThrowLeftProb { get; set; } = D.ThrowLeftProb;
        public double SwitchProb { get; set; } = D.SwitchProb;
        public double BatLeftGivenRightThrow { get; set; } = D.BatLeftGivenRightThrow;
        public double BatLeftGivenLeftThrow { get; set; } = D.BatLeftGivenLeftThrow;
        // 投手球速（地肩＋投手センス）
        public double PitcherSenseBonus { get; set; } = D.PitcherSenseBonus;
        public double NonPitcherSenseFactor { get; set; } = D.NonPitcherSenseFactor;
        public double VelocityArmWeight { get; set; } = D.VelocityArmWeight;
        public double VelocitySenseWeight { get; set; } = D.VelocitySenseWeight;
        // 伸びしろ
        public double GrowthMean { get; set; } = D.GrowthMean;
        public double GrowthSd { get; set; } = D.GrowthSd;
        public double GrowthMin { get; set; } = D.GrowthMin;
        public double GrowthMax { get; set; } = D.GrowthMax;
        public double LateGrowthBonus { get; set; } = D.LateGrowthBonus;
        // 才能上限
        public double CapGapMean { get; set; } = D.CapGapMean;
        public double CapGapSd { get; set; } = D.CapGapSd;
        public double LateCapBonus { get; set; } = D.LateCapBonus;
        public double SpeedCapGapFactor { get; set; } = D.SpeedCapGapFactor;
        public double ProdigyProb { get; set; } = D.ProdigyProb;
        // 投手経歴
        public double PitcherBackgroundProb { get; set; } = D.PitcherBackgroundProb;
        // 変化球レパートリー（設計書02 §2.2）
        public double PitcherSecondPitchProb { get; set; } = D.PitcherSecondPitchProb;
        public double PitcherThirdPitchProb { get; set; } = D.PitcherThirdPitchProb;
        public double FielderBreakingProb { get; set; } = D.FielderBreakingProb;
        public double BackgroundFielderExtraProb { get; set; } = D.BackgroundFielderExtraProb;
        public double PitchOffsetSd { get; set; } = D.PitchOffsetSd;
        // 守備位置適性の初期分布（設計書01 §1.1, 創発モデル）
        public double AptitudeBaseMean { get; set; } = D.AptitudeBaseMean;
        public double AptitudeBaseSd { get; set; } = D.AptitudeBaseSd;
        public double AptitudeGroupAffinitySd { get; set; } = D.AptitudeGroupAffinitySd;
        public double AptitudePositionNoiseSd { get; set; } = D.AptitudePositionNoiseSd;
        // 旧フィールド（後方互換）
        public double InitLevelMean { get; set; } = D.InitLevelMean;
        public double InitLevelSd { get; set; } = D.InitLevelSd;
        // 球質タイプ（本格派/技巧派/軟投派）: 出現割合と配分オフセット。自校・AI校で共有する。
        public double ArchetypePowerShare { get; set; } = AD.PowerShare;
        public double ArchetypeFinesseShare { get; set; } = AD.FinesseShare;
        public double ArchetypeSoftTossShare { get; set; } = AD.SoftTossShare;
        public double ArchetypePowerVelocity { get; set; } = AD.PowerVelocity;
        public double ArchetypePowerControl { get; set; } = AD.PowerControl;
        public double ArchetypePowerStamina { get; set; } = AD.PowerStamina;
        public double ArchetypePowerPitchRank { get; set; } = AD.PowerPitchRank;
        public double ArchetypeFinesseVelocity { get; set; } = AD.FinesseVelocity;
        public double ArchetypeFinesseControl { get; set; } = AD.FinesseControl;
        public double ArchetypeFinesseStamina { get; set; } = AD.FinesseStamina;
        public double ArchetypeFinessePitchRank { get; set; } = AD.FinessePitchRank;
        public double ArchetypeSoftTossVelocity { get; set; } = AD.SoftTossVelocity;
        public double ArchetypeSoftTossControl { get; set; } = AD.SoftTossControl;
        public double ArchetypeSoftTossStamina { get; set; } = AD.SoftTossStamina;
        public double ArchetypeSoftTossPitchRank { get; set; } = AD.SoftTossPitchRank;
        // 打順編成＋DH使用判断（issue #54, 設計書11 §4）。自校・AI校で共有する。
        public double LineupLeadoffDisciplineWeight { get; set; } = LD.LeadoffDisciplineWeight;
        public double LineupLeadoffContactWeight { get; set; } = LD.LeadoffContactWeight;
        public double LineupLeadoffSpeedWeight { get; set; } = LD.LeadoffSpeedWeight;
        public double LineupSecondContactWeight { get; set; } = LD.SecondContactWeight;
        public double LineupSecondBuntWeight { get; set; } = LD.SecondBuntWeight;
        public double LineupOverallContactWeight { get; set; } = LD.OverallContactWeight;
        public double LineupOverallPowerWeight { get; set; } = LD.OverallPowerWeight;
        public double LineupDhPitcherBattingGap { get; set; } = LD.DhPitcherBattingGap;

        private static readonly PitcherArchetypeCoefficients AD = new();
        private static readonly LineupCoefficients LD = new();

        public RosterCoefficients ToModel() => new()
        {
            Archetypes = new PitcherArchetypeCoefficients
            {
                PowerShare = ArchetypePowerShare,
                FinesseShare = ArchetypeFinesseShare,
                SoftTossShare = ArchetypeSoftTossShare,
                PowerVelocity = ArchetypePowerVelocity,
                PowerControl = ArchetypePowerControl,
                PowerStamina = ArchetypePowerStamina,
                PowerPitchRank = ArchetypePowerPitchRank,
                FinesseVelocity = ArchetypeFinesseVelocity,
                FinesseControl = ArchetypeFinesseControl,
                FinesseStamina = ArchetypeFinesseStamina,
                FinessePitchRank = ArchetypeFinessePitchRank,
                SoftTossVelocity = ArchetypeSoftTossVelocity,
                SoftTossControl = ArchetypeSoftTossControl,
                SoftTossStamina = ArchetypeSoftTossStamina,
                SoftTossPitchRank = ArchetypeSoftTossPitchRank,
            },
            Lineup = new LineupCoefficients
            {
                LeadoffDisciplineWeight = LineupLeadoffDisciplineWeight,
                LeadoffContactWeight = LineupLeadoffContactWeight,
                LeadoffSpeedWeight = LineupLeadoffSpeedWeight,
                SecondContactWeight = LineupSecondContactWeight,
                SecondBuntWeight = LineupSecondBuntWeight,
                OverallContactWeight = LineupOverallContactWeight,
                OverallPowerWeight = LineupOverallPowerWeight,
                DhPitcherBattingGap = LineupDhPitcherBattingGap,
            },
            IntakeMean = IntakeMean,
            IntakeSd = IntakeSd,
            IntakeMin = IntakeMin,
            IntakeMax = IntakeMax,
            PitcherShare = PitcherShare,
            TalentCenterDefault = TalentCenterDefault,
            TalentSd = TalentSd,
            TalentMin = TalentMin,
            TalentMax = TalentMax,
            ConcentrationMean = ConcentrationMean,
            ConcentrationSd = ConcentrationSd,
            SpecialtyFloor = SpecialtyFloor,
            OffSpecialtyFactor = OffSpecialtyFactor,
            OffSpecialtyFloor = OffSpecialtyFloor,
            PhysicalMean = PhysicalMean,
            PhysicalSd = PhysicalSd,
            MentalMean = MentalMean,
            MentalSd = MentalSd,
            LeadershipMean = LeadershipMean,
            LeadershipSd = LeadershipSd,
            LeadMean = LeadMean,
            LeadSd = LeadSd,
            LeadMentalCorr = LeadMentalCorr,
            LeadCapMentalCorr = LeadCapMentalCorr,
            ThrowLeftProb = ThrowLeftProb,
            SwitchProb = SwitchProb,
            BatLeftGivenRightThrow = BatLeftGivenRightThrow,
            BatLeftGivenLeftThrow = BatLeftGivenLeftThrow,
            PitcherSenseBonus = PitcherSenseBonus,
            NonPitcherSenseFactor = NonPitcherSenseFactor,
            VelocityArmWeight = VelocityArmWeight,
            VelocitySenseWeight = VelocitySenseWeight,
            GrowthMean = GrowthMean,
            GrowthSd = GrowthSd,
            GrowthMin = GrowthMin,
            GrowthMax = GrowthMax,
            LateGrowthBonus = LateGrowthBonus,
            CapGapMean = CapGapMean,
            CapGapSd = CapGapSd,
            LateCapBonus = LateCapBonus,
            SpeedCapGapFactor = SpeedCapGapFactor,
            ProdigyProb = ProdigyProb,
            PitcherBackgroundProb = PitcherBackgroundProb,
            PitcherSecondPitchProb = PitcherSecondPitchProb,
            PitcherThirdPitchProb = PitcherThirdPitchProb,
            FielderBreakingProb = FielderBreakingProb,
            BackgroundFielderExtraProb = BackgroundFielderExtraProb,
            PitchOffsetSd = PitchOffsetSd,
            AptitudeBaseMean = AptitudeBaseMean,
            AptitudeBaseSd = AptitudeBaseSd,
            AptitudeGroupAffinitySd = AptitudeGroupAffinitySd,
            AptitudePositionNoiseSd = AptitudePositionNoiseSd,
            InitLevelMean = InitLevelMean,
            InitLevelSd = InitLevelSd,
        };
    }

    private sealed class PersistentRosterDto
    {
        private static readonly PersistentRosterCoefficients D = new();
        private static readonly PhenomCoefficients PD = new();
        public int PitchersPerCohort { get; set; } = D.PitchersPerCohort;
        public double FreshmanGap { get; set; } = D.FreshmanGap;
        public double FameRecruitWeight { get; set; } = D.FameRecruitWeight;
        public double AnnualGrowth { get; set; } = D.AnnualGrowth;
        public double SeniorGrowthFactor { get; set; } = D.SeniorGrowthFactor;
        public double TargetTolerance { get; set; } = D.TargetTolerance;
        public double MaxResidualPerNode { get; set; } = D.MaxResidualPerNode;
        public double SummerNodeShare { get; set; } = D.SummerNodeShare;
        public double AutumnNodeShare { get; set; } = D.AutumnNodeShare;
        public double WinterNodeShare { get; set; } = D.WinterNodeShare;
        // 怪物（Phenom）
        public double PhenomSpikeRatePerSchoolYear { get; set; } = PD.SpikeRatePerSchoolYear;
        public double PhenomAllRoundRatePerSchoolYear { get; set; } = PD.AllRoundRatePerSchoolYear;
        public double PhenomAceWeight { get; set; } = PD.AceWeight;
        public double PhenomFinesseWeight { get; set; } = PD.FinesseWeight;
        public double PhenomSluggerWeight { get; set; } = PD.SluggerWeight;
        public double PhenomSpeedsterWeight { get; set; } = PD.SpeedsterWeight;
        public double PhenomStrongArmWeight { get; set; } = PD.StrongArmWeight;
        public int PhenomMainMin { get; set; } = PD.MainMin;
        public int PhenomMainMax { get; set; } = PD.MainMax;
        public int PhenomSupportMin { get; set; } = PD.SupportMin;
        public int PhenomSupportMax { get; set; } = PD.SupportMax;
        public int PhenomAllRoundMin { get; set; } = PD.AllRoundMin;
        public int PhenomAllRoundMax { get; set; } = PD.AllRoundMax;

        public PersistentRosterCoefficients ToModel() => new()
        {
            PitchersPerCohort = PitchersPerCohort,
            FreshmanGap = FreshmanGap,
            FameRecruitWeight = FameRecruitWeight,
            AnnualGrowth = AnnualGrowth,
            SeniorGrowthFactor = SeniorGrowthFactor,
            TargetTolerance = TargetTolerance,
            MaxResidualPerNode = MaxResidualPerNode,
            SummerNodeShare = SummerNodeShare,
            AutumnNodeShare = AutumnNodeShare,
            WinterNodeShare = WinterNodeShare,
            Phenom = new PhenomCoefficients
            {
                SpikeRatePerSchoolYear = PhenomSpikeRatePerSchoolYear,
                AllRoundRatePerSchoolYear = PhenomAllRoundRatePerSchoolYear,
                AceWeight = PhenomAceWeight,
                FinesseWeight = PhenomFinesseWeight,
                SluggerWeight = PhenomSluggerWeight,
                SpeedsterWeight = PhenomSpeedsterWeight,
                StrongArmWeight = PhenomStrongArmWeight,
                MainMin = PhenomMainMin,
                MainMax = PhenomMainMax,
                SupportMin = PhenomSupportMin,
                SupportMax = PhenomSupportMax,
                AllRoundMin = PhenomAllRoundMin,
                AllRoundMax = PhenomAllRoundMax,
            },
        };
    }

    private sealed class TeamStrengthDto
    {
        private static readonly TeamStrengthCoefficients D = new();
        public double PitchingWeight { get; set; } = D.PitchingWeight;
        public double BattingWeight { get; set; } = D.BattingWeight;
        public double DefenseWeight { get; set; } = D.DefenseWeight;
        public double DepthWeight { get; set; } = D.DepthWeight;
        public double MobilityWeight { get; set; } = D.MobilityWeight;
        public double MentalWeight { get; set; } = D.MentalWeight;
        public double ContactWeight { get; set; } = D.ContactWeight;
        public double PowerWeight { get; set; } = D.PowerWeight;
        public double LaunchWeight { get; set; } = D.LaunchWeight;
        public double DisciplineWeight { get; set; } = D.DisciplineWeight;
        public double VelocityWeight { get; set; } = D.VelocityWeight;
        public double ControlWeight { get; set; } = D.ControlWeight;
        public double StaminaWeight { get; set; } = D.StaminaWeight;
        public double PitchRankWeight { get; set; } = D.PitchRankWeight;
        public double FieldingWeight { get; set; } = D.FieldingWeight;
        public double CatchingWeight { get; set; } = D.CatchingWeight;
        public double ArmWeight { get; set; } = D.ArmWeight;
        public double SpeedWeight { get; set; } = D.SpeedWeight;
        public double StealWeight { get; set; } = D.StealWeight;
        public double AceWeight { get; set; } = D.AceWeight;
        public double SecondPitcherWeight { get; set; } = D.SecondPitcherWeight;
        public double RestPitcherWeight { get; set; } = D.RestPitcherWeight;
        public double BenchBatterWeight { get; set; } = D.BenchBatterWeight;
        public double BackupPitcherWeight { get; set; } = D.BackupPitcherWeight;
        public int LineupSize { get; set; } = D.LineupSize;
        public int BenchSampleSize { get; set; } = D.BenchSampleSize;
        public double OverallScale { get; set; } = D.OverallScale;
        public double OverallOffset { get; set; } = D.OverallOffset;

        public TeamStrengthCoefficients ToModel() => new()
        {
            PitchingWeight = PitchingWeight,
            BattingWeight = BattingWeight,
            DefenseWeight = DefenseWeight,
            DepthWeight = DepthWeight,
            MobilityWeight = MobilityWeight,
            MentalWeight = MentalWeight,
            ContactWeight = ContactWeight,
            PowerWeight = PowerWeight,
            LaunchWeight = LaunchWeight,
            DisciplineWeight = DisciplineWeight,
            VelocityWeight = VelocityWeight,
            ControlWeight = ControlWeight,
            StaminaWeight = StaminaWeight,
            PitchRankWeight = PitchRankWeight,
            FieldingWeight = FieldingWeight,
            CatchingWeight = CatchingWeight,
            ArmWeight = ArmWeight,
            SpeedWeight = SpeedWeight,
            StealWeight = StealWeight,
            AceWeight = AceWeight,
            SecondPitcherWeight = SecondPitcherWeight,
            RestPitcherWeight = RestPitcherWeight,
            BenchBatterWeight = BenchBatterWeight,
            BackupPitcherWeight = BackupPitcherWeight,
            LineupSize = LineupSize,
            BenchSampleSize = BenchSampleSize,
            OverallScale = OverallScale,
            OverallOffset = OverallOffset,
        };
    }

    private sealed class NationDto
    {
        private static readonly NationCoefficients D = new();
        public double StrengthMean { get; set; } = D.StrengthMean;
        public double StrengthSd { get; set; } = D.StrengthSd;
        public double StrengthMin { get; set; } = D.StrengthMin;
        public double StrengthMax { get; set; } = D.StrengthMax;
        public double StoriedFameBonus { get; set; } = D.StoriedFameBonus;
        public double StoriedStrengthBonus { get; set; } = D.StoriedStrengthBonus;
        public double EmergingFamePenalty { get; set; } = D.EmergingFamePenalty;
        public double AggregateScale { get; set; } = D.AggregateScale;
        public double MeanReversion { get; set; } = D.MeanReversion;
        public double ChurnSd { get; set; } = D.ChurnSd;
        public double StrengthPerWin { get; set; } = D.StrengthPerWin;
        public double FameToStrength { get; set; } = D.FameToStrength;
        public double FameDecay { get; set; } = D.FameDecay;
        public double FameChampion { get; set; } = D.FameChampion;
        public double FamePerWin { get; set; } = D.FamePerWin;

        public NationCoefficients ToModel() => new()
        {
            StrengthMean = StrengthMean,
            StrengthSd = StrengthSd,
            StrengthMin = StrengthMin,
            StrengthMax = StrengthMax,
            StoriedFameBonus = StoriedFameBonus,
            StoriedStrengthBonus = StoriedStrengthBonus,
            EmergingFamePenalty = EmergingFamePenalty,
            AggregateScale = AggregateScale,
            MeanReversion = MeanReversion,
            ChurnSd = ChurnSd,
            StrengthPerWin = StrengthPerWin,
            FameToStrength = FameToStrength,
            FameDecay = FameDecay,
            FameChampion = FameChampion,
            FamePerWin = FamePerWin,
        };
    }

    private sealed class CareerDto
    {
        private static readonly CareerCoefficients D = new();
        public double CoachingGrowthPerYear { get; set; } = D.CoachingGrowthPerYear;
        public double CoachingGrowthPerWin { get; set; } = D.CoachingGrowthPerWin;
        public double CoachingCap { get; set; } = D.CoachingCap;
        public double CoachingToStrength { get; set; } = D.CoachingToStrength;
        public int FreeKoshienThreshold { get; set; } = D.FreeKoshienThreshold;
        public double FreeFameThreshold { get; set; } = D.FreeFameThreshold;
        public double RetainTrustThreshold { get; set; } = D.RetainTrustThreshold;
        public double RetainTrustSpread { get; set; } = D.RetainTrustSpread;
        public double RetainProbability { get; set; } = D.RetainProbability;
        public double FameKoshienAppearance { get; set; } = D.FameKoshienAppearance;
        public double FameNationalChampion { get; set; } = D.FameNationalChampion;
        public double FamePerWin { get; set; } = D.FamePerWin;
        public double FameDecay { get; set; } = D.FameDecay;
        public double TrustReset { get; set; } = D.TrustReset;
        public double TrustPerWin { get; set; } = D.TrustPerWin;
        public double TrustKoshien { get; set; } = D.TrustKoshien;
        public double TrustPoorSeasonPenalty { get; set; } = D.TrustPoorSeasonPenalty;
        public double AnnualBudgetBase { get; set; } = D.AnnualBudgetBase;
        public double BudgetPerTrust { get; set; } = D.BudgetPerTrust;
        public double SummerCampCost { get; set; } = D.SummerCampCost;
        public double WinterCampCost { get; set; } = D.WinterCampCost;
        public double ScoutCost { get; set; } = D.ScoutCost;
        public double NewSchoolStrengthMean { get; set; } = D.NewSchoolStrengthMean;
        public double NewSchoolStrengthSd { get; set; } = D.NewSchoolStrengthSd;
        public double FreeChoiceSchoolStrength { get; set; } = D.FreeChoiceSchoolStrength;

        public CareerCoefficients ToModel() => new()
        {
            CoachingGrowthPerYear = CoachingGrowthPerYear,
            CoachingGrowthPerWin = CoachingGrowthPerWin,
            CoachingCap = CoachingCap,
            CoachingToStrength = CoachingToStrength,
            FreeKoshienThreshold = FreeKoshienThreshold,
            FreeFameThreshold = FreeFameThreshold,
            RetainTrustThreshold = RetainTrustThreshold,
            RetainTrustSpread = RetainTrustSpread,
            RetainProbability = RetainProbability,
            FameKoshienAppearance = FameKoshienAppearance,
            FameNationalChampion = FameNationalChampion,
            FamePerWin = FamePerWin,
            FameDecay = FameDecay,
            TrustReset = TrustReset,
            TrustPerWin = TrustPerWin,
            TrustKoshien = TrustKoshien,
            TrustPoorSeasonPenalty = TrustPoorSeasonPenalty,
            AnnualBudgetBase = AnnualBudgetBase,
            BudgetPerTrust = BudgetPerTrust,
            SummerCampCost = SummerCampCost,
            WinterCampCost = WinterCampCost,
            ScoutCost = ScoutCost,
            NewSchoolStrengthMean = NewSchoolStrengthMean,
            NewSchoolStrengthSd = NewSchoolStrengthSd,
            FreeChoiceSchoolStrength = FreeChoiceSchoolStrength,
        };
    }
}
