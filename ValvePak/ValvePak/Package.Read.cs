using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.IO.Hashing;
using System.Linq;
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
			ThrowIfAlreadyRead();
			ThrowIfFilesAdded();

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
			ObjectDisposedException.ThrowIf(IsDisposed, this);
			ThrowIfAlreadyRead();
			ThrowIfFilesAdded();

			if (FileName == null)
			{
				throw new InvalidOperationException("If you call Read() directly with a stream, you must call SetFileName() first.");
			}

			StreamOffset = input.Position;

			ReadHeader(input);
			ReadEntries();
			ReadSections();

			IsReadPackage = true;
			HighestChunkIndex = GetChunkIndicesWithData()
				.Concat(AccessPackFileHashes.Select(static fraction => fraction.ArchiveIndex))
				.Where(static archiveIndex => archiveIndex < DirectoryArchiveIndex)
				.Max(static archiveIndex => (int?)archiveIndex) ?? -1;

			if (HighestChunkIndex >= 0)
			{
				ChunkBaseName = Path.GetFullPath(FileName);
			}
		}

		/// <summary>
		/// Opens the directory file at the given path instead of the one read before, keeping the current <see cref="Entries"/>.
		/// </summary>
		private void ReopenDirectoryFile(string path)
		{
			CloseDirectoryFile();
			ReadFilePath = path;

			var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

			try
			{
				StreamOffset = 0;
				ReadHeader(input);
				input.Position = HeaderSize + TreeSize;
				ReadSections();
			}
			catch
			{
				CloseDirectoryFile();
				throw;
			}
		}

		private void ReadHeader(Stream input)
		{
			Reader = new BinaryReader(input);
			ReadFilePath = (input as FileStream)?.Name;

			if (Reader.ReadUInt32() != MAGIC)
			{
				throw new InvalidDataException("Given file is not a VPK.");
			}

			var version = Reader.ReadUInt32();
			TreeSize = Reader.ReadUInt32();

			if (version == 1)
			{
				FileDataSectionSize = 0;
				ArchiveMD5SectionSize = 0;
				OtherMD5SectionSize = 0;
				SignatureSectionSize = 0;
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

			var embeddedDataOffset = (long)HeaderSize + TreeSize;

			// Taking the handle flushes the stream's buffer, so it is done here before any other thread can read from the stream
			if (input is FileStream fileStream)
			{
				Archives[DirectoryArchiveIndex] = new FileArchive(fileStream.SafeFileHandle, ownsHandle: false, StreamOffset, embeddedDataOffset);
			}
			else
			{
				Archives[DirectoryArchiveIndex] = new StreamArchive(input, StreamOffset, embeddedDataOffset);
			}
		}

		private void ReadSections()
		{
			Debug.Assert(Reader != null);

			// Skip over file data, if any
			Reader.BaseStream.Position += FileDataSectionSize;

			ReadArchiveMD5Section();
			ReadOtherMD5Section();
			ReadSignatureSection();
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

			var totalLength = (long)entry.Length + entry.SmallData.Length;

			if (output.Length < totalLength)
			{
				throw new ArgumentOutOfRangeException(nameof(output), "Size of the provided output buffer is smaller than entry.TotalLength.");
			}

			if (entry.SmallData.Length > 0)
			{
				entry.SmallData.CopyTo(output, 0);
			}

			if (entry.PendingData != null)
			{
				entry.PendingData.CopyTo(output, entry.SmallData.Length);
			}
			else if (entry.Length > 0)
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

			var actualChecksum = Crc32.HashToUInt32(output.AsSpan(0, (int)totalLength));

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

				// Types that differ only in case share a list when the dictionary is case insensitive
				if (typeEntries.TryGetValue(typeName, out var existingEntries))
				{
					existingEntries.AddRange(entries);
					entries = existingEntries;
				}
				else
				{
					typeEntries.Add(typeName, entries);
				}

				if (Comparer != null)
				{
					// Sorting at the end is faster than doing BinarySearch+Insert
					entries.Sort(Comparer);
				}
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

			var input = Reader.BaseStream;

			if (ArchiveMD5SectionSize > input.Length - input.Position)
			{
				throw new InvalidDataException($"Archive MD5 section size ({ArchiveMD5SectionSize}) is larger than the rest of the file ({input.Length - input.Position}).");
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
					hashFraction.ArchiveIndex = DirectoryArchiveIndex;
					hashFraction.HashType = EHashType.MD5;
				}

				AccessPackFileHashes.Add(hashFraction);
			}
		}

		private void ReadOtherMD5Section()
		{
			Debug.Assert(Reader != null);

			if (OtherMD5SectionSize < 48)
			{
				TreeChecksum = null;
				ArchiveMD5EntriesChecksum = null;
				WholeFileChecksum = null;
				Reader.BaseStream.Position += OtherMD5SectionSize;
				return;
			}

			TreeChecksum = Reader.ReadBytes(16);
			ArchiveMD5EntriesChecksum = Reader.ReadBytes(16);
			FileSizeBeforeWholeFileHash = (uint)(Reader.BaseStream.Position - StreamOffset);
			WholeFileChecksum = Reader.ReadBytes(16);
			Reader.BaseStream.Position += OtherMD5SectionSize - 48;
		}

		private void ReadSignatureSection()
		{
			Debug.Assert(Reader != null);

			FileSizeBeforeSignature = (uint)(Reader.BaseStream.Position - StreamOffset);
			SignatureType = ESignatureType.Unknown;
			PublicKey = null;
			Signature = null;

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

		/// <summary>
		/// Returns the archive for the given index, opening its chunk file on first use.
		/// The embedded archive (<see cref="DirectoryArchiveIndex"/>) is added when the directory file is read.
		/// </summary>
		private Archive GetArchive(ushort archiveIndex)
		{
			if (Archives.TryGetValue(archiveIndex, out var archive))
			{
				return archive;
			}

			lock (ArchivesLock)
			{
				ObjectDisposedException.ThrowIf(IsDisposed, this);

				if (Archives.TryGetValue(archiveIndex, out archive))
				{
					return archive;
				}

				if (archiveIndex == DirectoryArchiveIndex)
				{
					ThrowIfDirectoryFileClosed();
				}

				var fileName = GetArchiveIndexFullFilePath(GetChunkBaseName(), archiveIndex);
				var handle = File.OpenHandle(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

				archive = new FileArchive(handle, ownsHandle: true, origin: 0, dataOffset: 0);
				Archives[archiveIndex] = archive;

				return archive;
			}
		}

		/// <summary>
		/// Reads entry data at the given offset until the output is full or the end of the archive is reached.
		/// This method is safe to call from multiple threads.
		/// </summary>
		/// <returns>The number of bytes read.</returns>
		private int ReadArchiveData(ushort archiveIndex, long offset, Span<byte> output)
		{
			var archive = GetArchive(archiveIndex);

			return archive.Read(archive.DataOffset + offset, output);
		}

		private void ReadArchiveDataExactly(ushort archiveIndex, long offset, Span<byte> output)
		{
			var bytesRead = ReadArchiveData(archiveIndex, offset, output);

			if (bytesRead != output.Length)
			{
				throw new EndOfStreamException($"Reached the end of archive {archiveIndex} after reading {bytesRead} of {output.Length} bytes at offset {offset}.");
			}
		}

		private void CloseArchive(ushort archiveIndex)
		{
			lock (ArchivesLock)
			{
				if (Archives.TryRemove(archiveIndex, out var archive))
				{
					archive.Dispose();
				}
			}
		}

		private void CloseDirectoryFile()
		{
			CloseArchive(DirectoryArchiveIndex);
			Reader?.Dispose();
			Reader = null;
		}

		/// <summary>
		/// Returns a stream that reads the entry directly from a memory mapping of its archive when possible,
		/// otherwise reads entry into a byte array and returns <see cref="MemoryStream"/>.
		/// This only works when entries have no preload bytes.
		/// Files smaller or equal to 4096 bytes will be read into memory.
		///
		/// When trying to map a file in a non-split packages (<see cref="IsDirVPK"/>),
		/// this will only memory map if the package was read by providing <see cref="FileStream"/> or using <see cref="Read(string)"/>.
		///
		/// Each archive is mapped once.
		/// Returned streams can only be read while the package is open, they throw <see cref="ObjectDisposedException"/>
		/// once the package is disposed, once <see cref="Write(string, bool)"/> replaces the file they read from, or once it repacks, which closes all files the package read from.
		/// This method is safe to call from multiple threads.
		/// </summary>
		/// <param name="entry">Package entry.</param>
		/// <returns>Stream for a given package entry contents.</returns>
		/// <exception cref="EndOfStreamException">The archive ends before all bytes of the entry could be read.</exception>
		public Stream GetMemoryMappedStreamIfPossible(PackageEntry entry)
		{
			ArgumentNullException.ThrowIfNull(entry);
			ObjectDisposedException.ThrowIf(IsDisposed, this);

			if (entry.Length > 4096 && entry.SmallData.Length == 0 && entry.PendingData == null)
			{
				var archive = GetArchive(entry.ArchiveIndex);
				var stream = archive.OpenMappedStream(archive.DataOffset + entry.Offset, entry.Length);

				if (stream != null)
				{
					return stream;
				}
			}

			ReadEntry(entry, out var output, false);

			return new MemoryStream(output);
		}

		/// <summary>
		/// Returns a read-only stream over a whole chunk file, or for <see cref="DirectoryArchiveIndex"/> over the file data in the directory file, at <see cref="PackageEntry.Offset"/> positions.
		/// Like streams from <see cref="GetMemoryMappedStreamIfPossible"/>, it can only be read while the package is open and has not replaced the file it reads from.
		/// </summary>
		/// <param name="archiveIndex">Index of the chunk file, or <see cref="DirectoryArchiveIndex"/>.</param>
		/// <exception cref="FileNotFoundException">The chunk file does not exist.</exception>
		public Stream OpenArchiveStream(ushort archiveIndex)
		{
			ArgumentOutOfRangeException.ThrowIfGreaterThan(archiveIndex, DirectoryArchiveIndex);
			ObjectDisposedException.ThrowIf(IsDisposed, this);

			var archive = GetArchive(archiveIndex);
			var length = Math.Max(0, archive.Length - archive.DataOffset);

			if (archiveIndex == DirectoryArchiveIndex && Version != 1)
			{
				length = Math.Min(length, FileDataSectionSize);
			}

			return archive.OpenMappedStream(archive.DataOffset, length) ?? new ArchiveRangeStream(archive, archive.DataOffset, length);
		}

		/// <summary>
		/// Returns the full path of a chunk file of this package, which does not have to exist, such as "pak01_003.vpk" for "pak01_dir.vpk".
		/// </summary>
		/// <param name="archiveIndex">Index of the chunk file, up to <see cref="MaxChunkIndex"/>.</param>
		/// <exception cref="InvalidOperationException">The package has no file name.</exception>
		public string GetChunkFilePath(ushort archiveIndex)
		{
			ArgumentOutOfRangeException.ThrowIfGreaterThan(archiveIndex, MaxChunkIndex);

			return GetArchiveIndexFullFilePath(GetChunkBaseName(), archiveIndex);
		}

		/// <summary>
		/// Parses a path in the form that <see cref="GetChunkFilePath(ushort)"/> returns: a base name, an underscore,
		/// three digits or four to five digits without a leading zero, and ".vpk".
		/// </summary>
		/// <param name="path">The path to parse.</param>
		/// <param name="baseName">The path without the underscore, the index and ".vpk".</param>
		/// <param name="archiveIndex">The index of the chunk file.</param>
		/// <returns>True if the path is in the form of a chunk file path.</returns>
		public static bool TryParseChunkFilePath(string path, [NotNullWhen(true)] out string? baseName, out ushort archiveIndex)
		{
			ArgumentNullException.ThrowIfNull(path);

			const string Extension = ".vpk";

			baseName = null;
			archiveIndex = 0;

			if (!path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			var name = path.AsSpan(0, path.Length - Extension.Length);
			var separator = name.LastIndexOf('_');

			if (separator < 1)
			{
				return false;
			}

			var digits = name[(separator + 1)..];

			if (digits.Length is < 3 or > 5 || digits.ContainsAnyExceptInRange('0', '9') || (digits.Length > 3 && digits[0] == '0'))
			{
				return false;
			}

			var index = int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);

			if (index > MaxChunkIndex)
			{
				return false;
			}

			baseName = path[..separator];
			archiveIndex = (ushort)index;

			return true;
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
