using System.Buffers.Binary;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal readonly record struct ForgeItemSelectionPacket(
    int BagPage,
    int PageSlot,
    int DestinationSlot,
    int RequestMode,
    uint ItemId,
    short Quality,
    short Grade,
    short Stack,
    short Bound)
{
    public const int PayloadLength = 56;
    public const int OrdinaryForgeMode = 0;
    public const int EquipmentDestinationSlot = 0;
    public const int PrimaryMaterialDestinationSlot = 1;
    public const int OddsMaterialDestinationSlot = 5;
    public const int OddsMaterialIncrementAction = 88;
    public const int SlotsPerPage = 24;
    public const int PageCount = 4;

    public int KitBagSlot => checked((BagPage * SlotsPerPage) + PageSlot);

    public bool IsOddsMaterialIncrement => DestinationSlot == OddsMaterialIncrementAction;

    public bool Matches(CompactItemEntry item)
    {
        return !item.IsEmpty &&
               item.Id == ItemId &&
               item.Quality == Quality &&
               item.Grade == Grade &&
               item.Stack == Stack &&
               item.Bound == Bound;
    }

    public static bool TryParse(ReadOnlySpan<byte> payload, out ForgeItemSelectionPacket selection)
    {
        selection = default;
        if (payload.Length != PayloadLength)
        {
            return false;
        }

        var page = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(0, 4));
        var pageSlot = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4));
        // This field identifies the destination inside the forge window. It is
        // not a quantity. Slot 5 is the odds-crystal descriptor; action 88 is
        // the paired successful one-crystal increment.
        var destinationSlot = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8, 4));
        var mode = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(12, 4));
        if (page >= PageCount ||
            pageSlot >= SlotsPerPage ||
            destinationSlot > int.MaxValue ||
            mode > int.MaxValue)
        {
            return false;
        }

        // Action 88 is the client's successful "+1 crystal" notification.
        // Only the bag coordinates, action, and mode are initialized in this
        // frame; all descriptor bytes after offset 16 are scratch data.
        if (destinationSlot == OddsMaterialIncrementAction)
        {
            selection = new ForgeItemSelectionPacket(
                (int)page,
                (int)pageSlot,
                (int)destinationSlot,
                (int)mode,
                0,
                0,
                0,
                0,
                0);
            return true;
        }

        var itemId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(16, 4));
        var quality = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(20, 4));
        var grade = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(24, 4));
        var stack = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(28, 4));
        var bound = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(32, 4));

        if (itemId == 0 ||
            quality > short.MaxValue ||
            grade > short.MaxValue ||
            stack == 0 ||
            stack > short.MaxValue ||
            bound > short.MaxValue)
        {
            return false;
        }

        selection = new ForgeItemSelectionPacket(
            (int)page,
            (int)pageSlot,
            (int)destinationSlot,
            (int)mode,
            itemId,
            (short)quality,
            (short)grade,
            (short)stack,
            (short)bound);
        return true;
    }
}
