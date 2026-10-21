using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
		internal const string DotDot = "..";
		internal const string DirectorySeparator = "/";

		/// <summary>
		/// Archive index of entries whose data is in the directory file instead of a chunk file.
		/// </summary>
		public const ushort DirectoryArchiveIndex = 0x7FFF;

		/// <summary>
		/// Maximum size in bytes of the file data in a directory file.
		/// </summary>
		public const int MaxDirectoryFileDataSize = int.MaxValue;

		/// <summary>
		/// Maximum size in bytes of a chunk file.
		/// </summary>
		public const uint MaxChunkFileSize = uint.MaxValue;

		/// <summary>
		/// Maximum number of preload bytes (<see cref="PackageEntry.SmallData"/>) of an entry.
		/// </summary>
		public const ushort MaxPreloadSize = ushort.MaxValue;

		/// <summary>
		/// Maximum length in UTF-8 bytes of a <see cref="PackageEntry.TypeName"/>.
		/// </summary>
		public const int MaxTypeNameLength = 128;

		/// <summary>
		/// Maximum length in UTF-8 bytes of a <see cref="PackageEntry.FileName"/>.
		/// </summary>
		public const int MaxFileNameLength = 260;

		private const int MaxStackPathLength = 256;

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

		private string? ReadFilePath;
		private bool IsReadFromAnotherPackage;

		private readonly ConcurrentDictionary<ushort, Archive> Archives = new();
		private readonly Lock ArchivesLock = new();

		/// <summary>
		/// Gets the file name.
		/// </summary>
		public string? FileName { get; private set; }

		/// <summary>
		/// Gets the full path of the directory file that this package reads from, or null if it does not read from the start of a file.
		/// </summary>
		public string? FilePath => StreamOffset == 0 ? ReadFilePath : null;

		/// <summary>
		/// Gets whether this package had "_dir" in the name, indicating it has multiple chunk files.
		/// </summary>
		public bool IsDirVPK { get; private set; }

		/// <summary>
		/// Gets whether entries or chunk hashes of this package refer to chunk files, or files were added to chunk files.
		/// Unlike <see cref="IsDirVPK"/>, this does not depend on the file name.
		/// </summary>
		public bool HasChunkFiles => HighestChunkIndex >= 0;

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
			if (Reader != null || IsReadPackage)
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

		private void ThrowIfDirectoryFileClosed()
		{
			if (Reader != null)
			{
				return;
			}

			throw new InvalidOperationException(ReadFilePath == null
				? "The package has not been read."
				: $"The directory file '{ReadFilePath}' could not be opened again after writing, so data in it can not be read.");
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

			if (HighestChunkIndex >= 0)
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
		/// Searches for the entry at the given path, which is normalized like <see cref="TryParseEntryPath"/> does.
		///
		/// If <see cref="OptimizeEntriesForBinarySearch"/> was called on this package, this method uses a binary search.
		/// Optimized packages also support case insensitive search by using a different <see cref="StringComparison"/>.
		/// </summary>
		/// <param name="filePath">Full path to the file to find.</param>
		/// <returns>The entry, or null if there is none or the path is rejected.</returns>
		public PackageEntry? FindEntry(string filePath)
		{
			ArgumentNullException.ThrowIfNull(filePath);

			return FindEntry(filePath.AsSpan());
		}

		/// <inheritdoc cref="FindEntry(string)"/>
		public PackageEntry? FindEntry(ReadOnlySpan<char> filePath)
		{
			Span<char> buffer = filePath.Length <= MaxStackPathLength || IsNormalizedPath(filePath) ? stackalloc char[MaxStackPathLength] : new char[filePath.Length];

			if (SplitEntryPath(filePath, buffer, out var directory, out var fileName, out var typeName) != null)
			{
				return null;
			}

			return FindEntry(directory, fileName, typeName);
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
		/// Normalizes a file path to the path that <see cref="AddFile(string, byte[], bool)"/> stores it at: '\' is a directory separator,
		/// empty and "." segments are removed, ".." removes the segment before it, ASCII letters are lowercased, and the type name is split from the file name at its last dot,
		/// where a type name of a single space means the file has no extension.
		/// </summary>
		/// <param name="filePath">Full file path.</param>
		/// <param name="normalizedPath">The normalized path, or null if the path is rejected.</param>
		/// <returns>True if a file can be stored at the path, false if it does not end with a file name, has a ".." above the root,
		/// a null character or an unpaired surrogate, or has a name longer than <see cref="MaxFileNameLength"/> or <see cref="MaxTypeNameLength"/>.</returns>
		public static bool TryParseEntryPath(string filePath, [NotNullWhen(true)] out string? normalizedPath)
		{
			ArgumentNullException.ThrowIfNull(filePath);

			Span<char> buffer = filePath.Length <= MaxStackPathLength ? stackalloc char[MaxStackPathLength] : new char[filePath.Length];

			if (GetEntryPathError(filePath, buffer, out var directory, out var fileName, out var typeName) != null)
			{
				normalizedPath = null;
				return false;
			}

			normalizedPath = JoinEntryPath(directory, fileName, typeName);
			return true;
		}

		/// <summary>
		/// Normalizes a directory path to the <see cref="PackageEntry.DirectoryName"/> of the files in it, like <see cref="TryParseEntryPath"/> normalizes directories.
		/// </summary>
		/// <param name="directoryPath">Directory path, which may end with a directory separator.</param>
		/// <returns>The normalized path, or an empty string for the root directory or if the path has a ".." above the root, a null character or an unpaired surrogate.</returns>
		public static string NormalizeDirectoryPath(string directoryPath)
		{
			ArgumentNullException.ThrowIfNull(directoryPath);

			GetDirectoryPathError(directoryPath, out var directory);

			return directory;
		}

		/// <summary>
		/// Normalizes a directory path like <see cref="TryParseEntryPath"/> normalizes directories, to an empty string for the root or a rejected path.
		/// </summary>
		/// <returns>Why the path can not be a directory, or null.</returns>
		private static string? GetDirectoryPathError(string directoryPath, out string directory)
		{
			var length = directoryPath.Length + 2;
			Span<char> buffer = length <= MaxStackPathLength ? stackalloc char[MaxStackPathLength * 2] : new char[length * 2];
			var filePath = buffer[..length];

			directoryPath.CopyTo(filePath);
			filePath[^2] = DirectorySeparatorChar;
			filePath[^1] = 'x';

			var error = SplitEntryPath(filePath, buffer[length..], out var directorySpan, out _, out _);

			if (error != null || directorySpan is Space)
			{
				directory = string.Empty;
				return error;
			}

			directory = directorySpan.SequenceEqual(directoryPath) ? directoryPath : directorySpan.ToString();
			return null;
		}

		/// <summary>
		/// Splits a file path like <see cref="SplitEntryPath"/> does and checks the lengths of the names.
		/// </summary>
		/// <returns>Why the path can not be stored, or null.</returns>
		private static string? GetEntryPathError(ReadOnlySpan<char> path, Span<char> buffer, out ReadOnlySpan<char> directory, out ReadOnlySpan<char> fileName, out ReadOnlySpan<char> typeName)
		{
			var error = SplitEntryPath(path, buffer, out directory, out fileName, out typeName);

			if (error != null)
			{
				return error;
			}

			if (Encoding.UTF8.GetByteCount(typeName) > MaxTypeNameLength)
			{
				return "has a type name longer than 128 bytes in UTF-8";
			}

			if (Encoding.UTF8.GetByteCount(fileName) > MaxFileNameLength)
			{
				return "has a file name longer than 260 bytes in UTF-8";
			}

			return null;
		}

		/// <summary>
		/// Normalizes a file path like <see cref="TryParseEntryPath"/> does and splits it into the directory, file and type names it is stored as.
		/// A path that normalizing changes is written into the buffer, which must be at least as long as the path.
		/// </summary>
		/// <returns>Why no file can be at the path, or null.</returns>
		private static string? SplitEntryPath(ReadOnlySpan<char> path, Span<char> buffer, out ReadOnlySpan<char> directory, out ReadOnlySpan<char> fileName, out ReadOnlySpan<char> typeName)
		{
			directory = fileName = typeName = default;

			if (path.IsEmpty || IsDirectorySeparator(path[^1]) || path[(path.LastIndexOfAny(DirectorySeparatorChar, WindowsDirectorySeparator) + 1)..] is Dot or DotDot)
			{
				return "does not end with a file name";
			}

			if (HasInvalidCharacter(path))
			{
				return "has a null character or an unpaired surrogate";
			}

			var normalizedPath = path;

			if (!IsNormalizedPath(path))
			{
				var length = ResolveSegments(path, buffer);

				if (length < 0)
				{
					return "has a \"..\" segment above the root";
				}

				normalizedPath = buffer[..length];
			}

			var lastSeparator = normalizedPath.LastIndexOf(DirectorySeparatorChar);
			var file = normalizedPath[(lastSeparator + 1)..];
			var dot = file.LastIndexOf('.');

			directory = lastSeparator > -1 ? normalizedPath[..lastSeparator] : Space;
			fileName = dot > -1 ? file[..dot] : file;
			typeName = dot > -1 ? file[(dot + 1)..] : Space;

			if (dot == 0 || dot == file.Length - 1 || (typeName is Space && fileName is Dot or DotDot))
			{
				directory = fileName = typeName = default;
				return "has a file name that is empty or ends with a dot";
			}

			return null;
		}

		/// <summary>
		/// Returns whether the path has a null character, which ends a name in the directory tree, or a surrogate that is not part of a pair, which UTF-8 can not encode.
		/// </summary>
		private static bool HasInvalidCharacter(ReadOnlySpan<char> path)
		{
			if (path.Contains('\0'))
			{
				return true;
			}

			int i;

			while ((i = path.IndexOfAnyInRange('\uD800', '\uDFFF')) >= 0)
			{
				if (!char.IsHighSurrogate(path[i]) || i + 1 >= path.Length || !char.IsLowSurrogate(path[i + 1]))
				{
					return true;
				}

				path = path[(i + 2)..];
			}

			return false;
		}

		/// <summary>
		/// Joins the segments of the path with '/' into the buffer and lowercases ASCII letters, leaving out empty and "." segments and removing the segment before each "..".
		/// </summary>
		/// <returns>The length of the resolved path, or -1 if a ".." has no segment before it.</returns>
		private static int ResolveSegments(ReadOnlySpan<char> path, Span<char> buffer)
		{
			var length = 0;

			foreach (var range in path.SplitAny(DirectorySeparatorChar, WindowsDirectorySeparator))
			{
				var segment = path[range];

				if (segment.IsEmpty || segment is Dot)
				{
					continue;
				}

				if (segment is DotDot)
				{
					if (length == 0)
					{
						return -1;
					}

					length = Math.Max(buffer[..length].LastIndexOf(DirectorySeparatorChar), 0);
					continue;
				}

				if (length > 0)
				{
					buffer[length++] = DirectorySeparatorChar;
				}

				foreach (var c in segment)
				{
					buffer[length++] = ToLowerAscii(c);
				}
			}

			return length;
		}

		/// <summary>
		/// Returns whether normalizing leaves the path as it is, because it has no '\', no upper case ASCII letters and no empty, "." or ".." segments.
		/// </summary>
		private static bool IsNormalizedPath(ReadOnlySpan<char> path)
		{
			if (path.Contains(WindowsDirectorySeparator) || path.ContainsAnyInRange('A', 'Z'))
			{
				return false;
			}

			foreach (var range in path.Split(DirectorySeparatorChar))
			{
				if (path[range] is "" or Dot or DotDot)
				{
					return false;
				}
			}

			return true;
		}

		/// <summary>
		/// Returns whether the path of a file has the type name after a dot, which is left out only for a file without an extension and without a dot in its name.
		/// </summary>
		internal static bool IsTypeNameInPath(ReadOnlySpan<char> fileName, ReadOnlySpan<char> typeName)
		{
			return typeName is not Space || fileName.Contains('.');
		}

		private static string JoinEntryPath(ReadOnlySpan<char> directory, ReadOnlySpan<char> fileName, ReadOnlySpan<char> typeName)
		{
			var file = IsTypeNameInPath(fileName, typeName) ? string.Concat(fileName, Dot, typeName) : fileName.ToString();

			return directory is Space ? file : string.Concat(directory, DirectorySeparator, file);
		}

		private static bool IsDirectorySeparator(char c)
		{
			return c is DirectorySeparatorChar or WindowsDirectorySeparator;
		}

		/// <summary>
		/// This sorts <see cref="Entries"/> so that it can be searched through using binary search.
		/// Use <see cref="StringComparison.OrdinalIgnoreCase"/> if you want <see cref="FindEntry(string)"/> to search case insensitively.
		/// </summary>
		/// <param name="comparison">Comparison method to use.</param>
		public void OptimizeEntriesForBinarySearch(StringComparison comparison = StringComparison.Ordinal)
		{
			if (Entries != null)
			{
				throw new InvalidOperationException("This method must be called before a package is read.");
			}

			Comparer = new CaseInsensitivePackageEntryComparer(comparison);
		}

		private static readonly AsciiCaseInsensitiveComparer AsciiComparer = new();

		/// <summary>
		/// Gets the comparer that compares paths and names ordinally with only ASCII letters case insensitive, which also looks up <see cref="ReadOnlySpan{T}"/> keys in dictionaries.
		/// </summary>
		public static StringComparer PathComparer => AsciiComparer;

		private sealed class AsciiCaseInsensitiveComparer : StringComparer, IAlternateEqualityComparer<ReadOnlySpan<char>, string>
		{
			public override int Compare(string? x, string? y)
			{
				return x == null || y == null ? string.CompareOrdinal(x, y) : CompareAscii(x, y);
			}

			public override bool Equals(string? x, string? y)
			{
				return x == null || y == null ? x == y : Equals(x.AsSpan(), y);
			}

			public bool Equals(ReadOnlySpan<char> alternate, string other)
			{
				return alternate.Length == other.Length && CompareAscii(alternate, other) == 0;
			}

			public override int GetHashCode(string obj)
			{
				return GetHashCode(obj.AsSpan());
			}

			public int GetHashCode(ReadOnlySpan<char> alternate)
			{
				return string.GetHashCode(alternate, StringComparison.OrdinalIgnoreCase);
			}

			public string Create(ReadOnlySpan<char> alternate)
			{
				return alternate.ToString();
			}

			private static int CompareAscii(ReadOnlySpan<char> x, ReadOnlySpan<char> y)
			{
				var length = Math.Min(x.Length, y.Length);

				for (var i = 0; i < length; i++)
				{
					var comparison = ToLowerAscii(x[i]).CompareTo(ToLowerAscii(y[i]));

					if (comparison != 0)
					{
						return comparison;
					}
				}

				return x.Length.CompareTo(y.Length);
			}
		}
	}
}
