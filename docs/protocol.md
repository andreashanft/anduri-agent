# Anduri agent protocol · version 1

The Anduri iPad app shows sensor data from a PC. On the PC, the **Anduri agent** reads the sensors and streams them to paired iPads over an encrypted WebSocket. This document is the contract between the two. The sample messages in [`protocol/samples`](protocol/samples) and the test vectors in [`protocol/pairing-vectors.json`](protocol/pairing-vectors.json) are checked by the test suites of both the app and the agent.

## Overview

| | |
|---|---|
| Discovery | Bonjour / mDNS service `_anduri._tcp` |
| Default port | `48123` (configurable on both sides) |
| Transport | WebSocket over TLS (`wss://`), HTTP/1.1 upgrade on path `/` or `/v1` |
| Messages | UTF-8 JSON text frames, one message per frame |
| Encryption | TLS 1.2 or later with the agent's self-signed certificate |
| Authentication | Certificate fingerprint pinned at pairing, plus a device token |
| Update rate | 0.5 to 10 seconds per client, 1 s by default |

Every message is a JSON object with a `type` field. **Both sides ignore message types and fields they don't recognize**, so newer agents and apps keep working with older ones within the same protocol version.

Timestamps are Unix time in seconds (a JSON number, fractions allowed). Binary values are Base64 (`nonce`, `proof`) or Base64url without padding (`token`). Fingerprints are lowercase hex without separators.

## Discovery

The agent advertises one service instance:

- **Type:** `_anduri._tcp`, domain `local.`
- **Instance name:** the agent's display name, e.g. `RIG-01` (defaults to the host name).
- **Port:** the WebSocket port.
- **TXT record:**

| Key | Example | Meaning |
|---|---|---|
| `v` | `1` | Protocol version |
| `id` | `0B7A1E6C-3D52-4C1F-9A0E-2F5D8C7B6A41` | Agent id, stable across restarts |
| `name` | `RIG-01` | Display name (the instance name may get a suffix on conflicts) |
| `ver` | `1.1.0` | Agent version |
| `sensors` | `38` | Number of sensors in the catalog |
| `os` | `linux` | `linux`, `windows` or `macos` |
| `fp` | `3e584012d24fef73` | First 16 hex characters of the certificate fingerprint |

The app uses `id` to recognize a PC it has already paired with, even after its IP address changes. `fp` is only a hint for the UI; security always relies on the full fingerprint (see [Pairing](#pairing)).

Agents before 1.1.0 advertised `_pcvitals._tcp`. The Anduri app needs agent 1.1.0 or later: it doesn't browse for the old type, and it closes connections to older agents after `welcome` or `pair.challenge` and asks the user to update the agent.

## Certificate

On first start the agent creates a self-signed **ECDSA P-256** certificate for its name (valid for 10 years) and keeps it with its state. The **fingerprint** is the SHA-256 hash of the certificate's DER encoding.

The certificate only changes if the agent's state is deleted or the certificate is explicitly regenerated. Paired iPads then refuse to connect until they are paired again.

## Connection flow

```
iPad                                   agent
 │  TLS handshake (fingerprint pinned)   │
 │──────────── hello ───────────────────▶│  token checked
 │◀─────────── welcome ──────────────────│
 │◀─────────── catalog ──────────────────│
 │◀─────────── history ──────────────────│  if requested
 │◀─────────── snapshot ─────────────────│  every interval
 │◀─────────── snapshot ─────────────────│
 │──────────── setInterval ─────────────▶│
 │◀─────────── interval ─────────────────│
 │◀─────────── catalog ──────────────────│  whenever sensors change
```

The client must send `hello` (or `pair.request`) within **10 seconds** of the WebSocket opening, or the agent closes the connection with code `4408`.

### `hello` · client → agent

```json
{"type":"hello","protocol":1,"deviceId":"6F9619FF-…","token":"ZGVmZ2…","interval":1,"history":true}
```

| Field | Type | |
|---|---|---|
| `protocol` | integer | Highest protocol version the client speaks |
| `deviceId` | string | The device id used when pairing |
| `token` | string | Token from `pair.accepted` |
| `interval` | number | Requested seconds between snapshots; the agent clamps it to 0.5…10 |
| `history` | boolean | Whether to send the agent's history buffer before the first snapshot |

If the device or token is unknown, the agent sends `error` with code `unauthorized` and closes with `4401`. The client must not retry with the same token and should ask the user to pair again. If `protocol` is lower than the agent supports, the agent answers `error` with `unsupported_protocol` and closes with `4400`.

### `welcome` · agent → client

```json
{"type":"welcome","protocol":1,"agent":{"id":"0B7A…","name":"RIG-01","version":"1.1.0","os":"linux"},"interval":1}
```

`protocol` is the version used for this connection. `interval` is the effective interval after clamping. It is followed by `catalog`, then `history` if requested, then snapshots. A client may close the connection when `agent.version` is older than it supports (see [Discovery](#discovery)).

### `catalog` · agent → client

The full list of sensors. It replaces any previous catalog. The agent sends it after `welcome` and again whenever sensors appear or disappear (a drive is mounted, a USB controller is plugged in).

```json
{"type":"catalog","sensors":[{"id":"cpu.temp","name":"CPU package temperature","kind":"temperature","unit":"°C","hardware":"AMD Ryzen 9 7950X3D","label":"Package"}]}
```

| Field | Type | Required | |
|---|---|---|---|
| `id` | string | yes | Stable id, see [Sensor ids](#sensor-ids) |
| `name` | string | yes | Full name, e.g. "GPU core temperature" |
| `kind` | string | yes | One of the [kinds](#kinds-and-units) |
| `unit` | string | yes | Unit of every value of this sensor |
| `hardware` | string | no | Device name, e.g. "NVIDIA GeForce RTX 4090" |
| `label` | string | no | Short label for tiles, e.g. "Core", "Radiator 280", "/" |
| `shortLabel` | string | no | Even shorter label for small tiles |
| `capacity` | number | no | Total for `memory` and `storage`, in `unit` |
| `maximum` | number | no | Rated maximum, e.g. GPU boost clock |
| `detail` | string | no | Free text, e.g. "16 cores · 32 threads", "2.5 GbE" |

### `history` · agent → client

The agent keeps the last **15 minutes** of every sensor at 1-second resolution and sends them in columns.

```json
{"type":"history","start":1758039100,"step":1,"series":{"cpu.temp":[41.2,41.5,null,42.0]}}
```

Value `i` of every series belongs to time `start + i × step`. `null` marks a missing sample. All series have the same length.

### `snapshot` · agent → client

The latest value of every sensor, sent at the connection's interval.

```json
{"type":"snapshot","t":1758040000.5,"values":{"cpu.temp":41.8,"net.down":11500000}}
```

`t` is when the values were read. Sensors that couldn't be read are left out. Values for ids not in the catalog are ignored.

### `setInterval` · client → agent, `interval` · agent → client

```json
{"type":"setInterval","interval":2}
{"type":"interval","interval":2}
```

Changes the snapshot interval for this connection. The agent answers with the effective (clamped) interval.

### `error` · agent → client

```json
{"type":"error","code":"unauthorized","message":"This device isn't paired with RIG-01."}
```

| Code | Close code | Meaning |
|---|---|---|
| `bad_request` | 4400 | Malformed or unexpected message |
| `unsupported_protocol` | 4400 | No common protocol version |
| `unauthorized` | 4401 | Unknown device or wrong token |
| `timeout` | 4408 | No `hello` or `pair.request` in time |
| `busy` | 4409 | Another device is pairing right now |
| `too_many_attempts` | 4429 | Too many wrong pairing codes |

`message` is human-readable English text for logs. Clients show their own text based on `code`. Every `error` is followed by closing the connection with its close code. `bad_request` also covers known messages in the wrong state, for example `setInterval` before `hello`, a second `hello`, or `pair.confirm` without a pairing window. It also covers binary frames and frames larger than 64 KB.

## Pairing

Pairing gives an iPad a token and fixes (pins) the agent's certificate on the iPad. The user confirms it with a 6-digit code that the agent shows on the PC.

```
iPad                                         agent
 │  TLS handshake, fingerprint F recorded       │
 │──────────── pair.request ──────────────────▶│  shows code on the PC
 │◀─────────── pair.challenge (nonce) ─────────│  window open for 120 s
 │  user types the code                          │
 │──────────── pair.confirm (client proof) ───▶│  checks proof with its own F
 │◀─────────── pair.accepted (token, proof) ───│  stores SHA-256(token)
 │  checks agent proof, stores F and token       │
 │──────────── hello ─────────────────────────▶│  session continues
```

1. The client opens the WebSocket **without** a pinned certificate and records the fingerprint `F` of the certificate the server presented.
2. The client sends `pair.request`:
   ```json
   {"type":"pair.request","protocol":1,"device":{"id":"6F9619FF-…","name":"Homer’s iPad","model":"iPad"}}
   ```
   `device.id` is a UUID the app creates once and keeps. `device.name` is shown on the PC.
3. The agent opens a **pairing window** for this connection. It draws a uniformly random 6-digit code (`000000`…`999999`) and a 32-byte random nonce. It shows the code to the user (console, log or system notification) together with the device name. Then it answers:
   ```json
   {"type":"pair.challenge","nonce":"AAECAw…","expiresIn":120,"agent":{"id":"0B7A…","name":"RIG-01","version":"1.1.0","os":"linux"}}
   ```
   Only one pairing window can be open at a time. If another connection holds it, the agent answers `pair.rejected` with reason `busy` and closes with `4409`. A new `pair.request` on the same connection replaces that connection's window (new code, new nonce, 5 fresh attempts). The window closes as soon as its connection closes.
4. The user types the code on the iPad. The client computes

   `clientProof = HMAC-SHA256(key: UTF8(code), message: UTF8("anduri-pair-v1/client") ‖ nonce ‖ F ‖ UTF8(device.id))`

   where `nonce` and `F` are the raw 32 bytes. It then sends:
   ```json
   {"type":"pair.confirm","proof":"OA21Rn…"}
   ```
5. The agent computes the same proof with **its own** certificate fingerprint and compares in constant time.
   - **Mismatch:** `{"type":"pair.rejected","reason":"wrong_code","attemptsLeft":4}`. After 5 wrong codes the agent sends `pair.rejected` with `too_many_attempts` and closes with `4429`.
   - **Window expired:** `pair.rejected` with `expired`. The client may send a new `pair.request` on the same connection.
   - **Match:** the agent creates a random 32-byte token and stores the device (id, name, SHA-256 of the token's 32 raw bytes, pairing date). It answers:
     ```json
     {"type":"pair.accepted","token":"ZGVmZ2…","proof":"Czj2yI…","agent":{…}}
     ```
     `proof` is `HMAC-SHA256(key: UTF8(code), message: UTF8("anduri-pair-v1/agent") ‖ nonce ‖ F ‖ UTF8(device.id))`.
6. The client checks the agent's proof. If it matches, it stores `F`, the token and the agent id. It then either sends `hello` on the same connection within 10 seconds, or closes and connects again with the certificate pinned. If the proof doesn't match, the client closes the connection and discards the token.

While a pairing window is open, the agent keeps the connection open for the window's 120 seconds plus a 60-second grace period. `expired` is only sent in reply to `pair.confirm`, never on its own.

Because both proofs include `F`, a machine in the middle presenting a different certificate can't produce a valid proof and can't complete pairing just by relaying messages. The code is short, though: an attacker who sits in the middle during the 120-second window and captures a client proof could brute-force the code offline. The agent never stores the raw token, so reading its state file doesn't reveal tokens. Revoking a device on the agent (`anduri-agent devices revoke <id>`) makes its next `hello` fail with `unauthorized`.

### Pairing reasons

| `reason` | Meaning |
|---|---|
| `wrong_code` | Proof didn't match; `attemptsLeft` says how many tries remain |
| `expired` | The window closed before a correct code arrived |
| `too_many_attempts` | Window closed after 5 wrong codes; the connection closes |
| `busy` | Another pairing is in progress; the connection closes with `4409` |

### Test vectors

[`protocol/pairing-vectors.json`](protocol/pairing-vectors.json) contains a code, nonce, fingerprint and device id with the expected client and agent proofs, a proof for a wrong code, and a token with its SHA-256 hash.

## Sensor ids

Ids are lowercase, dot-separated and stable across agent restarts. The primary sensors use **well-known ids**, so a dashboard set up for one PC mostly works on another:

| Id | Kind | Unit | Source (Linux) |
|---|---|---|---|
| `cpu.load` | load | % | `/proc/stat` |
| `cpu.temp` | temperature | °C | hwmon `k10temp` Tctl or `coretemp` package |
| `cpu.power` | power | W | RAPL (`/sys/class/powercap`, needs root) |
| `gpu.load` | load | % | NVML |
| `gpu.temp` | temperature | °C | NVML |
| `gpu.hotspot` | temperature | °C | NvAPI (`libnvidia-api.so.1`; NVML has no hotspot) |
| `gpu.power` | power | W | NVML |
| `gpu.clock` | frequency | MHz | NVML |
| `gpu.memclock` | frequency | MHz | NVML |
| `mem.ram` | memory | GB | `/proc/meminfo` (used = total − available) |
| `mem.vram` | memory | GB | NVML |
| `loop.coolant` | temperature | °C | Aquacomputer hwmon, CoolerControl |
| `loop.ambient` | temperature | °C | Aquacomputer hwmon (external sensor), if present |
| `loop.flow` | flow | l/h | Aquacomputer hwmon, CoolerControl |
| `loop.pump` | rpm | rpm | Aquacomputer hwmon, CoolerControl |
| `net.down`, `net.up` | network | B/s | Default-route interface, `/sys/class/net` |

Further sensors use source-specific ids, for example `fan.nct6799.2`, `drive.root`, `drive.home`, `gpu1.temp` for a second GPU, or `cc.<device>.<channel>` for CoolerControl channels.

## Kinds and units

| Kind | Unit | Notes |
|---|---|---|
| `temperature` | `°C` | |
| `load` | `%` | 0–100 |
| `power` | `W` | |
| `rpm` | `rpm` | Fans and pumps |
| `flow` | `l/h` | |
| `memory` | `GB` | Used amount; `capacity` is the total. GB means 2^30 bytes |
| `frequency` | `MHz` | |
| `storage` | `GB` | Used space; `capacity` is the size. GB means 2^30 bytes |
| `network` | `B/s` | Bytes per second |
| `voltage` | `V` | |
| `other` | any | Anything else, e.g. fan duty in `%` |

## Close codes

Besides the codes listed under `error`, the agent closes with `1001` when it shuts down. Clients reconnect with exponential backoff (1 s, 2 s, 4 s … up to 30 s, with jitter) after any close except `4401` and `4429`.
