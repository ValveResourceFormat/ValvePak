using System.IO;

namespace SteamDatabase.ValvePak
{
	/// <summary>
	/// Represents an entry in the VPK archive hashes section, containing checksum information for a chunk of archive data.
	/// </summary>
	public class ChunkHashFraction
	{
		/// <summary>
		/// Size in bytes of a serialized entry in the archive hashes section.
		/// </summary>
		public const int SectionEntrySize = 28;

		/// <summary>
		/// Gets or sets the archive index.
		/// </summary>
		public required ushort ArchiveIndex { get; set; }

		/// <summary>
		/// Gets or sets the hash algorithm type used for this entry.
		/// </summary>
		public required EHashType HashType { get; set; }

		/// <summary>
		/// Gets or sets the offset in the package.
		/// </summary>
		public required uint Offset { get; set; }

		/// <summary>
		/// Gets or sets the length in bytes.
		/// </summary>
		public required uint Length { get; set; }

		/// <summary>
		/// Gets or sets the expected checksum.
		/// </summary>
		public required byte[] Checksum { get; set; }

		internal void Write(BinaryWriter writer)
		{
			writer.Write(ArchiveIndex);
			writer.Write((ushort)HashType);
			writer.Write(Offset);
			writer.Write(Length);
			writer.Write(Checksum);
		}
	}
}
