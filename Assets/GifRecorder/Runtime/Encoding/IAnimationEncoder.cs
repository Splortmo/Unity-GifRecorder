using System;
using System.Collections;
using System.IO;

namespace GifRecorder.Encoding
{
    /// <summary>
    /// An animated image format. Frames are encoded through <see cref="EncodeJob"/>s, which may run in parallel on
    /// worker threads or be time-sliced on the main thread (WebGL); header and trailer are written by the pipeline's writer.
    /// </summary>
    public interface IAnimationEncoder
    {
        string FileExtension { get; }

        /// <summary>Smallest unit a frame delay can be expressed in.</summary>
        int TimeQuantumMs { get; }

        /// <summary>Shortest delay that viewers reliably honour.</summary>
        int MinDelayMs { get; }

        void WriteHeader(Stream output, int width, int height);

        /// <summary>
        /// Creates a job that encodes one frame into a self-contained block of bytes. No work happens until the job is stepped.
        /// <paramref name="previous"/> and <paramref name="next"/> are the neighbouring frames (null at the ends);
        /// jobs must only read them. A job must not depend on other jobs having run.
        /// </summary>
        EncodeJob CreateJob(Frame frame, Frame previous, Frame next, FrameInfo info);

        /// <summary>Writes the end of the file. The stream is seekable, so encoders may patch earlier data.</summary>
        void WriteTrailer(Stream output, int frameCount);
    }

    /// <summary>
    /// A resumable unit of encoding work. Each <see cref="Step"/> does a small slice (well under a millisecond on
    /// desktop), so it can be interleaved with gameplay when no threads are available.
    /// </summary>
    public sealed class EncodeJob
    {
        IEnumerator steps;
        bool done;

        /// <summary>Encoded bytes, valid once the job is done. Rented from <see cref="EncodedBuffers"/>.</summary>
        public ArraySegment<byte> Result { get; internal set; }

        public bool IsDone => done;

        EncodeJob() { }

        /// <summary>Wraps an iterator; every <c>yield return</c> marks a point where work may be suspended.</summary>
        public static EncodeJob Create(Func<EncodeJob, IEnumerator> body)
        {
            var job = new EncodeJob();
            job.steps = body(job);
            return job;
        }

        /// <summary>Runs one slice. Returns true once the job has finished.</summary>
        public bool Step()
        {
            if (!done && !steps.MoveNext()) done = true;
            return done;
        }

        public ArraySegment<byte> RunToCompletion()
        {
            while (!Step()) { }
            return Result;
        }
    }
}
