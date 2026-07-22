using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class ForgeProtocolChecks
{
    public static Task RunAsync()
    {
        CheckSelectionPacket();
        CheckSelectionParserFuzz();
        CheckSelectionIdentityBinding();
        CheckOddsCrystalAccumulation();
        CheckResultPacket();
        return Task.CompletedTask;
    }

    private static void CheckSelectionParserFuzz()
    {
        var random = new Random(0x277E);
        for (var length = 0; length <= 80; length++)
        {
            for (var sample = 0; sample < 16; sample++)
            {
                var payload = new byte[length];
                random.NextBytes(payload);
                if (!ForgeItemSelectionPacket.TryParse(payload, out var selection))
                {
                    continue;
                }

                Check.Equal(ForgeItemSelectionPacket.PayloadLength, length, "only exact forge selection length parses");
                Check.True(selection.KitBagSlot is >= 0 and < 96, "fuzzed parsed forge slot stays in range");
                if (!selection.IsOddsMaterialIncrement)
                {
                    Check.True(selection.ItemId != 0, "fuzzed parsed forge item ID is nonzero");
                    Check.True(selection.Stack > 0, "fuzzed parsed forge stack is positive");
                }
            }
        }
    }

    private static void CheckOddsCrystalAccumulation()
    {
        var incrementPayload = Enumerable.Repeat((byte)0xFF, ForgeItemSelectionPacket.PayloadLength).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(incrementPayload.AsSpan(0), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(incrementPayload.AsSpan(4), 18);
        BinaryPrimitives.WriteUInt32LittleEndian(
            incrementPayload.AsSpan(8),
            ForgeItemSelectionPacket.OddsMaterialIncrementAction);
        BinaryPrimitives.WriteUInt32LittleEndian(
            incrementPayload.AsSpan(12),
            ForgeItemSelectionPacket.OrdinaryForgeMode);
        Check.True(
            ForgeItemSelectionPacket.TryParse(incrementPayload, out var increment) &&
            increment.IsOddsMaterialIncrement,
            "action-88 forge frame parses without trusting its scratch descriptor");
        Check.Equal(18, increment.KitBagSlot, "action-88 frame preserves trustworthy bag coordinates");
        Check.Equal(0u, increment.ItemId, "action-88 scratch item descriptor is discarded");

        var crystals = CompactItemEntry.Parse("[4232,,,,,,1,1,1,25,0,0]");
        var reservations = new ForgeOddsReservationSet();
        reservations.ValidateDescriptor(18, crystals);
        Check.Equal(0, reservations.TotalQuantity, "canonical destination-5 descriptor does not add a crystal");

        for (var quantity = 1; quantity <= 17; quantity++)
        {
            Check.True(
                reservations.TryIncrement(18, crystals),
                $"action-88 frame {quantity} adds one authoritative crystal");
            Check.Equal(quantity, reservations.TotalQuantity, "crystal increment actions accumulate");
        }

        var secondStack = crystals with { Stack = 8 };
        reservations.ValidateDescriptor(95, secondStack);
        Check.True(
            reservations.TryIncrement(95, secondStack),
            "the same crystal ID can continue from a second authoritative stack");
        Check.Equal(18, reservations.TotalQuantity, "same-ID stacks share the aggregate reservation");
        Check.Equal(2, reservations.CaptureSelections().Count, "per-stack deductions remain distinct");
        Check.True(reservations.IsFullyLinked, "either packet order becomes fully linked once descriptors arrive");

        while (reservations.TotalQuantity < EquipmentForgeCalculator.MaximumOddsQuantity)
        {
            Check.True(reservations.TryIncrement(18, crystals), "crystals can accumulate through native cap");
        }
        Check.True(
            !reservations.TryIncrement(18, crystals),
            "the twenty-sixth action-88 frame is rejected");
        Check.Equal(EquipmentForgeCalculator.MaximumOddsQuantity, reservations.TotalQuantity, "rejected excess preserves cap");

        var differentCrystals = CompactItemEntry.Parse("[4231,,,,,,1,1,1,5,0,0]");
        Check.True(
            reservations.TryIncrement(19, differentCrystals),
            "a different crystal ID starts a new reservation");
        Check.Equal(1, reservations.TotalQuantity, "different crystal ID clears the prior aggregate");
        Check.True(!reservations.IsFullyLinked, "an action arriving first waits for its canonical descriptor");
        reservations.ValidateDescriptor(19, differentCrystals);
        Check.True(reservations.IsFullyLinked, "later canonical descriptor links an action-first reservation");
    }

    private static void CheckSelectionIdentityBinding()
    {
        var account = new GameAccount { Id = 13 };
        var character = new GameCharacter { Id = 2, AccountId = 13 };
        Check.True(
            GameClientHandler.ForgeSelectionMatchesIdentity(13, 2, account, character),
            "forge batch remains bound to its originating account and character");
        Check.True(
            !GameClientHandler.ForgeSelectionMatchesIdentity(7, 2, account, character),
            "forge batch cannot cross accounts on one TCP session");
        Check.True(
            !GameClientHandler.ForgeSelectionMatchesIdentity(13, 3, account, character),
            "forge batch cannot cross characters");
        Check.True(
            !GameClientHandler.ForgeSelectionMatchesIdentity(
                13,
                2,
                account,
                new GameCharacter { Id = 2, AccountId = 7 }),
            "forge batch rejects an inconsistent character owner");
        Check.True(
            !GameClientHandler.ForgeSelectionMatchesIdentity(13, 2, account, null),
            "forge batch is invalidated when the active character is cleared");
    }

    private static void CheckSelectionPacket()
    {
        var packet = Convert.FromHexString(
            "3C007E27" +
            "00000000" +
            "12000000" +
            "05000000" +
            "00000000" +
            "88100000" +
            "01000000" +
            "01000000" +
            "63000000" +
            "01000000" +
            "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF");

        Check.Equal(60, packet.Length, "captured forge selection length");
        Check.True(
            ForgeItemSelectionPacket.TryParse(packet.AsSpan(4), out var selection),
            "captured crystal selection parses");
        Check.Equal(18, selection.KitBagSlot, "page-local forge slot becomes authoritative bag slot");
        Check.Equal(5, selection.DestinationSlot, "crystal forge-window destination slot");
        Check.Equal(4232u, selection.ItemId, "crystal item ID");

        var authoritative = CompactItemEntry.Empty with
        {
            Id = 4232,
            Quality = 1,
            Grade = 1,
            Stack = 99,
            Bound = 1
        };
        Check.True(selection.Matches(authoritative), "selection matches authoritative bag snapshot");
        Check.True(
            !selection.Matches(authoritative with { Stack = 98 }),
            "stale client descriptor does not match a changed stack");
        Check.True(
            !ForgeItemSelectionPacket.TryParse(packet.AsSpan(4, 52), out _),
            "truncated selection is rejected");
    }

    private static void CheckResultPacket()
    {
        var success = PacketBuilder.ForgeResult(success: true, resultKind: 1);
        Check.Equal(40, success.Length, "forge result length");
        Check.Equal((ushort)40, BinaryPrimitives.ReadUInt16LittleEndian(success), "forge result declared length");
        Check.Equal(Opcodes.ForgeStart, BinaryPrimitives.ReadUInt16LittleEndian(success.AsSpan(2)), "forge result opcode");
        Check.Equal((byte)1, success[4], "forge result success flag uses wire offset four");
        Check.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(success.AsSpan(8)), "ordinary forge result kind uses wire offset eight");
        Check.True(success.AsSpan(12).IndexOfAnyExcept((byte)0) < 0, "unused forge-result fields remain zero");

        var rejected = PacketBuilder.ForgeResult(success: false, resultKind: 0);
        Check.Equal((byte)0, rejected[4], "rejected forge clears success flag");
        Check.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(rejected.AsSpan(8)), "rejected forge uses no operation kind");
    }
}
