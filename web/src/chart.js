import { DISTRICT_COLORS } from './map-data.js';

function rectangle(ctx, area, color) {
  ctx.save();
  ctx.translate(...area.center);
  ctx.rotate(-(area.yaw ?? 0) * Math.PI / 180);
  ctx.fillStyle = color;
  ctx.fillRect(-area.size[0] / 2, -area.size[1] / 2, ...area.size);
  ctx.restore();
}

export function createChart(layout, radar, atlas, legend, interiorLegend) {
  const { min, max } = layout.bounds;
  const worldWidth = max[0] - min[0];
  const worldHeight = max[1] - min[1];
  const map = document.createElement('canvas');
  const density = 1.15;
  map.width = Math.ceil(worldWidth * density);
  map.height = Math.ceil(worldHeight * density);
  const painter = map.getContext('2d');
  painter.fillStyle = '#183d4c';
  painter.fillRect(0, 0, map.width, map.height);
  painter.scale(density, density);
  painter.translate(-min[0], -min[1]);
  for (const land of layout.ground ?? []) rectangle(painter, land, DISTRICT_COLORS[land.theme] ?? '#749292');
  for (const water of layout.water ?? []) rectangle(painter, water, '#183d4c');
  for (const road of layout.roads) rectangle(painter, road, road.kind === 'path' ? '#657b65' : '#3c5156');
  painter.fillStyle = '#32464b';
  for (const obstacle of layout.obstacles) {
    if (obstacle.size[1] < 1.1 || obstacle.size[0] * obstacle.size[2] < 4) continue;
    rectangle(painter, { center: [obstacle.center[0], obstacle.center[2]], size: [obstacle.size[0], obstacle.size[2]] }, '#32464b');
  }
  for (const district of layout.districts ?? []) {
    const item = document.createElement('li');
    const key = document.createElement('span');
    key.style.backgroundColor = DISTRICT_COLORS[district.theme];
    item.append(key, district.name);
    legend.append(item);
  }
  const radarContext = radar.getContext('2d');
  const atlasContext = atlas.getContext('2d');
  const interiors = layout.landmarks.filter(landmark => landmark.kind === 'interior');
  if (interiors.length) {
    interiorLegend.textContent = '◆ 출입 가능 · ' + interiors.map(room => room.name).join(' · ');
  }
  let lastRadarAt = -Infinity;

  function base(ctx, center, scale) {
    const { width, height } = ctx.canvas;
    ctx.fillStyle = '#183d4c';
    ctx.fillRect(0, 0, width, height);
    const point = (x, z) => [width / 2 + (x - center.x) * scale, height / 2 + (z - center.z) * scale];
    ctx.drawImage(map, ...point(...min), worldWidth * scale, worldHeight * scale);
    return point;
  }

  function dot(ctx, x, y, color, radius) {
    ctx.fillStyle = color;
    ctx.beginPath();
    ctx.arc(x, y, radius, 0, Math.PI * 2);
    ctx.fill();
  }

  function entrance(ctx, x, y, radius) {
    ctx.save();
    ctx.translate(x, y);
    ctx.rotate(Math.PI / 4);
    ctx.fillStyle = '#fac27d';
    ctx.strokeStyle = '#2c2724';
    ctx.lineWidth = 1.7;
    ctx.fillRect(-radius, -radius, radius * 2, radius * 2);
    ctx.strokeRect(-radius, -radius, radius * 2, radius * 2);
    ctx.restore();
  }

  function player(ctx, x, y, yaw) {
    ctx.save();
    ctx.translate(x, y);
    ctx.rotate(-yaw);
    ctx.beginPath();
    ctx.moveTo(0, -9);
    ctx.lineTo(-5, 6);
    ctx.lineTo(0, 3);
    ctx.lineTo(5, 6);
    ctx.closePath();
    ctx.lineWidth = 3;
    ctx.strokeStyle = '#233332';
    ctx.stroke();
    ctx.fillStyle = '#fff2db';
    ctx.fill();
    ctx.restore();
  }

  return {
    radar(state, position, yaw, elapsed) {
      if (elapsed - lastRadarAt < .06) return;
      lastRadarAt = elapsed;
      const ctx = radarContext;
      const point = base(ctx, position, radar.width / (state.isDriving ? 134 : 88));
      for (const car of state.vehicles ?? []) {
        if (car.id === state.playerVehicleId) continue;
        dot(ctx, ...point(car.position.x, car.position.z), car.occupied ? '#e2b378' : '#aee1ce', 2.3);
      }
      for (const enemy of state.enemies) {
        if (enemy.team === 3 && !enemy.flags.includes('dead')) dot(ctx, ...point(enemy.position.x, enemy.position.z), '#91c8ff', 3);
      }
      for (const room of interiors) entrance(ctx, ...point(room.position[0], room.position[2]), 2.3);
      player(ctx, ...point(position.x, position.z), yaw);
      ctx.fillStyle = '#fff2db';
      ctx.font = 'bold 11px sans-serif';
      ctx.fillText('N', 7, 13);
    },
    atlas(position, yaw) {
      const ctx = atlasContext;
      const scale = Math.min((atlas.width - 40) / worldWidth, (atlas.height - 32) / worldHeight);
      const point = base(ctx, { x: (min[0] + max[0]) * .5, z: (min[1] + max[1]) * .5 }, scale);
      ctx.font = 'bold 15px sans-serif';
      ctx.textAlign = 'center';
      for (const district of layout.districts ?? []) {
        const [x, y] = point(...district.center);
        ctx.lineWidth = 4;
        ctx.strokeStyle = '#1d3237';
        ctx.strokeText(district.name, x, y);
        ctx.fillStyle = '#fff0d7';
        ctx.fillText(district.name, x, y);
      }
      for (const room of interiors) entrance(ctx, ...point(room.position[0], room.position[2]), 4.0);
      player(ctx, ...point(position.x, position.z), yaw);
      ctx.textAlign = 'left';
    },
  };
}
