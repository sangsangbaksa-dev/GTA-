export const Buttons = {
  None: 0,
  Sprint: 1 << 0,
  Crouch: 1 << 1,
  Aim: 1 << 2,
  Fire: 1 << 3,
  Reload: 1 << 4,
  Interact: 1 << 5,
  Jump: 1 << 6,
  Melee: 1 << 7,
  Pickpocket: 1 << 8,
  Carjack: 1 << 9,
};

export function createInputController(canvas, callbacks) {
  const heldKeys = new Set();
  let mouseButtons = 0;
  let pulses = 0;
  let yaw = 0;
  let pitch = -0.3;
  let weaponSlot = 0;
  let dancing = false;
  let active = false;

  const normalizeAngle = (angle) => {
    let a = angle;
    while (a > Math.PI) a -= Math.PI * 2;
    while (a < -Math.PI) a += Math.PI * 2;
    return a;
  };

  window.addEventListener("keydown", (event) => {
    if (event.repeat) return;
    const code = event.code;

    if (code === "KeyM") {
      callbacks.onToggleMap?.();
      event.preventDefault();
      return;
    }
    if (code === "KeyJ") {
      callbacks.onToggleJobs?.();
      event.preventDefault();
      return;
    }
    if (code === "Escape") {
      callbacks.onEscape?.();
      return;
    }

    if (!active) return;
    heldKeys.add(code);

    if (code === "Digit1") weaponSlot = 0;
    else if (code === "Digit2") weaponSlot = 1;
    else if (code === "KeyR") pulses |= Buttons.Reload;
    else if (code === "KeyE") pulses |= Buttons.Interact;
    else if (code === "KeyF") pulses |= Buttons.Pickpocket;
    else if (code === "KeyV") pulses |= Buttons.Melee;
    else if (code === "KeyG") pulses |= Buttons.Carjack;
    else if (code === "KeyB") dancing = !dancing;
  });

  window.addEventListener("keyup", (event) => {
    heldKeys.delete(event.code);
  });

  window.addEventListener("blur", () => {
    heldKeys.clear();
    mouseButtons = 0;
  });

  canvas.addEventListener("mousedown", (event) => {
    window.__HARBOR_AUDIO_UNLOCKED = true;
    if (!active) return;
    if (document.pointerLockElement !== canvas && canvas.requestPointerLock) {
      canvas.requestPointerLock().catch?.(() => {});
    }
    if (event.button === 0) {
      mouseButtons |= 1;
      pulses |= Buttons.Fire;
    } else if (event.button === 2) {
      mouseButtons |= 2;
    }
  });

  window.addEventListener("mouseup", (event) => {
    if (event.button === 0) mouseButtons &= ~1;
    else if (event.button === 2) mouseButtons &= ~2;
  });

  canvas.addEventListener("contextmenu", (event) => event.preventDefault());

  window.addEventListener("mousemove", (event) => {
    if (!active) return;
    const locked = document.pointerLockElement === canvas;
    if (!locked && (mouseButtons & 2) === 0) return;
    const aiming = (mouseButtons & 2) !== 0;
    const sens = aiming ? 0.0017 : 0.0025;
    yaw = normalizeAngle(yaw - event.movementX * sens);
    pitch = Math.max(-1.05, Math.min(0.45, pitch - event.movementY * sens));
  });

  return {
    setActive(nextActive) {
      active = nextActive;
      if (!active) {
        heldKeys.clear();
        mouseButtons = 0;
        if (document.pointerLockElement === canvas && document.exitPointerLock) {
          document.exitPointerLock();
        }
      }
    },

    requestLock() {
      window.__HARBOR_AUDIO_UNLOCKED = true;
      if (canvas.requestPointerLock) {
        canvas.requestPointerLock().catch?.(() => {});
      }
    },

    resetView() {
      yaw = 0;
      pitch = -0.3;
      weaponSlot = 0;
      dancing = false;
    },

    sample(dt, isDriving, vehicleYaw = 0, vehicleSpeed = 0) {
      let right = 0;
      let forward = 0;
      let held = Buttons.None;

      if (active) {
        if (heldKeys.has("KeyA") || heldKeys.has("ArrowLeft")) right -= 1;
        if (heldKeys.has("KeyD") || heldKeys.has("ArrowRight")) right += 1;
        if (heldKeys.has("KeyW") || heldKeys.has("ArrowUp")) forward += 1;
        if (heldKeys.has("KeyS") || heldKeys.has("ArrowDown")) forward -= 1;

        const len = Math.hypot(right, forward);
        if (!isDriving && len > 1) {
          right /= len;
          forward /= len;
        }

        if (heldKeys.has("ShiftLeft") || heldKeys.has("ShiftRight")) held |= Buttons.Sprint;
        if (isDriving && heldKeys.has("Space")) held |= Buttons.Sprint;
        if (!isDriving && (heldKeys.has("KeyC") || heldKeys.has("ControlLeft"))) held |= Buttons.Crouch;
        if (heldKeys.has("KeyE")) held |= Buttons.Interact;
        if (!isDriving && (mouseButtons & 2) !== 0) held |= Buttons.Aim;
        if (!isDriving && (mouseButtons & 1) !== 0) held |= Buttons.Fire;

        // Gamepad polling
        const pads = navigator.getGamepads ? navigator.getGamepads() : [];
        const pad = pads && pads[0];
        if (pad) {
          const lx = pad.axes[0] || 0;
          const ly = -(pad.axes[1] || 0);
          if (Math.hypot(lx, ly) > 0.2) {
            right = lx;
            forward = ly;
          }
          const rx = pad.axes[2] || 0;
          const ry = pad.axes[3] || 0;
          if (Math.hypot(rx, ry) > 0.18) {
            yaw = normalizeAngle(yaw - rx * 2.4 * dt);
            pitch = Math.max(-1.05, Math.min(0.45, pitch - ry * 1.8 * dt));
          }
          if (pad.buttons[0]?.pressed && isDriving) held |= Buttons.Sprint;
          if (pad.buttons[1]?.pressed && !isDriving) held |= Buttons.Crouch;
          if (pad.buttons[2]?.pressed) held |= Buttons.Interact;
          if (pad.buttons[5]?.pressed) pulses |= Buttons.Reload;
          if (pad.buttons[6]?.value > 0.4 && !isDriving) held |= Buttons.Aim;
          if (pad.buttons[7]?.value > 0.4 && !isDriving) held |= Buttons.Fire;
        }

        if (isDriving && Math.abs(vehicleSpeed) > 1.2 && document.pointerLockElement !== canvas) {
          const diff = normalizeAngle(vehicleYaw - yaw);
          yaw = normalizeAngle(yaw + diff * (1 - Math.exp(-dt * 3.5)));
        }

        if (
          isDriving ||
          right !== 0 ||
          forward !== 0 ||
          (held & (Buttons.Aim | Buttons.Fire | Buttons.Crouch | Buttons.Interact)) !== 0 ||
          (pulses & (Buttons.Melee | Buttons.Carjack)) !== 0
        ) {
          dancing = false;
        }
      }

      const currentPulses = active ? pulses : 0;
      pulses = 0;

      return {
        right,
        forward,
        yaw,
        pitch,
        held,
        pulses: currentPulses,
        weaponSlot,
        dancing,
      };
    },
  };
}
