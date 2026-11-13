using Microsoft.Extensions.DependencyInjection;
using Skimr.Infrastructure.Fetching;
using Skimr.IntegrationTests.Infrastructure;

namespace Skimr.IntegrationTests;

/// <summary>Proves the app's real DI registration routes outbound requests through the SSRF guard.</summary>
public sealed class OutboundGuardTests(SqliteAppFixture app) : IClassFixture<SqliteAppFixture>
{
    [Theory]
    [InlineData("http://127.0.0.1:1/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://[::1]:1/")]
    public async Task Registered_outbound_client_blocks_internal_addresses(string url)
    {
        var http = app.Factory.Services.GetRequiredService<IOutboundHttp>();

        var act = () => http.GetAsync(new FetchRequest(new Uri(url), "*/*"), TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<FetchException>()).Which.Message.Should().StartWith("Blocked");
    }
}
