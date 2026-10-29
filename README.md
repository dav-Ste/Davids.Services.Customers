# Davids Customers API

A .NET 10 microservice managing customer profiles, lifecycle state transitions, sign-up provisioning, and the 20-order VIP threshold using EF Core SQL Server (`customersdb`), direct ASP.NET Core Minimal APIs, RabbitMQ inbox/outbox workers, and zero-shared-secret OIDC JWKS validation.

## Projects

- `Davids.Services.Customers.Core`: customer entity, states, completed-order relationship, and domain errors.
- `Davids.Services.Customers.Application`: DTOs, use-case service, plain integration contracts, and outbox abstraction.
- `Davids.Services.Customers.Infrastructure`: SQL Server repository/migrations, OIDC authority validation settings, Vault KV-v2 loader, and RabbitMQ inbox/outbox worker.
- `Davids.Services.Customers.Api`: minimal REST endpoints, dual-authority RS256 OIDC JWT bearer validation, and authorization policies.
- `Davids.Services.Customers.Tests`: in-process HTTP and infrastructure tests using SQLite and synthetic/JWKS RS256 authorities.

## Request routing and zero-secret OIDC authentication

```text
Client -> YARP /customers/... -> Customers API /customers/...
```

YARP leaves `/customers` unchanged and routes directly to the `customers` cluster (populated via Aspire service endpoints in local development or Kubernetes/Istio `Service` DNS in cluster deployments).

The API Gateway validates tokens at the edge, and the Customers API independently validates **two cryptographically isolated RS256 OIDC authorities** via `/.well-known/openid-configuration` & `/.well-known/jwks` public-key discovery (**zero signing keys stored in `Customers` or `secret/data/davids/customers`**):

- **`CustomerBearer`** (`CustomerOidc`): validates RS256 tokens from `Davids.Services.Identity` (`Audience: davids-services`), restricted strictly to `role == "user"`.
- **`AdminBearer`** (`AdminOidc`): validates RS256 tokens from `Davids.Services.AdminIdentity` (`Audience: davids-admin`), restricted strictly to `role == "admin"`.

Access is enforced at both the Gateway and the Customers API:

- `GET /customers` (including `?search=&state=&vip=`): `AdminBearer` (`admin`) role only.
- `GET /customers/{customerId}` and `/customers/{customerId}/state`: the customer whose GUID is in `CustomerBearer` `sub`, or a verified `AdminBearer` administrator.
- `POST /customers`: the matching `CustomerBearer` customer, or a verified `AdminBearer` administrator, can complete registration.
- `PUT /customers/{customerId}`: `AdminBearer` (`admin`) role only; updates full name, email, address, and lifecycle state.
- `PUT /customers/{customerId}/state/{state}`: `AdminBearer` (`admin`) role only.

Customer accounts (`identitydb`), administrator accounts (`adminidentitydb` in `Davids.Services.AdminIdentity`), and customer profiles (`customersdb`) are stored in separate SQL Server databases and never co-located.

## HTTP contract

All paths below are called through the YARP gateway by prefixing the path with the gateway's endpoint URL. JSON uses camel-case fields; `fullname` is accepted case-insensitively on profile completion.

| Method | Path | Access | Result |
|---|---|---|---|
| GET | `/customers?search=&state=&vip=` | administrator | Array of `{ id, email, fullName, address, isVip, state, createdAt, completedOrdersCount }` |
| GET | `/customers/{customerId}` | owner or administrator | Details including email, full name, address, state, VIP flag, `createdAt`, and completed order IDs |
| GET | `/customers/{customerId}/state` | owner or administrator | `{ id, state }` |
| POST | `/customers` | owner or administrator | Completes an existing incomplete profile; `201 Created`, `Location: /customers/{customerId}` |
| PUT | `/customers/{customerId}` | administrator | Updates `fullName`, `address`, optional `email` (unique), and optional `state`; `200 OK` with updated `CustomerDetailsDto` |
| PUT | `/customers/{customerId}/state/{state}` | administrator | Changes state; `204 No Content` |

## Event integration

After a customer registers via `POST /sign-up`, `Davids.Services.Identity`'s transactional outbox publishes `signed_up` on the `identity` topic exchange. Customers consumes `identity.signed_up` from durable queue `customers-service/identity.signed_up` and creates an `incomplete` customer record in one SQL transaction alongside its `Inbox` entry.

Customers also consumes `orders.order_completed` from the `orders` exchange (promoting customers to VIP after 20 distinct completed orders) and publishes `customer_created`, `customer_updated`, `customer_state_changed`, and `customer_became_vip` through its SQL `Outbox` to the durable `customers` exchange.

## Standalone Build & Test

Clone the sibling `Davids.Shared` repository (`../Davids.Shared`) alongside `Davids.Services.Customers`:

```powershell
dotnet restore Davids.Services.Customers.slnx --locked-mode
dotnet build Davids.Services.Customers.slnx --no-restore -c Release
dotnet test Davids.Services.Customers.slnx --no-build --no-restore -c Release
```
