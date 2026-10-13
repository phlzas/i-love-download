# I Love Download ❤️

A Windows desktop download manager that downloads a **single file across multiple network interfaces at once** — splitting it into byte-range chunks, sending each chunk over a different link (Ethernet + Wi‑Fi + phone tethering), then reassembling it. Pause/resume across restarts and time-window scheduling on top.

> **Status: M2 — walking skeleton.** One URL → one job row in the DB → a file on disk, driven from a Tauri desktop window. Real multi-interface chunking is **M3** (in progress). See [Roadmap](#roadmap).

---

## What works today (M2)

- `POST /downloads { "url": "..." }` queues a job, persists it to SQLite, and downloads it on a background worker (single connection for now).
- `GET /downloads` / `GET /downloads/{id}` report live status + byte progress.
- Unfinished jobs are re-queued automatically on restart.
- A minimal Tauri v2 window lists the queue and polls the API every second.

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
| **M3** 🚧 | Byte-range chunking + the multi-interface scheduler (the core idea), resume, scheduling, reporting, LLM, auth, caching — one concept at a time |
| **M4** | Runnable by a stranger (single command), packaged |
| **M5** | Submit (public repo + overview doc) |

## Non-goals

No BitTorrent/magnet links, Windows-only (no macOS/Linux), no browser extension.
