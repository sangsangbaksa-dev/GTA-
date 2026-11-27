using Godot;

namespace Harbor.Game.Player;

/// <summary>Drives embedded Mixamo motion-capture clips with smooth crossfades on Harbor character rigs.</summary>
internal sealed class MixamoGait
{
    private const string LibraryName = "mixamo_local";
    private static readonly string[] LoopingClips =
    [
        "Idle", "Walk", "Jog", "Run", "TurnLeft", "TurnRight", "Dance", "CrouchIdle", "CrouchWalk"
    ];

    private readonly AnimationPlayer _player;
    private readonly StringName _prefix;
    private string _activeClip = "";
    private bool _turningLatched;

    public string ActiveClip => _activeClip;

    private MixamoGait(AnimationPlayer player, string prefix)
    {
        _player = player;
        _prefix = prefix;
        _player.PlaybackActive = true;
        PlayClip("Idle", 0f, 1f);
    }

    public static MixamoGait? Create(Node3D model)
    {
        foreach (var node in model.FindChildren("*", "AnimationPlayer", recursive: true, owned: false))
        {
            if (node is not AnimationPlayer player) continue;
            if (!TryConfigureLoopingLibrary(player, out var prefix)) continue;
            return new MixamoGait(player, prefix);
        }

        return null;
    }

    public void Update(
        float speed,
        bool crouched,
        bool sprinting,
        bool dancing,
        float turnRate,
        bool retreating,
        float dt)
    {
        var dirSign = retreating ? -1f : 1f;
        var absTurn = Mathf.Abs(turnRate);
        if (absTurn > 0.58f) _turningLatched = true;
        else if (absTurn < 0.24f || speed > 0.22f) _turningLatched = false;

        string target;
        float blendSeconds;
        float speedScale = 1f;

        if (dancing && !crouched && speed <= 0.22f)
        {
            target = Has("Dance") ? "Dance" : "Idle";
            blendSeconds = 0.26f;
        }
        else if (crouched)
        {
            if (speed > 0.20f && Has("CrouchWalk"))
            {
                target = "CrouchWalk";
                speedScale = Mathf.Clamp(speed / 1.95f, 0.68f, 1.42f) * dirSign;
                blendSeconds = 0.20f;
            }
            else
            {
                target = Has("CrouchIdle") ? "CrouchIdle" : "Idle";
                blendSeconds = 0.22f;
            }
        }
        else if (speed > 0.22f)
        {
            if ((sprinting && speed > 5.15f || speed > 5.45f) && Has("Run"))
            {
                target = "Run";
                speedScale = Mathf.Clamp(speed / 6.0f, 0.82f, 1.28f) * dirSign;
                blendSeconds = 0.20f;
            }
            else if ((sprinting || speed > 3.75f) && Has("Jog"))
            {
                target = "Jog";
                speedScale = Mathf.Clamp(speed / 4.35f, 0.80f, 1.26f) * dirSign;
                blendSeconds = 0.20f;
            }
            else
            {
                target = "Walk";
                speedScale = Mathf.Clamp(speed / 2.65f, 0.72f, 1.32f) * dirSign;
                blendSeconds = 0.22f;
            }
        }
        else if (_turningLatched)
        {
            var turnClip = turnRate > 0 ? "TurnLeft" : "TurnRight";
            target = Has(turnClip) ? turnClip : "Idle";
            speedScale = Mathf.Clamp(absTurn / 2.1f, 0.78f, 1.45f);
            blendSeconds = 0.18f;
        }
        else
        {
            target = "Idle";
            blendSeconds = 0.25f;
        }

        PlayClip(target, blendSeconds, speedScale);
    }

    private bool Has(string clip) => _player.HasAnimation(_prefix + clip);

    private void PlayClip(string clip, float blendSeconds, float speedScale)
    {
        var fullName = _prefix + clip;
        if (!_player.HasAnimation(fullName)) return;

        if (_activeClip != clip)
        {
            _activeClip = clip;
            _player.Play(fullName, customBlend: blendSeconds);
        }

        _player.SpeedScale = speedScale;
    }

    private static bool TryConfigureLoopingLibrary(AnimationPlayer player, out string prefix)
    {
        prefix = "";
        string? sourceLibName = null;
        foreach (var libName in player.GetAnimationLibraryList())
        {
            var candidatePrefix = string.IsNullOrEmpty(libName) ? "" : libName + "/";
            if (player.HasAnimation(candidatePrefix + "Idle")
                && player.HasAnimation(candidatePrefix + "Walk")
                && player.HasAnimation(candidatePrefix + "Run"))
            {
                sourceLibName = libName;
                break;
            }
        }

        if (sourceLibName == null) return false;

        if (!player.HasAnimationLibrary(LibraryName))
        {
            var srcLib = player.GetAnimationLibrary(sourceLibName);
            var localLib = new AnimationLibrary();
            foreach (var animName in srcLib.GetAnimationList())
            {
                var copy = (Animation)srcLib.GetAnimation(animName).Duplicate();
                foreach (var loopName in LoopingClips)
                {
                    if (animName == loopName)
                    {
                        copy.LoopMode = Animation.LoopModeEnum.Linear;
                        break;
                    }
                }
                localLib.AddAnimation(animName, copy);
            }
            player.AddAnimationLibrary(LibraryName, localLib);
        }

        prefix = LibraryName + "/";
        return true;
    }
}
