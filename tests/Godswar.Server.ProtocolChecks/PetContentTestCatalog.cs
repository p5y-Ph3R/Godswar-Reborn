using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;

namespace Godswar.Server.ProtocolChecks;

internal static class PetContentTestCatalog
{
    public static IPetContentCatalog Instance { get; } =
        PetContentBaseline.Create();
}
