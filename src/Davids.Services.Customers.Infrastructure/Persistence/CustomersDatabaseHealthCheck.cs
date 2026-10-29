using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Davids.Services.Customers.Infrastructure.Persistence;

public sealed class CustomersDatabaseHealthCheck(CustomersDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Customers database is unavailable.");
        }
        catch (OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Health check canceled.");
        }
    }
}
