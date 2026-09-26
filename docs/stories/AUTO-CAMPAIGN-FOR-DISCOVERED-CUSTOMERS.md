# Jira Story: Auto-Start Campaign for Customers Discovered by Lead Discovery

| Field | Value |
|---|---|
| **Issue Type** | Story |
| **Epic** | Lead Discovery → Campaign Automation |
| **Components** | Backend API (`WhatsAppSalesAutomation.Application` / `Infrastructure`), Angular Admin Console |
| **Priority** | High |
| **Labels** | lead-discovery, campaigns, automation, cron-job |
| **Related code** | `LeadDiscoveryJob`, `LeadDiscoveryRunService`, `LeadDiscoveryProfile`, `DiscoveredLead`, `CampaignService`, `Campaign`, `CampaignCustomer`, `CampaignSendService` |

---

## 1. Summary

As a **tenant admin**, when the scheduled Lead Discovery cron job finds and saves a new, qualified
customer, I want the system to **automatically enroll that customer into a pre-configured existing
campaign** (reusing its content/template and schedule) — so that new leads start receiving the right
outreach without a human having to manually build or launch a campaign for every discovery run.

This story extends the existing Lead Discovery pipeline (`LeadDiscoveryJob` →
`LeadDiscoveryRunService.RunForTenantAsync`) so that, immediately after a `DiscoveredLead`/`Customer`
pair is persisted, the system looks up an **existing, active `Campaign`** configured against that
tenant's Lead Discovery Profile, clones it into a dated **execution campaign**, attaches the new
customer to it, and lets the existing campaign-scheduling machinery (`CampaignService.StartAsync`,
`CampaignSendService`) take it from there.

## 2. Background (current system)

- **Cron job**: `LeadDiscoveryJob` runs per tenant (`lead-discovery:{tenantId}`) via `TenantJobRunner`,
  driven by `ILeadDiscoveryRunService.RunForTenantAsync`.
- **Customer-search configuration**: today this is `LeadDiscoveryProfile` — **one row per tenant**
  (unique index on `TenantId`), holding `TargetBusinessType`, `Keywords`, `Locations`, qualification
  rules, and `IsEnabled`.
- **Discovered customer**: a qualified candidate is saved as a `DiscoveredLead` row *and* a CRM
  `Customer` row (`LeadDiscoveryRunService.AddCustomer`), inside the same per-round save loop in
  `RunForTenantAsync`. The customer's `OptInStatus` is left at its `PendingOptIn` default — **no
  consent is recorded automatically**.
- **Campaigns**: `Campaign` → `CampaignStep`(s) → `CampaignCustomer` (join + progress). A campaign has
  `Status` (`Draft/Scheduled/Running/Paused/Stopped/Completed`) and `ScheduledStartAt`, which is
  **pinned to the tenant's own local time** (`Tenant.Timezone`, via `ITenantTimeZoneProvider`) rather
  than UTC — this is exactly the "current date/time in the tenant's timezone" mechanism the business
  requirement asks for, and already exists; see `CampaignService.StartAsync` /
  `CampaignSendService.ProcessInitialSendsAsync`.
- **Audience attach**: `CampaignService.SetAudienceAsync` attaches customers to a campaign, but **only
  customers with `OptInStatus == OptedIn`** are eligible to actually be sent to
  (`CampaignSendService` checks this again at send time). See **Open Question #1** below — this is a
  direct conflict with "the campaign should start for the newly discovered customer on the same day."

## 3. Acceptance Criteria

1. **Given** the Lead Discovery cron job discovers and saves a new customer, **when** an eligible,
   active source campaign is configured for that tenant's search configuration, **then** the system
   creates a new execution campaign cloned from the source campaign's steps/content and enrolls the
   new customer into it, without modifying the source campaign.
2. **Given** no source campaign is configured (or Auto Campaign is disabled) for the search
   configuration, **when** a customer is discovered, **then** the system skips campaign creation for
   that customer, logs/audits the skip, and the cron job continues processing remaining customers.
3. **Given** the source campaign is `Draft`, `Stopped`, deleted, or otherwise not "active/usable",
   **when** a customer is discovered, **then** the system does **not** create an execution campaign,
   and records the reason (Failed/Skipped) in the audit trail.
4. **Given** an execution campaign is created, **then** its name is
   `"<Source Campaign Name>-<CurrentDate>"`, where `<CurrentDate>` is computed at creation time in the
   **tenant's configured timezone** (`ITenantTimeZoneProvider`), consistent with how `Campaign.ScheduledStartAt`
   is already pinned to tenant-local time.
5. **Given** the source campaign's schedule/time configuration, **when** the execution campaign is
   created, **then** the execution campaign inherits the same schedule/time behavior (same-day,
   configured time), subject to `CampaignService.StartAsync`'s existing Draft→Scheduled/Running rules.
6. **Given** the Lead Discovery cron job runs multiple times on the same day for the same search
   configuration, **when** it re-processes, **then** it must not create a second execution campaign for
   an already-enrolled customer for the same source campaign on the same day (idempotent — see
   Business Rules / Data Model).
7. **Given** one customer's campaign enrollment fails (execution campaign creation, attach, or
   scheduling failure), **when** other customers were discovered in the same run, **then** processing
   of the remaining customers continues and the run does not fail outright.
8. **Given** any processing outcome (Started / Skipped / Failed), **then** it is recorded with enough
   detail (tenant, search configuration, discovered customer, source campaign, execution campaign,
   timestamp, reason) for an admin to audit it — extending the existing `LeadDiscoveryRun`
   summary/audit pattern.
9. **UI**: a tenant admin can configure, per search configuration: whether Auto Campaign is enabled,
   which existing active campaign is the source, and see the configuration's derived status. The
   screen must **not** allow creating campaign content — only selecting an existing one.
10. **UI**: saving a configuration with Auto Campaign enabled but no valid active source campaign
    selected is blocked with a validation error.

## 4. UI Requirements

Because `LeadDiscoveryProfile` is currently **one row per tenant**, the "mapping" the business
requirement describes is effectively a **1:1 extension of the existing Lead Discovery Profile
screen** (`lead-discovery-profile` component in the Angular app), not a new list/mapping grid —
*unless* the product decision is to support multiple search configurations per tenant in the future
(see Open Questions). Recommended: add a new **"Auto Campaign"** section to the existing profile
screen.

Suggested fields (as specified in the requirement):

| Field | Type | Required | Notes |
|---|---|---|---|
| Customer Search Configuration | Read-only context (this tenant's profile) or dropdown if multi-profile is later supported | Yes | Today: implicit (one profile per tenant) |
| Auto Campaign Enabled | Toggle | Yes | Off by default |
| Source Campaign | Dropdown of existing campaigns, **filtered to active/usable statuses** | Yes when enabled | Must block save if enabled with no selection |
| Campaign Schedule | Read-only, derived from the selected source campaign | Yes | No independent schedule editing here |
| Status | Read-only/derived | No | e.g. "Active", "Disabled", "Needs a source campaign" |

- Must show validation immediately when toggling Auto Campaign on without a valid campaign selected.
- Must clearly disclose that campaign **content** is not editable from this screen — a link to the
  existing Campaign detail/edit screen (`campaign-detail`, `campaign-form-dialog`) for that.
- Save/Update calls a new/extended endpoint on the Lead Discovery Profile API.

## 5. Business Rules

- Only existing, active/usable campaigns can be selected as the source.
- Execution campaign content is always cloned from the source campaign at creation time (snapshot),
  never regenerated or AI-authored.
- Execution campaign name = `"<Source Campaign Name>-<CurrentDate>"`, tenant-timezone date.
- The source campaign itself is never modified by this process.
- No source campaign configured, or Auto Campaign disabled → skip, not fail.
- Idempotent: a given `DiscoveredLead`/`Customer` must not be enrolled twice into execution campaigns
  generated from the same source campaign because of repeated cron runs.
- A failure processing one customer must not abort processing of the remaining discovered customers
  in that run (mirrors the existing per-round save pattern in `LeadDiscoveryRunService`).
- All outcomes are auditable (Started / Skipped / Failed).

## 6. Processing Flow

**Happy path**

```
LeadDiscoveryJob (cron, per tenant)
  → LeadDiscoveryRunService.RunForTenantAsync
    → discovers + saves DiscoveredLead + Customer (existing behavior)
    → [NEW] AutoCampaignEnrollmentService.ProcessAsync(tenantId, discoveredCustomer)
        → load LeadDiscoveryProfile's Auto Campaign config
        → Auto Campaign enabled? no → audit Skip, continue
        → load Source Campaign
        → Source Campaign active/usable? no → audit Skip/Failed, continue
        → already enrolled for this source campaign today? yes → audit Skip (duplicate), continue
        → clone source campaign (steps/content) into new execution Campaign
        → name = "<Source Campaign Name>-<CurrentDate:tenant-tz>"
        → attach discovered customer via CampaignService (respecting OptIn rules — see Open Question #1)
        → apply source campaign's schedule (ScheduledStartAt semantics)
        → CampaignService.StartAsync (or Schedule) the execution campaign
        → audit Started
  → continue to next discovered customer (errors isolated per customer)
```

**Skip path (no source campaign)**

```
LeadDiscoveryJob → discover customer → find LeadDiscoveryProfile
  → Auto Campaign enabled but no Source Campaign configured
    → skip campaign creation → audit Skip → continue processing
```

## 7. Edge Cases

| # | Case | Expected behavior |
|---|---|---|
| 1 | No source campaign configured | Skip, audit "Skipped: no source campaign configured" |
| 2 | Source campaign inactive (`Draft`/`Stopped`) | Skip/Fail per rule, audited, no execution campaign created |
| 3 | Source campaign deleted after being configured | Treat as unconfigured; skip + audit; consider surfacing a UI warning on the config screen |
| 4 | Auto Campaign disabled | Skip, audit "Skipped: auto campaign disabled" |
| 5 | Multiple customers discovered in one run | Each processed independently; one failure doesn't block others |
| 6 | Same customer discovered again | `RemoveDuplicatesAsync` already prevents a duplicate `DiscoveredLead`/`Customer`; enrollment must also be idempotent for the (rare) re-discovery path |
| 7 | Same search config runs multiple times same day | Idempotency key must prevent duplicate execution campaigns/enrollment for the same customer + source campaign + day |
| 8 | Name collision (two execution campaigns generated same name same day) | Name generation must be collision-safe (e.g., unique constraint + disambiguating suffix, or batch multiple same-day customers into one execution campaign — see Open Question #2) |
| 9 | Campaign creation fails | Record Failed, continue with next customer |
| 10 | Customer enrollment (`SetAudienceAsync`) fails | Record Failed, continue |
| 11 | Scheduling/start fails | Record Failed; execution campaign may remain in `Draft` for manual recovery |
| 12 | Source campaign content changes after an execution campaign was already cloned from it | No retroactive effect — execution campaign is a snapshot at creation time (see Open Question #5) |
| 13 | Configured campaign time already passed for today | Depends on `CampaignService.StartAsync`'s Draft→Running vs Scheduled logic; needs explicit decision (see Open Question #3) |
| 14 | Tenant timezone vs platform default | Use `ITenantTimeZoneProvider` (already handles "no timezone set" fallback to IST) — do not use raw UTC |
| 15 | Cron job partially succeeds | Existing per-customer save loop already isolates rounds; new logic must follow the same isolate-and-continue pattern, with per-customer try/catch |

## 8. Data / Domain Considerations

*(No table names assumed — describing logical changes against existing entities.)*

- **`LeadDiscoveryProfile`** (or a new 1:1 linked config entity) needs: `AutoCampaignEnabled` (bool),
  `SourceCampaignId` (nullable FK-by-Guid, matching this codebase's convention of unconstrained Guid
  references across aggregates, e.g. `DiscoveredLead.CustomerId`).
- **New entity** to track each generated execution campaign and its provenance, e.g.
  `CampaignAutoEnrollment` or `ExecutionCampaignSource`, holding: `TenantId`, `SourceCampaignId`,
  `ExecutionCampaignId`, `DiscoveredLeadId`/`CustomerId`, `CreatedAtUtc`/tenant-local creation date,
  `Status` (Started/Skipped/Failed), `Reason`/error detail. This is also the **idempotency guard**
  (unique constraint on `TenantId + SourceCampaignId + CustomerId` or
  `+ CustomerId + ExecutionDate` depending on the batching decision in Open Question #2).
- **`Campaign`**: no schema change expected — the execution campaign is just a normal `Campaign` row,
  cloned from the source's `Name`/`Description`/`ScheduledStartAt`/`Steps`.
- **`CampaignStep`** / **`CampaignStepMedia`**: cloned (deep copy) from the source campaign's steps at
  creation time — a snapshot, not a reference.
- **`CampaignCustomer`**: new row linking the execution campaign to the discovered `Customer`, same as
  any normal `SetAudienceAsync` attach.
- **Audit**: extend the existing `Audit` module / `LeadDiscoveryRun` summary pattern so an admin can
  see, per run, how many customers were auto-enrolled, skipped, or failed, and why.

## 9. Backend/API Considerations

- **`LeadDiscoveryRunService.RunForTenantAsync`**: after `_context.SaveChangesAsync()` persists each
  round's `DiscoveredLead`/`Customer` rows, invoke the new enrollment step per newly-saved customer,
  wrapped so a failure there does not abort the discovery run itself.
- **New application service**, e.g. `IAutoCampaignEnrollmentService`, encapsulating: lookup config →
  validate source campaign → clone → attach → schedule → audit. Keeps `LeadDiscoveryRunService`
  focused on discovery, per existing separation of concerns in this codebase (Application services per
  bounded concept).
- **`CampaignService`**: likely needs a new internal "clone campaign" capability (steps + media, not
  audience) distinct from `CreateAsync`, since `CreateAsync` today only takes name/description/
  schedule, not a full step clone. Consider `ICampaignService.CloneAsync(sourceCampaignId, newName, …)`.
- **`CampaignService.SetAudienceAsync`**: reused as-is to attach the discovered customer — but note
  its opt-in filtering (see Open Question #1).
- **`CampaignService.StartAsync`**: reused as-is to move the execution campaign from `Draft` to
  `Scheduled`/`Running`, so this story does not need to reimplement scheduling.
- **`LeadDiscoveryProfile` API/DTOs**: extend `LeadDiscoveryDtos` and the profile endpoint(s) with the
  new Auto Campaign fields; extend `LeadDiscoveryValidators` to require a valid, active
  `SourceCampaignId` when `AutoCampaignEnabled = true` (mirrors the UI-side validation).
- **`LeadDiscoveryJob`**: no signature change expected; the new step happens inside
  `RunForTenantAsync`, which the job already calls through `TenantJobRunner`.
- **Audit/logging**: extend whatever the platform's `Audit` module (`Application/Audit`) uses for
  other automated actions, plus enough detail in the per-run summary string (`RunStats.ToSummary()`
  pattern) or a dedicated per-customer audit table (see Data Model above).

## 10. Non-Functional Requirements

- **Idempotency**: no duplicate execution campaigns/enrollments across repeated cron runs (see Edge
  Cases #6, #7).
- **Multi-tenancy**: all new entities/queries respect `TenantId` scoping (`ITenantOwned`), matching
  every existing entity in this codebase.
- **Reliability / transaction consistency**: execution-campaign creation + audience attach + audit
  write should be transactionally consistent per customer, or safely retryable/idempotent if not
  atomic.
- **Error handling**: one customer's failure isolated per Edge Case #9–11 and Acceptance Criterion #7.
- **Logging/monitoring**: Started/Skipped/Failed counts surfaced the way `LeadDiscoveryRun` already
  surfaces discovery stats, so this is visible without digging through raw logs.
- **Scalability**: batch sizes are already capped (`LeadDiscoveryProfile.BatchSize`, plan limits), so
  volume here is bounded by existing discovery throughput — no new unbounded loop.
- **Performance impact on the cron job**: enrollment must not meaningfully slow down the existing
  discovery loop; consider whether enrollment should be synchronous (within the same job run, matching
  the requirement's "same day, same configured time") or queued as a follow-up background job/step.
- **Timezone handling**: use `ITenantTimeZoneProvider` exclusively for `<CurrentDate>` and schedule
  comparisons — never `DateTime.UtcNow`/`IDateTimeProvider.IstNow` directly, per the existing
  `Campaign.ScheduledStartAt` convention.
- **Security/authorization**: the new config screen/endpoints follow the same tenant-admin
  authorization as the existing Lead Discovery Profile and Campaign endpoints.

## 11. Out of Scope

- AI-generated campaign content.
- Creating campaign content from scratch via this feature.
- A new campaign-management engine — this reuses `CampaignService`/`CampaignSendService` as-is.
- Changing the source campaign's own content/configuration.
- Replacing the existing campaign scheduling mechanism (`ScheduledStartAt` +
  `CampaignSendService.ProcessInitialSendsAsync`) — only reusing it.

## 12. Assumptions & Open Questions

1. **Opt-in conflict (critical)**: `CampaignSendService` only sends to customers with
   `OptInStatus == OptedIn`, but a newly discovered customer is created with `PendingOptIn` and no
   consent recorded. Auto-enrolling into a campaign will attach the customer but **no message will
   actually go out** until someone opts them in — is that the intended behavior ("started" =
   enrolled/queued, not necessarily sent), or does this story need a separate decision about how
   consent is obtained for auto-discovered leads before WhatsApp messaging compliance allows a send?
2. **One profile per tenant today**: `LeadDiscoveryProfile` has a unique `TenantId` index — there is
   currently only one "customer search configuration" per tenant. Does the business actually need
   multiple search configurations mapped to different campaigns (as the UI requirement's wording
   implies), or is a single 1:1 mapping on the existing profile sufficient for this story, with
   multi-profile support tracked separately?
3. **`<CurrentDate>` exact format**: e.g. `2026-09-22` vs `22-Sep-2026` vs `Campaign Name-20260922` —
   needs a concrete decision; recommend ISO `yyyy-MM-dd` for sortability and to avoid locale ambiguity.
4. **Same-day multiple discoveries**: should all customers discovered on the same day (possibly across
   multiple cron runs) share **one** execution campaign named `Source-<date>`, or does each discovered
   customer get its **own** execution campaign? This materially changes the data model (idempotency
   key) and the UI's campaign list volume. Recommend: one execution campaign per source campaign per
   day, with all that day's discovered customers attached as audience (fewer campaigns, matches
   `SetAudienceAsync`'s "safe to call repeatedly to grow an audience" design).
5. **Schedule already passed for today**: if the source campaign's configured send time has already
   passed when a customer is discovered later that day, does the execution campaign send immediately,
   queue for the next occurrence, or wait until the following day at the configured time?
6. **Snapshot vs live copy**: should the execution campaign's content be a one-time snapshot of the
   source campaign at creation time (recommended, matches "do not modify the source campaign" and
   avoids surprise content changes mid-run), or should it track/reflect later edits to the source?
7. **Timezone source of truth**: confirmed as `Tenant.Timezone` via `ITenantTimeZoneProvider` — flagging
   for explicit sign-off since the story text also mentions "platform's defined timezone."
8. **Synchronous vs queued execution**: should enrollment happen inline inside
   `LeadDiscoveryRunService.RunForTenantAsync` (simpler, matches "same run"), or be queued as a
   separate background job step for resilience/scale? Recommend inline for v1, given current batch
   sizes are small and bounded.

## 13. Suggested Sub-tasks

1. Data model: add Auto Campaign fields to `LeadDiscoveryProfile` (or new 1:1 config entity) +
   migration.
2. Data model: new enrollment/audit tracking entity + migration + idempotency constraint.
3. Backend: `ICampaignService.CloneAsync` (or equivalent) to snapshot-copy a campaign's steps/media
   into a new `Campaign`.
4. Backend: `IAutoCampaignEnrollmentService` orchestrating lookup → validate → clone → attach →
   schedule → audit, with per-customer error isolation.
5. Backend: wire the enrollment service into `LeadDiscoveryRunService.RunForTenantAsync`.
6. Backend: extend `LeadDiscoveryDtos`/`LeadDiscoveryValidators` and the profile API for the new
   fields, including "enabled requires valid active source campaign" validation.
7. Backend: audit/logging extension for Started/Skipped/Failed outcomes.
8. Frontend: extend `lead-discovery-profile` component with the Auto Campaign section (dropdown,
   toggle, derived schedule/status, validation).
9. Tests: unit tests for enrollment service (all edge cases in §7), integration test for the full
   discovery→enrollment flow, idempotency test for repeated cron runs.
10. Docs: update `docs/PHASE2-BACKEND-SETUP.md`/module docs as appropriate once the design questions
    above are resolved.

## 14. Definition of Done

- All acceptance criteria in §3 pass, including automated tests for the idempotency and error-isolation
  behaviors.
- UI configuration screen implemented, validated, and reviewed against the requirement's field list.
- Open Questions in §12 resolved and reflected back into this story (or a follow-up story) before
  implementation sign-off, particularly #1 (opt-in) and #4 (batching).
- Audit trail visible to tenant admins for Started/Skipped/Failed outcomes.
- No change to source campaign content/configuration as a side effect of this feature (verified by
  test).
