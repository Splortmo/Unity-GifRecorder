mergeInto(LibraryManager.library, {
  GifRecorder_Publish: function (namePtr, mimePtr, dataPtr, length, download) {
    var name = UTF8ToString(namePtr);
    var mimeType = UTF8ToString(mimePtr);
    // Copy out of the wasm heap: it may be resized (and the view invalidated) later.
    var bytes = HEAPU8.slice(dataPtr, dataPtr + length);
    var blob = new Blob([bytes], { type: mimeType });
    var url = URL.createObjectURL(blob);

    window.dispatchEvent(new CustomEvent("gifrecorder:saved", {
      detail: { name: name, mimeType: mimeType, blob: blob, url: url }
    }));

    if (download) {
      var link = document.createElement("a");
      link.href = url;
      link.download = name;
      link.style.display = "none";
      document.body.appendChild(link);
      link.click();
      document.body.removeChild(link);
    }

    // Release the object URL once the download and any listeners have had time to read it.
    // Listeners that need the file for longer should keep detail.blob instead.
    setTimeout(function () { URL.revokeObjectURL(url); }, 60000);
  }
});
