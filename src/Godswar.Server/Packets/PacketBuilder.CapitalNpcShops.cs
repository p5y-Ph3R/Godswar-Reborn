using System.Buffers.Binary;
using System.IO.Compression;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    private const int ShopCatalogHeaderBytes = 16;
    private const int ShopCatalogItemBytes = 88;

    // These captured records have no ItemBaseAttribute entry in the local
    // client. Advertising them makes Origin dereference a missing definition
    // while constructing the vendor window.
    private static readonly uint[] UnsupportedBoundGoldVendorItemIds =
        [4064, 4177, 4515, 10059];

    // Exact opcode-10071 catalog streams captured from the reference server on
    // 2026-08-28. Only the authoritative NPC ID and live B-Gold balance are
    // patched at egress; every item record, category, order, and price remains
    // byte-identical to the capture.
    private const string BoundGoldVendorCatalogGzip =
        "H4sIAAAAAAAEAO3bz2sTQRQH8EnS2Ji0Nm2RUsFALZiKnqLWqhV7qgc9pIqKtQrSatIKXuqPtGoVQfEi9qpehEL9BXqwF/8EQUR68qDiQSsUT+JJlLib5DljdjJMeO9dyjwM6UL2k3E6+3U2L85Gj6YPrxVChJOhh7dCYjkihPdHZL1Hj/coVirklbCpULGYTCuHlefpqHT7Cd35VUJ0eM/D3mOB0I03SpdyvL0xIZq957z3mCN0F73xdomyfY/QfRAru9Tj/RMrrwd/jVHO76nV5d8btbspLkROlOe3i9C947mDDO6PuJzfLLEL8ztM6B5JCJER5bnIEbqvEnJuKd31TXKsBUJ3VuaxUPP4pvg/kyMRYfU+bZFi8XZ78H0gj8GFawXrQh77rprJWBfyGFyq8UIe+66acVgX8th31UzGupDH1OOFPPZdNTuxLuQxtQt5XJ2dWLc6j+8TuZDH/t9fzWQKt0NxIZOxrprH/u8OshPr+nkM14SayVhXzeO7QmYy1u2r5HEo3FLaHzcnZdarZZv767zc/x0Lvs9EGu8mlGM4qZVgvCmN28Y03nYCVzfejUzjTTONdxfTeHczjfcAkztJ4A4or4QfRwncG5rxnidwD2ncJMF1vKRxLzKts1mZm6V97AWC9zmneZ9pAlc3L5eYxlsgcNdo1vMU03g/tODdjMb9SOCOa9xPBG6d62HU1q1zfq3dOufX2q1zfq1d3fwa9inWru7fD0POW7u6nI9uxq+z75r9JldBHofD8VIeD3bjxp/yxv9Gk0P7mNwsgTupHMNJQwSubj0fJHBfhoPuMJN7nMBdiAbdEQI3r2ww4KQTBG5/T9A9SeCO9QddV67Uqs7jMaYcOs3knmFyc0xunskdZ3InmNyzTG6ByZ1icl25UkvmcayUx9NM6+4yk3uFyb3K5M4wudeY3OfIz9989/2xoPuCwH03I1/q8s3VSqjq/fEXguukWzmGk74yuUtM7jcmd4jAVV8IPz9Dfl6YqvG53jzBeHU5/4jJfczkPmFyXblSS+ZxuJTHbwnWXaNyDCctMrmuXLlytVIK8jhS2R/3Etz3ZpvkMZy0g8D9tSHo9hG4O/cE3ZGa+0377wHq9kNzrXj3p6avaejnddq4tfp5hn6CtVtnPwHlGvoJKNfQT0C5rlypVZ3Hhn4Tat0Z+k0o19BvQrmGfhPKNfSbUK6h34RyDf0ElGvoJ6BcQz8B5Rr6CS6PXZFVdR4b+k2odWfoN6Hcz8jvsaZq/D+T/cjvCfvuFs0+NkMwDx3KMbhbCVzd9/62Ebh7Nfvu7QTu9Yaga7i/s3brvL+zdnX3d65cqSXzuKGUx4b7f+t1p7v/f1o7N61d5TL5574mcHV9rAGCz0E6NTnvypUrV7XqL5yJVYP4RwAA";

    private const string BindingGoldShopCatalogGzip =
        "H4sIAAAAAAAEAO3Zz0tUURTA8ffG0VGwZiSdmugXSElBG7MCy0aIWrQICiOicmMTKdgipU0/cDVlKzeVREaLaJEQkQT9CWlaWkGRVosg+oGbaOEimHzD3ObBO8nIPV8heGc1F+Z+3uHMfYcz7w2UH21oq3McJ5pw72ZdJ5twnJwQ7nw4JcQqN5er9q3NpisK7gnBvargDvm+aT72K7iDZUF3gfqmSnUXWd+S3UXWt2RXqu8ZhfpuFvLtVHC7BLdLwU1Egu45qA49UB16oTpchOpwCarDZagOA3/7cdzx+vHYJvvrtAjXeQ6545A7AbkvIPcl5E5C7hTkvoLcrwpuj29tNn1TcJPRoPtdwZ2tCboVDfZuujLo3lbob/VCHWIK+f4S5gkqTD92C/Px4xq7/CNeXXxrs2lEwd0vuDcV3JnyoPtEwb0l/I4P4vauNB8PK7jrBPcZlO8olO8Nhd9tn+BOWvYLz40J7hTkvofcacidgdxPCm5ccMehfIv9uCo/H09A13kLue8g9wPkfoTOR2O9vbtScLcpuNJc2KTgpoU5druC2yfMWWGEsRRR7MexfD/erXCeq3xrs6lFwd0puHsU3C+Cm1ZwR4R+8cPyf6TnDjYH3bkt9u5BId8F3A4bN+zHYYQRDNOPI4XnFa3r7c5zcv487/KtzaZ7Cu604B6A8n0I5ZuF8n0K5Tuq4B4T3DEFd5nwPOg1lO8bKN82KN/jUL7tUL4ZKN9uy+dintso5Htawe0U3GI/rsjPx2cVriPNmx1QXU5BdclAdVizwd79LbwPaYbcMMIIY+nC9OOywnzcv8Luvk79476+DrlDkPsIcg/VMu5JyM1AbjfkpuoYdyPkboXcVsidg9xoknGrIfczVIdiP16en4+vKfShPt8LdbPpDuTeh9xhyD2s0Ick9wjU385D+V6A3NUK94nkroXuvx2QuxdyZ6H6/oTc/y3+ANMqFmwQMgAA";

    private static readonly Lazy<byte[]> BoundGoldVendorCatalog = new(
        () => FilterUnsupportedCatalogItems(
            InflateCatalog(
                BoundGoldVendorCatalogGzip,
                expectedLength: 18_424,
                expectedPacketCount: 13,
                expectedCapturedNpcId: 5_461),
            UnsupportedBoundGoldVendorItemIds));

    private static readonly Lazy<byte[]> BindingGoldShopCatalog = new(
        () => InflateCatalog(
            BindingGoldShopCatalogGzip,
            expectedLength: 12_816,
            expectedPacketCount: 9,
            expectedCapturedNpcId: 5_460));

    public static byte[] CapitalNpcShopCatalog(
        uint npcId,
        int currencyBalance,
        CapitalNpcServiceKind service)
    {
        var source = GetCapitalShopCatalogSource(service);

        var packets = (byte[])source.Clone();
        var offset = 0;
        while (offset < packets.Length)
        {
            var packetLength = BinaryPrimitives.ReadUInt16LittleEndian(
                packets.AsSpan(offset, sizeof(ushort)));
            BinaryPrimitives.WriteUInt32LittleEndian(
                packets.AsSpan(offset + 4, sizeof(uint)),
                npcId);
            BinaryPrimitives.WriteInt32LittleEndian(
                packets.AsSpan(offset + 12, sizeof(int)),
                Math.Max(0, currencyBalance));
            offset += packetLength;
        }

        return packets;
    }

    public static bool TryResolveCapitalNpcShopOffer(
        CapitalNpcServiceKind service,
        int category,
        int listingIndex,
        uint expectedItemId,
        out CapitalShopOffer offer)
    {
        offer = default;
        if (category is < 0 or > byte.MaxValue ||
            listingIndex < 0 ||
            expectedItemId == 0 ||
            !CapitalNpcServiceProtocol.TryGetShopCurrency(
                service,
                out var currency))
        {
            return false;
        }

        var packets = GetCapitalShopCatalogSource(service);
        var packetOffset = 0;
        var currentIndex = 0;
        while (packetOffset < packets.Length)
        {
            var packetLength = BinaryPrimitives.ReadUInt16LittleEndian(
                packets.AsSpan(packetOffset));
            var itemCount = packets[packetOffset + 10];
            for (var itemIndex = 0;
                 itemIndex < itemCount;
                 itemIndex++, currentIndex++)
            {
                if (currentIndex != listingIndex)
                {
                    continue;
                }

                var record = packets.AsSpan(
                    packetOffset + ShopCatalogHeaderBytes +
                    (itemIndex * ShopCatalogItemBytes),
                    ShopCatalogItemBytes);
                return packets[packetOffset + 8] == category &&
                    TryReadCapitalShopOffer(
                        record,
                        expectedItemId,
                        currency,
                        out offer);
            }
            packetOffset += packetLength;
        }

        return false;
    }

    private static byte[] GetCapitalShopCatalogSource(
        CapitalNpcServiceKind service) =>
        service switch
        {
            CapitalNpcServiceKind.BoundGoldVendor =>
                BoundGoldVendorCatalog.Value,
            CapitalNpcServiceKind.BindingGoldShop =>
                BindingGoldShopCatalog.Value,
            _ => throw new ArgumentOutOfRangeException(
                nameof(service),
                service,
                "The selected capital NPC is not a shop.")
        };

    private static bool TryReadCapitalShopOffer(
        ReadOnlySpan<byte> record,
        uint expectedItemId,
        CapitalNpcShopCurrency currency,
        out CapitalShopOffer offer)
    {
        offer = default;
        var itemId = BinaryPrimitives.ReadUInt32LittleEndian(record);
        var price = BinaryPrimitives.ReadUInt32LittleEndian(
            record.Slice(68));
        var socketCount = BinaryPrimitives.ReadInt16LittleEndian(
            record.Slice(34));
        if (itemId != expectedItemId ||
            price is 0 or > int.MaxValue ||
            socketCount is < 0 or > 4)
        {
            return false;
        }

        var sockets = new (short? EffectId, short? Level, short? Value)[4];
        for (var index = 0; index < socketCount; index++)
        {
            var encoded = BinaryPrimitives.ReadInt16LittleEndian(
                record.Slice(36 + (index * 2)));
            if (encoded <= 0)
            {
                return false;
            }
            sockets[index] = (
                checked((short)(encoded / 100)),
                checked((short)((encoded % 100) + 1)),
                BinaryPrimitives.ReadInt16LittleEndian(
                    record.Slice(44 + (index * 2))));
        }

        var item = new CompactItemEntry(
            itemId,
            ReadShopAttribute(record, 4),
            ReadShopAttribute(record, 8),
            ReadShopAttribute(record, 12),
            ReadShopAttribute(record, 16),
            ReadShopAttribute(record, 20),
            record[24],
            record[25],
            record[26],
            record[27],
            BinaryPrimitives.ReadInt32LittleEndian(record.Slice(28)),
            BinaryPrimitives.ReadInt16LittleEndian(record.Slice(32)),
            null,
            null,
            null,
            null,
            null,
            socketCount,
            sockets[0].EffectId,
            sockets[0].Level,
            sockets[1].EffectId,
            sockets[1].Level,
            sockets[2].EffectId,
            sockets[2].Level,
            sockets[3].EffectId,
            sockets[3].Level,
            null,
            null,
            null,
            null)
        {
            Socket1Value = sockets[0].Value,
            Socket2Value = sockets[1].Value,
            Socket3Value = sockets[2].Value,
            Socket4Value = sockets[3].Value
        };
        var candidate = new CapitalShopOffer(
            item,
            checked((int)price),
            currency);
        if (!candidate.IsValid)
        {
            return false;
        }

        offer = candidate;
        return true;
    }

    private static int? ReadShopAttribute(
        ReadOnlySpan<byte> record,
        int offset)
    {
        var value = BinaryPrimitives.ReadInt32LittleEndian(
            record.Slice(offset));
        return value == -1 ? null : value;
    }

    private static byte[] InflateCatalog(
        string compressedBase64,
        int expectedLength,
        int expectedPacketCount,
        uint expectedCapturedNpcId)
    {
        using var compressed = new MemoryStream(
            Convert.FromBase64String(compressedBase64));
        using var gzip = new GZipStream(
            compressed,
            CompressionMode.Decompress);
        using var output = new MemoryStream(expectedLength);
        gzip.CopyTo(output);
        var packets = output.ToArray();
        if (packets.Length != expectedLength)
        {
            throw new InvalidDataException(
                "Captured capital shop stream has an invalid length.");
        }

        var offset = 0;
        var packetCount = 0;
        while (offset < packets.Length)
        {
            if (offset + 16 > packets.Length)
            {
                throw new InvalidDataException(
                    "Captured capital shop stream has a truncated header.");
            }

            var packetLength = BinaryPrimitives.ReadUInt16LittleEndian(
                packets.AsSpan(offset, sizeof(ushort)));
            var opcode = BinaryPrimitives.ReadUInt16LittleEndian(
                packets.AsSpan(offset + 2, sizeof(ushort)));
            var capturedNpcId = BinaryPrimitives.ReadUInt32LittleEndian(
                packets.AsSpan(offset + 4, sizeof(uint)));
            if (packetLength < 16 ||
                offset + packetLength > packets.Length ||
                opcode != Opcodes.NpcShopCatalog ||
                capturedNpcId != expectedCapturedNpcId)
            {
                throw new InvalidDataException(
                    "Captured capital shop stream failed validation.");
            }

            packetCount++;
            offset += packetLength;
        }

        if (packetCount != expectedPacketCount)
        {
            throw new InvalidDataException(
                "Captured capital shop stream has an invalid packet count.");
        }

        return packets;
    }

    private static byte[] FilterUnsupportedCatalogItems(
        byte[] packets,
        uint[] unsupportedItemIds)
    {
        var filtered = (byte[])packets.Clone();
        var packetOffset = 0;
        while (packetOffset < packets.Length)
        {
            var packetLength = BinaryPrimitives.ReadUInt16LittleEndian(
                packets.AsSpan(packetOffset));
            var itemCount = packets[packetOffset + 10];
            var payloadBytes = packetLength - ShopCatalogHeaderBytes;
            if (payloadBytes < 0 ||
                payloadBytes % ShopCatalogItemBytes != 0 ||
                itemCount > payloadBytes / ShopCatalogItemBytes)
            {
                throw new InvalidDataException(
                    "Captured capital shop item framing is invalid.");
            }

            var retainedCount = 0;
            for (var index = 0; index < itemCount; index++)
            {
                var recordOffset =
                    packetOffset + ShopCatalogHeaderBytes +
                    (index * ShopCatalogItemBytes);
                var itemId = BinaryPrimitives.ReadUInt32LittleEndian(
                    packets.AsSpan(recordOffset));
                if (Array.IndexOf(unsupportedItemIds, itemId) < 0)
                {
                    packets.AsSpan(recordOffset, ShopCatalogItemBytes)
                        .CopyTo(filtered.AsSpan(
                            packetOffset + ShopCatalogHeaderBytes +
                            (retainedCount * ShopCatalogItemBytes)));
                    retainedCount++;
                }
            }

            filtered[packetOffset + 10] = checked((byte)retainedCount);
            filtered.AsSpan(
                    packetOffset + ShopCatalogHeaderBytes +
                    (retainedCount * ShopCatalogItemBytes),
                    payloadBytes -
                    (retainedCount * ShopCatalogItemBytes))
                .Clear();
            packetOffset += packetLength;
        }

        return filtered;
    }
}
