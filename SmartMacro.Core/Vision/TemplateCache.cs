namespace SmartMacro.Core.Vision;

/// <summary>
/// Vision Engine Turbo: a small, thread-safe, bounded (LRU) cache of decoded template pictures.
///
/// Macros store their pictures as text (Find image templates = <see cref="TemplateCodec"/> data, Smart-Aim click
/// thumbnails = Base64 PNG via <see cref="PngCodec"/>). Decoding that text (Base64 → zlib / PNG → BGRA) costs real time
/// and memory, and it used to happen again on every run of a step, every repeat of a macro and every time the editor
/// re-validated. With this cache the same text is decoded once and the same <see cref="BgraImage"/> instance is handed
/// out afterwards.
///
/// Rules:
/// - The key is (kind, width, height, text length, text hash); a hit is only used when the stored text is really the
///   same (reference check first, then an ordinal compare), so a hash collision can never return the wrong picture.
/// - Pictures that can't be decoded are cached too (with their error), so a damaged macro isn't decoded over and over.
/// - At most <see cref="Capacity"/> entries and <see cref="MaxBytes"/> bytes of pixels; the least recently used
///   entries go first. The newest entry is always kept, even when it alone is bigger than the byte limit.
/// - Returned pictures are SHARED: treat them as read-only (the matchers only read them). Clone before drawing on one.
/// </summary>
public sealed class TemplateCache
{
    /// <summary>How many decoded pictures the shared cache keeps.</summary>
    public const int DefaultCapacity = 128;

    /// <summary>How many bytes of pixels the shared cache keeps at most (256 MB).</summary>
    public const long DefaultMaxBytes = 256L * 1024 * 1024;

    /// <summary>The cache used by playback, the validator and the editor tools.</summary>
    public static TemplateCache Shared { get; } = new();

    private enum Format : byte
    {
        Png = 1,
        Template = 2,
    }

    private readonly record struct Key(Format Format, int Width, int Height, int Length, int Hash);

    private sealed class Entry
    {
        public Entry(Key key, string source, BgraImage? image, string? error)
        {
            Key = key;
            Source = source;
            Image = image;
            Error = error;
            Bytes = image?.Pixels.LongLength ?? 0;
        }

        public Key Key { get; }
        public string Source { get; }
        public BgraImage? Image { get; }
        public string? Error { get; }
        public long Bytes { get; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<Key, LinkedListNode<Entry>> _map = new();
    private readonly LinkedList<Entry> _lru = new(); // most recently used first
    private long _bytes;
    private long _hits;
    private long _decodes;

    public TemplateCache(int capacity = DefaultCapacity, long maxBytes = DefaultMaxBytes)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity), "The cache must hold at least one picture.");
        if (maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes), "The byte limit must be positive.");
        Capacity = capacity;
        MaxBytes = maxBytes;
    }

    public int Capacity { get; }
    public long MaxBytes { get; }

    /// <summary>How many pictures (and remembered errors) are cached right now.</summary>
    public int Count
    {
        get { lock (_gate) return _map.Count; }
    }

    /// <summary>Bytes of pixels held right now.</summary>
    public long CachedBytes
    {
        get { lock (_gate) return _bytes; }
    }

    /// <summary>Lookups answered from the cache (no decoding).</summary>
    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>Lookups that had to decode the text (first time, or after the entry was pushed out).</summary>
    public long Decodes => Interlocked.Read(ref _decodes);

    // ---------------- Shared-cache shortcuts ----------------

    /// <summary>A Base64 PNG (Smart-Aim click thumbnail) as pixels from the shared cache, or null when it can't be used.</summary>
    public static BgraImage? GetOrDecode(string? base64) => Shared.TryGetPng(base64, out var image, out _) ? image : null;

    /// <summary>A Base64 PNG from the shared cache; false with a plain-English reason when it can't be used.</summary>
    public static bool TryGetOrDecode(string? base64, out BgraImage? image, out string? error) =>
        Shared.TryGetPng(base64, out image, out error);

    /// <summary>A stored Find image template (<see cref="TemplateCodec"/> text) from the shared cache, or null when damaged.</summary>
    public static BgraImage? GetOrDecode(string? data, int width, int height) =>
        Shared.TryGetTemplate(data, width, height, out var image, out _) ? image : null;

    /// <summary>A stored Find image template from the shared cache; false with a plain-English reason when it can't be used.</summary>
    public static bool TryGetOrDecode(string? data, int width, int height, out BgraImage? image, out string? error) =>
        Shared.TryGetTemplate(data, width, height, out image, out error);

    // ---------------- Lookups ----------------

    /// <summary>Base64 PNG text → pixels, or null when it can't be used (see <see cref="TryGetPng"/> for the reason).</summary>
    public BgraImage? GetPng(string? base64) => TryGetPng(base64, out var image, out _) ? image : null;

    /// <summary>Stored template text → pixels, or null when it can't be used (see <see cref="TryGetTemplate"/> for the reason).</summary>
    public BgraImage? GetTemplate(string? data, int width, int height) =>
        TryGetTemplate(data, width, height, out var image, out _) ? image : null;

    /// <summary>Base64 PNG text → pixels (decoded at most once while it stays cached).</summary>
    public bool TryGetPng(string? base64, out BgraImage? image, out string? error)
    {
        // Nothing to decode: answer directly (cheap) instead of filling the cache with empty keys.
        if (string.IsNullOrWhiteSpace(base64)) return PngCodec.TryDecodeBase64(base64, out image, out error);

        var key = new Key(Format.Png, 0, 0, base64.Length, base64.GetHashCode());
        return Lookup(key, base64, static (text, _, _) =>
        {
            var ok = PngCodec.TryDecodeBase64(text, out var decoded, out var reason);
            return (ok ? decoded : null, ok ? null : reason ?? "The picture can't be read.");
        }, 0, 0, out image, out error);
    }

    /// <summary><see cref="TemplateCodec"/> text of the given size → pixels (decoded at most once while it stays cached).</summary>
    public bool TryGetTemplate(string? data, int width, int height, out BgraImage? image, out string? error)
    {
        if (string.IsNullOrEmpty(data)) return TemplateCodec.TryDecode(data, width, height, out image, out error);

        var key = new Key(Format.Template, width, height, data.Length, data.GetHashCode());
        return Lookup(key, data, static (text, w, h) =>
        {
            var ok = TemplateCodec.TryDecode(text, w, h, out var decoded, out var reason);
            return (ok ? decoded : null, ok ? null : reason ?? "The stored image is damaged.");
        }, width, height, out image, out error);
    }

    /// <summary>Forgets every cached picture (tests; or to free memory).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _lru.Clear();
            _bytes = 0;
        }
    }

    private bool Lookup(Key key, string source, Func<string, int, int, (BgraImage? Image, string? Error)> decode, int width, int height,
        out BgraImage? image, out string? error)
    {
        lock (_gate)
        {
            if (TryHit(key, source, out var cached))
            {
                Interlocked.Increment(ref _hits);
                image = cached.Image;
                error = cached.Error;
                return image is not null;
            }
        }

        // Decode outside the lock: other threads keep getting their (different) pictures meanwhile.
        Interlocked.Increment(ref _decodes);
        (BgraImage? Image, string? Error) decoded;
        try
        {
            decoded = decode(source, width, height);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            decoded = (null, $"The picture can't be read ({ex.Message}).");
        }

        lock (_gate)
        {
            // Another thread may have decoded the same text meanwhile: everyone gets that one instance.
            if (TryHit(key, source, out var raced))
            {
                image = raced.Image;
                error = raced.Error;
                return image is not null;
            }

            var entry = new Entry(key, source, decoded.Image, decoded.Error);
            if (_map.TryGetValue(key, out var old)) Remove(old); // same key, different text (hash collision): newest wins
            _map[key] = _lru.AddFirst(entry);
            _bytes += entry.Bytes;
            Trim();
        }

        image = decoded.Image;
        error = decoded.Error;
        return image is not null;
    }

    // Must hold _gate.
    private bool TryHit(Key key, string source, out Entry entry)
    {
        entry = null!;
        if (!_map.TryGetValue(key, out var node)) return false;
        var candidate = node.Value;
        if (!ReferenceEquals(candidate.Source, source) && !string.Equals(candidate.Source, source, StringComparison.Ordinal)) return false;

        if (!ReferenceEquals(_lru.First, node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
        }

        entry = candidate;
        return true;
    }

    // Must hold _gate.
    private void Trim()
    {
        while (_lru.Count > 1 && (_lru.Count > Capacity || _bytes > MaxBytes))
            Remove(_lru.Last!);
    }

    // Must hold _gate.
    private void Remove(LinkedListNode<Entry> node)
    {
        _lru.Remove(node);
        _map.Remove(node.Value.Key);
        _bytes -= node.Value.Bytes;
    }
}
