using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Progression;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PlayerRuntimeEcsCutoverChecks
{
    private sealed class RuntimePolicyProgressionExecutor :
        IProgressionIntervalSettlementCommandExecutor
    {
        private readonly Dictionary<
            string,
            ProgressionIntervalSettlementReceipt> _committed =
                new(StringComparer.Ordinal);

        public bool FailSettlement { get; set; }

        public List<CommandEnvelope<ProgressionIntervalSettlementCommand>>
            Envelopes { get; } = [];

        public Task<ProgressionIntervalSettlementExecutionResult>
            ExecuteAsync(
                CommandEnvelope<ProgressionIntervalSettlementCommand>
                    envelope,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Envelopes.Add(envelope);
            if (FailSettlement)
            {
                throw new InvalidOperationException(
                    "expected progression settlement failure");
            }

            if (_committed.TryGetValue(
                    envelope.OperationId,
                    out var committed))
            {
                return Task.FromResult(
                    ProgressionIntervalSettlementExecutionResult.Duplicate(
                        committed,
                        committed.Projection));
            }

            var command = envelope.Command;
            var projection = new ProgressionIntervalProjection(
                command.OnlineSessionId,
                command.IntervalSequence,
                command.OnlineUntilUtc,
                command.IntervalSequence,
                ZodiacEnergy: 0,
                ZodiacEnergyRemainderX100: 0,
                DateOnly.FromDateTime(
                    command.OnlineUntilUtc.UtcDateTime),
                command.OnlineUntilUtc.UtcTicks -
                    command.OnlineFromUtc.UtcTicks,
                ZodiacLastCompensationDay: null);
            var receipt = new ProgressionIntervalSettlementReceipt(
                envelope.Subject.CharacterId,
                command.OnlineSessionId,
                command.IntervalSequence,
                command.OnlineFromUtc,
                command.OnlineUntilUtc,
                GainedZodiacEnergyX100: 0,
                ZodiacCompensationApplied: false,
                UpdatedBoostCount: 0,
                projection,
                AuditReference: "runtime-policy",
                Guid.NewGuid());
            _committed.Add(envelope.OperationId, receipt);
            return Task.FromResult(
                ProgressionIntervalSettlementExecutionResult.Committed(
                    receipt));
        }
    }
}
