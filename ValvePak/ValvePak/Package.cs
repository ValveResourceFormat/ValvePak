using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace SteamDatabase.ValvePak
{
	/// <summary>
	/// VPK (Valve Pak) files are uncompressed archives used to package game content.
	/// </summary>
	public class Package : IDisposable
	{
		public const int MAGIC = 0x55AA1234;

		/// <summary>
		/// Always '/' as per Valve's vpk implementation.
		/// </summary>
		public const char DirectorySeparatorChar = '/';

		private BinaryReader Reader;

		/// <summary>
		/// Gets the file name.
		/// </summary>
		public string FileName { get; private set; }

		/// <summary>
		/// Gets whether this package had "_dir" in the name, indicating it has multiple chunk files.
		/// </summary>
		public bool IsDirVPK { get; private set; }

		/// <summary>
		/// Gets the VPK version.
		/// </summary>
		public uint Version { get; private set; }

		/// <summary>
		/// Gets the size in bytes of the header.
		/// </summary>
		public uint HeaderSize { get; private set; }

		/// <summary>
		/// Gets the size in bytes of the directory tree.
		/// </summary>
		public uint TreeSize { get; private set; }

		/// <summary>
		/// Gets how many bytes of file content are stored in this VPK file (0 in CSGO).
		/// </summary>
		public uint FileDataSectionSize { get; private set; }

		/// <summary>
		/// Gets the size in bytes of the section containing MD5 checksums for external archive content.
		/// </summary>
		public uint ArchiveMD5SectionSize { get; private set; }

		/// <summary>
		/// Gets the size in bytes of the section containing MD5 checksums for content in this file.
		/// </summary>
		public uint OtherMD5SectionSize { get; private set; }

		/// <summary>
		/// Gets the size in bytes of the section containing the public key and signature.
		/// </summary>
		public uint SignatureSectionSize { get; private set; }

		/// <summary>
		/// Gets the MD5 checksum of the file tree.
		/// </summary>
		public byte[] TreeChecksum { get; private set; }

		/// <summary>
		/// Gets the MD5 checksum of the archive MD5 checksum section entries.
		/// </summary>
		public byte[] ArchiveMD5EntriesChecksum { get; private set; }

		/// <summary>
		/// Gets the MD5 checksum of the complete package until the signature structure.
		/// </summary>
		public byte[] WholeFileChecksum { get; private set; }

		/// <summary>
		/// Gets the public key.
		/// </summary>
		public byte[] PublicKey { get; private set; }

		/// <summary>
		/// Gets the signature.
		/// </summary>
		public byte[] Signature { get; private set; }

		/// <summary>
		/// Gets the package entries.
		/// </summary>
		public Dictionary<string, List<PackageEntry>> Entries { get; private set; }

		/// <summary>
		/// Gets the archive MD5 checksum section entries. Also known as cache line hashes.
		/// </summary>
		public List<ArchiveMD5SectionEntry> ArchiveMD5Entries { get; private set; }

		/// <summary>
		/// Releases binary reader.
		/// </summary>
		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		protected virtual void Dispose(bool disposing)
		{
			if (disposing && Reader != null)
			{
				Reader.Dispose();
				Reader = null;
			}
		}

		/// <summary>
		/// Sets the file name.
		/// </summary>
		/// <param name="fileName">Filename.</param>
		public void SetFileName(string fileName)
		{
			if (fileName == null)
			{
				throw new ArgumentNullException(nameof(fileName));
			}

			if (fileName.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
			{
				fileName = fileName[0..^4];
			}

			if (fileName.EndsWith("_dir", StringComparison.OrdinalIgnoreCase))
			{
				IsDirVPK = true;

				fileName = fileName[0..^4];
			}

			FileName = fileName;
		}

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
		/// Searches for a given file entry in the file list.
		/// </summary>
		/// <param name="filePath">Full path to the file to find.</param>
		public PackageEntry FindEntry(string filePath)
		{
			if (filePath == null)
			{
				throw new ArgumentNullException(nameof(filePath));
			}

			filePath = filePath.Replace('\\', DirectorySeparatorChar);

			var lastSeparator = filePath.LastIndexOf(DirectorySeparatorChar);
			var directory = lastSeparator > -1 ? filePath[..lastSeparator] : string.Empty;
			var fileName = filePath[(lastSeparator + 1)..];

			return FindEntry(directory, fileName);
		}

		/// <summary>
		/// Searches for a given file entry in the file list.
		/// </summary>
		/// <param name="directory">Directory to search in.</param>
		/// <param name="fileName">File name to find.</param>
		public PackageEntry FindEntry(string directory, string fileName)
		{
			if (directory == null)
			{
				throw new ArgumentNullException(nameof(directory));
			}

			if (fileName == null)
			{
				throw new ArgumentNullException(nameof(fileName));
			}

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

			return FindEntry(directory, fileName, extension);
		}

		/// <summary>
		/// Searches for a given file entry in the file list.
		/// </summary>
		/// <param name="directory">Directory to search in.</param>
		/// <param name="fileName">File name to find, without the extension.</param>
		/// <param name="extension">File extension, without the leading dot.</param>
		public PackageEntry FindEntry(string directory, string fileName, string extension)
		{
			if (directory == null)
			{
				throw new ArgumentNullException(nameof(directory));
			}

			if (fileName == null)
			{
				throw new ArgumentNullException(nameof(fileName));
			}

			if (extension == null)
			{
				throw new ArgumentNullException(nameof(extension));
			}

			if (!Entries.ContainsKey(extension))
			{
				return null;
			}

			// We normalize path separators when reading the file list
			// And remove the trailing slash
			directory = directory.Replace('\\', DirectorySeparatorChar).Trim(DirectorySeparatorChar);

			// If the directory is empty after trimming, set it to a space to match Valve's behaviour
			if (directory.Length == 0)
			{
				directory = " ";
			}

			return Entries[extension].Find(x => x.DirectoryName == directory && x.FileName == fileName);
		}

		/// <summary>
		/// Reads the entry from the VPK package.
		/// </summary>
		/// <param name="entry">Package entry.</param>
		/// <param name="output">Output buffer.</param>
		/// <param name="validateCrc">If true, CRC32 will be calculated and verified for read data.</param>
		public void ReadEntry(PackageEntry entry, out byte[] output, bool validateCrc = true)
		{
			if (entry == null)
			{
				throw new ArgumentNullException(nameof(entry));
			}

			output = new byte[entry.SmallData.Length + entry.Length];

			if (entry.SmallData.Length > 0)
			{
				entry.SmallData.CopyTo(output, 0);
			}

			if (entry.Length > 0)
			{
				Stream fs = null;

				try
				{
					var offset = entry.Offset;

					if (entry.ArchiveIndex != 0x7FFF)
					{
						if (!IsDirVPK)
						{
							throw new InvalidOperationException("Given VPK filename does not end in '_dir.vpk', but entry is referencing an external archive.");
						}

						var fileName = $"{FileName}_{entry.ArchiveIndex:D3}.vpk";

						fs = new FileStream(fileName, FileMode.Open, FileAccess.Read);
					}
					else
					{
						fs = Reader.BaseStream;

						offset += HeaderSize + TreeSize;
					}

					fs.Seek(offset, SeekOrigin.Begin);

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

			if (validateCrc && entry.CRC32 != Crc32.Compute(output))
			{
				throw new InvalidDataException("CRC32 mismatch for read data.");
			}
		}

		private void ReadEntries()
		{
			var typeEntries = new Dictionary<string, List<PackageEntry>>();
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
						};

						entry.CRC32 = Reader.ReadUInt32();
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

				typeEntries.Add(typeName, entries);
			}

			Entries = typeEntries;
		}

		/// <summary>
		/// Verify checksums and signatures provided in the VPK
		/// </summary>
		public void VerifyHashes()
		{
			if (Version != 2)
			{
				throw new InvalidDataException("Only version 2 is supported.");
			}

			using (var md5 = MD5.Create())
			{
				Reader.BaseStream.Position = 0;

				var hash = md5.ComputeHash(Reader.ReadBytes((int)(HeaderSize + TreeSize + FileDataSectionSize + ArchiveMD5SectionSize + 32)));

				if (!hash.SequenceEqual(WholeFileChecksum))
				{
					throw new InvalidDataException($"Package checksum mismatch ({BitConverter.ToString(hash)} != expected {BitConverter.ToString(WholeFileChecksum)})");
				}

				Reader.BaseStream.Position = HeaderSize;

				hash = md5.ComputeHash(Reader.ReadBytes((int)TreeSize));

				if (!hash.SequenceEqual(TreeChecksum))
				{
					throw new InvalidDataException($"File tree checksum mismatch ({BitConverter.ToString(hash)} != expected {BitConverter.ToString(TreeChecksum)})");
				}

				Reader.BaseStream.Position = HeaderSize + TreeSize + FileDataSectionSize;

				hash = md5.ComputeHash(Reader.ReadBytes((int)ArchiveMD5SectionSize));

				if (!hash.SequenceEqual(ArchiveMD5EntriesChecksum))
				{
					throw new InvalidDataException($"Archive MD5 entries checksum mismatch ({BitConverter.ToString(hash)} != expected {BitConverter.ToString(ArchiveMD5EntriesChecksum)})");
				}

				// TODO: verify archive checksums
			}

			if (!IsSignatureValid())
			{
				throw new InvalidDataException("VPK signature is not valid.");
			}
		}

		/// <summary>
		/// Verifies the RSA signature.
		/// </summary>
		/// <returns>True if signature is valid, false otherwise.</returns>
		public bool IsSignatureValid()
		{
			if (PublicKey == null || Signature == null)
			{
				return true;
			}

			Reader.BaseStream.Position = 0;

			using var rsa = RSA.Create();
			rsa.ImportSubjectPublicKeyInfo(PublicKey, out _);

			var data = Reader.ReadBytes((int)(HeaderSize + TreeSize + FileDataSectionSize + ArchiveMD5SectionSize + OtherMD5SectionSize));

			return rsa.VerifyData(data, Signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		}

		private void ReadArchiveMD5Section()
		{
			ArchiveMD5Entries = new List<ArchiveMD5SectionEntry>();

			if (ArchiveMD5SectionSize == 0)
			{
				return;
			}

			var entries = ArchiveMD5SectionSize / 28; // 28 is sizeof(VPK_MD5SectionEntry), which is int + int + int + 16 chars

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
				throw new InvalidDataException($"Encountered OtherMD5Section with size of {OtherMD5SectionSize} (should be 48)");
			}

			TreeChecksum = Reader.ReadBytes(16);
			ArchiveMD5EntriesChecksum = Reader.ReadBytes(16);
			WholeFileChecksum = Reader.ReadBytes(16);
		}

		private void ReadSignatureSection()
		{
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
