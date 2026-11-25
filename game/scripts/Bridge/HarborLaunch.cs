using System.Linq;
using Godot;

namespace Harbor.Game.Bridge;

public partial class HarborLaunch : Node
{
    public const string CityScene = "res://scenes/Main.tscn";
    public const string PlaytestScene = "res://scenes/HarborPlaytest.tscn";

    public override void _Ready()
    {
        // Preserve the city session's CLI entry points and let the playtest own --connect.
        var city = OS.GetCmdlineUserArgs().Any(arg => arg.Split('=', 2)[0]
            is "--city" or "--server" or "--offline" or "--host" or "--join");
        GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, city ? CityScene : PlaytestScene);
    }
}
