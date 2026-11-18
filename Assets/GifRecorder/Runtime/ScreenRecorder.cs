using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;
#if ENABLE_INPUT_SYSTEM && GIFREC_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace GifRecorder
{
    /// <summary>
    /// Records the game screen (including overlay UI) or a single camera (with transparency) to an animated GIF or
    /// APNG at runtime. Capture happens on the GPU with asynchronous readback where available; encoding runs on
    /// background threads, or time-sliced on the main thread on WebGL.
    /// </summary>
    [AddComponentMenu("Recording/Screen Recorder")]
    [DisallowMultipleComponent]
    public sealed partial class ScreenRecorder : MonoBehaviour
    {
        [SerializeField] RecorderSettings settings = new RecorderSettings();

        [Tooltip("Start recording as soon as this component starts.")]
        [SerializeField] bool recordOnStart;

        [Tooltip("Toggle recording and take screenshots with keyboard keys.")]
        [SerializeField] bool enableHotkey = true;

#if ENABLE_INPUT_SYSTEM && GIFREC_INPUT_SYSTEM
        [SerializeField] Key toggleKey = Key.F9;
        [SerializeField] Key screenshotKey = Key.F8;
#elif ENABLE_LEGACY_INPUT_MANAGER
        [SerializeField] KeyCode toggleKey = KeyCode.F9;
        [SerializeField] KeyCode screenshotKey = KeyCode.F8;
#endif

        [Tooltip("Show guide lines framing the recorded part of the screen (adds a Recording Guide component at runtime). " +
                 "The guide is drawn outside the recorded area, so it never appears in recordings.")]
        [SerializeField] bool showGuide = true;

        [Tooltip("Invoked on the main thread with the full path once a file has been written.")]
        [SerializeField] UnityEvent<string> onRecordingSaved = new UnityEvent<string>();

        /// <summary>Raised on the main thread with the full path once a file has been written.</summary>
        public event Action<string> RecordingSaved;

        /// <summary>Raised on the main thread if a recording could not be written.</summary>
        public event Action<Exception> RecordingFailed;

        RecordingSession active;
        readonly List<RecordingSession> draining = new List<RecordingSession>();
        readonly List<RecordingSession> encoding = new List<RecordingSession>();
        Coroutine captureLoop;

        /// <summary>Settings used for the next recording. Changes don't affect a recording in progress.</summary>
        public RecorderSettings Settings
        {
            get => settings;
            set => settings = value ?? throw new ArgumentNullException(nameof(value));
        }

        public UnityEvent<string> OnRecordingSaved => onRecordingSaved;

        /// <summary>Show the on-screen guide lines. Independent of <see cref="RecorderSettings.fixedOutputSize"/>.</summary>
        public bool ShowGuide
        {
            get => showGuide;
            set
            {
                showGuide = value;
                SyncGuide();
            }
        }

        RecordingGuide guide;
        bool guideSynced;

        void SyncGuide()
        {
            guideSynced = true;
            if (!Application.isPlaying) return;
            if (guide == null) guide = GetComponent<RecordingGuide>();
            if (guide == null && showGuide) guide = gameObject.AddComponent<RecordingGuide>();
            if (guide != null) guide.enabled = showGuide;
        }

        public bool IsRecording => active != null;

        /// <summary>True while any finished recording is still being encoded or written.</summary>
        public bool IsEncoding => encoding.Count > 0;

        /// <summary>Seconds recorded so far in the current recording.</summary>
        public float RecordingTime => active != null ? (float)active.Elapsed(Time.unscaledTimeAsDouble) : 0f;

        public int CapturedFrameCount => active?.CapturedFrames ?? 0;

        /// <summary>Frames captured but not yet written, across all recordings.</summary>
        public int PendingFrameCount
        {
            get
            {
                int count = 0;
                foreach (var session in encoding) count += session.PendingFrames;
                if (active != null) count += active.PendingFrames;
                return count;
            }
        }

        /// <summary>Output path of the current recording, or null.</summary>
        public string CurrentOutputPath => active?.OutputPath;

        /// <summary>Output size of the current recording.</summary>
        public Vector2Int CurrentOutputSize => active?.Size ?? Vector2Int.zero;

        /// <summary>
        /// The part of the screen the current recording captures (or the next one would, with the current settings),
        /// in screen pixels with the origin at the bottom-left, plus the output size it is scaled to.
        /// Returns false when the capture camera doesn't draw to the screen.
        /// </summary>
        public bool TryGetCaptureRegion(out RectInt screenRegion, out Vector2Int outputSize)
        {
            var s = active != null ? active.Settings : settings;
            int vx = 0, vy = 0, vw = Screen.width, vh = Screen.height;
            if (s.captureSource == CaptureSource.Camera)
            {
                var cam = s.captureCamera != null ? s.captureCamera : Camera.main;
                if (cam == null || cam.targetTexture != null)
                {
                    screenRegion = default;
                    outputSize = default;
                    return false;
                }
                var viewport = cam.pixelRect;
                vx = Mathf.RoundToInt(viewport.x);
                vy = Mathf.RoundToInt(viewport.y);
                vw = Mathf.RoundToInt(viewport.width);
                vh = Mathf.RoundToInt(viewport.height);
            }
            vw = Math.Max(1, vw);
            vh = Math.Max(1, vh);

            RectInt region;
            if (active != null)
            {
                outputSize = active.Size;
                region = active.CurrentRegion(vw, vh);
            }
            else
            {
                var layout = s.GetCaptureLayout(vw, vh);
                outputSize = layout.Output;
                region = layout.Source;
            }
            screenRegion = new RectInt(vx + region.x, vy + region.y, region.width, region.height);
            return true;
        }

        void OnValidate()
        {
            settings?.Validate();
            guideSynced = false; // apply a changed checkbox on the next Update (components can't be added here)
        }

        void Start()
        {
            if (duplicate) return;
            SyncGuide();
            if (recordOnStart) BeginRecording(null);
        }

        void Update()
        {
            if (duplicate) return;
            if (!guideSynced) SyncGuide();
            if (enableHotkey && KeyPressed(toggleKey))
            {
                if (IsRecording) EndRecording();
                else BeginRecording(null);
            }
            if (enableHotkey && KeyPressed(screenshotKey)) CaptureScreenshot(settings.screenshotIncludeUI, null);

            if (active != null && active.Settings.maxDuration > 0f &&
                active.Elapsed(Time.unscaledTimeAsDouble) >= active.Settings.maxDuration)
            {
                EndRecording();
            }

            PollDraining();
            PumpTimeSliced();
        }

        /// <summary>Gives time-sliced recordings their per-frame encoding budget, oldest recording first.</summary>
        void PumpTimeSliced()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            foreach (var session in encoding)
            {
                if (!session.IsTimeSliced || session.IsFinished) continue;
                double remaining = session.Settings.timeSliceBudgetMs - stopwatch.Elapsed.TotalMilliseconds;
                if (remaining <= 0) return;
                session.Pump(remaining);
            }
            if (active != null && active.IsTimeSliced)
            {
                double remaining = active.Settings.timeSliceBudgetMs - stopwatch.Elapsed.TotalMilliseconds;
                if (remaining > 0) active.Pump(remaining);
            }
        }

#if ENABLE_INPUT_SYSTEM && GIFREC_INPUT_SYSTEM
        static bool KeyPressed(Key key)
        {
            var keyboard = Keyboard.current;
            return keyboard != null && key != Key.None && keyboard[key].wasPressedThisFrame;
        }
#elif ENABLE_LEGACY_INPUT_MANAGER
        static bool KeyPressed(KeyCode key) => key != KeyCode.None && Input.GetKeyDown(key);
#else
        static bool KeyPressed(int key) => false;
        const int toggleKey = 0, screenshotKey = 0;
#endif

        /// <summary>Starts a new recording with a copy of <paramref name="source"/> (null = <see cref="Settings"/>).</summary>
        void BeginRecording(RecorderSettings source)
        {
            if (active != null) return;
            if (!isActiveAndEnabled)
            {
                Debug.LogWarning("[GifRecorder] Cannot start recording on a disabled ScreenRecorder.", this);
                return;
            }

            var snapshot = (source ?? settings).Clone();
            snapshot.Validate();
            if (snapshot.preserveTransparency && snapshot.captureSource != CaptureSource.Camera)
                Debug.LogWarning("[GifRecorder] Preserve Transparency needs Capture Source = Camera: the screen back buffer " +
                                 "has no alpha. Recording without transparency.", this);
            string path = CreateOutputPath(snapshot);

            try
            {
                active = new RecordingSession(snapshot, path, Time.unscaledTimeAsDouble);
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
                RecordingFailed?.Invoke(e);
                ReleaseIfIdle();
                return;
            }

            if (captureLoop == null) captureLoop = StartCoroutine(CaptureLoop());
        }

        Task<string> EndRecording()
        {
            if (active == null) return Task.FromResult<string>(null);

            var session = active;
            active = null;

            var task = session.Stop(Time.unscaledTimeAsDouble);
            if (!session.IsFinalized) draining.Add(session);
            encoding.Add(session);

            task.ContinueWith(t =>
            {
                encoding.Remove(session);
                if (t.IsFaulted)
                {
                    var error = t.Exception.GetBaseException();
                    Debug.LogError($"[GifRecorder] Failed to write {session.OutputPath}: {error.Message}");
                    Debug.LogException(error);
                    RecordingFailed?.Invoke(error);
                }
                else
                {
                    Debug.Log($"[GifRecorder] Saved {session.CapturedFrames} frames ({session.Size.x}x{session.Size.y}) to {t.Result}");
                    WebGLBridge.Publish(t.Result, session.Settings.webGLDownload);
                    RecordingSaved?.Invoke(t.Result);
                    onRecordingSaved?.Invoke(t.Result);
                }
                ReleaseIfIdle();
            }, TaskScheduler.FromCurrentSynchronizationContext());

            return task;
        }

        IEnumerator CaptureLoop()
        {
            var endOfFrame = new WaitForEndOfFrame();
            while (active != null)
            {
                yield return endOfFrame;
                active?.PollReadbacks();
                active?.OnEndOfFrame(Time.unscaledTimeAsDouble, Time.unscaledDeltaTime);
            }
            captureLoop = null;
        }

        void PollDraining()
        {
            for (int i = draining.Count - 1; i >= 0; i--)
            {
                draining[i].PollReadbacks();
                if (draining[i].IsFinalized) draining.RemoveAt(i);
            }
        }

        void FlushAll()
        {
            active?.FlushReadbacks();
            foreach (var session in draining) session.FlushReadbacks();
            draining.Clear();
        }

        void OnDisable()
        {
            if (active != null) EndRecording();
            FlushAll();
            if (captureLoop != null)
            {
                StopCoroutine(captureLoop);
                captureLoop = null;
            }
        }

        void OnApplicationQuit()
        {
            if (active != null) EndRecording();
            FlushAll();
#if !UNITY_EDITOR
            // Background threads die with the player process, so give pending files a chance to finish.
            foreach (var session in encoding.ToArray())
            {
                if (session.IsTimeSliced)
                {
                    session.Pump(30000);
                    continue;
                }
                try { session.Completion.Wait(TimeSpan.FromSeconds(30)); }
                catch (Exception) { /* reported through the task continuation */ }
            }
#endif
        }

        static string CreateOutputPath(RecorderSettings s, string kind = null, string extension = null)
        {
            string folder = s.ResolveOutputFolder();
            string prefix = string.IsNullOrWhiteSpace(s.fileNamePrefix) ? "Recording" : s.fileNamePrefix.Trim();
            if (kind != null) prefix += "_" + kind;
            foreach (char c in Path.GetInvalidFileNameChars()) prefix = prefix.Replace(c, '_');

            extension = extension ?? (s.format == RecordingFormat.Apng ? "png" : "gif");
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string path = Path.Combine(folder, $"{prefix}_{stamp}.{extension}");
            for (int i = 2; File.Exists(path) || File.Exists(path + ".part"); i++)
                path = Path.Combine(folder, $"{prefix}_{stamp}_{i}.{extension}");
            return path;
        }
    }
}
