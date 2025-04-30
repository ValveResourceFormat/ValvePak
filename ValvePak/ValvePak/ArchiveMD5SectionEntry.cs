namespace SteamDatabase.ValvePak
{
	/// <summary>
	/// VPK_ArchiveMD5SectionEntry
	/// </summary>
	public class ArchiveMD5SectionEntry
	{
		/// <summary>
		/// Gets or sets the CRC32 checksum of this entry.
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
		/// Gets or sets the expected Checksum checksum.
		/// </summary>
		public required byte[] Checksum { get; set; }
	}
}
