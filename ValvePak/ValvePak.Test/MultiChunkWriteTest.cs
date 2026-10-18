using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TUnit.Assertions.Enums;
using TUnit.Core.Enums;

namespace ValvePak.Test
{
	internal sealed class MultiChunkWriteTest
	{
		private const int FractionSize = 1024 * 1024;

		private string TempDirectory = string.Empty;

		[Before(HookType.Test)]
		public void SetUp()
		{
			TempDirectory = Path.Combine(Path.GetTempPath(), "ValvePakTest_" + Path.GetRandomFileName());
			Directory.CreateDirectory(TempDirectory);
		}

		[After(HookType.Test)]
		public void TearDown()
		{
			Directory.Delete(TempDirectory, recursive: true);
		}

		[Test]
		public async Task WriteMultiChunkPackage()
		{
			var dirPath = TempPath("test_dir.vpk");

			using (var package = new Package())
			{
				package.SetFileName(dirPath);
				package.WriteChunkSize = 800;

				for (var i = 0; i < 10; i++)
				{
					var entry = package.AddFile($"files/chunked_{i}.bin", CreateTestData(400, (byte)i), multiChunk: true);

					// 400 byte files with an 800 byte chunk size: a chunk file at exactly 800 bytes is not full yet, so each one holds three files
					await Assert.That(entry.ArchiveIndex).IsEqualTo((ushort)(i / 3));
				}

				package.AddFile("in_dir.txt", Encoding.UTF8.GetBytes("this file is in the directory file"));

				// Files with no data must stay in the directory file
				var emptyEntry = package.AddFile("empty.bin", [], multiChunk: true);
				await Assert.That(emptyEntry.ArchiveIndex).IsEqualTo((ushort)0x7FFF);

				package.Write(dirPath);
			}

			using (Assert.Multiple())
			{
				await Assert.That(new FileInfo(TempPath("test_000.vpk")).Length).IsEqualTo(1200L);
				await Assert.That(new FileInfo(TempPath("test_001.vpk")).Length).IsEqualTo(1200L);
				await Assert.That(new FileInfo(TempPath("test_002.vpk")).Length).IsEqualTo(1200L);
				await Assert.That(new FileInfo(TempPath("test_003.vpk")).Length).IsEqualTo(400L);
				await Assert.That(File.Exists(TempPath("test_004.vpk"))).IsFalse();
			}

			using var readBack = new Package();
			readBack.Read(dirPath);

			using (Assert.Multiple())
			{
				await Assert.That(readBack.IsDirVPK).IsTrue();
				await Assert.That(readBack.ArchiveMD5SectionSize).IsEqualTo(4u * ChunkHashFraction.SectionEntrySize);
				await Assert.That(readBack.AccessPackFileHashes).Count().IsEqualTo(4);
			}

			await AssertPackageVerifies(readBack);

			for (var i = 0; i < 10; i++)
			{
				var entry = await AssertEntryData(readBack, $"files/chunked_{i}.bin", 400, (byte)i);
				await Assert.That(entry.ArchiveIndex).IsEqualTo((ushort)(i / 3));
			}

			var dirEntry = readBack.FindEntry("in_dir.txt");
			await Assert.That(dirEntry).IsNotNull();
			await Assert.That(dirEntry.ArchiveIndex).IsEqualTo((ushort)0x7FFF);
		}

		[Test]
		public async Task WritesChunkHashFractions()
		{
			var dirPath = TempPath("fractions_dir.vpk");
			var fileA = CreateTestData(2 * FractionSize, 1); // exactly two fractions
			var fileB = CreateTestData(300_000, 2);

			using (var package = new Package())
			{
				package.SetFileName(dirPath);
				package.WriteChunkSize = 1024 * 1024;
				package.AddFile("a.bin", fileA, multiChunk: true);
				package.AddFile("b.bin", fileB, multiChunk: true);
				package.Write(dirPath);
			}

			using var readBack = new Package();
			readBack.Read(dirPath);

			// Chunk 0 is an exact multiple of the 1 MiB fraction size, which produces a trailing zero sized fraction
			await Assert.That(readBack.AccessPackFileHashes).Count().IsEqualTo(4);

			using (Assert.Multiple())
			{
				await Assert.That(readBack.AccessPackFileHashes[0].ArchiveIndex).IsZero();
				await Assert.That(readBack.AccessPackFileHashes[0].Offset).IsZero();
				await Assert.That(readBack.AccessPackFileHashes[0].Length).IsEqualTo((uint)FractionSize);
				await Assert.That(readBack.AccessPackFileHashes[0].HashType).IsEqualTo(EHashType.Blake3);
				await Assert.That(readBack.AccessPackFileHashes[0].Checksum).IsEquivalentTo(HashBlake3(fileA.AsSpan(0, FractionSize)), CollectionOrdering.Matching);

				await Assert.That(readBack.AccessPackFileHashes[1].ArchiveIndex).IsZero();
				await Assert.That(readBack.AccessPackFileHashes[1].Offset).IsEqualTo((uint)FractionSize);
				await Assert.That(readBack.AccessPackFileHashes[1].Length).IsEqualTo((uint)FractionSize);

				await Assert.That(readBack.AccessPackFileHashes[2].ArchiveIndex).IsZero();
				await Assert.That(readBack.AccessPackFileHashes[2].Offset).IsEqualTo((uint)(2 * FractionSize));
				await Assert.That(readBack.AccessPackFileHashes[2].Length).IsZero();

				await Assert.That(readBack.AccessPackFileHashes[3].ArchiveIndex).IsEqualTo((ushort)1);
				await Assert.That(readBack.AccessPackFileHashes[3].Offset).IsZero();
				await Assert.That(readBack.AccessPackFileHashes[3].Length).IsEqualTo(300_000u);
			}

			await AssertPackageVerifies(readBack);
		}

		[Test]
		public async Task HashesFractionSpanningMultipleFiles()
		{
			var dirPath = TempPath("spanning_dir.vpk");
			var fileA = CreateTestData(600_000, 1);
			var fileB = CreateTestData(800_000, 2);

			using (var package = new Package())
			{
				package.SetFileName(dirPath);

				// Both files fit in one chunk, and the 1 MiB fraction boundary falls in the middle of the second file
				package.AddFile("a.bin", fileA, multiChunk: true);
				package.AddFile("b.bin", fileB, multiChunk: true);
				package.Write(dirPath);
			}

			using var readBack = new Package();
			readBack.Read(dirPath);

			await Assert.That(readBack.AccessPackFileHashes).Count().IsEqualTo(2);

			var fraction0 = new byte[FractionSize];
			fileA.CopyTo(fraction0, 0);
			fileB.AsSpan(0, FractionSize - fileA.Length).CopyTo(fraction0.AsSpan(fileA.Length));

			using (Assert.Multiple())
			{
				await Assert.That(readBack.AccessPackFileHashes[0].Offset).IsZero();
				await Assert.That(readBack.AccessPackFileHashes[0].Length).IsEqualTo((uint)FractionSize);
				await Assert.That(readBack.AccessPackFileHashes[0].Checksum).IsEquivalentTo(HashBlake3(fraction0), CollectionOrdering.Matching);

				await Assert.That(readBack.AccessPackFileHashes[1].Offset).IsEqualTo((uint)FractionSize);
				await Assert.That(readBack.AccessPackFileHashes[1].Length).IsEqualTo((uint)(fileA.Length + fileB.Length - FractionSize));
				await Assert.That(readBack.AccessPackFileHashes[1].Checksum).IsEquivalentTo(HashBlake3(fileB.AsSpan(FractionSize - fileA.Length)), CollectionOrdering.Matching);
			}

			await AssertPackageVerifies(readBack);
		}

		[Test]
		public async Task WritesChunkDataInAddOrder()
		{
			var dirPath = TempPath("add_order_dir.vpk");

			using (var package = new Package())
			{
				package.SetFileName(dirPath);
				package.WriteChunkSize = 1024;

				// Chunk data is laid out in the order files are added, not in file tree order which groups files by extension
				for (var i = 0; i < 3; i++)
				{
					var entryA = package.AddFile($"a{i}.txt", CreateTestData(600, (byte)(i * 2)), multiChunk: true);
					var entryB = package.AddFile($"b{i}.jpg", CreateTestData(600, (byte)((i * 2) + 1)), multiChunk: true);

					using (Assert.Multiple())
					{
						await Assert.That(entryA.ArchiveIndex).IsEqualTo((ushort)i);
						await Assert.That(entryA.Offset).IsZero();
						await Assert.That(entryB.ArchiveIndex).IsEqualTo((ushort)i);
						await Assert.That(entryB.Offset).IsEqualTo(600u);
					}
				}

				package.Write(dirPath);
			}

			using var readBack = new Package();
			readBack.Read(dirPath);

			await AssertPackageVerifies(readBack);

			for (var i = 0; i < 3; i++)
			{
				var entryA = await AssertEntryData(readBack, $"a{i}.txt", 600, (byte)(i * 2));
				var entryB = await AssertEntryData(readBack, $"b{i}.jpg", 600, (byte)((i * 2) + 1));

				using (Assert.Multiple())
				{
					await Assert.That(entryA.ArchiveIndex).IsEqualTo((ushort)i);
					await Assert.That(entryB.ArchiveIndex).IsEqualTo((ushort)i);
				}
			}
		}

		[Test]
		public async Task RemoveFileLeavesDataInChunkFiles()
		{
			var dirPath = TempPath("removed_dir.vpk");

			using (var package = new Package())
			{
				package.SetFileName(dirPath);
				package.WriteChunkSize = 1024;

				for (var i = 0; i < 9; i++)
				{
					package.AddFile($"chunked_{i}.bin", CreateTestData(400, (byte)i), multiChunk: true);
				}

				using (Assert.Multiple())
				{
					// Remove a file from the middle of chunk 0, and all files of chunk 1
					await Assert.That(package.RemoveFile(package.FindEntry("chunked_1.bin")!)).IsTrue();
					await Assert.That(package.RemoveFile(package.FindEntry("chunked_3.bin")!)).IsTrue();
					await Assert.That(package.RemoveFile(package.FindEntry("chunked_4.bin")!)).IsTrue();
					await Assert.That(package.RemoveFile(package.FindEntry("chunked_5.bin")!)).IsTrue();
				}

				package.Write(dirPath);
			}

			// Data of removed files stays in the chunk files, and the chunk hashes still cover it
			using (Assert.Multiple())
			{
				await Assert.That(new FileInfo(TempPath("removed_000.vpk")).Length).IsEqualTo(1200L);
				await Assert.That(new FileInfo(TempPath("removed_001.vpk")).Length).IsEqualTo(1200L);
				await Assert.That(new FileInfo(TempPath("removed_002.vpk")).Length).IsEqualTo(1200L);
			}

			using var readBack = new Package();
			readBack.Read(dirPath);

			await AssertPackageVerifies(readBack);

			await Assert.That(readBack.AccessPackFileHashes).Count().IsEqualTo(3);

			foreach (var i in new[] { 0, 2, 6, 7, 8 })
			{
				await AssertEntryData(readBack, $"chunked_{i}.bin", 400, (byte)i);
			}

			await Assert.That(readBack.FindEntry("chunked_1.bin")).IsNull();
		}

		[Test]
		public async Task AddFileThrowsWhenExceedingChunkLimit()
		{
			using var package = new Package();
			package.SetFileName(TempPath("limit_dir.vpk"));
			package.WriteChunkSize = 1;

			// With a chunk size of 1 every file of 2 bytes starts a new chunk
			PackageEntry? entry = null;

			for (var i = 0; i < 0x7FFF; i++)
			{
				entry = package.AddFile($"{i}.bin", [1, 2], multiChunk: true);
			}

			await Assert.That(entry!.ArchiveIndex).IsEqualTo((ushort)0x7FFE);

			await Assert.That(() => package.AddFile("one too many.bin", [1, 2], multiChunk: true)).ThrowsExactly<InvalidOperationException>()
				.WithMessageContaining("maximum amount of chunk files", StringComparison.Ordinal);
		}

		[Test]
		public async Task WriteVersion1MultiChunkPackage()
		{
			var dirPath = TempPath("v1_dir.vpk");

			using (var package = new Package())
			{
				package.SetFileName(dirPath);
				package.Version = 1;
				package.WriteChunkSize = 1024;

				for (var i = 0; i < 5; i++)
				{
					package.AddFile($"chunked_{i}.bin", CreateTestData(400, (byte)i), multiChunk: true);
				}

				package.Write(dirPath);
			}

			using (Assert.Multiple())
			{
				await Assert.That(File.Exists(TempPath("v1_000.vpk"))).IsTrue();
				await Assert.That(File.Exists(TempPath("v1_001.vpk"))).IsTrue();
			}

			using var readBack = new Package();
			readBack.Read(dirPath);

			using (Assert.Multiple())
			{
				await Assert.That(readBack.Version).IsEqualTo(1u);
				await Assert.That(readBack.ArchiveMD5SectionSize).IsZero();
			}

			// Version 1 has no hashes to verify beyond the file checksums
			await Assert.That(() => readBack.VerifyFileChecksums()).ThrowsNothing();

			for (var i = 0; i < 5; i++)
			{
				await AssertEntryData(readBack, $"chunked_{i}.bin", 400, (byte)i);
			}
		}

		[Test]
		public async Task AddFileWithMultiChunkThrowsWithoutFileName()
		{
			using var package = new Package();

			await Assert.That(() => package.AddFile("chunked.bin", CreateTestData(400, 0), multiChunk: true)).ThrowsExactly<InvalidOperationException>()
				.WithMessageContaining("SetFileName", StringComparison.Ordinal);
		}

		[Test]
		public async Task WriteThrowsForDifferentDirectoryFileName()
		{
			using var package = new Package();
			package.SetFileName(TempPath("expected_dir.vpk"));
			package.AddFile("chunked.bin", CreateTestData(400, 0), multiChunk: true);

			await Assert.That(() => package.Write(TempPath("other_dir.vpk"))).ThrowsExactly<InvalidOperationException>()
				.WithMessageContaining("expected_dir.vpk", StringComparison.Ordinal);

			await Assert.That(File.Exists(TempPath("other_dir.vpk"))).IsFalse();
		}

		[Test]
		public async Task SetFileNameThrowsAfterChunkFileWasCreated()
		{
			using var package = new Package();
			package.SetFileName(TempPath("first_dir.vpk"));
			package.AddFile("chunked.bin", CreateTestData(400, 0), multiChunk: true);

			await Assert.That(() => package.SetFileName(TempPath("second_dir.vpk"))).ThrowsExactly<InvalidOperationException>();
		}

		[Test]
		public async Task WritesChunkDataWhenFilesAreAdded()
		{
			var dirPath = TempPath("streamed_dir.vpk");

			// A package that was not read replaces existing chunk files
			await File.WriteAllBytesAsync(TempPath("streamed_000.vpk"), new byte[4096]);

			using var package = new Package();
			package.SetFileName(dirPath);
			package.WriteChunkSize = 1024;

			package.AddFile("a.bin", CreateTestData(600, 1), multiChunk: true);
			var entry = package.AddFile("b.bin", CreateTestData(600, 2), multiChunk: true);

			using (Assert.Multiple())
			{
				await Assert.That(new FileInfo(TempPath("streamed_000.vpk")).Length).IsEqualTo(1200L);
				await Assert.That(File.Exists(dirPath)).IsFalse();
			}

			// Entries can be read back before the directory file is written
			package.ReadEntry(entry, out var data);
			await Assert.That(data).IsEquivalentTo(CreateTestData(600, 2), CollectionOrdering.Matching);
		}

		[Test]
		public async Task AddsFilesFromNonSeekableStreams()
		{
			var dirPath = TempPath("forward_only_dir.vpk");
			var largeData = CreateTestData(3 * 1024 * 1024 / 2, 1); // larger than the copy buffer

			using (var package = new Package())
			{
				package.SetFileName(dirPath);

				using (var stream = new ForwardOnlyStream(largeData))
				{
					var entry = package.AddFile("large.bin", stream, multiChunk: true);
					await Assert.That(entry.ArchiveIndex).IsZero();
				}

				using (var stream = new ForwardOnlyStream(CreateTestData(400, 2)))
				{
					var entry = package.AddFile("in_dir.bin", stream);
					await Assert.That(entry.ArchiveIndex).IsEqualTo((ushort)0x7FFF);
				}

				using (var stream = new ForwardOnlyStream([]))
				{
					// Files with no data must stay in the directory file
					var entry = package.AddFile("empty.bin", stream, multiChunk: true);
					await Assert.That(entry.ArchiveIndex).IsEqualTo((ushort)0x7FFF);
				}

				package.Write(dirPath);
			}

			using var readBack = new Package();
			readBack.Read(dirPath);

			await AssertPackageVerifies(readBack);
			await AssertEntryData(readBack, "large.bin", largeData.Length, 1);
			await AssertEntryData(readBack, "in_dir.bin", 400, 2);
			await AssertEntryData(readBack, "empty.bin", 0, 0);
		}

		[Test]
		public async Task WritesDirectoryFileToStream()
		{
			var dirPath = TempPath("to_stream_dir.vpk");

			using (var package = new Package())
			{
				package.SetFileName(dirPath);
				package.AddFile("chunked.bin", CreateTestData(400, 1), multiChunk: true);
				package.AddFile("in_dir.bin", CreateTestData(400, 2));

				using var output = new FileStream(dirPath, FileMode.Create, FileAccess.ReadWrite);
				package.Write(output);
			}

			using var readBack = new Package();
			readBack.Read(dirPath);

			await AssertPackageVerifies(readBack);
			await AssertEntryData(readBack, "chunked.bin", 400, 1);
			await AssertEntryData(readBack, "in_dir.bin", 400, 2);
		}

		[Test]
		public async Task WritesAgainAfterAddingMoreFiles()
		{
			var dirPath = TempPath("rewritten_dir.vpk");

			using (var package = new Package())
			{
				package.SetFileName(dirPath);
				package.WriteChunkSize = 1024;

				package.AddFile("first.bin", CreateTestData(400, 1), multiChunk: true);
				package.Write(dirPath);

				// The current chunk file stays open, so later files are appended to it
				package.AddFile("second.bin", CreateTestData(400, 2), multiChunk: true);
				package.AddFile("third.bin", CreateTestData(400, 3), multiChunk: true);
				package.AddFile("fourth.bin", CreateTestData(400, 4), multiChunk: true);
				package.Write(dirPath);
			}

			using (Assert.Multiple())
			{
				await Assert.That(new FileInfo(TempPath("rewritten_000.vpk")).Length).IsEqualTo(1200L);
				await Assert.That(new FileInfo(TempPath("rewritten_001.vpk")).Length).IsEqualTo(400L);
			}

			using var readBack = new Package();
			readBack.Read(dirPath);

			await AssertPackageVerifies(readBack);
			await Assert.That(readBack.AccessPackFileHashes).Count().IsEqualTo(2);

			await AssertEntryData(readBack, "first.bin", 400, 1);
			await AssertEntryData(readBack, "second.bin", 400, 2);
			await AssertEntryData(readBack, "third.bin", 400, 3);
			await AssertEntryData(readBack, "fourth.bin", 400, 4);
		}

		[Test]
		public async Task WriteChunkSizeValidation()
		{
			using var package = new Package();
			await Assert.That(package.WriteChunkSize).IsEqualTo(100 * 1024 * 1024);
			await Assert.That(() => package.WriteChunkSize = 0).ThrowsExactly<ArgumentOutOfRangeException>();
			await Assert.That(() => package.WriteChunkSize = -1).ThrowsExactly<ArgumentOutOfRangeException>();
		}

		[Test]
		public async Task StartsNewChunkFileForStreamThatDoesNotFitTheCurrentOne()
		{
			using var package = new Package();
			package.SetFileName(TempPath("big_dir.vpk"));

			var first = package.AddFile("first.bin", CreateTestData(100, 1), multiChunk: true);

			// Streams report lengths that would take a chunk file past 4 GiB
			using var fitsCurrent = new ReportedLengthStream(CreateTestData(200, 2), uint.MaxValue - 100L);
			var second = package.AddFile("second.bin", fitsCurrent, multiChunk: true);

			using var fitsNew = new ReportedLengthStream(CreateTestData(300, 3), uint.MaxValue - 299L);
			var third = package.AddFile("third.bin", fitsNew, multiChunk: true);

			using var fitsNone = new ReportedLengthStream(CreateTestData(10, 4), uint.MaxValue + 1L);

			using (Assert.Multiple())
			{
				await Assert.That(first.ArchiveIndex).IsEqualTo((ushort)0);
				await Assert.That(second.ArchiveIndex).IsEqualTo((ushort)0);
				await Assert.That(third.ArchiveIndex).IsEqualTo((ushort)1);
				await Assert.That(() => package.AddFile("none.bin", fitsNone, multiChunk: true)).ThrowsExactly<ArgumentException>();
				await Assert.That(File.Exists(TempPath("big_002.vpk"))).IsFalse();
			}

			await AssertEntryData(package, "second.bin", 200, 2);
			await AssertEntryData(package, "third.bin", 300, 3);
		}

		[Test]
		public async Task WriteRefusesFilesItReadsFromAndMissingChunkFiles()
		{
			var dirPath = CopyFixtures("test_dir.vpk", "test_000.vpk");
			var originalDir = await File.ReadAllBytesAsync(dirPath);

			using (var package = new Package())
			{
				package.Read(dirPath);

				foreach (var path in new[] { dirPath, TempPath("test_000.vpk") })
				{
					using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
					await Assert.That(() => package.Write(stream)).ThrowsExactly<InvalidOperationException>();
				}
			}

			using var memoryPackage = new Package();
			var input = ReadFromMemory(memoryPackage, dirPath);
			await Assert.That(() => memoryPackage.Write(input)).ThrowsExactly<InvalidOperationException>();

			File.Delete(TempPath("test_000.vpk"));

			await Assert.That(() => memoryPackage.Write(dirPath)).ThrowsExactly<FileNotFoundException>();
			await Assert.That(await File.ReadAllBytesAsync(dirPath)).IsEquivalentTo(originalDir, CollectionOrdering.Matching);
		}

		[Test]
		public async Task EditsReadPackageWithChunkFiles()
		{
			var dirPath = CopyFixtures("test_dir.vpk", "test_000.vpk");
			var originalChunk = await File.ReadAllBytesAsync(TempPath("test_000.vpk"));
			var stale = CreateTestData(100, 9);
			var preload = CreateTestData(10, 7);

			// Chunk files that the directory file does not refer to are skipped
			await File.WriteAllBytesAsync(TempPath("test_001.vpk"), stale);
			await File.WriteAllBytesAsync(TempPath("test_003.vpk"), stale);

			using (var package = new Package())
			{
				ReadFromMemory(package, dirPath);
				package.WriteChunkSize = 400;
				package.RemoveFile(package.FindEntry("steammessages_base.proto")!);

				var replaced = package.AddFile("steammessages_base.proto", CreateTestData(500, 3), multiChunk: true);
				replaced.SmallData = preload;
				replaced.CRC32 = Crc32.HashToUInt32([.. preload, .. CreateTestData(500, 3)]);
				var second = package.AddFile("added/second.bin", CreateTestData(100, 4), multiChunk: true);
				package.AddFile("added/embedded.bin", CreateTestData(300, 2));

				using (Assert.Multiple())
				{
					await Assert.That(replaced.ArchiveIndex).IsEqualTo((ushort)2);
					await Assert.That(second.ArchiveIndex).IsEqualTo((ushort)4);
				}

				package.Write(dirPath);
			}

			// Data of removed files stays in the chunk file it is in
			using (Assert.Multiple())
			{
				await Assert.That(await File.ReadAllBytesAsync(TempPath("test_000.vpk"))).IsEquivalentTo(originalChunk, CollectionOrdering.Matching);
				await Assert.That(Directory.GetFiles(TempDirectory)).Count().IsEqualTo(6);
			}

			using (var readBack = new Package())
			{
				readBack.Read(dirPath);

				await AssertPackageVerifies(readBack);
				await AssertEntryData(readBack, "added/second.bin", 100, 4);
				await AssertEntryData(readBack, "added/embedded.bin", 300, 2);
				await Assert.That(readBack.FindEntry("steammessages_base.proto")!.SmallData).Count().IsEqualTo(preload.Length);

				// The read chunk file had no hashes, and data in the directory file is not hashed when there are chunk files
				await Assert.That(readBack.AccessPackFileHashes.Select(static fraction => fraction.ArchiveIndex).ToArray()).IsEquivalentTo(new ushort[] { 2, 4 }, CollectionOrdering.Matching);
			}

			// Hashes that cover the whole chunk file are kept even when its data changed, and are computed again once it grew
			await File.WriteAllBytesAsync(TempPath("test_004.vpk"), stale);
			await Assert.That(() => WriteFromMemoryAndVerifyChunkHashes(dirPath)).ThrowsExactly<InvalidDataException>();

			await File.AppendAllTextAsync(TempPath("test_004.vpk"), "grown");
			await Assert.That(() => WriteFromMemoryAndVerifyChunkHashes(dirPath)).ThrowsNothing();
		}

		[Test]
		public async Task WritesOverTheFileItWasReadFrom()
		{
			var path = CopyFixtures("preload.vpk");

			using (var package = new Package())
			{
				package.Read(path);
				package.AddFile("added.bin", CreateTestData(300, 2));
				package.AddFile("chunked.bin", CreateTestData(500, 3), multiChunk: true);
				package.Write(path);

				// The package reads from the written file, where the data of every entry has moved
				await AssertPackageVerifies(package);
				await Assert.That(package.FindEntry("lorem.txt")!.SmallData).Count().IsEqualTo(56);

				package.Write(path);
			}

			await Assert.That(Directory.GetFiles(TempDirectory)).Count().IsEqualTo(2);

			using var readBack = new Package();
			readBack.Read(path);

			await AssertPackageVerifies(readBack);
			await AssertEntryData(readBack, "added.bin", 300, 2);
			await AssertEntryData(readBack, "chunked.bin", 500, 3);
			await Assert.That(readBack.FindEntry("lorem.txt")!.SmallData).Count().IsEqualTo(56);
		}

		[Test]
		[RunOn(OS.Windows)]
		public async Task WritesOtherFilesInPlaceAndKeepsTheReadFileWhenItCanNotBeReplaced()
		{
			var path = CopyFixtures("preload.vpk");
			var original = await File.ReadAllBytesAsync(path);
			var otherPath = TempPath("other.vpk");
			await File.WriteAllBytesAsync(otherPath, new byte[4096]);
			File.SetAttributes(otherPath, FileAttributes.Hidden);

			using var package = new Package();
			package.Read(path);
			package.AddFile("added.bin", CreateTestData(300, 2));

			File.SetAttributes(path, FileAttributes.ReadOnly);
			await Assert.That(() => package.Write(path)).ThrowsExactly<UnauthorizedAccessException>();
			File.SetAttributes(path, FileAttributes.Normal);

			package.Write(otherPath);

			using var readBack = new Package();
			readBack.Read(otherPath);

			using (Assert.Multiple())
			{
				await Assert.That(await File.ReadAllBytesAsync(path)).IsEquivalentTo(original, CollectionOrdering.Matching);
				await Assert.That(Directory.GetFiles(TempDirectory)).Count().IsEqualTo(2);
				await Assert.That(File.GetAttributes(otherPath).HasFlag(FileAttributes.Hidden)).IsTrue();
				await Assert.That(() => readBack.VerifyFileChecksums()).ThrowsNothing();
			}
		}

		[Test]
		public async Task CutsOffOtherFileWhenWritingFailsPartway()
		{
			var path = CopyFixtures("test_single.vpk");
			var otherPath = TempPath("other.vpk");
			var stale = new byte[65536];
			Array.Fill(stale, (byte)0xAA);
			await File.WriteAllBytesAsync(otherPath, stale);

			using var package = new Package();
			package.Read(path);

			// Cut off the embedded data that writing reads from the read file
			using (var source = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
			{
				source.SetLength(package.HeaderSize + package.TreeSize);
			}

			await Assert.That(() => package.Write(otherPath)).ThrowsExactly<EndOfStreamException>();

			var written = await File.ReadAllBytesAsync(otherPath);

			// The header and tree were written, and nothing of the old file follows them
			using (Assert.Multiple())
			{
				await Assert.That(BitConverter.ToInt32(written)).IsEqualTo(Package.MAGIC);
				await Assert.That(written.Length).IsLessThan(stale.Length);
			}
		}

		[Test]
		public async Task RepackDropsDataOfRemovedFiles()
		{
			var dirPath = TempPath("repack_dir.vpk");

			using (var package = new Package())
			{
				package.SetFileName(dirPath);
				package.WriteChunkSize = 1000;

				for (var i = 0; i < 9; i++)
				{
					package.AddFile($"file_{i}.bin", CreateTestData(400, (byte)i), multiChunk: true);
				}

				package.AddFile("in_dir.bin", CreateTestData(300, 9));
				package.Write(dirPath);

				foreach (var i in new[] { 0, 1, 3, 4, 5, 7, 8 })
				{
					package.RemoveFile(package.FindEntry($"file_{i}.bin")!);
				}

				package.AddFile("last.bin", CreateTestData(100, 9), multiChunk: true);
				package.Write(dirPath, repack: true);

				var next = package.AddFile("next.bin", CreateTestData(400, 8), multiChunk: true);
				await Assert.That(next.ArchiveIndex).IsEqualTo((ushort)1);

				package.Write(dirPath);
			}

			await Assert.That(Directory.GetFiles(TempDirectory)).Count().IsEqualTo(3);
			File.SetAttributes(TempPath("repack_000.vpk"), FileAttributes.ReadOnly);
			File.SetAttributes(TempPath("repack_001.vpk"), FileAttributes.ReadOnly);

			using (var package = new Package())
			{
				package.Read(dirPath);
				package.RemoveFile(package.FindEntry("file_2.bin")!);
				package.Write(dirPath, repack: true);

				// The package now reads from the repacked files
				await AssertPackageVerifies(package);
				await AssertEntryData(package, "in_dir.bin", 300, 9);
			}

			await Assert.That(Directory.GetFiles(TempDirectory)).Count().IsEqualTo(2);

			using var readBack = new Package();
			readBack.Read(dirPath);

			// Data is written in directory tree order
			await Assert.That((await AssertEntryData(readBack, "file_6.bin", 400, 6)).Offset).IsZero();
			await Assert.That((await AssertEntryData(readBack, "next.bin", 400, 8)).Offset).IsEqualTo(500u);
		}

		[Test]
		public async Task RepackWritesToNewBaseName()
		{
			var dirPath = CopyFixtures("test_dir.vpk", "test_000.vpk");
			var originalFiles = Directory.GetFiles(TempDirectory).ToDictionary(static path => path, File.ReadAllBytes);
			var newPath = TempPath("copy_dir.vpk");
			await File.WriteAllTextAsync(TempPath("copy_001.vpk"), "stale");
			await File.WriteAllTextAsync(TempPath("copy_003.vpk"), "stale");

			using (var package = new Package())
			{
				package.Read(dirPath);
				package.Write(newPath, repack: true);

				await Assert.That(package.FileName).IsEqualTo(TempPath("copy"));
				await Assert.That(() => package.Write(dirPath)).ThrowsExactly<InvalidOperationException>();
				await AssertPackageVerifies(package);
			}

			// A package without chunk files leaves the chunk files of another package with the same name alone
			using (var package = new Package())
			{
				package.AddFile("a.bin", CreateTestData(10, 1));
				package.Write(TempPath("test.vpk"), repack: true);
			}

			await AssertFilesUnchanged(originalFiles);

			// Deleting stops at the first chunk file that does not exist
			await Assert.That(File.Exists(TempPath("copy_001.vpk"))).IsFalse();
			await Assert.That(File.Exists(TempPath("copy_003.vpk"))).IsTrue();
		}

		[Test]
		[RunOn(OS.Windows)]
		public async Task RepackKeepsAllFilesWhenOneCanNotBeReplaced()
		{
			var dirPath = CopyFixtures("test_dir.vpk", "test_000.vpk");
			var originalFiles = Directory.GetFiles(TempDirectory).ToDictionary(static path => path, File.ReadAllBytes);

			using var package = new Package();
			package.Read(dirPath);
			package.RemoveFile(package.FindEntry("kitten.jpg")!);

			// The chunk file is replaced before the directory file, which can not be
			File.SetAttributes(dirPath, FileAttributes.ReadOnly);
			await Assert.That(() => package.Write(dirPath, repack: true)).ThrowsExactly<UnauthorizedAccessException>();
			File.SetAttributes(dirPath, FileAttributes.Normal);

			await Assert.That(Directory.GetFiles(TempDirectory)).Count().IsEqualTo(originalFiles.Count);

			await AssertFilesUnchanged(originalFiles);

			// The package still reads the original files, and can be repacked once they can be replaced
			await AssertPackageVerifies(package);
			package.Write(dirPath, repack: true);
			await AssertPackageVerifies(package);
		}

		[Test]
		public async Task RepackRefusesToReplaceChunkFilesOfTheReadFileFromAnotherPath()
		{
			var dirPath = CopyFixtures("test_dir.vpk", "test_000.vpk");
			var originalFiles = Directory.GetFiles(TempDirectory).ToDictionary(static path => path, File.ReadAllBytes);

			using var package = new Package();
			package.Read(dirPath);
			package.RemoveFile(package.FindEntry("kitten.jpg")!);

			// test.vpk has the same chunk file names as test_dir.vpk, which would be left referring to replaced chunk files
			await Assert.That(() => package.Write(TempPath("test.vpk"), repack: true)).ThrowsExactly<InvalidOperationException>();
			await Assert.That(Directory.GetFiles(TempDirectory)).Count().IsEqualTo(originalFiles.Count);

			await AssertFilesUnchanged(originalFiles);
			await AssertPackageVerifies(package);
		}

		private string TempPath(string fileName)
		{
			return Path.Combine(TempDirectory, fileName);
		}

		private string CopyFixtures(params string[] fileNames)
		{
			foreach (var fileName in fileNames)
			{
				File.Copy(Path.Combine(AppContext.BaseDirectory, "Files", fileName), TempPath(fileName));
			}

			return TempPath(fileNames[0]);
		}

		private static async Task AssertFilesUnchanged(Dictionary<string, byte[]> originalFiles)
		{
			foreach (var (path, data) in originalFiles)
			{
				await Assert.That(await File.ReadAllBytesAsync(path)).IsEquivalentTo(data, CollectionOrdering.Matching);
			}
		}

		private static MemoryStream ReadFromMemory(Package package, string dirPath)
		{
			var input = new MemoryStream(File.ReadAllBytes(dirPath));
			package.SetFileName(dirPath);
			package.Read(input);

			return input;
		}

		private static void WriteFromMemoryAndVerifyChunkHashes(string dirPath)
		{
			using (var package = new Package())
			{
				ReadFromMemory(package, dirPath);
				package.Write(dirPath);
			}

			using var readBack = new Package();
			readBack.Read(dirPath);
			readBack.VerifyChunkHashes();
		}

		private static byte[] HashBlake3(ReadOnlySpan<byte> data)
		{
			using var hasher = Blake3.Hasher.New();
			hasher.Update(data);

			var hash = new byte[16];
			hasher.Finalize(hash);

			return hash;
		}

		private static async Task AssertPackageVerifies(Package package)
		{
			using (Assert.Multiple())
			{
				await Assert.That(() => package.VerifyHashes()).ThrowsNothing();
				await Assert.That(() => package.VerifyChunkHashes()).ThrowsNothing();
				await Assert.That(() => package.VerifyFileChecksums()).ThrowsNothing();
			}
		}

		private static async Task<PackageEntry> AssertEntryData(Package package, string path, int length, byte seed)
		{
			var entry = package.FindEntry(path);
			await Assert.That(entry).IsNotNull();

			package.ReadEntry(entry, out var data);
			await Assert.That(data).IsEquivalentTo(CreateTestData(length, seed), CollectionOrdering.Matching);

			return entry;
		}

		private static byte[] CreateTestData(int length, byte seed)
		{
			var data = new byte[length];

			for (var i = 0; i < length; i++)
			{
				data[i] = (byte)(seed + (i * 37));
			}

			return data;
		}

		/// <summary>
		/// Stream that can not seek and returns at most 1000 bytes per read.
		/// </summary>
		private sealed class ForwardOnlyStream : Stream
		{
			private readonly MemoryStream Inner;

			public ForwardOnlyStream(byte[] data)
			{
				Inner = new MemoryStream(data, writable: false);
			}

			public override bool CanRead => true;
			public override bool CanSeek => false;
			public override bool CanWrite => false;
			public override long Length => throw new NotSupportedException();
			public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
			public override void Flush() { }
			public override int Read(byte[] buffer, int offset, int count) => Inner.Read(buffer, offset, Math.Min(count, 1000));
			public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
			public override void SetLength(long value) => throw new NotSupportedException();
			public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

			protected override void Dispose(bool disposing)
			{
				if (disposing)
				{
					Inner.Dispose();
				}

				base.Dispose(disposing);
			}
		}

		/// <summary>
		/// Memory stream that reports a different length than the data it holds.
		/// </summary>
		private sealed class ReportedLengthStream : MemoryStream
		{
			private readonly long ReportedLength;

			public ReportedLengthStream(byte[] data, long reportedLength)
				: base(data, writable: false)
			{
				ReportedLength = reportedLength;
			}

			public override long Length => ReportedLength;
		}
	}
}
