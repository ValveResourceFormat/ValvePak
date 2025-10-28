using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SteamDatabase.ValvePak
{
	public partial class Package
	{
		/// <summary>
		/// Default chunk size for multi-chunk VPK files (200 MB).
		/// </summary>
		public const int DefaultChunkSize = 200 * 1024 * 1024;

		/// <summary>
		/// Size of chunk hash fractions for MD5 calculation (1 MB).
		/// </summary>
		internal const int ChunkHashFractionSize = 1024 * 1024;

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
		/// <returns>The added entry.</returns>
		public PackageEntry AddFile(string filePath, byte[] fileData)
		{
			ArgumentNullException.ThrowIfNull(filePath);

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

			// Putting file data into SmallData is kind of a hack
			var entry = new PackageEntry
			{
				FileName = fileName,
				DirectoryName = directory,
				TypeName = extension,
				SmallData = fileData,
				CRC32 = Crc32.HashToUInt32(fileData),
				ArchiveIndex = 0x7FFF,
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
		/// </summary>
		/// <param name="filename">The file to open and write.</param>
		public void Write(string filename)
		{
			using var fs = new FileStream(filename, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
			fs.SetLength(0);

			Write(fs);
		}

		/// <summary>
		/// Opens and writes the given filename with multi-chunk support.
		/// </summary>
		/// <param name="filename">The file to open and write.</param>
		/// <param name="chunkSize">Maximum size per chunk file in bytes. Use <see cref="DefaultChunkSize"/> for the default.</param>
		public void Write(string filename, int chunkSize)
		{
			ArgumentNullException.ThrowIfNull(filename);
			ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);

			if (Entries == null || Entries.Count == 0)
			{
				throw new InvalidOperationException("No entries to write.");
			}

			var basePath = filename.AsSpan();
			if (basePath.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
			{
				basePath = basePath[..^4];
			}

			if (basePath.EndsWith("_dir", StringComparison.OrdinalIgnoreCase))
			{
				basePath = basePath[..^4];
			}

			var allEntries = Entries.Values.SelectMany(e => e).ToList();

			AssignChunkPlacement(allEntries, chunkSize);

			WriteChunkDataFiles(basePath, allEntries);
			ArchiveMD5Entries.Clear();
			CalculateChunkHashes(basePath, allEntries, ArchiveMD5Entries);

			var originalIsDirVPK = IsDirVPK;
			IsDirVPK = true;

			try
			{
				var dirFilePath = $"{basePath}_dir.vpk";
				using var fs = new FileStream(dirFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
				Write(fs);
			}
			finally
			{
				IsDirVPK = originalIsDirVPK;
			}
		}

		/// <summary>
		/// Writes to the given <see cref="Stream"/>.
		/// </summary>
		/// <param name="stream">The input <see cref="Stream"/> to write to.</param>
		public void Write(Stream stream)
		{
			ArgumentNullException.ThrowIfNull(stream);

			if (!stream.CanSeek || !stream.CanRead)
			{
				throw new InvalidOperationException("Stream must be seekable and readable.");
			}

			using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

			// TODO: input.SetLength()
			var streamOffset = stream.Position;
			ulong fileDataSectionSize = 0;

			var tree = new Dictionary<string, Dictionary<string, List<PackageEntry>>>();

			// Build the file tree
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

					if (!IsDirVPK)
					{
						fileDataSectionSize += entry.TotalLength;

						if (fileDataSectionSize > int.MaxValue)
						{
							throw new InvalidOperationException("Package contents exceed 2GiB. Use Write(string, int) for multi-chunk VPKs.");
						}
					}
				}
			}

			// Header
			writer.Write(MAGIC);
			writer.Write(2); // Version
			writer.Write(0); // TreeSize, to be updated later
			writer.Write(0); // FileDataSectionSize, to be updated later
			writer.Write(0); // ArchiveMD5SectionSize, to be updated later
			writer.Write(48); // OtherMD5SectionSize
			writer.Write(0); // SignatureSectionSize

			var headerSize = (int)(stream.Position - streamOffset);
			uint fileOffset = 0;

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

						writer.Write(Encoding.UTF8.GetBytes(entry.FileName));
						writer.Write(NullByte);
						writer.Write(entry.CRC32);
						writer.Write((short)0); // SmallData, we will put it into data instead

						if (IsDirVPK || entry.ArchiveIndex != 0x7FFF)
						{
							writer.Write(entry.ArchiveIndex);
							writer.Write(entry.Offset);
						}
						else
						{
							writer.Write((ushort)0x7FFF);
							writer.Write(fileOffset);
							fileOffset += fileLength;
						}

						writer.Write(fileLength);
						writer.Write(ushort.MaxValue); // terminator, 0xFFFF
					}

					writer.Write(NullByte);
				}

				writer.Write(NullByte);
			}

			writer.Write(NullByte);

			var fileTreeSize = stream.Position - headerSize;

			// File data
			long fileDataSize = 0;
			if (!IsDirVPK)
			{
				var fileDataStart = stream.Position;
				foreach (var typeEntries in tree)
				{
					foreach (var directoryEntries in typeEntries.Value)
					{
						foreach (var entry in directoryEntries.Value)
						{
							ReadEntry(entry, out var fileData, validateCrc: false);
							writer.Write(fileData);
						}
					}
				}
				fileDataSize = stream.Position - fileDataStart;
			}

			var archiveMD5SectionStart = stream.Position;
			long archiveMD5SectionSize = 0;
			if (IsDirVPK && ArchiveMD5Entries.Count > 0)
			{
				foreach (var entry in ArchiveMD5Entries)
				{
					writer.Write(entry.ArchiveIndex);
					writer.Write(entry.Offset);
					writer.Write(entry.Length);
					writer.Write(entry.Checksum);
				}
				archiveMD5SectionSize = stream.Position - archiveMD5SectionStart;
			}

			// Set tree size
			stream.Seek(streamOffset + (2 * sizeof(int)), SeekOrigin.Begin);
			writer.Write((int)fileTreeSize);
			writer.Write((int)fileDataSize);
			writer.Write((int)archiveMD5SectionSize);

			// Calculate file hashes
			stream.Seek(streamOffset, SeekOrigin.Begin);

			var buffer = ArrayPool<byte>.Shared.Rent(4096);

			try
			{
				// TODO: It is possible to transform these hashes while writing the file to remove seeking and stream reading
				using var fileTreeMD5 = MD5.Create();
				using var fullFileMD5 = MD5.Create();

				stream.Read(buffer, 0, headerSize);
				fullFileMD5.TransformBlock(buffer, 0, headerSize, null, 0);

				int bytesRead;
				var fileTreeRead = 0;

				// Calculate file tree size hash
				while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
				{
					fileTreeRead += bytesRead;

					if (fileTreeRead >= fileTreeSize)
					{
						var treeHashBytes = (int)(fileTreeSize - (fileTreeRead - bytesRead));
						fullFileMD5.TransformBlock(buffer, 0, treeHashBytes, null, 0);
						fileTreeMD5.TransformFinalBlock(buffer, 0, treeHashBytes);

						stream.Seek(streamOffset + headerSize + fileTreeSize, SeekOrigin.Begin);
						break;
					}

					fullFileMD5.TransformBlock(buffer, 0, bytesRead, null, 0);
					fileTreeMD5.TransformBlock(buffer, 0, bytesRead, null, 0);
				}

				// Calculate remaining file data hash
				var fileDataRead = 0L;
				while (fileDataRead < fileDataSize && (bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
				{
					var bytesToHash = (int)Math.Min(bytesRead, fileDataSize - fileDataRead);
					fullFileMD5.TransformBlock(buffer, 0, bytesToHash, null, 0);
					fileDataRead += bytesToHash;

					if (fileDataRead >= fileDataSize)
					{
						stream.Seek(streamOffset + headerSize + fileTreeSize + fileDataSize, SeekOrigin.Begin);
						break;
					}
				}

				byte[]? archiveMD5SectionData = null;
				if (archiveMD5SectionSize > 0)
				{
					archiveMD5SectionData = new byte[archiveMD5SectionSize];
					stream.ReadExactly(archiveMD5SectionData, 0, (int)archiveMD5SectionSize);
					fullFileMD5.TransformBlock(archiveMD5SectionData, 0, (int)archiveMD5SectionSize, null, 0);
				}

				// File tree hash
				var treeHash = fileTreeMD5.Hash;
				Debug.Assert(treeHash != null);

				writer.Write(treeHash);

				fullFileMD5.TransformBlock(treeHash, 0, treeHash.Length, null, 0);

				// File hashes hash
				var archiveMD5EntriesHash = archiveMD5SectionData != null
					? MD5.HashData(archiveMD5SectionData)
					: MD5.HashData([]);
				writer.Write(archiveMD5EntriesHash);

				// Full file hash
				fullFileMD5.TransformFinalBlock(archiveMD5EntriesHash, 0, archiveMD5EntriesHash.Length);
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
		/// Assigns chunk indices and offsets to all entries using next-fit algorithm.
		/// </summary>
		private static void AssignChunkPlacement(List<PackageEntry> entries, int chunkSize)
		{
			ushort currentChunk = 0;
			uint currentOffset = 0;

			foreach (var entry in entries)
			{
				var fileSize = entry.TotalLength;

				if (currentOffset >= chunkSize)
				{
					currentChunk++;
					currentOffset = 0;

					if (currentChunk >= 0x7FFF)
					{
						throw new InvalidOperationException("Too many chunk files (maximum 32767).");
					}
				}

				entry.ArchiveIndex = currentChunk;
				entry.Offset = currentOffset;
				currentOffset += fileSize;
			}
		}

		/// <summary>
		/// Writes chunk data files (e.g., pakname_000.vpk, pakname_001.vpk).
		/// </summary>
		private void WriteChunkDataFiles(ReadOnlySpan<char> basePath, List<PackageEntry> entries)
		{
			var chunkGroups = entries
				.Where(e => e.ArchiveIndex != 0x7FFF)
				.GroupBy(e => e.ArchiveIndex)
				.OrderBy(g => g.Key);

			foreach (var chunkGroup in chunkGroups)
			{
				var chunkPath = $"{basePath}_{chunkGroup.Key:D3}.vpk";
				using var fs = new FileStream(chunkPath, FileMode.Create, FileAccess.Write, FileShare.None);
				using var writer = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false);

				foreach (var entry in chunkGroup.OrderBy(e => e.Offset))
				{
					ReadEntry(entry, out var fileData, validateCrc: false);
					writer.Write(fileData);
				}
			}
		}

		/// <summary>
		/// Calculates MD5 hashes for all chunk files in 1MB fractions.
		/// </summary>
		private static void CalculateChunkHashes(ReadOnlySpan<char> basePath, List<PackageEntry> entries, List<ArchiveMD5SectionEntry> archiveMD5Entries)
		{
			var chunkIndices = entries
				.Where(e => e.ArchiveIndex != 0x7FFF)
				.Select(e => e.ArchiveIndex)
				.Distinct()
				.OrderBy(i => i);

			foreach (var chunkIndex in chunkIndices)
			{
				var chunkPath = $"{basePath}_{chunkIndex:D3}.vpk";
				using var fs = new FileStream(chunkPath, FileMode.Open, FileAccess.Read, FileShare.Read);

				uint offset = 0;
				var buffer = new byte[ChunkHashFractionSize];

				while (true)
				{
					var bytesRead = fs.Read(buffer, 0, buffer.Length);
					if (bytesRead == 0)
					{
						break;
					}

					var hash = MD5.HashData(buffer.AsSpan(0, bytesRead));

					archiveMD5Entries.Add(new ArchiveMD5SectionEntry
					{
						ArchiveIndex = chunkIndex,
						Offset = offset,
						Length = (uint)bytesRead,
						Checksum = hash
					});

					offset += (uint)bytesRead;
				}
			}
		}
	}
}
