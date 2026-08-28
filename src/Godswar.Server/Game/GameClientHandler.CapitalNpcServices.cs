using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<bool> TryHandleCapitalNpcDialogOpenAsync(
        NpcSpawnDefinition npc,
        CancellationToken cancellationToken)
    {
        if (!CapitalNpcServiceProtocol.TryResolve(npc, out var service) ||
            service == CapitalNpcServiceKind.ExchangeMentor)
        {
            return false;
        }

        var packet = service == CapitalNpcServiceKind.TeachingManager
            ? PacketBuilder.NpcDescriptionDialogOpenAck(
                npc.InteractionId,
                npc.NpcKey)
            : PacketBuilder.NpcShopDialogOpenAck(
                npc.InteractionId,
                npc.NpcKey);
        await _session.SendAsync(
            packet,
            cancellationToken,
            "CapitalNpcDialogOpenAck");
        return true;
    }

    private async Task<bool> TryHandleCapitalNpcPageRequestAsync(
        NpcSpawnDefinition npc,
        CancellationToken cancellationToken)
    {
        if (!CapitalNpcServiceProtocol.TryResolve(npc, out var service))
        {
            return false;
        }

        if (service is
            CapitalNpcServiceKind.BoundGoldVendor or
            CapitalNpcServiceKind.BindingGoldShop)
        {
            await _session.SendAsync(
                PacketBuilder.CapitalNpcShopCatalog(
                    npc.InteractionId,
                    GetCapitalShopCurrencyBalance(service),
                service),
                cancellationToken,
                "NpcShopCatalog",
                framed: false);
        }

        return true;
    }

    private async Task HandleCapitalNpcCreditExchangeAsync(
        uint npcId,
        int dialogIndex,
        int subId,
        CancellationToken cancellationToken)
    {
        if (dialogIndex != CapitalNpcServiceProtocol.ExchangeDialogIndex)
        {
            return;
        }

        if (CapitalNpcServiceProtocol.TryGetExchangePage(
                subId,
                out var pageSubIds))
        {
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(
                    npcId,
                    dialogIndex,
                    pageSubIds),
                cancellationToken,
                "NpcFunctionActionResponse");
            return;
        }

        // The browse capture did not include a confirmed exchange. Consume
        // those mutation attempts without inventing balances or rewards.
        if (subId is >= 311 and <= 316)
        {
            Console.WriteLine(
                $"[npc] credit exchange confirmation unavailable " +
                $"npc={npcId} subId={subId}");
        }
    }

    private async Task HandleCapitalNpcShopPurchaseAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null ||
            !CapitalNpcServiceProtocol.TryParsePurchase(
                packet.Payload,
                out var intent) ||
            !TryResolveMapNpc(intent.NpcId, out var npc) ||
            !CapitalNpcServiceProtocol.TryResolve(npc, out var service) ||
            service is not (
                CapitalNpcServiceKind.BoundGoldVendor or
                CapitalNpcServiceKind.BindingGoldShop) ||
            !PacketBuilder.TryResolveCapitalNpcShopOffer(
                service,
                intent.Category,
                intent.ListingIndex,
                intent.ItemId,
                out var offer))
        {
            Console.Error.WriteLine(
                "[npc-shop] rejected malformed or untrusted purchase " +
                $"length={packet.Length}");
            return;
        }

        var result = await _store.PurchaseCapitalShopItemAsync(
            _account.Id,
            _character.Id,
            Guid.NewGuid(),
            offer,
            intent.Quantity,
            cancellationToken);
        if (!result.Purchased || result.Character is null)
        {
            Console.WriteLine(
                $"[npc-shop] purchase rejected character={_character.Name} " +
                $"npc={intent.NpcId} item={intent.ItemId} " +
                $"quantity={intent.Quantity} status={result.Status}");
            await SendCapitalShopCatalogAsync(
                npc,
                service,
                result.CurrencyBalance,
                cancellationToken);
            return;
        }

        InstallCapitalShopProjection(result.Character);
        _registry.UpdateCharacter(
            _session,
            _character,
            advanceWorldRevision: false);
        await _session.SendAsync(
            BuildLocalPlayerStatusUpdate(),
            cancellationToken,
            "NpcShopWalletStatus");
        await SendKitBagRefreshAsync(cancellationToken);
        await SendCapitalShopCatalogAsync(
            npc,
            service,
            GetCapitalShopCurrencyBalance(service),
            cancellationToken);
        Console.WriteLine(
            $"[npc-shop] purchased character={_character.Name} " +
            $"npc={intent.NpcId} item={intent.ItemId} " +
            $"quantity={intent.Quantity} unitPrice={offer.UnitPrice} " +
            $"currency={offer.Currency} " +
            $"balance={GetCapitalShopCurrencyBalance(service)}");
    }

    private void InstallCapitalShopProjection(GameCharacter updated)
    {
        if (_character is null ||
            updated.Id != _character.Id ||
            updated.AccountId != _character.AccountId ||
            updated.RealmId != _character.RealmId)
        {
            throw new InvalidDataException(
                "The purchased shop projection has the wrong owner.");
        }

        // A shop purchase changes only the wallet and kit bag. The legacy
        // character reload contains stored base HP/MP, not the live derived
        // equipment projection, so replacing the whole character would clamp
        // the authoritative live vitals to those base values.
        _character.Silver = updated.Silver;
        _character.Gold = updated.Gold;
        _character.BindingGold = updated.BindingGold;
        _character.KitBag = updated.KitBag;
    }

    private int GetCapitalShopCurrencyBalance(
        CapitalNpcServiceKind service)
    {
        if (_character is null ||
            !CapitalNpcServiceProtocol.TryGetShopCurrency(
                service,
                out var currency))
        {
            return 0;
        }
        return currency == CapitalNpcShopCurrency.Gold
            ? _character.Gold
            : _character.BindingGold;
    }

    private Task SendCapitalShopCatalogAsync(
        NpcSpawnDefinition npc,
        CapitalNpcServiceKind service,
        int currencyBalance,
        CancellationToken cancellationToken) =>
        _session.SendAsync(
            PacketBuilder.CapitalNpcShopCatalog(
                npc.InteractionId,
                Math.Max(0, currencyBalance),
                service),
            cancellationToken,
            "NpcShopCatalogRefresh",
            framed: false);
}
