using System;
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
			ObjectDisposedException.ThrowIf(IsDisposed, this);

			SetFileName(filename);

			var fs = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

			Read(fs);
		}

		/// <summary>
		/// Reads the given <see cref="Stream"/>.
		/// If this package was read before, everything read from the previous package is replaced.
		///
		/// The stream must stay open while the package is used, entry data is read from it on demand.
		/// It is disposed together with the package.
		/// </summary>
		/// <param name="input">The input <see cref="Stream"/> to read from.</param>
		public void Read(Stream input)
		{
			ArgumentNullException.ThrowIfNull(input);
			ObjectDisposedException.ThrowIf(IsDisposed, this);

			if (FileName == null)
			{
				throw new InvalidOperationException("If you call Read() directly with a stream, you must call SetFileName() first.");
			}

			ResetReadState(input);

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
		/// This method is safe to call from multiple threads.
		/// </summary>
		/// <param name="entry">Package entry.</param>
		/// <param name="output">Output buffer.</param>
		/// <param name="validateCrc">If true, CRC32 will be calculated and verified for read data.</param>
		/// <exception cref="EndOfStreamException">The archive ends before all bytes of the entry could be read.</exception>
		public void ReadEntry(PackageEntry entry, out byte[] output, bool validateCrc = true)
		{
			ArgumentNullException.ThrowIfNull(entry);
			ObjectDisposedException.ThrowIf(IsDisposed, this);

			output = new byte[entry.TotalLength];

			ReadEntry(entry, output, validateCrc);
		}

		/// <summary>
		/// Reads the entry from the VPK package into a user-provided output byte array.
		/// This method is safe to call from multiple threads.
		/// </summary>
		/// <param name="entry">Package entry.</param>
		/// <param name="output">Output buffer, size of the buffer must be at least <see cref="PackageEntry.TotalLength"/>.</param>
		/// <param name="validateCrc">If true, CRC32 will be calculated and verified for read data.</param>
		/// <exception cref="EndOfStreamException">The archive ends before all bytes of the entry could be read.</exception>
		public void ReadEntry(PackageEntry entry, byte[] output, bool validateCrc = true)
		{
			ArgumentNullException.ThrowIfNull(entry);
			ArgumentNullException.ThrowIfNull(output);
			ObjectDisposedException.ThrowIf(IsDisposed, this);

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
				var length = (int)entry.Length;
				var bytesRead = ReadArchiveData(entry.ArchiveIndex, entry.Offset, output.AsSpan(entry.SmallData.Length, length));

				if (bytesRead != length)
				{
					throw new EndOfStreamException($"Reached the end of archive {entry.ArchiveIndex} after reading {bytesRead} of {length} bytes for '{entry.GetFullPath()}'.");
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
		/// Largest directory tree in bytes that is read into a single buffer, larger trees are parsed from the stream.
		/// </summary>
		private const uint MaxTreeBufferSize = 64 * 1024 * 1024;

		private void ReadEntries()
		{
			Debug.Assert(Reader != null);

			var input = Reader.BaseStream;
			var treeStart = input.Position;

			if (TreeSize > 0 && TreeSize <= MaxTreeBufferSize && input.CanSeek && TreeSize <= input.Length - treeStart)
			{
				var tree = GC.AllocateUninitializedArray<byte>((int)TreeSize);
				var treeLength = input.ReadAtLeast(tree, tree.Length, throwOnEndOfStream: false);

				if (TryReadEntries(tree.AsSpan(0, treeLength), out var typeEntries, out var treeSize))
				{
					Entries = typeEntries;
					TreeSize = (uint)treeSize;
					input.Position = treeStart + treeSize;
					return;
				}

				input.Position = treeStart;
			}

			ReadEntriesFromStream();
		}

		/// <summary>
		/// Parses the directory tree from a buffer.
		/// Returns false if the buffer ends before the tree does, in which case it has to be read from the stream.
		/// </summary>
		private bool TryReadEntries(ReadOnlySpan<byte> tree, out Dictionary<string, List<PackageEntry>> typeEntries, out int treeSize)
		{
			const int EntryMetadataSize = 18;

			typeEntries = CreateEntriesDictionary();
			treeSize = 0;

			var position = 0;

			// Types
			while (true)
			{
				if (!TryReadNullTermUtf8String(tree, ref position, out var typeName))
				{
					return false;
				}

				if (typeName.Length == 0)
				{
					break;
				}

				var entries = new List<PackageEntry>();

				// Directories
				while (true)
				{
					if (!TryReadNullTermUtf8String(tree, ref position, out var directoryName))
					{
						return false;
					}

					if (directoryName.Length == 0)
					{
						break;
					}

					// Files
					while (true)
					{
						if (!TryReadNullTermUtf8String(tree, ref position, out var fileName))
						{
							return false;
						}

						if (fileName.Length == 0)
						{
							break;
						}

						if (tree.Length - position < EntryMetadataSize)
						{
							return false;
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
								return false;
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

			treeSize = position;

			return true;
		}

		private void ReadEntriesFromStream()
		{
			Debug.Assert(Reader != null);

			var typeEntries = CreateEntriesDictionary();
			using var ms = new MemoryStream();

			// Types
			while (true)
			{
				var typeName = ReadNullTermUtf8String(Reader, ms);

				if (string.IsNullOrEmpty(typeName))
				{
					break;
				}

				var entries = new List<PackageEntry>();

				// Directories
				while (true)
				{
					var directoryName = ReadNullTermUtf8String(Reader, ms);

					if (string.IsNullOrEmpty(directoryName))
					{
						break;
					}

					// Files
					while (true)
					{
						var fileName = ReadNullTermUtf8String(Reader, ms);

						if (string.IsNullOrEmpty(fileName))
						{
							break;
						}

						var entry = new PackageEntry
						{
							FileName = fileName,
							DirectoryName = directoryName,
							TypeName = typeName,
							CRC32 = Reader.ReadUInt32()
						};
						var smallDataSize = Reader.ReadUInt16();
						entry.ArchiveIndex = Reader.ReadUInt16();
						entry.Offset = Reader.ReadUInt32();
						entry.Length = Reader.ReadUInt32();

						var terminator = Reader.ReadUInt16();

						if (terminator != 0xFFFF)
						{
							throw new FormatException($"Invalid terminator, was 0x{terminator:X} but expected 0x{0xFFFF:X}.");
						}

						if (smallDataSize > 0)
						{
							entry.SmallData = new byte[smallDataSize];
							Reader.BaseStream.ReadExactly(entry.SmallData);
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

			Entries = typeEntries;

			// Set to real size that was read for hash verification, in case it was tampered with
			TreeSize = (uint)(Reader.BaseStream.Position - StreamOffset - HeaderSize);
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

			var input = Reader.BaseStream;
			var sectionStart = input.Position;
			var sectionLength = (long)entries * ChunkHashFraction.SectionEntrySize;

			if (sectionLength <= Array.MaxLength && input.CanSeek && sectionLength <= input.Length - sectionStart)
			{
				var section = GC.AllocateUninitializedArray<byte>((int)sectionLength);

				if (input.ReadAtLeast(section, section.Length, throwOnEndOfStream: false) == section.Length)
				{
					for (var i = 0; i < entries; i++)
					{
						var data = section.AsSpan(i * ChunkHashFraction.SectionEntrySize, ChunkHashFraction.SectionEntrySize);

						AddChunkHashFraction(new ChunkHashFraction
						{
							ArchiveIndex = BinaryPrimitives.ReadUInt16LittleEndian(data),
							HashType = (EHashType)BinaryPrimitives.ReadUInt16LittleEndian(data[2..]),
							Offset = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
							Length = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
							Checksum = data[12..].ToArray()
						});
					}

					return;
				}

				input.Position = sectionStart;
			}

			for (var i = 0; i < entries; i++)
			{
				AddChunkHashFraction(new ChunkHashFraction
				{
					ArchiveIndex = Reader.ReadUInt16(),
					HashType = (EHashType)Reader.ReadUInt16(),
					Offset = Reader.ReadUInt32(),
					Length = Reader.ReadUInt32(),
					Checksum = Reader.ReadBytes(16)
				});
			}
		}

		private void AddChunkHashFraction(ChunkHashFraction hashFraction)
		{
			if (hashFraction.ArchiveIndex == 0 && hashFraction.HashType == (EHashType)0x8000)
			{
				hashFraction.ArchiveIndex = 0x7FFF;
				hashFraction.HashType = EHashType.MD5;
			}

			AccessPackFileHashes.Add(hashFraction);
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

			if (size < 0 || (input.CanSeek && size > input.Length - input.Position))
			{
				throw new InvalidDataException($"Invalid {name} size in the signature section ({size}).");
			}

			return Reader.ReadBytes(size);
		}

		/// <summary>
		/// Returns the archive for the given index, opening its file on first use.
		/// The embedded archive (0x7FFF) shares the handle of the <see cref="FileStream"/> the package was read from,
		/// and is null if the package was read from any other kind of stream.
		/// </summary>
		private ArchiveFile? GetArchive(ushort archiveIndex)
		{
			if (Archives.TryGetValue(archiveIndex, out var archive))
			{
				return archive;
			}

			if (archiveIndex == 0x7FFF && Reader?.BaseStream is not FileStream)
			{
				ObjectDisposedException.ThrowIf(IsDisposed, this);

				return null;
			}

			lock (ArchivesLock)
			{
				ObjectDisposedException.ThrowIf(IsDisposed, this);

				if (Archives.TryGetValue(archiveIndex, out archive))
				{
					return archive;
				}

				if (archiveIndex == 0x7FFF)
				{
					if (Reader?.BaseStream is not FileStream fileStream)
					{
						return null;
					}

					archive = new ArchiveFile(fileStream.SafeFileHandle, ownsHandle: false);
				}
				else
				{
					var fileName = GetArchiveIndexFullFilePath(FileName, archiveIndex);
					var handle = File.OpenHandle(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

					archive = new ArchiveFile(handle, ownsHandle: true);
				}

				Archives[archiveIndex] = archive;

				return archive;
			}
		}

		/// <summary>
		/// Returns the offset in the archive file of data at the given offset in the archive data section.
		/// The package itself may start at a non-zero position of the stream it was read from.
		/// </summary>
		private long GetArchiveDataOffset(ushort archiveIndex, long offset)
		{
			if (archiveIndex == 0x7FFF)
			{
				return StreamOffset + offset + HeaderSize + TreeSize;
			}

			return offset;
		}

		/// <summary>
		/// Reads archive data at the given offset until the output is full or the end of the archive is reached.
		/// This method is safe to call from multiple threads.
		/// </summary>
		/// <returns>The number of bytes read.</returns>
		private int ReadArchiveData(ushort archiveIndex, long offset, Span<byte> output)
		{
			var fileOffset = GetArchiveDataOffset(archiveIndex, offset);
			var archive = GetArchive(archiveIndex);
			var totalRead = 0;
			int bytesRead;

			if (archive != null)
			{
				while (totalRead < output.Length && (bytesRead = RandomAccess.Read(archive.Handle, output[totalRead..], fileOffset + totalRead)) != 0)
				{
					totalRead += bytesRead;
				}

				return totalRead;
			}

			var reader = Reader;
			ObjectDisposedException.ThrowIf(reader == null, this);

			lock (reader)
			{
				var stream = reader.BaseStream;
				stream.Position = fileOffset;

				while (totalRead < output.Length && (bytesRead = stream.Read(output[totalRead..])) != 0)
				{
					totalRead += bytesRead;
				}
			}

			return totalRead;
		}

		/// <summary>
		/// Returns a stream that reads the entry directly from a memory mapping of its archive when possible,
		/// otherwise reads entry into a byte array and returns <see cref="MemoryStream"/>.
		/// This only works when entries have no preload bytes.
		/// Files smaller or equal to 4096 bytes will be copied into a <see cref="MemoryStream"/>.
		///
		/// When trying to map a file in a non-split packages (<see cref="IsDirVPK"/>),
		/// this will only memory map if the package was read by providing <see cref="FileStream"/> or using <see cref="Read(string)"/>.
		///
		/// Each archive is mapped once and stays mapped until the package is disposed.
		/// Returned streams can only be read while the package is open, they throw <see cref="ObjectDisposedException"/>
		/// once the package is disposed or read again.
		/// This method is safe to call from multiple threads.
		/// </summary>
		/// <param name="entry">Package entry.</param>
		/// <returns>Stream for a given package entry contents.</returns>
		/// <exception cref="EndOfStreamException">The archive ends before all bytes of the entry could be read.</exception>
		public Stream GetMemoryMappedStreamIfPossible(PackageEntry entry)
		{
			ArgumentNullException.ThrowIfNull(entry);
			ObjectDisposedException.ThrowIf(IsDisposed, this);

			if (entry.Length > 0 && entry.SmallData.Length == 0 && GetArchive(entry.ArchiveIndex) is { } archive)
			{
				var offset = GetArchiveDataOffset(entry.ArchiveIndex, entry.Offset);
				var mapping = archive.GetMapping();
				var isInRange = mapping != null && offset + entry.Length <= archive.MappedLength;

				if (isInRange && archive.View is { } view)
				{
					if (entry.Length > 4096)
					{
						return new UnmanagedMemoryStream(view.SafeMemoryMappedViewHandle, view.PointerOffset + offset, entry.Length);
					}

					var data = new byte[entry.Length];
					view.SafeMemoryMappedViewHandle.ReadSpan((ulong)(view.PointerOffset + offset), data.AsSpan());

					return new MemoryStream(data);
				}

				if (isInRange && entry.Length > 4096)
				{
					return mapping!.CreateViewStream(offset, entry.Length, MemoryMappedFileAccess.Read);
				}
			}

			ReadEntry(entry, out var output, false);

			return new MemoryStream(output);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static string GetArchiveIndexFullFilePath(string? baseFileName, ushort archiveIndex)
		{
			return $"{baseFileName}_{archiveIndex:D3}.vpk";
		}

		private static bool TryReadNullTermUtf8String(ReadOnlySpan<byte> buffer, ref int position, out string value)
		{
			var length = buffer[position..].IndexOf((byte)0);

			if (length < 0)
			{
				value = string.Empty;
				return false;
			}

			value = Encoding.UTF8.GetString(buffer.Slice(position, length));
			position += length + 1;

			return true;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static string ReadNullTermUtf8String(BinaryReader reader, MemoryStream ms)
		{
			while (true)
			{
				var b = reader.ReadByte();

				if (b == 0x00)
				{
					break;
				}

				ms.WriteByte(b);
			}

			ms.TryGetBuffer(out var buffer);

			var str = Encoding.UTF8.GetString(buffer);

			ms.SetLength(0);

			return str;
		}
	}
}
