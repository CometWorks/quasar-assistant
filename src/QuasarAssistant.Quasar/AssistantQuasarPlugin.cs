using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions;
using Quasar.Plugin.Abstractions.Extensions;
using Quasar.Plugin.Abstractions.Navigation;
using Quasar.Plugin.Abstractions.Security;
using QuasarAssistant.Components;

namespace QuasarAssistant.Quasar;

public sealed class AssistantQuasarPlugin : IQuasarPlugin
{
    public string Id => AssistantServiceCollectionExtensions.PluginId;

    public string DisplayName => "Assistant";

    public void ConfigureServices(IServiceCollection services, QuasarPluginContext context) =>
        services.AddQuasarAssistant(
            Path.Combine(context.InstallDirectory, "plugin-data", context.PluginId)
        );

    // No endpoints. The only operations are the MCP calls and the player's Grid Backups ones, registered with
    // the services: nothing an operation or agent could call reaches the settings.
    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints, QuasarPluginContext context) { }

    public IEnumerable<Assembly> GetRazorAssemblies()
    {
        yield return typeof(AssistantDrawer).Assembly;
    }

    public IEnumerable<QuasarNavItem> GetNavItems()
    {
        yield return new QuasarNavItem(
            "Assistant",
            // Not "/assistant": the nav link matches by prefix, which would light it up on Approvals too.
            "/assistant/chat",
            AssistantIcons.Assistant,
            QuasarNavZones.Tools,
            700,
            QuasarPolicyNames.CanUseAssistant
        );
        yield return new QuasarNavItem(
            "Approvals",
            "/assistant/approvals",
            AssistantIcons.Approval,
            QuasarNavZones.Tools,
            701,
            QuasarPolicyNames.CanUseAssistant
        );
        yield return new QuasarNavItem(
            "Assistant settings",
            "/assistant/settings",
            AssistantIcons.Settings,
            QuasarNavZones.Settings,
            700,
            QuasarPolicyNames.CanManageSecurity
        );
    }

    public IEnumerable<QuasarExtensionContribution> GetExtensions()
    {
        yield return new QuasarExtensionContribution(
            QuasarExtensionTargets.LayoutDrawer,
            typeof(AssistantDrawer),
            QuasarPatchMode.After,
            700,
            Id,
            QuasarPolicyNames.CanUseAssistant
        );
    }
}
