# Plan-driven application setup

A tenant (the "Talent") starts an **application** on a **plan**. Before it can run, it must be set up with exactly
the information that plan needs - no more. What a plan needs is **data**, not code: a new plan, or a new question
on an existing one, is an admin edit, never a release.

UI side and flowcharts: `AI-Sales-Automation-System-angular/src/app/features/applications/FLOWS.md`.

## Vocabulary (how the business terms map to this codebase)

| Business term | Here |
| --- | --- |
| Talent | the tenant's user (roles `Admin`, `SalesManager`); the platform operator is `PlatformSuperAdmin` |
| Plan | the existing platform `Plan` (Starter / Growth / Scale in the seed). Pricing and limits stay where they were |
| Application | `PlanApplication` - new. A tenant's instance of a plan that is set up, then run |
| Plan requirements | `PlanSetupVersion` + `PlanRequirement` - new, global (platform-owned) |

An application is **not** tied to the tenant's billing subscription: any active plan with a published setup can be
chosen. If applications should be limited to the subscribed plan, that is one check in `ApplicationSetupService.CreateAsync`.

## Data model

```
Plan 1──* PlanSetupVersion (Draft | Published | Superseded, VersionNumber, ValidityDays)
              1──* PlanRequirement (FieldKey, type, required, options, validation, order, section, condition, metric, active)

Tenant 1──* PlanApplication (PlanId, PlanSetupVersionId  <- pinned, SetupStatus, ...)
                 1──* ApplicationSetupValue   (FieldKey, FieldValue)            current answers
                 1──* ApplicationSetupAuditEntry (append-only)                  who/when/previous/new/reason/version
                 1──* ApplicationExecution    (SetupSnapshotJson)               frozen answers each run used
```

- `ApplicationSetupValue` is keyed by **field key**, not requirement id, so answers survive a plan change or version
  migration. Answers the current version no longer asks are kept but ignored.
- Values are stored as canonical strings: text as is, numbers invariant, checkbox `true`/`false`, date `yyyy-MM-dd`,
  multi-select a JSON array. The API exposes them typed.
- Versions are immutable once published. Publishing supersedes the previous one; existing applications stay on the
  version they were created on until `migrate`.

## Field types

Text, MultilineText, Number, Decimal, Currency, Date, Dropdown, MultiSelect, Radio, Checkbox, FileUpload, Url, Email, Phone.

A FileUpload answer is the id of an entry in the tenant's media library (so it accepts what that library accepts:
images and short videos). Validation rules per field: min/max (numbers, or number of picks for a multi-select),
min/max length, regex pattern with its own message. Required means: text not blank, a choice made, at least one
pick, a checkbox ticked, a file uploaded.

## Conditional questions

A requirement may depend on another field (`ConditionFieldKey` + operator + value). Operators: Equals, NotEquals,
Contains (a multi-select includes it), In (comma list), NotEmpty, Empty. A hidden question is never required, never
validated and not part of a run. A hidden parent hides its children. An unknown parent or a loop hides the field
(the safe direction). The admin editor refuses a dependency on a missing field or one that would loop.

## Setup status

`NotStarted -> InProgress -> Completed`, plus `Incomplete` (completed, then edited so a required answer is missing),
`RequiresUpdate` (plan changed or version migrated and new answers are needed), `Expired` (older than the version's
`ValidityDays`). Recomputed on every save and again at execution (`SetupEvaluator` is pure and is the only authority).
Only an explicit "complete" from the review step moves a never-confirmed setup to `Completed`.

## Execution gate

`POST /api/v1/applications/{id}/execute` re-evaluates right now. If the setup is not complete and valid it answers
**409** with `"Please complete the required setup before running this application."` and a `missing` list, records an
`ExecutionBlocked` audit entry and runs nothing. Otherwise it stores a frozen copy of the answers that applied
(`ApplicationExecution`), so later edits apply from the **next** run - the "apply from next execution" policy.

This repo has no application *runtime* yet: "execution" records the run with its frozen configuration and is the one
gate any future runtime must go through. A runtime should read `ApplicationExecution.SetupSnapshotJson`, never the live answers.

## API

Talent (`/api/v1/applications`, any tenant user may read; `Admin`/`SalesManager` may write and run):
`GET plans`, `GET`, `GET {id}`, `POST`, `GET|PUT {id}/setup`, `POST {id}/change-plan`, `POST {id}/migrate`,
`POST {id}/execute`, `GET {id}/executions`, `GET {id}/audit`.

Admin (`/api/v1/platform/setup`, `PlatformSuperAdmin`, every write goes to the platform audit log):
`GET plans`, `POST plans/{planId}/versions`, `GET|PUT|DELETE versions/{id}`, `GET versions/{id}/preview`,
`POST versions/{id}/publish`, `POST versions/{id}/requirements`, `PUT|DELETE requirements/{id}`.

All enums travel by name.

## Adding or changing what a plan asks

1. Platform console -> **Setup Plans** -> the plan -> **New version** (a draft copy of the live one).
2. Add / edit / reorder / deactivate questions, set rules and conditions; **Preview** shows the Talent's wizard.
3. **Publish.** New applications use it immediately; existing ones keep their version and are offered an update.

`SetupRequirementSeeder` gives each seeded plan a published v1 on first boot, once. It never touches a plan that
already has any setup version.

## Revenue and cost

A requirement can carry a `MetricKey` (`package_price`, `expected_customers`, `expected_leads`, `marketing_cost`,
`social_media_cost`, `operational_cost`). `SetupProjectionCalculator` turns the tagged answers into expected revenue,
total cost, profit and ROI; a figure is `null` until its inputs exist. This is the hook for the plan-level
revenue/cost planning - fixed per-plan assumptions (conversion rates, fixed costs) can be added as further tagged
fields or a version-level table without changing the shape.

## Decisions worth knowing

- **Draft saves are lenient, completion is strict.** Save & Continue stores what it is given (the server reports what is
  wrong); only completion and execution demand validity.
- **A plan change that needs nothing new keeps a completed setup completed**; one that needs something becomes
  `RequiresUpdate`.
- **Audit**: the append-only `ApplicationSetupAuditEntry` carries the detail; the tenant audit log also records the
  application's own status/plan changes (`PlanApplication` is in `AuditedEntityCatalog`).
- Answers are business data and are stored and audited as entered; do not use a requirement to collect secrets.

## Not covered

- A runtime that actually does the work of an application (see above).
- Per-plan fixed assumptions for the revenue/cost projection beyond tagged answers.
- Tying applications to the tenant's billing subscription or plan limits.
