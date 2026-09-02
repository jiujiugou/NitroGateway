---
name: opcua-production-workflow
description: Execute the versioned OPC-UA production workflow defined by a repository work item.
---

# OPC-UA production workflow

Use this skill only for an OPC-UA work item that has a task contract under `docs/work-items/`.

## Execution rules

1. Read the work item's `plan.yaml`, `context.md`, and the selected task contract before acting.
2. Check every dependency in `plan.yaml`; stop if a dependency is not completed.
3. Respect the task's `write_scope`. Analysis and acceptance tasks are read-only unless their contract explicitly says otherwise.
4. Write the required result artifact before reporting completion.
5. Do not silently expand the task scope. Record blockers and out-of-scope findings in the result artifact.
6. For implementation tasks, follow the repository ADR, AC, and worklog rules in `AGENTS.md`.
7. For acceptance tasks, read `notes/AcceptanceCriteria/REMADE.md` and return real commands, output, expected result, and PASS/FAIL without changing files.

## Result contract

Every task must report:

- task id and status
- inputs inspected
- findings or changes
- evidence and commands
- unresolved blockers
- next task(s) that may start
