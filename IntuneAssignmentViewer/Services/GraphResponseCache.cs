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
    private readonly IMemoryCache _cache;
    private readonly ILogger<GraphResponseCache> _logger;
    private readonly PerformanceOptions _perf;
    private readonly CacheOptions _cacheOpts;

    private readonly SemaphoreSlim _concurrencyLimiter;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _tokenExpiresOn;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _urlLocks = new();

    public GraphResponseCache(
        IHttpClientFactory httpClientFactory,
        TokenCredential credential,
        IMemoryCache cache,
        IOptions<PerformanceOptions> perf,
        IOptions<CacheOptions> cacheOpts,
        ILogger<GraphResponseCache> logger)
    {
        _httpClientFactory = httpClientFactory;
        _credential = credential;
        _cache = cache;
        _perf = perf.Value;
        _cacheOpts = cacheOpts.Value;
        _logger = logger;
        _concurrencyLimiter = new SemaphoreSlim(_perf.MaxConcurrentGraphRequests, _perf.MaxConcurrentGraphRequests);
    }

    /// <summary>
    /// GET a Graph URL with memory caching and concurrency limiting.
    /// Returns null on error (404, 403, network failure, etc.).
    /// </summary>
    public async Task<JsonElement?> GetAsync(string url, TimeSpan ttl)
    {
        // Fast path: cache hit
        if (_cache.TryGetValue<JsonElement?>(url, out var cached))
        {
            return cached;
        }

        // Coalesce concurrent identical requests via per-URL lock
        var urlLock = _urlLocks.GetOrAdd(url, _ => new SemaphoreSlim(1, 1));
        await urlLock.WaitAsync();
        try
        {
            // Re-check after acquiring the lock
            if (_cache.TryGetValue<JsonElement?>(url, out cached))
            {
                return cached;
            }

            var fetched = await FetchWithRetryAsync(url);

            // Cache *all* outcomes including null (e.g. 403/404) for a short time to
            // prevent re-hammering missing/forbidden endpoints.
            var effectiveTtl = fetched == null
                ? TimeSpan.FromMinutes(_cacheOpts.NegativeTtlMinutes)
                : ttl;
            _cache.Set(url, fetched, effectiveTtl);
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
    /// Pre-fetch multiple Graph GET URLs in batches of up to 20 using POST /$batch,
    /// then populate the cache. Already-cached URLs are skipped. Returns once all
    /// batches have completed.
    /// </summary>
    public async Task PreFetchBatchAsync(IEnumerable<string> urls, TimeSpan ttl)
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
            await ExecuteBatchAsync(chunks[i], ttl);
            // Small gap between batches to avoid hitting the /$batch endpoint's
            // own per-minute throttling limit (~50 req/min/tenant on some plans)
            if (i < chunks.Count - 1 && _perf.BatchSpacingMs > 0)
            {
                await Task.Delay(_perf.BatchSpacingMs);
            }
        }
    }

    private async Task ExecuteBatchAsync(string[] urls, TimeSpan ttl)
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

        await _concurrencyLimiter.WaitAsync();
        HttpResponseMessage? response = null;
        try
        {
            var client = await GetClientAsync();
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            response = await client.PostAsync(batchUrl, content);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("$batch failed: {Status}", response.StatusCode);
                // Fall back: cache nothing — subsequent individual GETs will populate
                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
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
                _cache.Set(originalUrl, body, ttlToUse);
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
    public async Task<HttpResponseMessage> PostAsync(string url, HttpContent content)
    {
        await _concurrencyLimiter.WaitAsync();
        try
        {
            var client = await GetClientAsync();
            return await client.PostAsync(url, content);
        }
        finally
        {
            _concurrencyLimiter.Release();
        }
    }

    private async Task<JsonElement?> FetchWithRetryAsync(string url)
    {
        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            TimeSpan? retryDelay = null;

            await _concurrencyLimiter.WaitAsync();
            HttpResponseMessage? response = null;
            try
            {
                var client = await GetClientAsync();
                response = await client.GetAsync(url);

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
                        var body = await response.Content.ReadAsStringAsync();
                        _logger.LogWarning("Graph request failed: {Status} {Url} - {Body}",
                            response.StatusCode, url, body.Length > 500 ? body[..500] : body);
                    }
                    return null;
                }
                else
                {
                    await using var stream = await response.Content.ReadAsStreamAsync();
                    using var doc = await JsonDocument.ParseAsync(stream);
                    return doc.RootElement.Clone();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error fetching: {Url}", url);
                return null;
            }
            finally
            {
                response?.Dispose();
                // Release the global concurrency permit BEFORE sleeping for retry,
                // otherwise we deadlock under load (10 throttled requests holding
                // all permits while sleeping their backoff).
                _concurrencyLimiter.Release();
            }

            if (retryDelay.HasValue)
            {
                await Task.Delay(retryDelay.Value);
                continue;
            }
            break;
        }
        return null;
    }

    /// <summary>Bust the cache (e.g. on user-triggered Refresh).</summary>
    public void InvalidateAll()
    {
        if (_cache is MemoryCache mc) mc.Compact(1.0);
    }

    public async Task<HttpClient> GetClientAsync()
    {
        var client = _httpClientFactory.CreateClient("GraphBeta");
        var token = await GetTokenAsync();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<string> GetTokenAsync()
    {
        if (_cachedToken != null && DateTimeOffset.UtcNow < _tokenExpiresOn.AddMinutes(-5))
            return _cachedToken;

        await _tokenLock.WaitAsync();
        try
        {
            if (_cachedToken != null && DateTimeOffset.UtcNow < _tokenExpiresOn.AddMinutes(-5))
                return _cachedToken;

            var ctx = new TokenRequestContext(new[] { "https://graph.microsoft.com/.default" });
            var t = await _credential.GetTokenAsync(ctx, CancellationToken.None);
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
        _concurrencyLimiter.Dispose();
        _tokenLock.Dispose();
        foreach (var l in _urlLocks.Values) l.Dispose();
    }
}
