using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LoveDownload.Api.Services;

namespace LoveDownload.Api.Llm;

/// <summary>A validated scheduling proposal. Never mutates stored state on its own.</summary>
public sealed record ScheduleProposal(bool Enabled, string Start, string End, string Explanation);

/// <summary>Outcome of parsing a natural-language schedule request. <see cref="Source"/> is "stub" or "llm".</summary>
public sealed record ScheduleParseOutcome(bool Ok, int StatusCode, ScheduleProposal? Proposal, string? Error, string Source);

/// <summary>
/// Turns one natural-language scheduling request into the existing schedule window shape
/// <c>{ enabled, start: "HH:mm", end: "HH:mm" }</c>. The model answer is treated as untrusted
/// input and validated before it is returned or applied.
///
/// Graceful degradation: when the feature is disabled (LLM_ENABLED=false) or stub mode is on
/// (LLM_STUB=true) a deterministic offline parser handles common phrasings, so end-to-end
/// works without a live model. Provider failures become clean JSON errors, never a crash.
/// </summary>
public sealed class ScheduleNlParser
{
    private readonly LlmOptions _options;
    private readonly PromptLoader _prompt;
    private readonly ILlmClient _client;

    public ScheduleNlParser(LlmOptions options, PromptLoader prompt, ILlmClient client)
    {
        _options = options;
        _prompt = prompt;
        _client = client;
    }

    public async Task<ScheduleParseOutcome> ParseAsync(string? prompt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return new ScheduleParseOutcome(false, StatusCodes.Status400BadRequest, null, "prompt must be a non-empty string", "none");
        }

        var text = prompt.Trim();

        if (!_options.Enabled || _options.Stub)
        {
            var stub = OfflineScheduleStub.TryParse(text);
            if (stub is null)
            {
                return new ScheduleParseOutcome(false, StatusCodes.Status422UnprocessableEntity, null,
                    "offline stub could not find a start and end time in the prompt (set LLM_ENABLED=true and LLM_STUB=false for a live model)", "stub");
            }

            var stubProposal = Materialize(stub.Value.Enabled, stub.Value.Start, stub.Value.End, stub.Value.Explanation);
            return stubProposal is null
                ? new ScheduleParseOutcome(false, StatusCodes.Status422UnprocessableEntity, null, "offline stub produced an invalid window", "stub")
                : new ScheduleParseOutcome(true, StatusCodes.Status200OK, stubProposal, null, "stub");
        }

        try
        {
            var result = await _client.CompleteAsync(_prompt.System, text, _prompt.Version, cancellationToken);
            var parsed = ParseModelJson(result.Text);
            if (parsed is null)
            {
                return new ScheduleParseOutcome(false, StatusCodes.Status422UnprocessableEntity, null,
                    "the model did not return a JSON object with enabled/start/end", "llm");
            }

            var proposal = Materialize(parsed.Value.Enabled, parsed.Value.Start, parsed.Value.End, parsed.Value.Explanation);
            if (proposal is null)
            {
                return new ScheduleParseOutcome(false, StatusCodes.Status422UnprocessableEntity, null,
                    $"the model returned start/end that are not valid HH:mm ('{parsed.Value.Start}' / '{parsed.Value.End}')", "llm");
            }

            return new ScheduleParseOutcome(true, StatusCodes.Status200OK, proposal, null, "llm");
        }
        catch (LlmException ex)
        {
            // The model is configured but unreachable / failing: report it, never crash.
            return new ScheduleParseOutcome(false, StatusCodes.Status503ServiceUnavailable, null,
                $"schedule parsing failed: {ex.Message}", "llm");
        }
    }

    private static ScheduleProposal? Materialize(bool enabled, string? start, string? end, string? explanation)
    {
        if (!ScheduleStore.TryParseHhMm(start, out var startMinutes) || !ScheduleStore.TryParseHhMm(end, out var endMinutes))
        {
            return null;
        }

        var canonicalStart = Format(startMinutes);
        var canonicalEnd = Format(endMinutes);
        var reason = string.IsNullOrWhiteSpace(explanation)
            ? $"{canonicalStart}-{canonicalEnd} daily window."
            : explanation!.Trim();

        return new ScheduleProposal(enabled, canonicalStart, canonicalEnd, reason);
    }

    private static string Format(int minutes) => $"{minutes / 60:D2}:{minutes % 60:D2}";

    private static (bool Enabled, string? Start, string? End, string? Explanation)? ParseModelJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var cleaned = StripCodeFences(text);
        var open = cleaned.IndexOf('{');
        var close = cleaned.LastIndexOf('}');
        if (open < 0 || close <= open)
        {
            return null;
        }

        var json = cleaned.Substring(open, close - open + 1);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return (ReadBool(root, "enabled"), ReadString(root, "start"), ReadString(root, "end"), ReadString(root, "explanation"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string StripCodeFences(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline >= 0)
            {
                trimmed = trimmed[(firstNewline + 1)..];
            }

            var fence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0)
            {
                trimmed = trimmed[..fence];
            }
        }

        return trimmed.Trim();
    }

    private static bool ReadBool(JsonElement obj, string name)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return property.Value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => property.Value.GetString()!.Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on",
                JsonValueKind.Number => property.Value.GetDouble() != 0,
                _ => false,
            };
        }

        return false;
    }

    private static string? ReadString(JsonElement obj, string name)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetRawText(),
                _ => null,
            };
        }

        return null;
    }
}

/// <summary>
/// Deterministic offline parser used when the model is disabled or stubbed. It finds up to two
/// clock times in the prompt (12h with am/pm, or 24h) and maps them to a start/end window, so
/// "only download between 2am and 6am" becomes 02:00-06:00 without any network call.
/// </summary>
internal static class OfflineScheduleStub
{
    private static readonly Regex TimeToken = new(
        @"(?<h>\d{1,2})(?::(?<m>\d{2}))?\s*(?<ap>am|pm)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] DisablePhrases =
    [
        "disable", "turn off", "switch off", "any time", "anytime", "all day", "all-day",
        "always", "no schedule", "no restriction", "unrestricted", "24/7",
        "don't restrict", "dont restrict", "no window",
    ];

    public static (bool Enabled, string Start, string End, string Explanation)? TryParse(string prompt)
    {
        var lower = prompt.ToLowerInvariant();
        var disabled = DisablePhrases.Any(lower.Contains);

        if (disabled)
        {
            return (false, "00:00", "00:00", "Scheduling disabled - downloads run at any time.");
        }

        var tokens = new List<(int Hour, int Minute, string Ap)>();
        foreach (Match match in TimeToken.Matches(lower))
        {
            if (!int.TryParse(match.Groups["h"].Value, out var hour))
            {
                continue;
            }

            var minute = match.Groups["m"].Success
                ? int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture)
                : 0;
            var ap = match.Groups["ap"].Success ? match.Groups["ap"].Value.ToLowerInvariant() : string.Empty;

            if (minute > 59 || hour > 23)
            {
                continue;
            }

            if (ap.Length > 0 && (hour < 1 || hour > 12))
            {
                continue;
            }

            tokens.Add((hour, minute, ap));
        }

        if (tokens.Count < 2)
        {
            return null;
        }

        // "between 2 and 6 am" -> the bare "2" adopts the neighbouring "am".
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Ap.Length != 0)
            {
                continue;
            }

            if (i + 1 < tokens.Count && tokens[i + 1].Ap.Length != 0)
            {
                tokens[i] = (tokens[i].Hour, tokens[i].Minute, tokens[i + 1].Ap);
            }
            else if (i - 1 >= 0 && tokens[i - 1].Ap.Length != 0)
            {
                tokens[i] = (tokens[i].Hour, tokens[i].Minute, tokens[i - 1].Ap);
            }
        }

        var startMinutes = ToMinutes(tokens[0]);
        var endMinutes = ToMinutes(tokens[1]);
        var start = $"{startMinutes / 60:D2}:{startMinutes % 60:D2}";
        var end = $"{endMinutes / 60:D2}:{endMinutes % 60:D2}";

        return (true, start, end, $"Offline stub mapped the request to a {start}-{end} daily window.");
    }

    private static int ToMinutes((int Hour, int Minute, string Ap) token)
    {
        var hour = token.Hour;
        if (token.Ap == "am" && hour == 12)
        {
            hour = 0;
        }
        else if (token.Ap == "pm" && hour < 12)
        {
            hour += 12;
        }

        return (hour * 60) + token.Minute;
    }
}
