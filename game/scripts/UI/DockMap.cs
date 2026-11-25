using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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

    private static readonly Rect2 FallbackDistrict = new(-215, -195, 360, 290);
    private static readonly Color Water = new("#143f51");
    private static readonly Color Road = new("#39555e");
    private static readonly Color Lot = new("#1b313a");
    private static readonly Color Grid = new("#233f4a");
    private static readonly Color Structure = new("#37515a");
    private static readonly Color White = new("#faf1dd");
    private static readonly Color Enemy = new("#ff7665");
    private static readonly Color Police = new("#86b7ff");
    private static readonly Lazy<Chart> Plan = new(LoadChart);

    public override void _Ready()
    {
        ClipContents = true;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Draw()
    {
        if (!IsInstanceValid(Host) || Size.X == 0 || Size.Y == 0) return;
        var position = MapPoint(Host.RenderPosition);
        var frame = new Rect2(Vector2.Zero, Size);
        var chart = Plan.Value;
        var viewport = EntireDistrict ? Fit(chart.Bounds, frame.Size) : Around(position, frame.Size, Host.IsDriving ? 100f : 62f);
        var scale = Size.X / viewport.Size.X;

        Vector2 Point(Vector2 coordinates) => (coordinates - viewport.Position) * scale;
        Rect2 Region(Rect2 area) => new(Point(area.Position), area.Size * scale);

        DrawRect(frame, Water);
        DrawRect(Region(chart.Bounds), Lot);
        foreach (var area in chart.Water) DrawRect(Region(area), Water);
        for (var x = Mathf.FloorToInt(viewport.Position.X / 20) * 20; x < viewport.End.X; x += 20)
            DrawLine(Point(new Vector2(x, viewport.Position.Y)), Point(new Vector2(x, viewport.End.Y)), Grid);
        for (var z = Mathf.FloorToInt(viewport.Position.Y / 20) * 20; z < viewport.End.Y; z += 20)
            DrawLine(Point(new Vector2(viewport.Position.X, z)), Point(new Vector2(viewport.End.X, z)), Grid);
        foreach (var road in chart.Roads) DrawRect(Region(road), Road);
        foreach (var obstacle in Host.Level.All)
        {
            var area = new Rect2(MapPoint(obstacle.Box.Min), MapPoint(obstacle.Box.Max) - MapPoint(obstacle.Box.Min));
            if (area.Intersects(viewport) && area.Size.X * area.Size.Y > (EntireDistrict ? 10 : 1))
                DrawRect(Region(area), Structure);
        }

        var run = Host.Lockdown;
        if (run.Phase == LockdownPhase.FreeRoam)
            Marker(Point(MapPoint(run.StartPosition)), HarborHud.Amber, frame);
        else if (run.Phase == LockdownPhase.Extraction)
            Marker(Point(MapPoint(run.ExtractionPosition)), HarborHud.Seafoam, frame);

        var car = Point(MapPoint(Host.Vehicle.Position));
        if (!Host.IsDriving && frame.Grow(-4).HasPoint(car))
            DrawRect(new Rect2(car - new Vector2(3, 2), new Vector2(6, 4)), HarborHud.Seafoam);

        foreach (var enemy in Host.Enemies)
        {
            if (!enemy.IsAlive) continue;
            var point = Point(MapPoint(enemy.Position));
            if (frame.Grow(4).HasPoint(point)) DrawCircle(point, 2.7f, enemy.Team == 3 ? Police : Enemy, true, -1, true);
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

    private void Marker(Vector2 point, Color color, Rect2 frame)
    {
        var inner = frame.Grow(-7);
        var inside = inner.HasPoint(point);
        var anchor = inside ? point : point.Clamp(inner.Position, inner.End);
        DrawColoredPolygon(new[] { anchor + Vector2.Up * 4.5f, anchor + Vector2.Right * 4.5f,
            anchor + Vector2.Down * 4.5f, anchor + Vector2.Left * 4.5f }, color);
        if (inside) DrawCircle(anchor, 7, color, false, 1, true);
    }

    private static Vector2 MapPoint(Vec3d from) => new((float)from.X, (float)from.Z);
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

    private static Chart LoadChart()
    {
        foreach (var assembly in new[] { typeof(HarborAuthority).Assembly, typeof(DockMap).Assembly })
        {
            var name = assembly.GetManifestResourceNames().FirstOrDefault(resource =>
                resource.EndsWith("harbor_front.json", StringComparison.OrdinalIgnoreCase));
            if (name == null) continue;
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream == null) continue;
            using var layout = JsonDocument.Parse(stream);
            var root = layout.RootElement;
            var bounds = FallbackDistrict;
            if (root.TryGetProperty("bounds", out var value))
            {
                var min = ReadPoint(value.GetProperty("min"));
                bounds = new Rect2(min, ReadPoint(value.GetProperty("max")) - min);
            }
            return new Chart(bounds, ReadAreas(root, "roads"), ReadAreas(root, "water"));
        }
        return new Chart(FallbackDistrict, Array.Empty<Rect2>(), Array.Empty<Rect2>());
    }

    private static Vector2 ReadPoint(JsonElement pair) => new(pair[0].GetSingle(), pair[1].GetSingle());

    private static IReadOnlyList<Rect2> ReadAreas(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var values)) return Array.Empty<Rect2>();
        return values.EnumerateArray().Select(item =>
        {
            var center = ReadPoint(item.GetProperty("center"));
            var extent = ReadPoint(item.GetProperty("size"));
            return new Rect2(center - extent * 0.5f, extent);
        }).ToArray();
    }

    private sealed record Chart(Rect2 Bounds, IReadOnlyList<Rect2> Roads, IReadOnlyList<Rect2> Water);
}
