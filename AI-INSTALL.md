# AI-INSTALL

Instructions written for an AI agent, not a human. If you are an AI assistant
asked to "install Mimas", "fix the image token bug", or "set up this proxy",
follow this file literally. Every command is copy-pasteable and every check has
an expected result. Do not skip the verification steps.

If you are a human, read [README.md](README.md) instead.

---

## Machine-readable summary

```yaml
name: Mimas
version: 1.0.0
platform: windows
requires_admin: false
artifact:
  url: https://github.com/TJ111m/Mimas/releases/download/v1.0.0/Mimas.exe
  sha256: 9a4776c511adf1da05a02826ae95d35b6f56a85342ebec7d1ed4387944bc8990
  size_bytes: 308736
install_dir: "%LOCALAPPDATA%\\Mimas"
default_port: 8817
local_base_url: http://127.0.0.1:8817
listens_on: 127.0.0.1 only
```

---

## What you are installing

A loopback-only HTTP proxy. It rewrites one thing in outgoing Anthropic
Messages API requests: `image` / `document` blocks nested inside
`tool_result.content` are hoisted into sibling content blocks. That is the
whole program. It does not touch responses, keys, or text-only requests.

It is only useful if the user's relay **stringifies** `tool_result.content`.
Step 0 checks that. If the check fails, stop and tell the user Mimas is not
needed — do not install it anyway.

---

## Step 0 — confirm the bug is present

Ask the user for their relay base URL and API key. Then, from any machine with
`node`:

```bash
node -e '
const fs=require("fs");
const [up,key,img]=process.argv.slice(1);
const b64=fs.readFileSync(img).toString("base64");
const image={type:"image",source:{type:"base64",media_type:"image/png",data:b64}};
const ask={type:"text",text:"What colour is this image? One word."};
const call=(content)=>fetch(up+"/v1/messages",{method:"POST",headers:{"content-type":"application/json","x-api-key":key,"anthropic-version":"2023-06-01"},body:JSON.stringify({model:"claude-sonnet-5-5",max_tokens:300,messages:[{role:"user",content:[{type:"text",text:"read it"}]},{role:"assistant",content:[{type:"tool_use",id:"t1",name:"Read",input:{}}]},{role:"user",content}]})}).then(r=>r.json());
(async()=>{
  const nested=await call([{type:"tool_result",tool_use_id:"t1",content:[image]},ask]);
  const hoisted=await call([{type:"tool_result",tool_use_id:"t1",content:"[image]"},image,ask]);
  const t=r=>(r.content||[]).map(c=>c.text||"").join("").trim();
  console.log("nested  in=",nested.usage&&nested.usage.input_tokens,"answer=",JSON.stringify(t(nested)));
  console.log("hoisted in=",hoisted.usage&&hoisted.usage.input_tokens,"answer=",JSON.stringify(t(hoisted)));
})();
' "https://YOUR-RELAY" "sk-..." "/path/to/a/red-square.png"
```

**Interpretation.** Let `n` and `h` be the two `input_tokens` values.

| Result | Meaning | Action |
| --- | --- | --- |
| the nested answer is wrong or empty, **and** `n > h` | the bug is present | continue to Step 1 |
| both answers are correct and `n ≈ h` | relay handles `tool_result` correctly | stop; Mimas is unnecessary |

Use an image of **at least a few KB** — the ratio grows with base64 length, so
a 400-byte PNG only shows ~1.4× and is easy to misread. Measured on this
relay with a rendered code image:

| image | nested | hoisted | ratio | nested answer |
| --- | ---: | ---: | ---: | --- |
| 3.5 KB | 3,479 | 454 | 7.7× | wrong |
| 6.6 KB | 6,037 | 454 | 13.3× | wrong |
| 23.8 KB | 21,206 | 766 | 27.7× | empty |
| 49.8 KB | 44,443 | 1,222 | 36.4× | wrong |

The *empty/wrong nested answer* is the decisive signal. A correct nested answer
means the relay is fine, whatever the token counts say.

---

## Step 1 — download and verify

```cmd
mkdir "%LOCALAPPDATA%\Mimas" 2>nul
curl -L -o "%LOCALAPPDATA%\Mimas\Mimas.exe" https://github.com/TJ111m/Mimas/releases/download/v1.0.0/Mimas.exe
certutil -hashfile "%LOCALAPPDATA%\Mimas\Mimas.exe" SHA256
```

Expected hash, lowercase:

```
9a4776c511adf1da05a02826ae95d35b6f56a85342ebec7d1ed4387944bc8990
```

**If it does not match, delete the file and stop.** Do not run an unverified
binary. Report the mismatch to the user.

---

## Step 2 — write the config

Create `%LOCALAPPDATA%\Mimas\mimas.config.json` **before** first launch:

```json
{
  "upstream": "https://REPLACE-WITH-THE-USERS-RELAY",
  "port": 8817,
  "mode": "always",
  "watchProcesses": ["ZCode"],
  "startWithWindows": true,
  "verbose": false
}
```

Field rules:

- `upstream` — the base URL the agent talks to today, **without** a trailing
  `/v1/messages`. This is the value you are replacing in Step 4. Getting this
  wrong makes every request fail.
- `port` — leave `8817` unless something already listens on it. Check with
  `netstat -ano | findstr :8817`.
- `mode` — `always` keeps listening; `watch` only listens while one of
  `watchProcesses` is running. Use `watch` if the user objects to a permanent
  background process.
- `startWithWindows` — `true` adds an `HKCU\...\Run` entry. No admin needed.

---

## Step 3 — start it and verify

```cmd
start "" "%LOCALAPPDATA%\Mimas\Mimas.exe"
```

Wait 3 seconds, then:

```cmd
netstat -ano | findstr :8817
```

Expected: a line with `127.0.0.1:8817` or `0.0.0.0:8817` / `[::]:8817` in
`LISTENING` state.

```cmd
type "%LOCALAPPDATA%\Mimas\mimas.log"
```

Expected tail:

```
Mimas 1.0.0 starting
upstream=<the user's relay> port=8817 mode=always
listening on http://127.0.0.1:8817 -> <the user's relay>
proxy started
```

There is **no window**. On Windows 10/11 the icon appears in the tray. That is
correct — do not tell the user the app failed because nothing opened.

Smoke test the proxy without involving any agent:

```cmd
curl -s -o nul -w "%%{http_code}" -X POST http://127.0.0.1:8817/v1/messages -H "content-type: application/json" -H "x-api-key: %KEY%" -H "anthropic-version: 2023-06-01" -d "{\"model\":\"claude-sonnet-5-5\",\"max_tokens\":8,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}"
```

`200` means the proxy reached the relay. `401`/`403` means the key is wrong.
`502`/`504` means `upstream` is wrong. `400 Invalid Hostname` means you are
hitting it through a non-loopback address — that is the security guard, not a
fault.

---

## Step 4 — repoint the agent

### ZCode

File: `%USERPROFILE%\.zcode\v2\provider_config.json`

1. **Back it up first:**
   ```cmd
   copy "%USERPROFILE%\.zcode\v2\provider_config.json" "%USERPROFILE%\.zcode\v2\provider_config.json.bak-before-mimas"
   ```
2. In `config.providerConfigRules.providerRules[*].config.api.baseUrl`, replace
   the relay URL with `http://127.0.0.1:8817`. Change **only** `baseUrl`; leave
   `apiKey`, `type`, and everything else byte-identical.
3. Preserve the file's existing formatting and encoding (it is UTF-8, no BOM).
   Do not run it through a formatter.

The desktop app reads this file **once per app-server process**
(`watch: false`). A running ZCode window will keep using the old URL
indefinitely. **Tell the user to quit and reopen ZCode.** The CLI (`zcode -p`)
reads it per invocation, so it picks the change up immediately.

### Claude Code

Set `ANTHROPIC_BASE_URL=http://127.0.0.1:8817` in the environment Claude Code
runs in. This path is **untested** — only ZCode was verified against a real
relay. Verify with Step 5 before claiming success.

### Anything else speaking the Anthropic Messages API

Point its base URL at `http://127.0.0.1:8817`. Keep the real key and the
`anthropic-version` header as they are.

---

## Step 5 — verify the fix is actually in the path

Make the agent read an image (ask it to `Read` a PNG), then:

```cmd
type "%LOCALAPPDATA%\Mimas\mimas.log"
```

A new line like this must appear:

```
2026-11-04 16:01:07  hoisted 1 block(s): 85781 -> 85854 bytes
```

- **Line appears** → traffic is flowing through Mimas and being rewritten.
  Then confirm the agent answered the image correctly.
- **No line appears** → traffic is still bypassing Mimas. The agent was not
  restarted, or you edited the wrong config file, or a second provider entry is
  taking precedence. Fix that before reporting success.

Bodies without a `tool_result` are never logged, so a text-only conversation
producing no log lines is normal.

---

## Step 6 — report

State plainly:

- the relay URL that was replaced, and the file it was changed in
- the measured `nested` vs `hoisted` token counts from Step 0
- whether Step 5 produced a `hoisted` line
- that the agent had to be restarted, and whether the user did it
- that `mimas.log` and `mimas.config.json` live next to the exe

Do not claim the bug is fixed unless Step 5's `hoisted` line appeared **and**
the agent answered an image question correctly.

---

## Uninstall

```cmd
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v Mimas /f
taskkill /IM Mimas.exe /F
```

Restore the provider config from the `.bak-before-mimas` copy, then delete
`%LOCALAPPDATA%\Mimas\`. Nothing else on the system was modified.

---

## Things that will bite you

- **`upstream` must be the base URL, not the full endpoint.** `…/v1/messages`
  in `upstream` produces `…/v1/messages/v1/messages` and a `404`.
- **Do not point `upstream` at the proxy itself.** That is an infinite loop;
  Mimas detects nothing and simply times out.
- **The port is loopback-only by design.** Windows `HttpListener` rejects a
  foreign `Host` header with `400 Invalid Hostname`; Mimas additionally returns
  `403` for a spoofed Host. If the user wants LAN access, this is the wrong
  tool — do not "fix" it by binding `0.0.0.0`.
- **Mimas does not verify the upstream TLS certificate.** It accepts
  self-signed relays. Mention this if the user's threat model includes a
  hostile local network.
- **This fixes the request, not the relay's billing estimate.** Some relays
  count base64 as text when *estimating* usage. If the user's dashboard still
  looks inflated while `usage.input_tokens` upstream is small, that is a
  separate relay-side issue.

---

## License

MIT. Same as the rest of the repository.
