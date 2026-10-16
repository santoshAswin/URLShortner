namespace UrlShortener.Api.Models;

public sealed class ClickEvent
{
    public long Id { get; set; }
    public long ShortUrlId { get; set; }
    public DateTime ClickedAt { get; set; }
    public string ReferrerHost { get; set; } = "direct";

    public ShortUrl ShortUrl { get; set; } = null!;
}