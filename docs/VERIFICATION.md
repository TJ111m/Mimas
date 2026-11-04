# Verification log

Everything below was run on Windows 11 (10.0.26200), .NET Framework 4.8,
against the relay that exhibited the bug.

## 1. Confirming the bug exists

A capture proxy sat in front of the relay and recorded the exact bytes ZCode
sent. ZCode's request was already correct — the image was nested in
`tool_result.content`, exactly as Anthropic documents.

The relay stringifies `tool_result.content` before forwarding. Measured with a
1.1 MB noisy PNG:

| Image position | `input_tokens` | Model sees the image |
| --- | ---: | :---: |
| Top-level user content block | 1,155 | yes |
| Inside `tool_result.content` (array) | 1,047,059 | no |
| Inside `tool_result.content` (stringified) | 1,047,057 | no |

Vision check: asked "what colour is the image?" on a solid red 128×128 PNG, the
nested form returned an **empty string**; the hoisted form returned `Red`.

## 2. Rewriter unit tests

```
scripts\test.cmd
```

20 assertions, all passing:

- text-only bodies untouched (byte-identical)
- top-level images untouched
- nested image hoisted, tool_result keeps a text placeholder
- **hoisted block lands after the last `tool_result`, never between two of them**
- image-only result gets a placeholder
- two images in one result both hoisted
- text + image in one result: text preserved, image hoisted
- `data:` URL string hoisted with media type intact
- already-stringified block array hoisted
- message order and assistant messages untouched
- number formatting survives JSON round-trip (`1.0` stays `1.0`)

## 3. Proxy behaviour

Through Mimas, same requests as §1:

| Case | `input_tokens` |
| --- | ---: |
| Top-level image | 1,155 |
| Image in `tool_result.content` (array) | 1,203 |
| Image in `tool_result.content` (stringified) | 1,194 |
| Image after a preceding tool_result | 1,191 |

Vision: `Red.` — correct.

Edge cases, all `200`: two images in one tool_result, text+image mixed,
text-only tool_result, text-only request.

Real captured agent request replayed direct vs. through Mimas: **133,306 →
20,116 tokens** (6.6×), same 16 output tokens.

Multi-turn with the image retained in history:

| Turns after image | Direct | Mimas |
| --- | ---: | ---: |
| +0 | 8,879 | 1,044 |
| +1 | 8,892 | 1,057 |
| +3 | 8,918 | 1,083 |

## 4. End-to-end through ZCode itself

A scratch data home pointing at `http://127.0.0.1:8817`:

```
Read red128.png and tell me its color in one word.
→ Red
```

## 5. Process supervision

- `mode: watch`, watched process absent → nothing listening on 8817.
- Start the watched process → proxy comes up within one poll interval.
- Kill the watched process → proxy stops, port released.
- `mode: always` → listens immediately, tray process has no window
  (`MainWindowTitle` empty).

## 6. Exposure

- `127.0.0.1:8817` → `200`
- `192.168.x.x:8817` → `400 Bad Request - Invalid Hostname` (http.sys rejects
  the foreign Host header before the app sees it)
- `127.0.0.1:8817` with `Host: evil.example.com` → `403` (app-level guard)

## 7. Release artifact

Downloaded `Mimas.exe` from the published release and compared hashes:

```
sha256: 9a4776c511adf1da05a02826ae95d35b6f56a85342ebec7d1ed4387944bc8990
match: true
```

The downloaded binary was then run standalone (fresh empty directory) and
re-verified against the relay: it created `mimas.config.json` on first launch,
started listening, and returned `Red.` for the nested-image vision test.

## What was NOT verified

- Relays other than the one tested. The rewriter is shape-based, not
  host-specific, but only one relay was exercised.
- SSE streaming under sustained load. Streaming was exercised (all real agent
  runs used `stream: true`) but not stress-tested.
- Non-Windows platforms. Mimas is Windows-only by construction (WinForms tray,
  `HttpListener`, HKCU Run key).
