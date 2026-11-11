using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Plugin.Abstractions;
using Quasar.Plugin.Abstractions.Extensions;
using Quasar.Plugin.Abstractions.Navigation;
using Quasar.Plugin.Abstractions.Security;
using QuasarAssistant.Components;
using QuasarAssistant.Lore;

namespace QuasarAssistant.Quasar;

public sealed class AssistantQuasarPlugin : IQuasarPlugin
{
    public string Id => AssistantServiceCollectionExtensions.PluginId;

    public string DisplayName => "Assistant";

    public void ConfigureServices(IServiceCollection services, QuasarPluginContext context) =>
        services.AddQuasarAssistant(
            Path.Combine(context.InstallDirectory, "plugin-data", context.PluginId)
        );

    // Only the lore API, whose operations are on the agents' deny list: nothing an agent could call reaches the
    // settings or the lore. The other operations (MCP calls, the player's Grid Backups ones) have no routes.
    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints, QuasarPluginContext context) =>
        endpoints.MapLoreApi();

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
            "Monitor",
            "/assistant/monitor",
            AssistantIcons.Monitor,
            QuasarNavZones.Tools,
            702,
            QuasarPolicyNames.CanUseAssistant
        );
        yield return new QuasarNavItem(
            "Lore",
            "/assistant/lore",
            AssistantIcons.Lore,
            QuasarNavZones.Settings,
            701,
            QuasarPolicyNames.CanEditServers
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
