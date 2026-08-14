using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class ElementalResonanceContractChecks
{
    public static void Run()
    {
        CheckShapeAndActivation();
        CheckPrometheus();
        CheckPoseidon();
        CheckZeus();
        CheckGaia();
        CheckAeolus();
        CheckApollo();
        CheckHades();
        CheckDeterministicExecution();
    }

    private static void CheckShapeAndActivation()
    {
        Check.True(
            ElementalResonanceCatalog.GameplayExecutionEnabled,
            "typed resonance execution policy is enabled without implicit handler wiring");
        Check.Equal(21, ElementalResonanceCatalog.All.Count,
            "seven elements each have three resonance tiers");
        foreach (var element in Enum.GetValues<ElementKind>())
        {
            var definitions = ElementalResonanceCatalog.For(element);
            Check.True(
                definitions.Count == 3 &&
                definitions.All(value => value.Element == element) &&
                definitions.Select(static value => value.RequiredPieces)
                    .SequenceEqual([3, 6, 10]),
                $"{element} has locked 3/6/10 definitions");
            Check.Equal(0,
                ElementalResonanceCatalog.ActiveFor(element, 2).Count,
                $"{element} has no resonance before three pieces");
            Check.True(
                ElementalResonanceCatalog.ActiveFor(element, 3)
                    .Select(static value => value.RequiredPieces)
                    .SequenceEqual([3]) &&
                ElementalResonanceCatalog.ActiveFor(element, 6)
                    .Select(static value => value.RequiredPieces)
                    .SequenceEqual([3, 6]) &&
                ElementalResonanceCatalog.ActiveFor(element, 10)
                    .Select(static value => value.RequiredPieces)
                    .SequenceEqual([3, 6, 10]),
                $"{element} activation is cumulative");
        }

        Check.Throws<ArgumentOutOfRangeException>(
            () => ElementalResonanceCatalog.ActiveFor(ElementKind.Fire, -1),
            "negative equipped count is rejected");
    }

    private static void CheckPrometheus()
    {
        AssertTier(
            ElementKind.Fire,
            3,
            ElementalResonanceEffectKind.PrometheusBurn,
            new BurnParameters(600, 3_000, 3, true, true));
        AssertTier(
            ElementKind.Fire,
            6,
            ElementalResonanceEffectKind.PrometheusBurn,
            new BurnParameters(1_000, 4_000, 4, true, true),
            replacesLowerTier: true);
        AssertTier(
            ElementKind.Fire,
            10,
            ElementalResonanceEffectKind.PrometheusDetonation,
            new DetonationParameters(5, 1_200, true, true));
    }

    private static void CheckPoseidon()
    {
        AssertTier(
            ElementKind.Water,
            3,
            ElementalResonanceEffectKind.PoseidonRecoveryPulse,
            new PeriodicMaxResourceRecoveryParameters(6_000, 100, 100));
        AssertTier(
            ElementKind.Water,
            6,
            ElementalResonanceEffectKind.PoseidonFifthHitGuard,
            new IncomingHitGuardParameters(5, 2_500));
        AssertTier(
            ElementKind.Water,
            10,
            ElementalResonanceEffectKind.PoseidonGuardRecovery,
            new PreventedDamageRecoveryParameters(5_000, 2_500, 300, 300));
    }

    private static void CheckZeus()
    {
        AssertTier(
            ElementKind.Lightning,
            3,
            ElementalResonanceEffectKind.ZeusBolt,
            new TriggeredDirectDamageParameters(4, 1_500));
        AssertTier(
            ElementKind.Lightning,
            6,
            ElementalResonanceEffectKind.ZeusChain,
            new ChainDamageParameters(1, 5_000, 1_000));
        AssertTier(
            ElementKind.Lightning,
            10,
            ElementalResonanceEffectKind.ZeusStormCrown,
            new StormCrownParameters(2, 500, 1_000));
    }

    private static void CheckGaia()
    {
        AssertTier(
            ElementKind.Earth,
            3,
            ElementalResonanceEffectKind.GaiaMaximumHealth,
            new StatBonusParameters(800));
        AssertTier(
            ElementKind.Earth,
            6,
            ElementalResonanceEffectKind.GaiaMitigation,
            new IncomingDamageMitigationParameters(800));
        AssertTier(
            ElementKind.Earth,
            10,
            ElementalResonanceEffectKind.GaiaReflection,
            new ReflectionParameters(1_500, 200, false));
    }

    private static void CheckAeolus()
    {
        AssertTier(
            ElementKind.Wind,
            3,
            ElementalResonanceEffectKind.AeolusMovementSpeed,
            new StatBonusParameters(500));
        AssertTier(
            ElementKind.Wind,
            6,
            ElementalResonanceEffectKind.AeolusMomentum,
            new MomentumParameters(5_000, 3_000, 1_000, true));
        AssertTier(
            ElementKind.Wind,
            10,
            ElementalResonanceEffectKind.AeolusEvasion,
            new IncomingHitEvasionParameters(6));
    }

    private static void CheckApollo()
    {
        AssertTier(
            ElementKind.Light,
            3,
            ElementalResonanceEffectKind.ApolloRecovery,
            new RecoveryPulseAmplificationParameters(1_000));
        AssertTier(
            ElementKind.Light,
            6,
            ElementalResonanceEffectKind.ApolloBarrier,
            new OverhealBarrierParameters(5_000, 1_000));
        AssertTier(
            ElementKind.Light,
            10,
            ElementalResonanceEffectKind.ApolloLethalProtection,
            new LethalBarrierParameters(1, true, true));
    }

    private static void CheckHades()
    {
        AssertTier(
            ElementKind.Dark,
            3,
            ElementalResonanceEffectKind.HadesLifeSteal,
            new AppliedDamageHealingParameters(200, 200));
        AssertTier(
            ElementKind.Dark,
            6,
            ElementalResonanceEffectKind.HadesExecute,
            new LowHealthDamageParameters(2_500, 1_200));
        AssertTier(
            ElementKind.Dark,
            10,
            ElementalResonanceEffectKind.HadesKillRestoration,
            new KillResourceRestorationParameters(800, 800));
    }

    private static void AssertTier(
        ElementKind element,
        int requiredPieces,
        ElementalResonanceEffectKind effect,
        ElementalResonanceParameters parameters,
        bool replacesLowerTier = false)
    {
        var actual = ElementalResonanceCatalog.For(element)
            .Single(value => value.RequiredPieces == requiredPieces);
        Check.True(
            actual.Effect == effect &&
            actual.Parameters == parameters &&
            actual.ReplacesLowerTierOfSameEffect == replacesLowerTier,
            $"{element} {requiredPieces}-piece resonance parameters are locked");
    }
}
