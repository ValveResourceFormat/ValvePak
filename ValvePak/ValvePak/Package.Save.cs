using System;
using System.Buffers;
using System.Collections.Generic;
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
		/// The path is normalized and lowercased like Valve's packer does, see <see cref="FindEntry(ReadOnlySpan{char})"/>.
		/// </summary>
		/// <param name="entry">The package entry to rename.</param>
		/// <param name="newFilePath">Full new file path for this entry.</param>
		/// <exception cref="ArgumentException">The entry is not in this package, another entry already has the new path ignoring case, or the new path has an empty file name or contains a null character.</exception>
		public void RenameFile(PackageEntry entry, string newFilePath)
		{
			ArgumentNullException.ThrowIfNull(entry);
			ArgumentNullException.ThrowIfNull(newFilePath);
			ObjectDisposedException.ThrowIf(IsDisposed, this);

			var (directoryName, fileName, typeName) = ParseEntryPath(newFilePath, nameof(newFilePath));
			var existingEntry = FindEntryIgnoringCase(directoryName, fileName, typeName);

			if (existingEntry != null && existingEntry != entry)
			{
				throw new ArgumentException($"An entry already exists at '{existingEntry.GetFullPath()}'.", nameof(newFilePath));
			}

			if (!RemoveFile(entry))
			{
				throw new ArgumentException("The entry is not in this package.", nameof(entry));
			}

			(entry.DirectoryName, entry.FileName, entry.TypeName) = (directoryName, fileName, typeName);

			InsertEntry(entry);
		}

		private PackageEntry? FindEntryIgnoringCase(string directoryName, string fileName, string typeName)
		{
			foreach (var (entryTypeName, typeEntries) in Entries ?? [])
			{
				if (!entryTypeName.Equals(typeName, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				var match = typeEntries.Find(entry => entry.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase)
					&& entry.DirectoryName.Equals(directoryName, StringComparison.OrdinalIgnoreCase));

				if (match != null)
				{
					return match;
				}
			}

			return null;
		}

		/// <summary>
		/// Add file to current package. Be careful to not add duplicate entries, because this does not check for duplicates.
		/// The path is normalized and lowercased like Valve's packer does, see <see cref="FindEntry(ReadOnlySpan{char})"/>.
		/// </summary>
		/// <param name="filePath">Full file path for this entry.</param>
		/// <param name="fileData">File data for this entry.</param>
		/// <param name="multiChunk">If true, the data is written right away into a numbered chunk file (such as "example_001.vpk") instead of being kept in memory for the directory file.
		/// <see cref="SetFileName(string)"/> must be called first with the path of the directory file, the chunk files are created next to it.
		/// New chunk files are numbered after the highest chunk file of the package, and a read package also skips numbers that already have a file.
		/// A new chunk file is started whenever the current one is larger than <see cref="WriteChunkSize"/>.
		/// Chunk files stay on disk even if the directory file is never written.</param>
		/// <returns>The added entry.</returns>
		/// <exception cref="ArgumentException">The path has an empty file name, or contains a null character.</exception>
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

			var chunkFile = GetChunkFileForNextFile();
			var offset = chunkFile.Length;
			chunkFile.Write(fileData);

			return AddChunkFileEntry(entry, chunkFile, offset, Crc32.HashToUInt32(fileData));
		}

		/// <summary>
		/// Add file to current package, reading its data from the current position of the stream to its end.
		/// Be careful to not add duplicate entries, because this does not check for duplicates.
		/// The path is normalized and lowercased like Valve's packer does, see <see cref="FindEntry(ReadOnlySpan{char})"/>.
		/// </summary>
		/// <param name="filePath">Full file path for this entry.</param>
		/// <param name="fileData">Stream to read the file data from. It is not disposed.</param>
		/// <param name="multiChunk">If true, the data is copied right away into a numbered chunk file (such as "example_001.vpk") instead of being kept in memory for the directory file.
		/// <see cref="SetFileName(string)"/> must be called first with the path of the directory file, the chunk files are created next to it.
		/// New chunk files are numbered after the highest chunk file of the package, and a read package also skips numbers that already have a file.
		/// A new chunk file is started whenever the current one is larger than <see cref="WriteChunkSize"/>.
		/// Chunk files stay on disk even if the directory file is never written.</param>
		/// <returns>The added entry.</returns>
		/// <exception cref="ArgumentException">The path has an empty file name, or contains a null character.</exception>
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

			var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);

			try
			{
				var bytesRead = fileData.Read(buffer);

				if (bytesRead == 0)
				{
					// Files with no data are only ever written into the directory file
					return AddFileToDirectoryFile(entry, []);
				}

				var chunkFile = GetChunkFileForNextFile();
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
			entry.SmallData = fileData;
			entry.CRC32 = Crc32.HashToUInt32(fileData);
			entry.ArchiveIndex = 0x7FFF;

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
		private ChunkFileWriter GetChunkFileForNextFile()
		{
			if (CurrentChunkFile != null && CurrentChunkFile.Length <= (uint)WriteChunkSize)
			{
				return CurrentChunkFile;
			}

			var baseName = GetChunkBaseName();
			var archiveIndex = (ushort)(HighestChunkIndex + 1);

			while (IsReadPackage && File.Exists(GetArchiveIndexFullFilePath(baseName, archiveIndex)))
			{
				archiveIndex++;
			}

			var chunkFile = new ChunkFileWriter(archiveIndex, GetArchiveIndexFullFilePath(baseName, archiveIndex), IsReadPackage ? FileMode.CreateNew : FileMode.Create);

			FinishCurrentChunkFile();
			CurrentChunkFile = chunkFile;
			HighestChunkIndex = archiveIndex;

			return chunkFile;
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
			if (FileName == null)
			{
				throw new InvalidOperationException("Call SetFileName() with the path of the directory file before adding files with multi chunk enabled, chunk files are created next to it.");
			}

			return ChunkBaseName ??= Path.GetFullPath(FileName);
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
		/// Splits the given path into lowercased directory, file and type names.
		/// </summary>
		private static (string DirectoryName, string FileName, string TypeName) ParseEntryPath(string filePath, string paramName)
		{
			if (filePath.Contains('\0', StringComparison.Ordinal))
			{
				throw new ArgumentException($"'{filePath}' contains a null character.", paramName);
			}

			SplitPath(NormalizePath(filePath, out _), out var directory, out var fileName, out var extension);

			if (fileName.IsEmpty)
			{
				throw new ArgumentException($"'{filePath}' has an empty file name.", paramName);
			}

			return (ToLowerAscii(directory), ToLowerAscii(fileName), ToLowerAscii(extension));
		}

		private static string ToLowerAscii(ReadOnlySpan<char> value)
		{
			if (!value.ContainsAnyInRange('A', 'Z'))
			{
				return value.ToString();
			}

			return string.Create(value.Length, value, static (span, source) =>
			{
				for (var i = 0; i < source.Length; i++)
				{
					var c = source[i];
					span[i] = c is >= 'A' and <= 'Z' ? (char)(c | 0x20) : c;
				}
			});
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
		/// Opens and writes the given filename.
		///
		/// If the package has chunk files, from being read or from files added with multi chunk enabled (see <see cref="AddFile(string, byte[], bool)"/>),
		/// this only writes the directory file, which refers to the data where it is in the chunk files.
		/// Unless <paramref name="repack"/> is enabled, it must be written to the path the chunk files are named after, which should end with "_dir.vpk".
		/// Hashes of chunk files are kept if they cover the whole file, computed again otherwise, and left out for chunk files that had none.
		///
		/// Writing over the file the package was read from, or repacking, writes temporary files that replace the existing files once all were written,
		/// and the package reads from the written files afterwards. This must not run concurrently with reads from this package,
		/// and streams from <see cref="GetMemoryMappedStreamIfPossible"/> can no longer be read once they read from a replaced file, or from any file after repacking.
		/// Any other file is written in place.
		///
		/// The VPK version that is written is controlled by <see cref="Version"/>.
		/// </summary>
		/// <param name="filename">The file to open and write.</param>
		/// <param name="repack">If true, the data in chunk files is copied into new chunk files named after <paramref name="filename"/> in directory tree order,
		/// which drops the data of removed and replaced files. Either all existing files are replaced or none are.
		/// If new chunk files were written or the package had chunk files with that name, existing chunk files numbered after the new ones are then deleted where possible
		/// until a number has no file, and in the latter case also up to the highest number the package had.</param>
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
			var writesOverChunkFiles = HighestChunkIndex >= 0 && PathsEqual(baseName, GetChunkBaseName());

			// Readers find chunk files by appending "_###.vpk" to the directory file name without the "_dir.vpk" suffix
			if (!repack && HighestChunkIndex >= 0 && !writesOverChunkFiles)
			{
				throw new InvalidOperationException($"This package has chunk files named after '{GetChunkBaseName()}_dir.vpk', the directory file must be written to that path so that they can be found, or be written with repack enabled.");
			}

			var entries = GetEntriesInTreeOrder();

			if (!replacesReadFile && !repack)
			{
				var chunkHashFractions = GetChunkHashFractions();

				using var output = new FileStream(fullPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
				WriteDirectoryFile(output, entries, chunkHashFractions, chunkLocations: []);
				output.SetLength(output.Position);

				return;
			}

			var token = Path.GetRandomFileName();
			var replacements = new List<(string TemporaryPath, string Path)>();

			try
			{
				var chunkLocations = new Dictionary<PackageEntry, (ushort ArchiveIndex, uint Offset)>();
				List<ChunkHashFraction>? chunkHashFractions;

				if (repack)
				{
					(chunkLocations, chunkHashFractions) = WriteRepackedChunkFiles(entries, baseName, token, replacements);
				}
				else
				{
					chunkHashFractions = GetChunkHashFractions();
				}

				var chunkFileCount = replacements.Count;
				var temporaryPath = $"{fullPath}.{token}.tmp";
				replacements.Add((temporaryPath, fullPath));

				using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
				{
					WriteDirectoryFile(stream, entries, chunkHashFractions, chunkLocations);
				}

				var readsWrittenFile = replacesReadFile || (repack && Reader != null);

				if (repack)
				{
					FinishCurrentChunkFile();

					foreach (var archiveIndex in Archives.Keys.Where(static archiveIndex => archiveIndex != 0x7FFF))
					{
						CloseArchive(archiveIndex);
					}
				}

				if (replacesReadFile)
				{
					CloseDirectoryFile();
				}

				List<string> backupPaths;

				try
				{
					backupPaths = ReplaceFiles(replacements, token);
				}
				catch (Exception exception) when (replacesReadFile)
				{
					try
					{
						ReopenDirectoryFile(fullPath);
					}
					catch (Exception reopenException)
					{
						throw new AggregateException(exception, reopenException);
					}

					throw;
				}

				if (repack)
				{
					var staleHighestIndex = writesOverChunkFiles ? HighestChunkIndex : -1;

					foreach (var (entry, (archiveIndex, offset)) in chunkLocations)
					{
						entry.ArchiveIndex = archiveIndex;
						entry.Offset = offset;
					}

					FileName = baseName;
					ChunkBaseName = baseName;
					IsDirVPK = isDirVpk;
					HighestChunkIndex = chunkFileCount - 1;
					UpdatedChunkHashFractions.Clear();

					foreach (var fractions in (chunkHashFractions ?? []).GroupBy(static fraction => fraction.ArchiveIndex))
					{
						UpdatedChunkHashFractions[fractions.Key] = [.. fractions];
					}

					backupPaths.ForEach(TryDeleteFile);

					if (chunkFileCount > 0 || writesOverChunkFiles)
					{
						for (var i = chunkFileCount; i < 0x7FFF; i++)
						{
							var path = GetArchiveIndexFullFilePath(baseName, (ushort)i);

							if (i > staleHighestIndex && !File.Exists(path))
							{
								break;
							}

							TryDeleteFile(path);
						}
					}
				}

				if (readsWrittenFile)
				{
					ReopenWrittenDirectoryFile(fullPath, entries);
				}
			}
			finally
			{
				foreach (var (temporaryPath, _) in replacements)
				{
					TryDeleteFile(temporaryPath);
				}
			}
		}

		/// <summary>
		/// Copies the data of all entries in chunk files into new chunk files under temporary names, adding each of them to <paramref name="replacements"/>.
		/// </summary>
		/// <returns>Where the data of each copied entry was written to, and the hashes of the new chunk files or null if there are none.</returns>
		private (Dictionary<PackageEntry, (ushort ArchiveIndex, uint Offset)> Locations, List<ChunkHashFraction>? HashFractions) WriteRepackedChunkFiles(
			List<PackageEntry> entries, string baseName, string token, List<(string TemporaryPath, string Path)> replacements)
		{
			var locations = new Dictionary<PackageEntry, (ushort ArchiveIndex, uint Offset)>();
			var hashFractions = new List<ChunkHashFraction>();
			ChunkFileWriter? chunkFile = null;

			try
			{
				foreach (var entry in entries)
				{
					if (entry.ArchiveIndex == 0x7FFF || entry.Length == 0)
					{
						continue;
					}

					if (chunkFile == null || chunkFile.Length > (uint)WriteChunkSize)
					{
						var archiveIndex = (ushort)(chunkFile == null ? 0 : chunkFile.ArchiveIndex + 1);
						var path = GetArchiveIndexFullFilePath(baseName, archiveIndex);
						var temporaryPath = $"{path}.{token}.tmp";
						var nextChunkFile = new ChunkFileWriter(archiveIndex, temporaryPath);
						replacements.Add((temporaryPath, path));

						if (chunkFile != null)
						{
							hashFractions.AddRange(chunkFile.GetHashFractions());
							chunkFile.Dispose();
						}

						chunkFile = nextChunkFile;
					}

					locations[entry] = (chunkFile.ArchiveIndex, chunkFile.Length);
					ReadArchiveDataInBlocks(entry.ArchiveIndex, entry.Offset, entry.Length, chunkFile.Write);
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
		/// Moves the temporary files over their targets, of which the last is the directory file.
		/// Existing chunk files are moved aside first and are moved back if any move fails, so that either all files are replaced or none are.
		/// </summary>
		/// <returns>The paths that the replaced chunk files were moved aside to.</returns>
		private static List<string> ReplaceFiles(List<(string TemporaryPath, string Path)> replacements, string token)
		{
			var backups = new Dictionary<string, string>();
			var replaced = new List<string>();

			try
			{
				foreach (var (_, path) in replacements[..^1])
				{
					if (File.Exists(path))
					{
						var backupPath = $"{path}.{token}.bak";
						File.Move(path, backupPath);
						backups.Add(path, backupPath);
					}
				}

				foreach (var (temporaryPath, path) in replacements)
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

			WriteDirectoryFile(stream, GetEntriesInTreeOrder(), GetChunkHashFractions(), chunkLocations: []);
		}

		/// <summary>
		/// Points the entries with data in the directory file at where their data was written, and reads from the written directory file from then on.
		/// </summary>
		private void ReopenWrittenDirectoryFile(string path, List<PackageEntry> entries)
		{
			var offset = 0u;

			foreach (var entry in entries)
			{
				if (entry.ArchiveIndex == 0x7FFF)
				{
					entry.Length = entry.TotalLength;
					entry.SmallData = [];
					entry.Offset = offset;
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
		/// Returns all entries in the order they are written into the directory tree, and checks that the data of entries in the directory file can be read.
		/// </summary>
		private List<PackageEntry> GetEntriesInTreeOrder()
		{
			// Group the entries by type and directory
			var entries = GetAllEntries()
				.GroupBy(static entry => entry.TypeName)
				.SelectMany(static typeEntries => typeEntries.GroupBy(GetTreeDirectoryName).SelectMany(static directoryEntries => directoryEntries))
				.ToList();

			foreach (var entry in entries)
			{
				if (entry.FileName.Length == 0 || entry.TypeName.Length == 0)
				{
					throw new InvalidOperationException($"Entry '{entry.GetFullPath()}' has an empty file name or extension, which the directory tree can not store.");
				}
			}

			if (entries.Exists(static entry => entry.ArchiveIndex == 0x7FFF && entry.Length > 0))
			{
				ThrowIfDirectoryFileClosed();
			}

			return entries;
		}

		private static string GetTreeDirectoryName(PackageEntry entry)
		{
			return entry.DirectoryName.Length == 0 ? Space : entry.DirectoryName;
		}

		/// <summary>
		/// Writes the directory file, with entries in chunk files where they are unless they are in <paramref name="chunkLocations"/>.
		/// </summary>
		private void WriteDirectoryFile(Stream stream, List<PackageEntry> entries, List<ChunkHashFraction>? chunkHashFractions, Dictionary<PackageEntry, (ushort ArchiveIndex, uint Offset)> chunkLocations)
		{
			// The file tree is built in memory first, so that the header can be written with its size and the tree can be hashed
			using var treeStream = new MemoryStream();
			using var writer = new BinaryWriter(treeStream);

			// Entries with data in the directory file, in tree order, which is the order their data offsets are assigned in
			var embeddedEntries = new List<PackageEntry>();
			uint embeddedDataOffset = 0;

			const byte NullByte = 0;

			foreach (var typeEntries in entries.GroupBy(static entry => entry.TypeName))
			{
				writer.Write(Encoding.UTF8.GetBytes(typeEntries.Key));
				writer.Write(NullByte);

				foreach (var directoryEntries in typeEntries.GroupBy(GetTreeDirectoryName))
				{
					writer.Write(Encoding.UTF8.GetBytes(directoryEntries.Key));
					writer.Write(NullByte);

					foreach (var entry in directoryEntries)
					{
						var archiveIndex = entry.ArchiveIndex;
						var fileOffset = entry.Offset;
						var fileLength = entry.Length;
						var preloadData = entry.SmallData;

						if (entry.ArchiveIndex == 0x7FFF)
						{
							fileLength = entry.TotalLength;
							preloadData = [];

							if ((ulong)embeddedDataOffset + fileLength > int.MaxValue)
							{
								throw new InvalidOperationException("Package contents exceed 2GiB, add files with multi chunk enabled to split them into chunk files.");
							}

							fileOffset = embeddedDataOffset;
							embeddedDataOffset += fileLength;
							embeddedEntries.Add(entry);
						}
						else if (preloadData.Length > ushort.MaxValue)
						{
							throw new InvalidOperationException($"Entry '{entry.GetFullPath()}' has {preloadData.Length} preload bytes, the directory tree can store at most {ushort.MaxValue}.");
						}
						else if (chunkLocations.TryGetValue(entry, out var location))
						{
							(archiveIndex, fileOffset) = location;
						}

						writer.Write(Encoding.UTF8.GetBytes(entry.FileName));
						writer.Write(NullByte);
						writer.Write(entry.CRC32);
						writer.Write((ushort)preloadData.Length);
						writer.Write(archiveIndex);
						writer.Write(fileOffset);
						writer.Write(fileLength);
						writer.Write(ushort.MaxValue); // terminator, 0xFFFF
						writer.Write(preloadData);
					}

					writer.Write(NullByte);
				}

				writer.Write(NullByte);
			}

			writer.Write(NullByte);

			var treeData = treeStream.GetBuffer().AsSpan(0, (int)treeStream.Length);

			// Archive MD5 section, contains hashes of chunk files
			var archiveMd5SectionBytes = SerializeChunkHashFractions(chunkHashFractions ?? []);

			using var headerStream = new MemoryStream();
			using var headerWriter = new BinaryWriter(headerStream);
			headerWriter.Write(MAGIC);
			headerWriter.Write(Version);
			headerWriter.Write(treeData.Length); // TreeSize

			if (Version >= 2)
			{
				headerWriter.Write(embeddedDataOffset); // FileDataSectionSize
				headerWriter.Write(archiveMd5SectionBytes.Length); // ArchiveMD5SectionSize
				headerWriter.Write(48); // OtherMD5SectionSize
				headerWriter.Write(0); // SignatureSectionSize
			}

			var header = headerStream.GetBuffer().AsSpan(0, (int)headerStream.Length);

			using var fullFileMD5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);

			void WriteAndHash(ReadOnlySpan<byte> data)
			{
				stream.Write(data);
				fullFileMD5.AppendData(data);
			}

			WriteAndHash(header);
			WriteAndHash(treeData);

			foreach (var entry in embeddedEntries)
			{
				WriteAndHash(GetEntryData(entry));
			}

			if (Version < 2)
			{
				// Version 1 has no checksums
				return;
			}

			WriteAndHash(archiveMd5SectionBytes);
			WriteAndHash(MD5.HashData(treeData));
			WriteAndHash(MD5.HashData(archiveMd5SectionBytes));

			stream.Write(fullFileMD5.GetHashAndReset());
		}

		private bool IsReadFile(string path)
		{
			return (ReadFilePath != null && PathsEqual(path, ReadFilePath))
				|| Enumerable.Range(0, HighestChunkIndex + 1).Any(archiveIndex => PathsEqual(path, GetArchiveIndexFullFilePath(GetChunkBaseName(), (ushort)archiveIndex)));
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
				.Where(static fraction => fraction.ArchiveIndex != 0x7FFF)
				.GroupBy(static fraction => fraction.ArchiveIndex)
				.ToDictionary(static fractions => fractions.Key, static fractions => fractions.OrderBy(static fraction => fraction.Offset).ToList());
			var usedArchiveIndices = GetAllEntries()
				.Where(static entry => entry.Length > 0)
				.Select(static entry => entry.ArchiveIndex)
				.ToHashSet();
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
					if (file.Length > uint.MaxValue)
					{
						throw new InvalidOperationException($"Chunk file {archiveIndex} is {file.Length} bytes, its hashes can only be computed for chunk files smaller than 4 GiB.");
					}

					using var hashes = new ChunkFileWriter(archiveIndex, filePath: null);
					ReadArchiveDataInBlocks(archiveIndex, 0, file.Length, hashes.Write);
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
		/// Returns the data for an entry that is being written out.
		/// </summary>
		private byte[] GetEntryData(PackageEntry entry)
		{
			if (entry.Length == 0)
			{
				// Data added by AddFile lives entirely in SmallData, avoid copying it
				return entry.SmallData;
			}

			ReadEntry(entry, out var fileData, validateCrc: false);
			return fileData;
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

		/// <summary>
		/// Writes data for a single chunk file, hashing it in 1 MiB fractions for the archive MD5 section as it is written.
		/// </summary>
		private sealed class ChunkFileWriter : IDisposable
		{
			private const int FileFractionSize = 0x00100000; // 1 MiB

			private readonly FileStream? stream;
			private readonly IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
			private readonly List<ChunkHashFraction> fractions = [];
			private uint fractionOffset;
			private uint bytesInFraction;

			/// <summary>
			/// Gets the index of this chunk file.
			/// </summary>
			public ushort ArchiveIndex { get; }

			/// <summary>
			/// Gets the size in bytes of the data written so far.
			/// </summary>
			public uint Length => fractionOffset + bytesInFraction;

			/// <summary>
			/// Creates the chunk file.
			/// </summary>
			/// <param name="archiveIndex">Index of this chunk file.</param>
			/// <param name="filePath">Path of the chunk file, or null to only hash the data.</param>
			/// <param name="mode">Either <see cref="FileMode.Create"/> to replace an existing file, or <see cref="FileMode.CreateNew"/> to fail if one exists.</param>
			public ChunkFileWriter(ushort archiveIndex, string? filePath, FileMode mode = FileMode.Create)
			{
				if (archiveIndex >= 0x7FFF)
				{
					throw new InvalidOperationException("Reached the maximum amount of chunk files (32767).");
				}

				ArchiveIndex = archiveIndex;
				stream = filePath == null ? null : new FileStream(filePath, mode, FileAccess.Write, FileShare.Read, bufferSize: 65536);
			}

			public void Write(ReadOnlySpan<byte> data)
			{
				// Offsets and lengths in the directory tree are 32-bit
				if ((long)Length + data.Length > uint.MaxValue)
				{
					throw new InvalidOperationException($"Chunk file {ArchiveIndex} would exceed 4 GiB.");
				}

				stream?.Write(data);

				while (!data.IsEmpty)
				{
					var length = Math.Min(FileFractionSize - (int)bytesInFraction, data.Length);
					md5.AppendData(data[..length]);
					bytesInFraction += (uint)length;
					data = data[length..];

					if (bytesInFraction == FileFractionSize)
					{
						fractions.Add(CreateFraction(md5.GetHashAndReset()));
						fractionOffset += bytesInFraction;
						bytesInFraction = 0;
					}
				}
			}

			public void Flush()
			{
				stream?.Flush();
			}

			/// <summary>
			/// Returns the hashes of the data written so far. The last fraction covers the data after the last full fraction,
			/// and is zero sized when the chunk file size is an exact multiple of the fraction size.
			/// </summary>
			public List<ChunkHashFraction> GetHashFractions()
			{
				var allFractions = new List<ChunkHashFraction>(fractions);
				allFractions.Add(CreateFraction(md5.GetCurrentHash()));

				return allFractions;
			}

			private ChunkHashFraction CreateFraction(byte[] checksum)
			{
				return new ChunkHashFraction
				{
					ArchiveIndex = ArchiveIndex,
					HashType = EHashType.MD5,
					Offset = fractionOffset,
					Length = bytesInFraction,
					Checksum = checksum,
				};
			}

			public void Dispose()
			{
				stream?.Dispose();
				md5.Dispose();
			}
		}
	}
}
