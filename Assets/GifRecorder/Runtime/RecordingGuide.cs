using UnityEngine;
using UnityEngine.UI;

namespace GifRecorder
{
    /// <summary>
    /// Runtime overlay that frames the part of the screen a <see cref="ScreenRecorder"/> records: the area outside
    /// is tinted, a border is drawn around it, and a label shows the output resolution.
    /// </summary>
    /// <remarks>
    /// Everything is drawn outside the recorded region, so the guide never appears in Screen recordings. When the
    /// region covers the whole screen there is nowhere outside to draw, so only the label is shown, and only while idle.
    /// The layout is rebuilt only when the region, output size or screen size changes.
    /// Normally managed by <see cref="ScreenRecorder.ShowGuide"/>; add it manually only to customise its appearance.
    /// </remarks>
    [AddComponentMenu("Recording/Recording Guide")]
    [DisallowMultipleComponent]
    public sealed class RecordingGuide : MonoBehaviour
    {
        [Tooltip("Recorder to visualise. Empty = the ScreenRecorder on this GameObject, or the first one in the scene.")]
        [SerializeField] ScreenRecorder recorder;

        [SerializeField] bool showWhenIdle = true;
        [SerializeField] bool showWhileRecording = true;
        [SerializeField] bool showLabel = true;

        [SerializeField] Color borderColor = new Color(1f, 0.8f, 0.1f, 0.9f);
        [SerializeField] Color recordingBorderColor = new Color(1f, 0.25f, 0.2f, 0.95f);
        [SerializeField] Color outsideTint = new Color(0f, 0f, 0f, 0.4f);
        [SerializeField, Min(1f)] float borderWidth = 2f;
        [SerializeField, Min(6)] int labelSize = 14;
        [SerializeField] int sortingOrder = 32000;

        const float LabelHeight = 22f;

        GameObject root;
        readonly RectTransform[] shades = new RectTransform[4];
        readonly RectTransform[] borders = new RectTransform[4];
        Image[] borderImages;
        RectTransform labelRect;
        Text label;

        bool visible, hasLayout, layoutRecording;
        RectInt layoutRegion;
        Vector2Int layoutOutput, layoutScreen;

        public ScreenRecorder Recorder
        {
            get => recorder;
            set
            {
                recorder = value;
                hasLayout = false;
            }
        }

        void Awake()
        {
            if (recorder == null) recorder = GetComponent<ScreenRecorder>();
            if (recorder == null) recorder = FindAnyObjectByType<ScreenRecorder>();
            Build();
        }

        void OnEnable()
        {
            hasLayout = false;
            visible = false;
            if (root != null) root.SetActive(false);
        }

        void OnDisable()
        {
            if (root != null) root.SetActive(false);
        }

        void OnDestroy()
        {
            if (root != null) Destroy(root);
        }

        void LateUpdate()
        {
            bool recording = recorder != null && recorder.IsRecording;
            bool show = recorder != null && !recorder.IsGuideSuppressed && (recording ? showWhileRecording : showWhenIdle);
            RectInt region = default;
            Vector2Int output = default;
            if (show) show = recorder.TryGetCaptureRegion(out region, out output);

            if (show != visible)
            {
                visible = show;
                root.SetActive(show);
                hasLayout = false;
            }
            if (!show) return;

            var screen = new Vector2Int(Screen.width, Screen.height);
            if (hasLayout && region.Equals(layoutRegion) && output == layoutOutput && screen == layoutScreen && recording == layoutRecording)
                return;

            Layout(region, output, screen, recording);
            hasLayout = true;
            layoutRegion = region;
            layoutOutput = output;
            layoutScreen = screen;
            layoutRecording = recording;
        }

        void Layout(RectInt r, Vector2Int output, Vector2Int screen, bool recording)
        {
            float t = borderWidth;
            int sw = screen.x, sh = screen.y;

            // Tint: bottom, top, left, right of the region.
            Place(shades[0], 0, 0, sw, r.yMin);
            Place(shades[1], 0, r.yMax, sw, sh - r.yMax);
            Place(shades[2], 0, r.yMin, r.xMin, r.height);
            Place(shades[3], r.xMax, r.yMin, sw - r.xMax, r.height);

            // Border just outside the region (clipped by the screen edge when the region touches it).
            Place(borders[0], r.xMin - t, r.yMin - t, r.width + 2 * t, t);
            Place(borders[1], r.xMin - t, r.yMax, r.width + 2 * t, t);
            Place(borders[2], r.xMin - t, r.yMin, t, r.height);
            Place(borders[3], r.xMax, r.yMin, t, r.height);
            var color = recording ? recordingBorderColor : borderColor;
            foreach (var image in borderImages) image.color = color;

            // Label outside the region: above, below, right or left of it. With no room outside (full-screen region)
            // it goes inside, and is hidden while recording so it can't end up in the video.
            label.text = recording ? $"● REC  {output.x}×{output.y}" : $"{output.x}×{output.y}";
            label.color = color;
            float width = label.preferredWidth + 8f;
            float gap = t + 4f;
            bool showText = showLabel;
            float lx, ly;
            if (sh - r.yMax - t >= LabelHeight) { lx = Mathf.Max(4f, r.xMin); ly = r.yMax + t; }
            else if (r.yMin - t >= LabelHeight) { lx = Mathf.Max(4f, r.xMin); ly = r.yMin - t - LabelHeight; }
            else if (sw - r.xMax - gap >= width) { lx = r.xMax + gap; ly = r.yMax - LabelHeight - 4f; }
            else if (r.xMin - gap >= width) { lx = r.xMin - gap - width; ly = r.yMax - LabelHeight - 4f; }
            else
            {
                lx = r.xMin + 8f;
                ly = r.yMax - LabelHeight - 4f;
                showText &= !recording;
            }
            labelRect.gameObject.SetActive(showText);
            if (showText) Place(labelRect, lx, ly, width, LabelHeight);
        }

        static void Place(RectTransform rect, float x, float y, float width, float height)
        {
            bool empty = width <= 0f || height <= 0f;
            if (rect.gameObject.activeSelf == empty) rect.gameObject.SetActive(!empty);
            if (empty) return;
            rect.anchoredPosition = new Vector2(x, y);
            rect.sizeDelta = new Vector2(width, height);
        }

        void Build()
        {
            root = new GameObject("Recording Guide", typeof(RectTransform), typeof(Canvas));
            root.hideFlags = HideFlags.DontSave;
            var canvas = root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay; // 1 canvas unit = 1 screen pixel
            canvas.sortingOrder = sortingOrder;
            DontDestroyOnLoad(root);

            for (int i = 0; i < 4; i++) shades[i] = CreateImage("Shade", outsideTint);
            borderImages = new Image[4];
            for (int i = 0; i < 4; i++)
            {
                borders[i] = CreateImage("Border", borderColor);
                borderImages[i] = borders[i].GetComponent<Image>();
            }

            var labelObject = new GameObject("Label", typeof(RectTransform), typeof(Text), typeof(Shadow));
            labelRect = Anchor(labelObject);
            label = labelObject.GetComponent<Text>();
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = labelSize;
            label.fontStyle = FontStyle.Bold;
            label.alignment = TextAnchor.MiddleLeft;
            label.raycastTarget = false;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;

            root.SetActive(false);
        }

        RectTransform CreateImage(string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            var image = go.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return Anchor(go);
        }

        RectTransform Anchor(GameObject go)
        {
            var rect = (RectTransform)go.transform;
            rect.SetParent(root.transform, false);
            rect.anchorMin = rect.anchorMax = Vector2.zero;
            rect.pivot = Vector2.zero;
            return rect;
        }
    }
}
