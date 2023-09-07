using System;
using System.IO;
using System.Security.Cryptography;

namespace SteamDatabase.ValvePak
{
	/// <summary>
	/// VPK (Valve Pak) files are uncompressed archives used to package game content.
	/// </summary>
	public partial class Package : IDisposable
	{
		class SubStream : Stream
		{
			private readonly Stream baseStream;
			private readonly long length;
			private long position;
			public SubStream(Stream baseStream, long offset, long length)
			{
				this.baseStream = baseStream;
				this.length = length;

				baseStream.Seek(offset, SeekOrigin.Begin);
			}
			public override int Read(byte[] buffer, int offset, int count)
			{
				var remaining = length - position;
				if (remaining <= 0) return 0;
				if (remaining < count) count = (int)remaining;
				var read = baseStream.Read(buffer, offset, count);
				position += read;
				return read;
			}
			public override long Position
			{
				get => position;
				set => throw new NotSupportedException();
			}
			public override long Length => length;
			public override bool CanRead => true;
			public override bool CanWrite => false;
			public override bool CanSeek => false;
			public override void Flush() => baseStream.Flush();
			public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
			public override void SetLength(long value) => throw new NotSupportedException();
			public override void Write(byte[] buffer, int offset, int count) => throw new NotImplementedException();
		}

		/// <summary>
		/// Gets the size in bytes of the whole file before <see cref="WholeFileChecksum"/>.
		/// </summary>
		private uint FileSizeBeforeWholeFileHash;

		/// <summary>
		/// Gets the size in bytes of the whole file before <see cref="ArchiveMD5Entries"/>.
		/// </summary>
		private uint FileSizeBeforeArchiveMD5Entries;

		/// <summary>
		/// Gets the size in bytes of the whole file before <see cref="Signature"/>.
		/// </summary>
		private uint FileSizeBeforeSignature;

		/// <summary>
		/// Verify MD5 hashes provided in the VPK.
		/// </summary>
		public void VerifyHashes()
		{
			if (Version != 2)
			{
				throw new InvalidDataException("Only version 2 is supported.");
			}

			using var md5 = MD5.Create();
			var subStream = new SubStream(Reader.BaseStream, 0, FileSizeBeforeWholeFileHash);
			var hash = md5.ComputeHash(subStream);

			if (!HashEquals(hash, WholeFileChecksum))
			{
				throw new InvalidDataException($"Package checksum mismatch ({BitConverter.ToString(hash)} != expected {BitConverter.ToString(WholeFileChecksum)})");
			}

			subStream = new SubStream(Reader.BaseStream, HeaderSize, (int)TreeSize);
			hash = md5.ComputeHash(subStream);

			if (!HashEquals(hash, TreeChecksum))
			{
				throw new InvalidDataException($"File tree checksum mismatch ({BitConverter.ToString(hash)} != expected {BitConverter.ToString(TreeChecksum)})");
			}

			subStream = new SubStream(Reader.BaseStream, FileSizeBeforeArchiveMD5Entries, (int)ArchiveMD5SectionSize);
			hash = md5.ComputeHash(subStream);

			if (!HashEquals(hash, ArchiveMD5EntriesChecksum))
			{
				throw new InvalidDataException($"Archive MD5 entries checksum mismatch ({BitConverter.ToString(hash)} != expected {BitConverter.ToString(ArchiveMD5EntriesChecksum)})");
			}
		}

		/// <summary>
		/// Verify MD5 hashes of individual chunk files provided in the VPK.
		/// </summary>
		public void VerifyChunkHashes()
		{
			using var md5 = MD5.Create();
			Stream stream = null;
			var lastArchiveIndex = uint.MaxValue;

			try
			{
				// TODO: When created by Valve, entries are sorted, and are 1MB chunks
				foreach (var entry in ArchiveMD5Entries)
				{
					if (entry.ArchiveIndex > short.MaxValue)
					{
						throw new InvalidDataException("Unexpected archive index");
					}

					if (lastArchiveIndex != entry.ArchiveIndex)
					{
						if (lastArchiveIndex != 0x7FFF)
						{
							stream?.Close();
						}

						stream = GetFileStream((ushort)entry.ArchiveIndex);
						lastArchiveIndex = entry.ArchiveIndex;
					}
					else
					{
						var offset = entry.ArchiveIndex == 0x7FFF ? HeaderSize + TreeSize : 0;
						stream.Seek(offset, SeekOrigin.Begin);
					}

					var subStream = new SubStream(stream, stream.Position + entry.Offset, entry.Length);
					var hash = md5.ComputeHash(subStream);

					if (!HashEquals(hash, entry.Checksum))
					{
						throw new InvalidDataException($"Package checksum mismatch in archive {entry.ArchiveIndex} at {entry.Offset} ({BitConverter.ToString(hash)} != expected {BitConverter.ToString(entry.Checksum)})");
					}
				}
			}
			finally
			{
				if (lastArchiveIndex != 0x7FFF)
				{
					stream?.Close();
				}
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

			using var rsa = RSA.Create();
			rsa.ImportSubjectPublicKeyInfo(PublicKey, out _);

			var subStream = new SubStream(Reader.BaseStream, 0, FileSizeBeforeSignature);

			return rsa.VerifyData(subStream, Signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		}

		private static bool HashEquals(byte[] a, byte[] b)
		{
			if (a.Length != b.Length)
			{
				return false;
			}

			for (int i = 0; i < a.Length; i++)
			{
				if (a[i] != b[i])
				{
					return false;
				}
			}

			return true;
		}

	}
}
