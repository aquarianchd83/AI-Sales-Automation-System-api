# Connect with Meta - WhatsApp onboarding

A tenant Admin presses **Connect with Meta** (Settings, and onboarding's WhatsApp step). Meta's own Embedded Signup popup handles
sign-in, consent, business details and number OTP. Everything after that is done by `MetaEmbeddedSignupService`; the tenant never sees
a WABA id, phone number id, token or webhook setting.

## Flow

| Step | What happens | If Meta blocks it |
| --- | --- | --- |
| `authorization` | Browser returns a one-time code (and Meta's reported ids). Code is exchanged server-side with the platform App Secret. | Cancelled / denied -> "reconnect and grant permissions". |
| `assets` | The WABA and number are re-read **with the new token** (`debug_token`, `/{waba}/phone_numbers`); the browser's ids are only a hint. A number already connected to another tenant is refused. | No account shared, number in use elsewhere. |
| `credentials` | Token, WABA id, phone number id, platform App id/secret saved, encrypted, on the tenant's `TenantWhatsAppConfig` row. | - |
| `registration` | `POST /{phone}/register` only when Meta does not already show the number `CONNECTED`. | Number verification / not registered. |
| `webhook` | `POST /{waba}/subscribed_apps` if the platform App is not already subscribed. | Permissions, restriction. |
| `templates` | Reads template names/statuses and returns approved / pending / rejected counts. Informational - does not block activation. | Shown as "action required" only. |
| `verification` | The number is read back with the stored token; success stamps `VerifiedAtUtc`. | Expired token, restriction, etc. |

`Completed` (and the green "WhatsApp is active") only when every step except templates is `Completed`.
Earlier steps stay done when a later one fails: credentials are saved at `credentials`, and **Retry** calls
`POST /tenant-settings/whatsapp/meta-signup/resume`, which carries on from the stored state without a new Meta sign-in.

## Endpoints (tenant Admin)

- `GET  /api/v1/tenant-settings/whatsapp/meta-signup/config` - App id + configuration id for the popup, or why it is unavailable.
- `GET  .../status` - live status of each step (reads Meta, writes nothing).
- `POST .../complete` - body `{ code, wabaId, phoneNumberId, clientEvent, clientStep, clientErrorMessage }`.
- `POST .../resume` - re-run from the first step not done.

Meta-side problems come back **200** with `issue` (`title`, `message`, `primaryAction`, `secondaryAction`, `reference`) and the step list.
`MetaIssueCatalog` picks the message from Meta's code/subcode/wording and the step, so a temporary outage never says "verify your business".
Raw Meta text is never returned. Detailed errors (HTTP status, Meta code/subcode, `fbtrace_id`, tenant, step) are logged; tokens, the
client secret and the authorization code are not (the typed HttpClient has its loggers removed).

## Platform setup (not doable in code)

1. A Meta App (type Business) with **WhatsApp** and **Facebook Login for Business**; set `WhatsApp:AppId`, `WhatsApp:AppSecret`.
2. Create an **Embedded Signup configuration** and set `WhatsApp:EmbeddedSignupConfigId` (Configuration -> WhatsApp). Until all three are set the button is disabled with a "contact platform support" message.
3. App Review / Advanced Access for `whatsapp_business_management` and `whatsapp_business_messaging`, and Meta **Tech Provider** status, so tenants other than the app owner can onboard. Business verification of the platform's own Meta Business.
4. The App's webhook callback must point at `/api/v1/webhooks/...` with `WhatsApp:WebhookVerifyToken` (already how the platform works).
5. Whitelist the production origin in the App's *Allowed domains for the JavaScript SDK*.

## Not covered here (separate work / external)

- **Centralised Meta billing.** Recovering Meta charges from the tenant wallet needs a Meta partner credit line and billing set up on each client WABA. That is a Meta/business arrangement, not code; nothing here assumes it.
- Sending-time wallet reservation, delivery-status reconciliation and webhook idempotency belong to the existing wallet, billing and webhook code and were not changed.
- Template **creation** and campaign gating on approved templates use the existing message-template module.
- Persisting a per-step history needs a schema change (a migration); steps are currently derived live from Meta plus the saved connection, and the existing audit-trail interceptor records changes to the connection row.
- Disconnect for tenants (Platform Admin can already remove a connection).
