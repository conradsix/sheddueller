namespace Sheddueller.Enqueueing;

internal static class JobTagNormalizer
{
    public static IReadOnlyList<JobTag> Normalize(IReadOnlyList<JobTag>? tags, string subject)
    {
        if (tags is null || tags.Count == 0)
        {
            return [];
        }

        var seen = new HashSet<JobTag>();
        var normalized = new List<JobTag>(tags.Count);
        foreach (var tag in tags)
        {
            ArgumentNullException.ThrowIfNull(tag);
            var name = tag.Name.Trim();
            var value = tag.Value.Trim();
            if (name.Length == 0)
            {
                throw new ArgumentException($"{subject} tag names must be non-empty after trimming.", nameof(tags));
            }

            if (value.Length == 0)
            {
                throw new ArgumentException($"{subject} tag values must be non-empty after trimming.", nameof(tags));
            }

            var normalizedTag = new JobTag(name, value);
            if (seen.Add(normalizedTag))
            {
                normalized.Add(normalizedTag);
            }
        }

        return normalized;
    }
}
