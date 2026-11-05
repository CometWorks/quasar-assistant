using System.Text;
using System.Text.Json;
using QuasarAssistant.Agent;

namespace QuasarAssistant.Tests;

public sealed class ResultFilterTests
{
    [Fact]
    public void RedactsSecretNamedValuesAtAnyDepth()
    {
        var json = ResultFilter.Json(
            new
            {
                name = "alpha",
                rconPassword = "hunter2",
                nested = new
                {
                    apiKey = "abc",
                    steamToken = "t",
                    list = new[] { new { client_secret = "s", port = 27016 } },
                },
                privateKey = "pk",
            }
        );
        Assert.DoesNotContain("hunter2", json);
        Assert.DoesNotContain("\"abc\"", json);
        Assert.DoesNotContain("\"pk\"", json);
        Assert.Contains("\"name\":\"alpha\"", json);
        Assert.Contains("\"port\":27016", json);
        Assert.Equal(5, json.Split("<redacted>").Length - 1);
    }

    [Theory]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.abc.def", "eyJhbGci")]
    [InlineData("key sk-ant-api03-AAAAAAAAAAAAAAAAAAAAAAAA in a log", "sk-ant-api03")]
    [InlineData("connecting with password=hunter2 to the db", "hunter2")]
    [InlineData("\"api_key\": \"xyz123\"", "xyz123")]
    public void RedactsSecretsInText(string text, string secret)
    {
        var redacted = ResultFilter.RedactText(text);
        Assert.DoesNotContain(secret, redacted);
        Assert.Contains("<redacted>", redacted);
    }

    [Fact]
    public void LeavesOrdinaryTextAlone()
    {
        const string text = "Server alpha started with 12 players, grid Token Ring has 300 blocks.";
        Assert.Equal(text, ResultFilter.RedactText(text));
    }

    [Fact]
    public void CapsAndSaysItCut()
    {
        var text = new string('é', 20000); // two bytes each
        var capped = ResultFilter.Cap(text, 16 * 1024);
        var kept = capped[..capped.IndexOf("\n[cut:", StringComparison.Ordinal)];
        Assert.True(Encoding.UTF8.GetByteCount(kept) <= 16 * 1024);
        Assert.Contains("the result was 40000 bytes", capped);
        Assert.Equal("short", ResultFilter.Cap("short", 16 * 1024));
    }
}
