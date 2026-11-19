# Billing screen demo (overview) - script and storyboard

Video: `docs/video/out/demo-overview/demo-overview.mp4`. Voice: Microsoft Zira Desktop. The picture is a recording of the real Angular Billing screen against a stand-in backend (`docs/video/demo/`); each demo video is its own recording, paced to its voiceover.

## Scene 1: intro

- **Time:** 0:00 - 0:10 (11 s)

> This is a recording of the new billing screen. It is the real screen, talking to a stand-in backend, so no real payment is involved.

## Scene 2: plans

- **Time:** 0:10 - 0:21 (10 s)

> A tenant on the free Trial plan sees the Premium plan, with its price and its tax spelled out, and a Subscribe now button.

## Scene 3: subscribe

- **Time:** 0:21 - 0:37 (16 s)

> Pressing Subscribe asks for confirmation. It states the monthly amount, that the tenant will approve in their UPI app, and that the plan starts only once Razorpay confirms the first payment.

## Scene 4: pending

- **Time:** 0:37 - 0:50 (13 s)

> The tenant is sent to Razorpay in a new tab, and this page shows the request as Pending authorization. The plan has not changed, and nothing has been credited.

## Scene 5: confirmed

- **Time:** 0:50 - 1:14 (24 s)

> Now the stand-in plays Razorpay. First the mandate is approved, and the page shows Payment pending. Then the first payment is confirmed. The page finds this out by asking the server every few seconds, not because the tenant came back. Only now does it show Active, and the plan and the wallet change.

## Scene 6: history

- **Time:** 1:14 - 1:23 (9 s)

> The payment appears in the history, with how it was paid, its reference, and the tax it included.

## Scene 7: invoices

- **Time:** 1:23 - 1:28 (6 s)

> Invoices are fetched on request, and each has a link.

## Scene 8: failure

- **Time:** 1:28 - 1:45 (17 s)

> Now a payment fails. The screen turns red, says the payment didn't go through, and says it will be tried again automatically. The failed attempt is listed in the history with its reason, and it adds no credits.

## Scene 9: cancel

- **Time:** 1:45 - 2:04 (19 s)

> Once the retry succeeds, the automatic payment is active again. The tenant can cancel at any time, after a confirmation. The plan stays active until the end of the period already paid for, and the status reads Cancellation scheduled.

## Scene 10: outro

- **Time:** 2:04 - 2:17 (13 s)

> To be clear about what you saw: the screen is real, but Razorpay was played by a stand-in. The next step is to run the written test checklist against Razorpay's test mode.
