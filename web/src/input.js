const SPRINT = 1;
const CROUCH = 1 << 1;
const AIM = 1 << 2;
const FIRE = 1 << 3;
const RELOAD = 1 << 4;
const VEHICLE = 1 << 8;
const PICKPOCKET = 1 << 9;
const PULSES = new Map([['KeyF', VEHICLE], ['KeyG', PICKPOCKET], ['KeyR', RELOAD], ['Fire', FIRE]]);
const BUTTONS = new Set([
  'KeyW', 'KeyA', 'KeyS', 'KeyD', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight',
  'KeyQ', 'KeyE', 'KeyC', 'ShiftLeft', 'ShiftRight', 'Space', 'Digit1', 'Digit2', ...PULSES.keys(),
]);

export function createInput(canvas, touch, active, onMenu) {
  const held = new Set();
  let presses = 0;
  let yaw = 0;
  let pitch = -.1;
  let slot = 0;
  let drag;
  const coarse = matchMedia('(pointer: coarse)');

  const turn = (dx, dy) => {
    yaw -= dx * .003;
    pitch = Math.max(-.82, Math.min(.65, pitch - dy * .0027));
  };
  const key = (code, enabled) => {
    if (!enabled) {
      held.delete(code);
      return;
    }
    if (!held.has(code)) {
      presses |= PULSES.get(code) ?? 0;
      if (code === 'Digit1' || code === 'Digit2') slot = Number(code.at(-1));
    }
    held.add(code);
  };
  const clear = () => {
    held.clear();
    presses = 0;
    drag = null;
  };
  window.addEventListener('blur', clear);
  document.addEventListener('visibilitychange', () => {
    if (document.hidden) clear();
  });
  document.addEventListener('keydown', event => {
    if (!event.repeat && (event.code === 'KeyM' || event.code === 'Escape')) {
      if (onMenu(event.code)) event.preventDefault();
      return;
    }
    if (!active() || !BUTTONS.has(event.code) || event.altKey || event.metaKey || event.ctrlKey) return;
    event.preventDefault();
    key(event.code, true);
  });
  window.addEventListener('keyup', event => key(event.code, false));
  canvas.addEventListener('contextmenu', event => event.preventDefault());
  canvas.addEventListener('pointerdown', event => {
    if (!active()) return;
    if (event.pointerType === 'touch' || document.pointerLockElement !== canvas) {
      drag = { pointer: event.pointerId, x: event.clientX, y: event.clientY };
      canvas.setPointerCapture(event.pointerId);
    }
    if (event.pointerType !== 'touch') {
      if (event.button === 2) key('Aim', true);
      if (event.button === 0) key('Fire', true);
    }
  });
  window.addEventListener('pointerup', event => {
    if (event.button === 2) key('Aim', false);
    if (event.button === 0 && event.pointerType !== 'touch') key('Fire', false);
    if (drag?.pointer === event.pointerId) drag = null;
  });
  window.addEventListener('pointercancel', event => {
    if (drag?.pointer === event.pointerId) drag = null;
    if (event.pointerType !== 'touch') clear();
  });
  document.addEventListener('pointermove', event => {
    if (!active()) return;
    if (document.pointerLockElement === canvas) turn(event.movementX, event.movementY);
    else if (drag?.pointer === event.pointerId) {
      turn(event.clientX - drag.x, event.clientY - drag.y);
      drag.x = event.clientX;
      drag.y = event.clientY;
    }
  });

  for (const button of touch.querySelectorAll('[data-key]')) {
    const code = button.dataset.key;
    button.addEventListener('pointerdown', event => {
      event.preventDefault();
      button.setPointerCapture(event.pointerId);
      key(code, true);
    });
    for (const event of ['pointerup', 'pointercancel', 'lostpointercapture'])
      button.addEventListener(event, () => key(code, false));
  }

  return {
    clear,
    reset() {
      clear();
      yaw = 0; pitch = -.1; slot = 0;
    },
    capture() {
      if (coarse.matches) return;
      try {
        const request = canvas.requestPointerLock?.();
        request?.catch(() => {});
      } catch {
        // Drag and keyboard camera remain available.
      }
    },
    release() {
      clear();
      if (document.pointerLockElement === canvas) document.exitPointerLock();
    },
    sample(dt, driving) {
      yaw += (Number(held.has('KeyQ')) - Number(held.has('KeyE'))) * 2.1 * dt;
      const axis = (pos, altPos, neg, altNeg) =>
        Number(held.has(pos) || held.has(altPos)) - Number(held.has(neg) || held.has(altNeg));
      let buttons = 0;
      if (driving ? held.has('Space') : held.has('ShiftLeft') || held.has('ShiftRight')) buttons |= SPRINT;
      if (held.has('KeyC')) buttons |= CROUCH;
      if (held.has('Aim')) buttons |= AIM;
      if (held.has('Fire')) buttons |= FIRE;
      const pulses = presses;
      presses = 0;
      return {
        right: axis('KeyD', 'ArrowRight', 'KeyA', 'ArrowLeft'),
        forward: axis('KeyW', 'ArrowUp', 'KeyS', 'ArrowDown'),
        yaw, pitch, buttons, pulses, slot,
      };
    },
    direction: () => ({ yaw, pitch }),
    coarse,
  };
}
