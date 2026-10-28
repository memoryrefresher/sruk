namespace SeaRaider.Core.Iptv;

/// <summary>What a channel is about, for the category chips on Live TV.</summary>
public enum IptvCategory
{
    Other,
    Sports,
    News,
    Movies,
    Kids,
    Music,
}

/// <summary>One channel (or one title) of an M3U playlist.</summary>
/// <param name="Number">Its place in the playlist, from 1: the number typed on the remote-style keypad.</param>
/// <param name="Name">Its name.</param>
/// <param name="Group">The playlist's own group ("group-title"), or empty.</param>
/// <param name="LogoUrl">Its logo ("tvg-logo"), or null.</param>
/// <param name="StreamUrl">The address exactly as the playlist has it.</param>
/// <param name="TvgId">The guide id ("tvg-id"), or empty.</param>
/// <param name="Category">Sorted from the group and the name.</param>
public sealed record IptvChannel(int Number, string Name, string Group, string? LogoUrl, string StreamUrl, string TvgId, IptvCategory Category)
{
    /// <summary>Stable across reloads of the playlist (the address can carry a changing token, the name doesn't).</summary>
    public string Key => $"{TvgId}|{Name}";
}
