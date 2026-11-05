---
name: standup
description: Daily standup report for the Agentic team - agent health, work finished and in progress, blockers, and spend over the last 24 hours. Use when the user asks for a standup, a status report or "what happened today".
version: 1.1.0
---

# Daily standup report

Use the `agentic_api` tool (GET only - nothing needs approval):

1. `GET /api/agents` - every agent with its state, balance and queue.
2. `GET /api/operations` - what is running, waiting or held, failure rates, model capacity.
3. `GET /api/tasks?status=Completed` and `GET /api/tasks?status=Failed` - recent outcomes.
4. `GET /api/finance` - spend per agent.

Write the report in Markdown with these sections:

- **Team health** - agents alive / dead / paused; call out any agent that is Dead, Unhealthy or low on money with ==highlight==.
- **Finished** - the most important completed tasks (one line each).
- **In progress** - what is running now and what is queued.
- **Blocked** - failed tasks, held tasks and open approvals, with the reason.
- **Spend** - total and the top three spenders.

Keep it under 40 lines. End with one recommended next step.
