import { createChart } from './chart.js';
import { createInput } from './input.js';
import { assetUrl, districtAt } from './map-data.js';
import './style.css';

const byId = (id) => document.getElementById(id);
const canvas = byId('world');
const intro = byId('intro');
const start = byId('start');
const status = byId('status');
const hud = byId('hud');
const menu = byId('menu');
const map = byId('map');
const touch = byId('touch');
const district = byId('district-name');
const wallet = byId('wallet');
const wanted = byId('wanted');
const stars = byId('stars');
const pursuit = byId('pursuit');
const crosshair = byId('crosshair');
const equipment = byId('equipment');
const ammunition = byId('ammunition');
const health = byId('health');
const armor = byId('armor');
const notice = byId('notice');
const vehicleAction = byId('vehicle-action');
const streetHint = byId('street-action');
const serviceHint = byId('service-action');
const crimeMessages = {
  pickpocketed: (amount) => '소매치기  +$' + amount.toLocaleString('ko-KR'),
  cargo_taken: (amount) => '화물 절도  +$' + amount.toLocaleString('ko-KR'),
  spotted: () => '들켰습니다. 주변에 신고가 접수됐습니다.',
  vehicle_taken: () => '차량 탈취가 신고됐습니다.',
  no_target: () => '가까운 대상이 없습니다.',
  exit_blocked: () => '차량을 멈추고 문 옆 공간을 확보하세요.',
  service_health: amount => '체력 회복 · -
};

let screen = 'intro';
let mapParent = 'play';
let api, frame, layout, chart;
let serializedFrame = "";
let frameVersion = 0;
let elapsed = 0;
let simulationTime = 0;
let crimeSequence = 0;
let noticeUntil = 0;
let audio;
let hadPointerLock = false;
let contextLost = false;
const input = createInput(canvas, touch, () => screen === 'play' && frame.alive, code => {
  if (!frame || screen === 'intro') return false;
  if (code === 'KeyM') {
    if (screen === 'map') setScreen(mapParent);
    else openMap();
  } else if (screen === 'map') setScreen(mapParent);
  else setScreen(screen === 'play' ? 'menu' : 'play');
  return true;
});

document.addEventListener('pointerlockchange', () => {
  const locked = document.pointerLockElement === canvas;
  if (hadPointerLock && !locked && screen === 'play') setScreen('menu');
  hadPointerLock = locked;
});

function setScreen(next) {
  screen = next;
  simulationTime = performance.now();
  intro.hidden = next !== 'intro';
  hud.hidden = next !== 'play';
  menu.hidden = next !== 'menu';
  map.hidden = next !== 'map';
  touch.hidden = next !== 'play' || !input.coarse.matches;
  if (next === 'play') {
    input.capture();
    audio?.resume();
  } else {
    input.release();
    audio?.pause();
  }
  if (next === 'menu') byId('resume').focus();
  if (next === 'map') {
    chart.atlas(frame.position, frame.state.isDriving ? frame.state.vehicle.yaw : input.direction().yaw);
    byId('close-map').focus();
  }
}

function openMap() {
  mapParent = screen === 'menu' ? 'menu' : 'play';
  setScreen('map');
}

function replaceFrame(raw) {
  frame = JSON.parse(raw);
  serializedFrame = raw;
  frameVersion++;
}

function reset() {
  replaceFrame(api.Start());
  input.reset();
  crimeSequence = 0;
  noticeUntil = 0;
  setScreen('play');
}

byId('pause-button').addEventListener('click', () => setScreen('menu'));
byId('map-button').addEventListener('click', openMap);
byId('resume').addEventListener('click', () => setScreen('play'));
byId('menu-map').addEventListener('click', openMap);
byId('close-map').addEventListener('click', () => setScreen(mapParent));
byId('reset').addEventListener('click', reset);
start.addEventListener('click', () => {
  if (!frame) return;
  audio ??= sound();
  setScreen('play');
});

function label(el, text) {
  if (el.textContent !== text) el.textContent = text;
}
function hint(el, text) {
  el.hidden = !text;
  label(el, text);
}

function updateHud() {
  const { state, alive } = frame;
  const { streetAction, streetCrime, playerCombat, maxWantedLevel, wantedLevel, policeUnits, isDriving, vehicle } = state;
  label(district, districtAt(layout, frame.position));
  label(wallet, '$' + (streetCrime?.dirtyCash ?? 0).toLocaleString('ko-KR'));
  wanted.hidden = !wantedLevel;
  if (wantedLevel) {
    label(stars, '★'.repeat(wantedLevel) + '☆'.repeat(Math.max(0, maxWantedLevel - wantedLevel)));
    stars.setAttribute('aria-label', '수배 ' + wantedLevel + '단계');
    label(pursuit, policeUnits ? '추격 중 · ' + policeUnits + '명' : '수배 중');
  }
  label(equipment, isDriving ? vehicle.displayName : playerCombat.weaponName);
  label(ammunition, isDriving ? Math.round(Math.abs(vehicle.speed) * 3.6) + ' km/h' :
    playerCombat.isReloading ? '재장전 중' :
    String(playerCombat.magazine).padStart(2, '0') + ' / ' + String(playerCombat.reserve).padStart(2, '0'));
  health.max = playerCombat.maxHealth;
  health.value = playerCombat.health;
  armor.max = playerCombat.maxArmor;
  armor.value = playerCombat.armor;
  crosshair.hidden = isDriving || !alive;
  crosshair.classList.toggle('aimed', frame.aiming);
  const prefix = input.coarse.matches ? '' : 'F · ';
  hint(vehicleAction, !alive ? '' : isDriving ? prefix + '하차' : streetAction?.vehicleId >= 0 ?
    prefix + (streetAction.vehicleOccupied ? '차량 탈취' : '탑승') : '');
  const streetLabel = streetAction?.cargoId ? '화물 훔치기' : streetAction?.canPickpocket ? '소매치기' : '';
  hint(streetHint, alive && !isDriving && streetLabel ? (input.coarse.matches ? '' : 'G · ') + streetLabel : '');
  const service = state.service;
  hint(serviceHint, alive && !isDriving && service?.id ? (input.coarse.matches ? '' : 'T · ') + service.label + ' 
    crimeSequence = streetCrime.sequence;
    label(notice, crimeMessages[streetCrime.status]?.(streetCrime.amount) ?? '');
    noticeUntil = elapsed + 3.4;
  }
  if (!alive) {
    noticeUntil = elapsed + .5;
    label(notice, '쓰러졌습니다. 항만에서 다시 시작합니다.');
  }
  notice.hidden = elapsed >= noticeUntil || !notice.textContent;
  chart.radar(state, frame.position, isDriving ? vehicle.yaw : input.direction().yaw, elapsed);
}

function handleShots(shots) {
  for (const shot of shots) {
    const distance = Math.hypot(frame.position.x - shot.origin.x, frame.position.z - shot.origin.z);
    audio?.shot(shot.weaponId, distance);
  }
}

// Advance the local authority on its own clock so slow GPU frames do not slow input or physics.
function simulate() {
  const now = performance.now();
  const dt = Math.min((now - simulationTime) / 1000, .12);
  simulationTime = now;
  if (screen !== 'play' || document.hidden || contextLost) return;
  elapsed += dt;
  const { right, forward, yaw, pitch, buttons, pulses, slot } = input.sample(dt, frame.state.isDriving);
  const next = api.Tick(dt, right, forward, yaw, pitch, buttons, pulses, slot);
  if (!next) return;
  replaceFrame(next);
  handleShots(frame.shots);
  updateHud();
}

function sound() {
  const create = (name, volume) => {
    const sound = new Audio(assetUrl('assets/audio/' + name + '.ogg'));
    sound.volume = volume;
    return sound;
  };
  const base = create('dock_ambience', .12);
  const shots = { wasp_smg: create('wasp', .25), kestrel_p10: create('kestrel', .25) };
  base.loop = true;
  return {
    resume: () => base.play().catch(() => {}),
    pause: () => base.pause(),
    shot(id, distance) {
      if (distance > 65) return;
      const shot = (shots[id] ?? shots.wasp_smg).cloneNode();
      shot.volume = .32 * (1 - distance / 72);
      shot.play().catch(() => {});
    },
  };
}

async function json(path) {
  const response = await fetch(assetUrl(path));
  if (!response.ok) throw new Error('Could not load ' + path + ' (' + response.status + ')');
  return response.json();
}

function prefersLowDetail() {
  const probe = document.createElement('canvas');
  const gl = probe.getContext('webgl2');
  if (!gl) throw new Error('WebGL 2 is not available');
  const info = gl.getExtension('WEBGL_debug_renderer_info');
  const software = info && /swiftshader|llvmpipe|software rasterizer/i.test(gl.getParameter(info.UNMASKED_RENDERER_WEBGL));
  gl.getExtension('WEBGL_lose_context')?.loseContext();
  return Boolean(software || input.coarse.matches);
}

async function startGodot() {
  let ready;
  const initialized = new Promise(resolve => { ready = resolve; });
  const lowDetail = prefersLowDetail();
  globalThis.harborView = {
    ready,
    lowDetail: () => lowDetail,
    screen: () => screen,
    yaw: () => input.direction().yaw,
    pitch: () => input.direction().pitch,
    frame: () => serializedFrame,
    frameVersion: () => frameVersion,
    cameraDistance: (...args) => api.CameraDistance(...args),
    groundElevation: (x, z) => api.GroundElevation(x, z),
    requestChunk(file, complete) {
      fetch(assetUrl('godot/chunks/' + file), { cache: 'force-cache', signal: AbortSignal.timeout(12_000) })
        .then(response => {
          if (!response.ok) throw new Error('Could not load world chunk ' + file);
          return response.arrayBuffer();
        })
        .then(bytes => {
          engine.copyToFS('/tmp/harbor-world/' + file, bytes);
        })
        .then(() => complete(file, true), () => complete(file, false));
    },
    worldChunks(loaded, total) {
      canvas.dataset.loadedChunks = String(loaded);
      canvas.dataset.totalChunks = String(total);
    },
  };

  const executable = assetUrl('godot/harbor');
  await new Promise((resolve, reject) => {
    const script = document.createElement('script');
    script.src = executable + '.js';
    script.onload = resolve;
    script.onerror = () => reject(new Error('Godot launcher could not be loaded'));
    document.head.append(script);
  });
  const engine = new globalThis.Engine({
    canvas,
    executable,
    mainPack: executable + '.pck',
    focusCanvas: false,
    canvasResizePolicy: 2,
    ensureCrossOriginIsolationHeaders: false,
  });
  await engine.startGame({
    onProgress(current, total) {
      if (total > 0) status.textContent = '3D 도시 불러오는 중 · ' + Math.floor(current / total * 100) + '%';
    },
  });
  let timer;
  try {
    await Promise.race([
      initialized,
      new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('Godot scene did not initialize')), 60_000); }),
    ]);
  } finally {
    clearTimeout(timer);
  }
  canvas.dataset.engine = 'godot';
}

async function initialize() {
  const runtimePath = assetUrl('runtime/_framework/dotnet.js');
  const [runtime, plan] = await Promise.all([
    import(/* @vite-ignore */ runtimePath).then(({ dotnet }) => dotnet.create()),
    json('world/harbor_front.json'),
  ]);
  layout = plan;
  const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
  api = exports.Harbor.Browser.BrowserHarbor;
  replaceFrame(api.Start());
  chart = createChart(layout, byId('radar'), byId('atlas'), byId('map-legend'), byId('map-interiors'));
  await startGodot();
  start.textContent = '도시로 들어가기';
  status.textContent = input.coarse.matches ? '화면을 드래그해 둘러보세요.' : 'WASD 이동 · Space 점프 · 마우스 카메라 · F 차량 · M 지도';
  start.disabled = false;
  setInterval(simulate, 1000 / 30);
}

canvas.addEventListener('webglcontextlost', event => {
  event.preventDefault();
  contextLost = true;
  setScreen('intro');
  start.disabled = true;
  start.textContent = '그래픽 복구 대기 중';
  status.textContent = '그래픽 연결이 끊어졌습니다.';
});
canvas.addEventListener('webglcontextrestored', () => {
  contextLost = false;
  if (frame) setScreen('menu');
});
initialize().catch(error => {
  console.error(error);
  status.textContent = '게임을 불러올 수 없습니다. WebGL 2를 지원하는 브라우저에서 페이지를 다시 열어 보세요.';
  start.textContent = '불러오기 실패';
});
 + amount,
  service_ammo: amount => '탄약 보충 · -
};

let screen = 'intro';
let mapParent = 'play';
let api, frame, layout, chart;
let serializedFrame = "";
let frameVersion = 0;
let elapsed = 0;
let simulationTime = 0;
let crimeSequence = 0;
let noticeUntil = 0;
let audio;
let hadPointerLock = false;
let contextLost = false;
const input = createInput(canvas, touch, () => screen === 'play' && frame.alive, code => {
  if (!frame || screen === 'intro') return false;
  if (code === 'KeyM') {
    if (screen === 'map') setScreen(mapParent);
    else openMap();
  } else if (screen === 'map') setScreen(mapParent);
  else setScreen(screen === 'play' ? 'menu' : 'play');
  return true;
});

document.addEventListener('pointerlockchange', () => {
  const locked = document.pointerLockElement === canvas;
  if (hadPointerLock && !locked && screen === 'play') setScreen('menu');
  hadPointerLock = locked;
});

function setScreen(next) {
  screen = next;
  simulationTime = performance.now();
  intro.hidden = next !== 'intro';
  hud.hidden = next !== 'play';
  menu.hidden = next !== 'menu';
  map.hidden = next !== 'map';
  touch.hidden = next !== 'play' || !input.coarse.matches;
  if (next === 'play') {
    input.capture();
    audio?.resume();
  } else {
    input.release();
    audio?.pause();
  }
  if (next === 'menu') byId('resume').focus();
  if (next === 'map') {
    chart.atlas(frame.position, frame.state.isDriving ? frame.state.vehicle.yaw : input.direction().yaw);
    byId('close-map').focus();
  }
}

function openMap() {
  mapParent = screen === 'menu' ? 'menu' : 'play';
  setScreen('map');
}

function replaceFrame(raw) {
  frame = JSON.parse(raw);
  serializedFrame = raw;
  frameVersion++;
}

function reset() {
  replaceFrame(api.Start());
  input.reset();
  crimeSequence = 0;
  noticeUntil = 0;
  setScreen('play');
}

byId('pause-button').addEventListener('click', () => setScreen('menu'));
byId('map-button').addEventListener('click', openMap);
byId('resume').addEventListener('click', () => setScreen('play'));
byId('menu-map').addEventListener('click', openMap);
byId('close-map').addEventListener('click', () => setScreen(mapParent));
byId('reset').addEventListener('click', reset);
start.addEventListener('click', () => {
  if (!frame) return;
  audio ??= sound();
  setScreen('play');
});

function label(el, text) {
  if (el.textContent !== text) el.textContent = text;
}
function hint(el, text) {
  el.hidden = !text;
  label(el, text);
}

function updateHud() {
  const { state, alive } = frame;
  const { streetAction, streetCrime, playerCombat, maxWantedLevel, wantedLevel, policeUnits, isDriving, vehicle } = state;
  label(district, districtAt(layout, frame.position));
  label(wallet, '$' + (streetCrime?.dirtyCash ?? 0).toLocaleString('ko-KR'));
  wanted.hidden = !wantedLevel;
  if (wantedLevel) {
    label(stars, '★'.repeat(wantedLevel) + '☆'.repeat(Math.max(0, maxWantedLevel - wantedLevel)));
    stars.setAttribute('aria-label', '수배 ' + wantedLevel + '단계');
    label(pursuit, policeUnits ? '추격 중 · ' + policeUnits + '명' : '수배 중');
  }
  label(equipment, isDriving ? vehicle.displayName : playerCombat.weaponName);
  label(ammunition, isDriving ? Math.round(Math.abs(vehicle.speed) * 3.6) + ' km/h' :
    playerCombat.isReloading ? '재장전 중' :
    String(playerCombat.magazine).padStart(2, '0') + ' / ' + String(playerCombat.reserve).padStart(2, '0'));
  health.max = playerCombat.maxHealth;
  health.value = playerCombat.health;
  armor.max = playerCombat.maxArmor;
  armor.value = playerCombat.armor;
  crosshair.hidden = isDriving || !alive;
  crosshair.classList.toggle('aimed', frame.aiming);
  const prefix = input.coarse.matches ? '' : 'F · ';
  hint(vehicleAction, !alive ? '' : isDriving ? prefix + '하차' : streetAction?.vehicleId >= 0 ?
    prefix + (streetAction.vehicleOccupied ? '차량 탈취' : '탑승') : '');
  const streetLabel = streetAction?.cargoId ? '화물 훔치기' : streetAction?.canPickpocket ? '소매치기' : '';
  hint(streetHint, alive && !isDriving && streetLabel ? (input.coarse.matches ? '' : 'G · ') + streetLabel : '');
  if (streetCrime?.sequence && streetCrime.sequence !== crimeSequence) {
    crimeSequence = streetCrime.sequence;
    label(notice, crimeMessages[streetCrime.status]?.(streetCrime.amount) ?? '');
    noticeUntil = elapsed + 3.4;
  }
  if (!alive) {
    noticeUntil = elapsed + .5;
    label(notice, '쓰러졌습니다. 항만에서 다시 시작합니다.');
  }
  notice.hidden = elapsed >= noticeUntil || !notice.textContent;
  chart.radar(state, frame.position, isDriving ? vehicle.yaw : input.direction().yaw, elapsed);
}

function handleShots(shots) {
  for (const shot of shots) {
    const distance = Math.hypot(frame.position.x - shot.origin.x, frame.position.z - shot.origin.z);
    audio?.shot(shot.weaponId, distance);
  }
}

// Advance the local authority on its own clock so slow GPU frames do not slow input or physics.
function simulate() {
  const now = performance.now();
  const dt = Math.min((now - simulationTime) / 1000, .12);
  simulationTime = now;
  if (screen !== 'play' || document.hidden || contextLost) return;
  elapsed += dt;
  const { right, forward, yaw, pitch, buttons, pulses, slot } = input.sample(dt, frame.state.isDriving);
  const next = api.Tick(dt, right, forward, yaw, pitch, buttons, pulses, slot);
  if (!next) return;
  replaceFrame(next);
  handleShots(frame.shots);
  updateHud();
}

function sound() {
  const create = (name, volume) => {
    const sound = new Audio(assetUrl('assets/audio/' + name + '.ogg'));
    sound.volume = volume;
    return sound;
  };
  const base = create('dock_ambience', .12);
  const shots = { wasp_smg: create('wasp', .25), kestrel_p10: create('kestrel', .25) };
  base.loop = true;
  return {
    resume: () => base.play().catch(() => {}),
    pause: () => base.pause(),
    shot(id, distance) {
      if (distance > 65) return;
      const shot = (shots[id] ?? shots.wasp_smg).cloneNode();
      shot.volume = .32 * (1 - distance / 72);
      shot.play().catch(() => {});
    },
  };
}

async function json(path) {
  const response = await fetch(assetUrl(path));
  if (!response.ok) throw new Error('Could not load ' + path + ' (' + response.status + ')');
  return response.json();
}

function prefersLowDetail() {
  const probe = document.createElement('canvas');
  const gl = probe.getContext('webgl2');
  if (!gl) throw new Error('WebGL 2 is not available');
  const info = gl.getExtension('WEBGL_debug_renderer_info');
  const software = info && /swiftshader|llvmpipe|software rasterizer/i.test(gl.getParameter(info.UNMASKED_RENDERER_WEBGL));
  gl.getExtension('WEBGL_lose_context')?.loseContext();
  return Boolean(software || input.coarse.matches);
}

async function startGodot() {
  let ready;
  const initialized = new Promise(resolve => { ready = resolve; });
  const lowDetail = prefersLowDetail();
  globalThis.harborView = {
    ready,
    lowDetail: () => lowDetail,
    screen: () => screen,
    yaw: () => input.direction().yaw,
    pitch: () => input.direction().pitch,
    frame: () => serializedFrame,
    frameVersion: () => frameVersion,
    cameraDistance: (...args) => api.CameraDistance(...args),
    groundElevation: (x, z) => api.GroundElevation(x, z),
    requestChunk(file, complete) {
      fetch(assetUrl('godot/chunks/' + file), { cache: 'force-cache', signal: AbortSignal.timeout(12_000) })
        .then(response => {
          if (!response.ok) throw new Error('Could not load world chunk ' + file);
          return response.arrayBuffer();
        })
        .then(bytes => {
          engine.copyToFS('/tmp/harbor-world/' + file, bytes);
        })
        .then(() => complete(file, true), () => complete(file, false));
    },
    worldChunks(loaded, total) {
      canvas.dataset.loadedChunks = String(loaded);
      canvas.dataset.totalChunks = String(total);
    },
  };

  const executable = assetUrl('godot/harbor');
  await new Promise((resolve, reject) => {
    const script = document.createElement('script');
    script.src = executable + '.js';
    script.onload = resolve;
    script.onerror = () => reject(new Error('Godot launcher could not be loaded'));
    document.head.append(script);
  });
  const engine = new globalThis.Engine({
    canvas,
    executable,
    mainPack: executable + '.pck',
    focusCanvas: false,
    canvasResizePolicy: 2,
    ensureCrossOriginIsolationHeaders: false,
  });
  await engine.startGame({
    onProgress(current, total) {
      if (total > 0) status.textContent = '3D 도시 불러오는 중 · ' + Math.floor(current / total * 100) + '%';
    },
  });
  let timer;
  try {
    await Promise.race([
      initialized,
      new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('Godot scene did not initialize')), 60_000); }),
    ]);
  } finally {
    clearTimeout(timer);
  }
  canvas.dataset.engine = 'godot';
}

async function initialize() {
  const runtimePath = assetUrl('runtime/_framework/dotnet.js');
  const [runtime, plan] = await Promise.all([
    import(/* @vite-ignore */ runtimePath).then(({ dotnet }) => dotnet.create()),
    json('world/harbor_front.json'),
  ]);
  layout = plan;
  const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
  api = exports.Harbor.Browser.BrowserHarbor;
  replaceFrame(api.Start());
  chart = createChart(layout, byId('radar'), byId('atlas'), byId('map-legend'), byId('map-interiors'));
  await startGodot();
  start.textContent = '도시로 들어가기';
  status.textContent = input.coarse.matches ? '화면을 드래그해 둘러보세요.' : 'WASD 이동 · Space 점프 · 마우스 카메라 · F 차량 · M 지도';
  start.disabled = false;
  setInterval(simulate, 1000 / 30);
}

canvas.addEventListener('webglcontextlost', event => {
  event.preventDefault();
  contextLost = true;
  setScreen('intro');
  start.disabled = true;
  start.textContent = '그래픽 복구 대기 중';
  status.textContent = '그래픽 연결이 끊어졌습니다.';
});
canvas.addEventListener('webglcontextrestored', () => {
  contextLost = false;
  if (frame) setScreen('menu');
});
initialize().catch(error => {
  console.error(error);
  status.textContent = '게임을 불러올 수 없습니다. WebGL 2를 지원하는 브라우저에서 페이지를 다시 열어 보세요.';
  start.textContent = '불러오기 실패';
});
 + amount,
  service_armor: amount => '방어구 보충 · -
};

let screen = 'intro';
let mapParent = 'play';
let api, frame, layout, chart;
let serializedFrame = "";
let frameVersion = 0;
let elapsed = 0;
let simulationTime = 0;
let crimeSequence = 0;
let noticeUntil = 0;
let audio;
let hadPointerLock = false;
let contextLost = false;
const input = createInput(canvas, touch, () => screen === 'play' && frame.alive, code => {
  if (!frame || screen === 'intro') return false;
  if (code === 'KeyM') {
    if (screen === 'map') setScreen(mapParent);
    else openMap();
  } else if (screen === 'map') setScreen(mapParent);
  else setScreen(screen === 'play' ? 'menu' : 'play');
  return true;
});

document.addEventListener('pointerlockchange', () => {
  const locked = document.pointerLockElement === canvas;
  if (hadPointerLock && !locked && screen === 'play') setScreen('menu');
  hadPointerLock = locked;
});

function setScreen(next) {
  screen = next;
  simulationTime = performance.now();
  intro.hidden = next !== 'intro';
  hud.hidden = next !== 'play';
  menu.hidden = next !== 'menu';
  map.hidden = next !== 'map';
  touch.hidden = next !== 'play' || !input.coarse.matches;
  if (next === 'play') {
    input.capture();
    audio?.resume();
  } else {
    input.release();
    audio?.pause();
  }
  if (next === 'menu') byId('resume').focus();
  if (next === 'map') {
    chart.atlas(frame.position, frame.state.isDriving ? frame.state.vehicle.yaw : input.direction().yaw);
    byId('close-map').focus();
  }
}

function openMap() {
  mapParent = screen === 'menu' ? 'menu' : 'play';
  setScreen('map');
}

function replaceFrame(raw) {
  frame = JSON.parse(raw);
  serializedFrame = raw;
  frameVersion++;
}

function reset() {
  replaceFrame(api.Start());
  input.reset();
  crimeSequence = 0;
  noticeUntil = 0;
  setScreen('play');
}

byId('pause-button').addEventListener('click', () => setScreen('menu'));
byId('map-button').addEventListener('click', openMap);
byId('resume').addEventListener('click', () => setScreen('play'));
byId('menu-map').addEventListener('click', openMap);
byId('close-map').addEventListener('click', () => setScreen(mapParent));
byId('reset').addEventListener('click', reset);
start.addEventListener('click', () => {
  if (!frame) return;
  audio ??= sound();
  setScreen('play');
});

function label(el, text) {
  if (el.textContent !== text) el.textContent = text;
}
function hint(el, text) {
  el.hidden = !text;
  label(el, text);
}

function updateHud() {
  const { state, alive } = frame;
  const { streetAction, streetCrime, playerCombat, maxWantedLevel, wantedLevel, policeUnits, isDriving, vehicle } = state;
  label(district, districtAt(layout, frame.position));
  label(wallet, '$' + (streetCrime?.dirtyCash ?? 0).toLocaleString('ko-KR'));
  wanted.hidden = !wantedLevel;
  if (wantedLevel) {
    label(stars, '★'.repeat(wantedLevel) + '☆'.repeat(Math.max(0, maxWantedLevel - wantedLevel)));
    stars.setAttribute('aria-label', '수배 ' + wantedLevel + '단계');
    label(pursuit, policeUnits ? '추격 중 · ' + policeUnits + '명' : '수배 중');
  }
  label(equipment, isDriving ? vehicle.displayName : playerCombat.weaponName);
  label(ammunition, isDriving ? Math.round(Math.abs(vehicle.speed) * 3.6) + ' km/h' :
    playerCombat.isReloading ? '재장전 중' :
    String(playerCombat.magazine).padStart(2, '0') + ' / ' + String(playerCombat.reserve).padStart(2, '0'));
  health.max = playerCombat.maxHealth;
  health.value = playerCombat.health;
  armor.max = playerCombat.maxArmor;
  armor.value = playerCombat.armor;
  crosshair.hidden = isDriving || !alive;
  crosshair.classList.toggle('aimed', frame.aiming);
  const prefix = input.coarse.matches ? '' : 'F · ';
  hint(vehicleAction, !alive ? '' : isDriving ? prefix + '하차' : streetAction?.vehicleId >= 0 ?
    prefix + (streetAction.vehicleOccupied ? '차량 탈취' : '탑승') : '');
  const streetLabel = streetAction?.cargoId ? '화물 훔치기' : streetAction?.canPickpocket ? '소매치기' : '';
  hint(streetHint, alive && !isDriving && streetLabel ? (input.coarse.matches ? '' : 'G · ') + streetLabel : '');
  if (streetCrime?.sequence && streetCrime.sequence !== crimeSequence) {
    crimeSequence = streetCrime.sequence;
    label(notice, crimeMessages[streetCrime.status]?.(streetCrime.amount) ?? '');
    noticeUntil = elapsed + 3.4;
  }
  if (!alive) {
    noticeUntil = elapsed + .5;
    label(notice, '쓰러졌습니다. 항만에서 다시 시작합니다.');
  }
  notice.hidden = elapsed >= noticeUntil || !notice.textContent;
  chart.radar(state, frame.position, isDriving ? vehicle.yaw : input.direction().yaw, elapsed);
}

function handleShots(shots) {
  for (const shot of shots) {
    const distance = Math.hypot(frame.position.x - shot.origin.x, frame.position.z - shot.origin.z);
    audio?.shot(shot.weaponId, distance);
  }
}

// Advance the local authority on its own clock so slow GPU frames do not slow input or physics.
function simulate() {
  const now = performance.now();
  const dt = Math.min((now - simulationTime) / 1000, .12);
  simulationTime = now;
  if (screen !== 'play' || document.hidden || contextLost) return;
  elapsed += dt;
  const { right, forward, yaw, pitch, buttons, pulses, slot } = input.sample(dt, frame.state.isDriving);
  const next = api.Tick(dt, right, forward, yaw, pitch, buttons, pulses, slot);
  if (!next) return;
  replaceFrame(next);
  handleShots(frame.shots);
  updateHud();
}

function sound() {
  const create = (name, volume) => {
    const sound = new Audio(assetUrl('assets/audio/' + name + '.ogg'));
    sound.volume = volume;
    return sound;
  };
  const base = create('dock_ambience', .12);
  const shots = { wasp_smg: create('wasp', .25), kestrel_p10: create('kestrel', .25) };
  base.loop = true;
  return {
    resume: () => base.play().catch(() => {}),
    pause: () => base.pause(),
    shot(id, distance) {
      if (distance > 65) return;
      const shot = (shots[id] ?? shots.wasp_smg).cloneNode();
      shot.volume = .32 * (1 - distance / 72);
      shot.play().catch(() => {});
    },
  };
}

async function json(path) {
  const response = await fetch(assetUrl(path));
  if (!response.ok) throw new Error('Could not load ' + path + ' (' + response.status + ')');
  return response.json();
}

function prefersLowDetail() {
  const probe = document.createElement('canvas');
  const gl = probe.getContext('webgl2');
  if (!gl) throw new Error('WebGL 2 is not available');
  const info = gl.getExtension('WEBGL_debug_renderer_info');
  const software = info && /swiftshader|llvmpipe|software rasterizer/i.test(gl.getParameter(info.UNMASKED_RENDERER_WEBGL));
  gl.getExtension('WEBGL_lose_context')?.loseContext();
  return Boolean(software || input.coarse.matches);
}

async function startGodot() {
  let ready;
  const initialized = new Promise(resolve => { ready = resolve; });
  const lowDetail = prefersLowDetail();
  globalThis.harborView = {
    ready,
    lowDetail: () => lowDetail,
    screen: () => screen,
    yaw: () => input.direction().yaw,
    pitch: () => input.direction().pitch,
    frame: () => serializedFrame,
    frameVersion: () => frameVersion,
    cameraDistance: (...args) => api.CameraDistance(...args),
    groundElevation: (x, z) => api.GroundElevation(x, z),
    requestChunk(file, complete) {
      fetch(assetUrl('godot/chunks/' + file), { cache: 'force-cache', signal: AbortSignal.timeout(12_000) })
        .then(response => {
          if (!response.ok) throw new Error('Could not load world chunk ' + file);
          return response.arrayBuffer();
        })
        .then(bytes => {
          engine.copyToFS('/tmp/harbor-world/' + file, bytes);
        })
        .then(() => complete(file, true), () => complete(file, false));
    },
    worldChunks(loaded, total) {
      canvas.dataset.loadedChunks = String(loaded);
      canvas.dataset.totalChunks = String(total);
    },
  };

  const executable = assetUrl('godot/harbor');
  await new Promise((resolve, reject) => {
    const script = document.createElement('script');
    script.src = executable + '.js';
    script.onload = resolve;
    script.onerror = () => reject(new Error('Godot launcher could not be loaded'));
    document.head.append(script);
  });
  const engine = new globalThis.Engine({
    canvas,
    executable,
    mainPack: executable + '.pck',
    focusCanvas: false,
    canvasResizePolicy: 2,
    ensureCrossOriginIsolationHeaders: false,
  });
  await engine.startGame({
    onProgress(current, total) {
      if (total > 0) status.textContent = '3D 도시 불러오는 중 · ' + Math.floor(current / total * 100) + '%';
    },
  });
  let timer;
  try {
    await Promise.race([
      initialized,
      new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('Godot scene did not initialize')), 60_000); }),
    ]);
  } finally {
    clearTimeout(timer);
  }
  canvas.dataset.engine = 'godot';
}

async function initialize() {
  const runtimePath = assetUrl('runtime/_framework/dotnet.js');
  const [runtime, plan] = await Promise.all([
    import(/* @vite-ignore */ runtimePath).then(({ dotnet }) => dotnet.create()),
    json('world/harbor_front.json'),
  ]);
  layout = plan;
  const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
  api = exports.Harbor.Browser.BrowserHarbor;
  replaceFrame(api.Start());
  chart = createChart(layout, byId('radar'), byId('atlas'), byId('map-legend'), byId('map-interiors'));
  await startGodot();
  start.textContent = '도시로 들어가기';
  status.textContent = input.coarse.matches ? '화면을 드래그해 둘러보세요.' : 'WASD 이동 · Space 점프 · 마우스 카메라 · F 차량 · M 지도';
  start.disabled = false;
  setInterval(simulate, 1000 / 30);
}

canvas.addEventListener('webglcontextlost', event => {
  event.preventDefault();
  contextLost = true;
  setScreen('intro');
  start.disabled = true;
  start.textContent = '그래픽 복구 대기 중';
  status.textContent = '그래픽 연결이 끊어졌습니다.';
});
canvas.addEventListener('webglcontextrestored', () => {
  contextLost = false;
  if (frame) setScreen('menu');
});
initialize().catch(error => {
  console.error(error);
  status.textContent = '게임을 불러올 수 없습니다. WebGL 2를 지원하는 브라우저에서 페이지를 다시 열어 보세요.';
  start.textContent = '불러오기 실패';
});
 + amount,
  service_wanted: () => '수배 중에는 서비스를 이용할 수 없습니다.',
  service_funds: () => '게임 머니가 부족합니다.',
  service_full: () => '이미 충분히 보충되어 있습니다.',
  service_unavailable: () => '서비스 장소 가까이에서 이용하세요.',
};

let screen = 'intro';
let mapParent = 'play';
let api, frame, layout, chart;
let serializedFrame = "";
let frameVersion = 0;
let elapsed = 0;
let simulationTime = 0;
let crimeSequence = 0;
let noticeUntil = 0;
let audio;
let hadPointerLock = false;
let contextLost = false;
const input = createInput(canvas, touch, () => screen === 'play' && frame.alive, code => {
  if (!frame || screen === 'intro') return false;
  if (code === 'KeyM') {
    if (screen === 'map') setScreen(mapParent);
    else openMap();
  } else if (screen === 'map') setScreen(mapParent);
  else setScreen(screen === 'play' ? 'menu' : 'play');
  return true;
});

document.addEventListener('pointerlockchange', () => {
  const locked = document.pointerLockElement === canvas;
  if (hadPointerLock && !locked && screen === 'play') setScreen('menu');
  hadPointerLock = locked;
});

function setScreen(next) {
  screen = next;
  simulationTime = performance.now();
  intro.hidden = next !== 'intro';
  hud.hidden = next !== 'play';
  menu.hidden = next !== 'menu';
  map.hidden = next !== 'map';
  touch.hidden = next !== 'play' || !input.coarse.matches;
  if (next === 'play') {
    input.capture();
    audio?.resume();
  } else {
    input.release();
    audio?.pause();
  }
  if (next === 'menu') byId('resume').focus();
  if (next === 'map') {
    chart.atlas(frame.position, frame.state.isDriving ? frame.state.vehicle.yaw : input.direction().yaw);
    byId('close-map').focus();
  }
}

function openMap() {
  mapParent = screen === 'menu' ? 'menu' : 'play';
  setScreen('map');
}

function replaceFrame(raw) {
  frame = JSON.parse(raw);
  serializedFrame = raw;
  frameVersion++;
}

function reset() {
  replaceFrame(api.Start());
  input.reset();
  crimeSequence = 0;
  noticeUntil = 0;
  setScreen('play');
}

byId('pause-button').addEventListener('click', () => setScreen('menu'));
byId('map-button').addEventListener('click', openMap);
byId('resume').addEventListener('click', () => setScreen('play'));
byId('menu-map').addEventListener('click', openMap);
byId('close-map').addEventListener('click', () => setScreen(mapParent));
byId('reset').addEventListener('click', reset);
start.addEventListener('click', () => {
  if (!frame) return;
  audio ??= sound();
  setScreen('play');
});

function label(el, text) {
  if (el.textContent !== text) el.textContent = text;
}
function hint(el, text) {
  el.hidden = !text;
  label(el, text);
}

function updateHud() {
  const { state, alive } = frame;
  const { streetAction, streetCrime, playerCombat, maxWantedLevel, wantedLevel, policeUnits, isDriving, vehicle } = state;
  label(district, districtAt(layout, frame.position));
  label(wallet, '$' + (streetCrime?.dirtyCash ?? 0).toLocaleString('ko-KR'));
  wanted.hidden = !wantedLevel;
  if (wantedLevel) {
    label(stars, '★'.repeat(wantedLevel) + '☆'.repeat(Math.max(0, maxWantedLevel - wantedLevel)));
    stars.setAttribute('aria-label', '수배 ' + wantedLevel + '단계');
    label(pursuit, policeUnits ? '추격 중 · ' + policeUnits + '명' : '수배 중');
  }
  label(equipment, isDriving ? vehicle.displayName : playerCombat.weaponName);
  label(ammunition, isDriving ? Math.round(Math.abs(vehicle.speed) * 3.6) + ' km/h' :
    playerCombat.isReloading ? '재장전 중' :
    String(playerCombat.magazine).padStart(2, '0') + ' / ' + String(playerCombat.reserve).padStart(2, '0'));
  health.max = playerCombat.maxHealth;
  health.value = playerCombat.health;
  armor.max = playerCombat.maxArmor;
  armor.value = playerCombat.armor;
  crosshair.hidden = isDriving || !alive;
  crosshair.classList.toggle('aimed', frame.aiming);
  const prefix = input.coarse.matches ? '' : 'F · ';
  hint(vehicleAction, !alive ? '' : isDriving ? prefix + '하차' : streetAction?.vehicleId >= 0 ?
    prefix + (streetAction.vehicleOccupied ? '차량 탈취' : '탑승') : '');
  const streetLabel = streetAction?.cargoId ? '화물 훔치기' : streetAction?.canPickpocket ? '소매치기' : '';
  hint(streetHint, alive && !isDriving && streetLabel ? (input.coarse.matches ? '' : 'G · ') + streetLabel : '');
  if (streetCrime?.sequence && streetCrime.sequence !== crimeSequence) {
    crimeSequence = streetCrime.sequence;
    label(notice, crimeMessages[streetCrime.status]?.(streetCrime.amount) ?? '');
    noticeUntil = elapsed + 3.4;
  }
  if (!alive) {
    noticeUntil = elapsed + .5;
    label(notice, '쓰러졌습니다. 항만에서 다시 시작합니다.');
  }
  notice.hidden = elapsed >= noticeUntil || !notice.textContent;
  chart.radar(state, frame.position, isDriving ? vehicle.yaw : input.direction().yaw, elapsed);
}

function handleShots(shots) {
  for (const shot of shots) {
    const distance = Math.hypot(frame.position.x - shot.origin.x, frame.position.z - shot.origin.z);
    audio?.shot(shot.weaponId, distance);
  }
}

// Advance the local authority on its own clock so slow GPU frames do not slow input or physics.
function simulate() {
  const now = performance.now();
  const dt = Math.min((now - simulationTime) / 1000, .12);
  simulationTime = now;
  if (screen !== 'play' || document.hidden || contextLost) return;
  elapsed += dt;
  const { right, forward, yaw, pitch, buttons, pulses, slot } = input.sample(dt, frame.state.isDriving);
  const next = api.Tick(dt, right, forward, yaw, pitch, buttons, pulses, slot);
  if (!next) return;
  replaceFrame(next);
  handleShots(frame.shots);
  updateHud();
}

function sound() {
  const create = (name, volume) => {
    const sound = new Audio(assetUrl('assets/audio/' + name + '.ogg'));
    sound.volume = volume;
    return sound;
  };
  const base = create('dock_ambience', .12);
  const shots = { wasp_smg: create('wasp', .25), kestrel_p10: create('kestrel', .25) };
  base.loop = true;
  return {
    resume: () => base.play().catch(() => {}),
    pause: () => base.pause(),
    shot(id, distance) {
      if (distance > 65) return;
      const shot = (shots[id] ?? shots.wasp_smg).cloneNode();
      shot.volume = .32 * (1 - distance / 72);
      shot.play().catch(() => {});
    },
  };
}

async function json(path) {
  const response = await fetch(assetUrl(path));
  if (!response.ok) throw new Error('Could not load ' + path + ' (' + response.status + ')');
  return response.json();
}

function prefersLowDetail() {
  const probe = document.createElement('canvas');
  const gl = probe.getContext('webgl2');
  if (!gl) throw new Error('WebGL 2 is not available');
  const info = gl.getExtension('WEBGL_debug_renderer_info');
  const software = info && /swiftshader|llvmpipe|software rasterizer/i.test(gl.getParameter(info.UNMASKED_RENDERER_WEBGL));
  gl.getExtension('WEBGL_lose_context')?.loseContext();
  return Boolean(software || input.coarse.matches);
}

async function startGodot() {
  let ready;
  const initialized = new Promise(resolve => { ready = resolve; });
  const lowDetail = prefersLowDetail();
  globalThis.harborView = {
    ready,
    lowDetail: () => lowDetail,
    screen: () => screen,
    yaw: () => input.direction().yaw,
    pitch: () => input.direction().pitch,
    frame: () => serializedFrame,
    frameVersion: () => frameVersion,
    cameraDistance: (...args) => api.CameraDistance(...args),
    groundElevation: (x, z) => api.GroundElevation(x, z),
    requestChunk(file, complete) {
      fetch(assetUrl('godot/chunks/' + file), { cache: 'force-cache', signal: AbortSignal.timeout(12_000) })
        .then(response => {
          if (!response.ok) throw new Error('Could not load world chunk ' + file);
          return response.arrayBuffer();
        })
        .then(bytes => {
          engine.copyToFS('/tmp/harbor-world/' + file, bytes);
        })
        .then(() => complete(file, true), () => complete(file, false));
    },
    worldChunks(loaded, total) {
      canvas.dataset.loadedChunks = String(loaded);
      canvas.dataset.totalChunks = String(total);
    },
  };

  const executable = assetUrl('godot/harbor');
  await new Promise((resolve, reject) => {
    const script = document.createElement('script');
    script.src = executable + '.js';
    script.onload = resolve;
    script.onerror = () => reject(new Error('Godot launcher could not be loaded'));
    document.head.append(script);
  });
  const engine = new globalThis.Engine({
    canvas,
    executable,
    mainPack: executable + '.pck',
    focusCanvas: false,
    canvasResizePolicy: 2,
    ensureCrossOriginIsolationHeaders: false,
  });
  await engine.startGame({
    onProgress(current, total) {
      if (total > 0) status.textContent = '3D 도시 불러오는 중 · ' + Math.floor(current / total * 100) + '%';
    },
  });
  let timer;
  try {
    await Promise.race([
      initialized,
      new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('Godot scene did not initialize')), 60_000); }),
    ]);
  } finally {
    clearTimeout(timer);
  }
  canvas.dataset.engine = 'godot';
}

async function initialize() {
  const runtimePath = assetUrl('runtime/_framework/dotnet.js');
  const [runtime, plan] = await Promise.all([
    import(/* @vite-ignore */ runtimePath).then(({ dotnet }) => dotnet.create()),
    json('world/harbor_front.json'),
  ]);
  layout = plan;
  const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
  api = exports.Harbor.Browser.BrowserHarbor;
  replaceFrame(api.Start());
  chart = createChart(layout, byId('radar'), byId('atlas'), byId('map-legend'), byId('map-interiors'));
  await startGodot();
  start.textContent = '도시로 들어가기';
  status.textContent = input.coarse.matches ? '화면을 드래그해 둘러보세요.' : 'WASD 이동 · Space 점프 · 마우스 카메라 · F 차량 · M 지도';
  start.disabled = false;
  setInterval(simulate, 1000 / 30);
}

canvas.addEventListener('webglcontextlost', event => {
  event.preventDefault();
  contextLost = true;
  setScreen('intro');
  start.disabled = true;
  start.textContent = '그래픽 복구 대기 중';
  status.textContent = '그래픽 연결이 끊어졌습니다.';
});
canvas.addEventListener('webglcontextrestored', () => {
  contextLost = false;
  if (frame) setScreen('menu');
});
initialize().catch(error => {
  console.error(error);
  status.textContent = '게임을 불러올 수 없습니다. WebGL 2를 지원하는 브라우저에서 페이지를 다시 열어 보세요.';
  start.textContent = '불러오기 실패';
});
 + service.price : '');
  if (streetCrime?.sequence && streetCrime.sequence !== crimeSequence) {
    crimeSequence = streetCrime.sequence;
    label(notice, crimeMessages[streetCrime.status]?.(streetCrime.amount) ?? '');
    noticeUntil = elapsed + 3.4;
  }
  if (!alive) {
    noticeUntil = elapsed + .5;
    label(notice, '쓰러졌습니다. 항만에서 다시 시작합니다.');
  }
  notice.hidden = elapsed >= noticeUntil || !notice.textContent;
  chart.radar(state, frame.position, isDriving ? vehicle.yaw : input.direction().yaw, elapsed);
}

function handleShots(shots) {
  for (const shot of shots) {
    const distance = Math.hypot(frame.position.x - shot.origin.x, frame.position.z - shot.origin.z);
    audio?.shot(shot.weaponId, distance);
  }
}

// Advance the local authority on its own clock so slow GPU frames do not slow input or physics.
function simulate() {
  const now = performance.now();
  const dt = Math.min((now - simulationTime) / 1000, .12);
  simulationTime = now;
  if (screen !== 'play' || document.hidden || contextLost) return;
  elapsed += dt;
  const { right, forward, yaw, pitch, buttons, pulses, slot } = input.sample(dt, frame.state.isDriving);
  const next = api.Tick(dt, right, forward, yaw, pitch, buttons, pulses, slot);
  if (!next) return;
  replaceFrame(next);
  handleShots(frame.shots);
  updateHud();
}

function sound() {
  const create = (name, volume) => {
    const sound = new Audio(assetUrl('assets/audio/' + name + '.ogg'));
    sound.volume = volume;
    return sound;
  };
  const base = create('dock_ambience', .12);
  const shots = { wasp_smg: create('wasp', .25), kestrel_p10: create('kestrel', .25) };
  base.loop = true;
  return {
    resume: () => base.play().catch(() => {}),
    pause: () => base.pause(),
    shot(id, distance) {
      if (distance > 65) return;
      const shot = (shots[id] ?? shots.wasp_smg).cloneNode();
      shot.volume = .32 * (1 - distance / 72);
      shot.play().catch(() => {});
    },
  };
}

async function json(path) {
  const response = await fetch(assetUrl(path));
  if (!response.ok) throw new Error('Could not load ' + path + ' (' + response.status + ')');
  return response.json();
}

function prefersLowDetail() {
  const probe = document.createElement('canvas');
  const gl = probe.getContext('webgl2');
  if (!gl) throw new Error('WebGL 2 is not available');
  const info = gl.getExtension('WEBGL_debug_renderer_info');
  const software = info && /swiftshader|llvmpipe|software rasterizer/i.test(gl.getParameter(info.UNMASKED_RENDERER_WEBGL));
  gl.getExtension('WEBGL_lose_context')?.loseContext();
  return Boolean(software || input.coarse.matches);
}

async function startGodot() {
  let ready;
  const initialized = new Promise(resolve => { ready = resolve; });
  const lowDetail = prefersLowDetail();
  globalThis.harborView = {
    ready,
    lowDetail: () => lowDetail,
    screen: () => screen,
    yaw: () => input.direction().yaw,
    pitch: () => input.direction().pitch,
    frame: () => serializedFrame,
    frameVersion: () => frameVersion,
    cameraDistance: (...args) => api.CameraDistance(...args),
    groundElevation: (x, z) => api.GroundElevation(x, z),
    requestChunk(file, complete) {
      fetch(assetUrl('godot/chunks/' + file), { cache: 'force-cache', signal: AbortSignal.timeout(12_000) })
        .then(response => {
          if (!response.ok) throw new Error('Could not load world chunk ' + file);
          return response.arrayBuffer();
        })
        .then(bytes => {
          engine.copyToFS('/tmp/harbor-world/' + file, bytes);
        })
        .then(() => complete(file, true), () => complete(file, false));
    },
    worldChunks(loaded, total) {
      canvas.dataset.loadedChunks = String(loaded);
      canvas.dataset.totalChunks = String(total);
    },
  };

  const executable = assetUrl('godot/harbor');
  await new Promise((resolve, reject) => {
    const script = document.createElement('script');
    script.src = executable + '.js';
    script.onload = resolve;
    script.onerror = () => reject(new Error('Godot launcher could not be loaded'));
    document.head.append(script);
  });
  const engine = new globalThis.Engine({
    canvas,
    executable,
    mainPack: executable + '.pck',
    focusCanvas: false,
    canvasResizePolicy: 2,
    ensureCrossOriginIsolationHeaders: false,
  });
  await engine.startGame({
    onProgress(current, total) {
      if (total > 0) status.textContent = '3D 도시 불러오는 중 · ' + Math.floor(current / total * 100) + '%';
    },
  });
  let timer;
  try {
    await Promise.race([
      initialized,
      new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('Godot scene did not initialize')), 60_000); }),
    ]);
  } finally {
    clearTimeout(timer);
  }
  canvas.dataset.engine = 'godot';
}

async function initialize() {
  const runtimePath = assetUrl('runtime/_framework/dotnet.js');
  const [runtime, plan] = await Promise.all([
    import(/* @vite-ignore */ runtimePath).then(({ dotnet }) => dotnet.create()),
    json('world/harbor_front.json'),
  ]);
  layout = plan;
  const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
  api = exports.Harbor.Browser.BrowserHarbor;
  replaceFrame(api.Start());
  chart = createChart(layout, byId('radar'), byId('atlas'), byId('map-legend'), byId('map-interiors'));
  await startGodot();
  start.textContent = '도시로 들어가기';
  status.textContent = input.coarse.matches ? '화면을 드래그해 둘러보세요.' : 'WASD 이동 · Space 점프 · 마우스 카메라 · F 차량 · M 지도';
  start.disabled = false;
  setInterval(simulate, 1000 / 30);
}

canvas.addEventListener('webglcontextlost', event => {
  event.preventDefault();
  contextLost = true;
  setScreen('intro');
  start.disabled = true;
  start.textContent = '그래픽 복구 대기 중';
  status.textContent = '그래픽 연결이 끊어졌습니다.';
});
canvas.addEventListener('webglcontextrestored', () => {
  contextLost = false;
  if (frame) setScreen('menu');
});
initialize().catch(error => {
  console.error(error);
  status.textContent = '게임을 불러올 수 없습니다. WebGL 2를 지원하는 브라우저에서 페이지를 다시 열어 보세요.';
  start.textContent = '불러오기 실패';
});
 + amount,
  service_ammo: amount => '탄약 보충 · -
};

let screen = 'intro';
let mapParent = 'play';
let api, frame, layout, chart;
let serializedFrame = "";
let frameVersion = 0;
let elapsed = 0;
let simulationTime = 0;
let crimeSequence = 0;
let noticeUntil = 0;
let audio;
let hadPointerLock = false;
let contextLost = false;
const input = createInput(canvas, touch, () => screen === 'play' && frame.alive, code => {
  if (!frame || screen === 'intro') return false;
  if (code === 'KeyM') {
    if (screen === 'map') setScreen(mapParent);
    else openMap();
  } else if (screen === 'map') setScreen(mapParent);
  else setScreen(screen === 'play' ? 'menu' : 'play');
  return true;
});

document.addEventListener('pointerlockchange', () => {
  const locked = document.pointerLockElement === canvas;
  if (hadPointerLock && !locked && screen === 'play') setScreen('menu');
  hadPointerLock = locked;
});

function setScreen(next) {
  screen = next;
  simulationTime = performance.now();
  intro.hidden = next !== 'intro';
  hud.hidden = next !== 'play';
  menu.hidden = next !== 'menu';
  map.hidden = next !== 'map';
  touch.hidden = next !== 'play' || !input.coarse.matches;
  if (next === 'play') {
    input.capture();
    audio?.resume();
  } else {
    input.release();
    audio?.pause();
  }
  if (next === 'menu') byId('resume').focus();
  if (next === 'map') {
    chart.atlas(frame.position, frame.state.isDriving ? frame.state.vehicle.yaw : input.direction().yaw);
    byId('close-map').focus();
  }
}

function openMap() {
  mapParent = screen === 'menu' ? 'menu' : 'play';
  setScreen('map');
}

function replaceFrame(raw) {
  frame = JSON.parse(raw);
  serializedFrame = raw;
  frameVersion++;
}

function reset() {
  replaceFrame(api.Start());
  input.reset();
  crimeSequence = 0;
  noticeUntil = 0;
  setScreen('play');
}

byId('pause-button').addEventListener('click', () => setScreen('menu'));
byId('map-button').addEventListener('click', openMap);
byId('resume').addEventListener('click', () => setScreen('play'));
byId('menu-map').addEventListener('click', openMap);
byId('close-map').addEventListener('click', () => setScreen(mapParent));
byId('reset').addEventListener('click', reset);
start.addEventListener('click', () => {
  if (!frame) return;
  audio ??= sound();
  setScreen('play');
});

function label(el, text) {
  if (el.textContent !== text) el.textContent = text;
}
function hint(el, text) {
  el.hidden = !text;
  label(el, text);
}

function updateHud() {
  const { state, alive } = frame;
  const { streetAction, streetCrime, playerCombat, maxWantedLevel, wantedLevel, policeUnits, isDriving, vehicle } = state;
  label(district, districtAt(layout, frame.position));
  label(wallet, '$' + (streetCrime?.dirtyCash ?? 0).toLocaleString('ko-KR'));
  wanted.hidden = !wantedLevel;
  if (wantedLevel) {
    label(stars, '★'.repeat(wantedLevel) + '☆'.repeat(Math.max(0, maxWantedLevel - wantedLevel)));
    stars.setAttribute('aria-label', '수배 ' + wantedLevel + '단계');
    label(pursuit, policeUnits ? '추격 중 · ' + policeUnits + '명' : '수배 중');
  }
  label(equipment, isDriving ? vehicle.displayName : playerCombat.weaponName);
  label(ammunition, isDriving ? Math.round(Math.abs(vehicle.speed) * 3.6) + ' km/h' :
    playerCombat.isReloading ? '재장전 중' :
    String(playerCombat.magazine).padStart(2, '0') + ' / ' + String(playerCombat.reserve).padStart(2, '0'));
  health.max = playerCombat.maxHealth;
  health.value = playerCombat.health;
  armor.max = playerCombat.maxArmor;
  armor.value = playerCombat.armor;
  crosshair.hidden = isDriving || !alive;
  crosshair.classList.toggle('aimed', frame.aiming);
  const prefix = input.coarse.matches ? '' : 'F · ';
  hint(vehicleAction, !alive ? '' : isDriving ? prefix + '하차' : streetAction?.vehicleId >= 0 ?
    prefix + (streetAction.vehicleOccupied ? '차량 탈취' : '탑승') : '');
  const streetLabel = streetAction?.cargoId ? '화물 훔치기' : streetAction?.canPickpocket ? '소매치기' : '';
  hint(streetHint, alive && !isDriving && streetLabel ? (input.coarse.matches ? '' : 'G · ') + streetLabel : '');
  if (streetCrime?.sequence && streetCrime.sequence !== crimeSequence) {
    crimeSequence = streetCrime.sequence;
    label(notice, crimeMessages[streetCrime.status]?.(streetCrime.amount) ?? '');
    noticeUntil = elapsed + 3.4;
  }
  if (!alive) {
    noticeUntil = elapsed + .5;
    label(notice, '쓰러졌습니다. 항만에서 다시 시작합니다.');
  }
  notice.hidden = elapsed >= noticeUntil || !notice.textContent;
  chart.radar(state, frame.position, isDriving ? vehicle.yaw : input.direction().yaw, elapsed);
}

function handleShots(shots) {
  for (const shot of shots) {
    const distance = Math.hypot(frame.position.x - shot.origin.x, frame.position.z - shot.origin.z);
    audio?.shot(shot.weaponId, distance);
  }
}

// Advance the local authority on its own clock so slow GPU frames do not slow input or physics.
function simulate() {
  const now = performance.now();
  const dt = Math.min((now - simulationTime) / 1000, .12);
  simulationTime = now;
  if (screen !== 'play' || document.hidden || contextLost) return;
  elapsed += dt;
  const { right, forward, yaw, pitch, buttons, pulses, slot } = input.sample(dt, frame.state.isDriving);
  const next = api.Tick(dt, right, forward, yaw, pitch, buttons, pulses, slot);
  if (!next) return;
  replaceFrame(next);
  handleShots(frame.shots);
  updateHud();
}

function sound() {
  const create = (name, volume) => {
    const sound = new Audio(assetUrl('assets/audio/' + name + '.ogg'));
    sound.volume = volume;
    return sound;
  };
  const base = create('dock_ambience', .12);
  const shots = { wasp_smg: create('wasp', .25), kestrel_p10: create('kestrel', .25) };
  base.loop = true;
  return {
    resume: () => base.play().catch(() => {}),
    pause: () => base.pause(),
    shot(id, distance) {
      if (distance > 65) return;
      const shot = (shots[id] ?? shots.wasp_smg).cloneNode();
      shot.volume = .32 * (1 - distance / 72);
      shot.play().catch(() => {});
    },
  };
}

async function json(path) {
  const response = await fetch(assetUrl(path));
  if (!response.ok) throw new Error('Could not load ' + path + ' (' + response.status + ')');
  return response.json();
}

function prefersLowDetail() {
  const probe = document.createElement('canvas');
  const gl = probe.getContext('webgl2');
  if (!gl) throw new Error('WebGL 2 is not available');
  const info = gl.getExtension('WEBGL_debug_renderer_info');
  const software = info && /swiftshader|llvmpipe|software rasterizer/i.test(gl.getParameter(info.UNMASKED_RENDERER_WEBGL));
  gl.getExtension('WEBGL_lose_context')?.loseContext();
  return Boolean(software || input.coarse.matches);
}

async function startGodot() {
  let ready;
  const initialized = new Promise(resolve => { ready = resolve; });
  const lowDetail = prefersLowDetail();
  globalThis.harborView = {
    ready,
    lowDetail: () => lowDetail,
    screen: () => screen,
    yaw: () => input.direction().yaw,
    pitch: () => input.direction().pitch,
    frame: () => serializedFrame,
    frameVersion: () => frameVersion,
    cameraDistance: (...args) => api.CameraDistance(...args),
    groundElevation: (x, z) => api.GroundElevation(x, z),
    requestChunk(file, complete) {
      fetch(assetUrl('godot/chunks/' + file), { cache: 'force-cache', signal: AbortSignal.timeout(12_000) })
        .then(response => {
          if (!response.ok) throw new Error('Could not load world chunk ' + file);
          return response.arrayBuffer();
        })
        .then(bytes => {
          engine.copyToFS('/tmp/harbor-world/' + file, bytes);
        })
        .then(() => complete(file, true), () => complete(file, false));
    },
    worldChunks(loaded, total) {
      canvas.dataset.loadedChunks = String(loaded);
      canvas.dataset.totalChunks = String(total);
    },
  };

  const executable = assetUrl('godot/harbor');
  await new Promise((resolve, reject) => {
    const script = document.createElement('script');
    script.src = executable + '.js';
    script.onload = resolve;
    script.onerror = () => reject(new Error('Godot launcher could not be loaded'));
    document.head.append(script);
  });
  const engine = new globalThis.Engine({
    canvas,
    executable,
    mainPack: executable + '.pck',
    focusCanvas: false,
    canvasResizePolicy: 2,
    ensureCrossOriginIsolationHeaders: false,
  });
  await engine.startGame({
    onProgress(current, total) {
      if (total > 0) status.textContent = '3D 도시 불러오는 중 · ' + Math.floor(current / total * 100) + '%';
    },
  });
  let timer;
  try {
    await Promise.race([
      initialized,
      new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('Godot scene did not initialize')), 60_000); }),
    ]);
  } finally {
    clearTimeout(timer);
  }
  canvas.dataset.engine = 'godot';
}

async function initialize() {
  const runtimePath = assetUrl('runtime/_framework/dotnet.js');
  const [runtime, plan] = await Promise.all([
    import(/* @vite-ignore */ runtimePath).then(({ dotnet }) => dotnet.create()),
    json('world/harbor_front.json'),
  ]);
  layout = plan;
  const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
  api = exports.Harbor.Browser.BrowserHarbor;
  replaceFrame(api.Start());
  chart = createChart(layout, byId('radar'), byId('atlas'), byId('map-legend'), byId('map-interiors'));
  await startGodot();
  start.textContent = '도시로 들어가기';
  status.textContent = input.coarse.matches ? '화면을 드래그해 둘러보세요.' : 'WASD 이동 · Space 점프 · 마우스 카메라 · F 차량 · M 지도';
  start.disabled = false;
  setInterval(simulate, 1000 / 30);
}

canvas.addEventListener('webglcontextlost', event => {
  event.preventDefault();
  contextLost = true;
  setScreen('intro');
  start.disabled = true;
  start.textContent = '그래픽 복구 대기 중';
  status.textContent = '그래픽 연결이 끊어졌습니다.';
});
canvas.addEventListener('webglcontextrestored', () => {
  contextLost = false;
  if (frame) setScreen('menu');
});
initialize().catch(error => {
  console.error(error);
  status.textContent = '게임을 불러올 수 없습니다. WebGL 2를 지원하는 브라우저에서 페이지를 다시 열어 보세요.';
  start.textContent = '불러오기 실패';
});
 + amount,
  service_armor: amount => '방어구 보충 · -
};

let screen = 'intro';
let mapParent = 'play';
let api, frame, layout, chart;
let serializedFrame = "";
let frameVersion = 0;
let elapsed = 0;
let simulationTime = 0;
let crimeSequence = 0;
let noticeUntil = 0;
let audio;
let hadPointerLock = false;
let contextLost = false;
const input = createInput(canvas, touch, () => screen === 'play' && frame.alive, code => {
  if (!frame || screen === 'intro') return false;
  if (code === 'KeyM') {
    if (screen === 'map') setScreen(mapParent);
    else openMap();
  } else if (screen === 'map') setScreen(mapParent);
  else setScreen(screen === 'play' ? 'menu' : 'play');
  return true;
});

document.addEventListener('pointerlockchange', () => {
  const locked = document.pointerLockElement === canvas;
  if (hadPointerLock && !locked && screen === 'play') setScreen('menu');
  hadPointerLock = locked;
});

function setScreen(next) {
  screen = next;
  simulationTime = performance.now();
  intro.hidden = next !== 'intro';
  hud.hidden = next !== 'play';
  menu.hidden = next !== 'menu';
  map.hidden = next !== 'map';
  touch.hidden = next !== 'play' || !input.coarse.matches;
  if (next === 'play') {
    input.capture();
    audio?.resume();
  } else {
    input.release();
    audio?.pause();
  }
  if (next === 'menu') byId('resume').focus();
  if (next === 'map') {
    chart.atlas(frame.position, frame.state.isDriving ? frame.state.vehicle.yaw : input.direction().yaw);
    byId('close-map').focus();
  }
}

function openMap() {
  mapParent = screen === 'menu' ? 'menu' : 'play';
  setScreen('map');
}

function replaceFrame(raw) {
  frame = JSON.parse(raw);
  serializedFrame = raw;
  frameVersion++;
}

function reset() {
  replaceFrame(api.Start());
  input.reset();
  crimeSequence = 0;
  noticeUntil = 0;
  setScreen('play');
}

byId('pause-button').addEventListener('click', () => setScreen('menu'));
byId('map-button').addEventListener('click', openMap);
byId('resume').addEventListener('click', () => setScreen('play'));
byId('menu-map').addEventListener('click', openMap);
byId('close-map').addEventListener('click', () => setScreen(mapParent));
byId('reset').addEventListener('click', reset);
start.addEventListener('click', () => {
  if (!frame) return;
  audio ??= sound();
  setScreen('play');
});

function label(el, text) {
  if (el.textContent !== text) el.textContent = text;
}
function hint(el, text) {
  el.hidden = !text;
  label(el, text);
}

function updateHud() {
  const { state, alive } = frame;
  const { streetAction, streetCrime, playerCombat, maxWantedLevel, wantedLevel, policeUnits, isDriving, vehicle } = state;
  label(district, districtAt(layout, frame.position));
  label(wallet, '$' + (streetCrime?.dirtyCash ?? 0).toLocaleString('ko-KR'));
  wanted.hidden = !wantedLevel;
  if (wantedLevel) {
    label(stars, '★'.repeat(wantedLevel) + '☆'.repeat(Math.max(0, maxWantedLevel - wantedLevel)));
    stars.setAttribute('aria-label', '수배 ' + wantedLevel + '단계');
    label(pursuit, policeUnits ? '추격 중 · ' + policeUnits + '명' : '수배 중');
  }
  label(equipment, isDriving ? vehicle.displayName : playerCombat.weaponName);
  label(ammunition, isDriving ? Math.round(Math.abs(vehicle.speed) * 3.6) + ' km/h' :
    playerCombat.isReloading ? '재장전 중' :
    String(playerCombat.magazine).padStart(2, '0') + ' / ' + String(playerCombat.reserve).padStart(2, '0'));
  health.max = playerCombat.maxHealth;
  health.value = playerCombat.health;
  armor.max = playerCombat.maxArmor;
  armor.value = playerCombat.armor;
  crosshair.hidden = isDriving || !alive;
  crosshair.classList.toggle('aimed', frame.aiming);
  const prefix = input.coarse.matches ? '' : 'F · ';
  hint(vehicleAction, !alive ? '' : isDriving ? prefix + '하차' : streetAction?.vehicleId >= 0 ?
    prefix + (streetAction.vehicleOccupied ? '차량 탈취' : '탑승') : '');
  const streetLabel = streetAction?.cargoId ? '화물 훔치기' : streetAction?.canPickpocket ? '소매치기' : '';
  hint(streetHint, alive && !isDriving && streetLabel ? (input.coarse.matches ? '' : 'G · ') + streetLabel : '');
  if (streetCrime?.sequence && streetCrime.sequence !== crimeSequence) {
    crimeSequence = streetCrime.sequence;
    label(notice, crimeMessages[streetCrime.status]?.(streetCrime.amount) ?? '');
    noticeUntil = elapsed + 3.4;
  }
  if (!alive) {
    noticeUntil = elapsed + .5;
    label(notice, '쓰러졌습니다. 항만에서 다시 시작합니다.');
  }
  notice.hidden = elapsed >= noticeUntil || !notice.textContent;
  chart.radar(state, frame.position, isDriving ? vehicle.yaw : input.direction().yaw, elapsed);
}

function handleShots(shots) {
  for (const shot of shots) {
    const distance = Math.hypot(frame.position.x - shot.origin.x, frame.position.z - shot.origin.z);
    audio?.shot(shot.weaponId, distance);
  }
}

// Advance the local authority on its own clock so slow GPU frames do not slow input or physics.
function simulate() {
  const now = performance.now();
  const dt = Math.min((now - simulationTime) / 1000, .12);
  simulationTime = now;
  if (screen !== 'play' || document.hidden || contextLost) return;
  elapsed += dt;
  const { right, forward, yaw, pitch, buttons, pulses, slot } = input.sample(dt, frame.state.isDriving);
  const next = api.Tick(dt, right, forward, yaw, pitch, buttons, pulses, slot);
  if (!next) return;
  replaceFrame(next);
  handleShots(frame.shots);
  updateHud();
}

function sound() {
  const create = (name, volume) => {
    const sound = new Audio(assetUrl('assets/audio/' + name + '.ogg'));
    sound.volume = volume;
    return sound;
  };
  const base = create('dock_ambience', .12);
  const shots = { wasp_smg: create('wasp', .25), kestrel_p10: create('kestrel', .25) };
  base.loop = true;
  return {
    resume: () => base.play().catch(() => {}),
    pause: () => base.pause(),
    shot(id, distance) {
      if (distance > 65) return;
      const shot = (shots[id] ?? shots.wasp_smg).cloneNode();
      shot.volume = .32 * (1 - distance / 72);
      shot.play().catch(() => {});
    },
  };
}

async function json(path) {
  const response = await fetch(assetUrl(path));
  if (!response.ok) throw new Error('Could not load ' + path + ' (' + response.status + ')');
  return response.json();
}

function prefersLowDetail() {
  const probe = document.createElement('canvas');
  const gl = probe.getContext('webgl2');
  if (!gl) throw new Error('WebGL 2 is not available');
  const info = gl.getExtension('WEBGL_debug_renderer_info');
  const software = info && /swiftshader|llvmpipe|software rasterizer/i.test(gl.getParameter(info.UNMASKED_RENDERER_WEBGL));
  gl.getExtension('WEBGL_lose_context')?.loseContext();
  return Boolean(software || input.coarse.matches);
}

async function startGodot() {
  let ready;
  const initialized = new Promise(resolve => { ready = resolve; });
  const lowDetail = prefersLowDetail();
  globalThis.harborView = {
    ready,
    lowDetail: () => lowDetail,
    screen: () => screen,
    yaw: () => input.direction().yaw,
    pitch: () => input.direction().pitch,
    frame: () => serializedFrame,
    frameVersion: () => frameVersion,
    cameraDistance: (...args) => api.CameraDistance(...args),
    groundElevation: (x, z) => api.GroundElevation(x, z),
    requestChunk(file, complete) {
      fetch(assetUrl('godot/chunks/' + file), { cache: 'force-cache', signal: AbortSignal.timeout(12_000) })
        .then(response => {
          if (!response.ok) throw new Error('Could not load world chunk ' + file);
          return response.arrayBuffer();
        })
        .then(bytes => {
          engine.copyToFS('/tmp/harbor-world/' + file, bytes);
        })
        .then(() => complete(file, true), () => complete(file, false));
    },
    worldChunks(loaded, total) {
      canvas.dataset.loadedChunks = String(loaded);
      canvas.dataset.totalChunks = String(total);
    },
  };

  const executable = assetUrl('godot/harbor');
  await new Promise((resolve, reject) => {
    const script = document.createElement('script');
    script.src = executable + '.js';
    script.onload = resolve;
    script.onerror = () => reject(new Error('Godot launcher could not be loaded'));
    document.head.append(script);
  });
  const engine = new globalThis.Engine({
    canvas,
    executable,
    mainPack: executable + '.pck',
    focusCanvas: false,
    canvasResizePolicy: 2,
    ensureCrossOriginIsolationHeaders: false,
  });
  await engine.startGame({
    onProgress(current, total) {
      if (total > 0) status.textContent = '3D 도시 불러오는 중 · ' + Math.floor(current / total * 100) + '%';
    },
  });
  let timer;
  try {
    await Promise.race([
      initialized,
      new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('Godot scene did not initialize')), 60_000); }),
    ]);
  } finally {
    clearTimeout(timer);
  }
  canvas.dataset.engine = 'godot';
}

async function initialize() {
  const runtimePath = assetUrl('runtime/_framework/dotnet.js');
  const [runtime, plan] = await Promise.all([
    import(/* @vite-ignore */ runtimePath).then(({ dotnet }) => dotnet.create()),
    json('world/harbor_front.json'),
  ]);
  layout = plan;
  const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
  api = exports.Harbor.Browser.BrowserHarbor;
  replaceFrame(api.Start());
  chart = createChart(layout, byId('radar'), byId('atlas'), byId('map-legend'), byId('map-interiors'));
  await startGodot();
  start.textContent = '도시로 들어가기';
  status.textContent = input.coarse.matches ? '화면을 드래그해 둘러보세요.' : 'WASD 이동 · Space 점프 · 마우스 카메라 · F 차량 · M 지도';
  start.disabled = false;
  setInterval(simulate, 1000 / 30);
}

canvas.addEventListener('webglcontextlost', event => {
  event.preventDefault();
  contextLost = true;
  setScreen('intro');
  start.disabled = true;
  start.textContent = '그래픽 복구 대기 중';
  status.textContent = '그래픽 연결이 끊어졌습니다.';
});
canvas.addEventListener('webglcontextrestored', () => {
  contextLost = false;
  if (frame) setScreen('menu');
});
initialize().catch(error => {
  console.error(error);
  status.textContent = '게임을 불러올 수 없습니다. WebGL 2를 지원하는 브라우저에서 페이지를 다시 열어 보세요.';
  start.textContent = '불러오기 실패';
});
 + amount,
  service_wanted: () => '수배 중에는 서비스를 이용할 수 없습니다.',
  service_funds: () => '게임 머니가 부족합니다.',
  service_full: () => '이미 충분히 보충되어 있습니다.',
  service_unavailable: () => '서비스 장소 가까이에서 이용하세요.',
};

let screen = 'intro';
let mapParent = 'play';
let api, frame, layout, chart;
let serializedFrame = "";
let frameVersion = 0;
let elapsed = 0;
let simulationTime = 0;
let crimeSequence = 0;
let noticeUntil = 0;
let audio;
let hadPointerLock = false;
let contextLost = false;
const input = createInput(canvas, touch, () => screen === 'play' && frame.alive, code => {
  if (!frame || screen === 'intro') return false;
  if (code === 'KeyM') {
    if (screen === 'map') setScreen(mapParent);
    else openMap();
  } else if (screen === 'map') setScreen(mapParent);
  else setScreen(screen === 'play' ? 'menu' : 'play');
  return true;
});

document.addEventListener('pointerlockchange', () => {
  const locked = document.pointerLockElement === canvas;
  if (hadPointerLock && !locked && screen === 'play') setScreen('menu');
  hadPointerLock = locked;
});

function setScreen(next) {
  screen = next;
  simulationTime = performance.now();
  intro.hidden = next !== 'intro';
  hud.hidden = next !== 'play';
  menu.hidden = next !== 'menu';
  map.hidden = next !== 'map';
  touch.hidden = next !== 'play' || !input.coarse.matches;
  if (next === 'play') {
    input.capture();
    audio?.resume();
  } else {
    input.release();
    audio?.pause();
  }
  if (next === 'menu') byId('resume').focus();
  if (next === 'map') {
    chart.atlas(frame.position, frame.state.isDriving ? frame.state.vehicle.yaw : input.direction().yaw);
    byId('close-map').focus();
  }
}

function openMap() {
  mapParent = screen === 'menu' ? 'menu' : 'play';
  setScreen('map');
}

function replaceFrame(raw) {
  frame = JSON.parse(raw);
  serializedFrame = raw;
  frameVersion++;
}

function reset() {
  replaceFrame(api.Start());
  input.reset();
  crimeSequence = 0;
  noticeUntil = 0;
  setScreen('play');
}

byId('pause-button').addEventListener('click', () => setScreen('menu'));
byId('map-button').addEventListener('click', openMap);
byId('resume').addEventListener('click', () => setScreen('play'));
byId('menu-map').addEventListener('click', openMap);
byId('close-map').addEventListener('click', () => setScreen(mapParent));
byId('reset').addEventListener('click', reset);
start.addEventListener('click', () => {
  if (!frame) return;
  audio ??= sound();
  setScreen('play');
});

function label(el, text) {
  if (el.textContent !== text) el.textContent = text;
}
function hint(el, text) {
  el.hidden = !text;
  label(el, text);
}

function updateHud() {
  const { state, alive } = frame;
  const { streetAction, streetCrime, playerCombat, maxWantedLevel, wantedLevel, policeUnits, isDriving, vehicle } = state;
  label(district, districtAt(layout, frame.position));
  label(wallet, '$' + (streetCrime?.dirtyCash ?? 0).toLocaleString('ko-KR'));
  wanted.hidden = !wantedLevel;
  if (wantedLevel) {
    label(stars, '★'.repeat(wantedLevel) + '☆'.repeat(Math.max(0, maxWantedLevel - wantedLevel)));
    stars.setAttribute('aria-label', '수배 ' + wantedLevel + '단계');
    label(pursuit, policeUnits ? '추격 중 · ' + policeUnits + '명' : '수배 중');
  }
  label(equipment, isDriving ? vehicle.displayName : playerCombat.weaponName);
  label(ammunition, isDriving ? Math.round(Math.abs(vehicle.speed) * 3.6) + ' km/h' :
    playerCombat.isReloading ? '재장전 중' :
    String(playerCombat.magazine).padStart(2, '0') + ' / ' + String(playerCombat.reserve).padStart(2, '0'));
  health.max = playerCombat.maxHealth;
  health.value = playerCombat.health;
  armor.max = playerCombat.maxArmor;
  armor.value = playerCombat.armor;
  crosshair.hidden = isDriving || !alive;
  crosshair.classList.toggle('aimed', frame.aiming);
  const prefix = input.coarse.matches ? '' : 'F · ';
  hint(vehicleAction, !alive ? '' : isDriving ? prefix + '하차' : streetAction?.vehicleId >= 0 ?
    prefix + (streetAction.vehicleOccupied ? '차량 탈취' : '탑승') : '');
  const streetLabel = streetAction?.cargoId ? '화물 훔치기' : streetAction?.canPickpocket ? '소매치기' : '';
  hint(streetHint, alive && !isDriving && streetLabel ? (input.coarse.matches ? '' : 'G · ') + streetLabel : '');
  if (streetCrime?.sequence && streetCrime.sequence !== crimeSequence) {
    crimeSequence = streetCrime.sequence;
    label(notice, crimeMessages[streetCrime.status]?.(streetCrime.amount) ?? '');
    noticeUntil = elapsed + 3.4;
  }
  if (!alive) {
    noticeUntil = elapsed + .5;
    label(notice, '쓰러졌습니다. 항만에서 다시 시작합니다.');
  }
  notice.hidden = elapsed >= noticeUntil || !notice.textContent;
  chart.radar(state, frame.position, isDriving ? vehicle.yaw : input.direction().yaw, elapsed);
}

function handleShots(shots) {
  for (const shot of shots) {
    const distance = Math.hypot(frame.position.x - shot.origin.x, frame.position.z - shot.origin.z);
    audio?.shot(shot.weaponId, distance);
  }
}

// Advance the local authority on its own clock so slow GPU frames do not slow input or physics.
function simulate() {
  const now = performance.now();
  const dt = Math.min((now - simulationTime) / 1000, .12);
  simulationTime = now;
  if (screen !== 'play' || document.hidden || contextLost) return;
  elapsed += dt;
  const { right, forward, yaw, pitch, buttons, pulses, slot } = input.sample(dt, frame.state.isDriving);
  const next = api.Tick(dt, right, forward, yaw, pitch, buttons, pulses, slot);
  if (!next) return;
  replaceFrame(next);
  handleShots(frame.shots);
  updateHud();
}

function sound() {
  const create = (name, volume) => {
    const sound = new Audio(assetUrl('assets/audio/' + name + '.ogg'));
    sound.volume = volume;
    return sound;
  };
  const base = create('dock_ambience', .12);
  const shots = { wasp_smg: create('wasp', .25), kestrel_p10: create('kestrel', .25) };
  base.loop = true;
  return {
    resume: () => base.play().catch(() => {}),
    pause: () => base.pause(),
    shot(id, distance) {
      if (distance > 65) return;
      const shot = (shots[id] ?? shots.wasp_smg).cloneNode();
      shot.volume = .32 * (1 - distance / 72);
      shot.play().catch(() => {});
    },
  };
}

async function json(path) {
  const response = await fetch(assetUrl(path));
  if (!response.ok) throw new Error('Could not load ' + path + ' (' + response.status + ')');
  return response.json();
}

function prefersLowDetail() {
  const probe = document.createElement('canvas');
  const gl = probe.getContext('webgl2');
  if (!gl) throw new Error('WebGL 2 is not available');
  const info = gl.getExtension('WEBGL_debug_renderer_info');
  const software = info && /swiftshader|llvmpipe|software rasterizer/i.test(gl.getParameter(info.UNMASKED_RENDERER_WEBGL));
  gl.getExtension('WEBGL_lose_context')?.loseContext();
  return Boolean(software || input.coarse.matches);
}

async function startGodot() {
  let ready;
  const initialized = new Promise(resolve => { ready = resolve; });
  const lowDetail = prefersLowDetail();
  globalThis.harborView = {
    ready,
    lowDetail: () => lowDetail,
    screen: () => screen,
    yaw: () => input.direction().yaw,
    pitch: () => input.direction().pitch,
    frame: () => serializedFrame,
    frameVersion: () => frameVersion,
    cameraDistance: (...args) => api.CameraDistance(...args),
    groundElevation: (x, z) => api.GroundElevation(x, z),
    requestChunk(file, complete) {
      fetch(assetUrl('godot/chunks/' + file), { cache: 'force-cache', signal: AbortSignal.timeout(12_000) })
        .then(response => {
          if (!response.ok) throw new Error('Could not load world chunk ' + file);
          return response.arrayBuffer();
        })
        .then(bytes => {
          engine.copyToFS('/tmp/harbor-world/' + file, bytes);
        })
        .then(() => complete(file, true), () => complete(file, false));
    },
    worldChunks(loaded, total) {
      canvas.dataset.loadedChunks = String(loaded);
      canvas.dataset.totalChunks = String(total);
    },
  };

  const executable = assetUrl('godot/harbor');
  await new Promise((resolve, reject) => {
    const script = document.createElement('script');
    script.src = executable + '.js';
    script.onload = resolve;
    script.onerror = () => reject(new Error('Godot launcher could not be loaded'));
    document.head.append(script);
  });
  const engine = new globalThis.Engine({
    canvas,
    executable,
    mainPack: executable + '.pck',
    focusCanvas: false,
    canvasResizePolicy: 2,
    ensureCrossOriginIsolationHeaders: false,
  });
  await engine.startGame({
    onProgress(current, total) {
      if (total > 0) status.textContent = '3D 도시 불러오는 중 · ' + Math.floor(current / total * 100) + '%';
    },
  });
  let timer;
  try {
    await Promise.race([
      initialized,
      new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('Godot scene did not initialize')), 60_000); }),
    ]);
  } finally {
    clearTimeout(timer);
  }
  canvas.dataset.engine = 'godot';
}

async function initialize() {
  const runtimePath = assetUrl('runtime/_framework/dotnet.js');
  const [runtime, plan] = await Promise.all([
    import(/* @vite-ignore */ runtimePath).then(({ dotnet }) => dotnet.create()),
    json('world/harbor_front.json'),
  ]);
  layout = plan;
  const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
  api = exports.Harbor.Browser.BrowserHarbor;
  replaceFrame(api.Start());
  chart = createChart(layout, byId('radar'), byId('atlas'), byId('map-legend'), byId('map-interiors'));
  await startGodot();
  start.textContent = '도시로 들어가기';
  status.textContent = input.coarse.matches ? '화면을 드래그해 둘러보세요.' : 'WASD 이동 · Space 점프 · 마우스 카메라 · F 차량 · M 지도';
  start.disabled = false;
  setInterval(simulate, 1000 / 30);
}

canvas.addEventListener('webglcontextlost', event => {
  event.preventDefault();
  contextLost = true;
  setScreen('intro');
  start.disabled = true;
  start.textContent = '그래픽 복구 대기 중';
  status.textContent = '그래픽 연결이 끊어졌습니다.';
});
canvas.addEventListener('webglcontextrestored', () => {
  contextLost = false;
  if (frame) setScreen('menu');
});
initialize().catch(error => {
  console.error(error);
  status.textContent = '게임을 불러올 수 없습니다. WebGL 2를 지원하는 브라우저에서 페이지를 다시 열어 보세요.';
  start.textContent = '불러오기 실패';
});
