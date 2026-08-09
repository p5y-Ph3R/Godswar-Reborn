using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task PublishPriestHealCastVisualAsync(
        GamePacket packet,
        PriestHealTarget? singleTarget,
        CancellationToken cancellationToken)
    {
        var character = _character!;
        var casterWorldObjectId =
            WorldObjectIds.ForPlayer(character.Id);
        var selfTargeted = singleTarget is null ||
                           singleTarget.Value.IsCaster;
        await _session.SendAsync(
            selfTargeted
                ? PacketBuilder.SelfTargetSkillCastVisual(
                    packet.Buffer,
                    LocalPlayerObjectId)
                : PacketBuilder.SkillCastVisual(
                    packet.Buffer,
                    LocalPlayerObjectId),
            cancellationToken,
            "PriestHealCastSelf");
        await _registry.BroadcastToMapAsync(
            character.CurrentMap,
            selfTargeted
                ? PacketBuilder.SelfTargetSkillCastVisual(
                    packet.Buffer,
                    casterWorldObjectId)
                : PacketBuilder.SkillCastVisual(
                    packet.Buffer,
                    casterWorldObjectId),
            cancellationToken,
            _session,
            "PriestHealCastWorld");
    }

    private async Task PublishPriestHealVitalsAsync(
        PriestHealTarget target,
        CancellationToken cancellationToken)
    {
        int currentHp;
        int currentMp;
        lock (target.Character.VitalsSync)
        {
            currentHp = target.Character.CurrentHp;
            currentMp = target.Character.CurrentMp;
        }

        try
        {
            await target.Session.SendAsync(
                PacketBuilder.PlayerVitalsUpdate(
                    LocalPlayerObjectId,
                    currentHp,
                    currentMp),
                cancellationToken,
                "PriestHealVitalsSelf");
        }
        catch (Exception ex) when (
            ex is IOException or ObjectDisposedException)
        {
            _registry.Remove(target.Session);
        }

        await _registry.BroadcastToMapAsync(
            target.Character.CurrentMap,
            PacketBuilder.PlayerVitalsUpdate(
                target.WorldObjectId,
                currentHp,
                currentMp),
            cancellationToken,
            target.Session,
            "PriestHealVitalsWorld");
    }

    private async Task PublishPriestHealCombatTextAsync(
        SkillCastRequest cast,
        PriestHealingSkillKind kind,
        IReadOnlyList<PriestHealResult> results,
        CancellationToken cancellationToken)
    {
        var visible = results
            .Where(static result => result.CombatTextAmount > 0)
            .ToArray();
        if (visible.Length == 0)
        {
            return;
        }

        var character = _character!;
        var casterWorldObjectId =
            WorldObjectIds.ForPlayer(character.Id);
        await SendPriestHealCombatTextAsync(
            _session,
            recipientIsCaster: true,
            casterWorldObjectId,
            cast.SkillId,
            kind,
            visible,
            cancellationToken);

        foreach (var recipient in _registry.GetMapSessions(
                     character.CurrentMap,
                     _session))
        {
            if (!_registry.IsCurrentWorldSessionSnapshot(
                    _session,
                    recipient))
            {
                continue;
            }

            await SendPriestHealCombatTextAsync(
                recipient.Session,
                recipientIsCaster: false,
                casterWorldObjectId,
                cast.SkillId,
                kind,
                visible,
                cancellationToken);
        }
    }

    private async Task SendPriestHealCombatTextAsync(
        ClientSession recipient,
        bool recipientIsCaster,
        uint casterWorldObjectId,
        uint skillId,
        PriestHealingSkillKind kind,
        IReadOnlyList<PriestHealResult> results,
        CancellationToken cancellationToken)
    {
        var healerObjectId = recipientIsCaster
            ? LocalPlayerObjectId
            : casterWorldObjectId;
        byte[] combatText;
        if (kind == PriestHealingSkillKind.SingleTarget)
        {
            var result = results[0];
            combatText = PacketBuilder.SkillHealing(
                healerObjectId,
                ReferenceEquals(recipient, result.Target.Session)
                    ? LocalPlayerObjectId
                    : result.Target.WorldObjectId,
                result.CombatTextAmount,
                skillId,
                result.Target.Character.PositionX,
                result.Target.Character.PositionZ);
        }
        else
        {
            combatText = PacketBuilder.SkillClusterHealing(
                healerObjectId,
                skillId,
                results.Select(result =>
                        new SkillClusterHealingEntry(
                            ReferenceEquals(
                                recipient,
                                result.Target.Session)
                                ? LocalPlayerObjectId
                                : result.Target.WorldObjectId,
                            result.CombatTextAmount))
                    .ToArray());
        }

        try
        {
            await recipient.SendAsync(
                combatText,
                cancellationToken,
                recipientIsCaster
                    ? "PriestHealCombatTextSelf"
                    : "PriestHealCombatTextWorld");
        }
        catch (Exception ex) when (
            ex is IOException or ObjectDisposedException)
        {
            _registry.Remove(recipient);
        }
    }

    private async Task PublishPriestHealCasterManaAsync(
        int currentMana,
        CancellationToken cancellationToken)
    {
        try
        {
            await _session.SendAsync(
                PacketBuilder.PlayerManaUpdate(
                    LocalPlayerObjectId,
                    currentMana),
                cancellationToken,
                "PriestHealMana");
        }
        catch (Exception ex) when (
            ex is IOException or ObjectDisposedException)
        {
            _registry.Remove(_session);
        }
    }

    private async Task PublishPriestHealImpactAsync(
        SkillCastRequest cast,
        PriestHealTarget? singleTarget,
        CancellationToken cancellationToken)
    {
        var character = _character!;
        var casterWorldObjectId =
            WorldObjectIds.ForPlayer(character.Id);
        var areaHeal = singleTarget is null;
        var target = singleTarget ?? new PriestHealTarget(
            _session,
            character.AccountId,
            character,
            casterWorldObjectId,
            IsCaster: true,
            WorldContext: null);
        var targetX = target.Character.PositionX;
        var targetZ = target.Character.PositionZ;
        await _session.SendAsync(
            PacketBuilder.SkillCastImpact(
                LocalPlayerObjectId,
                areaHeal
                    ? uint.MaxValue
                    : target.IsCaster
                    ? LocalPlayerObjectId
                    : target.WorldObjectId,
                cast.SkillId,
                targetX,
                targetZ),
            cancellationToken,
            "PriestHealImpactSelf");
        await _registry.BroadcastToMapAsync(
            character.CurrentMap,
            PacketBuilder.SkillCastImpact(
                casterWorldObjectId,
                areaHeal
                    ? uint.MaxValue
                    : target.WorldObjectId,
                cast.SkillId,
                targetX,
                targetZ),
            cancellationToken,
            _session,
            "PriestHealImpactWorld");
    }

    private async Task PersistPriestHealVitalsAsync(
        IReadOnlyList<PriestHealResult> results,
        CancellationToken cancellationToken)
    {
        var character = _character!;
        var persisted = new HashSet<int>();
        foreach (var result in results)
        {
            if (result.Applied <= 0 &&
                !result.Target.IsCaster ||
                !persisted.Add(result.Target.Character.Id))
            {
                continue;
            }

            try
            {
                await _registry.PersistPlayerVitalsAsync(
                    result.Target.AccountId,
                    result.Target.Character,
                    cancellationToken);
            }
            catch (Exception ex) when (
                ex is not OperationCanceledException)
            {
                Console.WriteLine(
                    $"[skill] priest heal vitals persistence deferred " +
                    $"character={result.Target.Character.Name}: " +
                    ex.Message);
            }
        }

        if (!persisted.Contains(character.Id))
        {
            try
            {
                await _registry.PersistPlayerVitalsAsync(
                    character.AccountId,
                    character,
                    cancellationToken);
            }
            catch (Exception ex) when (
                ex is not OperationCanceledException)
            {
                Console.WriteLine(
                    $"[skill] priest heal caster vitals persistence " +
                    $"deferred character={character.Name}: " + ex.Message);
            }
        }
    }

    private readonly record struct PriestHealTarget(
        ClientSession Session,
        int AccountId,
        GameCharacter Character,
        uint WorldObjectId,
        bool IsCaster,
        GameSessionContext? WorldContext);

    private readonly record struct PriestHealResult(
        PriestHealTarget Target,
        int Before,
        int After,
        int Resolved,
        int Applied,
        int CombatTextAmount);
}
