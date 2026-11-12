# <ID> <Name> — <status>
**Boundary:** what is inside / outside (one line each).
**Context pack** (read only these, ≤ ~1,500 lines): card · own `Application/<M>/*Contracts.cs` · dependencies' **contracts only** · own `Infrastructure/<M>/` + its EF config · one pattern from [_PATTERNS.md](_PATTERNS.md).
**Endpoints:** METHOD path — permission.
**Tests:** `dotnet test backend/tests/Mechware.UnitTests --filter "FullyQualifiedName~<ns>"` (+ IntegrationTests). Check `Skipped: 0`.
**Depends on:** … **Used by:** …
Paths in backticks must exist (`python scripts/check_cards.py` checks them in CI); write files a future unit will create in plain text, without backticks.
## Units
| Unit | Goal | Exit test | Model |
|---|---|---|---|
## Open
## HANDOFF (≤10 lines, last unit)
date · unit · PR · what changed · what's next · surprises
