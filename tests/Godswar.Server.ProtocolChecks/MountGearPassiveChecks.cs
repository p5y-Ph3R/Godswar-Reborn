using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class MountGearPassiveChecks
{
    public const string CheckName =
        "Persistent mount-gear Zephyr passive policy";

    public static Task RunAsync()
    {
        AssertLoadoutAndStrongestHostRules();
        AssertMitigationCaps();
        AssertMountAndSocketRequirementsFailClosed();
        AssertDurableProjectionContract();
        return Task.CompletedTask;
    }

    private static void AssertLoadoutAndStrongestHostRules()
    {
        var character = CharacterWith(
            (EquipmentSlots.Mount, Item(14220)),
            (EquipmentSlots.MountHead, SpiritHost(
                14500,
                (21, 10, 300),
                (21, 10, 200))),
            (EquipmentSlots.MountArmor, SpiritHost(
                14600,
                (21, 10, 250),
                (24, 10, 1_500))),
            (EquipmentSlots.MountSoul, SpiritHost(
                14700,
                (21, 10, 200),
                (22, 10, 200))),
            (EquipmentSlots.MountOrnament, SpiritHost(
                14800,
                (22, 10, 180),
                (23, 10, 2_000))),
            (EquipmentSlots.MountAmulet, SpiritHost(
                14900,
                (22, 10, 150),
                (24, 10, 1_200))));

        var aggregate = MountGearPassiveAggregateComposer.Compose(
            character,
            TestItemContent.Catalog);
        Check.Equal(5, aggregate.Hosts.Length,
            "all compatible equipped mount-gear hosts are inspected");
        Check.Equal(
            300,
            Host(aggregate, EquipmentSlots.MountHead)
                .AttunementBasisPoints,
            "the strongest duplicate Daedalus roll wins on one host");
        Check.Equal(
            250,
            Host(aggregate, EquipmentSlots.MountArmor)
                .AttunementBasisPoints,
            "the second strongest Daedalus host remains active");
        Check.Equal(
            0,
            Host(aggregate, EquipmentSlots.MountSoul)
                .AttunementBasisPoints,
            "Daedalus reinforces at most the strongest two hosts");
        Check.Equal(
            200,
            Host(aggregate, EquipmentSlots.MountSoul)
                .TemperingBasisPoints,
            "the strongest Hephaestus host remains active");
        Check.Equal(
            180,
            Host(aggregate, EquipmentSlots.MountOrnament)
                .TemperingBasisPoints,
            "the second strongest Hephaestus host remains active");
        Check.Equal(
            0,
            Host(aggregate, EquipmentSlots.MountAmulet)
                .TemperingBasisPoints,
            "Hephaestus reinforces at most the strongest two hosts");
        Check.Equal(2_000, aggregate.ManaBurnReductionBasisPoints,
            "Mnemosyne takes the strongest valid loadout roll");
        Check.Equal(1_500, aggregate.CooldownExtensionReductionBasisPoints,
            "Themis takes the strongest valid loadout roll");
    }

    private static void AssertMitigationCaps()
    {
        var aggregate = MountGearPassiveAggregateComposer.Compose(
            CharacterWith(
                (EquipmentSlots.Mount, Item(14220)),
                (EquipmentSlots.MountOrnament, SpiritHost(
                    14800,
                    (23, 10, 2_000),
                    (24, 10, 1_500)))),
            TestItemContent.Catalog);

        var pveMana = aggregate.MitigateManaBurn(1_000, isPvp: false);
        var pvpMana = aggregate.MitigateManaBurn(1_000, isPvp: true);
        Check.True(
            pveMana.PreventedMana == 200 &&
            pveMana.AppliedMana == 800 &&
            pveMana.ReductionBasisPoints == 2_000,
            "Mnemosyne caps PvE mana-burn reduction at 20%");
        Check.True(
            pvpMana.PreventedMana == 120 &&
            pvpMana.AppliedMana == 880 &&
            pvpMana.ReductionBasisPoints == 1_200,
            "Mnemosyne caps PvP mana-burn reduction at 12%");

        var requested = TimeSpan.FromSeconds(10);
        var pveCooldown = aggregate.MitigateCooldownExtension(
            requested,
            isPvp: false);
        var pvpCooldown = aggregate.MitigateCooldownExtension(
            requested,
            isPvp: true);
        Check.True(
            pveCooldown.PreventedExtension == TimeSpan.FromSeconds(1.5) &&
            pveCooldown.AppliedExtension == TimeSpan.FromSeconds(8.5) &&
            pveCooldown.ReductionBasisPoints == 1_500,
            "Themis caps PvE hostile cooldown-extension reduction at 15%");
        Check.True(
            pvpCooldown.PreventedExtension == TimeSpan.FromSeconds(1) &&
            pvpCooldown.AppliedExtension == TimeSpan.FromSeconds(9) &&
            pvpCooldown.ReductionBasisPoints == 1_000,
            "Themis caps PvP hostile cooldown-extension reduction at 10%");
        Check.Throws<ArgumentOutOfRangeException>(
            () => aggregate.MitigateManaBurn(-1, false),
            "negative hostile mana burn fails closed");
        Check.Throws<ArgumentOutOfRangeException>(
            () => aggregate.MitigateCooldownExtension(
                TimeSpan.FromTicks(-1),
                false),
            "negative hostile cooldown extension fails closed");
    }

    private static void AssertMountAndSocketRequirementsFailClosed()
    {
        Check.True(
            HolyStoneEquipmentEligibility.IsMountGear(
                TestItemContent.Catalog,
                14500) &&
            !HolyStoneEquipmentEligibility.IsMountGear(
                TestItemContent.Catalog,
                1000),
            "server item metadata, not client slot claims, identifies mount gear");
        var noMount = MountGearPassiveAggregateComposer.Compose(
            CharacterWith(
                (EquipmentSlots.MountHead, SpiritHost(
                    14500,
                    (21, 10, 300)))),
            TestItemContent.Catalog);
        Check.Equal(MountGearPassiveAggregate.Empty, noMount,
            "passives require a compatible equipped mount, not Ride state");

        var unopenedThirdSocket = SpiritHost(
            14500,
            (21, 10, 300)) with
        {
            Socket3EffectId = 23,
            Socket3Level = 10,
            Socket3Value = 2_000
        };
        var firstTwoOnly = MountGearPassiveAggregateComposer.Compose(
            CharacterWith(
                (EquipmentSlots.Mount, Item(14220)),
                (EquipmentSlots.MountHead, unopenedThirdSocket)),
            TestItemContent.Catalog);
        Check.Equal(0, firstTwoOnly.ManaBurnReductionBasisPoints,
            "mount gear reads only native holy_socket1 and holy_socket2");

        var overDrilled = SpiritHost(
            14500,
            (21, 10, 300),
            (22, 10, 200)) with { SocketCount = 3 };
        var invalidHost = MountGearPassiveAggregateComposer.Compose(
            CharacterWith(
                (EquipmentSlots.Mount, Item(14220)),
                (EquipmentSlots.MountHead, overDrilled)),
            TestItemContent.Catalog);
        Check.Equal(0, invalidHost.Hosts.Length,
            "mount gear with more than two opened sockets fails closed");

        var outOfBracket = MountGearPassiveAggregateComposer.Compose(
            CharacterWith(
                (EquipmentSlots.Mount, Item(14220)),
                (EquipmentSlots.MountHead, SpiritHost(
                    14500,
                    (21, 10, 301)))),
            TestItemContent.Catalog);
        Check.Equal(
            0,
            Host(outOfBracket, EquipmentSlots.MountHead)
                .AttunementBasisPoints,
            "persisted rolls outside the reviewed bracket are ignored");
    }

    private static void AssertDurableProjectionContract()
    {
        var sql = PostgresMountGearPassiveProjectionSql
            .CommonTableExpressions;
        Check.True(
            sql.Contains("mount.slot_index = 20", StringComparison.Ordinal) &&
            sql.Contains("revision.sealed_at IS NOT NULL",
                StringComparison.Ordinal) &&
            sql.Contains("mount.user_id = @characterId",
                StringComparison.Ordinal) &&
            sql.Contains(
                "gear.slot_index BETWEEN 15 AND 19",
                StringComparison.Ordinal) &&
            sql.Contains(
                "gear.holy_socket_count BETWEEN 1 AND 2",
                StringComparison.Ordinal) &&
            sql.Contains("socket.effect_id IN (21, 22)",
                StringComparison.Ordinal) &&
            sql.Contains("loadout_roll_rank <= 2",
                StringComparison.Ordinal),
            "SQL projection enforces mount, native sockets, effects, and top-two hosts");
        Check.True(
            sql.Contains("host.item_quality", StringComparison.Ordinal) &&
            sql.Contains("host.item_grade", StringComparison.Ordinal) &&
            sql.Contains("host.attribute5", StringComparison.Ordinal),
            "SQL projects Daedalus from quality and Hephaestus from ordinary grade attributes");
        Check.True(
            PostgresCharacterRuntimeItemProjectionSql.CalculatedStatsForCharacter
                .Contains(
                    "mount_gear_spirit_stat_values",
                    StringComparison.Ordinal),
            "durable character stat calculation consumes Zephyr deltas");
    }

    private static MountGearPassiveHost Host(
        MountGearPassiveAggregate aggregate,
        int equipmentSlot) =>
        aggregate.Hosts.Single(host =>
            host.EquipmentSlot == equipmentSlot);

    private static GameCharacter CharacterWith(
        params (int Slot, CompactItemEntry Item)[] entries)
    {
        var character = new GameCharacter
        {
            Level = 120,
            Profession = 0
        };
        foreach (var entry in entries)
        {
            character.Equipment = EquipmentSlots.SetSlot(
                character.Equipment,
                character.Profession,
                entry.Slot,
                entry.Item.ToCompactString());
        }

        return character;
    }

    private static CompactItemEntry SpiritHost(
        uint itemId,
        params (short EffectId, short Level, short Value)[] rolls)
    {
        var item = Item(itemId) with
        {
            SocketCount = checked((short)rolls.Length)
        };
        if (rolls.Length > 0)
        {
            item = item with
            {
                Socket1EffectId = rolls[0].EffectId,
                Socket1Level = rolls[0].Level,
                Socket1Value = rolls[0].Value
            };
        }
        if (rolls.Length > 1)
        {
            item = item with
            {
                Socket2EffectId = rolls[1].EffectId,
                Socket2Level = rolls[1].Level,
                Socket2Value = rolls[1].Value
            };
        }

        return item;
    }

    private static CompactItemEntry Item(uint itemId) =>
        CompactItemEntry.Empty with
        {
            Id = itemId,
            Quality = 1,
            Grade = 1,
            Stack = 1
        };
}
