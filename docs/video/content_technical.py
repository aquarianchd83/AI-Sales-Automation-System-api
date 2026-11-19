# -*- coding: utf-8 -*-
"""The developer walkthrough (about eight minutes). Facts here are taken from the code and the documents in docs/razorpay-autopay-*.md."""

TITLE = "UPI AutoPay: how it is built"
SLUG = "technical"
VOICE = "Microsoft David Desktop"

ARCH = """
<div class="lane"><span class="lane-label">Tenant request</span>
  <div class="node">Angular billing screen</div><div class="arrow">&rarr;</div>
  <div class="node">BillingAutoPayController</div><div class="arrow">&rarr;</div>
  <div class="node">AutoPayService</div><div class="arrow">&rarr;</div>
  <div class="node">IRazorpayGateway</div><div class="arrow">&rarr;</div>
  <div class="node ext">Razorpay</div></div>
<div class="lane"><span class="lane-label">Razorpay calls back</span>
  <div class="node ext">Razorpay</div><div class="arrow">&rarr;</div>
  <div class="node">Webhook endpoint</div><div class="arrow">&rarr;</div>
  <div class="node db">Stored event</div><div class="arrow">&rarr;</div>
  <div class="node">Hangfire job</div><div class="arrow">&rarr;</div>
  <div class="node">Event processor</div></div>
<div class="lane"><span class="lane-label">What a payment means</span>
  <div class="node">Event processor</div><div class="arrow">&rarr;</div>
  <div class="node">AutoPayEntitlements</div><div class="arrow">&rarr;</div>
  <div class="node db">Payments</div>
  <div class="node db">Subscription period</div>
  <div class="node db">Wallet ledger</div>
  <div class="node">Notifier</div></div>
"""

SLIDES = [
    dict(
        kind="title",
        title="UPI AutoPay: how it is built",
        subtitle="Architecture, webhook handling, exactly-once guarantees, tests and rollout",
        tag="Developer walkthrough",
        visual="Title card.",
        narration=(
            "This is a developer walkthrough of the Razorpay UPI AutoPay integration. We will cover the architecture, how webhooks "
            "are handled, how payments and credits are protected from duplication, what is tested, and how to roll it out."
        ),
    ),
    dict(
        kind="cards",
        title="Four design rules",
        cards=[
            ("Razorpay debits, we never do", "No job of ours initiates a debit. We create the mandate and react to what Razorpay reports."),
            ("The browser proves nothing", "Plan access and credits follow a signed webhook or Razorpay's own API, never a redirect."),
            ("Reuse the wallet", "Credits go through the existing ledger. There is no second balance."),
            ("Off by default", "The switch Razorpay:AutoPayEnabled is false until an operator turns it on."),
        ],
        visual="Four rule cards.",
        narration=(
            "Four rules shaped the design. Razorpay debits the customer, and nothing of ours ever initiates a debit. The browser proves "
            "nothing: plan access and credits follow a signed webhook or Razorpay's own API, never a redirect. The existing wallet "
            "ledger is reused, so there is no second balance. And the whole feature is off by default."
        ),
    ),
    dict(
        kind="diagram",
        title="Architecture",
        html=ARCH,
        visual="Three flow lanes: tenant request, Razorpay callback, and what a payment means.",
        narration=(
            "Here is the shape of it. A tenant request goes from the billing screen through a thin controller to the AutoPay service, "
            "and from there through the gateway interface to Razorpay. The other direction is the webhook. The endpoint verifies and "
            "stores the event, a Hangfire job picks it up, and the event processor applies it. What a payment means for the tenant is "
            "kept apart, in AutoPay entitlements: the payment record, the plan period, the wallet credits and the notice."
        ),
    ),
    dict(
        kind="table",
        title="Data model",
        headers=["Table", "What changed"],
        rows=[
            ["RazorpayPlans", "New. One Razorpay plan per exact charge. Razorpay plans cannot be edited."],
            ["RazorpaySubscriptions", "New. One row per mandate attempt, with Razorpay's status and billing cycle. Never deleted."],
            ["Subscriptions", "Adds the mandate behind the plan, and a cancel-at-period-end flag."],
            ["Payments", "Adds Razorpay payment, invoice and subscription ids, method, status and failure reason."],
            ["RazorpayWebhookEvents", "Adds processing status, retry count, error, and the whole payload."],
        ],
        note="No UPI id, PIN or bank detail is stored anywhere.",
        visual="Table of five tables and their changes.",
        narration=(
            "The data model adds two tables and extends three. Razorpay plans are immutable, so one row maps each exact charge to a "
            "Razorpay plan. Each mandate attempt is a row that is never deleted. The existing subscription row points at the mandate "
            "behind the plan. Payments gain the gateway's ids and a succeeded or failed status. Webhook events keep their whole payload "
            "and a processing status. No UPI id, PIN or bank detail is stored."
        ),
    ),
    dict(
        kind="steps",
        title="Checkout, on the server",
        steps=[
            ("1", "Plan id only", "The request carries nothing else"),
            ("2", "Price from the database", "Country price plus tax, in paise"),
            ("3", "Refuse early", "Free, not rupees, or over the UPI limit"),
            ("4", "Check open mandates", "A running one blocks; a waiting one is reused"),
            ("5", "Create at Razorpay", "Plan if new, then the subscription"),
        ],
        note="UPI limit: 15,000 rupees per debit, configurable.",
        visual="Five checkout steps.",
        narration=(
            "Checkout accepts only a plan id. The amount is worked out on the server from the country price plus tax, in paise. "
            "A free plan, a tenant not billed in rupees, or a price over the UPI limit is refused before anything is created at Razorpay. "
            "Any open mandate is checked against Razorpay first. A running one blocks the checkout, and a waiting one for the same amount "
            "is simply returned. Only then is the plan created, if it is new, and the subscription."
        ),
    ),
    dict(
        kind="steps",
        title="Receiving a webhook",
        steps=[
            ("1", "Raw body", "Read the exact bytes received"),
            ("2", "Verify", "HMAC-SHA256, constant-time compare"),
            ("3", "De-duplicate", "Unique index on the event id"),
            ("4", "Store whole", "The full payload, status Received"),
            ("5", "Acknowledge", "200 only after it is saved, then queue the job"),
        ],
        note="Nothing is applied inside the request.",
        visual="Five webhook intake steps.",
        narration=(
            "The webhook endpoint reads the raw bytes and verifies the HMAC signature with a constant time comparison. It de-duplicates on "
            "the event id with a unique index, stores the whole payload as received, and only then answers 200 and queues a job. "
            "Nothing is applied inside the request, so a failure while applying can never lose an event."
        ),
    ),
    dict(
        kind="bullets",
        title="Processing, in any order",
        items=[
            ("Each event carries the whole subscription", "So every event is a full picture of the mandate at that moment."),
            ("Newer than what we have", "Its snapshot is applied as it is."),
            ("Older, or the same second", "Not trusted. Razorpay is asked where the subscription stands now."),
            ("Ended is final", "Cancelled, completed and expired mandates are never brought back."),
            ("A late charge is still recorded", "The money was taken, so it is owed its record and credits."),
        ],
        visual="Five rules for out-of-order events.",
        narration=(
            "Razorpay does not guarantee order, so the processor does not assume it. Every subscription event carries the whole "
            "subscription as it stood when raised. A newer event is applied as it is. An older one, or one from the same second, is not "
            "trusted, and Razorpay is asked directly. An ended mandate is never brought back. And a charge that arrives late is still "
            "recorded, because the money was taken."
        ),
    ),
    dict(
        kind="cards",
        title="Exactly once: four layers",
        cards=[
            ("1. Delivery", "Unique index on the webhook event id. A redelivery is not stored again."),
            ("2. Processing", "A Completed or Ignored event is never applied again."),
            ("3. Payment record", "Unique index on provider and payment id. Two events, two workers or reconciliation give one row."),
            ("4. Credits", "The ledger grants an operation key once: rzp-charge plus the payment id."),
        ],
        note="The payment and the plan change are one save. Credits follow, and are safe to repeat.",
        visual="Four layered guards.",
        narration=(
            "Duplicates are stopped at four independent layers, all in the database or the ledger rather than in memory. The event id "
            "is unique. An event already applied is never applied again. The payment table has a unique index on provider and payment id, "
            "so the same payment through two events, two workers, or the reconciliation job is still one row. And the ledger grants "
            "each operation key once, built from the Razorpay payment id. The payment and the plan change are saved together, and the "
            "credits follow, safely repeatable."
        ),
    ),
    dict(
        kind="table",
        title="What each event does",
        headers=["Event", "Mandate", "Plan, payment and wallet"],
        rows=[
            ["authenticated, activated", "Authenticated, Active", "Nothing. A mandate is not a payment."],
            ["charged", "Active, cycle updated", "Payment recorded, plan paid through, credits once, tenant told."],
            ["pending", "Pending", "Plan unchanged. Grace days apply. Tenant told."],
            ["halted", "Halted", "Plan past due. Tenant told. No credits."],
            ["cancelled, completed", "Ended", "Paid-for plan kept, marked not renewing."],
            ["payment failed", "Unchanged", "A Failed payment row with the reason. No credits."],
        ],
        visual="Event effects table.",
        narration=(
            "This table summarises the events. Authenticated and activated change only the mandate, because a mandate is not a payment. "
            "A charged event records the payment, extends the paid period, adds the credits once and tells the tenant. Pending leaves the "
            "plan alone and applies the grace days. Halted marks the plan past due. Cancelled keeps what was paid for. And a failed payment "
            "is recorded with its reason, matched to the mandate through its invoice, and adds nothing."
        ),
    ),
    dict(
        kind="cards",
        title="Background jobs",
        cards=[
            ("Webhook job", "Enqueued per stored event. Applies it once."),
            ("Retry pass, every 5 minutes", "Takes events no job picked up, and failed ones when due. Wait doubles from 5 minutes to 6 hours, 10 attempts."),
            ("Reconciliation, daily", "Compares every live mandate with Razorpay, recovers missed payments from invoices, cancels abandoned authorisations."),
            ("Lapsed plans, hourly", "Moves a plan to Trial once the mandate has stopped paying and the paid period plus four grace days is over."),
        ],
        note="None of them starts a debit.",
        visual="Four job cards.",
        narration=(
            "Four jobs support this. The webhook job applies one stored event. A retry pass every five minutes applies events that no job "
            "picked up, and failed events when they are due, with a wait that doubles up to six hours, for ten attempts. A daily reconciliation "
            "compares every live mandate with Razorpay, and recovers a missed payment from its invoices. And an hourly pass ends plans whose "
            "mandate has stopped paying. None of them ever starts a debit."
        ),
    ),
    dict(
        kind="bullets",
        title="The Angular screen",
        items=[
            ("Subscribe", "Sends only the plan id, opens Razorpay in a new tab, and stays on the page."),
            ("Status comes from the server", "The page asks the server, which asks Razorpay, every 5 seconds for 3 minutes."),
            ("Active only when confirmed", "Returning from Razorpay changes nothing by itself. Then a Check status button remains."),
            ("Guards", "Plan buttons are disabled while a mandate is charging. Refund buttons are hidden for these payments."),
        ],
        visual="Four points about the billing screen.",
        narration=(
            "On the screen, Subscribe sends only the plan id and opens Razorpay in a new tab, so the billing page stays where it is. "
            "While the mandate is waiting, the page asks the server every five seconds for three minutes, and the server asks Razorpay. "
            "Active appears only when the server says so. After that, a Check status button remains. Plan buttons are disabled while a "
            "mandate is charging."
        ),
    ),
    dict(
        kind="stats",
        title="Tests",
        stats=[
            ("2,801", "backend tests, 197 new"),
            ("1,593", "screen tests, 32 new"),
            ("0", "calls to Razorpay"),
        ],
        note="Razorpay is played by an in-memory fake. Real services, the real ledger and a relational database run underneath.",
        visual="Test counts and a caution.",
        narration=(
            "There are two thousand eight hundred and one backend tests, one hundred and ninety seven of them new, and one thousand five "
            "hundred and ninety three screen tests, thirty two new. They cover duplicates, races, out of order events, failures and "
            "retries. But there are zero calls to Razorpay. A fake plays Razorpay, with real services, the real ledger and a relational "
            "database underneath. Two workers racing was exercised only on SQLite."
        ),
    ),
    dict(
        kind="steps",
        title="Rollout",
        steps=[
            ("1", "Deploy the API", "The migration applies at start-up. Behaviour is unchanged."),
            ("2", "Deploy Angular", "The screen is unchanged while the switch is off."),
            ("3", "Razorpay setup", "Keys, the webhook URL and eleven events, in test mode."),
            ("4", "Run the checklist", "Thirteen manual steps against test mode."),
            ("5", "Switch on, then go live", "One small real mandate first."),
        ],
        note="Rollback: set the switch to false. Existing mandates keep running.",
        visual="Five rollout steps.",
        narration=(
            "For rollout, deploy the API first. The migration applies at start-up and nothing changes yet. Then deploy the Angular app. "
            "Configure Razorpay in test mode with the keys and the webhook, run the thirteen step manual checklist, and only then switch "
            "the feature on. Go live with one small real mandate. To roll back, set the switch to false. Existing mandates keep running "
            "and their webhooks are still processed."
        ),
    ),
    dict(
        kind="bullets",
        title="Unverified assumptions and known gaps",
        items=[
            ("To confirm with real payloads", "Which events a UPI mandate sends and in what order, and whether payment failed always carries an invoice id."),
            ("To confirm on the account", "Subscriptions and UPI enabled, cancel at cycle end for UPI, and what the invoice link shows."),
            ("Not built", "Refunds of these payments, credit pack payments, pause and resume from the app, switching between paid plans."),
            ("Open decisions", "The four grace days, and Razorpay's invoice as the GST invoice."),
        ],
        visual="Four rows of caveats.",
        narration=(
            "Finally, what is not yet known. We still need to see real UPI webhook payloads, to confirm which events arrive and in what order, "
            "and that a failed payment always names its invoice. The account needs Subscriptions and UPI enabled, and cancel at cycle end "
            "must be confirmed for UPI. Refunds, credit pack payments, pause from the app and switching between paid plans are not built. "
            "Two decisions remain: the grace days, and the GST invoice. All of this is written up in the documents folder. Thank you."
        ),
    ),
]
