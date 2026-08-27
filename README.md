# HrPayroll Bridge Agent

A lightweight Windows background service that connects a branch's biometric attendance terminal to the HR & Payroll backend. Runs unattended on a branch PC or laptop, reads clock-in/clock-out punches off the terminal, queues them locally, and syncs them to the central system — so the Super Admin dashboard reflects who's checked in and who hasn't, in near real time, even if the branch's internet connection is unreliable.

Companion project to the [HrPayroll backend](../hrpayroll) — see that repo for the API this agent talks to.

---

## Tech Stack

| Layer | Technology |
|---|---|
| Language | C# |
| Runtime | .NET 10 |
| Hosting | Generic Host / `BackgroundService`, packaged as a Windows Service |
| Local storage | SQLite (`Microsoft.Data.Sqlite`) |
| HTTP | `IHttpClientFactory` (`Microsoft.Extensions.Http`) |
| Terminal hardware | ZKTeco standalone terminal, TCP/IP protocol *(in progress — see Roadmap)* |

---

## Why this exists

Employees clock in and out at a standalone biometric terminal at each branch. The terminal does its own on-device fingerprint capture and matching; this agent's job is narrower and more mechanical:

1. Read raw punch events off the terminal
2. Translate the terminal's internal user id into a real employee id (via a mapping the backend already knows about)
3. Queue the event locally so a dropped connection never loses an attendance record
4. Sync queued events to the backend, retrying automatically until it succeeds

Terminal-side matching and enrollment are the hardware's job. This agent never touches raw fingerprint data — only punch events and a small id-mapping cache.

---

## Architecture

```
Terminal (ZKTeco, or Mock during development)
        │  raw punch events
        ▼
ITerminalAdapter          ← the seam: MockTerminalAdapter today, ZkTecoTerminalAdapter later
        │
        ▼
Worker (BackgroundService)
        │
        ├──────────────► OfflineQueueStore (SQLite)   — durable local queue, survives crashes/restarts
        │
        ▼
BackendApiClient           — device-token authenticated HTTP client
        │
        ▼
HrPayroll backend  →  POST /attendance/biometric/sync
                       GET  /attendance/biometric/mappings
```

**The `ITerminalAdapter` interface is the key design decision in this project.** Nothing downstream — the queue, the sync client, the retry logic — knows or cares whether punches came from a real terminal or a simulated one. This let the entire agent be built and tested end-to-end against a `MockTerminalAdapter` before any physical hardware was available. Swapping in the real ZKTeco protocol later only requires a new class implementing the same interface; nothing else in the app changes.

---

## Project Structure

```
HrPayroll.BridgeAgent/
├── Program.cs                  Host builder, DI registration, entry point
├── Worker.cs                   Main loop: poll terminal → enqueue → sync pending
├── appsettings.json            Backend URL, device credentials, poll/sync intervals
│
├── Terminal/
│   ├── ITerminalAdapter.cs     Interface + TerminalEvent / TerminalMapping records
│   └── MockTerminalAdapter.cs  Simulated terminal — generates alternating CLOCK_IN/CLOCK_OUT punches
│                                (ZkTecoTerminalAdapter.cs to follow — see Roadmap)
│
├── OfflineQueue/
│   ├── QueuedEvent.cs           Local queue row model
│   └── OfflineQueueStore.cs     SQLite-backed durable queue (enqueue, get pending, mark synced/failed)
│
├── Sync/
│   └── BackendApiClient.cs      Typed HTTP client for /attendance/biometric/sync and /mappings
│
└── Config/
    └── AgentSettings.cs         Strongly-typed binding for the "Agent" appsettings.json section
```

---

## How it works

On each cycle (interval configurable via `appsettings.json`):

1. **Poll** — `ITerminalAdapter.PollEventsAsync()` returns any new punches since the last check.
2. **Enqueue** — every event is written to the local SQLite queue immediately, keyed by a unique `EventId`. A duplicate `EventId` is silently ignored (`INSERT OR IGNORE`) — this is the agent-side half of the sync's idempotency guarantee; the backend's own `findByEventId` check on `/attendance/biometric/sync` is the other half. Between the two, a retried event is never double-counted, whether the duplication happens on this side or in transit.
3. **Sync** — every pending row in the queue is POSTed to the backend. On success, the row is deleted. On failure — network error, timeout, or a rejection from the backend — the row stays queued, its `Attempts` count increments, and it's retried on the next cycle. A backend outage or an offline branch laptop never loses an attendance event; punches simply accumulate locally until connectivity returns.

Terminal reads and backend syncs are deliberately decoupled through the queue: a slow or unreachable backend never blocks reading new punches off the terminal, and a terminal read failure never affects previously queued events waiting to sync.

---

## Configuration

`appsettings.json`:

```json
{
  "Agent": {
    "BackendBaseUrl": "http://localhost:8081",
    "DeviceSerial": "MOCK-DEVICE-001",
    "DeviceToken": "REPLACE_WITH_REAL_TOKEN_FROM_DEVICE_REGISTRATION",
    "PollIntervalSeconds": 15,
    "SyncIntervalSeconds": 10
  }
}
```

| Setting | Description |
|---|---|
| `BackendBaseUrl` | Base URL of the HrPayroll backend this agent syncs to |
| `DeviceSerial` | This device's serial, as registered via `POST /attendance/devices` |
| `DeviceToken` | Plaintext device token, shown exactly once at registration — the backend only ever stores its hash |
| `PollIntervalSeconds` / `SyncIntervalSeconds` | How often the worker checks the terminal / attempts to sync the queue |

`DeviceSerial` and `DeviceToken` are sent as `X-Device-Serial` / `X-Device-Token` headers on every request — this is how the backend's `DeviceAuthFilter` authenticates the agent, entirely separately from user JWT auth. There is currently one known gap: `Worker.cs` hardcodes the device serial used in outgoing sync requests rather than reading it from `AgentSettings` — tracked in Roadmap below.

### One-time backend setup, per device

Before this agent can sync anything successfully:

1. **Register the device** — `POST /attendance/devices` (ADMIN/SUPER_ADMIN JWT), body: `{ "branchId": <id>, "deviceSerial": "<serial>" }`. Copy the returned `plaintextToken` into `DeviceToken` above — it is never shown again.
2. **Map the terminal user** — `POST /attendance/enrollment` (ADMIN/HR_MANAGER/BRANCH_MANAGER JWT), body: `{ "employeeId": <id>, "deviceSerial": "<serial>", "terminalUserId": "<id the terminal assigns>" }`. Until this mapping exists, the backend rejects sync attempts for that terminal user with "No employee mapped to terminal user id...".

---

## Running locally

Requires the HrPayroll backend running and reachable at the URL in `appsettings.json` (`docker compose up -d` in the backend repo).

```bash
dotnet run
```

Or press F5 in Visual Studio. With `MockTerminalAdapter` active, the agent generates an alternating `CLOCK_IN`/`CLOCK_OUT` punch every poll cycle for a hardcoded terminal user, queues it, and attempts to sync — useful for exercising the full pipeline without any physical hardware.

---

## Roadmap

Rough order of what's left before this can run against real hardware in a branch:

- [ ] `ZkTecoTerminalAdapter` — real TCP/IP protocol implementation against a ZKTeco standalone terminal, replacing `MockTerminalAdapter` behind the same `ITerminalAdapter` interface
- [ ] Pull `DeviceSerial` from `AgentSettings` in `Worker.cs` instead of the current hardcoded value
- [ ] Pull terminal user → employee mappings from `GET /attendance/biometric/mappings` into a local cache, rather than assuming the terminal already resolved identity
- [ ] Move the SQLite database file from the install directory to a proper `ProgramData` path, so it survives application updates
- [ ] Retry backoff policy (currently retries every cycle indefinitely; needs a cap or backoff for permanently-failing events, e.g. an unmapped terminal user)
- [ ] Structured file logging (Serilog or similar) — there's no console once this runs as an installed service
- [ ] Package as a Windows Service (`sc create`, or an installer using WinSW) so it runs at boot without a logged-in session
- [ ] Config for multiple physical terminals per branch, if needed

---

## Author

**Dickson Moseti**
Full-Stack Developer

---

## License

This project is proprietary software.
