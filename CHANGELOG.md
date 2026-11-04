# Changelog

## 1.1.0

The window release. Same rewriter, same loopback-only guarantees, but now
configurable without hand-editing JSON.

### Added

- **Settings window.** A 980×660 dark window, five pages: 概览 (live counters,
  start/stop/restart, last-call summary), 上游 (relay URL, port, run mode,
  watched processes, autostart, three diagnostics buttons), Agent (detect and
  repoint ZCode / Claude Code, with backup and restore), 日志 (live tail),
  关于 (version, paths, scope).
- **First-run wizard.** Three steps on a fresh install: upstream address, when
  Mimas should run, which agents to point at it. Existing `mimas.config.json`
  files are detected and the wizard is skipped, so upgrades are silent.
- **Live per-call logging.** Every request emits one line —
  `POST /v1/messages  200  1841ms  in=71.3KB  hoisted=1` — with status, latency,
  request size, and the hoist count. Rewrites also log their byte delta.
  The GUI tails it with colour coding, auto-scroll and pause-on-scroll.
- **Diagnostics.** 测试上游 does a real TCP connect plus TLS handshake (cert
  deliberately not validated, matching the proxy's own behaviour); 测试本地端口
  checks the listener; a chain test POSTs through the whole path.
- **Agent integration.** `AgentIntegration` rewrites only the `baseUrl` string
  values in `%USERPROFILE%\.zcode\v2\provider_config.json`, preserving every
  other byte, and writes a one-time `.bak-before-mimas` backup. Claude Code is
  handled through `ANTHROPIC_BASE_URL` in `HKCU\Environment`.
- **Flags.** `--ui` opens the window at startup, `--no-tray` runs window-only,
  `--no-wizard` suppresses first-run setup. Starting a second copy brings the
  existing window forward instead of failing.

### Fixed

- **Restart did nothing.** v1.0.0 created one `HttpListener` at construction
  and reused it for the process lifetime. `Stop()` closes it, and a closed
  `HttpListener` throws on the next `Start()`, so "restart" silently no-opped
  and the proxy stayed down until the process was killed. `Start()` now builds
  a fresh listener every time, and the restart path waits 120 ms for http.sys
  to release the port. Covered by `scripts\test-proxy.cmd`:
  `ten start/stop cycles all bind (10/10)`.
- **Per-request log writes blocked the proxy.** `File.AppendAllText` per request
  is now a bounded queue drained by a background thread, batched per wake.
- **`LogHub` grew without limit** in long sessions. It is now a 2000-entry ring
  buffer.

### Changed

- Log lines now carry a `LogKind` (info / start / rewrite / error / request) so
  the UI can colour them and filter ordinary calls out.
- `AppConfig` gained `setupCompleted`; its absence in a v1.0.0 file is what
  marks an install as an upgrade rather than a fresh install.

### Unchanged

- Still loopback-only, still no key handling, still no response rewriting, still
  no outbound connections except the configured upstream.
- Still builds with the `csc.exe` that ships with Windows — no SDK, no NuGet,
  no admin.

## 1.0.0

- Hoist `image` / `document` blocks out of `tool_result.content` into sibling
  content blocks after the last `tool_result`.
- Tray-only process, `mimas.config.json`, `always` / `watch` run modes.
