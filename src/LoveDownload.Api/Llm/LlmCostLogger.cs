using System.Text.Json;

namespace LoveDownload.Api.Llm;

/// <summary>
/// Writes one structured cost line per model call: both to stdout (Twelve-Factor: logs
/// are event streams) and to logs/llm-cost.jsonl under THIS repository's root so a single
/// call can be quoted in the README. (Ported from the A17 layer; repo root retargeted.)
/// </summary>
public sealed class LlmCostLogger : ILlmCostLogger
{
    private static readonly object Gate = new();
    private readonly ILogger<LlmCostLogger> _logger;
    private readonly string _filePath;

    public LlmCostLogger(ILogger<LlmCostLogger> logger)
    {
        _logger = logger;

        var dir = Path.Combine(LoveDownload.Api.RepoPaths.Root, "logs");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "llm-cost.jsonl");
    }

    public void Log(LlmCallLog entry)
    {
        _logger.LogInformation(
            "llm.call version={PromptVersion} model={Model} attempt={Attempt} outcome={Outcome} status={StatusCode} in={InputTokens} out={OutputTokens} ms={DurationMs} retried={Retried}",
            entry.PromptVersion, entry.Model, entry.Attempt, entry.Outcome, entry.StatusCode,
            entry.InputTokens, entry.OutputTokens, entry.DurationMs, entry.Retried);

        lock (Gate)
        {
            File.AppendAllText(_filePath, JsonSerializer.Serialize(entry) + Environment.NewLine);
        }
    }
}
