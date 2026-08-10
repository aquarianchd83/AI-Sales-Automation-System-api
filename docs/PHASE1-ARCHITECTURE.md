# Phase 1 — Architecture, Database Design & Implementation Plan

**System:** WhatsApp Marketing + AI Sales Automation + CRM + Human Handoff Platform
**Stack:** C# / .NET 8, ASP.NET Core Web API, Angular (Admin Panel SPA), EF Core, SQL Server, Clean Architecture, Hangfire
**Scope of this document:** Design only — no executable code. Implementation begins in Phase 2.

---

## 1. Guiding Principles

1. **Clean Architecture** — Domain has zero outward dependencies; Application depends only on Domain; Infrastructure implements Application's interfaces; API/Angular are the outermost shells.
2. **Provider abstraction** — WhatsApp, AI/LLM, Knowledge/RAG, Media Storage, and Notifications are all accessed through interfaces defined in the Application layer, so implementations (Meta Cloud API, OpenAI/Azure OpenAI/Anthropic, S3/Azure Blob, SMTP/SignalR, etc.) can be swapped via DI without touching business logic.
3. **Idempotency-first** — every outbound message, every webhook event, and every background job is designed around a natural or synthetic idempotency key so retries and duplicate webhook deliveries never double-send or double-process.
4. **Compliance-first** — the send pipeline enforces opt-in status, opt-out handling, 24-hour customer service window vs. template-required window, and per-number rate limits *before* a message reaches the WhatsApp API client.
5. **Auditable** — every state-changing action (message sent, mode switch, handoff, lead score change, config change) is captured in an append-only audit log.
6. **Plain service-layer Application design** — no CQRS/MediatR. Each bounded context in the Application layer exposes a straightforward interface + implementation (e.g. `ICampaignService`/`CampaignService`) with one method per use case, taking/returning DTOs. Controllers depend on these interfaces directly via constructor injection. Cross-cutting concerns (validation, logging, idempotency, transactions) are handled explicitly inside each service method or via ordinary ASP.NET Core middleware/action filters, not a mediator pipeline.

---

## 2. High-Level Architecture

```
                              ┌─────────────────────────┐
                              │   Angular Admin Panel    │
                              │  (SPA, JWT bearer auth)  │
                              └────────────┬─────────────┘
                                           │ HTTPS/REST + SignalR (WS)
                              ┌────────────▼─────────────┐
                              │   ASP.NET Core Web API    │  <- Presentation
                              │  Controllers / SignalR Hub│
                              │  Middleware, Filters      │
                              └────────────┬─────────────┘
                                           │
                              ┌────────────▼─────────────┐
                              │     Application Layer     │  <- Use cases (plain service classes)
                              │  Application Services       │
                              │  DTOs / Validators (FluentValidation)
                              │  Interfaces (IWhatsAppService, IAiService,
                              │   IKnowledgeBaseService, IMediaStorageService,
                              │   INotificationService, IRepository<T>, IUnitOfWork)
                              └────────────┬─────────────┘
                                           │
                              ┌────────────▼─────────────┐
                              │       Domain Layer        │  <- Entities, Value Objects,
                              │  Enums, Domain Events,     │     Domain Services, Rules
                              │  Aggregates, Specifications │
                              └────────────▲─────────────┘
                                           │ implements interfaces
                              ┌────────────┴─────────────┐
                              │    Infrastructure Layer   │
                              │  EF Core + SQL Server      │
                              │  WhatsApp Cloud API Client │
                              │  AI/LLM Client (OpenAI/etc)│
                              │  RAG/Vector Store Client   │
                              │  Blob/S3 Media Storage     │
                              │  Hangfire (SQL Server store)│
                              │  Serilog Sinks, Identity   │
                              └────────────┬─────────────┘
                                           │
                        ┌──────────────────┼───────────────────┐
                        ▼                  ▼                   ▼
                 ┌─────────────┐   ┌───────────────┐   ┌───────────────┐
                 │ SQL Server  │   │ WhatsApp Cloud │   │  AI Provider  │
                 │  Database   │   │      API       │   │ (LLM + Vector)│
                 └─────────────┘   └───────┬────────┘   └───────────────┘
                                            │ webhooks (inbound msgs, statuses)
                                            ▼
                                  ┌───────────────────┐
                                  │ /api/webhooks/wa    │
                                  │ (signature-verified)│
                                  └───────────────────┘
```

### Cross-cutting flows

- **Outbound campaign flow:** Hangfire recurring/scheduled job → `CampaignSenderJob` → `IWhatsAppService.SendTemplateMessageAsync` → `MessageLog` row created with `Idempotency Key = CampaignCustomerId + StepNumber` → status updated via webhook callbacks.
- **Inbound message flow:** WhatsApp webhook → `WebhookEvent` persisted (raw payload) → dedup by WhatsApp `message.id` → `InboundMessageProcessedEvent` → `ConversationOrchestrator` decides AI vs Human vs Hybrid → response sent and/or handoff created.
- **AI flow:** `ConversationOrchestrator` → `IAiService.GetResponseAsync(conversationContext)` → internally calls `IKnowledgeBaseService.RetrieveRelevantChunksAsync` (RAG) → LLM call with grounded context + function-calling for structured outputs (intent, lead attributes, confidence, opt-out flag) → response persisted as `AiInteraction`.

---

## 3. Solution / Project Folder Structure

```
WhatsAppSalesAutomation.sln
│
├── src/
│   ├── Core/
│   │   ├── WhatsAppSalesAutomation.Domain/
│   │   │   ├── Entities/
│   │   │   ├── Enums/
│   │   │   ├── ValueObjects/
│   │   │   ├── Events/                     (domain events, e.g. CustomerOptedOutEvent)
│   │   │   ├── Exceptions/
│   │   │   ├── Interfaces/                 (IAggregateRoot, IAuditable, ISoftDeletable)
│   │   │   └── Specifications/
│   │   │
│   │   └── WhatsAppSalesAutomation.Application/
│   │       ├── Common/
│   │       │   ├── Interfaces/             (IApplicationDbContext, IWhatsAppService,
│   │       │   │                             IAiService, IKnowledgeBaseService,
│   │       │   │                             IMediaStorageService, INotificationService,
│   │       │   │                             ICurrentUserService, IDateTimeProvider)
│   │       │   ├── Exceptions/             (ValidationException, NotFoundException, ConflictException)
│   │       │   ├── Idempotency/            (IIdempotencyService + implementation contract)
│   │       │   ├── Mappings/               (AutoMapper profiles)
│   │       │   └── Models/                 (PagedResult<T>, Result<T>)
│   │       ├── Customers/                  (ICustomerService, CustomerService, DTOs/, Validators/)
│   │       ├── Campaigns/                  (ICampaignService, CampaignService, DTOs/, Validators/)
│   │       ├── Messaging/                  (IMessagingOrchestrator, MessagingOrchestrator, DTOs/)
│   │       ├── Conversations/              (IConversationService, ConversationService, DTOs/)
│   │       ├── Leads/                      (ILeadService, LeadService, DTOs/, Validators/)
│   │       ├── KnowledgeBase/              (IKnowledgeBaseAdminService, DTOs/, Validators/)
│   │       ├── HumanHandoff/               (IHandoffService, HandoffService, DTOs/)
│   │       ├── Users/                      (IUserService, UserService, DTOs/, Validators/)
│   │       ├── Reports/                    (IReportService, ReportService, DTOs/)
│   │       └── DependencyInjection.cs
│   │
│   ├── Infrastructure/
│   │   ├── WhatsAppSalesAutomation.Infrastructure/
│   │   │   ├── Persistence/
│   │   │   │   ├── ApplicationDbContext.cs
│   │   │   │   ├── Configurations/         (EF Fluent API per entity)
│   │   │   │   ├── Migrations/
│   │   │   │   ├── Repositories/
│   │   │   │   └── Interceptors/           (AuditableEntitySaveChangesInterceptor)
│   │   │   ├── Identity/                   (ASP.NET Core Identity + JWT)
│   │   │   ├── BackgroundJobs/              (Hangfire job classes)
│   │   │   ├── Services/                    (NotificationService, DateTimeProvider)
│   │   │   └── DependencyInjection.cs
│   │   │
│   │   ├── WhatsAppSalesAutomation.WhatsAppApi/
│   │   │   ├── WhatsAppCloudApiClient.cs   (implements IWhatsAppService)
│   │   │   ├── Models/                      (Meta Graph API DTOs)
│   │   │   ├── WebhookSignatureValidator.cs
│   │   │   └── DependencyInjection.cs
│   │   │
│   │   ├── WhatsAppSalesAutomation.AI/
│   │   │   ├── LlmAiService.cs             (implements IAiService)
│   │   │   ├── Prompts/                     (system prompt templates)
│   │   │   ├── FunctionSchemas/             (structured-output tool defs)
│   │   │   └── DependencyInjection.cs
│   │   │
│   │   ├── WhatsAppSalesAutomation.KnowledgeBase/
│   │   │   ├── RagKnowledgeBaseService.cs  (implements IKnowledgeBaseService)
│   │   │   ├── Embedding/                   (embedding generation)
│   │   │   ├── VectorStore/                 (SQL Server vector / pgvector-alt / Qdrant client)
│   │   │   └── DependencyInjection.cs
│   │   │
│   │   └── WhatsAppSalesAutomation.MediaStorage/
│   │       ├── AzureBlobMediaStorageService.cs / S3MediaStorageService.cs
│   │       └── DependencyInjection.cs
│   │
│   ├── Presentation/
│   │   └── WhatsAppSalesAutomation.Api/
│   │       ├── Controllers/                 (v1: Auth, Users, Customers, Campaigns,
│   │       │                                  Media, Conversations, Leads, KnowledgeBase,
│   │       │                                  Webhooks, Reports, Config)
│   │       ├── Hubs/                        (ConversationHub - SignalR for live Agent Inbox)
│   │       ├── Middleware/                  (ExceptionHandling, RequestLogging, RateLimiting)
│   │       ├── Filters/
│   │       ├── Extensions/                  (Swagger, Serilog, Auth, Hangfire dashboard setup)
│   │       ├── appsettings.json / .Development.json
│   │       └── Program.cs
│   │
│   └── Client/
│       └── whatsapp-admin-panel/            (Angular 18+ workspace)
│           ├── src/app/
│           │   ├── core/                    (auth guard, interceptors, services)
│           │   ├── shared/                  (shared components, pipes, directives)
│           │   ├── features/
│           │   │   ├── dashboard/
│           │   │   ├── customers/
│           │   │   ├── campaigns/
│           │   │   ├── media-library/
│           │   │   ├── inbox/
│           │   │   ├── knowledge-base/
│           │   │   ├── leads-crm/
│           │   │   ├── reports/
│           │   │   ├── settings/
│           │   │   └── users-roles/
│           │   └── app.routes.ts
│           └── angular.json
│
├── tests/
│   ├── WhatsAppSalesAutomation.Domain.UnitTests/
│   ├── WhatsAppSalesAutomation.Application.UnitTests/
│   ├── WhatsAppSalesAutomation.Infrastructure.IntegrationTests/
│   └── WhatsAppSalesAutomation.Api.FunctionalTests/
│
├── docs/
│   ├── PHASE1-ARCHITECTURE.md               (this file)
│   └── ...(future phase docs)
│
├── docker/
│   ├── Dockerfile.api
│   ├── Dockerfile.client
│   └── docker-compose.yml
│
└── WhatsAppSalesAutomation.sln
```

---

## 4. Database Design

### 4.1 Entity List (by bounded context)

**Identity / Access**
- `Users`, `Roles`, `UserRoles`, `RefreshTokens`

**Customer & CRM**
- `Customers`, `CustomerTags`, `CustomerTagMap`, `Leads`, `LeadActivities`

**Campaign & Messaging**
- `Campaigns`, `CampaignSteps` (initial + up to 4 follow-ups), `CampaignStepMedia`, `CampaignCustomers`, `MessageTemplates`, `Messages` (message log), `MessageMedia`, `MediaAssets`

**Conversation & AI**
- `Conversations`, `ConversationMessages` (unified inbound/outbound transcript), `AiInteractions`, `KnowledgeBaseArticles`, `KnowledgeBaseChunks`, `HumanHandoffs`

**Platform**
- `WebhookEvents`, `AuditLogs`, `SystemConfigurations`, `Notifications`

### 4.2 Entity Relationship Diagram (textual)

```
Users (1) ────< UserRoles >──── (1) Roles

Customers (1) ──< CustomerTagMap >── (1) CustomerTags
Customers (1) ────< Leads (1:1 active lead, 1:many history) 
Customers (1) ────< CampaignCustomers >──── (1) Campaigns
Customers (1) ────< Conversations (1: one active conversation per customer, but historical allowed)
Customers (1) ────< Messages (all messages ever sent/received tied to this customer)

Campaigns (1) ────< CampaignSteps (0:1 Initial + 0:4 FollowUp, ordered by StepNumber)
CampaignSteps (1) ──< CampaignStepMedia >── (1) MediaAssets
Campaigns (1) ────< CampaignCustomers  (join: campaign x customer, tracks per-customer progress)
CampaignCustomers (1) ──< Messages (each step sent to this customer produces a Message row)

Messages (1) ──< MessageMedia >── (1) MediaAssets
Messages (many) ──> (1) Conversations  (a message belongs to a conversation thread)
Messages (many) ──> (0..1) CampaignCustomers  (only for campaign-originated messages)
Messages (1) ──> (0..1) MessageTemplates (if sent via WA template)

Conversations (1) ──< ConversationMessages  (denormalized/linked view over Messages, optional if Messages IS the transcript)
Conversations (1) ──< AiInteractions  (one row per AI turn: intent, confidence, entities extracted)
Conversations (1) ──< HumanHandoffs  (escalation events)
Conversations (many) ──> (0..1) Users  (AssignedAgentId)
Conversations (many) ──> (1) Customers
Conversations has ConversationMode: AI | Human | Hybrid

Leads (many) ──> (1) Customers
Leads (many) ──> (0..1) Campaigns (source campaign)
Leads (many) ──> (0..1) Users (AssignedTo - sales agent/manager)
Leads (1) ──< LeadActivities (score changes, notes, stage changes)

KnowledgeBaseArticles (1) ──< KnowledgeBaseChunks (chunked + embedded for RAG)
AiInteractions (many) ──> (0..n) KnowledgeBaseChunks (KBChunksUsed, via join table AiInteractionSources)

WebhookEvents  (raw inbound log, independent, linked to Messages by WhatsAppMessageId after processing)
AuditLogs      (polymorphic: EntityName + EntityId + Action + ChangesJson + UserId + Timestamp)
SystemConfigurations (key-value, typed, category e.g. "Escalation Rules", "Follow-up Delays", "Rate Limits")
MediaAssets    (many) ──> (1) Users (UploadedBy)
```

### 4.3 Core Table Definitions (key columns only — full DDL delivered in Phase 2)

**Users**
`Id (uniqueidentifier PK), FullName, Email (unique), PhoneNumber, PasswordHash, IsActive, CreatedAt, LastLoginAt`

**Roles** (seeded): `SuperAdmin`, `Admin`, `SalesManager`, `SalesAgent`

**Customers**
`Id PK, PhoneNumberE164 (unique, indexed), FirstName, LastName, Email, Source, OptInStatus (enum: PendingOptIn/OptedIn/OptedOut), OptInTimestamp, OptOutTimestamp, PreferredLanguage, AssignedAgentId FK Users, CreatedAt, UpdatedAt, IsDeleted`

**CustomerTags / CustomerTagMap**
`Tags.Id, Tags.Name (unique)`; `CustomerTagMap(CustomerId, TagId)` composite PK

**Campaigns**
`Id PK, Name, Description, Status (enum: Draft/Scheduled/Running/Paused/Stopped/Completed), ScheduledStartAt, CreatedBy FK Users, CreatedAt, StartedAt, StoppedAt, TargetAudienceFilterJson`

**CampaignSteps**
`Id PK, CampaignId FK, StepType (enum: Initial/FollowUp1..4), StepNumber (0-4), MessageText, DelayDaysAfterPrevious (0 for Initial), WhatsAppTemplateId FK nullable, IsActive`
*Constraint:* at most 1 Initial + 4 FollowUp per campaign; each step must have 2–5 associated `CampaignStepMedia` rows (enforced in Application validation, DB check via trigger/stored proc optional).

**CampaignStepMedia**
`Id PK, CampaignStepId FK, MediaAssetId FK, DisplayOrder`

**CampaignCustomers** (join + progress tracker)
`Id PK, CampaignId FK, CustomerId FK, Status (enum: Pending/InitialSent/AwaitingResponse/FollowUp1Sent.../Responded/OptedOut/HandedOff/Completed/Failed), CurrentStepNumber, LastMessageSentAt, LastCustomerResponseAt, NextFollowUpDueAt, StoppedReason, UNIQUE(CampaignId, CustomerId)`

**MessageTemplates**
`Id PK, Name, Language, Category (Marketing/Utility/Authentication), WhatsAppTemplateName, WhatsAppTemplateStatus (Approved/Pending/Rejected), BodyPlaceholdersJson`

**MediaAssets**
`Id PK, FileName, ContentType, SizeBytes, StorageProvider, StorageKey/Url, WhatsAppMediaId (cached upload handle), Checksum (dedup), UploadedBy FK Users, CreatedAt`

**Messages** (the single source-of-truth message log — inbound + outbound)
`Id PK, ConversationId FK, CustomerId FK, CampaignCustomerId FK nullable, Direction (Inbound/Outbound), MessageType (Text/Template/Media/Interactive), Text, WhatsAppMessageId (unique, indexed — used for dedup), IdempotencyKey (unique, indexed — synthetic for outbound), Status (Queued/Sent/Delivered/Read/Failed), FailureReason, SentAt, DeliveredAt, ReadAt, CreatedAt`

**MessageMedia**
`Id PK, MessageId FK, MediaAssetId FK, DisplayOrder`

**Conversations**
`Id PK, CustomerId FK, Mode (AI/Human/Hybrid), Status (Open/Closed/Escalated), AssignedAgentId FK Users nullable, LastMessageAt, AiConfidenceLast, LastDetectedIntent, LastLeadScore (Hot/Warm/Cold), Summary (AI-generated running summary), CreatedAt, ClosedAt`

**AiInteractions**
`Id PK, ConversationId FK, InboundMessageId FK, DetectedIntent, ConfidenceScore, ExtractedEntitiesJson (budget/interest/timeline), ProposedResponseText, ActionTaken (Replied/Escalated/NoActionNeeded), ModelUsed, PromptTokens, CompletionTokens, LatencyMs, CreatedAt`

**AiInteractionSources** (join for RAG citation)
`AiInteractionId FK, KnowledgeBaseChunkId FK, RelevanceScore`

**KnowledgeBaseArticles**
`Id PK, Title, Category, SourceType (Manual/Upload), Content (canonical approved text), Status (Draft/Published/Archived), Version, ApprovedBy FK Users, CreatedAt, UpdatedAt`

**KnowledgeBaseChunks**
`Id PK, ArticleId FK, ChunkIndex, ChunkText, Embedding (vector/varbinary), TokenCount`

**Leads**
`Id PK, CustomerId FK, CampaignId FK nullable, Stage (New/Qualifying/Qualified/Negotiation/Won/Lost), Score (Hot/Warm/Cold), ScoreNumeric, Budget, Interest, PurchaseTimeline, AssignedTo FK Users, LastActivityAt, CreatedAt, UpdatedAt`

**LeadActivities**
`Id PK, LeadId FK, ActivityType (ScoreChanged/StageChanged/Note/AssignmentChanged), OldValue, NewValue, Note, CreatedBy FK Users nullable (null = system/AI), CreatedAt`

**HumanHandoffs**
`Id PK, ConversationId FK, TriggerReason (CustomerRequested/LowConfidence/CannotAnswer/Complaint/Negotiation/ComplexTechnical/RuleTriggered), TriggeredByRuleId nullable, Status (Pending/Assigned/InProgress/Resolved), AssignedAgentId FK Users nullable, AssignedAt, ResolvedAt, Notes, CreatedAt`

**WebhookEvents**
`Id PK, Provider (WhatsApp), EventType (message/status/...), RawPayload (nvarchar(max)), WhatsAppMessageId nullable indexed, ProcessedAt nullable, ProcessingStatus (Pending/Processed/Failed/Duplicate), ReceivedAt`

**AuditLogs**
`Id PK, EntityName, EntityId, Action (Create/Update/Delete/StatusChange), ChangesJson, PerformedBy FK Users nullable, PerformedAt, IpAddress`

**SystemConfigurations**
`Id PK, Category, Key, Value, ValueType, Description, UpdatedBy FK Users, UpdatedAt` — drives configurable escalation rules, follow-up delay defaults, rate limits, AI confidence threshold, messaging window rules.

**RefreshTokens**
`Id PK, UserId FK, TokenHash, ExpiresAt, RevokedAt, CreatedByIp`

### 4.4 Key Indexes / Constraints

- `Customers.PhoneNumberE164` — unique index (dedup on import).
- `Messages.WhatsAppMessageId` — unique filtered index (WHERE NOT NULL) for inbound dedup.
- `Messages.IdempotencyKey` — unique index for outbound dedup (prevents double-send on job retry).
- `CampaignCustomers (CampaignId, CustomerId)` — unique composite.
- `WebhookEvents.WhatsAppMessageId` + `EventType` — non-unique index for fast lookup, dedup handled in processing logic via a separate unique constraint on `(WhatsAppMessageId, EventType, StatusValue)` for status events.
- Soft-delete pattern (`IsDeleted`, global query filter) on `Customers`, `Campaigns`, `KnowledgeBaseArticles`.
- All tables: `RowVersion` (concurrency token) where concurrent updates are plausible (`CampaignCustomers`, `Conversations`, `Leads`).

---

## 5. API List (ASP.NET Core Web API — versioned `/api/v1/...`)

### Auth
- `POST /api/v1/auth/login`
- `POST /api/v1/auth/refresh-token`
- `POST /api/v1/auth/logout`
- `POST /api/v1/auth/change-password`

### Users & Roles
- `GET/POST /api/v1/users`
- `GET/PUT/DELETE /api/v1/users/{id}`
- `PUT /api/v1/users/{id}/roles`
- `GET /api/v1/roles`

### Customers
- `GET/POST /api/v1/customers`
- `GET/PUT/DELETE /api/v1/customers/{id}`
- `POST /api/v1/customers/import` (CSV/Excel upload → background validation job)
- `GET /api/v1/customers/import/{jobId}/status`
- `POST /api/v1/customers/{id}/tags`
- `POST /api/v1/customers/{id}/opt-out`
- `GET /api/v1/customers/{id}/timeline` (messages + lead history)

### Campaigns
- `GET/POST /api/v1/campaigns`
- `GET/PUT/DELETE /api/v1/campaigns/{id}`
- `POST /api/v1/campaigns/{id}/steps` (define initial + follow-ups)
- `PUT /api/v1/campaigns/{id}/steps/{stepId}`
- `POST /api/v1/campaigns/{id}/audience` (attach customers by filter or explicit list)
- `POST /api/v1/campaigns/{id}/start`
- `POST /api/v1/campaigns/{id}/pause`
- `POST /api/v1/campaigns/{id}/resume`
- `POST /api/v1/campaigns/{id}/stop`
- `GET /api/v1/campaigns/{id}/progress` (per-customer status breakdown)

### Media Library
- `POST /api/v1/media/upload`
- `GET /api/v1/media`
- `DELETE /api/v1/media/{id}`

### WhatsApp Webhooks
- `GET /api/v1/webhooks/whatsapp` (verification handshake)
- `POST /api/v1/webhooks/whatsapp` (inbound messages + statuses, signature-verified)

### Conversations / Agent Inbox
- `GET /api/v1/conversations` (filter: mode, status, assigned agent)
- `GET /api/v1/conversations/{id}`
- `POST /api/v1/conversations/{id}/messages` (agent manual reply)
- `PUT /api/v1/conversations/{id}/mode` (switch AI/Human/Hybrid)
- `POST /api/v1/conversations/{id}/assign`
- `POST /api/v1/conversations/{id}/close`
- SignalR Hub `/hubs/conversations` — real-time push of new messages/handoffs to Agent Inbox

### Human Handoff
- `GET /api/v1/handoffs` (queue view)
- `POST /api/v1/handoffs/{id}/claim`
- `POST /api/v1/handoffs/{id}/resolve`

### Leads / CRM
- `GET /api/v1/leads`
- `GET/PUT /api/v1/leads/{id}`
- `POST /api/v1/leads/{id}/activities`
- `PUT /api/v1/leads/{id}/assign`

### Knowledge Base
- `GET/POST /api/v1/knowledge-base/articles`
- `GET/PUT/DELETE /api/v1/knowledge-base/articles/{id}`
- `POST /api/v1/knowledge-base/articles/{id}/publish`
- `POST /api/v1/knowledge-base/reindex`

### Reports
- `GET /api/v1/reports/campaign-performance`
- `GET /api/v1/reports/lead-funnel`
- `GET /api/v1/reports/agent-performance`
- `GET /api/v1/reports/ai-performance`

### System Configuration
- `GET/PUT /api/v1/config/escalation-rules`
- `GET/PUT /api/v1/config/follow-up-defaults`
- `GET/PUT /api/v1/config/rate-limits`
- `GET /api/v1/audit-logs`

### Jobs / Ops (protected, SuperAdmin only)
- `GET /hangfire` (Hangfire Dashboard, authenticated)
- `GET /api/v1/health` (health checks: DB, WhatsApp API, AI provider)

---

## 6. Admin Panel (Angular) — Page List

1. **Login** (+ forgot password)
2. **Dashboard** — campaign KPIs, active conversations, handoff queue depth, lead funnel snapshot
3. **Customers**
   - Customer list (search/filter/tags)
   - Customer detail (profile, timeline, lead info)
   - Import wizard (CSV/Excel upload, column mapping, validation preview, import status)
4. **Campaigns**
   - Campaign list
   - Campaign builder wizard (Initial message + up to 4 follow-ups, media picker, delay config, audience selection)
   - Campaign detail/progress dashboard (funnel: Sent → Delivered → Read → Responded → Opted-out per step)
5. **Media Library** — grid/list of uploaded videos & assets, upload, tag, usage-in-campaigns view
6. **Agent Inbox / Conversations**
   - Conversation list (filter by mode/status/assigned agent/unassigned)
   - Conversation detail: chat transcript, AI summary panel, detected intent, lead score, customer info panel, campaign origin, mode switch control, quick-reply/manual send
7. **Human Handoff Queue** — pending escalations, claim/assign, resolve
8. **Knowledge Base**
   - Article list (status: Draft/Published/Archived)
   - Article editor (rich text, category, versioning)
   - Reindex/publish controls
9. **Leads / CRM**
   - Lead pipeline board (New/Qualifying/Qualified/Negotiation/Won/Lost)
   - Lead detail (budget/interest/timeline, activity history, assignment)
10. **Reports**
    - Campaign performance
    - Lead funnel & conversion
    - Agent performance
    - AI performance (confidence trends, escalation rate, containment rate)
11. **System Configuration**
    - Escalation rules
    - Follow-up delay defaults & messaging window rules
    - Rate limits
    - WhatsApp integration settings (phone number ID, tokens — masked)
    - AI provider settings (model, confidence threshold)
12. **User & Role Management** — users list, invite/create, role assignment
13. **Audit Log Viewer**

---

## 7. Background Jobs (Hangfire)

| Job | Trigger | Idempotency Strategy |
|---|---|---|
| `CampaignInitialSenderJob` | Recurring (e.g. every 1 min) scans `CampaignCustomers` with Status=Pending for Running campaigns | Unique `IdempotencyKey = CampaignId:CustomerId:Step0`; checked before send |
| `FollowUpSchedulerJob` | Recurring, evaluates `NextFollowUpDueAt <= now` and `Status != Responded/OptedOut/HandedOff` | Idempotency key per step number; re-checks response status immediately before send (guards race) |
| `MessageStatusRetryJob` | Recurring, retries `Failed` sends within retry policy window | Uses same `IdempotencyKey`; exponential backoff via Hangfire `AutomaticRetry` + custom max-attempts column |
| `AiProcessingJob` | Enqueued immediately on inbound webhook (fire-and-forget from webhook handler, processed by Hangfire for durability) | Keyed by `WhatsAppMessageId` — a message is AI-processed at most once |
| `LeadScoringJob` | Enqueued after each `AiInteraction` that updates lead attributes; also nightly recurring re-score sweep | Recomputes deterministically from current lead attributes — naturally idempotent |
| `WebhookEventCleanupJob` | Recurring nightly | N/A (housekeeping) |
| `KnowledgeBaseReindexJob` | Enqueued on article publish, or manual trigger | Keyed by ArticleId+Version |

All jobs use `[DisableConcurrentExecution]` where per-entity races are possible, plus DB-level unique constraints as the final guard (defense in depth).

---

## 8. Conversation & Escalation State Machine (summary)

```
Conversation.Mode = AI (default) | Human | Hybrid

Inbound message arrives:
  -> if Customer.OptInStatus == OptedOut or message is opt-out keyword: stop all automation, log, notify agent (no AI reply)
  -> else if Mode == Human: no AI action, notify assigned agent via SignalR
  -> else (AI or Hybrid):
       AiInteraction computed (intent, confidence, entities)
       if confidence < threshold OR intent in {ComplaintIntent, HumanRequestIntent, NegotiationIntent, ComplexTechnicalIntent}
           OR a configured SystemConfiguration escalation rule matches:
             -> create HumanHandoff, set Conversation.Status=Escalated, notify agent queue
             -> if Mode == AI: still no auto-reply beyond an optional holding message
             -> if Mode == Hybrid: AI may still answer FAQ-safe portions before handing off complex part (configurable)
       else:
             -> AI sends grounded reply via IWhatsAppService
             -> Lead attributes/score updated
```

---

## 9. Security & Compliance Controls (design-level)

- **AuthN/AuthZ:** ASP.NET Core Identity + JWT (short-lived access token + rotating refresh token), policy-based authorization mapped to the 4 roles; controller/action-level `[Authorize(Policy=...)]`.
- **Webhook verification:** Meta's `X-Hub-Signature-256` HMAC validated against raw request body before any processing; GET verification challenge (`hub.verify_token`) checked against configured secret.
- **Secrets:** WhatsApp access token, AI provider keys, DB connection string via `IConfiguration` + environment variables / secret manager (Azure Key Vault or AWS Secrets Manager abstraction) — never in source or appsettings committed to git.
- **Rate limiting:** ASP.NET Core built-in `Microsoft.AspNetCore.RateLimiting` on public endpoints (webhook, auth); separate outbound rate governor respecting WhatsApp per-number messaging limits before calling the Cloud API.
- **Validation:** FluentValidation validators invoked explicitly at the top of each Application Service method (request DTO in → `ValidateAndThrowAsync` → proceed); the API layer's model binding covers basic type/shape errors before the service is even called.
- **Idempotency:** synthetic idempotency keys on all outbound sends, checked directly in `MessagingOrchestrator`/repository methods against the DB unique index before insert; a shared `IIdempotencyService` (Application interface, Infrastructure-backed) handles the optional client-supplied `Idempotency-Key` header on state-changing POST endpoints (e.g. manual agent send) via an API-layer action filter.
- **Audit logging:** `SaveChanges` interceptor auto-captures entity changes into `AuditLogs`; explicit audit entries for mode switches, handoffs, opt-outs, config changes.
- **Logging:** Serilog structured logging → console + file + (optional) Seq/App Insights sink; correlation IDs per request/job.
- **Opt-in/opt-out & messaging windows:** enforced in the `Application` layer before any `IWhatsAppService` call — free-form text only inside the 24-hour customer service window, otherwise an approved template is required; opt-out keywords (`STOP`, etc.) intercepted before AI processing.

---

## 10. Complete Implementation Plan (Phases 2–6 preview)

| Phase | Deliverables |
|---|---|
| **2** | Solution scaffold (all projects, references), EF Core `ApplicationDbContext` + migrations for Identity/Customers, ASP.NET Identity + JWT auth, Users/Roles CRUD, Customer CRUD + CSV/Excel import, Serilog wired, Swagger, base Angular shell (auth, layout, customers screens) |
| **3** | Campaign & CampaignStep & Media entities/migrations, Media Library (upload to blob/S3 abstraction), `WhatsAppCloudApiClient` (send template/text/media), Initial-message send pipeline, Follow-up engine, Hangfire setup + jobs from §7, Campaign Angular UI |
| **4** | Webhook endpoint (verify + receive), `WebhookEvent` processing pipeline, `Conversations`/`Messages` unification, SignalR Agent Inbox hub, Human Handoff entities/workflow, Agent Inbox Angular UI |
| **5** | `IAiService` + provider implementation, intent detection & structured extraction, `IKnowledgeBaseService` RAG pipeline (embedding + retrieval), Lead qualification & scoring, AI/Human/Hybrid mode logic wired into orchestrator, Knowledge Base Angular UI |
| **6** | Leads/CRM UI + pipeline board, Reports & dashboards, Audit log viewer, security hardening pass (rate limiting, headers, pen-test fixes), unit/integration/functional test suites, Dockerfiles + docker-compose, deployment docs (Azure/AWS + on-prem SQL Server) |

Each phase will ship compilable, runnable code with its own run/test instructions, building strictly on the artifacts from the prior phase.

---

## 11. Open Configuration Assumptions (confirm or override before Phase 2)

1. **AI provider:** Assume Anthropic Claude (via Messages API) as default `IAiService` implementation, swappable — confirm or specify OpenAI/Azure OpenAI instead.
2. **Vector store for RAG:** Assume SQL Server-native vector search (SQL Server 2025) or a pluggable external vector DB (Qdrant/pgvector) — confirm preference; default plan uses an abstraction that starts with an in-SQL cosine-similarity implementation to avoid an extra infra dependency for Phase 5, upgradeable later.
3. **Media storage:** Assume Azure Blob Storage by default (swap to AWS S3 via the same interface) — confirm cloud target.
4. **Deployment target:** Assume Docker containers behind IIS/Nginx or cloud App Service — confirm for Phase 6 deployment docs.
5. **WhatsApp Business Account:** You will supply a Meta WhatsApp Business Account, phone number ID, and permanent access token when we reach Phase 3 integration; templates will need Meta approval before Phase 3 testing of template-based sends.

If no response is given, Phase 2 will proceed with the defaults above.

---

**End of Phase 1.** Reply to confirm, or request changes to the architecture/DB/API/page list, before Phase 2 (solution scaffold, database, auth, customer management) begins.
