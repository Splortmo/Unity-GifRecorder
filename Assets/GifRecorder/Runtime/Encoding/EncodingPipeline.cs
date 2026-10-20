using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GifRecorder.Encoding
{
    /// <summary>
    /// Streams frames to disk without blocking the caller.
    /// <list type="bullet">
    /// <item><b>Threaded</b>: frames are encoded in parallel on the thread pool and written in order by a dedicated
    /// writer thread.</item>
    /// <item><b>Time-sliced</b> (for platforms without threads, e.g. WebGL): nothing runs on its own; the owner calls
    /// <see cref="Pump"/> once per frame with a time budget, and encoding advances in small resumable steps.</item>
    /// </list>
    /// Memory stays bounded by the encoding backlog rather than by the length of the recording.
    /// </summary>
    /// <remarks>
    /// <see cref="AddFrame"/>, <see cref="Complete"/> and <see cref="Pump"/> must be called from a single thread
    /// (e.g. the Unity main thread).
    /// </remarks>
    public sealed class EncodingPipeline
    {
        readonly IAnimationEncoder encoder;
        readonly int width, height;
        readonly double nominalFrameMs;
        readonly string tempPath;
        readonly bool threaded;
        readonly TaskCompletionSource<string> completion =
            new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Threaded mode.
        readonly BlockingCollection<WorkItem> queue;

        // Time-sliced mode.
        readonly Queue<WorkItem> pending;
        FileStream sliceStream;
        Frame sliceWrittenPrevious;

        Frame heldFrame;
        double heldTimeMs;
        Frame lastScheduled;
        long emittedMs;
        int scheduledCount;
        int writtenCount;
        volatile bool failed;
        bool completed;
        bool finished;

        readonly struct WorkItem
        {
            public readonly Task<ArraySegment<byte>> Task;
            public readonly EncodeJob Job;
            public readonly Frame Frame;

            public WorkItem(Task<ArraySegment<byte>> task, EncodeJob job, Frame frame)
            {
                Task = task;
                Job = job;
                Frame = frame;
            }
        }

        public string OutputPath { get; }

        /// <summary>True when encoding only advances through <see cref="Pump"/>.</summary>
        public bool IsTimeSliced => !threaded;

        /// <summary>Completes with the output path once the file is fully written, or faults if encoding failed.</summary>
        public Task<string> Completion => completion.Task;

        public int FramesAdded => scheduledCount + (heldFrame != null ? 1 : 0);
        public int FramesWritten => Volatile.Read(ref writtenCount);

        /// <summary>Frames handed to the pipeline that are not on disk yet.</summary>
        public int PendingFrames => Math.Max(0, FramesAdded - FramesWritten);

        /// <param name="threaded">False selects time-sliced mode, which never starts threads or tasks.</param>
        public EncodingPipeline(IAnimationEncoder encoder, int width, int height, string outputPath, double nominalFrameMs, bool threaded = true)
        {
            this.encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
            if (width <= 0 || height <= 0 || width > ushort.MaxValue || height > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(width), "Output size must be between 1 and 65535.");
            this.width = width;
            this.height = height;
            this.nominalFrameMs = Math.Max(1, nominalFrameMs);
            this.threaded = threaded;
            OutputPath = outputPath;
            tempPath = outputPath + ".part";

            if (threaded)
            {
                queue = new BlockingCollection<WorkItem>();
                var writer = new Thread(WriterLoop) { IsBackground = true, Name = "GifRecorder Writer" };
                writer.Start();
            }
            else
            {
                pending = new Queue<WorkItem>();
            }
        }

        /// <summary>
        /// Adds a frame captured at <paramref name="timeMs"/> (relative to the start of the recording).
        /// The pipeline takes ownership of the frame and releases it once it is no longer needed.
        /// </summary>
        public void AddFrame(Frame frame, double timeMs)
        {
            if (completed) throw new InvalidOperationException("The pipeline has already been completed.");
            if (frame.Width != width || frame.Height != height)
                throw new ArgumentException($"Frame is {frame.Width}x{frame.Height}, expected {width}x{height}.", nameof(frame));

            if (failed)
            {
                frame.Release();
                heldFrame?.Release();
                heldFrame = null;
                return;
            }

            // A frame's delay (and its successor, which encoders may look at) is only known once the next one
            // arrives, so hold one frame back.
            if (heldFrame != null)
                Schedule(heldFrame, Math.Max(timeMs, heldTimeMs), frame);
            heldFrame = frame;
            heldTimeMs = timeMs;
        }

        /// <summary>Signals that no more frames will be added. The file is finalized in the background (or by <see cref="Pump"/>).</summary>
        public Task<string> Complete()
        {
            if (completed) return Completion;
            completed = true;

            if (heldFrame != null)
            {
                if (failed) heldFrame.Release();
                else Schedule(heldFrame, heldTimeMs + nominalFrameMs, null);
                heldFrame = null;
            }
            queue?.CompleteAdding();
            return Completion;
        }

        void Schedule(Frame frame, double endTimeMs, Frame next)
        {
            // Quantize against the absolute timeline so rounding errors don't accumulate.
            int quantum = Math.Max(1, encoder.TimeQuantumMs);
            long units = (long)Math.Round((endTimeMs - emittedMs) / quantum);
            long delay = Math.Max(encoder.MinDelayMs, units * quantum);
            emittedMs += delay;

            var info = new FrameInfo(scheduledCount++, (int)Math.Min(delay, int.MaxValue));
            var job = encoder.CreateJob(frame, lastScheduled, next, info);
            lastScheduled = frame;

            if (threaded)
                queue.Add(new WorkItem(Task.Run(() => job.RunToCompletion()), job, frame));
            else
                pending.Enqueue(new WorkItem(null, job, frame));
        }

        /// <summary>
        /// Time-sliced mode: advances encoding and writing for roughly <paramref name="budgetMs"/> milliseconds.
        /// Returns true once the pipeline has finished (successfully or not). Does nothing in threaded mode.
        /// </summary>
        public bool Pump(double budgetMs)
        {
            if (threaded) return Completion.IsCompleted;
            if (finished) return true;

            var watch = Stopwatch.StartNew();
            try
            {
                if (sliceStream == null)
                {
                    sliceStream = OpenOutput();
                    encoder.WriteHeader(sliceStream, width, height);
                }

                do
                {
                    if (pending.Count == 0)
                    {
                        if (completed) FinishSliced();
                        break;
                    }

                    var item = pending.Peek();
                    if (!item.Job.Step()) continue;

                    pending.Dequeue();
                    var data = item.Job.Result;
                    sliceStream.Write(data.Array, data.Offset, data.Count);
                    EncodedBuffers.Return(data);
                    sliceWrittenPrevious?.Release();
                    sliceWrittenPrevious = item.Frame;
                    writtenCount++;
                } while (watch.Elapsed.TotalMilliseconds < budgetMs);
            }
            catch (Exception e)
            {
                FailSliced(e);
            }
            return finished;
        }

        void FinishSliced()
        {
            sliceWrittenPrevious?.Release();
            sliceWrittenPrevious = null;
            if (writtenCount == 0) throw new InvalidOperationException("No frames were captured.");

            encoder.WriteTrailer(sliceStream, writtenCount);
            sliceStream.Dispose();
            sliceStream = null;
            MoveIntoPlace();
            finished = true;
            completion.TrySetResult(OutputPath);
        }

        void FailSliced(Exception e)
        {
            failed = true;
            finished = true;
            sliceStream?.Dispose();
            sliceStream = null;
            TryDelete(tempPath);

            sliceWrittenPrevious?.Release();
            sliceWrittenPrevious = null;
            while (pending.Count > 0) pending.Dequeue().Frame.Release();
            heldFrame?.Release();
            heldFrame = null;
            completion.TrySetException(e);
        }

        void WriterLoop()
        {
            FileStream stream = null;
            Frame previous = null;
            int count = 0;
            try
            {
                stream = OpenOutput();
                encoder.WriteHeader(stream, width, height);

                foreach (var item in queue.GetConsumingEnumerable())
                {
                    var data = item.Task.GetAwaiter().GetResult();
                    stream.Write(data.Array, data.Offset, data.Count);
                    EncodedBuffers.Return(data);

                    // The previous frame was only needed by this frame's (now finished) job and the one before it.
                    previous?.Release();
                    previous = item.Frame;
                    count++;
                    Interlocked.Increment(ref writtenCount);
                }
                previous?.Release();
                previous = null;

                if (count == 0) throw new InvalidOperationException("No frames were captured.");

                encoder.WriteTrailer(stream, count);
                stream.Dispose();
                stream = null;
                MoveIntoPlace();
                completion.TrySetResult(OutputPath);
            }
            catch (Exception e)
            {
                failed = true;
                stream?.Dispose();
                TryDelete(tempPath);

                // Keep consuming until Complete() so every frame is returned to its pool.
                foreach (var item in queue.GetConsumingEnumerable())
                {
                    try { EncodedBuffers.Return(item.Task.GetAwaiter().GetResult()); } catch { /* already failing */ }
                    previous?.Release();
                    previous = item.Frame;
                }
                previous?.Release();

                completion.TrySetException(e);
            }
        }

        FileStream OpenOutput()
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(OutputPath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            return new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        }

        void MoveIntoPlace()
        {
            if (File.Exists(OutputPath)) File.Delete(OutputPath);
            File.Move(tempPath, OutputPath);
        }

        static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // best effort
            }
        }
    }
}
