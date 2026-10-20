using System.IO;
using UnityEditor;
using UnityEngine;

namespace GifRecorder.Editor
{
    [CustomEditor(typeof(ScreenRecorder))]
    sealed class ScreenRecorderEditor : UnityEditor.Editor
    {
        static readonly string[] GifFields = { "gifColors", "gifDithering", "gifPaletteSampling" };
        static readonly string[] ApngFields = { "apngCompression" };

        public override bool RequiresConstantRepaint() => Application.isPlaying;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var recorder = (ScreenRecorder)target;
            var settings = serializedObject.FindProperty("settings");
            var format = (RecordingFormat)settings.FindPropertyRelative("format").enumValueIndex;

            EditorGUILayout.LabelField("Capture", EditorStyles.boldLabel);
            Field(settings, "format");
            Field(settings, "frameRate");
            Field(settings, "fixedOutputSize");
            if (settings.FindPropertyRelative("fixedOutputSize").boolValue)
            {
                EditorGUI.indentLevel++;
                Field(settings, "outputSize");
                EditorGUI.indentLevel--;
                EditorGUILayout.HelpBox("The centre of the screen is cropped to this aspect ratio.", MessageType.None);
            }
            else
            {
                Field(settings, "resolutionScale");
                Field(settings, "maxWidth");
                Field(settings, "maxHeight");
            }
            Field(settings, "maxDuration");
            Field(settings, "loopCount");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Source & Transparency", EditorStyles.boldLabel);
            Field(settings, "captureSource");
            var source = (CaptureSource)settings.FindPropertyRelative("captureSource").enumValueIndex;
            if (source == CaptureSource.Camera)
            {
                Field(settings, "captureCamera");
                EditorGUILayout.HelpBox("Camera capture renders the camera an extra time per captured frame and does not include Screen Space - Overlay UI.", MessageType.None);
            }
            Field(settings, "preserveTransparency");
            bool transparency = settings.FindPropertyRelative("preserveTransparency").boolValue;
            if (transparency && source != CaptureSource.Camera)
                EditorGUILayout.HelpBox("The screen back buffer is always opaque. Set Capture Source to Camera to keep transparency " +
                                        "(e.g. a Solid Color background with alpha < 1).", MessageType.Warning);
            if (transparency && format == RecordingFormat.Gif)
                Field(settings, "gifAlphaThreshold");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(format == RecordingFormat.Gif ? "GIF Quality" : "APNG Quality", EditorStyles.boldLabel);
            foreach (var name in format == RecordingFormat.Gif ? GifFields : ApngFields) Field(settings, name);
            if (format == RecordingFormat.Gif && settings.FindPropertyRelative("frameRate").intValue > 50)
                EditorGUILayout.HelpBox("GIF viewers cap playback at 50 fps; captures will be limited to that rate.", MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
            Field(settings, "outputFolder");
            Field(settings, "fileNamePrefix");
            Field(settings, "webGLDownload");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Performance", EditorStyles.boldLabel);
            Field(settings, "encodingMode");
            var mode = (EncodingMode)settings.FindPropertyRelative("encodingMode").enumValueIndex;
            bool webGL = EditorUserBuildSettings.activeBuildTarget == BuildTarget.WebGL;
            if (mode == EncodingMode.TimeSliced || webGL)
                Field(settings, "timeSliceBudgetMs");
            if (webGL && mode == EncodingMode.Threaded)
                EditorGUILayout.HelpBox("WebGL has no threads; players will use time-sliced encoding.", MessageType.Info);
            Field(settings, "maxQueuedFrames");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Recording UI", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("showGuide"), new GUIContent("Show Guide Lines"));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Screenshot", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(settings.FindPropertyRelative("screenshotIncludeUI"), new GUIContent("Include UI"));
            if (!settings.FindPropertyRelative("screenshotIncludeUI").boolValue)
                EditorGUILayout.HelpBox("Renders the Capture Camera (or Camera.main) on its own: no overlay UI, and transparency " +
                                        "is kept when Preserve Transparency is on.", MessageType.None);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("onScreenshotSaved"));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Control", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("recordOnStart"));
            var hotkey = serializedObject.FindProperty("enableHotkey");
            EditorGUILayout.PropertyField(hotkey);
            var key = serializedObject.FindProperty("toggleKey");
            if (key != null && hotkey.boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(key, new GUIContent("Record Key"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("screenshotKey"));
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.PropertyField(serializedObject.FindProperty("onRecordingSaved"));

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();
            DrawRuntimeControls(recorder);
        }

        static void Field(SerializedProperty parent, string name) =>
            EditorGUILayout.PropertyField(parent.FindPropertyRelative(name));

        static void DrawRuntimeControls(ScreenRecorder recorder)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!Application.isPlaying))
                {
                    var label = recorder.IsRecording ? "Stop Recording" : "Start Recording";
                    if (GUILayout.Button(label, GUILayout.Height(28))) recorder.ToggleRecording();
                    if (GUILayout.Button("Screenshot", GUILayout.Height(28), GUILayout.Width(90))) recorder.TakeScreenshot();
                }
                if (GUILayout.Button("Open Folder", GUILayout.Height(28), GUILayout.Width(100)))
                {
                    string folder = recorder.Settings.ResolveOutputFolder();
                    Directory.CreateDirectory(folder);
                    EditorUtility.RevealInFinder(folder + Path.DirectorySeparatorChar);
                }
            }

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter Play Mode to record. Files go to: " + recorder.Settings.ResolveOutputFolder(), MessageType.None);
                return;
            }

            if (recorder.IsRecording)
            {
                var size = recorder.CurrentOutputSize;
                EditorGUILayout.HelpBox(
                    $"Recording {recorder.RecordingTime:0.0}s · {recorder.CapturedFrameCount} frames · {size.x}x{size.y}\n" +
                    $"Encoding backlog: {recorder.PendingFrameCount} frames", MessageType.Info);
            }
            else if (recorder.IsEncoding)
            {
                EditorGUILayout.HelpBox($"Finishing file… {recorder.PendingFrameCount} frames left", MessageType.Info);
            }
        }

        [MenuItem("GameObject/Screen Recorder", false, 10)]
        static void CreateRecorder(MenuCommand command)
        {
            var go = new GameObject("Screen Recorder", typeof(ScreenRecorder));
            GameObjectUtility.SetParentAndAlign(go, command.context as GameObject);
            Undo.RegisterCreatedObjectUndo(go, "Create Screen Recorder");
            Selection.activeObject = go;
        }
    }
}
