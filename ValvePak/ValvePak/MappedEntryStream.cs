using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace ValvePak
{
	/// <summary>
	/// Read-only stream over a range of a memory mapped view.
	/// Holds a reference on the view for its whole lifetime, so the view stays mapped until this stream is disposed or finalized.
	/// </summary>
	internal sealed unsafe class MappedEntryStream : Stream
	{
		private SafeMemoryMappedViewHandle? ViewHandle;
		private byte* Pointer;
		private readonly long StreamLength;
		private long StreamPosition;

		/// <summary>
		/// Initializes a new instance of the <see cref="MappedEntryStream"/> class.
		/// </summary>
		/// <param name="viewHandle">Handle of the view.</param>
		/// <param name="offset">Offset in bytes of the range from the start of the view.</param>
		/// <param name="length">Length in bytes of the range.</param>
		public MappedEntryStream(SafeMemoryMappedViewHandle viewHandle, long offset, long length)
		{
			byte* pointer = null;
			viewHandle.AcquirePointer(ref pointer);

			ViewHandle = viewHandle;
			Pointer = pointer + offset;
			StreamLength = length;
		}

		/// <summary>
		/// Releases the reference on the view when the stream was not disposed.
		/// </summary>
		~MappedEntryStream()
		{
			Dispose(false);
		}

		/// <inheritdoc/>
		protected override void Dispose(bool disposing)
		{
			var viewHandle = Interlocked.Exchange(ref ViewHandle, null);

			if (viewHandle != null)
			{
				Pointer = null;
				viewHandle.ReleasePointer();
			}

			base.Dispose(disposing);
		}

		/// <inheritdoc/>
		public override bool CanRead => Pointer != null;

		/// <inheritdoc/>
		public override bool CanSeek => Pointer != null;

		/// <inheritdoc/>
		public override bool CanWrite => false;

		/// <inheritdoc/>
		public override long Length
		{
			get
			{
				ThrowIfDisposed();
				return StreamLength;
			}
		}

		/// <inheritdoc/>
		public override long Position
		{
			get
			{
				ThrowIfDisposed();
				return StreamPosition;
			}
			set
			{
				ThrowIfDisposed();
				ArgumentOutOfRangeException.ThrowIfNegative(value);
				StreamPosition = value;
			}
		}

		/// <inheritdoc/>
		public override int Read(byte[] buffer, int offset, int count)
		{
			ValidateBufferArguments(buffer, offset, count);

			return Read(buffer.AsSpan(offset, count));
		}

		/// <inheritdoc/>
		public override int Read(Span<byte> buffer)
		{
			ThrowIfDisposed();

			var remaining = StreamLength - StreamPosition;

			if (remaining <= 0)
			{
				return 0;
			}

			var count = (int)Math.Min(remaining, buffer.Length);
			new ReadOnlySpan<byte>(Pointer + StreamPosition, count).CopyTo(buffer);
			StreamPosition += count;

			return count;
		}

		/// <inheritdoc/>
		public override int ReadByte()
		{
			ThrowIfDisposed();

			if (StreamPosition >= StreamLength)
			{
				return -1;
			}

			return Pointer[StreamPosition++];
		}

		/// <inheritdoc/>
		public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
		{
			ValidateBufferArguments(buffer, offset, count);

			return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
		}

		/// <inheritdoc/>
		public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			if (cancellationToken.IsCancellationRequested)
			{
				return ValueTask.FromCanceled<int>(cancellationToken);
			}

			try
			{
				return ValueTask.FromResult(Read(buffer.Span));
			}
			catch (ObjectDisposedException e)
			{
				return ValueTask.FromException<int>(e);
			}
		}

		/// <inheritdoc/>
		public override void CopyTo(Stream destination, int bufferSize)
		{
			ThrowIfDisposed();
			ValidateCopyToArguments(destination, bufferSize);

			while (StreamPosition < StreamLength)
			{
				var count = (int)Math.Min(StreamLength - StreamPosition, Array.MaxLength);
				destination.Write(new ReadOnlySpan<byte>(Pointer + StreamPosition, count));
				StreamPosition += count;
			}
		}

		/// <inheritdoc/>
		public override long Seek(long offset, SeekOrigin origin)
		{
			ThrowIfDisposed();

			var position = origin switch
			{
				SeekOrigin.Begin => offset,
				SeekOrigin.Current => StreamPosition + offset,
				SeekOrigin.End => StreamLength + offset,
				_ => throw new ArgumentException("Invalid seek origin.", nameof(origin)),
			};

			if (position < 0)
			{
				throw new IOException("An attempt was made to move the position before the beginning of the stream.");
			}

			StreamPosition = position;

			return position;
		}

		/// <inheritdoc/>
		public override void Flush()
		{
			ThrowIfDisposed();
		}

		/// <inheritdoc/>
		public override void SetLength(long value) => throw new NotSupportedException("Stream does not support writing.");

		/// <inheritdoc/>
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Stream does not support writing.");

		private void ThrowIfDisposed()
		{
			ObjectDisposedException.ThrowIf(Pointer == null, this);
		}
	}
}
