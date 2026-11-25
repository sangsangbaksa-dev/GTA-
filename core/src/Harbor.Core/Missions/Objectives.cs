using Harbor.Core.Data;
using Harbor.Core.Math;

namespace Harbor.Core.Missions;

public enum ObjectiveType
{
    /// <summary>Get a party member (or all with <c>all</c>) within <c>radius</c> of <c>at</c>.</summary>
    Reach,
    /// <summary>Hold interact within <c>radius</c> of <c>at</c> for <c>seconds</c> (hack, drill, threaten). Taking damage pauses it.</summary>
    Interact,
    /// <summary>Keep at least one party member inside the area for <c>seconds</c> (defend).</summary>
    Hold,
    /// <summary>Every surviving party member gets farther than <c>distance</c> from <c>at</c>.</summary>
    Escape,
    /// <summary>Spawn <c>count</c> carry items of kind <c>item</c> at <c>at</c>; deliver them to <c>to</c>.</summary>
    Deliver,
}

/// <summary>One step of a data-driven mission (data/activities/contracts/*.json → objectives[]).</summary>
public sealed record ObjectiveDef
{
    public ObjectiveType Type { get; init; }
    /// <summary>Localization key shown on the HUD.</summary>
    public string Label { get; init; } = "";
    public double[] At { get; init; } = new double[3];
    public double Radius { get; init; } = 3;
    public double Seconds { get; init; }
    public bool All { get; init; }
    /// <summary>Interact: progress drops to 0 instead of pausing when interrupted.</summary>
    public bool ResetOnInterrupt { get; init; }
    /// <summary>Crime reported to Heat when the objective is first worked on.</summary>
    public string Crime { get; init; } = "";
    public double Distance { get; init; } = 150;
    public string Item { get; init; } = "";
    public int Count { get; init; } = 1;
    public long ValueEach { get; init; }
    public double[] To { get; init; } = new double[3];

    public Vec3d AtPos => At.ToVec();
    public Vec3d ToPos => To.ToVec();

    public void Validate(string owner)
    {
        Json.Require(At.Length == 3 && To.Length == 3, $"{owner}: objective positions must be [x,y,z]");
        Json.Require(Radius > 0, $"{owner}: objective radius must be > 0");
        switch (Type)
        {
            case ObjectiveType.Interact or ObjectiveType.Hold:
                Json.Require(Seconds > 0, $"{owner}: {Type} needs seconds > 0"); break;
            case ObjectiveType.Escape:
                Json.Require(Distance > Radius, $"{owner}: escape distance must exceed radius"); break;
            case ObjectiveType.Deliver:
                Json.Require(Item.Length > 0 && Count is > 0 and <= 8 && ValueEach >= 0, $"{owner}: deliver needs item, count 1..8, valueEach >= 0"); break;
        }
    }
}
