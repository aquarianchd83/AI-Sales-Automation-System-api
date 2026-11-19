# Razorpay UPI AutoPay - API

All tenant endpoints are under `api/v1/billing`, require a JWT with the tenant `Admin` role, and act on the tenant
named in the token. A request can never name another tenant.

Responses are the DTO itself. Errors are `application/problem+json`:

| Status | When |
|---|---|
| 400 | Validation failed. |
| 401 / 403 | Not signed in / not a tenant Admin. |
| 404 | The plan or mandate does not exist, **or belongs to another tenant**. |
| 409 | The request cannot be done in the current state, or Razorpay refused. The `detail` says why, in Razorpay's words where it gave any. |

## New endpoints

### GET `billing/autopay/availability`

Whether this tenant pays for plans through AutoPay.

```json
{ "available": true, "reason": null, "mode": "test" }
```

`available` is false, with a `reason`, when the switch is off, Razorpay is not set up, or the tenant is not billed in
rupees. `mode` is `test` or `live`.

### POST `billing/autopay/checkout`

Starts a mandate for one plan.

Request - the plan only; the amount is never accepted from the client:

```json
{ "planId": "6f1d2c1e-0000-0000-0000-000000000000" }
```

Response:

```json
{
  "id": "0b7e...-our-mandate-id",
  "subscriptionId": "sub_Nx1abc",
  "status": "PendingAuthorization",
  "authorizationUrl": "https://rzp.io/i/abc123",
  "keyId": "rzp_test_xxxxxxxx",
  "mode": "test",
  "amountMinor": 284026,
  "currency": "INR",
  "planName": "Premium"
}
```

`authorizationUrl` is Razorpay's hosted page. `keyId` and `subscriptionId` allow Razorpay Checkout to be opened
instead. `amountMinor` is in paise and includes tax.

Calling it again while the same mandate still waits returns that mandate, not a new one.

409 when: AutoPay is off or not set up; the plan is free, not sold in the tenant's country, not billed in rupees, or
above the UPI limit; the tenant already has a running mandate; Razorpay could not be reached or refused.

### GET `billing/autopay/subscription`

The tenant's mandate that matters now: a running one, else one waiting to be authorised, else the most recent.
**204** when the tenant never started one.

```json
{
  "id": "0b7e...",
  "subscriptionId": "sub_Nx1abc",
  "planId": "6f1d...",
  "planName": "Premium",
  "status": "Active",
  "gatewayStatus": "Active",
  "amount": 2840.26,
  "currencyCode": "INR",
  "billingFrequency": "Monthly",
  "currentPeriodStartUtc": "2026-11-16T10:05:00Z",
  "currentPeriodEndUtc": "2026-12-16T10:05:00Z",
  "nextRenewalUtc": "2026-12-16T10:05:00Z",
  "autoRenew": true,
  "cancelAtCycleEnd": false,
  "cancelledAtUtc": null,
  "paymentMethod": "upi",
  "authorizationUrl": null,
  "mode": "test",
  "createdAt": "2026-11-16T10:00:00Z"
}
```

`status` is one of `PendingAuthorization`, `PaymentPending`, `Active`, `PastDue`, `Halted`, `Paused`,
`CancellationScheduled`, `Cancelled`, `Expired`. `authorizationUrl` is set only while the mandate waits to be
authorised. `nextRenewalUtc` is null when it will not renew.

### GET `billing/autopay/subscriptions/{id}`

One mandate, same shape. 404 when it is not this tenant's.

### POST `billing/autopay/subscriptions/{id}/refresh`

Asks Razorpay where the mandate stands, records it, and returns it. This is what a screen calls after the tenant
returns from Razorpay. It updates the mandate record only; the plan and credits follow the confirmed payment, which
arrives by webhook.

409 when Razorpay cannot be reached.

### POST `billing/autopay/subscriptions/{id}/cancel`

```json
{ "cancelAtCycleEnd": true }
```

- `true` (the default when no body is sent): renewal stops after the period already paid for.
- `false`: the mandate is cancelled now.

Either way, access already paid for is kept until the period ends. A mandate that was never authorised, or is halted,
is always cancelled immediately. A cancelled mandate cannot be restarted.

Returns the mandate. Asking again changes nothing. 409, with nothing changed, when Razorpay refuses.

### GET `billing/autopay/subscriptions/{id}/invoices`

The invoices Razorpay raised for the mandate, newest first. Read from Razorpay on each call.

```json
[
  {
    "invoiceId": "inv_Nx2def",
    "invoiceNumber": null,
    "status": "paid",
    "amount": 2840.26,
    "amountPaid": 2840.26,
    "currencyCode": "INR",
    "issuedAtUtc": "2026-11-16T10:05:00Z",
    "paidAtUtc": "2026-11-16T10:05:30Z",
    "periodStartUtc": "2026-11-16T10:05:00Z",
    "periodEndUtc": "2026-12-16T10:05:00Z",
    "transactionId": "pay_Nx3ghi",
    "url": "https://rzp.io/i/xyz"
  }
]
```

`url` is Razorpay's `short_url` for the invoice.

## Changed endpoints

### GET `billing/payments`

Each row gains five optional fields. Existing clients are unaffected.

| Field | Meaning |
|---|---|
| `status` | `Succeeded` or `Failed`. A failed row is an attempt the gateway could not complete: nothing was paid. |
| `paymentMethod` | `upi`, `card`, ... as Razorpay reported it. |
| `failureReason` | Razorpay's reason, on a failed row. |
| `transactionId` | Razorpay's payment id. |
| `invoiceId` | Razorpay's invoice id. |

`provider` is `Razorpay` for AutoPay payments and `Simulated` for the others.

### POST `billing/plans/{planId}/choose`

Unchanged while AutoPay is off. With it on, 409 for:

- a paid plan billed in rupees ("Use Subscribe to set it up"), and
- any plan change while the tenant's mandate is still charging ("Cancel the automatic payment before changing plan").

Free plans, and tenants not billed in rupees, still use it.

### GET `billing/payments/{paymentId}/refund-eligibility` and refund requests

A Razorpay payment, and a failed payment, are reported as not eligible.

### Unchanged

`GET billing/plans` (plan name, amount, tax, credits, features) and `GET billing/subscription` (plan, status, period)
are as before.

## Webhook endpoint

`POST api/v1/webhooks/razorpay` - anonymous, authenticated by Razorpay's signature. See
[razorpay-autopay-webhooks.md](razorpay-autopay-webhooks.md).

## Mapping from the original brief

| Brief | Implemented as |
|---|---|
| `GET /api/plans` | `GET api/v1/billing/plans` (existing) |
| `POST /api/subscriptions/checkout` | `POST api/v1/billing/autopay/checkout` |
| `GET /api/subscriptions/current` | `GET api/v1/billing/autopay/subscription` |
| `GET /api/subscriptions/{id}` | `GET api/v1/billing/autopay/subscriptions/{id}` |
| `POST /api/subscriptions/{id}/cancel` | `POST api/v1/billing/autopay/subscriptions/{id}/cancel` |
| `GET /api/payments/history` | `GET api/v1/billing/payments` (existing, extended) |
| `GET /api/subscriptions/{id}/invoices` | `GET api/v1/billing/autopay/subscriptions/{id}/invoices` |
| `POST /api/webhooks/razorpay` | `POST api/v1/webhooks/razorpay` (existing, extended) |

Routes and response shapes follow this codebase's conventions (`api/v1`, plain DTOs, problem details) rather than
the brief's examples. `planId` is the plan's id, not its code.

## Angular

`BillingService` (`src/app/core/services/billing.service.ts`) wraps these as `getAutoPayAvailability`,
`startAutoPayCheckout`, `getAutoPaySubscription`, `refreshAutoPaySubscription`, `cancelAutoPaySubscription` and
`getAutoPayInvoices`. The billing screen (`features/billing/billing-list`) is the only caller.

The screen opens `authorizationUrl` in a new tab and stays where it is. While a mandate is `PendingAuthorization` or
`PaymentPending` it calls `refresh` every 5 seconds for 3 minutes, then stops and leaves a "Check status" button. It
shows "Active" only when the server returns it.
