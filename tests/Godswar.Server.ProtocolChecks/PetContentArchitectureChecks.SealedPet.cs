namespace Godswar.Server.ProtocolChecks;

internal static partial class PetContentArchitectureChecks
{
    private static void AssertSealedPetReaderComposition(string root)
    {
        var runtime = Read(
            root,
            "src/Godswar.Server/Infrastructure/" +
            "PostgresApplicationDataRuntime.cs");
        Check.True(
            runtime.Contains(
                "SealedPetSnapshots = characterReader;",
                StringComparison.Ordinal) &&
            runtime.Contains(
                "ISealedPetSnapshotReader SealedPetSnapshots",
                StringComparison.Ordinal),
            "PostgreSQL exposes the concrete sealed-pet reader before " +
            "the ordinary snapshot reader is wrapped");

        var composition = Read(
            root,
            "src/Godswar.Server/ServerGameplayPersistenceComposition.cs");
        Check.True(
            composition.Contains(
                "postgresRuntime.SealedPetSnapshots",
                StringComparison.Ordinal) &&
            composition.Contains(
                "ISealedPetSnapshotReader SealedPetSnapshots",
                StringComparison.Ordinal),
            "gameplay persistence carries the sealed-pet reader as an " +
            "explicit production dependency");

        var factory = Read(
            root,
            "src/Godswar.Server/GameClientHandlerFactory.cs");
        Check.True(
            factory.Contains(
                "ISealedPetSnapshotReader sealedPetSnapshots",
                StringComparison.Ordinal) &&
            factory.Contains(
                "sealedPetSnapshots:",
                StringComparison.Ordinal),
            "the game-handler factory wires the explicit sealed-pet reader");

        var program = Read(root, "src/Godswar.Server/Program.cs");
        Check.True(
            program.Contains(
                "gameplayPersistence.SealedPetSnapshots",
                StringComparison.Ordinal),
            "the production host supplies the sealed-pet reader to the " +
            "game-handler factory");
    }
}
