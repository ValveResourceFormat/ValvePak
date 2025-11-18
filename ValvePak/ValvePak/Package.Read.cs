using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Hashing;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Text;

namespace SteamDatabase.ValvePak
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
		/// </summary>
		/// <param name="input">The input <see cref="Stream"/> to read from.</param>
		public void Read(Stream input)
		{
			ArgumentNullException.ThrowIfNull(input);

			if (FileName == null)
			{
				throw new InvalidOperationException("If you call Read() directly with a stream, you must call SetFileName() first.");
			}

			Reader = new BinaryReader(input);

			if (Reader.ReadUInt32() != MAGIC)
			{
				throw new InvalidDataException("Given file is not a VPK.");
			}

			Version = Reader.ReadUInt32();
			TreeSize = Reader.ReadUInt32();

			if (Version == 1)
			{
				// Nothing else
			}
			else if (Version == 2)
			{
				FileDataSectionSize = Reader.ReadUInt32();
				ArchiveMD5SectionSize = Reader.ReadUInt32();
				OtherMD5SectionSize = Reader.ReadUInt32();
				SignatureSectionSize = Reader.ReadUInt32();
			}
			else if (Version == 0x00030002) // Apex Legends, Titanfall
			{
				throw new NotSupportedException("Respawn uses customized vpk format which this library does not support.");
			}
			else
			{
				throw new InvalidDataException($"Bad VPK version. ({Version})");
			}

			HeaderSize = (uint)input.Position;

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

		private void ReadEntries()
		{
			Debug.Assert(Reader != null);

			var stringComparer = Comparer == null ? null : StringComparer.FromComparison(Comparer.Comparison);
			var typeEntries = new Dictionary<string, List<PackageEntry>>(stringComparer);
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

							int bytesRead;
							int totalRead = 0;
							while ((bytesRead = Reader.Read(entry.SmallData, totalRead, entry.SmallData.Length - totalRead)) != 0)
							{
								totalRead += bytesRead;
							}
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
			TreeSize = (uint)Reader.BaseStream.Position - HeaderSize;
		}

		private void ReadArchiveMD5Section()
		{
			Debug.Assert(Reader != null);

			FileSizeBeforeArchiveMD5Entries = (uint)Reader.BaseStream.Position;

			if (ArchiveMD5SectionSize == 0)
			{
				AccessPackFileHashes = [];
				return;
			}

			var entries = (int)(ArchiveMD5SectionSize / 28); // 28 is sizeof(VPK_MD5SectionEntry), which is int + int + int + 16 chars

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
			FileSizeBeforeWholeFileHash = (uint)Reader.BaseStream.Position;
			WholeFileChecksum = Reader.ReadBytes(16);
		}

		private void ReadSignatureSection()
		{
			Debug.Assert(Reader != null);

			FileSizeBeforeSignature = (uint)Reader.BaseStream.Position;

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

				if (publicKeySize > 0)
				{
					PublicKey = Reader.ReadBytes(publicKeySize);
				}

				if (signatureSize2 > 0)
				{
					Signature = Reader.ReadBytes(signatureSize2);
				}

				return;
			}

			SignatureType = ESignatureType.FullFile;
			PublicKey = Reader.ReadBytes(publicKeySize);

			var signatureSize = Reader.ReadInt32();
			Signature = Reader.ReadBytes(signatureSize);
		}

		private Stream GetFileStream(ushort archiveIndex)
		{
			Stream stream;

			if (archiveIndex != 0x7FFF)
			{
				var fileName = GetArchiveIndexFullFilePath(archiveIndex);

				stream = new FileStream(fileName, FileMode.Open, FileAccess.Read);
			}
			else
			{
				Debug.Assert(Reader != null);

				stream = Reader.BaseStream;
				stream.Seek(HeaderSize + TreeSize, SeekOrigin.Begin);
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
					path = GetArchiveIndexFullFilePath(entry.ArchiveIndex);
				}

				stream = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
				MemoryMappedPaks[entry.ArchiveIndex] = stream;
			}

			var offset = entry.Offset;

			if (entry.ArchiveIndex == 0x7FFF)
			{
				offset += HeaderSize + TreeSize;
			}

			return stream.CreateViewStream(offset, entry.Length, MemoryMappedFileAccess.Read);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private string GetArchiveIndexFullFilePath(ushort archiveIndex)
		{
			return $"{FileName}_{archiveIndex:D3}.vpk";
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
