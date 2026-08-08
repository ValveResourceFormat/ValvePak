using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using SteamDatabase.ValvePak;

namespace ValvePak.Test
{
	[TestFixture]
	internal sealed class MultiChunkWriteTest
	{
		private const int FractionSize = 1024 * 1024;

		private string TempDirectory;

		[SetUp]
		public void SetUp()
		{
			TempDirectory = Path.Combine(Path.GetTempPath(), "ValvePakTest_" + Path.GetRandomFileName());
			Directory.CreateDirectory(TempDirectory);
		}

		[TearDown]
		public void TearDown()
		{
			Directory.Delete(TempDirectory, recursive: true);
		}

		[Test]
		public void WriteMultiChunkPackage()
		{
			var dirPath = TempPath("test_dir.vpk");

			using (var package = new Package())
			{
				package.WriteChunkSize = 1024;

				for (var i = 0; i < 10; i++)
				{
					var entry = package.AddFile($"files/chunked_{i}.bin", CreateTestData(400, (byte)i), multiChunk: true);

					// 400 byte files with a 1024 chunk size, so three files per chunk
					Assert.That(entry.ArchiveIndex, Is.EqualTo((ushort)(i / 3)));
				}

				package.AddFile("in_dir.txt", Encoding.UTF8.GetBytes("this file is in the directory file"));

				// Files with no data must stay in the directory file
				var emptyEntry = package.AddFile("empty.bin", [], multiChunk: true);
				Assert.That(emptyEntry.ArchiveIndex, Is.EqualTo(0x7FFF));

				package.Write(dirPath);
			}

			using (Assert.EnterMultipleScope())
			{
				Assert.That(new FileInfo(TempPath("test_000.vpk")).Length, Is.EqualTo(1200));
				Assert.That(new FileInfo(TempPath("test_001.vpk")).Length, Is.EqualTo(1200));
				Assert.That(new FileInfo(TempPath("test_002.vpk")).Length, Is.EqualTo(1200));
				Assert.That(new FileInfo(TempPath("test_003.vpk")).Length, Is.EqualTo(400));
				Assert.That(File.Exists(TempPath("test_004.vpk")), Is.False);
			}

			using var readBack = new Package();
			readBack.Read(dirPath);

			using (Assert.EnterMultipleScope())
			{
				Assert.That(readBack.IsDirVPK, Is.True);
				Assert.That(readBack.ArchiveMD5SectionSize, Is.EqualTo(4 * ChunkHashFraction.SectionEntrySize));
				Assert.That(readBack.AccessPackFileHashes, Has.Count.EqualTo(4));
			}

			AssertPackageVerifies(readBack);

			for (var i = 0; i < 10; i++)
			{
				var entry = AssertEntryData(readBack, $"files/chunked_{i}.bin", 400, (byte)i);
				Assert.That(entry.ArchiveIndex, Is.EqualTo((ushort)(i / 3)));
			}

			var dirEntry = readBack.FindEntry("in_dir.txt");
			Assert.That(dirEntry, Is.Not.Null);
			Assert.That(dirEntry.ArchiveIndex, Is.EqualTo(0x7FFF));
		}

		[Test]
		public void WritesChunkHashFractions()
		{
			var dirPath = TempPath("fractions_dir.vpk");
			var fileA = CreateTestData(2 * FractionSize, 1); // exactly two fractions
			var fileB = CreateTestData(300_000, 2);

			using (var package = new Package())
			{
				package.WriteChunkSize = 1024 * 1024;
				package.AddFile("a.bin", fileA, multiChunk: true);
				package.AddFile("b.bin", fileB, multiChunk: true);
				package.Write(dirPath);
			}

			using var readBack = new Package();
			readBack.Read(dirPath);

			// Chunk 0 is an exact multiple of the 1 MiB fraction size, which produces a trailing zero sized fraction
			Assert.That(readBack.AccessPackFileHashes, Has.Count.EqualTo(4));

			using (Assert.EnterMultipleScope())
			{
				Assert.That(readBack.AccessPackFileHashes[0].ArchiveIndex, Is.Zero);
				Assert.That(readBack.AccessPackFileHashes[0].Offset, Is.Zero);
				Assert.That(readBack.AccessPackFileHashes[0].Length, Is.EqualTo(FractionSize));
				Assert.That(readBack.AccessPackFileHashes[0].HashType, Is.EqualTo(EHashType.MD5));
				Assert.That(readBack.AccessPackFileHashes[0].Checksum, Is.EqualTo(MD5.HashData(fileA.AsSpan(0, FractionSize))));

				Assert.That(readBack.AccessPackFileHashes[1].ArchiveIndex, Is.Zero);
				Assert.That(readBack.AccessPackFileHashes[1].Offset, Is.EqualTo(FractionSize));
				Assert.That(readBack.AccessPackFileHashes[1].Length, Is.EqualTo(FractionSize));

				Assert.That(readBack.AccessPackFileHashes[2].ArchiveIndex, Is.Zero);
				Assert.That(readBack.AccessPackFileHashes[2].Offset, Is.EqualTo(2 * FractionSize));
				Assert.That(readBack.AccessPackFileHashes[2].Length, Is.Zero);

				Assert.That(readBack.AccessPackFileHashes[3].ArchiveIndex, Is.EqualTo(1));
				Assert.That(readBack.AccessPackFileHashes[3].Offset, Is.Zero);
				Assert.That(readBack.AccessPackFileHashes[3].Length, Is.EqualTo(300_000));
			}

			AssertPackageVerifies(readBack);
		}

		[Test]
		public void HashesFractionSpanningMultipleFiles()
		{
			var dirPath = TempPath("spanning_dir.vpk");
			var fileA = CreateTestData(600_000, 1);
			var fileB = CreateTestData(800_000, 2);

			using (var package = new Package())
			{
				// Both files fit in one chunk, and the 1 MiB fraction boundary falls in the middle of the second file
				package.AddFile("a.bin", fileA, multiChunk: true);
				package.AddFile("b.bin", fileB, multiChunk: true);
				package.Write(dirPath);
			}

			using var readBack = new Package();
			readBack.Read(dirPath);

			Assert.That(readBack.AccessPackFileHashes, Has.Count.EqualTo(2));

			var fraction0 = new byte[FractionSize];
			fileA.CopyTo(fraction0, 0);
			fileB.AsSpan(0, FractionSize - fileA.Length).CopyTo(fraction0.AsSpan(fileA.Length));

			using (Assert.EnterMultipleScope())
			{
				Assert.That(readBack.AccessPackFileHashes[0].Offset, Is.Zero);
				Assert.That(readBack.AccessPackFileHashes[0].Length, Is.EqualTo(FractionSize));
				Assert.That(readBack.AccessPackFileHashes[0].Checksum, Is.EqualTo(MD5.HashData(fraction0)));

				Assert.That(readBack.AccessPackFileHashes[1].Offset, Is.EqualTo(FractionSize));
				Assert.That(readBack.AccessPackFileHashes[1].Length, Is.EqualTo(fileA.Length + fileB.Length - FractionSize));
				Assert.That(readBack.AccessPackFileHashes[1].Checksum, Is.EqualTo(MD5.HashData(fileB.AsSpan(FractionSize - fileA.Length))));
			}

			AssertPackageVerifies(readBack);
		}

		[Test]
		public void WritesChunkDataInterleavedByTreeOrder()
		{
			var dirPath = TempPath("interleaved_dir.vpk");

			using (var package = new Package())
			{
				package.WriteChunkSize = 1024;

				// Chunk data is written in file tree order, which groups files by extension.
				// Alternating extensions makes the write order differ from the add order,
				// so consecutive writes alternate between chunk files.
				for (var i = 0; i < 3; i++)
				{
					var entryA = package.AddFile($"a{i}.txt", CreateTestData(600, (byte)(i * 2)), multiChunk: true);
					var entryB = package.AddFile($"b{i}.jpg", CreateTestData(600, (byte)((i * 2) + 1)), multiChunk: true);

					using (Assert.EnterMultipleScope())
					{
						Assert.That(entryA.ArchiveIndex, Is.EqualTo(i));
						Assert.That(entryB.ArchiveIndex, Is.EqualTo(i));
					}
				}

				package.Write(dirPath);
			}

			using var readBack = new Package();
			readBack.Read(dirPath);

			AssertPackageVerifies(readBack);

			for (var i = 0; i < 3; i++)
			{
				var entryA = AssertEntryData(readBack, $"a{i}.txt", 600, (byte)(i * 2));
				var entryB = AssertEntryData(readBack, $"b{i}.jpg", 600, (byte)((i * 2) + 1));

				using (Assert.EnterMultipleScope())
				{
					Assert.That(entryA.ArchiveIndex, Is.EqualTo(i));
					Assert.That(entryB.ArchiveIndex, Is.EqualTo(i));
				}
			}
		}

		[Test]
		public void RemoveFileRewritesChunksAndHashes()
		{
			var dirPath = TempPath("removed_dir.vpk");

			using (var package = new Package())
			{
				package.WriteChunkSize = 1024;

				for (var i = 0; i < 9; i++)
				{
					package.AddFile($"chunked_{i}.bin", CreateTestData(400, (byte)i), multiChunk: true);
				}

				using (Assert.EnterMultipleScope())
				{
					// Remove a file from the middle of chunk 0, and all files of chunk 1
					Assert.That(package.RemoveFile(package.FindEntry("chunked_1.bin")!), Is.True);
					Assert.That(package.RemoveFile(package.FindEntry("chunked_3.bin")!), Is.True);
					Assert.That(package.RemoveFile(package.FindEntry("chunked_4.bin")!), Is.True);
					Assert.That(package.RemoveFile(package.FindEntry("chunked_5.bin")!), Is.True);
				}

				package.Write(dirPath);
			}

			using (Assert.EnterMultipleScope())
			{
				Assert.That(new FileInfo(TempPath("removed_000.vpk")).Length, Is.EqualTo(800));
				Assert.That(File.Exists(TempPath("removed_001.vpk")), Is.False);
				Assert.That(new FileInfo(TempPath("removed_002.vpk")).Length, Is.EqualTo(1200));
			}

			using var readBack = new Package();
			readBack.Read(dirPath);

			// Hashes must cover the rewritten chunk layout, not the layout at the time the files were added
			AssertPackageVerifies(readBack);

			Assert.That(readBack.AccessPackFileHashes, Has.Count.EqualTo(2));

			foreach (var i in new[] { 0, 2, 6, 7, 8 })
			{
				AssertEntryData(readBack, $"chunked_{i}.bin", 400, (byte)i);
			}

			Assert.That(readBack.FindEntry("chunked_1.bin"), Is.Null);
		}

		[Test]
		public void AddFileThrowsWhenExceedingChunkLimit()
		{
			using var package = new Package();
			package.WriteChunkSize = 1;

			// With a chunk size of 1 every file starts a new chunk
			PackageEntry? entry = null;

			for (var i = 0; i < 0x7FFF; i++)
			{
				entry = package.AddFile($"{i}.bin", [1], multiChunk: true);
			}

			Assert.That(entry!.ArchiveIndex, Is.EqualTo(0x7FFE));

			var ex = Assert.Throws<InvalidOperationException>(() => package.AddFile("one too many.bin", [1], multiChunk: true));
			Assert.That(ex.Message, Does.Contain("maximum amount of chunk files"));
		}

		[Test]
		public void WriteVersion1MultiChunkPackage()
		{
			var dirPath = TempPath("v1_dir.vpk");

			using (var package = new Package())
			{
				package.Version = 1;
				package.WriteChunkSize = 1024;

				for (var i = 0; i < 5; i++)
				{
					package.AddFile($"chunked_{i}.bin", CreateTestData(400, (byte)i), multiChunk: true);
				}

				package.Write(dirPath);
			}

			using (Assert.EnterMultipleScope())
			{
				Assert.That(File.Exists(TempPath("v1_000.vpk")), Is.True);
				Assert.That(File.Exists(TempPath("v1_001.vpk")), Is.True);
			}

			using var readBack = new Package();
			readBack.Read(dirPath);

			using (Assert.EnterMultipleScope())
			{
				Assert.That(readBack.Version, Is.EqualTo(1));
				Assert.That(readBack.ArchiveMD5SectionSize, Is.Zero);
			}

			// Version 1 has no hashes to verify beyond the file checksums
			Assert.DoesNotThrow(() => readBack.VerifyFileChecksums());

			for (var i = 0; i < 5; i++)
			{
				AssertEntryData(readBack, $"chunked_{i}.bin", 400, (byte)i);
			}
		}

		[Test]
		public void WriteToStreamThrowsWithChunkedFiles()
		{
			using var package = new Package();
			package.AddFile("chunked.bin", CreateTestData(400, 0), multiChunk: true);

			using var output = new MemoryStream();
			var ex = Assert.Throws<InvalidOperationException>(() => package.Write(output));
			Assert.That(ex.Message, Does.Contain("chunk files"));
		}

		[Test]
		public void WriteChunkSizeValidation()
		{
			using var package = new Package();
			Assert.That(package.WriteChunkSize, Is.EqualTo(200 * 1024 * 1024));
			Assert.Throws<ArgumentOutOfRangeException>(() => package.WriteChunkSize = 0);
			Assert.Throws<ArgumentOutOfRangeException>(() => package.WriteChunkSize = -1);
		}

		private string TempPath(string fileName)
		{
			return Path.Combine(TempDirectory, fileName);
		}

		private static void AssertPackageVerifies(Package package)
		{
			using (Assert.EnterMultipleScope())
			{
				Assert.DoesNotThrow(() => package.VerifyHashes());
				Assert.DoesNotThrow(() => package.VerifyChunkHashes());
				Assert.DoesNotThrow(() => package.VerifyFileChecksums());
			}
		}

		private static PackageEntry AssertEntryData(Package package, string path, int length, byte seed)
		{
			var entry = package.FindEntry(path);
			Assert.That(entry, Is.Not.Null);

			package.ReadEntry(entry, out var data);
			Assert.That(data, Is.EqualTo(CreateTestData(length, seed)));

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
	}
}
