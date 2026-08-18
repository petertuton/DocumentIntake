using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using DurableFunctionsMonitor.DotNetIsolated;
using DocumentIntake.Functions.Options;
using DocumentIntake.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication(workerAppBuilder =>
    {
        workerAppBuilder.UseDurableFunctionsMonitor((settings, _) =>
        {
            settings.Mode = DfmMode.ReadOnly;
        });
    })
    .ConfigureServices((context, services) =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        services.AddOptions<StorageOptions>()
            .Bind(context.Configuration.GetSection(StorageOptions.SectionName))
            .ValidateOnStart();

        services.AddOptions<ContentUnderstandingOptions>()
            .Bind(context.Configuration.GetSection(ContentUnderstandingOptions.SectionName))
            .ValidateOnStart();

        services.AddOptions<DataverseOptions>()
            .Bind(context.Configuration.GetSection(DataverseOptions.SectionName))
            .ValidateOnStart();

        // One credential instance, reused by every Azure client and token handler.
        services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());

        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
            if (string.IsNullOrWhiteSpace(options.BlobServiceUri))
            {
                throw new InvalidOperationException(
                    $"{StorageOptions.SectionName}:{nameof(StorageOptions.BlobServiceUri)} must be configured.");
            }

            return new BlobServiceClient(new Uri(options.BlobServiceUri), sp.GetRequiredService<TokenCredential>());
        });

        services.AddSingleton<IBlobRouter, BlobRouter>();

        services
            .AddHttpClient<IContentUnderstandingClient, ContentUnderstandingClient>(ContentUnderstandingClient.HttpClientName)
            .AddHttpMessageHandler(sp => new ManagedIdentityAuthHandler(
                sp.GetRequiredService<TokenCredential>(),
                "https://cognitiveservices.azure.com/.default"));

        services
            .AddHttpClient<IDataverseClient, DataverseClient>(DataverseClient.HttpClientName)
            .AddHttpMessageHandler(sp =>
            {
                // Scope is the Dataverse environment url; the identity must exist as an
                // application user in Dataverse. Swap this handler if an app registration is chosen.
                var options = sp.GetRequiredService<IOptions<DataverseOptions>>().Value;
                var scope = string.IsNullOrWhiteSpace(options.EnvironmentUrl)
                    ? "https://service.powerapps.com/.default"
                    : $"{options.EnvironmentUrl.TrimEnd('/')}/.default";

                return new ManagedIdentityAuthHandler(sp.GetRequiredService<TokenCredential>(), scope);
            });

        services.AddSingleton<IFieldMapper>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<FieldMapper>>();
            return new FieldMapper(FieldMapLoader.Load(logger), logger);
        });
    })
    .Build();

host.Run();

/// <summary>
/// Loads the analyzer-field to form-column mapping that ships alongside the app.
/// </summary>
internal static class FieldMapLoader
{
    private const string RelativePath = "analyzers/contentunderstanding/field-map.json";

    public static IReadOnlyDictionary<string, string> Load(ILogger logger)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, RelativePath),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", RelativePath),
            Path.Combine(Directory.GetCurrentDirectory(), RelativePath),
        };

        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(candidate);
            if (!File.Exists(full))
            {
                continue;
            }

            using var stream = File.OpenRead(full);
            using var document = JsonDocument.Parse(stream);

            if (document.RootElement.TryGetProperty("columns", out var columns))
            {
                var map = columns.EnumerateObject()
                    .ToDictionary(p => p.Name, p => p.Value.GetString() ?? p.Name, StringComparer.OrdinalIgnoreCase);

                logger.LogInformation("Loaded {Count} field mappings from {Path}.", map.Count, full);
                return map;
            }
        }

        logger.LogWarning("Field map not found; no fields will be mapped.");
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
