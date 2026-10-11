using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace ValvePak
{
	/// <summary>
	/// VPK (Valve Pak) files are uncompressed archives used to package game content.
	/// </summary>
	public partial class Package : IDisposable
	{
		[ExcludeFromCodeCoverage]
		sealed class SubStream : Stream, IDisposable
		{
#pragma warning disable CA2213 // Disposable fields should be disposed
			private readonly Stream baseStream;
#pragma warning restore CA2213
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
		/// Size in bytes of the buffer that chunk hash fractions are read into, which covers a whole fraction as created by Valve.
		/// </summary>
		private const uint ChunkHashBufferSize = 1024 * 1024;

		/// <summary>
		/// Gets the size in bytes of the whole file before <see cref="WholeFileChecksum"/>.
		/// </summary>
		private uint FileSizeBeforeWholeFileHash;

		/// <summary>
		/// Gets the size in bytes of the whole file before <see cref="AccessPackFileHashes"/>.
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
			ObjectDisposedException.ThrowIf(IsDisposed, this);

			if (Version != 2)
			{
				throw new InvalidDataException("Only version 2 is supported.");
			}

			Debug.Assert(Reader != null);
			Debug.Assert(TreeChecksum != null);
			Debug.Assert(ArchiveMD5EntriesChecksum != null);
			Debug.Assert(WholeFileChecksum != null);

			using var subStream = new SubStream(Reader.BaseStream, HeaderSize, (int)TreeSize);
			var hash = MD5.HashData(subStream);

			if (!hash.SequenceEqual(TreeChecksum))
			{
				throw new InvalidDataException($"File tree checksum mismatch ({BitConverter.ToString(hash)} != expected {BitConverter.ToString(TreeChecksum)})");
			}

			using var subStream2 = new SubStream(Reader.BaseStream, FileSizeBeforeArchiveMD5Entries, (int)ArchiveMD5SectionSize);
			hash = MD5.HashData(subStream2);

			if (!hash.SequenceEqual(ArchiveMD5EntriesChecksum))
			{
				throw new InvalidDataException($"Archive MD5 entries checksum mismatch ({BitConverter.ToString(hash)} != expected {BitConverter.ToString(ArchiveMD5EntriesChecksum)})");
			}

			using var subStream3 = new SubStream(Reader.BaseStream, 0, FileSizeBeforeWholeFileHash);
			hash = MD5.HashData(subStream3);

			if (!hash.SequenceEqual(WholeFileChecksum))
			{
				throw new InvalidDataException($"Package checksum mismatch ({BitConverter.ToString(hash)} != expected {BitConverter.ToString(WholeFileChecksum)})");
			}
		}

		/// <summary>
		/// Verify hashes of individual chunk files provided in the VPK.
		/// </summary>
		/// <param name="progressReporter">If provided, will report a string with the current verification progress.</param>
		public void VerifyChunkHashes(IProgress<string>? progressReporter = null)
		{
			ObjectDisposedException.ThrowIf(IsDisposed, this);

			// When created by Valve, entries are sorted, and are 1MB chunks
			var allEntries = AccessPackFileHashes
				.OrderBy(file => file.ArchiveIndex)
				.ThenBy(file => file.Offset)
				.ToList();

			var maxLength = allEntries.Count > 0 ? allEntries.Max(file => file.Length) : 0;
			var buffer = new byte[Math.Min(maxLength, ChunkHashBufferSize)];
			Span<byte> hash = stackalloc byte[16];

			using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);

			foreach (var entry in allEntries)
			{
				var hashTypeName = entry.HashType switch
				{
					EHashType.Blake3 => "Blake3",
					EHashType.MD5 => "MD5",
					_ => string.Empty,
				};

				progressReporter?.Report($"Verifying {hashTypeName} hash at offset {entry.Offset} in archive {entry.ArchiveIndex}.");

				GetArchive(entry.ArchiveIndex);

				long position = 0;
				ReadOnlySpan<byte> data;

				switch (entry.HashType)
				{
					case EHashType.Blake3:
						using (var hasher = Blake3.Hasher.New())
						{
							while (ReadChunkHashFraction(entry, buffer, ref position, out data))
							{
								hasher.Update(data);
							}

							hasher.Finalize(hash);
						}
						break;

					case EHashType.MD5:
						while (ReadChunkHashFraction(entry, buffer, ref position, out data))
						{
							md5.AppendData(data);
						}

						md5.GetHashAndReset(hash);
						break;

					default:
						throw new InvalidDataException($"Unrecognized hash type: {entry.HashType} ({(ushort)entry.HashType})");
				}

				if (!hash.SequenceEqual(entry.Checksum))
				{
					throw new InvalidDataException($"Package checksum mismatch ({hashTypeName}) in archive {entry.ArchiveIndex} at {entry.Offset} ({Convert.ToHexString(hash)} != expected {Convert.ToHexString(entry.Checksum)})");
				}
			}

			progressReporter?.Report("Successfully verified archive hashes.");
		}

		/// <summary>
		/// Reads the next part of the data covered by a chunk hash fraction into the buffer.
		/// </summary>
		/// <returns>False once all data was read, or the end of the archive was reached.</returns>
		private bool ReadChunkHashFraction(ChunkHashFraction fraction, byte[] buffer, scoped ref long position, out ReadOnlySpan<byte> data)
		{
			var length = (int)Math.Min(fraction.Length - position, buffer.Length);

			if (length <= 0)
			{
				data = default;
				return false;
			}

			var bytesRead = ReadArchiveData(fraction.ArchiveIndex, fraction.Offset + position, buffer.AsSpan(0, length));

			data = buffer.AsSpan(0, bytesRead);
			position = bytesRead < length ? fraction.Length : position + bytesRead;

			return bytesRead > 0;
		}

		/// <summary>
		/// Verify CRC32 checksums of all files in the package.
		/// </summary>
		/// <param name="progressReporter">If provided, will report a string with the current verification progress.</param>
		public void VerifyFileChecksums(IProgress<string>? progressReporter = null)
		{
			ObjectDisposedException.ThrowIf(IsDisposed, this);

			if (Entries == null)
			{
				return;
			}

			var allEntries = Entries
				.SelectMany(file => file.Value)
				.OrderBy(file => file.ArchiveIndex)
				.ThenBy(file => file.Offset);

			var buffer = Array.Empty<byte>();

			foreach (var entry in allEntries)
			{
				progressReporter?.Report($"Verifying CRC32 checksum for '{entry.GetFullPath()}' in archive {entry.ArchiveIndex}.");

				if (buffer.Length < entry.TotalLength)
				{
					buffer = new byte[entry.TotalLength];
				}

				ReadEntry(entry, buffer, validateCrc: true);
			}

			progressReporter?.Report("Successfully verified file CRC32 checksums.");
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

			if (Reader == null)
			{
				return false;
			}

			using var rsa = RSA.Create();
			rsa.ImportSubjectPublicKeyInfo(PublicKey, out _);

			if (SignatureType == ESignatureType.OnlyFileChecksum)
			{
				if (WholeFileChecksum == null)
				{
					return false;
				}

				using var subStream3 = new SubStream(Reader.BaseStream, 0, FileSizeBeforeWholeFileHash);
				var hash = MD5.HashData(subStream3);

				if (!hash.SequenceEqual(WholeFileChecksum))
				{
					return false;
				}

				// SHA256(MD5) is certainly a choice
				return rsa.VerifyData(hash, Signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
			}

			if (SignatureType != ESignatureType.FullFile)
			{
				return false;
			}

			using var subStream = new SubStream(Reader.BaseStream, 0, FileSizeBeforeSignature);

			return rsa.VerifyData(subStream, Signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		}
	}
}
