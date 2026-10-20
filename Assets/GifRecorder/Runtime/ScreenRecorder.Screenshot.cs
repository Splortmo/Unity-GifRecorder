using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;

namespace GifRecorder
{
    public sealed partial class ScreenRecorder
    {
        [Tooltip("Invoked on the main thread with the full path once a screenshot has been written.")]
        [SerializeField] UnityEvent<string> onScreenshotSaved = new UnityEvent<string>();

        /// <summary>Raised on the main thread with the full path once a screenshot has been written.</summary>
        public event Action<string> ScreenshotSaved;

        public UnityEvent<string> OnScreenshotSaved => onScreenshotSaved;

        int guideSuppressed;

        /// <summary>True while a screenshot that includes the UI is being taken, so the guide hides itself.</summary>
        internal bool IsGuideSuppressed => guideSuppressed > 0;

        /// <summary>
        /// Saves a full-resolution PNG of the next rendered frame, using <see cref="RecorderSettings.screenshotIncludeUI"/>.
        /// Works while a recording is running. The task completes on the main thread with the file path.
        /// For custom sizes or cameras, or to get the bytes instead of a file, use <see cref="ScreenshotCapture"/>.
        /// </summary>
        public Task<string> TakeScreenshot() => TakeScreenshot(settings.screenshotIncludeUI);

        /// <param name="includeUI">
        /// True: the final screen image, including Screen Space - Overlay UI (always opaque).
        /// False: the capture camera (or Camera.main) rendered on its own, keeping transparency if
        /// <see cref="RecorderSettings.preserveTransparency"/> is on.
        /// </param>
        public Task<string> TakeScreenshot(bool includeUI)
        {
            var s = settings.Clone();
            s.Validate();

            // The guide is screen-space UI: hide it while a screenshot with UI is taken.
            bool suppress = includeUI && guide != null && guide.isActiveAndEnabled;
            if (suppress) guideSuppressed++;

            var options = new ScreenshotOptions
            {
                IncludeUI = includeUI,
                Camera = s.captureCamera,
                Transparent = !includeUI && s.preserveTransparency,
                SkipFrame = suppress, // the guide hides itself in LateUpdate
                Threaded = s.UsesThreads,
            };

            Task<byte[]> capture;
            try
            {
                capture = ScreenshotCapture.CapturePngAsync(this, options);
            }
            catch (Exception e)
            {
                capture = Task.FromException<byte[]>(e);
            }

            string path = CreateOutputPath(s, "Screenshot", "png");
            return capture.ContinueWith(t =>
            {
                if (suppress) guideSuppressed--;
                if (t.IsFaulted)
                {
                    var error = t.Exception.GetBaseException();
                    Debug.LogError($"[GifRecorder] Screenshot failed: {error.Message}");
                    throw error;
                }

                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllBytes(path, t.Result);

                Debug.Log($"[GifRecorder] Saved screenshot to {path}");
                WebGLBridge.Publish(path, s.webGLDownload, "image/png");
                ScreenshotSaved?.Invoke(path);
                onScreenshotSaved?.Invoke(path);
                return path;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }
}
