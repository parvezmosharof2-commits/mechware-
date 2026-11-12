namespace Mechware.Infrastructure.Search;

/// <summary>
/// Built-in search synonyms. Groups are mutual (each word lists the others); Banglish spellings map one way to the English word.
/// An owner/admin-editable list comes later; until then <c>SearchIndexWorker</c> applies this set when it starts.
/// </summary>
public static class SearchSynonyms
{
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Default { get; } = Build();

    private static Dictionary<string, IReadOnlyList<string>> Build()
    {
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        string[][] groups =
        [
            ["vfd", "inverter", "ac drive", "frequency converter", "variable frequency drive"],
            ["plc", "programmable logic controller"],
            ["mcb", "miniature circuit breaker"],
            ["mccb", "moulded case circuit breaker", "molded case circuit breaker"],
            ["contactor", "magnetic contactor"],
            ["smps", "power supply", "switching power supply"],
            ["hmi", "operator panel", "touch panel"],
        ];
        foreach (var group in groups)
            foreach (var word in group)
                map[word] = group.Where(w => w != word).ToArray();

        map["invertar"] = ["inverter"];
        map["motar"] = ["motor"];
        map["sensar"] = ["sensor"];
        map["breakar"] = ["breaker"];
        map["kontaktor"] = ["contactor"];
        return map;
    }
}
