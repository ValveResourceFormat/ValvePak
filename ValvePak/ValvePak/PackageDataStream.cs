using System;
using System.IO;

namespace ValvePak
{
	/// <summary>
	/// Read-only seekable stream over data stored in a package: bytes held in memory such as the preload bytes of an entry,
	/// followed by a range of an archive, which it reads from without owning it. Either part can be empty.
	/// </summary>
	internal sealed class PackageDataStream : Stream
	{
#pragma warning disable CA2213 // Disposable fields should be disposed
		private readonly Archive? Archive;
#pragma warning restore CA2213
		private readonly long Start;
		private readonly long RangeLength;
		private readonly byte[] Prefix;
		private long CurrentPosition;
		private bool IsDisposed;

		/// <summary>
		/// Creates a stream over the given range of the archive, which starts with <paramref name="prefix"/> if given.
		/// </summary>
		public PackageDataStream(Archive archive, long start, long length, byte[]? prefix = null)
		{
			Archive = archive;
			Start = start;
			RangeLength = length;
			Prefix = prefix ?? [];
		}

		/// <summary>
		/// Creates a stream over bytes held in memory.
		/// </summary>
		public PackageDataStream(byte[] data)
		{
			Prefix = data;
		}

		public override bool CanRead => !IsDisposed;

		public override bool CanSeek => !IsDisposed;

		public override bool CanWrite => false;

		public override long Length => Prefix.Length + RangeLength;

		public override long Position
		{
			get => CurrentPosition;
			set
			{
				ArgumentOutOfRangeException.ThrowIfNegative(value);
				ObjectDisposedException.ThrowIf(IsDisposed, this);

				CurrentPosition = value;
			}
		}

		public override int Read(byte[] buffer, int offset, int count)
		{
			ValidateBufferArguments(buffer, offset, count);

			return Read(buffer.AsSpan(offset, count));
		}

		public override int Read(Span<byte> buffer)
		{
			var bytesRead = ReadAt(CurrentPosition, buffer);
			CurrentPosition += bytesRead;

			return bytesRead;
		}

		/// <summary>
		/// Reads at the given position of the stream without moving its position.
		/// This method is safe to call from multiple threads.
		/// </summary>
		/// <returns>The number of bytes read, which is less than requested only at the end of the stream or of the archive.</returns>
		public int ReadAt(long position, Span<byte> buffer)
		{
			ObjectDisposedException.ThrowIf(IsDisposed, this);

			var prefixRead = 0;

			if (position < Prefix.Length)
			{
				prefixRead = Math.Min(Prefix.Length - (int)position, buffer.Length);
				Prefix.AsSpan((int)position, prefixRead).CopyTo(buffer);
				position += prefixRead;
				buffer = buffer[prefixRead..];
			}

			var available = Length - position;

			if (available <= 0 || buffer.IsEmpty || Archive == null)
			{
				return prefixRead;
			}

			if (buffer.Length > available)
			{
				buffer = buffer[..(int)available];
			}

			return prefixRead + Archive.Read(Start + position - Prefix.Length, buffer);
		}

		/// <summary>
		/// Returns a stream that reads the given range of this stream directly from a memory mapping of the archive.
		/// </summary>
		/// <returns>The stream, or null if the archive can not be mapped or the range is not entirely in the archive range.</returns>
		public Stream? OpenMappedStream(long position, long length)
		{
			ObjectDisposedException.ThrowIf(IsDisposed, this);

			var offset = position - Prefix.Length;

			if (Archive == null || offset < 0 || length > RangeLength - offset)
			{
				return null;
			}

			return Archive.OpenMappedStream(Start + offset, length);
		}

		public override long Seek(long offset, SeekOrigin origin)
		{
			ObjectDisposedException.ThrowIf(IsDisposed, this);

			var position = origin switch
			{
				SeekOrigin.Begin => offset,
				SeekOrigin.Current => CurrentPosition + offset,
				SeekOrigin.End => Length + offset,
				_ => throw new ArgumentOutOfRangeException(nameof(origin)),
			};

			if (position < 0)
			{
				throw new IOException("An attempt was made to move the position before the beginning of the stream.");
			}

			CurrentPosition = position;

			return position;
		}

		public override void Flush()
		{
		}

		public override void SetLength(long value)
		{
			throw new NotSupportedException("The stream is read-only.");
		}

		public override void Write(byte[] buffer, int offset, int count)
		{
			throw new NotSupportedException("The stream is read-only.");
		}

		protected override void Dispose(bool disposing)
		{
			IsDisposed = true;

			base.Dispose(disposing);
		}
	}
}
