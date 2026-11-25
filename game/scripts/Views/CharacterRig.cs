using System.Collections.Generic;
using Godot;

namespace Harbor.Game.Views;

/// <summary>Procedural animation for the Blender humanoid rig (51 bones, T-pose rest, no baked clips).
/// Every frame it rebuilds the pose by pointing limb bones along desired directions in character space
/// (+Z forward, +Y up, +X = character's left), walking the hierarchy with its own forward kinematics so the
/// result does not depend on Godot's lazy bone-pose caches. Presentation only — hit volumes live in the core.</summary>
public sealed class CharacterRig
{
    public Skeleton3D Skeleton { get; }
    private readonly int[] _parent;
    private readonly Transform3D[] _rest;
    private readonly Vector3[] _restDirLocal; // bone direction (towards its first child) in its own rest frame
    private readonly Transform3D[] _cur;
    private readonly Dictionary<string, int> _idx = new();
    private readonly Basis _charToSkel;       // character space → skeleton space
    private readonly Dictionary<int, Vector3> _targets = new();
    private readonly int _hips;
    private readonly Vector3 _hipsRestOrigin;

    public CharacterRig(Skeleton3D skeleton, Node3D characterRoot)
    {
        Skeleton = skeleton;
        var n = skeleton.GetBoneCount();
        _parent = new int[n];
        _rest = new Transform3D[n];
        _restDirLocal = new Vector3[n];
        _cur = new Transform3D[n];
        var restGlobal = new Transform3D[n];
        for (var i = 0; i < n; i++)
        {
            _idx[skeleton.GetBoneName(i)] = i;
            _parent[i] = skeleton.GetBoneParent(i);
            _rest[i] = skeleton.GetBoneRest(i);
        }
        for (var i = 0; i < n; i++) restGlobal[i] = Global(i, restGlobal);
        for (var i = 0; i < n; i++)
        {
            var child = -1;
            for (var c = 0; c < n; c++) if (_parent[c] == i) { child = c; break; }
            var dirG = child >= 0 ? restGlobal[child].Origin - restGlobal[i].Origin : restGlobal[i].Basis.Y;
            if (dirG.LengthSquared() < 1e-10) dirG = restGlobal[i].Basis.Y;
            _restDirLocal[i] = (restGlobal[i].Basis.Inverse() * dirG).Normalized();
        }
        // skeleton transform relative to the character root
        var t = Transform3D.Identity;
        for (Node? node = skeleton; node != null && node != characterRoot; node = node.GetParent())
            if (node is Node3D n3) t = n3.Transform * t;
        _charToSkel = t.Basis.Inverse();
        _hips = Bone("hips");
        _hipsRestOrigin = _hips >= 0 ? _rest[_hips].Origin : Vector3.Zero;
    }

    private Transform3D Global(int i, Transform3D[] arr) => _parent[i] >= 0 ? arr[_parent[i]] * _rest[i] : _rest[i];

    public int Bone(string name) => _idx.TryGetValue(name, out var i) ? i : -1;

    /// <summary>Requests that bone <paramref name="name"/> points along <paramref name="dirChar"/> (character space).</summary>
    public void Point(string name, Vector3 dirChar)
    {
        var i = Bone(name);
        if (i >= 0 && dirChar.LengthSquared() > 1e-8) _targets[i] = (_charToSkel * dirChar).Normalized();
    }

    /// <summary>Model-space (skeleton-space) transform of a bone after the last <see cref="Apply"/>.</summary>
    public Transform3D BoneGlobal(string name) { var i = Bone(name); return i >= 0 ? _cur[i] : Transform3D.Identity; }

    /// <summary>Applies all requested directions plus a hips offset (character space, metres), then clears requests.</summary>
    public void Apply(Vector3 hipsOffsetChar)
    {
        var n = _rest.Length;
        for (var i = 0; i < n; i++)
        {
            var parentG = _parent[i] >= 0 ? _cur[_parent[i]] : Transform3D.Identity;
            var local = _rest[i];
            if (i == _hips) local.Origin = _hipsRestOrigin + _charToSkel * hipsOffsetChar;
            var g = parentG * local;
            if (_targets.TryGetValue(i, out var want))
            {
                var have = (g.Basis * _restDirLocal[i]).Normalized();
                var q = ShortestArc(have, want);
                var newBasis = new Basis(q) * g.Basis;
                local.Basis = parentG.Basis.Inverse() * newBasis;
                g = new Transform3D(newBasis, g.Origin);
            }
            _cur[i] = g;
            Skeleton.SetBonePosePosition(i, local.Origin);
            Skeleton.SetBonePoseRotation(i, local.Basis.GetRotationQuaternion());
        }
        _targets.Clear();
    }

    private static Quaternion ShortestArc(Vector3 from, Vector3 to)
    {
        var d = Mathf.Clamp(from.Dot(to), -1f, 1f);
        if (d > 0.99999f) return Quaternion.Identity;
        if (d < -0.99999f)
        {
            var axis = from.Cross(Vector3.Right);
            if (axis.LengthSquared() < 1e-6) axis = from.Cross(Vector3.Up);
            return new Quaternion(axis.Normalized(), Mathf.Pi);
        }
        return new Quaternion(from.Cross(to).Normalized(), Mathf.Acos(d));
    }
}
