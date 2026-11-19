# -*- coding: utf-8 -*-
"""The business overview video (about three minutes). Every claim here is one the project can stand behind: what is built and
tested in code, and, said plainly, what has not yet been tried against Razorpay itself."""

TITLE = "Automatic monthly billing with UPI AutoPay"
SLUG = "overview"
VOICE = "Microsoft Zira Desktop"

SLIDES = [
    dict(
        kind="title",
        title="Automatic monthly billing with UPI AutoPay",
        subtitle="What was built, what it protects, and what is still to do",
        tag="Overview for the team",
        visual="Title card on a dark background with the product name.",
        narration=(
            "This is a short overview of the new automatic monthly billing for AI Sales Automation. "
            "Tenants can now pay for their plan through UPI AutoPay. They approve once in their UPI app, "
            "and Razorpay takes the payment every month after that."
        ),
    ),
    dict(
        kind="steps",
        title="How a tenant subscribes",
        steps=[
            ("1", "Choose a plan", "Subscribe now on the Billing screen"),
            ("2", "Approve in a UPI app", "Google Pay, PhonePe, Paytm and others, on Razorpay's page"),
            ("3", "Razorpay confirms", "The first payment is taken and reported to us"),
            ("4", "Plan starts", "Paid through the month, credits added to the wallet"),
            ("5", "Renews by itself", "Razorpay takes each month's payment"),
        ],
        note="The tenant never has to pay by hand after the first approval.",
        visual="Five numbered steps left to right, each with a short caption.",
        narration=(
            "The tenant chooses a plan and presses Subscribe. They are taken to Razorpay, where they approve "
            "a mandate in their UPI app, such as Google Pay. Once Razorpay confirms the first payment, the plan "
            "starts and the plan's credits are added to the wallet. After that, Razorpay takes each month's payment "
            "by itself. The tenant does nothing more."
        ),
    ),
    dict(
        kind="cards",
        title="What the tenant sees on the Billing screen",
        cards=[
            ("Clear status", "Pending authorization, Active, Past due, Halted, Paused, Cancellation scheduled, Cancelled or Expired."),
            ("Their next payment", "The monthly amount, the next payment date, and whether auto-renewal is on."),
            ("Control", "Cancel automatic payment at any time, with a confirmation. Paid-for access is kept."),
            ("Records", "Every payment with its method and reference, failed attempts with the reason, and the invoices."),
        ],
        visual="Four cards describing the Billing screen.",
        narration=(
            "On the Billing screen the tenant sees the state of their automatic payment, the monthly amount, "
            "and the date of the next payment. They can cancel whenever they like, after a confirmation, and they "
            "keep what they have already paid for. Their payment history shows every attempt, including failed ones "
            "with the reason, and each invoice."
        ),
    ),
    dict(
        kind="cards",
        title="Three promises that keep money and credits correct",
        cards=[
            ("The price is ours", "The amount always comes from our database, with tax. The browser only says which plan."),
            ("Confirmed means confirmed", "A plan starts only when Razorpay reports the payment. Coming back from Razorpay's page proves nothing."),
            ("Credits once per payment", "However many times Razorpay repeats a message, one payment adds one set of credits."),
        ],
        visual="Three large cards, one per promise.",
        narration=(
            "Three promises matter most. First, the price is always ours. It comes from our database, never from the browser. "
            "Second, a plan starts only when Razorpay confirms the payment. A tenant coming back to our page proves nothing. "
            "Third, credits are added once per payment. Razorpay sometimes repeats its messages, and a repeat never adds credits twice."
        ),
    ),
    dict(
        kind="steps",
        title="When a payment fails",
        steps=[
            ("1", "Razorpay retries", "Over the next three days"),
            ("2", "Plan keeps working", "Four grace days after the paid period"),
            ("3", "Tenant is told", "In the app, with what to do"),
            ("4", "Back to the free plan", "If it never succeeds. Wallet credits are kept."),
        ],
        note="A failed attempt is recorded with its reason, and never adds credits.",
        visual="Four steps from a failed payment to the free plan.",
        narration=(
            "If a payment fails, Razorpay tries again over the next three days. The plan keeps working through four grace days, "
            "and the tenant is told what to do. If the payment never goes through, the tenant moves back to the free plan. "
            "Credits already in their wallet stay theirs. A failed payment never adds credits."
        ),
    ),
    dict(
        kind="stats",
        title="Built and checked",
        stats=[
            ("2,801", "backend tests passing"),
            ("1,593", "screen tests passing"),
            ("7", "phases, in two code repositories"),
        ],
        note="These tests use a stand-in for Razorpay. Nothing has yet been run against Razorpay itself.",
        visual="Three big numbers and a caution line.",
        narration=(
            "The work is built in seven phases. All two thousand eight hundred and one backend tests pass, and all one thousand "
            "five hundred and ninety three screen tests pass. To be clear, those tests use a stand-in for Razorpay. "
            "Nothing has yet been run against Razorpay itself."
        ),
    ),
    dict(
        kind="bullets",
        title="What is still needed",
        items=[
            ("On the Razorpay account", "Subscriptions switched on, and UPI enabled for them, in test and live."),
            ("A test run", "A written checklist of thirteen steps, using Razorpay's test mode."),
            ("One real mandate", "A small live one, through its second monthly payment and settlement."),
            ("Two decisions", "The four grace days, and whether Razorpay's invoice can serve as the GST invoice."),
            ("Not built yet", "Refunds of these payments, paying for credit packs, and switching between two paid plans."),
        ],
        visual="Five rows: what must happen before launch.",
        narration=(
            "Before launch, a few things are needed. Razorpay must switch on Subscriptions and UPI for our account. "
            "We then run the written test checklist in test mode, and finally one small real mandate, all the way through its "
            "second monthly payment. Two decisions are open: the four grace days, and whether Razorpay's invoice can be used as "
            "the tax invoice. Refunds, credit pack payments and switching between paid plans are not built yet."
        ),
    ),
    dict(
        kind="title",
        title="Safe to switch on, step by step",
        subtitle="Off by default. Nothing changes for any tenant until it is turned on.",
        tag="Next: the test checklist",
        visual="Closing card.",
        narration=(
            "The feature is off by default, so nothing changes for any tenant until it is switched on. "
            "The next step is the test checklist. Thank you."
        ),
    ),
]
