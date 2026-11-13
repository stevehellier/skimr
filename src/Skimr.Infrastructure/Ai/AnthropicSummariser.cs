using System.Text;
using System.Text.Json;
using Skimr.Infrastructure.Fetching;

namespace Skimr.Infrastructure.Ai;

/// <summary>Anthropic Messages API over plain HTTP (PLAN D13), API version 2026-10-21.</summary>
internal sealed class AnthropicSummariser(OutboundHttp http, AiOptions options, TimeProvider time) : ChatSummariser(http, options, time)
{
    public const string ApiVersion = "2026-10-21";
    public const string FallbackBeta = "server-side-fallback-2026-07-01";

    /// <summary>
    /// Non-streaming ceiling. Summaries are a few sentences, but on current models thinking
    /// counts toward it, and a low cap cuts the answer off mid-way.
    /// </summary>
    public const int MaxTokens = 16_000;

    /// <summary>Models that take the server-side refusal fallback ("default" routing).</summary>
    private static readonly string[] FallbackModels = ["claude-fable-5-2", "claude-fable-5-1", "claude-opus-5-5", "claude-opus-5", "claude-sonnet-5-5"];

    /// <summary>Model families that accept output_config.effort; older ones reject it.</summary>
    private static readonly string[] EffortPrefixes = ["claude-fable-5", "claude-mythos-5", "claude-opus-5", "claude-sonnet-5", "claude-haiku-5", "claude-opus-4-6", "claude-opus-4-7", "claude-opus-4-8", "claude-sonnet-4-6"];

    protected override async Task<string> CompleteAsync(string system, string user, CancellationToken cancellationToken)
    {
        var model = Options.Model;
        var fallback = FallbackModels.Contains(model, StringComparer.Ordinal);
        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["max_tokens"] = MaxTokens,
            ["system"] = system,
            ["messages"] = new[] { new { role = "user", content = user } },
        };
        if (EffortPrefixes.Any(p => model.StartsWith(p, StringComparison.Ordinal)))
        {
            // Summaries and yes/no filtering don't need deep reasoning.
            body["output_config"] = new { effort = "low" };
        }

        var headers = new Dictionary<string, string>
        {
            ["x-api-key"] = Options.ApiKey ?? "",
            ["anthropic-version"] = ApiVersion,
        };
        if (fallback)
        {
            // If a safety classifier declines, the API retries on a suitable model in the same call.
            body["fallbacks"] = "default";
            headers["anthropic-beta"] = FallbackBeta;
        }

        using var json = await PostAsync(new Uri(Options.BaseUrl!, "v1/messages"), body, headers, ErrorMessage, cancellationToken);
        var root = json.RootElement;
        var stop = root.TryGetProperty("stop_reason", out var s) ? s.GetString() : null;
        if (stop == "refusal")
        {
            throw new AiException("The model declined to process this article.");
        }

        var text = new StringBuilder();
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                // Thinking blocks and anything else that isn't the answer are skipped.
                if (block.TryGetProperty("type", out var type) && type.GetString() == "text" && block.TryGetProperty("text", out var t))
                {
                    text.Append(t.GetString());
                }
            }
        }

        return text.ToString();
    }

    private static string? ErrorMessage(JsonDocument json) =>
        json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty("message", out var message) ? message.GetString() : null;
}
