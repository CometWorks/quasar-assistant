using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions;
using Quasar.Plugin.Abstractions.Extensions;
using Quasar.Plugin.Abstractions.Navigation;
using Quasar.Plugin.Abstractions.Security;
using TemplatePlugin.Components;
using TemplatePlugin.Services;

namespace TemplatePlugin.Quasar;

public sealed class TemplateQuasarPlugin : IQuasarPlugin
{
    public string Id => "todo.template-plugin";

    public string DisplayName => "Template Plugin";

    public void ConfigureServices(IServiceCollection services, QuasarPluginContext context)
    {
        // Use context.InstallDirectory for Quasar-owned persistent files.
        services.AddTemplatePluginUi();
    }

    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints, QuasarPluginContext context)
    {
        // Map plugin-specific minimal APIs here if the UI needs Quasar-hosted endpoints.
    }

    public IEnumerable<Assembly> GetRazorAssemblies()
    {
        yield return typeof(TemplateDashboardPanel).Assembly;
    }

    public IEnumerable<QuasarNavItem> GetNavItems()
    {
        yield return new QuasarNavItem(
            "Template",
            "/template-plugin",
            TemplateIcons.Extension,
            QuasarNavZones.Main,
            800,
            QuasarPolicyNames.CanView);
    }

    public IEnumerable<QuasarExtensionContribution> GetExtensions()
    {
        yield return new QuasarExtensionContribution(
            QuasarExtensionTargets.EntityActions,
            typeof(TemplateEntityAction),
            QuasarPatchMode.After,
            800,
            Id,
            QuasarPolicyNames.CanView);
    }
}
