# Deployment

How to stand this platform up somewhere other than a developer's machine. Two deployables:

| | |
|---|---|
| **API** | `src/Presentation/WhatsAppSalesAutomation.Api` — ASP.NET Core 8, one origin (`api.<domain>`) |
| **Frontend** | the sibling Angular repo — one build, served from every tenant subdomain (`*.<domain>`) |

They are separate origins on purpose, which is why CORS is configured rather than absent. See
[Wildcard DNS and CORS](#4-wildcard-dns-and-cors).

> **Containers are deliberately not covered here.** No Dockerfile or compose file is committed, and
> this guide describes the framework-dependent / self-contained publish route instead. Adding
> containers later does not invalidate anything below: the configuration, secret and database
> sections are the same either way.

---

## 1. Prerequisites

| | Version | Notes |
|---|---|---|
| .NET | **8.0** runtime (ASP.NET Core) | `TargetFramework` is `net8.0` across every project. The SDK is only needed on the build machine. |
| SQL Server | **2019 or later** to run | See [SQL Server and vector search](#3-sql-server-and-vector-search) — **2025 or Azure SQL** to get native vector retrieval. |
| Reverse proxy | any | TLS terminates here. Forwarded headers are already handled — see [TLS and forwarded headers](#5-tls-and-forwarded-headers). |

The API also needs outbound HTTPS to:

- `graph.facebook.com` — WhatsApp Cloud API
- the configured AI provider (`api.openai.com` by default)
- `api.stripe.com` — only if billing is enabled

---

## 2. Build and publish

```bash
dotnet publish src/Presentation/WhatsAppSalesAutomation.Api -c Release -o ./publish
```

Run it behind the reverse proxy:

```bash
ASPNETCORE_ENVIRONMENT=Production dotnet ./publish/WhatsAppSalesAutomation.Api.dll
```

### What happens on first start

`Program.cs` does all of this before serving a request, and all of it is idempotent — it converges on
every boot rather than only the first, so a restart is also how a deployment recovers from drift:

1. `Database.MigrateAsync()` — applies every pending EF migration.
2. `IdentitySeeder` — creates roles, and the SuperAdmin if `Seed:SuperAdminEmail` is set.
3. `AppSettingsSeeder` — inserts a DB row for any `AppSettingCatalog` key that has none yet, then
   reloads configuration so the first request already sees DB-backed values.
4. `PlanSeeder`, `QuotaCatalogSeeder`, `FaqSeeder`, `QualificationSeeder` — real catalogue data every
   environment needs, not dev conveniences.
5. `ITenantJobProvisioner.ReconcileAllAsync()` — registers each tenant's recurring jobs.

**The database user therefore needs DDL rights**, not just DML. If your policy forbids that, generate
a script instead and let a DBA run it:

```bash
dotnet ef migrations script --idempotent \
  --project src/Infrastructure/WhatsAppSalesAutomation.Infrastructure \
  --startup-project src/Infrastructure/WhatsAppSalesAutomation.Infrastructure \
  --output migrate.sql
```

Note both `--project` and `--startup-project` point at Infrastructure. That works because of
`DesignTimeDbContextFactory`, and it means migration tooling never has to boot the web host — so it
also works while the API is running.

---

## 3. SQL Server and vector search

The knowledge base can store embeddings two ways, and **the application picks one at startup by
probing the server** (`SqlServerVectorCapability`). It does not compare version numbers, because the
only question that matters is whether this connection can execute the syntax.

| Server | Store used | Consequence |
|---|---|---|
| SQL Server **2025**, Azure SQL | `SqlServerVectorStore` — native `VECTOR(1536)` + DiskANN index | Similarity is computed in the database. Scales. |
| Anything older | `JsonColumnVectorStore` — JSON column, cosine in the application | Correct, materially slower. Every eligible chunk's vector crosses the wire per query. |

Which one is active is logged at startup, at `Information` for the native path and **`Warning`** for
the fallback. Check for this line after any deployment:

```
Knowledge base vector search: using native SQL Server VECTOR (server version 17.x)
```

The `EmbeddingVector` column and its index are created by the `AddPhase6VectorStorage` migration
**only if the server supports them**, through dynamic SQL — so the same migration runs unchanged on
both, and upgrading the server later backfills the column from the JSON one automatically on the next
migration run.

### Full-text search

The keyword half of hybrid retrieval. Like the vector store it is **chosen at startup by probing the
server**, and logged: `SqlServerFullTextKeywordStore` where the Full-Text feature is installed *and*
the `KnowledgeBaseChunks` index exists, otherwise `Bm25KeywordStore` (BM25 scored in the application).

| Server | Keyword store | Consequence |
|---|---|---|
| Full-Text installed | SQL Server Full-Text (`FREETEXTTABLE`) | Scales. |
| Not installed | in-application BM25 | Correct, but reads every eligible chunk's text per query. |

The `AddPhase6FullTextSearch` migration creates the catalogue and index **only where the feature is
installed**, and is a no-op elsewhere. It runs outside the migration transaction (SQL Server refuses
`CREATE FULLTEXT INDEX` inside one), so it is written to be safely re-runnable. To move an existing
deployment onto the native path: install the Full-Text feature, then re-run the migration script.

Note this deliberately differs from the design document's EC-23 ("vector-only mode"): on a server with
neither native vectors nor Full-Text, vector-only retrieval cannot find an exact token such as an error
code, so a slower keyword leg is used instead of none.

### Hangfire

Background jobs use the same database under the `Hangfire` schema. No separate store to provision.

---

## 3a. Support knowledge base: indexing, reranking, thresholds

**Indexing runs as a Hangfire job** (`KnowledgeIndexingJob`), not in the request. It needs no separate
worker - the API process hosts the Hangfire server - but it does mean an embedding provider outage
shows up as a `Failed` row in `KnowledgeIngestionJobs`, not as an HTTP error. A failed job is resumable:
staged chunks and vectors are kept, and re-queueing the same article version continues from the last
completed batch.

**GLOBAL (platform) articles need platform-level embedding credentials that do not exist yet.** A
platform article is indexed with no tenant in scope, and every embedding provider's credentials are
resolved per tenant - so today it is embedded by the `Simulated` provider only, and tenants using a real
provider will not retrieve it. The job records this in its `VerificationNotes` rather than failing. Until
that is resolved, treat platform-authored knowledge as not yet live for real-provider tenants.

### Publishing and uploading

The KB screen's **Publish** now runs the ingestion pipeline inline (chunk, scan, embed, swap), so the
response still arrives when indexing is done. It differs from the old publish in three ways worth knowing:

- **A failed publish leaves the article as it was.** If indexing fails or the content is refused, the
  article goes back to its previous status and the user gets a 400 explaining why - it is never left
  "Published" with nothing indexed, and a previously published version keeps serving.
- **Content that reads like an instruction to the AI is refused.** Tool-invocation and role-assumption
  wording is blocked outright. Milder wording ("ignore previous instructions") is refused until the caller
  publishes again with `?securityReviewed=true`, having read the quoted sentence in the error.
- **Publish always re-embeds**, even an unchanged article, as it always has.

`POST /api/v1/knowledge-base/articles/upload` (multipart: `file`, optional `title`, `category`,
`sourceType`) creates a **draft** from a .md, .txt, .html, .docx or .pdf up to 10 MB. Nothing is published
by an upload. The response carries a quality score; a low one (PDFs especially) means read it before
publishing. `GET /api/v1/knowledge-base/articles/{id}/indexing` reports the latest indexing run, its
security findings and any post-index warnings.

Note: the sales AI's retrieval was tightened at the same time. It now ignores inactive (staged) chunks and
platform-owned (GLOBAL) knowledge, so a re-index in progress can never leak stale text into a reply and
platform support content is never quoted to a tenant's customers.

### Reranker

```json
"Reranker": { "Cohere": { "ApiKey": "", "Model": "rerank-v3.5", "TimeoutSeconds": 3 } }
```

Supply the key as `Reranker__Cohere__ApiKey`. **Leaving it empty is a supported state**, not a
misconfiguration: retrieval then runs in `FusionOnly` mode with a stricter evidence gate (top score 0.75
instead of 0.62, three supporting chunks instead of two, no single-chunk exception). The agent escalates
more; it does not guess more. Each retrieval records which mode it ran in, and a rerank circuit breaker
opens for 60 seconds after five consecutive failures.

### Thresholds

`SupportRag` in `appsettings.json` (the full list is `SupportRagOptions`). Two values are **safety
floors, not tuning knobs**, and retrieval refuses to run with them below their minimum:

| Setting | Floor |
|---|---|
| `MinTopRerankScore` | 0.50 |
| `MinSupportingChunks` | 1 |

Retrieval throws on an invalid configuration rather than answering with a gate that has been configured
into answering from whatever it found.

### Simulating retrieval

`POST /api/v1/platform/knowledge/retrieval/simulate` (PlatformSuperAdmin) runs the real pipeline for a
question, optionally as a given tenant, bypassing the result cache, and returns every stage's
diagnostics. Each call is audited - who and which tenant, **not** the query text.

---

## 3b. Audit log and reports

`GET /api/v1/audit-logs` (Admin) is the tenant's audit trail; `GET /api/v1/reports/{campaign-performance,
lead-funnel,agent-performance,ai-performance}?days=30` (Admin, SalesManager) are the reports.

**The audit trail records an allow-list, not every change.** `AuditedEntityCatalog` names the entity types
and the specific properties recorded (lead stage/score/assignee, campaign status, conversation mode,
handoff status, customer opt-in state, knowledge-article lifecycle). Phone numbers, names, emails, message
text and article bodies are deliberately never recorded. Extending it is one line, and the default for
anything not listed is that it is not recorded.

**The table is append-only and there is no retention job yet.** The application refuses to modify or delete
a row, and nothing purges old ones, so `AuditLogs` grows without bound. Volume is one small row per
audited change, which is modest, but plan a retention policy before it matters - it will need a deliberate
job that bypasses the append-only guard, not a manual `DELETE` at 2am.

Changes made by a background job, the AI agent or a webhook are recorded with no actor (`PerformedBy` null),
and changes made through an impersonated support session record the platform user in `ImpersonatedBy`.
Recorded IPs are the address the application sees, so they are only meaningful with forwarded headers
configured correctly (section 5).

---

## 4. Wildcard DNS and CORS

Tenants are reached at `acme.<domain>`, `globex.<domain>`, … from **one** frontend deployment behind a
wildcard DNS record, while the API stays on its own origin. Every tenant subdomain is therefore a
cross-origin caller.

```json
"Cors": {
  "AllowedOrigins": [ "https://*.saleautomation.com" ]
}
```

One `*` wildcard segment per entry is supported. **The wildcard does not match the apex** —
`https://*.saleautomation.com` does not allow `https://saleautomation.com`. List the apex separately
if it needs to call the API.

---

## 5. TLS and forwarded headers

Terminate TLS at the proxy and forward `X-Forwarded-For` and `X-Forwarded-Proto`. The API already
calls `UseForwardedHeaders` **before** `UseHttpsRedirection`, which matters more than it looks: without
it the app sees an "insecure" request and redirects to its own local HTTPS port, and the WhatsApp
webhook dead-ends there.

`ForwardedHeadersOptions` uses the default trusted-proxy range (loopback). **If the proxy is not on
loopback**, set `KnownProxies`/`KnownNetworks`, or the forwarded headers are ignored and every per-IP
rate-limit partition collapses onto the proxy's address.

---

## 6. Secrets

`appsettings.json` is tracked by git. **Nothing secret may live in it.** See
`TODO-BEFORE-PRODUCTION.md` for the values currently sitting there in plaintext and needing rotation.

Every setting below is overridable by environment variable — `__` is the section separator:

| Setting | Environment variable |
|---|---|
| `ConnectionStrings:DefaultConnection` | `ConnectionStrings__DefaultConnection` |
| `Jwt:Secret` | `Jwt__Secret` |
| `AiProviders:OpenAI:ApiKey` | `AiProviders__OpenAI__ApiKey` |
| `WhatsApp:AccessToken` | `WhatsApp__AccessToken` |
| `WhatsApp:AppSecret` | `WhatsApp__AppSecret` |
| `WhatsApp:WebhookVerifyToken` | `WhatsApp__WebhookVerifyToken` |
| `Stripe:SecretKey` | `Stripe__SecretKey` |
| `Stripe:WebhookSecret` | `Stripe__WebhookSecret` |

**`Jwt:Secret` must be replaced.** It ships as the literal placeholder
`REPLACE_WITH_A_LONG_RANDOM_SECRET_AT_LEAST_32_CHARACTERS`; until it is changed, anyone who can read
the repository can forge a valid token for any tenant.

### Settings that live in the database instead

`AppSettingCatalog` keys (WhatsApp / AiProviders / Campaigns / Media / Messaging / Ai) are read from
the `AppSettings` **table**, which is layered over the JSON providers so a DB row always wins. They
are edited from the Platform Admin Console's Settings screen and take effect without a restart.

Values marked `IsSecret` are encrypted at rest with ASP.NET Core Data Protection. **The key ring lives
on local disk at `App_Data/keys`.** Two consequences:

- Losing that directory makes every stored secret unreadable — they must be re-entered.
- **Scaling beyond one instance requires a shared key ring** (a shared volume, or repoint
  `PersistKeysToFileSystem` at a real key store). Two instances with separate key rings cannot read
  each other's secrets, and the failure looks like intermittently wrong credentials.

---

## 7. Rate limiting

Three fixed-window buckets (`RateLimitOptions`). Deliberately **not** in `AppSettingCatalog` — a
limiter the limited party can raise is not a limiter, so changing these is a deployment.

```json
"RateLimiting": {
  "Enabled": true,
  "Auth":    { "PermitLimit": 10,  "WindowSeconds": 60 },
  "Webhook": { "PermitLimit": 600, "WindowSeconds": 60 },
  "Api":     { "PermitLimit": 300, "WindowSeconds": 60 },
  "ExemptIpAddresses": []
}
```

| Bucket | Partition | Applies to |
|---|---|---|
| `Auth` | client IP | `/api/v1/auth/*` — the credential-stuffing surface |
| `Webhook` | client IP | `/api/v1/webhooks/*` |
| `Api` | user, falling back to IP | everything else, plus the global backstop |

**Raise `Webhook` before raising the others.** A 429 returned to Meta is a message this platform never
receives — it is a lost message, not a retried one. The default of 600/minute is well above normal
fan-out; a tenant estate large enough to approach it needs this raised.

Rejections log at `Warning` with method, path, remote IP and user. On the `Auth` bucket that line *is*
the brute-force signal — alert on it.

Put a health checker's IP in `ExemptIpAddresses`, or it will eventually trip the global bucket.

---

## 8. Security headers

`SecurityHeaderOptions`. On by default; HSTS is not.

```json
"SecurityHeaders": {
  "Enabled": true,
  "EnableHsts": true,
  "HstsMaxAgeDays": 180,
  "HstsIncludeSubDomains": true,
  "HstsPreload": false
}
```

**Turn `EnableHsts` on only in deployed environments**, which is why it defaults to false. A browser
that receives HSTS from a dev box refuses plain HTTP to that host for the full max-age, and nothing
the application does can take it back. It is additionally suppressed on non-HTTPS requests, so the
loopback hop behind the proxy never emits it.

Leave `HstsPreload` false unless HTTPS is certain on **every** subdomain, forever — preloading asks
browser vendors to hardcode the domain, and removal takes months.

The default Content-Security-Policy permits the inline styles and scripts the `wwwroot` admin pages
and the Hangfire dashboard need. If those ever move off this origin, tighten it to
`default-src 'none'; frame-ancestors 'none'` — a pure JSON API needs nothing else.

---

## 9. Operational endpoints

| Path | Access |
|---|---|
| `/swagger` | **currently exposed in every environment** — the `IsDevelopment()` guard in `Program.cs` is commented out. Re-enable it or put it behind the proxy before going public. |
| `/hangfire` | `HangfireDashboardAuthorizationFilter`; unrestricted in Development only |
| `/hubs/conversations` | SignalR. The proxy must allow WebSocket upgrade, or clients silently fall back to long polling |
| `/media/*` | uploaded campaign media from `App_Data/media` — local disk, so it needs a persistent volume and is **not** multi-instance safe as-is |

---

## 10. Scaling

Single-instance assumptions to resolve before running more than one:

| Concern | Today | Needed |
|---|---|---|
| Data Protection key ring | local `App_Data/keys` | shared volume or key store |
| Media storage | local `App_Data/media` | shared volume or a cloud provider behind `IMediaStorageService` |
| Hangfire | SQL Server storage | already safe — the server-side lock prevents duplicate execution |
| SignalR | in-memory backplane | a backplane (Redis / Azure SignalR) |
| Startup seeding | idempotent, converges | safe to run concurrently |

---

## 11. Post-deployment checklist

- [ ] `Jwt:Secret` replaced with 32+ random characters
- [ ] Every secret in section 6 supplied by environment variable or a secret store, and any value ever committed **rotated**
- [ ] `Cors:AllowedOrigins` lists the real tenant wildcard (and the apex if used)
- [ ] `SecurityHeaders:EnableHsts` is `true`
- [ ] Startup log checked for the vector-store line — native or fallback, and whether that was expected
- [ ] Startup log checked for full-text availability
- [ ] `Reranker:Cohere:ApiKey` supplied, or `FusionOnly` mode accepted (check retrieval diagnostics)
- [ ] Recent rows in `KnowledgeIngestionJobs` checked for `Failed` / `AwaitingApproval` / `Rejected`
- [ ] Health checker IP added to `RateLimiting:ExemptIpAddresses`
- [ ] Swagger closed, or accepted as public
- [ ] Meta webhook registered against `https://api.<domain>/api/v1/webhooks/whatsapp`
- [ ] Stripe webhook registered and `Stripe:WebhookSecret` set, or billing left deliberately off
- [ ] `App_Data/keys` and `App_Data/media` on persistent storage and in the backup set
- [ ] Alerting on `Warning`-level rate-limit rejections against `/api/v1/auth/*`
