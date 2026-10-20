using System;
using System.IO;
using GifRecorder.Encoding;
using UnityEngine;

namespace GifRecorder
{
    public enum RecordingFormat
    {
        Gif,
        /// <summary>Animated PNG: lossless colour and alpha, larger files.</summary>
        Apng,
    }

    public enum CaptureSource
    {
        /// <summary>The final screen image, including overlay UI. The back buffer has no usable alpha.</summary>
        Screen,
        /// <summary>
        /// Renders one camera into an off-screen texture. Keeps the camera's alpha (e.g. a Solid Color background
        /// with transparency), but excludes Screen Space - Overlay UI and costs an extra camera render per capture.
        /// </summary>
        Camera,
    }

    public enum EncodingMode
    {
        /// <summary>Threaded where available, time-sliced on WebGL.</summary>
        Auto,
        /// <summary>Encode on worker threads.</summary>
        Threaded,
        /// <summary>Encode on the main thread within a per-frame time budget (what WebGL uses).</summary>
        TimeSliced,
    }

    [Serializable]
    public sealed class RecorderSettings
    {
        [Tooltip("Output file format.")]
        public RecordingFormat format = RecordingFormat.Gif;

        [Tooltip("Frames captured per second. GIF timing has 10 ms resolution and viewers cap it at 50 fps.")]
        [Range(1, 60)] public int frameRate = 20;

        [Tooltip("Record at an exact width x height. The centre of the screen (or camera) is cropped to that aspect " +
                 "ratio; Resolution Scale and Max Width/Height are ignored.")]
        public bool fixedOutputSize = false;

        [Tooltip("Output size in pixels when Fixed Output Size is on.")]
        public Vector2Int outputSize = new Vector2Int(640, 480);

        [Tooltip("Output resolution relative to the screen (or camera).")]
        [Range(0.05f, 1f)] public float resolutionScale = 0.5f;

        [Tooltip("Upper bound for the output width in pixels (aspect ratio is kept). 0 = no limit.")]
        [Min(0)] public int maxWidth = 960;

        [Tooltip("Upper bound for the output height in pixels (aspect ratio is kept). 0 = no limit.")]
        [Min(0)] public int maxHeight = 0;

        [Tooltip("Stop automatically after this many seconds. 0 = record until stopped.")]
        [Min(0)] public float maxDuration = 0f;

        [Tooltip("How many times the animation plays. 0 = loop forever.")]
        [Min(0)] public int loopCount = 0;

        [Tooltip("Screen: what the player sees, including overlay UI (always opaque). " +
                 "Camera: renders one camera off-screen and keeps its alpha channel.")]
        public CaptureSource captureSource = CaptureSource.Screen;

        [Tooltip("Camera to record when Capture Source is Camera. Empty = Camera.main.")]
        public Camera captureCamera;

        [Tooltip("Keep transparency from the captured alpha channel (requires Capture Source = Camera). " +
                 "APNG keeps full alpha; GIF keeps 1-bit transparency.")]
        public bool preserveTransparency = false;

        [Tooltip("GIF only: pixels with alpha below this value become transparent.")]
        [Range(1, 255)] public int gifAlphaThreshold = 128;

        [Tooltip("Colours per frame (2-256). Fewer colours give smaller files.")]
        [Range(2, 256)] public int gifColors = 256;

        [Tooltip("Floyd-Steinberg dithering: smoother gradients, but somewhat larger files.")]
        public bool gifDithering = true;

        [Tooltip("Build each palette from every n-th pixel. 1 = best quality, higher = faster encoding.")]
        [Range(1, 10)] public int gifPaletteSampling = 1;

        [Tooltip("Deflate effort for APNG. Optimal gives the smallest files but encodes slowest.")]
        public PngCompression apngCompression = PngCompression.Optimal;

        [Tooltip("Auto: worker threads where available, time-sliced main-thread encoding on WebGL.")]
        public EncodingMode encodingMode = EncodingMode.Auto;

        [Tooltip("Time-sliced mode: milliseconds of encoding work per frame.")]
        [Range(0.5f, 33f)] public float timeSliceBudgetMs = 4f;

        [Tooltip("WebGL: offer the finished file as a browser download.")]
        public bool webGLDownload = true;

        [Tooltip("Screenshots: include Screen Space - Overlay UI by capturing the final screen image. When off, the " +
                 "capture camera (or Camera.main) is rendered on its own, which also keeps transparency if " +
                 "Preserve Transparency is on.")]
        public bool screenshotIncludeUI = true;

        [Tooltip("Folder for recordings. Relative paths resolve against the project folder in the Editor and " +
                 "Application.persistentDataPath in players. Empty = \"Recordings\".")]
        public string outputFolder = "";

        [Tooltip("File name prefix; a timestamp is appended.")]
        public string fileNamePrefix = "Recording";

        [Tooltip("Skip captures while this many frames are waiting to be encoded, to bound memory use.")]
        [Min(1)] public int maxQueuedFrames = 240;

        public RecorderSettings Clone() => (RecorderSettings)MemberwiseClone();

        /// <summary>Whether this platform and setting combination encodes on worker threads.</summary>
        public bool UsesThreads
        {
            get
            {
                switch (encodingMode)
                {
                    case EncodingMode.Threaded: return PlatformSupportsThreads;
                    case EncodingMode.TimeSliced: return false;
                    default: return PlatformSupportsThreads;
                }
            }
        }

        public static bool PlatformSupportsThreads =>
#if UNITY_WEBGL && !UNITY_EDITOR
            false;
#else
            true;
#endif

        /// <summary>Transparency is only meaningful when capturing a camera; the back buffer is always opaque.</summary>
        public bool KeepsAlpha => preserveTransparency && captureSource == CaptureSource.Camera;

        public IAnimationEncoder CreateEncoder()
        {
            switch (format)
            {
                case RecordingFormat.Apng:
                    return new ApngEncoder
                    {
                        Compression = apngCompression,
                        LoopCount = loopCount,
                        Transparency = KeepsAlpha,
                    };
                default:
                    return new GifEncoder
                    {
                        MaxColors = gifColors,
                        Dithering = gifDithering,
                        PaletteSampleStep = gifPaletteSampling,
                        LoopCount = loopCount,
                        Transparency = KeepsAlpha,
                        AlphaThreshold = gifAlphaThreshold,
                    };
            }
        }

        /// <summary>
        /// What gets recorded from a source of the given size: the source rectangle (in source pixels, origin
        /// bottom-left) and the output size it is scaled to.
        /// </summary>
        public CaptureLayout GetCaptureLayout(int sourceWidth, int sourceHeight)
        {
            sourceWidth = Math.Max(1, sourceWidth);
            sourceHeight = Math.Max(1, sourceHeight);
            if (!fixedOutputSize)
                return new CaptureLayout(new RectInt(0, 0, sourceWidth, sourceHeight), GetOutputSize(sourceWidth, sourceHeight));

            var size = ClampOutputSize(outputSize);
            return new CaptureLayout(CaptureLayout.CenteredCrop(sourceWidth, sourceHeight, size), size);
        }

        static Vector2Int ClampOutputSize(Vector2Int size) =>
            new Vector2Int(Mathf.Clamp(size.x, 1, 8192), Mathf.Clamp(size.y, 1, 8192));

        /// <summary>Output size for a given source size, honouring scale and the width/height limits (non-fixed mode).</summary>
        public Vector2Int GetOutputSize(int sourceWidth, int sourceHeight)
        {
            double scale = Mathf.Clamp(resolutionScale, 0.01f, 1f);
            if (maxWidth > 0) scale = Math.Min(scale, (double)maxWidth / sourceWidth);
            if (maxHeight > 0) scale = Math.Min(scale, (double)maxHeight / sourceHeight);
            return new Vector2Int(
                Math.Max(1, (int)Math.Round(sourceWidth * scale)),
                Math.Max(1, (int)Math.Round(sourceHeight * scale)));
        }

        public string ResolveOutputFolder()
        {
            string folder = string.IsNullOrWhiteSpace(outputFolder) ? "Recordings" : outputFolder;
            if (Path.IsPathRooted(folder)) return folder;
#if UNITY_EDITOR
            string root = Path.GetDirectoryName(Application.dataPath);
#else
            string root = Application.persistentDataPath;
#endif
            return Path.GetFullPath(Path.Combine(root, folder));
        }

        internal void Validate()
        {
            frameRate = Mathf.Clamp(frameRate, 1, 60);
            resolutionScale = Mathf.Clamp(resolutionScale, 0.05f, 1f);
            maxWidth = Math.Max(0, maxWidth);
            maxHeight = Math.Max(0, maxHeight);
            maxDuration = Math.Max(0f, maxDuration);
            loopCount = Math.Max(0, loopCount);
            gifAlphaThreshold = Mathf.Clamp(gifAlphaThreshold, 1, 255);
            gifColors = Mathf.Clamp(gifColors, 2, 256);
            gifPaletteSampling = Mathf.Clamp(gifPaletteSampling, 1, 10);
            timeSliceBudgetMs = Mathf.Clamp(timeSliceBudgetMs, 0.5f, 33f);
            maxQueuedFrames = Math.Max(1, maxQueuedFrames);
            outputSize = ClampOutputSize(outputSize);
        }
    }

    /// <summary>A source rectangle and the output size it is scaled to.</summary>
    public readonly struct CaptureLayout : IEquatable<CaptureLayout>
    {
        /// <summary>Recorded part of the source, in source pixels with the origin at the bottom-left.</summary>
        public readonly RectInt Source;
        public readonly Vector2Int Output;

        public CaptureLayout(RectInt source, Vector2Int output)
        {
            Source = source;
            Output = output;
        }

        /// <summary>The largest centred rectangle of the source with the aspect ratio of <paramref name="target"/>.</summary>
        public static RectInt CenteredCrop(int sourceWidth, int sourceHeight, Vector2Int target)
        {
            double aspect = (double)target.x / target.y;
            int w = sourceWidth, h = sourceHeight;
            if ((double)sourceWidth / sourceHeight > aspect) w = Math.Max(1, Math.Min(sourceWidth, (int)Math.Round(sourceHeight * aspect)));
            else h = Math.Max(1, Math.Min(sourceHeight, (int)Math.Round(sourceWidth / aspect)));
            return new RectInt((sourceWidth - w) / 2, (sourceHeight - h) / 2, w, h);
        }

        public bool Equals(CaptureLayout other) => Source.Equals(other.Source) && Output == other.Output;
        public override bool Equals(object obj) => obj is CaptureLayout other && Equals(other);
        public override int GetHashCode() => Source.GetHashCode() * 31 + Output.GetHashCode();
        public override string ToString() => $"{Source} -> {Output.x}x{Output.y}";
    }
}
