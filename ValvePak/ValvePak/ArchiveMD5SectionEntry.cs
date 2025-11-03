namespace SteamDatabase.ValvePak
{
	/// <summary>
	/// Represents an entry in the VPK archive MD5 section, containing checksum information for a chunk of archive data.
	/// </summary>
	public class ArchiveMD5SectionEntry
	{
		/// <summary>
		/// Gets or sets the archive index.
		/// </summary>
		public required uint ArchiveIndex { get; set; }

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
