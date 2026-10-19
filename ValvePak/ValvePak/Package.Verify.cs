using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

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
		/// Verifies the signature, the checksums, the chunk hashes, and the CRC32 checksum of each entry whose data no checksum or chunk hash covers.
		/// </summary>
		/// <param name="progressReporter">If provided, will report the number of reports that follow first, then a string for each step.</param>
		/// <param name="cancellationToken">A token to cancel the verification.</param>
		/// <exception cref="InvalidOperationException">The package has not been read.</exception>
		/// <exception cref="InvalidDataException">The signature is not valid, or a checksum, chunk hash or CRC32 checksum does not match.</exception>
		public void Verify(IProgress<string>? progressReporter = null, CancellationToken cancellationToken = default)
		{
			ObjectDisposedException.ThrowIf(IsDisposed, this);

			ThrowIfDirectoryFileClosed();

			var entriesWithUnhashedData = GetEntriesWithUnhashedData();
			var reportCount = 3 + AccessPackFileHashes.Count + entriesWithUnhashedData.Count;

			progressReporter?.Report(reportCount.ToString(CultureInfo.InvariantCulture));

			if (IsSigned && !IsSignatureValid(cancellationToken))
			{
				throw new InvalidDataException("The package signature is not valid.");
			}

			progressReporter?.Report(IsSigned ? "Verified the package signature." : "The package is not signed.");

			var checksumMessages = VerifyChecksums(cancellationToken);

			progressReporter?.Report(checksumMessages.Count > 0 ? string.Join(' ', checksumMessages) : "Verified the package checksums.");

			VerifyChunkHashFractions(progressReporter, cancellationToken);
			VerifyEntryChecksums(entriesWithUnhashedData, progressReporter, cancellationToken);

			progressReporter?.Report("Verified the package.");
		}

		/// <summary>
		/// Returns the entries, sorted by archive and offset, whose data no checksum or chunk hash covers.
		/// </summary>
		private List<PackageEntry> GetEntriesWithUnhashedData()
		{
			var ranges = AccessPackFileHashes.Select(static fraction => (fraction.ArchiveIndex, Start: (long)fraction.Offset, End: (long)fraction.Offset + fraction.Length));

			if (Version != 1)
			{
				ranges = ranges.Append((DirectoryArchiveIndex, 0, FileDataSectionSize));
			}

			var hashedRanges = ranges
				.GroupBy(static range => range.ArchiveIndex)
				.ToDictionary(static group => group.Key, static group => MergeRanges(group.Select(static range => (range.Start, range.End))));

			return [.. GetAllEntries()
				.Where(entry => !IsDataHashed(entry, hashedRanges))
				.OrderBy(static entry => entry.ArchiveIndex)
				.ThenBy(static entry => entry.Offset)];
		}

		private bool IsDataHashed(PackageEntry entry, Dictionary<ushort, List<(long Start, long End)>> hashedRanges)
		{
			if (Version == 1 || entry.PendingData != null)
			{
				return false;
			}

			if (entry.Length == 0)
			{
				return true;
			}

			var end = (long)entry.Offset + entry.Length;

			return hashedRanges.TryGetValue(entry.ArchiveIndex, out var ranges) && ranges.Exists(range => range.Start <= entry.Offset && end <= range.End);
		}

		/// <summary>
		/// Sorts the ranges and joins those that overlap or touch.
		/// </summary>
		private static List<(long Start, long End)> MergeRanges(IEnumerable<(long Start, long End)> ranges)
		{
			var merged = new List<(long Start, long End)>();

			foreach (var range in ranges.OrderBy(static range => range.Start))
			{
				if (merged.Count > 0 && range.Start <= merged[^1].End)
				{
					merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, range.End));
				}
				else
				{
					merged.Add(range);
				}
			}

			return merged;
		}

		/// <summary>
		/// Verify MD5 hashes provided in the VPK.
		/// </summary>
		/// <remarks>
		/// Version 1 packages have no checksums, so nothing is verified for them.
		/// The whole file checksum covers the file tree and archive MD5 section checksums, so when it matches in a package without a signature header,
		/// a mismatching file tree or archive MD5 section checksum is reported to <paramref name="progressReporter"/> instead of thrown.
		/// </remarks>
		/// <param name="progressReporter">If provided, will report a string for each checksum mismatch that is not thrown, or that a version 1 package has no checksums.</param>
		/// <exception cref="InvalidOperationException">The package has not been read, or its directory file could not be opened again after writing.</exception>
		/// <exception cref="InvalidDataException">The package has no checksums, or a checksum does not match.</exception>
		public void VerifyHashes(IProgress<string>? progressReporter = null)
		{
			foreach (var message in VerifyChecksums(CancellationToken.None))
			{
				progressReporter?.Report(message);
			}
		}

		/// <summary>
		/// Verifies the checksums like <see cref="VerifyHashes"/> does, returning the messages it reports.
		/// </summary>
		private List<string> VerifyChecksums(CancellationToken cancellationToken)
		{
			ObjectDisposedException.ThrowIf(IsDisposed, this);

			ThrowIfDirectoryFileClosed();

			if (Version == 1)
			{
				return ["Version 1 packages have no checksums to verify."];
			}

			if (TreeChecksum == null || ArchiveMD5EntriesChecksum == null || WholeFileChecksum == null)
			{
				throw new InvalidDataException("The package has no checksum section.");
			}

			var wholeFileHash = HashPackageRange(0, FileSizeBeforeWholeFileHash, HashAlgorithmName.MD5, cancellationToken);
			var wholeFileHashMatches = wholeFileHash.SequenceEqual(WholeFileChecksum);
			var toleratesSectionMismatch = wholeFileHashMatches && SignatureType != ESignatureType.OnlyFileChecksum;
			var messages = new List<string>();

			VerifySectionHash("File tree", HeaderSize, TreeSize, TreeChecksum, toleratesSectionMismatch, messages, cancellationToken);
			VerifySectionHash("Archive MD5 entries", FileSizeBeforeArchiveMD5Entries, ArchiveMD5SectionSize, ArchiveMD5EntriesChecksum, toleratesSectionMismatch, messages, cancellationToken);

			if (!wholeFileHashMatches)
			{
				throw new InvalidDataException($"Package checksum mismatch ({BitConverter.ToString(wholeFileHash)} != expected {BitConverter.ToString(WholeFileChecksum)})");
			}

			return messages;
		}

		private void VerifySectionHash(string sectionName, long offset, long length, byte[] expected, bool toleratesMismatch, List<string> messages, CancellationToken cancellationToken)
		{
			var hash = HashPackageRange(offset, length, HashAlgorithmName.MD5, cancellationToken);

			if (hash.SequenceEqual(expected))
			{
				return;
			}

			var message = $"{sectionName} checksum mismatch ({BitConverter.ToString(hash)} != expected {BitConverter.ToString(expected)})";

			if (!toleratesMismatch)
			{
				throw new InvalidDataException(message);
			}

			messages.Add($"{message}, ignored because the package checksum matches.");
		}

		/// <summary>
		/// Computes the hash of a range of the package, at an offset from the start of the package.
		/// The hash covers fewer bytes if the package ends before the range does.
		/// </summary>
		private byte[] HashPackageRange(long offset, long length, HashAlgorithmName hashAlgorithm, CancellationToken cancellationToken)
		{
			var archive = GetArchive(DirectoryArchiveIndex);
			var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(length, ChunkHashBufferSize));

			try
			{
				using var hash = IncrementalHash.CreateHash(hashAlgorithm);

				while (length > 0)
				{
					cancellationToken.ThrowIfCancellationRequested();

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

			VerifyChunkHashFractions(progressReporter, CancellationToken.None);

			progressReporter?.Report("Successfully verified archive hashes.");
		}

		/// <summary>
		/// Verifies every entry of <see cref="AccessPackFileHashes"/>, reporting a string before each.
		/// </summary>
		private void VerifyChunkHashFractions(IProgress<string>? progressReporter, CancellationToken cancellationToken)
		{
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
				cancellationToken.ThrowIfCancellationRequested();

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

			var allEntries = GetAllEntries()
				.OrderBy(file => file.ArchiveIndex)
				.ThenBy(file => file.Offset);

			VerifyEntryChecksums(allEntries, progressReporter, CancellationToken.None);

			progressReporter?.Report("Successfully verified file CRC32 checksums.");
		}

		/// <summary>
		/// Verifies the CRC32 checksum of each of the entries, reporting a string before each.
		/// </summary>
		private void VerifyEntryChecksums(IEnumerable<PackageEntry> entries, IProgress<string>? progressReporter, CancellationToken cancellationToken)
		{
			var crc = new Crc32();

			foreach (var entry in entries)
			{
				cancellationToken.ThrowIfCancellationRequested();

				progressReporter?.Report($"Verifying CRC32 checksum for '{entry.GetFullPath()}' in archive {entry.ArchiveIndex}.");

				crc.Append(entry.SmallData);

				if (entry.PendingData != null)
				{
					crc.Append(entry.PendingData);
				}
				else
				{
					ReadArchiveDataInBlocks(entry.ArchiveIndex, entry.Offset, entry.Length, crc.Append);
				}

				var actualChecksum = crc.GetCurrentHashAsUInt32();
				crc.Reset();

				if (entry.CRC32 != actualChecksum)
				{
					throw new InvalidDataException($"CRC32 mismatch for '{entry.GetFullPath()}' (expected {entry.CRC32:X2}, got {actualChecksum:X2}).");
				}
			}
		}

		/// <summary>
		/// Verifies the RSA signature.
		/// </summary>
		/// <returns>True if the signature is valid or the package has no signature (see <see cref="IsSigned"/>), false otherwise.</returns>
		public bool IsSignatureValid()
		{
			return IsSignatureValid(CancellationToken.None);
		}

		private bool IsSignatureValid(CancellationToken cancellationToken)
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

				var hash = HashPackageRange(0, FileSizeBeforeWholeFileHash, HashAlgorithmName.MD5, cancellationToken);

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

			var signedHash = HashPackageRange(0, FileSizeBeforeSignature, HashAlgorithmName.SHA256, cancellationToken);

			return rsa.VerifyHash(signedHash, Signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		}
	}
}
