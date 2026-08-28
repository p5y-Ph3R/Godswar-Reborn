using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Godswar.Server.Application.WorldInstances;

internal static class MedusaTitleAwardPolicy
{
    public const string ChallengersKey = "medusa.challengers";
    public const string SlayersKey = "medusa.slayers";
    public const string ExecutionersKey = "medusa.executioners";
    public const string GorgonBreakerKey = "medusa.gorgon-breaker";
    public const string BaneOfTheThreeSistersKey =
        "medusa.bane-of-the-three-sisters";
    public const string HeirOfPerseusKey = "medusa.heir-of-perseus";

    public static IReadOnlyList<MedusaTitleDefinition> Titles =>
        MedusaRewardPolicyCatalog.Current.Titles;

    public static bool IsKnownSemanticKey(string? value) => value is
        ChallengersKey or
        SlayersKey or
        ExecutionersKey or
        GorgonBreakerKey or
        BaneOfTheThreeSistersKey or
        HeirOfPerseusKey;

    /// <summary>
    /// Consumes the authored best-only, inclusive timing policy. No caller may
    /// supply, stack, or downgrade the result.
    /// </summary>
    public static bool TryResolveBestAward(
        MedusaEncounterDifficulty difficulty,
        int finalScore,
        TimeSpan elapsed,
        out MedusaTitleDefinition definition)
    {
        if (!MedusaIslandPolicy.HasVictoryScore(finalScore))
        {
            definition = default;
            return false;
        }

        if (!MedusaIslandEncounterPolicy.TryResolveBestCompletionTitle(
                difficulty,
                finalScore,
                elapsed,
                out var award))
        {
            definition = default;
            return false;
        }

        if (MedusaRewardPolicyCatalog.Current.TryGetTitle(
                award.Title,
                out var candidate) &&
            string.Equals(
                candidate.DisplayName,
                award.DisplayName,
                StringComparison.Ordinal))
        {
            definition = candidate;
            return true;
        }

        throw new InvalidDataException(
            "The authored Medusa title lacks a semantic-key definition.");
    }

    /// <summary>
    /// Returns the title ID shipped in the paired client localization. This
    /// lookup does not itself grant ownership.
    /// </summary>
    public static bool TryGetClientTitleId(
        MedusaTitleSemanticKey semanticKey,
        out uint titleId)
    {
        foreach (var definition in Titles)
        {
            if (definition.SemanticKey == semanticKey)
            {
                titleId = definition.ClientTitleId;
                return true;
            }
        }

        titleId = 0;
        return false;
    }

    public static uint GetClientTitleId(MedusaEncounterTitle encounterTitle)
    {
        foreach (var definition in Titles)
        {
            if (definition.EncounterTitle == encounterTitle)
            {
                return definition.ClientTitleId;
            }
        }

        throw new ArgumentOutOfRangeException(
            nameof(encounterTitle),
            "The Medusa title has no client presentation ID.");
    }

    public static string ComputeRequestHash(
        MedusaTitleSettlementRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var writer = new HashWriter("medusa-title-settlement-v1");
        writer.Write(request.OperationId);
        writer.Write(request.AdmissionId.Value);
        writer.Write(request.WorldInstanceId.Value);
        writer.Write((byte)request.Difficulty);
        writer.Write(request.ContentMapId.Value);
        writer.Write(request.EncounterContentFingerprint);
        writer.Write(request.RosterHash);
        writer.Write(request.AdmissionRequestHash);
        writer.Write(request.FrozenMembers.Count);
        foreach (var member in request.FrozenMembers)
        {
            writer.Write(member.AccountId);
            writer.Write(member.CharacterId);
        }
        writer.Write(request.CompletedAtUtc.UtcTicks);
        writer.Write(request.Elapsed.Ticks);
        writer.Write(request.FinalScore);
        return writer.Complete();
    }

    private sealed class HashWriter : IDisposable
    {
        private readonly MemoryStream _stream = new();
        private bool _completed;

        public HashWriter(string domain) => Write(domain);

        public void Write(byte value) => _stream.WriteByte(value);

        public void Write(int value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            _stream.Write(bytes);
        }

        public void Write(short value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(short)];
            BinaryPrimitives.WriteInt16LittleEndian(bytes, value);
            _stream.Write(bytes);
        }

        public void Write(long value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
            _stream.Write(bytes);
        }

        public void Write(Guid value)
        {
            Span<byte> bytes = stackalloc byte[16];
            if (!value.TryWriteBytes(bytes, bigEndian: true, out var written) ||
                written != bytes.Length)
            {
                throw new InvalidOperationException("A GUID could not be encoded.");
            }
            _stream.Write(bytes);
        }

        public void Write(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Write(bytes.Length);
            _stream.Write(bytes);
        }

        public string Complete()
        {
            ObjectDisposedException.ThrowIf(_completed, this);
            _completed = true;
            return Convert.ToHexString(SHA256.HashData(
                _stream.GetBuffer().AsSpan(0, checked((int)_stream.Length))));
        }

        public void Dispose()
        {
            _completed = true;
            _stream.Dispose();
        }
    }
}
