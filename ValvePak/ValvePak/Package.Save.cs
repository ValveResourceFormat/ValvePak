using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ValvePak
{
	public partial class Package
	{
		/// <summary>
		/// Gets or sets the size in bytes after which files added with multi chunk enabled (see <see cref="AddFile(string, byte[], bool)"/>)
		/// or repacked (see <see cref="Write(string, bool)"/>) go into a new chunk file.
		/// A new chunk file is started once the current one is larger than this size, so a chunk file can end past it by up to the size of its last file.
		/// Defaults to 100 MiB.
		/// </summary>
		public int WriteChunkSize
		{
			get;
			set
			{
				ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);

				field = value;
			}
		} = 100 * 1024 * 1024;

		/// <summary>
		/// Size in bytes of the buffer that file data is copied through.
		/// </summary>
		private const int CopyBufferSize = 1024 * 1024;

		private const int UnsignedSignatureSectionSize = 20;

		/// <summary>
		/// The chunk file that files added with multi chunk enabled are currently written to.
		/// </summary>
		private ChunkFileWriter? CurrentChunkFile;

		/// <summary>
		/// Hashes of chunk files that this package wrote or hashed again, which take precedence over <see cref="AccessPackFileHashes"/>.
		/// </summary>
		private readonly Dictionary<ushort, List<ChunkHashFraction>> UpdatedChunkHashFractions = [];

		private string? ChunkBaseName;

		/// <summary>
		/// Highest index of the chunk files that the package refers to or created, or -1 if there are none.
		/// </summary>
		private int HighestChunkIndex = -1;

		private bool IsReadPackage;

		/// <summary>
		/// Remove file from current package.
		/// </summary>
		/// <param name="entry">The package entry to remove.</param>
		/// <returns>Returns true if entry was removed, false otherwise.</returns>
		public bool RemoveFile(PackageEntry entry)
		{
			ArgumentNullException.ThrowIfNull(entry);
			ObjectDisposedException.ThrowIf(IsDisposed, this);

			if (Entries == null)
			{
				return false;
			}

			if (!Entries.TryGetValue(entry.TypeName, out var typeEntries))
			{
				return false;
			}

			var removed = typeEntries.Remove(entry);

			if (typeEntries.Count == 0)
			{
				Entries.Remove(entry.TypeName);
			}

			return removed;
		}

		/// <summary>
		/// Moves an entry in current package to a new path. Only the directory tree changes, the file data is not touched.
		/// Be careful to not move it onto the path of another entry, because this does not check for duplicates.
		/// The path is normalized like <see cref="NormalizeEntryPath(string)"/> does.
		/// </summary>
		/// <param name="entry">The package entry to rename.</param>
		/// <param name="newFilePath">Full new file path for this entry.</param>
		/// <exception cref="ArgumentException">The entry is not in this package, or the new path is rejected, see <see cref="NormalizeEntryPath(string)"/>.</exception>
		public void RenameFile(PackageEntry entry, string newFilePath)
		{
			ArgumentNullException.ThrowIfNull(entry);
			ArgumentNullException.ThrowIfNull(newFilePath);
			ObjectDisposedException.ThrowIf(IsDisposed, this);

			var (directoryName, fileName, typeName) = ParseEntryPath(newFilePath, nameof(newFilePath));

			if (!RemoveFile(entry))
			{
				throw new ArgumentException("The entry is not in this package.", nameof(entry));
			}

			(entry.DirectoryName, entry.FileName, entry.TypeName) = (directoryName, fileName, typeName);

			InsertEntry(entry);
		}

		/// <summary>
		/// Removes every entry in a directory and its subdirectories from current package.
		/// </summary>
		/// <param name="directory">The directory as entries store it in <see cref="PackageEntry.DirectoryName"/>, with or without a trailing '/'.</param>
		/// <returns>The removed entries.</returns>
		/// <exception cref="ArgumentException"><paramref name="directory"/> is the root.</exception>
		public IReadOnlyList<PackageEntry> RemoveDirectory(string directory)
		{
			ArgumentNullException.ThrowIfNull(directory);
			ObjectDisposedException.ThrowIf(IsDisposed, this);

			var removed = GetEntriesInDirectory(TrimStoredDirectory(directory, nameof(directory)));

			RemoveEntries(removed);

			return removed;
		}

		/// <summary>
		/// Moves every entry in a directory and its subdirectories into another directory. Only the directory tree changes, the file data is not touched.
		/// Be careful to not move entries onto the paths of other entries, because this does not check for duplicates.
		/// </summary>
		/// <param name="directory">The directory as entries store it, see <see cref="RemoveDirectory(string)"/>.</param>
		/// <param name="newDirectory">The directory to move the entries into, normalized like <see cref="NormalizeDirectoryPath(string)"/> does.</param>
		/// <returns>The moved entries.</returns>
		/// <exception cref="ArgumentException"><paramref name="directory"/> or <paramref name="newDirectory"/> is the root.</exception>
		public IReadOnlyList<PackageEntry> RenameDirectory(string directory, string newDirectory)
		{
			ArgumentNullException.ThrowIfNull(directory);
			ArgumentNullException.ThrowIfNull(newDirectory);
			ObjectDisposedException.ThrowIf(IsDisposed, this);

			var storedDirectory = TrimStoredDirectory(directory, nameof(directory));
			var targetDirectory = NormalizeDirectoryPath(newDirectory);

			if (targetDirectory.Length == 0)
			{
				throw new ArgumentException($"'{newDirectory}' is the root.", nameof(newDirectory));
			}

			var moved = GetEntriesInDirectory(storedDirectory);

			RemoveEntries(moved);

			foreach (var entry in moved)
			{
				entry.DirectoryName = string.Concat(targetDirectory, entry.DirectoryName.AsSpan(storedDirectory.Length));

				InsertEntry(entry);
			}

			return moved;
		}

		private static string TrimStoredDirectory(string directory, string paramName)
		{
			if (directory.EndsWith(DirectorySeparatorChar))
			{
				directory = directory[..^1];
			}

			if (directory is "" or Space)
			{
				throw new ArgumentException("The root directory can not be renamed or removed.", paramName);
			}

			return directory;
		}

		/// <summary>
		/// Returns the entries whose stored directory is the given directory or one of its subdirectories, with ASCII letters case insensitive.
		/// </summary>
		private List<PackageEntry> GetEntriesInDirectory(string directory)
		{
			return [.. GetAllEntries().Where(entry => entry.DirectoryName.Length >= directory.Length
				&& (entry.DirectoryName.Length == directory.Length || entry.DirectoryName[directory.Length] == DirectorySeparatorChar)
				&& AsciiComparer.Equals(entry.DirectoryName.AsSpan(0, directory.Length), directory))];
		}

		private void RemoveEntries(List<PackageEntry> entries)
		{
			var removed = entries.ToHashSet();

			foreach (var typeName in removed.Select(static entry => entry.TypeName).Distinct())
			{
				var typeEntries = Entries![typeName];
				typeEntries.RemoveAll(removed.Contains);

				if (typeEntries.Count == 0)
				{
					Entries.Remove(typeName);
				}
			}
		}

		/// <summary>
		/// Add file to current package. Be careful to not add duplicate entries, because this does not check for duplicates.
		/// The path is normalized like <see cref="NormalizeEntryPath(string)"/> does.
		/// </summary>
		/// <param name="filePath">Full file path for this entry.</param>
		/// <param name="fileData">File data for this entry.</param>
		/// <param name="multiChunk">If true, the data is written right away into a numbered chunk file (such as "example_001.vpk") instead of being kept in memory for the directory file.
		/// <see cref="SetFileName(string)"/> must be called first with the path of the directory file, the chunk files are created next to it.
		/// New chunk files are numbered after the highest chunk file of the package, and a read package also skips numbers that already have a file.
		/// A new chunk file is started whenever the current one is larger than <see cref="WriteChunkSize"/>.
		/// Chunk files stay on disk even if the directory file is never written.</param>
		/// <returns>The added entry.</returns>
		/// <exception cref="ArgumentException">The path is rejected, see <see cref="NormalizeEntryPath(string)"/>.</exception>
		public PackageEntry AddFile(string filePath, byte[] fileData, bool multiChunk = false)
		{
			ArgumentNullException.ThrowIfNull(filePath);
			ArgumentNullException.ThrowIfNull(fileData);
			ObjectDisposedException.ThrowIf(IsDisposed, this);

			var entry = CreateEntry(filePath, nameof(filePath));

			// Files with no data are only ever written into the directory file
			if (!multiChunk || fileData.Length == 0)
			{
				return AddFileToDirectoryFile(entry, fileData);
			}

			var chunkFile = GetChunkFileForNextFile(fileData.Length);
			var offset = chunkFile.Length;
			chunkFile.Write(fileData);

			return AddChunkFileEntry(entry, chunkFile, offset, Crc32.HashToUInt32(fileData));
		}

		/// <summary>
		/// Add file to current package, reading its data from the current position of the stream to its end.
		/// Be careful to not add duplicate entries, because this does not check for duplicates.
		/// The path is normalized like <see cref="NormalizeEntryPath(string)"/> does.
		/// </summary>
		/// <param name="filePath">Full file path for this entry.</param>
		/// <param name="fileData">Stream to read the file data from. It is not disposed.</param>
		/// <param name="multiChunk">If true, the data is copied right away into a numbered chunk file (such as "example_001.vpk") instead of being kept in memory for the directory file.
		/// <see cref="SetFileName(string)"/> must be called first with the path of the directory file, the chunk files are created next to it.
		/// New chunk files are numbered after the highest chunk file of the package, and a read package also skips numbers that already have a file.
		/// A new chunk file is started whenever the current one is larger than <see cref="WriteChunkSize"/>.
		/// Chunk files stay on disk even if the directory file is never written.</param>
		/// <returns>The added entry.</returns>
		/// <exception cref="ArgumentException">The path is rejected, see <see cref="NormalizeEntryPath(string)"/>.</exception>
		public PackageEntry AddFile(string filePath, Stream fileData, bool multiChunk = false)
		{
			ArgumentNullException.ThrowIfNull(filePath);
			ArgumentNullException.ThrowIfNull(fileData);
			ObjectDisposedException.ThrowIf(IsDisposed, this);

			var entry = CreateEntry(filePath, nameof(filePath));

			if (!multiChunk)
			{
				return AddFileToDirectoryFile(entry, ReadToEnd(fileData));
			}

			var knownLength = fileData.CanSeek ? fileData.Length - fileData.Position : 0;

			if (knownLength > MaxChunkFileSize)
			{
				throw new ArgumentException($"The stream has {knownLength} bytes left, a chunk file can hold at most {MaxChunkFileSize}.", nameof(fileData));
			}

			var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);

			try
			{
				var bytesRead = fileData.Read(buffer);

				if (bytesRead == 0)
				{
					// Files with no data are only ever written into the directory file
					return AddFileToDirectoryFile(entry, []);
				}

				var chunkFile = GetChunkFileForNextFile(knownLength);
				var offset = chunkFile.Length;
				var crc = new Crc32();

				do
				{
					var data = buffer.AsSpan(0, bytesRead);
					chunkFile.Write(data);
					crc.Append(data);

					bytesRead = fileData.Read(buffer);
				}
				while (bytesRead > 0);

				return AddChunkFileEntry(entry, chunkFile, offset, crc.GetCurrentHashAsUInt32());
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(buffer);
			}
		}

		/// <summary>
		/// Reads the rest of the stream into an array.
		/// </summary>
		private static byte[] ReadToEnd(Stream stream)
		{
			if (stream.CanSeek)
			{
				var data = new byte[stream.Length - stream.Position];
				stream.ReadExactly(data);

				return data;
			}

			using var memory = new MemoryStream();
			stream.CopyTo(memory);

			return memory.ToArray();
		}

		/// <summary>
		/// Adds an entry whose data is kept in memory until it is written into the directory file.
		/// </summary>
		private PackageEntry AddFileToDirectoryFile(PackageEntry entry, byte[] fileData)
		{
			entry.PendingData = fileData;
			entry.Length = (uint)fileData.Length;
			entry.CRC32 = Crc32.HashToUInt32(fileData);
			entry.ArchiveIndex = DirectoryArchiveIndex;

			InsertEntry(entry);

			return entry;
		}

		/// <summary>
		/// Adds an entry for the data that was written to the chunk file since it was at the given offset.
		/// </summary>
		private PackageEntry AddChunkFileEntry(PackageEntry entry, ChunkFileWriter chunkFile, uint offset, uint crc)
		{
			// Hand the data to the OS so the entry can be read back before the package is written
			chunkFile.Flush();

			entry.CRC32 = crc;
			entry.ArchiveIndex = chunkFile.ArchiveIndex;
			entry.Offset = offset;
			entry.Length = chunkFile.Length - offset;

			InsertEntry(entry);

			return entry;
		}

		/// <summary>
		/// Returns the chunk file that the next file should be written to, starting a new one if the current one is full.
		/// </summary>
		/// <param name="length">Size in bytes of the next file, or 0 if it is not known.</param>
		private ChunkFileWriter GetChunkFileForNextFile(long length)
		{
			if (HasRoomForNextFile(CurrentChunkFile, length))
			{
				return CurrentChunkFile;
			}

			var baseName = GetChunkBaseName();
			var archiveIndex = HighestChunkIndex + 1;

			while (IsReadPackage && archiveIndex < DirectoryArchiveIndex && File.Exists(GetArchiveIndexFullFilePath(baseName, (ushort)archiveIndex)))
			{
				archiveIndex++;
			}

			ThrowIfChunkLimitReached(archiveIndex);

			var chunkFile = new ChunkFileWriter((ushort)archiveIndex, GetArchiveIndexFullFilePath(baseName, (ushort)archiveIndex), IsReadPackage ? FileMode.CreateNew : FileMode.Create);

			FinishCurrentChunkFile();
			CurrentChunkFile = chunkFile;
			HighestChunkIndex = archiveIndex;
			ChunkBaseName = baseName;

			return chunkFile;
		}

		/// <summary>
		/// Returns whether the chunk file is open, not larger than <see cref="WriteChunkSize"/>, and can hold the next file within the 4 GiB that offsets can address,
		/// so that the next file still goes into it.
		/// </summary>
		/// <param name="chunkFile">The chunk file the previous file went into.</param>
		/// <param name="length">Size in bytes of the next file, or 0 if it is not known.</param>
		private bool HasRoomForNextFile([NotNullWhen(true)] ChunkFileWriter? chunkFile, long length)
		{
			return chunkFile != null && chunkFile.Length <= (uint)WriteChunkSize && chunkFile.Length + length <= MaxChunkFileSize;
		}

		private static void ThrowIfChunkLimitReached(int archiveIndex)
		{
			if (archiveIndex > MaxChunkIndex)
			{
				throw new InvalidOperationException("Reached the maximum amount of chunk files (32767).");
			}
		}

		private void FinishCurrentChunkFile()
		{
			if (CurrentChunkFile == null)
			{
				return;
			}

			UpdatedChunkHashFractions[CurrentChunkFile.ArchiveIndex] = CurrentChunkFile.GetHashFractions();
			CurrentChunkFile.Dispose();
			CurrentChunkFile = null;
		}

		private string GetChunkBaseName()
		{
			if (Parent != null)
			{
				throw new NotSupportedException("Chunk files of a package opened from an entry of another package are not supported.");
			}

			if (FileName == null)
			{
				throw new InvalidOperationException("Call SetFileName() with the path of the directory file before adding files with multi chunk enabled, chunk files are created next to it.");
			}

			return ChunkBaseName ?? Path.GetFullPath(FileName);
		}

		/// <summary>
		/// Creates an entry with the directory, file and type names split from the normalized path.
		/// </summary>
		private static PackageEntry CreateEntry(string filePath, string paramName)
		{
			var (directoryName, fileName, typeName) = ParseEntryPath(filePath, paramName);

			return new PackageEntry
			{
				DirectoryName = directoryName,
				FileName = fileName,
				TypeName = typeName,
			};
		}

		/// <summary>
		/// Splits a file path into the directory, file and type names that <see cref="AddFile(string, byte[], bool)"/> stores it with, normalized like <see cref="NormalizeEntryPath(string)"/> does.
		/// </summary>
		/// <param name="filePath">Full file path.</param>
		/// <returns>The names, with a space for a missing directory or type name.</returns>
		/// <exception cref="ArgumentException">The path is rejected.</exception>
		public static (string DirectoryName, string FileName, string TypeName) ParseEntryPath(string filePath)
		{
			ArgumentNullException.ThrowIfNull(filePath);

			return ParseEntryPath(filePath, nameof(filePath));
		}

		private static (string DirectoryName, string FileName, string TypeName) ParseEntryPath(string filePath, string paramName)
		{
			if (GetEntryPathError(filePath, out var directory, out var fileName, out var typeName) is { } error)
			{
				throw new ArgumentException($"'{filePath}' {error}.", paramName);
			}

			return (directory.ToString(), fileName.ToString(), typeName.ToString());
		}

		private static char ToLowerAscii(char c)
		{
			return c is >= 'A' and <= 'Z' ? (char)(c | 0x20) : c;
		}

		/// <summary>
		/// Adds the entry to <see cref="Entries"/>, at its sorted position if the package is optimized for binary search.
		/// </summary>
		private void InsertEntry(PackageEntry entry)
		{
			Entries ??= CreateEntriesDictionary();

			if (!Entries.TryGetValue(entry.TypeName, out var typeEntries))
			{
				typeEntries = [];
				Entries[entry.TypeName] = typeEntries;
			}

			if (Comparer == null)
			{
				typeEntries.Add(entry);
			}
			else
			{
				var index = typeEntries.BinarySearch(entry, Comparer);
				typeEntries.Insert(index < 0 ? ~index : index, entry);
			}
		}

		private IEnumerable<PackageEntry> GetAllEntries()
		{
			return Entries?.Values.SelectMany(static entries => entries) ?? [];
		}

		/// <summary>
		/// Returns the archive indices of the entries with data in chunk files.
		/// </summary>
		private IEnumerable<ushort> GetChunkIndicesWithData()
		{
			return GetAllEntries().Where(IsInChunkFile).Select(static entry => entry.ArchiveIndex);
		}

		private static bool IsInChunkFile(PackageEntry entry)
		{
			return entry.ArchiveIndex != DirectoryArchiveIndex && entry.Length > 0;
		}

		private static bool IsInDirectoryFile(PackageEntry entry)
		{
			return entry.ArchiveIndex == DirectoryArchiveIndex && entry.Length > 0;
		}

		/// <summary>
		/// Opens and writes the given filename.
		///
		/// If the package has chunk files, from being read or from files added with multi chunk enabled (see <see cref="AddFile(string, byte[], bool)"/>),
		/// this only writes the directory file, which refers to the data where it is in the chunk files.
		/// Unless <paramref name="repack"/> is enabled, it must be written to the path the chunk files are named after, which should end with "_dir.vpk".
		/// A package that was not read also deletes existing chunk files numbered after the ones it wrote, until a number has no file.
		/// Hashes of chunk files are kept if they cover the whole file, computed again otherwise, and left out for chunk files that had none.
		///
		/// Writing over the file the package was read from, or repacking, writes temporary files that replace the existing files once all were written,
		/// and the package reads from the written files afterwards. This must not run concurrently with reads from this package,
		/// and streams from <see cref="GetMemoryMappedStreamIfPossible"/> can no longer be read once they read from a replaced file, or from any file after repacking.
		/// Any other file is written in place, and is left unchanged if writing fails before the first byte, or cut off where writing stopped otherwise.
		///
		/// The VPK version that is written is controlled by <see cref="Version"/>.
		/// </summary>
		/// <param name="filename">The file to open and write.</param>
		/// <param name="repack">If true, the data in chunk files is copied into new chunk files named after <paramref name="filename"/> in directory tree order,
		/// or for a package without chunk files written to a "_dir.vpk", the data in the directory file,
		/// which drops the data of removed and replaced files. Either all existing files are replaced or none are.
		/// If the package has chunk files with that name and was read from a file, it must be written to that file.
		/// If new chunk files were written or the package had chunk files with that name, existing chunk files numbered after the new ones are then deleted where possible
		/// until a number has no file, and in the latter case also up to the highest number the package had.</param>
		/// <exception cref="InvalidOperationException"><paramref name="repack"/> is false and <see cref="RequiresRepack(string)"/> is true,
		/// <paramref name="repack"/> is true and the package has chunk files named after <paramref name="filename"/> but was read from another file,
		/// or <paramref name="filename"/> is the file the package was read from inside a larger file.</exception>
		public void Write(string filename, bool repack = false)
		{
			ArgumentNullException.ThrowIfNull(filename);
			ObjectDisposedException.ThrowIf(IsDisposed, this);

			var fullPath = Path.GetFullPath(filename);
			var replacesReadFile = ReadFilePath != null && PathsEqual(fullPath, ReadFilePath);

			if (replacesReadFile && StreamOffset != 0)
			{
				throw new InvalidOperationException("This package was read from inside a larger file, which can not be written over.");
			}

			var baseName = StripDirVpkSuffixes(fullPath, out var isDirVpk);
			var writesOverChunkFiles = WritesOverChunkFiles(baseName);

			// Readers find chunk files by appending "_###.vpk" to the directory file name without the "_dir.vpk" suffix
			if (!repack && HighestChunkIndex >= 0 && !writesOverChunkFiles)
			{
				throw new InvalidOperationException($"This package has chunk files named after '{GetChunkBaseName()}_dir.vpk', the directory file must be written to that path so that they can be found, or be written with repack enabled.");
			}

			// The directory file the package was read from refers to the chunk files that repacking replaces
			if (repack && writesOverChunkFiles && ReadFilePath != null && !replacesReadFile)
			{
				throw new InvalidOperationException($"Repacking replaces the chunk files that '{ReadFilePath}' refers to, write it to that path instead.");
			}

			var entries = GetEntriesInTreeOrder();

			if (!replacesReadFile && !repack)
			{
				var chunkHashFractions = GetChunkHashFractions();

				using var output = new FileStream(fullPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);

				try
				{
					WriteDirectoryFile(output, entries, chunkHashFractions, chunkLocations: null);
				}
				catch when (output.Position > 0)
				{
					// Drop the rest of the file that was written over, so that it does not follow the part that was written
					output.SetLength(output.Position);
					throw;
				}

				output.SetLength(output.Position);

				if (!IsReadPackage && writesOverChunkFiles)
				{
					DeleteStaleChunkFiles(baseName, HighestChunkIndex + 1, staleHighestIndex: -1);
				}

				return;
			}

			var token = Path.GetRandomFileName();
			var chunkReplacements = new List<FileReplacement>();
			var directoryReplacement = new FileReplacement(GetTemporaryPath(fullPath, token), fullPath);

			try
			{
				Dictionary<PackageEntry, ChunkLocation>? chunkLocations = null;
				List<ChunkHashFraction>? chunkHashFractions;

				if (repack)
				{
					(chunkLocations, chunkHashFractions) = WriteRepackedChunkFiles(entries, baseName, isDirVpk && HighestChunkIndex < 0, token, chunkReplacements);
				}
				else
				{
					chunkHashFractions = GetChunkHashFractions();
				}

				using (var stream = new FileStream(directoryReplacement.TemporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
				{
					WriteDirectoryFile(stream, entries, chunkHashFractions, chunkLocations);
				}

				var readsWrittenFile = replacesReadFile || (repack && Reader != null);

				if (repack)
				{
					FinishCurrentChunkFile();

					foreach (var archiveIndex in Archives.Keys.Where(static archiveIndex => archiveIndex != DirectoryArchiveIndex))
					{
						CloseArchive(archiveIndex);
					}
				}

				var backupPaths = ReplaceFilesOrReopen(chunkReplacements, directoryReplacement, token, replacesReadFile);

				if (chunkLocations != null)
				{
					var staleHighestIndex = writesOverChunkFiles ? HighestChunkIndex : -1;

					ApplyRepackedLayout(chunkLocations, chunkHashFractions, baseName, isDirVpk, chunkReplacements.Count);
					backupPaths.ForEach(TryDeleteFile);

					if (chunkReplacements.Count > 0 || writesOverChunkFiles)
					{
						DeleteStaleChunkFiles(baseName, chunkReplacements.Count, staleHighestIndex);
					}
				}

				if (readsWrittenFile)
				{
					ReopenWrittenDirectoryFile(fullPath, entries);
				}
			}
			finally
			{
				foreach (var (temporaryPath, _) in chunkReplacements)
				{
					TryDeleteFile(temporaryPath);
				}

				TryDeleteFile(directoryReplacement.TemporaryPath);
			}
		}

		/// <summary>
		/// Returns whether <see cref="Write(string, bool)"/> has to repack to write the directory file to the given path, because the package has chunk files named after another path.
		/// </summary>
		/// <param name="filename">The file the directory file is going to be written to.</param>
		public bool RequiresRepack(string filename)
		{
			ArgumentNullException.ThrowIfNull(filename);
			ObjectDisposedException.ThrowIf(IsDisposed, this);

			return HighestChunkIndex >= 0 && !WritesOverChunkFiles(StripDirVpkSuffixes(Path.GetFullPath(filename), out _));
		}

		/// <summary>
		/// Returns whether the package has chunk files and they are named after the given base name.
		/// </summary>
		private bool WritesOverChunkFiles(string baseName)
		{
			return HighestChunkIndex >= 0 && PathsEqual(baseName, GetChunkBaseName());
		}

		private static string GetTemporaryPath(string path, string token)
		{
			return $"{path}.{token}.tmp";
		}

		/// <summary>
		/// Points the copied entries at the repacked chunk files and makes the package refer to the chunk files named after <paramref name="baseName"/>.
		/// </summary>
		private void ApplyRepackedLayout(Dictionary<PackageEntry, ChunkLocation> chunkLocations, List<ChunkHashFraction>? chunkHashFractions, string baseName, bool isDirVpk, int chunkFileCount)
		{
			foreach (var (entry, (archiveIndex, offset)) in chunkLocations)
			{
				entry.ArchiveIndex = archiveIndex;
				entry.Offset = offset;
				entry.PendingData = null;
			}

			FileName = baseName;
			ChunkBaseName = chunkFileCount > 0 ? baseName : null;
			IsDirVPK = isDirVpk;
			HighestChunkIndex = chunkFileCount - 1;
			UpdatedChunkHashFractions.Clear();

			foreach (var fractions in (chunkHashFractions ?? []).GroupBy(static fraction => fraction.ArchiveIndex))
			{
				UpdatedChunkHashFractions[fractions.Key] = [.. fractions];
			}
		}

		/// <summary>
		/// Deletes chunk files from <paramref name="firstIndex"/> on until a number has no file, and up to <paramref name="staleHighestIndex"/> regardless.
		/// </summary>
		private static void DeleteStaleChunkFiles(string baseName, int firstIndex, int staleHighestIndex)
		{
			for (var i = firstIndex; i < DirectoryArchiveIndex; i++)
			{
				var path = GetArchiveIndexFullFilePath(baseName, (ushort)i);

				if (i > staleHighestIndex && !File.Exists(path))
				{
					break;
				}

				TryDeleteFile(path);
			}
		}

		/// <summary>
		/// Replaces the files, closing the directory file first when it is the one the package reads from, and opening it again if replacing fails.
		/// </summary>
		/// <returns>The paths that the replaced chunk files were moved aside to.</returns>
		private List<string> ReplaceFilesOrReopen(List<FileReplacement> chunkReplacements, FileReplacement directoryReplacement, string token, bool replacesReadFile)
		{
			if (!replacesReadFile)
			{
				return ReplaceFiles(chunkReplacements, directoryReplacement, token);
			}

			CloseDirectoryFile();

			try
			{
				return ReplaceFiles(chunkReplacements, directoryReplacement, token);
			}
			catch (Exception exception)
			{
				try
				{
					ReopenDirectoryFile(directoryReplacement.Path);
				}
				catch (Exception reopenException)
				{
					throw new AggregateException(exception, reopenException);
				}

				throw;
			}
		}

		/// <summary>
		/// Copies the data of all entries in chunk files, and optionally in the directory file, into new chunk files under temporary names, adding each of them to <paramref name="replacements"/>.
		/// </summary>
		/// <returns>Where the data of each copied entry was written to, and the hashes of the new chunk files or null if there are none.</returns>
		private (Dictionary<PackageEntry, ChunkLocation> Locations, List<ChunkHashFraction>? HashFractions) WriteRepackedChunkFiles(
			List<PackageEntry> entries, string baseName, bool includesDirectoryFileData, string token, List<FileReplacement> replacements)
		{
			var locations = new Dictionary<PackageEntry, ChunkLocation>();
			var hashFractions = new List<ChunkHashFraction>();
			ChunkFileWriter? chunkFile = null;

			try
			{
				foreach (var entry in entries)
				{
					if (!IsInChunkFile(entry) && !(includesDirectoryFileData && IsInDirectoryFile(entry)))
					{
						continue;
					}

					if (!HasRoomForNextFile(chunkFile, entry.Length))
					{
						var archiveIndex = chunkFile == null ? 0 : chunkFile.ArchiveIndex + 1;
						ThrowIfChunkLimitReached(archiveIndex);

						var path = GetArchiveIndexFullFilePath(baseName, (ushort)archiveIndex);
						var temporaryPath = GetTemporaryPath(path, token);
						var nextChunkFile = new ChunkFileWriter((ushort)archiveIndex, temporaryPath);
						replacements.Add(new FileReplacement(temporaryPath, path));

						if (chunkFile != null)
						{
							hashFractions.AddRange(chunkFile.GetHashFractions());
							chunkFile.Dispose();
						}

						chunkFile = nextChunkFile;
					}

					locations[entry] = new ChunkLocation(chunkFile.ArchiveIndex, chunkFile.Length);

					if (entry.PendingData != null)
					{
						chunkFile.Write(entry.PendingData);
					}
					else
					{
						ReadArchiveDataInBlocks(entry.ArchiveIndex, entry.Offset, entry.Length, chunkFile.Write);
					}
				}

				if (chunkFile == null)
				{
					return (locations, null);
				}

				hashFractions.AddRange(chunkFile.GetHashFractions());

				return (locations, hashFractions);
			}
			finally
			{
				chunkFile?.Dispose();
			}
		}

		/// <summary>
		/// Moves the temporary files over their targets, the directory file last.
		/// Existing chunk files are moved aside first and are moved back if any move fails, so that either all files are replaced or none are.
		/// </summary>
		/// <returns>The paths that the replaced chunk files were moved aside to.</returns>
		private static List<string> ReplaceFiles(List<FileReplacement> chunkReplacements, FileReplacement directoryReplacement, string token)
		{
			var backups = new Dictionary<string, string>();
			var replaced = new List<string>();

			try
			{
				foreach (var (_, path) in chunkReplacements)
				{
					if (File.Exists(path))
					{
						var backupPath = $"{path}.{token}.bak";
						File.Move(path, backupPath);
						backups.Add(path, backupPath);
					}
				}

				foreach (var (temporaryPath, path) in chunkReplacements.Append(directoryReplacement))
				{
					File.Move(temporaryPath, path, overwrite: true);
					replaced.Add(path);
				}
			}
			catch (Exception exception)
			{
				var failures = new List<Exception> { exception };

				void Undo(Action action)
				{
					try
					{
						action();
					}
					catch (Exception undoException) when (undoException is IOException or UnauthorizedAccessException)
					{
						failures.Add(undoException);
					}
				}

				foreach (var path in replaced.Where(path => !backups.ContainsKey(path)))
				{
					Undo(() => File.Delete(path));
				}

				foreach (var (path, backupPath) in backups)
				{
					Undo(() => File.Move(backupPath, path, overwrite: true));
				}

				if (failures.Count > 1)
				{
					throw new AggregateException(failures);
				}

				throw;
			}

			return [.. backups.Values];
		}

		private static void TryDeleteFile(string path)
		{
			try
			{
				var file = new FileInfo(path);

				if (file.Exists)
				{
					file.IsReadOnly = false;
					file.Delete();
				}
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
			}
		}

		/// <summary>
		/// Writes to the given <see cref="Stream"/>, which must not be the stream or a file this package reads from.
		///
		/// If the package has chunk files, from being read or from files added with multi chunk enabled (see <see cref="AddFile(string, byte[], bool)"/>),
		/// this only writes the directory file. Readers find the chunk files next to the directory file by name,
		/// so the written data should end up at the path they are named after, which is the path the package was read from, given to <see cref="SetFileName(string)"/>,
		/// or last repacked to with <see cref="Write(string, bool)"/>.
		///
		/// The VPK version that is written is controlled by <see cref="Version"/>.
		///
		/// The package is written at the current position of the stream.
		/// </summary>
		/// <param name="stream">The input <see cref="Stream"/> to write to.</param>
		public void Write(Stream stream)
		{
			ArgumentNullException.ThrowIfNull(stream);
			ObjectDisposedException.ThrowIf(IsDisposed, this);

			if (ReferenceEquals(stream, Reader?.BaseStream) || (stream is FileStream fileStream && IsReadFile(fileStream.Name)))
			{
				throw new InvalidOperationException("This package reads from the given stream or file, use Write(string) to write over the directory file it was read from.");
			}

			WriteDirectoryFile(stream, GetEntriesInTreeOrder(), GetChunkHashFractions(), chunkLocations: null);
		}

		/// <summary>
		/// Points the entries with data in the directory file at where their data was written, and reads from the written directory file from then on.
		/// </summary>
		private void ReopenWrittenDirectoryFile(string path, List<PackageEntry> entries)
		{
			var offset = 0u;

			foreach (var entry in entries)
			{
				if (IsInDirectoryFile(entry))
				{
					entry.Offset = offset;
					entry.PendingData = null;
					offset += entry.Length;
				}
			}

			try
			{
				ReopenDirectoryFile(path);
			}
			catch (Exception exception)
			{
				throw new IOException($"'{path}' was written, but could not be opened again to read from it.", exception);
			}
		}

		/// <summary>
		/// Returns all entries sorted the way Valve's packer writes the directory tree: by type, then directory, then file name,
		/// and checks that the data of entries in the directory file can be read.
		/// </summary>
		private List<PackageEntry> GetEntriesInTreeOrder()
		{
			var entries = GetAllEntries().ToList();

			foreach (var entry in entries)
			{
				if (entry.FileName.Length == 0 || entry.TypeName.Length == 0)
				{
					throw new InvalidOperationException($"Entry '{entry.GetFullPath()}' has an empty file name or extension, which the directory tree can not store.");
				}
			}

			if (entries.Exists(static entry => IsInDirectoryFile(entry) && entry.PendingData == null))
			{
				ThrowIfDirectoryFileClosed();
			}

			entries.Sort(CompareTreeOrder);

			return entries;
		}

		private static int CompareTreeOrder(PackageEntry x, PackageEntry y)
		{
			var comparison = CompareNames(x.TypeName, y.TypeName);

			if (comparison == 0)
			{
				comparison = CompareNames(GetTreeDirectoryName(x), GetTreeDirectoryName(y));
			}

			return comparison != 0 ? comparison : CompareNames(x.FileName, y.FileName);
		}

		/// <summary>
		/// Compares names with ASCII letters lowercased, and names that are equal that way ordinally.
		/// </summary>
		private static int CompareNames(string x, string y)
		{
			var comparison = AsciiComparer.Compare(x, y);

			return comparison != 0 ? comparison : string.CompareOrdinal(x, y);
		}

		private static string GetTreeDirectoryName(PackageEntry entry)
		{
			return entry.DirectoryName.Length == 0 ? Space : entry.DirectoryName;
		}

		/// <summary>
		/// Writes the directory file, with entries in chunk files where they are unless they are in <paramref name="chunkLocations"/>.
		/// </summary>
		private void WriteDirectoryFile(Stream stream, List<PackageEntry> entries, List<ChunkHashFraction>? chunkHashFractions, Dictionary<PackageEntry, ChunkLocation>? chunkLocations)
		{
			// The file tree is built in memory first, so that the header can be written with its size and the tree can be hashed
			using var treeStream = new MemoryStream();
			using var writer = new BinaryWriter(treeStream);

			// Entries with data in the directory file, in tree order, which is the order their data offsets are assigned in
			var embeddedEntries = new List<PackageEntry>();
			uint embeddedDataOffset = 0;

			const byte NullByte = 0;

			void WriteNullTerminated(string value)
			{
				writer.Write(Encoding.UTF8.GetBytes(value));
				writer.Write(NullByte);
			}

			foreach (var typeEntries in entries.GroupBy(static entry => entry.TypeName))
			{
				WriteNullTerminated(typeEntries.Key);

				foreach (var directoryEntries in typeEntries.GroupBy(GetTreeDirectoryName))
				{
					WriteNullTerminated(directoryEntries.Key);

					foreach (var entry in directoryEntries)
					{
						var archiveIndex = entry.ArchiveIndex;
						var fileOffset = entry.Offset;

						if (entry.Length == 0)
						{
							archiveIndex = DirectoryArchiveIndex;
							fileOffset = embeddedDataOffset;
						}
						else if (chunkLocations != null && chunkLocations.TryGetValue(entry, out var location))
						{
							(archiveIndex, fileOffset) = location;
						}
						else if (archiveIndex == DirectoryArchiveIndex)
						{
							if ((ulong)embeddedDataOffset + entry.Length > MaxDirectoryFileDataSize)
							{
								throw new InvalidOperationException("Package contents exceed 2GiB, add files with multi chunk enabled to split them into chunk files.");
							}

							fileOffset = embeddedDataOffset;
							embeddedDataOffset += entry.Length;
							embeddedEntries.Add(entry);
						}

						if (entry.SmallData.Length > MaxPreloadSize)
						{
							throw new InvalidOperationException($"Entry '{entry.GetFullPath()}' has {entry.SmallData.Length} preload bytes, the directory tree can store at most {MaxPreloadSize}.");
						}

						WriteNullTerminated(entry.FileName);
						writer.Write(entry.CRC32);
						writer.Write((ushort)entry.SmallData.Length);
						writer.Write(archiveIndex);
						writer.Write(fileOffset);
						writer.Write(entry.Length);
						writer.Write(ushort.MaxValue); // terminator, 0xFFFF
						writer.Write(entry.SmallData);
					}

					writer.Write(NullByte);
				}

				writer.Write(NullByte);
			}

			writer.Write(NullByte);

			var treeData = treeStream.GetBuffer().AsSpan(0, (int)treeStream.Length);

			var fractionCount = chunkHashFractions?.Count ?? (int)((embeddedDataOffset + (ChunkHasher.FileFractionSize - 1L)) / ChunkHasher.FileFractionSize);

			Span<byte> header = stackalloc byte[Version >= 2 ? 28 : 12];
			BinaryPrimitives.WriteInt32LittleEndian(header, MAGIC);
			BinaryPrimitives.WriteUInt32LittleEndian(header[4..], Version);
			BinaryPrimitives.WriteInt32LittleEndian(header[8..], treeData.Length); // TreeSize

			if (Version >= 2)
			{
				BinaryPrimitives.WriteUInt32LittleEndian(header[12..], embeddedDataOffset); // FileDataSectionSize
				BinaryPrimitives.WriteInt32LittleEndian(header[16..], fractionCount * ChunkHashFraction.SectionEntrySize); // ArchiveMD5SectionSize
				BinaryPrimitives.WriteInt32LittleEndian(header[20..], 48); // OtherMD5SectionSize
				BinaryPrimitives.WriteInt32LittleEndian(header[24..], UnsignedSignatureSectionSize); // SignatureSectionSize
			}

			using var fullFileMD5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
			using var embeddedDataHashes = new ChunkHasher(DirectoryArchiveIndex);

			void WriteAndHash(ReadOnlySpan<byte> data)
			{
				stream.Write(data);
				fullFileMD5.AppendData(data);
			}

			WriteAndHash(header);
			WriteAndHash(treeData);

			foreach (var entry in embeddedEntries)
			{
				var data = GetEmbeddedData(entry);
				WriteAndHash(data);

				// Data in the directory file is only hashed in packages without chunk files
				if (chunkHashFractions == null)
				{
					embeddedDataHashes.Append(data);
				}
			}

			if (Version < 2)
			{
				// Version 1 has no checksums
				return;
			}

			var archiveMd5SectionBytes = SerializeChunkHashFractions(chunkHashFractions ?? embeddedDataHashes.GetHashFractions(includeEmptyLast: false));
			Debug.Assert(archiveMd5SectionBytes.Length == fractionCount * ChunkHashFraction.SectionEntrySize);

			WriteAndHash(archiveMd5SectionBytes);
			WriteAndHash(MD5.HashData(treeData));
			WriteAndHash(MD5.HashData(archiveMd5SectionBytes));

			stream.Write(fullFileMD5.GetHashAndReset());

			Span<byte> signatureSection = stackalloc byte[UnsignedSignatureSectionSize];
			BinaryPrimitives.WriteInt32LittleEndian(signatureSection, MAGIC);
			BinaryPrimitives.WriteInt32LittleEndian(signatureSection[4..], (int)ESignatureType.OnlyFileChecksum);
			stream.Write(signatureSection);
		}

		private bool IsReadFile(string path)
		{
			if (ReadFilePath != null && PathsEqual(path, ReadFilePath))
			{
				return true;
			}

			if (HighestChunkIndex < 0)
			{
				return false;
			}

			var baseName = GetChunkBaseName();

			return Enumerable.Range(0, HighestChunkIndex + 1).Any(archiveIndex => PathsEqual(path, GetArchiveIndexFullFilePath(baseName, (ushort)archiveIndex)));
		}

		private static bool PathsEqual(string path1, string path2)
		{
			return string.Equals(path1, path2, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
		}

		/// <summary>
		/// Returns the archive MD5 section entries for the chunk files, or null if the package has none.
		/// </summary>
		private List<ChunkHashFraction>? GetChunkHashFractions()
		{
			if (HighestChunkIndex < 0)
			{
				return null;
			}

			var baseName = GetChunkBaseName();
			var readFractions = AccessPackFileHashes
				.Where(static fraction => fraction.ArchiveIndex != DirectoryArchiveIndex)
				.GroupBy(static fraction => fraction.ArchiveIndex)
				.ToDictionary(static fractions => fractions.Key, static fractions => fractions.OrderBy(static fraction => fraction.Offset).ToList());
			var usedArchiveIndices = GetChunkIndicesWithData().ToHashSet();
			var hashFractions = new List<ChunkHashFraction>();

			for (var i = 0; i <= HighestChunkIndex; i++)
			{
				var archiveIndex = (ushort)i;

				if (CurrentChunkFile?.ArchiveIndex == archiveIndex)
				{
					hashFractions.AddRange(CurrentChunkFile.GetHashFractions());
					continue;
				}

				var file = new FileInfo(GetArchiveIndexFullFilePath(baseName, archiveIndex));

				if (!file.Exists)
				{
					if (usedArchiveIndices.Contains(archiveIndex))
					{
						throw new FileNotFoundException($"Chunk file {archiveIndex} does not exist, but entries are in it.", file.FullName);
					}

					continue;
				}

				if (!UpdatedChunkHashFractions.TryGetValue(archiveIndex, out var fractions) && !readFractions.TryGetValue(archiveIndex, out fractions))
				{
					continue;
				}

				if (!CoversFile(fractions, file.Length))
				{
					if (file.Length > MaxChunkFileSize)
					{
						throw new InvalidOperationException($"Chunk file {archiveIndex} is {file.Length} bytes, its hashes can only be computed for chunk files smaller than 4 GiB.");
					}

					using var hashes = new ChunkHasher(archiveIndex);
					ReadArchiveDataInBlocks(archiveIndex, 0, file.Length, hashes.Append);
					fractions = hashes.GetHashFractions();
					UpdatedChunkHashFractions[archiveIndex] = fractions;
				}

				hashFractions.AddRange(fractions);
			}

			return hashFractions;
		}

		private static bool CoversFile(List<ChunkHashFraction> fractions, long fileLength)
		{
			var offset = 0L;

			foreach (var fraction in fractions)
			{
				if (fraction.Offset != offset)
				{
					return false;
				}

				offset += fraction.Length;
			}

			return offset == fileLength;
		}

		private void ReadArchiveDataInBlocks(ushort archiveIndex, long offset, long length, Action<ReadOnlySpan<byte>> action)
		{
			var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);

			try
			{
				for (var end = offset + length; offset < end;)
				{
					var data = buffer.AsSpan(0, (int)Math.Min(end - offset, CopyBufferSize));
					ReadArchiveDataExactly(archiveIndex, offset, data);
					action(data);
					offset += data.Length;
				}
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(buffer);
			}
		}

		/// <summary>
		/// Returns the data of an entry that is written into the directory file, without its preload bytes.
		/// </summary>
		private byte[] GetEmbeddedData(PackageEntry entry)
		{
			if (entry.PendingData != null)
			{
				return entry.PendingData;
			}

			var data = new byte[entry.Length];
			ReadArchiveDataExactly(entry.ArchiveIndex, entry.Offset, data);

			return data;
		}

		private static byte[] SerializeChunkHashFractions(List<ChunkHashFraction> fractions)
		{
			if (fractions.Count == 0)
			{
				return [];
			}

			var bytes = new byte[fractions.Count * ChunkHashFraction.SectionEntrySize];
			using var ms = new MemoryStream(bytes);
			using var writer = new BinaryWriter(ms);

			foreach (var fraction in fractions)
			{
				fraction.Write(writer);
			}

			return bytes;
		}

		private readonly record struct ChunkLocation(ushort ArchiveIndex, uint Offset);

		private readonly record struct FileReplacement(string TemporaryPath, string Path);

		/// <summary>
		/// Writes data for a single chunk file, hashing it for the archive MD5 section as it is written.
		/// </summary>
		private sealed class ChunkFileWriter : IDisposable
		{
			private readonly FileStream stream;
			private readonly ChunkHasher hasher;

			/// <summary>
			/// Gets the index of this chunk file.
			/// </summary>
			public ushort ArchiveIndex => hasher.ArchiveIndex;

			/// <summary>
			/// Gets the size in bytes of the data written so far.
			/// </summary>
			public uint Length => hasher.Length;

			/// <summary>
			/// Creates the chunk file.
			/// </summary>
			/// <param name="archiveIndex">Index of this chunk file.</param>
			/// <param name="filePath">Path of the chunk file.</param>
			/// <param name="mode">Either <see cref="FileMode.Create"/> to replace an existing file, or <see cref="FileMode.CreateNew"/> to fail if one exists.</param>
			public ChunkFileWriter(ushort archiveIndex, string filePath, FileMode mode = FileMode.Create)
			{
				stream = new FileStream(filePath, mode, FileAccess.Write, FileShare.Read, bufferSize: 65536);
				hasher = new ChunkHasher(archiveIndex);
			}

			public void Write(ReadOnlySpan<byte> data)
			{
				hasher.Append(data);
				stream.Write(data);
			}

			public void Flush()
			{
				stream.Flush();
			}

			/// <inheritdoc cref="ChunkHasher.GetHashFractions"/>
			public List<ChunkHashFraction> GetHashFractions()
			{
				return hasher.GetHashFractions();
			}

			public void Dispose()
			{
				stream.Dispose();
				hasher.Dispose();
			}
		}

		/// <summary>
		/// Hashes the data of a single archive in 1 MiB fractions for the archive MD5 section.
		/// </summary>
		private sealed class ChunkHasher : IDisposable
		{
			public const int FileFractionSize = 0x00100000; // 1 MiB

			private Blake3.Hasher hasher = Blake3.Hasher.New();
			private readonly List<ChunkHashFraction> fractions = [];
			private uint fractionOffset;
			private uint bytesInFraction;

			/// <summary>
			/// Gets the index of the archive the hashed data is in.
			/// </summary>
			public ushort ArchiveIndex { get; }

			/// <summary>
			/// Gets the size in bytes of the data hashed so far.
			/// </summary>
			public uint Length => fractionOffset + bytesInFraction;

			public ChunkHasher(ushort archiveIndex)
			{
				ArchiveIndex = archiveIndex;
			}

			public void Append(ReadOnlySpan<byte> data)
			{
				// Offsets and lengths in the directory tree are 32-bit
				if ((long)Length + data.Length > MaxChunkFileSize)
				{
					throw new InvalidOperationException($"Chunk file {ArchiveIndex} would exceed 4 GiB.");
				}

				while (!data.IsEmpty)
				{
					var length = Math.Min(FileFractionSize - (int)bytesInFraction, data.Length);
					hasher.Update(data[..length]);
					bytesInFraction += (uint)length;
					data = data[length..];

					if (bytesInFraction == FileFractionSize)
					{
						fractions.Add(CreateFraction());
						hasher.Reset();
						fractionOffset += bytesInFraction;
						bytesInFraction = 0;
					}
				}
			}

			/// <summary>
			/// Returns the hashes of the data written so far. The last fraction covers the data after the last full fraction,
			/// and is zero sized when the chunk file size is an exact multiple of the fraction size.
			/// </summary>
			/// <param name="includeEmptyLast">If false, a zero sized last fraction is left out.</param>
			public List<ChunkHashFraction> GetHashFractions(bool includeEmptyLast = true)
			{
				return bytesInFraction > 0 || includeEmptyLast ? [.. fractions, CreateFraction()] : [.. fractions];
			}

			private ChunkHashFraction CreateFraction()
			{
				var checksum = new byte[16];
				hasher.Finalize(checksum);

				return new ChunkHashFraction
				{
					ArchiveIndex = ArchiveIndex,
					HashType = EHashType.Blake3,
					Offset = fractionOffset,
					Length = bytesInFraction,
					Checksum = checksum,
				};
			}

			public void Dispose()
			{
				hasher.Dispose();
			}
		}
	}
}
