# Phase 6 — Implementation Backlog

**Companion to:** `PHASE6-AI-SUPPORT-RAG-ARCHITECTURE.md`
**उद्देश्य:** उस design को किस क्रम में बनाना है, और हर चरण के बाद क्या काम करने लगेगा।

> इस backlog का मूल सिद्धांत: **हर stage के अंत में कुछ न कुछ चालू और जाँचने योग्य हो।**
> "सब कुछ बनाकर आख़िर में जोड़ेंगे" इस तरह के system में सबसे महँगी गलती है, क्योंकि retrieval
> quality का पता तभी चलता है जब असली content पर असली queries चलें।

---

## Stage 0 — नींव (foundation)

**लक्ष्य:** Tenant isolation का नया model और schema तैयार, पर व्यवहार में कोई बदलाव नहीं।

| # | कार्य | Files | निर्भरता |
|---|---|---|---|
| 0.1 | `ITenantScopedOrGlobal` interface | `Domain/Common/` | — |
| 0.2 | `ApplicationDbContext` में नया query filter + startup assertion (कोई entity दोनों interface न implement करे) | `Infrastructure/Persistence/` | 0.1 |
| 0.3 | `TenantStampingSaveChangesInterceptor` — NULL TenantId write सिर्फ़ SuperAdmin | `Infrastructure/Persistence/` | 0.1 |
| 0.4 | नए enums (§E.3) | `Domain/Enums/` | — |
| 0.5 | `KnowledgeAuthority` constants (§E.4) | `Domain/Constants/` | 0.4 |
| 0.6 | `SupportRagOptions` + validation floors (§K.7) | `Application/Common/Options/` | — |
| 0.7 | Migration M1–M5: article columns, constraints, versions table, backfill | `Infrastructure/Migrations/` | 0.1–0.5 |
| 0.8 | Architecture test: support path में `IgnoreQueryFilters()` नहीं (§P.2) | `tests/` | — |

**Exit criteria:** मौजूदा सारे tests pass; `KnowledgeBaseArticle` GLOBAL scope रख सकता है; backfill script staging पर चला और verify हुआ।

---

## Stage 1 — Vector storage

**लक्ष्य:** In-memory cosine से native `VECTOR` पर migration, बिना downtime।

| # | कार्य | निर्भरता |
|---|---|---|
| 1.1 | SQL Server `VECTOR` capability check (startup, §Y EC-22) | 0.7 |
| 1.2 | `IVectorStore` + `SqlServerVectorStore` (§J.7) | 1.1 |
| 1.3 | EF value converter `ReadOnlyMemory<float>` ↔ `VECTOR(1536)` | 1.1 |
| 1.4 | Migration M6–M7: नया column + dual-write | 1.3 |
| 1.5 | पूरा re-index → VECTOR column भरो → verification | 1.4 |
| 1.6 | Migration M8–M11: read switch, पुराना column drop, vector + FTS index | 1.5 |

**Exit criteria:** मौजूदा sales RAG (`IKnowledgeBaseService`) native vectors पर चल रहा है, वही results, बेहतर latency। यह stage support agent से पहले है क्योंकि यह पहले से चल रही चीज़ को बेहतर करता है — और अगर यहाँ कुछ टूटता है, तो वह एक जानी-पहचानी सतह पर टूटता है।

---

## Stage 2 — Ingestion pipeline

| # | कार्य | निर्भरता |
|---|---|---|
| 2.1 | `KnowledgeIngestionJobs` table + entity (M12 का हिस्सा) | 0.7 |
| 2.2 | `DocumentTextExtractor` — md/txt/html/docx/pdf (§G.3) | — |
| 2.3 | Cleaning + sanitization + injection detection (§G.4) | 2.2 |
| 2.4 | Metadata enrichment (§G.5) | 2.3 |
| 2.5 | `StructureAwareChunker` (§H) — **यह सबसे ज़्यादा unit tests वाला component है** | 2.4 |
| 2.6 | Batched embedding + retry + resumability (§I.4) | 2.5 |
| 2.7 | Transactional staging swap (§G.6) | 2.6, 1.2 |
| 2.8 | Post-index verification: duplicate, conflict, smoke retrieval (§G.7) | 2.7 |
| 2.9 | `KnowledgeMetadataSyncJob` (§G.8) | 2.7 |
| 2.10 | Chunk quality metrics (§H.8) | 2.5 |

**Exit criteria:** एक .docx और एक .pdf article upload होकर सही chunks + vectors बनाते हैं; atomic groups सही बनते हैं; injection corpus का T-1..T-10 block/flag होता है।

---

## Stage 3 — Retrieval

| # | कार्य | निर्भरता |
|---|---|---|
| 3.1 | Query normalization + PII masking + expansion (§K.2) | — |
| 3.2 | Hard metadata filter CTE (§K.3) | 1.2 |
| 3.3 | Vector leg + keyword leg (§K.4) | 3.2 |
| 3.4 | RRF fusion + multi-query (§K.5) | 3.3 |
| 3.5 | Metadata boosting (§K.6) | 3.4 |
| 3.6 | `CrossEncoderReranker` + fallback mode (§L) | 3.5 |
| 3.7 | Atomic group expansion (§M.3) | 3.6 |
| 3.8 | `IKnowledgeRetrievalService` + diagnostics (§K.8) | 3.7 |
| 3.9 | Caching: embedding cache, tenant-keyed result cache (§P.3) | 3.8 |
| 3.10 | `POST /platform/knowledge/retrieval/simulate` (§V.3) | 3.8 |

**Exit criteria:** `/retrieval/simulate` हर stage का breakdown लौटाता है; P95 < 400 ms; AC-12 से AC-16 pass।

> **यहाँ रुककर content team को लगाएँ।** Simulate endpoint उन्हें पहला GLOBAL article set लिखने और
> जाँचने देता है — और OD-7 के अनुसार वह critical path है। Stage 4 के साथ समानांतर चले।

---

## Stage 4 — Support ticket module

| # | कार्य | निर्भरता |
|---|---|---|
| 4.1 | `Support*` entities + migration M13 (§W.8–W.10) | 0.7 |
| 4.2 | `ISupportTicketService` + tenant APIs (§V.1) | 4.1 |
| 4.3 | Ticket number generation, SLA calculation | 4.1 |
| 4.4 | SignalR events (§V.6) | 4.2 |
| 4.5 | Platform staff APIs (§V.4) | 4.1 |
| 4.6 | Angular: tenant ticket UI | 4.2, 4.4 |
| 4.7 | Angular: platform escalation queue UI | 4.5 |

**Exit criteria:** Ticket raise → human agent reply का पूरा flow बिना AI के काम करता है। **यह जानबूझकर है** — अगर AI बाद में बंद करनी पड़े, तो support चलता रहे।

---

## Stage 5 — Tools

| # | कार्य | निर्भरता |
|---|---|---|
| 5.1 | `ISupportTool` + `ISupportToolRegistry` (§O.2) | — |
| 5.2 | तीन-परत authorization (§O.4) | 5.1 |
| 5.3 | Redaction layer — allow-list based (§O.6) | 5.1 |
| 5.4 | 11 Read tools (§O.3) | 5.1–5.3 |
| 5.5 | 3 SensitiveRead tools | 5.4 |
| 5.6 | 7 Mutating tools — **सिर्फ़ human-triggered path** | 5.4 |
| 5.7 | `SupportAgentToolCall` audit (§T.4) | 5.2 |
| 5.8 | Intent → tool mapping (§O.8) | 5.4 |

**Exit criteria:** AC-22 से AC-26 pass; हर tool का authorization unit-tested; mutating tools model definitions में कहीं नहीं दिखते।

---

## Stage 6 — Agent orchestrator

| # | कार्य | निर्भरता |
|---|---|---|
| 6.1 | Intent + module + risk classifier (§K.2) | 3.8 |
| 6.2 | Context assembly + token budget (§M) | 3.8 |
| 6.3 | System prompt + structured output schema (§N.2, §N.3) | — |
| 6.4 | Gates 0–5 (§S.1) | 6.1, 3.8, 5.2 |
| 6.5 | LLM call + tool loop (§O.7) | 6.2, 6.3, 5.1 |
| 6.6 | Grounding verification GV-1..GV-9 (§N.5) | 6.5 |
| 6.7 | Gates 6–7 + decision (§S.1) | 6.6 |
| 6.8 | `SupportAgentRun` + `SupportAgentRunEvidence` audit (§T.2, §T.3) | 6.7 |
| 6.9 | Prompt caching (§N.6) | 6.3 |
| 6.10 | Ticket-level distributed lock (§Y EC-26) | 6.4 |

**Exit criteria:** X.1 का उदाहरण end-to-end चलता है; AC-17 से AC-21 pass।

---

## Stage 7 — Escalation

| # | कार्य | निर्भरता |
|---|---|---|
| 7.1 | `EscalationPacket` assembly (§S.6) | 6.8 |
| 7.2 | `ISupportEscalationService` + `SupportEscalation` (§T.5) | 7.1 |
| 7.3 | Frustration detector (§S.5) | 6.1 |
| 7.4 | Tenant-facing escalation messages (§S.7) | 7.2 |
| 7.5 | Notifications (SignalR + platform notification) | 7.2 |
| 7.6 | Angular: escalation packet viewer | 7.2, 4.7 |
| 7.7 | Human `resume-ai` path (§Y EC-31) | 7.2 |

**Exit criteria:** AC-35 से AC-39 pass; X.2 का उदाहरण end-to-end चलता है।

---

## Stage 8 — Conflict resolution

| # | कार्य | निर्भरता |
|---|---|---|
| 8.1 | `KnowledgeConflict` entity (§R.6) | 0.7 |
| 8.2 | `KnowledgeConflictResolver` — pure function (§R.3) | — |
| 8.3 | Retrieval-time detection (§R.2) | 3.8, 8.2 |
| 8.4 | `KnowledgeConflictScanJob` (§R.2) | 8.1, 8.2 |
| 8.5 | Conflict APIs + Angular resolution UI (§V.3) | 8.1 |

**Exit criteria:** AC-32 से AC-34 pass; X.3 के दोनों परिदृश्य सही व्यवहार करते हैं।

---

## Stage 9 — Knowledge administration

| # | कार्य | निर्भरता |
|---|---|---|
| 9.1 | 6-state lifecycle service + transitions (§Q.1) | 0.7 |
| 9.2 | Separation of duties enforcement (§U.2, BR-7) | 9.1 |
| 9.3 | Versioning + rollback (§Q.2–Q.4) | 9.1 |
| 9.4 | Duplicate detection तीनों स्तर (§U.3) | 2.8 |
| 9.5 | Lifecycle jobs — expiry, review, archive, integrity (§Q.5) | 9.1 |
| 9.6 | `KnowledgeRetrievalLog` + `FailedRetrievalDigestJob` (§U.5) | 3.8 |
| 9.7 | Article usage analytics (§U.6) | 6.8 |
| 9.8 | `KnowledgeFeedback` + तीनों feedback स्रोत (§U.7) | 7.2 |
| 9.9 | SuperAdmin knowledge APIs (§V.3) | 9.1–9.8 |
| 9.10 | Angular: knowledge admin console | 9.9 |

**Exit criteria:** AC-1 से AC-7 pass; साप्ताहिक digest वास्तविक data पर उपयोगी output देता है।

---

## Stage 10 — Observability और quality

| # | कार्य | निर्भरता |
|---|---|---|
| 10.1 | Metrics emission (§T.6) | 6.8 |
| 10.2 | SuperAdmin dashboard — 10 panels (§T.6) | 10.1 |
| 10.3 | Alerts (§T.6) | 10.1 |
| 10.4 | Retention jobs (§T.7) | 6.8 |
| 10.5 | Regression set — 150 curated जोड़े (§U.8) | 6.7 |
| 10.6 | Injection corpus test suite (§P.5) | 6.7 |
| 10.7 | Isolation test suite — 5,000 queries (§Z.6) | 3.8 |
| 10.8 | Shadow mode infrastructure (§U.8) | 6.7 |
| 10.9 | Sampled review workflow | 9.8 |

**Exit criteria:** AC-44 से AC-55 pass।

---

## समानांतर track — Content

यह engineering stages के साथ-साथ चलता है और **इसकी अपनी critical path है**।

| # | कार्य | कब |
|---|---|---|
| C.1 | Article authoring template (§E.5) team को सिखाना | Stage 0 के दौरान |
| C.2 | मौजूदा `FaqService` की FAQs का audit + migration plan | Stage 1 |
| C.3 | पहले 30 GLOBAL articles — सबसे ज़्यादा पूछे जाने वाले विषय | Stage 2–3 |
| C.4 | Policy articles: refund, billing, WhatsApp, AI credits, lead discovery | Stage 3 |
| C.5 | Troubleshooting guides — top 20 tickets से | Stage 4 |
| C.6 | `/retrieval/simulate` से हर article की जाँच | Stage 3 के बाद लगातार |
| C.7 | Hindi keywords हर article में जोड़ना (§J.6) | Stage 3–4 |
| C.8 | Regression set के 150 जोड़े लिखना | Stage 6 |

> **चेतावनी:** अगर Stage 6 तक 50+ अच्छी GLOBAL articles तैयार नहीं हैं, तो AI agent का पहला
> रिलीज़ लगभग 100% escalation दर दिखाएगा — और वह एक content समस्या होगी, engineering समस्या नहीं।
> इसे पहले से रोकना Stage 3 पर content team को लगाने से ही संभव है।

---

## Rollout योजना

```
1. INTERNAL       — सिर्फ़ staff tenants, सारे तंत्र चालू, हर जवाब human-reviewed
2. SHADOW         — असली tenants, AI चलती है पर कुछ भेजती नहीं; निर्णय रिकॉर्ड होते हैं
                    → containment अनुमान, escalation mix, quality sample
3. PILOT          — 10 चुने हुए tenants, सिर्फ़ Low-risk intents
4. GRADUAL        — 25% → 50% → 100% tenants, हर चरण पर एक हफ़्ता निगरानी
5. INTENT EXPAND  — Medium-risk intents जोड़ो (High/Prohibited कभी नहीं)
```

**हर चरण पर rollback trigger:**
- Grounding failure > 2%
- कोई भी cross-tenant incident
- Human-reviewed wrong-answer > 5%
- Tenant feedback 👎 > 25%

**Kill switch:** एक per-tenant और एक global flag जो AI handling बंद कर दे और सारे tickets सीधे human
queue में भेजे। यह Stage 4 में ही बन जाना चाहिए, Stage 6 में नहीं — क्योंकि जिस दिन उसकी ज़रूरत पड़ेगी,
उस दिन उसे बनाने का समय नहीं होगा।
