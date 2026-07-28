namespace Godswar.Server.Game;

internal enum PlayerSkillCastControl
{
    None = 0,
    Stunned,
    Silenced
}

internal enum SkillCastInterruptionReason
{
    ClientRequest = 0,
    Movement,
    Stunned,
    Silenced,
    Death,
    MapTransition,
    Replaced,
    InvalidState
}

/// <summary>
/// Native status IDs which stop an in-progress intonation or prevent a new
/// one. The mappings come from Status.ini: stun/freeze includes
/// HaltIntonate, while Magic Locked uses NonMagicUsing/NonZSUsing and must be
/// enforced explicitly by the authoritative server.
/// </summary>
internal static class PlayerSkillCastControlCatalog
{
    /// <summary>
    /// Resolves controls which continuously prevent a new spell or combat
    /// technique. These are Status.ini entries carrying NonMagicUsing and
    /// NonZSUsing. HaltIntonate-only Frozen statuses deliberately do not
    /// remain a cast blocker after their one-shot interruption.
    /// </summary>
    public static PlayerSkillCastControl ResolveActiveBlock(uint statusId)
    {
        return statusId switch
        {
            330 or 331 => PlayerSkillCastControl.Stunned,
            >= 360 and <= 364 => PlayerSkillCastControl.Silenced,
            >= 400 and <= 402 => PlayerSkillCastControl.Stunned,
            404 => PlayerSkillCastControl.Silenced,
            407 or 408 or 564 or 1433 or 1436 or 1444 =>
                PlayerSkillCastControl.Stunned,
            1446 or 1447 => PlayerSkillCastControl.Stunned,
            1448 or 1449 => PlayerSkillCastControl.Silenced,
            _ => PlayerSkillCastControl.None
        };
    }

    /// <summary>
    /// Resolves a one-shot interruption when a status is applied. Status.ini
    /// Effect 0 (HaltIntonate) interrupts an in-flight cast. Controls which
    /// disable both magic and techniques also interrupt on application even
    /// when their Effect list omits HaltIntonate (for example Magic Locked).
    /// </summary>
    public static SkillCastInterruptionReason? ResolveAppliedInterruption(
        uint statusId)
    {
        if (statusId is >= 299 and <= 305)
        {
            return SkillCastInterruptionReason.Stunned;
        }

        var activeBlock = ResolveActiveBlock(statusId);
        return activeBlock == PlayerSkillCastControl.None
            ? null
            : ToInterruptionReason(activeBlock);
    }

    public static SkillCastInterruptionReason ToInterruptionReason(
        PlayerSkillCastControl control)
    {
        return control switch
        {
            PlayerSkillCastControl.Stunned =>
                SkillCastInterruptionReason.Stunned,
            PlayerSkillCastControl.Silenced =>
                SkillCastInterruptionReason.Silenced,
            _ => SkillCastInterruptionReason.InvalidState
        };
    }
}
