namespace Harbor.Core.Sim;

/// <summary>Converts variable frame time into fixed simulation steps.
/// The engine calls <see cref="Advance"/> every frame and uses <see cref="Alpha"/> to interpolate visuals.</summary>
public sealed class FixedTickLoop(double tickRateHz = 30.0, int maxStepsPerFrame = 5)
{
    public double StepSeconds { get; } = 1.0 / tickRateHz;
    public long Tick { get; private set; }
    public double Alpha => _accumulator / StepSeconds;

    private double _accumulator;

    /// <returns>Number of fixed steps executed.</returns>
    public int Advance(double frameSeconds, Action<double> step)
    {
        _accumulator += frameSeconds;
        var steps = 0;
        while (_accumulator >= StepSeconds && steps < maxStepsPerFrame)
        {
            step(StepSeconds);
            _accumulator -= StepSeconds;
            Tick++;
            steps++;
        }
        if (steps == maxStepsPerFrame) _accumulator = 0; // drop backlog instead of spiralling
        return steps;
    }
}
