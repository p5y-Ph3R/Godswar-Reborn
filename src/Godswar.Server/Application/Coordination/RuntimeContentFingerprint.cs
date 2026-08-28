using System.Security.Cryptography;
using System.Text;

namespace Godswar.Server.Application.Coordination;

/// <summary>
/// Identifies the complete process-pinned gameplay content set used by a
/// worker. Coordination must reject placement across workers that pin a
/// different world, item, pet, pet owner-Merge, learned pet-skill, or Holy
/// Spirit balance, Faction Crier balance, or realm-calendar catalog revision.
/// </summary>
internal static class RuntimeContentFingerprint
{
    public static string Create(
        string worldRevision,
        string itemRevision,
        string petRevision,
        string petOwnerMergeRevision,
        string petLearnedSkillRevision,
        string holySpiritBalanceRevision) =>
        Create(
            worldRevision,
            itemRevision,
            petRevision,
            petOwnerMergeRevision,
            petLearnedSkillRevision,
            holySpiritBalanceRevision,
            new string('0', 64));

    public static string Create(
        string worldRevision,
        string itemRevision,
        string petRevision,
        string petOwnerMergeRevision,
        string petLearnedSkillRevision,
        string holySpiritBalanceRevision,
        string factionCrierBalanceRevision)
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
        ValidateRevision(
            factionCrierBalanceRevision,
            nameof(factionCrierBalanceRevision));

        var canonical = Encoding.UTF8.GetBytes(
            "runtime-content-v6\n" +
            $"world:{worldRevision}\n" +
            $"items:{itemRevision}\n" +
            $"pets:{petRevision}\n" +
            $"pet-owner-merge:{petOwnerMergeRevision}\n" +
            $"pet-learned-skills:{petLearnedSkillRevision}\n" +
            $"holy-spirit-balance:{holySpiritBalanceRevision}\n" +
            $"faction-crier-balance:{factionCrierBalanceRevision}\n");
        return Convert.ToHexString(SHA256.HashData(canonical));
    }

    public static string Create(
        string worldRevision,
        string itemRevision,
        string petRevision,
        string petOwnerMergeRevision,
        string petLearnedSkillRevision,
        string holySpiritBalanceRevision,
        string factionCrierBalanceRevision,
        string realmCalendarCatalogRevision,
        string onlineAwardBalanceRevision,
        string warehouseExpansionPolicyRevision)
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
        ValidateRevision(
            factionCrierBalanceRevision,
            nameof(factionCrierBalanceRevision));
        ValidateRevision(
            realmCalendarCatalogRevision,
            nameof(realmCalendarCatalogRevision));
        ValidateRevision(
            onlineAwardBalanceRevision,
            nameof(onlineAwardBalanceRevision));
        ValidateRevision(
            warehouseExpansionPolicyRevision,
            nameof(warehouseExpansionPolicyRevision));

        var canonical = Encoding.UTF8.GetBytes(
            "runtime-content-v9\n" +
            $"world:{worldRevision}\n" +
            $"items:{itemRevision}\n" +
            $"pets:{petRevision}\n" +
            $"pet-owner-merge:{petOwnerMergeRevision}\n" +
            $"pet-learned-skills:{petLearnedSkillRevision}\n" +
            $"holy-spirit-balance:{holySpiritBalanceRevision}\n" +
            $"faction-crier-balance:{factionCrierBalanceRevision}\n" +
            $"realm-calendars:{realmCalendarCatalogRevision}\n" +
            $"online-award-balance:{onlineAwardBalanceRevision}\n" +
            $"warehouse-expansion-policy:" +
            $"{warehouseExpansionPolicyRevision}\n");
        return Convert.ToHexString(SHA256.HashData(canonical));
    }

    public static string Create(
        string worldRevision,
        string itemRevision,
        string petRevision,
        string petOwnerMergeRevision,
        string petLearnedSkillRevision,
        string holySpiritBalanceRevision,
        string factionCrierBalanceRevision,
        string realmCalendarCatalogRevision,
        string onlineAwardBalanceRevision)
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
        ValidateRevision(
            factionCrierBalanceRevision,
            nameof(factionCrierBalanceRevision));
        ValidateRevision(
            realmCalendarCatalogRevision,
            nameof(realmCalendarCatalogRevision));
        ValidateRevision(
            onlineAwardBalanceRevision,
            nameof(onlineAwardBalanceRevision));

        var canonical = Encoding.UTF8.GetBytes(
            "runtime-content-v8\n" +
            $"world:{worldRevision}\n" +
            $"items:{itemRevision}\n" +
            $"pets:{petRevision}\n" +
            $"pet-owner-merge:{petOwnerMergeRevision}\n" +
            $"pet-learned-skills:{petLearnedSkillRevision}\n" +
            $"holy-spirit-balance:{holySpiritBalanceRevision}\n" +
            $"faction-crier-balance:{factionCrierBalanceRevision}\n" +
            $"realm-calendars:{realmCalendarCatalogRevision}\n" +
            $"online-award-balance:{onlineAwardBalanceRevision}\n");
        return Convert.ToHexString(SHA256.HashData(canonical));
    }

    public static string Create(
        string worldRevision,
        string itemRevision,
        string petRevision,
        string petOwnerMergeRevision,
        string petLearnedSkillRevision,
        string holySpiritBalanceRevision,
        string factionCrierBalanceRevision,
        string realmCalendarCatalogRevision)
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
        ValidateRevision(
            factionCrierBalanceRevision,
            nameof(factionCrierBalanceRevision));
        ValidateRevision(
            realmCalendarCatalogRevision,
            nameof(realmCalendarCatalogRevision));

        var canonical = Encoding.UTF8.GetBytes(
            "runtime-content-v7\n" +
            $"world:{worldRevision}\n" +
            $"items:{itemRevision}\n" +
            $"pets:{petRevision}\n" +
            $"pet-owner-merge:{petOwnerMergeRevision}\n" +
            $"pet-learned-skills:{petLearnedSkillRevision}\n" +
            $"holy-spirit-balance:{holySpiritBalanceRevision}\n" +
            $"faction-crier-balance:{factionCrierBalanceRevision}\n" +
            $"realm-calendars:{realmCalendarCatalogRevision}\n");
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
