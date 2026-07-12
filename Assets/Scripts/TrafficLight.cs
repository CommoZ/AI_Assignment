using UnityEngine;

/// <summary>
/// Simple Red/Yellow/Green traffic light state machine.
/// Colours a target renderer if one is assigned.
/// A <see cref="TrafficLightGroup"/> can override cycling and drive the state externally.
/// </summary>
public class TrafficLight : MonoBehaviour
{
    public enum State { Green, Yellow, Red }

    [Header("Timings (seconds)")]
    public float greenTime  = 6f;
    public float yellowTime = 2f;
    public float redTime    = 6f;

    [Header("Visual (optional)")]
    [Tooltip("Renderer whose material colour will be tinted to the light state.")]
    public Renderer lightRenderer;

    [Header("Runtime")]
    [SerializeField] private State currentState = State.Red;
    private float timer;

    [Header("Manual control")]
    [Tooltip("If true, this light does NOT auto-cycle. Its state is set by clicks (ClickableTrafficLight) or by calling Toggle()/SetState().")]
    public bool manualControl = false;

    /// <summary>If true, an external controller (e.g. TrafficLightGroup) drives the state.</summary>
    public bool ExternallyDriven { get; set; }

    public State CurrentState => currentState;

    /// <summary>Cars may pass freely on Green. Yellow and Red stop new arrivals.</summary>
    public bool CanPass => currentState == State.Green;

    /// <summary>
    /// Whether a car this far from the stop line must stop for the light.
    ///  - Green: never.
    ///  - Red: always.
    ///  - Yellow: yes, UNLESS the car is already inside <paramref name="commitDistance"/> and can
    ///    no longer stop safely, in which case it clears the junction (dilemma zone). This stops
    ///    a shared-yellow phase from freezing cars that are already committed.
    /// </summary>
    public bool RequiresStop(float distanceToStopLine, float commitDistance)
    {
        switch (currentState)
        {
            case State.Green:  return false;
            case State.Yellow: return distanceToStopLine > commitDistance;
            default:           return true; // Red
        }
    }

    private void Start()
    {
        ApplyVisual();
    }

    private void Update()
    {
        if (ExternallyDriven || manualControl) return;

        timer += Time.deltaTime;
        switch (currentState)
        {
            case State.Green:  if (timer >= greenTime)  SetState(State.Yellow); break;
            case State.Yellow: if (timer >= yellowTime) SetState(State.Red);    break;
            case State.Red:    if (timer >= redTime)    SetState(State.Green);  break;
        }
    }

    /// <summary>Flip straight between Green (go) and Red (stop). Used by click control.</summary>
    public void Toggle()
    {
        SetState(currentState == State.Green ? State.Red : State.Green);
    }

    public void SetState(State newState)
    {
        currentState = newState;
        timer = 0f;
        ApplyVisual();
    }

    private void ApplyVisual()
    {
        if (lightRenderer == null) return;
        Color c = currentState switch
        {
            State.Green  => Color.green,
            State.Yellow => new Color(1f, 0.85f, 0.1f),
            _            => Color.red,
        };
        // Use a per-instance material so tinting doesn't affect other objects.
        lightRenderer.material.color = c;
    }
}
