# NOW (keep ≤ 30 lines; update at the end of every unit)

**Last units:** C6.1 adapter ✅ · C6.2 indexer + rebuild ✅ · C6.3 search API ✅ (2026-11-12). Card: [modules/C6.md](modules/C6.md).
Branch `claude/c6-search-and-module-plan` (pushed; PR not opened yet — open it on GitHub).

C6 module review done; its 8 findings fixed (C6.3b).
C6.4 ✅ · C6.5 accuracy 100 % / k6 p95 17.8 ms ✅ → **module C6 complete** (C6.2b moved to E5). A6.5 PR template ✅.
**Next:** A6.1 `check-cards` + A6.2 `test-module` scripts, then A6.3 layering ratchet, A6.4 swagger/types drift (Sonnet sub-agents).

**After that:** C7 PLP/PDP + H1 home (frontend).

**How work runs:** main session Opus plans and verifies; routine code → Sonnet sub-agent; money/security → Opus sub-agent (memory `feedback-subagent-delegation`).

**Before starting:** Docker Desktop running → `docker compose -f infra/docker/docker-compose.yml up -d postgres redis meilisearch`.

**Owner actions (start now):** VAT consultant · lawyer · SSLCommerz/bKash merchant · SMS sender ID + email domain · pen-test booking · 5 real buyers + real product data. Open decisions: [MODULES.md](MODULES.md) §5.
