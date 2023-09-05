using System;
using System.Collections.Generic;
using System.IO;

namespace SteamDatabase.ValvePak
{
	/// <summary>
	/// VPK (Valve Pak) files are uncompressed archives used to package game content.
	/// </summary>
	public partial class Package : IDisposable
	{
		public const int MAGIC = 0x55AA1234;

		/// <summary>
		/// Always '/' as per Valve's vpk implementation.
		/// </summary>
		public const char DirectorySeparatorChar = '/';

		private BinaryReader Reader;

		/// <summary>
		/// Gets the file name.
		/// </summary>
		public string FileName { get; private set; }

		/// <summary>
		/// Gets whether this package had "_dir" in the name, indicating it has multiple chunk files.
		/// </summary>
		public bool IsDirVPK { get; private set; }

		/// <summary>
		/// Gets the VPK version.
		/// </summary>
		public uint Version { get; private set; }

		/// <summary>
		/// Gets the size in bytes of the header.
		/// </summary>
		public uint HeaderSize { get; private set; }

		/// <summary>
		/// Gets the size in bytes of the directory tree.
		/// </summary>
		public uint TreeSize { get; private set; }

		/// <summary>
		/// Gets how many bytes of file content are stored in this VPK file (0 in CSGO).
		/// </summary>
		public uint FileDataSectionSize { get; private set; }

		/// <summary>
		/// Gets the size in bytes of the section containing MD5 checksums for external archive content.
		/// </summary>
		public uint ArchiveMD5SectionSize { get; private set; }

		/// <summary>
		/// Gets the size in bytes of the section containing MD5 checksums for content in this file.
		/// </summary>
		public uint OtherMD5SectionSize { get; private set; }

		/// <summary>
		/// Gets the size in bytes of the section containing the public key and signature.
		/// </summary>
		public uint SignatureSectionSize { get; private set; }

		/// <summary>
		/// Gets the MD5 checksum of the file tree.
		/// </summary>
		public byte[] TreeChecksum { get; private set; }

		/// <summary>
		/// Gets the MD5 checksum of the archive MD5 checksum section entries.
		/// </summary>
		public byte[] ArchiveMD5EntriesChecksum { get; private set; }

		/// <summary>
		/// Gets the MD5 checksum of the complete package until the signature structure.
		/// </summary>
		public byte[] WholeFileChecksum { get; private set; }

		/// <summary>
		/// Gets the public key.
		/// </summary>
		public byte[] PublicKey { get; private set; }

		/// <summary>
		/// Gets the signature.
		/// </summary>
		public byte[] Signature { get; private set; }

		/// <summary>
		/// Gets the package entries.
		/// </summary>
		public Dictionary<string, List<PackageEntry>> Entries { get; private set; }

		/// <summary>
		/// Gets the archive MD5 checksum section entries. Also known as cache line hashes.
		/// </summary>
		public List<ArchiveMD5SectionEntry> ArchiveMD5Entries { get; private set; }

		private CaseInsensitivePackageEntryComparer Comparer;

		/// <summary>
		/// Releases binary reader.
		/// </summary>
		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		protected virtual void Dispose(bool disposing)
		{
			if (disposing && Reader != null)
			{
				Reader.Dispose();
				Reader = null;
			}
		}

		/// <summary>
		/// Sets the file name.
		/// </summary>
		/// <param name="fileName">Filename.</param>
		public void SetFileName(string fileName)
		{
			if (fileName == null)
			{
				throw new ArgumentNullException(nameof(fileName));
			}

			if (fileName.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
			{
				fileName = fileName[0..^4];
			}

			if (fileName.EndsWith("_dir", StringComparison.OrdinalIgnoreCase))
			{
				IsDirVPK = true;

				fileName = fileName[0..^4];
			}

			FileName = fileName;
		}

		/// <summary>
		/// Searches for a given file entry in the file list.
		///
		/// If <see cref="OptimizeEntriesForBinarySearch"/> was called on this package, this method will use <see cref="List{T}.BinarySearch(T, IComparer{T})"/>.
		/// Optimized packages also support case insensitive search by using a different <see cref="StringComparison"/>.
		/// </summary>
		/// <param name="filePath">Full path to the file to find.</param>
		public PackageEntry FindEntry(string filePath)
		{
			if (filePath == null)
			{
				throw new ArgumentNullException(nameof(filePath));
			}

			if (Comparer != null)
			{
				return FindEntryWithBinarySearch(filePath);
			}

			filePath = filePath.Replace('\\', DirectorySeparatorChar);

			var lastSeparator = filePath.LastIndexOf(DirectorySeparatorChar);
			var directory = lastSeparator > -1 ? filePath[..lastSeparator] : string.Empty;
			var fileName = filePath[(lastSeparator + 1)..];

#pragma warning disable CS0618 // Type or member is obsolete
			return FindEntry(directory, fileName);
#pragma warning restore CS0618
		}

		/// <summary>
		/// Searches for a given file entry in the file list.
		/// </summary>
		/// <param name="directory">Directory to search in.</param>
		/// <param name="fileName">File name to find.</param>
		[Obsolete("Use FindEntry(string filePath) overload instead, this method will be removed in the future.")]
		public PackageEntry FindEntry(string directory, string fileName)
		{
			if (directory == null)
			{
				throw new ArgumentNullException(nameof(directory));
			}

			if (fileName == null)
			{
				throw new ArgumentNullException(nameof(fileName));
			}

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

			return FindEntry(directory, fileName, extension);
		}

		/// <summary>
		/// Searches for a given file entry in the file list.
		/// </summary>
		/// <param name="directory">Directory to search in.</param>
		/// <param name="fileName">File name to find, without the extension.</param>
		/// <param name="extension">File extension, without the leading dot.</param>
		[Obsolete("Use FindEntry(string filePath) overload instead, this method will be removed in the future.")]
		public PackageEntry FindEntry(string directory, string fileName, string extension)
		{
			if (directory == null)
			{
				throw new ArgumentNullException(nameof(directory));
			}

			if (fileName == null)
			{
				throw new ArgumentNullException(nameof(fileName));
			}

			if (extension == null)
			{
				throw new ArgumentNullException(nameof(extension));
			}

			if (Entries == null || !Entries.TryGetValue(extension, out var entriesForExtension))
			{
				return null;
			}

			// We normalize path separators when reading the file list
			// And remove the trailing slash
			directory = directory.Replace('\\', DirectorySeparatorChar).Trim(DirectorySeparatorChar);

			// If the directory is empty after trimming, set it to a space to match Valve's behaviour
			if (directory.Length == 0)
			{
				directory = " ";
			}

			return entriesForExtension.Find(x => x.DirectoryName == directory && x.FileName == fileName);
		}

		/// <summary>
		/// Searches for a given file entry in the file list after it has been optimized with <see cref="OptimizeEntriesForBinarySearch"/>.
		/// This also supports case insensitive search by using a different <see cref="StringComparison"/>.
		/// </summary>
		/// <param name="filePath">Full path to the file to find.</param>
		private PackageEntry FindEntryWithBinarySearch(string filePath)
		{
			filePath = filePath.Replace('\\', DirectorySeparatorChar);

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

			if (Entries == null || !Entries.TryGetValue(extension, out var entriesForExtension))
			{
				return null;
			}

			// We normalize path separators when reading the file list
			// And remove the trailing slash
			directory = directory.Trim(DirectorySeparatorChar);

			// If the directory is empty after trimming, set it to a space to match Valve's behaviour
			if (directory.Length == 0)
			{
				directory = " ";
			}

			var searchEntry = new PackageEntry
			{
				DirectoryName = directory,
				FileName = fileName,
				TypeName = extension,
			};

			var index = entriesForExtension.BinarySearch(searchEntry, Comparer);

			return index < 0 ? null : entriesForExtension[index];
		}

		/// <summary>
		/// This sorts <see cref="Entries"/> so that it can be searched through using binary search.
		/// Use <see cref="StringComparison.OrdinalIgnoreCase"/> if you want <see cref="FindEntryWithBinarySearch"/> to search case insensitively.
		/// </summary>
		/// <remarks>
		/// This is experimental and may be removed in a future release.
		/// </remarks>
		/// <param name="comparison">Comparison method to use.</param>
		public void OptimizeEntriesForBinarySearch(StringComparison comparison = StringComparison.Ordinal)
		{
			if (Entries != null)
			{
				throw new InvalidOperationException("This method must be called before a package is read.");
			}

			Comparer = new CaseInsensitivePackageEntryComparer(comparison);
		}
	}
}
