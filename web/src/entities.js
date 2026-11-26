import * as THREE from 'three';
import { clone } from 'three/addons/utils/SkeletonUtils.js';
import { loadModel } from './world.js';

const HUMAN_ASSETS = [
  ...['rook', 'dockhand', 'crew', 'officer'].map(name => 'assets/characters/chr_harbor_' + name + '.glb'),
  'assets/characters/chr_npc_civilian.glb',
];
const STRIDES = { Walk: 1.27, Jog: 1.85, Run: 2.64, CrouchWalk: .94 };

export function angleLerp(from, to, weight) {
  return from + Math.atan2(Math.sin(to - from), Math.cos(to - from)) * weight;
}

function pose(model, pos, yaw, weight) {
  model.position.x = THREE.MathUtils.lerp(model.position.x, pos.x, weight);
  model.position.y = THREE.MathUtils.lerp(model.position.y, pos.y, weight);
  model.position.z = THREE.MathUtils.lerp(model.position.z, pos.z, weight);
  model.rotation.y = angleLerp(model.rotation.y, yaw, weight);
}

class Human {
  constructor(source) {
    this.root = clone(source.scene);
    this.root.traverse(node => {
      if (node.isMesh) {
        node.castShadow = true;
        node.receiveShadow = true;
        if (node.isSkinnedMesh) node.frustumCulled = false;
      }
    });
    this.mixer = new THREE.AnimationMixer(this.root);
    this.actions = new Map(source.animations.map(clip => [clip.name, this.mixer.clipAction(clip)]));
    this.active = null;
  }

  update(person, dt, weight) {
    pose(this.root, person.position, person.yaw + Math.PI, weight);
    this.root.rotation.z = THREE.MathUtils.damp(this.root.rotation.z, person.alive === false ? -Math.PI * .46 : 0, 9, dt);
    const { crouched, speed } = person;
    const state = crouched
      ? speed < .22 ? 'CrouchIdle' : 'CrouchWalk'
      : speed < .22 ? 'Idle' : person.fleeing || speed >= 4.35 ? 'Run' : speed < 2.45 ? 'Walk' : 'Jog';
    const next = this.actions.get(state) ?? this.actions.get('Idle');
    if (next && this.active !== next) {
      const previous = this.active;
      next.reset().setEffectiveWeight(1).fadeIn(.17).play();
      previous?.fadeOut(.17);
      this.active = next;
    }
    if (this.active) {
      const stride = STRIDES[state];
      this.active.timeScale = stride ? THREE.MathUtils.clamp(speed * this.active.getClip().duration / stride, .45, 1.7) : 1;
    }
    this.mixer.update(dt);
  }

  dispose() {
    this.mixer.stopAllAction();
    this.mixer.uncacheRoot(this.root);
  }
}

const VEHICLES = ['calloway_compact', 'pier_runner', 'breakwater_utility'];
const PAINT = ['#476d73', '#973f39', '#c4ad85', '#40527d'];
const WHEELS = ['FL', 'FR', 'RL', 'RR'];
const PAINT_NAMES = new Set(['pip_copper_orange', 'vehicle_paint']);
const BOX = new THREE.BoxGeometry();
const CARGO_WOOD = new THREE.MeshStandardMaterial({ color: '#9b7750', roughness: .88 });
const CARGO_STRAP = new THREE.MeshStandardMaterial({ color: '#f3be78', roughness: .58 });

function crate() {
  const root = new THREE.Group();
  const part = (material, x, y, z, w, h, d) => {
    const mesh = new THREE.Mesh(BOX, material);
    mesh.position.set(x, y, z);
    mesh.scale.set(w, h, d);
    root.add(mesh);
  };
  part(CARGO_WOOD, 0, .27, 0, .70, .54, .50);
  for (const side of [-1, 1]) part(CARGO_STRAP, side * .225, .28, 0, .065, .575, .522);
  return { root, dispose() {} };
}

class Traffic {
  constructor(state, model) {
    this.root = new THREE.Group();
    const chassis = model.scene.clone();
    chassis.rotation.y = Math.PI;
    this.root.add(chassis);
    this.rolls = WHEELS.map(name => chassis.getObjectByName('Wheel' + name + 'Roll')).filter(Boolean);
    this.steer = WHEELS.slice(0, 2).map(name => chassis.getObjectByName('Wheel' + name)).filter(Boolean);
    this.radius = state.wheelRadius;
    this.paint = new Map();
    this.distance = state.distance;
    const tint = state.id !== 1 && state.appearance % 4 !== 0 ? PAINT[state.appearance % PAINT.length] : null;
    const paint = (material) => {
      if (!tint || !PAINT_NAMES.has(material.name)) return material;
      if (!this.paint.has(material)) {
        const variant = material.clone();
        variant.color.set(tint);
        this.paint.set(material, variant);
      }
      return this.paint.get(material);
    };
    chassis.traverse(node => {
      if (!node.isMesh) return;
      node.castShadow = node.receiveShadow = true;
      node.material = Array.isArray(node.material) ? node.material.map(paint) : paint(node.material);
    });
  }

  update(state, dt, weight) {
    const aligned = this.root.position.distanceToSquared(state.position) > 36 ? 1 : weight;
    pose(this.root, state.position, state.yaw, aligned);
    for (const wheel of this.steer)
      wheel.rotation.y = THREE.MathUtils.damp(wheel.rotation.y, -state.steering, 14, dt);
    this.distance = THREE.MathUtils.lerp(this.distance, state.distance, aligned);
    for (const wheel of this.rolls) wheel.rotation.x = (this.distance / this.radius) % (Math.PI * 2);
  }

  dispose() {
    for (const material of this.paint.values()) material.dispose();
  }
}

function sync(scene, actors, states, create, update) {
  for (const state of states) {
    let view = actors.get(state.id);
    if (!view) {
      view = create(state);
      pose(view.root, state.position, state.yaw ?? 0, 1);
      actors.set(state.id, view);
      scene.add(view.root);
    }
    update(view, state);
  }
  const active = new Set(states.map(state => state.id));
  for (const [id, view] of actors) {
    if (active.has(id)) continue;
    scene.remove(view.root);
    view.dispose();
    actors.delete(id);
  }
}

export async function createEntities(scene, softwareGL) {
  const vehicleRange = softwareGL ? 64 : 170;
  const pedestrianRange = softwareGL ? 30 : 100;
  const [models, cars] = await Promise.all([
    Promise.all(HUMAN_ASSETS.map(loadModel)),
    Promise.all(VEHICLES.map(model => loadModel('assets/vehicles/veh_' + model + '.glb'))),
  ]);
  const carModels = new Map(VEHICLES.map((id, index) => [id, cars[index]]));
  const player = new Human(models[0]);
  scene.add(player.root);
  const pedestrians = new Map();
  const enemies = new Map();
  const vehicles = new Map();
  const cargoCaches = new Map();
  const pedestrianModels = [models[1], models[4], models[2], models[4]];
  let initialized = false;

  return {
    player,
    update(frame, dt) {
      const { state, velocity, position, aiming } = frame;
      const distance = (pos) => (pos.x - position.x) ** 2 + (pos.z - position.z) ** 2;
      const weight = initialized ? 1 - Math.exp(-17 * dt) : 1;
      initialized = true;
      player.root.visible = !state.isDriving;
      const speed = Math.hypot(velocity.x, velocity.z);
      const yaw = speed > .32 && !aiming ? Math.atan2(-velocity.x, -velocity.z) : frame.yaw;
      const transition = player.root.position.distanceToSquared(position) > 36 ? 1 : weight;
      player.update({ ...frame, yaw, speed }, dt, transition);
      const visible = state.vehicles.filter(vehicle => vehicle.id === state.playerVehicleId || distance(vehicle.position) < vehicleRange ** 2);
      sync(scene, vehicles, visible, vehicle => new Traffic(vehicle, carModels.get(vehicle.modelId) ?? cars[0]),
        (view, vehicle) => view.update(vehicle, dt, weight));
      sync(scene, cargoCaches, (state.ambient.cargo ?? []).filter(cache => cache.available), crate, () => {});
      sync(scene, pedestrians, state.ambient.pedestrians.filter(person => distance(person.position) < pedestrianRange ** 2),
        person => new Human(pedestrianModels[person.appearance % pedestrianModels.length]),
        (view, person) => view.update(person, dt, weight));
      sync(scene, enemies, state.enemies, enemy => new Human(models[enemy.team === 3 ? 3 : 2]),
        (view, enemy) => view.update({
          ...enemy,
          crouched: enemy.flags.includes('crouched'),
          alive: !enemy.flags.includes('dead'),
          speed: Math.hypot(enemy.velocity.x, enemy.velocity.z),
        }, dt, weight));
      return vehicles.get(state.playerVehicleId ?? 1)?.root ?? null;
    },
  };
}
