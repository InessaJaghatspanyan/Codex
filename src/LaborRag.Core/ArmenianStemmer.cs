namespace LaborRag.Core;

/// <summary>
/// A light suffix stripper for Armenian: removes plural, case and article endings so
/// inflected forms of a word share one stem (աշխատողի, աշխատողների → աշխատող;
/// տույժի, տույժերը → տույժ). Nouns in -ություն keep that ending, which forms a
/// different word (ազատություն "freedom" vs ազատել "to dismiss"); only their case
/// forms are unified (ազատության, ազատությունը → ազատություն). Words from other
/// scripts are returned unchanged.
/// </summary>
public static class ArmenianStemmer
{
    private const int MinStem = 3;

    // Longest first; plural + case combinations before their parts.
    private static readonly string[] Suffixes = new[]
    {
        "ներից", "ներով", "ներում", "ներին", "ների", "ները", "ներն", "ներ",
        "երից", "երով", "երում", "երին", "երի", "երը", "երն", "եր",
        "ելու", "ելը", "ալու", "ալը", "ում", "ից", "ով", "ին", "ել", "ալ", "ի", "ը", "ն",
    }.OrderByDescending(s => s.Length).ToArray();

    private static readonly string[] AbstractEndings =
    [
        "ություններից", "ություններով", "ություններում", "ություններին", "ությունների", "ությունները", "ություններն", "ություններ",
        "ությունից", "ությունով", "ությունում", "ությունին", "ությունը", "ությունն", "ությամբ", "ության", "ություն",
    ];

    public static string Stem(string word)
    {
        if (word.Length <= MinStem + 1 || !word.All(c => c >= 'Ա' && c <= 'և')) return word;
        foreach (var ending in AbstractEndings)
        {
            if (word.Length - ending.Length >= MinStem && word.EndsWith(ending, StringComparison.Ordinal))
                return word[..^ending.Length] + "ություն";
        }
        foreach (var suffix in Suffixes)
        {
            if (word.Length - suffix.Length >= MinStem && word.EndsWith(suffix, StringComparison.Ordinal))
                return word[..^suffix.Length];
        }
        return word;
    }
}
