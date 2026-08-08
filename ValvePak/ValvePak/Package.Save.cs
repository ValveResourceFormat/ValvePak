using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SteamDatabase.ValvePak
{
	public partial class Package
	{
		/// <summary>
		/// Gets or sets the maximum size in bytes of chunk files created when adding files with multi chunk enabled,
		/// see <see cref="AddFile(string, byte[], bool)"/>. A new chunk file is started once the current one reaches this size.
		/// Defaults to 200 MiB.
		/// </summary>
		public int WriteChunkSize
		{
			get;
			set
			{
				ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);

				field = value;
			}
		} = 200 * 1024 * 1024;

		/// <summary>
		/// Index of the chunk file that <see cref="AddFile(string, byte[], bool)"/> is currently assigning files to.
		/// </summary>
		private int CurrentChunkFileIndex;

		/// <summary>
		/// Size in bytes assigned to the current chunk file so far.
		/// </summary>
		private uint CurrentChunkFileSize;

		/// <summary>
		/// Remove file from current package.
		/// </summary>
		/// <param name="entry">The package entry to remove.</param>
		/// <returns>Returns true if entry was removed, false otherwise.</returns>
		public bool RemoveFile(PackageEntry entry)
		{
			ArgumentNullException.ThrowIfNull(entry);

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
		/// Add file to current package. Be careful to not add duplicate entries, because this does not check for duplicates.
		/// </summary>
		/// <param name="filePath">Full file path for this entry.</param>
		/// <param name="fileData">File data for this entry.</param>
		/// <param name="multiChunk">If true, this file will be assigned to a numbered chunk file (such as "example_001.vpk") instead of the directory file.
		/// A new chunk file is started whenever the current one reaches <see cref="WriteChunkSize"/>.
		/// Packages containing such files must be written using <see cref="Write(string)"/> so that the chunk files can be written next to the directory file.</param>
		/// <returns>The added entry.</returns>
		public PackageEntry AddFile(string filePath, byte[] fileData, bool multiChunk = false)
		{
			ArgumentNullException.ThrowIfNull(filePath);
			ArgumentNullException.ThrowIfNull(fileData);

			filePath = filePath.Replace(WindowsDirectorySeparator, DirectorySeparatorChar);

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

			if (directory.Length == 0)
			{
				directory = Space;
			}

			var archiveIndex = (ushort)0x7FFF;

			// Files with no data are only ever written into the directory file
			if (multiChunk && fileData.Length > 0)
			{
				if (CurrentChunkFileSize >= (uint)WriteChunkSize)
				{
					// Current chunk file is full, start a new one
					CurrentChunkFileIndex++;
					CurrentChunkFileSize = 0;

					if (CurrentChunkFileIndex >= 0x7FFF)
					{
						throw new InvalidOperationException("Reached the maximum amount of chunk files (32767).");
					}
				}

				archiveIndex = (ushort)CurrentChunkFileIndex;
				CurrentChunkFileSize += (uint)fileData.Length;
			}

			// Putting file data into SmallData is kind of a hack
			var entry = new PackageEntry
			{
				FileName = fileName,
				DirectoryName = directory,
				TypeName = extension,
				SmallData = fileData,
				CRC32 = Crc32.HashToUInt32(fileData),
				ArchiveIndex = archiveIndex,
			};

			if (Entries == null)
			{
				var stringComparer = Comparer == null ? null : StringComparer.FromComparison(Comparer.Comparison);
				Entries = new Dictionary<string, List<PackageEntry>>(stringComparer);
			}

			if (!Entries.TryGetValue(extension, out var typeEntries))
			{
				typeEntries = [];
				Entries[extension] = typeEntries;
			}

			typeEntries.Add(entry);

			return entry;
		}

		/// <summary>
		/// Opens and writes the given filename.
		///
		/// If any files were added with multi chunk enabled (see <see cref="AddFile(string, byte[], bool)"/>),
		/// chunk files (such as "example_001.vpk") will be written next to the given file,
		/// and the given filename should end with "_dir.vpk".
		///
		/// The VPK version that is written is controlled by <see cref="Version"/>.
		/// </summary>
		/// <param name="filename">The file to open and write.</param>
		public void Write(string filename)
		{
			ArgumentNullException.ThrowIfNull(filename);

			// Chunk files are named by appending "_###.vpk" to the base name, which is the given filename without the "_dir.vpk" suffix
			var chunkFileBaseName = StripDirVpkSuffixes(filename, out _);

			using var fs = new FileStream(filename, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
			fs.SetLength(0);

			Write(fs, chunkFileBaseName);
		}

		/// <summary>
		/// Writes to the given <see cref="Stream"/>.
		///
		/// This can not write packages that contain files added with multi chunk enabled,
		/// use <see cref="Write(string)"/> for that instead.
		///
		/// The VPK version that is written is controlled by <see cref="Version"/>.
		/// </summary>
		/// <param name="stream">The input <see cref="Stream"/> to write to.</param>
		public void Write(Stream stream)
		{
			Write(stream, chunkFileBaseName: null);
		}

		private void Write(Stream stream, string? chunkFileBaseName)
		{
			if (IsDirVPK)
			{
				throw new InvalidOperationException("This package was opened from a _dir.vpk, writing back is currently unsupported.");
			}

			ArgumentNullException.ThrowIfNull(stream);

			if (!stream.CanSeek || !stream.CanRead)
			{
				throw new InvalidOperationException("Stream must be seekable and readable.");
			}

			using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

			// TODO: input.SetLength()
			var streamOffset = stream.Position;
			ulong fileDataSectionSize = 0;
			var hasChunkFiles = false;

			var tree = new Dictionary<string, Dictionary<string, List<PackageEntry>>>();

			// Precalculate the file tree and set data offsets
			foreach (var typeEntries in Entries ?? [])
			{
				var typeTree = new Dictionary<string, List<PackageEntry>>();
				tree[typeEntries.Key] = typeTree;

				foreach (var entry in typeEntries.Value)
				{
					var directoryName = entry.DirectoryName.Length == 0 ? Space : entry.DirectoryName;

					if (!typeTree.TryGetValue(directoryName, out var directoryEntries))
					{
						directoryEntries = [];
						typeTree[directoryName] = directoryEntries;
					}

					directoryEntries.Add(entry);

					if (entry.ArchiveIndex != 0x7FFF)
					{
						hasChunkFiles = true;
						continue;
					}

					fileDataSectionSize += entry.TotalLength;

					if (fileDataSectionSize > int.MaxValue)
					{
						throw new InvalidOperationException("Package contents exceed 2GiB, add files with multi chunk enabled to split them into chunk files.");
					}
				}
			}

			if (hasChunkFiles && chunkFileBaseName == null)
			{
				throw new InvalidOperationException("This package contains files in chunk files, use Write(string) so that the chunk files can be written next to the directory file.");
			}

			// Header
			writer.Write(MAGIC);
			writer.Write(Version);
			writer.Write(0); // TreeSize, to be updated later

			if (Version >= 2)
			{
				writer.Write(0); // FileDataSectionSize, to be updated later
				writer.Write(0); // ArchiveMD5SectionSize, to be updated later
				writer.Write(48); // OtherMD5SectionSize
				writer.Write(0); // SignatureSectionSize
			}

			var headerSize = (int)(stream.Position - streamOffset);
			var archiveOffsets = new Dictionary<ushort, uint>();

			// Entries grouped per archive index in tree order, which is the order their data offsets are assigned in.
			// The directory file entries (0x7FFF) sort last.
			var archiveEntries = new SortedDictionary<ushort, List<PackageEntry>>();

			const byte NullByte = 0;

			// File tree data
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

						ref var fileOffset = ref CollectionsMarshal.GetValueRefOrAddDefault(archiveOffsets, entry.ArchiveIndex, out _);

						if ((ulong)fileOffset + fileLength > uint.MaxValue)
						{
							throw new InvalidOperationException($"Chunk file {entry.ArchiveIndex} exceeds 4GiB.");
						}

						if (!archiveEntries.TryGetValue(entry.ArchiveIndex, out var entriesForArchive))
						{
							entriesForArchive = [];
							archiveEntries[entry.ArchiveIndex] = entriesForArchive;
						}

						entriesForArchive.Add(entry);

						writer.Write(Encoding.UTF8.GetBytes(entry.FileName));
						writer.Write(NullByte);
						writer.Write(entry.CRC32);
						writer.Write((short)0); // SmallData, we will put it into data instead
						writer.Write(entry.ArchiveIndex);
						writer.Write(fileOffset);
						writer.Write(fileLength);
						writer.Write(ushort.MaxValue); // terminator, 0xFFFF

						fileOffset += fileLength;
					}

					writer.Write(NullByte);
				}

				writer.Write(NullByte);
			}

			writer.Write(NullByte);

			var fileTreeSize = stream.Position - headerSize;
			var chunkHashFractions = new List<ChunkHashFraction>();

			// File data, one archive at a time so only a single chunk file is open and written sequentially
			foreach (var (archiveIndex, entriesForArchive) in archiveEntries)
			{
				if (archiveIndex == 0x7FFF)
				{
					foreach (var entry in entriesForArchive)
					{
						writer.Write(GetEntryData(entry));
					}

					continue;
				}

				Debug.Assert(chunkFileBaseName != null);

				using var chunkFileWriter = new ChunkFileWriter(archiveIndex, GetArchiveIndexFullFilePath(chunkFileBaseName, archiveIndex), computeHashes: Version >= 2);

				foreach (var entry in entriesForArchive)
				{
					chunkFileWriter.Write(GetEntryData(entry));
				}

				chunkHashFractions.AddRange(chunkFileWriter.Finish());
			}

			var afterFileData = stream.Position;
			var fileDataSize = afterFileData - fileTreeSize - headerSize;

			// Archive MD5 section, contains hashes of chunk files
			var archiveMd5SectionBytes = SerializeChunkHashFractions(chunkHashFractions);
			writer.Write(archiveMd5SectionBytes);

			// Set tree size
			// TODO: It is possible to precalculate these sizes to remove seeking
			stream.Seek(streamOffset + (2 * sizeof(int)), SeekOrigin.Begin);
			writer.Write((int)fileTreeSize);

			if (Version < 2)
			{
				// Version 1 has no checksums
				stream.Seek(afterFileData, SeekOrigin.Begin);
				return;
			}

			writer.Write((int)fileDataSize);
			writer.Write(archiveMd5SectionBytes.Length); // ArchiveMD5SectionSize

			// Calculate file hashes
			stream.Seek(streamOffset, SeekOrigin.Begin);

			var buffer = ArrayPool<byte>.Shared.Rent(4096);

			try
			{
				// TODO: It is possible to transform these hashes while writing the file to remove seeking and stream reading
				using var fileTreeMD5 = MD5.Create();
				using var fullFileMD5 = MD5.Create();

				stream.ReadExactly(buffer, 0, headerSize);
				fullFileMD5.TransformBlock(buffer, 0, headerSize, null, 0);

				int bytesRead;
				var fileTreeRead = 0;

				// Calculate file tree size hash
				while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
				{
					fullFileMD5.TransformBlock(buffer, 0, bytesRead, null, 0);

					fileTreeRead += bytesRead;

					if (fileTreeRead >= fileTreeSize)
					{
						fileTreeMD5.TransformFinalBlock(buffer, 0, (int)(fileTreeSize - (fileTreeRead - bytesRead)));
						break;
					}

					fileTreeMD5.TransformBlock(buffer, 0, bytesRead, null, 0);
				}

				// Calculate remaining file data hash
				while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
				{
					fullFileMD5.TransformBlock(buffer, 0, bytesRead, null, 0);
				}

				// File tree hash
				var treeHash = fileTreeMD5.Hash;
				Debug.Assert(treeHash != null);

				writer.Write(treeHash);

				fullFileMD5.TransformBlock(treeHash, 0, treeHash.Length, null, 0);

				// Archive MD5 section hash
				var fileHashesMD5 = MD5.HashData(archiveMd5SectionBytes);
				writer.Write(fileHashesMD5);

				// Full file hash
				fullFileMD5.TransformFinalBlock(fileHashesMD5, 0, fileHashesMD5.Length);
				var fullHash = fullFileMD5.Hash;
				Debug.Assert(fullHash != null);
				writer.Write(fullHash);
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

			private readonly ushort archiveIndex;
			private readonly FileStream stream;
			private readonly IncrementalHash? md5;
			private readonly List<ChunkHashFraction> fractions = [];
			private uint fractionOffset;
			private uint bytesInFraction;

			public ChunkFileWriter(ushort archiveIndex, string filePath, bool computeHashes)
			{
				this.archiveIndex = archiveIndex;
				stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536);
				md5 = computeHashes ? IncrementalHash.CreateHash(HashAlgorithmName.MD5) : null;
			}

			public void Write(ReadOnlySpan<byte> data)
			{
				stream.Write(data);

				if (md5 == null)
				{
					return;
				}

				while (!data.IsEmpty)
				{
					var length = Math.Min(FileFractionSize - (int)bytesInFraction, data.Length);
					md5.AppendData(data[..length]);
					bytesInFraction += (uint)length;
					data = data[length..];

					if (bytesInFraction == FileFractionSize)
					{
						AddFraction();
					}
				}
			}

			public List<ChunkHashFraction> Finish()
			{
				if (md5 != null)
				{
					// The last fraction covers the remaining data, and is zero sized when
					// the chunk file size is an exact multiple of the fraction size
					AddFraction();
				}

				return fractions;
			}

			private void AddFraction()
			{
				fractions.Add(new ChunkHashFraction
				{
					ArchiveIndex = archiveIndex,
					HashType = EHashType.MD5,
					Offset = fractionOffset,
					Length = bytesInFraction,
					Checksum = md5!.GetHashAndReset(),
				});

				fractionOffset += bytesInFraction;
				bytesInFraction = 0;
			}

			public void Dispose()
			{
				stream.Dispose();
				md5?.Dispose();
			}
		}
	}
}
