using Godswar.Server.Application.Pets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private const ushort PetOwnerMergeRequestLength = 4;

    private async Task HandlePetOwnerMergeRequestAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            Console.WriteLine(
                "[pet] rejected unauthenticated owner Merge request");
            return;
        }
        if (_registry.IsTrainingDummyCore(_character))
        {
            Console.WriteLine(
                $"[pet] ignored pinned training-dummy owner Merge " +
                $"character={_character.Name}");
            return;
        }

        if (packet.Length != PetOwnerMergeRequestLength ||
            packet.Buffer.Length != PetOwnerMergeRequestLength ||
            !packet.Payload.IsEmpty)
        {
            Console.WriteLine(
                "[pet] rejected malformed owner Merge request " +
                $"length={packet.Length} bytes={packet.Buffer.Length}");
            return;
        }

        PetCommandOperationIdentity identity;
        if (packet.ClientOperationId is { } operationId &&
            operationId != Guid.Empty)
        {
            identity = PetCommandOperationIdentity.SecureClient(operationId);
        }
        else
        {
            if (_session.IsSecure)
            {
                // A mutation that crossed the secure shim without its
                // operation token is an identity downgrade. Never turn it
                // into a server-generated operation or compatibility call.
                Console.WriteLine(
                    "[pet] rejected tokenless secure owner Merge request");
                return;
            }

            if (!AllowLegacyPlayerMutationFallback("pet_owner_merge"))
            {
                return;
            }

            identity = PetCommandOperationIdentity.RawLocalServer(
                Guid.NewGuid(),
                _commandConnectionId);
        }

        await HandleDurablePetOwnerMergeToggleAsync(
            identity,
            cancellationToken);
    }
}
