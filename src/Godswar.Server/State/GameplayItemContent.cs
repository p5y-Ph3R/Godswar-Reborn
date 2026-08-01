using Godswar.Server.Application.Items;

namespace Godswar.Server.State;

/// <summary>
/// Immutable gameplay views derived once from one process-pinned item revision.
/// </summary>
internal sealed class GameplayItemContent
{
    public GameplayItemContent(IItemTemplateCatalog templates)
    {
        Templates = templates ?? throw new ArgumentNullException(
            nameof(templates));
        DeveloperMounts = new DeveloperMountCatalog(templates);
        Mounts = new MountCatalog(templates, DeveloperMounts);
    }

    public IItemTemplateCatalog Templates { get; }

    public DeveloperMountCatalog DeveloperMounts { get; }

    public MountCatalog Mounts { get; }
}

internal interface IGameplayItemContentProvider
{
    GameplayItemContent ItemContent { get; }
}
