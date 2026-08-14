namespace Godswar.Server.State;

internal enum ElementalResonanceEffectKind : byte
{
    PrometheusBurn,
    PrometheusDetonation,
    PoseidonRecoveryPulse,
    PoseidonFifthHitGuard,
    PoseidonGuardRecovery,
    ZeusBolt,
    ZeusChain,
    ZeusStormCrown,
    GaiaMaximumHealth,
    GaiaMitigation,
    GaiaReflection,
    AeolusMovementSpeed,
    AeolusMomentum,
    AeolusEvasion,
    ApolloRecovery,
    ApolloBarrier,
    ApolloLethalProtection,
    HadesLifeSteal,
    HadesExecute,
    HadesKillRestoration
}

internal abstract record ElementalResonanceParameters;

internal sealed record BurnParameters(
    int TotalDamageBasisPoints,
    int DurationMilliseconds,
    int TickCount,
    bool NonStacking,
    bool PreserveStrongerBurn) : ElementalResonanceParameters;

internal sealed record DetonationParameters(
    int EveryCommittedDirectHit,
    int TriggeringHitDamageBasisPoints,
    bool DetonateRemainingBurn,
    bool ReapplyBurn) : ElementalResonanceParameters;

internal sealed record PeriodicMaxResourceRecoveryParameters(
    int IntervalMilliseconds,
    int MaximumHealthBasisPoints,
    int MaximumManaBasisPoints) : ElementalResonanceParameters;

internal sealed record IncomingHitGuardParameters(
    int EveryIncomingDirectHit,
    int DamageReductionBasisPoints) : ElementalResonanceParameters;

internal sealed record PreventedDamageRecoveryParameters(
    int HealthRecoveryBasisPoints,
    int ManaRecoveryBasisPoints,
    int MaximumHealthCapBasisPoints,
    int MaximumManaCapBasisPoints) : ElementalResonanceParameters;

internal sealed record TriggeredDirectDamageParameters(
    int EveryCommittedDirectHit,
    int AppliedDamageBasisPoints) : ElementalResonanceParameters;

internal sealed record ChainDamageParameters(
    int AdditionalTargetOrdinal,
    int RangeMillimeters,
    int OriginalAppliedHitBasisPoints) : ElementalResonanceParameters;

internal sealed record StormCrownParameters(
    int AdditionalTargetOrdinal,
    int OriginalAppliedHitBasisPoints,
    int PrimaryNonBossStunMilliseconds) : ElementalResonanceParameters;

internal sealed record StatBonusParameters(
    int StatBasisPoints) : ElementalResonanceParameters;

internal sealed record IncomingDamageMitigationParameters(
    int FinalDamageReductionBasisPoints) : ElementalResonanceParameters;

internal sealed record ReflectionParameters(
    int PostMitigationDamageBasisPoints,
    int AttackerMaximumHealthCapBasisPoints,
    bool CanTriggerReflection) : ElementalResonanceParameters;

internal sealed record MomentumParameters(
    int AcceptedMovementMillimeters,
    int OpportunityMilliseconds,
    int NextHitDamageBasisPoints,
    bool ConsumeOnHit) : ElementalResonanceParameters;

internal sealed record IncomingHitEvasionParameters(
    int EveryIncomingHit) : ElementalResonanceParameters;

internal sealed record RecoveryPulseAmplificationParameters(
    int RecoveryBasisPoints) : ElementalResonanceParameters;

internal sealed record OverhealBarrierParameters(
    int OverhealBasisPoints,
    int MaximumHealthCapBasisPoints) : ElementalResonanceParameters;

internal sealed record LethalBarrierParameters(
    int RemainingHealthPoints,
    bool RequiresBarrier,
    bool ConsumeBarrier) : ElementalResonanceParameters;

internal sealed record AppliedDamageHealingParameters(
    int AppliedDamageBasisPoints,
    int MaximumHealthCapPerHitBasisPoints) : ElementalResonanceParameters;

internal sealed record LowHealthDamageParameters(
    int TargetHealthThresholdBasisPoints,
    int DamageBasisPoints) : ElementalResonanceParameters;

internal sealed record KillResourceRestorationParameters(
    int MaximumHealthBasisPoints,
    int MaximumManaBasisPoints) : ElementalResonanceParameters;

internal sealed record ElementalResonanceTierDefinition(
    ElementKind Element,
    int RequiredPieces,
    ElementalResonanceEffectKind Effect,
    ElementalResonanceParameters Parameters,
    bool ReplacesLowerTierOfSameEffect = false);

internal static class ElementalResonanceCatalog
{
    // The deterministic policy/state-machine implementation consumes these
    // definitions. Runtime adapters still opt in explicitly; profile
    // calculation alone never applies an effect or admits a PvP target.
    public const bool GameplayExecutionEnabled = true;

    public static IReadOnlyList<ElementalResonanceTierDefinition> All { get; } =
    [
        Tier(ElementKind.Fire, 3, ElementalResonanceEffectKind.PrometheusBurn,
            new BurnParameters(600, 3_000, 3, true, true)),
        Tier(ElementKind.Fire, 6, ElementalResonanceEffectKind.PrometheusBurn,
            new BurnParameters(1_000, 4_000, 4, true, true), true),
        Tier(ElementKind.Fire, 10,
            ElementalResonanceEffectKind.PrometheusDetonation,
            new DetonationParameters(5, 1_200, true, true)),

        Tier(ElementKind.Water, 3,
            ElementalResonanceEffectKind.PoseidonRecoveryPulse,
            new PeriodicMaxResourceRecoveryParameters(6_000, 100, 100)),
        Tier(ElementKind.Water, 6,
            ElementalResonanceEffectKind.PoseidonFifthHitGuard,
            new IncomingHitGuardParameters(5, 2_500)),
        Tier(ElementKind.Water, 10,
            ElementalResonanceEffectKind.PoseidonGuardRecovery,
            new PreventedDamageRecoveryParameters(5_000, 2_500, 300, 300)),

        Tier(ElementKind.Lightning, 3,
            ElementalResonanceEffectKind.ZeusBolt,
            new TriggeredDirectDamageParameters(4, 1_500)),
        Tier(ElementKind.Lightning, 6,
            ElementalResonanceEffectKind.ZeusChain,
            new ChainDamageParameters(1, 5_000, 1_000)),
        Tier(ElementKind.Lightning, 10,
            ElementalResonanceEffectKind.ZeusStormCrown,
            new StormCrownParameters(2, 500, 1_000)),

        Tier(ElementKind.Earth, 3,
            ElementalResonanceEffectKind.GaiaMaximumHealth,
            new StatBonusParameters(800)),
        Tier(ElementKind.Earth, 6,
            ElementalResonanceEffectKind.GaiaMitigation,
            new IncomingDamageMitigationParameters(800)),
        Tier(ElementKind.Earth, 10,
            ElementalResonanceEffectKind.GaiaReflection,
            new ReflectionParameters(1_500, 200, false)),

        Tier(ElementKind.Wind, 3,
            ElementalResonanceEffectKind.AeolusMovementSpeed,
            new StatBonusParameters(500)),
        Tier(ElementKind.Wind, 6,
            ElementalResonanceEffectKind.AeolusMomentum,
            new MomentumParameters(5_000, 3_000, 1_000, true)),
        Tier(ElementKind.Wind, 10,
            ElementalResonanceEffectKind.AeolusEvasion,
            new IncomingHitEvasionParameters(6)),

        Tier(ElementKind.Light, 3,
            ElementalResonanceEffectKind.ApolloRecovery,
            new RecoveryPulseAmplificationParameters(1_000)),
        Tier(ElementKind.Light, 6,
            ElementalResonanceEffectKind.ApolloBarrier,
            new OverhealBarrierParameters(5_000, 1_000)),
        Tier(ElementKind.Light, 10,
            ElementalResonanceEffectKind.ApolloLethalProtection,
            new LethalBarrierParameters(1, true, true)),

        Tier(ElementKind.Dark, 3,
            ElementalResonanceEffectKind.HadesLifeSteal,
            new AppliedDamageHealingParameters(200, 200)),
        Tier(ElementKind.Dark, 6,
            ElementalResonanceEffectKind.HadesExecute,
            new LowHealthDamageParameters(2_500, 1_200)),
        Tier(ElementKind.Dark, 10,
            ElementalResonanceEffectKind.HadesKillRestoration,
            new KillResourceRestorationParameters(800, 800))
    ];

    private static readonly IReadOnlyDictionary<
        ElementKind,
        IReadOnlyList<ElementalResonanceTierDefinition>> ByElement =
        Enum.GetValues<ElementKind>().ToDictionary(
            static element => element,
            element => (IReadOnlyList<ElementalResonanceTierDefinition>)
                Array.AsReadOnly(All
                    .Where(value => value.Element == element)
                    .OrderBy(static value => value.RequiredPieces)
                    .ToArray()));

    public static IReadOnlyList<ElementalResonanceTierDefinition> For(
        ElementKind element) => ByElement[element];

    public static IReadOnlyList<ElementalResonanceTierDefinition> ActiveFor(
        ElementKind element,
        int equippedCount)
    {
        if (equippedCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(equippedCount));
        }

        return Array.AsReadOnly(ByElement[element]
            .Where(value => value.RequiredPieces <= equippedCount)
            .ToArray());
    }

    private static ElementalResonanceTierDefinition Tier(
        ElementKind element,
        int requiredPieces,
        ElementalResonanceEffectKind effect,
        ElementalResonanceParameters parameters,
        bool replacesLowerTierOfSameEffect = false) =>
        new(
            element,
            requiredPieces,
            effect,
            parameters,
            replacesLowerTierOfSameEffect);
}
