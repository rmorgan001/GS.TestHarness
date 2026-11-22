/* Copyright(C) 2026  Rob Morgan

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published
    by the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace GS.TestHarness.Engine;

/// <summary>
/// A GitHub repository given as a server's source code ("https://github.com/owner/repo", optionally
/// "/tree/branch" and a folder in it): downloaded once as a zip into a cache on this computer, which the
/// <see cref="SourceLocator"/> then reads as it would a folder. Giving the URL again downloads it again (the latest).
/// A private repository needs a token in the GITHUB_TOKEN environment variable.
/// </summary>
public sealed partial record GitHubSource(string Owner, string Repo, string? Ref, string? SubPath)
{
    /// <summary>Where downloaded repositories are kept (settable for tests).</summary>
    internal static string CacheRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GS.TestHarness", "github");

    private const string Marker = ".gs-harness-download";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GS.TestHarness", "1.0"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    [GeneratedRegex(@"^(?:https?://)?(?:www\.)?github\.com/(?<owner>[\w.-]+)/(?<repo>[\w.-]+?)(?:\.git)?(?:/(?:tree|blob)/(?<ref>[^/?#]+)(?:/(?<path>[^?#]*))?)?/?(?:[?#].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    /// <summary>The repository a GitHub URL names, or null when <paramref name="text"/> isn't one.</summary>
    public static GitHubSource? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = UrlPattern().Match(text.Trim());
        if (!m.Success) return null;
        var path = m.Groups["path"].Value.Trim('/');
        return new GitHubSource(m.Groups["owner"].Value, m.Groups["repo"].Value,
            m.Groups["ref"].Success ? Uri.UnescapeDataString(m.Groups["ref"].Value) : null, path.Length > 0 ? Uri.UnescapeDataString(path) : null);
    }

    /// <summary>Whether <paramref name="text"/> is a URL (of GitHub or not) rather than a folder.</summary>
    public static bool LooksLikeUrl(string? text)
        => text is not null && (text.Contains("://", StringComparison.Ordinal) || text.TrimStart().StartsWith("github.com/", StringComparison.OrdinalIgnoreCase));

    /// <summary>"owner/repo" (and "@branch", and the folder).</summary>
    public string Display => $"{Owner}/{Repo}{(Ref is null ? "" : "@" + Ref)}{(SubPath is null ? "" : "/" + SubPath)}";

    private string Cache => Path.Combine(CacheRoot, Safe(Owner), Safe(Repo), Safe(Ref ?? "default-branch"));

    private static string Safe(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    /// <summary>The downloaded copy's folder (the repository's top, or the folder given in the URL), or null when it hasn't been downloaded.</summary>
    public string? LocalFolder
    {
        get
        {
            var src = Path.Combine(Cache, "src");
            if (!File.Exists(Path.Combine(Cache, Marker)) || !Directory.Exists(src)) return null;
            var folder = SubPath is null ? src : Path.Combine(src, SubPath.Replace('/', Path.DirectorySeparatorChar));
            return Directory.Exists(folder) ? folder : null;
        }
    }

    /// <summary>When the copy was downloaded, or null.</summary>
    public DateTime? Downloaded => File.Exists(Path.Combine(Cache, Marker)) ? File.GetLastWriteTime(Path.Combine(Cache, Marker)) : null;

    /// <summary>The access used for the cached download, or null for an older copy with no record.</summary>
    public GitHubAccess? DownloadAccess
    {
        get
        {
            var marker = Path.Combine(Cache, Marker);
            return File.Exists(marker) && Enum.TryParse<GitHubAccess>(File.ReadLines(marker).Skip(2).FirstOrDefault(), out var access)
                && Enum.IsDefined(access) ? access : null;
        }
    }

    public static GitHubAccess InitialAccess => Environment.GetEnvironmentVariable("GITHUB_TOKEN") is { Length: > 0 }
        ? GitHubAccess.Token : GitHubAccess.Anonymous;

    public static string AccessDescription(GitHubAccess access) => access switch
    {
        GitHubAccess.Token => "GitHub access: using a private token from GITHUB_TOKEN.",
        GitHubAccess.Anonymous => "GitHub access: anonymous (no token).",
        GitHubAccess.AnonymousRetry => "GitHub access: anonymous retry after the private-token request failed.",
        _ => throw new ArgumentOutOfRangeException(nameof(access))
    };

    /// <summary>
    /// Downloads the repository (its default branch, or the branch, tag or commit given) and replaces the copy kept
    /// here. Retries a failed token-authenticated HTTP request once anonymously. Returns why it couldn't, or null.
    /// </summary>
    public async Task<string?> DownloadAsync(CancellationToken token = default, Action<GitHubAccess>? reportAccess = null)
    {
        var url = $"https://api.github.com/repos/{Uri.EscapeDataString(Owner)}/{Uri.EscapeDataString(Repo)}/zipball{(Ref is null ? "" : "/" + Uri.EscapeDataString(Ref))}";
        var work = Path.Combine(CacheRoot, ".work-" + Guid.NewGuid().ToString("N"));
        var access = InitialAccess;
        try
        {
            using var response = await SendAsync(url, token, mode =>
            {
                access = mode;
                reportAccess?.Invoke(mode);
            });
            if (!response.IsSuccessStatusCode)
            {
                var problem = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "GitHub rejected anonymous access (401 Unauthorized). Check whether a proxy requires authentication.",
                    HttpStatusCode.NotFound => $"GitHub has no {Display}, or it's private and needs a valid GITHUB_TOKEN with access to the repository.",
                    HttpStatusCode.Forbidden or (HttpStatusCode)429 => "GitHub refused the anonymous download (access denied or rate limited): try again later, or set a valid GITHUB_TOKEN.",
                    _ => $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}."
                };
                return $"{problem} {AccessDescription(access)}";
            }
            Directory.CreateDirectory(work);
            var zip = Path.Combine(work, "repo.zip");
            await using (var file = File.Create(zip))
                await response.Content.CopyToAsync(file, token);
            var unpacked = Path.Combine(work, "unpacked");
            ZipFile.ExtractToDirectory(zip, unpacked);
            // The zip holds one folder, "owner-repo-commit".
            var top = Directory.GetDirectories(unpacked) is [var only] && Directory.GetFiles(unpacked).Length == 0 ? only : unpacked;

            if (Directory.Exists(Cache)) Directory.Delete(Cache, recursive: true);
            Directory.CreateDirectory(Cache);
            Directory.Move(top, Path.Combine(Cache, "src"));
            await File.WriteAllTextAsync(Path.Combine(Cache, Marker), $"{url}\n{DateTime.UtcNow:O}\n{access}\n", token);
            if (SubPath is not null && LocalFolder is null) return $"The repository has no folder {SubPath}. {AccessDescription(access)}";
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or TaskCanceledException)
        {
            return $"Couldn't download {Display} from GitHub: {ex.Message} {AccessDescription(access)}";
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(string url, CancellationToken token, Action<GitHubAccess> reportAccess)
    {
        var githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        var hasToken = githubToken is { Length: > 0 };
        reportAccess(hasToken ? GitHubAccess.Token : GitHubAccess.Anonymous);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (hasToken) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);
        var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!hasToken || response.IsSuccessStatusCode) return response;

        response.Dispose();
        token.ThrowIfCancellationRequested();
        reportAccess(GitHubAccess.AnonymousRetry);
        using var anonymousRequest = new HttpRequestMessage(HttpMethod.Get, url);
        return await Http.SendAsync(anonymousRequest, HttpCompletionOption.ResponseHeadersRead, token);
    }
}

public enum GitHubAccess
{
    Anonymous,
    Token,
    AnonymousRetry
}
