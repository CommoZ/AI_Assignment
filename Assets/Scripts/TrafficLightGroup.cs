using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Coordinates a set of traffic lights so they are out-of-phase.
/// Typical use: an intersection with lights A (N/S) and B (E/W) — while A is Green,
/// B is Red, then both go Yellow together, then swap.
/// Add each side as its own list; all lights inside a list share the same state.
/// </summary>
public class TrafficLightGroup : MonoBehaviour
{
    [Tooltip("Lights that share phase A (green first).")]
    public List<TrafficLight> phaseA = new List<TrafficLight>();

    [Tooltip("Lights that share phase B (red first).")]
    public List<TrafficLight> phaseB = new List<TrafficLight>();

    public float greenTime  = 6f;
    public float yellowTime = 2f;

    private float timer;
    private int step; // 0: A green / B red, 1: A yellow / B red, 2: A red / B green, 3: A red / B yellow

    private void Start()
    {
        // Take ownership of every non-manual light. A light a user has grabbed via
        // ClickableTrafficLight (manualControl) is left alone so the two don't fight.
        foreach (var l in phaseA) if (l != null && !l.manualControl) l.ExternallyDriven = true;
        foreach (var l in phaseB) if (l != null && !l.manualControl) l.ExternallyDriven = true;
        ApplyStep();
    }

    private void Update()
    {
        timer += Time.deltaTime;
        float duration = (step == 1 || step == 3) ? yellowTime : greenTime;
        if (timer >= duration)
        {
            timer = 0f;
            step = (step + 1) % 4;
            ApplyStep();
        }
    }

    private void ApplyStep()
    {
        switch (step)
        {
            case 0: SetAll(phaseA, TrafficLight.State.Green);  SetAll(phaseB, TrafficLight.State.Red);    break;
            case 1: SetAll(phaseA, TrafficLight.State.Yellow); SetAll(phaseB, TrafficLight.State.Red);    break;
            case 2: SetAll(phaseA, TrafficLight.State.Red);    SetAll(phaseB, TrafficLight.State.Green);  break;
            case 3: SetAll(phaseA, TrafficLight.State.Red);    SetAll(phaseB, TrafficLight.State.Yellow); break;
        }
    }

    private static void SetAll(List<TrafficLight> lights, TrafficLight.State s)
    {
        // Skip manually-controlled lights: precedence is manualControl > group > auto-cycle.
        foreach (var l in lights) if (l != null && !l.manualControl) l.SetState(s);
    }
}
