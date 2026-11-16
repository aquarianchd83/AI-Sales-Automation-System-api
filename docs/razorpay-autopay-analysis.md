# Razorpay UPI AutoPay - Phase 1: Repository Analysis

Date: 2026-11-16. Branches inspected: `PROD` in both repositories (API at `a50aee2`, Angular at `c0b6106`).
No code was changed to produce this document.

Scope note: the work spans **two git repositories** - `AI-Sales-Automation-System-api` (this one) and the sibling
`AI-Sales-Automation-System-angular`. Each needs its own feature branch and pull request.

---

## 1. Summary of findings

1. **Much of the requested plumbing already exists.** There is a working Razorpay gateway client, signature
   verification (constant-time), a signed and de-duplicated webhook endpoint, encrypted key storage, an Angular
   Checkout loader and a platform "Razorpay test" page. It handles one-off **orders** only and is deliberately not
   connected to tenant billing.
2. **Tenant billing is simulated.** `POST /api/v1/billing/plans/{planId}/choose` marks the tenant Active on a paid
   plan and records a `Payment` with `Provider = "Simulated"` without any money moving. The same is true for credit
   packs. This is live on the `PROD` branch.
3. **The wallet is a proper ledger and must be reused as is.** Grants, an append-only ledger, a cached balance with
   a row version, and idempotent operation keys are all in place.
4. **The current subscription model conflicts with paid recurring billing** and needs a decision before any code is
   written (section 7): plans "never expire", and monthly credits are handed out by an hourly job per calendar month,
   independent of any payment.
5. **There is no invoice entity.** It was removed (`RemoveUsageInvoices` migration). The `Payment` row carries the
   full tax snapshot and is what the billing screen lists.
6. **Several items in the brief do not match this codebase** and should follow the codebase instead: there is no
   repository / unit-of-work layer, routes are `/api/v1/...`, and responses are plain DTOs with `ProblemDetails`
   errors rather than a `{ "success": true }` envelope.

---

## 2. Architecture and conventions (as found)

| Topic | What the code does |
|---|---|
| Solution | `WhatsAppSalesAutomation.sln`, .NET 8. `Domain`, `Application` (Core), `Infrastructure`, `Api` (Presentation), `Tests`. |
| Data access | Services take `IApplicationDbContext` directly. No repository or unit-of-work classes. |
| Multi-tenancy | Entities implement `ITenantOwned`; a global query filter is built reflectively in `ApplicationDbContext`. `ITenantContext.TenantId` comes from the JWT. Cross-tenant code (jobs, webhooks) uses `IgnoreQueryFilters()` with an explicit `TenantId` predicate. |
| Auth | JWT bearer, roles in `AppRoles` (`Admin` for tenant billing, `PlatformSuperAdmin` for the console). |
| Routing | `[Route("api/v1/...")]`. Tenant billing lives under `api/v1/billing`. |
| Responses | The DTO itself on success. Errors via `ExceptionHandlingMiddleware` as `ProblemDetails`: `NotFoundException` 404, `ConflictException` 409, `FluentValidation.ValidationException` 400, `PlanLimitExceededException` 402. |
| Entities | `BaseEntity` (`Guid Id`, `CreatedAt`, `UpdatedAt` stamped by an interceptor). Enums stored as strings. UTC everywhere (`...Utc` suffix, `IDateTimeProvider`). No soft-delete on billing tables; catalog rows are retired with `IsActive`. |
| EF | `IEntityTypeConfiguration` classes in `Persistence/Configurations`, migrations in `Infrastructure/Migrations`, SQL Server. Known constraints: no query operator after a `.Select()` into a custom record; `dotnet ef` must be run from a scratch copy when Visual Studio locks the output path. |
| Settings and secrets | `AppSettings` table through `IAppSettingsStore`, secrets encrypted by `ISecretProtector`, catalogued in `AppSettingCatalog`. Razorpay keys are stored **only** here, never in `appsettings.json`. |
| Background jobs | Hangfire 1.8 (SQL Server storage). Platform-wide recurring jobs in `RecurringJobsRegistrar`; per-tenant schedules through `HangfireTenantJobScheduler`. `InboundWebhookProcessingJob` is the existing "persist, then process asynchronously with attempts" pattern. |
| Notifications | `ITenantNotifier.NotifyAsync(TenantNotificationRequest)`: in-app row plus email and optional WhatsApp, de-duplicated by a unique `(TenantId, Kind, QuotaType, EpisodeKey)` index. `TenantNotificationKind` is stored as text, so new kinds need no migration. |
| Rate limiting | Named policies; `WebhookPolicy` (per IP) is already applied to the Razorpay webhook. |
| Logging | Serilog with a tenant enricher. |
| Tests | xUnit, EF Core Sqlite (`SqliteApplicationDbContext`), `TestHost`. `RazorpayTests.cs` already has a `FakeGateway`, an in-memory settings store and an `HttpMessageHandler` stub. |
| Angular | Angular 15 with Angular Material, lazy feature modules, `core/services` + `core/models`, Karma/Jasmine (186 spec files). Production CSP already allows `checkout.razorpay.com` and `*.razorpay.com`. |

---

## 3. Existing relevant entities

### Billing (`Domain/Entities/Billing`)

| Entity | Table | Purpose | Reuse |
|---|---|---|---|
| `Plan` | `Plans` | Global catalog. `Code`, `Name`, limits, `PriceMonthlyCents` (base USD), `StarterCredits`, `MonthlyCredits`, `IsActive`. Seeded: `trial` (free, 50 starter credits) and `premium` (100 credits a month). | Reuse. |
| `PlanPrice` | `PlanPrices` | Explicit price per `(PlanId, CountryCode)` in local currency. The only source of a price; a country with no row is not sold the plan. Premium in India is seeded at 29 x 83 = Rs 2,407 before tax. | Reuse. This is the amount source. |
| `PlanFeature` | `PlanFeatures` | Which features a plan includes. | Reuse. |
| `Subscription` | `Subscriptions` | **One row per tenant** (unique `TenantId`): `PlanId`, `Status` (`Trialing`, `Active`, `PastDue`, `Canceled`), `CurrentPeriodStartUtc`, `CurrentPeriodEndUtc`. Its comment says gateway ids do not belong here. | Reuse as "current access". Extend minimally. |
| `Payment` | `Payments` | Append-only payment history with a full snapshot: plan name, local amount, tax lines (CGST/SGST/IGST), total, INR equivalent, FX rate, period, `Provider`, `PaidAtUtc`, `Kind` (`Subscription`, `CreditPack`, `Refund`). | Reuse as the payment and invoice record. Add gateway reference columns. |
| `QuotaWallet` | `QuotaWallets` | Cached balance per tenant, with `RowVersion`. | Reuse untouched. |
| `QuotaGrant` | `QuotaGrants` | A bucket of credits with origin (`PlanAllocation`, `CreditPurchase`, `Adjustment`, `Trial`), expiry and `PaymentId`. | Reuse untouched. |
| `QuotaLedgerEntry` | `QuotaLedgerEntries` | Immutable ledger with `OperationKey`, reference type and id. | Reuse untouched. |
| `CreditPack`, `CreditPackPrice` | | One-off credit purchases (also simulated today). | Out of scope. |
| `RefundRequest` | `RefundRequests` | Refund workflow over `Payment`, through `IRefundGateway` (simulated). | Out of scope, see 7.8. |
| `TenantNotification` | `TenantNotifications` | In-app notification plus delivery status. | Reuse. |

### Payments (`Domain/Entities/Payments`)

| Entity | Table | Purpose | Reuse |
|---|---|---|---|
| `RazorpayOrder` | `RazorpayOrders` | Orders made from the platform test page. Platform-global. | Leave as is. |
| `RazorpayWebhookEvent` | `RazorpayWebhookEvents` | Signed webhooks. Unique index on `EventId`. Columns: `Event`, `OrderId`, `PaymentId`, `Applied`, `Note`, `Payload` (**truncated to 8,000 characters**). | Reuse and extend (section 6). |

---

## 4. Existing relevant services and endpoints

### Services

| Service | Location | Notes |
|---|---|---|
| `IRazorpayGateway` / `RazorpayGateway` | `Application/Razorpay`, `Infrastructure/Payments` | Typed `HttpClient`, basic auth per request, returns `RazorpayResult<T>` and never throws on a gateway error. Has: ping, create order, fetch payment, capture, refund. **No plan or subscription calls.** |
| `RazorpaySignatures`, `RazorpayKeys` | `Application/Razorpay` | HMAC-SHA256, lowercase hex, `CryptographicOperations.FixedTimeEquals`. Webhook signature over the raw bytes. Mode (`test`/`live`) derived from the key id prefix. |
| `IRazorpayService` / `RazorpayService` | `Application/Razorpay` | Test-page orchestration and `HandleWebhookAsync`: verify, de-duplicate on event id (falls back to a body hash), apply to a `RazorpayOrder` **inline**, catch the unique-index race. Unknown events are stored and ignored. |
| `IBillingService` / `BillingService` | `Application/Billing`, `Infrastructure/Billing` | Plan catalog with tax quote, current subscription, **simulated** `ChoosePlanAsync` and `PurchaseCreditPackAsync`, payment history. |
| `IPricingService` | `Application/Billing` | The single place a price and its tax are computed (`PriceQuote`). GST splits by tenant state. |
| `PaymentFactory` | `Application/Billing` | Builds a `Payment` row from a quote. Hard-codes `Provider = "Simulated"`. |
| `ICreditAllocationService` | `Application/Credits/CreditWallet.cs` | `AssignPlanAsync` (sets plan, grants starter and this month's credits), `AllocateMonthlyAsync` (calendar-month grant for every Active subscription). |
| `IQuotaLedgerService` (`QuotaLedgerService`, wrapped by `CreditRoutingLedger`) | `Application/Quota` | `GrantPlanCreditsAsync(tenantId, units, operationKey, note, origin, paymentId)` is idempotent per operation key. Plan credits never expire. |
| `ICreditWalletService`, `IFeatureAccessService` | `Application/Credits` | Balance, entitlements, per-feature caps. `FeatureAccessService` reads the plan's features from `Subscription.PlanId` without looking at `Status`. |
| `AiUsageService` | `Application/Ai` | Already denies AI when the subscription is `Canceled`, `PastDue`, or `CurrentPeriodEndUtc` has passed. |
| `IRefundService` | `Application/Billing/Refunds` | A refunded subscription payment sets the subscription to `Canceled` and clears the plan. |
| `ITenantNotifier`, `ITenantBillingNoticeService` | `Application/Notifications`, `Application/Billing` | Notification delivery and the tenant's notification list. `PlanRenewalFailed`, `PlanExpiring7`, `PlanExpiring1` kinds already exist. |
| `IPlatformBillingService`, `IPlatformTenantService.OverridePlanAsync` | `Application/Platform` | Console plan editor, per-country prices, operator plan override. |
| `ITenantJobProvisioner.SyncTenantAsync` | | Re-syncs a tenant's jobs when its status changes. Called after a plan is chosen. |
| `SubscriptionMaintenanceJob` | `Infrastructure/BackgroundJobs` | Hourly: `AllocateMonthlyAsync` and expiry of stale refund requests. |

### Endpoints

| Endpoint | Auth | Notes |
|---|---|---|
| `GET api/v1/billing/plans` | Anonymous | Plans with local price, tax lines, total, credits and features. Already satisfies the brief's `GET /api/plans`. |
| `GET api/v1/billing/subscription` | Admin | Current plan, status, period. |
| `POST api/v1/billing/plans/{planId}/choose` | Admin | **Simulated payment.** |
| `GET api/v1/billing/payments` | Admin | Payment history. |
| `GET api/v1/billing/wallet`, `quota`, `quota/ledger`, `entitlements` | Admin / any user | Wallet. |
| `POST api/v1/billing/credit-packs/{packId}/purchase` | Admin | Simulated. |
| `POST api/v1/webhooks/razorpay` | Anonymous, signature, rate limited, 256 KB cap | Reads the raw body. Returns 200 for signed events and duplicates, 400 otherwise. |
| `api/v1/platform/razorpay/*` | PlatformSuperAdmin | Keys, key test, test orders, verify, refund, webhook log. |

---

## 5. Existing Angular implementation

| Area | Files | Notes |
|---|---|---|
| Billing screen | `features/billing/billing-list/*` | Plan cards, current subscription, payment history, refund request. `choosePlan()` calls the simulated endpoint after a `ConfirmDialogComponent`. Also embedded in onboarding. |
| Wallet | `features/billing/wallet/*`, `credit-usage`, `credit-costs` | Balance, ledger, caps, pack purchase. |
| Services and models | `core/services/billing.service.ts`, `core/models/billing.model.ts`, `wallet.model.ts`, `razorpay.model.ts` | |
| Razorpay Checkout loader | `core/services/razorpay-checkout.service.ts` | Loads `checkout.js` once and opens it for an **order**. Emits `success`, `failed`, `dismissed`. Can be extended to accept a `subscription_id`. |
| Test page | `features/razorpay-test/*`, `core/services/razorpay-test.service.ts` | Platform console only. |
| Shared UI | `shared/components/confirm-dialog`, `page-header`, Material snack bars | Reuse. |
| Notifications | `features/notifications`, bell in `layout/shell` | Reuse for payment failure notices. |

There is no invoice download in the billing screen today.

---

## 6. Gap analysis

| Requirement | Status | Gap |
|---|---|---|
| Gateway abstraction | Partial | `IRazorpayGateway` exists. Extend it with create plan, create / fetch / cancel / pause / resume subscription and fetch subscription invoices rather than adding a second `IPaymentGatewayService` beside it (that would be duplicate functionality). |
| Razorpay configuration | Done | Key id, secret, webhook secret in encrypted settings. Base URL is a constant in the gateway; make it configurable only if needed. |
| Plan mapping to Razorpay plan ids | Missing | Razorpay plans are immutable and carry one fixed amount. Our charge depends on country price **and tax**. One column on `Plan` is not enough; a mapping table keyed by plan, amount, currency and mode is needed. |
| Checkout / authorisation API | Missing | |
| Local mandate / subscription record | Missing | Nothing stores a Razorpay subscription id. |
| Current subscription, details, cancel | Partial | Read exists; no cancel, no renewal date, no auto-renew or cancellation flags. |
| Payment history | Partial | Exists, but has no gateway payment id, method, status or failure reason. Failed attempts are not recorded anywhere. |
| Invoices | Missing | No invoice entity or document. Razorpay generates an invoice per cycle (`invoice_id` on the payment). |
| Webhook: signature, raw body, de-duplication | Done | |
| Webhook: durable persist then asynchronous processing, status, retry count, error | Missing | Processing is inline and the stored payload is truncated, so an event cannot be reprocessed from the table. |
| Webhook: subscription events | Missing | Stored and ignored today. |
| Credits on successful charge, exactly once | Partial | The ledger primitive is idempotent. Nothing calls it from a payment event, and the calendar-month job would grant in parallel (7.1). |
| Reconciliation, pending clean-up, expiry, failure notices | Missing | |
| Angular: plans, status, history, cancel | Partial | Screens exist against the simulated flow. |
| Tests | Partial | Good doubles exist for the gateway and wallet. |

---

## 7. Design conflicts and decisions needed

Each item has a recommendation. Items marked **Decision** change behaviour that exists today.

### 7.1 Who grants the monthly credits - **Decision**

Today the hourly job grants `Plan.MonthlyCredits` to every `Active` subscription once per calendar month under the
key `monthly:yyyyMM`, whether or not anything was paid. With AutoPay the brief requires credits to follow a
successful charge.

Recommendation: a subscription billed through Razorpay gets its credits **only** from `subscription.charged`, under
the key `rzp-charge:{razorpay_payment_id}`. `AllocateMonthlyAsync` skips tenants whose subscription is
gateway-billed. Tenants on a plan without a mandate (operator override, free plans, non-INR tenants) keep today's
behaviour. Without the skip, a paying tenant would receive credits twice a month.

### 7.2 "Plans never expire" - **Decision**

`AssignPlanAsync` clears the billing period, and several comments state the plan has no period. A paid recurring
plan needs a paid-through date.

Recommendation: for gateway-billed subscriptions, set `CurrentPeriodStartUtc` / `CurrentPeriodEndUtc` from Razorpay's
`current_start` / `current_end`. `AiUsageService` already enforces `CurrentPeriodEndUtc` and `PastDue`, so expiry
handling for AI works once those are populated. Feature access (`FeatureAccessService`) ignores status; when a
subscription finally lapses the tenant is moved back to the Trial plan (existing `AssignTrialPlanAsync`), which is how
features are withdrawn today.

### 7.3 Unused credits

The brief says not to carry credits forward unless existing rules support it. Existing rules do: plan grants use
`NeverExpires` and the code states "unspent credits carry over". Recommendation: keep carry-over. Changing it would
alter the wallet model, which the brief forbids.

### 7.4 The simulated purchase endpoints - **Decision**

`POST billing/plans/{planId}/choose` gives any tenant Admin a paid plan for free. Once AutoPay is live this is a
revenue hole.

Recommendation: when Razorpay is configured and the plan has a positive price for the tenant, `choose` refuses with
409 and points to the AutoPay checkout. It keeps working for free plans (the Trial downgrade path). Credit-pack
purchase stays simulated and is listed as a pending item; it is a separate piece of work.

### 7.5 Where gateway ids live

`Subscription` is one row per tenant and explicitly provider-agnostic. A tenant can have several mandate attempts
over time (abandoned, cancelled, replaced on a plan change).

Recommendation: keep `Subscription` as the single "current access" row and add a separate history table for the
Razorpay subscription / mandate (section 8). `Subscription` gains only a pointer and the cancel-at-period-end flag.
A separate `PaymentMandates` table is not needed: Razorpay does not expose the UPI mandate as its own object in the
subscription flow, so the Razorpay subscription record is the mandate record.

### 7.6 Subscription status vocabulary

`SubscriptionStatus` has four values and is read in about ten places. The UI needs eight display states.

Recommendation: keep the enum as the access state and store Razorpay's own state on the new table. The API returns a
derived display status:

| Display | Derived from |
|---|---|
| Pending Authorization | Razorpay `created`, no active plan access from it yet |
| Active | Razorpay `authenticated` with a captured first charge, or `active` |
| Payment Pending / Past Due | Razorpay `pending` (retries running); local `PastDue` |
| Halted | Razorpay `halted` |
| Cancellation Scheduled | cancel-at-period-end set, period not over |
| Cancelled | Razorpay `cancelled` |
| Expired | Razorpay `expired` or `completed`, or paid-through date passed |
| Paused | Razorpay `paused` |

### 7.7 Amount, tax and currency

- The Razorpay plan amount must be the **tax-inclusive total** from `IPricingService.Quote` (CGST+SGST and IGST give
  the same total, so the tenant's state does not multiply plans).
- UPI AutoPay is INR-only. AutoPay is offered only where the tenant's quote is in INR; other countries keep the
  current flow. This is a product limitation to confirm.
- Documented cap: Rs 15,000 per recurring UPI debit (higher only for specific merchant categories). Premium in India
  is about Rs 2,840 including 18% GST. The checkout validates the cap against a configurable limit and refuses
  plans above it.
- The amount is always computed server-side; the request carries only the plan id.

### 7.8 Refunds

`IRefundGateway` is simulated. Approving a refund for a Razorpay payment would mark it refunded without returning
money. Recommendation for this project: refund approval is blocked for `Provider = "Razorpay"` payments with a clear
message until a real refund gateway is built. Listed as a pending item, not built here.

### 7.9 Routes and response shape

Recommendation: follow the codebase, not the brief's examples.

| Brief | Proposed |
|---|---|
| `GET /api/plans` | existing `GET api/v1/billing/plans` |
| `POST /api/subscriptions/checkout` | `POST api/v1/billing/autopay/checkout` |
| `GET /api/subscriptions/current` | existing `GET api/v1/billing/subscription`, extended with new optional fields |
| `GET /api/subscriptions/{id}` | `GET api/v1/billing/autopay/subscriptions/{id}` |
| `POST /api/subscriptions/{id}/cancel` | `POST api/v1/billing/autopay/subscriptions/{id}/cancel` |
| `GET /api/payments/history` | existing `GET api/v1/billing/payments`, extended |
| `GET /api/subscriptions/{id}/invoices` | `GET api/v1/billing/autopay/subscriptions/{id}/invoices` |
| `POST /api/webhooks/razorpay` | existing `POST api/v1/webhooks/razorpay` |

Existing DTOs only gain optional trailing fields, so current clients keep working.

---

## 8. Proposed database changes

Existing tables reused without change: `Plans`, `PlanPrices`, `PlanFeatures`, `QuotaWallets`, `QuotaGrants`,
`QuotaLedgerEntries`, `TenantNotifications`, `RazorpayOrders`, `AppSettings`.

### New tables

**`RazorpayPlans`** (global) - maps an internal plan and exact charge to an immutable Razorpay plan.

| Column | Notes |
|---|---|
| `Id`, `CreatedAt`, `UpdatedAt` | |
| `PlanId` | FK to `Plans` |
| `Mode` | `test` or `live` |
| `Currency`, `AmountMinor` | paise, tax-inclusive |
| `Period`, `Interval` | `monthly`, 1 |
| `RazorpayPlanId` | unique |

Unique index on `(PlanId, Mode, Currency, AmountMinor, Period, Interval)`: a plan is created at Razorpay once per
distinct charge and reused after that. A price change produces a new row; existing mandates keep their old plan.

**`RazorpaySubscriptions`** (tenant-owned) - one row per mandate attempt; history is kept.

| Column | Notes |
|---|---|
| `Id`, `TenantId`, `CreatedAt`, `UpdatedAt` | |
| `PlanId`, `RazorpayPlanRowId` | FKs |
| `RazorpaySubscriptionId` | unique |
| `Mode` | |
| `Status` | Razorpay's state, as text |
| `AmountMinor`, `Currency` | snapshot of the agreed recurring amount |
| `ShortUrl` | hosted authorisation link |
| `TotalCount`, `PaidCount` | |
| `CurrentStartUtc`, `CurrentEndUtc`, `ChargeAtUtc` | |
| `AuthenticatedAtUtc`, `ActivatedAtUtc`, `CancelledAtUtc`, `EndedAtUtc` | |
| `CancelAtCycleEnd` | |
| `PaymentMethod`, `CustomerId` | as reported by Razorpay; no VPA, PIN or bank data |
| `LastEventAtUtc` | Razorpay `created_at` of the newest event applied, for out-of-order protection |
| `LastReconciledAtUtc`, `CreatedByUserId` | |

Indexes: `(TenantId, Status)`; a filtered unique index allowing at most one row per tenant in `Created` (one
authorisation waiting). It deliberately does not cover the other open states: a future plan change needs the old
mandate and its replacement open at the same time, so "one running mandate" is checked in the checkout service.

### Changed tables

**`Subscriptions`** - add `RazorpaySubscriptionRowId` (nullable FK, the mandate currently backing access) and
`CancelAtPeriodEnd` (bool, default false).

**`Payments`** - add `ProviderPaymentId`, `ProviderInvoiceId`, `ProviderSubscriptionId` (nullable, max 40),
`PaymentMethod` (nullable, max 30), `Status` (`Succeeded` default, `Failed`), `FailureReason` (nullable, max 500).
Filtered unique index on `(Provider, ProviderPaymentId)` where the id is not null: **this is the database-level
guard that one Razorpay payment can never be recorded twice.** Existing rows default to `Succeeded`. History
queries that feed revenue reports must exclude `Failed` rows; that is checked in Phase 5.

**`RazorpayWebhookEvents`** - add `SubscriptionId` (nullable, max 40), `ProcessingStatus` (`Received`, `Processing`,
`Completed`, `Failed`, `Ignored`), `ProcessedAtUtc`, `RetryCount`, `ErrorMessage`, `EventCreatedAtUtc`. Widen
`Payload` to `nvarchar(max)` and stop truncating, so an event can be reprocessed from the table. Existing rows are
back-filled as `Completed`.

### Exactly-once credit allocation

Three independent layers, each at database level:

1. `RazorpayWebhookEvents.EventId` unique - a redelivered event is not processed again.
2. `Payments (Provider, ProviderPaymentId)` unique - the same payment arriving through two different events
   (`subscription.charged`, `payment.captured`, reconciliation) is recorded once.
3. Ledger operation key `rzp-charge:{payment id}` plus the wallet `RowVersion` - the grant is posted once even if two
   workers race.

The payment row, the subscription period update and the mandate row update are saved in one transaction. The grant
follows and is safe to repeat, which is the pattern `PurchaseCreditPackAsync` already uses.

---

## 9. Proposed code changes

### Backend - new

| File | Purpose |
|---|---|
| `Domain/Entities/Payments/RazorpayPlan.cs`, `RazorpaySubscription.cs` | Entities above |
| `Application/Billing/AutoPay/IAutoPayService.cs`, `AutoPayService.cs`, `AutoPayDtos.cs` | Checkout, status, cancel, invoices. Subscription business logic. |
| `Application/Billing/AutoPay/RazorpayPlanMapper.cs` | Find or create the Razorpay plan for a quote |
| `Application/Billing/AutoPay/RazorpaySubscriptionEventProcessor.cs` | Applies one stored webhook event |
| `Application/Billing/AutoPay/SubscriptionChargeRecorder.cs` | Payment row, period, credits, notification - shared by webhook and reconciliation |
| `Application/Billing/AutoPay/AutoPayOptions.cs` | UPI cap, total cycle count, grace days, clean-up age |
| `Infrastructure/BackgroundJobs/RazorpayWebhookProcessingJob.cs` | Asynchronous processing with attempts |
| `Infrastructure/BackgroundJobs/AutoPayReconciliationJob.cs` | Daily reconciliation, pending-authorisation clean-up, expiry and grace handling |
| `Api/Controllers/BillingAutoPayController.cs` | Thin controller, `Admin` role |
| EF configurations, one migration per phase | |
| Tests: `AutoPayCheckoutTests`, `RazorpaySubscriptionWebhookTests`, `AutoPayReconciliationTests`, gateway tests | |

### Backend - modified

| File | Change |
|---|---|
| `IRazorpayGateway`, `RazorpayGateway` | Plan and subscription calls |
| `RazorpayService.HandleWebhookAsync` | Store the full payload and status, enqueue `subscription.*` and subscription-linked `payment.*` events; order events for the test page keep their current inline handling |
| `RazorpaySignatures` | Add the subscription checkout signature (`payment_id|subscription_id`) |
| `BillingService`, `IBillingService`, `BillingDtos` | Extended subscription and payment DTOs; `ChoosePlanAsync` guard (7.4) |
| `CreditAllocationService.AllocateMonthlyAsync` | Skip gateway-billed subscriptions (7.1) |
| `PaymentFactory` | Accept provider details |
| `RefundService` | Block Razorpay payments (7.8) |
| `Subscription`, `Payment`, `RazorpayWebhookEvent`, their configurations, `IApplicationDbContext`, `ApplicationDbContext` | New columns and sets |
| `RecurringJobsRegistrar`, both `DependencyInjection` files, `AppSettingCatalog` | Registration |
| `TenantNotificationKind` and its catalog | Payment received, payment failed, subscription halted, subscription cancelled |

### Angular - modified and new

| File | Change |
|---|---|
| `core/models/billing.model.ts`, `core/services/billing.service.ts` | AutoPay models and calls |
| `core/services/razorpay-checkout.service.ts` | Open Checkout for a `subscription_id` |
| `features/billing/billing-list/*` | Subscribe through AutoPay, status chip, renewal date, auto-renew, cancel with confirmation, payment-failure banner, history columns |
| `features/billing/autopay-return/*` (new) | Return page that polls the backend for confirmation; never assumes success |

No new UI framework and no new NuGet or npm packages.

---

## 10. Razorpay facts checked against the current documentation

Read on 2026-11-16 from razorpay.com/docs (Subscriptions API, states, webhooks, FAQ, supported apps, retries, test).

| Topic | Finding |
|---|---|
| Plans | `POST /v1/plans` with `period`, `interval`, `item.name/amount/currency`. Amount in paise. **Cannot be edited or deleted.** |
| Subscriptions | `POST /v1/subscriptions` with `plan_id`, `total_count` (required), `customer_notify`, `notes`, optional `start_at`, `expire_by`. Response has `id`, `status = created`, `short_url` for the authorisation payment. |
| Duration | Docs disagree: "maximum duration of 100 years" in one place, "up to 10 years" in an error table. Recommendation: `total_count = 120` (10 years, monthly), configurable. |
| Authorisation | Hosted `short_url`, or Standard Checkout with `subscription_id`. Checkout returns `razorpay_payment_id`, `razorpay_subscription_id`, `razorpay_signature = HMAC_SHA256(payment_id + "|" + subscription_id, key_secret)`. |
| UPI cap | Rs 15,000 per debit by default; Rs 1,00,000 only for listed merchant categories. |
| UPI apps | Google Pay, PhonePe, Paytm, Amazon Pay, BHIM and others. |
| UPI authorisation | A Rs 5 token payment, auto-refunded. |
| UPI restrictions | A UPI subscription **cannot be updated** (no plan or amount change). No manual charge. The customer can pause from the UPI app, and the merchant cannot resume a customer-paused subscription. |
| Cancel | `POST /v1/subscriptions/:id/cancel` with `cancel_at_cycle_end`. Cancelled subscriptions cannot be restarted. The docs do not confirm `cancel_at_cycle_end` for UPI specifically. |
| Failed charge | Subscription goes to `pending`; Razorpay retries on T+1, T+2, T+3; then `halted`. |
| States | `created`, `authenticated`, `active`, `pending`, `halted`, `cancelled`, `completed`, `expired`, `paused`. |
| Events | `subscription.authenticated`, `activated`, `charged`, `completed`, `updated`, `pending`, `halted`, `cancelled`, `paused`, `resumed`. `charged` carries the subscription entity (`current_start`, `current_end`, `charge_at`, `paid_count`, `remaining_count`) and the payment entity (`id`, `amount`, `status`, `method`, `invoice_id`). |
| Webhooks | `X-Razorpay-Signature`, HMAC-SHA256 over the raw body. `x-razorpay-event-id` is unique per event. Duplicates are expected. Order is not guaranteed. Retry duration and timeout are not stated. |
| Invoices | Generated automatically for every billing cycle. |
| Test mode | Subscriptions work in test mode with test cards. **A UPI test flow for subscriptions is not documented.** |
| Enablement | Subscriptions must be enabled on the account for test and live. UPI on subscriptions is described as early access and may need Razorpay support to enable. |

Not read and to be verified in Phase 3 before coding against them: the pause, resume, update and
fetch-subscription-invoices reference pages.

### Plan upgrade and downgrade

Because a UPI subscription cannot be updated, a plan change needs a new mandate. Only one paid plan exists today
(Premium), so the only live transitions are Trial to Premium (new mandate) and Premium to Trial (cancel at cycle
end). For a future second paid plan the chosen behaviour is: create the new subscription with `start_at` set to the
current paid-through date, cancel the old one at cycle end once the new one is authenticated, and switch the plan
only when the new subscription's first charge is confirmed. No overlap in billing and no second credit grant in the
same period. This will be documented in the architecture document and not built until a second paid plan exists.

---

## 11. Implementation plan

Each phase ends buildable, with tests run, and is reviewed before the next starts. API work happens on a feature
branch. The branch base needs confirming: `master` is the repository's main branch, `PROD` is what is checked out.

| Phase | Content | Verification |
|---|---|---|
| 2. Database | Entities, configurations, one migration. No behaviour change. | Build, existing tests, migration applied to a scratch database |
| 3. Gateway and checkout | Gateway plan/subscription calls, plan mapper, checkout, status, cancel, controller, `choose` guard | Unit tests with the fake gateway; gateway tests with the HTTP stub |
| 4. Webhooks | Full-payload persistence, asynchronous job, event processor, out-of-order handling, reconciliation and clean-up jobs | Duplicate, replay, out-of-order and failure-then-retry tests |
| 5. Wallet and billing | Charge recorder, payment rows, credits exactly once, monthly-job skip, notifications, invoice listing | Idempotency tests: same event five times, same payment via two events, concurrent workers |
| 6. Angular | Billing screen changes, return page | Karma specs |
| 7. Documentation and full test run | Remaining five documents | |

---

## 12. Items that cannot be verified from code

- Subscriptions enabled on the Razorpay account, in test and live.
- UPI AutoPay enabled for subscriptions on the account.
- A real mandate authorisation in Google Pay, a real recurring debit, and settlement.
- Whether `cancel_at_cycle_end` is honoured for UPI subscriptions.
- Webhook events subscribed in the Razorpay dashboard for both modes.
- GST invoice requirements: whether Razorpay's auto-generated invoice is acceptable as the tax invoice or the
  platform must issue its own numbered GST invoice. This is an accounting decision.
