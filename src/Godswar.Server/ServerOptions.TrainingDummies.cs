using Godswar.Server.Game;

namespace Godswar.Server;

internal sealed partial class ServerOptions
{
    private void ApplyTrainingDummyEnvironment()
    {
        Game.TrainingDummies.Enabled = ReadBool(
            "GODSWAR_TRAINING_DUMMIES_ENABLED",
            Game.TrainingDummies.Enabled);
        var identities = Environment.GetEnvironmentVariable(
            "GODSWAR_TRAINING_DUMMY_IDENTITIES");
        if (identities is not null)
        {
            Game.TrainingDummies.Identities =
                TrainingDummyOptions.ParseIdentities(identities);
        }
    }
}
