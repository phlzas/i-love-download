using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace LoveDownload.Api.Llm;

/// <summary>
/// Speaks the OpenAI-compatible /chat/completions shape. The provider is chosen purely
/// by BaseUrl, so the same class serves LM Studio, Ollama, OpenRouter or OpenAI.
///
/// Production behaviour implemented here:
///  - an explicit per-attempt timeout (a real one, not the SDK's ten-minute default);
///  - retries ONLY on timeout / 429 / 5xx, never on 400/401/403;
///  - exponential backoff with jitter, obeying Retry-After when the server sends it;
///  - one structured cost-log line per attempt.
///
/// Configuration is validated on each call (not in the constructor) so the type can be
/// resolved from DI even when the feature is disabled; a missing model surfaces as a
/// clean <see cref="LlmFailureKind.Configuration"/> error at request time instead of
/// crashing app startup.
/// </summary>
public sealed class OpenAiCompatibleLlmClient : ILlmClient
{
    private static readonly Random Jitter = new();
    private readonly HttpClient _http;
    private readonly LlmOptions _options;
    private readonly ILlmCostLogger _cost;

    public OpenAiCompatibleLlmClient(HttpClient http, LlmOptions options, ILlmCostLogger cost)
    {
        _http = http;
        _options = options;
        _cost = cost;

        _http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = Timeout.InfiniteTimeSpan;

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ApiKey);
        }
    }

    public async Task<LlmCallResult> CompleteAsync(string systemPrompt, string userContent, string promptVersion, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new LlmException(LlmFailureKind.Configuration, "LLM_API_KEY is not set.");
        }

        if (string.IsNullOrWhiteSpace(_options.Model))
        {
            throw new LlmException(LlmFailureKind.Configuration, "LLM_MODEL is not set.");
        }

        var body = JsonSerializer.Serialize(BuildPayload(systemPrompt, userContent));
        var maxAttempts = _options.MaxRetries + 1;
        LlmException? lastFailure = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token);
                stopwatch.Stop();

                if (!response.IsSuccessStatusCode)
                {
                    var (kind, retryAfter) = Classify(response);
                    var errorBody = await SafeReadAsync(response);
                    _cost.Log(new LlmCallLog(promptVersion, _options.Model, attempt, kind.ToString(), (int)response.StatusCode, 0, 0, stopwatch.ElapsedMilliseconds, attempt > 1));
                    lastFailure = new LlmException(kind, $"Provider returned {(int)response.StatusCode}: {Truncate(errorBody, 200)}", (int)response.StatusCode);

                    if (!IsRetryable(kind) || attempt == maxAttempts)
                    {
                        break;
                    }

                    await DelayAsync(attempt, retryAfter, cancellationToken);
                    continue;
                }

                var payload = await response.Content.ReadAsStringAsync(attemptCts.Token);
                var completion = ParseCompletion(payload);
                _cost.Log(new LlmCallLog(promptVersion, _options.Model, attempt, "ok", (int)response.StatusCode, completion.PromptTokens, completion.CompletionTokens, stopwatch.ElapsedMilliseconds, attempt > 1));
                return new LlmCallResult(completion.Text, completion.PromptTokens, completion.CompletionTokens, stopwatch.ElapsedMilliseconds, attempt);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                _cost.Log(new LlmCallLog(promptVersion, _options.Model, attempt, "timeout", null, 0, 0, stopwatch.ElapsedMilliseconds, attempt > 1));
                lastFailure = new LlmException(LlmFailureKind.Timeout, $"Model call timed out after {_options.TimeoutSeconds}s.");

                if (attempt == maxAttempts)
                {
                    break;
                }

                await DelayAsync(attempt, null, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                stopwatch.Stop();
                _cost.Log(new LlmCallLog(promptVersion, _options.Model, attempt, "network", null, 0, 0, stopwatch.ElapsedMilliseconds, attempt > 1));
                lastFailure = new LlmException(LlmFailureKind.Network, $"Network error: {ex.Message}", null, ex);

                if (attempt == maxAttempts)
                {
                    break;
                }

                await DelayAsync(attempt, null, cancellationToken);
            }
        }

        throw lastFailure ?? new LlmException(LlmFailureKind.Network, "LLM call failed.");
    }

    private object BuildPayload(string systemPrompt, string userContent)
    {
        var messages = new object[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userContent },
        };

        return _options.JsonMode
            ? new { model = _options.Model, temperature = _options.Temperature, messages, response_format = new { type = "json_object" } }
            : new { model = _options.Model, temperature = _options.Temperature, messages };
    }

    private static (LlmFailureKind Kind, TimeSpan? RetryAfter) Classify(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        var kind = status switch
        {
            429 => LlmFailureKind.RateLimited,
            >= 500 => LlmFailureKind.ServerError,
            >= 400 => LlmFailureKind.ClientError,
            _ => LlmFailureKind.ServerError,
        };

        TimeSpan? retryAfter = null;
        if (response.StatusCode == HttpStatusCode.TooManyRequests && response.Headers.RetryAfter is { } header)
        {
            if (header.Delta is { } delta)
            {
                retryAfter = delta;
            }
            else if (header.Date is { } date)
            {
                retryAfter = date - DateTimeOffset.UtcNow;
            }
        }

        return (kind, retryAfter);
    }

    // Retry only transient failures. A 400/401/403 will not improve by trying again,
    // and on a metered tier every pointless retry burns real quota.
    private static bool IsRetryable(LlmFailureKind kind)
        => kind is LlmFailureKind.Timeout or LlmFailureKind.RateLimited or LlmFailureKind.ServerError or LlmFailureKind.Network;

    private static async Task DelayAsync(int attempt, TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        TimeSpan delay;
        if (retryAfter is { } honoured && honoured > TimeSpan.Zero)
        {
            delay = honoured;
        }
        else
        {
            var backoffSeconds = Math.Pow(2, attempt - 1);
            var jitter = Jitter.NextDouble() * 0.25;
            delay = TimeSpan.FromSeconds(backoffSeconds + jitter);
        }

        await Task.Delay(delay, cancellationToken);
    }

    private static (string Text, int PromptTokens, int CompletionTokens) ParseCompletion(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var text = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;

        var promptTokens = 0;
        var completionTokens = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("prompt_tokens", out var prompt))
            {
                promptTokens = prompt.GetInt32();
            }

            if (usage.TryGetProperty("completion_tokens", out var completion))
            {
                completionTokens = completion.GetInt32();
            }
        }

        return (text, promptTokens, completionTokens);
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response)
    {
        try
        {
            return await response.Content.ReadAsStringAsync();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];
}
