using Davids.Services.Customers.Application.Services;
using Davids.Services.Customers.Core.Entities;
using Davids.Services.Customers.Infrastructure;
using Davids.Services.Customers.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Davids.Services.Customers.Tests;

[Trait("Category", "Unit")]
public sealed class ArchitectureTests
{
    [Fact]
    public void CoreLayerHasZeroOuterLayerOrInfrastructureFrameworkDependencies()
    {
        var coreRefs = typeof(Customer).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(coreRefs, n => n.StartsWith("Davids.", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(coreRefs, n => n.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(coreRefs, n => n.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(coreRefs, n => n.StartsWith("RabbitMQ", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ApplicationLayerDependsOnlyOnCoreAndNeverOnInfrastructureOrApi()
    {
        var appRefs = typeof(CustomersService).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

        Assert.Contains("Davids.Services.Customers.Core", appRefs, StringComparer.Ordinal);
        Assert.DoesNotContain("Davids.Services.Customers.Infrastructure", appRefs, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Davids.Services.Customers.Api", appRefs, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Davids.ServiceDefaults", appRefs, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(appRefs, n => n.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(appRefs, n => n.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(appRefs, n => n.StartsWith("RabbitMQ", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InfrastructureAndApiLayersFollowInwardDependenciesAndNeverCoupleToSiblingServices()
    {
        var infraRefs = typeof(CustomersDbContext).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain("Davids.Services.Customers.Api", infraRefs, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(infraRefs, n => n.StartsWith("Davids.Services.Identity", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(infraRefs, n => n.StartsWith("Davids.Services.AdminIdentity", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(infraRefs, n => n.StartsWith("Davids.APIGateway", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(infraRefs, n => n.StartsWith("Davids.Frontend", StringComparison.OrdinalIgnoreCase));

        var apiRefs = typeof(Program).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(apiRefs, n => n.StartsWith("Davids.Services.Identity", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(apiRefs, n => n.StartsWith("Davids.Services.AdminIdentity", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(apiRefs, n => n.StartsWith("Davids.APIGateway", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(apiRefs, n => n.StartsWith("Davids.Frontend", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EfCoreRuntimeModelMatchesMigrationsSnapshotWithoutPendingChanges()
    {
        using var designTimeDb = new DesignTimeFactory().CreateDbContext([]);
        Assert.False(
            designTimeDb.Database.HasPendingModelChanges(),
            "Design-time CustomersDbContext has pending EF Core model changes relative to CustomersDbContextModelSnapshot.");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:customersdb"] = "Server=localhost;Database=davids_customers;Integrated Security=true;TrustServerCertificate=true",
                ["CustomerOidc:Authority"] = "https://localhost:7504",
                ["AdminOidc:Authority"] = "https://localhost:17325",
                ["Messaging:Enabled"] = "false"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddCustomersInfrastructure(configuration);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var runtimeDb = scope.ServiceProvider.GetRequiredService<CustomersDbContext>();
        Assert.False(
            runtimeDb.Database.HasPendingModelChanges(),
            "Runtime DI CustomersDbContext has pending EF Core model changes relative to CustomersDbContextModelSnapshot.");
    }
}
