using System;
using System.IO;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif
using UnityEngine;

namespace GifRecorder
{
    /// <summary>
    /// Hands finished recordings to the browser on WebGL: dispatches a <c>gifrecorder:saved</c> DOM event on
    /// <c>window</c> (detail: <c>{ name, mimeType, blob, url }</c>) and optionally triggers a download.
    /// No-op on other platforms.
    /// </summary>
    public static class WebGLBridge
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        static extern void GifRecorder_Publish(string name, string mimeType, byte[] data, int length, int download);
#endif

        /// <param name="mimeType">Defaults to image/gif or image/apng by file extension.</param>
        public static void Publish(string path, bool download, string mimeType = null)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            try
            {
                var bytes = File.ReadAllBytes(path);
                string mime = mimeType ?? (path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ? "image/gif" : "image/apng");
                GifRecorder_Publish(Path.GetFileName(path), mime, bytes, bytes.Length, download ? 1 : 0);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
#endif
        }
    }
}
