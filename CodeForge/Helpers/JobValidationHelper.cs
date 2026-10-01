namespace CodeForge.Helpers;

public static class JobValidationHelper
{
    public static bool IsValidGitHubUrl(string? repoUrl)
    {
        if (string.IsNullOrWhiteSpace(repoUrl) ||
            !Uri.TryCreate(repoUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var cleanPath = uri.AbsolutePath.Trim('/');
        var parts = cleanPath.Split('/');

        return parts.Length == 2 &&
               !string.IsNullOrWhiteSpace(parts[0]) &&
               !string.IsNullOrWhiteSpace(parts[1]);
    }

    public static bool IsValidDescription(string? description)
    {
        return !string.IsNullOrWhiteSpace(description) &&
               description.Length <= 500;
    }

    public static bool IsValidTitle(string? title)
    {
        return !string.IsNullOrWhiteSpace(title) &&
               title.Length <= 100;
    }
}
