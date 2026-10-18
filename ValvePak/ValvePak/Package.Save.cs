using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;

namespace ValvePak
{
	public partial class Package
	{
		/// <summary>
		/// Gets or sets the size in bytes of chunk files created when adding files with multi chunk enabled,
		/// see <see cref="AddFile(string, byte[], bool)"/>. A new chunk file is started once the current one is larger than this size,
		/// so a chunk file can end past it by up to the size of its last file.
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
		/// Size in bytes of the buffer that file data is copied through when it is added to a chunk file.
		/// </summary>
		private const int AddFileBufferSize = 1024 * 1024;

		/// <summary>
		/// The chunk file that files added with multi chunk enabled are currently written to.
		/// </summary>
		private ChunkFileWriter? CurrentChunkFile;

		/// <summary>
		/// Hashes of the chunk files before <see cref="CurrentChunkFile"/>.
		/// </summary>
		private readonly List<ChunkHashFraction> FinishedChunkHashFractions = [];

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
		/// The path is lowercased like Valve's packer does.
		/// </summary>
		/// <param name="entry">The package entry to rename.</param>
		/// <param name="newFilePath">Full new file path for this entry.</param>
		/// <exception cref="ArgumentException">The entry is not in this package, another entry already has the new path ignoring case, or the new path has an empty file name or extension or contains a null character.</exception>
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
		/// The path is lowercased like Valve's packer does.
		/// </summary>
		/// <param name="filePath">Full file path for this entry.</param>
		/// <param name="fileData">File data for this entry.</param>
		/// <param name="multiChunk">If true, the data is written right away into a numbered chunk file (such as "example_001.vpk") instead of being kept in memory for the directory file.
		/// <see cref="SetFileName(string)"/> must be called first with the path of the directory file, the chunk files are created next to it.
		/// A new chunk file is started whenever the current one is larger than <see cref="WriteChunkSize"/>.
		/// Chunk files stay on disk even if the directory file is never written.</param>
		/// <returns>The added entry.</returns>
		/// <exception cref="ArgumentException">The path has an empty file name or extension, or contains a null character.</exception>
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
		/// The path is lowercased like Valve's packer does.
		/// </summary>
		/// <param name="filePath">Full file path for this entry.</param>
		/// <param name="fileData">Stream to read the file data from. It is not disposed.</param>
		/// <param name="multiChunk">If true, the data is copied right away into a numbered chunk file (such as "example_001.vpk") instead of being kept in memory for the directory file.
		/// <see cref="SetFileName(string)"/> must be called first with the path of the directory file, the chunk files are created next to it.
		/// A new chunk file is started whenever the current one is larger than <see cref="WriteChunkSize"/>.
		/// Chunk files stay on disk even if the directory file is never written.</param>
		/// <returns>The added entry.</returns>
		/// <exception cref="ArgumentException">The path has an empty file name or extension, or contains a null character.</exception>
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

			var buffer = ArrayPool<byte>.Shared.Rent(AddFileBufferSize);

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
			if (FileName == null)
			{
				throw new InvalidOperationException("Call SetFileName() with the path of the directory file before adding files with multi chunk enabled, chunk files are created next to it.");
			}

			var archiveIndex = 0;

			if (CurrentChunkFile != null)
			{
				if (CurrentChunkFile.Length <= (uint)WriteChunkSize)
				{
					return CurrentChunkFile;
				}

				archiveIndex = CurrentChunkFile.ArchiveIndex + 1;

				if (archiveIndex >= 0x7FFF)
				{
					throw new InvalidOperationException("Reached the maximum amount of chunk files (32767).");
				}
			}

			var chunkFile = new ChunkFileWriter((ushort)archiveIndex, GetArchiveIndexFullFilePath(FileName, (ushort)archiveIndex));

			if (CurrentChunkFile != null)
			{
				FinishedChunkHashFractions.AddRange(CurrentChunkFile.GetHashFractions());
				CurrentChunkFile.Dispose();
			}

			CurrentChunkFile = chunkFile;

			return chunkFile;
		}

		/// <summary>
		/// Creates an entry with the directory, file and type names split from the given path.
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

			filePath = ToLowerAscii(filePath.Replace(WindowsDirectorySeparator, DirectorySeparatorChar));

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
				extension = Space;
			}

			if (fileName.Length == 0 || extension.Length == 0)
			{
				throw new ArgumentException($"'{filePath}' has an empty file name or extension.", paramName);
			}

			directory = directory.Trim(DirectorySeparatorChar);

			if (directory.Length == 0)
			{
				directory = Space;
			}

			return (directory, fileName, extension);
		}

		private static string ToLowerAscii(string value)
		{
			if (!value.AsSpan().ContainsAnyInRange('A', 'Z'))
			{
				return value;
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

		/// <summary>
		/// Opens and writes the given filename.
		///
		/// If any files were added with multi chunk enabled (see <see cref="AddFile(string, byte[], bool)"/>),
		/// their data is already in the chunk files, and this only writes the directory file.
		/// It must be written to the path given to <see cref="SetFileName(string)"/>, which should end with "_dir.vpk".
		///
		/// The VPK version that is written is controlled by <see cref="Version"/>.
		/// </summary>
		/// <param name="filename">The file to open and write.</param>
		public void Write(string filename)
		{
			ArgumentNullException.ThrowIfNull(filename);
			ObjectDisposedException.ThrowIf(IsDisposed, this);

			if (CurrentChunkFile != null)
			{
				Debug.Assert(FileName != null);

				// Readers find chunk files by appending "_###.vpk" to the directory file name without the "_dir.vpk" suffix
				var expectedBaseName = Path.GetFullPath(FileName);
				var writtenBaseName = Path.GetFullPath(StripDirVpkSuffixes(filename, out _));

				if (writtenBaseName != expectedBaseName)
				{
					throw new InvalidOperationException($"Chunk files were created for '{FileName}_dir.vpk', the directory file must be written to that path so that they can be found.");
				}
			}

			using var fs = new FileStream(filename, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);

			Write(fs);

			fs.SetLength(fs.Position);
		}

		/// <summary>
		/// Writes to the given <see cref="Stream"/>.
		///
		/// If any files were added with multi chunk enabled (see <see cref="AddFile(string, byte[], bool)"/>),
		/// their data is already in the chunk files, and this only writes the directory file.
		/// Readers find the chunk files next to the directory file by name, so the stream should be the file at the path given to <see cref="SetFileName(string)"/>.
		///
		/// The VPK version that is written is controlled by <see cref="Version"/>.
		///
		/// The package is written at the current position of the stream.
		/// </summary>
		/// <param name="stream">The input <see cref="Stream"/> to write to.</param>
		public void Write(Stream stream)
		{
			if (Reader != null && IsDirVPK)
			{
				throw new InvalidOperationException("This package was opened from a _dir.vpk, writing back is currently unsupported.");
			}

			ArgumentNullException.ThrowIfNull(stream);
			ObjectDisposedException.ThrowIf(IsDisposed, this);

			var tree = new Dictionary<string, Dictionary<string, List<PackageEntry>>>();

			// Group the entries by type and directory
			foreach (var typeEntries in Entries ?? [])
			{
				foreach (var entry in typeEntries.Value)
				{
					if (entry.FileName.Length == 0 || entry.TypeName.Length == 0)
					{
						throw new InvalidOperationException($"Entry '{entry.GetFullPath()}' has an empty file name or extension, which the directory tree can not store.");
					}

					if (!tree.TryGetValue(entry.TypeName, out var typeTree))
					{
						typeTree = [];
						tree[entry.TypeName] = typeTree;
					}

					var directoryName = entry.DirectoryName.Length == 0 ? Space : entry.DirectoryName;

					if (!typeTree.TryGetValue(directoryName, out var directoryEntries))
					{
						directoryEntries = [];
						typeTree[directoryName] = directoryEntries;
					}

					directoryEntries.Add(entry);
				}
			}

			// The file tree is built in memory first, so that the header can be written with its size and the tree can be hashed
			using var treeStream = new MemoryStream();
			using var writer = new BinaryWriter(treeStream);

			// Entries with data in the directory file, in tree order, which is the order their data offsets are assigned in
			var embeddedEntries = new List<PackageEntry>();
			uint embeddedDataOffset = 0;

			const byte NullByte = 0;

			foreach (var typeEntries in tree)
			{
				writer.Write(Encoding.UTF8.GetBytes(typeEntries.Key));
				writer.Write(NullByte);

				foreach (var directoryEntries in typeEntries.Value)
				{
					writer.Write(Encoding.UTF8.GetBytes(directoryEntries.Key));
					writer.Write(NullByte);

					foreach (var entry in directoryEntries.Value)
					{
						var fileLength = entry.TotalLength;

						// Data in chunk files was written when the file was added
						var fileOffset = entry.Offset;

						if (entry.ArchiveIndex == 0x7FFF)
						{
							if ((ulong)embeddedDataOffset + fileLength > int.MaxValue)
							{
								throw new InvalidOperationException("Package contents exceed 2GiB, add files with multi chunk enabled to split them into chunk files.");
							}

							fileOffset = embeddedDataOffset;
							embeddedDataOffset += fileLength;
							embeddedEntries.Add(entry);
						}

						writer.Write(Encoding.UTF8.GetBytes(entry.FileName));
						writer.Write(NullByte);
						writer.Write(entry.CRC32);
						writer.Write((short)0); // SmallData, we will put it into data instead
						writer.Write(entry.ArchiveIndex);
						writer.Write(fileOffset);
						writer.Write(fileLength);
						writer.Write(ushort.MaxValue); // terminator, 0xFFFF
					}

					writer.Write(NullByte);
				}

				writer.Write(NullByte);
			}

			writer.Write(NullByte);

			var treeData = treeStream.GetBuffer().AsSpan(0, (int)treeStream.Length);

			// Archive MD5 section, contains hashes of chunk files
			var chunkHashFractions = new List<ChunkHashFraction>(FinishedChunkHashFractions);

			if (CurrentChunkFile != null)
			{
				chunkHashFractions.AddRange(CurrentChunkFile.GetHashFractions());
			}

			var archiveMd5SectionBytes = SerializeChunkHashFractions(chunkHashFractions);

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

			private readonly FileStream stream;
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
			/// Creates the chunk file, replacing an existing file.
			/// </summary>
			/// <param name="archiveIndex">Index of this chunk file.</param>
			/// <param name="filePath">Path of the chunk file.</param>
			public ChunkFileWriter(ushort archiveIndex, string filePath)
			{
				ArchiveIndex = archiveIndex;
				stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 65536);
			}

			public void Write(ReadOnlySpan<byte> data)
			{
				// Offsets and lengths in the directory tree are 32-bit
				if ((long)Length + data.Length > uint.MaxValue)
				{
					throw new InvalidOperationException($"Chunk file {ArchiveIndex} would exceed 4 GiB.");
				}

				stream.Write(data);

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
				stream.Flush();
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
				stream.Dispose();
				md5.Dispose();
			}
		}
	}
}
