using System.Text;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class HolySpiritEffectivenessPolicyChecks
{
    private static void AssertCooledReductionBalance()
    {
        AssertGradeTenBracket(9080, 220, 800,
            HolySpiritValueKind.HundredthPercent,
            "physical history accepts the former 8.00% envelope");
        AssertGradeTenBracket(9081, 220, 800,
            HolySpiritValueKind.HundredthPercent,
            "magic history accepts the former 8.00% envelope");
        AssertGradeTenBracket(9086, 280, 700,
            HolySpiritValueKind.HundredthPercent,
            "critical history accepts the former 7.00% envelope");

        var balance = new HolySpiritBalanceSnapshot(
            55,
            55,
            60,
            7,
            DateTimeOffset.Parse("2026-08-21T00:00:00Z"),
            "protocol-check");
        balance.Validate();
        var maximumSource = new BoundaryRandomSource(useMaximum: true);
        Check.Equal(
            550,
            HolySpiritEffectivenessPolicy.Roll(
                9080, 10, false,
                balance.CooledPhysicalReductionGradeOneMaximum,
                maximumSource).Value,
            "pinned physical balance caps new Grade-10 rolls at 5.50%");
        Check.Equal(
            550,
            HolySpiritEffectivenessPolicy.Roll(
                9081, 10, false,
                balance.CooledMagicReductionGradeOneMaximum,
                maximumSource).Value,
            "pinned magic balance caps new Grade-10 rolls at 5.50%");
        Check.Equal(
            600,
            HolySpiritEffectivenessPolicy.Roll(
                9086, 10, false,
                balance.CooledCriticalReductionGradeOneMaximum,
                maximumSource).Value,
            "pinned critical balance caps new Grade-10 rolls at 6.00%");

        AssertGradeTenBracket(9082, 160, 400,
            HolySpiritValueKind.Flat,
            "physical flat absorption stays a distinct channel");
        AssertGradeTenBracket(9083, 140, 350,
            HolySpiritValueKind.Flat,
            "magic flat absorption stays a distinct channel");
        AssertGradeTenBracket(9087, 400, 1000,
            HolySpiritValueKind.Flat,
            "critical flat reduction stays a distinct channel");

        var migration = PostgresSchemaMigrationCatalog
            .CreateCooledHolyStoneBalance();
        Check.Equal(
            "20260821_098_cooled_holy_stone_balance",
            migration.Id,
            "Cooled balance uses its reserved forward migration identity");
        Check.Equal(
            "8C9F55A94E23E86140750E6AA9CEE6E787F89123234AED54DEC4A29066EC14AB",
            migration.Checksum,
            "applied Cooled migration 098 remains checksum-identical");
        for (var ordinal = 1; ordinal <= 4; ordinal++)
        {
            var prefix = $"holy_socket{ordinal}";
            Check.True(
                migration.Sql.Contains(
                    $"{prefix}_effect_id IN (",
                    StringComparison.Ordinal) &&
                migration.Sql.Contains(
                    $"{prefix}_value =",
                    StringComparison.Ordinal),
                $"socket {ordinal} is included in the max-roll migration");
            Check.True(
                migration.Sql.Contains(
                    $"THEN {prefix}_level * 80",
                    StringComparison.Ordinal),
                $"socket {ordinal} upgrades only the prior 5.5% max roll");
        }
        Check.True(
            migration.Sql.Contains("* 55", StringComparison.Ordinal) &&
            !migration.Sql.Contains(
                "holy_socket1_effect_id IN (13",
                StringComparison.Ordinal) &&
            !migration.Sql.Contains("* 70", StringComparison.Ordinal),
            "critical and flat Cooled channels are not rewritten");

        var mutable = PostgresSchemaMigrationCatalog
            .CreateHolySpiritBalanceSettings();
        Check.Equal(
            "20260821_099_holy_spirit_balance_settings",
            mutable.Id,
            "mutable balance uses the next forward migration identity");
        Check.Equal(
            "83A3D45CE103DD9D9A917F3B246BE2462EEED69AF4B2971C9C8BD79A4C331678",
            mutable.Checksum,
            "mutable balance migration checksum is review-pinned");
        Check.True(
            mutable.Sql.Contains(
                "VALUES (1, 55, 55, 60, 0, 'migration-099')",
                StringComparison.Ordinal) &&
            mutable.Sql.Contains(
                "AND revision = @expectedRevision",
                StringComparison.Ordinal) == false,
            "migration seeds one reviewed mutable balance snapshot");
        for (var ordinal = 1; ordinal <= 4; ordinal++)
        {
            var prefix = $"holy_socket{ordinal}";
            Check.True(
                mutable.Sql.Contains(
                    $"::smallint[])[{prefix}_level]",
                    StringComparison.Ordinal) &&
                mutable.Sql.Contains(
                    $"CASE {prefix}_effect_id",
                    StringComparison.Ordinal),
                $"socket {ordinal} clamps explicit values and oversized " +
                "legacy NULL fallbacks");
        }

        var projection = PostgresCharacterRuntimeItemProjectionSql
            .CalculatedStatsForCharacter;
        Check.True(
            projection.Contains(
                "@cooledPhysicalReductionGradeOneMaximum",
                StringComparison.Ordinal) &&
            projection.Contains(
                "@cooledMagicReductionGradeOneMaximum",
                StringComparison.Ordinal) &&
            projection.Contains(
                "@cooledCriticalReductionGradeOneMaximum",
                StringComparison.Ordinal) &&
            !projection.Contains(
                "FROM public.holy_spirit_balance_settings",
                StringComparison.Ordinal),
            "combat SQL consumes the startup-pinned snapshot through parameters");
        using var command = new NpgsqlCommand();
        PostgresHolySpiritBalanceBinding.AddParameters(command, balance);
        Check.True(
            command.Parameters.Count == 3 &&
            Convert.ToInt32(command.Parameters[0].Value) == 55 &&
            Convert.ToInt32(command.Parameters[1].Value) == 55 &&
            Convert.ToInt32(command.Parameters[2].Value) == 60,
            "projection binding carries the exact pinned balance values");
        Check.True(
            PostgresHolySpiritBalanceStore.UpdateSql.Contains(
                "AND revision = @expectedRevision",
                StringComparison.Ordinal) &&
            PostgresHolySpiritBalanceStore.UpdateSql.Contains(
                "updated_by = @updatedBy",
                StringComparison.Ordinal) &&
            PostgresHolySpiritBalanceStore.ClampSocketsSql.Contains(
                "(13, @criticalMaximum)",
                StringComparison.Ordinal) &&
            PostgresHolySpiritBalanceStore.ClampSocketsSql.Contains(
                "::smallint[])[holy_socket4_level]",
                StringComparison.Ordinal),
            "management CAS also clamps explicit and oversized legacy NULL " +
            "values across every persisted socket channel");
        AssertHistoricalReceiptReplay(9080, 9, 800);
        AssertHistoricalReceiptReplay(9086, 13, 700);
    }

    private static void AssertHistoricalReceiptReplay(
        uint spiritItemId,
        short effectId,
        short value)
    {
        var before = CompactItemEntry.Empty with
        {
            Id = 9031,
            Quality = 1,
            Grade = 10,
            Bound = 1,
            Stack = 1
        };
        var after = before with
        {
            SocketCount = 1,
            Socket1EffectId = effectId,
            Socket1Level = 10,
            Socket1Value = value
        };
        var spirit = CompactItemEntry.Empty with
        {
            Id = spiritItemId,
            Quality = 1,
            Grade = 1,
            Bound = 1,
            Stack = 1
        };
        var beforeState = before.ToCompactString();
        var afterState = after.ToCompactString();
        var spiritState = spirit.ToCompactString();
        var nativeResult = HolySpiritNativeResult.GetResultSubId(
            HolyStoneCommandOperation.ImplementSpirit,
            HolyStoneCommandResultStatus.SpiritImplemented,
            beforeState,
            afterState,
            spiritState);
        var receipt = new HolyStoneExecutionReceipt(
            characterId: 1,
            HolyStoneCommandOperation.ImplementSpirit,
            HolyStoneCommandEnvelope.SpartaNpcId,
            HolyStoneCommandEnvelope.DialogIndex,
            HolyStoneCommandResultStatus.SpiritImplemented,
            nativeResult,
            HolyStoneTargetLocation.KitBag,
            targetSlot: 0,
            HolyStoneCommandEnvelope.ServerSelectedSocketIndex,
            targetItemInstanceId: 1,
            beforeState,
            beforeState,
            afterState,
            stoneKitBagSlot: 1,
            stoneItemInstanceId: 2,
            spiritState,
            spiritState,
            "[]",
            outputKitBagSlot: -1,
            outputItemInstanceId: null,
            outputBeforeCompactItemState: null,
            outputAfterCompactItemState: null,
            goldSpent: 0,
            goldBefore: 0,
            goldAfter: 0,
            walletRevision: 0,
            inventoryRevision: 1,
            auditReference: "42",
            outboxEventId: Guid.Parse(
                "11111111-1111-1111-1111-111111111111"));
        var payload = HolyStonePersistenceCodec.Encode(receipt);
        var replayed = HolyStonePersistenceCodec.DecodeAndVerify(
            Encoding.UTF8.GetString(payload),
            HolyStonePersistenceCodec.Hash(payload),
            "spirit_implemented",
            expectedAuditId: 42,
            HolyStoneCommandOperation.ImplementSpirit);
        Check.Equal(
            value,
            CompactItemEntry.Parse(
                replayed.AuthoritativeTargetAfterCompactItemState)
                .Socket1Value!.Value,
            $"historical effect {effectId} receipt survives live cap decrease");
    }

    private static void AssertGradeTenBracket(
        uint itemId,
        int expectedMinimum,
        int expectedMaximum,
        HolySpiritValueKind expectedKind,
        string context)
    {
        Check.True(
            HolySpiritEffectivenessPolicy.TryGetDefinition(
                itemId,
                out var definition) &&
            definition.ValueKind == expectedKind &&
            HolySpiritEffectivenessPolicy.TryGetGradeBracket(
                itemId,
                10,
                out var minimum,
                out var maximum) &&
            minimum == expectedMinimum &&
            maximum == expectedMaximum,
            context);
    }
}
