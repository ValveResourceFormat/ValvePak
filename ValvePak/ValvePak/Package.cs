using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace ValvePak
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

		/// <summary>
		/// Position in the read stream where the package starts.
		/// </summary>
		private long StreamOffset;

		private readonly ConcurrentDictionary<ushort, Archive> Archives = new();
		private readonly Lock ArchivesLock = new();

		/// <summary>
		/// Gets the file name.
		/// </summary>
		public string? FileName { get; private set; }

		/// <summary>
		/// Gets whether this package had "_dir" in the name, indicating it has multiple chunk files.
		/// </summary>
		public bool IsDirVPK { get; private set; }

		/// <summary>
		/// Gets or sets the VPK version. This version is also used when writing the package.
		///
		/// Only versions 1 and 2 can be set. Version 1 does not contain MD5 checksums, and is used by older games such as Left 4 Dead 2.
		/// </summary>
		public uint Version
		{
			get;
			set
			{
				if (value is not (1 or 2))
				{
					throw new ArgumentOutOfRangeException(nameof(value), value, "Only versions 1 and 2 are supported.");
				}

				field = value;
			}
		} = 2;

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
		/// Gets whether the package has both a public key and a signature.
		/// </summary>
		public bool IsSigned => PublicKey != null && Signature != null;

		/// <summary>
		/// Gets the signature type.
		/// </summary>
		public ESignatureType SignatureType { get; private set; } = ESignatureType.Unknown;

		/// <summary>
		/// Gets the package entries.
		/// </summary>
		public Dictionary<string, List<PackageEntry>>? Entries { get; private set; }

		/// <summary>
		/// Gets the archive MD5 checksum section entries. Also known as cache line hashes.
		/// </summary>
		public List<ChunkHashFraction> AccessPackFileHashes { get; private set; } = [];

		private CaseInsensitivePackageEntryComparer? Comparer;
		private bool IsDisposed;

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
				CurrentChunkFile?.Dispose();
				CurrentChunkFile = null;

				lock (ArchivesLock)
				{
					IsDisposed = true;

					foreach (var archive in Archives.Values)
					{
						archive.Dispose();
					}

					Archives.Clear();
				}

				if (Reader != null)
				{
					Reader.Dispose();
					Reader = null;
				}
			}
		}

		private void ThrowIfAlreadyRead()
		{
			if (Reader != null)
			{
				throw new InvalidOperationException("This package has already been read, create a new instance to read another package.");
			}
		}

		private void ThrowIfFilesAdded()
		{
			if (Reader == null && Entries != null)
			{
				throw new InvalidOperationException("Files were added to this package, create a new instance to read a package.");
			}
		}

		private Dictionary<string, List<PackageEntry>> CreateEntriesDictionary()
		{
			var stringComparer = Comparer == null ? null : StringComparer.FromComparison(Comparer.Comparison);

			return new Dictionary<string, List<PackageEntry>>(stringComparer);
		}

		/// <summary>
		/// Sets the file name, which chunk file names are built from. This must be called before the package is read,
		/// or before files are added with multi chunk enabled when creating a package.
		/// </summary>
		/// <param name="fileName">Filename.</param>
		public void SetFileName(string fileName)
		{
			ArgumentNullException.ThrowIfNull(fileName);
			ThrowIfAlreadyRead();

			if (CurrentChunkFile != null)
			{
				throw new InvalidOperationException("The file name can not be changed after files were added to chunk files.");
			}

			FileName = StripDirVpkSuffixes(fileName, out var isDirVpk);
			IsDirVPK = isDirVpk;
		}

		/// <summary>
		/// Strips the ".vpk" extension and the "_dir" suffix to produce the base name that chunk file names are built from.
		/// </summary>
		private static string StripDirVpkSuffixes(string fileName, out bool isDirVpk)
		{
			if (fileName.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
			{
				fileName = fileName[0..^4];
			}

			isDirVpk = fileName.EndsWith("_dir", StringComparison.OrdinalIgnoreCase);

			if (isDirVpk)
			{
				fileName = fileName[0..^4];
			}

			return fileName;
		}

		/// <summary>
		/// Searches for a given file entry in the file list.
		///
		/// If <see cref="OptimizeEntriesForBinarySearch"/> was called on this package, this method will use <see cref="List{T}.BinarySearch(T, IComparer{T})"/>.
		/// Optimized packages also support case insensitive search by using a different <see cref="StringComparison"/>.
		/// </summary>
		/// <param name="filePath">Full path to the file to find.</param>
		/// <remarks>
		/// The path is normalized the same way as paths of added files, see <see cref="FindEntry(ReadOnlySpan{char})"/>.
		/// </remarks>
		public PackageEntry? FindEntry(string filePath)
		{
			ArgumentNullException.ThrowIfNull(filePath);

			return FindEntry(filePath.AsSpan());
		}

		/// <summary>
		/// Searches for a given file entry in the file list.
		///
		/// If <see cref="OptimizeEntriesForBinarySearch"/> was called on this package, this method will use <see cref="List{T}.BinarySearch(T, IComparer{T})"/>.
		/// Optimized packages also support case insensitive search by using a different <see cref="StringComparison"/>.
		/// </summary>
		/// <param name="filePath">Full path to the file to find.</param>
		/// <remarks>
		/// The path is normalized the same way as paths of added files: <see cref="WindowsDirectorySeparator"/> (\) becomes <see cref="DirectorySeparator"/> (/),
		/// empty and "." segments and trailing dots are removed, and a missing directory or extension is a space.
		/// If that finds no entry, the path is also looked up as it is, split at its last <see cref="DirectorySeparator"/> and at each dot of the file name,
		/// which finds entries of read packages whose names normalizing changes.
		/// The path is not lowercased like added paths are, so the search is case sensitive unless
		/// <see cref="OptimizeEntriesForBinarySearch"/> was called with a case insensitive <see cref="StringComparison"/>.
		/// </remarks>
		public PackageEntry? FindEntry(ReadOnlySpan<char> filePath)
		{
			SplitPath(NormalizePath(filePath, out var changed), out var directory, out var fileName, out var extension);

			return FindEntry(directory, fileName, extension) ?? FindEntryByStoredName(filePath, changed ? -1 : fileName.Length);
		}

		/// <summary>
		/// Finds the entry whose <see cref="PackageEntry.GetFullPath"/> is the given path, trying each split of the file name into a file and type name.
		/// </summary>
		/// <param name="path">The path as it was given.</param>
		/// <param name="skipSplit">Position in the file name that was already looked up, where the length of the file name means without a type, or -1.</param>
		private PackageEntry? FindEntryByStoredName(ReadOnlySpan<char> path, int skipSplit)
		{
			var lastSeparator = path.LastIndexOf(DirectorySeparatorChar);
			var directory = lastSeparator > -1 ? path[..lastSeparator] : Space;
			var name = path[(lastSeparator + 1)..];
			PackageEntry? entry = null;

			for (var split = 1; entry == null && split <= name.Length; split++)
			{
				if (split == skipSplit)
				{
					continue;
				}

				if (split == name.Length)
				{
					entry = FindEntry(directory, name, Space);
				}
				else if (name[split] == '.' && split < name.Length - 1)
				{
					entry = FindEntry(directory, name[..split], name[(split + 1)..]);
				}
			}

			return entry;
		}

		private PackageEntry? FindEntry(ReadOnlySpan<char> directory, ReadOnlySpan<char> fileName, ReadOnlySpan<char> extension)
		{
			if (Entries == null || !Entries.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(extension, out var entriesForExtension))
			{
				return default;
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
		/// Replaces <see cref="WindowsDirectorySeparator"/> with <see cref="DirectorySeparatorChar"/>, removes empty and "." segments, and removes trailing dots.
		/// </summary>
		private static ReadOnlySpan<char> NormalizePath(ReadOnlySpan<char> path, out bool changed)
		{
			changed = !IsNormalizedPath(path);

			if (!changed)
			{
				return path;
			}

			var builder = new StringBuilder(path.Length);

			foreach (var range in path.SplitAny(DirectorySeparatorChar, WindowsDirectorySeparator))
			{
				var segment = path[range];

				if (IsSkippedSegment(segment))
				{
					continue;
				}

				if (builder.Length > 0)
				{
					builder.Append(DirectorySeparatorChar);
				}

				builder.Append(segment);
			}

			var length = builder.Length;

			while (length > 0 && builder[length - 1] == '.')
			{
				length--;
			}

			return builder.ToString(0, length);
		}

		private static bool IsNormalizedPath(ReadOnlySpan<char> path)
		{
			if (path.Contains(WindowsDirectorySeparator))
			{
				return false;
			}

			foreach (var range in path.Split(DirectorySeparatorChar))
			{
				if (IsSkippedSegment(path[range]))
				{
					return false;
				}
			}

			return !path.EndsWith('.');
		}

		private static bool IsSkippedSegment(ReadOnlySpan<char> segment)
		{
			return segment.IsEmpty || segment is Dot;
		}

		/// <summary>
		/// Splits a normalized path into the directory, file and type names of an entry, using a space for a missing directory or extension.
		/// </summary>
		private static void SplitPath(ReadOnlySpan<char> path, out ReadOnlySpan<char> directory, out ReadOnlySpan<char> fileName, out ReadOnlySpan<char> extension)
		{
			var lastSeparator = path.LastIndexOf(DirectorySeparatorChar);
			directory = lastSeparator > -1 ? path[..lastSeparator] : Space;
			fileName = path[(lastSeparator + 1)..];

			var dot = fileName.LastIndexOf('.');
			extension = Space;

			if (dot > -1)
			{
				extension = fileName[(dot + 1)..];
				fileName = fileName[..dot];
			}
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
