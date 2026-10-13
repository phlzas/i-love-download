# I Love Download ❤️

A Windows desktop download manager that downloads a **single file across multiple network interfaces at once** — splitting it into byte-range chunks, sending each chunk over a different link (Ethernet + Wi‑Fi + phone tethering), then reassembling it. Pause/resume across restarts and time-window scheduling on top.

> **Status: M2 done — M3 (concepts) in progress.** The walking skeleton runs, and the core concepts are landing one at a time: byte-range **chunking across multiple network interfaces**, **pause/resume across restarts**, and a **time-window scheduler** are in and verified — **3 of 7 concepts**. Ahead: PDF reporting, LLM scheduling, auth, caching, then M4 (runnable by a stranger) and M5 (submit). See the [Roadmap](#roadmap) and the overview/submission doc [`My 10x Solution - Omar Ahmed.md`](<My 10x Solution - Omar Ahmed.md>).

---

## What works today

- `POST /downloads { "url": "..." }` queues a job, persists it to SQLite, and downloads it on a background worker.
- `GET /downloads` / `GET /downloads/{id}` report live status + byte progress, including `connectionsUsed` and `interfacesUsed`.
- Range-capable files download as parallel **byte-range chunks**, each bound to a different network interface, then reassembled and verified.
- `POST /downloads/{id}/pause` / `/resume` stop and restart a download; a paused job and its partial chunks survive an app restart.
- `GET` / `PUT /schedule` manage a daily time window — jobs only run inside it, parking as `Scheduled` and auto-resuming when it opens.
- A job killed mid-download is re-queued and auto-resumed on restart.
- A minimal Tauri v2 window lists the queue and polls the API every second.

## Chunked, multi-interface engine

- `ChunkedDownloadEngine` probes the URL (HEAD, then a ranged GET fallback) for `Content-Length` + `Accept-Ranges: bytes`.
- When Range is supported and the length exceeds one chunk, `[0,len)` is split (default 4 MiB chunks, default 8 concurrent connections), the chunks download in parallel, and the parts are concatenated and verified against `Content-Length`.
- Each chunk connection is bound to a usable network interface via `SocketsHttpHandler.ConnectCallback` + `Socket.Bind`; `NetworkInterfaceSelector` enumerates interfaces and assigns them round-robin.
- Range-unsupported / unknown-length / single-chunk downloads fall back to `SingleConnectionDownloadEngine`.
- Tune via the `Engine` config section (`ChunkSizeBytes`, `MaxConnections`, `InterfaceAllowList`, `AddressFamily`).

## Pause / resume across restarts

- `POST /downloads/{id}/pause` sets the job to `Paused` and cancels its in-flight chunk connections; the half-written `.part` files are kept on disk.
- `POST /downloads/{id}/resume` re-queues it; the engine reuses completed parts and resumes partial ones with a `Range` request, so no byte is downloaded twice.
- On startup, `Downloading` jobs are re-queued (auto-resume) while `Paused` jobs stay paused. A job killed mid-download resumes to a byte-identical file.

## Time-window scheduling

- A single persisted daily window: the `Schedule` config seeds a `ScheduleSetting` row that can be changed at runtime.
- Semantics: `[start, end)` in **local time**; a `start` later than `end` wraps midnight; `start == end` means all day; **disabled means no gating**.
- A hosted `DownloadScheduler` ticks every second: when the window closes it parks running jobs as `Scheduled` (partial chunks kept) and when it opens it re-queues them; a job created outside the window starts as `Scheduled`.
- `GET /schedule` returns the window plus `isOpen` / `nextOpenAt`; `PUT /schedule { "enabled", "start", "end" }` updates it live.

## Verification

Each concept ships an end-to-end PowerShell verifier — it builds the solution, starts the API, drives it with `curl`, and asserts the results:

- `scripts/verify-m3.ps1` — chunking integrity, single-connection fallback, interface binding (16 checks).
- `scripts/verify-m4.ps1` — pause / resume / restart recovery, byte-exact resume (34 checks).
- `scripts/verify-m5.ps1` — time-window scheduling: park, auto-resume, mid-download window close (29 checks).

The M4 and M5 verifiers accept `$env:M4_RANGE_URL` to point at any Range-capable URL (for example a local server) when a public mirror is unreliable.

## Requirements

- **Windows**
- **.NET 9 SDK** — `dotnet --version` → `9.x`
- **Node.js 20+** and **npm** (for the desktop shell)
- **Rust (stable, MSVC toolchain)** + WebView2 runtime — needed to build the Tauri shell (Tauri CLI comes via npm)

## Run it

Two processes: the **engine/API** (does the work) and the **shell** (the window).

**1) Backend API** (from the repo root):
```powershell
dotnet run --project src/LoveDownload.Api
```
It listens on `http://127.0.0.1:5099`. Try it:
```powershell
# queue a download, then read its status
curl.exe -X POST http://127.0.0.1:5099/downloads -H "Content-Type: application/json" -d "{\"url\":\"https://raw.githubusercontent.com/torvalds/linux/master/README\"}"
curl.exe http://127.0.0.1:5099/downloads

# pause / resume a job (use an id from the list above)
curl.exe -X POST http://127.0.0.1:5099/downloads/<id>/pause
curl.exe -X POST http://127.0.0.1:5099/downloads/<id>/resume

# set a daily window (01:00–05:00) and read it back
curl.exe -X PUT  http://127.0.0.1:5099/schedule -H "Content-Type: application/json" -d "{\"enabled\":true,\"start\":\"01:00\",\"end\":\"05:00\"}"
curl.exe http://127.0.0.1:5099/schedule
```

**2) Desktop shell** (in another terminal):
```powershell
cd app
npm install
npm run tauri dev
```

Downloaded files land in `downloads/<job-id>/` and the SQLite database is `data/lovedownload.db` (both are git-ignored).

## API contract

Base URL `http://127.0.0.1:5099`. Errors are always `{ "error": "..." }`.

| Method | Path | Result |
|---|---|---|
| `POST` | `/downloads` | `201` with the created job · `400` if `url` is not an absolute http/https URL |
| `GET` | `/downloads` | `200` with an array of jobs (newest first) |
| `GET` | `/downloads/{id}` | `200` with one job · `404` if unknown |
| `POST` | `/downloads/{id}/pause` | `200` with the job (`Paused`) · `404` unknown · `409` if already `Completed`/`Failed` |
| `POST` | `/downloads/{id}/resume` | `200` with the job (`Queued`) · `404` unknown · `409` if not `Paused` |
| `GET` | `/schedule` | `200` with the window: `enabled`, `start`, `end`, `isOpen`, `nextOpenAt`, `now` |
| `PUT` | `/schedule` | `200` with the updated window · `400` if `start`/`end` are not `HH:mm` (00:00–23:59) |

A job looks like:
```json
{
  "id": "guid",
  "url": "https://...",
  "fileName": "README",
  "filePath": "downloads/<id>/README",
  "status": "Queued | Scheduled | Downloading | Paused | Completed | Failed",
  "bytesDownloaded": 6034,
  "totalBytes": 6034,
  "connectionsUsed": 3,
  "interfacesUsed": "Wi-Fi (192.168.1.7)",
  "error": null,
  "createdAt": "2026-10-13T16:57:35+00:00",
  "updatedAt": "2026-10-13T16:57:36+00:00"
}
```

## Project layout

```
LoveDownload.sln
src/
  LoveDownload.Api/       ASP.NET Core API + EF Core (SQLite) + background worker + scheduler
  LoveDownload.Engine/    the download engine (IDownloadEngine: ChunkedDownloadEngine + single-connection fallback)
app/                      Tauri v2 desktop shell (UI only)
scripts/                  end-to-end verifiers (verify-m3.ps1, verify-m4.ps1, verify-m5.ps1)
My 10x Solution - Omar Ahmed.md   overview / submission one-pager
```

The .NET side owns **all** program logic; the Tauri shell only renders and calls the API.

## Roadmap

Milestones follow the brief (one-pager §8): **M1 one-pager → M2 walking skeleton → M3 concepts, one at a time → M4 runnable by a stranger → M5 package & submit.**

| Milestone | Scope | Status |
|---|---|---|
| **M1** | One-pager: problem, who, 10x claim, the 7 concepts, non-goal | ✅ |
| **M2** | Walking skeleton: API + DB + worker + desktop window, one URL end-to-end to a file on disk | ✅ |
| **M3** | Concepts, one at a time — see the checklist below | 🚧 |
| **M4** | Runnable by a stranger: exact README steps, seed/demo data, a written 5-minute demo path | ⬜ |
| **M5** | Package & submit: finish the overview doc, tick the requirements, paste the public repo link | ⬜ |

**M3 — concept checklist** (one-pager §4 / §6):

| # | Concept | Where it lives | Status |
|---|---|---|---|
| 1 | API endpoints | `src/LoveDownload.Api/Controllers/` | ✅ |
| 2 | Database (jobs, schedules) | EF Core + SQLite — `Data/`, `Models/` | ✅ |
| 3 | Background / cron jobs (scheduler) | engine chunk workers + `Services/DownloadScheduler.cs` | ✅ |
| 4 | Reporting — PDF | *(planned)* `Reporting/PdfReportService.cs` | ⬜ |
| 5 | LLM scheduling | *(planned)* reuses the A17 LLM layer | ⬜ |
| 6 | Authentication | *(planned)* reuses the BE-03 auth | ⬜ |
| 7 | Caching (probe / speed) | *(planned)* `ProbeCache.cs` | ⬜ |

Core features: ① add URL → chunked parallel ✅ · ② bind chunks to interfaces, reassemble + verify ✅ · ③ pause / resume across restart ✅ · ④ schedule a start time / window ✅ · ⑤ live per-link dashboard + PDF report ⬜.

> *The git history labels each concept commit with its own number — `feat(m3)` chunking, `feat(m4)` pause/resume, `feat(m5)` scheduler — all of which sit inside the brief's **M3**. The verifier filenames (`scripts/verify-m3/m4/m5.ps1`) follow those internal labels.*

## Non-goals

No BitTorrent/magnet links, Windows-only (no macOS/Linux), no browser extension.

## Future ideas

Anything that appears mid-build and is out of scope is parked here: a delete-job endpoint, a full transfer-history view, Speedify-style link bonding (needs a cloud endpoint), and a bandwidth-cap / fair-share scheduler.
