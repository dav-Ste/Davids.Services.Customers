using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Davids.Services.Customers.Infrastructure.Persistence;

public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<CustomersDbContext>
{
    public CustomersDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__customersdb")
            ?? "Server=localhost;Database=davids_customers;Integrated Security=true;TrustServerCertificate=true";
        var options = new DbContextOptionsBuilder<CustomersDbContext>().UseSqlServer(connection).Options;
        return new CustomersDbContext(options);
    }
}
