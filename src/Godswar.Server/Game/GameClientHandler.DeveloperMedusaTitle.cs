using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private const string DeveloperMedusaTitlePrefix = "/medusatitle";

    private async Task<bool> HandleDeveloperMedusaTitleCommandAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (!TryReadTalkText(packet.Payload, out var text) ||
            !TryReadDeveloperMedusaTitleCommand(text, out var valid))
        {
            return false;
        }

        // Recognized developer commands never enter public map chat.
        if (_account is null ||
            _character is null ||
            !_developerCommands.Allows(_account.Id))
        {
            Console.WriteLine(
                "[developer-title] denied " +
                $"account={_account?.Id ?? 0} " +
                $"character={_character?.Name ?? "none"}");
            return true;
        }

        if (!valid)
        {
            await SendDeveloperMedusaTitleFeedbackAsync(
                packet,
                "[title] Usage: /medusatitle test",
                cancellationToken);
            return true;
        }

        var receipt = await _registry.GrantDeveloperMedusaTitleTestAsync(
            _session,
            cancellationToken);
        var message = receipt is { Succeeded: true, Award.Title: { } title }
            ? $"[title] Granted and selected '{title.DisplayName}'."
            : $"[title] Test grant failed: " +
              $"{receipt?.Status.ToString() ?? "provider unavailable"}.";
        await SendDeveloperMedusaTitleFeedbackAsync(
            packet,
            message,
            cancellationToken);
        return true;
    }

    internal static bool TryReadDeveloperMedusaTitleCommand(
        string text,
        out bool valid)
    {
        valid = false;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var commandOffset = text.IndexOf(
            DeveloperMedusaTitlePrefix,
            StringComparison.OrdinalIgnoreCase);
        if (commandOffset < 0)
        {
            return false;
        }

        var tokens = text[commandOffset..].Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);
        valid = tokens.Length == 2 &&
            tokens[0].Equals(
                DeveloperMedusaTitlePrefix,
                StringComparison.OrdinalIgnoreCase) &&
            tokens[1].Equals("test", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private Task SendDeveloperMedusaTitleFeedbackAsync(
        GamePacket commandPacket,
        string message,
        CancellationToken cancellationToken) =>
        _session.SendAsync(
            PacketBuilder.DeveloperCommandTalkReply(
                commandPacket.Payload,
                message),
            cancellationToken,
            "DeveloperMedusaTitleFeedback");

}
