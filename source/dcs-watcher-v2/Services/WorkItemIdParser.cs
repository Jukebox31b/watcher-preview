using System.Text.RegularExpressions;
using DcsWatcherV2.Models;

namespace DcsWatcherV2.Services;

public static partial class WorkItemIdParser
{
    public static WorkItemId? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = WorkItemRegex().Match(text);
        if (!match.Success || !int.TryParse(match.Groups["number"].Value, out var number))
        {
            return null;
        }

        _ = int.TryParse(match.Groups["revision"].Value, out var revision);

        var revisionSuffix = match.Groups["revisionSuffix"].Value.ToUpperInvariant();

        return new WorkItemId
        {
            Family = match.Groups["family"].Value.ToUpperInvariant(),
            Number = number,
            Revision = revision,
            RevisionSuffix = revisionSuffix,
            RevisionSuffixRank = GetRevisionSuffixRank(revisionSuffix),
            OriginalText = text
        };
    }

    public static bool IsSameFamilyNewer(WorkItemId envelope, WorkItemId report)
    {
        return envelope.Family.Equals(report.Family, StringComparison.OrdinalIgnoreCase) &&
               Compare(envelope, report) > 0;
    }

    public static int Compare(WorkItemId left, WorkItemId right)
    {
        var familyComparison = string.Compare(left.Family, right.Family, StringComparison.OrdinalIgnoreCase);
        if (familyComparison != 0)
        {
            return familyComparison;
        }

        var numberComparison = left.Number.CompareTo(right.Number);
        if (numberComparison != 0)
        {
            return numberComparison;
        }

        var revisionComparison = left.Revision.CompareTo(right.Revision);
        if (revisionComparison != 0)
        {
            return revisionComparison;
        }

        var suffixRankComparison = GetRevisionSuffixRank(left.RevisionSuffix)
            .CompareTo(GetRevisionSuffixRank(right.RevisionSuffix));
        return suffixRankComparison != 0
            ? suffixRankComparison
            : string.Compare(left.RevisionSuffix, right.RevisionSuffix, StringComparison.OrdinalIgnoreCase);
    }

    public static long GetRevisionSuffixRank(string? suffix)
    {
        if (string.IsNullOrWhiteSpace(suffix))
        {
            return 0;
        }

        long rank = 0;
        foreach (var character in suffix.ToUpperInvariant())
        {
            if (character is < 'A' or > 'Z')
            {
                return 0;
            }

            var value = character - 'A' + 1;
            if (rank > (long.MaxValue - value) / 26)
            {
                return long.MaxValue;
            }

            rank = (rank * 26) + value;
        }

        return rank;
    }

    [GeneratedRegex(@"(?i)\b(?<family>SC|MEC)[-_ ]?(?<number>\d+)(?:R(?<revision>\d+)(?<revisionSuffix>[A-Z]+)?)?\b")]
    private static partial Regex WorkItemRegex();
}
