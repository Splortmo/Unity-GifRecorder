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

        /// <summary>Saves a PNG of the next rendered frame using a copy of <paramref name="source"/> (null = <see cref="Settings"/>).</summary>
        Task<string> CaptureScreenshot(bool includeUI, RecorderSettings source)
        {
            var s = (source ?? settings).Clone();
            s.Validate();
            pendingScreenshots++;

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
                try
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
                }
                finally
                {
                    pendingScreenshots--;
                    ReleaseIfIdle();
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }
}
