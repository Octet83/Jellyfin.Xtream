// Copyright (C) 2022  Kevin Jilissen

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.IO;
using System.Threading;

namespace Jellyfin.Xtream.Service;

/// <summary>
/// Stream which writes to a self-overwriting internal buffer.
/// </summary>
public class WrappedBufferReadStream : Stream
{
    /// <summary>
    /// How deep behind the live edge a genuinely fresh consumer starts. The write side trims
    /// reconnect overlap before it enters the buffer, so this span contains only clean, monotone
    /// content — it is a standing delay buffer that absorbs the dead-air of upstream reconnects
    /// (2 s backoff + re-downloading the provider's re-served backlog), which otherwise starves
    /// the transcoder and pauses playback at every drop. ~20 MiB ≈ 13-20 s at typical bitrates.
    /// </summary>
    private const long DeepPrerollBytes = 20 * 1024 * 1024;

    /// <summary>
    /// How far behind the furthest already-consumed position a RE-attaching consumer resumes.
    /// Must be small: an FFmpeg HTTP re-GET continues the same demuxer input, and every byte it
    /// has already seen is replayed as a timestamp rewind (freeze + audio replay). 512 KiB keeps
    /// enough trailing data to land on a PAT/PMT without a perceptible rewind.
    /// </summary>
    private const long ResumePrerollBytes = 512 * 1024;

    private readonly WrappedBufferStream _sourceBuffer;

    private readonly long _initialReadHead;

    /// <summary>
    /// Initializes a new instance of the <see cref="WrappedBufferReadStream"/> class.
    /// </summary>
    /// <param name="sourceBuffer">The source buffer to read from.</param>
    public WrappedBufferReadStream(WrappedBufferStream sourceBuffer)
    {
        _sourceBuffer = sourceBuffer;

        // Fresh session (nobody consumed far yet): start deep for the delay buffer. Re-attach
        // (a previous reader already consumed up to MaxReadHead): resume just behind that point
        // so the demuxer is not re-fed a long span it has already muxed. Never start beyond the
        // live edge nor on bytes the ring has already overwritten.
        long deepStart = sourceBuffer.TotalBytesWritten - DeepPrerollBytes;
        long resumeStart = sourceBuffer.MaxReadHead - ResumePrerollBytes;
        long start = Math.Max(deepStart, resumeStart);
        start = Math.Min(start, sourceBuffer.TotalBytesWritten);
        start = Math.Max(start, sourceBuffer.TotalBytesWritten - sourceBuffer.BufferSize);
        _initialReadHead = Math.Max(0, start);
        ReadHead = _initialReadHead;
    }

    /// <summary>
    /// Gets the virtual position in the source buffer.
    /// </summary>
    public long ReadHead { get; private set; }

    /// <summary>
    /// Gets the number of bytes that have been written to this stream.
    /// </summary>
    public long TotalBytesRead { get => ReadHead - _initialReadHead; }

    /// <inheritdoc />
    public override long Position
    {
        get => ReadHead % _sourceBuffer.BufferSize; set { }
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override bool CanSeek => false;

#pragma warning disable CA1065
    /// <inheritdoc />
    public override long Length { get => throw new NotImplementedException(); }
#pragma warning restore CA1065

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        long gap = _sourceBuffer.TotalBytesWritten - ReadHead;

        // We cannot return with 0 bytes read, as that indicates the end of the stream has been reached
        while (gap == 0)
        {
            Thread.Sleep(1);
            gap = _sourceBuffer.TotalBytesWritten - ReadHead;
        }

        if (gap > _sourceBuffer.BufferSize)
        {
            // The reader fell more than a full buffer behind — typically a transient FFmpeg stall on a
            // stream discontinuity in the source. The overtaken bytes are already gone, so rather than
            // killing the whole stream (which froze playback), skip forward to recent data and carry on.
            // The consumer sees a jump instead of a hard failure. Resume close to the live edge: every
            // byte of stale history re-read here reaches the demuxer as a timestamp rewind.
            ReadHead = _sourceBuffer.TotalBytesWritten - ResumePrerollBytes;
            gap = _sourceBuffer.TotalBytesWritten - ReadHead;
        }

        // The number of bytes that can be copied.
        long canCopy = Math.Min(count, gap);
        long read = 0;

        // Copy inside a loop to simplify wrapping logic.
        while (read < canCopy)
        {
            // The amount of bytes that we can directly write from the current position without wrapping.
            long readable = Math.Min(canCopy - read, _sourceBuffer.BufferSize - Position);

            // Copy the data.
            Array.Copy(_sourceBuffer.Buffer, Position, buffer, offset + read, readable);
            read += readable;
            ReadHead += readable;
        }

        // Publish the consumption high-water mark so a re-attaching consumer resumes here instead
        // of being re-fed history its demuxer has already muxed.
        _sourceBuffer.ReportReadHead(ReadHead);

        return (int)read;
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public override void SetLength(long value)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public override void Flush()
    {
        // Do nothing
    }
}
