using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartMacro.Core.Network;

/// <summary>Outcome of querying an HTTPS release manifest for a newer version of SmartMacro.</summary>
public enum UpdateCheckOutcome
{
    UpToDate = 0,
    UpdateAvailable = 1,
    CheckFailed = 2,
}

/// <summary>Lightweight JSON manifest schema describing the latest public SmartMacro release.</summary>
public sealed class UpdateManifest
{
    [JsonPropertyName("latestVersion")]
    public string LatestVersion { get; set; } = string.Empty;

    [JsonPropertyName("releaseNotes")]
    public string? ReleaseNotes { get; set; }

    [JsonPropertyName("downloadUrl")]
    public string? DownloadUrl { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonPropertyName("publishedAt")]
    public string? PublishedAt { get; set; }
}

/// <summary>Result returned by <see cref="UpdateCheckService.CheckForUpdatesAsync"/>.</summary>
public sealed record UpdateCheckResult(
    UpdateCheckOutcome Outcome,
    string CurrentVersion,
    string? LatestVersion,
    string? ReleaseNotes,
    string? DownloadUrl,
    string? Sha256,
    string? PublishedAt,
    string Message)
{
    public static UpdateCheckResult UpToDate(string currentVersion, string? latestVersion = null)
        => new(
            UpdateCheckOutcome.UpToDate,
            currentVersion,
            latestVersion ?? currentVersion,
            null,
            null,
            null,
            null,
            $"You are running the latest version ({currentVersion}).");

    public static UpdateCheckResult Available(
        string currentVersion,
        string latestVersion,
        string? releaseNotes,
        string? downloadUrl,
        string? sha256,
        string? publishedAt)
        => new(
            UpdateCheckOutcome.UpdateAvailable,
            currentVersion,
            latestVersion,
            releaseNotes,
            downloadUrl,
            sha256,
            publishedAt,
            $"Update available: v{latestVersion} (installed: v{currentVersion}).");

    public static UpdateCheckResult Failed(string currentVersion, string message)
        => new(
            UpdateCheckOutcome.CheckFailed,
            currentVersion,
            null,
            null,
            null,
            null,
            null,
            message);
}

/// <summary>
/// On-demand HTTPS update manifest checker with strict URL/IP validation, bounded response reads,
/// SHA-256 metadata normalization, and semantic version comparison.
/// </summary>
public sealed class UpdateCheckService
{
    public const string DefaultManifestUrl =
        "https://raw.githubusercontent.com/nenesakura-39/SmartMacro/main/updates/manifest.json";

    public const string OfficialReleasesUrl =
        "https://github.com/nenesakura-39/SmartMacro/releases";

    public const string OfficialCommunityUrl =
        "https://discord.gg/CKBk5KnGmk";

    public const int MaxManifestBytes = 64 * 1024;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private static readonly HttpRequestOptionsKey<IPAddress[]> PinnedAddressesOptionKey =
        new("SmartMacro.UpdatePinnedAddresses");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly Lazy<HttpClient> SharedClient = new(CreateDefaultHttpClient);

    private readonly HttpMessageInvoker? _customInvoker;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>>? _dnsResolver;

    public UpdateCheckService(
        HttpMessageInvoker? httpInvoker = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? dnsResolver = null)
    {
        _customInvoker = httpInvoker;
        _dnsResolver = dnsResolver;
    }

    public static UpdateCheckService Shared { get; } = new();

    /// <summary>
    /// Fetches the release manifest on demand and compares its version against <paramref name="currentVersion"/>.
    /// </summary>
    public async Task<UpdateCheckResult> CheckForUpdatesAsync(
        string currentVersion,
        string? manifestUrl = null,
        CancellationToken cancellationToken = default)
    {
        var current = string.IsNullOrWhiteSpace(currentVersion) ? "0.0.0" : currentVersion.Trim();
        var effectiveUrl = string.IsNullOrWhiteSpace(manifestUrl)
            ? DefaultManifestUrl
            : manifestUrl.Trim();

        var urlValidation = await WebhookSecurity.ValidateUrlAndResolveAsync(
            effectiveUrl,
            allowLocalNetwork: false,
            _dnsResolver,
            cancellationToken).ConfigureAwait(false);

        if (!urlValidation.IsValid || urlValidation.Uri is null)
            return UpdateCheckResult.Failed(current, urlValidation.Error ?? "Invalid update manifest URL.");

        using var request = new HttpRequestMessage(HttpMethod.Get, urlValidation.Uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("SmartMacro-UpdateCheck/1.0");
        request.Options.Set(PinnedAddressesOptionKey, urlValidation.ResolvedAddresses);

        string json;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(RequestTimeout);

            var invoker = _customInvoker ?? SharedClient.Value;
            using var response = await invoker.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return UpdateCheckResult.Failed(
                    current,
                    $"Update feed returned HTTP {(int)response.StatusCode}.");
            }

            if (response.Content.Headers.ContentLength is long declaredLength && declaredLength > MaxManifestBytes)
                return UpdateCheckResult.Failed(current, "Update manifest exceeded the 64 KB size limit.");

            json = await ReadBoundedStringAsync(response.Content, MaxManifestBytes, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            return UpdateCheckResult.Failed(current, ex.Message);
        }
        catch (Exception ex)
        {
            return UpdateCheckResult.Failed(current, $"Could not reach update feed: {ex.Message}");
        }

        UpdateManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<UpdateManifest>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return UpdateCheckResult.Failed(current, "Update manifest contained invalid JSON.");
        }

        if (manifest is null || string.IsNullOrWhiteSpace(manifest.LatestVersion))
            return UpdateCheckResult.Failed(current, "Update manifest did not specify 'latestVersion'.");

        if (!TryParseComparableVersion(manifest.LatestVersion, out var latestVer, out var latestPre)
            || !TryParseComparableVersion(current, out var currentVer, out var currentPre))
        {
            return UpdateCheckResult.Failed(current, "Update manifest version format was not recognized.");
        }

        var comparison = CompareVersions(latestVer, latestPre, currentVer, currentPre);
        var normalizedLatest = latestPre is null
            ? FormatCleanVersion(manifest.LatestVersion, latestVer)
            : $"{latestVer.Major}.{latestVer.Minor}.{latestVer.Build}-{latestPre}";

        if (comparison <= 0)
            return UpdateCheckResult.UpToDate(current, normalizedLatest);

        string? safeDownloadUrl = null;
        if (!string.IsNullOrWhiteSpace(manifest.DownloadUrl))
        {
            if (WebhookSecurity.ValidateUrlSyntax(manifest.DownloadUrl, allowLocalNetwork: false, out var parsedDownload, out _)
                && parsedDownload is not null)
            {
                safeDownloadUrl = parsedDownload.AbsoluteUri;
            }
        }

        safeDownloadUrl ??= OfficialReleasesUrl;

        var safeSha256 = NormalizeSha256(manifest.Sha256);
        var notes = string.IsNullOrWhiteSpace(manifest.ReleaseNotes)
            ? null
            : manifest.ReleaseNotes.Trim();

        var published = string.IsNullOrWhiteSpace(manifest.PublishedAt)
            ? null
            : manifest.PublishedAt.Trim();

        return UpdateCheckResult.Available(
            current,
            normalizedLatest,
            notes,
            safeDownloadUrl,
            safeSha256,
            published);
    }

    /// <summary>Parses a semantic/dotted version string such as "1.2.3" or "v1.2.3-beta1".</summary>
    public static bool TryParseComparableVersion(
        string? rawVersion,
        out Version normalizedVersion,
        out string? preReleaseTag)
    {
        normalizedVersion = new Version(0, 0, 0, 0);
        preReleaseTag = null;

        if (string.IsNullOrWhiteSpace(rawVersion))
            return false;

        var s = rawVersion.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            s = s[1..];

        var plusIndex = s.IndexOf('+', StringComparison.Ordinal);
        if (plusIndex >= 0)
            s = s[..plusIndex];

        var dashIndex = s.IndexOf('-', StringComparison.Ordinal);
        if (dashIndex >= 0)
        {
            preReleaseTag = s[(dashIndex + 1)..].Trim();
            if (preReleaseTag.Length == 0)
                preReleaseTag = null;
            s = s[..dashIndex];
        }

        if (!Version.TryParse(s, out var parsed))
            return false;

        normalizedVersion = new Version(
            parsed.Major,
            parsed.Minor,
            Math.Max(0, parsed.Build),
            Math.Max(0, parsed.Revision));
        return true;
    }

    /// <summary>Returns positive when a &gt; b, zero when equal, negative when a &lt; b.</summary>
    public static int CompareVersions(
        Version a,
        string? aPreRelease,
        Version b,
        string? bPreRelease)
    {
        var cmp = a.CompareTo(b);
        if (cmp != 0)
            return cmp;

        // Stable release (no prerelease tag) outranks a prerelease of the same base version.
        if (string.IsNullOrEmpty(aPreRelease) && !string.IsNullOrEmpty(bPreRelease))
            return 1;
        if (!string.IsNullOrEmpty(aPreRelease) && string.IsNullOrEmpty(bPreRelease))
            return -1;

        return string.Compare(aPreRelease, bPreRelease, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeSha256(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var trimmed = raw.Trim().ToLowerInvariant();
        if (trimmed.Length != 64)
            return null;

        foreach (var c in trimmed)
        {
            if (!Uri.IsHexDigit(c))
                return null;
        }

        return trimmed;
    }

    private static string FormatCleanVersion(string original, Version normalized)
    {
        var trimmed = original.Trim().TrimStart('v', 'V');
        return trimmed.Length > 0 && !trimmed.Contains('-', StringComparison.Ordinal)
            ? trimmed
            : $"{normalized.Major}.{normalized.Minor}.{normalized.Build}";
    }

    private static async Task<string> ReadBoundedStringAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[4096];
        var total = 0;

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            total += read;
            if (total > maxBytes)
                throw new InvalidOperationException($"Update manifest exceeded the {maxBytes / 1024} KB limit.");

            memory.Write(buffer, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(memory.ToArray());
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(4),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = ConnectPinnedAsync,
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = RequestTimeout,
        };
    }

    private static async ValueTask<Stream> ConnectPinnedAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        if (!context.InitialRequestMessage.Options.TryGetValue(PinnedAddressesOptionKey, out var addresses)
            || addresses.Length == 0)
        {
            throw new HttpRequestException("Missing pinned SSRF address policy for update check.");
        }

        foreach (var ip in addresses)
        {
            if (WebhookSecurity.IsDisallowedAddress(ip))
                throw new HttpRequestException($"Blocked update check connection to non-public address {ip}.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };

        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken)
                .ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
