using System.Diagnostics;
using Activity = Notch.Core.Activities.Activity;
using System.Globalization;
using System.Threading.Channels;
using Notch.Core.Activities;
using Notch.Core.Plugins;

namespace PayNowAlerts;

/// <summary>
/// Watches a PayNow store and celebrates each sale: an alert in the pill with the buyer's avatar and
/// what they bought, an animation on the glow around the pill, and (optionally) a reminder that
/// stays until the user has looked. A card and a tab show today's figures and the latest sales.
/// </summary>
/// <remarks>
/// PayNow can only send webhooks to a public address, which a desktop rarely has, so the plugin
/// polls the store's completed orders instead and remembers which ones it has already announced.
/// </remarks>
public sealed class PayNowPlugin : INotchPlugin
{
    private const string PageId = "sales";
    private const string AlertId = "sale";
    private const string ReminderId = "unseen";
    private const string SeenKey = "seenOrders";
    private const int FetchLimit = 50;
    private const int MaxSeen = 200;
    private const int MaxRecent = 30;

    // More than this many sales at once are announced together instead of one by one.
    private const int MaxAnnouncedSeparately = 3;

    // Orders completed longer ago than this are never announced, only remembered.
    private static readonly TimeSpan Freshness = TimeSpan.FromHours(6);
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(33);

    private static readonly GlowColor SaleColor = new(0x3F, 0xE0, 0x7A);

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<Announcement> _announcements = Channel.CreateUnbounded<Announcement>();
    private readonly LinkedList<string> _seenOrder = new();
    private readonly HashSet<string> _seen = [];
    private readonly List<Order> _recent = [];

    private IPluginHost? _host;
    private PluginOptions _options = new();
    private PayNowClient? _client;
    private Summary? _summary;
    private string? _error;
    private DateTimeOffset? _lastChecked;
    private int _unseen;
    private long _unseenMinor;
    private string _currency = "EUR";

    public void Start(IPluginHost host)
    {
        _host = host;
        _options = PluginOptions.Load(host.Settings, host.Log);
        foreach (string id in host.Settings.Get<string[]>(SeenKey, []) ?? [])
        {
            Remember(id);
        }

        host.Shell.Changed += OnShellChanged;

        // Started even without a store, so "Test alert" works before the plugin is set up.
        _ = Task.Run(() => AnnounceLoopAsync(_stop.Token));

        if (_options.Problem is not null)
        {
            _error = _options.Problem;
            Render();
            return;
        }

        _client = new PayNowClient(_options.StoreId, _options.ApiKey!);
        Render();
        _ = Task.Run(() => PollLoopAsync(_stop.Token));
    }

    public void Stop()
    {
        _stop.Cancel();
        _announcements.Writer.TryComplete();
        _client?.Dispose();
        _host?.Glow.Clear();
    }

    // ---- Polling --------------------------------------------------------------------------------

    private async Task PollLoopAsync(CancellationToken cancel)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.PollSeconds));
        do
        {
            try
            {
                await PollAsync(cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                string message = e switch
                {
                    PayNowException => e.Message,
                    HttpRequestException or TaskCanceledException => "Could not reach PayNow. Trying again shortly.",
                    _ => "Could not read the store's orders.",
                };

                bool changed;
                lock (_gate)
                {
                    changed = _error != message;
                    _error = message;
                }

                if (changed)
                {
                    _host?.Log.Warn($"{message} ({e.GetType().Name}: {e.Message})");
                    Render();
                }
            }
        }
        while (await timer.WaitForNextTickAsync(cancel).ConfigureAwait(false));
    }

    private async Task PollAsync(CancellationToken cancel)
    {
        IPluginHost host = _host!;
        IReadOnlyList<Order> orders = await _client!.GetRecentOrdersAsync(FetchLimit, cancel).ConfigureAwait(false);
        Summary? summary = await _client.GetSummaryAsync(cancel).ConfigureAwait(false);

        List<Order> fresh = [];
        bool firstRun;
        lock (_gate)
        {
            // The first time the plugin sees a store it only learns what is there; it does not
            // announce a store's whole history.
            firstRun = _seen.Count == 0;
            DateTimeOffset now = DateTimeOffset.UtcNow;

            // Oldest first, so sales are announced in the order they happened.
            foreach (Order order in orders.Where(o => o.Id.Length > 0).Reverse())
            {
                if (!Remember(order.Id) || firstRun)
                {
                    continue;
                }

                if (order.Status is not (null or "completed") || now - (order.CompletedAt ?? now) > Freshness)
                {
                    continue;
                }

                fresh.Add(order);
            }

            _recent.Clear();
            _recent.AddRange(orders.Take(MaxRecent));
            _summary = summary ?? _summary;
            _currency = summary?.Today?.Currency ?? orders.FirstOrDefault()?.Currency?.ToUpperInvariant() ?? _currency;
            _error = null;
            _lastChecked = DateTimeOffset.Now;
        }

        host.Settings.Set(SeenKey, SeenSnapshot());

        List<Order> announced = _options.SkipFree ? fresh.Where(o => o.TotalAmount > 0).ToList() : fresh;
        if (firstRun)
        {
            host.Log.Info($"Watching store {_options.StoreId}; {orders.Count} earlier orders will not be announced.");
        }
        else if (fresh.Count > 0)
        {
            host.Log.Info($"{fresh.Count} new sale(s), {announced.Count} announced.");
        }

        if (announced.Count > MaxAnnouncedSeparately)
        {
            _announcements.Writer.TryWrite(new Announcement(announced, IsTest: false));
        }
        else
        {
            foreach (Order order in announced)
            {
                _announcements.Writer.TryWrite(new Announcement([order], IsTest: false));
            }
        }

        Render();
    }

    /// <summary>Adds an order id to the remembered set; false when it was already there.</summary>
    private bool Remember(string id)
    {
        lock (_gate)
        {
            if (!_seen.Add(id))
            {
                return false;
            }

            _seenOrder.AddLast(id);
            while (_seenOrder.Count > MaxSeen)
            {
                _seen.Remove(_seenOrder.First!.Value);
                _seenOrder.RemoveFirst();
            }

            return true;
        }
    }

    private string[] SeenSnapshot()
    {
        lock (_gate)
        {
            return [.. _seenOrder];
        }
    }

    // ---- Announcing -----------------------------------------------------------------------------

    private sealed record Announcement(IReadOnlyList<Order> Orders, bool IsTest);

    private async Task AnnounceLoopAsync(CancellationToken cancel)
    {
        try
        {
            await foreach (Announcement next in _announcements.Reader.ReadAllAsync(cancel).ConfigureAwait(false))
            {
                try
                {
                    await AnnounceAsync(next, cancel).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    _host?.Log.Error("Could not announce a sale.", e);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task AnnounceAsync(Announcement announcement, CancellationToken cancel)
    {
        IPluginHost host = _host!;
        IReadOnlyList<Order> orders = announcement.Orders;
        long total = orders.Sum(o => o.TotalAmount);
        bool big = _options.BigSaleMinor > 0 && total >= _options.BigSaleMinor;
        double scale = big ? 1.6 : 1;
        ICelebration? celebration = Celebrations.Named(_options.Animation);
        double seconds = celebration is null ? 4 : Math.Max(4, (celebration.Duration * scale) + 1.5);

        Order first = orders[0];
        string title;
        string detail;
        byte[]? image = null;
        if (orders.Count == 1)
        {
            title = $"{first.Buyer} bought {first.What}";
            detail = Price(first);
            image = await (_client?.GetAvatarAsync(first.AvatarUrl, cancel) ?? Task.FromResult<byte[]?>(null)).ConfigureAwait(false);
        }
        else
        {
            title = $"{orders.Count} new sales";
            detail = Money(total, first.Currency ?? _currency);
        }

        host.Activities.Publish(new Activity
        {
            Id = AlertId,
            Tier = ActivityTier.Transient,
            Title = big ? $"Big sale! {title}" : title,
            Detail = detail,
            Glyph = big ? "" : "",   // FavoriteStar / ShoppingCart
            Image = image,
            Glow = celebration is null ? new Glow(SaleColor, GlowPattern.Flash) : null,
            Lifetime = TimeSpan.FromSeconds(seconds),
        });

        if (!announcement.IsTest && _options.KeepAlert)
        {
            lock (_gate)
            {
                _unseen += orders.Count;
                _unseenMinor += total;
            }
        }

        if (celebration is not null)
        {
            await PlayAsync(celebration, scale, cancel).ConfigureAwait(false);
        }
        else
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), cancel).ConfigureAwait(false);
        }

        // A short pause, so two sales in a row read as two.
        await Task.Delay(TimeSpan.FromSeconds(1.2), cancel).ConfigureAwait(false);
        Render();
    }

    private async Task PlayAsync(ICelebration celebration, double scale, CancellationToken cancel)
    {
        IPluginHost host = _host!;
        double duration = celebration.Duration * scale;
        var clock = Stopwatch.StartNew();
        try
        {
            while (clock.Elapsed.TotalSeconds < duration)
            {
                // Nothing is drawn while the notch is expanded; skip the work but keep the timing.
                if (!host.Shell.IsExpanded)
                {
                    host.Glow.Set(celebration.Frame(clock.Elapsed.TotalSeconds, scale));
                }

                await Task.Delay(FrameInterval, cancel).ConfigureAwait(false);
            }
        }
        finally
        {
            host.Glow.Clear();
        }
    }

    private void Test()
    {
        Order sample = new()
        {
            Id = "test",
            Currency = _currency,
            TotalAmount = 499,
            Customer = new Customer { Name = "Steve" },
            Lines = [new OrderLine { ProductName = "Test purchase", Quantity = 1 }],
        };
        _announcements.Writer.TryWrite(new Announcement([sample], IsTest: true));
    }

    // ---- Reminder, card and page ----------------------------------------------------------------

    private void OnShellChanged(object? sender, EventArgs e)
    {
        try
        {
            IPluginHost host = _host!;
            if (host.Shell.AreCardsVisible || host.Shell.IsPageVisible(PageId))
            {
                MarkSeen();
            }
        }
        catch (Exception ex)
        {
            _host?.Log.Error("Could not update the reminder.", ex);
        }
    }

    private void MarkSeen()
    {
        lock (_gate)
        {
            if (_unseen == 0)
            {
                return;
            }

            _unseen = 0;
            _unseenMinor = 0;
        }

        Render();
    }

    private void Render()
    {
        IPluginHost? host = _host;
        if (host is null)
        {
            return;
        }

        string? error;
        Summary? summary;
        List<Order> recent;
        int unseen;
        long unseenMinor;
        string currency;
        DateTimeOffset? lastChecked;
        lock (_gate)
        {
            error = _error;
            summary = _summary;
            recent = [.. _recent];
            unseen = _unseen;
            unseenMinor = _unseenMinor;
            currency = _currency;
            lastChecked = _lastChecked;
        }

        if (unseen > 0)
        {
            host.Activities.Publish(new Activity
            {
                Id = ReminderId,
                Tier = ActivityTier.Attention,
                Title = unseen == 1 ? "1 new sale" : $"{unseen} new sales",
                Detail = Money(unseenMinor, currency),
                Glyph = "",
                Glow = new Glow(SaleColor, GlowPattern.Breathe, 0.6),
            });
        }
        else
        {
            host.Activities.Remove(ReminderId);
        }

        Order? latest = recent.FirstOrDefault();
        host.Cards.Set(new PluginCard
        {
            Id = "today",
            Label = "PayNow today",
            Value = error is not null && summary is null ? "Not connected"
                : summary is not null ? Money(summary.Today?.Minor ?? 0, summary.Today?.Currency ?? currency)
                : "…",
            Detail = error ?? (summary is not null
                ? $"{Count(summary.TodayCount, "sale")}" + (latest is null ? "" : $" · last: {latest.Buyer}, {Ago(latest.CompletedAt)}")
                : "Checking the store…"),
            Color = error is not null ? GlowColor.Amber : SaleColor,
            Clicked = () =>
            {
                MarkSeen();
                if (error is not null && _client is null)
                {
                    host.Shell.OpenSettings();
                }
                else
                {
                    host.Pages.Open(PageId);
                }
            },
        });

        List<PluginStat> stats = [];
        if (summary is not null)
        {
            stats.Add(new PluginStat { Label = "Today", Value = Money(summary.Today?.Minor ?? 0, currency), Detail = Count(summary.TodayCount, "sale"), Color = SaleColor });
            stats.Add(new PluginStat { Label = "This month", Value = Money(summary.Month?.Minor ?? 0, currency), Detail = Count(summary.MonthCount, "sale") });
            stats.Add(new PluginStat { Label = "All time", Value = Money(summary.Lifetime?.Minor ?? 0, currency), Detail = Count(summary.LifetimeCount, "sale") });
        }

        List<PluginBlock> blocks = [];
        if (error is not null)
        {
            blocks.Add(new PluginText { Text = error, Color = GlowColor.Amber });
        }

        if (recent.Count > 0)
        {
            blocks.Add(new PluginText { Text = "Latest sales", Style = PluginTextStyle.Heading });
            blocks.Add(new PluginTable
            {
                Columns = ["When", "Who", "What", "Paid"],
                Rows = [.. recent.Select(o => (IReadOnlyList<string>)[Ago(o.CompletedAt), o.Buyer, o.What, Price(o)])],
            });
        }
        else if (error is null)
        {
            blocks.Add(new PluginText { Text = lastChecked is null ? "Checking the store…" : "No sales yet.", Style = PluginTextStyle.Muted });
        }

        if (lastChecked is not null)
        {
            blocks.Add(new PluginText { Text = $"Checked at {lastChecked:t}, every {_options.PollSeconds} seconds.", Style = PluginTextStyle.Muted });
        }

        host.Pages.Set(new PluginPage
        {
            Id = PageId,
            Title = "PayNow",
            Stats = stats,
            Blocks = blocks,
            Actions =
            [
                new PluginAction { Label = "Test alert", Hint = "Plays the alert and animation for a pretend sale", Clicked = Test },
                new PluginAction { Label = "Settings", Clicked = host.Shell.OpenSettings },
            ],
        });
    }

    // ---- Formatting -----------------------------------------------------------------------------

    private static string Price(Order order) =>
        !string.IsNullOrWhiteSpace(order.TotalAmountText) ? order.TotalAmountText! : Money(order.TotalAmount, order.Currency ?? "EUR");

    internal static string Money(long minor, string currency)
    {
        string code = currency.ToUpperInvariant();
        string symbol = code switch
        {
            "EUR" => "€",
            "USD" => "$",
            "GBP" => "£",
            "JPY" => "¥",
            _ => "",
        };
        decimal amount = code == "JPY" ? minor : minor / 100m;
        string number = amount.ToString(code == "JPY" ? "N0" : "N2", CultureInfo.InvariantCulture);
        return symbol.Length > 0 ? symbol + number : $"{number} {code}";
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private static string Ago(DateTimeOffset? when)
    {
        if (when is null)
        {
            return "";
        }

        TimeSpan age = DateTimeOffset.UtcNow - when.Value;
        return age.TotalMinutes < 1 ? "just now"
            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago"
            : when.Value.ToLocalTime().ToString("d MMM", CultureInfo.CurrentCulture);
    }
}
