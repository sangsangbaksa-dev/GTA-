import * as THREE from 'three';
import { createChart } from './chart.js';
import { angleLerp, createEntities } from './entities.js';
import { createInput } from './input.js';
import { assetUrl, createView, districtAt } from './world.js';
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
const crimeMessages = {
  pickpocketed: (amount) => '소매치기  +$' + amount.toLocaleString('ko-KR'),
  cargo_taken: (amount) => '화물 절도  +$' + amount.toLocaleString('ko-KR'),
  spotted: () => '들켰습니다. 주변에 신고가 접수됐습니다.',
  vehicle_taken: () => '차량 탈취가 신고됐습니다.',
  no_target: () => '가까운 대상이 없습니다.',
  exit_blocked: () => '차량을 멈추고 문 옆 공간을 확보하세요.',
};

let screen = 'intro';
let mapParent = 'play';
let api, frame, layout, view, entities, chart;
let elapsed = 0;
let previousTime = 0;
let simulationTime = 0;
let crimeSequence = 0;
let noticeUntil = 0;
let audio;
let vehicleView = null;
let cameraArm = 5.7;
let chaseYaw = 0;
let hadPointerLock = false;
let contextLost = false;
const focus = new THREE.Vector3();
const cameraOffset = new THREE.Vector3();
const desired = new THREE.Vector3();
const aim = new THREE.Vector3();
const dir = new THREE.Vector3();
const bullet = new THREE.Color('#ffe2a4');
const traces = [];
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

function reset() {
  frame = JSON.parse(api.Start());
  input.reset();
  crimeSequence = 0;
  noticeUntil = 0;
  chaseYaw = 0;
  for (const item of traces) disposeTrace(item);
  traces.length = 0;
  entities.player.root.position.copy(frame.position);
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

function camera(dt) {
  const { state } = frame;
  if (screen === 'intro') {
    focus.copy(frame.position);
    view.camera.position.set(focus.x + 31, 13, focus.z + 22);
    view.camera.lookAt(focus.x + 7, 1.5, focus.z - 26);
    return;
  }
  const driving = state.isDriving && vehicleView;
  const orientation = input.direction();
  chaseYaw = driving ? angleLerp(chaseYaw, state.vehicle.yaw, 1 - Math.exp(-5 * dt)) : orientation.yaw;
  focus.copy(driving ? vehicleView.position : entities.player.root.position);
  const pivotHeight = driving ? 1.12 : frame.crouched ? 1.08 : 1.48;
  aim.copy(focus).addScaledVector(UP, pivotHeight);
  const pitch = driving ? -.26 : orientation.pitch;
  const radius = driving ? 7.1 : frame.aiming ? 2.8 : 4.9;
  const shoulder = driving ? 0 : frame.aiming ? .62 : .46;
  cameraOffset.set(Math.sin(chaseYaw) * radius * Math.cos(pitch) + Math.cos(chaseYaw) * shoulder,
    -Math.sin(pitch) * radius + (driving ? 1.05 : .58),
    Math.cos(chaseYaw) * radius * Math.cos(pitch) - Math.sin(chaseYaw) * shoulder);
  const distance = cameraOffset.length();
  dir.copy(cameraOffset).divideScalar(distance);
  const limit = api.CameraDistance(aim.x, aim.y, aim.z, dir.x, dir.y, dir.z, distance);
  cameraArm = Math.min(limit, THREE.MathUtils.damp(cameraArm, limit, 10, dt));
  desired.copy(aim).addScaledVector(dir, cameraArm);
  view.camera.position.copy(desired);
  if (driving) view.camera.lookAt(aim.x - Math.sin(chaseYaw) * 1.8, aim.y + .22, aim.z - Math.cos(chaseYaw) * 1.8);
  else {
    const shotPitch = orientation.pitch;
    dir.set(-Math.sin(chaseYaw) * Math.cos(shotPitch), Math.sin(shotPitch), -Math.cos(chaseYaw) * Math.cos(shotPitch));
    view.camera.lookAt(aim.addScaledVector(dir, 20));
  }
}

const UP = new THREE.Vector3(0, 1, 0);

function handleShots(shots) {
  for (const shot of shots) {
    const geometry = new THREE.BufferGeometry().setFromPoints([shot.origin, shot.end]);
    const material = new THREE.LineBasicMaterial({ color: bullet, transparent: true });
    const trail = new THREE.Line(geometry, material);
    view.scene.add(trail);
    traces.push({ trail, expire: elapsed + .125 });
    const distance = Math.hypot(frame.position.x - shot.origin.x, frame.position.z - shot.origin.z);
    audio?.shot(shot.weaponId, distance);
  }
}

function disposeTrace({ trail }) {
  view.scene.remove(trail);
  trail.geometry.dispose();
  trail.material.dispose();
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
  frame = JSON.parse(next);
  handleShots(frame.shots);
  updateHud();
}

function render(now) {
  const dt = Math.min((now - (previousTime || now)) / 1000, .12);
  previousTime = now;
  if (!document.hidden && !contextLost && (screen === 'play' || screen === 'intro')) {
    if (screen === 'intro') elapsed += dt;
    for (let i = traces.length - 1; i >= 0; i--) {
      const trace = traces[i];
      const remaining = trace.expire - elapsed;
      if (remaining > 0) trace.trail.material.opacity = Math.min(1, remaining * 8);
      else {
        disposeTrace(trace);
        traces.splice(i, 1);
      }
    }
    vehicleView = entities.update(frame, dt);
    camera(dt);
    view.render(elapsed, focus);
  }
  requestAnimationFrame(render);
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

async function initialize() {
  const runtimePath = assetUrl('runtime/_framework/dotnet.js');
  view = createView(canvas);
  const runtimePromise = import(/* @vite-ignore */ runtimePath).then(({ dotnet }) => dotnet.create());
  const [runtime, plan, visuals] = await Promise.all([
    runtimePromise, json('world/harbor_front.json'), json('world/harbor_visuals.json'),
  ]);
  layout = plan;
  const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
  api = exports.Harbor.Browser.BrowserHarbor;
  frame = JSON.parse(api.Start());
  [entities] = await Promise.all([createEntities(view.scene, view.softwareGL), view.populate(visuals, layout)]);
  chart = createChart(layout, byId('radar'), byId('atlas'), byId('map-legend'));
  start.textContent = '도시로 들어가기';
  status.textContent = input.coarse.matches ? '화면을 드래그해 둘러보세요.' : 'WASD 이동 · 마우스 카메라 · F 차량 · M 지도';
  start.disabled = false;
  setInterval(simulate, 1000 / 30);
  requestAnimationFrame(render);
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
