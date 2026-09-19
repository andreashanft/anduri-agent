# Anduri agent

The Anduri agent runs on your PC, reads its hardware sensors and streams them to the Anduri iPad app over an encrypted WebSocket. It:

- reads CPU load, temperature and package power, memory, NVIDIA GPUs (NVML), Aquacomputer devices, motherboard fans and temperatures (hwmon), CoolerControl or liquidctl devices, drives and network throughput,
- keeps a 15-minute, 1-second history so the iPad's charts are full as soon as it connects,
- announces itself on the local network via Bonjour (`_anduri._tcp`), so the iPad finds it without typing an IP address,
- pairs iPads with a 6-digit code shown on the PC, then only accepts paired iPads.

Linux is the primary target. On macOS (and anywhere else without a sensor backend) the agent streams a simulated water-cooled rig, which is handy for developing the app. The wire protocol is specified in [`docs/protocol.md`](docs/protocol.md).

## Contents

- [Build and run](#build-and-run)
- [Install on Linux as a systemd service](#install-on-linux-as-a-systemd-service)
- [Permissions per sensor](#permissions-per-sensor)
- [Pairing an iPad](#pairing-an-ipad)
- [Configuration](#configuration)
- [Sensors and ids](#sensors-and-ids)
- [Discovery and firewall](#discovery-and-firewall)
- [Troubleshooting](#troubleshooting)
- [Development](#development)

## Build and run

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
dotnet build
dotnet test

# Real sensors (Linux)
dotnet run --project src/Anduri.Agent -- run

# Simulated rig, e.g. on a Mac, with a throwaway state directory
dotnet run --project src/Anduri.Agent -- run --simulate --name RIG-01 --state-dir /tmp/anduri-state
```

The agent listens on `wss://<pc>:48123/` (also `/v1`) and logs where its state is and the certificate fingerprint:

```
22:07:39 info: Anduri.Agent[…] Anduri agent “RIG-01” listening on wss://*:48123/ — certificate fingerprint cf2ebbd6…, state in /tmp/anduri-state
22:07:41 info: Anduri.Agent.Discovery.CommandAdvertiser[…] Advertising “RIG-01” as _anduri._tcp on port 48123 via dns-sd
```

Stop it with Ctrl+C (or SIGTERM). Connected iPads get close code 1001 and reconnect when it's back.

### Commands

```
anduri-agent run [--port 48123] [--name RIG-01] [--simulate] [--config path] [--state-dir path] [--discovery auto|avahi|dns-sd|managed|off] [--verbose]
anduri-agent devices list
anduri-agent devices revoke <id>        # full id or a unique prefix of at least 4 characters
anduri-agent cert show                  # fingerprint of the TLS certificate
anduri-agent cert regenerate            # new certificate; every iPad has to pair again
anduri-agent --version
```

`devices`, `cert` and `run` all accept `--state-dir` and `--config`. Exit codes: 0 success, 1 failure, 2 usage error.

### Publish a single executable for Linux

```sh
dotnet publish src/Anduri.Agent -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:PublishTrimmed=true -o publish/linux-x64
```

This gives one self-contained `publish/linux-x64/anduri-agent` (about 22 MB trimmed, 100 MB without `PublishTrimmed`) that doesn't need .NET on the target. It needs glibc and OpenSSL (libssl), which every desktop distribution has. Use `-r linux-arm64` for ARM boards. The build is trim-clean: all JSON goes through source-generated serializers.

## Install on Linux as a systemd service

```sh
sudo install -m 0755 publish/linux-x64/anduri-agent /usr/local/bin/anduri-agent
sudo install -m 0644 deploy/anduri-agent.service /etc/systemd/system/
# Optional: configuration (0600, it can hold the CoolerControl password)
sudo install -D -m 0600 deploy/config.example.json /etc/anduri-agent/config.json
sudo systemctl daemon-reload
sudo systemctl enable --now anduri-agent
journalctl -u anduri-agent -f
```

The unit ([`deploy/anduri-agent.service`](deploy/anduri-agent.service)):

- keeps its state in `/var/lib/anduri-agent` (`StateDirectory=`; the agent picks it up from `$STATE_DIRECTORY`),
- reads **only `/etc/anduri-agent/config.json`**. The service's home is `/var/lib/anduri-agent` and it can't see home directories, so a `~/.config/anduri-agent/config.json` in your home is ignored. Put the file in `/etc/anduri-agent/` (or point `ExecStart=` at it with `--config`). At startup the log says which file was loaded: `Config file: /etc/anduri-agent/config.json` or `No config file, using defaults (looked for …)`,
- runs as root, **with every capability dropped**, a read-only file system (`ProtectSystem=strict`) and no access to home directories. Root is only there because the RAPL energy counters and liquidctl's USB devices are root-only by default. Port 48123 needs no privileges.

Manage the service's paired devices with the same state directory:

```sh
sudo anduri-agent devices list --state-dir /var/lib/anduri-agent
sudo anduri-agent devices revoke 6F96 --state-dir /var/lib/anduri-agent
sudo anduri-agent cert show --state-dir /var/lib/anduri-agent
```

**Updating:** replace the executable and restart the service. Pairings and the certificate stay in the state directory, so paired iPads reconnect by themselves (within 30 seconds when the app asked for the update).

```sh
sudo install -m 0755 publish/linux-x64/anduri-agent /usr/local/bin/anduri-agent
sudo systemctl restart anduri-agent
```

**Running unprivileged instead:** create a system user (`sudo useradd --system --home-dir /var/lib/anduri-agent --shell /usr/sbin/nologin anduri`), add `User=anduri` and `Group=anduri` to the unit, give that group the RAPL and liquidctl permissions below, and run management commands as that user (`sudo -u anduri anduri-agent devices list --state-dir /var/lib/anduri-agent`). Files written by root in the state directory would otherwise become unreadable for the service.

## Permissions per sensor

| Sensors | Source | Needs root? | What's required |
|---|---|---|---|
| `cpu.load`, `mem.ram`, `drive.*`, `net.*` | `/proc`, `/sys/class/net`, `statvfs` | No | Nothing |
| `cpu.temp`, `fan.<chip>.*`, `temp.<chip>.*` | hwmon (`k10temp`, `coretemp`, `nct6775`, `it87` …) | No | The kernel driver for your Super I/O chip. Some boards need `nct6775`/`it87` loaded manually or an out-of-tree `it87` |
| `cpu.power` | RAPL, `/sys/class/powercap/intel-rapl:*/energy_uj` (Intel and AMD Zen) | **Yes**, or a udev rule | Since the 2020 PLATYPUS side-channel fix (kernel 5.10, backported to older stable kernels) `energy_uj` is root-only. See the rule below. Without it `cpu.power` is left out and logged once |
| `gpu.*`, `mem.vram` | NVML (`libnvidia-ml.so.1`) | No | NVIDIA's proprietary driver (nouveau has no NVML) |
| `gpu.hotspot` | NvAPI (`libnvidia-api.so.1`) | Maybe: CoolerControl and LACT call it as root, as does the service | The same driver. NvAPI is undocumented on Linux, so this may not work on every driver or GPU; the log says how the hotspot is read or why it isn't |
| `loop.*`, `fan.aqua.*`, `temp.octo.*` … | hwmon `aquacomputer_d5next` | No | The in-kernel driver (loads automatically for D5 Next, Octo, Quadro, Farbwerk 360, High Flow Next, Leakshield, Aquaero, Aquastream …) |
| `cc.*` | CoolerControl daemon REST API | Daemon runs as root; the agent doesn't | CoolerControl 4.0+ requires credentials: an access token (recommended) or the `CCAdmin` password in the config |
| `lc.*` | `liquidctl --json status` (only while CoolerControl isn't reachable) | No, with udev rules | USB/hidraw access through liquidctl's udev rules (`71-liquidctl.rules`), or root |

RAPL without root, for the agent's group only (replace `anduri` with the group the service runs as):

```sh
# /etc/udev/rules.d/60-anduri-rapl.rules
SUBSYSTEM=="powercap", KERNEL=="intel-rapl:*", RUN+="/bin/chgrp anduri /sys%p/energy_uj", RUN+="/bin/chmod g+r /sys%p/energy_uj"
```

Then `sudo udevadm trigger --subsystem-match=powercap`. On a single-user machine `RUN+="/bin/chmod 0444 /sys%p/energy_uj"` also works, but makes the counters readable for every local user.

## Pairing an iPad

1. Start the agent. Make sure the iPad is on the same network.
2. In the app, add a PC. It appears under its name (e.g. **RIG-01**) through Bonjour, or enter the IP address and port 48123.
3. The app asks for a code. The PC's log (or `journalctl -u anduri-agent -f`) shows:
   ```
   Pairing request from “Homer’s iPad” — code 481 207 (expires in 2:00)
   ```
   When the agent runs in a terminal, the code is also printed in a box.
4. Type the code on the iPad. Both sides check a proof that covers the certificate fingerprint, so a machine in the middle can't complete the pairing. The iPad then pins the agent's certificate and keeps a token.

Rules: one pairing at a time (a second iPad gets `busy`), the code is valid for 2 minutes, and 5 wrong codes end the attempt.

**Revoking an iPad:** `anduri-agent devices list`, then `anduri-agent devices revoke <id>`. Its next connection is refused with `unauthorized` and the app asks to pair again. This works while the agent is running.

**Certificate:** the agent creates a self-signed ECDSA P-256 certificate (10 years) on first start. `anduri-agent cert show` prints its SHA-256 fingerprint, which the app shows after pairing. If the certificate changes (`cert regenerate`, or the state directory was deleted), paired iPads refuse to connect until they pair again. That's intended: it's exactly what an impostor would look like.

The state directory holds `agent.json` (the stable agent id), `certificate.pfx` (mode 0600) and `devices.json` (device id, name, SHA-256 of the token, pairing date and last seen). Raw tokens are never stored.

## Configuration

The config file is JSON; comments and trailing commas are allowed, unknown keys are an error. The agent uses `--config <path>`, otherwise the first of `~/.config/anduri-agent/config.json` and `/etc/anduri-agent/config.json` that exists. Command-line flags override the file. A complete example: [`deploy/config.example.json`](deploy/config.example.json).

> **Where the file goes depends on how the agent runs.** `~` is the home of the user running the agent. Run from a terminal, that's your home, so `~/.config/anduri-agent/config.json` works. The systemd service runs as root with its home set to `/var/lib/anduri-agent` and no access to `/home`, so it only finds `/etc/anduri-agent/config.json`. At startup the agent logs which file it loaded (`Config file: …`) or where it looked (`No config file, using defaults (looked for …)`); check with `journalctl -u anduri-agent -b | grep -i config`.

| Key | Default | Meaning |
|---|---|---|
| `name` | host name | Name shown on the iPad and advertised via Bonjour |
| `port` | `48123` | WebSocket port |
| `stateDir` | `$STATE_DIRECTORY`, `$XDG_STATE_HOME/anduri-agent` or `~/.local/state/anduri-agent` | Agent id, certificate, paired devices |
| `discovery` | `auto` | `auto`, `avahi`, `dns-sd`, `managed` or `off` (see [Discovery](#discovery-and-firewall)) |
| `simulate` | `false` | Stream the simulated rig |
| `sources.cpu` / `memory` / `nvidia` / `nvApi` / `aquacomputer` / `hwmon` / `coolerControl` / `liquidctl` / `drives` / `network` | `true` | Turn sources off. Sources without hardware turn themselves off anyway. `nvApi` is the GPU hotspot reading |
| `coolerControl.url` | `http://127.0.0.1:11987` | coolercontrold address. Plain HTTP is allowed from loopback |
| `coolerControl.token` | – | Access token (`cc_…`) from CoolerControl → Access Protection. Read-only is enough |
| `coolerControl.username` | `CCAdmin` | Used with `password` when no token is set |
| `coolerControl.password` | – | CoolerControl password |
| `coolerControl.includeAllDevices` | `false` | Also report CoolerControl's CPU, GPU and hwmon devices (duplicates of what the agent reads directly) |
| `coolerControl.allowSelfSignedCertificate` | `true` | Accept coolercontrold's self-signed certificate for `https` URLs |
| `liquidctl.path` | `liquidctl` | liquidctl executable |
| `liquidctl.interval` | `5` | Seconds between `liquidctl --json status` calls |
| `drives.include` | `[]` | If not empty, only these mount points. `"/mnt/*"` matches everything below `/mnt` |
| `drives.exclude` | `["/boot", "/boot/efi", "/efi"]` | Mount points to skip, same patterns |
| `network.interface` | default-route interface | Interface for `net.down`/`net.up` |
| `sensors.<id>.name` / `label` / `shortLabel` | – | Rename a sensor, e.g. `"fan.nct6799.2": {"label": "CPU fan", "shortLabel": "CPU"}` |
| `sensors.<id>.id` | – | Report the sensor under another id, e.g. `"temp.octo.1": {"id": "loop.coolant"}`. Wins over automatic assignments |
| `sensors.<id>.hidden` | `false` | Leave the sensor out |
| `hostRoot` | `/` | Prefix for `/proc` and `/sys`, for testing against a copied tree |

## Sensors and ids

Ids are stable across restarts. Well-known ids (from the protocol) come first; a dashboard built on one PC mostly works on another.

| Source | Ids | Notes |
|---|---|---|
| CPU | `cpu.load`, `cpu.temp`, `cpu.power` | Load from `/proc/stat` deltas. Temperature from `k10temp` Tdie/Tctl or `coretemp` "Package id 0". Power from RAPL energy deltas (all packages, counter wraparound handled). Hardware name and "16 cores · 32 threads" from `/proc/cpuinfo` |
| Memory | `mem.ram` | Used = MemTotal − MemAvailable, GB (GiB), capacity = total |
| NVIDIA | `gpu.load`, `gpu.temp`, `gpu.hotspot`, `gpu.power`, `gpu.clock`, `gpu.memclock`, `gpu.fan`, `mem.vram`; second GPU `gpu1.*` with `gpu1.vram` | NVML doesn't report a hotspot, so `gpu.hotspot` comes from NvAPI, the same undocumented interface LACT and CoolerControl use: slot 9 of the thermal sensors up to Ada, the aggregated hotspot register on Blackwell (RTX 50). Turn it off with `sources.nvApi: false`. `gpu.power` carries the enforced power limit as `maximum`, `gpu.clock` the maximum core clock |
| Aquacomputer | `loop.coolant` ("Coolant temp"), `loop.ambient` ("External sensor"), `loop.flow` (flow in dL/h ÷ 10 → l/h), `loop.pump` ("Pump speed"), `fan.aqua.<n>` (fan controllers first, then `fan.aqua2.<n>` …), `temp.<device>.<n>` ("Sensor n"), `temp.<device>.virtual<n>`, `flow.<device>.<n>`, `power.<device>.pump`, `voltage.<device>.12v`, `pressure.<device>`, `quality.<device>`, `conductivity.<device>`, `reservoir.<device>.filled` / `.volume` (Leakshield, ml) | The first device to report a coolant temperature, flow or pump gets the `loop.*` id; others get device ids. Octo and Quadro only have generic "Sensor n" inputs: map yours with `"sensors": {"temp.octo.1": {"id": "loop.coolant"}}`. Unconnected sensors (ENODATA) and fans that never spun are left out |
| hwmon | `fan.<chip>.<n>`, `temp.<chip>.<n>` | Everything except CPU, Aquacomputer and ACPI chips: Super I/O, NVMe, DIMMs, AMD GPUs. `<chip>` is the driver name (`nct6799`, `nvme`, `nvme_2` for a second one). Labels come from `*_label`; fans that read 0 (unconnected headers) and implausible temperatures are skipped until they report a real value |
| CoolerControl | `cc.<device>.<channel>`, `cc.<device>.<channel>.duty`, plus `loop.coolant` / `loop.flow` / `loop.pump` for "liquid"/"water" temperatures and "flow"/"pump" channels | Liquidctl, custom-sensor and plugin devices by default |
| liquidctl | `lc.<device>.<key>`, plus `loop.*` like CoolerControl | Only while CoolerControl isn't working |
| Drives | `drive.root`, `drive.home`, `drive.mnt_games` … | Real file systems only (ext4, btrfs, xfs, zfs, vfat, ntfs …; no tmpfs, overlay, squashfs, snaps or network mounts). Label = mount point, used and capacity in GB. A btrfs subvolume mounted twice counts once |
| Network | `net.down`, `net.up` | Bytes per second of the default-route interface; detail "1 GbE", "2.5 GbE", "10 GbE" or "Wi-Fi" |
| Simulated | the ids of the app's mock rig (`cpu.load` … `psu.12v`) | `--simulate`, or automatically on macOS |

When two sources report the same id, the earlier one in this table wins (a direct Aquacomputer reading beats CoolerControl's copy). A source that fails is logged once, left out, and retried every 30 seconds (2 minutes if its hardware or daemon isn't there at all); the others keep working.

## Discovery and firewall

The agent advertises one `_anduri._tcp` instance named after the agent, with TXT keys `v`, `id`, `name`, `ver`, `sensors`, `os` and `fp` (first 16 hex characters of the certificate fingerprint). How, with `discovery: auto`:

| Platform | Method |
|---|---|
| Linux with avahi-daemon running | `avahi-publish-service` subprocess (Avahi owns UDP 5353) |
| macOS | `dns-sd -R` subprocess |
| Anything else, or if the above fails | Built-in mDNS responder (answers PTR/SRV/TXT/A over IPv4 on UDP 5353) |

If nothing works, the log says so and the iPad can still connect by IP address and port. The `sensors` TXT value is updated when the catalog changes (at most every 30 seconds, because the subprocess methods briefly re-register).

Check the advertisement from another machine or the PC itself:

```sh
avahi-browse -rt _anduri._tcp          # Linux
dns-sd -B _anduri._tcp local.          # macOS
dns-sd -L RIG-01 _anduri._tcp local.   # macOS, shows the TXT record
```

**Firewall:** allow TCP 48123 (the agent) and UDP 5353 (mDNS):

```sh
# ufw
sudo ufw allow 48123/tcp && sudo ufw allow 5353/udp
# firewalld
sudo firewall-cmd --permanent --add-port=48123/tcp --add-service=mdns && sudo firewall-cmd --reload
```

## Troubleshooting

**The iPad doesn't find the PC**
- Run `avahi-browse -rt _anduri._tcp` on the PC. If the agent isn't listed, check the agent's log for the discovery line and whether `avahi-daemon` is running (`systemctl status avahi-daemon`).
- Open the firewall ports above.
- The iPad must be in the same subnet. mDNS doesn't cross routers or VLANs, and guest Wi-Fi networks often isolate clients.
- The app needs the Local Network permission (iPad Settings → Privacy & Security → Local Network).
- As a fallback, add the PC by IP address and port 48123.

**The iPad says the certificate changed / refuses to connect after pairing**
The agent's certificate is different from the one pinned during pairing: the state directory was deleted or moved (e.g. running without `--state-dir` after using one, or as another user), or `cert regenerate` was run. Pair again. Compare `anduri-agent cert show` with the fingerprint in the app.

**The app says "update agent"**
The app needs agent 1.1.0 or later. Older agents also advertise the retired `_pcvitals._tcp` name, so the iPad doesn't find them on the network. [Update the agent](#install-on-linux-as-a-systemd-service); the pairing is kept.

**`unauthorized` / the app asks to pair again**
The device was revoked, or `devices.json` was reset. Pair again.

**Pairing says `busy`**
Another pairing is in progress. It ends after 2 minutes, after 5 wrong codes, or when that device disconnects.

**`cpu.power` is missing**
RAPL needs root or the udev rule above. The log says "CPU package power (RAPL energy_uj) is only readable by root".

**No GPU sensors**
Check `nvidia-smi`. The agent needs `libnvidia-ml.so.1` from the proprietary driver. The log says "Sensor source nvidia isn't available: …".

**No GPU hotspot**
The log says either "GPU hotspot temperature for … comes from NvAPI (…)" or "No GPU hotspot temperature for …: <reason>". NvAPI needs `libnvidia-api.so.1`, which some distribution packages split out of the main driver package (e.g. `libnvidia-api1` or `nvidia-utils`). Please report the reason line if CoolerControl or LACT show a hotspot and the agent doesn't.

**No Aquacomputer or fan sensors**
Check `sensors` (lm-sensors). If your device isn't listed, load the driver (`sudo modprobe aquacomputer_d5next`, `nct6775`, `it87`). For an Octo or Quadro, map the right "Sensor n" input to `loop.coolant` in the config.

**CoolerControl sensors are missing**
The log says why: not reachable (`coolercontrold` not running or a different port) or authentication required. Create an access token in CoolerControl under Access Protection and set `coolerControl.token`.

**Settings in the config file have no effect**
Check the startup log (`journalctl -u anduri-agent -b | grep -i config`). The service only reads `/etc/anduri-agent/config.json`; a file in `~/.config/anduri-agent/` is only used when you start the agent yourself. Restart the service after editing the file.

**More detail**
Run with `--verbose`, or `journalctl -u anduri-agent -e` for the service.

## Development

```
anduri-agent/
  Anduri.Agent.slnx
  Directory.Build.props             net10.0, nullable, warnings as errors
  deploy/                           systemd unit, example config
  docs/                             the wire protocol, its sample messages and pairing vectors
  src/Anduri.Agent/
    Cli/                            command line
    Configuration/                  config file model and loader
    Discovery/                      avahi / dns-sd subprocess, managed mDNS responder, DNS wire format
    Pairing/                        proofs, codes, tokens, the pairing window
    Protocol/                       message records, source-generated JSON
    Sensors/                        ISensorSource, sampler, history buffer, hub
      Linux/                        procfs/sysfs sources (CPU, RAPL, memory, hwmon, Aquacomputer, drives, network)
      Nvidia/                       NVML through runtime-bound function pointers
      CoolerControl/                coolercontrold client and liquidctl fallback
      Simulated/                    the simulated rig
    Server/                         Kestrel host, per-connection session state machine
    State/                          state directory, agent id, certificate, paired devices
  tests/Anduri.Agent.Tests/         xUnit: protocol samples, pairing vectors, parsers on fixture trees, end-to-end on loopback
```

Runtime dependencies are only the .NET 10 shared frameworks (ASP.NET Core for Kestrel and WebSockets). Tests use xUnit and `Microsoft.Extensions.TimeProvider.Testing`.

**Adding a sensor source** (e.g. LibreHardwareMonitor on Windows): implement `ISensorSource` (`Describe()` and `ReadAsync()`), throw `SensorSourceUnavailableException` when the hardware or library isn't there, and add it in `SensorSourceFactory`. Sources are polled every 0.5 s (or their `PollInterval`), never concurrently with themselves. Every Linux source takes a `HostPaths` root so tests can run it against a fixture tree.

**Behaviour details beyond the protocol text**
- Every `error` closes the connection with the code from the protocol's table; `bad_request` covers malformed JSON, binary frames, frames over 64 KB, missing required fields, and known messages in the wrong state (e.g. `setInterval` before `hello`, `pair.confirm` without `pair.request`, a second `hello`). Unknown message types are ignored.
- `busy` is sent as `pair.rejected` with reason `busy`, then the connection closes with 4409. `too_many_attempts` is `pair.rejected` and close 4429. `expired` leaves the connection open for a new `pair.request`.
- After a `pair.request`, the 10-second hello timeout is replaced by the window (120 s) plus 60 s. After `pair.accepted` the client has 10 seconds to send `hello`.
- The first `snapshot` is sent right after `catalog`/`history`, then one per interval. Values are rounded to two decimals.
- `history` series contain only sensors in the current catalog that have at least one sample in the window.
