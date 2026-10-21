using System;
using System.IO;
using System.Threading;

namespace ValvePak
{
	/// <summary>
	/// Archive data embedded in a package that was read from a stream other than a <see cref="FileStream"/>.
	/// Reads seek the shared stream, so they are serialized with a lock.
	/// </summary>
	internal sealed class StreamArchive : Archive
	{
#pragma warning disable CA2213 // Disposable fields should be disposed
		private readonly Stream Stream;
#pragma warning restore CA2213
		private readonly Lock StreamLock = new();

		public StreamArchive(Stream stream, long origin, long dataOffset)
			: base(origin, dataOffset)
		{
			Stream = stream;
		}

		public override long Length
		{
			get
			{
				lock (StreamLock)
				{
					return Stream.Length - Origin;
				}
			}
		}

		public override int Read(long offset, Span<byte> output)
		{
			lock (StreamLock)
			{
				Stream.Position = Origin + offset;

				return Stream.ReadAtLeast(output, output.Length, throwOnEndOfStream: false);
			}
		}

		/// <summary>
		/// Does nothing, the stream is owned by the package.
		/// </summary>
		public override void Dispose()
		{
		}
	}
}
