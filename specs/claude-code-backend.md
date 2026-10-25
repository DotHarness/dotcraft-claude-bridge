# Claude Code backend

| Field | Value |
|---|---|
| Version | 0.1.0 |
| Status | Living |
| Date | 2026-10-23 |

## Ownership

The `claude-code` plugin contributes the `claude-code` model backend through
DotCraft's model backend extension point, and a Desktop module that owns the
backend's subscription UI. Claude Code owns model transport and the local Claude
subscription identity. DotCraft owns its instructions, conversation state, tool
declarations, approval and execution.

Each request supplies the complete DotCraft instructions, conversation and tool
schemas. The CLI keeps its own Claude Code system prompt and receives DotCraft's
instructions unchanged through `--append-system-prompt-file`; the bridge does not
remove or rewrite them. Every conversation run starts in one stable, private work
folder under the plugin's data root, outside any repository, so the working
directory in Claude Code's prompt stays the same and its sessions are kept apart
from the user's own projects. Requests with tools keep Claude Code's own
`WebSearch`, which the CLI runs inside the turn and the bridge does not surface as a
host tool call; every other built-in tool is off, and DotCraft's tools come from an
explicit MCP tool catalog. Local CLI settings, CLAUDE.md files, git status, memory,
tool search and automatic compaction are disabled.

## Sampling and tool continuation

1. A fresh CLI run receives prior conversation as role-tagged JSON, followed by the
   current user input. Images are supplied as image blocks.
2. A loopback Streamable HTTP MCP endpoint exposes the declared function tools.
   Tool names map deterministically to host names, and JSON schemas are preserved.
   Each request authenticates with a run-specific bearer token.
3. CLI stream events become provider-neutral text, reasoning, function calls,
   usage and finish reasons. A complete tool-use message ends the current sampling
   response while the CLI and pending MCP calls wait for host results.
4. MCP calls are correlated by `_meta["claudecode/toolUseId"]`. DotCraft approves
   and executes the tools. The next sampling supplies results by call ID, including
   parallel calls. The bridge returns text and image content to the pending MCP
   calls and marks failed executions with `isError`.
5. After a final CLI result, a conversation turn keeps its CLI run for the next
   turn. The next turn sends only the new user input, so the CLI keeps its own
   conversation and prompt cache instead of receiving the whole history again.

Retained runs are scoped to provider, thread, context window and request kind.
Continuation requires unchanged instructions, model, tools and sampling settings,
with history extended by exactly what the run expects: the pending tool results
inside a turn, or only user input after a final result. Reasoning content is not
compared. A changed request or history starts a fresh run from the supplied
conversation. Compaction and other non-turn requests end their run at the final
result, and requests without conversation identity use isolated runs. DotCraft's
conversation remains authoritative through compaction, forks and rollback.

## Saved sessions

Conversation runs keep their Claude Code session on disk. An idle run stays alive
for `idleTimeoutSeconds` (one hour by default, Claude's prompt cache lifetime), and
at most six idle runs are kept. A run that leaves after a final result, by idle
expiry or because more runs are idle, is shelved: its process ends and its session
is kept. The next turn of that conversation starts Claude Code with `--resume` on
the session and sends only the new user input, so the conversation reaches
Anthropic as the cached prefix it was before. The run sends `initialize` first;
Claude Code answers only after it loads the session, and a session it cannot load
falls back to a fresh run with the whole conversation in the same request.

A forked conversation with no run of its own branches from the conversation it was
forked from. When a live or shelved run of that conversation is idle with the same
instructions, model, tools and sampling settings, and the fork's history extends it
by user input only, the fork starts Claude Code with `--resume` on that session and
`--fork-session` and sends only the new input. The fork gets its own session and
reuses the source's cached prefix, and the source session is left unchanged. Any
other fork starts a fresh run.

Shelved sessions last until the prompt cache lifetime has passed since the run went
idle, and at most 64 are kept. Every other end of a run, including tool calls left
unanswered, a changed conversation, an account change, sign-out and plugin
shutdown, deletes the run's session files. At startup the bridge removes session
files in its work folder's project that are older than the cache lifetime.

## Request options

DotCraft's resolved reasoning effort maps to `--effort` (`low`, `medium`, `high`,
`xhigh`, `max`); the CLI lowers a level that the selected model does not support.
Disabled reasoning disables CLI thinking. A required tool choice and a JSON
response format become instructions, because the CLI has no equivalent request
options that keep DotCraft's tool catalog intact.

## Account and model discovery

The backend uses the Claude Code account signed in on the machine through one `cli`
authentication method, and Claude Code owns the credentials. Status comes from
`claude auth status --json` and accepts a Claude subscription identity. Sign-in
completes at once when the CLI is already signed in. Otherwise it runs
`claude auth login --claudeai` with the CLI's own browser launch disabled, sends the
link the CLI prints as an `auth_url` event, and writes each `manual_code` answer to
the CLI until it accepts one; a rejected code is reported as an `info` event and
asked for again. Sign-out stops retained runs and runs `claude auth logout`. When the
CLI is not signed in, or rejects its stored credentials, model discovery and
sampling fail with `ClaudeCode.SubscriptionLoginRequired`.

CLI runs never use bare mode, because bare mode ignores subscription credentials.
An inherited `CLAUDE_CODE_SIMPLE` is removed for the same reason. Isolation instead
comes from empty setting sources, a built-in tool set limited to `WebSearch`, a
strict MCP configuration and disabled CLAUDE.md loading.

Model discovery uses the stream-json initialize control request without inference.
Usage comes from the CLI's `get_usage` control request: the five-hour and seven-day
subscription windows become the provider's primary and secondary limits, with the
subscription type as the plan. Executable selection and idle timeout follow the
[plugin settings schema](../src/DotCraft.ClaudeCode/settings.schema.json).

## Usage limits

The CLI's `rate_limit_event` messages update one limit state per backend. A rejected
request ends the turn with `ClaudeCode.UsageLimitReached`. A request that would draw
extra usage ends the turn with `ClaudeCode.ExtraUsageBlocked` and stops its CLI run
unless the `allowExtraUsage` setting is on. Both states refuse further sampling
until the reported reset time, or for five minutes when none is reported, and
appear as the usage snapshot's limit-reached kind (`five_hour`, `seven_day` or
`extra_usage`).

## Desktop module

The Desktop module wraps `composer.status.subscription` and renders its own usage
indicator only when the Composer context's provider uses the `claude-code`
backend; other providers keep the default content. It reads usage through
`provider/auth/usage`, refreshes when a turn completes or fails and every five
minutes, and adds a `composer.before` notice while a limit is used up.

## Run lifetime

Cancellation, abandoned response streams and authentication changes end retained
runs; idle expiry shelves them as described in [Saved sessions](#saved-sessions).
Plugin stopping cancels active work and releases child process
trees, MCP listeners, pending tool calls and temporary run files. Files containing
prompts or authorization data are private to the current operating-system user.
