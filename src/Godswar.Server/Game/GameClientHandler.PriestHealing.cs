using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task BeginIntonedPriestHealingSkillCastAsync(
        GamePacket packet,
        SkillCastRequest cast,
        SkillCombatDefinition combat,
        PriestHealingSkillDefinition healing,
        CancellationToken cancellationToken)
    {
        var character = _character;
        if (character is null ||
            healing.Kind != PriestHealingSkillKind.Area)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (IsSkillCooldownActive(cast.SkillId, now))
        {
            return;
        }

        int currentMana;
        lock (character.VitalsSync)
        {
            currentMana = character.CurrentMp;
        }

        var manaCost = Math.Max(0, combat.Mp);
        if (currentMana < manaCost)
        {
            await _session.SendAsync(
                PacketBuilder.PlayerManaUpdate(
                    LocalPlayerObjectId,
                    currentMana),
                cancellationToken,
                "PriestHealManaRejected");
            Console.WriteLine(
                $"[skill] rejected priest heal intonation for insufficient MP " +
                $"character={character.Name} skill={cast.SkillId} " +
                $"mp={currentMana} cost={manaCost}");
            return;
        }

        var worldObjectId = CurrentPlayerObjectId;
        var started = await TryBeginPendingSkillCastAsync(
            cast.SkillId,
            combat.CastTime,
            "priest-heal",
            async token =>
            {
                await _session.SendAsync(
                    PacketBuilder.SelfTargetSkillCastVisual(
                        packet.Buffer,
                        LocalPlayerObjectId),
                    token,
                    "PriestAreaHealCastSelf");
                await _registry.BroadcastToMapAsync(
                    character.CurrentMap,
                    PacketBuilder.SelfTargetSkillCastVisual(
                        packet.Buffer,
                        worldObjectId),
                    token,
                    _session,
                    "PriestAreaHealCastWorld");
            },
            token => HandleSkillCastAsync(
                packet,
                token,
                intonationCompleted: true,
                intonedCombatSnapshot: combat),
            cancellationToken,
            () => IsIntonedPriestHealingCompletionStillValid(
                cast,
                combat,
                healing));
        if (!started)
        {
            Console.WriteLine(
                $"[skill] rejected priest heal intonation while another " +
                $"cast is pending character={character.Name} " +
                $"skill={cast.SkillId}");
        }
    }

    private bool IsIntonedPriestHealingCompletionStillValid(
        SkillCastRequest cast,
        SkillCombatDefinition combat,
        PriestHealingSkillDefinition healing)
    {
        var character = _character;
        if (character is null ||
            healing.Kind != PriestHealingSkillKind.Area)
        {
            return false;
        }

        lock (character.VitalsSync)
        {
            return character.CurrentHp > 0 &&
                   character.CurrentMp >= Math.Max(0, combat.Mp) &&
                   !IsSkillCooldownActive(
                       cast.SkillId,
                       DateTimeOffset.UtcNow);
        }
    }

    private async Task HandlePriestHealingSkillCastAsync(
        GamePacket packet,
        SkillCastRequest cast,
        SkillCombatDefinition combat,
        PriestHealingSkillDefinition healing,
        bool publishCastVisual,
        CancellationToken cancellationToken)
    {
        var character = _character;
        if (character is null ||
            !RevalidateCurrentWorldEffectOwnership(
                "priest_healing_skill"))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (IsSkillCooldownActive(cast.SkillId, now))
        {
            return;
        }

        PriestHealTarget? singleTarget = null;
        if (healing.Kind == PriestHealingSkillKind.SingleTarget)
        {
            if (!TryResolvePriestSingleHealTarget(
                    cast,
                    combat,
                    out var resolvedTarget))
            {
                Console.WriteLine(
                    $"[skill] rejected invalid priest heal target " +
                    $"character={character.Name} skill={cast.SkillId} " +
                    $"target={cast.TargetObjectId}");
                return;
            }

            singleTarget = resolvedTarget;
        }

        var manaCost = Math.Max(0, combat.Mp);
        int currentMana;
        var manaReserved = false;
        lock (character.VitalsSync)
        {
            currentMana = character.CurrentMp;
            if (character.CurrentHp > 0 &&
                currentMana >= manaCost)
            {
                character.CurrentMp -= manaCost;
                currentMana = character.CurrentMp;
                if (manaCost > 0)
                {
                    character.MarkVitalsChanged();
                }

                manaReserved = true;
            }
        }

        if (!manaReserved)
        {
            await _session.SendAsync(
                PacketBuilder.PlayerManaUpdate(
                    LocalPlayerObjectId,
                    currentMana),
                cancellationToken,
                "PriestHealManaRejected");
            Console.WriteLine(
                $"[skill] rejected insufficient MP character={character.Name} " +
                $"skill={cast.SkillId} mp={currentMana} cost={manaCost}");
            return;
        }

        // Ownership can change while a different session is being resolved.
        // Never commit an effect from a stale world owner.
        if (!RevalidateCurrentWorldEffectOwnership(
                "priest_healing_commit"))
        {
            RefundPriestHealMana(character, manaCost);
            return;
        }

        var targets = healing.Kind == PriestHealingSkillKind.Area
            ? ResolvePriestAreaHealTargets(combat)
            : [singleTarget!.Value];
        targets.RemoveAll(target =>
            !CanApplyPriestHealTarget(
                target,
                combat,
                healing.Kind));
        if (targets.Count == 0 ||
            healing.Kind == PriestHealingSkillKind.Area &&
            targets.All(static target => !target.IsCaster))
        {
            RefundPriestHealMana(character, manaCost);
            int refundedMana;
            lock (character.VitalsSync)
            {
                refundedMana = character.CurrentMp;
            }

            await _session.SendAsync(
                PacketBuilder.PlayerManaUpdate(
                    LocalPlayerObjectId,
                    refundedMana),
                cancellationToken,
                "PriestHealManaRefund");
            Console.WriteLine(
                $"[skill] rejected stale priest heal target " +
                $"character={character.Name} skill={cast.SkillId}");
            return;
        }

        _nextSkillCastAt[cast.SkillId] = now + combat.Cooldown;
        _registry.UpdateCharacter(
            _session,
            character,
            advanceWorldRevision: false);

        var results = new List<PriestHealResult>(targets.Count);
        int outgoingHealingBonus;
        lock (character.VitalsSync)
        {
            outgoingHealingBonus =
                character.CalculatedStats?.CureBonus ?? 0;
        }

        foreach (var target in targets)
        {
            results.Add(ApplyPriestHeal(
                target,
                healing.HealAmount,
                outgoingHealingBonus,
                now));
        }

        // The HP/MP mutation is authoritative before any socket write. Queue
        // its checkpoint first so a disconnect during animation publication
        // cannot turn a successful heal into an in-memory-only result.
        await PersistPriestHealVitalsAsync(
            results,
            cancellationToken);

        if (publishCastVisual)
        {
            await PublishPriestHealCastVisualAsync(
                packet,
                singleTarget,
                cancellationToken);
        }

        // Preserve the original client's observed completion order. Single
        // Heal publishes its recovery before impact; Area Heal publishes its
        // position impact before the recovery cluster.
        if (healing.Kind == PriestHealingSkillKind.SingleTarget)
        {
            await PublishPriestHealCombatTextAsync(
                cast,
                healing.Kind,
                results,
                cancellationToken);
            await PublishPriestHealImpactAsync(
                cast,
                singleTarget,
                cancellationToken);
        }
        else
        {
            await PublishPriestHealImpactAsync(
                cast,
                singleTarget,
                cancellationToken);
            await PublishPriestHealCombatTextAsync(
                cast,
                healing.Kind,
                results,
                cancellationToken);
        }

        lock (character.VitalsSync)
        {
            currentMana = character.CurrentMp;
        }

        await PublishPriestHealCasterManaAsync(
            currentMana,
            cancellationToken);

        // A recovery packet is a signed HP delta. Follow it with the absolute
        // authoritative values so loss, caps, or client drift reconcile safely.
        foreach (var result in results)
        {
            await PublishPriestHealVitalsAsync(
                result.Target,
                cancellationToken);
        }

        Console.WriteLine(
            $"[skill] priest heal character={character.Name} " +
            $"skill={cast.SkillId} kind={healing.Kind} " +
            $"targets={results.Count} " +
            $"resolved={results.Sum(static result => (long)result.Resolved)} " +
            $"applied={results.Sum(static result => result.Applied)} " +
            $"mp={currentMana}/{character.MaxMp}");
    }

    private bool IsSkillCooldownActive(
        uint skillId,
        DateTimeOffset now)
    {
        if (!_nextSkillCastAt.TryGetValue(
                skillId,
                out var nextCastAt) ||
            nextCastAt <= now)
        {
            return false;
        }

        Console.WriteLine(
            $"[skill] rejected cooldown " +
            $"character={_character?.Name ?? "<none>"} " +
            $"skill={skillId} " +
            $"remaining={(nextCastAt - now).TotalSeconds:F2}");
        return true;
    }

    private bool TryResolvePriestSingleHealTarget(
        SkillCastRequest cast,
        SkillCombatDefinition combat,
        out PriestHealTarget target)
    {
        var character = _character!;
        var selfTarget = CreatePriestSelfHealTarget(character);
        var casterWorldObjectId = selfTarget.WorldObjectId;
        if (cast.TargetObjectId is 0 or LocalPlayerObjectId ||
            cast.TargetObjectId == casterWorldObjectId)
        {
            target = selfTarget;
            return IsLivingFriendlyTarget(
                character,
                target.Character);
        }

        if (!_registry.TryGetCurrentWorldSessionByObjectId(
                _session,
                character.CurrentMap,
                cast.TargetObjectId,
                out var context) ||
            !IsLivingFriendlyTarget(character, context.Character))
        {
            target = selfTarget;
            return IsLivingFriendlyTarget(character, character);
        }

        if (!SkillCombatResolver.IsWithinRange(
                character.PositionX,
                character.PositionZ,
                context.Character.PositionX,
                context.Character.PositionZ,
                combat))
        {
            target = default;
            return false;
        }

        target = new PriestHealTarget(
            context.Session,
            context.AccountId,
            context.Character,
            context.ObjectId,
            IsCaster: false,
            context);
        return true;
    }

    private PriestHealTarget CreatePriestSelfHealTarget(
        GameCharacter character) =>
        new(
            _session,
            character.AccountId,
            character,
            CurrentPlayerObjectId,
            IsCaster: true,
            WorldContext: null);

    private List<PriestHealTarget> ResolvePriestAreaHealTargets(
        SkillCombatDefinition combat)
    {
        var character = _character!;
        var targets = new List<PriestHealTarget>
        {
            new(
                _session,
                character.AccountId,
                character,
                CurrentPlayerObjectId,
                IsCaster: true,
                WorldContext: null)
        };
        foreach (var context in _registry.GetMapSessions(
                     character.CurrentMap,
                     _session))
        {
            if (!IsLivingFriendlyTarget(
                    character,
                    context.Character) ||
                !SkillCombatResolver.IsWithinArea(
                    character.PositionX,
                    character.PositionZ,
                    context.Character.PositionX,
                    context.Character.PositionZ,
                    combat))
            {
                continue;
            }

            targets.Add(new PriestHealTarget(
                context.Session,
                context.AccountId,
                context.Character,
                context.ObjectId,
                IsCaster: false,
                context));
        }

        return targets;
    }

    private bool CanApplyPriestHealTarget(
        PriestHealTarget target,
        SkillCombatDefinition combat,
        PriestHealingSkillKind kind)
    {
        if (target.IsCaster)
        {
            return RevalidateCurrentWorldEffectOwnership(
                "priest_healing_target");
        }

        var caster = _character!;
        if (target.WorldContext is not { } context ||
            !_registry.IsCurrentWorldSessionSnapshot(
                _session,
                context) ||
            !IsLivingFriendlyTarget(
                caster,
                target.Character))
        {
            return false;
        }

        return kind == PriestHealingSkillKind.Area
            ? SkillCombatResolver.IsWithinArea(
                caster.PositionX,
                caster.PositionZ,
                target.Character.PositionX,
                target.Character.PositionZ,
                combat)
            : SkillCombatResolver.IsWithinRange(
                caster.PositionX,
                caster.PositionZ,
                target.Character.PositionX,
                target.Character.PositionZ,
                combat);
    }

    private static void RefundPriestHealMana(
        GameCharacter character,
        int manaCost)
    {
        if (manaCost <= 0)
        {
            return;
        }

        lock (character.VitalsSync)
        {
            character.CurrentMp = (int)Math.Min(
                Math.Max(0, character.MaxMp),
                (long)character.CurrentMp + manaCost);
            character.MarkVitalsChanged();
        }
    }

    private static bool IsLivingFriendlyTarget(
        GameCharacter caster,
        GameCharacter target)
    {
        lock (target.VitalsSync)
        {
            return target.CurrentHp > 0 &&
                   target.Camp == caster.Camp;
        }
    }

    private PriestHealResult ApplyPriestHeal(
        PriestHealTarget target,
        int baseHeal,
        int outgoingHealingBonusBasisPoints,
        DateTimeOffset authoritativeAt)
    {
        var character = target.Character;
        var resolved = PriestHealingMath.ResolveHealAmount(
            baseHeal,
            outgoingHealingBonusBasisPoints,
            character.CalculatedStats?.BeCureBonus ?? 0);
        resolved = checked((int)Math.Clamp(
            _registry.AdjustElementalHealingReceived(
                target.Session,
                character,
                authoritativeAt,
                resolved),
            0,
            int.MaxValue));
        lock (character.VitalsSync)
        {
            var before = character.CurrentHp;
            if (before <= 0)
            {
                return new PriestHealResult(
                    target,
                    before,
                    before,
                    Resolved: 0,
                    Applied: 0,
                    CombatTextAmount: 0);
            }

            var maximum = Math.Max(1, character.MaxHp);
            var after = (int)Math.Min(
                maximum,
                (long)before + resolved);
            if (after != before)
            {
                character.CurrentHp = after;
                character.MarkVitalsChanged();
            }

            return new PriestHealResult(
                target,
                before,
                after,
                resolved,
                after - before,
                PriestHealingMath.ResolveCombatTextAmount(
                    resolved,
                    after - before));
        }
    }

}
