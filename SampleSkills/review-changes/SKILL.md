---
name: review-changes
description: Reviews the uncommitted changes in a Git repository on this computer for bugs, security problems and missing tests. Use when the user asks to review their changes, their diff or their working copy.
version: 1.0.0
allowed-tools: run_command, read_file, grep
---

# Review uncommitted changes

The user names a repository folder (or it is the first workspace folder).

1. Run `git -C "<folder>" status --short` and `git -C "<folder>" diff --stat` with `run_command`.
2. Run `git -C "<folder>" diff` (add `--cached` too if files are staged). For big diffs, review file by file.
3. Read the surrounding code with `read_file` when a change cannot be judged from the diff alone; use `grep` to find callers.
4. Report, most important first:
   - **Bugs** - wrong logic, null/empty cases, off-by-one, broken error handling.
   - **Security** - secrets in code, injection, unsafe file or process use.
   - **Tests** - behaviour that changed without a test.
   - **Small things** - only if they matter.

For each finding give `file:line`, what is wrong and the fix. If everything looks fine, say so in one line. Do not change any files unless the user asks.
