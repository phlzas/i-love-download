# My 10x Solution — Omar Ahmed

> **Milestone M1 — the one-pager.** One page that protects three weeks: the problem,
> who has it, the 10x claim, the 5+ concepts, and one non-goal. Nothing here is wasted —
> this page becomes the capstone submission document later.
>
> **Confirm:** the filename must be `My 10x Solution - {Your Name and Surname}` — swap in
> your exact name/surname if `Omar Ahmed` is not how it should read.

| | |
|---|---|
| **Capstone** | **I Love Download** ❤️ — a Windows download manager that pulls one file through every network link you own, at the same time |
| **Track** | FlyRank Backend Development — *Your 10x Solution* (own idea) |
| **Repo** | *(new public repo, to be created)* `i-love-download` |
| **Stack** | .NET 9 engine + local API · SQLite · Tauri desktop shell · all $0 |

---

## 1. The problem

A normal computer has several ways onto the internet — Ethernet, Wi-Fi, a tethered phone — but
the operating system picks **one** and sends every byte through it, so the other links sit
completely idle. Large downloads therefore crawl at single-link speed while spare capacity
goes unused. And when the download has to stop — the laptop sleeps, you need the bandwidth,
or a data cap applies — most tools either stall or restart from zero and lose everything
already fetched.

## 2. Who has this problem

Anyone who downloads big files on a machine with more than one network path:

- People whose **primary link is slow or capped**, but who also have a second path (hotspot,
  tethering, a neighbour's Ethernet-to-a-router setup, an old phone on Wi-Fi).
- People who must **fit a download into a window** — "run it after midnight on the free tier",
  "stop at 7 a.m. when the house wakes up".
- People whose **connection drops mid-download** and who then lose all progress.
- Themselves, every time they babysit a big download overnight.

## 3. The 10x claim

**One sentence:** downloading a large file across *every* live network link at once — scheduled
inside a time window and resumable across restarts without losing a byte — becomes a single
configured dashboard job instead of an all-night manual babysitting task.

**Planned measurement (the honest number):** same 1 GB file, one link vs. all links, report
elapsed time and % of combined peak — so the 10x is a measured before/after, not a slogan.

## 4. The 5+ concepts

Seven concepts from the program, **zero swaps** (table 1 only). At least 3 from table 1 is
required; this uses all of them.

| # | Concept | What it does here | Where it lives in the code |
|---|---|---|---|
| 1 | **API endpoints** | Local REST API to create / list / pause / resume / delete download jobs, with real validation and correct status codes | `src/LoveDownload.Api/Controllers/DownloadsController.cs` |
| 2 | **Database** | Persist jobs, per-segment byte ranges + status, known interfaces, schedules, transfer history — so a restart resumes exactly where it stopped | EF Core + SQLite: `Data/`, `Models/DownloadJob.cs`, `Models/Segment.cs` |
| 3 | **Background / cron jobs** | Scheduler service starts a job at a time, stops it after a duration / at a window end, and auto-resumes; segment workers run off the request path | `src/LoveDownload.Engine/DownloadScheduler.cs` (hosted service) |
| 4 | **Reporting — PDF** | Per-job PDF: bytes transferred, average speed per link, elapsed time, time saved vs. single-link | `Engine/Reporting/PdfReportService.cs` (QuestPDF) |
| 5 | **LLM integration** | Natural-language scheduling — *"download tonight, prefer Ethernet, finish by 7 a.m."* → validated schedule JSON, with a cost log | `Llm/ScheduleParser.cs` (reuses Week 7 / A17 LLM layer) |
| 6 | **Authentication** | Local login; the download API routes are actually protected | reuses BE-03 auth (ASP.NET Core Identity + JWT) |
| + | **Caching** | Cache URL probes (size / ETag / `Accept-Ranges`) and measured interface speeds so a resume or repeat job skips re-probing | `Engine/ProbeCache.cs` |

## 5. Non-goal (one explicit thing I will NOT build)

**No BitTorrent / magnet support, no macOS or Linux, no browser extension.** Windows only,
direct HTTP(S) downloads only. (Sketched in a line: I will *not* try to be Plexo.)

## 6. Core feature list (scope guard — 5 features, ~3 weeks part-time)

1. **Add a URL** → the engine splits it into chunks and downloads them in parallel.
2. **Bind chunks to chosen network interfaces** (per-connection source-IP bind) → reassemble and verify the file.
3. **Pause / resume that survives an app restart** (state in the database).
4. **Schedule** a job: start time + stop window / duration.
5. **Dashboard** with live per-link speed + a **PDF report** per job.

> Anything else that appears mid-build → written down under "Future ideas" in the README, then ignored.

## 7. Stack (proposed) — and one decision to confirm

- **Engine + local HTTP API = .NET 9 (ASP.NET Core) + EF Core/SQLite.** All five concepts live
  here, in the language the program taught, reusing BE-03 (auth) and A17 (LLM).
- **Desktop shell = Tauri** (HTML/CSS/JS front end, animated progress + live speed chart),
  calling the local API. The window is the only thing Tauri owns.
- **Reporting:** QuestPDF. **LLM:** local LM Studio (free, as in A17).

> **Open decision:** the brief only needs "a plain page or a clean API", and asks that a stranger
> can start it with 1–2 commands. Two options:
> **(A)** Tauri shell + .NET engine (prettiest; needs the Rust toolchain + bundling the .NET API
> as a sidecar). **(B)** serve the same web UI straight from the .NET API (one command to run,
> still shiny, no Rust). Pick A or B before M2.

## 8. Milestones (from the brief)

- **M1 — one-pager** ← *this document* ✅
- **M2 — walking skeleton:** one URL → chunks → database row → a completed file on disk (ugly, but it runs).
- **M3 — concepts, one at a time:** add the remaining concepts one by one; each runs and is committed before the next starts.
- **M4 — runnable by a stranger:** exact README steps, seed/demo data, a written 5-minute demo path.
- **M5 — package & submit:** finish this doc, tick the Requirements boxes, paste the public repo link.

## 9. Prior art & how it actually works (be honest about it)

This is **not a new idea** — the capstone is not judged on novelty, so this section exists to make
sure the doc never over-claims. Verified before building:

- **aria2** ships `--multiple-interface` ("Requests will be split among the interfaces to achieve
  link aggregation"), but it works by **binding the socket to a chosen interface's source address** —
  so it needs two default gateways to actually use both links, and its own maintainer says the naive
  "double bandwidth" expectation is wrong ([issue #867](https://github.com/aria2/aria2/issues/867)).
- **HypoMux** (Windows) is the nearest prior art: multi-NIC aggregation via `IP_UNICAST_IF` +
  source-address binding behind a local proxy — and it says plainly it does **not** split a single
  TCP connection across paths.
- **IDM / FDM / XDM / Motrix** are multi-*connection* (many sockets to one server) but single NIC.
  **wget** (`--bind-address`) and **curl** (`--interface`) bind one interface per process and cannot
  split one file. **Speedify / Peplink** bond links, but only with a cloud/peer endpoint.
- **Plexo** already ships a polished version of this exact product.

**The honest engineering truth:** a single TCP connection cannot be split locally; the gain comes
from opening *many* connections and spreading them across links — and it caps out if both links
share one upstream ISP. So the real value here is the **scheduler, per-link byte-range assignment,
reassembly and robust resume implemented in .NET** — not a magic speed multiplier.
