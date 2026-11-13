using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using Skimr.Web.Hosting;

namespace Skimr.Web.Api;

/// <summary>
/// OpenAPI document and Scalar UI at /api/docs (PLAN D58). Both need a signed-in browser
/// session (the fallback policy); the API itself takes bearer tokens only.
/// </summary>
internal static class ApiDocs
{
    public const string DocumentPath = "/api/docs/v1.json";

    public static IServiceCollection AddApiDocs(this IServiceCollection services) =>
        services.AddOpenApi("v1", options =>
        {
            options.ShouldInclude = description => description.RelativePath?.StartsWith("api/v1", StringComparison.Ordinal) == true;
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo { Title = "Skimr API", Version = "v1", Description = "Send `Authorization: Bearer <token>`. Create tokens under Account → API tokens." };
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes["bearer"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", Description = "API token" };
                document.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("bearer", document)] = [] }];
                return Task.CompletedTask;
            });
        });

    public static IEndpointRouteBuilder MapApiDocs(this IEndpointRouteBuilder app)
    {
        app.MapOpenApi("/api/docs/{documentName}.json")
            .WithMetadata(new Microsoft.AspNetCore.Mvc.SkipStatusCodePagesAttribute());
        app.MapScalarApiReference("/api/docs", options => options
                .WithTitle("Skimr API")
                .DisableDefaultFonts()
                .WithOpenApiRoutePattern(DocumentPath))
            .AddEndpointFilter(async (context, next) =>
            {
                // Scalar's page boots with an inline script; relax the CSP for this page only.
                context.HttpContext.Items[SecurityHeaders.OwnPolicyKey] = true;
                context.HttpContext.Response.Headers.ContentSecurityPolicy = DocsPolicy;
                return await next(context);
            });
        return app;
    }

    internal const string DocsPolicy =
        "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "font-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'self'; form-action 'self'";
}
