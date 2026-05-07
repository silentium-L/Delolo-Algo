---
name: Token Budget Discipline
description: Operating rules to keep token usage and read capacity low when iterating on the Bot Template — work surgically, not exploratorily
type: feedback
scope: Bot Template / cTrader strategies
---

# Token & Read-Capacity Discipline

The Bot Template is ~1100 lines, the original Clover is ~2500 lines. A naive workflow burns the conversation budget before any real work happens. These rules keep iterations cheap.

## The anti-patterns to avoid

- **Reading the whole file** when only one section matters. The template has clear `═══` banner sections; each section is 30–80 lines. Read the section, not the file.
- **Re-reading after editing.** The Edit tool already shows you the surrounding context. Don't read the file back to "verify" — verify by building.
- **Searching with `Grep` blindly** to "find where X is". Use [bot-map.md](bot-map.md) — the section→line ranges are documented. Read by offset.
- **Spawning subagents** for changes you can do directly. Each spawn re-reads the file from scratch and burns its own context window. Subagents are for parallel research, not for "be thorough".
- **Dumping full file contents in chat** when explaining a change. Quote the 5 relevant lines, not the function.
- **Batching edits then reading the whole file** to confirm. Trust the Edit tool's diff output; build to verify.

## Required habits

### 1. Use the bot-map first

Before any read, check [bot-map.md](bot-map.md) for the line-range of the section you need. Then read with `offset:` and `limit:` — never read 2000 lines when you need 50.

### 2. Read in section-sized chunks

Banner sections in `Bot Template.cs` are bounded by `═══════…` lines. Typical sizes:
- Parameters block: ~280 lines (read all if changing param surface)
- One management routine (e.g. `ManageChandelier`): ~50 lines
- Attribution block: ~150 lines (rarely needs re-reading)

Read **one section** per Read call. If you find yourself reading three, you're probably doing too much in one turn.

### 3. Edit, don't rewrite

`Write` overwrites the entire file — only justified for new files. Use `Edit` for surgical replacements. The Edit tool only sends the diff, not the whole file.

### 4. Build > Read for verification

`dotnet build` runs in <5 seconds, costs zero context, and produces ground truth. Reading the file to "make sure the change applied cleanly" is twice as expensive and less reliable.

### 5. Don't narrate mid-thought

User-visible commentary is tokens too. Lead with the change, end with the result. No "Let me think about…", no "First I'll read X, then I'll modify Y, then I'll…". State results, not plans.

### 6. Drop dead context

When the auto-memory grows or `MEMORY.md` index inflates, prune entries that are stale. A stale memory loaded into every conversation is a recurring tax.

### 7. Reuse, don't re-derive

The persona, the bot-map, the canonical defaults table — they exist so we don't re-explain them every turn. If a question is answered by an existing file, point at it, don't reproduce it.

## Concrete budget targets

| Operation | Target tokens (round trip) |
|---|---|
| Add one parameter + wire it up | < 3k |
| Implement a fresh strategy in `TryEnterStrategy` | < 8k |
| Tune existing parameters | < 1.5k |
| Reading the bot for orientation | < 2k (use bot-map, don't tour) |
| Bug hunt in trade management | < 6k (one section + build) |

If a routine task is doubling the target, stop and look at what's wasting budget.

## When to spawn a subagent

Only when **all** of these hold:
1. The work is genuinely independent of what you have in context.
2. You'd otherwise need to load >5k tokens of code to do it inline.
3. You can write a self-contained prompt without re-explaining the project.

Otherwise, do it inline. Cold subagent starts cost more than the time they "save".

## When to read the whole file

Almost never. Acceptable cases:
- First-ever orientation pass (already done — see bot-map.md).
- Major refactor touching every section (rare — the template is intentionally stable).
- Audit for a specific cross-cutting concern (e.g. "find every place we use `Last(0)`") — but that's a `Grep` job, not a Read.
