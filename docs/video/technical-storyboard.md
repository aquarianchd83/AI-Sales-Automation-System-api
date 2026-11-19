# UPI AutoPay: how it is built - script and storyboard

Video: `docs/video/out/technical/technical.mp4` - 14 scenes, about 7 min 36 s. Voice: Microsoft David Desktop. Everything below can be re-recorded by a person by reading the narration.

## Scene 1: UPI AutoPay: how it is built

- **Time:** 0:00 - 0:17 (18 s)
- **On screen:** Title card.

> This is a developer walkthrough of the Razorpay UPI AutoPay integration. We will cover the architecture, how webhooks are handled, how payments and credits are protected from duplication, what is tested, and how to roll it out.

## Scene 2: Four design rules

- **Time:** 0:17 - 0:45 (27 s)
- **On screen:** Four rule cards.

> Four rules shaped the design. Razorpay debits the customer, and nothing of ours ever initiates a debit. The browser proves nothing: plan access and credits follow a signed webhook or Razorpay's own API, never a redirect. The existing wallet ledger is reused, so there is no second balance. And the whole feature is off by default.

## Scene 3: Architecture

- **Time:** 0:45 - 1:20 (35 s)
- **On screen:** Three flow lanes: tenant request, Razorpay callback, and what a payment means.

> Here is the shape of it. A tenant request goes from the billing screen through a thin controller to the AutoPay service, and from there through the gateway interface to Razorpay. The other direction is the webhook. The endpoint verifies and stores the event, a Hangfire job picks it up, and the event processor applies it. What a payment means for the tenant is kept apart, in AutoPay entitlements: the payment record, the plan period, the wallet credits and the notice.

## Scene 4: Data model

- **Time:** 1:20 - 1:54 (34 s)
- **On screen:** Table of five tables and their changes.

> The data model adds two tables and extends three. Razorpay plans are immutable, so one row maps each exact charge to a Razorpay plan. Each mandate attempt is a row that is never deleted. The existing subscription row points at the mandate behind the plan. Payments gain the gateway's ids and a succeeded or failed status. Webhook events keep their whole payload and a processing status. No UPI id, PIN or bank detail is stored.

## Scene 5: Checkout, on the server

- **Time:** 1:54 - 2:30 (36 s)
- **On screen:** Five checkout steps.

> Checkout accepts only a plan id. The amount is worked out on the server from the country price plus tax, in paise. A free plan, a tenant not billed in rupees, or a price over the UPI limit is refused before anything is created at Razorpay. Any open mandate is checked against Razorpay first. A running one blocks the checkout, and a waiting one for the same amount is simply returned. Only then is the plan created, if it is new, and the subscription.

## Scene 6: Receiving a webhook

- **Time:** 2:30 - 2:55 (26 s)
- **On screen:** Five webhook intake steps.

> The webhook endpoint reads the raw bytes and verifies the HMAC signature with a constant time comparison. It de-duplicates on the event id with a unique index, stores the whole payload as received, and only then answers 200 and queues a job. Nothing is applied inside the request, so a failure while applying can never lose an event.

## Scene 7: Processing, in any order

- **Time:** 2:55 - 3:27 (32 s)
- **On screen:** Five rules for out-of-order events.

> Razorpay does not guarantee order, so the processor does not assume it. Every subscription event carries the whole subscription as it stood when raised. A newer event is applied as it is. An older one, or one from the same second, is not trusted, and Razorpay is asked directly. An ended mandate is never brought back. And a charge that arrives late is still recorded, because the money was taken.

## Scene 8: Exactly once: four layers

- **Time:** 3:27 - 4:06 (39 s)
- **On screen:** Four layered guards.

> Duplicates are stopped at four independent layers, all in the database or the ledger rather than in memory. The event id is unique. An event already applied is never applied again. The payment table has a unique index on provider and payment id, so the same payment through two events, two workers, or the reconciliation job is still one row. And the ledger grants each operation key once, built from the Razorpay payment id. The payment and the plan change are saved together, and the credits follow, safely repeatable.

## Scene 9: What each event does

- **Time:** 4:06 - 4:42 (36 s)
- **On screen:** Event effects table.

> This table summarises the events. Authenticated and activated change only the mandate, because a mandate is not a payment. A charged event records the payment, extends the paid period, adds the credits once and tells the tenant. Pending leaves the plan alone and applies the grace days. Halted marks the plan past due. Cancelled keeps what was paid for. And a failed payment is recorded with its reason, matched to the mandate through its invoice, and adds nothing.

## Scene 10: Background jobs

- **Time:** 4:42 - 5:17 (34 s)
- **On screen:** Four job cards.

> Four jobs support this. The webhook job applies one stored event. A retry pass every five minutes applies events that no job picked up, and failed events when they are due, with a wait that doubles up to six hours, for ten attempts. A daily reconciliation compares every live mandate with Razorpay, and recovers a missed payment from its invoices. And an hourly pass ends plans whose mandate has stopped paying. None of them ever starts a debit.

## Scene 11: The Angular screen

- **Time:** 5:17 - 5:48 (31 s)
- **On screen:** Four points about the billing screen.

> On the screen, Subscribe sends only the plan id and opens Razorpay in a new tab, so the billing page stays where it is. While the mandate is waiting, the page asks the server every five seconds for three minutes, and the server asks Razorpay. Active appears only when the server says so. After that, a Check status button remains. Plan buttons are disabled while a mandate is charging.

## Scene 12: Tests

- **Time:** 5:48 - 6:23 (35 s)
- **On screen:** Test counts and a caution.

> There are two thousand eight hundred and one backend tests, one hundred and ninety seven of them new, and one thousand five hundred and ninety three screen tests, thirty two new. They cover duplicates, races, out of order events, failures and retries. But there are zero calls to Razorpay. A fake plays Razorpay, with real services, the real ledger and a relational database underneath. Two workers racing was exercised only on SQLite.

## Scene 13: Rollout

- **Time:** 6:23 - 6:55 (32 s)
- **On screen:** Five rollout steps.

> For rollout, deploy the API first. The migration applies at start-up and nothing changes yet. Then deploy the Angular app. Configure Razorpay in test mode with the keys and the webhook, run the thirteen step manual checklist, and only then switch the feature on. Go live with one small real mandate. To roll back, set the switch to false. Existing mandates keep running and their webhooks are still processed.

## Scene 14: Unverified assumptions and known gaps

- **Time:** 6:55 - 7:36 (41 s)
- **On screen:** Four rows of caveats.

> Finally, what is not yet known. We still need to see real UPI webhook payloads, to confirm which events arrive and in what order, and that a failed payment always names its invoice. The account needs Subscriptions and UPI enabled, and cancel at cycle end must be confirmed for UPI. Refunds, credit pack payments, pause from the app and switching between paid plans are not built. Two decisions remain: the grace days, and the GST invoice. All of this is written up in the documents folder. Thank you.
