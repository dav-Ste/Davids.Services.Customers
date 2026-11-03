# Davids.Services.Customers

| Pull Request CI | CodeQL SAST | Platform Architecture & Orchestration |
|---|---|---|
| [![Pull Request CI — Security Gate](https://github.com/dav-Ste/Davids.Services.Customers/actions/workflows/pull-request-ci.yml/badge.svg?branch=main)](https://github.com/dav-Ste/Davids.Services.Customers/actions/workflows/pull-request-ci.yml) | [![Pull Request CI — CodeQL SAST](https://github.com/dav-Ste/Davids.Services.Customers/actions/workflows/codeql.yml/badge.svg?branch=main)](https://github.com/dav-Ste/Davids.Services.Customers/actions/workflows/codeql.yml) | [**`Davids` — Architecture & Security Diagram**](https://github.com/dav-Ste/Davids#architecture-project-relationships--security-posture) |

`Davids.Services.Customers` is the standalone `.NET 10` microservice repository (`Davids.Services.Customers.slnx`, default local port `:5002`) managing customer profiles, lifecycle state transitions, sign-up provisioning, and the 20-order VIP threshold using EF Core SQL Server (`customersdb`), direct ASP.NET Core Minimal APIs, RabbitMQ transactional inbox/outbox workers, and zero-shared-secret OIDC JWKS validation.

## Projects

- `src/Davids.Services.Customers.Core`: Customer aggregate, lifecycle states, completed-order relationship, and domain invariants.
- `src/Davids.Services.Customers.Application`: Backend application layer containing DTOs, use-case services, integration event contracts, and outbox abstractions.
- `src/Davids.Services.Customers.Infrastructure`: SQL Server (`customersdb`) repository/migrations, dual-authority OIDC JWKS validation settings, Vault KV-v2 loader (`secret/data/davids/customers`), and RabbitMQ inbox/outbox worker.
- `src/Davids.Services.Customers.Api`: Minimal REST endpoints, dual-authority `RS256` OIDC JWT bearer validation, and resource-owner vs. `AdminOnly` authorization policies.
- `tests/Davids.Services.Customers.Tests`: Unit, Clean Architecture boundary, and in-process SQLite + `RS256` JWKS integration tests.

## Request routing and zero-secret OIDC authentication

```text
Client -> YARP (:17320) /customers/... -> Customers API (:5002) /customers/...
```

YARP leaves `/customers` unchanged and routes directly to the `customers` cluster (populated via Aspire service endpoints in local development or Kubernetes/Istio `Service` DNS in cluster deployments).

[`Davids.APIGateway`](https://github.com/dav-Ste/Davids.APIGateway) validates tokens at the edge, and `Davids.Services.Customers` independently validates **two cryptographically isolated `RS256` OIDC authorities** via `/.well-known/openid-configuration` & `/.well-known/jwks` public-key discovery (**zero signing keys stored in `Customers` or `secret/data/davids/customers`**):

- **`CustomerBearer`** (`CustomerOidc`): Validates `RS256` tokens from [`Davids.Services.Identity`](https://github.com/dav-Ste/Davids.Services.Identity) (`Audience: davids-services`), restricted strictly to `role == "user"`.
- **`AdminBearer`** (`AdminOidc`): Validates `RS256` tokens from [`Davids.Services.AdminIdentity`](https://github.com/dav-Ste/Davids.Services.AdminIdentity) (`Audience: davids-admin`), restricted strictly to `role == "admin"`.

Access is enforced at both the Gateway and the Customers API:

- `GET /customers` (including `?search=&state=&vip=`): `AdminBearer` (`admin`) role only.
- `GET /customers/{customerId}` and `GET /customers/{customerId}/state`: The customer whose GUID matches `CustomerBearer` `sub`, or a verified `AdminBearer` administrator.
- `POST /customers`: The matching `CustomerBearer` customer, or a verified `AdminBearer` administrator, completing an existing `incomplete` profile.
- `PUT /customers/{customerId}`: `AdminBearer` (`admin`) role only; updates full name, address, optional unique email, and lifecycle state.
- `PUT /customers/{customerId}/state/{state}`: `AdminBearer` (`admin`) role only.

Customer accounts (`identitydb`), administrator accounts (`adminidentitydb`), and customer profiles (`customersdb`) are stored in three physically separate SQL Server databases and never co-located.

## HTTP contract

All paths below are called through the YARP gateway (`:17320`) or directly on `:5002`. JSON uses camel-case fields; `fullname` is accepted case-insensitively on profile completion:

| Method | Path | Access | Result |
|---|---|---|---|
| `GET` | `/customers/me` | `CustomerBearer` (`user`) | Self-service profile details for the authenticated customer |
| `PUT` | `/customers/me` | `CustomerBearer` (`user`) | Self-service update of `fullName` and `address` (cannot alter `isVip` or `state`); records `CustomerProfileChangeAuditLogs` |
| `GET` | `/customers?search=&state=&vip=` | Workforce (`admin`/`support`/`auditor`) | Array of `{ id, email, fullName, address, isVip, state, createdAt, completedOrdersCount }` |
| `GET` | `/customers/{customerId}` | Owner or Workforce (`admin`/`support`/`auditor`) | Details including email, full name, address, state, VIP flag, `createdAt`, and completed order IDs |
| `GET` | `/customers/{customerId}/state` | Owner or Workforce (`admin`/`support`/`auditor`) | `{ id, state }` |
| `POST` | `/customers` | Owner or Workforce (`admin`/`support`) | Completes an existing incomplete profile (`customerId`, `fullName`, `address`); `201 Created` |
| `PUT` | `/customers/{customerId}` | Workforce (`admin`/`support`) | Updates `fullName`, `address`, optional `email` (unique), and optional `state` + transactional `CustomerProfileChangeAuditLogs` |
| `PUT` | `/customers/{customerId}/state/{state}` | Workforce (`admin`/`support`) | Changes lifecycle state + transactional `CustomerProfileChangeAuditLogs`; `204 No Content` |
| `GET` | `/customers/change-audits` | Workforce (`admin`/`support`/`auditor`) | Recent customer profile change-audit trail |
| `GET` | `/customers/{customerId}/change-audits` | Workforce (`admin`/`support`/`auditor`) | Change-audit trail for a single customer |
| `GET` | `/health`, `/alive` | Probe | Readiness and liveness endpoints wired via `Davids.ServiceDefaults` |

## Event integration

After a customer registers via `POST /identity/sign-up`, `Davids.Services.Identity`'s transactional outbox publishes `signed_up` on the `identity` topic exchange. `Davids.Services.Customers` consumes `identity.signed_up` from durable queue `customers-service/identity.signed_up` and creates an `incomplete` customer record in one SQL transaction alongside its `Inbox` entry.

`Davids.Services.Customers` also consumes `orders.order_completed` from the `orders` exchange (promoting customers to VIP after 20 distinct completed orders) and publishes `customer_created`, `customer_updated`, `customer_state_changed`, and `customer_became_vip` through its SQL `Outbox` to the durable `customers` exchange.

## Build, test & container image

Clone [`Davids.Shared`](https://github.com/dav-Ste/Davids.Shared) (`../Davids.Shared`) alongside `Davids.Services.Customers` (or pass `/p:UseSharedPackageReference=true` to consume `Davids.ServiceDefaults` as a NuGet package without cloning `Davids.Shared`):

```powershell
dotnet restore Davids.Services.Customers.slnx --locked-mode
dotnet build Davids.Services.Customers.slnx --no-restore -c Release -warnaserror
dotnet test Davids.Services.Customers.slnx --no-build --no-restore -c Release
```

Build the hardened multi-stage container image (`USER 10001:10001`, `EXPOSE 8080`) from the parent workspace directory:

```powershell
docker build -f Davids.Services.Customers/Dockerfile -t davids-customers:local ..
```

## Related repositories

- [`Davids`](https://github.com/dav-Ste/Davids) — `.NET 10` Aspire AppHost, Vault bootstrap, Kubernetes/Istio manifests, architecture diagram & lifecycle security suite
- [`Davids.Shared`](https://github.com/dav-Ste/Davids.Shared) — `Davids.ServiceDefaults` shared observability, Data Protection, and resilience defaults
- [`Davids.APIGateway`](https://github.com/dav-Ste/Davids.APIGateway) — YARP `2.3.0` edge reverse proxy & confidential Admin OIDC BFF
- [`Davids.Services.Identity`](https://github.com/dav-Ste/Davids.Services.Identity) — Customer OpenIddict `RS256` OIDC Provider (`identitydb`)
- [`Davids.Services.AdminIdentity`](https://github.com/dav-Ste/Davids.Services.AdminIdentity) — Administrator OpenIddict `RS256` OIDC Provider (`adminidentitydb`)
- [`Davids.Frontend.Admin`](https://github.com/dav-Ste/Davids.Frontend.Admin) — Passwordless Workforce RBAC customer, IAM & audit web UI
- [`Davids.Frontend.Web`](https://github.com/dav-Ste/Davids.Frontend.Web) — Customer Self-Service Web Portal (`CustomerBffCookie` PKCE `S256` BFF)
