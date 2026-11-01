using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace MarsForge.App;

/// <summary>Search: well-known places on Mars, and latitude / longitude typed in many ways.</summary>
public static partial class Places
{
    public sealed record Place(string Name, string Note, double Lat, double Lon);

    // east-positive longitudes, -180..180
    public static readonly Place[] Known =
    [
        new("Jezero Crater", "Perseverance rover", 18.38, 77.58),
        new("Gale Crater", "Curiosity rover", -5.40, 137.80),
        new("Gusev Crater", "Spirit rover", -14.57, 175.47),
        new("Meridiani Planum", "Opportunity rover", -1.95, -5.53),
        new("Utopia Planitia", "Zhurong rover", 25.07, 109.93),
        new("Elysium Planitia", "InSight lander", 4.50, 135.62),
        new("Oxia Planum", "Rosalind Franklin landing site", 18.27, -24.63),
        new("Ares Vallis", "Pathfinder", 19.13, -33.22),
        new("Chryse Planitia", "Viking 1", 22.27, -47.95),
        new("Utopia Planitia (Viking 2)", "Viking 2", 47.64, 134.29),
        new("Green Valley", "Phoenix lander", 68.22, -125.75),
        new("Olympus Mons", "tallest volcano", 18.65, -133.80),
        new("Arsia Mons", "volcano", -8.26, -120.09),
        new("Valles Marineris", "giant canyon", -13.90, -59.20),
        new("Hellas Planitia", "giant impact basin", -42.40, 70.50),
        new("Mawrth Vallis", "clay-rich valley", 22.60, -17.00),
        new("Holden Crater", "ancient lake", -26.40, -34.10),
        new("Eberswalde Crater", "ancient river delta", -23.90, -33.30),
        new("Syrtis Major", "dark volcanic plain", 8.40, 69.50),
        new("Arcadia Planitia", "smooth northern plain", 47.20, -176.30),
    ];

    public static IEnumerable<Place> Find(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return Known.Take(8);
        return Known.Where(p => p.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || p.Note.Contains(text, StringComparison.OrdinalIgnoreCase)).Take(8);
    }

    [GeneratedRegex(@"([-+−]?\d+(?:[.,]\d+)?)\s*(?:[°º]\s*(?:(\d+(?:\.\d+)?)\s*['′]\s*)?(?:(\d+(?:\.\d+)?)\s*[""″]\s*)?)?\s*([NSEWnsew])?(?![a-zA-Z])")]
    private static partial Regex Number();

    /// <summary>Reads "18.4, 77.5", "18.4N 77.5E", "-5.4 137.8", "18°26'N 77°35'E", "lat 18.4 lon 282.5" ...
    /// Longitudes 0..360 are accepted. Returns null if it is not two valid numbers.</summary>
    public static (double lat, double lon)? ParseLatLon(string text)
    {
        var t = text.Replace('−', '-');
        var found = new List<(double value, char? hemi)>();
        foreach (Match m in Number().Matches(t))
        {
            double v = double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            double sign = v < 0 || m.Groups[1].Value.StartsWith('-') ? -1 : 1;
            v = Math.Abs(v);
            if (m.Groups[2].Success) v += double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) / 60;
            if (m.Groups[3].Success) v += double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) / 3600;
            char? h = m.Groups[4].Success ? char.ToUpperInvariant(m.Groups[4].Value[0]) : null;
            if (h is 'S' or 'W') sign = -sign;
            found.Add((sign * v, h));
        }
        if (found.Count != 2) return null;
        double lat, lon;
        if (found[0].hemi is 'E' or 'W' || found[1].hemi is 'N' or 'S') (lat, lon) = (found[1].value, found[0].value);
        else (lat, lon) = (found[0].value, found[1].value);
        if (lon > 180 && lon <= 360) lon -= 360;
        if (Math.Abs(lat) > 90 || Math.Abs(lon) > 180) return null;
        return (lat, lon);
    }

    public static string Format(double lat, double lon) =>
        string.Create(CultureInfo.InvariantCulture, $"{Math.Abs(lat):0.000}°{(lat >= 0 ? "N" : "S")}, {Math.Abs(lon):0.000}°{(lon >= 0 ? "E" : "W")}");
}
