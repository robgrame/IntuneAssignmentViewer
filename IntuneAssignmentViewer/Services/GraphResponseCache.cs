using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using IntuneAssignmentViewer.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace IntuneAssignmentViewer.Services;

/// <summary>
/// Singleton, tenant-wide cache and rate limiter for raw Microsoft Graph beta calls.
/// </summary>
/// <remarks>
/// Shared by all user circuits so the second user to hit the app within the TTL
/// gets results from memory in milliseconds. Includes a concurrency limiter to
/// avoid hammering Graph and triggering HTTP 429 throttling.
/// </remarks>
public sealed class GraphResponseCache : IDisposable
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TokenCredential _credential;
    private readonly MemoryCache _cache;
    private readonly ILogger<GraphResponseCache> _logger;
    private readonly PerformanceOptions _perf;
    private readonly CacheOptions _cacheOpts;

    private readonly SemaphoreSlim _concurrencyLimiter;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _tokenExpiresOn;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _urlLocks = new();

    // Index of cached URLs that point to /assignments collections, so we can
    // selectively invalidate them on user-requested Refresh.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _assignmentKeys = new();

    public GraphResponseCache(
        IHttpClientFactory httpClientFactory,
        TokenCredential credential,
        IOptions<PerformanceOptions> perf,
        IOptions<CacheOptions> cacheOpts,
        ILogger<GraphResponseCache> logger)
    {
        _httpClientFactory = httpClientFactory;
        _credential = credential;
        _perf = perf.Value;
        _cacheOpts = cacheOpts.Value;
        _logger = logger;
        _concurrencyLimiter = new SemaphoreSlim(_perf.MaxConcurrentGraphRequests, _perf.MaxConcurrentGraphRequests);

        // Dedicated MemoryCache so we can enforce a hard size cap without affecting
        // other consumers of the global IMemoryCache (e.g. group name cache).
        // SizeLimit is measured in arbitrary units; we use bytes (approximate JSON
        // payload size). 64 MB is plenty for very large tenants and safe on a 1 GB
        // on-prem instance.
        var sizeLimitBytes = Math.Max(8L * 1024 * 1024, _cacheOpts.MaxSizeMegabytes * 1024L * 1024L);
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = sizeLimitBytes });
    }

    /// <summary>
    /// GET a Graph URL with memory caching and concurrency limiting.
    /// Returns null on error (404, 403, network failure, etc.).
    /// </summary>
    public async Task<JsonElement?> GetAsync(string url, TimeSpan ttl, CancellationToken ct = default)
    {
        // Fast path: cache hit
        if (_cache.TryGetValue<JsonElement?>(url, out var cached))
        {
            return cached;
        }

        // Coalesce concurrent identical requests via per-URL lock
        var urlLock = _urlLocks.GetOrAdd(url, _ => new SemaphoreSlim(1, 1));
        await urlLock.WaitAsync(ct);
        try
        {
            // Re-check after acquiring the lock
            if (_cache.TryGetValue<JsonElement?>(url, out cached))
            {
                return cached;
            }

            var fetched = await FetchWithRetryAsync(url, ct);

            var effectiveTtl = fetched == null
                ? TimeSpan.FromMinutes(_cacheOpts.NegativeTtlMinutes)
                : ttl;
            SetCacheEntry(url, fetched, effectiveTtl);
            return fetched;
        }
        finally
        {
            urlLock.Release();
            if (urlLock.CurrentCount == 1)
                _urlLocks.TryRemove(url, out _);
        }
    }

    /// <summary>
    /// Stores a JsonElement in the cache with proper Size (bytes) accounting
    /// and tracks assignment-URL keys for targeted invalidation.
    /// </summary>
    private void SetCacheEntry(string url, JsonElement? value, TimeSpan ttl)
    {
        // Approximate size: serialize back to UTF-8 bytes. Null/empty entries
        // still cost 1 byte to prevent cache poisoning via many "null" entries.
        long size = 1;
        if (value.HasValue)
        {
            try { size = Math.Max(1, value.Value.GetRawText().Length); } catch { /* keep default */ }
        }

        _cache.Set(url, value, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ttl,
            Size = size
        });

        if (url.Contains("/assignments", StringComparison.OrdinalIgnoreCase))
            _assignmentKeys.TryAdd(url, 0);
    }

    /// <summary>
    /// Pre-fetch multiple Graph GET URLs in batches of up to 20 using POST /$batch,
    /// then populate the cache. Already-cached URLs are skipped. Returns once all
    /// batches have completed.
    /// </summary>
    public async Task PreFetchBatchAsync(IEnumerable<string> urls, TimeSpan ttl, CancellationToken ct = default)
    {
        if (!_perf.EnableBatchRequests) return;

        var toFetch = urls
            .Where(u => !string.IsNullOrWhiteSpace(u) && !_cache.TryGetValue(u, out _))
            .Distinct()
            .ToList();
        if (toFetch.Count == 0) return;

        var batchSize = Math.Clamp(_perf.BatchSize, 1, 20); // Graph hard limit = 20
        var chunks = toFetch.Chunk(batchSize).ToList();
        for (int i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            await ExecuteBatchAsync(chunks[i], ttl, ct);
            if (i < chunks.Count - 1 && _perf.BatchSpacingMs > 0)
            {
                try { await Task.Delay(_perf.BatchSpacingMs, ct); }
                catch (OperationCanceledException) { throw; }
            }
        }
    }

    private async Task ExecuteBatchAsync(string[] urls, TimeSpan ttl, CancellationToken ct)
    {
        // Build $batch request body
        var requests = urls.Select((u, i) => new
        {
            id = (i + 1).ToString(),
            method = "GET",
            // Graph $batch expects URL relative to /v1.0 or /beta.
            // Our cache URLs are absolute - we need to strip the host + version prefix.
            url = StripGraphBase(u)
        }).ToArray();

        // Use beta $batch because most of our endpoints are beta
        var batchUrl = "https://graph.microsoft.com/beta/$batch";
        var payload = JsonSerializer.Serialize(new { requests });

        await _concurrencyLimiter.WaitAsync(ct);
        HttpResponseMessage? response = null;
        try
        {
            var client = await GetClientAsync(ct);
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            response = await client.PostAsync(batchUrl, content, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("$batch failed: {Status}", response.StatusCode);
                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("responses", out var arr)) return;

            foreach (var r in arr.EnumerateArray())
            {
                if (!r.TryGetProperty("id", out var idEl)) continue;
                if (!int.TryParse(idEl.GetString(), out var idx) || idx < 1 || idx > urls.Length) continue;
                var originalUrl = urls[idx - 1];

                var status = r.TryGetProperty("status", out var st) ? st.GetInt32() : 0;

                // Never cache transient/throttling failures. The next access will retry.
                if (status == 429 || status == 503 || status >= 500)
                {
                    _logger.LogDebug("Skipping cache for batch sub-status {Status}: {Url}", status, originalUrl);
                    continue;
                }

                JsonElement? body = null;
                if (status == 200 && r.TryGetProperty("body", out var b))
                {
                    body = b.Clone();
                }
                // status 200 -> normal TTL; 403/404 -> negative TTL to avoid hammering
                var ttlToUse = body == null ? TimeSpan.FromMinutes(_cacheOpts.NegativeTtlMinutes) : ttl;
                SetCacheEntry(originalUrl, body, ttlToUse);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "$batch error");
        }
        finally
        {
            response?.Dispose();
            _concurrencyLimiter.Release();
        }
    }

    private static string StripGraphBase(string absoluteUrl)
    {
        // Convert https://graph.microsoft.com/beta/... to /...
        const string prefixBeta = "https://graph.microsoft.com/beta";
        const string prefixV1 = "https://graph.microsoft.com/v1.0";
        if (absoluteUrl.StartsWith(prefixBeta, StringComparison.OrdinalIgnoreCase))
            return absoluteUrl[prefixBeta.Length..];
        if (absoluteUrl.StartsWith(prefixV1, StringComparison.OrdinalIgnoreCase))
            return absoluteUrl[prefixV1.Length..];
        return absoluteUrl;
    }

    /// <summary>POST to Graph (e.g. $batch). Not cached.</summary>
    public async Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct = default)
    {
        await _concurrencyLimiter.WaitAsync(ct);
        try
        {
            var client = await GetClientAsync(ct);
            return await client.PostAsync(url, content, ct);
        }
        finally
        {
            _concurrencyLimiter.Release();
        }
    }

    private async Task<JsonElement?> FetchWithRetryAsync(string url, CancellationToken ct)
    {
        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            TimeSpan? retryDelay = null;

            await _concurrencyLimiter.WaitAsync(ct);
            HttpResponseMessage? response = null;
            try
            {
                var client = await GetClientAsync(ct);
                response = await client.GetAsync(url, ct);

                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
                    (int)response.StatusCode == 503)
                {
                    if (attempt < maxAttempts)
                    {
                        retryDelay = response.Headers.RetryAfter?.Delta
                            ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                        _logger.LogInformation("Throttled ({Status}), retry in {Delay}s: {Url}",
                            (int)response.StatusCode, retryDelay.Value.TotalSeconds, url);
                    }
                }
                else if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound ||
                        response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        _logger.LogDebug("Graph {Status} (skipped): {Url}", response.StatusCode, url);
                    }
                    else
                    {
                        var body = await response.Content.ReadAsStringAsync(ct);
                        _logger.LogWarning("Graph request failed: {Status} {Url} - {Body}",
                            response.StatusCode, url, body.Length > 500 ? body[..500] : body);
                    }
                    return null;
                }
                else
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(ct);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                    return doc.RootElement.Clone();
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error fetching: {Url}", url);
                return null;
            }
            finally
            {
                response?.Dispose();
                _concurrencyLimiter.Release();
            }

            if (retryDelay.HasValue)
            {
                await Task.Delay(retryDelay.Value, ct);
                continue;
            }
            break;
        }
        return null;
    }

    /// <summary>
    /// Bust ONLY the cached /assignments responses. Catalog lists and group-name
    /// entries are kept (they change rarely). Called when a user clicks Refresh.
    /// </summary>
    public void InvalidateAssignments()
    {
        int removed = 0;
        foreach (var key in _assignmentKeys.Keys.ToArray())
        {
            _cache.Remove(key);
            _assignmentKeys.TryRemove(key, out _);
            removed++;
        }
        _logger.LogInformation("Invalidated {Count} cached /assignments entries", removed);
    }

    /// <summary>Nuke EVERYTHING in this cache. Use sparingly (admin / tests).</summary>
    public void InvalidateAll()
    {
        _cache.Compact(1.0);
        _assignmentKeys.Clear();
    }

    public async Task<HttpClient> GetClientAsync(CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient("GraphBeta");
        var token = await GetTokenAsync(ct);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        if (_cachedToken != null && DateTimeOffset.UtcNow < _tokenExpiresOn.AddMinutes(-5))
            return _cachedToken;

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_cachedToken != null && DateTimeOffset.UtcNow < _tokenExpiresOn.AddMinutes(-5))
                return _cachedToken;

            var requestCtx = new TokenRequestContext(new[] { "https://graph.microsoft.com/.default" });
            var t = await _credential.GetTokenAsync(requestCtx, ct);
            _cachedToken = t.Token;
            _tokenExpiresOn = t.ExpiresOn;
            return _cachedToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    public void Dispose()
    {
        _cache.Dispose();
        _concurrencyLimiter.Dispose();
        _tokenLock.Dispose();
        foreach (var l in _urlLocks.Values) l.Dispose();
    }
}
