using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using UrlShortener.Api.Data;
using UrlShortener.Api.Models;

namespace UrlShortener.Api.Services;

public sealed partial class UrlShortenerService(UrlDbContext database)
{
    private const int GeneratedCodeLength = 8;
    private const int MaximumCodeAttempts = 5;
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public async Task<ShortUrl> CreateAsync(
        Uri destination,
        string? customCode,
        DateTimeOffset? expiresAt,
        CancellationToken cancellationToken)
    {
        if (customCode is not null && !CustomCodePattern().IsMatch(customCode))
        {
            throw new InvalidShortCodeException(
                "Use 3-32 letters, digits, hyphens, or underscores for a custom code.");
        }

        if (customCode is not null)
        {
            if (await database.ShortUrls.AnyAsync(url => url.Code == customCode, cancellationToken))
            {
                throw new ShortCodeConflictException();
            }

            var customUrl = NewShortUrl(customCode, destination, expiresAt);
            database.ShortUrls.Add(customUrl);
            try
            {
                await database.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
            {
                throw new ShortCodeConflictException();
            }

            return customUrl;
        }

        for (var attempt = 0; attempt < MaximumCodeAttempts; attempt++)
        {
            var generatedUrl = NewShortUrl(GenerateCode(), destination, expiresAt);
            database.ShortUrls.Add(generatedUrl);
            try
            {
                await database.SaveChangesAsync(cancellationToken);
                return generatedUrl;
            }
            catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
            {
                database.Entry(generatedUrl).State = EntityState.Detached;
            }
        }

        throw new InvalidOperationException("Could not allocate a unique short code after bounded retries.");
    }

    public Task<ShortUrl?> FindAsync(string code, CancellationToken cancellationToken) =>
        database.ShortUrls.SingleOrDefaultAsync(url => url.Code == code, cancellationToken);

    public async Task RecordClickAsync(ShortUrl url, CancellationToken cancellationToken)
    {
        database.ClickEvents.Add(new ClickEvent
        {
            ShortUrlId = url.Id,
            ClickedAt = DateTime.UtcNow,
            ReferrerHost = "direct"
        });
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<UrlAnalytics?> GetAnalyticsAsync(string code, CancellationToken cancellationToken)
    {
        var url = await database.ShortUrls
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Code == code, cancellationToken);
        if (url is null)
        {
            return null;
        }

        var clicks = database.ClickEvents.Where(click => click.ShortUrlId == url.Id);
        var totalClicks = await clicks.CountAsync(cancellationToken);
        var clicksByDay = await database.Database.SqlQuery<DailyClickCount>($"""
            SELECT date(ClickedAt) AS Date, COUNT(*) AS Count
            FROM ClickEvents
            WHERE ShortUrlId = {url.Id}
            GROUP BY date(ClickedAt)
            ORDER BY Date DESC
            LIMIT 30
            """)
            .ToListAsync(cancellationToken);

        return new UrlAnalytics(url.Code, url.DestinationUrl, totalClicks, clicksByDay);
    }

    private static ShortUrl NewShortUrl(string code, Uri destination, DateTimeOffset? expiresAt) => new()
    {
        Code = code,
        DestinationUrl = destination.AbsoluteUri,
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = expiresAt
    };

    private static string GenerateCode()
    {
        var characters = new char[GeneratedCodeLength];
        for (var index = 0; index < characters.Length; index++)
        {
            characters[index] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }
        return new string(characters);
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteErrorCode: 19 };

    [GeneratedRegex("^[A-Za-z0-9_-]{3,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex CustomCodePattern();
}

public sealed record UrlAnalytics(
    string Code,
    string DestinationUrl,
    int TotalClicks,
    IReadOnlyList<DailyClickCount> ClicksByDay);

public sealed record DailyClickCount(DateTime Date, int Count);

public sealed class InvalidShortCodeException(string message) : Exception(message);

public sealed class ShortCodeConflictException() : Exception;