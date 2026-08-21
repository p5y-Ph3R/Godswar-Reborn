using System.Security.Cryptography;
using System.Text;

namespace Godswar.Server.Application.Coordination;

/// <summary>
/// Identifies the complete process-pinned gameplay content set used by a
/// worker. Coordination must reject placement across workers that pin a
/// different world, item, pet, pet owner-Merge, learned pet-skill, or Holy
/// Spirit balance revision.
/// </summary>
internal static class RuntimeContentFingerprint
{
    public static string Create(
        string worldRevision,
        string itemRevision,
        string petRevision,
        string petOwnerMergeRevision,
        string petLearnedSkillRevision,
        string holySpiritBalanceRevision)
    {
        ValidateRevision(worldRevision, nameof(worldRevision));
        ValidateRevision(itemRevision, nameof(itemRevision));
        ValidateRevision(petRevision, nameof(petRevision));
        ValidateRevision(
            petOwnerMergeRevision,
            nameof(petOwnerMergeRevision));
        ValidateRevision(
            petLearnedSkillRevision,
            nameof(petLearnedSkillRevision));
        ValidateRevision(
            holySpiritBalanceRevision,
            nameof(holySpiritBalanceRevision));

        var canonical = Encoding.UTF8.GetBytes(
            "runtime-content-v5\n" +
            $"world:{worldRevision}\n" +
            $"items:{itemRevision}\n" +
            $"pets:{petRevision}\n" +
            $"pet-owner-merge:{petOwnerMergeRevision}\n" +
            $"pet-learned-skills:{petLearnedSkillRevision}\n" +
            $"holy-spirit-balance:{holySpiritBalanceRevision}\n");
        return Convert.ToHexString(SHA256.HashData(canonical));
    }

    private static void ValidateRevision(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Length != 64 ||
            value.Any(static character =>
                character is not (>= '0' and <= '9') and
                    not (>= 'A' and <= 'F')))
        {
            throw new ArgumentException(
                "Content revisions must be uppercase SHA-256 values.",
                name);
        }
    }
}
