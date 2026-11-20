using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace WhereEQ.Services;

/// <summary>Auction prices for one item on one server (pigparse.org: averages of WTS/WTB auctions it parsed).</summary>
public sealed record PriceInfo(string Item, int? Price, string Window, int Count, int? Wtb, DateTime? LastWts)
{
    /// <summary>"1,117 pp" or "no price".</summary>
    public string Text => Price is { } p ? $"{p:N0} pp" : "no price";

    public string Tip => Price is { } p
        ? $"{Item}: {p:N0} pp average asking price (WTS), {Window}, from {Count:N0} auctions" +
          (Wtb is { } b ? $"\nWanted (WTB) average: {b:N0} pp" : "") +
          (LastWts is { } t ? $"\nLast seen for sale {t.ToLocalTime():MMM d}" : "") +
          "\nPrices from pigparse.org (auction chat, not actual sales)"
        : $"{Item}: pigparse.org has no auction prices for it on this server";
}

/// <summary>
/// Looks up item prices on pigparse.org (GET <see cref="DefaultUrl"/>, no sign-in; the address is a setting). Only the item name and
/// server are sent. Answers are kept for an hour (pigparse rebuilds its averages every 10 minutes); failed lookups
/// are retried after 5 minutes.
/// </summary>
public static class PriceLookup
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly ConcurrentDictionary<string, (PriceInfo? Info, DateTime At, bool Failed)> Cache = new();
    private static readonly ConcurrentDictionary<string, Task<PriceInfo?>> InFlight = new();
    private static readonly TimeSpan Keep = TimeSpan.FromHours(1), RetryFailed = TimeSpan.FromMinutes(5);

    /// <summary>Raised (on a thread-pool thread) when a lookup finished, so lists can redraw.</summary>
    public static event Action? Updated;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var v = typeof(PriceLookup).Assembly.GetName().Version;
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"WhereEQ/{v?.Major ?? 0}.{v?.Minor ?? 1}.{v?.Build ?? 0}");
        return c;
    }

    /// <summary>pigparse's server name from the log's ("P1999Green" → "Green").</summary>
    public static string ServerName(string? logServer)
    {
        var s = logServer ?? "";
        if (s.Contains("blue", StringComparison.OrdinalIgnoreCase)) return "Blue";
        if (s.Contains("red", StringComparison.OrdinalIgnoreCase)) return "Red";
        if (s.Contains("quarm", StringComparison.OrdinalIgnoreCase)) return "Quarm";
        return "Green";
    }

    private static string Key(string server, string item) => server + "|" + item.ToLowerInvariant();

    /// <summary>True if there's a fresh answer (info is null when pigparse has no price for the item).</summary>
    public static bool TryGetCached(string server, string item, out PriceInfo? info)
    {
        info = null;
        if (!Cache.TryGetValue(Key(server, item), out var c)) return false;
        if (DateTime.UtcNow - c.At > (c.Failed ? RetryFailed : Keep)) return false;
        info = c.Info;
        return !c.Failed;
    }

    /// <summary>pigparse's price API; {server} = Green/Blue/Red/Quarm, {item} = the item name (URL-encoded).</summary>
    public const string DefaultUrl = "https://www.pigparse.org/api/item/get/{server}/{item}";

    private static string _url = DefaultUrl;
    /// <summary>Settings → Options → Loot → "Price lookup address" (in case pigparse moves its API). Changing it
    /// forgets cached prices.</summary>
    public static string UrlTemplate
    {
        get => _url;
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? DefaultUrl : value.Trim();
            if (v == _url) return;
            _url = v;
            Cache.Clear();
        }
    }

    /// <summary>The address for one item, or null if the template isn't a usable http(s) address.</summary>
    public static string? UrlFor(string template, string server, string item)
    {
        var url = template.Replace("{server}", Uri.EscapeDataString(server), StringComparison.OrdinalIgnoreCase)
                          .Replace("{item}", Uri.EscapeDataString(item), StringComparison.OrdinalIgnoreCase);
        return Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)
            ? u.AbsoluteUri : null;
    }

    /// <summary>Settings → Options → "Look up prices on pigparse.org". Off: nothing is ever sent.</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>The price, from the cache or pigparse. Null = no data, the site couldn't be reached, or lookups are off.</summary>
    public static Task<PriceInfo?> GetAsync(string server, string item)
    {
        if (!Enabled) return Task.FromResult<PriceInfo?>(null);
        if (TryGetCached(server, item, out var cached)) return Task.FromResult(cached);
        var key = Key(server, item);
        if (Cache.TryGetValue(key, out var c) && c.Failed && DateTime.UtcNow - c.At < RetryFailed) return Task.FromResult<PriceInfo?>(null);
        return InFlight.GetOrAdd(key, _ => FetchAsync(server, item, key));
    }

    /// <summary>Starts a lookup if there's no fresh answer (for lists that show prices).</summary>
    public static void Prefetch(string server, string item) => _ = GetAsync(server, item);

    private static async Task<PriceInfo?> FetchAsync(string server, string item, string key)
    {
        try
        {
            var url = UrlFor(UrlTemplate, server, item) ?? throw new InvalidOperationException("bad price lookup address");
            using var resp = await Http.GetAsync(url).ConfigureAwait(false);
            PriceInfo? info;
            if (resp.StatusCode == HttpStatusCode.NoContent) info = new PriceInfo(item, null, "", 0, null, null);
            else
            {
                resp.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
                info = Parse(item, doc.RootElement);
            }
            Cache[key] = (info, DateTime.UtcNow, false);
            return info;
        }
        catch
        {
            Cache[key] = (null, DateTime.UtcNow, true); // offline / site down: try again in a few minutes
            return null;
        }
        finally
        {
            InFlight.TryRemove(key, out _);
            try { Updated?.Invoke(); } catch { /* a redraw must never break lookups */ }
        }
    }

    /// <summary>The most recent window that has a price: 30 days, else 60, 90, 6 months, a year.</summary>
    internal static PriceInfo Parse(string item, JsonElement e)
    {
        int Int(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        var windows = new[] { ("30Days", "last 30 days"), ("60Days", "last 60 days"), ("90Days", "last 90 days"),
                              ("6Months", "last 6 months"), ("Year", "last year") };
        int? price = null; var window = ""; var count = 0;
        foreach (var (k, label) in windows)
        {
            var avg = Int($"totalWTSLast{k}Average");
            var n = Int($"totalWTSLast{k}Count");
            if (avg > 0 && n > 0) { price = avg; window = label; count = n; break; }
        }
        int? wtb = null;
        foreach (var (k, _) in windows)
            if (Int($"totalWTBLast{k}Average") is > 0 and var b && Int($"totalWTBLast{k}Count") > 0) { wtb = b; break; }
        DateTime? last = e.TryGetProperty("lastWTSSeen", out var l) && l.ValueKind == JsonValueKind.String && l.TryGetDateTime(out var d) ? d : null;
        var name = e.TryGetProperty("itemName", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString() ?? item : item;
        return new PriceInfo(name, price, window, count, wtb, last);
    }
}
