# -*- coding: utf-8 -*-
"""Voiceover for the recorded Billing-screen demo, in two flavours over the same recording: one for the business audience, one for developers.
Scene names match the marks written by demo/record.js. The recording is the real Angular screen against a stand-in backend (demo/mock-server.js)."""

SCENES = ['plans', 'subscribe', 'pending', 'confirmed', 'history', 'invoices', 'failure', 'cancel']

INTRO = dict(
    title="The Billing screen, in action",
    subtitle="A recording of the real Angular screen against a stand-in backend. No real Razorpay and no real payment.",
    tag="Demo",
)
OUTRO = dict(
    title="What this recording is, and is not",
    subtitle="It shows the screen's behaviour for each state. It does not show Razorpay, a UPI app, or a real payment.",
    tag="Still to do: the Razorpay test checklist",
)

VARIANTS = {
    'business': dict(
        slug='demo-overview', voice='Microsoft Zira Desktop', title='Billing screen demo (overview)',
        intro=("This is a recording of the new billing screen. It is the real screen, talking to a stand-in backend, so no real payment is involved."),
        outro=("To be clear about what you saw: the screen is real, but Razorpay was played by a stand-in. The next step is to run the written test checklist against Razorpay's test mode."),
        scenes=dict(
            plans=("A tenant on the free Trial plan sees the Premium plan, with its price and its tax spelled out, and a Subscribe now button."),
            subscribe=("Pressing Subscribe asks for confirmation. It states the monthly amount, that the tenant will approve in their UPI app, "
                       "and that the plan starts only once Razorpay confirms the first payment."),
            pending=("The tenant is sent to Razorpay in a new tab, and this page shows the request as Pending authorization. "
                     "The plan has not changed, and nothing has been credited."),
            confirmed=("Now the stand-in plays Razorpay. First the mandate is approved, and the page shows Payment pending. Then the first payment is confirmed. "
                       "The page finds this out by asking the server every few seconds, not because the tenant came back. Only now does it show Active, "
                       "and the plan and the wallet change."),
            history=("The payment appears in the history, with how it was paid, its reference, and the tax it included."),
            invoices=("Invoices are fetched on request, and each has a link."),
            failure=("Now a payment fails. The screen turns red, says the payment didn't go through, and says it will be tried again automatically. "
                     "The failed attempt is listed in the history with its reason, and it adds no credits."),
            cancel=("Once the retry succeeds, the automatic payment is active again. The tenant can cancel at any time, after a confirmation. "
                    "The plan stays active until the end of the period already paid for, and the status reads Cancellation scheduled."),
        ),
    ),
    'developer': dict(
        slug='demo-technical', voice='Microsoft David Desktop', title='Billing screen demo (technical)',
        intro=("This is a recording of the real Angular billing component, driven over the DevTools protocol, against a small mock server that plays "
               "our API and Razorpay. Nothing here touches Razorpay."),
        outro=("What this does not show: Razorpay's real responses, a UPI app, webhooks arriving, or the database. Those are covered by the backend tests with a fake gateway, "
               "and by the manual checklist in the testing document, which is still to be run."),
        scenes=dict(
            plans=("The page loads the plans, the subscription, the AutoPay availability and the current mandate. Availability is true, so a paid plan's button reads "
                   "Subscribe now instead of Upgrade."),
            subscribe=("Subscribe opens a confirmation. On confirm, the component posts only the plan id to billing slash autopay slash checkout. "
                       "The server computes the amount from the database, with tax, and creates the Razorpay plan and subscription."),
            pending=("The response carries the authorisation URL, which opens in a new tab. The component then reads the mandate back. "
                     "Its status is Pending authorization, and the plan access is untouched: no payment, no credits."),
            confirmed=("The mock now moves the mandate on. The component is polling the refresh endpoint every five seconds, and the server asks Razorpay. "
                       "It shows Payment pending, then Active. Only on that transition does it reload the subscription, the payment history and the entitlements."),
            history=("The history row comes from the payments endpoint, with the new optional fields: method, transaction id and status."),
            invoices=("Invoices are read from Razorpay through the server on demand, not stored. Each row links to Razorpay's hosted page."),
            failure=("A failed payment is recorded server side as a Failed payment row, matched to the mandate through its invoice. The mandate is Pending at Razorpay, "
                     "which the screen shows as Past due, with the retry message. The failed row is listed with its reason and is not money."),
            cancel=("After a successful retry the mandate is Active again. Cancel posts cancel at cycle end, true. The server records that the plan will not renew, "
                    "and the screen shows Cancellation scheduled. Whether Razorpay honours cancel at cycle end for UPI mandates is still to be confirmed."),
        ),
    ),
}
