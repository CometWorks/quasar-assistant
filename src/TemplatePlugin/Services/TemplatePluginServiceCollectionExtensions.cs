using Microsoft.Extensions.DependencyInjection;

namespace TemplatePlugin.Services;

public static class TemplatePluginServiceCollectionExtensions
{
    public static IServiceCollection AddTemplatePluginUi(this IServiceCollection services)
    {
        services.AddSingleton<TemplatePluginState>();
        return services;
    }
}
