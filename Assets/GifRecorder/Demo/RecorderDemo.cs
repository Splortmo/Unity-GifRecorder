using UnityEngine;

namespace GifRecorder.Demo
{
    /// <summary>
    /// Spawns some animated geometry and draws a small IMGUI panel for driving a <see cref="ScreenRecorder"/>.
    /// </summary>
    public sealed class RecorderDemo : MonoBehaviour
    {
        [SerializeField] ScreenRecorder recorder;
        [SerializeField] int cubeCount = 12;

        [Tooltip("Optional: meshes and material referenced from the scene, so stripped player builds (e.g. WebGL) include " +
                 "them. Falls back to primitives when empty.")]
        [SerializeField] Mesh cubeMesh;
        [SerializeField] Mesh sphereMesh;
        [SerializeField] Material shapeMaterial;

        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int Color = Shader.PropertyToID("_Color");

        Transform[] cubes;
        string lastSaved = "";

        // Frame-time stats over the last second, to show that recording doesn't hitch the main thread.
        float windowStart, windowMax, windowSum;
        int windowFrames;
        public float AverageFrameMs { get; private set; }
        public float MaxFrameMs { get; private set; }

        /// <summary>Per-second (average, max) frame times for the last 30 seconds, plus whether a recording was active.</summary>
        public readonly System.Collections.Generic.List<(float avg, float max, bool recording)> History =
            new System.Collections.Generic.List<(float, float, bool)>();

        void Start()
        {
            if (recorder == null) recorder = FindAnyObjectByType<ScreenRecorder>();
            if (recorder != null)
            {
                recorder.RecordingSaved += path => lastSaved = path;
                recorder.ScreenshotSaved += path => lastSaved = path;
            }

            cubes = new Transform[cubeCount];
            var block = new MaterialPropertyBlock();
            for (int i = 0; i < cubeCount; i++)
            {
                bool sphere = i % 3 == 0;
                GameObject cube;
                if (shapeMaterial != null && cubeMesh != null && sphereMesh != null)
                {
                    cube = new GameObject("", typeof(MeshFilter), typeof(MeshRenderer));
                    cube.GetComponent<MeshFilter>().sharedMesh = sphere ? sphereMesh : cubeMesh;
                    cube.GetComponent<MeshRenderer>().sharedMaterial = shapeMaterial;
                }
                else
                {
                    cube = GameObject.CreatePrimitive(sphere ? PrimitiveType.Sphere : PrimitiveType.Cube);
                }
                cube.name = "Demo Shape " + i;
                cube.transform.SetParent(transform, false);
                var color = UnityEngine.Color.HSVToRGB(i / (float)cubeCount, 0.75f, 1f);
                block.SetColor(BaseColor, color);
                block.SetColor(Color, color);
                cube.GetComponent<Renderer>().SetPropertyBlock(block);
                cubes[i] = cube.transform;
            }

            var cam = Camera.main;
            if (cam != null)
            {
                originalClearFlags = cam.clearFlags;
                originalBackground = cam.backgroundColor;
            }
            ApplyUrlParameters(Application.absoluteURL);
        }

        CameraClearFlags originalClearFlags;
        Color originalBackground;

        /// <summary>
        /// Records the main camera with a fully transparent Solid Color background (on), or the screen (off).
        /// </summary>
        public void SetTransparent(bool on)
        {
            if (recorder == null || recorder.IsRecording) return;
            var settings = recorder.Settings;
            settings.captureSource = on ? CaptureSource.Camera : CaptureSource.Screen;
            settings.preserveTransparency = on;

            var cam = Camera.main;
            if (cam == null) return;
            if (on)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                var c = originalClearFlags == CameraClearFlags.SolidColor ? originalBackground : new Color(0.2f, 0.4f, 0.6f);
                cam.backgroundColor = new Color(c.r, c.g, c.b, 0f);
            }
            else
            {
                cam.clearFlags = originalClearFlags;
                cam.backgroundColor = originalBackground;
            }
        }

        /// <summary>
        /// Lets a web page drive the demo, e.g. <c>index.html?format=apng&amp;transparent=1&amp;record=3&amp;download=0</c>.
        /// Supported keys: format (gif|apng), transparent (0|1), fps, mode (auto|threaded|sliced), download (0|1),
        /// record (seconds; starts recording immediately).
        /// </summary>
        void ApplyUrlParameters(string url)
        {
            if (recorder == null || string.IsNullOrEmpty(url)) return;
            int q = url.IndexOf('?');
            if (q < 0) return;

            var settings = recorder.Settings;
            float record = 0f;
            foreach (var pair in url.Substring(q + 1).Split('&'))
            {
                var kv = pair.Split('=');
                if (kv.Length != 2) continue;
                string key = kv[0].ToLowerInvariant(), value = UnityEngine.Networking.UnityWebRequest.UnEscapeURL(kv[1]).ToLowerInvariant();
                switch (key)
                {
                    case "format": settings.format = value == "apng" || value == "png" ? RecordingFormat.Apng : RecordingFormat.Gif; break;
                    case "transparent": SetTransparent(value == "1" || value == "true"); break;
                    case "fps": if (int.TryParse(value, out int fps)) settings.frameRate = Mathf.Clamp(fps, 1, 60); break;
                    case "download": settings.webGLDownload = value == "1" || value == "true"; break;
                    case "mode":
                        settings.encodingMode = value == "sliced" ? EncodingMode.TimeSliced : value == "threaded" ? EncodingMode.Threaded : EncodingMode.Auto;
                        break;
                    case "record":
                        float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out record);
                        break;
                }
            }

            if (record > 0f)
            {
                settings.maxDuration = record;
                recorder.StartRecording();
            }
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime * 1000f;
            windowMax = Mathf.Max(windowMax, dt);
            windowSum += dt;
            windowFrames++;
            if (Time.unscaledTime - windowStart >= 1f)
            {
                AverageFrameMs = windowSum / windowFrames;
                MaxFrameMs = windowMax;
                History.Add((AverageFrameMs, MaxFrameMs, recorder != null && recorder.IsRecording));
                if (History.Count > 30) History.RemoveAt(0);
                windowStart = Time.unscaledTime;
                windowMax = windowSum = 0f;
                windowFrames = 0;
            }

            float t = Time.time;
            for (int i = 0; i < cubes.Length; i++)
            {
                float angle = t * 0.6f + i * Mathf.PI * 2f / cubes.Length;
                float radius = 2.5f + Mathf.Sin(t * 1.3f + i) * 0.5f;
                cubes[i].localPosition = new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(t * 2f + i) * 0.75f, Mathf.Sin(angle) * radius);
                cubes[i].localRotation = Quaternion.Euler(t * 90f + i * 20f, t * 60f, 0f);
            }
        }

        void OnGUI()
        {
            if (recorder == null) return;

            GUILayout.BeginArea(new Rect(10, 10, 360, 310), GUI.skin.box);
            var settings = recorder.Settings;
            using (new GUILayout.HorizontalScope())
            {
                GUI.enabled = !recorder.IsRecording;
                GUILayout.Label("Format", GUILayout.Width(60));
                if (GUILayout.Toggle(settings.format == RecordingFormat.Gif, "GIF", "Button")) settings.format = RecordingFormat.Gif;
                if (GUILayout.Toggle(settings.format == RecordingFormat.Apng, "APNG", "Button")) settings.format = RecordingFormat.Apng;
                GUI.enabled = true;
            }
            using (new GUILayout.HorizontalScope())
            {
                GUI.enabled = !recorder.IsRecording;
                GUILayout.Label($"FPS {settings.frameRate}", GUILayout.Width(60));
                settings.frameRate = Mathf.RoundToInt(GUILayout.HorizontalSlider(settings.frameRate, 5, 60));
                GUI.enabled = true;
            }

            GUI.enabled = !recorder.IsRecording;
            bool transparent = settings.preserveTransparency && settings.captureSource == CaptureSource.Camera;
            bool toggled = GUILayout.Toggle(transparent, " Transparent background (records the camera, not the UI)");
            if (toggled != transparent) SetTransparent(toggled);
            bool crop = GUILayout.Toggle(settings.fixedOutputSize, " Fixed output size 480×480 (crops the centre)");
            if (crop != settings.fixedOutputSize)
            {
                settings.fixedOutputSize = crop;
                settings.outputSize = new Vector2Int(480, 480);
            }
            GUI.enabled = true;
            bool guide = GUILayout.Toggle(recorder.ShowGuide, " Show guide lines");
            if (guide != recorder.ShowGuide) recorder.ShowGuide = guide;

            using (new GUILayout.HorizontalScope())
            {
                if (GUILayout.Button(recorder.IsRecording ? "Stop Recording (F9)" : "Start Recording (F9)", GUILayout.Height(30)))
                    recorder.ToggleRecording();
                if (GUILayout.Button("Screenshot (F8)", GUILayout.Height(30), GUILayout.Width(120)))
                    recorder.TakeScreenshot();
            }
            settings.screenshotIncludeUI = GUILayout.Toggle(settings.screenshotIncludeUI, " Screenshot includes UI");

            if (recorder.IsRecording)
                GUILayout.Label($"● REC {recorder.RecordingTime:0.0}s  {recorder.CapturedFrameCount} frames  backlog {recorder.PendingFrameCount}");
            else if (recorder.IsEncoding)
                GUILayout.Label($"Finishing… {recorder.PendingFrameCount} frames left");
            GUILayout.Label($"Frame time: avg {AverageFrameMs:0.0} ms, max {MaxFrameMs:0.0} ms");
            if (!string.IsNullOrEmpty(lastSaved))
                GUILayout.Label("Saved: " + lastSaved, GUI.skin.label);
            GUILayout.EndArea();
        }
    }
}
