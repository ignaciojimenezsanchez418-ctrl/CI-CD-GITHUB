using FinDash.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FinDash.Api.Data;

public sealed class FinDashDbContext(DbContextOptions<FinDashDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<WatchlistItem> WatchlistItems => Set<WatchlistItem>();
    public DbSet<TransactionEntity> Transactions => Set<TransactionEntity>();
    public DbSet<PriceAlert> PriceAlerts => Set<PriceAlert>();
    public DbSet<AlertEvent> AlertEvents => Set<AlertEvent>();
    public DbSet<PriceSnapshot> PriceSnapshots => Set<PriceSnapshot>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // D9: montos exactos. En SQL Server queda decimal(18,8).
        builder.Properties<decimal>().HavePrecision(18, 8);

        // Enums como texto: la base se lee mejor y no se rompe si se reordenan.
        builder.Properties<Currency>().HaveConversion<string>().HaveMaxLength(8);
        builder.Properties<AssetKind>().HaveConversion<string>().HaveMaxLength(16);
        builder.Properties<TxType>().HaveConversion<string>().HaveMaxLength(8);
        builder.Properties<AlertCondition>().HaveConversion<string>().HaveMaxLength(8);
        builder.Properties<AlertState>().HaveConversion<string>().HaveMaxLength(16);

        // SQLite no sabe ordenar ni comparar DateTimeOffset; se guarda como número.
        // (Si se cambia a SQL Server, quitar esta línea y crear una migración nueva.)
        builder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.Property(u => u.Email).HasMaxLength(256);
            e.HasIndex(u => u.Email).IsUnique();
            e.Property(u => u.DisplayName).HasMaxLength(100);
        });

        b.Entity<Asset>(e =>
        {
            e.Property(a => a.Id).HasMaxLength(64);
            e.Property(a => a.Symbol).HasMaxLength(16);
            e.Property(a => a.Name).HasMaxLength(100);
            e.Property(a => a.Source).HasMaxLength(16);
            e.HasData(SeedData.Assets);
        });

        b.Entity<WatchlistItem>(e =>
        {
            e.HasKey(w => new { w.UserId, w.AssetId });
            e.HasOne<User>().WithMany().HasForeignKey(w => w.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Asset>().WithMany().HasForeignKey(w => w.AssetId);
        });

        b.Entity<TransactionEntity>(e =>
        {
            e.ToTable("Transactions");
            e.HasIndex(t => new { t.UserId, t.AssetId });
            e.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Asset>().WithMany().HasForeignKey(t => t.AssetId);
        });

        b.Entity<PriceAlert>(e =>
        {
            e.HasIndex(a => a.UserId);
            e.HasOne<User>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Asset>().WithMany().HasForeignKey(a => a.AssetId);
        });

        b.Entity<AlertEvent>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.DeliveredAt });
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<PriceAlert>().WithMany().HasForeignKey(x => x.AlertId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<PriceSnapshot>(e =>
        {
            e.HasIndex(s => new { s.AssetId, s.Timestamp });
            e.HasOne<Asset>().WithMany().HasForeignKey(s => s.AssetId);
        });
    }
}

/// <summary>Lo usa "dotnet ef" para crear migraciones sin arrancar la API.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<FinDashDbContext>
{
    public FinDashDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<FinDashDbContext>().UseSqlite("Data Source=findash.db").Options);
}
