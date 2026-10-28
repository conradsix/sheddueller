#pragma warning disable IDE0130

namespace Microsoft.AspNetCore.Builder;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.StaticWebAssets;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Sheddueller.Dashboard.Components;

/// <summary>
/// Endpoint mapping extensions for the embedded Sheddueller dashboard.
/// </summary>
public static class ShedduellerDashboardEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the dashboard UI under the supplied path using minimal hosting.
    /// </summary>
    /// <param name="app">The web application to map the dashboard branch on.</param>
    /// <param name="path">The absolute route path for the dashboard root.</param>
    /// <returns>The same web application for chained registration.</returns>
    public static WebApplication MapShedduellerDashboard(
        this WebApplication app,
        string path = "/sheddueller")
    {
        ArgumentNullException.ThrowIfNull(app);

        ((IApplicationBuilder)app).MapShedduellerDashboard(path);
        return app;
    }

    /// <summary>
    /// Maps the dashboard UI under the supplied path using an application branch.
    /// </summary>
    /// <param name="app">The application builder to map the dashboard branch on.</param>
    /// <param name="path">The absolute route path for the dashboard root.</param>
    /// <returns>The same application builder for chained middleware registration.</returns>
    public static IApplicationBuilder MapShedduellerDashboard(
        this IApplicationBuilder app,
        string path = "/sheddueller")
    {
        ArgumentNullException.ThrowIfNull(app);

        if (string.IsNullOrWhiteSpace(path) || path[0] != '/')
        {
            throw new ArgumentException("Dashboard path must be an absolute non-empty route path.", nameof(path));
        }

        if (app is IEndpointRouteBuilder endpoints)
        {
            EnableStaticWebAssets(endpoints.ServiceProvider);
        }

        app.Map(path, branch =>
        {
            if (RequiresCanonicalTrailingSlash(path))
            {
                branch.Use(async (context, next) =>
                {
                    if (!context.Request.Path.HasValue)
                    {
                        context.Response.Redirect(CreateCanonicalDashboardRoot(context, path: string.Empty));
                        return;
                    }

                    await next(context).ConfigureAwait(false);
                });
            }

            branch.UseStaticFiles();
            branch.UseRouting();
            branch.UseAntiforgery();
            branch.UseEndpoints(endpoints =>
            {
                MapStaticAssetsIfManifestExists(endpoints);
                endpoints.MapRazorComponents<DashboardApp>()
                  .AddInteractiveServerRenderMode();
            });
        });

        return app;
    }

    /// <summary>
    /// Maps the dashboard UI under the supplied path.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder to map the dashboard endpoints on.</param>
    /// <param name="path">The absolute route path for the dashboard root.</param>
    /// <returns>The mapped endpoint group for further conventions.</returns>
    public static IEndpointConventionBuilder MapShedduellerDashboard(
        this IEndpointRouteBuilder endpoints,
        string path = "/sheddueller")
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        if (string.IsNullOrWhiteSpace(path) || path[0] != '/')
        {
            throw new ArgumentException("Dashboard path must be an absolute non-empty route path.", nameof(path));
        }

        EnableStaticWebAssets(endpoints.ServiceProvider);

        var group = endpoints.MapGroup(path);
        MapStaticAssetsIfManifestExists(group);
        group.MapRazorComponents<DashboardApp>()
          .AddInteractiveServerRenderMode();

        return group;
    }

    private static bool RequiresCanonicalTrailingSlash(string path)
      => path.Length > 1 && path[^1] != '/';

    private static void MapStaticAssetsIfManifestExists(IEndpointRouteBuilder endpoints)
    {
        var environment = endpoints.ServiceProvider.GetRequiredService<IHostEnvironment>();
        var manifestPath = Path.Combine(
          AppContext.BaseDirectory,
          $"{environment.ApplicationName}.staticwebassets.endpoints.json");

        if (File.Exists(manifestPath))
        {
            endpoints.MapStaticAssets(manifestPath);
        }
    }

    private static void EnableStaticWebAssets(IServiceProvider serviceProvider)
    {
        var environment = serviceProvider.GetService<IWebHostEnvironment>();
        var configuration = serviceProvider.GetService<IConfiguration>();
        if (environment is not null && configuration is not null)
        {
            StaticWebAssetsLoader.UseStaticWebAssets(environment, configuration);
        }
    }

    private static string CreateCanonicalDashboardRoot(HttpContext context, string path)
      => string.Concat(
        context.Request.PathBase.ToUriComponent(),
        path,
        "/",
        context.Request.QueryString.ToUriComponent());
}
