using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PromptSaver.Domain.Entities;
using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Domain.Policies;

public sealed record ExtractedSkill(string Name, Confidence Confidence);

public sealed record ExtractedEntity(
    string Name,
    EntityType Type,
    Confidence Confidence);

public sealed record DeterministicMetadata(
    string? IntentCandidate,
    string Title,
    IReadOnlyList<ExtractedSkill> Skills,
    IReadOnlyList<ExtractedEntity> Entities)
{
    public bool HasValidIntentCandidate => IntentCandidate is not null;
}

public static partial class DeterministicMetadataExtractor
{
    public const string ExtractorVersion = "deterministic-v1";
    private const int MaximumIntentCharacters = 160;

    private static readonly HashSet<string> ControlledVerbs =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "create",
            "write",
            "draw",
            "summarize",
            "analyze",
            "fix",
            "explain",
            "search",
            "prepare",
            "convert",
        };

    private static readonly HashSet<string> ObjectStopWords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "a",
            "an",
            "the",
            "this",
            "that",
            "these",
            "those",
            "my",
            "our",
            "some",
            "please",
        };

    private static readonly (string Skill, string[] Terms)[] SkillVocabulary =
    [
        ("analysis", ["analyze", "analysis"]),
        ("architecture", ["architecture", "design"]),
        ("diagramming", ["diagram", "draw"]),
        ("presentation", ["powerpoint", "ppt", "presentation", "slides"]),
        ("software development", ["application", "code", "c#", "c++", "fix", "utility"]),
        ("summarization", ["summarize", "summary"]),
        ("writing", ["write", "release notes", "document"]),
    ];

    public static DeterministicMetadata Extract(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        string titleSource = FirstMeaningfulLine(body);
        Match match = LeadingIntentRegex().Match(titleSource);
        string? candidate = null;

        if (match.Success &&
            ControlledVerbs.Contains(match.Groups["verb"].Value) &&
            TryCreateObject(match.Groups["object"].Value, out string objectPhrase))
        {
            string verb = SentenceCase(match.Groups["verb"].Value.ToLowerInvariant());
            candidate = TruncateAtWordBoundary(
                $"{verb} {objectPhrase}",
                MaximumIntentCharacters);
        }

        string title = candidate ?? CreateFallbackTitle(titleSource);
        IReadOnlyList<ExtractedSkill> skills = ExtractSkills(body);
        IReadOnlyList<ExtractedEntity> entities = ExtractEntities(body);
        return new DeterministicMetadata(candidate, title, skills, entities);
    }

    private static bool TryCreateObject(string rawObject, out string objectPhrase)
    {
        string cleaned = IntentTextNormalizer.NormalizeDisplay(rawObject);
        int sentenceEnd = cleaned.IndexOfAny(['.', '!', '?', ';', ':']);
        if (sentenceEnd >= 0)
        {
            cleaned = cleaned[..sentenceEnd];
        }

        cleaned = TrailingPunctuationRegex().Replace(cleaned, string.Empty);
        cleaned = LeadingArticlesRegex().Replace(cleaned, string.Empty);

        string qualifier = string.Empty;
        Match forMatch = CustomerQualifierRegex().Match(cleaned);
        if (forMatch.Success)
        {
            cleaned = cleaned[..forMatch.Index].Trim();
            qualifier = " for a customer";
        }

        string[] tokens = IntentTextNormalizer.Tokenize(cleaned)
            .Where(token => !ObjectStopWords.Contains(token))
            .ToArray();
        if (tokens.Length == 0 || tokens.All(IntentTextNormalizer.IsStopWord))
        {
            objectPhrase = string.Empty;
            return false;
        }

        string normalizedObject = string.Join(' ', tokens.Select(FormatToken));
        objectPhrase = normalizedObject + qualifier;
        return objectPhrase.Length > 0;
    }

    private static string TruncateAtWordBoundary(string value, int maximumCharacters)
    {
        if (value.Length <= maximumCharacters)
        {
            return value;
        }

        int boundary = value.LastIndexOf(' ', maximumCharacters - 1);
        return value[..(boundary > 0 ? boundary : maximumCharacters)].TrimEnd();
    }

    private static ExtractedSkill[] ExtractSkills(string body)
    {
        string key = IntentTextNormalizer.NormalizeKey(body);
        return SkillVocabulary
            .Where(item => item.Terms.Any(
                term => key.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Select(item => new ExtractedSkill(item.Skill, Confidence.Create(0.8m)))
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static ExtractedEntity[] ExtractEntities(string body)
    {
        Dictionary<string, ExtractedEntity> entities =
            new(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in EntityRegex().Matches(body))
        {
            string value = match.Value.Trim();
            if (ControlledVerbs.Contains(value) ||
                ObjectStopWords.Contains(value) ||
                value.Length > 160)
            {
                continue;
            }

            EntityType type = value.ToLowerInvariant() switch
            {
                "azure" or "c#" or "c++" or "powerpoint" or "markdown" or "pdf" =>
                    EntityType.Technology,
                _ => EntityType.Organization,
            };
            entities.TryAdd(
                IntentTextNormalizer.NormalizeKey(value),
                new ExtractedEntity(value, type, Confidence.Create(0.75m)));
        }

        return entities.Values
            .OrderBy(entity => entity.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static string FirstMeaningfulLine(string body)
    {
        string line = body
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.None)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? string.Empty;
        return line.Trim();
    }

    private static string CreateFallbackTitle(string source)
    {
        string title = IntentTextNormalizer.NormalizeDisplay(source);
        if (title.Length == 0)
        {
            return "Untitled prompt";
        }

        if (title.Length > PromptTitle.MaximumCharacters)
        {
            title = title[..PromptTitle.MaximumCharacters].TrimEnd();
        }

        return SentenceCase(title);
    }

    private static string SentenceCase(string value) =>
        value.Length == 0
            ? value
            : string.Concat(value[..1].ToUpper(CultureInfo.InvariantCulture), value.AsSpan(1));

    private static string FormatToken(string token)
    {
        return token.ToLowerInvariant() switch
        {
            "ppt" => "PPT",
            "pdf" => "PDF",
            "c#" => "C#",
            "c++" => "C++",
            "markdown" => "Markdown",
            "powerpoint" => "PowerPoint",
            "azure" => "Azure",
            _ => token.ToLowerInvariant(),
        };
    }

    [GeneratedRegex(
        @"^\s*(?:(?:please)\s+)?(?<verb>create|write|draw|summarize|analyze|fix|explain|search|prepare|convert)\b\s+(?<object>.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingIntentRegex();

    [GeneratedRegex(@"^(?:a|an|the|this|that|these|those|my|our|some)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingArticlesRegex();

    [GeneratedRegex(@"[\s.!?;:]+$")]
    private static partial Regex TrailingPunctuationRegex();

    [GeneratedRegex(
        @"\s+for\s+(?:a\s+|an\s+|the\s+)?(?:customer|client|partner|[A-Z][\p{L}\p{N}_.-]*)(?=\s|$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex CustomerQualifierRegex();

    [GeneratedRegex(@"(?<!\w)(?:C\+\+|C#|[A-Z][\p{L}\p{N}]*(?:\s+[A-Z][\p{L}\p{N}]*)*|[A-Z]{2,})(?!\w)")]
    private static partial Regex EntityRegex();
}

public static partial class IntentTextNormalizer
{
    private static readonly HashSet<string> StopWords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "a",
            "an",
            "and",
            "for",
            "in",
            "of",
            "on",
            "the",
            "to",
            "with",
        };

    public static string NormalizeKey(string value) =>
        NormalizeDisplay(value).ToLowerInvariant();

    public static string NormalizeDisplay(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string normalized = value
            .Normalize(NormalizationForm.FormKC)
            .Replace('-', ' ')
            .Trim();
        return TerminalPunctuationRegex().Replace(
            WhitespaceRegex().Replace(normalized, " "),
            string.Empty);
    }

    public static IReadOnlyList<string> Tokenize(string value) =>
        TokenRegex()
            .Matches(NormalizeKey(value))
            .Select(match => match.Value)
            .ToArray();

    public static bool IsStopWord(string token) => StopWords.Contains(token);

    public static IReadOnlySet<string> ObjectTokens(string value)
    {
        IReadOnlyList<string> tokens = Tokenize(value);
        int start = tokens.Count > 0 &&
            DeterministicMetadataExtractor.Extract($"{tokens[0]} object").HasValidIntentCandidate
                ? 1
                : 0;
        return tokens
            .Skip(start)
            .Where(token => !StopWords.Contains(token))
            .ToHashSet(StringComparer.Ordinal);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[.!?;:]+$")]
    private static partial Regex TerminalPunctuationRegex();

    [GeneratedRegex(@"(?:C\+\+|C#|[\p{L}\p{N}]+(?:\.[\p{L}\p{N}]+)*)")]
    private static partial Regex TokenRegex();
}

public static class IntentLexicalScorer
{
    public const string AlgorithmVersion = "lexical-v1";

    public static IntentCandidateScore? Score(
        string candidate,
        Intent intent,
        IReadOnlyList<IntentAlias> aliases)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(aliases);

        if (intent.IsArchived || intent.Id == Intent.ReservedUnsortedId)
        {
            return null;
        }

        string normalizedCandidate = IntentTextNormalizer.NormalizeKey(candidate);
        if (normalizedCandidate == IntentTextNormalizer.NormalizeKey(intent.CanonicalName))
        {
            return CreateScore(intent.Id, 10000, DateTimeOffset.UnixEpoch);
        }

        if (aliases.Any(
            alias =>
                alias.IntentId == intent.Id &&
                normalizedCandidate == IntentTextNormalizer.NormalizeKey(alias.Value)))
        {
            return CreateScore(intent.Id, 9900, DateTimeOffset.UnixEpoch);
        }

        IEnumerable<string> names = aliases
            .Where(alias => alias.IntentId == intent.Id)
            .Select(alias => alias.Value)
            .Prepend(intent.CanonicalName);
        int best = names
            .Select(name => ScorePair(candidate, name))
            .DefaultIfEmpty(0)
            .Max();
        return best == 0
            ? null
            : CreateScore(intent.Id, best, DateTimeOffset.UnixEpoch);
    }

    public static IReadOnlyList<IntentCandidateScore> Rank(
        string candidate,
        IReadOnlyList<Intent> intents,
        IReadOnlyList<IntentAlias> aliases,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(intents);
        ArgumentNullException.ThrowIfNull(aliases);

        return intents
            .Select(intent => Score(candidate, intent, aliases))
            .Where(score => score is not null)
            .Select(score => CreateScore(score!.IntentId, score.Score.Value, createdAtUtc))
            .Where(
                score =>
                    score.Score.Value >= IntentAssignmentPolicy.ReviewCandidateMinimumBasisPoints)
            .OrderByDescending(score => score.Score.Value)
            .ThenBy(score => score.IntentId.Value)
            .Take(5)
            .Select((score, index) => score.WithRank(index + 1))
            .ToArray();
    }

    private static int ScorePair(string left, string right)
    {
        IReadOnlySet<string> leftObjects = IntentTextNormalizer.ObjectTokens(left);
        IReadOnlySet<string> rightObjects = IntentTextNormalizer.ObjectTokens(right);
        if (!leftObjects.Overlaps(rightObjects))
        {
            return 0;
        }

        string normalizedLeft = IntentTextNormalizer.NormalizeKey(left);
        string normalizedRight = IntentTextNormalizer.NormalizeKey(right);
        int[] scores =
        [
            ToBasisPoints(TokenDice(normalizedLeft, normalizedRight)),
            ToBasisPoints(TrigramCosine(normalizedLeft, normalizedRight)),
            ToBasisPoints(EditSimilarity(normalizedLeft, normalizedRight)),
        ];
        Array.Sort(scores);
        return scores[1];
    }

    private static double TokenDice(string left, string right)
    {
        HashSet<string> leftTokens =
            IntentTextNormalizer.Tokenize(left).ToHashSet(StringComparer.Ordinal);
        HashSet<string> rightTokens =
            IntentTextNormalizer.Tokenize(right).ToHashSet(StringComparer.Ordinal);
        if (leftTokens.Count == 0 && rightTokens.Count == 0)
        {
            return 1;
        }

        int intersection = leftTokens.Count(rightTokens.Contains);
        return 2d * intersection / (leftTokens.Count + rightTokens.Count);
    }

    private static double TrigramCosine(string left, string right)
    {
        Dictionary<string, int> leftCounts = Trigrams(left);
        Dictionary<string, int> rightCounts = Trigrams(right);
        if (leftCounts.Count == 0 || rightCounts.Count == 0)
        {
            return left == right ? 1 : 0;
        }

        double dot = leftCounts.Sum(
            item => item.Value * rightCounts.GetValueOrDefault(item.Key));
        double leftMagnitude = Math.Sqrt(leftCounts.Values.Sum(value => value * value));
        double rightMagnitude = Math.Sqrt(rightCounts.Values.Sum(value => value * value));
        return dot / (leftMagnitude * rightMagnitude);
    }

    private static Dictionary<string, int> Trigrams(string value)
    {
        string padded = $"  {value}  ";
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        for (int index = 0; index <= padded.Length - 3; index++)
        {
            string trigram = padded.Substring(index, 3);
            counts[trigram] = counts.GetValueOrDefault(trigram) + 1;
        }

        return counts;
    }

    private static double EditSimilarity(string left, string right)
    {
        int maximum = Math.Max(left.Length, right.Length);
        return maximum == 0
            ? 1
            : 1d - ((double)LevenshteinDistance(left, right) / maximum);
    }

    private static int LevenshteinDistance(string left, string right)
    {
        int[] previous = Enumerable.Range(0, right.Length + 1).ToArray();
        int[] current = new int[right.Length + 1];

        for (int leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            current[0] = leftIndex;
            for (int rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                int substitution = previous[rightIndex - 1] +
                    (left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1);
                current[rightIndex] = Math.Min(
                    Math.Min(previous[rightIndex] + 1, current[rightIndex - 1] + 1),
                    substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static int ToBasisPoints(double value) =>
        Math.Clamp((int)Math.Round(value * 10000, MidpointRounding.AwayFromZero), 0, 10000);

    private static IntentCandidateScore CreateScore(
        IntentId intentId,
        int score,
        DateTimeOffset createdAtUtc) =>
        IntentCandidateScore.Create(
            intentId,
            ScoreBasisPoints.Create(score),
            1,
            AlgorithmVersion,
            createdAtUtc);
}
