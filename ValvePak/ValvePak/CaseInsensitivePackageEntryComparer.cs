using System;
using System.Collections.Generic;

namespace SteamDatabase.ValvePak
{
	class CaseInsensitivePackageEntryComparer : IComparer<PackageEntry>
	{
		public StringComparison Comparison { get; } = default;

		public CaseInsensitivePackageEntryComparer(StringComparison comparison)
		{
			Comparison = comparison;
		}

		/// <remarks>
		/// Intentionally not comparing TypeName because this comparer is used on Entries which is split by extension already.
		/// </remarks>
		public int Compare(PackageEntry x, PackageEntry y)
		{
			var comp = x.FileName.Length.CompareTo(y.FileName.Length);

			if (comp != 0)
			{
				return comp;
			}

			comp = x.DirectoryName.Length.CompareTo(y.DirectoryName.Length);

			if (comp != 0)
			{
				return comp;
			}

			comp = string.Compare(x.FileName, y.FileName, Comparison);

			if (comp != 0)
			{
				return comp;
			}

			return string.Compare(x.DirectoryName, y.DirectoryName, Comparison);
		}
	}
}
