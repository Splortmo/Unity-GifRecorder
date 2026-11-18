using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GifRecorder
{
    /// <summary>
    /// Singleton access. There is at most one <see cref="ScreenRecorder"/>: <see cref="Init"/> creates it explicitly,
    /// a recorder placed in a scene registers itself, and any later recorder removes itself. The static
    /// recording and screenshot calls use that instance, or create a temporary one that destroys itself once its
    /// work is done.
    /// </summary>
    public sealed partial class ScreenRecorder
    {
        [Tooltip("Keep this recorder across scene loads (DontDestroyOnLoad) until it is destroyed manually. " +
                 "Only one recorder exists at a time; any later one removes itself.")]
        [SerializeField] bool persistent = true;

        /// <summary>The active recorder, or null if there is none.</summary>
        public static ScreenRecorder Instance { get; private set; }

        /// <summary>
        /// True for a recorder created by a static call without <see cref="Init"/>. It isn't kept across scene loads
        /// and destroys itself once its recordings and screenshots have been written.
        /// </summary>
        public bool IsTemporary => temporary;

        /// <summary>Whether this recorder is kept across scene loads.</summary>
        public bool Persistent
        {
            get => persistent;
            set => SetPersistent(value);
        }

        bool temporary;
        bool duplicate;
        int pendingScreenshots;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Instance = null; // for Enter Play Mode without domain reload

        /// <summary>
        /// Creates the recorder, or reconfigures the existing one (which then stops being temporary).
        /// </summary>
        /// <param name="persistent">Keep it across scene loads (DontDestroyOnLoad) until it is destroyed manually.</param>
        /// <param name="showBoundary">Show the guide that frames the recorded part of the screen (<see cref="ShowGuide"/>).</param>
        public static ScreenRecorder Init(bool persistent = true, bool showBoundary = true)
        {
            RequirePlayMode();
            var recorder = Instance;
            if (recorder == null) return Create(persistent, showBoundary, temporary: false);

            recorder.temporary = false;
            recorder.ShowGuide = showBoundary;
            recorder.SetPersistent(persistent);
            return recorder;
        }

        /// <summary>
        /// Starts a new recording on the singleton, creating a temporary recorder if there is none. Does nothing if a
        /// recording is already running. Returns the recorder, e.g. to subscribe to <see cref="RecordingSaved"/>.
        /// </summary>
        /// <param name="settings">Settings for this recording; null uses the recorder's <see cref="Settings"/>.</param>
        public static ScreenRecorder StartRecording(RecorderSettings settings = null)
        {
            var recorder = Acquire();
            recorder.BeginRecording(settings);
            return recorder;
        }

        /// <summary>
        /// Stops the current recording. The returned task completes (on the main thread when awaited) with the path of
        /// the written file; it completes with null if nothing was being recorded.
        /// </summary>
        public static Task<string> StopRecording() =>
            Instance != null ? Instance.EndRecording() : Task.FromResult<string>(null);

        public static void ToggleRecording()
        {
            if (Instance != null && Instance.IsRecording) StopRecording();
            else StartRecording();
        }

        /// <summary>
        /// Saves a full-resolution PNG of the next rendered frame, using <see cref="RecorderSettings.screenshotIncludeUI"/>.
        /// Works while a recording is running. The task completes on the main thread with the file path.
        /// For custom sizes or cameras, or to get the bytes instead of a file, use <see cref="ScreenshotCapture"/>.
        /// </summary>
        /// <param name="settings">Settings for this screenshot; null uses the recorder's <see cref="Settings"/>.</param>
        public static Task<string> TakeScreenshot(RecorderSettings settings = null)
        {
            var recorder = Acquire();
            return recorder.CaptureScreenshot((settings ?? recorder.settings).screenshotIncludeUI, settings);
        }

        /// <param name="includeUI">
        /// True: the final screen image, including Screen Space - Overlay UI (always opaque).
        /// False: the capture camera (or Camera.main) rendered on its own, keeping transparency if
        /// <see cref="RecorderSettings.preserveTransparency"/> is on.
        /// </param>
        /// <param name="settings">Settings for this screenshot; null uses the recorder's <see cref="Settings"/>.</param>
        public static Task<string> TakeScreenshot(bool includeUI, RecorderSettings settings = null) =>
            Acquire().CaptureScreenshot(includeUI, settings);

        static ScreenRecorder Acquire()
        {
            RequirePlayMode();
            return Instance != null ? Instance : Create(persistent: false, showBoundary: false, temporary: true);
        }

        static void RequirePlayMode()
        {
            if (!Application.isPlaying)
                throw new InvalidOperationException("[GifRecorder] The ScreenRecorder can only be used in Play Mode.");
        }

        static ScreenRecorder Create(bool persistent, bool showBoundary, bool temporary)
        {
            var go = new GameObject(temporary ? "Screen Recorder (temporary)" : "Screen Recorder");
            go.SetActive(false); // defer Awake until the fields below are set
            var recorder = go.AddComponent<ScreenRecorder>();
            recorder.persistent = persistent;
            recorder.showGuide = showBoundary;
            recorder.temporary = temporary;
            go.SetActive(true);
            return recorder;
        }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning($"[GifRecorder] A ScreenRecorder already exists on '{Instance.name}'; " +
                                 $"removing the one on '{name}'.", this);
                duplicate = true;
                enabled = false;
                Destroy(this);
                return;
            }
            Instance = this;
            if (persistent) SetPersistent(true);
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void SetPersistent(bool on)
        {
            persistent = on;
            bool isPersistent = gameObject.scene.name == "DontDestroyOnLoad";
            if (on == isPersistent) return;

            if (transform.parent != null)
            {
                Debug.LogWarning("[GifRecorder] Only a root GameObject can change whether it persists across scene loads; " +
                                 $"'{name}' has a parent.", this);
                return;
            }
            if (on) DontDestroyOnLoad(gameObject);
            else SceneManager.MoveGameObjectToScene(gameObject, SceneManager.GetActiveScene());
        }

        /// <summary>Destroys a temporary recorder once nothing is recording, encoding or being captured.</summary>
        void ReleaseIfIdle()
        {
            if (!temporary || this == null) return;
            if (active != null || encoding.Count > 0 || draining.Count > 0 || pendingScreenshots > 0) return;
            if (Instance == this) Instance = null;
            Destroy(gameObject);
        }
    }
}
