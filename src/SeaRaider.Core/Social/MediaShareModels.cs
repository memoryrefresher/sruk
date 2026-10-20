using System.Globalization;
using System.Text.RegularExpressions;
using LiteDB;

namespace SeaRaider.Core.Social;

/// <summary>
/// A video, trailer or stream shared with friends on the Media tab's "Shared" feed, with its reactions and comments.
/// Stored on every PC that received it (social.db). The person who shared it is the post's hub: reactions and comments
/// go to them, and their app passes them on to everyone the post was shared with.
/// </summary>
public sealed class SharedMediaPost
{
    [BsonId]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Friend code of whoever shared it.</summary>
    public string AuthorId { get; set; } = string.Empty;

    public string AuthorName { get; set; } = string.Empty;

    /// <summary>The link that was shared (a YouTube / Twitch / Vimeo address, a video file, or any page).</summary>
    public string Url { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    public string? ThumbnailUrl { get; set; }

    /// <summary>Channel or site name.</summary>
    public string? Channel { get; set; }

    /// <summary>What the sharer wrote with it.</summary>
    public string? Note { get; set; }

    /// <summary>"Watch from 1:23": where playback starts.</summary>
    public int? StartSeconds { get; set; }

    public DateTime PostedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>When it arrived on this PC (unread counts use this, not the sender's clock).</summary>
    public DateTime ReceivedUtc { get; set; } = DateTime.UtcNow;

    public List<SharedMediaReaction> Reactions { get; set; } = [];
    public List<SharedMediaComment> Comments { get; set; } = [];

    /// <summary>On the sharer's PC: the friends it was shared with (reactions and comments are passed on to them).</summary>
    public List<string> Audience { get; set; } = [];
}

/// <summary>One person's reaction (several emoji per person are fine).</summary>
public sealed class SharedMediaReaction
{
    public string UserId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Emoji { get; set; } = string.Empty;
}

public sealed class SharedMediaComment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AuthorId { get; set; } = string.Empty;
    public string AuthorName { get; set; } = string.Empty;

    /// <summary>The text; times in it ("2:15") play the video from there.</summary>
    public string Text { get; set; } = string.Empty;

    public DateTime PostedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ReceivedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>A message waiting for the relay (sent when the connection is back).</summary>
public sealed class SharedMediaOutboxItem
{
    [BsonId]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string To { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Friend-to-friend message kinds for the shared feed (older SeaRaider versions ignore them).</summary>
public static class MediaShareKinds
{
    public const string Post = "media-post";       // MediaPostPayload, sharer → each friend
    public const string React = "media-react";     // MediaReactPayload, to the sharer, who passes it on
    public const string Comment = "media-comment"; // MediaCommentPayload, to the sharer, who passes it on
    public const string Remove = "media-remove";   // MediaRemovePayload: a post (sharer only) or a comment

    public static bool IsMediaKind(string kind) => kind.StartsWith("media-", StringComparison.Ordinal);
}

public sealed record MediaPostPayload(string Id, string Url, string Title, string? ThumbnailUrl, string? Channel, string? Note,
    int? StartSeconds, DateTime PostedAt);

/// <param name="UserId">Who reacted (when the sharer passes it on, someone else than the sender).</param>
public sealed record MediaReactPayload(string PostId, string UserId, string Name, string Emoji, bool On);

public sealed record MediaCommentPayload(string PostId, string Id, string UserId, string Name, string Text, DateTime PostedAt);

/// <param name="CommentId">Null: the whole post.</param>
public sealed record MediaRemovePayload(string PostId, string? CommentId);

/// <summary>The reactions offered on shared posts.</summary>
public static class MediaReactions
{
    public static IReadOnlyList<(string Emoji, string Name)> All { get; } =
    [
        ("🔥", "Fire"),
        ("😂", "LOL"),
        ("😮", "Wow"),
        ("👍", "Like"),
        ("❤️", "Love"),
        ("💀", "Dead"),
    ];

    public static bool IsKnown(string? emoji) => emoji is not null && All.Any(r => r.Emoji == emoji);
}

/// <summary>Video times: "1:23", "1:02:03", "83s", "1m23s", and the start time in a shared link (?t=…).</summary>
public static partial class MediaTime
{
    /// <summary>Longest time accepted (24 h); anything longer is a mistake.</summary>
    public const int MaxSeconds = 24 * 3600;

    [GeneratedRegex(@"^(?:(?<h>\d{1,2}):)?(?<m>\d{1,2}):(?<s>\d{2})$")]
    private static partial Regex ClockRegex();

    [GeneratedRegex(@"^(?:(?<h>\d{1,2})h)?(?:(?<m>\d{1,3})m)?(?:(?<s>\d{1,5})s?)?$", RegexOptions.IgnoreCase)]
    private static partial Regex UnitsRegex();

    [GeneratedRegex(@"(?<![\w:])(?:\d{1,2}:)?\d{1,2}:\d{2}(?![\w:])")]
    private static partial Regex InTextRegex();

    [GeneratedRegex(@"(?:[?&#](?:t|start|time)=)(?<v>[0-9hms:]+)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    /// <summary>Seconds in a typed time, or null when it isn't one (blank is null too).</summary>
    public static int? Parse(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0) return null;

        int? result = null;
        if (ClockRegex().Match(value) is { Success: true } clock)
        {
            var h = clock.Groups["h"].Success ? int.Parse(clock.Groups["h"].Value, CultureInfo.InvariantCulture) : 0;
            var m = int.Parse(clock.Groups["m"].Value, CultureInfo.InvariantCulture);
            var s = int.Parse(clock.Groups["s"].Value, CultureInfo.InvariantCulture);
            if (s < 60 && (!clock.Groups["h"].Success || m < 60)) result = h * 3600 + m * 60 + s;
        }
        else if (UnitsRegex().Match(value) is { Success: true } units && value.Length > 0)
        {
            int Part(string name) => units.Groups[name].Success ? int.Parse(units.Groups[name].Value, CultureInfo.InvariantCulture) : 0;
            result = Part("h") * 3600 + Part("m") * 60 + Part("s");
        }
        return result is > 0 and <= MaxSeconds ? result : null;
    }

    /// <summary>"1:23" or "1:02:03".</summary>
    public static string Format(int seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, MaxSeconds));
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>The start time written into a link (YouTube ?t=83 / &amp;t=1m23s, Twitch ?t=1h2m3s, Vimeo #t=83s).</summary>
    public static int? FromUrl(string? url) =>
        url is not null && UrlRegex().Match(url) is { Success: true } m ? Parse(m.Groups["v"].Value) : null;

    /// <summary>The clock times written in a comment ("that jump at 2:15"), in order, without repeats.</summary>
    public static IReadOnlyList<int> FindInText(string? text)
    {
        var found = new List<int>();
        if (string.IsNullOrEmpty(text)) return found;
        foreach (Match match in InTextRegex().Matches(text))
            if (Parse(match.Value) is { } seconds && !found.Contains(seconds)) found.Add(seconds);
        return found;
    }
}
