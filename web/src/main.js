import { createInputController } from "./input.js";
import { drawHarborChart } from "./chart.js";

const ROSTER_PEOPLE = [
  {
    name: "Tomas Reyes",
    role: "Freight Clerk",
    desc: "Reyes runs cold-chain seals, customs manifests, and side jobs across the Calloway waterfront.",
  },
  {
    name: "Mara Vale",
    role: "Port Officer",
    desc: "Vale works patrol across the quays and bridge approaches.",
  },
  {
    name: "Niko Voss",
    role: "Rival Crew",
    desc: "Voss runs his crew between the yards and the market streets.",
  },
  {
    name: "Rook",
    role: "Courier",
    desc: "Rook takes the jobs that move between the port and the city.",
  },
  {
    name: "Amara Cole",
    role: "Dock Electrician",
    desc: "Cole keeps cranes and shore power running from the loading apron to the basin.",
  },
  {
    name: "Mateo Silva",
    role: "Quay Stevedore",
    desc: "Silva directs container lashings and heavy winch crews along the outer berth.",
  },
  {
    name: "Linnea Park",
    role: "Market Commuter",
    desc: "Park crosses the fish-pier arcade between the ferry terminal and the cold-storage stalls.",
  },
  {
    name: "Arthur Pendelton",
    role: "Customs Broker",
    desc: "Pendelton clears bonded manifests and tariff ledgers at the harbor exchange.",
  },
];

const el = (id) => document.getElementById(id);

const canvas = el("godot-canvas");
const bootScreen = el("boot-screen");
const bootStatus = el("boot-status");
const bootBar = el("boot-bar");
const bootDetail = el("boot-detail");

const menuScreen = el("menu-screen");
const menuEyebrow = el("menu-eyebrow");
const menuHeading = el("menu-heading");
const menuLead = el("menu-lead");
const btnPlay = el("btn-play");
const btnJobs = el("btn-jobs");
const btnMap = el("btn-map");
const btnRoster = el("btn-roster");
const btnControls = el("btn-controls");
const btnRestart = el("btn-restart");
const streamPill = el("stream-pill");

const hud = el("hud");
const hudOperation = el("hud-operation");
const hudObjective = el("hud-objective");
const hudDetail = el("hud-detail");
const hudWanted = el("hud-wanted");
const hudWeapon = el("hud-weapon");
const hudAmmo = el("hud-ammo");
const hudPrompt = el("hud-prompt");
const hudVitals = el("hud-vitals");
const barHealth = el("bar-health");
const barArmor = el("bar-armor");
const reticle = el("reticle");
const minimap = el("minimap");
const districtMap = el("district-map");

const jobToast = el("job-toast");
const toastKind = el("toast-kind");
const toastText = el("toast-text");

const jobsModal = el("jobs-modal");
const jobsCredits = el("jobs-credits");
const jobsList = el("jobs-list");
const jobTitle = el("job-title");
const jobBrief = el("job-brief");
const jobStats = el("job-stats");
const btnTrackJob = el("btn-track-job");
const btnJobsBack = el("btn-jobs-back");

const mapModal = el("map-modal");
const btnMapBack = el("btn-map-back");

const rosterModal = el("roster-modal");
const rosterDragZone = el("roster-drag-zone");
const rosterName = el("roster-name");
const rosterRole = el("roster-role");
const rosterDesc = el("roster-desc");
const btnRosterPrev = el("btn-roster-prev");
const btnRosterNext = el("btn-roster-next");
const btnRosterFrame = el("btn-roster-frame");
const btnRosterBack = el("btn-roster-back");

const controlsModal = el("controls-modal");
const btnControlsBack = el("btn-controls-back");

const resultModal = el("result-modal");
const resultTitle = el("result-title");
const resultDetail = el("result-detail");
const btnResultAgain = el("btn-result-again");

const baseUrl = new URL(import.meta.env.BASE_URL || "/", window.location.href).href;
window.__HARBOR_ASSET_ROOT = baseUrl;
window.__HARBOR_FRAME_JSON = "";
window.__HARBOR_ROSTER_JSON = "";
window.__HARBOR_TITLE_CAMERA = true;
window.__HARBOR_CONSUME_FRAME = () => window.__HARBOR_FRAME_JSON || "";

let simExports = null;
let mapData = null;
let latestFrame = null;
let screen = "boot"; // boot | title | play | pause | jobs | map | roster | controls | result
let returnScreen = "title";
let trackedJobId = null;
let selectedOfferId = null;
let shownJobRunId = -1;
let toastSeconds = 0;
let resultDismissedPhase = "";
let simTicks = 0;

let rosterIndex = 0;
let rosterFace = false;
let rosterYawDeg = -12;
let rosterDragging = false;
let rosterLastX = 0;

function setBootProgress(pct, statusText, detailText) {
  bootBar.style.width = `${Math.max(5, Math.min(100, pct))}%`;
  if (statusText) bootStatus.textContent = statusText;
  if (detailText) bootDetail.textContent = detailText;
}

const input = createInputController(canvas, {
  onToggleMap: () => {
    if (screen === "play") showScreen("map", "play");
    else if (screen === "map") showScreen(returnScreen);
  },
  onToggleJobs: () => {
    if (screen === "play") showScreen("jobs", "play");
    else if (screen === "jobs") showScreen(returnScreen);
  },
  onEscape: () => {
    if (screen === "play") showScreen("pause");
    else if (screen === "pause") showScreen("play");
    else if (["jobs", "map", "roster", "controls"].includes(screen)) showScreen(returnScreen);
  },
});

function showScreen(next, backTo = returnScreen) {
  if (["jobs", "map", "roster", "controls"].includes(next)) {
    returnScreen = backTo;
  }

  screen = next;
  for (const node of [
    bootScreen,
    menuScreen,
    jobsModal,
    mapModal,
    rosterModal,
    controlsModal,
    resultModal,
  ]) {
    node.classList.remove("visible");
  }

  const playing = next === "play";
  hud.classList.toggle("hidden", !playing);
  input.setActive(playing);
  window.__HARBOR_TITLE_CAMERA = next === "title";

  if (simExports) {
    simExports.SetPaused(!playing);
  }

  if (next === "roster") {
    syncRosterState();
    rosterModal.classList.add("visible");
  } else {
    window.__HARBOR_ROSTER_JSON = "";
  }

  if (next === "title" || next === "pause") {
    const isPause = next === "pause";
    menuEyebrow.textContent = isPause
      ? "PORT CALLOWAY  /  PAUSED"
      : "PORT CALLOWAY  /  WATERFRONT SANDBOX";
    menuHeading.textContent = isPause ? "PAUSED" : "PROJECT HARBOR";
    menuLead.textContent = isPause
      ? "Simulation is held at the current tick. Resume patrol, inspect contracts, or open the district chart."
      : "Step onto the Calloway quay as freight clerk Tomas Reyes. Browse waterfront jobs, drive the compact across streamed 64 m harbor blocks, or trigger Harbor Lockdown.";
    btnPlay.textContent = isPause ? "Resume" : "Enter Port Calloway";
    btnRestart.classList.toggle("hidden", !isPause);
    menuScreen.classList.add("visible");
  } else if (next === "jobs") {
    populateJobsBoard();
    jobsModal.classList.add("visible");
  } else if (next === "map") {
    drawHarborChart(districtMap, mapData, latestFrame, {
      entireDistrict: true,
      trackedJob: trackedJobId,
    });
    mapModal.classList.add("visible");
  } else if (next === "controls") {
    controlsModal.classList.add("visible");
  } else if (next === "result") {
    resultModal.classList.add("visible");
  } else if (next === "play") {
    canvas.focus();
  }
}

function syncRosterState() {
  const person = ROSTER_PEOPLE[rosterIndex];
  rosterName.textContent = person.name;
  rosterRole.textContent = person.role;
  rosterDesc.textContent = person.desc;
  btnRosterFrame.textContent = rosterFace ? "Full figure" : "Face detail";
  window.__HARBOR_ROSTER_JSON = JSON.stringify({
    active: true,
    index: rosterIndex,
    face: rosterFace,
    yawDeg: rosterYawDeg,
  });
}

function distMeters(a, b) {
  return Math.hypot(a[0] - b[0], a[2] - b[2]);
}

function populateJobsBoard() {
  if (!latestFrame) return;
  const jobs = latestFrame.jobs;
  const ppos = latestFrame.player.position;
  jobsCredits.textContent = `CREDITS  $${Number(jobs.credits || 0).toLocaleString()}`;
  jobsList.innerHTML = "";

  const selectEntry = (id, title, brief, stats) => {
    selectedOfferId = id;
    jobTitle.textContent = title;
    jobBrief.textContent = brief;
    jobStats.textContent = stats;
    btnTrackJob.textContent = id === trackedJobId ? "Return to tracked job" : "Track on map";
    for (const child of jobsList.querySelectorAll("button")) {
      child.classList.toggle("active-job", child.dataset.jobId === (id ?? "__lockdown__"));
    }
  };

  let matchedSelection = false;
  for (const offer of jobs.available || []) {
    const btn = document.createElement("button");
    btn.className = "btn";
    btn.dataset.jobId = offer.id;
    btn.textContent = offer.title;
    const d = Math.round(distMeters(offer.startPosition, ppos));
    const stats = `${offer.kind} · $${Number(offer.reward).toLocaleString()} · ${d} m away`;
    btn.addEventListener("click", () => selectEntry(offer.id, offer.title, offer.briefing, stats));
    jobsList.appendChild(btn);
    if (offer.id === trackedJobId) {
      matchedSelection = true;
      selectEntry(offer.id, offer.title, offer.briefing, stats);
    }
  }

  const ldBtn = document.createElement("button");
  ldBtn.className = "btn";
  ldBtn.dataset.jobId = "__lockdown__";
  ldBtn.textContent = "Harbor Lockdown";
  const ldDist = Math.round(distMeters(latestFrame.lockdown.startPosition, ppos));
  const ldBrief = "Clear the quay, hold for reinforcements, then reach the east gate for extraction.";
  const ldStats = `Combat · ${ldDist} m away`;
  ldBtn.addEventListener("click", () => selectEntry(null, "Harbor Lockdown", ldBrief, ldStats));
  jobsList.appendChild(ldBtn);

  if (!matchedSelection) {
    selectEntry(null, "Harbor Lockdown", ldBrief, ldStats);
  }
}

function updateHud(frame, dt) {
  const run = frame.lockdown;
  const jobs = frame.jobs;
  const active = jobs.active;
  const combat = frame.combat;

  if (run.phase === "Succeeded" || run.phase === "Failed") {
    if (resultDismissedPhase !== `${frame.runGeneration}:${run.phase}`) {
      resultTitle.textContent = run.phase === "Succeeded" ? "HARBOR CLEARED" : "LOCKDOWN FAILED";
      const best = frame.bestTime != null ? `  ·  BEST ${frame.bestTime.toFixed(1)}s` : "";
      resultDetail.textContent = `ELAPSED ${run.elapsed.toFixed(1)}s${best}`;
      showScreen("result");
      return;
    }
  } else {
    resultDismissedPhase = "";
  }

  const tracked = (jobs.available || []).find((o) => o.id === trackedJobId);
  const eligible = (jobs.available || []).find((o) => o.canStart);

  hudOperation.textContent = active
    ? "PORT CALLOWAY  /  ACTIVE JOB"
    : run.phase === "FreeRoam"
      ? "PORT CALLOWAY  /  HARBOR FRONT"
      : "HARBOR LOCKDOWN";

  if (active) {
    hudObjective.textContent = active.title;
    const timer = `${Math.ceil(active.remainingSeconds)}s`;
    const hostiles = active.remainingEnemies > 0 ? `  ·  ${active.remainingEnemies} HOSTILES` : "";
    hudDetail.textContent = `${active.objective.toUpperCase()}  (${active.stage}/${active.stageCount})  ·  ${timer}${hostiles}`;
  } else {
    hudObjective.textContent =
      run.phase === "FirstWave"
        ? "Clear the quay"
        : run.phase === "Reinforcements"
          ? "Hold the waterfront"
          : run.phase === "Extraction"
            ? "Reach extraction"
            : "Free roam";

    if (run.phase === "Reinforcements" && run.reinforcementSeconds > 0) {
      hudDetail.textContent = `REINFORCEMENTS IN ${Math.ceil(run.reinforcementSeconds)} s`;
    } else if (run.phase === "FirstWave" || run.phase === "Reinforcements") {
      hudDetail.textContent = `WAVE ${run.waveNumber}/${run.waveCount}     ${run.remainingEnemies} HOSTILE${run.remainingEnemies === 1 ? "" : "S"}`;
    } else if (run.phase === "Extraction") {
      hudDetail.textContent = "RETURN TO THE EAST GATE";
    } else if (tracked) {
      const dist = Math.round(distMeters(tracked.startPosition, frame.player.position));
      hudDetail.textContent = `TRACKING  /  ${tracked.title.toUpperCase()} (${dist} m)`;
    } else {
      hudDetail.textContent = "J  BROWSE CITY JOBS";
    }
  }

  let prompt = "";
  if (active?.canInteract) prompt = "E   Recover cargo";
  else if (!frame.isDriving && frame.canEnterVehicle) prompt = "E   Enter vehicle";
  else if (eligible) prompt = `E   Start ${eligible.title}`;
  else if (frame.isDriving) prompt = "E   Exit vehicle";
  else if (run.canStart) prompt = "E   Start Harbor Lockdown";

  hudPrompt.textContent = prompt;
  hudPrompt.classList.toggle("hidden", prompt.length === 0);

  const stars =
    frame.wantedLevel > 0
      ? `WANTED  ${"★".repeat(frame.wantedLevel)}${"☆".repeat(5 - frame.wantedLevel)}   /   `
      : "";
  const crimeSuffix = frame.crime?.lastAction ? `  ${frame.crime.lastAction}` : "";
  hudWanted.textContent = `${stars}CASH $${frame.crime?.cash ?? 0}${crimeSuffix}`;

  hudWeapon.textContent = frame.isDriving
    ? "CALLOWAY COMPACT"
    : (combat.weaponName || "UNARMED").toUpperCase();
  hudAmmo.textContent = frame.isDriving
    ? `${Math.round(Math.abs(frame.vehicle.speed) * 3.6)
        .toString()
        .padStart(2, "0")} km/h`
    : combat.isReloading
      ? "RELOADING"
      : `${String(combat.magazine).padStart(2, "0")}  /  ${String(combat.reserve).padStart(2, "0")}`;

  const hpPct = combat.maxHealth > 0 ? (combat.health / combat.maxHealth) * 100 : 0;
  const arPct = combat.maxArmor > 0 ? (combat.armor / combat.maxArmor) * 100 : 0;
  barHealth.style.width = `${Math.max(0, Math.min(100, hpPct))}%`;
  barArmor.style.width = `${Math.max(0, Math.min(100, arPct))}%`;
  hudVitals.textContent = `${String(combat.health).padStart(2, "0")} HEALTH          ${String(combat.armor).padStart(2, "0")} ARMOR`;

  reticle.classList.toggle("hidden", frame.isDriving);
  reticle.classList.toggle("aiming", Boolean(frame.camera.aiming));
  reticle.classList.toggle("target", Boolean(frame.camera.aimHasTarget));

  if (jobs.lastResult && jobs.lastResult.runId !== shownJobRunId) {
    shownJobRunId = jobs.lastResult.runId;
    toastKind.textContent =
      jobs.lastResult.status === "Succeeded" ? "CONTRACT COMPLETE" : "CONTRACT FAILED";
    toastText.textContent =
      jobs.lastResult.status === "Succeeded"
        ? `${jobs.lastResult.title}  ·  +$${Number(jobs.lastResult.reward).toLocaleString()}`
        : `${jobs.lastResult.title}  ·  ${jobs.lastResult.reason}`;
    toastSeconds = 4.2;
  }
  if (toastSeconds > 0) {
    toastSeconds = Math.max(0, toastSeconds - dt);
    jobToast.classList.toggle("hidden", toastSeconds <= 0);
  }

  drawHarborChart(minimap, mapData, frame, {
    entireDistrict: false,
    trackedJob: trackedJobId,
  });
}

async function bootDotnetRuntime() {
  const dotnetUrl = new URL("runtime/_framework/dotnet.js", baseUrl).href;
  const { dotnet } = await import(/* @vite-ignore */ dotnetUrl);
  const runtime = await dotnet
    .withDiagnosticTracing(false)
    .withApplicationArgumentsFromQuery()
    .create();
  const config = runtime.getConfig();
  const exports = await runtime.getAssemblyExports(config.mainAssemblyName);
  return exports.Harbor.Browser.BrowserHarbor;
}

function loadScriptTag(src) {
  return new Promise((resolve, reject) => {
    const script = document.createElement("script");
    script.src = src;
    script.async = true;
    script.onload = () => resolve();
    script.onerror = () => reject(new Error(`Failed to load script: ${src}`));
    document.head.appendChild(script);
  });
}

async function bootGodotEngine() {
  const manifestRes = await fetch(new URL("godot/manifest.json", baseUrl).href);
  if (!manifestRes.ok) {
    throw new Error(`Failed to load godot/manifest.json (${manifestRes.status})`);
  }
  const manifest = await manifestRes.json();
  await loadScriptTag(new URL(manifest.script, baseUrl).href);

  if (typeof window.Engine !== "function") {
    throw new Error("Godot Engine global was not registered.");
  }

  const engine = new window.Engine();
  const exeUrl = new URL(manifest.executable, baseUrl).pathname;
  const packUrl = new URL(manifest.mainPack, baseUrl).pathname;

  await engine.startGame({
    canvas,
    executable: exeUrl,
    mainPack: packUrl,
    canvasResizePolicy: 2,
    focusCanvas: false,
    onProgress: (current, total) => {
      if (total > 0) {
        const pct = 28 + Math.round((current / total) * 66);
        const mb = (current / 1048576).toFixed(1);
        const totalMb = (total / 1048576).toFixed(1);
        setBootProgress(pct, "Streaming Port Calloway WebGL2 assets...", `${mb} / ${totalMb} MB`);
      }
    },
  });
}

function bindUiActions() {
  btnPlay.addEventListener("click", () => {
    input.requestLock();
    showScreen("play");
  });
  btnJobs.addEventListener("click", () => showScreen("jobs", screen));
  btnMap.addEventListener("click", () => showScreen("map", screen));
  btnRoster.addEventListener("click", () => showScreen("roster", screen));
  btnControls.addEventListener("click", () => showScreen("controls", screen));
  btnRestart.addEventListener("click", () => {
    simExports?.NewRun();
    input.resetView();
    input.requestLock();
    showScreen("play");
  });

  btnTrackJob.addEventListener("click", () => {
    trackedJobId = selectedOfferId;
    showScreen(returnScreen);
  });
  btnJobsBack.addEventListener("click", () => showScreen(returnScreen));
  btnMapBack.addEventListener("click", () => showScreen(returnScreen));
  btnControlsBack.addEventListener("click", () => showScreen(returnScreen));

  btnRosterPrev.addEventListener("click", () => {
    rosterIndex = (rosterIndex - 1 + ROSTER_PEOPLE.length) % ROSTER_PEOPLE.length;
    syncRosterState();
  });
  btnRosterNext.addEventListener("click", () => {
    rosterIndex = (rosterIndex + 1) % ROSTER_PEOPLE.length;
    syncRosterState();
  });
  btnRosterFrame.addEventListener("click", () => {
    rosterFace = !rosterFace;
    syncRosterState();
  });
  btnRosterBack.addEventListener("click", () => showScreen(returnScreen));

  rosterDragZone.addEventListener("pointerdown", (e) => {
    rosterDragging = true;
    rosterLastX = e.clientX;
    rosterDragZone.setPointerCapture?.(e.pointerId);
  });
  rosterDragZone.addEventListener("pointermove", (e) => {
    if (!rosterDragging) return;
    const dx = e.clientX - rosterLastX;
    rosterLastX = e.clientX;
    rosterYawDeg += dx * 0.55;
    syncRosterState();
  });
  rosterDragZone.addEventListener("pointerup", () => {
    rosterDragging = false;
  });

  btnResultAgain.addEventListener("click", () => {
    if (latestFrame) {
      resultDismissedPhase = `${latestFrame.runGeneration}:${latestFrame.lockdown.phase}`;
    }
    simExports?.RestartLockdown();
    input.requestLock();
    showScreen("play");
  });
}

async function boot() {
  bindUiActions();
  setBootProgress(12, "Booting Harbor.Core (.NET 8 WebAssembly)...", "Loading simulation authority...");

  try {
    const [dotnetApi] = await Promise.all([
      bootDotnetRuntime().then((api) => {
        setBootProgress(26, "Simulation authority ready.", "Starting Godot 4.4.1 WebGL2...");
        return api;
      }),
      bootGodotEngine(),
    ]);

    simExports = dotnetApi;
    const initialJson = simExports.Start();
    window.__HARBOR_FRAME_JSON = initialJson;
    latestFrame = JSON.parse(initialJson);
    mapData = JSON.parse(simExports.ReadMapData());
    if (latestFrame?.jobs?.Result?.runId != null) {
      shownJobRunId = latestFrame.jobs.Result.runId;
    } else if (latestFrame?.jobs?.lastResult?.runId != null) {
      shownJobRunId = latestFrame.jobs.lastResult.runId;
    }

    setBootProgress(100, "Port Calloway ready.", "Entering harbor overview...");
    showScreen("title");

    let prevNow = performance.now();
    const tickFrame = (now) => {
      const dt = Math.min(0.1, Math.max(0.001, (now - prevNow) / 1000));
      prevNow = now;

      const sampled = input.sample(
        dt,
        Boolean(latestFrame?.isDriving),
        latestFrame?.vehicle?.yaw || 0,
        latestFrame?.vehicle?.speed || 0
      );

      const frameJson = simExports.Frame(
        dt,
        sampled.right,
        sampled.forward,
        sampled.yaw,
        sampled.pitch,
        sampled.held,
        sampled.pulses,
        sampled.weaponSlot,
        sampled.dancing
      );
      window.__HARBOR_FRAME_JSON = frameJson;
      latestFrame = JSON.parse(frameJson);
      simTicks += 1;

      const stream = window.__HARBOR_STREAM_STATUS;
      if (stream && streamPill) {
        streamPill.textContent = stream.manifestReady
          ? `64m Harbor Chunks: ${stream.loadedCount} resident / ${stream.totalAvailable} district cells`
          : "Indexing 64m harbor chunk grid...";
      }

      if (screen === "play") {
        updateHud(latestFrame, dt);
      } else if (screen === "map") {
        drawHarborChart(districtMap, mapData, latestFrame, {
          entireDistrict: true,
          trackedJob: trackedJobId,
        });
      }

      window.__HARBOR_DEBUG = {
        screen,
        simTicks,
        godotReady: Boolean(window.__HARBOR_GODOT_READY),
        stream: window.__HARBOR_STREAM_STATUS || null,
        player: latestFrame.player,
        vehicle: latestFrame.vehicle,
        lockdown: latestFrame.lockdown,
        jobsCount: latestFrame.jobs?.available?.length || 0,
        pedestrianCount: latestFrame.ambient?.pedestrians?.length || 0,
        trafficCount: latestFrame.ambient?.traffic?.length || 0,
      };

      requestAnimationFrame(tickFrame);
    };
    requestAnimationFrame(tickFrame);
  } catch (err) {
    console.error("[harbor-web] Boot failed:", err);
    bootStatus.textContent = "Failed to initialize Project Harbor Web build";
    bootDetail.textContent = String(err?.message || err);
  }
}

boot();
