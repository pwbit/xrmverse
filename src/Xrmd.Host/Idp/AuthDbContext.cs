using Microsoft.EntityFrameworkCore;

namespace Xrmd.Host.Idp;

/// <summary>
/// EF Core store of the local identity provider: the OpenIddict entities plus the credentials and the key ring, all in the
/// <c>auth</c> schema of the organization database. The tables are created by the SQL migration <c>0010_local_idp.sql</c>,
/// not by EF migrations; a test pins the migration to this model.
/// </summary>
public sealed class AuthDbContext(DbContextOptions<AuthDbContext> options) : DbContext(options)
{
    public DbSet<LocalCredential> Credentials => Set<LocalCredential>();
    public DbSet<SigningKey> SigningKeys => Set<SigningKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("auth");
        modelBuilder.UseOpenIddict();

        modelBuilder.Entity<LocalCredential>(e =>
        {
            e.ToTable("credential");
            e.HasKey(c => c.SystemUserId);
            e.Property(c => c.SystemUserId).ValueGeneratedNever();
            e.Property(c => c.UserName).HasMaxLength(256).IsRequired();
            e.Property(c => c.PasswordHash).HasMaxLength(512).IsRequired();
            e.Property(c => c.TotpKey).HasMaxLength(64);
            e.HasIndex(c => c.UserName).IsUnique();
        });

        modelBuilder.Entity<SigningKey>(e =>
        {
            e.ToTable("signing_key");
            e.HasKey(k => k.Id);
            e.Property(k => k.Id).ValueGeneratedNever();
            e.Property(k => k.Use).HasMaxLength(3).IsRequired().IsUnicode(false);
            e.Property(k => k.Pfx).IsRequired();
            e.HasIndex(k => k.Use).IsUnique();
        });

        modelBuilder.Entity<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreApplication>().ToTable("application");
        modelBuilder.Entity<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreAuthorization>().ToTable("authorization");
        modelBuilder.Entity<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreScope>().ToTable("scope");
        modelBuilder.Entity<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreToken>().ToTable("token");
    }
}
