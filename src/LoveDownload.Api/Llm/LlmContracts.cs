namespace LoveDownload.Api.Llm;

public enum LlmFailureKind
{
    Timeout,
    RateLimited,
    ServerError,
    ClientError,
    Network,
    Configuration,
}

/// <summary>A model call failed. <see cref="Kind"/> drives the retry decision.</summary>
public sealed class LlmException : Exception
{
    public LlmException(LlmFailureKind kind, string message, int? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    public LlmFailureKind Kind { get; }
    public int? StatusCode { get; }
}

/// <summary>Raw result of one successful completion call, with usage for cost logging.</summary>
public sealed record LlmCallResult(
    string Text,
    int PromptTokens,
    int CompletionTokens,
    long DurationMs,
    int Attempts);

/// <summary>One structured cost-log line, emitted per HTTP attempt.</summary>
public sealed record LlmCallLog(
    string PromptVersion,
    string Model,
    int Attempt,
    string Outcome,
    int? StatusCode,
    int InputTokens,
    int OutputTokens,
    long DurationMs,
    bool Retried);

public interface ILlmCostLogger
{
    void Log(LlmCallLog entry);
}

public interface ILlmClient
{
    Task<LlmCallResult> CompleteAsync(string systemPrompt, string userContent, string promptVersion, CancellationToken cancellationToken);
}
