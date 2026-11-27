const COLORS = {
  water: "#143f51",
  lot: "#1b313a",
  grid: "#233f4a",
  road: "#39555e",
  structure: "#37515a",
  border: "#51727c",
  white: "#faf1dd",
  amber: "#f7bc70",
  seafoam: "#88ded5",
  enemy: "#ff7665",
  police: "#86b7ff",
};

export function drawHarborChart(canvas, mapData, frame, options = {}) {
  if (!canvas || !mapData || !frame) return;
  const ctx = canvas.getContext("2d");
  if (!ctx) return;

  const w = canvas.width;
  const h = canvas.height;
  const entire = Boolean(options.entireDistrict);
  const trackedJob = options.trackedJob ?? null;

  const bMinX = mapData.min[0];
  const bMinZ = mapData.min[1];
  const bWidth = mapData.max[0] - bMinX;
  const bHeight = mapData.max[1] - bMinZ;

  const px = frame.player.position[0];
  const pz = frame.player.position[2];

  let viewW;
  let viewH;
  let viewX;
  let viewZ;

  if (entire) {
    viewW = Math.max(bWidth, (bHeight * w) / h) * 1.05;
    viewH = (viewW * h) / w;
    viewX = bMinX + bWidth * 0.5 - viewW * 0.5;
    viewZ = bMinZ + bHeight * 0.5 - viewH * 0.5;
  } else {
    viewW = frame.isDriving ? 102 : 64;
    viewH = (viewW * h) / w;
    viewX = px - viewW * 0.5;
    viewZ = pz - viewH * 0.5;
  }

  const scale = w / viewW;
  const toX = (wx) => (wx - viewX) * scale;
  const toY = (wz) => (wz - viewZ) * scale;

  ctx.fillStyle = COLORS.water;
  ctx.fillRect(0, 0, w, h);

  ctx.fillStyle = COLORS.lot;
  ctx.fillRect(toX(bMinX), toY(bMinZ), bWidth * scale, bHeight * scale);

  ctx.fillStyle = COLORS.water;
  for (const area of mapData.water) {
    ctx.fillRect(toX(area[0]), toY(area[1]), area[2] * scale, area[3] * scale);
  }

  ctx.strokeStyle = COLORS.grid;
  ctx.lineWidth = 1;
  ctx.beginPath();
  for (let gx = Math.floor(viewX / 20) * 20; gx < viewX + viewW; gx += 20) {
    const sx = toX(gx);
    ctx.moveTo(sx, 0);
    ctx.lineTo(sx, h);
  }
  for (let gz = Math.floor(viewZ / 20) * 20; gz < viewZ + viewH; gz += 20) {
    const sy = toY(gz);
    ctx.moveTo(0, sy);
    ctx.lineTo(w, sy);
  }
  ctx.stroke();

  ctx.fillStyle = COLORS.road;
  for (const road of mapData.roads) {
    ctx.fillRect(toX(road[0]), toY(road[1]), road[2] * scale, road[3] * scale);
  }

  ctx.fillStyle = COLORS.structure;
  const minArea = entire ? 10 : 1;
  for (const obs of mapData.obstacles) {
    if (obs[2] * obs[3] <= minArea) continue;
    if (
      obs[0] + obs[2] < viewX ||
      obs[0] > viewX + viewW ||
      obs[1] + obs[3] < viewZ ||
      obs[1] > viewZ + viewH
    ) {
      continue;
    }
    ctx.fillRect(toX(obs[0]), toY(obs[1]), obs[2] * scale, obs[3] * scale);
  }

  // Objectives
  const drawDiamond = (wx, wz, color) => {
    const rawX = toX(wx);
    const rawY = toY(wz);
    const pad = 7;
    const inside = rawX >= pad && rawX <= w - pad && rawY >= pad && rawY <= h - pad;
    const ax = Math.max(pad, Math.min(w - pad, rawX));
    const ay = Math.max(pad, Math.min(h - pad, rawY));

    ctx.fillStyle = color;
    ctx.beginPath();
    ctx.moveTo(ax, ay - 4.8);
    ctx.lineTo(ax + 4.8, ay);
    ctx.lineTo(ax, ay + 4.8);
    ctx.lineTo(ax - 4.8, ay);
    ctx.closePath();
    ctx.fill();

    if (inside) {
      ctx.strokeStyle = color;
      ctx.lineWidth = 1.2;
      ctx.beginPath();
      ctx.arc(ax, ay, 7.2, 0, Math.PI * 2);
      ctx.stroke();
    }
  };

  if (frame.jobs?.active) {
    drawDiamond(frame.jobs.active.waypoint[0], frame.jobs.active.waypoint[2], COLORS.seafoam);
  } else if (frame.lockdown?.phase === "Extraction") {
    drawDiamond(
      frame.lockdown.extractionPosition[0],
      frame.lockdown.extractionPosition[2],
      COLORS.seafoam
    );
  } else if (frame.lockdown?.phase === "FreeRoam") {
    if (!trackedJob) {
      drawDiamond(frame.lockdown.startPosition[0], frame.lockdown.startPosition[2], COLORS.amber);
    }
    for (const offer of frame.jobs?.available || []) {
      if (offer.id === trackedJob) {
        drawDiamond(offer.startPosition[0], offer.startPosition[2], COLORS.amber);
      } else {
        const ox = toX(offer.startPosition[0]);
        const oy = toY(offer.startPosition[2]);
        if (ox >= 4 && ox <= w - 4 && oy >= 4 && oy <= h - 4) {
          ctx.strokeStyle = COLORS.amber;
          ctx.lineWidth = 1.5;
          ctx.beginPath();
          ctx.arc(ox, oy, 3.2, 0, Math.PI * 2);
          ctx.stroke();
        }
      }
    }
  }

  // Parked vehicle
  if (!frame.isDriving && frame.vehicle) {
    const vx = toX(frame.vehicle.position[0]);
    const vy = toY(frame.vehicle.position[2]);
    if (vx >= 4 && vx <= w - 4 && vy >= 4 && vy <= h - 4) {
      ctx.fillStyle = COLORS.seafoam;
      ctx.fillRect(vx - 3.5, vy - 2.5, 7, 5);
    }
  }

  // Enemies & police
  for (const enemy of frame.enemies || []) {
    if (!enemy.alive) continue;
    const ex = toX(enemy.position[0]);
    const ey = toY(enemy.position[2]);
    if (ex >= -4 && ex <= w + 4 && ey >= -4 && ey <= h + 4) {
      ctx.fillStyle = enemy.team === 3 ? COLORS.police : COLORS.enemy;
      ctx.beginPath();
      ctx.arc(ex, ey, 2.8, 0, Math.PI * 2);
      ctx.fill();
    }
  }

  // Player heading chevron
  const hereX = toX(px);
  const hereY = toY(pz);
  const camYaw = frame.camera.yaw || 0;
  const hx = -Math.sin(camYaw);
  const hy = -Math.cos(camYaw);
  const sx = hy;
  const sy = -hx;

  ctx.fillStyle = COLORS.white;
  ctx.beginPath();
  ctx.moveTo(hereX + hx * 8, hereY + hy * 8);
  ctx.lineTo(hereX - hx * 5 + sx * 4.5, hereY - hy * 5 + sy * 4.5);
  ctx.lineTo(hereX - hx * 2, hereY - hy * 2);
  ctx.lineTo(hereX - hx * 5 - sx * 4.5, hereY - hy * 5 - sy * 4.5);
  ctx.closePath();
  ctx.fill();

  ctx.strokeStyle = COLORS.border;
  ctx.lineWidth = 1;
  ctx.strokeRect(0.5, 0.5, w - 1, h - 1);
}
