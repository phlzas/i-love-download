const API_BASE = "http://127.0.0.1:5099";
const POLL_MS = 1000;
const REQUEST_TIMEOUT_MS = 4000;

type JobStatus = "Queued" | "Downloading" | "Completed" | "Failed";

interface Job {
  id: string;
  url: string;
  fileName: string | null;
  status: JobStatus;
  bytesDownloaded: number;
  totalBytes: number | null;
  error: string | null;
  createdAt: string;
}

interface RowRefs {
  root: HTMLElement;
  name: HTMLElement;
  url: HTMLElement;
  pill: HTMLElement;
  pillText: HTMLElement;
  bar: HTMLElement;
  fill: HTMLElement;
  meta: HTMLElement;
  error: HTMLElement;
}

function qs<T extends Element>(selector: string, scope: ParentNode = document): T {
  const el = scope.querySelector<T>(selector);
  if (!el) throw new Error(`Missing element: ${selector}`);
  return el;
}

const els = {
  form: qs<HTMLFormElement>("#composer"),
  url: qs<HTMLInputElement>("#url"),
  button: qs<HTMLButtonElement>("#download-btn"),
  list: qs<HTMLElement>("#job-list"),
  empty: qs<HTMLElement>("#empty"),
  count: qs<HTMLElement>("#queue-count"),
  banner: qs<HTMLElement>("#banner"),
  bannerText: qs<HTMLElement>("#banner-text"),
  conn: qs<HTMLElement>("#conn"),
  connLabel: qs<HTMLElement>("#conn-label"),
};

const rows = new Map<string, RowRefs>();
let polling = false;

async function apiFetch(path: string, init?: RequestInit): Promise<Response> {
  const controller = new AbortController();
  const timer = window.setTimeout(() => controller.abort(), REQUEST_TIMEOUT_MS);
  try {
    return await fetch(`${API_BASE}${path}`, { ...init, signal: controller.signal });
  } finally {
    window.clearTimeout(timer);
  }
}

function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return "0 B";
  const units = ["B", "KB", "MB", "GB", "TB"];
  const index = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
  const value = bytes / Math.pow(1024, index);
  const decimals = index === 0 || value >= 100 ? 0 : 1;
  return `${value.toFixed(decimals)} ${units[index]}`;
}

function showBanner(message: string): void {
  els.bannerText.textContent = message;
  els.banner.hidden = false;
}

function hideBanner(): void {
  els.banner.hidden = true;
}

function setConnection(state: "online" | "offline"): void {
  els.conn.dataset.state = state;
  els.connLabel.textContent = state === "online" ? "Connected" : "Offline";
}

function createRow(job: Job): RowRefs {
  const root = document.createElement("article");
  root.className = "job";
  root.dataset.id = job.id;

  root.innerHTML = `
    <div class="job-top">
      <div class="job-id">
        <span class="job-name"></span>
        <span class="job-url"></span>
      </div>
      <span class="pill"><span class="pill-dot"></span><span class="pill-text"></span></span>
    </div>
    <div class="bar"><div class="bar-fill"></div></div>
    <div class="job-foot">
      <span class="job-meta"></span>
      <span class="job-error"></span>
    </div>`;

  return {
    root,
    name: qs(".job-name", root),
    url: qs(".job-url", root),
    pill: qs(".pill", root),
    pillText: qs(".pill-text", root),
    bar: qs(".bar", root),
    fill: qs(".bar-fill", root),
    meta: qs(".job-meta", root),
    error: qs(".job-error", root),
  };
}

function updateRow(row: RowRefs, job: Job): void {
  const label = job.fileName && job.fileName.length > 0 ? job.fileName : job.url;
  row.name.textContent = label;
  row.name.title = job.url;
  row.url.textContent = job.fileName ? job.url : "";

  row.root.dataset.status = job.status;
  row.pillText.textContent = job.status;

  const done = job.bytesDownloaded;
  const total = job.totalBytes;
  const known = typeof total === "number" && total > 0;

  if (job.status === "Downloading" && !known) {
    row.bar.classList.add("is-indeterminate");
    row.fill.style.width = "";
    row.meta.textContent = `${formatBytes(done)} downloaded`;
  } else {
    row.bar.classList.remove("is-indeterminate");
    let percent = 0;
    if (job.status === "Completed") {
      percent = 100;
    } else if (known) {
      percent = Math.min(100, Math.max(0, (done / (total as number)) * 100));
    }
    row.fill.style.width = `${percent}%`;

    if (job.status === "Queued") {
      row.meta.textContent = "Waiting to start";
    } else if (known) {
      row.meta.textContent = `${formatBytes(done)} / ${formatBytes(total as number)} · ${Math.round(percent)}%`;
    } else {
      row.meta.textContent = formatBytes(done);
    }
  }

  row.error.textContent = job.status === "Failed" && job.error ? job.error : "";
}

function renderJobs(jobs: Job[]): void {
  const sorted = [...jobs].sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt));
  const seen = new Set<string>();

  for (const job of sorted) {
    seen.add(job.id);
    let row = rows.get(job.id);
    if (!row) {
      row = createRow(job);
      rows.set(job.id, row);
      els.list.appendChild(row.root);
    }
    updateRow(row, job);
    els.list.appendChild(row.root);
  }

  for (const [id, row] of rows) {
    if (!seen.has(id)) {
      row.root.remove();
      rows.delete(id);
    }
  }

  const hasJobs = sorted.length > 0;
  els.list.hidden = !hasJobs;
  els.empty.hidden = hasJobs;

  const active = sorted.filter((j) => j.status === "Queued" || j.status === "Downloading").length;
  if (!hasJobs) {
    els.count.textContent = "—";
  } else if (active > 0) {
    els.count.textContent = `${sorted.length} items · ${active} active`;
  } else {
    els.count.textContent = `${sorted.length} items`;
  }
}

async function readError(res: Response): Promise<string> {
  try {
    const body = (await res.json()) as { error?: string };
    if (body && typeof body.error === "string") return body.error;
  } catch {
    /* fall through */
  }
  return `HTTP ${res.status}`;
}

async function poll(): Promise<void> {
  if (polling) return;
  polling = true;
  try {
    const res = await apiFetch("/downloads");
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const jobs = (await res.json()) as Job[];
    setConnection("online");
    hideBanner();
    renderJobs(Array.isArray(jobs) ? jobs : []);
  } catch {
    setConnection("offline");
    showBanner(
      `Can't reach the API at ${API_BASE}. Start the backend and the queue will appear here.`,
    );
  } finally {
    polling = false;
  }
}

els.form.addEventListener("submit", async (event) => {
  event.preventDefault();
  const url = els.url.value.trim();
  if (!url) {
    els.url.focus();
    return;
  }

  els.button.disabled = true;
  els.button.classList.add("is-busy");

  try {
    const res = await apiFetch("/downloads", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ url }),
    });
    if (!res.ok) {
      showBanner(`Couldn't queue that link: ${await readError(res)}`);
    } else {
      els.url.value = "";
      hideBanner();
      await poll();
    }
  } catch {
    setConnection("offline");
    showBanner(`Can't reach the API at ${API_BASE}. Start the backend and try again.`);
  } finally {
    els.button.disabled = false;
    els.button.classList.remove("is-busy");
  }
});

void poll();
window.setInterval(() => void poll(), POLL_MS);
