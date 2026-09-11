using ElectronNet.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ElectronNet.Features.SteamCache.Persistence;

internal sealed class SteamResourceCacheConfiguration : IEntityTypeConfiguration<SteamResourceCacheEntry>
{
    public void Configure(EntityTypeBuilder<SteamResourceCacheEntry> builder)
    {
        builder.ToTable("steam_resource_cache");
        builder.HasKey(entry => entry.Id);
        builder.HasIndex(entry => new
        {
            entry.ResourceKind,
            entry.ScopeId,
            entry.ResourceId,
            entry.Language,
            entry.Variant,
            entry.SchemaVersion
        }, "steam_resource_cache_key_idx").IsUnique();
        builder.HasIndex(entry => entry.RetainUntil, "steam_resource_cache_retain_until_idx");

        builder.Property(entry => entry.Id)
            .HasColumnName("id").HasColumnType(nameof(ESqliteTypeName.INTEGER)).ValueGeneratedOnAdd();
        builder.Property(entry => entry.ResourceKind)
            .HasColumnName("resource_kind").HasColumnType(nameof(ESqliteTypeName.TEXT)).HasMaxLength(64).IsRequired();
        builder.Property(entry => entry.ScopeId)
            .HasColumnName("scope_id").HasColumnType(nameof(ESqliteTypeName.TEXT)).HasMaxLength(128).IsRequired();
        builder.Property(entry => entry.ResourceId)
            .HasColumnName("resource_id").HasColumnType(nameof(ESqliteTypeName.TEXT)).HasMaxLength(128).IsRequired();
        builder.Property(entry => entry.Language)
            .HasColumnName("language").HasColumnType(nameof(ESqliteTypeName.TEXT)).HasMaxLength(32).HasDefaultValue("").IsRequired();
        builder.Property(entry => entry.Variant)
            .HasColumnName("variant").HasColumnType(nameof(ESqliteTypeName.TEXT)).HasMaxLength(64).HasDefaultValue("").IsRequired();
        builder.Property(entry => entry.SchemaVersion)
            .HasColumnName("schema_version").HasColumnType(nameof(ESqliteTypeName.INTEGER)).IsRequired();
        builder.Property(entry => entry.PayloadFormat)
            .HasColumnName("payload_format").HasColumnType(nameof(ESqliteTypeName.TEXT)).HasMaxLength(32).IsRequired();
        builder.Property(entry => entry.Payload)
            .HasColumnName("payload").HasColumnType(nameof(ESqliteTypeName.TEXT)).HasMaxLength(int.MaxValue).IsRequired();
        builder.Property(entry => entry.Source)
            .HasColumnName("source").HasColumnType(nameof(ESqliteTypeName.TEXT)).HasMaxLength(32).IsRequired();
        builder.Property(entry => entry.ETag)
            .HasColumnName("etag").HasColumnType(nameof(ESqliteTypeName.TEXT)).HasMaxLength(512);
        builder.Property(entry => entry.ContentHash)
            .HasColumnName("content_hash").HasColumnType(nameof(ESqliteTypeName.TEXT)).HasMaxLength(128);
        builder.Property(entry => entry.FetchedAt)
            .HasColumnName("fetched_at").HasColumnType(nameof(ESqliteTypeName.INTEGER)).IsRequired();
        builder.Property(entry => entry.RefreshAfter)
            .HasColumnName("refresh_after").HasColumnType(nameof(ESqliteTypeName.INTEGER)).IsRequired();
        builder.Property(entry => entry.RetainUntil)
            .HasColumnName("retain_until").HasColumnType(nameof(ESqliteTypeName.INTEGER));
        builder.Property(entry => entry.LastAccessedAt)
            .HasColumnName("last_accessed_at").HasColumnType(nameof(ESqliteTypeName.INTEGER)).IsRequired();
    }
}
