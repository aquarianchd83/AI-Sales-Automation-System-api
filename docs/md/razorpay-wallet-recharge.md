# Wallet recharge with Razorpay

Tenants buy wallet credits with Razorpay (UPI, cards, net banking, wallets). The wallet is the existing single credit wallet - there is no separate Razorpay wallet. This page covers how it works, how to configure it, and how to test it.

## The flow

1. **Quote** - `GET /api/v1/wallet/recharge/quote?amount=500`. The server prices the amount: GST on top (existing `Tax` options), total payable, credits bought, balance after. Creates nothing.
2. **Create order** - `POST /api/v1/wallet/recharge/create-order` `{ "amount": 500, "currency": "INR" }`. The tenant comes from the JWT, never from the body. The server validates min/max, re-prices, creates a Razorpay order (`POST /v1/orders`) and stores a `PaymentTransactions` row (`Created`). It returns the order id and the *public* key id for Checkout.
3. **Checkout** - Angular opens Razorpay Checkout for that order. A success callback only carries `razorpay_order_id`, `razorpay_payment_id`, `razorpay_signature`.
4. **Verify** - `POST /api/v1/wallet/recharge/verify-payment` with those three. The server checks the HMAC signature, that the order belongs to the caller's tenant, then fetches the payment from Razorpay and checks order id, amount and currency. An `authorized` payment is captured. Only a `captured` payment is credited.
5. **Webhook** - `POST /api/v1/webhooks/razorpay` (the existing endpoint). Signature is verified over the raw body. `payment.authorized`, `payment.captured`, `order.paid`, `payment.failed`, `refund.created`, `refund.processed` are applied to the matching top-up. This is what credits the wallet when the browser was closed.
6. **Reconciliation** - a Hangfire job (`wallet-recharge-reconciliation`, every 5 minutes) asks Razorpay (`GET /v1/orders/{id}/payments`) about top-ups that never heard back, credits genuine payments, and marks checkouts with no payment `Expired` after `Wallet:AbandonAfterMinutes`. `GET .../payment-status/{id}` does the same check on demand.

The browser's success callback never credits anything. The balance on screen changes only after the server says the top-up is `Credited`.

## Exactly-once crediting

Verify, webhook and reconciliation all end in one method (`WalletRechargeService.CompleteAsync`), which in **one database transaction** marks the top-up `Credited`, writes the `Payments` row and writes the wallet ledger entry. Three independent guards stop a second credit:

- unique index on `PaymentTransactions.RazorpayPaymentId` and on `Payments (Provider, ProviderPaymentId)` - a payment can be applied once;
- the ledger entry is keyed `topup:{transactionId}` and is skipped if it exists;
- a top-up already `Credited` is never credited again (a second, different payment on the same order is refused and needs a manual refund).

Duplicate webhooks are also recognised by the unique `RazorpayWebhookEvents.EventId`.

## Ledger

Credits are a purchase grant in the existing `QuotaLedgerEntry` ledger (`EntryType = Purchase`, `ReferenceType = Payment`, `ReferenceId` = the `Payments` row, note `RAZORPAY wallet top-up pay_...`), with `BalanceAfter` (previous balance = `BalanceAfter - UnitsDelta`). Entries are never edited. A refund (from the Razorpay dashboard, seen through the webhook) takes the refunded share of credits back with a new `RefundClawback` entry, once per refunded amount. Credits already spent cannot be clawed back; only what is left is taken.

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

The Razorpay MCP server is a dashboard assistant for development (looking up orders and payments while testing). The wallet recharge does **not** depend on it: order creation, signature and webhook verification, capture and reconciliation use Razorpay's REST API from the backend, with credentials kept server-side.
