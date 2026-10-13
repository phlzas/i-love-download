using LoveDownload.Api.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace LoveDownload.Api.Data;

/// <summary>
/// EF Core context for the optional auth layer. Inherits the Identity schema
/// (users, roles, claims...) and adds the auth-support tables. It is deliberately
/// separate from <see cref="AppDbContext"/> and lives in its own SQLite file
/// (data/auth.db), so enabling auth never touches lovedownload.db.
/// </summary>
public class AuthDbContext : IdentityDbContext<AppUser>
{
    public AuthDbContext(DbContextOptions<AuthDbContext> options)
        : base(options)
    {
    }

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<RevokedAccessToken> RevokedAccessTokens => Set<RevokedAccessToken>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<RefreshToken>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Token).IsUnique();
            entity.Property(x => x.Token).IsRequired();
            entity.HasOne(x => x.User)
                  .WithMany()
                  .HasForeignKey(x => x.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<RevokedAccessToken>(entity =>
        {
            entity.HasKey(x => x.Jti);
            entity.Property(x => x.Jti).IsRequired();
        });
    }
}
