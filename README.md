# GifRecorder

Runtime screen recorder for Unity that saves animated **GIF** or **APNG** files, with optional **transparency**.
All encoding is C# (palette quantization, LZW and PNG filtering are part of the addon; deflate uses the built-in
`System.IO.Compression`), with no plugins. It never blocks the
main thread:

* **Desktop, mobile and consoles:** frames are encoded in parallel on worker threads.
* **WebGL:** there are no threads, so encoding is split into small resumable steps that run on the main thread within a
  per-frame time budget (4 ms by default). Finished files are offered as a browser download.

Files are streamed to disk as they're encoded, so memory use doesn't grow with recording length.

**Compatibility:** Unity 2022.3 LTS and newer (including Unity 6), URP or the built-in render pipeline, including
WebGL. Developed on Unity 6000.3; compiles without errors or warnings on 2022.3 and 2023.2.

**Live demo:** [splortmo.github.io/Unity-GifRecorder](https://splortmo.github.io/Unity-GifRecorder/) (WebGL build of the demo
scene; recordings are downloaded by the browser).

## Installation

Download `GifRecorder-<version>.unitypackage` from the [Releases](https://github.com/Splortmo/Unity-GifRecorder/releases)
page and import it (**Assets ▸ Import Package ▸ Custom Package…**), or copy `Assets/GifRecorder` from this repository
into your project.

## Quick start

1. Add a recorder: **GameObject ▸ Screen Recorder**, or add the `Screen Recorder` component to any object.
2. Enter Play Mode and press **F9** (or use **Start Recording** in the inspector).
3. Press F9 again to stop. Files are saved to:
   * the Editor: `<project>/Recordings/`
   * players: `Application.persistentDataPath/Recordings/`
   * WebGL: the same path (IndexedDB), plus a browser download.

`Assets/GifRecorder/Demo/RecorderDemo.unity` contains animated shapes and an on-screen control panel (format, fps, transparency, record).

## Fixed output size and guide lines

Enable **Fixed Output Size** to record at an exact width × height, for example 480×480 for a square clip. The centre of
the screen (or camera) is cropped to that aspect ratio and scaled to the output size; **Resolution Scale** and
**Max Width/Height** are ignored in this mode.

**Show Guide Lines** (in the recorder's *Recording UI* section, on by default) is independent of the output size. It
shows the recorded area at runtime: everything outside the region is tinted, a border is drawn around it and the
output resolution is labelled. The border turns red while recording. The guide is drawn *outside* the recorded region,
so it never shows up in Screen recordings. If the region covers the whole screen there is no room outside, so only the
label is shown, and only while idle. The layout is rebuilt only when the recording resolution, the region or the screen
resolution changes. Toggle it from code with `recorder.ShowGuide`. To change colours or line width, add the **Recording
Guide** component yourself and edit it; otherwise the recorder adds one automatically.

`ScreenRecorder.TryGetCaptureRegion(out RectInt screenRegion, out Vector2Int outputSize)` exposes the same
information to your own UI.

## Screenshots

Press **F8** (or call `ScreenRecorder.TakeScreenshot()`) to save a full-resolution PNG next to the recordings. **Include UI**
(in the recorder's *Screenshot* section) chooses what is captured:

* **On:** the final screen image, including Screen Space - Overlay UI. Always opaque. The guide lines are hidden for
  that frame.
* **Off:** the Capture Camera (or `Camera.main`) rendered on its own. There's no overlay UI, and transparency is kept
  when **Preserve Transparency** is on.

`ScreenshotCapture.CapturePngAsync(host, new ScreenshotOptions { … })` returns the PNG bytes instead of writing a file.
Its options are:

* a custom width/height (0 keeps the aspect ratio)
* a specific camera
* a culling mask
* a transparent background
* MSAA

Encoding uses Unity's built-in `ImageConversion.EncodeArrayToPNG`. It runs on a worker thread, or in place on WebGL.

## Transparency

The screen back buffer is always opaque, so transparency needs the camera's own output:

1. Set **Capture Source = Camera** (optionally assign **Capture Camera**; the default is `Camera.main`).
2. Enable **Preserve Transparency**.
3. Give the camera a **Solid Color** background whose alpha is below 1. In URP, if post-processing is enabled on the
   camera, also enable *Alpha Processing* on the URP asset so post effects keep alpha.

What each format keeps:

* **APNG:** full 8-bit alpha (RGBA), including soft edges.
* **GIF:** 1-bit transparency. Pixels with alpha below **GIF Alpha Threshold** are transparent. When an opaque pixel
  turns transparent, the encoder clears the canvas ("restore to background" disposal); otherwise it keeps writing
  small delta frames.

Camera capture renders the camera one extra time per captured frame, at up to 2× the output size. It doesn't include
Screen Space - Overlay UI.

## Scripting

`ScreenRecorder` is a singleton, and recording and screenshots are static calls on it:

```csharp
using GifRecorder;

// Optional: create the recorder up front. persistent = DontDestroyOnLoad until you destroy it,
// showBoundary = show the on-screen guide framing the recorded area (never recorded itself).
var recorder = ScreenRecorder.Init(persistent: true, showBoundary: true);
recorder.Settings.format = RecordingFormat.Apng;
recorder.Settings.captureSource = CaptureSource.Camera;
recorder.Settings.preserveTransparency = true;

ScreenRecorder.StartRecording();
// later…
string path = await ScreenRecorder.StopRecording();   // completes once the file is written

string png = await ScreenRecorder.TakeScreenshot();    // or TakeScreenshot(includeUI: false)
```

* `ScreenRecorder.Instance` is the current recorder, or null. A recorder placed in a scene registers itself (its
  **Persistent** checkbox controls DontDestroyOnLoad). If one already exists, any later one removes itself.
* `Init` on an existing recorder reconfigures it instead of creating a second one.
* **Without `Init`:** if no recorder exists, `StartRecording` and `TakeScreenshot` create a temporary one. It has no
  guide, isn't kept across scene loads, and destroys itself once its recording or screenshot has been written.
* `StartRecording` and `TakeScreenshot` accept an optional `RecorderSettings` to use instead of `Settings` for that
  one job. `StartRecording` returns the recorder, so you can subscribe to its events.

`RecordingSaved` / `RecordingFailed` / `ScreenshotSaved` events and the `On Recording Saved` UnityEvent are raised on the
main thread. Settings are copied when a recording starts. A new recording can start while the previous one is still
being written.

**WebGL:** every finished file also fires a DOM event on `window`, so a web page can upload or display it:

```js
window.addEventListener("gifrecorder:saved", e => {
  const { name, mimeType, blob, url } = e.detail;   // url is an object URL for blob, revoked after 60 s
});
```

To keep a file for longer, hold on to `blob` (or call `URL.createObjectURL(blob)` yourself). Set **WebGL Download**
off to only raise the event without starting a download.

## Settings

| Setting | Meaning |
|---|---|
| Format | `Gif` (palette per frame) or `Apng` (lossless, saved as `.png`). |
| Frame Rate | Captures per second (1–60). GIF is capped at 50 fps, because viewers slow down faster GIFs. |
| Show Guide Lines | On-screen guide around the recorded area. Independent of the output size; never recorded. |
| Fixed Output Size / Output Size | Record at an exact size, cropping the centre of the source to its aspect ratio. |
| Resolution Scale, Max Width/Height | Output size relative to the source, with optional pixel limits (aspect ratio is kept). Ignored with a fixed output size. |
| Max Duration / Loop Count | Auto-stop after N seconds (0 = manual) / number of plays (0 = forever). |
| Capture Source / Capture Camera | `Screen` (includes UI, opaque) or `Camera` (one camera, keeps alpha). |
| Preserve Transparency / GIF Alpha Threshold | See *Transparency*. |
| GIF Colors | Palette size per frame, 2–256. Fewer colours give smaller files. |
| GIF Dithering | Floyd–Steinberg dithering: smoother gradients, slightly larger files. |
| GIF Palette Sampling | Build the palette from every n-th pixel. 1 = best, higher = faster. |
| APNG Compression | `Fastest` or `Optimal` (deflate effort). |
| Encoding Mode | `Auto` (threads, time-sliced on WebGL), `Threaded`, or `TimeSliced`. |
| Time Slice Budget (ms) | Main-thread encoding time per frame in time-sliced mode. |
| WebGL Download | Trigger a browser download when a WebGL recording finishes. |
| Max Queued Frames | Back-pressure: captures are skipped while this many frames are waiting to be encoded. |

## How it works

* **Capture** (main thread, ~0.03 ms): at the end of the frame, either `ScreenCapture.CaptureScreenshotIntoRenderTexture`
  or a camera render request (`RenderPipeline.SubmitRenderRequest`, with a `Camera.Render` fallback for the built-in
  pipeline) fills a texture. The texture is downsampled on the GPU by successive halving.
* **Readback:** `AsyncGPUReadback`, which is polled and never waited on. Platforms without it (e.g. WebGL) use
  `ReadPixels` on the small, already-downsampled texture.
* **Encoding:** each frame becomes an `EncodeJob`, a resumable iterator. The pipeline runs jobs either in parallel
  on the thread pool or step by step under a time budget. Both modes produce byte-identical files.
  * GIF: the frame is cropped to the changed region, gets a median-cut palette and optional dithering; unchanged pixels
    are written as transparent; then LZW.
  * APNG: the frame is cropped to the changed region, filtered per row with the adaptive PNG heuristic, and compressed
    with `System.IO.Compression.DeflateStream`, fed in chunks so it can be time-sliced.
* **Writing:** frames are written in order to `<file>.part`, the header is patched (the APNG frame count isn't known
  until the end), and the file is renamed when complete.

Frame delays come from the actual capture timestamps, quantised against the absolute timeline. A recording therefore
plays back at real speed even if the frame rate fluctuates.

## Notes

* Time-sliced encoding has to keep up with capture, or the backlog grows until **Max Queued Frames** starts skipping
  captures. On WebGL, raise the budget, or lower the resolution or frame rate, if you see the "falling behind" warning.
  GIF is considerably cheaper to encode than APNG.
* On WebGL, `ReadPixels` is synchronous. It runs on the small output texture, but still makes the CPU wait for the GPU
  once per captured frame.
* If the window is resized during a recording, frames are rescaled to the size chosen when recording started.
* Add `Recordings/` to your `.gitignore`.

## License

GifRecorder is licensed under the [MIT License with the Commons Clause 1.0](LICENSE.txt).

## AI disclosure

This project was developed with the assistance of AI (Claude by Anthropic). Code was AI-assisted and reviewed and tested by the author.
