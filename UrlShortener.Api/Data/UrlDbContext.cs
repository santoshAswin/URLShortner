using Microsoft.EntityFrameworkCore;
using UrlShortener.Api.Models;
using UrlShortener.Api.Orchestration;

namespace UrlShortener.Api.Data;

public sealed class UrlDbContext(DbContextOptions<UrlDbContext> options) : DbContext(options)
{
    public DbSet<ShortUrl> ShortUrls => Set<ShortUrl>();
    public DbSet<ClickEvent> ClickEvents => Set<ClickEvent>();
    public DbSet<OrchestrationRunEntity> OrchestrationRuns => Set<OrchestrationRunEntity>();
    public DbSet<OrchestrationAuditEventEntity> OrchestrationAuditEvents => Set<OrchestrationAuditEventEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ShortUrl>(entity =>
        {
            entity.HasKey(url => url.Id);
            entity.HasIndex(url => url.Code).IsUnique();
            entity.Property(url => url.Code).HasMaxLength(32).IsRequired();
            entity.Property(url => url.DestinationUrl).HasMaxLength(2048).IsRequired();
            entity.HasMany(url => url.Clicks)
                .WithOne(click => click.ShortUrl)
                .HasForeignKey(click => click.ShortUrlId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ClickEvent>(entity =>
        {
            entity.HasKey(click => click.Id);
            entity.HasIndex(click => new { click.ShortUrlId, click.ClickedAt });
            entity.Property(click => click.ReferrerHost).HasMaxLength(253);
        });

        modelBuilder.Entity<OrchestrationRunEntity>(entity =>
        {
            entity.HasKey(run => run.Id);
            entity.Property(run => run.Scenario).HasMaxLength(32).IsRequired();
            entity.Property(run => run.SnapshotJson).IsRequired();
            entity.HasMany(run => run.AuditEvents)
                .WithOne(auditEvent => auditEvent.Run)
                .HasForeignKey(auditEvent => auditEvent.RunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OrchestrationAuditEventEntity>(entity =>
        {
            entity.HasKey(auditEvent => auditEvent.Id);
            entity.HasIndex(auditEvent => new { auditEvent.RunId, auditEvent.Sequence }).IsUnique();
            entity.HasIndex(auditEvent => new { auditEvent.RunId, auditEvent.At });
            entity.Property(auditEvent => auditEvent.Type).HasMaxLength(64).IsRequired();
            entity.Property(auditEvent => auditEvent.StageId).HasMaxLength(64);
            entity.Property(auditEvent => auditEvent.Actor).HasMaxLength(128).IsRequired();
        });
    }
}