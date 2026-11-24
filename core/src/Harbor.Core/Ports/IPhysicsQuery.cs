using Harbor.Core.Math;

namespace Harbor.Core.Ports;

/// <summary>Engine physics queries the core may need (line of sight, ground height).
/// Implementations may return conservative answers when the area is not loaded.</summary>
public interface IPhysicsQuery
{
    bool Raycast(Vec3d from, Vec3d to, out Vec3d hit);
}
