using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Timeline;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KokoSim.Config;

/// <summary>
/// data/defensive-formations.yaml のローダ（CHANGELOG 33）。IO はこの層に隔離（不変条件#3）。
/// 陣形の調整は YAML 編集だけで完結する（不変条件#4）。
/// </summary>
public static class FormationsLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static FormationTable LoadFromFile(string path)
        => Parse(File.ReadAllText(path));

    public static FormationTable Parse(string yaml)
    {
        var dto = Deserializer.Deserialize<FileDto>(yaml)
            ?? throw new InvalidDataException("defensive-formations.yaml が空です。");
        var rules = new List<FormationRule>();
        foreach (var r in dto.Formations ?? new List<RuleDto>())
        {
            rules.Add(r.ToModel());
        }
        return rules.Count > 0 ? new FormationTable(rules) : FormationTable.Default;
    }

    private sealed class FileDto
    {
        public List<RuleDto>? Formations { get; set; }
    }

    private sealed class RuleDto
    {
        public string Zone { get; set; } = "";
        public string Runners { get; set; } = "any";
        public Dictionary<string, AssignmentDto>? Assignments { get; set; }

        public FormationRule ToModel()
        {
            var zone = Zone switch
            {
                "bunt" => BallZone.Bunt,
                "infield_left" => BallZone.InfieldLeft,
                "infield_right" => BallZone.InfieldRight,
                "outfield_left" => BallZone.OutfieldLeft,
                "outfield_center" => BallZone.OutfieldCenter,
                "outfield_right" => BallZone.OutfieldRight,
                _ => throw new InvalidDataException($"未知のゾーン: {Zone}"),
            };
            bool? runners = Runners switch
            {
                "none" => false,
                "on_base" => true,
                _ => null,
            };
            var assignments = new List<FormationAssignment>();
            foreach (var (posName, a) in Assignments ?? new Dictionary<string, AssignmentDto>())
            {
                assignments.Add(new FormationAssignment(ParsePosition(posName), ParseTask(a.Task), a.Target));
            }
            return new FormationRule(zone, runners, assignments);
        }

        private static FieldPosition ParsePosition(string name) => name switch
        {
            "pitcher" => FieldPosition.Pitcher,
            "catcher" => FieldPosition.Catcher,
            "first_base" => FieldPosition.FirstBase,
            "second_base" => FieldPosition.SecondBase,
            "third_base" => FieldPosition.ThirdBase,
            "shortstop" => FieldPosition.Shortstop,
            "left_field" => FieldPosition.LeftField,
            "center_field" => FieldPosition.CenterField,
            "right_field" => FieldPosition.RightField,
            _ => throw new InvalidDataException($"未知の守備位置: {name}"),
        };

        private static FielderTask ParseTask(string task) => task switch
        {
            "field_ball" => FielderTask.FieldBall,
            "cover_base" => FielderTask.CoverBase,
            "cutoff" => FielderTask.Cutoff,
            "backup" => FielderTask.Backup,
            "hold" => FielderTask.Hold,
            _ => throw new InvalidDataException($"未知のタスク: {task}"),
        };
    }

    private sealed class AssignmentDto
    {
        public string Task { get; set; } = "hold";
        public string Target { get; set; } = "none";
    }
}
