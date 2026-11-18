using Fengda.Boiler.Monitoring;
using Fengda.EmissionBounds;

namespace Fengda.Boiler.WebApi.Services;

internal sealed class EffectiveBurningWindowGate(EffectiveBurningRule rule)
{
    private readonly Queue<GateSample> samples = new();
    private bool active;
    private int entryCount;
    private int exitCount;
    private DateTime? lastProcessedAt;

    public int EntrySeconds => entryCount;
    public bool IsActive => active;

    public void Reset()
    {
        samples.Clear();
        active = false;
        entryCount = 0;
        exitCount = 0;
        lastProcessedAt = null;
    }

    public GateSample Process(BoilerSnapshot snapshot)
    {
        if (lastProcessedAt is { } previous && snapshot.RecordedAt != previous.AddSeconds(1))
        {
            Reset();
        }
        lastProcessedAt = snapshot.RecordedAt;

        var centerOpen = IsAbove(snapshot.GYDCS_K0QHL10AA101AI5, 1.0);
        var outerOpen = IsAbove(snapshot.GYDCS_K0QHL10AA101AI4, 1.0);
        var valveRunning = centerOpen || outerOpen;
        var hasFlow = IsFinite(snapshot.GYDCS_K0QHH10CF101, out var gasFlow);
        var hasFrequency = IsFinite(snapshot.GYDCS_K0QHL10AN001P, out var blowerFrequency);
        var hasCurrent = IsFinite(snapshot.GYDCS_K0QHL10AN001I, out var blowerCurrent);
        var gateInputsValid = hasFlow && hasFrequency && hasCurrent;

        bool eligible;
        if (!valveRunning || !gateInputsValid)
        {
            active = false;
            entryCount = 0;
            exitCount = 0;
            eligible = false;
        }
        else
        {
            var entryCandidate = gasFlow >= rule.GasFlowEnterM3H
                && blowerFrequency >= rule.BlowerFrequencyEnterHz
                && blowerCurrent >= rule.BlowerCurrentEnterA;
            var exitCandidate = gasFlow < rule.GasFlowExitM3H
                || blowerFrequency < rule.BlowerFrequencyExitHz
                || blowerCurrent < rule.BlowerCurrentExitA;

            if (active)
            {
                if (exitCandidate)
                {
                    exitCount++;
                    eligible = false;
                    if (exitCount >= rule.ExitConfirmSeconds)
                    {
                        active = false;
                        entryCount = 0;
                        exitCount = 0;
                    }
                }
                else
                {
                    exitCount = 0;
                    eligible = true;
                }
            }
            else if (entryCandidate)
            {
                entryCount++;
                eligible = false;
                if (entryCount >= rule.EnterDwellSeconds)
                {
                    active = true;
                    exitCount = 0;
                    MarkTrailingSamplesEligible(Math.Min(rule.EnterDwellSeconds - 1, samples.Count));
                    eligible = true;
                }
            }
            else
            {
                entryCount = 0;
                eligible = false;
            }
        }

        var current = new GateSample(snapshot.RecordedAt, valveRunning, gateInputsValid, eligible);
        samples.Enqueue(current);
        while (samples.Count > EmissionBoundsOnnxModel.WindowSize)
        {
            samples.Dequeue();
        }
        return current;
    }

    public bool IsWindowEligible(IReadOnlyList<BoilerSnapshot> window)
    {
        if (window.Count != EmissionBoundsOnnxModel.WindowSize || samples.Count != EmissionBoundsOnnxModel.WindowSize)
        {
            return false;
        }

        var gateSamples = samples.ToArray();
        for (var index = 0; index < window.Count; index++)
        {
            if (gateSamples[index].RecordedAt != window[index].RecordedAt || !gateSamples[index].Eligible)
            {
                return false;
            }
        }
        return active;
    }

    public GateSample? Latest => samples.Count == 0 ? null : samples.Last();

    private void MarkTrailingSamplesEligible(int count)
    {
        if (count < 1 || count > samples.Count)
        {
            return;
        }
        var existing = samples.ToArray();
        samples.Clear();
        var firstEligibleIndex = existing.Length - count;
        for (var index = 0; index < existing.Length; index++)
        {
            var sample = existing[index];
            samples.Enqueue(index >= firstEligibleIndex ? sample with { Eligible = true } : sample);
        }
    }

    private static bool IsAbove(double? value, double threshold) =>
        value is { } number && double.IsFinite(number) && number > threshold;

    private static bool IsFinite(double? value, out double number)
    {
        number = value.GetValueOrDefault();
        return value.HasValue && double.IsFinite(number);
    }

    internal sealed record GateSample(DateTime RecordedAt, bool ValveRunning, bool GateInputsValid, bool Eligible);
}
