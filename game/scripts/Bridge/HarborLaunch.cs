using System.Linq;
using Godot;

namespace Harbor.Game.Bridge;

public partial class HarborLaunch : Node
{
    public const string CityScene = "res://scenes/Main.tscn";
    public const string PlaytestScene = "res://scenes/HarborPlaytest.tscn";

    public override void _Ready()
    {
        var city = OS.GetCmdlineUserArgs().Any(arg => arg.Split('=', 2)[0]
            is "--city" or "--offline");
        GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, city ? CityScene : PlaytestScene);
    }
}
