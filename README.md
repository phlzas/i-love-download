# I Love Download ❤️

A Windows desktop download manager that downloads a **single file across multiple network interfaces at once** — splitting it into byte-range chunks, sending each chunk over a different link (Ethernet + Wi‑Fi + phone tethering), then reassembling it. Pause/resume across restarts and time-window scheduling on top.

> **Status: M3 — chunked engine.** One URL → one job row in the DB → a file on disk. The engine now probes for `Accept-Ranges`, splits Range-capable downloads into byte-range chunks sent in parallel over selected network interfaces, and reassembles them; anything else falls back to the single-connection path. See [Roadmap](#roadmap).

---

## What works today (M2)

- `POST /downloads { "url": "..." }` queues a job, persists it to SQLite, and downloads it on a background worker (single connection for now).
- `GET /downloads` / `GET /downloads/{id}` report live status + byte progress.
- Unfinished jobs are re-queued automatically on restart.
- A minimal Tauri v2 window lists the queue and polls the API every second.

## What M3 adds

- `ChunkedDownloadEngine` probes the URL (HEAD, then a ranged GET fallback) for `Content-Length` + `Accept-Ranges: bytes`.
- When Range is supported and the length exceeds one chunk, `[0,len)` is split (default 4 MiB chunks, default 8 concurrent connections), the chunks download in parallel, and the parts are concatenated and verified against `Content-Length`.
- Each chunk connection is bound to a usable network interface via `SocketsHttpHandler.ConnectCallback` + `Socket.Bind`; `NetworkInterfaceSelector` enumerates interfaces and assigns them round-robin.
- Range-unsupported / unknown-length / single-chunk downloads fall back to `SingleConnectionDownloadEngine`.
- Jobs expose `connectionsUsed` and `interfacesUsed`; tune via the `Engine` config section (`ChunkSizeBytes`, `MaxConnections`, `InterfaceAllowList`, `AddressFamily`).
- Run `scripts/verify-m3.ps1` for the M3 integrity / fallback / interface-binding checks.

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
curl.exe -X POST http://127.0.0.1:5099/downloads -H "Content-Type: application/json" -d "{\"url\":\"https://raw.githubusercontent.com/torvalds/linux/master/README\"}"
curl.exe http://127.0.0.1:5099/downloads
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

A job looks like:
```json
{
  "id": "guid",
  "url": "https://...",
  "fileName": "README",
  "status": "Queued | Downloading | Completed | Failed",
  "bytesDownloaded": 6034,
  "totalBytes": 6034,
  "error": null,
  "createdAt": "2026-10-13T16:57:35+00:00"
}
```

## Project layout

```
LoveDownload.sln
src/
  LoveDownload.Api/       ASP.NET Core API + EF Core (SQLite) + background worker
  LoveDownload.Engine/    the download engine (IDownloadEngine; single-connection today)
app/                      Tauri v2 desktop shell (UI only)
```

The .NET side owns **all** program logic; the Tauri shell only renders and calls the API.

## Roadmap

| Milestone | Scope |
|---|---|
| **M2** ✅ | Walking skeleton: API + DB + worker + Tauri window, one file end-to-end |
| **M3** 🚧 | Byte-range chunking + multi-interface binding (core engine done: probe, parallel ranged chunks, interface round-robin, integrity check, single-connection fallback); resume, scheduling, reporting, LLM, auth, caching still ahead |
| **M4** | Runnable by a stranger (single command), packaged |
| **M5** | Submit (public repo + overview doc) |

## Non-goals

No BitTorrent/magnet links, Windows-only (no macOS/Linux), no browser extension.
