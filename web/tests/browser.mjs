import assert from 'node:assert/strict';
import { chromium } from 'playwright';
import { preview } from 'vite';

const previewServer = await preview({ preview: { host: '127.0.0.1', port: 0, strictPort: true } });
const address = previewServer.httpServer.address();
const executablePath = process.env.CHROMIUM_PATH;
const browser = await chromium.launch({
  executablePath,
  args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader'],
});

try {
  const page = await browser.newPage({ viewport: { width: 850, height: 550 } });
  page.setDefaultTimeout(35_000);
  const errors = [];
  const fetchedChunks = new Set();
  page.on('response', response => {
    if (response.ok() && response.url().includes('/godot/chunks/')) fetchedChunks.add(response.url());
  });
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => {
    if (message.type() === 'error') errors.push(message.text());
  });
  await page.goto('http://127.0.0.1:' + address.port + '/', { waitUntil: 'domcontentloaded' });
  const start = page.getByRole('button', { name: '도시로 들어가기' });
  await start.waitFor({ timeout: 75_000 }).catch(error => {
    throw new Error('Browser could not load the sandbox: ' + errors.join('\n'), { cause: error });
  });
  const game = page.locator('canvas[data-engine="godot"]');
  await game.waitFor();
  const initialChunks = +await game.getAttribute('data-loaded-chunks');
  const totalChunks = +await game.getAttribute('data-total-chunks');
  assert.ok(initialChunks > 0 && initialChunks < totalChunks / 2);
  assert.ok(fetchedChunks.size >= initialChunks, 'the initial map chunks arrived separately over HTTP');
  if (process.env.HARBOR_SHOTS) await page.screenshot({ path: process.env.HARBOR_SHOTS + '-intro.png' });
  await start.focus();
  await page.keyboard.press('Enter');
  await page.locator('#hud').waitFor();
  assert.match(await page.locator('#district-name').textContent(), /Harbor Front/);
  assert.match(await page.locator('#equipment').textContent(), /Wasp/i);
  if (process.env.HARBOR_SHOTS) await page.screenshot({ path: process.env.HARBOR_SHOTS + '-play.png' });

  const vehicle = page.locator('#vehicle-action');
  await page.keyboard.down('d');
  await page.keyboard.down('s');
  try {
    await vehicle.waitFor({ timeout: 18_000 });
  } finally {
    await page.keyboard.up('s');
    await page.keyboard.up('d');
  }
  assert.match(await vehicle.textContent(), /탑승/);
  await page.keyboard.press('f');
  await page.waitForFunction(() => document.getElementById('vehicle-action').textContent.includes('하차'));
  assert.match(await page.locator('#equipment').textContent(), /Calloway/i);

  await page.keyboard.down('w');
  try {
    await page.waitForFunction(() => parseInt(document.getElementById('ammunition').textContent, 10) >= 7);
  } finally {
    await page.keyboard.up('w');
  }
  await page.keyboard.down('Space');
  try {
    await page.waitForFunction(() => parseInt(document.getElementById('ammunition').textContent, 10) <= 1);
  } finally {
    await page.keyboard.up('Space');
  }
  await page.keyboard.press('f');
  await page.waitForFunction(() => document.getElementById('equipment').textContent.includes('Wasp'));

  const magazine = parseInt(await page.locator('#ammunition').textContent(), 10);
  // In play the canvas owns the pointer lock, just as it does for a desktop player.
  await page.mouse.click(500, 325);
  await page.waitForFunction(previous =>
    parseInt(document.getElementById('ammunition').textContent, 10) < previous, magazine);
  await page.locator('#wanted').waitFor();
  assert.match(await page.locator('#stars').getAttribute('aria-label'), /수배 [1-5]단계/);

  await page.keyboard.press('m');
  await page.locator('#atlas').waitFor();
  assert.equal(await page.locator('#map-legend li').count(), 7);
  const districts = await page.locator('#map-legend').textContent();
  for (const name of ['Harbor Front', 'Glass Heights', 'Old Sluice', 'Cinder Flats', 'Breakwater Station', 'Alderwash', 'Lantern Point'])
    assert.match(districts, new RegExp(name));
  const interiors = await page.locator('#map-interiors').textContent();
  for (const name of ['Customs Exchange Cafe', 'Atlantic Repair', 'Alderwash Lodge'])
    assert.ok(interiors.includes(name), name + ' appears on the map');
  if (process.env.HARBOR_SHOTS) await page.screenshot({ path: process.env.HARBOR_SHOTS + '-map.png' });
  await page.getByRole('button', { name: '지도 닫기' }).click({ noWaitAfter: true });
  await page.locator('#hud').waitFor();
  await page.keyboard.press('Escape');
  await page.getByRole('heading', { name: '일시 정지' }).waitFor();
  await page.getByRole('button', { name: '계속하기' }).click({ noWaitAfter: true });
  await page.locator('#hud').waitFor();
  await page.keyboard.press('Escape');
  await page.getByRole('button', { name: '새로 시작' }).click({ noWaitAfter: true });
  await page.locator('#hud').waitFor();
  assert.equal(await page.locator('#wallet').textContent(), '$0');

  // Move the render snapshot to a distant district without waiting for a long drive in software WebGL.
  const lodge = page.waitForResponse(response => response.url().includes('/godot/chunks/cell_-3_2.') && response.ok());
  await page.evaluate(() => {
    const view = globalThis.harborView;
    const frame = JSON.parse(view.frame());
    frame.position = { x: -375, y: 0, z: 246 };
    const version = view.frameVersion() + 1;
    view.frame = () => JSON.stringify(frame);
    view.frameVersion = () => version;
  });
  await lodge;
  await page.waitForFunction(() => Number(document.querySelector('#world').dataset.loadedChunks) >= 9);
  assert.ok(+await game.getAttribute('data-loaded-chunks') < fetchedChunks.size, 'distant old chunks were released');

  assert.deepEqual(errors, []);
  console.log('Godot boot, on-demand chunk loading and release, vehicle controls, wanted response, map and interiors, pause/resume and reset verified.');
} finally {
  await browser.close();
  await new Promise((resolve, reject) => {
    previewServer.httpServer.close(error => error ? reject(error) : resolve());
  });
}
