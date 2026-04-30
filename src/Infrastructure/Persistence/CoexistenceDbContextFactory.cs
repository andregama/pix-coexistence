using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ConvivenciaPix.Infrastructure.Persistence;

/// <summary>
/// Used only by EF Core CLI tools (dotnet ef migrations add).
/// Not used at runtime — connection string is supplied by the host's IConfiguration.
/// </summary>
public sealed class CoexistenceDbContextFactory : IDesignTimeDbContextFactory<CoexistenceDbContext>
{
    public CoexistenceDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CoexistenceDbContext>()
            .UseSqlServer("Server=localhost,1433;Database=DB_COEXISTENCE;User Id=sa;Password=Dev@Strong123;TrustServerCertificate=True;")
            .Options;

        return new CoexistenceDbContext(options);
    }
}
