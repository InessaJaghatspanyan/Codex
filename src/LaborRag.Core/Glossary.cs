namespace LaborRag.Core;

/// <summary>
/// Maps common English and Russian labor-law terms, and everyday Armenian wording, to
/// the Armenian terms the Code uses, so such questions still match the Code's text. Patterns are
/// matched against the lowercased question as word prefixes (Russian and English
/// inflect too: "отпуска", "увольнении", "dismissed").
/// </summary>
public static class Glossary
{
    private static readonly (string[] Patterns, string Armenian)[] Entries =
    [
        (["vacation", "annual leave", "paid leave", "leave", "holiday entitlement", "отпуск"], "ամենամյա արձակուրդ"),
        (["maternity", "pregnan", "беремен", "декрет", "родам"], "հղիության և ծննդաբերության արձակուրդ հղի"),
        (["childcare", "parental", "child care", "уход за ребенк", "по уходу"], "երեխա խնամող"),
        (["minimum wage", "минимальн"], "նվազագույն աշխատավարձ"),
        (["salary", "wage", "pay", "paid", "remunerat", "зарплат", "заработн", "оплат"], "աշխատավարձ վարձատրություն"),
        (["average salary", "average wage", "средн"], "միջին աշխատավարձ"),
        (["bonus", "преми"], "պարգևատրում"),
        (["dismiss", "fire", "fired", "terminat", "layoff", "laid off", "уволь", "увольн", "расторж"], "աշխատանքային պայմանագրի լուծում աշխատանքից ազատում"),
        (["redundan", "downsiz", "сокращ"], "կրճատում զանգվածային ազատումներ"),
        (["resign", "quit", "собственному желан"], "աշխատողի նախաձեռնությամբ պայմանագրի լուծում"),
        (["severance", "выходное пособ", "пособи"], "արձակման նպաստ"),
        (["compensat", "компенсац"], "հատուցում"),
        (["notice", "уведомл", "предупрежд"], "ծանուցում"),
        (["probation", "trial period", "испытательн"], "փորձաշրջան"),
        (["overtime", "сверхурочн"], "արտաժամյա աշխատանք"),
        (["night", "ночн"], "գիշերային աշխատանք"),
        (["working hours", "working time", "work hours", "hours", "рабочее время", "рабочего времени", "рабочий день"], "աշխատաժամանակ"),
        (["part-time", "part time", "неполн"], "ոչ լրիվ աշխատաժամանակ"),
        (["second job", "combining", "совместител"], "համատեղություն"),
        (["remote", "home office", "work from home", "дистанцион", "удален"], "հեռավար աշխատանք"),
        (["break", "lunch", "перерыв"], "ընդմիջում"),
        (["rest", "weekend", "day off", "days off", "выходн", "отдых"], "հանգստի ժամանակ հանգստյան օր"),
        (["public holiday", "праздн"], "տոնական օր"),
        (["contract", "договор"], "աշխատանքային պայմանագիր"),
        (["collective agreement", "collective contract", "коллективн"], "կոլեկտիվ պայմանագիր"),
        (["employer", "работодател"], "գործատու"),
        (["employee", "worker", "работник"], "աշխատող"),
        (["sick", "illness", "болезн", "больничн", "нетрудоспособн"], "ժամանակավոր անաշխատունակություն"),
        (["disab", "инвалид"], "հաշմանդամություն"),
        (["discipline", "disciplinary", "reprimand", "дисциплин", "выговор"], "կարգապահական տույժ աշխատանքային կարգապահություն"),
        (["strike", "забастов"], "գործադուլ"),
        (["union", "профсоюз"], "արհեստակցական միություն"),
        (["dispute", "court", "спор", "суд"], "աշխատանքային վեճ դատարան"),
        (["minor", "under 18", "teenager", "несовершеннолет", "подрост"], "տասնութ տարեկան"),
        (["business trip", "командиров"], "գործուղում"),
        (["transfer", "перевод"], "տեղափոխում"),
        (["downtime", "idle time", "простой"], "պարապուրդ"),
        (["liability", "damage", "материальн"], "նյութական պատասխանատվություն"),
        (["discriminat", "дискриминац"], "խտրականություն"),
        (["harass", "домогат"], "սեռական ոտնձգություն"),
        (["training", "обучени", "квалификац"], "վերապատրաստում"),
        (["safety", "охран труда", "безопасн"], "աշխատանքի անվտանգություն"),
        // Everyday Armenian → the Code's wording.
        (["ազատել", "ազատում", "ազատվ", "հեռացն", "գործից"], "աշխատանքային պայմանագրի լուծում"),
        (["կին", "կնոջ", "կանայք"], "կանանց"),
        (["հղի"], "հղի կանանց"),
        (["ռոճիկ", "վարձ"], "աշխատավարձ"),
        (["maximum", "at most", "максимальн", "не более"], "առավելագույն"),
        (["minimum", "at least", "не менее"], "նվազագույն"),
        (["duration", "length", "how long", "продолжительн", "длительн", "срок"], "տևողություն ժամկետ"),
        (["week", "weekly", "недел"], "շաբաթ"),
        (["day", "days", "daily", "дней", "день", "дня"], "օր"),
        (["month", "months", "месяц"], "ամիս"),
        (["year", "years", "year", "лет", "года", "год"], "տարի"),
        (["written", "in writing", "письменн"], "գրավոր"),
    ];

    /// <summary>Armenian search terms for the English/Russian concepts found in the question.</summary>
    public static List<string> Expand(string question)
    {
        question = SearchIndex.StripArmenianMarks(question);
        var q = " " + question.ToLowerInvariant() + " ";
        var words = SearchIndex.Tokenize(question);
        var found = new List<string>();
        foreach (var (patterns, armenian) in Entries)
        {
            var hit = patterns.Any(p => p.Contains(' ') || p.Contains('-')
                ? q.Contains(p, StringComparison.Ordinal)
                : words.Any(w => Matches(w, p)));
            if (hit && !found.Contains(armenian)) found.Add(armenian);
        }
        return found;
    }

    // Short English words match whole words only ("rest" must not match "restriction");
    // longer patterns and Russian stems match as prefixes to cover inflection.
    private static bool Matches(string word, string pattern)
    {
        if (pattern.Length > 5 || !pattern.All(char.IsAscii)) return word.StartsWith(pattern, StringComparison.Ordinal);
        return word == pattern || word == pattern + "s" || word == pattern + "d" || word == pattern + "ed" || word == pattern + "ing";
    }
}
