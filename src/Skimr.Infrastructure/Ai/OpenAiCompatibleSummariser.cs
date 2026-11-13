using System.Text.Json;
using Skimr.Infrastructure.Fetching;

namespace Skimr.Infrastructure.Ai;

/// <summary>OpenAI-style /chat/completions over plain HTTP (PLAN D13): Ollama, LM Studio, vLLM, OpenAI.</summary>
internal sealed class OpenAiCompatibleSummariser(OutboundHttp http, AiOptions options, TimeProvider time) : ChatSummariser(http, options, time)
{
    protected override async Task<string> CompleteAsync(string system, string user, CancellationToken cancellationToken)
    {
        var body = new
        {
            model = Options.Model,
            messages = new[] { new { role = "system", content = system }, new { role = "user", content = user } },
            stream = false,
        };
        var headers = new Dictionary<string, string>();
        if (Options.ApiKey is { } key)
        {
            headers["Authorization"] = "Bearer " + key;
        }

        using var json = await PostAsync(new Uri(Options.BaseUrl!, "chat/completions"), body, headers, ErrorMessage, cancellationToken);
        var root = json.RootElement;
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            if (choice.TryGetProperty("finish_reason", out var finish) && finish.GetString() == "content_filter")
            {
                throw new AiException("The model declined to process this article.");
            }

            if (choice.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                return content.GetString()!;
            }
        }

        throw new AiException("The AI provider's answer had no message.");
    }

    private static string? ErrorMessage(JsonDocument json) =>
        json.RootElement.TryGetProperty("error", out var error) switch
        {
            true when error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message) => message.GetString(),
            true when error.ValueKind == JsonValueKind.String => error.GetString(),
            _ => null,
        };
}
