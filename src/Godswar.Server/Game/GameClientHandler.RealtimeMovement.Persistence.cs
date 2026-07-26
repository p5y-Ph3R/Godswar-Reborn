using System.Threading.Channels;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task RunRealtimePositionSavesAsync(
        ChannelReader<RealtimePositionSave> reader,
        CancellationToken cancellationToken)
    {
        var lastSaveUtc = DateTime.MinValue;
        var failureLogged = false;
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken))
            {
                if (!TryReadNewest(reader, out var save))
                {
                    continue;
                }

                var delay =
                    PositionPersistInterval -
                    (DateTime.UtcNow - lastSaveUtc);
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                    if (TryReadNewest(reader, out var newerSave))
                    {
                        save = newerSave;
                    }
                }

                try
                {
                    await _store.SaveCharacterPositionAsync(
                        save.AccountId,
                        save.CharacterId,
                        save.MapId,
                        save.X,
                        save.Z,
                        cancellationToken);
                    failureLogged = false;
                }
                catch (Exception error)
                    when (error is not OperationCanceledException)
                {
                    if (!failureLogged)
                    {
                        Console.WriteLine(
                            $"[realtime] position persistence temporarily failed: {error.Message}");
                        failureLogged = true;
                    }
                }
                finally
                {
                    lastSaveUtc = DateTime.UtcNow;
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static bool TryReadNewest(
        ChannelReader<RealtimePositionSave> reader,
        out RealtimePositionSave save)
    {
        save = default;
        var found = false;
        while (reader.TryRead(out var candidate))
        {
            save = candidate;
            found = true;
        }
        return found;
    }

    private readonly record struct RealtimePositionSave(
        int AccountId,
        int CharacterId,
        byte MapId,
        float X,
        float Z);
}
