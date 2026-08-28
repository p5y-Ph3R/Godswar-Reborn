using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.ProtocolChecks;

internal static class MedusaTitleAwardFoundationChecks
{
    public const string CheckName =
        "Medusa durable title award contracts";

    public static Task RunAsync()
    {
        AssertSemanticKeysAndStockMetadata();
        AssertBestOnlyInclusivePolicy();
        AssertCompletionIdentityIsExactAndFrozen();
        AssertReceiptAndSnapshotShapesFailClosed();
        return Task.CompletedTask;
    }

    private static void AssertSemanticKeysAndStockMetadata()
    {
        Check.Equal(
            6,
            MedusaTitleAwardPolicy.Titles.Count,
            "all authored completion titles have semantic ownership keys");
        AssertStockTitle(MedusaTitleAwardPolicy.ExecutionersKey, 5009);
        AssertStockTitle(MedusaTitleAwardPolicy.SlayersKey, 5010);
        AssertStockTitle(MedusaTitleAwardPolicy.ChallengersKey, 5011);
        AssertStockTitle(MedusaTitleAwardPolicy.HeirOfPerseusKey, 5152);
        AssertStockTitle(
            MedusaTitleAwardPolicy.BaneOfTheThreeSistersKey,
            5153);
        AssertStockTitle(MedusaTitleAwardPolicy.GorgonBreakerKey, 5154);

        Check.True(
            !default(MedusaTitleSemanticKey).IsValid,
            "default semantic title key is invalid");
        Check.Throws<ArgumentException>(
            () => new MedusaTitleSemanticKey("medusa.unproven"),
            "unknown semantic title key is rejected");
    }

    private static void AssertBestOnlyInclusivePolicy()
    {
        AssertAward(
            MedusaEncounterDifficulty.Enhanced,
            TimeSpan.FromMinutes(10),
            MedusaTitleAwardPolicy.ChallengersKey);
        AssertAward(
            MedusaEncounterDifficulty.Enhanced,
            TimeSpan.FromMinutes(10).Add(TimeSpan.FromMicroseconds(1)),
            MedusaTitleAwardPolicy.SlayersKey);
        AssertAward(
            MedusaEncounterDifficulty.Enhanced,
            TimeSpan.FromMinutes(15),
            MedusaTitleAwardPolicy.SlayersKey);
        AssertAward(
            MedusaEncounterDifficulty.Enhanced,
            TimeSpan.FromMinutes(15).Add(TimeSpan.FromMicroseconds(1)),
            MedusaTitleAwardPolicy.ExecutionersKey);
        AssertAward(
            MedusaEncounterDifficulty.Enhanced,
            TimeSpan.FromMinutes(20),
            MedusaTitleAwardPolicy.ExecutionersKey);
        Check.True(
            !MedusaTitleAwardPolicy.TryResolveBestAward(
                MedusaEncounterDifficulty.Enhanced,
                MedusaIslandPolicy.VictoryScore,
                TimeSpan.FromMinutes(20).Add(TimeSpan.FromMicroseconds(1)),
                out _),
            "Enhanced completion after 20 minutes settles no title");
        Check.True(
            !MedusaTitleAwardPolicy.TryResolveBestAward(
                MedusaEncounterDifficulty.Normal,
                MedusaIslandPolicy.VictoryScore,
                TimeSpan.FromMinutes(1),
                out _),
            "Normal completion has no authored title");
        Check.True(
            !MedusaTitleAwardPolicy.TryResolveBestAward(
                MedusaEncounterDifficulty.Enhanced,
                MedusaIslandPolicy.VictoryScore - 1,
                TimeSpan.FromMinutes(10),
                out _),
            "a score below 3,000 cannot award a title");
        Check.True(
            MedusaTitleAwardPolicy.TryResolveBestAward(
                MedusaEncounterDifficulty.Enhanced,
                MedusaIslandPolicy.VictoryScore + 1,
                TimeSpan.FromMinutes(10),
                out var aboveThreshold) &&
            aboveThreshold.SemanticKey.Value ==
                MedusaTitleAwardPolicy.ChallengersKey,
            "external scoring above 3,000 remains title eligible");
        AssertAward(
            MedusaEncounterDifficulty.Mythic,
            TimeSpan.FromMinutes(10),
            MedusaTitleAwardPolicy.HeirOfPerseusKey);
    }

    private static void AssertCompletionIdentityIsExactAndFrozen()
    {
        var admission = MedusaAdmissionId.New();
        var world = WorldInstanceId.New();
        var at = MedusaDurableAdmissionFoundationChecks.Utc(5);
        var members = new List<MedusaTitleSettlementMember>
        {
            new(20, 200),
            new(10, 100)
        };
        var request = Request(admission, world, members, at);
        members.Clear();
        var replay = Request(
            admission,
            world,
            [new(10, 100), new(20, 200)],
            at);

        Check.Equal(
            MedusaAdmissionSagaOperationIds.Completed(admission),
            request.OperationId,
            "title settlement shares deterministic completion operation identity");
        Check.Equal(
            request.RequestHash,
            replay.RequestHash,
            "canonical settlement hash is independent of supplied roster order");
        Check.Equal(
            2,
            request.FrozenMembers.Count,
            "settlement request owns a frozen roster copy");
        Check.True(
            request.RequestHash != Request(
                admission,
                world,
                [new(11, 100), new(20, 200)],
                at).RequestHash,
            "settlement hash binds account and character pairs");
        Check.True(
            request.RequestHash != Request(
                admission,
                world,
                [new(10, 100), new(20, 200)],
                at.Add(TimeSpan.FromMicroseconds(1)),
                TimeSpan.FromMinutes(10).Add(
                    TimeSpan.FromMicroseconds(1))).RequestHash,
            "settlement hash binds authoritative completion clock evidence");
        Check.Throws<ArgumentException>(
            () => Request(
                admission,
                world,
                [new(10, 100), new(10, 200)],
                at),
            "settlement roster rejects duplicate accounts");
        Check.Throws<ArgumentOutOfRangeException>(
            () => Request(
                admission,
                world,
                [new(10, 100)],
                at,
                TimeSpan.FromTicks(1)),
            "settlement elapsed time rejects sub-microsecond ambiguity");
        var aboveThreshold = Request(
            admission,
            world,
            [new(10, 100)],
            at,
            finalScore: MedusaIslandPolicy.VictoryScore + 1);
        Check.Equal(
            MedusaIslandPolicy.VictoryScore + 1,
            aboveThreshold.FinalScore,
            "settlement preserves a score above the title threshold");
        Check.True(
            MedusaIslandEncounterPolicy.TryGetDifficulty(
                MedusaEncounterDifficulty.Enhanced,
                out var enhanced),
            "Enhanced title score ceiling resolves");
        Check.Throws<ArgumentOutOfRangeException>(
            () => Request(
                admission,
                world,
                [new(10, 100)],
                at,
                finalScore:
                    MedusaIslandEncounterPolicy.TotalVictoryScore(enhanced) +
                    1),
            "settlement request rejects a score above the configured roster total");
    }

    private static void AssertReceiptAndSnapshotShapesFailClosed()
    {
        var request = Request(
            MedusaAdmissionId.New(),
            WorldInstanceId.New(),
            [new(10, 100)],
            MedusaDurableAdmissionFoundationChecks.Utc(15));
        var snapshot = Snapshot(request);
        var applied = new MedusaTitleSettlementReceipt(
            MedusaTitleSettlementStatus.Applied,
            request.AdmissionId,
            snapshot);
        Check.True(applied.IsSuccess, "applied settlement receipt is successful");
        Check.Throws<ArgumentOutOfRangeException>(
            () => new MedusaTitleSettlementReceipt(
                default,
                request.AdmissionId,
                null),
            "undefined settlement receipt status fails closed");
        Check.Throws<ArgumentException>(
            () => new MedusaTitleSettlementReceipt(
                MedusaTitleSettlementStatus.Applied,
                request.AdmissionId,
                null),
            "successful settlement receipt requires a snapshot");
        Check.Throws<ArgumentException>(
            () => new MedusaTitleSettlementReceipt(
                MedusaTitleSettlementStatus.TerminalConflict,
                request.AdmissionId,
                snapshot),
            "terminal conflict cannot masquerade as an applied snapshot");
        Check.Throws<ArgumentException>(
            () => new MedusaTitleSettlementReceipt(
                MedusaTitleSettlementStatus.Duplicate,
                MedusaAdmissionId.New(),
                snapshot),
            "settlement receipt rejects another admission snapshot");
        Check.Throws<InvalidDataException>(
            () => new MedusaTitleSettlementSnapshot(
                request.AdmissionId,
                request.OperationId,
                request.WorldInstanceId,
                request.Difficulty,
                new MapId(200),
                request.EncounterContentFingerprint,
                request.RosterHash,
                request.AdmissionRequestHash,
                request.CompletedAtUtc,
                request.Elapsed,
                request.FinalScore,
                new string('F', MedusaDurableAdmissionPolicy.Sha256HexLength),
                new MedusaTitleSemanticKey(
                    MedusaTitleAwardPolicy.ChallengersKey),
                request.FrozenMembers),
            "snapshot rejects an inexact request hash");
        Check.Throws<InvalidDataException>(
            () => new MedusaTitleOwnershipSnapshot(
                100,
                default,
                request.AdmissionId,
                request.OperationId,
                request.CompletedAtUtc),
            "ownership rejects a default semantic key");
        Check.Throws<InvalidDataException>(
            () => new MedusaTitleOwnershipSnapshot(
                100,
                new MedusaTitleSemanticKey(
                    MedusaTitleAwardPolicy.ChallengersKey),
                request.AdmissionId,
                Guid.NewGuid(),
                request.CompletedAtUtc),
            "ownership rejects mismatched completion provenance");
    }

    private static MedusaTitleSettlementRequest Request(
        MedusaAdmissionId admission,
        WorldInstanceId world,
        IReadOnlyCollection<MedusaTitleSettlementMember> members,
        DateTimeOffset completedAt,
        TimeSpan? elapsed = null,
        int? finalScore = null) =>
        new(
            admission,
            world,
            MedusaEncounterDifficulty.Enhanced,
            new string('A', MedusaDurableAdmissionPolicy.Sha256HexLength),
            new string('B', MedusaDurableAdmissionPolicy.Sha256HexLength),
            new string('C', MedusaDurableAdmissionPolicy.Sha256HexLength),
            members,
            completedAt,
            elapsed ?? TimeSpan.FromMinutes(10),
            finalScore ?? MedusaIslandPolicy.VictoryScore);

    private static MedusaTitleSettlementSnapshot Snapshot(
        MedusaTitleSettlementRequest request) =>
        new(
            request.AdmissionId,
            request.OperationId,
            request.WorldInstanceId,
            request.Difficulty,
            new MapId(200),
            request.EncounterContentFingerprint,
            request.RosterHash,
            request.AdmissionRequestHash,
            request.CompletedAtUtc,
            request.Elapsed,
            request.FinalScore,
            request.RequestHash,
            new MedusaTitleSemanticKey(
                MedusaTitleAwardPolicy.ChallengersKey),
            request.FrozenMembers);

    private static void AssertStockTitle(string key, uint expectedId)
    {
        var semanticKey = new MedusaTitleSemanticKey(key);
        Check.True(
            MedusaTitleAwardPolicy.TryGetClientTitleId(
                semanticKey,
                out var actualId),
            $"stock title metadata exists for {key}");
        Check.Equal(expectedId, actualId, $"stock title ID for {key}");
    }

    private static void AssertAward(
        MedusaEncounterDifficulty difficulty,
        TimeSpan elapsed,
        string expectedKey)
    {
        Check.True(
            MedusaTitleAwardPolicy.TryResolveBestAward(
                difficulty,
                MedusaIslandPolicy.VictoryScore,
                elapsed,
                out var award),
            $"{difficulty} {elapsed} resolves one award");
        Check.True(
            string.Equals(
                expectedKey,
                award.SemanticKey.Value,
                StringComparison.Ordinal),
            $"{difficulty} {elapsed} resolves exact best-only semantic key");
    }
}
