using System;
using System.Collections;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GifRecorder
{
    /// <summary>What <see cref="ScreenshotCapture.CapturePngAsync"/> captures.</summary>
    public sealed class ScreenshotOptions
    {
        /// <summary>
        /// True: the final screen image, including Screen Space - Overlay UI (always opaque).
        /// False: <see cref="Camera"/> rendered on its own (no overlay UI).
        /// </summary>
        public bool IncludeUI = true;

        /// <summary>
        /// Output size. 0 for both = native size; 0 for one = derived from the other using the source aspect ratio.
        /// A size with a different aspect ratio crops the centre of the screen (with UI) or reshapes the camera (without UI).
        /// </summary>
        public int Width, Height;

        /// <summary>Camera to render when <see cref="IncludeUI"/> is false. Null = Camera.main.</summary>
        public Camera Camera;

        /// <summary>
        /// Camera only: clear to a fully transparent background and keep the alpha channel in the PNG.
        /// </summary>
        public bool Transparent;

        /// <summary>Camera only: culling mask to render with, e.g. <c>~LayerMask.GetMask("UI")</c>. Null = the camera's own.</summary>
        public int? CullingMask;

        /// <summary>Camera only: MSAA samples (1, 2, 4, 8). 0 = QualitySettings.antiAliasing.</summary>
        public int Antialiasing;

        /// <summary>Wait one frame before capturing (lets overlays hide themselves in LateUpdate).</summary>
        public bool SkipFrame;

        /// <summary>Encode on a worker thread. Defaults to true where threads exist (not on WebGL).</summary>
        public bool Threaded = RecorderSettings.PlatformSupportsThreads;
    }

    /// <summary>Takes PNG screenshots of the screen or of a single camera.</summary>
    public static class ScreenshotCapture
    {
        /// <summary>
        /// 8-bit capture format that matches the project's colour space: sRGB in Linear projects, plain UNorm in Gamma
        /// projects (where an sRGB target would gamma-encode values that are already gamma-encoded).
        /// </summary>
        internal static GraphicsFormat Format =>
            QualitySettings.activeColorSpace == ColorSpace.Linear ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R8G8B8A8_UNorm;

        /// <summary>
        /// Captures the next rendered frame as PNG bytes. <paramref name="host"/> runs the capture coroutine.
        /// The task completes on the main thread.
        /// </summary>
        public static Task<byte[]> CapturePngAsync(MonoBehaviour host, ScreenshotOptions options)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));
            options = options ?? new ScreenshotOptions();
            var tcs = new TaskCompletionSource<byte[]>();
            if (!host.isActiveAndEnabled)
                tcs.SetException(new InvalidOperationException("The host component is disabled."));
            else
                host.StartCoroutine(Capture(options, tcs));
            return tcs.Task;
        }

        /// <summary>Resolves a requested size (0 = derive) against the source size.</summary>
        public static Vector2Int ResolveSize(int width, int height, int sourceWidth, int sourceHeight)
        {
            if (width <= 0 && height <= 0) return new Vector2Int(sourceWidth, sourceHeight);
            if (width <= 0) width = Mathf.Max(1, Mathf.RoundToInt(height * (float)sourceWidth / sourceHeight));
            if (height <= 0) height = Mathf.Max(1, Mathf.RoundToInt(width * (float)sourceHeight / sourceWidth));
            return new Vector2Int(width, height);
        }

        static IEnumerator Capture(ScreenshotOptions o, TaskCompletionSource<byte[]> tcs)
        {
            if (o.SkipFrame) yield return null;
            yield return new WaitForEndOfFrame();

            var format = Format;
            RenderTexture source = null, output = null;
            Vector2Int size = default;
            Exception failure = null;
            bool keepAlpha = false;
            try
            {
                Camera cam = null;
                if (!o.IncludeUI)
                {
                    cam = o.Camera != null ? o.Camera : Camera.main;
                    if (cam == null)
                    {
                        Debug.LogWarning("[GifRecorder] Screenshot without UI needs a camera; capturing the screen instead.");
                    }
                }

                RectInt region;
                bool flip;
                if (cam != null)
                {
                    size = ResolveSize(o.Width, o.Height, Mathf.Max(1, cam.pixelWidth), Mathf.Max(1, cam.pixelHeight));
                    int samples = o.Antialiasing > 0 ? o.Antialiasing : Mathf.Max(1, QualitySettings.antiAliasing);
                    source = new RenderTexture(size.x, size.y, 24, format) { antiAliasing = samples };
                    RenderCamera(cam, source, o);
                    region = new RectInt(0, 0, size.x, size.y);
                    flip = false; // camera textures are stored bottom-up, which is what the PNG encoder expects
                    keepAlpha = o.Transparent;
                }
                else
                {
                    int sw = Mathf.Max(1, Screen.width), sh = Mathf.Max(1, Screen.height);
                    size = ResolveSize(o.Width, o.Height, sw, sh);
                    source = new RenderTexture(sw, sh, 0, format);
                    ScreenCapture.CaptureScreenshotIntoRenderTexture(source);
                    region = CaptureLayout.CenteredCrop(sw, sh, size);
                    flip = SystemInfo.graphicsUVStartsAtTop; // the screen copy is top-down on D3D/Metal/Vulkan
                }

                // Crop, scale, flip and resolve MSAA in one pass into a plain texture we can read back.
                output = new RenderTexture(size.x, size.y, 0, format);
                RecordingSession.Downsample(source, region, output, flip);
            }
            catch (Exception e)
            {
                failure = e;
            }
            finally
            {
                Release(source);
            }
            if (failure != null)
            {
                Release(output);
                tcs.SetException(failure);
                yield break;
            }

            byte[] pixels;
            if (SystemInfo.supportsAsyncGPUReadback)
            {
                var request = AsyncGPUReadback.Request(output, 0, TextureFormat.RGBA32);
                while (!request.done) yield return null;
                if (request.hasError)
                {
                    Release(output);
                    tcs.SetException(new InvalidOperationException("GPU readback failed."));
                    yield break;
                }
                pixels = request.GetData<byte>().ToArray();
            }
            else
            {
                var read = new Texture2D(size.x, size.y, TextureFormat.RGBA32, false, false);
                var previous = RenderTexture.active;
                RenderTexture.active = output;
                read.ReadPixels(new Rect(0, 0, size.x, size.y), 0, 0, false);
                RenderTexture.active = previous;
                pixels = read.GetRawTextureData<byte>().ToArray();
                UnityEngine.Object.Destroy(read);
            }
            Release(output);

            int w = size.x, h = size.y;
            byte[] Encode()
            {
                if (!keepAlpha)
                    for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255; // the back buffer's alpha is meaningless
                // Unity's PNG encoder takes bottom-up rows and is safe to call from any thread.
                return ImageConversion.EncodeArrayToPNG(pixels, GraphicsFormat.R8G8B8A8_UNorm, (uint)w, (uint)h);
            }

            if (o.Threaded && RecorderSettings.PlatformSupportsThreads)
            {
                var task = Task.Run(Encode);
                while (!task.IsCompleted) yield return null;
                if (task.IsFaulted) tcs.SetException(task.Exception.GetBaseException());
                else tcs.SetResult(task.Result);
            }
            else
            {
                // No threads (WebGL): one PNG encode on the main thread is a one-off cost.
                try { tcs.SetResult(Encode()); }
                catch (Exception e) { tcs.SetException(e); }
            }
        }

        static void RenderCamera(Camera cam, RenderTexture target, ScreenshotOptions o)
        {
            var previousTarget = cam.targetTexture;
            var previousMask = cam.cullingMask;
            var previousFlags = cam.clearFlags;
            var previousBackground = cam.backgroundColor;
            try
            {
                if (o.CullingMask.HasValue) cam.cullingMask = o.CullingMask.Value;
                if (o.Transparent)
                {
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                }
                // Assigning the target also gives the camera the target's aspect ratio for this render.
                cam.targetTexture = target;
                RecordingSession.RenderCamera(cam, target);
            }
            finally
            {
                cam.targetTexture = previousTarget;
                cam.cullingMask = previousMask;
                cam.clearFlags = previousFlags;
                cam.backgroundColor = previousBackground;
            }
        }

        static void Release(RenderTexture texture)
        {
            if (texture == null) return;
            texture.Release();
            if (Application.isPlaying) UnityEngine.Object.Destroy(texture);
            else UnityEngine.Object.DestroyImmediate(texture);
        }
    }
}
