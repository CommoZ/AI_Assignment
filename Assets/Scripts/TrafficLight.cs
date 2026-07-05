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

    /// <summary>If true, an external controller (e.g. TrafficLightGroup) drives the state.</summary>
    public bool ExternallyDriven { get; set; }

    public State CurrentState => currentState;

    /// <summary>Cars may pass on Green only. Yellow and Red both stop new arrivals.</summary>
    public bool CanPass => currentState == State.Green;

    private void Start()
    {
        ApplyVisual();
    }

    private void Update()
    {
        if (ExternallyDriven) return;

        timer += Time.deltaTime;
        switch (currentState)
        {
            case State.Green:  if (timer >= greenTime)  SetState(State.Yellow); break;
            case State.Yellow: if (timer >= yellowTime) SetState(State.Red);    break;
            case State.Red:    if (timer >= redTime)    SetState(State.Green);  break;
        }
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
