using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SteamDatabase.ValvePak
{
	public partial class Package
	{
		/// <summary>
		/// Remove file from current package.
		/// </summary>
		/// <param name="entry">The package entry to remove.</param>
		/// <returns>Returns true if entry was removed, false otherwise.</returns>
		public bool RemoveFile(PackageEntry entry)
		{
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

			if (directory.Length == 0)
			{
				directory = " ";
			}

			// Putting file data into SmallData is kind of a hack
			var entry = new PackageEntry
			{
				FileName = fileName,
				DirectoryName = directory,
				TypeName = extension,
				SmallData = fileData,
				CRC32 = Crc32.Compute(fileData, fileData.Length),
				ArchiveIndex = 0x7FFF,
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
		/// Writes to the given <see cref="Stream"/>.
		/// </summary>
		/// <param name="stream">The input <see cref="Stream"/> to write to.</param>
		public void Write(Stream stream)
		{
			if (IsDirVPK)
			{
				throw new InvalidOperationException("This package was opened from a _dir.vpk, writing back is currently unsupported.");
			}

			if (!stream.CanSeek || !stream.CanRead)
			{
				throw new InvalidOperationException("Stream must be seekable and readable.");
			}

			using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

			// TODO: input.SetLength()
			var streamOffset = stream.Position;
			ulong fileDataSectionSize = 0;

			var tree = new Dictionary<string, Dictionary<string, List<PackageEntry>>>();

			// Precalculate the file tree and set data offsets
			foreach (var typeEntries in Entries)
			{
				var typeTree = new Dictionary<string, List<PackageEntry>>();
				tree[typeEntries.Key] = typeTree;

				foreach (var entry in typeEntries.Value)
				{
					var directoryName = entry.DirectoryName.Length == 0 ? " " : entry.DirectoryName;

					if (!typeTree.TryGetValue(directoryName, out var directoryEntries))
					{
						directoryEntries = new List<PackageEntry>();
						typeTree[directoryName] = directoryEntries;
					}

					directoryEntries.Add(entry);

					fileDataSectionSize += entry.TotalLength;

					if (fileDataSectionSize > int.MaxValue)
					{
						throw new InvalidOperationException("Package contents exceed 2GiB, and splitting packages is currently unsupported.");
					}
				}
			}

			// Header
			writer.Write(MAGIC);
			writer.Write(2); // Version
			writer.Write(0); // TreeSize, to be updated later
			writer.Write(0); // FileDataSectionSize, to be updated later
			writer.Write(0); // ArchiveMD5SectionSize
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

			// File data
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

			var afterFileData = stream.Position;
			var fileDataSize = afterFileData - fileTreeSize - headerSize;

			// Set tree size
			// TODO: It is possible to precalculate these sizes to remove seeking
			stream.Seek(streamOffset + (2 * sizeof(int)), SeekOrigin.Begin);
			writer.Write((int)fileTreeSize);
			writer.Write((int)fileDataSize);

			// Calculate file hashes
			stream.Seek(streamOffset, SeekOrigin.Begin);

			var buffer = ArrayPool<byte>.Shared.Rent(4096);

			try
			{
				// TODO: It is possible to transform these hashes while writing the file to remove seeking and stream reading
				using var fileTreeMD5 = MD5.Create();
				using var fullFileMD5 = MD5.Create();
				using var hashesMD5 = MD5.Create();

				stream.Read(buffer, 0, headerSize);
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
				writer.Write(fileTreeMD5.Hash);

				fullFileMD5.TransformBlock(fileTreeMD5.Hash, 0, fileTreeMD5.Hash.Length, null, 0);

				// File hashes hash
				var fileHashesMD5 = hashesMD5.ComputeHash(Array.Empty<byte>()); // We did not write any file hashes
				writer.Write(fileHashesMD5);

				// Full file hash
				fullFileMD5.TransformFinalBlock(fileHashesMD5, 0, fileHashesMD5.Length);
				writer.Write(fullFileMD5.Hash);
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(buffer);
			}
		}
	}
}
