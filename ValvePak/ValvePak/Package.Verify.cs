using System;
using System.Buffers;
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
		/// <remarks>
		/// Version 1 packages have no checksums, so nothing is verified for them.
		/// The whole file checksum covers the file tree and archive MD5 section checksums, so when it matches,
		/// a mismatching file tree or archive MD5 section checksum is reported to <paramref name="progressReporter"/> instead of thrown.
		/// </remarks>
		/// <param name="progressReporter">If provided, will report a string for each checksum mismatch that is not thrown, or that a version 1 package has no checksums.</param>
		/// <exception cref="InvalidOperationException">The package has not been read, or its directory file could not be opened again after writing.</exception>
		/// <exception cref="InvalidDataException">The package has no checksums, or the whole file checksum does not match.</exception>
		public void VerifyHashes(IProgress<string>? progressReporter = null)
		{
			ObjectDisposedException.ThrowIf(IsDisposed, this);

			ThrowIfDirectoryFileClosed();

			if (Version == 1)
			{
				progressReporter?.Report("Version 1 packages have no checksums to verify.");
				return;
			}

			if (TreeChecksum == null || ArchiveMD5EntriesChecksum == null || WholeFileChecksum == null)
			{
				throw new InvalidDataException("The package has no checksum section.");
			}

			var wholeFileHash = HashPackageRange(0, FileSizeBeforeWholeFileHash, HashAlgorithmName.MD5);
			var wholeFileHashMatches = wholeFileHash.SequenceEqual(WholeFileChecksum);

			VerifySectionHash("File tree", HeaderSize, TreeSize, TreeChecksum, wholeFileHashMatches, progressReporter);
			VerifySectionHash("Archive MD5 entries", FileSizeBeforeArchiveMD5Entries, ArchiveMD5SectionSize, ArchiveMD5EntriesChecksum, wholeFileHashMatches, progressReporter);

			if (!wholeFileHashMatches)
			{
				throw new InvalidDataException($"Package checksum mismatch ({BitConverter.ToString(wholeFileHash)} != expected {BitConverter.ToString(WholeFileChecksum)})");
			}
		}

		private void VerifySectionHash(string sectionName, long offset, long length, byte[] expected, bool wholeFileHashMatches, IProgress<string>? progressReporter)
		{
			var hash = HashPackageRange(offset, length, HashAlgorithmName.MD5);

			if (hash.SequenceEqual(expected))
			{
				return;
			}

			var message = $"{sectionName} checksum mismatch ({BitConverter.ToString(hash)} != expected {BitConverter.ToString(expected)})";

			if (!wholeFileHashMatches)
			{
				throw new InvalidDataException(message);
			}

			progressReporter?.Report($"{message}, ignored because the package checksum matches.");
		}

		/// <summary>
		/// Computes the hash of a range of the package, at an offset from the start of the package.
		/// The hash covers fewer bytes if the package ends before the range does.
		/// </summary>
		private byte[] HashPackageRange(long offset, long length, HashAlgorithmName hashAlgorithm)
		{
			var archive = GetArchive(0x7FFF);
			var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(length, ChunkHashBufferSize));

			try
			{
				using var hash = IncrementalHash.CreateHash(hashAlgorithm);

				while (length > 0)
				{
					var count = (int)Math.Min(length, buffer.Length);
					var bytesRead = archive.Read(offset, buffer.AsSpan(0, count));

					if (bytesRead == 0)
					{
						break;
					}

					hash.AppendData(buffer, 0, bytesRead);
					offset += bytesRead;
					length -= bytesRead;
				}

				return hash.GetHashAndReset();
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(buffer);
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
				.ThenBy(file => file.Offset);

			var buffer = new byte[ChunkHashBufferSize];
			Span<byte> hash = stackalloc byte[16];

			using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
			using var blake3 = Blake3.Hasher.New();

			foreach (var entry in allEntries)
			{
				var hashTypeName = entry.HashType switch
				{
					EHashType.Blake3 => "Blake3",
					EHashType.MD5 => "MD5",
					_ => throw new InvalidDataException($"Unrecognized hash type: {entry.HashType} ({(ushort)entry.HashType})"),
				};

				progressReporter?.Report($"Verifying {hashTypeName} hash at offset {entry.Offset} in archive {entry.ArchiveIndex}.");

				for (long position = 0; position < entry.Length;)
				{
					var length = (int)Math.Min(entry.Length - position, buffer.Length);
					var bytesRead = ReadArchiveData(entry.ArchiveIndex, entry.Offset + position, buffer.AsSpan(0, length));

					if (bytesRead == 0)
					{
						break;
					}

					var data = buffer.AsSpan(0, bytesRead);

					if (entry.HashType == EHashType.Blake3)
					{
						blake3.Update(data);
					}
					else
					{
						md5.AppendData(data);
					}

					position += bytesRead;
				}

				if (entry.HashType == EHashType.Blake3)
				{
					blake3.Finalize(hash);
					blake3.Reset();
				}
				else
				{
					md5.GetHashAndReset(hash);
				}

				if (!hash.SequenceEqual(entry.Checksum))
				{
					throw new InvalidDataException($"Package checksum mismatch ({hashTypeName}) in archive {entry.ArchiveIndex} at {entry.Offset} ({Convert.ToHexString(hash)} != expected {Convert.ToHexString(entry.Checksum)})");
				}
			}

			progressReporter?.Report("Successfully verified archive hashes.");
		}

		/// <summary>
		/// Verify CRC32 checksums of all files in the package.
		/// </summary>
		/// <param name="progressReporter">If provided, will report a string with the current verification progress.</param>
		/// <exception cref="EndOfStreamException">An archive ends before all bytes of an entry could be read.</exception>
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
		/// <returns>True if the signature is valid or the package has no signature (see <see cref="IsSigned"/>), false otherwise.</returns>
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

				var hash = HashPackageRange(0, FileSizeBeforeWholeFileHash, HashAlgorithmName.MD5);

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

			var signedHash = HashPackageRange(0, FileSizeBeforeSignature, HashAlgorithmName.SHA256);

			return rsa.VerifyHash(signedHash, Signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		}
	}
}
