"""Bake original, in-place locomotion into the Harbor character GLBs.

The stance phase moves each sole backward through measured ground distance; a
smooth recovery arc and two-bone IK keep the ankles supplied with a target rather
than faking the stride through a single swinging thigh. The skeleton uses Blender
+Z up and -Y forward. Distances are in metres for the 1.80 m reference body.
"""
import math
from dataclasses import dataclass

import bpy
from mathutils import Euler, Matrix, Quaternion, Vector


@dataclass(frozen=True)
class Gait:
    name: str
    frames: int
    stride: float = 0.0
    support: float = 0.6
    lift: float = 0.0
    drop: float = 0.0
    lean: float = 0.0
    arm: float = 0.0
    elbow: float = 0.0
    roll: float = 1.0


GAITS = (
    Gait("Idle", 132, elbow=0.14),
    Gait("Walk", 32, 1.27, 0.62, 0.100, 0.010, 0.025, 0.25, 0.20),
    Gait("Jog", 26, 1.85, 0.45, 0.194, 0.020, 0.12, 0.42, 0.75, 1.25),
    Gait("Run", 21, 2.64, 0.36, 0.290, 0.027, 0.26, 0.66, 1.03, 1.65),
    Gait("CrouchIdle", 108, drop=0.34, lean=0.37, elbow=0.44),
    Gait("CrouchWalk", 35, 0.94, 0.65, 0.076, 0.34, 0.37, 0.12, 0.52, 0.46),
)


def mix(a, b, t):
    return a + (b - a) * t


def smoothstep(t):
    t = max(0.0, min(t, 1.0))
    return t * t * (3.0 - 2.0 * t)


def rotation(x=0.0, y=0.0, z=0.0):
    return Euler((x, y, z), "XYZ").to_quaternion()


class Pose:
    def __init__(self, rig):
        self.rig = rig
        self.rest = {bone.name: bone.matrix_local.copy() for bone in rig.data.bones}
        self.transforms = {}

    def base(self, name):
        bone = self.rig.pose.bones[name]
        rest = self.rest[name]
        if bone.parent is None:
            return rest
        parent = bone.parent.name
        return self.transforms[parent] @ self.rest[parent].inverted() @ rest

    def set(self, name, transform, frame=None):
        bone = self.rig.pose.bones[name]
        rest = self.rest[name]
        self.transforms[name] = transform
        if bone.parent is None:
            delta = rest.inverted() @ transform
        else:
            parent = bone.parent.name
            delta = rest.inverted() @ self.rest[parent] @ self.transforms[parent].inverted() @ transform
        bone.rotation_mode = "QUATERNION"
        q = delta.to_quaternion()
        if bone.rotation_quaternion.dot(q) < 0:
            q.negate()
        bone.rotation_quaternion = q
        if frame is not None:
            bone.keyframe_insert(data_path="rotation_quaternion", frame=frame, group=name)
            if bone.parent is None:
                bone.location = delta.translation
                bone.keyframe_insert(data_path="location", frame=frame, group=name)

    def turn(self, name, q, frame=None, offset=None):
        base = self.base(name)
        pos = base.translation if offset is None else base.translation + offset
        self.set(name, Matrix.LocRotScale(pos, q @ base.to_quaternion(), None), frame)

    def aim(self, name, toward, frame):
        base = self.base(name)
        current = base.to_quaternion()
        change = (current @ Vector((0, 1, 0))).rotation_difference(toward.normalized())
        self.set(name, Matrix.LocRotScale(base.translation, change @ current, None), frame)


def foot_path(gait, phase, ankle, center_x, side, scale):
    """Return a ground-relative ankle target and world rotation for either leg."""
    rest_width = abs(ankle.x - center_x)
    stance_width = min((0.102 if gait.drop > 0.1 else 0.09) * scale,
                       max(0.068 * scale, rest_width * 0.70))
    x = center_x + side * stance_width - ankle.x
    if gait.stride == 0:
        return ankle + Vector((x, side * 0.012 * scale, 0.002 * scale)), rotation(z=side * 0.055)

    reach = gait.stride * gait.support * 0.5
    if phase < gait.support:
        t = phase / gait.support
        travel = mix(-reach, reach, t)
        pitch = gait.roll * (-0.24 * (1 - smoothstep(t / 0.22)) + 0.37 * smoothstep((t - 0.56) / 0.44))
        lift = 0.0
    else:
        t = (phase - gait.support) / (1 - gait.support)
        # The first part of recovery continues in the direction of toe-off.
        # This connects the nearly linear support pass without a visible pop.
        overshoot = gait.stride * (1 - gait.support) * 0.078
        travel = mix(reach, -reach, smoothstep(t)) + math.sin(math.tau * t) * overshoot
        pitch = gait.roll * mix(0.37, -0.24, smoothstep(t))
        lift = gait.lift * math.sin(math.pi * t) ** 1.48
        x += side * 0.004 * scale * math.sin(math.pi * t)

    q = rotation(x=pitch, z=side * 0.04)
    # Roll about the heel or ball of the sole so the support foot does not sink.
    pivot = Vector((0, 0.044 if pitch < 0 else -0.151, -0.088)) * scale
    target = ankle + pivot - q @ pivot + Vector((x, (travel + (0.03 if gait.support < 0.5 else 0)) * scale,
                                                 (lift + 0.004) * scale))
    return target, q


def aim_leg(pose, side, target, tilt, frame):
    thigh, shin, foot = (f"{part}.{side}" for part in ("thigh", "shin", "foot"))
    hip = pose.base(thigh).translation
    a = (pose.rest[thigh].translation - pose.rest[shin].translation).length
    b = (pose.rest[shin].translation - pose.rest[foot].translation).length
    delta = target - hip
    length = min(delta.length, (a + b) * 0.998)
    direction = delta.normalized()
    bend = Vector(((0.018 if side == "L" else -0.018), -1, 0))
    bend = (bend - direction * bend.dot(direction)).normalized()
    along = (a * a - b * b + length * length) / (2 * length)
    knee = hip + direction * along + bend * math.sqrt(max(a * a - along * along, 0))
    pose.aim(thigh, knee - hip, frame)
    pose.aim(shin, target - pose.base(shin).translation, frame)
    world_foot = tilt @ pose.rest[foot].to_quaternion()
    pose.set(foot, Matrix.LocRotScale(pose.base(foot).translation, world_foot, None), frame)


def pose_frame(pose, gait, cycle, frame, scale):
    step = gait.stride > 0
    crouch = gait.drop > 0.1
    wave = math.sin(math.tau * cycle)
    cross = math.cos(math.tau * cycle)
    breathing = math.sin(math.tau * cycle)
    yaw = wave * (0.087 if not crouch else 0.058) if step else 0.028 + 0.015 * wave
    rock = cross * (0.021 if step else 0.008)
    hip_rotation = rotation(x=-0.10 if crouch else -0.022, y=rock, z=yaw)
    targets = {}
    hips = pose.rest["hips"].translation
    for side, sign in (("L", 1), ("R", -1)):
        phase = (cycle + (0 if sign > 0 else 0.5)) % 1.0
        targets[side] = foot_path(gait, phase, pose.rest[f"foot.{side}"].translation, hips.x, sign, scale)

    bob = -0.012 * math.cos(2 * math.tau * cycle) if step else 0.004 * breathing
    if step and gait.support < 0.5:
        bob = 0.045 * math.cos(2 * math.tau * (cycle + 0.03))
    center = Vector(((0.010 * cross if step else 0.004 + 0.004 * cross) * scale,
                     (0.11 if crouch else (0.025 if step else 0.007)) * scale,
                     (-0.025 - gait.drop + bob) * scale))
    # Compress into the actual reachable range at the widest stride frame.
    # Account for thigh/shin lengths and lateral pelvis motion as well as reach.
    for side, (target, _) in targets.items():
        hip = hips + hip_rotation @ (pose.rest[f"thigh.{side}"].translation - hips) + center
        reach = sum((pose.rest[f"{a}.{side}"].translation - pose.rest[f"{b}.{side}"].translation).length
                    for a, b in (("thigh", "shin"), ("shin", "foot"))) * 0.995
        horizontal = (target.x - hip.x) ** 2 + (target.y - hip.y) ** 2
        max_height = target.z + math.sqrt(max(reach * reach - horizontal, 0.0))
        center.z -= max(0.0, hip.z - max_height)

    pose.turn("hips", hip_rotation, frame, center)
    pose.turn("spine", rotation(x=gait.lean * 0.72, y=-rock * 0.36, z=-yaw * 0.55), frame)
    pose.turn("chest", rotation(x=gait.lean * 0.50 + 0.018 * breathing * (0.4 if step else 1),
                                y=-rock * 0.58, z=-yaw * 1.21), frame)
    pose.turn("neck", rotation(x=-gait.lean * 0.55, y=rock * 0.36, z=yaw * 0.43), frame)
    pose.turn("head", rotation(x=-gait.lean * 0.13, z=-0.024 * cross), frame)

    for side, sign in (("L", 1), ("R", -1)):
        target, tilt = targets[side]
        aim_leg(pose, side, target, tilt, frame)
        arm_wave = math.cos(math.tau * (cycle - 0.06)) * sign
        clavicle = f"shoulder.{side}"
        upper, fore, hand = (f"{part}.{side}" for part in ("upper_arm", "forearm", "hand"))
        pose.turn(clavicle, rotation(y=sign * 0.052, z=arm_wave * gait.arm * 0.13), frame)
        base = pose.base(upper)
        rest_direction = base.to_quaternion() @ Vector((0, 1, 0))
        lower = math.atan2(abs(rest_direction.x), -rest_direction.z) - 0.075 - gait.arm * 0.12
        stride = arm_wave * gait.arm + (0.11 if crouch else 0.024)
        pose.turn(upper, rotation(x=stride, y=sign * max(0.0, lower), z=-sign * 0.038), frame)
        elbow = gait.elbow + 0.12 * gait.arm * (1 - arm_wave)
        pose.turn(fore, rotation(x=-elbow, y=sign * 0.055, z=-sign * 0.05), frame)
        pose.turn(hand, rotation(x=0.067 + stride * 0.12, y=-sign * 0.065, z=-sign * 0.04), frame)
        for finger in ("thumb", "index", "middle", "ring", "pinky"):
            for joint in (1, 2, 3):
                name = f"{finger}_{joint}.{side}"
                # Rest fingers are already curved. Add a loose grip as the arm accelerates.
                curl = (0.105 + gait.arm * 0.25) * (0.45 if finger == "thumb" else 1.0)
                if frame in (0, gait.frames):
                    bone = pose.rig.pose.bones[name]
                    bone.rotation_mode = "QUATERNION"
                    bone.rotation_quaternion = Quaternion((1, 0, 0), -sign * curl)
                    bone.keyframe_insert(data_path="rotation_quaternion", frame=frame, group=name)


def create_clips(rig, scale):
    scene = bpy.context.scene
    scene.render.fps = 30
    scene.frame_start = 0
    scene.frame_end = max(gait.frames for gait in GAITS)
    pose = Pose(rig)
    animation = rig.animation_data_create()
    for gait in GAITS:
        action = bpy.data.actions.new(gait.name)
        animation.action = action
        for frame in range(gait.frames + 1):
            pose_frame(pose, gait, (frame % gait.frames) / gait.frames, frame, scale)
        animation.action = None
        track = animation.nla_tracks.new()
        track.name = gait.name
        track.strips.new(gait.name, 0, action)
        track.mute = True
    for bone in rig.pose.bones:
        bone.matrix_basis = Matrix.Identity(4)
    bpy.context.view_layer.update()
