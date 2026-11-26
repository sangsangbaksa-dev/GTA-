using Godot;
using Harbor.Core.Math;
using Harbor.Core.Server;
using Harbor.Game.Bridge;
using Harbor.Game.Player;

namespace Harbor.Game.UI;

public partial class DockMap : Control
{
    public CoreHost Host { get; init; } = null!;
    public ThirdPersonController Player { get; init; } = null!;
    public bool EntireDistrict { get; init; }

    private static readonly Color Water = new("#143f51");
    private static readonly Color Road = new("#39555e");
    private static readonly Color Grid = new("#233f4a");
    private static readonly Color Structure = new("#37515a");
    private static readonly Color White = new("#faf1dd");
    private static readonly Color Police = new("#86b7ff");

    public override void _Ready()
    {
        ClipContents = true;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Draw()
    {
        if (!IsInstanceValid(Host) || Host.Layout == null || Size.X == 0 || Size.Y == 0) return;
        var position = MapPoint(Host.RenderPosition);
        var frame = new Rect2(Vector2.Zero, Size);
        var layout = Host.Layout;
        var min = MapPoint(layout.Bounds.Min);
        var bounds = new Rect2(min, MapPoint(layout.Bounds.Max) - min);
        var viewport = EntireDistrict ? Fit(bounds, frame.Size) : Around(position, frame.Size, Host.IsDriving ? 120f : 72f);
        var scale = Size.X / viewport.Size.X;

        Vector2 Point(Vector2 coordinates) => (coordinates - viewport.Position) * scale;
        Rect2 Region(Rect2 area) => new(Point(area.Position), area.Size * scale);

        DrawRect(frame, Water);
        if (layout.Ground.Count == 0)
        {
            DrawRect(Region(bounds), DistrictColor("harbor"));
            foreach (var area in layout.Water) DrawRect(Region(Footprint(area)), Water);
        }
        foreach (var area in layout.Ground)
            DrawRect(Region(Footprint(area)), DistrictColor(area.Theme));
        var spacing = EntireDistrict ? 100 : 20;
        for (var x = Mathf.FloorToInt(viewport.Position.X / spacing) * spacing; x < viewport.End.X; x += spacing)
            DrawLine(Point(new Vector2(x, viewport.Position.Y)), Point(new Vector2(x, viewport.End.Y)), Grid);
        for (var z = Mathf.FloorToInt(viewport.Position.Y / spacing) * spacing; z < viewport.End.Y; z += spacing)
            DrawLine(Point(new Vector2(viewport.Position.X, z)), Point(new Vector2(viewport.End.X, z)), Grid);
        foreach (var road in layout.Roads)
        {
            var area = Footprint(road);
            var color = road.Kind switch
            {
                "bridge" or "arterial" => new Color("#58717a"),
                "path" or "service" => new Color("#695b46"),
                "runway" => new Color("#69756e"),
                _ => Road,
            };
            if (road.Yaw == 0) DrawRect(Region(area), color);
            else
            {
                var center = Point(area.GetCenter());
                var extent = area.Size * (scale / 2);
                var angle = Mathf.DegToRad(-(float)road.Yaw);
                DrawColoredPolygon(new[] { center + (-extent).Rotated(angle), center + new Vector2(extent.X, -extent.Y).Rotated(angle),
                    center + extent.Rotated(angle), center + new Vector2(-extent.X, extent.Y).Rotated(angle) }, color);
            }
        }

        var visibleVolume = new Harbor.Core.Geometry.Aabb(new Vec3d(viewport.Position.X, -3, viewport.Position.Y),
            new Vec3d(viewport.End.X, 10, viewport.End.Y));
        var obstacles = EntireDistrict ? Host.Level.All : Host.Level.Overlapping(visibleVolume);
        foreach (var obstacle in obstacles)
        {
            var area = new Rect2(MapPoint(obstacle.Box.Min), MapPoint(obstacle.Box.Max) - MapPoint(obstacle.Box.Min));
            if (area.Intersects(viewport) && area.Size.X * area.Size.Y > (EntireDistrict ? 10 : 1))
                DrawRect(Region(area), Structure);
        }

        if (EntireDistrict)
            foreach (var district in layout.Districts)
            {
                var font = ThemeDB.FallbackFont;
                var nameSize = font.GetStringSize(district.Name, fontSize: 13);
                var center = Point(MapPoint(district.Center));
                var baseline = center + new Vector2(-nameSize.X / 2, 5);
                DrawRect(new Rect2(baseline + new Vector2(-4, -12), new Vector2(nameSize.X + 8, 16)), new Color("#142832dd"));
                DrawString(font, baseline, district.Name, fontSize: 13, modulate: White);
            }

        foreach (var vehicle in Host.Vehicles)
        {
            if (Host.HasPlayer && vehicle.DriverId == Host.Client.LocalActorId) continue;
            var point = Point(MapPoint(vehicle.Position));
            if (frame.Grow(-4).HasPoint(point))
                DrawRect(new Rect2(point - new Vector2(3, 2), new Vector2(6, 4)),
                    vehicle.Occupied ? new Color("#a7a797") : HarborHud.Seafoam);
        }

        foreach (var unit in Host.Enemies)
        {
            if (!unit.IsAlive || unit.Team != 3) continue;
            var point = Point(MapPoint(unit.Position));
            if (frame.Grow(4).HasPoint(point)) DrawCircle(point, 2.7f, Police, true, -1, true);
        }

        var here = Point(position);
        var direction = Player.Camera.GlobalBasis.Z;
        var heading = new Vector2(-direction.X, -direction.Z).Normalized();
        if (heading.LengthSquared() < 0.0001f) heading = Vector2.Up;
        var side = new Vector2(heading.Y, -heading.X);
        DrawColoredPolygon(new[] { here + heading * 8, here - heading * 5 + side * 4.5f,
            here - heading * 2, here - heading * 5 - side * 4.5f }, White);
        DrawRect(frame.Grow(-0.5f), new Color("#51727c"), false, 1);
    }

    private static Vector2 MapPoint(Vec3d from) => new((float)from.X, (float)from.Z);
    private static Vector2 MapPoint(double[] pair) => new((float)pair[0], (float)pair[1]);
    private static Rect2 Footprint(HarborArea area)
    {
        var extent = MapPoint(area.Size);
        return new Rect2(MapPoint(area.Center) - extent / 2, extent);
    }

    private static Color DistrictColor(string theme) => theme switch
    {
        "city" => new Color("#384c57"),
        "low_income" => new Color("#53443e"),
        "desert" => new Color("#725746"),
        "military" => new Color("#45534a"),
        "forest" => new Color("#25493c"),
        "wealthy" => new Color("#53705b"),
        _ => new Color("#27404b"),
    };

    private static Rect2 Around(Vector2 center, Vector2 dimensions, float width)
    {
        var size = new Vector2(width, dimensions.Y * width / dimensions.X);
        return new Rect2(center - size * 0.5f, size);
    }

    private static Rect2 Fit(Rect2 region, Vector2 dimensions)
    {
        var width = Mathf.Max(region.Size.X, region.Size.Y * dimensions.X / dimensions.Y) * 1.05f;
        return Around(region.GetCenter(), dimensions, width);
    }
}
