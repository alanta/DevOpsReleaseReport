using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ReleaseReport;

public static class Configuration
{
    public static IServiceCollection AddReleaseServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AzureDevopsApiOptions>(configuration.GetSection(AzureDevopsApiOptions.ConfigurationSection));
        services.AddTransient<IReleaseInformationService, YamlPipelineReleaseInformationService>();

        return services;
    }
}