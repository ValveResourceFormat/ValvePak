using System;
using System.IO;

namespace ValvePak
{
	/// <summary>
	/// A source of archive data of a package: a chunk file, or the directory file for data embedded in it.
	/// Offsets are relative to the start of the archive, which for the directory file is where the package starts.
	/// </summary>
	internal abstract class Archive : IDisposable
	{
		protected Archive(long origin, long dataOffset)
		{
			Origin = origin;
			DataOffset = dataOffset;
		}

		/// <summary>
		/// Gets the offset of entry data, which in the directory file follows the header and the directory tree.
		/// </summary>
		public long DataOffset { get; }

		/// <summary>
		/// Gets the position in the underlying file or stream where the archive starts.
		/// </summary>
		protected long Origin { get; }

		/// <summary>
		/// Gets the current length of the archive from its start.
		/// </summary>
		public abstract long Length { get; }

		/// <summary>
		/// Reads data at the given offset until the output is full or the end of the archive is reached.
		/// This method is safe to call from multiple threads.
		/// </summary>
		/// <returns>The number of bytes read.</returns>
		public abstract int Read(long offset, Span<byte> output);

		/// <summary>
		/// Returns a stream that reads the given range directly from a memory mapping of the archive.
		/// </summary>
		/// <returns>The stream, or null if the archive can not be mapped or the range is past its end.</returns>
		public virtual Stream? OpenMappedStream(long offset, long length) => null;

		public abstract void Dispose();
	}
}
