using System;
using System.Collections.Generic;
using System.IO;
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
			if (input == null)
			{
				throw new ArgumentNullException(nameof(input));
			}

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
			// TODO: Add overload to read into existing byte array (ArrayPool)
			if (entry == null)
			{
				throw new ArgumentNullException(nameof(entry));
			}

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
				Stream fs = null;

				try
				{
					fs = GetFileStream(entry.ArchiveIndex);
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
						fs?.Close();
					}
				}
			}

			if (validateCrc && entry.CRC32 != Crc32.Compute(output, totalLength))
			{
				throw new InvalidDataException("CRC32 mismatch for read data.");
			}
		}

		private void ReadEntries()
		{
			var stringComparer = Comparer == null ? null : StringComparer.FromComparison(Comparer.Comparison);
			var typeEntries = new Dictionary<string, List<PackageEntry>>(stringComparer);
			using var ms = new MemoryStream();

			// Types
			while (true)
			{
				var typeName = ReadNullTermUtf8String(ms);

				if (string.IsNullOrEmpty(typeName))
				{
					break;
				}

				var entries = new List<PackageEntry>();

				// Directories
				while (true)
				{
					var directoryName = ReadNullTermUtf8String(ms);

					if (string.IsNullOrEmpty(directoryName))
					{
						break;
					}

					// Files
					while (true)
					{
						var fileName = ReadNullTermUtf8String(ms);

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
						else
						{
							entry.SmallData = Array.Empty<byte>();
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
			FileSizeBeforeArchiveMD5Entries = (uint)Reader.BaseStream.Position;

			if (ArchiveMD5SectionSize == 0)
			{
				ArchiveMD5Entries = new List<ArchiveMD5SectionEntry>();
				return;
			}

			var entries = (int)(ArchiveMD5SectionSize / 28); // 28 is sizeof(VPK_MD5SectionEntry), which is int + int + int + 16 chars

			ArchiveMD5Entries = new List<ArchiveMD5SectionEntry>(entries);

			for (var i = 0; i < entries; i++)
			{
				ArchiveMD5Entries.Add(new ArchiveMD5SectionEntry
				{
					ArchiveIndex = Reader.ReadUInt32(),
					Offset = Reader.ReadUInt32(),
					Length = Reader.ReadUInt32(),
					Checksum = Reader.ReadBytes(16)
				});
			}
		}

		private void ReadOtherMD5Section()
		{
			if (OtherMD5SectionSize != 48)
			{
				return;
			}

			TreeChecksum = Reader.ReadBytes(16);
			ArchiveMD5EntriesChecksum = Reader.ReadBytes(16);
			FileSizeBeforeWholeFileHash = (uint)Reader.BaseStream.Position;
			WholeFileChecksum = Reader.ReadBytes(16);
		}

		private void ReadSignatureSection()
		{
			FileSizeBeforeSignature = (uint)Reader.BaseStream.Position;

			if (SignatureSectionSize == 0)
			{
				return;
			}

			var publicKeySize = Reader.ReadInt32();

			if (SignatureSectionSize == 20 && publicKeySize == MAGIC)
			{
				// CS2 has this
				return;
			}

			PublicKey = Reader.ReadBytes(publicKeySize);

			var signatureSize = Reader.ReadInt32();
			Signature = Reader.ReadBytes(signatureSize);
		}

		private Stream GetFileStream(ushort archiveIndex)
		{
			Stream stream;

			if (archiveIndex != 0x7FFF)
			{
				if (!IsDirVPK)
				{
					throw new InvalidOperationException("Given VPK filename does not end in '_dir.vpk', but entry is referencing an external archive.");
				}

				var fileName = $"{FileName}_{archiveIndex:D3}.vpk";

				stream = new FileStream(fileName, FileMode.Open, FileAccess.Read);
			}
			else
			{
				stream = Reader.BaseStream;
				stream.Seek(HeaderSize + TreeSize, SeekOrigin.Begin);
			}

			return stream;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private string ReadNullTermUtf8String(MemoryStream ms)
		{
			while (true)
			{
				var b = Reader.ReadByte();

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
