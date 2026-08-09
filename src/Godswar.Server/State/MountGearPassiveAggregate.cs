using System.Collections.Immutable;
using Godswar.Server.Application.Items;

namespace Godswar.Server.State;

internal static class ZephyrSpiritEffects
{
    public const short DaedalusAttunement = 21;
    public const short HephaestusTempering = 22;
    public const short MnemosynePreservation = 23;
    public const short ThemisContinuity = 24;
}

internal readonly record struct MountGearPassiveHost(
    int EquipmentSlot,
    uint ItemId,
    int AttunementBasisPoints,
    int TemperingBasisPoints);

internal readonly record struct ManaBurnMitigation(
    int RequestedMana,
    int PreventedMana,
    int AppliedMana,
    int ReductionBasisPoints);

internal readonly record struct CooldownExtensionMitigation(
    TimeSpan RequestedExtension,
    TimeSpan PreventedExtension,
    TimeSpan AppliedExtension,
    int ReductionBasisPoints);

/// <summary>
/// Immutable view of valid Zephyr effects on an equipped mount loadout. It is
/// independent of Ride state. Mnemosyne and Themis expose pure mitigation
/// calculations only; no producer invokes them until hostile mana-burn and
/// cooldown-extension mechanics become authoritative.
/// </summary>
internal sealed record MountGearPassiveAggregate(
    ImmutableArray<MountGearPassiveHost> Hosts,
    int ManaBurnReductionBasisPoints,
    int CooldownExtensionReductionBasisPoints)
{
    public const int MaximumReinforcedHosts = 2;
    public const int MaximumAttunementBasisPoints = 300;
    public const int MaximumTemperingBasisPoints = 200;
    public const int MaximumPveManaBurnReductionBasisPoints = 2_000;
    public const int MaximumPvpManaBurnReductionBasisPoints = 1_200;
    public const int MaximumPveCooldownExtensionReductionBasisPoints = 1_500;
    public const int MaximumPvpCooldownExtensionReductionBasisPoints = 1_000;

    public static MountGearPassiveAggregate Empty { get; } =
        new([], 0, 0);

    public ManaBurnMitigation MitigateManaBurn(
        int requestedMana,
        bool isPvp)
    {
        if (requestedMana < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedMana));
        }

        var cap = isPvp
            ? MaximumPvpManaBurnReductionBasisPoints
            : MaximumPveManaBurnReductionBasisPoints;
        var reduction = Math.Clamp(
            ManaBurnReductionBasisPoints,
            0,
            cap);
        var prevented = (int)Math.Min(
            requestedMana,
            (long)requestedMana * reduction / 10_000L);
        return new ManaBurnMitigation(
            requestedMana,
            prevented,
            requestedMana - prevented,
            reduction);
    }

    public CooldownExtensionMitigation MitigateCooldownExtension(
        TimeSpan requestedExtension,
        bool isPvp)
    {
        if (requestedExtension < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedExtension));
        }

        var cap = isPvp
            ? MaximumPvpCooldownExtensionReductionBasisPoints
            : MaximumPveCooldownExtensionReductionBasisPoints;
        var reduction = Math.Clamp(
            CooldownExtensionReductionBasisPoints,
            0,
            cap);
        var preventedTicks = Math.Min(
            requestedExtension.Ticks,
            requestedExtension.Ticks * (long)reduction / 10_000L);
        var prevented = TimeSpan.FromTicks(preventedTicks);
        return new CooldownExtensionMitigation(
            requestedExtension,
            prevented,
            requestedExtension - prevented,
            reduction);
    }
}

internal static class MountGearPassiveAggregateComposer
{
    private static readonly int[] MountGearSlots =
    [
        EquipmentSlots.MountHead,
        EquipmentSlots.MountArmor,
        EquipmentSlots.MountSoul,
        EquipmentSlots.MountOrnament,
        EquipmentSlots.MountAmulet
    ];

    public static MountGearPassiveAggregate Compose(
        GameCharacter character,
        IItemTemplateCatalog templates)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(templates);
        if (!TryGetCompatibleMount(character, templates, out var mountLevel))
        {
            return MountGearPassiveAggregate.Empty;
        }

        var hosts = new List<MutableHost>(MountGearSlots.Length);
        foreach (var slot in MountGearSlots)
        {
            var item = EquipmentSlots.GetItem(
                character.Equipment,
                character.Profession,
                slot);
            if (!TryValidateHost(
                    character,
                    templates,
                    mountLevel,
                    slot,
                    item))
            {
                continue;
            }

            var rolls = ReadValidRolls(item);
            hosts.Add(new MutableHost(
                slot,
                item.Id,
                Strongest(rolls, ZephyrSpiritEffects.DaedalusAttunement),
                Strongest(rolls, ZephyrSpiritEffects.HephaestusTempering),
                Strongest(rolls, ZephyrSpiritEffects.MnemosynePreservation),
                Strongest(rolls, ZephyrSpiritEffects.ThemisContinuity)));
        }

        var attunementSlots = SelectTopHostSlots(
            hosts,
            static host => host.AttunementBasisPoints);
        var temperingSlots = SelectTopHostSlots(
            hosts,
            static host => host.TemperingBasisPoints);
        var immutableHosts = hosts
            .Select(host => new MountGearPassiveHost(
                host.EquipmentSlot,
                host.ItemId,
                attunementSlots.Contains(host.EquipmentSlot)
                    ? Math.Min(
                        host.AttunementBasisPoints,
                        MountGearPassiveAggregate
                            .MaximumAttunementBasisPoints)
                    : 0,
                temperingSlots.Contains(host.EquipmentSlot)
                    ? Math.Min(
                        host.TemperingBasisPoints,
                        MountGearPassiveAggregate
                            .MaximumTemperingBasisPoints)
                    : 0))
            .ToImmutableArray();

        return new MountGearPassiveAggregate(
            immutableHosts,
            hosts.Select(static host => host.ManaBurnReductionBasisPoints)
                .DefaultIfEmpty(0)
                .Max(),
            hosts.Select(static host =>
                    host.CooldownExtensionReductionBasisPoints)
                .DefaultIfEmpty(0)
                .Max());
    }

    private static bool TryGetCompatibleMount(
        GameCharacter character,
        IItemTemplateCatalog templates,
        out int mountLevel)
    {
        mountLevel = 0;
        var mount = EquipmentSlots.GetItem(
            character.Equipment,
            character.Profession,
            EquipmentSlots.Mount);
        if (mount.IsEmpty ||
            !templates.TryGet(mount.Id, out var template) ||
            template.EquipmentSlot != EquipmentSlots.Mount ||
            !string.Equals(
                template.Kind,
                "mount",
                StringComparison.OrdinalIgnoreCase) ||
            !CanUseTemplate(character, template))
        {
            return false;
        }

        mountLevel = template.MinLevel ?? 1;
        return true;
    }

    private static bool TryValidateHost(
        GameCharacter character,
        IItemTemplateCatalog templates,
        int mountLevel,
        int slot,
        CompactItemEntry item)
    {
        if (item.IsEmpty ||
            item.SocketCount is < 1 or > 2 ||
            !templates.TryGet(item.Id, out var template) ||
            template.EquipmentSlot != slot ||
            !EquipmentEligibility.IsMountGearKind(template.Kind) ||
            !CanUseTemplate(character, template))
        {
            return false;
        }

        return mountLevel >= (template.MinLevel ?? 1);
    }

    private static bool CanUseTemplate(
        GameCharacter character,
        ItemTemplateDefinition template) =>
        character.Level >= (template.MinLevel ?? 1) &&
        (!template.MaxLevel.HasValue ||
         character.Level <= template.MaxLevel.Value) &&
        (template.ClassIds.Count == 0 ||
         template.ClassIds.Contains(character.Profession));

    private static IReadOnlyList<(short EffectId, int Value)>
        ReadValidRolls(CompactItemEntry item)
    {
        var sockets = new[]
        {
            (EffectId: item.Socket1EffectId,
                Level: item.Socket1Level,
                Value: item.Socket1Value),
            (EffectId: item.Socket2EffectId,
                Level: item.Socket2Level,
                Value: item.Socket2Value)
        };
        var rolls = new List<(short EffectId, int Value)>(2);
        for (var index = 0;
             index < Math.Min(item.SocketCount, (short)2);
             index++)
        {
            var socket = sockets[index];
            if (socket.EffectId is not { } effectId ||
                socket.Level is not { } level ||
                socket.Value is not { } value ||
                !TryGetZephyrDefinition(effectId, out var definition) ||
                !HolySpiritEffectivenessPolicy.TryGetGradeBracket(
                    definition.ItemId,
                    level,
                    out var minimum,
                    out var maximum) ||
                value < minimum || value > maximum)
            {
                continue;
            }

            rolls.Add((effectId, value));
        }

        return rolls;
    }

    private static bool TryGetZephyrDefinition(
        short effectId,
        out HolySpiritDefinition definition)
    {
        definition = HolySpiritEffectivenessPolicy.All
            .SingleOrDefault(candidate =>
                candidate.EffectId == effectId &&
                candidate.Affinity == HolyStoneAffinity.Zephyr);
        return definition.ItemId != 0;
    }

    private static int Strongest(
        IEnumerable<(short EffectId, int Value)> rolls,
        short effectId) =>
        rolls.Where(roll => roll.EffectId == effectId)
            .Select(static roll => roll.Value)
            .DefaultIfEmpty(0)
            .Max();

    private static HashSet<int> SelectTopHostSlots(
        IEnumerable<MutableHost> hosts,
        Func<MutableHost, int> valueSelector) =>
        hosts.Where(host => valueSelector(host) > 0)
            .OrderByDescending(valueSelector)
            .ThenBy(static host => host.EquipmentSlot)
            .Take(MountGearPassiveAggregate.MaximumReinforcedHosts)
            .Select(static host => host.EquipmentSlot)
            .ToHashSet();

    private sealed record MutableHost(
        int EquipmentSlot,
        uint ItemId,
        int AttunementBasisPoints,
        int TemperingBasisPoints,
        int ManaBurnReductionBasisPoints,
        int CooldownExtensionReductionBasisPoints);
}
