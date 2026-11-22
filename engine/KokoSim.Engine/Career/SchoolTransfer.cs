using System.Collections.Generic;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Season;
using KokoSim.Engine.Stats;
using NationModel = KokoSim.Engine.Nation.Nation;

namespace KokoSim.Engine.Career;

/// <summary>転任前の自校（全国の外の専用ID で表していた学校）の同定と強さ。<see cref="Name"/> は自校の表記（幹）。</summary>
public sealed record DepartingSchool(string Name, int PrefectureId, int? DistrictId, double Strength);

/// <summary>
/// 転任の結果。<see cref="Roster"/> が新しい自校の部員、<see cref="OldSchoolId"/> が旧校の全国での ID。
/// <see cref="ArrivingCareer"/> は転任先が AI校だった間に積んだ通算成績を新しい部員のIDへ付け替えた帳簿
/// （呼び出し側が自校ストアの通算へ据える。<c>Execute</c> に stats を渡したときだけ入る）。
/// </summary>
public sealed record SchoolTransferResult(List<DevelopingPlayer> Roster, int NextPlayerId, int OldSchoolId)
{
    public StatBook ArrivingCareer { get; init; } = new();
}

/// <summary>
/// 監督の転任で「部員は学校に所属する」を実現する手順（設計書04 §1.3・issue #641）。
/// <list type="number">
/// <item>旧校を AI校として全国に残す。新設校だった（全国生成に無い）なら新しい ID で追加し、
/// 率いていた生成校なら全国へ戻す。旧部員は AI校の永続ロスターへ写す。</item>
/// <item>転任先 AI校の部員を自校の部員へ写し、その学校を全国から外す（自校は専用ID で大会に出る＝
/// 二重出場しない）。</item>
/// </list>
/// 選手の通算成績は学校の帳簿に付く（issue #1261）: 旧校へ渡す部員は自校の選手IDを引き継ぐので在任中の帳簿を
/// そのまま旧校の通算へ据え、転任先の部員はIDを振り直すので転任先の帳簿を旧ID→新IDで付け替えて自校の通算にする
/// （<paramref name="nationStats"/> と <paramref name="departingCareer"/> を渡したとき）。
/// 状態（<paramref name="overlay"/>・<paramref name="rosters"/>）を書き換える。乱数は変換の推定にだけ使い、
/// (校ID, 選手ID) 由来の専用ストリームなので他の抽選列を乱さない（不変条件#2）。
/// </summary>
public static class SchoolTransfer
{
    public static SchoolTransferResult Execute(
        NationOverlay overlay, NationModel generated, NationRosters rosters,
        DepartingSchool departing, IReadOnlyList<DevelopingPlayer> departingActive,
        School target, int yearIndex, int nextPlayerId, ulong masterSeed,
        NationTournamentStats? nationStats = null, StatBook? departingCareer = null)
    {
        // 1. 旧校を全国に残す。
        int oldId;
        var returning = overlay.ManagedSchoolId is not null;
        if (overlay.ManagedSchoolId is { } managed)
        {
            oldId = managed;   // 率いていた生成校（または以前に追加した学校）を全国へ戻す
        }
        else
        {
            oldId = overlay.NextSchoolId(generated);
            // 全国の校名は「〜高校」込み（自校の校名は幹）＝他のAI校と同じ表記で並べる。
            overlay.Added.Add(new AddedSchool(
                oldId, SchoolNames.Full(departing.Name), departing.PrefectureId, departing.DistrictId,
                departing.Strength, NationOverlay.InitialFame(departing.Strength)));
        }
        rosters.Put(TransferRosterConversion.ToAiRoster(oldId, departingActive, yearIndex));
        if (nationStats is not null && departingCareer is not null)
            nationStats.ReplaceCareerBook(oldId, departingCareer);

        // 2. 転任先の部員を引き継ぎ、全国から外す。
        rosters.GetOrBootstrap(target, yearIndex);
        var targetRoster = rosters.Take(target.Id)!;
        overlay.ManagedSchoolId = target.Id;
        var idMap = new Dictionary<int, int>();
        var (roster, next) = TransferRosterConversion.ToManagerRoster(
            targetRoster, nextPlayerId, masterSeed, idMap: idMap);
        var arriving = nationStats?.TakeCareerBook(target.Id, idMap) ?? new StatBook();

        // 戻した学校の強さは率いていた部員の総合へ合わせる（全国から外していた間は年次進化の対象外だった）。
        // 外す/戻すを済ませた後に引く＝旧校が一覧に見えている状態で実体を取る。
        if (returning)
            foreach (var s in overlay.Apply(generated).Schools)
                if (s.Id == oldId) s.Strength = departing.Strength;

        return new SchoolTransferResult(roster, next, oldId) { ArrivingCareer = arriving };
    }
}
