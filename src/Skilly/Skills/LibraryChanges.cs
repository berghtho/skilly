namespace Skilly.Skills;

public sealed record LibraryMemberState(string LocalPath, string SkillName, bool Present);

public sealed record LibraryChangeSummary(
    IReadOnlyList<string> AddedSkills,
    IReadOnlyList<string> RemovedSkills)
{
    public bool HasChanges => AddedSkills.Count > 0 || RemovedSkills.Count > 0;
}

public static class LibraryChangeDiff
{
    public static LibraryChangeSummary Compute(
        IReadOnlyList<LibraryMemberState> before,
        IReadOnlyList<LibraryMemberState> after)
    {
        var beforePresent = PresentByPath(before);
        var afterPresent = PresentByPath(after);
        return new LibraryChangeSummary(
            Names(afterPresent.Where(pair => !beforePresent.ContainsKey(pair.Key))),
            Names(beforePresent.Where(pair => !afterPresent.ContainsKey(pair.Key))));
    }

    private static Dictionary<string, string> PresentByPath(IReadOnlyList<LibraryMemberState> members)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in members.Where(static member => member.Present))
        {
            result[member.LocalPath] = member.SkillName;
        }

        return result;
    }

    private static List<string> Names(IEnumerable<KeyValuePair<string, string>> members)
        => [.. members
            .Select(static pair => pair.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)];
}
