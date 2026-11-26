using Godot;

namespace Harbor.Game.Bridge;

public partial class HarborLaunch : Node
{
    public const string PlaytestScene = "res://scenes/HarborPlaytest.tscn";

    public override void _Ready() =>
        GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, PlaytestScene);
}
