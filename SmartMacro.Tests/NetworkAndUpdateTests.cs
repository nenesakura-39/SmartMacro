using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using SmartMacro.Core.Models;
using SmartMacro.Core.Network;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;

namespace SmartMacro.Tests;

public sealed class NetworkAndUpdateTests : IDisposable
{
    private readonly string _tempDir;

    public NetworkAndUpdateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"SmartMacro_NetTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Ignore temp cleanup races on Windows.
        }
    }

    [Fact]
    public async Task WebhookDeliveryService_EscapesControlCharsQuotesAndUnicodeCleanly()
    {
        var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var invoker = new HttpMessageInvoker(handler);
        var service = new WebhookDeliveryService(invoker, PublicDnsResolver);

        const string nastyMessage = "Boss \"Hydra\"\tPhase\r\n2\\3\u0007 complete! — リク 🎮";
        var payload = new WebhookMessagePayload
        {
            Content = nastyMessage,
            MacroName = "Daily\tBoss \"Run\"",
            Title = "📸 SmartMacro Capture",
        };

        var result = await service.SendAsync("https://discord.com/api/webhooks/123/abc", payload);

        Assert.True(result.Success, result.Message);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal("application/json", sent.MediaType);

        using var doc = JsonDocument.Parse(sent.BodyText!);
        Assert.Equal(nastyMessage, doc.RootElement.GetProperty("content").GetString());
        Assert.Equal(nastyMessage, doc.RootElement.GetProperty("text").GetString());
        var embed = doc.RootElement.GetProperty("embeds")[0];
        Assert.Equal("Daily\tBoss \"Run\"", embed.GetProperty("fields")[0].GetProperty("value").GetString());
    }

    [Theory]
    [InlineData("http://discord.com/api/webhooks/123/abc")]
    [InlineData("ftp://discord.com/api/webhooks/123/abc")]
    [InlineData("https://user:pass@discord.com/api/webhooks/123/abc")]
    [InlineData("https://localhost/webhook")]
    [InlineData("https://sub.localhost/webhook")]
    [InlineData("https://homeassistant.local/api/webhook/test")]
    [InlineData("https://metadata.google.internal/computeMetadata/v1/")]
    [InlineData("https://intranet/webhook")]
    [InlineData("https://127.0.0.1/webhook")]
    [InlineData("https://127.255.0.1/webhook")]
    [InlineData("https://0.0.0.0/webhook")]
    [InlineData("https://10.0.0.8/webhook")]
    [InlineData("https://172.16.4.2/webhook")]
    [InlineData("https://172.31.255.10/webhook")]
    [InlineData("https://192.168.1.42/webhook")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://100.64.12.5/webhook")]
    [InlineData("https://224.0.0.1/webhook")]
    [InlineData("https://[::1]/webhook")]
    [InlineData("https://[::]/webhook")]
    [InlineData("https://[fc00::1]/webhook")]
    [InlineData("https://[fd12:3456:789a::1]/webhook")]
    [InlineData("https://[fe80::1]/webhook")]
    [InlineData("https://[::ffff:127.0.0.1]/webhook")]
    [InlineData("https://[2002:7f00:0001::]/webhook")]
    public async Task WebhookSecurity_BlocksUnsafeAndPrivateTargetsByDefault(string url)
    {
        var result = await WebhookSecurity.ValidateUrlAndResolveAsync(
            url,
            allowLocalNetwork: false,
            PublicDnsResolver);

        Assert.False(result.IsValid);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task WebhookSecurity_BlocksDnsReboundPrivateIpUnlessLocalOptInEnabled()
    {
        Task<IPAddress[]> MixedResolver(string _, CancellationToken __) =>
            Task.FromResult(new[]
            {
                IPAddress.Parse("93.184.216.34"),
                IPAddress.Parse("10.20.30.40"),
            });

        var blocked = await WebhookSecurity.ValidateUrlAndResolveAsync(
            "https://rebind.example.com/hook",
            allowLocalNetwork: false,
            MixedResolver);

        Assert.False(blocked.IsValid);
        Assert.Contains("10.20.30.40", blocked.Error, StringComparison.Ordinal);

        var allowed = await WebhookSecurity.ValidateUrlAndResolveAsync(
            "https://rebind.example.com/hook",
            allowLocalNetwork: true,
            MixedResolver);

        Assert.True(allowed.IsValid);
        Assert.Equal(2, allowed.ResolvedAddresses.Length);
    }

    [Theory]
    [InlineData("https://localhost:8443/ntfy")]
    [InlineData("https://192.168.1.50:8123/api/webhook/abc")]
    [InlineData("https://homeassistant.local/api/webhook/abc")]
    public async Task WebhookSecurity_AllowsLocalTargetsWhenOptedIn(string url)
    {
        Task<IPAddress[]> LanResolver(string _, CancellationToken __) =>
            Task.FromResult(new[] { IPAddress.Parse("192.168.1.50") });

        var result = await WebhookSecurity.ValidateUrlAndResolveAsync(
            url,
            allowLocalNetwork: true,
            LanResolver);

        Assert.True(result.IsValid, result.Error);
    }

    [Fact]
    public async Task WebhookDeliveryService_EnforcesThreadSafeRateLimitUnderConcurrency()
    {
        var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var invoker = new HttpMessageInvoker(handler);
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        var service = new WebhookDeliveryService(invoker, PublicDnsResolver, clock);

        var tasks = Enumerable.Range(0, 12)
            .Select(i => service.SendAsync(
                "https://discord.com/api/webhooks/123/abc",
                new WebhookMessagePayload { Content = $"Burst #{i}" }))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r.Success));
        Assert.Equal(11, results.Count(r => r.WasRateLimited));
        Assert.Single(handler.Requests);

        clock.Advance(TimeSpan.FromSeconds(1.5));
        var tooSoon = await service.SendAsync(
            "https://discord.com/api/webhooks/123/abc",
            new WebhookMessagePayload { Content = "Still inside 2s" });
        Assert.True(tooSoon.WasRateLimited);

        clock.Advance(TimeSpan.FromSeconds(0.6));
        var allowed = await service.SendAsync(
            "https://discord.com/api/webhooks/123/abc",
            new WebhookMessagePayload { Content = "After 2s" });
        Assert.True(allowed.Success);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task WebhookDeliveryService_SendsMultipartFormDataWithPngAttachment()
    {
        var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var invoker = new HttpMessageInvoker(handler);
        var service = new WebhookDeliveryService(invoker, PublicDnsResolver);

        var img = BgraImage.Create(4, 4);
        img.FillRectangle(new ScreenRect(0, 0, 4, 4), new RgbColor(34, 211, 238));
        var pngBytes = PngCodec.Encode(img);

        var payload = new WebhookMessagePayload
        {
            Content = "Captured rare drop!",
            MacroName = "LootWatcher",
            Title = "📸 SmartMacro Capture",
            EmbedColor = 0x38BDF8,
            AttachmentBytes = pngBytes,
            AttachmentFileName = "../weird name/boss_loot.png",
        };

        var result = await service.SendAsync("https://discord.com/api/webhooks/123/abc", payload);

        Assert.True(result.Success, result.Message);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal("multipart/form-data", sent.MediaType);

        Assert.Equal(2, sent.MultipartParts.Count);
        var jsonPart = sent.MultipartParts.Single(p => p.Name == "payload_json");
        var filePart = sent.MultipartParts.Single(p => p.Name == "file");

        Assert.Equal("boss_loot.png", filePart.FileName);
        Assert.Equal("image/png", filePart.ContentType);
        Assert.Equal(pngBytes, filePart.Bytes);

        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(jsonPart.Bytes));
        Assert.Equal(
            "attachment://boss_loot.png",
            doc.RootElement.GetProperty("embeds")[0].GetProperty("image").GetProperty("url").GetString());
    }

    [Fact]
    public async Task PlaybackEngine_ScreenshotStep_UploadsPngAndSendsFailureAlertOnTimeout()
    {
        var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var invoker = new HttpMessageInvoker(handler);
        var webhookService = new WebhookDeliveryService(invoker, PublicDnsResolver);

        var screen = BgraImage.Create(48, 48);
        screen.FillRectangle(new ScreenRect(0, 0, 48, 48), new RgbColor(20, 30, 50));
        var capture = new FakeCapture(screen);
        var logsDir = Path.Combine(_tempDir, "Logs");
        var shotDir = Path.Combine(_tempDir, "Shots");

        var engine = new PlaybackEngine(new FakeInput(), new FakeClock(), capture: capture)
        {
            FailureSnapshotDirectory = logsDir,
            FailureWebhookUrl = "https://hooks.slack.com/services/T00/B00/XXX",
            WebhookClient = webhookService,
        };

        var successMacro = new Macro
        {
            Name = "Capture Boss",
            Variables = new List<MacroVariableDefinition>
            {
                new() { Name = "boss", Type = MacroVariableType.Text, DefaultValue = "Malenia" },
            },
            Steps =
            {
                new ScreenshotStep
                {
                    FolderPath = shotDir,
                    FileNamePrefix = "Boss",
                    NotificationMessage = "Cleared {boss}!",
                    WebhookUrl = "https://discord.com/api/webhooks/1/secret",
                    AttachScreenshot = true,
                },
            },
        };

        var okResult = await engine.RunAsync(successMacro);
        Assert.Equal(PlaybackOutcome.Completed, okResult.Outcome);
        Assert.Single(handler.Requests);
        Assert.Equal("multipart/form-data", handler.Requests[0].MediaType);
        Assert.Equal(new[] { (byte)0x89, (byte)'P', (byte)'N', (byte)'G' },
            handler.Requests[0].MultipartParts.Single(p => p.Name == "file").Bytes[..4]);

        var failingMacro = new Macro
        {
            Name = "Wait For Missing Sprite",
            Steps =
            {
                new FindColorStep
                {
                    Color = "#FF00FF",
                    Tolerance = 2,
                    TimeoutMs = 0,
                    WhenNotFound = TextNotFoundBehavior.StopMacro,
                },
            },
        };

        var failResult = await engine.RunAsync(failingMacro);
        Assert.Equal(PlaybackOutcome.Failed, failResult.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(failResult.FailureSnapshotPath));
        Assert.True(File.Exists(failResult.FailureSnapshotPath));

        Assert.Equal(2, handler.Requests.Count);
        var alertRequest = handler.Requests[1];
        Assert.Equal("multipart/form-data", alertRequest.MediaType);
        var jsonBytes = alertRequest.MultipartParts.Single(p => p.Name == "payload_json").Bytes;
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(jsonBytes));
        Assert.Contains("Wait For Missing Sprite", doc.RootElement.GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Equal(0xEF4444, doc.RootElement.GetProperty("embeds")[0].GetProperty("color").GetInt32());
        Assert.Equal("last-failure-snapshot.png",
            alertRequest.MultipartParts.Single(p => p.Name == "file").FileName);
    }

    [Fact]
    public void ScreenshotStep_And_AppSettings_PersistAndValidateNewFields()
    {
        var macro = new Macro
        {
            Name = "Attachment Persistence",
            Steps =
            {
                new ScreenshotStep
                {
                    AttachScreenshot = true,
                    WebhookUrl = "https://discord.com/api/webhooks/99/token",
                    NotificationMessage = "Done!",
                },
            },
        };

        Assert.Equal(17, MacroDocument.RequiredSchemaVersion(macro));
        var json = MacroFileFormat.Serialize(macro);
        Assert.True(MacroFileFormat.TryParse(json, out var loaded, out var parseError), parseError);
        var loadedShot = Assert.IsType<ScreenshotStep>(Assert.Single(loaded!.Steps));
        Assert.True(loadedShot.AttachScreenshot);

        var localWarningIssues = MacroValidator.ValidateStep(new ScreenshotStep
        {
            WebhookUrl = "https://localhost:8443/hook",
        });
        Assert.DoesNotContain(localWarningIssues, i => i.Severity == IssueSeverity.Error);
        Assert.Contains(localWarningIssues, i => i.Severity == IssueSeverity.Warning && i.Property == nameof(ScreenshotStep.WebhookUrl));

        var badUrlIssues = MacroValidator.ValidateStep(new ScreenshotStep
        {
            WebhookUrl = "https://user:pass@discord.com/api/webhooks/1/2",
        });
        Assert.Contains(badUrlIssues, i => i.Severity == IssueSeverity.Error && i.Property == nameof(ScreenshotStep.WebhookUrl));

        var settings = new AppSettings
        {
            AllowLocalWebhooks = true,
            FailureWebhookUrl = "  https://localhost:8443/fail  ",
            UpdateManifestUrl = "https://127.0.0.1/manifest.json",
        };
        settings.Sanitize();

        Assert.True(settings.AllowLocalWebhooks);
        Assert.Equal("https://localhost:8443/fail", settings.FailureWebhookUrl);
        Assert.Null(settings.UpdateManifestUrl);

        settings.FailureWebhookUrl = "http://discord.com/api/webhooks/1/2";
        settings.Sanitize();
        Assert.Null(settings.FailureWebhookUrl);
    }

    [Fact]
    public async Task UpdateCheckService_ParsesManifestDetectsNewerVersionAndSanitizesDownloadUrl()
    {
        const string sha = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
        var manifestJson = JsonSerializer.Serialize(new
        {
            latestVersion = "v1.2.0",
            releaseNotes = "Hardened webhooks & faster vision.",
            downloadUrl = "https://github.com/nenesakura-39/SmartMacro/releases/tag/v1.2.0",
            sha256 = sha,
            publishedAt = "2026-11-15",
        });

        var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(manifestJson, Encoding.UTF8, "application/json"),
        });
        using var invoker = new HttpMessageInvoker(handler);
        var checker = new UpdateCheckService(invoker, PublicDnsResolver);

        var available = await checker.CheckForUpdatesAsync("1.0.0", "https://updates.example.com/manifest.json");
        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, available.Outcome);
        Assert.Equal("1.2.0", available.LatestVersion);
        Assert.Equal(sha.ToLowerInvariant(), available.Sha256);
        Assert.Equal("https://github.com/nenesakura-39/SmartMacro/releases/tag/v1.2.0", available.DownloadUrl);

        var sameNormalized = await checker.CheckForUpdatesAsync("1.2", "https://updates.example.com/manifest.json");
        Assert.Equal(UpdateCheckOutcome.UpToDate, sameNormalized.Outcome);

        var preReleaseCurrent = await checker.CheckForUpdatesAsync("1.2.0-beta.1", "https://updates.example.com/manifest.json");
        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, preReleaseCurrent.Outcome);
    }

    [Fact]
    public async Task UpdateCheckService_SanitizesUnsafeDownloadUrlAndRejectsPrivateManifestUrl()
    {
        var manifestWithBadDownload = JsonSerializer.Serialize(new
        {
            latestVersion = "2.0.0",
            downloadUrl = "http://127.0.0.1/evil.exe",
            sha256 = "not-a-valid-hash",
        });

        var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(manifestWithBadDownload, Encoding.UTF8, "application/json"),
        });
        using var invoker = new HttpMessageInvoker(handler);
        var checker = new UpdateCheckService(invoker, PublicDnsResolver);

        var result = await checker.CheckForUpdatesAsync("1.0.0", "https://updates.example.com/manifest.json");
        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
        Assert.Equal(UpdateCheckService.OfficialReleasesUrl, result.DownloadUrl);
        Assert.Null(result.Sha256);

        var blockedLocal = await checker.CheckForUpdatesAsync("1.0.0", "https://127.0.0.1/manifest.json");
        Assert.Equal(UpdateCheckOutcome.CheckFailed, blockedLocal.Outcome);

        var oversizedHandler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', UpdateCheckService.MaxManifestBytes + 128), Encoding.UTF8, "application/json"),
        });
        using var oversizedInvoker = new HttpMessageInvoker(oversizedHandler);
        var oversizedChecker = new UpdateCheckService(oversizedInvoker, PublicDnsResolver);
        var tooLarge = await oversizedChecker.CheckForUpdatesAsync("1.0.0", "https://updates.example.com/manifest.json");
        Assert.Equal(UpdateCheckOutcome.CheckFailed, tooLarge.Outcome);
    }

    private static Task<IPAddress[]> PublicDnsResolver(string _, CancellationToken __)
        => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") });

    private sealed class ManualTimeProvider(DateTimeOffset startUtc) : TimeProvider
    {
        private long _utcTicks = startUtc.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

        public void Advance(TimeSpan delta) => Interlocked.Add(ref _utcTicks, delta.Ticks);
    }

    private sealed record RecordedPart(string Name, string? FileName, string? ContentType, byte[] Bytes);

    private sealed record RecordedHttpRequest(
        Uri? Uri,
        string? MediaType,
        string? BodyText,
        List<RecordedPart> MultipartParts);

    private sealed class RecordingHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public ConcurrentQueue<RecordedHttpRequest> Queue { get; } = new();

        public List<RecordedHttpRequest> Requests => Queue.ToList();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var mediaType = request.Content?.Headers.ContentType?.MediaType;
            string? bodyText = null;
            var parts = new List<RecordedPart>();

            if (request.Content is MultipartFormDataContent multipart)
            {
                foreach (var part in multipart)
                {
                    var disp = part.Headers.ContentDisposition;
                    var name = disp?.Name?.Trim('"') ?? string.Empty;
                    var fileName = disp?.FileName?.Trim('"');
                    var partType = part.Headers.ContentType?.MediaType;
                    var bytes = await part.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                    parts.Add(new RecordedPart(name, fileName, partType, bytes));
                }
            }
            else if (request.Content is not null)
            {
                bodyText = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            Queue.Enqueue(new RecordedHttpRequest(request.RequestUri, mediaType, bodyText, parts));
            return responder(request);
        }
    }
}
