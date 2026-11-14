using QuasarAssistant.Components;

namespace QuasarAssistant.Tests;

public sealed class MarkdownTests
{
    [Fact]
    public void ShortAnswerIsOneParagraphWithInlineMarkup()
    {
        Assert.Equal(
            "<p>The server <strong>Alpha</strong> has <code>12</code> players, <em>fine</em>.</p>",
            Markdown.ToHtml("The server **Alpha** has `12` players, *fine*.")
        );
    }

    [Fact]
    public void LongAnswerRendersEveryBlock()
    {
        const string answer = """
            ## Server health

            Up for **3 days**.
            Two things:

            1. The save is slow.
            2. Grids over the limit:
               - *Hammerhead*
               - _Driller 7_

            | Player | PCU |
            |:-------|----:|
            | Kestrel | 18,420 |
            | Mira \| Ondo | 950 |

            ```xml
            <MaxBackupSaves>5</MaxBackupSaves>
            ```

            > Restart to apply.

            ---
            See [the book](https://example.com/book?a=1&b=2).
            """;
        Assert.Equal(
            "<h2>Server health</h2>"
                + "<p>Up for <strong>3 days</strong>.<br>Two things:</p>"
                + "<ol><li>The save is slow.</li><li>Grids over the limit:<ul><li><em>Hammerhead</em></li><li><em>Driller 7</em></li></ul></li></ol>"
                + "<div class=\"assistant-md-table\"><table><thead><tr><th style=\"text-align:left\">Player</th><th style=\"text-align:right\">PCU</th></tr></thead>"
                + "<tbody><tr><td style=\"text-align:left\">Kestrel</td><td style=\"text-align:right\">18,420</td></tr>"
                + "<tr><td style=\"text-align:left\">Mira | Ondo</td><td style=\"text-align:right\">950</td></tr></tbody></table></div>"
                + "<pre><code>&lt;MaxBackupSaves&gt;5&lt;/MaxBackupSaves&gt;</code></pre>"
                + "<blockquote><p>Restart to apply.</p></blockquote>"
                + "<hr>"
                + "<p>See <a href=\"https://example.com/book?a=1&amp;b=2\" target=\"_blank\" rel=\"noopener noreferrer nofollow\">the book</a>.</p>",
            Markdown.ToHtml(answer)
        );
    }

    [Fact]
    public void ListsWithBlankLinesBetweenItemsAreLoose()
    {
        Assert.Equal(
            "<ul><li><p>one</p></li><li><p>two</p></li></ul><p>after</p>",
            Markdown.ToHtml("- one\n\n- two\n\nafter")
        );
        Assert.Equal(
            "<ol start=\"3\"><li>three</li><li>four</li></ol>",
            Markdown.ToHtml("3. three\n4. four")
        );
    }

    [Fact]
    public void SnakeCaseAndCodeKeepTheirMarkers()
    {
        Assert.Equal(
            "<p>Set <code>max_backup_saves</code> or max_backup_saves to <code>**5**</code>.</p>",
            Markdown.ToHtml("Set `max_backup_saves` or max_backup_saves to `**5**`.")
        );
    }

    [Fact]
    public void BareAndAngleUrlsBecomeLinksAndSitePathsStayInTheTab()
    {
        Assert.Equal(
            "<p>Docs: <a href=\"https://example.com/a_b\" target=\"_blank\" rel=\"noopener noreferrer nofollow\">https://example.com/a_b</a>, "
                + "<a href=\"http://x.test/\" target=\"_blank\" rel=\"noopener noreferrer nofollow\">http://x.test/</a> and "
                + "<a href=\"/servers/alpha\">Alpha</a>.</p>",
            Markdown.ToHtml(
                "Docs: https://example.com/a_b, <http://x.test/> and [Alpha](/servers/alpha)."
            )
        );
    }

    [Theory]
    [InlineData("<script>alert(1)</script>", "<p>&lt;script&gt;alert(1)&lt;/script&gt;</p>")]
    [InlineData("<img src=x onerror=alert(1)>", "<p>&lt;img src=x onerror=alert(1)&gt;</p>")]
    [InlineData("[click](javascript:alert(1))", "<p>click</p>")]
    [InlineData("[click](JavaScript:alert(1))", "<p>click</p>")]
    [InlineData("[click](data:text/html,<b>x</b>)", "<p>click</p>")]
    [InlineData("[click](//evil.test/x)", "<p>click</p>")]
    [InlineData("<javascript:alert(1)>", "<p>&lt;javascript:alert(1)&gt;</p>")]
    [InlineData("**<b>bold</b>**", "<p><strong>&lt;b&gt;bold&lt;/b&gt;</strong></p>")]
    [InlineData("`<i>`", "<p><code>&lt;i&gt;</code></p>")]
    [InlineData("# <h1>x</h1>", "<h1>&lt;h1&gt;x&lt;/h1&gt;</h1>")]
    [InlineData(
        "| <a> | b |\n|---|---|\n| <svg onload=x> | c |",
        "<div class=\"assistant-md-table\"><table><thead><tr><th>&lt;a&gt;</th><th>b</th></tr></thead><tbody><tr><td>&lt;svg onload=x&gt;</td><td>c</td></tr></tbody></table></div>"
    )]
    [InlineData("marker \u0001" + "0\u0002 text", "<p>marker 0 text</p>")]
    public void ModelOutputCannotInjectHtml(string markdown, string html)
    {
        Assert.Equal(html, Markdown.ToHtml(markdown));
    }

    [Fact]
    public void LinkUrlCannotBreakOutOfTheAttribute()
    {
        var html = Markdown.ToHtml("[x](https://a.test/\"onmouseover=\"alert(1))");
        Assert.DoesNotContain("\"onmouseover", html);
        Assert.Contains("href=\"https://a.test/&quot;onmouseover=&quot;alert(1)\"", html);
    }

    [Fact]
    public void UnclosedFenceWhileStreamingIsCode()
    {
        Assert.Equal(
            "<p>Run:</p><pre><code>dotnet build\n&lt;</code></pre>",
            Markdown.ToHtml("Run:\n```\ndotnet build\n<")
        );
    }
}
