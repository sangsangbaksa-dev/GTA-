using Godot;

namespace Harbor.Game.UI;

public partial class CombatReticle : Control
{
    public bool Armed { get; set; }
    public bool Aiming { get; set; }
    public bool HasTarget { get; set; }
    private float _hit;
    private float _damage;

    public void FlashHit() => _hit = 0.23f;
    public void FlashDamage() => _damage = 0.42f;

    public void Advance(float delta)
    {
        _hit = Mathf.Max(0, _hit - delta);
        _damage = Mathf.Max(0, _damage - delta);
        QueueRedraw();
    }

    public override void _Draw()
    {
        var center = Size * 0.5f;
        var white = Aiming && HasTarget ? HarborHud.Amber : new Color(1, 0.97f, 0.89f, Aiming ? 0.89f : 0.55f);
        if (Armed)
        {
            var space = Aiming ? 5 : 9;
            foreach (var direction in new[] { Vector2.Up, Vector2.Down, Vector2.Left, Vector2.Right })
            {
                DrawLine(center + direction * space, center + direction * (space + 5), new Color(0, 0, 0, 0.55f), 3, true);
                DrawLine(center + direction * space, center + direction * (space + 5), white, 1.4f, true);
            }
            if (Aiming) DrawCircle(center, 1.65f, white, true, -1, true);
        }
        if (_hit > 0)
        {
            var color = new Color(HarborHud.Amber, Mathf.Min(1, _hit * 9));
            foreach (var direction in new[] { new Vector2(1, 1), new Vector2(-1, 1), new Vector2(1, -1), new Vector2(-1, -1) })
                DrawLine(center + direction * 9, center + direction * 15, color, 2.3f, true);
        }
        if (_damage > 0)
        {
            var color = new Color(1, 0.23f, 0.17f, _damage * 1.1f);
            DrawRect(new Rect2(Vector2.One * 2, Size - Vector2.One * 4), color, false, 4);
        }
    }
}
