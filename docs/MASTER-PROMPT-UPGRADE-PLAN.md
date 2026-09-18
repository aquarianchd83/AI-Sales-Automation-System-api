# Universal WhatsApp AI Sales Agent — Modification Plan

**Goal:** implement the "Universal WhatsApp AI Sales Agent — Master System Prompt" (role, dynamic
qualification, lead scoring, hot-lead detection, human handoff, Hinglish-first tone) as the AI
behaviour of this platform, without breaking the existing AI/Human/Hybrid conversation pipeline.

**Scope of this document:** gap analysis + concrete modification plan only. No code changes are
made by this document; it is the map for the implementation work.

---

## 1. How the master prompt maps onto this codebase today

The master prompt is a *template* with placeholders (`{{BUSINESS_NAME}}`, `{{RETRIEVED_KNOWLEDGE}}`,
`{{QUALIFICATION_FIELDS}}`, `{{LEAD_SCORING_RULES}}`, …) that a SaaS platform fills in per tenant,
per turn. This repo already has an equivalent, much narrower version of that pipeline:

| Master prompt concept | Current implementation |
|---|---|
| System prompt | `AiPromptSupport.SystemPrompt(customerName)` (Infrastructure/Ai) — static text, no business context |
| `{{RETRIEVED_KNOWLEDGE}}` | `IKnowledgeBaseService.RetrieveRelevantChunksAsync` → `AiConversationContext.GroundingChunks`, rendered in `AiPromptSupport.BuildUserMessage` |
| `{{QUALIFICATION_FIELDS}}` | **Does not exist.** Only 3 hard-coded fields: `budget`, `interest`, `purchase_timeline` |
| `{{LEAD_SCORING_RULES}}` | **Does not exist.** Hard-coded formula in `LeadService.ComputeScoreNumeric` (+30/+30/+20/±20) |
| Lead JSON object (§9) | `Lead` entity (fixed columns) + `LeadDto` |
| Intent taxonomy (§8) | Free-text `DetectedIntent` string + `AiOptions.EscalationIntents` (only 4 configured intents actually change behaviour) |
| Hot lead detection / stop qualifying (§11) | Not implemented — escalation is driven only by `ConfidenceScore < threshold` or intent match, never by `LeadScoreBand.Hot` |
| Human handoff summary (§12) | `HandoffService.GetOrCreateOpenHandoffAsync` stores one `Notes` string: `"AI escalation - intent 'X', confidence Y%"` — no structured lead summary |
| Opt-in/opt-out (§17) | `InboundWebhookProcessor.OptOutKeywords` — 7 exact-match keywords, English only |
| Language (§19) | **Not implemented at all** — no instruction to the model about language, so it will default to whatever the provider guesses |
| Business context (§2) | `Tenant` has `Name`, `Industry`, `BusinessDescription`, `WebsiteUrl` — **missing `Location` and `WorkingHours`** |
| Tone (§18) | Partially covered ("keep the reply short") but not the full "no robotic questionnaires" guidance |

**Bottom line:** the retrieval, conversation-memory, and escalation *machinery* already exists and is
solid (RAG, running summary, merge-not-overwrite lead updates, idempotent replies, handoff
creation). What's missing is (a) the richer prompt text itself, (b) making qualification fields and
scoring rules **tenant-configurable data** instead of 3 hard-coded columns, and (c) a few new
Tenant/Lead fields and one new admin-configured schema.

---

## 2. Data model changes

### 2.1 `Tenant` — business context fields
`src/Core/WhatsAppSalesAutomation.Domain/Entities/Tenancy/Tenant.cs`

Add, alongside the existing business-profile block:

```csharp
public string? Location { get; set; }          // free text, e.g. "Mohali, Punjab"
public string? WorkingHours { get; set; }       // free text, e.g. "Mon-Sat 10am-7pm IST"
public string PrimaryLanguage { get; set; } = "hinglish"; // "hinglish" | "english" | "hindi" | ...
```

- `PrimaryLanguage` is the **default** the agent opens a fresh conversation in when it has no
  customer message to infer a language from yet; per §19/§7 (user's own instruction), default it to
  `"hinglish"`. The model still mirrors whatever language the customer actually writes in once a
  message arrives — this field only seeds the very first outbound/greeting behaviour and is a
  business-configurable override (some tenants may want pure English/Hindi).
- Extend `ITenantBusinessDetails` (`Application/Tenancy/TenantBusinessDetails.cs`) with `Location`,
  `WorkingHours`, `PrimaryLanguage`, add length/enum validation to `TenantBusinessDetailsValidator`,
  and wire them through `TenantBusinessDetails.ApplyTo`, `UpdateTenantBusinessProfileRequest`,
  `TenantProfileDto`, and `Platform.CreatePlatformTenantRequest` (same pattern every existing field
  in that file already follows).
- EF: add columns via `TenantConfiguration` + a new migration.

### 2.2 New entity — `QualificationFieldDefinition` (tenant-configured qualification schema)
`src/Core/WhatsAppSalesAutomation.Domain/Entities/Leads/QualificationFieldDefinition.cs`

This is the structured version of the master prompt's §4 `{{QUALIFICATION_FIELDS}}` block:

```csharp
public class QualificationFieldDefinition : BaseEntity, ITenantOwned
{
    public Guid TenantId { get; set; }
    public string FieldKey { get; set; }        // "budget", "property_type", "doctor_count" ...
    public string Question { get; set; }         // what the agent asks when this field is missing
    public string? Description { get; set; }      // guidance for the model on what counts as an answer
    public QualificationFieldType DataType { get; set; } // Text, Number, Date, Enum
    public bool IsRequired { get; set; }
    public int Priority { get; set; }             // lower = asked sooner among missing required fields
    public List<string>? PossibleValues { get; set; } // for Enum type; JSON column
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}
```

- One tenant can define any number of fields (property_type, budget, location, doctors, patient
  volume, …) — this replaces the fixed `Budget`/`Interest`/`PurchaseTimeline` trio as *the* source of
  truth for what the agent asks about, matching §4/§5/§6 exactly ("the actual configuration may be
  different for every business").
- Needs `IQualificationFieldService` (Application/Leads or a new `Application/Qualification` folder),
  CRUD DTOs/validators, EF configuration, and a migration, following the exact same shape as
  `MessageTemplateService`/`TagService` (simple per-tenant CRUD, already a well-worn pattern here).

### 2.3 `Lead` — dynamic qualification answers
`src/Core/WhatsAppSalesAutomation.Domain/Entities/Leads/Lead.cs`

Keep `Budget`/`Interest`/`PurchaseTimeline` for backward compatibility (existing pipeline board
filters/sort on them), but add:

```csharp
/// JSON dictionary of FieldKey -> extracted value, one entry per QualificationFieldDefinition
/// this lead has answered. Superset of Budget/Interest/PurchaseTimeline for tenants using custom
/// qualification schemas.
public string? QualificationDataJson { get; set; }
```

- Mirrors how `Tenant.DomainKeywords` is already persisted as one JSON column (see
  `TenantConfiguration`) — same pattern, not a new technique for this codebase.
- `LeadDto`/`LeadMappings` gets a `Dictionary<string,string?> Qualification` property deserialized
  from this column.

### 2.4 New entity or JSON column — `LeadScoringRule` (tenant-configured `{{LEAD_SCORING_RULES}}`)

Simplest option consistent with the rest of the settings system: a single JSON column on `Tenant`
(`LeadScoringRulesJson`) rather than a new table, since these rules are edited as one whole ruleset,
not queried individually — same reasoning as `DomainKeywords`.

```csharp
public class LeadScoringRule
{
    public string TriggerType { get; set; } // "IntentDetected" | "FieldProvided" | "KeywordMatch"
    public string TriggerValue { get; set; } // e.g. "DemoRequest", "budget", "site visit"
    public int Points { get; set; }          // can be negative, e.g. "Just exploring": -10
}
```

Stored as `List<LeadScoringRule>` serialized to `Tenant.LeadScoringRulesJson`, with a sane
platform-wide default (mirrors the master prompt's §10 example table) seeded for new tenants and
editable from the tenant's settings UI — same "business can configure, platform ships a default"
pattern as `AiOptions.EscalationIntents`.

### 2.5 Migrations required

One new EF Core migration (following the existing naming convention, e.g.
`20260916xxxxxx_AddMasterPromptQualificationAndBusinessContext`) covering:
- `Tenants`: `Location`, `WorkingHours`, `PrimaryLanguage`, `LeadScoringRulesJson`
- New `QualificationFieldDefinitions` table
- `Leads`: `QualificationDataJson`

---

## 3. Application layer changes

### 3.1 `IAiService` / `AiConversationContext` — carry business context + qualification schema
`src/Core/WhatsAppSalesAutomation.Application/Common/Interfaces/IAiService.cs`

`AiConversationContext` currently only carries `CustomerName`, message, history, grounding chunks,
summary. Add the fields the master prompt's §2/§4/§10 require the model to see every turn:

```csharp
public record AiConversationContext(
    Guid ConversationId,
    string CustomerName,
    string InboundMessageText,
    IReadOnlyList<AiConversationTurn> RecentHistory,
    IReadOnlyList<AiKnowledgeSnippet> GroundingChunks,
    string? ExistingSummary,
    AiBusinessContext Business,                                  // NEW
    IReadOnlyList<AiQualificationField> QualificationFields,      // NEW
    IReadOnlyDictionary<string, string?> AlreadyKnownQualification); // NEW — merged from Lead.QualificationDataJson + fixed columns

public record AiBusinessContext(
    string BusinessName, string? Industry, string? Location, string? Description,
    string? WebsiteUrl, string? WorkingHours, string PrimaryLanguage);

public record AiQualificationField(
    string FieldKey, string Question, string? Description, string DataType,
    bool IsRequired, int Priority);
```

`AiExtractedEntities` becomes dynamic instead of the fixed 3-tuple:

```csharp
public record AiExtractedEntities(
    string? Budget, string? Interest, string? PurchaseTimeline,          // kept for compatibility
    IReadOnlyDictionary<string, string?> QualificationValues);           // NEW — FieldKey -> value
```

### 3.2 `ConversationOrchestrator` — assemble the new context, add hot-lead short-circuit
`src/Core/WhatsAppSalesAutomation.Application/Ai/ConversationOrchestrator.cs`

- Before building `AiConversationContext`, load the tenant's `QualificationFieldDefinition` rows
  (active, ordered by `Priority`) and the current lead's already-known qualification data (merge
  `Lead.Budget/Interest/PurchaseTimeline` + `QualificationDataJson`), plus `Tenant` business fields —
  pass all of it into the new context record.
- After `_leads.ApplyAiExtractedAttributesAsync(...)`, add the master prompt's §11 rule explicitly:
  if `lead.Score == LeadScoreBand.Hot` **and** the detected intent is one of the "ready to act" set
  (Purchase/Demo/Appointment/SiteVisit/HumanRequest — configurable, not hard-coded, see §3.4 below),
  force escalation (`escalate = true`) even if confidence is high — today only low confidence or a
  fixed `EscalationIntents` list triggers escalation; hot-lead score is not currently a trigger at
  all, which is the gap that produces "keeps asking qualification questions to someone who just said
  they want to buy now."
- When escalating, build the structured handoff summary described in §3.5 instead of the current
  one-line `Notes` string.

### 3.3 `LeadService` — configurable scoring engine
`src/Core/WhatsAppSalesAutomation.Application/Leads/LeadService.cs`

- Replace `ComputeScoreNumeric`'s hard-coded formula with a rule evaluator that reads
  `Tenant.LeadScoringRulesJson` (§2.4) and sums matching rules against: which qualification fields
  are now populated (`FieldProvided`), the detected intent (`IntentDetected`), and optionally keyword
  matches on the inbound text (`KeywordMatch`, for things like "site visit", "demo", "quotation" the
  master prompt's §11 examples call out). Keep the existing 0–100 clamp and `BandFor` thresholds —
  those are presentation-layer concerns the master prompt doesn't dictate, no reason to change them.
- `ApplyAiExtractedAttributesAsync` needs to also merge `entities.QualificationValues` into
  `Lead.QualificationDataJson` (same "merge, don't overwrite; only overwrite on a non-empty new
  value" rule already applied to Budget/Interest/PurchaseTimeline — reuse, don't reinvent).

### 3.4 New config surface — "ready to act" intents drive hot-lead escalation
`src/Core/WhatsAppSalesAutomation.Application/Common/Options/AiOptions.cs`

Add `string[] HotLeadIntents` (default empty, real default lives in `appsettings.json`, same pattern
`EscalationIntents` already uses) — the master prompt's §11 examples (purchase/demo/site
visit/payment/quotation/ready-to-buy/immediate-human-assistance). Register it as tenant-overridable
in `AppSettingCatalog` (`Ai:HotLeadIntents`) next to the existing `Ai:EscalationIntents` entry.

### 3.5 `HandoffService` — structured internal summary
`src/Core/WhatsAppSalesAutomation.Application/Handoffs/HandoffService.cs`

`GetOrCreateOpenHandoffAsync(conversationId, triggerReason, notes, ...)` currently takes one free-text
`notes` string. Add a `HandoffSummaryBuilder` (new small static helper in `Application/Handoffs/`)
that formats the master prompt's §12 example shape from data already on hand at the call site
(customer name, lead's qualification dictionary, lead score/band, detected intent, why it escalated):

```
Lead: {CustomerName}
Requirement: {top qualification fields, e.g. Interest}
{each populated QualificationField}: {value}
Lead Score: {ScoreNumeric}
Temperature: {Score band}
Reason: {escalation reason}
```

`ConversationOrchestrator` builds this and passes it as `notes` instead of the current one-liner. No
interface signature change needed — `HandoffService`'s public contract stays the same; only the
caller's construction of `notes` gets richer.

### 3.6 `InboundWebhookProcessor` — broaden opt-out detection
`src/Core/WhatsAppSalesAutomation.Application/Webhooks/InboundWebhookProcessor.cs`

`OptOutKeywords` is English-only and exact-match. The master prompt's §17 list ("don't message me",
"remove me", "no more messages", "I don't want this") is phrase-shaped, not single tokens, and
Hinglish customers will opt out in mixed language too. Recommended change: keep the deliberate
exact-match design (documented reasoning: avoid false positives like "please stop calling me") but
widen the set to include the multi-word phrases from §17 as additional exact-match entries, and add
common Hinglish equivalents ("mujhe message mat karo", "band karo", "nahi chahiye"). This stays a
config-free, low-risk change — a `HashSet<string>` addition, not new logic.

---

## 4. Infrastructure layer changes

### 4.1 `AiPromptSupport` — replace the system prompt with the master prompt template
`src/Infrastructure/WhatsAppSalesAutomation.Infrastructure/Ai/AiPromptSupport.cs`

This is the centerpiece of the change — **all three real provider clients share this file**, so one
edit here reaches Anthropic/OpenAI/Google uniformly (the `Simulated` client, §4.3, needs its own
treatment since it doesn't call a real model).

- `SystemPrompt(...)` needs a new signature taking `AiBusinessContext` and
  `IReadOnlyList<AiQualificationField>`, and its body becomes a templated rendering of the master
  prompt sections 1, 2, 4–8, 10–22 (role, business context block, qualification objective,
  conversational-not-form-like qualification, answer-then-qualify ordering, intent taxonomy,
  hot-lead/handoff rules, tone, and — critically — §19/§20/user instruction: **default reply
  language is Hinglish, mirroring the customer's own English/Hindi/Hinglish choice once they've sent
  a message.**
- `BuildUserMessage(...)` gains a new section rendering `QualificationFields` (question + required +
  priority) and `AlreadyKnownQualification` (so the model does not re-ask what's already known — the
  master prompt's §5 "never ask a question if the customer already provided that information" is
  only enforceable if the model can see what's already collected).
- `ToolInputSchema()` / `ToolResultPayload` extend `budget`/`interest`/`purchase_timeline` with a
  generic `qualification_values: { "<field_key>": "<value>", ... }` object (JSON Schema
  `additionalProperties: string`, still within the "plain subset all three providers accept" the file
  already documents), plus a `detected_intent` enum-like string field aligned to the master prompt's
  §8 taxonomy, and `lead_temperature_hint` if the business wants the model's own read alongside the
  deterministic score (optional — recommend deferring this one; deterministic scoring in
  `LeadService` should stay the source of truth per §10 "apply the configured rules consistently").
- `ToAiReplyResult(...)` maps the new `qualification_values` object into
  `AiExtractedEntities.QualificationValues`.

### 4.2 Anthropic / OpenAI / Google clients — pass the new context through
`src/Infrastructure/WhatsAppSalesAutomation.Infrastructure/Ai/{AnthropicAiClient,OpenAiAiClient,GoogleAiClient}.cs`

Each currently calls `AiPromptSupport.SystemPrompt(context.CustomerName)`. Update the call site to
the new signature (`AiPromptSupport.SystemPrompt(context.Business, context.QualificationFields)`).
No structural change to the request/response envelope handling in any of the three — they already
delegate everything prompt-shaped to `AiPromptSupport`, which is exactly why this change is
low-risk and localized.

### 4.3 `SimulatedAiClient` — keep it honest for dev/demo use
`src/Infrastructure/WhatsAppSalesAutomation.Infrastructure/Ai/SimulatedAiClient.cs`

Read this file before changing it — it's the no-API-key fallback used for local dev/tests. It should
at minimum stop returning only `Budget/Interest/PurchaseTimeline` and instead echo back one of the
tenant's configured `QualificationFields` as a canned "extracted" value, so dev/test environments
exercise the new dynamic-field path instead of silently only ever testing the legacy 3-field path.

### 4.4 `AiServiceFactory` / `ITenantAiConfigProvider`
`src/Infrastructure/WhatsAppSalesAutomation.Infrastructure/Ai/AiServiceFactory.cs`,
`Tenancy/TenantAiConfigProvider.cs`

No interface change expected — these resolve *which* client to call and its credentials, not prompt
content. Confirm during implementation that nothing here also caches/shapes `AiConversationContext`
in a way that would need updating for the new fields (a quick read, not a redesign).

---

## 5. Presentation layer (API + Admin UI)

### 5.1 Tenant business profile
`src/Presentation/WhatsAppSalesAutomation.Api/Controllers/TenantProfileController.cs` +
`Application/Tenancy/{TenantDtos,TenantService,TenantValidators}.cs`

Extend `UpdateTenantBusinessProfileRequest`/`TenantProfileDto` with `Location`, `WorkingHours`,
`PrimaryLanguage` (§2.1). `PlatformTenantsController`'s tenant-creation path
(`PlatformTenantDtos`/`PlatformTenantService`) gets the same three fields for consistency, same as
how it already mirrors every other `ITenantBusinessDetails` field.

### 5.2 Qualification schema CRUD (new)

New controller `QualificationFieldsController` (`api/v1/qualification-fields`) backed by
`IQualificationFieldService`/`QualificationFieldService` (§2.2) — list/create/update/delete/reorder,
`[Authorize(Roles = AppRoles.Admin)]`, tenant-scoped like every other admin-facing controller here
(`TagsController`, `MessageTemplatesController` are the closest structural precedents).

Admin UI: there's currently a static-HTML admin surface under
`src/Presentation/WhatsAppSalesAutomation.Api/wwwroot/admin/` (`knowledge-base.html`,
`settings.html`) — confirm with the team whether the "real" admin panel is this static surface or a
separate Angular app referenced in `docs/PHASE1-ARCHITECTURE.md` (§3) that isn't in this repo yet;
either way, a `qualification-fields.html` (or Angular route) following the existing
`knowledge-base.html` pattern is the natural home for tenant admins to define their schema.

### 5.3 Lead scoring rules config (new)

Extend `TenantSettingsController`/`SettingsController` (or add a small dedicated endpoint on
`TenantProfileController`) to read/write `Tenant.LeadScoringRulesJson` as a typed
`List<LeadScoringRuleDto>`, validated (non-empty `TriggerType`/`TriggerValue`, integer `Points`) via
FluentValidation like every other settings payload in this codebase.

---

## 6. Language handling (Hinglish-first)

Per the user's explicit instruction ("Language should be hinglish") layered onto the master prompt's
own §19 ("respond in the language used by the customer... do not unnecessarily switch languages"):

1. `Tenant.PrimaryLanguage` defaults to `"hinglish"` (§2.1) — this is what the agent uses for the
   very first outbound message in a conversation, before it has any customer text to mirror.
2. `AiPromptSupport.SystemPrompt` gets an explicit language instruction block:
   > "Default to natural Hinglish (Roman-script Hindi mixed with English, as commonly written on
   > WhatsApp) unless {{PRIMARY_LANGUAGE}} says otherwise. If the customer writes in pure English,
   > reply in English; if in Hindi/Devanagari, reply in Hindi. Match the customer's own script and
   > code-mixing style once they've sent a message — do not switch languages mid-conversation without
   > the customer doing so first."
3. This is purely a prompt-text change (§4.1) — no new field is needed beyond `PrimaryLanguage`
   already covered in §2.1; called out separately here because it's the one requirement the user
   added on top of the pasted master prompt and is easy to lose track of among the larger schema
   changes above.

---

## 7. Suggested implementation phasing

Given the dependency chain (schema → context plumbing → prompt text → provider clients → UI), a
single PR doing everything is risky. Recommended order, each independently shippable:

1. **Migration + entities**: `Tenant` fields, `QualificationFieldDefinition`, `Lead.QualificationDataJson`,
   `Tenant.LeadScoringRulesJson`. No behaviour change yet — additive schema only.
2. **Application plumbing**: `AiConversationContext`/`AiExtractedEntities` expansion,
   `ConversationOrchestrator` assembling the new context, `LeadService` dynamic scoring, hot-lead
   escalation rule, `HandoffService` structured summary. `AiPromptSupport` still ignores the new
   fields at this point (backward-compatible no-op) so this ships safely before the prompt itself
   changes.
3. **Prompt rewrite**: `AiPromptSupport` (system prompt + user message + tool schema) + the three
   real clients' call-site updates + `SimulatedAiClient` update. This is the turn where actual AI
   behaviour changes — worth its own PR so a regression is easy to bisect to.
4. **Admin UI**: qualification-field CRUD, lead-scoring-rule editor, tenant profile
   Location/WorkingHours/PrimaryLanguage fields.
5. **Opt-out keyword widening + Hinglish default** — small, low-risk, can land anytime after step 1
   (no dependency on steps 2–4).

## 8. Risks / open questions to resolve before implementation

- **Backward compatibility of existing leads**: tenants with no `QualificationFieldDefinition` rows
  configured should keep working exactly as today (fall back to the legacy Budget/Interest/
  PurchaseTimeline-only behaviour) — the plan above treats the new schema as additive, never
  required, but this needs to be an explicit acceptance criterion, not an assumption.
- **Prompt token/cost growth**: business context + a full qualification-field list + already-known
  answers all add tokens to every single turn's system+user message. Worth capping
  `QualificationFields`/`AlreadyKnownQualification` render size the same way `ConversationHistoryTurns`
  already caps history, rather than assuming it stays small.
- **`SimulatedAiClient` fidelity**: since it's what runs without any API key configured (default dev
  experience per `AiProviders:Provider`), it needs enough of the new behaviour implemented that a
  developer testing locally actually sees qualification-field-driven questions and hot-lead escalation,
  not just the three legacy fields.
- **Admin UI target**: confirm whether `wwwroot/admin/*.html` or a separate (not-yet-in-repo) Angular
  app is the real target for the new qualification/scoring config screens before starting §5.2/§5.3 —
  building against the wrong surface would be wasted work.
- **Language default vs. tenant industry norms**: defaulting every tenant to Hinglish is a strong
  global default per the user's instruction; confirm `Tenant.PrimaryLanguage` being overridable is
  sufficient, or whether some tenants need it enforced (not overridable) for compliance reasons.
