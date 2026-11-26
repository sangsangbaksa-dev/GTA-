import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { mergeGeometries } from 'three/addons/utils/BufferGeometryUtils.js';

const DEG = Math.PI / 180;
const PLANE = new THREE.PlaneGeometry(1, 1);
const roof = new THREE.Shape().moveTo(-.5, -.5).lineTo(.5, -.5).lineTo(0, .5).closePath();
const SHAPES = {
  box: new THREE.BoxGeometry(),
  cylinder: new THREE.CylinderGeometry(.5, .5, 1, 12),
  sphere: new THREE.SphereGeometry(.5, 12, 8),
  cone: new THREE.ConeGeometry(.5, 1, 12),
  gable: new THREE.ExtrudeGeometry(roof, { depth: 1, bevelEnabled: false }).translate(0, 0, -.5),
};
const url = (path) => new URL(path.replace(/^res:\/\//, ''), document.baseURI).href;
export const assetUrl = url;
const loader = new GLTFLoader();
const models = new Map();

export function loadModel(path) {
  if (!models.has(path)) models.set(path, loader.loadAsync(url(path)));
  return models.get(path);
}

export const DISTRICT_COLORS = {
  harbor: '#789b9e',
  city: '#9cbabe',
  low_income: '#b9a183',
  desert: '#c38e5d',
  military: '#a5a587',
  forest: '#618f73',
  wealthy: '#87a783',
};

export function districtAt(layout, position) {
  const districts = layout.districts ?? [];
  const contains = ({ center, size }) =>
    Math.abs(position.x - center[0]) <= size[0] / 2 && Math.abs(position.z - center[1]) <= size[1] / 2;
  return districts.find(contains)?.name ?? 'Port Calloway';
}

function sky(scene) {
  const dome = new THREE.Mesh(new THREE.SphereGeometry(1, 28, 14), new THREE.ShaderMaterial({
    side: THREE.BackSide,
    depthWrite: false,
    uniforms: {
      zenith: { value: new THREE.Color('#6198ba') },
      horizon: { value: new THREE.Color('#d2d9cb') },
      bright: { value: new THREE.Color('#ffd9a3') },
    },
    vertexShader: `
      varying vec3 direction;
      void main() {
        direction = position;
        gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
      }`,
    fragmentShader: `
      uniform vec3 zenith, horizon, bright;
      varying vec3 direction;
      void main() {
        vec3 ray = normalize(direction);
        float height = smoothstep(-.10, .72, ray.y);
        float glow = pow(max(0.0, dot(ray, normalize(vec3(-.55,.23,-.56)))), 9.0);
        float sun = smoothstep(.99955, .99977, dot(ray, normalize(vec3(-.55,.23,-.56))));
        gl_FragColor = vec4(mix(horizon, zenith, height) + bright * (glow * .22 + sun), 1.0);
        #include <tonemapping_fragment>
        #include <colorspace_fragment>
      }`,
  }));
  dome.frustumCulled = false;
  scene.add(dome);
  return dome;
}

function waterMaterial() {
  return new THREE.ShaderMaterial({
    uniforms: {
      time: { value: 0 },
      deep: { value: new THREE.Color('#22617a') },
      sheen: { value: new THREE.Color('#a8d0d1') },
      haze: { value: new THREE.Color('#b4cad0') },
    },
    vertexShader: `
      varying vec3 worldPoint;
      void main() {
        worldPoint = (modelMatrix * vec4(position, 1.0)).xyz;
        gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
      }`,
    fragmentShader: `
      uniform float time;
      uniform vec3 deep, sheen, haze;
      varying vec3 worldPoint;
      void main() {
        vec2 p = worldPoint.xz;
        float ripple = sin(p.x * 1.9 + p.y * .73 - time * 2.5)
          * sin(p.x * -.72 + p.y * 1.85 + time * 1.5);
        float longWave = sin(p.y * .23 + p.x * .52 + time * .8);
        vec3 view = cameraPosition - worldPoint;
        float glancing = pow(1.0 - abs(normalize(view).y), 4.0);
        vec3 color = mix(deep, sheen, .13 + glancing * .43 + ripple * .07 + longWave * .05);
        float fog = smoothstep(110.0, 630.0, length(view));
        gl_FragColor = vec4(mix(color, haze, fog), 1.0);
        #include <tonemapping_fragment>
        #include <colorspace_fragment>
      }`,
  });
}

export function createView(canvas) {
  const scene = new THREE.Scene();
  scene.fog = new THREE.Fog('#b4cad0', 155, 660);
  const camera = new THREE.PerspectiveCamera(63, 1, .08, 2100);
  const renderer = new THREE.WebGLRenderer({ canvas, antialias: true, powerPreference: 'high-performance' });
  renderer.toneMapping = THREE.ACESFilmicToneMapping;
  renderer.toneMappingExposure = 1.27;
  const gl = renderer.getContext();
  const info = gl.getExtension('WEBGL_debug_renderer_info');
  const softwareGL = info && /swiftshader|llvmpipe/i.test(gl.getParameter(info.UNMASKED_RENDERER_WEBGL));
  renderer.setPixelRatio(softwareGL ? .67 : Math.min(devicePixelRatio, 1.6));
  renderer.shadowMap.enabled = !softwareGL && !matchMedia('(pointer: coarse)').matches;
  renderer.shadowMap.type = THREE.PCFShadowMap;
  scene.add(new THREE.HemisphereLight('#d0eaff', '#a18571', 2.1));
  const sun = new THREE.DirectionalLight('#ffdcab', 2.95);
  sun.castShadow = renderer.shadowMap.enabled;
  sun.shadow.mapSize.set(1024, 1024);
  Object.assign(sun.shadow.camera, { left: -42, right: 42, bottom: -42, top: 42, near: 1, far: 155 });
  sun.shadow.normalBias = .06;
  scene.add(sun, sun.target);
  const dome = sky(scene);
  dome.scale.setScalar(softwareGL ? 410 : 1300);
  camera.far = softwareGL ? 420 : 1500;
  if (softwareGL) scene.fog = new THREE.Fog('#b4cad0', 112, 385);
  const water = waterMaterial();
  const detailBatches = [];

  const resize = () => {
    const { clientWidth, clientHeight } = canvas;
    renderer.setSize(Math.max(1, clientWidth), Math.max(1, clientHeight), false);
    camera.aspect = clientWidth / Math.max(1, clientHeight);
    camera.updateProjectionMatrix();
  };
  window.addEventListener('resize', resize);
  resize();

  return {
    scene, camera, softwareGL,
    render(seconds, focus) {
      water.uniforms.time.value = seconds;
      sun.target.position.copy(focus);
      sun.position.copy(focus).add(SUN_OFFSET);
      dome.position.copy(camera.position);
      if (softwareGL) {
        for (const batch of detailBatches) {
          const bounds = batch.boundingSphere;
          const radius = batch.userData.drawDistance + bounds.radius;
          batch.visible = bounds.center.distanceToSquared(camera.position) <= radius * radius;
        }
      }
      renderer.render(scene, camera);
    },
    async populate(visuals, layout) {
      addPrimitives(scene, visuals, softwareGL, detailBatches);
      const waterHeight = visuals.waterLevel ?? -.8;
      const { min, max } = layout.bounds;
      const ocean = new THREE.Mesh(PLANE, water);
      ocean.rotation.x = -Math.PI / 2;
      ocean.scale.set(max[0] - min[0] + 4200, max[1] - min[1] + 4200, 1);
      ocean.position.set((min[0] + max[0]) / 2, waterHeight, (min[1] + max[1]) / 2);
      scene.add(ocean);
      for (const sign of visuals.signs) addSign(scene, sign);
      await addPlacedModels(scene, visuals.assets, softwareGL, detailBatches);
    },
  };
}

const SUN_OFFSET = new THREE.Vector3(-42, 69, -40);

function setTransform(object, position, scale, rotation) {
  object.position.fromArray(position);
  object.scale.fromArray(scale);
  object.rotation.set(...rotation.map(angle => angle * DEG), 'YXZ');
  object.updateMatrix();
}

function sector(position, cellSize) {
  return Math.floor(position[0] / cellSize) + ':' + Math.floor(position[2] / cellSize);
}

// These authored static models use solid material colors. Bake those colors and
// local transforms once so software WebGL needs one draw per model in each cell.
function staticColorMesh(meshes) {
  const parts = meshes.map(mesh => {
    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute('position', mesh.geometry.attributes.position.clone());
    geometry.setAttribute('normal', mesh.geometry.attributes.normal.clone());
    geometry.setIndex(mesh.geometry.index);
    geometry.applyMatrix4(mesh.matrixWorld);
    const count = geometry.attributes.position.count;
    const colors = new Float32Array(count * 3);
    for (let i = 0; i < count; i++) mesh.material.color.toArray(colors, i * 3);
    geometry.setAttribute('color', new THREE.BufferAttribute(colors, 3));
    return geometry;
  });
  return new THREE.Mesh(mergeGeometries(parts), new THREE.MeshLambertMaterial({ vertexColors: true }));
}

async function addPlacedModels(scene, placements, softwareGL, detailBatches) {
  const paths = Map.groupBy(placements, placement => placement.path);
  await Promise.all([...paths].map(async ([path, items]) => {
    const { scene: source } = await loadModel(path);
    source.updateMatrixWorld(true);
    const meshes = [];
    source.traverseVisible(node => {
      if (node.isMesh && !/-col(?:only)?$/i.test(node.name)) meshes.push(node);
    });
    const drawMeshes = softwareGL ? [staticColorMesh(meshes)] : meshes;
    const transform = new THREE.Object3D();
    const cellSize = softwareGL ? 40 : 160;
    for (const chunk of Map.groupBy(items, item => sector(item.position, cellSize)).values()) {
      const transforms = chunk.map(item => {
        setTransform(transform, item.position, item.scale, item.rotation);
        return transform.matrix.clone();
      });
      for (const mesh of drawMeshes) {
        const batch = new THREE.InstancedMesh(mesh.geometry, mesh.material, transforms.length);
        batch.castShadow = batch.receiveShadow = true;
        const matrix = new THREE.Matrix4();
        transforms.forEach((placement, index) => batch.setMatrixAt(index, matrix.multiplyMatrices(placement, mesh.matrixWorld)));
        batch.computeBoundingSphere();
        if (softwareGL) {
          batch.userData.drawDistance = /buildings|crane|beacon|pilot_tug/.test(path) ? 210 : 75;
          detailBatches.push(batch);
        }
        scene.add(batch);
      }
    }
  }));
}

function addPrimitives(scene, visuals, softwareGL, detailBatches) {
  const materials = new Map(visuals.materials.map(({ id, color, roughness, metallic }) =>
    [id, new THREE.MeshStandardMaterial({
      color: new THREE.Color().setRGB(...color, THREE.SRGBColorSpace),
      roughness: roughness ?? .92,
      metalness: metallic ?? 0,
    })]));
  const flat = new THREE.MeshLambertMaterial();
  const buckets = Map.groupBy(visuals.primitives, item => {
    if (!softwareGL) return [item.shape, item.material, sector(item.center, 132)].join('|');
    const landmark = Math.max(...item.size) >= 12;
    return [item.shape, landmark, sector(item.center, landmark ? 100 : 40)].join('|');
  });
  const transform = new THREE.Object3D();
  for (const items of buckets.values()) {
    const first = items[0];
    const geometry = SHAPES[first.shape] ?? SHAPES.box;
    const material = materials.get(first.material);
    if (!material) throw new Error('World primitive has no material ' + first.material);
    const batch = new THREE.InstancedMesh(geometry, softwareGL ? flat : material, items.length);
    batch.receiveShadow = true;
    batch.castShadow = items.some(item => item.size[1] > 1.1 && Math.max(item.size[0], item.size[2]) < 120);
    for (const [index, item] of items.entries()) {
      setTransform(transform, item.center, item.size, item.rotation ?? [0, 0, 0]);
      batch.setMatrixAt(index, transform.matrix);
      if (softwareGL) batch.setColorAt(index, materials.get(item.material).color);
    }
    batch.computeBoundingSphere();
    if (softwareGL) {
      batch.userData.drawDistance = Math.max(...first.size) >= 12 ? 420 : 83;
      detailBatches.push(batch);
    }
    scene.add(batch);
  }
}

function addSign(scene, sign) {
  const canvas = document.createElement('canvas');
  canvas.width = 512;
  canvas.height = 96;
  const ctx = canvas.getContext('2d');
  ctx.fillStyle = '#183c40';
  ctx.fillRect(0, 0, canvas.width, canvas.height);
  ctx.strokeStyle = '#b0d5c8';
  ctx.lineWidth = 2;
  ctx.strokeRect(7, 7, canvas.width - 14, canvas.height - 14);
  ctx.fillStyle = '#fff7df';
  ctx.font = 'bold 38px system-ui, sans-serif';
  ctx.textAlign = 'center';
  ctx.textBaseline = 'middle';
  ctx.fillText(sign.text, canvas.width / 2, canvas.height / 2, canvas.width - 34);
  const map = new THREE.CanvasTexture(canvas);
  map.colorSpace = THREE.SRGBColorSpace;
  const mesh = new THREE.Mesh(PLANE, new THREE.MeshBasicMaterial({ map, side: THREE.DoubleSide }));
  mesh.position.fromArray(sign.position);
  mesh.rotation.set(...(sign.rotation ?? [0, 0, 0]).map(angle => angle * DEG), 'YXZ');
  mesh.scale.set(sign.width, sign.height, 1);
  scene.add(mesh);
}
