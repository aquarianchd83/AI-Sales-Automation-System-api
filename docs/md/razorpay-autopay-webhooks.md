# Razorpay UPI AutoPay - Webhooks

How a Razorpay webhook becomes a change in this system. For configuring the webhook in the Razorpay Dashboard, see
[razorpay-autopay-setup.md](razorpay-autopay-setup.md), section 3.

## 1. Endpoint

`POST api/v1/webhooks/razorpay`

- Anonymous: Razorpay cannot send a bearer token. The gate is the signature.
- Rate limited per IP (the `webhook` policy) and capped at 256 KB.
- Answers **200** for anything correctly signed, including a redelivery and an event that is ignored. Answers
  **400** for a bad signature or when no webhook secret is saved.

## 2. Receiving

In the request, in this order:

1. Read the raw body as bytes. The signature is over these exact bytes, never a re-serialised copy.
2. Compute HMAC-SHA256 of the body with the saved webhook secret and compare it with `X-Razorpay-Signature` in
   constant time. On mismatch nothing is stored.
3. Take `X-Razorpay-Event-Id` as the event's identity (a hash of the body when it is missing). If a row with that id
   exists, answer 200 and stop.
4. Store the event with its whole payload. A unique index on the event id also catches two deliveries arriving at the
   same moment.
5. Route it:

| Event | What happens in the request |
|---|---|
| `subscription.*` | Stored as `Received`, a background job enqueued, 200 returned. Not applied here. |
| `payment.failed` naming an invoice, and not one of the test page's orders | Same. |
| Anything else (`payment.captured`, `order.paid`, `refund.*`, ...) | Applied inline to the platform test page's order, as before; stored as `Completed` or `Ignored`. |

The 200 is sent only after the event is saved. If enqueuing fails, the event is still saved and acknowledged; the
retry pass applies it.

## 3. Processing

`RazorpaySubscriptionEventProcessor`, run by `RazorpayWebhookProcessingJob`:

1. Load the event row. If it is already `Completed` or `Ignored`, stop.
2. Read the stored body: the event name, Razorpay's `created_at`, the subscription entity, and the payment entity if
   there is one.
3. Find the mandate by Razorpay's subscription id.
   - Not found, and the subscription's notes do not say this platform created it: `Ignored`.
   - Not found, but its notes say `source = autopay`: fail, so it is retried (the event overtook the checkout's save).
4. Update the mandate's state (section 4), and save.
5. Pass a payment on:
   - `subscription.charged` with a captured payment: record the payment, set the plan, grant credits.
   - `subscription.charged` with no payment: fail. A charge is never passed as handled without its payment.
   - any event carrying a failed payment: record the failed attempt.
6. If the mandate's status changed, apply what that means for the tenant's plan.
7. Mark the event `Completed`.

Any exception marks the event `Failed`, increments `RetryCount`, and stores the message in `ErrorMessage`. Nothing is
silently dropped.

### `payment.failed`

A `payment.failed` event names the payment and its invoice but not the subscription. The processor fetches the
invoice from Razorpay (`GET /v1/invoices/{id}`), which names the subscription, and records a `Failed` payment row
against that mandate. An invoice that is not a mandate's is `Ignored`.

## 4. Order and duplicates

Razorpay may deliver an event more than once and does not guarantee order. Every subscription event carries the
whole subscription entity as it stood when the event was raised.

| Situation | Handling |
|---|---|
| The same event delivered again | Not stored again (unique event id). |
| The same stored event processed again | Nothing happens (already `Completed`). |
| An event newer than any applied to this mandate | Its subscription snapshot is applied. |
| An event older than, or from the same second as, the last one applied | Not trusted. The subscription is fetched from Razorpay and that state is applied. If Razorpay cannot be reached, the event fails and is retried. |
| Any event for a mandate that has ended | The mandate's state is left alone. `Cancelled`, `Completed` and `Expired` are final. |
| A `charged` event arriving after a cancellation | The payment is still recorded and credited: the money was taken. |
| The same payment in two different events | Recorded once (unique payment id), credited once (ledger key). |

## 5. What each event does

| Event | Mandate | Tenant's plan, payment and wallet |
|---|---|---|
| `subscription.authenticated` | `Authenticated`; customer id and method recorded | Nothing. A mandate is not a payment. |
| `subscription.activated` | `Active` | Nothing by itself. |
| `subscription.charged` | `Active`; billing cycle and paid count updated | Payment recorded; plan set and paid through to the cycle's end; credits granted once; tenant notified. |
| `subscription.pending` | `Pending` | Plan unchanged (grace applies). Tenant notified once per cycle. |
| `subscription.halted` | `Halted` | Plan `PastDue`. Tenant notified. No credits. |
| `subscription.paused` | `Paused` | Plan unchanged. Tenant notified. |
| `subscription.resumed` | `Active` (Razorpay sends status `active`) | Plan back to `Active` if it was `PastDue`. |
| `subscription.cancelled` | `Cancelled` | Plan kept to the end of the paid period; marked not renewing. Tenant notified. |
| `subscription.completed` | `Completed` | As cancelled. |
| `subscription.updated` | Whatever the snapshot says | Nothing unless the status changed. |
| `payment.failed` (with invoice) | Unchanged | A `Failed` payment row with the reason. No credits, no notice. |
| `payment.authorized`, `payment.captured` | - | Not used for subscriptions. |

A plan that is past its paid period plus the grace days, with a mandate that is not paying, is moved to the Trial
plan by the hourly `autopay-lapsed-plans` job, not by a webhook.

## 6. Retries

`razorpay-webhook-retry` runs every 5 minutes and applies:

- events still `Received` after 2 minutes (the job that should have applied them was lost), and
- `Failed` events that are due: the wait after the n-th failure is 5 minutes doubled n-1 times, capped at 6 hours.

After `WebhookMaxAttempts` (10) failures an event is left `Failed` and a `Critical` log entry is written. It is never
marked done and never deleted. To retry it after fixing the cause, set its `RetryCount` to 0.

Applying an event is safe to repeat at every step, which is what makes retrying safe.

## 7. Reconciliation

`autopay-reconciliation` runs daily and is the backstop for webhooks that never arrive. For each mandate that has not
ended and was made with the current keys' mode:

1. Fetch the subscription from Razorpay and apply its state. A change is logged and applied to the tenant's plan.
2. If the mandate is still waiting to be authorised after 72 hours, cancel it at Razorpay.
3. If Razorpay counts more payments than are recorded: fetch the subscription's invoices and, for each paid invoice
   whose payment is not on record, run the same code a `subscription.charged` webhook reaches. A payment whose
   webhook is merely late is therefore not counted twice.
4. If payments are still unaccounted for, log an error.

One mandate that cannot be checked does not stop the others.

## 8. Where to look

| Question | Answer |
|---|---|
| Did Razorpay's event arrive? | `RazorpayWebhookEvents` by `EventId`, `SubscriptionId` or `PaymentId`. |
| Was it applied? | `ProcessingStatus`, `ProcessedAtUtc`, `Note` (says what changed and whether Razorpay was consulted). |
| Why not? | `ErrorMessage`, `RetryCount`. |
| What does Razorpay say now? | `POST billing/autopay/subscriptions/{id}/refresh`, or the Razorpay Dashboard. |
| What was credited? | `QuotaLedgerEntries` with `OperationKey = 'rzp-charge:' + payment id`; `Payments` by `ProviderPaymentId`. |

## 9. Unverified against a live account

The handling above follows Razorpay's published documentation and sample payloads as read on 2026-11-16. Not yet
confirmed with real deliveries:

- which events Razorpay actually sends, and in what order, for a **UPI** mandate (its samples are for cards);
- whether `subscription.pending` and `subscription.halted` ever carry a payment entity (the samples do not);
- that `payment.failed` for a subscription debit always carries an `invoice_id`;
- that `X-Razorpay-Event-Id` is present on every delivery.
