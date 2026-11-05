using QuasarAssistant.Quasar;

namespace QuasarAssistant.Tests;

public sealed class NavTests
{
    [Fact]
    public void NoNavItemLightsUpOnAnotherOnesPage()
    {
        // Quasar's nav links match by prefix, so no href may sit under another one.
        var hrefs = new AssistantQuasarPlugin().GetNavItems().Select(item => item.Href).ToList();
        foreach (var href in hrefs)
            Assert.DoesNotContain(
                hrefs,
                other => other.StartsWith(href + "/", StringComparison.Ordinal)
            );
    }
}
