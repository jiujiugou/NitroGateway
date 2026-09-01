---
name: acceptance-review
description: Execute acceptance checks from a supplied document, delegate bounded verification subtasks when useful, and produce an evidence-backed review. Use for feature test retrospectives or acceptance documents.
---

# Acceptance Review

Read the supplied acceptance document and the repository's `notes/AcceptanceCriteria/REMADE.md` before acting. Treat the supplied document as the task specification and `REMADE.md` as the governing acceptance procedure; do not duplicate or redefine its rules here.

Extract the checks, commands, expected results, scope, and prerequisites. Group independent checks and delegate them to subtasks when available. Give each subtask only its check group, allowed files/commands, and this instruction: execute checks, do not modify files, and return commands, actual output, expected result, and status.

Collect results, resolve overlaps, and produce the report required by `REMADE.md`. Preserve real outputs and distinguish unavailable prerequisites from failed checks. Do not change production code, tests, configuration, notes, or the acceptance document unless explicitly asked. Do not execute destructive or external actions without confirmation.

Invoke with:

`$acceptance-review <acceptance-document-path>`
