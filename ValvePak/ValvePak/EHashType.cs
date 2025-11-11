namespace SteamDatabase.ValvePak
{
	/// <summary>
	/// Represents the hash algorithm type used in VPK archive MD5 section entries.
	/// </summary>
#pragma warning disable CA1028 // Enum Storage should be Int32
	public enum EHashType : ushort
#pragma warning restore CA1028
	{
		/// <summary>
		/// MD5 hash algorithm.
		/// </summary>
		MD5 = 0,

		/// <summary>
		/// Blake3 hash algorithm.
		/// </summary>
		Blake3 = 1,
	}
}
