namespace SteamDatabase.ValvePak
{
	/// <summary>
	/// Represents the signature type used in VPK archives.
	/// </summary>
	public enum ESignatureType
	{
		/// <summary>
		/// Type not yet determined.
		/// </summary>
		Unknown = -1,

		/// <summary>
		/// RSA legacy signature format.
		/// </summary>
		FullFile = 0,

		/// <summary>
		/// RSA-4096 signature format (version 1).
		/// </summary>
		OnlyFileChecksum = 1,
	}
}
