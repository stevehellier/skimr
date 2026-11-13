using Microsoft.Extensions.DependencyInjection;
using Skimr.Infrastructure.Content;
using Skimr.Infrastructure.Feeds;
using Skimr.Infrastructure.Fetching;

namespace Skimr.Infrastructure;

public static class InfrastructureServices
{
    public static IServiceCollection AddSkimrInfrastructure(this IServiceCollection services, FetchOptions fetch, ContentOptions content)
    {
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

        services.AddSingleton<ContentSanitizer>();
        services.AddSingleton<FeedDiscovery>();

        services.AddScoped<FeedRefresher>();
        services.AddScoped<IFeedRefresher>(sp => sp.GetRequiredService<FeedRefresher>());
        services.AddSingleton<FeedScheduler>();
        services.AddHostedService(sp => sp.GetRequiredService<FeedScheduler>());

        return services;
    }
}
