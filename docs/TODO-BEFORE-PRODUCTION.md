# TODO before production / before sharing this repo

Running notes on things deliberately left as-is for local development speed,
but that need attention before this project is deployed for real, made public,
or shared with anyone outside this machine. Added 2026-09-06.

## 1. Secrets currently sitting in plaintext in `appsettings.json`

`src/Presentation/WhatsAppSalesAutomation.Api/appsettings.json` is tracked by
git and currently contains real, live secrets:

- `AiProviders:OpenAI:ApiKey` - real OpenAI API key (added 2026-09-06, for the
  gpt-5-nano + text-embedding-3-small combo powering AI replies + RAG).
- `WhatsApp:AccessToken` - live Meta WhatsApp Cloud API access token.
- `WhatsApp:AppSecret` - Meta app secret.
- `WhatsApp:WebhookVerifyToken` - webhook verification token.

**Before this repo is pushed to a shared/public remote, or before going to
production:** move all of the above out of `appsettings.json` into one of:

- `dotnet user-secrets` (local dev only - already wired up, `UserSecretsId`
  exists in the Api project's `.csproj`)
- Environment variables (`AiProviders__OpenAI__ApiKey`, `WhatsApp__AccessToken`,
  etc.) for a real deployment
- A real secret store (Azure Key Vault / AWS Secrets Manager / etc.) for
  production

If the repo has already been pushed anywhere with these values in it by the
time this gets fixed, **rotate all of them** (new OpenAI key, new Meta access
token/app secret/webhook token) - assume anything ever committed is
compromised, not just "currently exposed."

## 2. JWT signing secret is still a placeholder

`Jwt:Secret` in `appsettings.json` is still the literal placeholder value
`REPLACE_WITH_A_LONG_RANDOM_SECRET_AT_LEAST_32_CHARACTERS`. This must be
replaced with an actual random secret (32+ chars) before auth is relied on for
anything real - as-is, anyone who reads the repo can forge valid JWTs.

## 3. AI provider is now real (billed) traffic

`AiProviders:Provider` / `EmbeddingProvider` were switched from `Simulated` to
`OpenAI` on 2026-09-06 so the bot actually grounds replies in the Knowledge
Base instead of using the keyword-based `SimulatedAiClient` stand-in. This
means every non-Human-mode inbound message now costs real money (~$0.00016
per message at current gpt-5-nano/text-embedding-3-small pricing - cheap, but
not free). Make sure billing/usage limits are set up at
platform.openai.com before high message volume goes through this.

## 4. Consider a secret-scanning safety net

Once the above is cleaned up, consider adding a pre-commit hook or CI check
(e.g. `gitleaks`, `git-secrets`) so a real key/token can't accidentally get
re-committed the same way in the future.
