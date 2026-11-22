using System.Text.RegularExpressions;

namespace QuasarAssistant.Lore;

/// <summary>
/// A skill folder in a Git repository, given as one URL: a GitHub or GitLab tree URL, a Forgejo or Gitea
/// <c>src/branch|tag|commit</c> URL, or a clone URL with an optional <c>#ref:path</c>. In the web forms the first
/// segment after <c>tree</c> or <c>branch</c> is the ref, so a branch with a <c>/</c> in its name needs the
/// <c>#ref:path</c> form.
/// </summary>
/// <param name="Ref">Branch, tag or commit; empty for the repository's default branch.</param>
/// <param name="Path">The skill folder inside the repository; empty for the root.</param>
public sealed partial record GitSkillUrl(string Repository, string Ref, string Path)
{
    public static bool TryParse(
        string? text,
        bool allowFile,
        out GitSkillUrl? url,
        out string? error
    )
    {
        url = null;
        text = text?.Trim() ?? "";
        if (
            !Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || !(uri.Scheme == Uri.UriSchemeHttps || (allowFile && uri.Scheme == Uri.UriSchemeFile))
        )
        {
            error =
                "Give the skill as an https URL, such as a GitHub tree URL of the skill folder.";
            return false;
        }
        if (uri.UserInfo.Length > 0)
        {
            error = "Leave credentials out of the URL; put an access token in the token field.";
            return false;
        }
        error = null;
        var hash = text.IndexOf('#');
        if (hash >= 0)
        {
            var spec = text[(hash + 1)..];
            var colon = spec.IndexOf(':');
            url = new(
                text[..hash],
                colon < 0 ? spec : spec[..colon],
                colon < 0 ? "" : Clean(spec[(colon + 1)..])
            );
            return true;
        }
        var match = WebForm().Match(text);
        url = match.Success
            ? new(
                match.Groups["repo"].Value + ".git",
                Uri.UnescapeDataString(match.Groups["ref"].Value),
                Clean(Uri.UnescapeDataString(match.Groups["path"].Value))
            )
            : new(text, "", "");
        return true;
    }

    private static string Clean(string path) => path.Trim().Trim('/');

    // github.com/o/r/tree/REF/path, gitlab.com/g/r/-/tree/REF/path, forgejo/o/r/src/branch/REF/path
    [GeneratedRegex(
        @"^(?<repo>https://[^/]+/.+?)(?:\.git)?/(?:-/)?(?:tree|src/(?:branch|tag|commit))/(?<ref>[^/?#]+)/?(?<path>[^?#]*)$"
    )]
    private static partial Regex WebForm();
}
