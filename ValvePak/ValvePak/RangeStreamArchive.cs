using System;
using System.IO;

namespace ValvePak
{
	/// <summary>
	/// Archive data embedded in a package that was read from an <see cref="ArchiveRangeStream"/>, such as a package inside another package.
	/// Reads go to the archive of the stream without moving the stream position, so they are not serialized.
	/// </summary>
	internal sealed class RangeStreamArchive : Archive
	{
#pragma warning disable CA2213 // Disposable fields should be disposed
		private readonly ArchiveRangeStream RangeStream;
#pragma warning restore CA2213

		public RangeStreamArchive(ArchiveRangeStream stream, long origin, long dataOffset)
			: base(origin, dataOffset)
		{
			RangeStream = stream;
		}

		public override long Length => RangeStream.Length - Origin;

		public override int Read(long offset, Span<byte> output)
		{
			return RangeStream.ReadAt(Origin + offset, output);
		}

		public override Stream? OpenMappedStream(long offset, long length)
		{
			return RangeStream.OpenMappedStream(Origin + offset, length);
		}

		/// <summary>
		/// Does nothing, the stream is owned by the package.
		/// </summary>
		public override void Dispose()
		{
		}
	}
}
