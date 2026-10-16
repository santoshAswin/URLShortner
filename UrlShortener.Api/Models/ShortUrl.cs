namespace UrlShortener.Api.Models;

public sealed class ShortUrl
{
    public long Id { get; set; }
    public required string Code { get; set; }
    public required string DestinationUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public List<ClickEvent> Clicks { get; set; } = [];
}