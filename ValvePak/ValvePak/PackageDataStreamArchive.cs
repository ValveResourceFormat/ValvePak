using System;
using System.IO;

namespace ValvePak
{
	/// <summary>
	/// Archive data embedded in a package that was read from a <see cref="PackageDataStream"/>, such as a package inside another package.
	/// Reads go to the archive of the stream without moving the stream position, so they are not serialized.
	/// </summary>
	internal sealed class PackageDataStreamArchive : Archive
	{
#pragma warning disable CA2213 // Disposable fields should be disposed
		private readonly PackageDataStream DataStream;
#pragma warning restore CA2213

		public PackageDataStreamArchive(PackageDataStream stream, long origin, long dataOffset)
			: base(origin, dataOffset)
		{
			DataStream = stream;
		}

		public override long Length => DataStream.Length - Origin;

		public override int Read(long offset, Span<byte> output)
		{
			return DataStream.ReadAt(Origin + offset, output);
		}

		public override Stream? OpenMappedStream(long offset, long length)
		{
			return DataStream.OpenMappedStream(Origin + offset, length);
		}

		/// <summary>
		/// Does nothing, the stream is owned by the package.
		/// </summary>
		public override void Dispose()
		{
		}
	}
}
