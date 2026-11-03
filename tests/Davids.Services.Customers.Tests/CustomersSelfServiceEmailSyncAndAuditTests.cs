using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Davids.Services.Customers.Application.Commands;
using Davids.Services.Customers.Application.DTO;
using Davids.Services.Customers.Application.Events;
using Davids.Services.Customers.Application.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Davids.Services.Customers.Tests;

[Trait("Category", "Integration")]
public sealed class CustomersSelfServiceEmailSyncAndAuditTests
{
    [Fact]
    public async Task CustomerSelfServiceWorkforceRbacEmailSyncAndProfileChangeAuditsWorkEndToEnd()
    {
        using var factory = new CustomersFactory();
        using var client = factory.CreateCustomersClient();

        var customerId = Guid.NewGuid();
        var otherCustomerId = Guid.NewGuid();
        var supportId = Guid.NewGuid();
        var auditorId = Guid.NewGuid();

        await factory.SeedIncompleteCustomerAsync(customerId, "alice.initial@example.com");
        await factory.SeedIncompleteCustomerAsync(otherCustomerId, "bob@example.com");

        // 1. Customer reads /customers/me and completes + updates own profile via PUT /customers/me.
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CustomersFactory.CreateCustomerToken(customerId));
        var initialMe = await client.GetFromJsonAsync<CustomerDetailsDto>("/customers/me");
        Assert.Equal("incomplete", initialMe!.State);

        var completedMeResp = await client.PutAsJsonAsync("/customers/me", new UpdateMyCustomerProfile(
            "Alice Mearns",
            "12 Ayr Road, Newton Mearns"));
        Assert.Equal(HttpStatusCode.OK, completedMeResp.StatusCode);
        var completedMe = await completedMeResp.Content.ReadFromJsonAsync<CustomerDetailsDto>();
        Assert.Equal("valid", completedMe!.State);
        Assert.Equal("Alice Mearns", completedMe.FullName);

        var updatedMeResp = await client.PutAsJsonAsync("/customers/me", new UpdateMyCustomerProfile(
            "Alice Mearns-Robertson",
            "99 Mearns Cross, Glasgow"));
        Assert.Equal(HttpStatusCode.OK, updatedMeResp.StatusCode);

        // Customer can inspect own /customers/{id}/change-audits, but NOT another customer's or global /customers/change-audits.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/customers/{customerId}/change-audits")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/customers/{otherCustomerId}/change-audits")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/customers/change-audits")).StatusCode);

        // 2. Identity outbox syncs email change via SyncEmailFromIdentityAsync -> updates email & records identity-sync audit.
        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<ICustomersService>();
            await svc.SyncEmailFromIdentityAsync(new EmailChanged(
                customerId,
                "alice.initial@example.com",
                "alice.synced@example.com"));
        }

        // 3. Support employee can read /customers, update profile, and transition state; Auditor is read-only.
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CustomersFactory.CreateAdminToken(auditorId, "auditor"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/customers")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/customers/{customerId}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PutAsJsonAsync($"/customers/{customerId}", new
            {
                fullName = "Blocked By Auditor",
                address = "Blocked Street"
            })).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PutAsync($"/customers/{customerId}/state/locked", content: null)).StatusCode);

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CustomersFactory.CreateAdminToken(supportId, "support"));
        var supportEditResp = await client.PutAsJsonAsync($"/customers/{customerId}", new
        {
            fullName = "Alice Mearns-Robertson",
            address = "101 Support Verified Way, Glasgow",
            state = "suspicious"
        });
        Assert.Equal(HttpStatusCode.OK, supportEditResp.StatusCode);

        var supportDetails = await supportEditResp.Content.ReadFromJsonAsync<CustomerDetailsDto>();
        Assert.Equal("alice.synced@example.com", supportDetails!.Email);
        Assert.Equal("101 Support Verified Way, Glasgow", supportDetails.Address);
        Assert.Equal("suspicious", supportDetails.State);

        // 4. Auditor inspects full timeline in /customers/change-audits.
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CustomersFactory.CreateAdminToken(auditorId, "auditor"));
        var audits = await client.GetFromJsonAsync<List<CustomerProfileChangeAuditDto>>(
            $"/customers/change-audits?targetCustomerId={customerId}");
        Assert.NotNull(audits);
        Assert.Contains(audits!, a => a.Action == "registration_completed" && a.ActorAuthority == "customer");
        Assert.Contains(audits!, a =>
            a.Action == "profile_updated" &&
            a.ActorAuthority == "customer" &&
            a.ActorSub == customerId.ToString("D"));
        Assert.Contains(audits!, a =>
            a.Action == "email_synced_from_identity" &&
            a.ActorAuthority == "identity-sync" &&
            a.Changes.Any(c => c.Field == "email" && c.NewValue == "alice.synced@example.com"));
        Assert.Contains(audits!, a =>
            a.Action == "profile_updated" &&
            a.ActorAuthority == "employee" &&
            a.ActorSub == supportId.ToString("D") &&
            a.ActorRole == "support" &&
            a.Changes.Any(c => c.Field == "state" && c.NewValue == "suspicious"));
    }
}
