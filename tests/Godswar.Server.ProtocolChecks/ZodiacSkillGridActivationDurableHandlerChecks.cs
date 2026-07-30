using System.Buffers.Binary;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    ZodiacSkillGridActivationDurableHandlerChecks
{
    public static async Task RunAsync()
    {
        await CheckCommittedActivationAsync();
        await CheckDuplicateUsesCurrentProjectionAsync();
        await CheckTransientPreconditionAsync();
        await CheckMissingOwnerPreconditionAsync();
        await CheckInvalidGridNeverExecutesAsync();
        await CheckReservedValuesFailClosedAsync();
        await CheckMissingProviderUsesCompatibilityAsync();
        await CheckFailureCannotFabricateSuccessAsync();
    }

    private static async Task CheckCommittedActivationAsync()
    {
        var executor = new CapturingExecutor(
            _ => Task.FromResult(
                ZodiacSkillGridActivationExecutionResult.Committed(
                    Receipt())));
        await using var fixture = CreateFixture(executor);

        await InvokeAsync(
            fixture.Handler,
            CreateActivationPacket());

        Check.Equal(1, executor.Count, "committed activation executes once");
        var envelope = executor.LastEnvelope ??
            throw new InvalidOperationException(
                "committed activation envelope was not captured.");
        Check.Equal(
            new CommandSubject(AccountId, CharacterId),
            envelope.Subject,
            "activation subject comes from authenticated state");
        Check.Equal(
            GridIndex,
            envelope.Command.GridIndex,
            "activation envelope binds requested grid");
        Check.Equal(
            0,
            envelope.Command.ExpectedLevel,
            "activation envelope binds inactive precondition");
        Check.Equal(
            (int)CommandEnvelopeValidation.Valid,
            (int)ZodiacSkillGridActivationCommandEnvelope.Validate(
                envelope),
            "handler produces a valid durable envelope");

        var packets = ReadPackets(fixture.Transport);
        AssertResponseShape(
            packets,
            expectedActivationCount: 1,
            expectedStatusCount: 1,
            "committed activation");
        Check.Equal(
            2_700,
            fixture.Character.Gold,
            "commit projects authoritative Gold");
        Check.Equal(
            1,
            fixture.Character.ZodiacSkillGridLevels[GridIndex],
            "commit projects activated grid");
    }

    private static async Task
        CheckDuplicateUsesCurrentProjectionAsync()
    {
        var executor = new CapturingExecutor(
            _ => Task.FromResult(
                ZodiacSkillGridActivationExecutionResult.Duplicate(
                    Receipt(),
                    currentGold: 1_900,
                    currentLevel: 4,
                    selectedSkillId: 10_057,
                    currentWalletRevision: 8)));
        await using var fixture = CreateFixture(executor);
        var before = CaptureUnrelatedState(fixture.Character);

        await InvokeAsync(
            fixture.Handler,
            CreateActivationPacket());

        var packets = ReadPackets(fixture.Transport);
        AssertResponseShape(
            packets,
            expectedActivationCount: 0,
            expectedStatusCount: 1,
            "duplicate activation");
        Check.Equal(
            1_900,
            fixture.Character.Gold,
            "duplicate uses current Gold, not historical receipt Gold");
        Check.Equal(
            4,
            fixture.Character.ZodiacSkillGridLevels[GridIndex],
            "duplicate uses current grid level");
        Check.Equal(
            10_057,
            fixture.Character.ZodiacSkillGridSkillIds[GridIndex],
            "duplicate uses current selected skill");
        AssertUnrelatedStatePreserved(
            fixture.Character,
            before,
            "duplicate projection");
    }

    private static async Task CheckTransientPreconditionAsync()
    {
        var executor = new CapturingExecutor(
            _ => Task.FromResult(
                ZodiacSkillGridActivationExecutionResult
                    .PreconditionFailed(
                        currentGold: 2_299,
                        currentLevel: 0,
                        selectedSkillId: -1,
                        currentWalletRevision: 3)));
        await using var fixture = CreateFixture(executor);

        await InvokeAsync(
            fixture.Handler,
            CreateActivationPacket());

        var packets = ReadPackets(fixture.Transport);
        AssertResponseShape(
            packets,
            expectedActivationCount: 0,
            expectedStatusCount: 1,
            "transient activation rejection");
        Check.Equal(
            2_299,
            fixture.Character.Gold,
            "transient rejection projects current Gold");
        Check.Equal(
            0,
            fixture.Character.ZodiacSkillGridLevels[GridIndex],
            "transient rejection keeps grid inactive");
    }

    private static async Task CheckMissingOwnerPreconditionAsync()
    {
        var executor = new CapturingExecutor(
            _ => Task.FromResult(
                ZodiacSkillGridActivationExecutionResult
                    .PreconditionFailed()));
        await using var fixture = CreateFixture(executor);
        var before = CaptureUnrelatedState(fixture.Character);
        var goldBefore = fixture.Character.Gold;
        var gridBefore =
            fixture.Character.ZodiacSkillGridLevels[GridIndex];

        await InvokeAsync(
            fixture.Handler,
            CreateActivationPacket());

        AssertResponseShape(
            ReadPackets(fixture.Transport),
            expectedActivationCount: 0,
            expectedStatusCount: 0,
            "missing-owner activation rejection");
        Check.Equal(
            goldBefore,
            fixture.Character.Gold,
            "missing-owner rejection has no fabricated wallet projection");
        Check.Equal(
            gridBefore,
            fixture.Character.ZodiacSkillGridLevels[GridIndex],
            "missing-owner rejection has no fabricated grid projection");
        AssertUnrelatedStatePreserved(
            fixture.Character,
            before,
            "missing-owner rejection");
    }

    private static async Task CheckInvalidGridNeverExecutesAsync()
    {
        var executor = new CapturingExecutor(
            _ => throw new InvalidOperationException(
                "invalid grid reached executor"));
        await using var fixture = CreateFixture(executor);

        await InvokeAsync(
            fixture.Handler,
            CreateActivationPacket(gridIndex: 16));

        Check.Equal(
            0,
            executor.Count,
            "invalid grid is rejected before executor");
        AssertResponseShape(
            ReadPackets(fixture.Transport),
            expectedActivationCount: 0,
            expectedStatusCount: 0,
            "invalid grid");
    }

    private static async Task
        CheckMissingProviderUsesCompatibilityAsync()
    {
        var store = new ZodiacCompatibilityStore
        {
            Result = new ZodiacSkillGridActivationResult(
                ZodiacSkillGridActivationStatus.Succeeded,
                GridIndex,
                GoldCost: 2_300,
                CurrentGold: 2_700,
                CurrentLevel: 1,
                SelectedSkillId: -1)
        };
        await using var fixture = CreateFixture(
            executor: null,
            store);

        await InvokeAsync(
            fixture.Handler,
            CreateActivationPacket());

        Check.Equal(
            1,
            store.ActivationCount,
            "missing durable provider uses compatibility store");
        AssertResponseShape(
            ReadPackets(fixture.Transport),
            expectedActivationCount: 1,
            expectedStatusCount: 1,
            "compatibility activation");
    }

    private static async Task
        CheckFailureCannotFabricateSuccessAsync()
    {
        var failing = new CapturingExecutor(
            _ => Task.FromException<
                ZodiacSkillGridActivationExecutionResult>(
                    new IOException("injected executor failure")));
        await using (var fixture = CreateFixture(failing))
        {
            var failed = false;
            try
            {
                await InvokeAsync(
                    fixture.Handler,
                    CreateActivationPacket());
            }
            catch (IOException)
            {
                failed = true;
            }
            Check.True(failed, "executor failure propagates");
            Check.Equal(
                0,
                ReadPackets(fixture.Transport).Count,
                "executor failure emits no false success");
        }

        var cancelling = new CapturingExecutor(
            token =>
            {
                token.ThrowIfCancellationRequested();
                throw new InvalidOperationException(
                    "cancelled token was not observed");
            });
        await using var cancelledFixture = CreateFixture(cancelling);
        using var source = new CancellationTokenSource();
        source.Cancel();
        var cancelled = false;
        try
        {
            await InvokeAsync(
                cancelledFixture.Handler,
                CreateActivationPacket(),
                source.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        Check.True(cancelled, "executor cancellation propagates");
        Check.Equal(
            0,
            ReadPackets(cancelledFixture.Transport).Count,
            "executor cancellation emits no false success");
    }

    private static void AssertResponseShape(
        IReadOnlyList<byte[]> packets,
        int expectedActivationCount,
        int expectedStatusCount,
        string description)
    {
        var zodiacPackets = packets
            .Where(packet => Opcode(packet) == Opcodes.Zodiac)
            .ToArray();
        Check.Equal(
            expectedActivationCount,
            zodiacPackets.Count(packet =>
                packet.Length == 24 &&
                ZodiacSid(packet) == 100),
            $"{description} SID100 count");
        Check.Equal(
            1,
            zodiacPackets.Count(packet =>
                packet.Length == 328 &&
                ZodiacSid(packet) == 1),
            $"{description} full-sync count");
        Check.Equal(
            expectedStatusCount,
            packets.Count(packet =>
                Opcode(packet) == 0x27B6),
            $"{description} status count");
        Check.Equal(
            1,
            ZodiacSid(zodiacPackets[^1]),
            $"{description} full sync is final");

        var activationIndex = packets
            .Select((packet, index) => (packet, index))
            .Where(item =>
                Opcode(item.packet) == Opcodes.Zodiac &&
                item.packet.Length == 24 &&
                ZodiacSid(item.packet) == 100)
            .Select(item => item.index)
            .DefaultIfEmpty(-1)
            .Single();
        var statusIndex = packets
            .Select((packet, index) => (packet, index))
            .Where(item => Opcode(item.packet) == 0x27B6)
            .Select(item => item.index)
            .DefaultIfEmpty(-1)
            .Single();
        var fullSyncIndex = packets
            .Select((packet, index) => (packet, index))
            .Single(item =>
                Opcode(item.packet) == Opcodes.Zodiac &&
                item.packet.Length == 328 &&
                ZodiacSid(item.packet) == 1)
            .index;
        if (expectedActivationCount == 1)
        {
            Check.True(
                activationIndex < statusIndex &&
                statusIndex < fullSyncIndex,
                $"{description} acknowledges then refreshes then syncs");
        }
        else if (expectedStatusCount == 1)
        {
            Check.True(
                statusIndex < fullSyncIndex,
                $"{description} refreshes before full sync");
        }
    }

    private static UnrelatedState CaptureUnrelatedState(
        GameCharacter character) =>
        new(
            character.Level,
            character.CurrentHp,
            character.CurrentMp,
            character.Experience,
            character.TalentExperience,
            character.TalentPoints,
            character.Silver,
            character.Equipment,
            character.KitBag,
            character.ZodiacSkillGridLevels[4],
            character.ZodiacSkillGridSkillIds[4]);

    private static void AssertUnrelatedStatePreserved(
        GameCharacter character,
        UnrelatedState expected,
        string description)
    {
        Check.Equal(expected.Level, character.Level, $"{description} level");
        Check.Equal(expected.Hp, character.CurrentHp, $"{description} HP");
        Check.Equal(expected.Mp, character.CurrentMp, $"{description} MP");
        Check.Equal(
            expected.Experience,
            character.Experience,
            $"{description} EXP");
        Check.Equal(
            expected.TalentExperience,
            character.TalentExperience,
            $"{description} Talent EXP");
        Check.Equal(
            expected.TalentPoints,
            character.TalentPoints,
            $"{description} Talent Points");
        Check.Equal(
            expected.Silver,
            character.Silver,
            $"{description} Silver");
        Check.Equal(
            expected.Equipment,
            character.Equipment,
            $"{description} equipment");
        Check.Equal(
            expected.KitBag,
            character.KitBag,
            $"{description} kit bag");
        Check.Equal(
            expected.OtherGridLevel,
            character.ZodiacSkillGridLevels[4],
            $"{description} other grid level");
        Check.Equal(
            expected.OtherGridSkill,
            character.ZodiacSkillGridSkillIds[4],
            $"{description} other grid skill");
    }

    private sealed record UnrelatedState(
        int Level,
        int Hp,
        int Mp,
        int Experience,
        int TalentExperience,
        int TalentPoints,
        int Silver,
        string Equipment,
        string KitBag,
        int OtherGridLevel,
        int OtherGridSkill);
}
