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

## 8. Live end-to-end run (2026-11-04, after install)

Mimas 1.0.0 installed to `%LOCALAPPDATA%\Mimas\`, `mode: always`, real ZCode
provider config pointed at `http://127.0.0.1:8817`.

### 8a. Size sweep, raw relay A/B

Same request twice — image nested in `tool_result.content` vs hoisted as a
sibling block — against the relay directly, no proxy involved. Image is a
rendered 6-character code; the question is "what code is written in the image".

| file | bytes | nested `input_tokens` | hoisted `input_tokens` | ratio | nested answer | hoisted answer |
| --- | ---: | ---: | ---: | ---: | --- | --- |
| `sweep_128.png` | 3,538 | 3,479 | 454 | 7.7× | *"I can't determine the code…"* | `K3` |
| `sweep_320.png` | 6,648 | 6,037 | 454 | 13.3× | *"The"* | `R7` |
| `sweep_640.png` | 23,756 | 21,206 | 766 | 27.7× | *(empty)* | `M4X` |
| `sweep_900.png` | 49,842 | 44,443 | 1,222 | 36.4× | *"does not contain any…"* | `PWZ9BQ` |

Two things to read off this: the hoisted cost tracks the image's *pixel*
content (~450–1,200 tokens), while the nested cost tracks its *base64 length*,
so the ratio grows with file size. And the nested form never once produced the
right answer.

### 8b. Real ZCode conversation, proxied vs direct

`zcode -p "…tell me the 6-character code written in code900.png…" --mode yolo`,
run once with the provider pointing at Mimas and once straight at the relay.

| | through Mimas | direct to relay |
| --- | ---: | ---: |
| API calls in the run | **2** | 21 |
| Σ `input_tokens` | **23,764** | 3,047,623 |
| Σ `cache_read_input_tokens` | 12,288 | 2,424,192 |
| `Bash` calls | **0** | 16 |
| wall clock | ~30 s | >7 min (timed out of the foreground call) |
| answer | `PWZ9BQ` ✅ | `PWZ9BQ` ✅ (after OCR-ing the file itself with Python) |

The direct run "succeeded" only because the model, unable to see the image,
fell back to writing Python to decode and OCR the PNG byte by byte — 16 Bash
calls, 19 extra model round-trips, and ~3M tokens. Through Mimas it read the
code off the image in one shot.

The same A/B on `big1600.png` (1.1 MB of pixel noise):

| | through Mimas | direct to relay |
| --- | ---: | ---: |
| calls | 2 | 20 |
| peak single-request `input_tokens` | **18,161** | 404,194 |
| answer | "uniform field of fine olive-green noise" | same, after multi-scale image analysis |

### 8c. Desktop app note

The ZCode desktop app reads `provider_config.json` once per app-server process
(`watch: false` in the bundled runtime). Changing `baseUrl` therefore has no
effect on an already-running window — the app must be restarted. Confirmed:
requests from a desktop session started before the change still went straight
to the relay (no `hoisted` lines in `mimas.log`) for 20+ turns after the edit.

## What was NOT verified

- Relays other than the one tested. The rewriter is shape-based, not
  host-specific, but only one relay was exercised.
- SSE streaming under sustained load. Streaming was exercised (all real agent
  runs used `stream: true`) but not stress-tested.
- Non-Windows platforms. Mimas is Windows-only by construction (WinForms tray,
  `HttpListener`, HKCU Run key).
