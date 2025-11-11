namespace SteamDatabase.ValvePak
{
	/// <summary>
	/// Represents an entry in the VPK archive hashes section, containing checksum information for a chunk of archive data.
	/// </summary>
	public class ChunkHashFraction
	{
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
	}
}
