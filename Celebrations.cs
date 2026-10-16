using Notch.Core.Activities;
using Notch.Core.Plugins;

namespace PayNowAlerts;

/// <summary>
/// One animation of the glow around the pill, played once for a sale. <see cref="Frame"/> is asked
/// for the light at <c>t</c> seconds into it, 0 to <see cref="Duration"/>; <c>scale</c> is 1 for a
/// normal sale and larger for a big one.
/// </summary>
internal interface ICelebration
{
    string Name { get; }

    double Duration { get; }

    GlowFrame Frame(double t, double scale);
}

internal static class Celebrations
{
    public const int Segments = 40;

    public static readonly IReadOnlyList<ICelebration> All =
        [new Confetti(), new Gold(), new Rainbow(), new Fireworks(), new Cash()];

    /// <summary>The named animation, or null for "Off" or a name it does not know.</summary>
    public static ICelebration? Named(string? name) =>
        All.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Rises fast, holds, then fades out over the last part of the animation.</summary>
    public static double Envelope(double t, double duration, double fadeShare = 0.35)
    {
        double rise = Math.Clamp(t / 0.15, 0, 1);
        double fadeStart = duration * (1 - fadeShare);
        double fall = t <= fadeStart ? 1 : Math.Clamp(1 - ((t - fadeStart) / (duration - fadeStart)), 0, 1);
        return rise * fall;
    }

    public static GlowColor Hue(double hue)
    {
        hue = ((hue % 1) + 1) % 1 * 6;
        double x = 1 - Math.Abs((hue % 2) - 1);
        (double r, double g, double b) = (int)hue switch
        {
            0 => (1d, x, 0d),
            1 => (x, 1d, 0d),
            2 => (0d, 1d, x),
            3 => (0d, x, 1d),
            4 => (x, 0d, 1d),
            _ => (1d, 0d, x),
        };
        return new GlowColor((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    public static GlowSegment[] Blank() => new GlowSegment[Segments];
}

/// <summary>Coloured sparks popping up all along the pill over a warm base.</summary>
internal sealed class Confetti : ICelebration
{
    private static readonly GlowColor[] Colors =
        [GlowColor.Yellow, GlowColor.Cyan, GlowColor.Violet, GlowColor.Green, GlowColor.Orange, new GlowColor(0xFF, 0x4F, 0xA3)];

    public string Name => "Confetti";

    public double Duration => 3.2;

    public GlowFrame Frame(double t, double scale)
    {
        double duration = Duration * scale;
        double envelope = Celebrations.Envelope(t, duration);
        GlowSegment[] segments = Celebrations.Blank();
        for (int i = 0; i < segments.Length; i++)
        {
            // Each segment pops at its own moments, chosen by a cheap hash so the pattern is stable.
            uint seed = Hash((uint)i);
            double period = 0.45 + ((seed % 100) / 100.0 * 0.5);
            double phase = ((seed >> 8) % 100) / 100.0 * period;
            double local = ((t + phase) % period) / period;
            double spark = Math.Pow(1 - local, 3);
            int round = (int)((t + phase) / period);
            GlowColor color = Colors[(int)(Hash(seed + (uint)round) % (uint)Colors.Length)];
            segments[i] = new GlowSegment(color.Lerp(GlowColor.Amber, 1 - spark), (0.25 + (0.75 * spark)) * envelope);
        }

        return new GlowFrame { Segments = segments, Reach = 1.1 * scale };
    }

    private static uint Hash(uint x)
    {
        x ^= x >> 16;
        x *= 0x7FEB352D;
        x ^= x >> 15;
        x *= 0x846CA68B;
        x ^= x >> 16;
        return x;
    }
}

/// <summary>A bright gold shimmer sweeping across the pill, twice, over a gold glow.</summary>
internal sealed class Gold : ICelebration
{
    private static readonly GlowColor Base = new(0xFF, 0xB8, 0x2E);
    private static readonly GlowColor Shine = new(0xFF, 0xF4, 0xC8);

    public string Name => "Gold";

    public double Duration => 2.8;

    public GlowFrame Frame(double t, double scale)
    {
        double duration = Duration * scale;
        double envelope = Celebrations.Envelope(t, duration);
        double sweep = (t / (duration * 0.45)) % 1 * 1.6 - 0.3;
        GlowSegment[] segments = Celebrations.Blank();
        for (int i = 0; i < segments.Length; i++)
        {
            double x = i / (double)(segments.Length - 1);
            double shine = Math.Exp(-Math.Pow((x - sweep) / 0.09, 2));
            segments[i] = new GlowSegment(Base.Lerp(Shine, shine), (0.55 + (0.45 * shine)) * envelope);
        }

        return new GlowFrame { Segments = segments, Reach = (1 + (0.3 * Math.Sin(t * 6) * envelope)) * scale };
    }
}

/// <summary>A rainbow running along the pill, fast at first and slowing as it fades.</summary>
internal sealed class Rainbow : ICelebration
{
    public string Name => "Rainbow";

    public double Duration => 3.0;

    public GlowFrame Frame(double t, double scale)
    {
        double duration = Duration * scale;
        double envelope = Celebrations.Envelope(t, duration);
        double travel = (t * 1.6) - (0.15 * t * t);
        GlowSegment[] segments = Celebrations.Blank();
        for (int i = 0; i < segments.Length; i++)
        {
            double x = i / (double)segments.Length;
            segments[i] = new GlowSegment(Celebrations.Hue(x - travel), envelope);
        }

        return new GlowFrame { Segments = segments, Reach = 1.2 * scale };
    }
}

/// <summary>Bursts that bloom out from points along the pill, one after another.</summary>
internal sealed class Fireworks : ICelebration
{
    private static readonly (double At, double Where, GlowColor Color)[] Bursts =
    [
        (0.0, 0.5, GlowColor.Yellow),
        (0.45, 0.2, new GlowColor(0xFF, 0x4F, 0xA3)),
        (0.8, 0.8, GlowColor.Cyan),
        (1.2, 0.35, GlowColor.Green),
        (1.55, 0.65, GlowColor.Violet),
        (1.9, 0.5, GlowColor.White),
    ];

    public string Name => "Fireworks";

    public double Duration => 3.4;

    public GlowFrame Frame(double t, double scale)
    {
        double duration = Duration * scale;
        double envelope = Celebrations.Envelope(t, duration, 0.25);
        GlowSegment[] segments = Celebrations.Blank();
        for (int i = 0; i < segments.Length; i++)
        {
            double x = i / (double)(segments.Length - 1);
            double best = 0;
            GlowColor color = GlowColor.White;
            foreach ((double at, double where, GlowColor burstColor) in Bursts)
            {
                // A big sale replays the bursts, so it keeps going for as long as it lasts.
                double age = (t - (at * scale)) % (2.2 * scale);
                if (age < 0)
                {
                    continue;
                }

                double radius = 0.35 * (1 - Math.Exp(-age * 4));
                double ring = Math.Exp(-Math.Pow((Math.Abs(x - where) - radius) / 0.05, 2));
                double light = ring * Math.Exp(-age * 1.8);
                if (light > best)
                {
                    best = light;
                    color = burstColor;
                }
            }

            segments[i] = new GlowSegment(color, Math.Min(1, best * 1.4) * envelope);
        }

        return new GlowFrame { Segments = segments, Reach = 1.3 * scale };
    }
}

/// <summary>"Cha-ching": two quick green flashes, then a green glow that settles.</summary>
internal sealed class Cash : ICelebration
{
    private static readonly GlowColor Money = new(0x3F, 0xE0, 0x7A);

    public string Name => "Cash";

    public double Duration => 2.4;

    public GlowFrame Frame(double t, double scale)
    {
        double duration = Duration * scale;
        double flash = Math.Max(Kick(t, 0), Kick(t, 0.28));
        double settle = Celebrations.Envelope(t, duration, 0.6) * 0.55;
        double intensity = Math.Max(flash, settle);
        return new GlowFrame
        {
            Color = Money.Lerp(GlowColor.White, flash * 0.5),
            Intensity = intensity,
            Reach = (1 + (0.8 * flash)) * scale,
        };
    }

    private static double Kick(double t, double at) => t < at ? 0 : Math.Exp(-(t - at) * 9);
}
