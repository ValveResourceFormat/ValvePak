using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace SteamDatabase.ValvePak
{
	/// <summary>
	/// VPK (Valve Pak) files are uncompressed archives used to package game content.
	/// </summary>
	public partial class Package : IDisposable
	{
		/// <summary>
		/// The VPK file signature magic number.
		/// </summary>
		public const int MAGIC = 0x55AA1234;

		internal const string Space = " ";
		internal const string Dot = ".";
		internal const string DirectorySeparator = "/";

		/// <summary>
		/// Always '/' as per Valve's vpk implementation.
		/// </summary>
		public const char DirectorySeparatorChar = '/';
		private const char WindowsDirectorySeparator = '\\';

		private BinaryReader? Reader;
		private readonly Dictionary<int, MemoryMappedFile> MemoryMappedPaks = [];

		/// <summary>
		/// Gets the file name.
		/// </summary>
		public string? FileName { get; private set; }

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
		public byte[]? TreeChecksum { get; private set; }

		/// <summary>
		/// Gets the MD5 checksum of the archive MD5 checksum section entries.
		/// </summary>
		public byte[]? ArchiveMD5EntriesChecksum { get; private set; }

		/// <summary>
		/// Gets the MD5 checksum of the complete package until the signature structure.
		/// </summary>
		public byte[]? WholeFileChecksum { get; private set; }

		/// <summary>
		/// Gets the public key.
		/// </summary>
		public byte[]? PublicKey { get; private set; }

		/// <summary>
		/// Gets the signature.
		/// </summary>
		public byte[]? Signature { get; private set; }

		/// <summary>
		/// Gets the package entries.
		/// </summary>
		public Dictionary<string, List<PackageEntry>>? Entries { get; private set; }

		/// <summary>
		/// Gets the archive MD5 checksum section entries. Also known as cache line hashes.
		/// </summary>
		public List<ChunkHashFraction> AccessPackFileHashes { get; private set; } = [];

		private CaseInsensitivePackageEntryComparer? Comparer;

		/// <summary>
		/// Releases binary reader.
		/// </summary>
		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		/// <summary>
		/// Releases binary reader.
		/// </summary>
		protected virtual void Dispose(bool disposing)
		{
			if (disposing)
			{
				if (Reader != null)
				{
					Reader.Dispose();
					Reader = null;
				}

				foreach (var stream in MemoryMappedPaks.Values)
				{
					stream.Dispose();
				}

				MemoryMappedPaks.Clear();
			}
		}

		/// <summary>
		/// Sets the file name.
		/// </summary>
		/// <param name="fileName">Filename.</param>
		public void SetFileName(string fileName)
		{
			ArgumentNullException.ThrowIfNull(fileName);

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
		/// <remarks>
		/// Normalizes the directory path separator from <see cref="WindowsDirectorySeparator"/> (\) to <see cref="DirectorySeparator"/> (/) in <paramref name="filePath"/>.
		/// </remarks>
		public PackageEntry? FindEntry(string filePath)
		{
			ArgumentNullException.ThrowIfNull(filePath);

			// Normalize path separators when reading the file list
			var filePathSpan = filePath.Replace(WindowsDirectorySeparator, DirectorySeparatorChar).AsSpan();

			return FindEntry(filePathSpan);
		}

		/// <summary>
		/// Searches for a given file entry in the file list.
		///
		/// If <see cref="OptimizeEntriesForBinarySearch"/> was called on this package, this method will use <see cref="List{T}.BinarySearch(T, IComparer{T})"/>.
		/// Optimized packages also support case insensitive search by using a different <see cref="StringComparison"/>.
		/// </summary>
		/// <param name="filePath">Full path to the file to find.</param>
		/// <remarks>
		/// Unlike the <see cref="FindEntry(string)"/> version, this one does not normalize the directory path separator.
		/// </remarks>
		public PackageEntry? FindEntry(ReadOnlySpan<char> filePath)
		{
			var lastSeparator = filePath.LastIndexOf(DirectorySeparatorChar);
			var directory = lastSeparator > -1 ? filePath[..lastSeparator] : string.Empty;
			var fileName = filePath[(lastSeparator + 1)..];

			var dot = fileName.LastIndexOf('.');
			string extension;

			if (dot > -1)
			{
				extension = fileName[(dot + 1)..].ToString();
				fileName = fileName[..dot];
			}
			else
			{
				// Valve uses a space for missing extensions
				extension = Space;
			}

			if (Entries == null || !Entries.TryGetValue(extension, out var entriesForExtension))
			{
				return default;
			}

			// Remove the trailing and leading slash
			directory = directory.Trim(DirectorySeparatorChar);

			// If the directory is empty after trimming, set it to a space to match Valve's behaviour
			if (directory.Length == 0)
			{
				directory = Space;
			}

			int hi = entriesForExtension.Count - 1;

			if (Comparer == null)
			{
				for (var i = 0; i <= hi; i++) // Don't use foreach
				{
					var entry = entriesForExtension[i];
					if (directory.SequenceEqual(entry.DirectoryName) && fileName.SequenceEqual(entry.FileName))
					{
						return entry;
					}
				}

				return default;
			}

			// Searches for a given file entry in the file list after it has been optimized with <see cref="OptimizeEntriesForBinarySearch"/>.
			// This also supports case insensitive search by using a different <see cref="StringComparison"/>.
			//
			// Manually implement binary search to avoid allocating new strings for file and directory names.
			// See <see cref="MemoryExtensions.BinarySearch{T}(ReadOnlySpan{T}, IComparable{T})"/> for reference.

			int lo = 0;

			while (lo <= hi)
			{
				var i = (int)(((uint)hi + (uint)lo) >> 1);
				var entry = entriesForExtension[i];

				// This code must match <see cref="CaseInsensitivePackageEntryComparer.Compare(PackageEntry, PackageEntry)"/>
				var comp = fileName.Length.CompareTo(entry.FileName.Length);

				if (comp == 0)
				{
					comp = directory.Length.CompareTo(entry.DirectoryName.Length);

					if (comp == 0)
					{
						comp = fileName.CompareTo(entry.FileName, Comparer.Comparison);

						if (comp == 0)
						{
							comp = directory.CompareTo(entry.DirectoryName, Comparer.Comparison);
						}
					}
				}

				if (comp == 0)
				{
					return entry;
				}
				else if (comp > 0)
				{
					lo = i + 1;
				}
				else
				{
					hi = i - 1;
				}
			}

			return default;
		}

		/// <summary>
		/// This sorts <see cref="Entries"/> so that it can be searched through using binary search.
		/// Use <see cref="StringComparison.OrdinalIgnoreCase"/> if you want <see cref="FindEntry(string)"/> to search case insensitively.
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
