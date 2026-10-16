using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PayNowAlerts;

/// <summary>The few fields of a PayNow order the plugin uses.</summary>
internal sealed record Order
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("pretty_id")]
    public string? PrettyId { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("currency")]
    public string? Currency { get; init; }

    /// <summary>In the smallest currency unit, in the store's currency.</summary>
    [JsonPropertyName("total_amount")]
    public long TotalAmount { get; init; }

    [JsonPropertyName("total_amount_str")]
    public string? TotalAmountText { get; init; }

    [JsonPropertyName("is_subscription")]
    public bool IsSubscription { get; init; }

    [JsonPropertyName("completed_at")]
    public DateTimeOffset? CompletedAt { get; init; }

    [JsonPropertyName("customer")]
    public Customer? Customer { get; init; }

    [JsonPropertyName("lines")]
    public IReadOnlyList<OrderLine> Lines { get; init; } = [];

    /// <summary>Who bought it: their game name where there is one.</summary>
    public string Buyer =>
        FirstText(Customer?.Profile?.Name, Customer?.Minecraft?.Name, Customer?.Steam?.Name, Customer?.Name) ?? "Someone";

    public string? AvatarUrl =>
        FirstText(Customer?.Profile?.AvatarUrl, Customer?.Minecraft?.AvatarUrl, Customer?.Steam?.AvatarUrl);

    /// <summary>"1,000 Gems", "32 × 1,000 Gems" or "VIP and 2 more".</summary>
    public string What
    {
        get
        {
            if (Lines.Count == 0)
            {
                return "an order";
            }

            OrderLine first = Lines[0];
            string name = string.IsNullOrWhiteSpace(first.ProductName) ? "a product" : first.ProductName.Trim();
            if (first.Quantity > 1)
            {
                name = $"{first.Quantity} × {name}";
            }

            return Lines.Count == 1 ? name : $"{name} and {Lines.Count - 1} more";
        }
    }

    private static string? FirstText(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}

internal sealed record OrderLine
{
    [JsonPropertyName("product_name")]
    public string? ProductName { get; init; }

    [JsonPropertyName("quantity")]
    public int Quantity { get; init; } = 1;
}

internal sealed record Customer
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("profile")]
    public Profile? Profile { get; init; }

    [JsonPropertyName("minecraft")]
    public Profile? Minecraft { get; init; }

    [JsonPropertyName("steam")]
    public Profile? Steam { get; init; }
}

internal sealed record Profile
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("avatar_url")]
    public string? AvatarUrl { get; init; }
}

/// <summary>The store's figures for today, this month and all time.</summary>
internal sealed record Summary
{
    [JsonPropertyName("today_revenue")]
    public Money? Today { get; init; }

    [JsonPropertyName("today_order_count")]
    public int TodayCount { get; init; }

    [JsonPropertyName("month_to_date_revenue")]
    public Money? Month { get; init; }

    [JsonPropertyName("month_to_date_order_count")]
    public int MonthCount { get; init; }

    [JsonPropertyName("lifetime_revenue")]
    public Money? Lifetime { get; init; }

    [JsonPropertyName("lifetime_order_count")]
    public int LifetimeCount { get; init; }
}

internal sealed record Money
{
    [JsonPropertyName("iso_currency")]
    public string? Currency { get; init; }

    [JsonPropertyName("amount_minor_units")]
    public long Minor { get; init; }
}

/// <summary>A failure the user can act on, with a sentence that says what to do.</summary>
internal sealed class PayNowException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Reads completed orders from the PayNow Management API.</summary>
internal sealed class PayNowClient : IDisposable
{
    private const string BaseUrl = "https://api.paynow.gg/v1/";
    private const int MaxAvatarBytes = 512 * 1024;

    private readonly HttpClient _http;

    // Avatars come from other hosts (crafthead, Steam), so they are fetched without the API key.
    private readonly HttpClient _avatars = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly string _storeId;

    public PayNowClient(string storeId, string apiKey)
    {
        _storeId = storeId;
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("APIKey", apiKey);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WNotch-PayNow/1.0");
    }

    /// <summary>The newest completed orders, newest first.</summary>
    public async Task<IReadOnlyList<Order>> GetRecentOrdersAsync(int limit, CancellationToken cancel)
    {
        string path = string.Create(CultureInfo.InvariantCulture,
            $"stores/{Uri.EscapeDataString(_storeId)}/orders?limit={limit}&status=completed");

        using HttpResponseMessage response = await _http.GetAsync(path, cancel).ConfigureAwait(false);
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
                throw new PayNowException("PayNow did not accept the API key. Enter it again in Notch Settings.");
            case HttpStatusCode.Forbidden:
                throw new PayNowException("The API key may not read this store's orders. Give it access to orders in the PayNow dashboard.");
            case HttpStatusCode.NotFound:
                throw new PayNowException("PayNow does not know that store ID. Check it in Notch Settings.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new PayNowException($"PayNow answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        await using Stream body = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<List<Order>>(body, cancellationToken: cancel).ConfigureAwait(false) ?? [];
    }

    /// <summary>Today's, this month's and all-time sales, counted in the user's own time zone.</summary>
    public async Task<Summary?> GetSummaryAsync(CancellationToken cancel)
    {
        string zone = TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out string? iana) ? iana : "UTC";
        string path = $"https://api.paynow.gg/v2/stores/{Uri.EscapeDataString(_storeId)}/stats/orders/summary?tz={Uri.EscapeDataString(zone)}";
        using HttpResponseMessage response = await _http.GetAsync(path, cancel).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Figures are a nicety: a key that may read orders but not statistics still announces sales.
            return null;
        }

        await using Stream body = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<Summary>(body, cancellationToken: cancel).ConfigureAwait(false);
    }

    /// <summary>The buyer's avatar as image bytes, or null when there is none or it cannot be fetched.</summary>
    public async Task<byte[]?> GetAvatarAsync(string? url, CancellationToken cancel)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        try
        {
            using HttpResponseMessage response = await _avatars.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxAvatarBytes)
            {
                return null;
            }

            byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancel).ConfigureAwait(false);
            return bytes.Length is > 0 and <= MaxAvatarBytes ? bytes : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _avatars.Dispose();
    }
}
