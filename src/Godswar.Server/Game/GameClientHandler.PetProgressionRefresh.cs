using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<bool> SendPetProgressionRefreshAsync(
        PetBootstrapSnapshot pet,
        string source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pet);

        var ordered = pet.StatValues
            .OrderBy(static stat => stat.StatCode)
            .ToArray();
        if (ordered.Length != 6 ||
            ordered.Where((stat, index) =>
                stat.StatCode != index + 1).Any())
        {
            return false;
        }

        var basic = ordered
            .Select(static stat =>
                PetSavvyRuntimeSemantics.ResolveNativeBasic(
                    stat.InitialSavvy,
                    stat.RarityAddedSavvy))
            .ToArray();
        var added = ResolvePetProgressionAdded(pet);
        await _session.SendAsync(
            PacketBuilder.PetLevelUpgrade(
                RequirePetContent(),
                checked((uint)pet.PetId),
                pet.Level,
                pet.Experience,
                ToPetSavvy(basic),
                added),
            cancellationToken,
            source);
        return true;
    }

    internal static PetSavvy ResolvePetProgressionAdded(
        PetBootstrapSnapshot pet)
    {
        ArgumentNullException.ThrowIfNull(pet);
        PetSavvyRuntimeSemantics.ValidateProjectionProvenance(pet);
        var ordered = pet.StatValues
            .OrderBy(static stat => stat.StatCode)
            .ToArray();
        if (ordered.Length != 6 ||
            ordered.Where((stat, index) =>
                stat.StatCode != index + 1).Any())
        {
            throw new InvalidDataException(
                "A pet progression refresh requires six ordered stats.");
        }

        return ToPetSavvy(ordered
            .Select(stat =>
                PetSavvyRuntimeSemantics.ResolveNativeAdded(
                    pet.Level,
                    stat.AddedSavvy,
                    stat.BaseGrowthRate,
                    stat.GrowthAcceleration,
                    stat.RarityAddedSavvy))
            .ToArray());
    }

    private static PetSavvy ToPetSavvy(IReadOnlyList<decimal> values)
    {
        if (values.Count != 6)
        {
            throw new InvalidDataException(
                "A native pet Savvy projection requires six values.");
        }

        return new PetSavvy(
            values[0],
            values[1],
            values[2],
            values[3],
            values[4],
            values[5]);
    }
}
