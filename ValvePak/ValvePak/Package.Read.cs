using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Hashing;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Text;

namespace ValvePak
{
	/// <summary>
	/// VPK (Valve Pak) files are uncompressed archives used to package game content.
	/// </summary>
	public partial class Package : IDisposable
	{
		/// <summary>
		/// Opens and reads the given filename.
		/// The file is held open until the object is disposed.
		/// </summary>
		/// <param name="filename">The file to open and read.</param>
		public void Read(string filename)
		{
			SetFileName(filename);

			var fs = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

			Read(fs);
		}

		/// <summary>
		/// Reads the given <see cref="Stream"/>.
		/// A package can only be read once.
		///
		/// The stream must stay open while the package is used, entry data is read from it on demand.
		/// It is disposed together with the package.
		/// </summary>
		/// <param name="input">The input <see cref="Stream"/> to read from.</param>
		public void Read(Stream input)
		{
			ArgumentNullException.ThrowIfNull(input);
			ThrowIfAlreadyRead();

			if (FileName == null)
			{
				throw new InvalidOperationException("If you call Read() directly with a stream, you must call SetFileName() first.");
			}

			StreamOffset = input.Position;
			Reader = new BinaryReader(input);

			if (Reader.ReadUInt32() != MAGIC)
			{
				throw new InvalidDataException("Given file is not a VPK.");
			}

			var version = Reader.ReadUInt32();
			TreeSize = Reader.ReadUInt32();

			if (version == 1)
			{
				// Nothing else
			}
			else if (version == 2)
			{
				FileDataSectionSize = Reader.ReadUInt32();
				ArchiveMD5SectionSize = Reader.ReadUInt32();
				OtherMD5SectionSize = Reader.ReadUInt32();
				SignatureSectionSize = Reader.ReadUInt32();
			}
			else if (version == 0x00030002) // Apex Legends, Titanfall
			{
				throw new NotSupportedException("Respawn uses customized vpk format which this library does not support.");
			}
			else
			{
				throw new InvalidDataException($"Bad VPK version. ({version})");
			}

			Version = version;

			HeaderSize = (uint)(input.Position - StreamOffset);

			ReadEntries();

			if (Version == 2)
			{
				// Skip over file data, if any
				input.Position += FileDataSectionSize;

				ReadArchiveMD5Section();
				ReadOtherMD5Section();
				ReadSignatureSection();
			}
		}

		/// <summary>
		/// Reads the entry from the VPK package.
		/// </summary>
		/// <param name="entry">Package entry.</param>
		/// <param name="output">Output buffer.</param>
		/// <param name="validateCrc">If true, CRC32 will be calculated and verified for read data.</param>
		public void ReadEntry(PackageEntry entry, out byte[] output, bool validateCrc = true)
		{
			ArgumentNullException.ThrowIfNull(entry);

			output = new byte[entry.TotalLength];

			ReadEntry(entry, output, validateCrc);
		}

		/// <summary>
		/// Reads the entry from the VPK package into a user-provided output byte array.
		/// </summary>
		/// <param name="entry">Package entry.</param>
		/// <param name="output">Output buffer, size of the buffer must be at least <see cref="PackageEntry.TotalLength"/>.</param>
		/// <param name="validateCrc">If true, CRC32 will be calculated and verified for read data.</param>
		public void ReadEntry(PackageEntry entry, byte[] output, bool validateCrc = true)
		{
			ArgumentNullException.ThrowIfNull(entry);
			ArgumentNullException.ThrowIfNull(output);

			var totalLength = (int)entry.TotalLength;

			if (output.Length < totalLength)
			{
				throw new ArgumentOutOfRangeException(nameof(output), "Size of the provided output buffer is smaller than entry.TotalLength.");
			}

			if (entry.SmallData.Length > 0)
			{
				entry.SmallData.CopyTo(output, 0);
			}

			if (entry.Length > 0)
			{
#pragma warning disable CA2000 // Dispose objects before losing scope
				var fs = GetFileStream(entry.ArchiveIndex);
#pragma warning restore CA2000 // Stream is base reader stream when reading from non-split vpk, it should not be disposed

				try
				{
					fs.Seek(entry.Offset, SeekOrigin.Current);

					int length = (int)entry.Length;
					int readOffset = entry.SmallData.Length;
					int bytesRead;
					int totalRead = 0;
					while ((bytesRead = fs.Read(output, readOffset + totalRead, length - totalRead)) != 0)
					{
						totalRead += bytesRead;
					}
				}
				finally
				{
					if (entry.ArchiveIndex != 0x7FFF)
					{
						fs.Dispose();
					}
				}
			}

			if (!validateCrc)
			{
				return;
			}

			var actualChecksum = Crc32.HashToUInt32(output.AsSpan(0, totalLength));

			if (entry.CRC32 != actualChecksum)
			{
				throw new InvalidDataException($"CRC32 mismatch for read data (expected {entry.CRC32:X2}, got {actualChecksum:X2}).");
			}
		}

		/// <summary>
		/// Reads exactly <see cref="TreeSize"/> bytes of the directory tree and parses the entries from them.
		/// </summary>
		private void ReadEntries()
		{
			Debug.Assert(Reader != null);

			var input = Reader.BaseStream;

			if (TreeSize > input.Length - input.Position)
			{
				throw new InvalidDataException($"Directory tree size ({TreeSize}) is larger than the rest of the file ({input.Length - input.Position}).");
			}

			var treeSize = (int)TreeSize;
			var tree = ArrayPool<byte>.Shared.Rent(treeSize);

			try
			{
				input.ReadExactly(tree, 0, treeSize);

				Entries = ParseEntries(tree.AsSpan(0, treeSize));
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(tree);
			}
		}

		private Dictionary<string, List<PackageEntry>> ParseEntries(ReadOnlySpan<byte> tree)
		{
			const int EntryMetadataSize = 18;

			var typeEntries = CreateEntriesDictionary();
			var position = 0;

			// Types
			while (true)
			{
				var typeName = ReadNullTermUtf8String(tree, ref position);

				if (typeName.Length == 0)
				{
					break;
				}

				var entries = new List<PackageEntry>();

				// Directories
				while (true)
				{
					var directoryName = ReadNullTermUtf8String(tree, ref position);

					if (directoryName.Length == 0)
					{
						break;
					}

					// Files
					while (true)
					{
						var fileName = ReadNullTermUtf8String(tree, ref position);

						if (fileName.Length == 0)
						{
							break;
						}

						if (tree.Length - position < EntryMetadataSize)
						{
							throw new InvalidDataException("Directory tree ends in the middle of an entry.");
						}

						var metadata = tree.Slice(position, EntryMetadataSize);
						position += EntryMetadataSize;

						var terminator = BinaryPrimitives.ReadUInt16LittleEndian(metadata[16..]);

						if (terminator != 0xFFFF)
						{
							throw new FormatException($"Invalid terminator, was 0x{terminator:X} but expected 0x{0xFFFF:X}.");
						}

						var entry = new PackageEntry
						{
							FileName = fileName,
							DirectoryName = directoryName,
							TypeName = typeName,
							CRC32 = BinaryPrimitives.ReadUInt32LittleEndian(metadata),
							ArchiveIndex = BinaryPrimitives.ReadUInt16LittleEndian(metadata[6..]),
							Offset = BinaryPrimitives.ReadUInt32LittleEndian(metadata[8..]),
							Length = BinaryPrimitives.ReadUInt32LittleEndian(metadata[12..]),
						};

						var smallDataSize = BinaryPrimitives.ReadUInt16LittleEndian(metadata[4..]);

						if (smallDataSize > 0)
						{
							if (tree.Length - position < smallDataSize)
							{
								throw new InvalidDataException("Directory tree ends in the middle of preload data.");
							}

							entry.SmallData = tree.Slice(position, smallDataSize).ToArray();
							position += smallDataSize;
						}

						entries.Add(entry);
					}
				}

				if (Comparer != null)
				{
					// Sorting at the end is faster than doing BinarySearch+Insert
					entries.Sort(Comparer);
				}

				typeEntries.Add(typeName, entries);
			}

			return typeEntries;
		}

		private void ReadArchiveMD5Section()
		{
			Debug.Assert(Reader != null);

			FileSizeBeforeArchiveMD5Entries = (uint)(Reader.BaseStream.Position - StreamOffset);

			if (ArchiveMD5SectionSize == 0)
			{
				AccessPackFileHashes = [];
				return;
			}

			if (ArchiveMD5SectionSize % ChunkHashFraction.SectionEntrySize != 0)
			{
				throw new InvalidDataException($"Archive MD5 section size ({ArchiveMD5SectionSize}) is not a multiple of {ChunkHashFraction.SectionEntrySize}.");
			}

			var entries = (int)(ArchiveMD5SectionSize / ChunkHashFraction.SectionEntrySize);

			AccessPackFileHashes = new List<ChunkHashFraction>(entries);

			for (var i = 0; i < entries; i++)
			{
				var hashFraction = new ChunkHashFraction
				{
					ArchiveIndex = Reader.ReadUInt16(),
					HashType = (EHashType)Reader.ReadUInt16(),
					Offset = Reader.ReadUInt32(),
					Length = Reader.ReadUInt32(),
					Checksum = Reader.ReadBytes(16)
				};

				if (hashFraction.ArchiveIndex == 0 && hashFraction.HashType == (EHashType)0x8000)
				{
					hashFraction.ArchiveIndex = 0x7FFF;
					hashFraction.HashType = EHashType.MD5;
				}

				AccessPackFileHashes.Add(hashFraction);
			}
		}

		private void ReadOtherMD5Section()
		{
			if (OtherMD5SectionSize != 48)
			{
				return;
			}

			Debug.Assert(Reader != null);

			TreeChecksum = Reader.ReadBytes(16);
			ArchiveMD5EntriesChecksum = Reader.ReadBytes(16);
			FileSizeBeforeWholeFileHash = (uint)(Reader.BaseStream.Position - StreamOffset);
			WholeFileChecksum = Reader.ReadBytes(16);
		}

		private void ReadSignatureSection()
		{
			Debug.Assert(Reader != null);

			FileSizeBeforeSignature = (uint)(Reader.BaseStream.Position - StreamOffset);

			if (SignatureSectionSize == 0)
			{
				return;
			}

			var publicKeySize = Reader.ReadInt32();

			if (SignatureSectionSize == 20 && publicKeySize == MAGIC)
			{
				SignatureType = (ESignatureType)Reader.ReadInt32();
				publicKeySize = Reader.ReadInt32();
				var signatureSize2 = Reader.ReadInt32();
				Reader.ReadInt32(); // reserved?

				if (publicKeySize != 0)
				{
					PublicKey = ReadSignatureBytes(publicKeySize, "public key");
				}

				if (signatureSize2 != 0)
				{
					Signature = ReadSignatureBytes(signatureSize2, "signature");
				}

				return;
			}

			SignatureType = ESignatureType.FullFile;
			PublicKey = ReadSignatureBytes(publicKeySize, "public key");

			var signatureSize = Reader.ReadInt32();
			Signature = ReadSignatureBytes(signatureSize, "signature");
		}

		private byte[] ReadSignatureBytes(int size, string name)
		{
			Debug.Assert(Reader != null);

			var input = Reader.BaseStream;

			if (size < 0 || size > input.Length - input.Position)
			{
				throw new InvalidDataException($"Invalid {name} size in the signature section ({size}).");
			}

			return Reader.ReadBytes(size);
		}

		private Stream GetFileStream(ushort archiveIndex)
		{
			Stream stream;

			if (archiveIndex != 0x7FFF)
			{
				var fileName = GetArchiveIndexFullFilePath(FileName, archiveIndex);

				stream = new FileStream(fileName, FileMode.Open, FileAccess.Read);
			}
			else
			{
				Debug.Assert(Reader != null);

				stream = Reader.BaseStream;
				stream.Seek(StreamOffset + HeaderSize + TreeSize, SeekOrigin.Begin);
			}

			return stream;
		}

		/// <summary>
		/// Returns <see cref="MemoryMappedViewStream"/> when possible, otherwise reads entry into a byte array and returns <see cref="MemoryStream"/>.
		/// This only works when entries have no preload bytes.
		/// Files smaller or equal to 4096 bytes will be read into memory.
		///
		/// When trying to map a file in a non-split packages (<see cref="IsDirVPK"/>),
		/// this will only memory map if the package was read by providing <see cref="FileStream"/> or using <see cref="Read(string)"/>.
		/// </summary>
		/// <param name="entry">Package entry.</param>
		/// <returns>Stream for a given package entry contents.</returns>
		public Stream GetMemoryMappedStreamIfPossible(PackageEntry entry)
		{
			ArgumentNullException.ThrowIfNull(entry);

			if (entry.Length <= 4096 || entry.SmallData.Length > 0)
			{
				ReadEntry(entry, out var output, false);
				return new MemoryStream(output);
			}

			if (!MemoryMappedPaks.TryGetValue(entry.ArchiveIndex, out var stream))
			{
				string path;

				// If the package was opened by providing a file path, we can memory map non directory files
				if (entry.ArchiveIndex == 0x7FFF)
				{
					Debug.Assert(Reader != null);

					if (Reader.BaseStream is FileStream fileStream)
					{
						path = fileStream.Name;
					}
					else
					{
						// This package was read by in a stream that is not FileStream, or not using Read(path).
						// In this case fallback to reading the file into memory. We could rely on the what was passed into SetFileName, but why.
						ReadEntry(entry, out var output, false);
						return new MemoryStream(output);
					}
				}
				else
				{
					path = GetArchiveIndexFullFilePath(FileName, entry.ArchiveIndex);
				}

				stream = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
				MemoryMappedPaks[entry.ArchiveIndex] = stream;
			}

			long offset = entry.Offset;

			if (entry.ArchiveIndex == 0x7FFF)
			{
				offset += StreamOffset + HeaderSize + TreeSize;
			}

			return stream.CreateViewStream(offset, entry.Length, MemoryMappedFileAccess.Read);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static string GetArchiveIndexFullFilePath(string? baseFileName, ushort archiveIndex)
		{
			return $"{baseFileName}_{archiveIndex:D3}.vpk";
		}

		/// <summary>
		/// Reads a null terminated string from the directory tree.
		/// Reaching the end of the tree reads as an empty string, which ends the current list like Valve's reader does.
		/// </summary>
		private static string ReadNullTermUtf8String(ReadOnlySpan<byte> tree, ref int position)
		{
			if (position >= tree.Length)
			{
				return string.Empty;
			}

			var length = tree[position..].IndexOf((byte)0);

			if (length < 0)
			{
				throw new InvalidDataException("Directory tree ends in the middle of a string.");
			}

			var value = Encoding.UTF8.GetString(tree.Slice(position, length));
			position += length + 1;

			return value;
		}
	}
}
