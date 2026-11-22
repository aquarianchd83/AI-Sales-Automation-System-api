# Wallet recharge with Razorpay

Tenants buy wallet credits with Razorpay (UPI, cards, net banking, wallets). The wallet is the existing single credit wallet - there is no separate Razorpay wallet. This page covers how it works, how to configure it, and how to test it.

## The flow

1. **Quote** - `GET /api/v1/wallet/recharge/quote?amount=500`. The server prices the amount: GST on top (existing `Tax` options), total payable, credits bought, balance after. Creates nothing.
2. **Create order** - `POST /api/v1/wallet/recharge/create-order` `{ "amount": 500, "currency": "INR" }`. The tenant comes from the JWT, never from the body. The server validates min/max, re-prices, creates a Razorpay order (REST `POST /orders`) and stores a `PaymentTransactions` row (`Created`). It returns the order id and the *public* key id for Checkout.
3. **Checkout** - Angular opens Razorpay Checkout for that order. A success callback only carries `razorpay_order_id`, `razorpay_payment_id`, `razorpay_signature`.
4. **Verify** - `POST /api/v1/wallet/recharge/verify-payment` with those three. The server checks the HMAC signature, that the order belongs to the caller's tenant, then fetches the payment from Razorpay and checks order id, amount and currency. An `authorized` payment is captured. Only a `captured` payment is credited.
5. **Webhook** - `POST /api/v1/webhooks/razorpay` (the existing endpoint). Signature is verified over the raw body. `payment.authorized`, `payment.captured`, `order.paid`, `payment.failed`, `refund.created`, `refund.processed` are applied to the matching top-up. This is what credits the wallet when the browser was closed.
6. **Reconciliation** - a Hangfire job (`wallet-recharge-reconciliation`, every 5 minutes) asks Razorpay (MCP tool `fetch_order_payments`) about top-ups that never heard back, credits genuine payments, and marks checkouts with no payment `Expired` after `Wallet:AbandonAfterMinutes`. `GET .../payment-status/{id}` does the same check on demand. An `Expired` top-up is still asked about hourly for 3 days, so a UPI payment completed late is credited even if its webhook was lost.

The browser's success callback never credits anything. The balance on screen changes only after the server says the top-up is `Credited`.

## Credit packs ("Buy extra credits")

The **Buy** buttons on the wallet screen pay through the same flow. `POST /api/v1/wallet/recharge/credit-packs/{packId}/create-order` prices the pack on the server (its price for the tenant's country plus GST - the same price the screen shows), needs an active plan as before, and creates the Razorpay order for the pack's credits. The `PaymentTransactions` row carries `CreditPackId`; Checkout, verify, webhook and reconciliation are exactly the top-up's, and on capture the wallet gets the pack's credits once, the `Payments` row is a `CreditPack` payment for that pack, and the tenant gets a "credits added" notice.

The old `POST /api/v1/billing/credit-packs/{id}/purchase` (credits with no payment) is refused once Razorpay keys are saved; it only works on an install with no payment gateway.

## Exactly-once crediting

Verify, webhook and reconciliation all end in one method (`WalletRechargeService.CompleteAsync`), which in **one database transaction** marks the top-up `Credited`, writes the `Payments` row and writes the wallet ledger entry. Three independent guards stop a second credit:

- unique index on `PaymentTransactions.RazorpayPaymentId` and on `Payments (Provider, ProviderPaymentId)` - a payment can be applied once;
- the ledger entry is keyed `topup:{transactionId}` and is skipped if it exists;
- `PaymentTransactions.RowVersion` (optimistic concurrency): two deliveries writing the same top-up at once cannot both win; the loser re-reads the row and decides again;
- a top-up already `Credited` is never credited again. A second, different payment on the same order (or a captured payment that does not match the order) is not credited and raises a `WalletPaymentNeedsAttention` operator alert: a captured one must be refunded from the Razorpay dashboard, an authorized one is released by Razorpay on its own.

`payment.authorized` with the payment's details is settled straight away: the server captures it and credits the wallet, so an account without auto-capture does not wait for reconciliation.

Duplicate webhooks are also recognised by the unique `RazorpayWebhookEvents.EventId`.

## Ledger

Credits are a purchase grant in the existing `QuotaLedgerEntry` ledger (`EntryType = Purchase`, `ReferenceType = Payment`, `ReferenceId` = the `Payments` row, note `RAZORPAY wallet top-up pay_...`), with `BalanceAfter` (previous balance = `BalanceAfter - UnitsDelta`). Entries are never edited. A refund (from the Razorpay dashboard, seen through the webhook) takes the refunded share of credits back with a new `RefundClawback` entry, once per refunded amount. Credits already spent cannot be clawed back; only what is left is taken. The clawback entry is keyed `refund:{transactionId}:{refundedPaise}`, so `refund.created` and `refund.processed` for the same refund take credits back once between them, even when they arrive together.

## Configuration

Razorpay keys are in the `AppSettings` table (Platform Admin Console > Razorpay; the secret is encrypted and never sent to a browser). **Test or Live is simply which key pair is saved** (`rzp_test_...` / `rzp_live_...`); each transaction records its mode. No code change or redeploy is needed to go live.

| Setting | Default | Meaning |
|---|---|---|
| `Razorpay:KeyId` / `Razorpay:KeySecret` | - | API keys |
| `Razorpay:WebhookSecret` | - | Verifies webhook signatures |
| `Wallet:MinRechargeInr` | 100 | Smallest top-up, rupees before tax |
| `Wallet:MaxRechargeInr` | 50000 | Largest top-up |
| `Wallet:CreditsPerRupee` | 1 | Credits one rupee buys (the wallet holds credits) |
| `Wallet:RechargePresets` | 100,500,1000,2000,5000 | Quick-pick amounts |
| `Wallet:BusinessName` | Sales Automation | Name shown in Checkout |
| `Wallet:AbandonAfterMinutes` | 60 | When an unpaid order counts as abandoned |

Tax comes from the existing `Tax` section (India: 18% GST, CGST+SGST or IGST by state). Only tenants billed in INR can recharge.

### Razorpay dashboard

Webhook URL: `https://<api-host>/api/v1/webhooks/razorpay`, with the same secret saved as `Razorpay:WebhookSecret`. Events: `payment.authorized`, `payment.captured`, `payment.failed`, `order.paid`, `refund.created`, `refund.processed` (subscription events stay enabled for AutoPay).

## Database

Migration `AddWalletRechargeTransactions` adds `PaymentTransactions` (tenant-owned, decimal(18,2) money, unique `InternalTransactionId`, `RazorpayOrderId`, filtered-unique `RazorpayPaymentId`). Webhook events reuse the existing `RazorpayWebhookEvents` table (unique `EventId`, raw payload, processing status) rather than a duplicate `PaymentWebhookEvents`.

## Test mode

1. Save `rzp_test_` keys and the webhook secret; point a test webhook (a tunnel to your machine) at the URL above.
2. Recharge as a tenant Admin at `/billing/wallet/recharge`.
3. In Checkout use UPI id `success@razorpay` (success) or `failure@razorpay` (failure), or Razorpay's test cards.
4. Close the browser right after paying: the webhook (or the 5-minute reconciliation) still credits the wallet.

Automated tests: `dotnet test --filter WalletRecharge` (API) and `ng test --include '**/wallet-recharge/**'` (Angular).

## Razorpay MCP

The backend talks to Razorpay through **Razorpay's MCP server** (`https://mcp.razorpay.com/mcp`), as an MCP client (`RazorpayMcpClient`: JSON-RPC 2.0 over Streamable HTTP - `initialize`, `notifications/initialized`, `tools/call`; replies read as JSON or as an event stream). It authenticates with Razorpay's merchant token, `Authorization: Basic base64(Razorpay:KeyId:Razorpay:KeySecret)`, built from the saved keys on every request; the key secret never reaches the browser. One MCP session is opened per key pair per request/job scope and reopened if the server forgets it.

| When | MCP tool | Arguments | What it is for |
|---|---|---|---|
| Verify | `fetch_payment` | `payment_id` | Razorpay's own record: order id, amount, currency, status |
| Reconciliation and status check | `fetch_order_payments` | `order_id` | Every attempt on an order, when no callback or webhook arrived |
| Settings key check | `fetch_all_orders` | `count: 1` | Checks the saved keys work |

Razorpay's hosted MCP server is **read-only**: it has no `create_order`, `capture_payment` or `create_refund` (calling one fails with "tool 'create_order' not found: tool not found"). So the writes always use the REST API (`RazorpayGateway`): creating an order (`POST /orders`), capturing an `authorized` payment (`POST /payments/{id}/capture`) and refunds (`POST /payments/{id}/refund`). If the configured MCP server is missing one of the read tools above, that read falls back to REST too.

A tool that fails (`isError`) comes back as a normal failed result in Razorpay's words; nothing is thrown, and the wallet flow treats it exactly like a failed API call (pending, retried by reconciliation).

**Not over MCP:**
- Signatures. The checkout signature and the webhook `X-Razorpay-Signature` are HMACs checked in the backend with the key secret / `Razorpay:WebhookSecret`; they are not Razorpay calls.
- Creating orders, capture and refunds (see above).
- UPI AutoPay plans, subscriptions and invoices. Razorpay's MCP server has no tools for them, so those keep using the REST API (`RazorpayGateway`).

Inbound, Razorpay still calls `POST /api/v1/webhooks/razorpay` for every event.

### Settings

| Setting | Default | Meaning |
|---|---|---|
| `Razorpay:Transport` | `Mcp` | `Mcp` = through the MCP server; `Rest` = straight to `https://api.razorpay.com/v1/` (a switch for an MCP outage, no redeploy) |
| `Razorpay:McpUrl` | `https://mcp.razorpay.com/mcp` | MCP endpoint (https only), e.g. a self-hosted Razorpay MCP server |

The Razorpay category is hidden on the generic Settings screen; change these through the settings API (`PUT /api/v1/settings/Razorpay` with `{ "values": { "Razorpay:Transport": "Rest" } }` - only the keys sent are changed) until the Razorpay screen gets a control for them.

Automated tests: `dotnet test --filter RazorpayMcp` runs the gateway against a fake MCP server (session, auth header, event-stream replies, tool errors, the REST switch).
