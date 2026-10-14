namespace LoveDownload.Api.Llm;

/// <summary>
/// Loads the scheduling prompt from prompts/schedule-v1.md and exposes its version.
/// The prompt is a versioned file, not a string inside a route handler.
///
/// Repo-root resolution is retargeted to THIS repository (LoveDownload) rather than the
/// A17/FlyRank repo: it reuses <see cref="LoveDownload.Api.RepoPaths"/>, which walks up
/// from the running assembly until it finds LoveDownload.sln.
/// </summary>
public sealed class PromptLoader
{
    public PromptLoader()
    {
        var path = Path.Combine(LoveDownload.Api.RepoPaths.Root, "prompts", "schedule-v1.md");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Prompt file not found: {path}");
        }

        System = File.ReadAllText(path);
        Version = Path.GetFileNameWithoutExtension(path);
    }

    public string Version { get; }
    public string System { get; }
}
