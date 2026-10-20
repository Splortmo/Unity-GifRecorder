using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using GifRecorder.Encoding;
using Unity.Collections;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GifRecorder
{
    /// <summary>
    /// One recording: grabs the back buffer (or renders a camera) at the end of a frame, downsamples it on the GPU,
    /// reads it back asynchronously and feeds the pixels to an <see cref="EncodingPipeline"/>. Main-thread only.
    /// </summary>
    internal sealed class RecordingSession
    {
        static GraphicsFormat Format => ScreenshotCapture.Format;

        static readonly ProfilerMarker CaptureMarker = new ProfilerMarker("GifRecorder.Capture");
        static readonly ProfilerMarker DeliverMarker = new ProfilerMarker("GifRecorder.Deliver");

        readonly RecorderSettings settings;
        readonly EncodingPipeline pipeline;
        readonly FramePool pool;
        readonly int width, height;
        readonly double startTime, interval;
        readonly Queue<PendingReadback> readbacks = new Queue<PendingReadback>();

        // Readback rows must come out top to bottom. A camera-rendered texture is stored bottom-up everywhere, but
        // CaptureScreenshotIntoRenderTexture is only bottom-up on OpenGL-like APIs; flip during the downsample.
        readonly bool flipScreen = !SystemInfo.graphicsUVStartsAtTop;
        const bool FlipCamera = true;
        readonly bool asyncReadback = SystemInfo.supportsAsyncGPUReadback;
        readonly Camera camera;

        RenderTexture sourceTexture, outputTexture;
        Texture2D readTexture;
        double nextCaptureTime;
        double stopTime = -1;
        bool backlogWarned;
        bool finalized;

        struct PendingReadback
        {
            public AsyncGPUReadbackRequest Request;
            public double TimeMs;
        }

        public string OutputPath => pipeline.OutputPath;
        public RecorderSettings Settings => settings;
        public bool IsStopped => stopTime >= 0;
        public int CapturedFrames { get; private set; }
        public int PendingFrames => pipeline.PendingFrames + readbacks.Count;
        public Task<string> Completion => pipeline.Completion;
        public Vector2Int Size => new Vector2Int(width, height);
        public bool IsTimeSliced => pipeline.IsTimeSliced;
        public bool IsFinished => pipeline.Completion.IsCompleted;

        public double Elapsed(double now) => (IsStopped ? stopTime : now) - startTime;

        public RecordingSession(RecorderSettings settings, string outputPath, double now)
        {
            this.settings = settings;
            var encoder = settings.CreateEncoder();

            int sourceWidth = Screen.width, sourceHeight = Screen.height;
            if (settings.captureSource == CaptureSource.Camera)
            {
                camera = settings.captureCamera != null ? settings.captureCamera : Camera.main;
                if (camera == null)
                    throw new InvalidOperationException("Capture Source is Camera, but no camera is assigned and there is no Camera.main.");
                sourceWidth = camera.pixelWidth;
                sourceHeight = camera.pixelHeight;
            }

            var size = settings.GetCaptureLayout(sourceWidth, sourceHeight).Output;
            width = size.x;
            height = size.y;
            interval = Math.Max(1.0 / settings.frameRate, encoder.MinDelayMs / 1000.0);
            startTime = now;
            nextCaptureTime = now;

            // With threads, keep a few spare buffers allocated in the background so the main thread never has to.
            bool threaded = settings.UsesThreads;
            pool = new FramePool(width, height, threaded ? Math.Min(8, settings.maxQueuedFrames) : 0);
            pipeline = new EncodingPipeline(encoder, width, height, outputPath, interval * 1000.0, threaded);

            outputTexture = new RenderTexture(width, height, 0, Format) { name = "GifRecorder Output", filterMode = FilterMode.Bilinear };
            outputTexture.Create();
        }

        /// <summary>Call after rendering has finished for the frame (WaitForEndOfFrame).</summary>
        public void OnEndOfFrame(double now, float deltaTime)
        {
            if (IsStopped) return;

            // Snap to the closest rendered frame instead of always the first one after the scheduled time.
            if (now + deltaTime * 0.5 < nextCaptureTime) return;
            nextCaptureTime += interval;
            if (nextCaptureTime <= now) nextCaptureTime = now + interval;

            if (PendingFrames >= settings.maxQueuedFrames)
            {
                if (!backlogWarned)
                {
                    backlogWarned = true;
                    Debug.LogWarning($"[GifRecorder] Encoder is falling behind ({PendingFrames} frames queued); skipping captures. " +
                                     "Lower the resolution or frame rate, or raise Max Queued Frames.");
                }
                return;
            }

            Capture((now - startTime) * 1000.0);
        }

        void Capture(double timeMs)
        {
            using var _ = CaptureMarker.Auto();

            if (camera != null)
            {
                if (!camera) return; // destroyed mid-recording
                // Render at up to 2x the output size (measured over the recorded region): enough supersampling
                // without paying for full resolution.
                int pw = Math.Max(1, camera.pixelWidth), ph = Math.Max(1, camera.pixelHeight);
                var crop = CropFor(pw, ph);
                double factor = Math.Min(1.0, Math.Max(2.0 * width / crop.width, 2.0 * height / crop.height));
                int cw = Math.Max(1, (int)Math.Round(pw * factor));
                int ch = Math.Max(1, (int)Math.Round(ph * factor));
                EnsureSource(cw, ch, 24, "GifRecorder Camera");
                RenderCamera(camera, sourceTexture);
                Downsample(sourceTexture, CropFor(cw, ch), outputTexture, FlipCamera);
            }
            else
            {
                int sw = Screen.width, sh = Screen.height;
                if (sw <= 0 || sh <= 0) return;
                EnsureSource(sw, sh, 0, "GifRecorder Screen");
                ScreenCapture.CaptureScreenshotIntoRenderTexture(sourceTexture);
                Downsample(sourceTexture, CropFor(sw, sh), outputTexture, flipScreen);
            }
            CapturedFrames++;

            if (asyncReadback)
            {
                var request = AsyncGPUReadback.Request(outputTexture, 0, TextureFormat.RGBA32);
                readbacks.Enqueue(new PendingReadback { Request = request, TimeMs = timeMs });
            }
            else
            {
                if (readTexture == null) readTexture = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
                var active = RenderTexture.active;
                RenderTexture.active = outputTexture;
                readTexture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                RenderTexture.active = active;
                Deliver(readTexture.GetRawTextureData<byte>(), timeMs);
            }
        }

        /// <summary>
        /// Recorded region of a source of the given size. With a fixed output size this is a centred crop with the
        /// output's aspect ratio; otherwise the whole source (rescaled if the source was resized mid-recording).
        /// </summary>
        RectInt CropFor(int sourceWidth, int sourceHeight) =>
            settings.fixedOutputSize
                ? CaptureLayout.CenteredCrop(sourceWidth, sourceHeight, new Vector2Int(width, height))
                : new RectInt(0, 0, sourceWidth, sourceHeight);

        /// <summary>Region the current recording captures, in source pixels (origin bottom-left).</summary>
        public RectInt CurrentRegion(int sourceWidth, int sourceHeight) => CropFor(sourceWidth, sourceHeight);

        void EnsureSource(int w, int h, int depth, string name)
        {
            if (sourceTexture != null && sourceTexture.width == w && sourceTexture.height == h) return;
            ReleaseTexture(ref sourceTexture);
            sourceTexture = new RenderTexture(w, h, depth, Format) { name = name };
            sourceTexture.Create();
        }

        internal static void RenderCamera(Camera cam, RenderTexture target)
        {
            // Render requests work with any SRP (URP/HDRP); the built-in pipeline renders through targetTexture.
            var request = new RenderPipeline.StandardRequest { destination = target };
            if (RenderPipeline.SupportsRenderRequest(cam, request))
            {
                RenderPipeline.SubmitRenderRequest(cam, request);
                return;
            }
            var previous = cam.targetTexture;
            cam.targetTexture = target;
            cam.Render();
            cam.targetTexture = previous;
        }

        /// <summary>
        /// Crops <paramref name="region"/> out of the source and downsamples it in successive halvings, so large
        /// reductions don't alias. Cropping (and flipping) happen in the first blit.
        /// </summary>
        internal static void Downsample(RenderTexture source, RectInt region, RenderTexture destination, bool flip)
        {
            // Blit samples uv * scale + offset. Recorded regions are always vertically centred, so the same v range
            // is correct whichever way up the source texture happens to be stored.
            float sx = (float)region.width / source.width, sy = (float)region.height / source.height;
            float ox = (float)region.x / source.width;
            var scale = flip ? new Vector2(sx, -sy) : new Vector2(sx, sy);
            var offset = flip
                ? new Vector2(ox, (float)(source.height - region.y) / source.height)
                : new Vector2(ox, (float)region.y / source.height);

            RenderTexture current = source, temporary = null;
            int w = region.width, h = region.height;
            while (w > destination.width * 2 || h > destination.height * 2)
            {
                w = Math.Max(destination.width, w / 2);
                h = Math.Max(destination.height, h / 2);
                var next = RenderTexture.GetTemporary(w, h, 0, Format);
                next.filterMode = FilterMode.Bilinear;
                Graphics.Blit(current, next, scale, offset);
                scale = Vector2.one;
                offset = Vector2.zero;
                if (temporary != null) RenderTexture.ReleaseTemporary(temporary);
                current = temporary = next;
            }
            Graphics.Blit(current, destination, scale, offset);
            if (temporary != null) RenderTexture.ReleaseTemporary(temporary);
        }

        /// <summary>Hands finished GPU readbacks to the encoder, in capture order.</summary>
        public void PollReadbacks()
        {
            while (readbacks.Count > 0 && readbacks.Peek().Request.done)
                Complete(readbacks.Dequeue());
            TryFinalize();
        }

        /// <summary>Blocks until all GPU readbacks are done. Used when the recorder is torn down.</summary>
        public void FlushReadbacks()
        {
            while (readbacks.Count > 0)
            {
                var pending = readbacks.Dequeue();
                pending.Request.WaitForCompletion();
                Complete(pending);
            }
            TryFinalize();
        }

        void Complete(PendingReadback pending)
        {
            if (pending.Request.hasError)
            {
                Debug.LogWarning("[GifRecorder] GPU readback failed; dropping a frame.");
                return;
            }
            Deliver(pending.Request.GetData<byte>(), pending.TimeMs);
        }

        void Deliver(NativeArray<byte> data, double timeMs)
        {
            using var _ = DeliverMarker.Auto();
            var frame = pool.Rent();
            int length = width * height * 4;
            if (data.Length < length)
            {
                frame.Release();
                Debug.LogWarning("[GifRecorder] Readback returned an unexpected amount of data; dropping a frame.");
                return;
            }
            NativeArray<byte>.Copy(data, frame.Pixels, length);
            pipeline.AddFrame(frame, timeMs);
        }

        /// <summary>Stops capturing. The returned task completes once the file has been written.</summary>
        public Task<string> Stop(double now)
        {
            if (!IsStopped) stopTime = now;
            TryFinalize();
            return pipeline.Completion;
        }

        void TryFinalize()
        {
            if (finalized || !IsStopped || readbacks.Count > 0) return;
            finalized = true;
            pipeline.Complete();
            ReleaseTexture(ref sourceTexture);
            ReleaseTexture(ref outputTexture);
            if (readTexture != null)
            {
                DestroyObject(readTexture);
                readTexture = null;
            }
        }

        public bool IsFinalized => finalized;

        /// <summary>Time-sliced mode: advances encoding by about <paramref name="budgetMs"/>. Returns true when the file is done.</summary>
        public bool Pump(double budgetMs) => pipeline.Pump(budgetMs);

        static void ReleaseTexture(ref RenderTexture texture)
        {
            if (texture == null) return;
            texture.Release();
            DestroyObject(texture);
            texture = null;
        }

        static void DestroyObject(UnityEngine.Object obj)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(obj);
            else UnityEngine.Object.DestroyImmediate(obj);
        }
    }
}
