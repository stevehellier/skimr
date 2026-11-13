using Microsoft.Extensions.DependencyInjection;
using Skimr.Infrastructure.Content;
using Skimr.Infrastructure.Feeds;
using Skimr.Infrastructure.Fetching;

namespace Skimr.Infrastructure;

public static class InfrastructureServices
{
    public static IServiceCollection AddSkimrInfrastructure(this IServiceCollection services, FetchOptions fetch, ContentOptions content, Reading.RetentionOptions retention, Ai.AiOptions ai)
    {
        services.AddSingleton(retention);
        services.AddSingleton(fetch);
        services.AddSingleton(content);
        services.AddSingleton(TimeProvider.System);

        services.AddHttpClient(OutboundHttp.ClientName, client =>
            {
                // OutboundHttp applies its own timeout across headers and body.
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", fetch.UserAgent);
            })
            .ConfigurePrimaryHttpMessageHandler(() => SafeHttpHandler.Create(fetch));
        services.AddSingleton<IOutboundHttp, OutboundHttp>();

        services.AddSingleton(ai);
        if (ai.Enabled)
        {
            // The admin chose this endpoint, so it may be on the LAN (Ollama, LM Studio); cloud
            // metadata addresses stay blocked, and redirects are refused (PLAN D63).
            var aiFetch = fetch with { AllowPrivateNetworks = true, MaxRedirects = 0, Timeout = ai.Timeout, MaxResponseBytes = 4 * 1024 * 1024 };
            services.AddHttpClient(OutboundHttp.AiClientName, client =>
                {
                    client.Timeout = Timeout.InfiniteTimeSpan;
                    client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", fetch.UserAgent);
                })
                .ConfigurePrimaryHttpMessageHandler(() => SafeHttpHandler.Create(aiFetch));
            services.AddSingleton<Ai.ISummariser>(sp =>
            {
                var http = new OutboundHttp(sp.GetRequiredService<IHttpClientFactory>(), aiFetch, sp.GetRequiredService<TimeProvider>(), OutboundHttp.AiClientName);
                return ai.Provider == Ai.AiProvider.Anthropic
                    ? new Ai.AnthropicSummariser(http, ai, sp.GetRequiredService<TimeProvider>())
                    : new Ai.OpenAiCompatibleSummariser(http, ai, sp.GetRequiredService<TimeProvider>());
            });
        }

        services.AddScoped<Ai.AiService>();
        services.AddHostedService<Ai.AiFeedWorker>();

        services.AddSingleton<ContentSanitizer>();
        services.AddSingleton<ImageProxy>();
        services.AddScoped<FaviconService>();
        services.AddSingleton<FeedDiscovery>();

        services.AddScoped<ArticleIngester>();
        services.AddScoped<WebSub>();
        services.AddScoped<FeedRefresher>();
        services.AddScoped<IFeedRefresher>(sp => sp.GetRequiredService<FeedRefresher>());
        services.AddSingleton<FeedScheduler>();
        services.AddScoped<Reading.ReadingService>();
        services.AddScoped<Reading.LibraryService>();
        services.AddScoped<Reading.BoardService>();
        services.AddScoped<Reading.OpmlService>();
        services.AddScoped<Reading.FullTextService>();
        services.AddScoped<Reading.RetentionService>();
        services.AddHostedService<Reading.RetentionWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<FeedScheduler>());

        return services;
    }
}
