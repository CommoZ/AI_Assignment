using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

/// <summary>
/// Keyboard shortcuts to jump between the generated scenes and to reset the current one, via the
/// Unity <see cref="SceneManager"/>. Function keys are used (not number keys) so they never collide
/// with typing into the seed field in <see cref="SimulationUI"/>.
///
///   F1 City · F2 Junctions · F3 Highway · F4 Ring · F5 reset (reload current scene)
///
/// Target scenes must be in Build Settings (the generator adds them on save; or run
/// <c>Traffic Sim ▸ Add Generated Scenes To Build Settings</c>). Added at runtime by
/// <see cref="SimulationUI"/>, so no scene regeneration is needed.
/// </summary>
public class SceneSwitcher : MonoBehaviour
{
    [System.Serializable]
    public struct Shortcut { public Key key; public string sceneName; }

    [Tooltip("Function-key -> scene-name map. Scene names are the .unity file names (no extension).")]
    public Shortcut[] shortcuts =
    {
        new Shortcut { key = Key.F1, sceneName = "City" },
        new Shortcut { key = Key.F2, sceneName = "Junctions" },
        new Shortcut { key = Key.F3, sceneName = "Highway" },
        new Shortcut { key = Key.F4, sceneName = "Ring" },
    };

    [Tooltip("Key that reloads (resets) the current scene.")]
    public Key resetKey = Key.F5;

    private void Update()
    {
        Keyboard kb = Keyboard.current;
        if (kb == null) return;

        if (kb[resetKey].wasPressedThisFrame)
        {
            LoadByName(SceneManager.GetActiveScene().name); // reload = reset
            return;
        }

        for (int i = 0; i < shortcuts.Length; i++)
        {
            if (kb[shortcuts[i].key].wasPressedThisFrame)
            {
                LoadByName(shortcuts[i].sceneName);
                return;
            }
        }
    }

    private static void LoadByName(string sceneName)
    {
        if (string.IsNullOrEmpty(sceneName)) return;
        if (!Application.CanStreamedLevelBeLoaded(sceneName))
        {
            Debug.LogWarning($"[SceneSwitcher] Scene '{sceneName}' is not in Build Settings. " +
                             "Run 'Traffic Sim ▸ Add Generated Scenes To Build Settings' (or regenerate it).");
            return;
        }
        SceneManager.LoadScene(sceneName);
    }
}
