# Tasks

## 1. Spec and docs

- [x] 1.1 Change the bound in the two scenarios of `blocked-programs` from 2 seconds to 5 seconds (spec delta).
- [x] 1.2 Check the code comments, `README.md`, `CONTEXT.md`, `GLOSSARY.md`, the web admin texts and the tests for the 2 second bound of a Blocked Start. None mention it. No change needed.
- [x] 1.3 Check the claim of the hard rule in `src` and in the archived design `add-blocked-programs`. Record it in `design.md` (D2).

## 2. Check and archive

- [x] 2.1 `openspec validate relax-blocked-start-bound --strict` passes.
- [x] 2.2 Archive the change. `openspec/specs/blocked-programs/spec.md` shows 5 seconds.
- [x] 2.3 `openspec validate --all --strict` passes.
