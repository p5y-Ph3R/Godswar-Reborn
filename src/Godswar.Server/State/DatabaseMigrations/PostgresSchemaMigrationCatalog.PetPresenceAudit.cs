namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreatePetPresenceAuditOperation() => new(
            "20260728_015_pet_presence_audit_operation",
            "Extend the authoritative pet audit vocabulary with carried-pet selection",
            """
            ALTER TABLE public.pet_operation_audit
                ADD CONSTRAINT ck_pet_operation_audit_operation_v2
                CHECK (
                    operation IN (
                        'owner_merge',
                        'pet_merge',
                        'rebirth',
                        'soul_contract',
                        'take',
                        'summon',
                        'dismiss',
                        'reveal_growth',
                        'seal',
                        'unseal'
                    )
                )
                NOT VALID;

            ALTER TABLE public.pet_operation_audit
                VALIDATE CONSTRAINT
                    ck_pet_operation_audit_operation_v2;

            ALTER TABLE public.pet_operation_audit
                DROP CONSTRAINT
                    pet_operation_audit_operation_check;

            ALTER TABLE public.pet_operation_audit
                RENAME CONSTRAINT
                    ck_pet_operation_audit_operation_v2
                TO pet_operation_audit_operation_check;
            """);
}
