# Mimas

A tiny local proxy that stops images inside `tool_result` blocks from being
billed as text.

Named after **Mimas**, a moon of Saturn.

> **Using an AI agent?** Point it at [AI-INSTALL.md](AI-INSTALL.md). That file
> is written as literal instructions for an assistant: verify the bug, download
> and checksum the binary, write the config, repoint the provider, and confirm
> the rewrite is actually in the request path.

---

## The bug

When an agent (ZCode, Claude Code, or anything else speaking the Anthropic
Messages API) reads an image with its `Read` tool, it sends the image in the
canonical Anthropic shape:

```json
{
  "role": "user",
  "content": [
    {
      "type": "tool_result",
      "tool_use_id": "toolu_01...",
      "content": [
        { "type": "image", "source": { "type": "base64", "media_type": "image/png", "data": "..." } }
      ]
    }
  ]
}
```

That is correct and is exactly what Anthropic documents.

Some Anthropic-compatible relays, however, **stringify `tool_result.content`
before forwarding**. The base64 payload then arrives upstream as *text*, so the
model tokenizes it as text instead of decoding it as an image.

Two things break at once:

1. **Cost.** The base64 string is tokenized. A 1.1 MB PNG becomes ~1,000,000
   input tokens instead of ~1,200.
2. **Correctness.** The model never sees the image. Asked "what colour is this
   picture?", it answers nothing at all.

### Measured against a relay with this behaviour

Same image, three positions in the request body:

| Where the image block sits | `input_tokens` | Model sees it? |
| --- | ---: | :---: |
| Top-level user content block | **1,155** | yes |
| Inside `tool_result.content` (array) | **1,047,059** | no |
| Inside `tool_result.content` (stringified) | **1,047,057** | no |

About **900×** more expensive, and blind.

---

## What Mimas does

Mimas sits on `127.0.0.1` between your agent and the relay and rewrites the
request so the image blocks travel as **sibling content blocks** instead of
nested ones.

```
before:  [ tool_result(content:[image]), text ]
after:   [ tool_result(content:"[image attached in the following content block]"), image, text ]
```

The hoisted blocks are spliced in **after the last `tool_result`**, because
Anthropic requires all `tool_result` blocks to be contiguous and leading within
a user message. Putting an image between two `tool_result` blocks returns
`400 tool_use ids were found without tool_result blocks immediately after`.

### Measured effect

| Case | Direct | Through Mimas | Result |
| --- | ---: | ---: | --- |
| 1.1 MB noisy PNG in `tool_result` | 131,333 | 18,143 | 7.2× cheaper |
| 1440×900 UI screenshot in `tool_result` | 8,873 | 1,038 | 8.5× cheaper |
| "what colour is this?" on a red square | *(no answer)* | `Red` | vision restored |
| Multi-turn, image kept in history | 8,918 / turn | 1,083 / turn | 8× cheaper per turn |

The token numbers come from the upstream's own `usage.input_tokens`.
Full method and raw numbers: [docs/VERIFICATION.md](docs/VERIFICATION.md).

### What it deliberately does not do

- No authentication changes, no key handling, no key logging.
- No response rewriting. Responses are streamed back byte for byte, so SSE
  keeps working.
- Text-only requests are forwarded untouched. Bodies without a `tool_result`
  block are never even parsed.
- No telemetry, no outbound connections except to your configured upstream.

---

## Install

Windows 10/11, .NET Framework 4.x (present on every Windows install).
No admin rights needed.

1. Download `Mimas.exe` from [Releases](../../releases) (or build it, see below).
2. Put it in its own folder, e.g. `%USERPROFILE%\Mimas`.
3. Run it. A ringed-planet icon appears in the tray, and on the very first run
   a setup wizard asks three things: your relay address, when Mimas should run,
   and which agents to point at it. You never have to hand-edit JSON.
4. Double-click the tray icon (or right-click → **打开设置…**) to reopen the
   window at any time.

Already had a `mimas.config.json` from v1.0.0? The wizard is skipped — your
existing config is used as-is, and the window opens on the dashboard.

### The window

A 980×660 dark window, five pages down the left sidebar:

| Page | What it's for |
| --- | --- |
| **概览** | Live counters (requests, rewrites, hoisted image blocks, errors), listening address, start/stop/restart, last-call summary |
| **上游** | Relay address, local port, run mode (`always` / `watch`), watched process list, autostart, plus **测试上游** and **测试本地端口** and a full end-to-end chain test |
| **Agent** | Detects ZCode and Claude Code, points their `baseUrl` at Mimas (with a one-time `.bak-before-mimas` backup), and restores them |
| **日志** | Every request as it happens — see below |
| **关于** | Version, config/log paths, what Mimas does and doesn't touch |

### Live per-call log

Every request produces exactly one line, in the **日志** page and in
`mimas.log`:

```
POST /v1/messages  200  1841ms  in=71.3KB out=1.2KB  hoisted=1  claude-sonnet-5-5
POST /v1/messages  200   903ms  in=12.4KB out=0.4KB  claude-sonnet-5-5
POST /v1/messages  401   122ms  in=1.2KB out=0.1KB
```

Method, path, status, latency, request and response size, the model name when
it can be read from the body, and `hoisted=N` when the body was rewritten.
Rewrites additionally log their byte delta
(`hoisted 1 block(s): 71332 -> 71405 bytes`). Rows are colour-coded —
rewrites amber, errors red, lifecycle green. The view auto-scrolls; scrolling up
pauses the tail and shows **已暂停自动滚动 · 滚到底部继续**.

The log is also kept in a 2000-entry in-memory ring so the page is instant, and
written to disk on a background thread so the proxy never blocks on I/O.

### Command line

```
Mimas.exe                 tray only, wizard on first run
Mimas.exe --ui            open the window at startup
Mimas.exe --no-tray       window only, no tray icon
Mimas.exe --no-wizard     never show the first-run wizard
```

Starting a second copy brings the existing window to the front instead of
failing.

### Config file

The GUI writes `mimas.config.json` next to the exe; you can still edit it by
hand and hit **重启代理**.

```json
{
  "upstream": "https://your-relay.example.com",
  "port": 8817,
  "mode": "watch",
  "watchProcesses": ["ZCode"],
  "startWithWindows": true,
  "verbose": false,
  "setupCompleted": true
}
```

| Field | Meaning |
| --- | --- |
| `upstream` | The relay you normally talk to. Mimas forwards here. |
| `port` | Local listen port. Default `8817`. |
| `mode` | `always` = listen continuously. `watch` = only listen while one of `watchProcesses` is running. |
| `watchProcesses` | Process names to watch in `watch` mode. `.exe` suffix optional. |
| `startWithWindows` | Adds/removes an `HKCU\...\Run` entry. Toggle from the GUI or the tray menu. |
| `verbose` | Accepted for compatibility; per-request rewrites are always logged. |
| `setupCompleted` | Set by the wizard. Missing in v1.0.0 configs, which is how an upgrade is told apart from a fresh install. |

### Tray menu

- **打开设置…** — show the window (also double-click the icon)
- **状态** — running / standby, and the local address
- **运行方式** — switch between `always` and `watch`
- **重启代理** — stop and rebind the listener (a fresh `HttpListener` each
  time, so this actually works — see [CHANGELOG](CHANGELOG.md))
- **打开配置文件 / 查看日志**
- **开机自启动**
- **退出**

---

## Security notes

- Mimas binds **only** `127.0.0.1` and `localhost`. Windows `HttpListener`
  rejects requests whose `Host` header is not one of the bound prefixes, so a
  LAN peer gets `400 Bad Request - Invalid Hostname` even if it reaches the
  port. Verified: `http://<lan-ip>:8817` returns 400, `127.0.0.1:8817` returns 200.
- Your API key passes through unmodified and is never written to disk by Mimas.
- Mimas does not verify the upstream's TLS certificate chain
  (`ServerCertificateValidationCallback` returns true) so it works behind
  self-signed relays. **If that matters to you, don't use this tool** — or patch
  `ProxyServer.cs`.

---

## Build from source

No SDK, no NuGet, no admin. Uses the C# compiler bundled with Windows:

```cmd
scripts\build.cmd
```

Output: `bin\Mimas.exe` (~300 KB, single file, no dependencies).

Three offline test suites — no network needed:

```cmd
scripts\test.cmd        rem the rewriter itself (20 assertions)
scripts\test-proxy.cmd  rem listener lifecycle, restart, per-call logging (12)
scripts\test-agent.cmd  rem ZCode config patcher, never touches the real file (12)
```

The UI can be rendered to PNGs without showing a window, for visual review:

```cmd
scripts\render-ui.cmd %TEMP%\mimas-ui
```

It parks each form at `(-32000, -32000)` with `ShowInTaskbar = false` and calls
`Show()` — WinForms will not create child handles (and therefore will not paint
child controls) for a form that was never displayed.

---

## Changelog

See [CHANGELOG.md](CHANGELOG.md).

---

## Limitations

- **Anthropic Messages shape only.** It rewrites `tool_result.content`. OpenAI
  `role: "tool"` payloads are not touched.
- **Fixes the request, not the relay.** If your relay's *token estimation* also
  counts base64 as text (some New API builds do), your bill may still look
  inflated even though the upstream sees the image correctly. Compare the
  upstream-reported `usage.input_tokens` with the relay's own estimate.
- **Not a general proxy.** No load balancing, no retries, no caching, no
  multi-provider routing.
- If the relay ever stops stringifying `tool_result.content`, Mimas becomes a
  no-op pass-through and costs you nothing but a local hop.

---

## Why the name

Mimas is the seventh moon of Saturn: small, mostly buried in its own crater,
and quietly keeping its orbit. That is the job here.

---

## License

MIT
