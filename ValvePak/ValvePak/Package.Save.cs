using System;
using System.Collections.Generic;
using System.IO;

namespace SteamDatabase.ValvePak
{
	public partial class Package
	{
		/// <summary>
		/// Add file to current package. Be careful to not add duplicate entries, because this does not check for duplicates.
		/// </summary>
		/// <param name="filePath">Full file path for this entry.</param>
		/// <param name="fileData">File data for this entry.</param>
		public void AddFile(string filePath, byte[] fileData)
		{
			var lastSeparator = filePath.LastIndexOf(DirectorySeparatorChar);
			var directory = lastSeparator > -1 ? filePath[..lastSeparator] : string.Empty;
			var fileName = filePath[(lastSeparator + 1)..];

			var dot = fileName.LastIndexOf('.');
			string extension;

			if (dot > -1)
			{
				extension = fileName[(dot + 1)..];
				fileName = fileName[..dot];
			}
			else
			{
				// Valve uses a space for missing extensions
				extension = " ";
			}

			var entry = new PackageEntry
			{
				FileName = fileName,
				DirectoryName = directory,
				TypeName = extension,
				SmallData = fileData,
				CRC32 = Crc32.Compute(fileData),
			};

			if (Entries == null)
			{
				var stringComparer = Comparer == null ? null : StringComparer.FromComparison(Comparer.Comparison);
				Entries = new Dictionary<string, List<PackageEntry>>(stringComparer);
			}

			if (!Entries.TryGetValue(extension, out var typeEntries))
			{
				typeEntries = new List<PackageEntry>();
				Entries[extension] = typeEntries;
			}

			typeEntries.Add(entry);
		}
	}
}
