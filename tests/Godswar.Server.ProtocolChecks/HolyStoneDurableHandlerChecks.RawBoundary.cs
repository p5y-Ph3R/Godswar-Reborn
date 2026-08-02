using System.Buffers.Binary;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Game;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class HolyStoneDurableHandlerChecks
{
    private static async Task
        CheckRawBoundaryRejectsDowngradesAsync()
    {
        foreach (var alias in new[] { 106, 206, 306, 406 })
        {
            await AssertRawRejectedAsync(
                HolyStoneCommandContractChecks.CreatePacket(
                    HolyStoneProtocol.SpartaNpcId,
                    alias,
                    static _ => { }),
                $"raw alias {alias}");
        }

        var canonical = CreateRawCanonicalMountPacket(
            HolyStoneProtocol.ClientEquippedWeaponReference,
            HolyStoneProtocol.ClientKitBagReferenceBase + 7);
        await AssertRawRejectedAsync(
            ResizePacket(canonical, -sizeof(int)),
            "short canonical Mount");
        await AssertRawRejectedAsync(
            ResizePacket(canonical, sizeof(int)),
            "oversized canonical Mount");

        var wrongDeclaredLength = canonical.Buffer.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(
            wrongDeclaredLength,
            HolyStoneProtocol.PacketBytes - 1);
        await AssertRawRejectedAsync(
            new GamePacket(wrongDeclaredLength),
            "declared-length mismatch");

        await AssertRawRejectedAsync(
            CreateRawCanonicalMountPacket(
                targetReference: 5,
                stoneReference: 7),
            "bare 0..95 references");
        await AssertRawRejectedAsync(
            HolyStoneCommandContractChecks.CreatePacket(
                HolyStoneProtocol.SpartaNpcId,
                HolyStoneProtocol.MountSubId,
                args =>
                {
                    args[HolyStoneProtocol.MountScratchArgumentIndex] = 0;
                    args[HolyStoneProtocol.TargetArgumentIndex] =
                        HolyStoneProtocol.ClientEquippedWeaponReference;
                    args[HolyStoneProtocol.StoneArgumentIndex] =
                        HolyStoneProtocol.ClientKitBagReferenceBase + 7;
                    args[4] = 44;
                }),
            "arbitrary extra argument");
        await AssertRawRejectedAsync(
            CreateRawCanonicalMountPacket(
                HolyStoneProtocol.ClientKitBagReferenceBase + 7,
                HolyStoneProtocol.ClientKitBagReferenceBase + 7),
            "same target and material slot");
        await AssertRawRejectedAsync(
                HolyStoneCommandContractChecks.CreatePacket(
                    HolyStoneProtocol.SpartaNpcId,
                    HolyStoneProtocol.UpgradeSubId,
                    static args => args[6] = 205),
            "unsupported raw Holy Stone Upgrade value shape");

        var navigation =
            HolyStoneCommandContractChecks.CreatePacket(
                HolyStoneProtocol.SpartaNpcId,
                HolyStoneProtocol.MountSubId,
                static _ => { });
        await AssertRawRejectedAsync(
            ResizePacket(navigation, sizeof(int)),
            "oversized navigation");
    }

    private static GamePacket CreateRawCanonicalMountPacket(
        int targetReference,
        int stoneReference) =>
        HolyStoneCommandContractChecks.CreatePacket(
            HolyStoneProtocol.SpartaNpcId,
            HolyStoneProtocol.MountSubId,
            args =>
            {
                args[HolyStoneProtocol.MountScratchArgumentIndex] = 0;
                args[HolyStoneProtocol.TargetArgumentIndex] =
                    targetReference;
                args[HolyStoneProtocol.StoneArgumentIndex] =
                    stoneReference;
            });

    private static GamePacket ResizePacket(
        GamePacket source,
        int delta)
    {
        var resized = new byte[source.Buffer.Length + delta];
        source.Buffer.AsSpan(
            0,
            Math.Min(source.Buffer.Length, resized.Length)).CopyTo(
                resized);
        BinaryPrimitives.WriteUInt16LittleEndian(
            resized,
            checked((ushort)resized.Length));
        return new GamePacket(resized);
    }

    private static async Task AssertRawRejectedAsync(
        GamePacket packet,
        string description)
    {
        await using var fixture = await CreateRawFixtureAsync();
        await InvokeAsync(fixture.Handler, packet);

        Check.Equal(
            0,
            fixture.Store.HolyStoneCount,
            $"{description} cannot reach the raw store");
        var response = fixture.Transport.ReadLegacyPackets().Single();
        AssertNpcResult(
            response,
            HolyStoneNativeResults.WrongSelectionSubId,
            description);
    }
}
