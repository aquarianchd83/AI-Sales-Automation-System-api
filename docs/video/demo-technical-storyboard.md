# Billing screen demo (technical) - script and storyboard

Video: `docs/video/out/demo-technical/demo-technical.mp4`. Voice: Microsoft David Desktop. The picture is a recording of the real Angular Billing screen against a stand-in backend (`docs/video/demo/`); each demo video is its own recording, paced to its voiceover.

## Scene 1: intro

- **Time:** 0:00 - 0:14 (15 s)

> This is a recording of the real Angular billing component, driven over the DevTools protocol, against a small mock server that plays our API and Razorpay. Nothing here touches Razorpay.

## Scene 2: plans

- **Time:** 0:14 - 0:29 (15 s)

> The page loads the plans, the subscription, the AutoPay availability and the current mandate. Availability is true, so a paid plan's button reads Subscribe now instead of Upgrade.

## Scene 3: subscribe

- **Time:** 0:29 - 0:50 (21 s)

> Subscribe opens a confirmation. On confirm, the component posts only the plan id to billing slash autopay slash checkout. The server computes the amount from the database, with tax, and creates the Razorpay plan and subscription.

## Scene 4: pending

- **Time:** 0:50 - 1:08 (18 s)

> The response carries the authorisation URL, which opens in a new tab. The component then reads the mandate back. Its status is Pending authorization, and the plan access is untouched: no payment, no credits.

## Scene 5: confirmed

- **Time:** 1:08 - 1:30 (22 s)

> The mock now moves the mandate on. The component is polling the refresh endpoint every five seconds, and the server asks Razorpay. It shows Payment pending, then Active. Only on that transition does it reload the subscription, the payment history and the entitlements.

## Scene 6: history

- **Time:** 1:30 - 1:40 (10 s)

> The history row comes from the payments endpoint, with the new optional fields: method, transaction id and status.

## Scene 7: invoices

- **Time:** 1:40 - 1:51 (10 s)

> Invoices are read from Razorpay through the server on demand, not stored. Each row links to Razorpay's hosted page.

## Scene 8: failure

- **Time:** 1:51 - 2:11 (20 s)

> A failed payment is recorded server side as a Failed payment row, matched to the mandate through its invoice. The mandate is Pending at Razorpay, which the screen shows as Past due, with the retry message. The failed row is listed with its reason and is not money.

## Scene 9: cancel

- **Time:** 2:11 - 2:34 (23 s)

> After a successful retry the mandate is Active again. Cancel posts cancel at cycle end, true. The server records that the plan will not renew, and the screen shows Cancellation scheduled. Whether Razorpay honours cancel at cycle end for UPI mandates is still to be confirmed.

## Scene 10: outro

- **Time:** 2:34 - 2:53 (19 s)

> What this does not show: Razorpay's real responses, a UPI app, webhooks arriving, or the database. Those are covered by the backend tests with a fake gateway, and by the manual checklist in the testing document, which is still to be run.
