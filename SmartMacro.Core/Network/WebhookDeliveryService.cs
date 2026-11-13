using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartMacro.Core.Network;

/// <summary>Structured result from validating and resolving a webhook target URL.</summary>
public sealed record WebhookValidationResult(
    bool IsValid,
    Uri? Uri,
    IPAddress[] ResolvedAddresses,
    string? Error)
{
    public static WebhookValidationResult Success(Uri uri, IPAddress[] resolvedAddresses)
        => new(true, uri, resolvedAddresses, null);

    public static WebhookValidationResult Failure(string error)
        => new(false, null, [], error);
}

/// <summary>Structured webhook notification payload supporting Discord and Slack incoming webhooks.</summary>
public sealed class WebhookMessagePayload
{
    public string Content { get; init; } = string.Empty;

    public string? MacroName { get; init; }

    public string? Title { get; init; }

    public string? Details { get; init; }

    public int? EmbedColor { get; init; }

    public byte[]? AttachmentBytes { get; init; }

    public string? AttachmentFileName { get; init; }

    public bool IncludeRichEmbed { get; init; } = true;
}

/// <summary>Outcome of an attempted webhook delivery.</summary>
public sealed record WebhookSendResult(
    bool Success,
    bool WasRateLimited,
    bool WasBlocked,
    int? StatusCode,
    string Message)
{
    public static WebhookSendResult Sent(int statusCode)
        => new(true, false, false, statusCode, $"Delivered (HTTP {statusCode}).");

    public static WebhookSendResult RateLimited()
        => new(false, true, false, null, "Skipped — rate limit active (minimum 2 s between webhooks).");

    public static WebhookSendResult Blocked(string reason)
        => new(false, false, true, null, reason);

    public static WebhookSendResult Failed(string reason, int? statusCode = null)
        => new(false, false, false, statusCode, reason);
}

/// <summary>
/// Validates webhook URLs and defends against SSRF (private/loopback/metadata IPs, local hostnames,
/// embedded credentials, and DNS rebinding / redirect hops).
/// </summary>
public static class WebhookSecurity
{
    public const int MaxUrlLength = 500;
    public const int MaxMessageLength = 1900;
    public const int MaxAttachmentBytes = 8 * 1024 * 1024;

    private static readonly string[] BlockedExactHosts =
    [
        "localhost",
        "ip6-localhost",
        "ip6-loopback",
        "metadata",
        "metadata.google.internal",
        "instance-data",
    ];

    private static readonly string[] BlockedHostSuffixes =
    [
        ".localhost",
        ".local",
        ".internal",
        ".localdomain",
        ".home.arpa",
        ".arpa",
    ];

    /// <summary>
    /// Validates HTTPS URL syntax and optional local-network restrictions without performing DNS I/O.
    /// </summary>
    public static bool ValidateUrlSyntax(
        string? rawUrl,
        bool allowLocalNetwork,
        out Uri? uri,
        out string? error)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            error = "Enter a valid https:// webhook URL.";
            return false;
        }

        var trimmed = rawUrl.Trim();
        if (trimmed.Length > MaxUrlLength)
        {
            error = $"Webhook URL must be {MaxUrlLength} characters or fewer.";
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            uri = null;
            error = "Enter a valid https:// webhook URL (or leave it empty).";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            uri = null;
            error = "Webhook URLs cannot contain embedded user credentials.";
            return false;
        }

        var host = NormalizeHost(uri.DnsSafeHost);
        if (string.IsNullOrEmpty(host))
        {
            uri = null;
            error = "Webhook URL must include a valid host name.";
            return false;
        }

        if (!allowLocalNetwork && IsLocalHostOrLiteral(uri))
        {
            uri = null;
            error = "Local or private network webhooks are blocked by default. Enable 'Allow local network webhooks' in Settings to allow LAN targets.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Returns true when the URI explicitly targets localhost, a local hostname, or a non-public IP literal.
    /// </summary>
    public static bool IsLocalHostOrLiteral(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var host = NormalizeHost(uri.DnsSafeHost);
        if (string.IsNullOrEmpty(host))
            return true;

        if (IPAddress.TryParse(host, out var literalIp))
            return IsDisallowedAddress(literalIp);

        return IsLocalHostname(host);
    }

    /// <summary>
    /// Validates URL syntax, resolves the target hostname, and ensures every resolved IP is safe under the active policy.
    /// </summary>
    public static async Task<WebhookValidationResult> ValidateUrlAndResolveAsync(
        string? rawUrl,
        bool allowLocalNetwork,
        Func<string, CancellationToken, Task<IPAddress[]>>? dnsResolver = null,
        CancellationToken cancellationToken = default)
    {
        if (!ValidateUrlSyntax(rawUrl, allowLocalNetwork, out var uri, out var syntaxError) || uri is null)
            return WebhookValidationResult.Failure(syntaxError ?? "Invalid webhook URL.");

        var host = NormalizeHost(uri.DnsSafeHost);
        if (IPAddress.TryParse(host, out var literalIp))
        {
            if (!allowLocalNetwork && IsDisallowedAddress(literalIp))
            {
                return WebhookValidationResult.Failure(
                    $"Blocked webhook to private/local IP {literalIp}. Enable 'Allow local network webhooks' in Settings for LAN targets.");
            }

            return WebhookValidationResult.Success(uri, [literalIp]);
        }

        IPAddress[] resolved;
        try
        {
            var resolver = dnsResolver ?? ResolveHostAddressesAsync;
            resolved = await resolver(uri.IdnHost, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return WebhookValidationResult.Failure($"Webhook DNS lookup failed for '{uri.Host}': {ex.Message}");
        }

        if (resolved is null || resolved.Length == 0)
            return WebhookValidationResult.Failure($"Webhook host '{uri.Host}' did not resolve to any IP address.");

        if (!allowLocalNetwork)
        {
            foreach (var ip in resolved)
            {
                if (IsDisallowedAddress(ip))
                {
                    return WebhookValidationResult.Failure(
                        $"Blocked webhook to private/local IP {ip}. Enable 'Allow local network webhooks' in Settings for LAN targets.");
                }
            }
        }

        return WebhookValidationResult.Success(uri, resolved);
    }

    /// <summary>
    /// Returns true if the address belongs to loopback, unspecified, link-local / cloud metadata,
    /// RFC1918 private, CGNAT, ULA, multicast, reserved, or embedded private ranges.
    /// </summary>
    public static bool IsDisallowedAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            var b0 = bytes[0];
            var b1 = bytes[1];
            var b2 = bytes[2];

            if (b0 == 0) return true;                                  // 0.0.0.0/8 ("this" network)
            if (b0 == 10) return true;                                 // 10.0.0.0/8 (RFC1918)
            if (b0 == 100 && (b1 & 0xC0) == 64) return true;           // 100.64.0.0/10 (CGNAT RFC6598)
            if (b0 == 127) return true;                                // 127.0.0.0/8 (Loopback)
            if (b0 == 169 && b1 == 254) return true;                   // 169.254.0.0/16 (Link-local & metadata)
            if (b0 == 172 && b1 >= 16 && b1 <= 31) return true;        // 172.16.0.0/12 (RFC1918)
            if (b0 == 192 && b1 == 0 && b2 == 0) return true;          // 192.0.0.0/24 (IETF protocol assignments)
            if (b0 == 192 && b1 == 168) return true;                   // 192.168.0.0/16 (RFC1918)
            if (b0 == 198 && (b1 == 18 || b1 == 19)) return true;      // 198.18.0.0/15 (Benchmarking)
            if (b0 >= 224) return true;                                // 224.0.0.0/4 Multicast & 240.0.0.0/4 Reserved/Broadcast
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IsLoopback(address)) return true;
            if (address.Equals(IPAddress.IPv6Any)) return true;
            if (address.IsIPv6LinkLocal) return true;
            if (address.IsIPv6SiteLocal) return true;
            if (address.IsIPv6Multicast) return true;

            var bytes = address.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC) return true; // fc00::/7 Unique Local Addresses

            // Check deprecated IPv4-compatible ::w.x.y.z addresses
            if (IsAllZero(bytes.AsSpan(0, 12)))
                return IsDisallowedAddress(new IPAddress(bytes.AsSpan(12, 4)));

            // Check 6to4 2002::/16 embedded IPv4
            if (bytes[0] == 0x20 && bytes[1] == 0x02)
                return IsDisallowedAddress(new IPAddress(bytes.AsSpan(2, 4)));

            return false;
        }

        return true;
    }

    private static bool IsAllZero(ReadOnlySpan<byte> span)
    {
        foreach (var b in span)
        {
            if (b != 0)
                return false;
        }

        return true;
    }

    private static string NormalizeHost(string? host)
        => string.IsNullOrWhiteSpace(host)
            ? string.Empty
            : host.Trim().Trim('[', ']').TrimEnd('.').ToLowerInvariant();

    private static bool IsLocalHostname(string normalizedHost)
    {
        foreach (var blocked in BlockedExactHosts)
        {
            if (string.Equals(normalizedHost, blocked, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        foreach (var suffix in BlockedHostSuffixes)
        {
            if (normalizedHost.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Single-label hostnames (no dot) resolve on local DNS/mDNS/NetBIOS suffixes only.
        return !normalizedHost.Contains('.', StringComparison.Ordinal);
    }

    private static Task<IPAddress[]> ResolveHostAddressesAsync(string host, CancellationToken cancellationToken)
        => Dns.GetHostAddressesAsync(host, cancellationToken);
}

/// <summary>
/// Hardened HTTPS webhook client using System.Text.Json serialization, SSRF validation,
/// pinned-IP TCP connections, disabled redirects, multipart PNG uploads, and thread-safe rate limiting.
/// </summary>
public sealed class WebhookDeliveryService
{
    public static readonly TimeSpan DefaultMinInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private static readonly HttpRequestOptionsKey<WebhookConnectPolicy> ConnectPolicyOptionKey =
        new("SmartMacro.WebhookConnectPolicy");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static readonly Lazy<HttpClient> SharedClient = new(CreateDefaultHttpClient);

    private readonly object _rateLock = new();
    private readonly HttpMessageInvoker? _customInvoker;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>>? _dnsResolver;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _minInterval;
    private DateTimeOffset _lastSentAt = DateTimeOffset.MinValue;

    public WebhookDeliveryService(
        HttpMessageInvoker? httpInvoker = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? dnsResolver = null,
        TimeProvider? timeProvider = null,
        TimeSpan? minInterval = null)
    {
        _customInvoker = httpInvoker;
        _dnsResolver = dnsResolver;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _minInterval = minInterval is { } custom && custom >= TimeSpan.Zero
            ? custom
            : DefaultMinInterval;
    }

    /// <summary>Shared default delivery service used by macro playback.</summary>
    public static WebhookDeliveryService Shared { get; } = new();

    /// <summary>
    /// Validates the URL, applies rate limiting, serializes the JSON payload (with optional PNG multipart upload),
    /// and posts it over HTTPS.
    /// </summary>
    public async Task<WebhookSendResult> SendAsync(
        string webhookUrl,
        WebhookMessagePayload payload,
        bool allowLocalNetwork = false,
        bool bypassRateLimit = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var validation = await WebhookSecurity.ValidateUrlAndResolveAsync(
            webhookUrl,
            allowLocalNetwork,
            _dnsResolver,
            cancellationToken).ConfigureAwait(false);

        if (!validation.IsValid || validation.Uri is null)
            return WebhookSendResult.Blocked(validation.Error ?? "Webhook URL blocked.");

        if (!bypassRateLimit && !TryAcquireRateLimitSlot())
            return WebhookSendResult.RateLimited();

        var hasAttachment = TryPrepareAttachment(payload, out var attachmentBytes, out var attachmentName);
        var json = SerializePayloadJson(payload, hasAttachment, attachmentName);

        using var request = new HttpRequestMessage(HttpMethod.Post, validation.Uri);
        request.Options.Set(
            ConnectPolicyOptionKey,
            new WebhookConnectPolicy(validation.ResolvedAddresses, allowLocalNetwork));

        if (hasAttachment && attachmentBytes is not null)
        {
            var multipart = new MultipartFormDataContent();
            multipart.Add(new StringContent(json, Encoding.UTF8, "application/json"), "payload_json");

            var fileContent = new ByteArrayContent(attachmentBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            multipart.Add(fileContent, "file", attachmentName);
            request.Content = multipart;
        }
        else
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(DefaultTimeout);

            var invoker = _customInvoker ?? SharedClient.Value;
            using var response = await invoker.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            return response.IsSuccessStatusCode
                ? WebhookSendResult.Sent(code)
                : WebhookSendResult.Failed($"Webhook returned HTTP {code}.", code);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return WebhookSendResult.Failed($"Webhook error: {ex.Message}");
        }
    }

    /// <summary>Serializes a <see cref="WebhookMessagePayload"/> to Discord/Slack compatible JSON.</summary>
    internal static string SerializePayloadJson(
        WebhookMessagePayload payload,
        bool includeAttachmentReference = false,
        string attachmentFileName = "screenshot.png")
    {
        var safeContent = TruncateText(payload.Content, WebhookSecurity.MaxMessageLength);
        List<WebhookEmbedWire>? embeds = null;

        if (payload.IncludeRichEmbed
            && (!string.IsNullOrWhiteSpace(payload.Title)
                || !string.IsNullOrWhiteSpace(payload.MacroName)
                || !string.IsNullOrWhiteSpace(payload.Details)
                || includeAttachmentReference))
        {
            List<WebhookEmbedFieldWire>? fields = null;
            if (!string.IsNullOrWhiteSpace(payload.MacroName))
            {
                fields =
                [
                    new WebhookEmbedFieldWire
                    {
                        Name = "Macro",
                        Value = TruncateText(payload.MacroName.Trim(), 256),
                        Inline = true,
                    },
                ];
            }

            embeds =
            [
                new WebhookEmbedWire
                {
                    Title = string.IsNullOrWhiteSpace(payload.Title) ? null : TruncateText(payload.Title.Trim(), 256),
                    Description = string.IsNullOrWhiteSpace(payload.Details) ? null : TruncateText(payload.Details.Trim(), 1024),
                    Color = payload.EmbedColor,
                    Timestamp = DateTimeOffset.UtcNow.ToString("O"),
                    Fields = fields,
                    Footer = new WebhookEmbedFooterWire { Text = "SmartMacro Studio" },
                    Image = includeAttachmentReference
                        ? new WebhookEmbedImageWire { Url = $"attachment://{attachmentFileName}" }
                        : null,
                },
            ];
        }

        var wire = new WebhookWirePayload
        {
            Content = safeContent,
            Text = safeContent,
            Embeds = embeds,
        };

        return JsonSerializer.Serialize(wire, JsonOptions);
    }

    private bool TryAcquireRateLimitSlot()
    {
        if (_minInterval <= TimeSpan.Zero)
            return true;

        lock (_rateLock)
        {
            var now = _timeProvider.GetUtcNow();
            if (_lastSentAt != DateTimeOffset.MinValue && (now - _lastSentAt) < _minInterval)
                return false;

            _lastSentAt = now;
            return true;
        }
    }

    private static bool TryPrepareAttachment(
        WebhookMessagePayload payload,
        out byte[]? bytes,
        out string fileName)
    {
        bytes = null;
        fileName = SanitizeAttachmentFileName(payload.AttachmentFileName);
        if (payload.AttachmentBytes is not { Length: > 0 } raw)
            return false;

        if (raw.Length > WebhookSecurity.MaxAttachmentBytes)
            return false;

        bytes = raw;
        return true;
    }

    private static string SanitizeAttachmentFileName(string? rawName)
    {
        var name = Path.GetFileName(rawName ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(name))
            return "screenshot.png";

        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_')
                sb.Append(ch);
            else
                sb.Append('_');
        }

        var sanitized = sb.ToString();
        if (!sanitized.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            sanitized += ".png";

        return sanitized.Length > 64 ? "screenshot.png" : sanitized;
    }

    private static string TruncateText(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var trimmed = value.Trim();
        if (trimmed.Length <= maxLength)
            return trimmed;

        return trimmed[..(maxLength - 1)] + "…";
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
            Timeout = DefaultTimeout,
        };
    }

    private static async ValueTask<Stream> ConnectPinnedAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        if (!context.InitialRequestMessage.Options.TryGetValue(ConnectPolicyOptionKey, out var policy)
            || policy.AllowedAddresses.Length == 0)
        {
            throw new HttpRequestException("Missing pinned SSRF address policy for webhook request.");
        }

        if (!policy.AllowLocalNetwork)
        {
            foreach (var ip in policy.AllowedAddresses)
            {
                if (WebhookSecurity.IsDisallowedAddress(ip))
                    throw new HttpRequestException($"Blocked webhook connection to non-public address {ip}.");
            }
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };

        try
        {
            await socket.ConnectAsync(policy.AllowedAddresses, context.DnsEndPoint.Port, cancellationToken)
                .ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private sealed record WebhookConnectPolicy(IPAddress[] AllowedAddresses, bool AllowLocalNetwork);

    private sealed class WebhookWirePayload
    {
        public string Content { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public List<WebhookEmbedWire>? Embeds { get; set; }
    }

    private sealed class WebhookEmbedWire
    {
        public string? Title { get; set; }
        public string? Description { get; set; }
        public int? Color { get; set; }
        public string? Timestamp { get; set; }
        public List<WebhookEmbedFieldWire>? Fields { get; set; }
        public WebhookEmbedFooterWire? Footer { get; set; }
        public WebhookEmbedImageWire? Image { get; set; }
    }

    private sealed class WebhookEmbedFieldWire
    {
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public bool Inline { get; set; }
    }

    private sealed class WebhookEmbedFooterWire
    {
        public string Text { get; set; } = string.Empty;
    }

    private sealed class WebhookEmbedImageWire
    {
        public string Url { get; set; } = string.Empty;
    }
}
