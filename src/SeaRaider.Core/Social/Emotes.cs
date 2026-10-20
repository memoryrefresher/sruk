using System.Text.RegularExpressions;

namespace SeaRaider.Core.Social;

/// <summary>How an emote moves in the chat (an Avalonia animation class of the same name).</summary>
public enum EmoteAnimation
{
    Bounce,
    Wobble,
    Pulse,
    Spin,
    Float,
    Shake,
}

/// <summary>A chat emote: typed as <see cref="Code"/> (":gg:"), shown as an animated glyph.</summary>
public sealed record Emote(string Code, string Glyph, string Name, EmoteAnimation Animation);

/// <summary>A run of chat text: plain text, or one emote.</summary>
public sealed record ChatSegment(string Text, Emote? Emote)
{
    public bool IsEmote => Emote is not null;
}

/// <summary>
/// The emote set and the chat text parser. Emotes travel as plain codes (":gg:") so any client can show them,
/// and render as animated emoji on SeaRaider.
/// </summary>
public static partial class Emotes
{
    public static IReadOnlyList<Emote> All { get; } =
    [
        new(":gg:", "🎮", "Good game", EmoteAnimation.Bounce),
        new(":wave:", "👋", "Wave", EmoteAnimation.Wobble),
        new(":fire:", "🔥", "Fire", EmoteAnimation.Pulse),
        new(":lol:", "😂", "LOL", EmoteAnimation.Shake),
        new(":wow:", "😮", "Wow", EmoteAnimation.Pulse),
        new(":heart:", "❤️", "Heart", EmoteAnimation.Pulse),
        new(":skull:", "💀", "Skull", EmoteAnimation.Wobble),
        new(":trophy:", "🏆", "Trophy", EmoteAnimation.Bounce),
        new(":sword:", "⚔️", "Swords", EmoteAnimation.Shake),
        new(":rage:", "😡", "Rage", EmoteAnimation.Shake),
        new(":cool:", "😎", "Cool", EmoteAnimation.Float),
        new(":rocket:", "🚀", "Rocket", EmoteAnimation.Float),
        new(":thumbs:", "👍", "Thumbs up", EmoteAnimation.Bounce),
        new(":clap:", "👏", "Clap", EmoteAnimation.Pulse),
        new(":think:", "🤔", "Thinking", EmoteAnimation.Wobble),
        new(":party:", "🎉", "Party", EmoteAnimation.Spin),
        new(":sleep:", "😴", "Sleepy", EmoteAnimation.Float),
        new(":ship:", "⛵", "Set sail", EmoteAnimation.Float),
        new(":kraken:", "🐙", "Kraken", EmoteAnimation.Wobble),
        new(":loot:", "💰", "Loot", EmoteAnimation.Bounce),
    ];

    private static readonly Dictionary<string, Emote> ByCode = All.ToDictionary(e => e.Code, StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@":[a-z0-9_]{2,16}:", RegexOptions.IgnoreCase)]
    private static partial Regex CodeRegex();

    public static Emote? Find(string code) => ByCode.GetValueOrDefault(code);

    /// <summary>Splits a message into text and emotes; unknown ":codes:" stay text.</summary>
    public static List<ChatSegment> Parse(string text)
    {
        var segments = new List<ChatSegment>();
        var last = 0;
        foreach (Match match in CodeRegex().Matches(text))
        {
            if (Find(match.Value) is not { } emote) continue;
            if (match.Index > last) segments.Add(new ChatSegment(text[last..match.Index], null));
            segments.Add(new ChatSegment(emote.Glyph, emote));
            last = match.Index + match.Length;
        }
        if (last < text.Length) segments.Add(new ChatSegment(text[last..], null));
        return segments;
    }

    /// <summary>The message is nothing but emotes (shown large).</summary>
    public static bool IsEmoteOnly(IReadOnlyList<ChatSegment> segments) =>
        segments.Count > 0 && segments.All(s => s.IsEmote || string.IsNullOrWhiteSpace(s.Text)) && segments.Any(s => s.IsEmote);
}
