namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private static readonly IReadOnlyDictionary<string, int>
        EpochBoundMechanicsArgumentCounts =
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["PreviewMonsterHit"] = 7,
                ["ReserveMonsterHit"] = 7,
                ["CommitMonsterHit"] = 7,
                ["ClearCharacterLifeAtCurrentClock"] = 4,
                ["TryGetActiveCharacterEffectView"] = 6,
                ["PreviewOutgoingDamage"] = 6
            };

    private static void CheckProductionMechanicsCallsRequireEpoch()
    {
        var sourceRoot = Path.Combine(
            FindMedusaEpochRepositoryRoot(),
            "src",
            "Godswar.Server");
        var checkedCalls = 0;

        foreach (var path in Directory.EnumerateFiles(
                     sourceRoot,
                     "*.cs",
                     SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(path);
            foreach (var expected in EpochBoundMechanicsArgumentCounts)
            {
                var token = "_mechanics." + expected.Key;
                var offset = 0;
                while ((offset = source.IndexOf(
                           token,
                           offset,
                           StringComparison.Ordinal)) >= 0)
                {
                    var open = offset + token.Length;
                    while (open < source.Length &&
                           char.IsWhiteSpace(source[open]))
                    {
                        open++;
                    }
                    if (open >= source.Length || source[open] != '(')
                    {
                        offset += token.Length;
                        continue;
                    }

                    var actual = CountInvocationArguments(source, open);
                    Check.True(
                        actual == expected.Value,
                        $"production {expected.Key} call in " +
                        $"{Path.GetRelativePath(sourceRoot, path)} uses " +
                        $"{actual} arguments instead of the exact epoch " +
                        $"overload's {expected.Value}");
                    checkedCalls++;
                    offset = open + 1;
                }
            }
        }

        Check.True(
            checkedCalls == 8,
            "production epoch ratchet inspects every current Map/registry " +
            "mechanics callsite");
    }

    private static int CountInvocationArguments(
        string source,
        int openParenthesis)
    {
        var parentheses = 1;
        var brackets = 0;
        var braces = 0;
        var commas = 0;
        var hasContent = false;
        var inString = false;
        var inCharacter = false;
        var inLineComment = false;
        var inBlockComment = false;

        for (var index = openParenthesis + 1;
             index < source.Length;
             index++)
        {
            var current = source[index];
            var next = index + 1 < source.Length
                ? source[index + 1]
                : '\0';
            if (inLineComment)
            {
                inLineComment = current != '\n';
                continue;
            }
            if (inBlockComment)
            {
                if (current == '*' && next == '/')
                {
                    inBlockComment = false;
                    index++;
                }
                continue;
            }
            if (inString)
            {
                if (current == '\\')
                {
                    index++;
                }
                else if (current == '"')
                {
                    inString = false;
                }
                continue;
            }
            if (inCharacter)
            {
                if (current == '\\')
                {
                    index++;
                }
                else if (current == '\'')
                {
                    inCharacter = false;
                }
                continue;
            }
            if (current == '/' && next == '/')
            {
                inLineComment = true;
                index++;
                continue;
            }
            if (current == '/' && next == '*')
            {
                inBlockComment = true;
                index++;
                continue;
            }
            if (current == '"')
            {
                inString = true;
                hasContent = true;
                continue;
            }
            if (current == '\'')
            {
                inCharacter = true;
                hasContent = true;
                continue;
            }

            switch (current)
            {
                case '(':
                    parentheses++;
                    break;
                case ')':
                    parentheses--;
                    if (parentheses == 0)
                    {
                        return hasContent ? commas + 1 : 0;
                    }
                    break;
                case '[':
                    brackets++;
                    break;
                case ']':
                    brackets--;
                    break;
                case '{':
                    braces++;
                    break;
                case '}':
                    braces--;
                    break;
                case ',' when parentheses == 1 &&
                                   brackets == 0 &&
                                   braces == 0:
                    commas++;
                    break;
                default:
                    if (!char.IsWhiteSpace(current))
                    {
                        hasContent = true;
                    }
                    break;
            }
        }

        throw new InvalidOperationException(
            "An epoch-bound mechanics invocation is unterminated.");
    }

    private static string FindMedusaEpochRepositoryRoot()
    {
        for (var current = new DirectoryInfo(
                 Directory.GetCurrentDirectory());
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "Reborn.sln")) ||
                Directory.Exists(Path.Combine(current.FullName, ".git")) &&
                Directory.Exists(Path.Combine(current.FullName, "src")))
            {
                return current.FullName;
            }
        }

        throw new InvalidOperationException(
            "The repository root is unavailable for the Medusa epoch ratchet.");
    }
}
