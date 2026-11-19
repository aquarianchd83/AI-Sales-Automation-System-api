# Razorpay UPI AutoPay - Testing

What is covered by automated tests, what is not, and what has to be checked by hand against Razorpay before this is
switched on for tenants.

## 1. The short version

- Automated tests cover the application's own logic thoroughly, against a Razorpay that lives in memory.
- **No automated test talks to Razorpay.** Nothing here has been run against Razorpay's test mode or live mode.
- A real UPI mandate, a real recurring debit and settlement can only be verified on a Razorpay account with
  Subscriptions and UPI enabled. That is the checklist in section 4, and it has not been done.

Compiling and passing these tests does not show that Google Pay AutoPay works in production.

## 2. Automated tests

### Backend (xUnit, EF Core on in-memory SQLite)

Run from the repository root:

```bash
dotnet test tests/WhatsAppSalesAutomation.Tests
```

Result on `feature/razorpay-autopay`: **2,801 passed, 0 failed.** 197 of them are new for AutoPay.

| File | Cases | Covers |
|---|---|---|
| `RazorpayAutoPaySchemaTests` | 9 | The database's own guards: one row per gateway payment, one waiting authorisation per tenant, one Razorpay plan per charge, defaults. |
| `RazorpaySubscriptionGatewayTests` | 10 | The HTTP side of plans and subscriptions: paths, request bodies, Razorpay's snake_case and nulls read back, its error message surfaced. Uses a stub `HttpMessageHandler`. |
| `AutoPayServiceTests` | 71 | Checkout (amount from the database plus tax in paise, UPI limit, currency, free plan, switch off, missing keys), reuse and replacement of a waiting mandate, races, Razorpay refusing, reading, refresh, tenant isolation, cancel, the status shown, the simulated path standing aside. |
| `RazorpaySubscriptionWebhookTests` | 56 | Store-then-process, signature, duplicates, every lifecycle event, events not ours, any order, late events, failures kept and retried, backoff, giving up, reconciliation, abandoned authorisations. |
| `AutoPayEntitlementsTests` | 48 | Payment row and tax split, plan and paid-through date, credits through the real ledger, exactly-once under repeats, two events, two workers and a failing wallet, month after month, amount mismatch, failed payments, mandate changes, lapsed plans, payment recovery, refund block, payment history, invoices. |
| `AiUsageServiceTests` (3 added) | 3 | The grace days after a paid period for a plan a mandate pays for. |

How the brief's required unit tests map:

| Required | Where |
|---|---|
| Plan amount validation | `AutoPayServiceTests`: paise from price plus tax, over the UPI limit, free plan, no price, wrong amount from Razorpay |
| Tenant authorisation | `AutoPayServiceTests.One_tenant_cannot_read_refresh_or_cancel_anothers_mandate`; `AutoPayEntitlementsTests.One_tenants_payment_never_reaches_anothers_wallet` |
| Subscription creation | `AutoPayServiceTests`, Checkout sections |
| Webhook signature validation | `RazorpaySignatureTests` (existing); `RazorpaySubscriptionWebhookTests.An_event_with_a_bad_signature_is_neither_stored_nor_queued` |
| Duplicate webhook handling | `The_same_event_delivered_five_times_is_stored_and_queued_once`; `Processing_the_same_stored_event_again_does_nothing` |
| Duplicate payment protection | `AutoPayEntitlementsTests`, "Exactly once" section; `RazorpayAutoPaySchemaTests.One_gateway_payment_can_be_recorded_only_once` |
| Wallet credit allocation | `AutoPayEntitlementsTests`: credits through the ledger linked to the payment, month after month, the calendar job skipping |
| Subscription cancellation | `AutoPayServiceTests`, Cancelling section; `A_cancelled_mandate_keeps_the_paid_for_plan...` |
| Subscription renewal | `Each_months_payment_adds_its_own_credits_and_moves_the_paid_through_date` |
| Payment failure handling | `AutoPayEntitlementsTests`, "A failed payment" and "The paid plan runs out" sections |

The brief's "integration tests" (test-mode subscription creation, authorisation, activation, recurring charge,
failure, cancellation, webhooks, reconciliation) are covered here **with the gateway mocked**: `FakeSubscriptionGateway`
plays Razorpay, and real services, the real ledger and a real relational database (SQLite) run underneath. They are
not run against Razorpay.

### Frontend (Karma, Jasmine, headless Chrome)

Run from the Angular repository:

```bash
npx ng test --watch=false --browsers=ChromeHeadlessCI
```

Result on `feature/razorpay-autopay`: **1,593 passed, 0 failed.** 32 are new, in `billing-list-autopay.spec.ts`:
Subscribe sends only the plan id and opens Razorpay; the mandate shows as pending, never active, until the server
says so; polling starts, stops on confirmation, on error, on timeout and on leaving the page; all nine statuses;
failure notices; cancel with confirmation; plan buttons disabled under a running mandate; failed payments in the
history; invoices.

## 3. What the automated tests do not show

| Not covered | Why it matters |
|---|---|
| Any call to Razorpay | Field names, required fields and error shapes are taken from Razorpay's documentation, not from a response. |
| Real webhook payloads | The test payloads are modelled on Razorpay's published samples, which are for cards. UPI payloads may differ. |
| SQL Server concurrency | Tests run on SQLite, which does not generate row versions. The unique indexes were created on SQL Server (LocalDB) and the migration applied and rolled back there, but two workers racing was exercised only on SQLite. |
| Hangfire | The jobs are thin and untested; that they are registered and fire on schedule has not been observed. |
| The HTTP layer | Controllers, routing, role checks and the webhook endpoint's raw-body handling are not driven end to end for the new endpoints. |
| The screen in a browser | Component specs only. It has not been opened against a running API. |
| The settings screen | That the Platform Admin Console's settings page shows `Razorpay:AutoPayEnabled` has not been checked. |
| Email and WhatsApp delivery of the new notices | The notifier is a recording fake in these tests. In-app only was requested (`AlsoWhatsApp: false`). |

## 4. Manual verification on Razorpay test mode

Prerequisites: [razorpay-autopay-setup.md](razorpay-autopay-setup.md) sections 1 to 3 done with **test** keys, the API
reachable from the internet for webhooks (a tunnel will do), `Razorpay:AutoPayEnabled` = `true` in that environment,
and a tenant whose country is India.

Razorpay's documentation describes testing subscriptions with test **cards**; it does not describe a UPI test flow
for subscriptions. If UPI cannot be exercised in test mode, steps 2 to 9 can be run with a test card to prove
everything except the UPI leg, which then has to be proven in live mode with a real mandate of a small amount.

| # | Do | Expect |
|---|---|---|
| 1 | Open Billing as a tenant Admin | The paid plan's button reads "Subscribe now". |
| 2 | Subscribe and confirm | Razorpay's page opens in a new tab. The card shows "Pending authorization". A `RazorpaySubscriptions` row exists with status `Created`. In Razorpay: one plan at the expected amount in paise, one subscription. |
| 3 | Complete the authorisation on Razorpay | Within seconds the card moves to "Payment pending" or "Active" without a reload. |
| 4 | Look at `RazorpayWebhookEvents` | `subscription.authenticated`, `activated`, `charged` rows, each `Completed`. Note the order they arrived in and whether any `Note` says "out of order". |
| 5 | Look at the tenant | `Payments`: one row, provider `Razorpay`, status `Succeeded`, correct total and tax lines. `Subscriptions`: on the plan, `Active`, period set, linked to the mandate. Wallet: the plan's monthly credits added once; one ledger entry keyed `rzp-charge:<payment id>`. One "Payment received" notification. |
| 6 | In the Razorpay Dashboard, resend the `subscription.charged` webhook several times | Each returns 200. No new payment, no extra credits, no second notification. |
| 7 | In the Razorpay Dashboard, use "Charge this now" on the subscription (allowed within 3 days in test mode) | A second payment, a second lot of credits, the paid-through date moved on. |
| 8 | Simulate a failed charge (Razorpay lets a test charge be set to fail) | The card shows "Past due" with the retry notice. A `Failed` payment row with a reason, if Razorpay sent `payment.failed`. No credits. Record exactly which events arrived. |
| 9 | Click "Invoices" | The invoices list, each with a working "Open invoice" link. Note what that link shows. |
| 10 | Cancel automatic payment and confirm | The card shows "Cancellation scheduled". The plan is still active. In Razorpay the subscription is set to cancel at cycle end. Confirm Razorpay accepted cancel-at-cycle-end. |
| 11 | Start a second tenant's checkout and close Razorpay's page without paying | The card stays "Pending authorization"; polling stops after 3 minutes; "Check status" still works. "Cancel request" cancels it at Razorpay. |
| 12 | Stop the API, trigger a charge in Razorpay, start the API and run `autopay-reconciliation` from the Hangfire dashboard | Either Razorpay's redelivery or the reconciliation records the payment, once. |
| 13 | Send the webhook URL a request with a wrong signature | 400, and no row stored. |

Record what Razorpay actually sent in steps 4 and 8. Four assumptions in
[razorpay-autopay-webhooks.md](razorpay-autopay-webhooks.md), section 9, depend on it.

## 5. Before live

- Repeat steps 1 to 6 and 10 in live mode with one real, small mandate approved in Google Pay or another UPI app.
- Wait for, or trigger, the second month's debit and confirm it is taken without any action by the tenant.
- Confirm the money settles to the bank account.
- Decide whether Razorpay's invoice is acceptable as the GST invoice, or whether the platform must issue its own.
- Never use real customer banking details or UPI credentials in automated tests.
