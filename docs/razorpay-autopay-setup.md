# Razorpay UPI AutoPay - Setup and Deployment

What has to be in place for AutoPay to work, in the order to do it. Do all of it with **test keys first**.

## 1. Razorpay account prerequisites

These are on Razorpay's side and cannot be checked from code.

| Requirement | Where |
|---|---|
| Subscriptions enabled, in test mode and in live mode | Razorpay Dashboard, or Razorpay support. Without it, creating a subscription fails with "The requested URL was not found on the server". |
| UPI enabled as a payment method for Subscriptions | Razorpay describes this as early access; it may need a request to Razorpay support. |
| The account's recurring UPI limit | Rs 15,000 per debit by default. Higher only for specific merchant categories. |

## 2. Configuration

### Razorpay keys

Stored encrypted in the `AppSettings` table, never in `appsettings.json`. Set them from the Platform Admin Console's
Razorpay page (`PUT api/v1/platform/razorpay/settings`).

| Setting key | Secret | Value |
|---|---|---|
| `Razorpay:KeyId` | No | `rzp_test_...` or `rzp_live_...` |
| `Razorpay:KeySecret` | Yes | API key secret |
| `Razorpay:WebhookSecret` | Yes | The secret entered on the Razorpay webhook |

The mode (test or live) is read from the key id's prefix. Each environment has its own database, so test and live keys
are separate by construction. Mandates and Razorpay plans record the mode they were made in; test ones are ignored
once live keys are in use.

### The AutoPay switch

| Setting key | Default | Effect |
|---|---|---|
| `Razorpay:AutoPayEnabled` | `false` | `true` makes tenants billed in rupees pay for a paid plan through AutoPay, and makes the simulated choose-plan endpoint refuse those plans. |

It is in the settings catalog under the `Razorpay` category. Leave it `false` until section 4 has passed.

### Optional tuning (`appsettings.json`, section `Billing:AutoPay`)

| Key | Default | Meaning |
|---|---|---|
| `MaxRecurringAmountInr` | 15000 | Largest monthly debit, tax included. Plans above it are refused at checkout. |
| `TotalCount` | 120 | Monthly cycles a mandate is created for (10 years). |
| `AuthorizationExpiryHours` | 48 | How long the tenant has to approve a mandate. 0 leaves it to Razorpay. |
| `WebhookMaxAttempts` | 10 | Tries before a failed webhook event is left for a person. |
| `WebhookPickupMinutes` | 2 | Age at which the retry pass takes an event no job picked up. |
| `AbandonedAuthorizationHours` | 72 | Age at which an unapproved mandate is cancelled at Razorpay. |

The grace period after a paid period ends is a code constant, `AutoPayOptions.GraceDays` = 4.

## 3. Razorpay webhook

Razorpay Dashboard > Account & Settings > Webhooks > Add new webhook. Do this once for test mode and once for live
mode.

| Field | Value |
|---|---|
| URL | `https://<api-host>/api/v1/webhooks/razorpay` (port 443) |
| Secret | The same value saved as `Razorpay:WebhookSecret` |
| Active events | See below |

Events to enable:

- `subscription.authenticated`
- `subscription.activated`
- `subscription.charged`
- `subscription.pending`
- `subscription.halted`
- `subscription.cancelled`
- `subscription.completed`
- `subscription.paused`
- `subscription.resumed`
- `subscription.updated`
- `payment.failed`

The platform's Razorpay test page also uses `payment.authorized`, `payment.captured`, `order.paid`, `refund.created`,
`refund.processed` and `refund.failed`; keep those enabled if they already are.

## 4. Plan prices

A plan is sold through AutoPay only when:

- it has a price row for India (`PlanPrices`, country `IN`) greater than zero, set in the Platform Admin Console's
  plan editor, and
- price plus tax is at or below `MaxRecurringAmountInr`.

Razorpay plans are created automatically on first checkout for each distinct amount. Changing a price needs no
action: the next checkout creates a new Razorpay plan, and existing mandates keep debiting the amount they were
authorised for.

## 5. Deployment

Deploy the API before the Angular app: the screen calls endpoints that must exist.

1. **Database.** The migration `20261116142726_AddRazorpayAutoPay` is applied automatically at API start-up
   (`Database.MigrateAsync`). It adds two tables and nullable or defaulted columns, and back-fills existing payments
   as `Succeeded` and existing webhook events as `Completed` or `Ignored`. It can be rolled back with
   `dotnet ef database update AddDatabaseCleanupLogs`.
2. **API.** Deploy. Three recurring Hangfire jobs are registered at start-up: `razorpay-webhook-retry`,
   `autopay-reconciliation`, `autopay-lapsed-plans`. With the switch off, none of them has anything to do and no
   tenant-facing behaviour changes.
3. **Angular.** Deploy. With the switch off the billing screen looks and behaves as before.
4. **Razorpay.** Sections 1 and 3 above, with test keys.
5. **Verify** with test keys: the checklist in [razorpay-autopay-testing.md](razorpay-autopay-testing.md), section 4.
6. **Switch on** `Razorpay:AutoPayEnabled`.
7. **Go live.** Replace the keys and webhook secret with live ones, add the live-mode webhook, and repeat the
   verification with a real mandate of a small amount before telling tenants.

### Rolling back

Set `Razorpay:AutoPayEnabled` to `false`. New checkouts stop and the simulated path returns. Existing mandates are
unaffected: Razorpay keeps debiting them and their webhooks are still processed. To stop them, cancel each one
(tenant screen, or the Razorpay Dashboard).

## 6. Operating it

| What to watch | Where |
|---|---|
| Webhook events that failed | `RazorpayWebhookEvents` where `ProcessingStatus = 'Failed'`; `ErrorMessage` and `RetryCount` say why and how often. Platform console: `GET api/v1/platform/razorpay/webhook-events`. |
| An event given up on | A `Critical` log entry: "failed N times and will not be retried again". Fix the cause, then set the row's `RetryCount` to 0 to have it retried. |
| Reconciliation | An `Information` log entry each day with counts; `Error` entries for a mandate where Razorpay still counts more payments than are recorded. |
| Jobs | Hangfire dashboard at `/hangfire`. |

A payment whose amount differs from its mandate is never credited; its event stays `Failed` with a message that
ends "Nothing was credited" and needs a person to decide.
