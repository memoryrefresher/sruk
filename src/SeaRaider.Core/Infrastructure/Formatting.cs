using System.Globalization;

namespace SeaRaider.Core;

/// <summary>Human-friendly formatting for sizes, speeds and durations.</summary>
public static class Formatting
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        var order = Math.Min((int)Math.Floor(Math.Log(bytes, 1024)), Units.Length - 1);
        var value = bytes / Math.Pow(1024, order);
        return value.ToString(order == 0 ? "0" : value < 10 ? "0.00" : value < 100 ? "0.0" : "0", CultureInfo.CurrentCulture)
               + " " + Units[order];
    }

    /// <summary>"14.9 GB", "800 MB", "1,5 GiB" → bytes; null when the text has no size.</summary>
    public static long? ParseBytes(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(text, @"(?<n>\d+(?:[.,]\d+)?)\s*(?<u>[KMGT])i?B\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var number = double.Parse(m.Groups["n"].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        var power = "KMGT".IndexOf(char.ToUpperInvariant(m.Groups["u"].Value[0])) + 1;
        return (long)(number * Math.Pow(1024, power));
    }

    public static string Speed(double bytesPerSecond) => bytesPerSecond < 1 ? "—" : Bytes((long)bytesPerSecond) + "/s";

    public static string Duration(TimeSpan? span)
    {
        if (span is not { } t) return "—";
        if (t.TotalSeconds < 1) return "< 1s";
        if (t.TotalMinutes < 1) return $"{t.Seconds}s";
        if (t.TotalHours < 1) return $"{t.Minutes}m {t.Seconds:00}s";
        if (t.TotalDays < 1) return $"{(int)t.TotalHours}h {t.Minutes:00}m";
        return $"{(int)t.TotalDays}d {t.Hours}h";
    }

    public static string Playtime(TimeSpan t) =>
        t.TotalMinutes < 1 ? "Never played" :
        t.TotalHours < 1 ? $"{(int)t.TotalMinutes} min played" :
        $"{t.TotalHours:0.#} hrs played";
}
