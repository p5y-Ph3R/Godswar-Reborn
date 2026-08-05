using Godswar.Server.Application.Commands;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private static CommandFamily?
        ResolveSecureGearMentorCommandFamily(int wireSubId) =>
        wireSubId switch
        {
            GearEnhancerProtocol.DecomposeGearSubId =>
                CommandFamily.GearMentorDecomposeGear,
            GearEnhancerProtocol.EnhanceAttributeSubId =>
                CommandFamily.GearMentorEnhanceAttribute,
            GearEnhancerProtocol.AddAttributeSubId =>
                CommandFamily.GearMentorAddAttribute,
            GearEnhancerProtocol.MakeAttributeStoneSubId =>
                CommandFamily.GearMentorMakeAttributeStone,
            GearEnhancerProtocol.DeleteAttributesSubId =>
                CommandFamily.GearMentorDeleteAttribute,
            GearEnhancerProtocol.TransformCrystalSubId =>
                CommandFamily.GearMentorTransformCrystal,
            GearEnhancerProtocol.CombineGemPiecesMenuSubId =>
                CommandFamily.GearMentorCombineGemPieces,
            _ => null
        };
}
