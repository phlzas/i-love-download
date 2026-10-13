using System.Globalization;

namespace LoveDownload.Api.Llm;

/// <summary>
/// LLM provider settings, bound from the "Llm" configuration section and overlaid with the
/// same environment variables the A17 client layer uses. The three "swap" values are BaseUrl,
/// ApiKey and Model - changing only those moves the same code between LM Studio, Ollama,
/// OpenRouter or OpenAI.
///
/// Safe defaults: <see cref="Enabled"/> is FALSE, so a fresh checkout never calls a model.
/// </summary>
public sealed class LlmOptions
{
    public string BaseUrl { get; set; } = "http://localhost:1234/v1";
    public string ApiKey { get; set; } = "lm-studio";
    public string Model { get; set; } = string.Empty;
    public double Temperature { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxRetries { get; set; } = 3;
    public bool Enabled { get; set; }
    public bool Stub { get; set; }
    public bool JsonMode { get; set; }

    /// <summary>
    /// Binds the "Llm" configuration section, then lets LLM_* environment variables win
    /// (so a deployment can flip a single value without editing appsettings.json).
    /// </summary>
    public static LlmOptions FromConfiguration(IConfiguration configuration)
    {
        var options = new LlmOptions();
        configuration.GetSection("Llm").Bind(options);

        static string? Raw(string name) => Environment.GetEnvironmentVariable(name);

        var baseUrl = Raw("LLM_BASE_URL");
        if (!string.IsNullOrWhiteSpace(baseUrl)) options.BaseUrl = baseUrl.Trim();

        var apiKey = Raw("LLM_API_KEY");
        if (!string.IsNullOrWhiteSpace(apiKey)) options.ApiKey = apiKey.Trim();

        var model = Raw("LLM_MODEL");
        if (!string.IsNullOrWhiteSpace(model)) options.Model = model.Trim();

        if (TryBool(Raw("LLM_ENABLED"), out var enabled)) options.Enabled = enabled;
        if (TryBool(Raw("LLM_STUB"), out var stub)) options.Stub = stub;
        if (TryBool(Raw("LLM_JSON_MODE"), out var jsonMode)) options.JsonMode = jsonMode;

        if (double.TryParse(Raw("LLM_TEMPERATURE"), NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature))
        {
            options.Temperature = temperature;
        }

        // An explicit endpoint timeout, clamped to <= 60s. The official SDKs default
        // to ten minutes, which is not a usable timeout for an HTTP request.
        if (int.TryParse(Raw("LLM_TIMEOUT_SECONDS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeout))
        {
            options.TimeoutSeconds = Math.Clamp(timeout, 1, 60);
        }

        if (int.TryParse(Raw("LLM_MAX_RETRIES"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var retries))
        {
            options.MaxRetries = Math.Clamp(retries, 0, 5);
        }

        return options;
    }

    private static bool TryBool(string? raw, out bool value)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = false;
            return false;
        }

        value = raw.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";
        return true;
    }
}
