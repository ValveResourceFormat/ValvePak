using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using SteamDatabase.ValvePak;

namespace Tests
{
	[TestFixture]
	public class WriteTest
	{
		[Test]
		public void CreateNewPackage()
		{
			var oldPath = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "steamdb_test_single.vpk");

			using var packageOld = new Package();
			packageOld.Read(oldPath);

			var fileEntry = packageOld.FindEntry("kitten.jpg");
			Assert.That(fileEntry, Is.Not.Null);
			packageOld.ReadEntry(fileEntry, out var fileData);

			var newName = "path/to/cool kitty.jpg";

			using var output = new MemoryStream();

			using (var packageNew = new Package())
			{
				packageNew.AddFile(newName, fileData);
				packageNew.AddFile("valvepak", Encoding.UTF8.GetBytes("This vpk was created by ValvePak 🤗\n\nVery cool!"));
				packageNew.Write(output);
			}

			output.Position = 0;

			// Verify
			using var packageWritten = new Package();
			packageWritten.SetFileName("test.vpk");
			packageWritten.Read(output);
			packageWritten.VerifyHashes();

			var newEntry = packageWritten.FindEntry(newName);

			Assert.That(newEntry, Is.Not.Null);
			Assert.Multiple(() =>
			{
				Assert.That(newEntry.CRC32, Is.EqualTo(0x9C800116));
				Assert.That(newEntry.ArchiveIndex, Is.EqualTo(0x7FFF));
				Assert.That(newEntry.DirectoryName, Is.EqualTo("path/to"));
				Assert.That(newEntry.TypeName, Is.EqualTo("jpg"));
				Assert.That(newEntry.FileName, Is.EqualTo("cool kitty"));
			});

			packageWritten.ReadEntry(newEntry, out var newFileData);
			Assert.Multiple(() =>
			{
				Assert.That(newFileData, Is.EqualTo(fileData));
				Assert.That(packageWritten.FindEntry("valvepak")!.CRC32, Is.EqualTo(0xF14F273C));
			});
		}

		[Test]
		public void WriteManyFiles()
		{
			using var output = new MemoryStream();
			using var packageNew = new Package();

			for (var i = 0; i < 1000; i++)
			{
				packageNew.AddFile($"long/path/to/a/file/that/should/take/enough/space/in/the/vpk/{i}.txt", Encoding.UTF8.GetBytes($"This is file {i} that is being written in ValvePak tests."));
			}

			packageNew.Write(output);

			output.Position = 0;

			// Verify
			using var packageWritten = new Package();
			packageWritten.SetFileName("test.vpk");
			packageWritten.Read(output);
			packageWritten.VerifyHashes();

			Assert.That(packageWritten.Entries!["txt"], Has.Count.EqualTo(1000));
		}

		[Test]
		public void AddAndRemoveFiles()
		{
			using var package = new Package();
			package.AddFile("test1.txt", []);
			package.AddFile("test2.txt", []);
			package.AddFile("test3.txt", []);
			package.AddFile("test4.txt", []);

			Assert.That(package.Entries!.ContainsKey("txt"), Is.True);
			Assert.That(package.Entries["txt"], Has.Count.EqualTo(4));
			Assert.That(package.RemoveFile(package.FindEntry("test2.txt")!), Is.True);
			Assert.That(package.FindEntry("test2.txt"), Is.Null);
			Assert.That(package.FindEntry("test1.txt"), Is.Not.Null);
			Assert.That(package.RemoveFile(new PackageEntry
			{
				FileName = "test5",
				TypeName = "txt",
				DirectoryName = " ",
			}), Is.False);
			Assert.That(package.Entries["txt"], Has.Count.EqualTo(3));
			Assert.That(package.RemoveFile(package.FindEntry("test4.txt")!), Is.True);
			Assert.That(package.RemoveFile(package.FindEntry("test3.txt")!), Is.True);
			Assert.That(package.RemoveFile(package.FindEntry("test1.txt")!), Is.True);
			Assert.That(package.Entries, Is.Empty);
		}

		[Test]
		public void SetsSpaces()
		{
			using var package = new Package();
			var file = package.AddFile("", []);
			Assert.Multiple(() =>
			{
				Assert.That(file.TypeName, Is.EqualTo(" "));
				Assert.That(file.DirectoryName, Is.EqualTo(" "));
				Assert.That(file.FileName, Is.EqualTo(""));
				Assert.That(package.Entries!.ContainsKey(" "), Is.True);
				Assert.That(package.Entries[" "][0], Is.EqualTo(file));
			});

			var file2 = package.AddFile("hello", []);
			Assert.Multiple(() =>
			{
				Assert.That(file2.TypeName, Is.EqualTo(" "));
				Assert.That(file2.DirectoryName, Is.EqualTo(" "));
				Assert.That(file2.FileName, Is.EqualTo("hello"));
			});

			var file3 = package.AddFile("hello.txt", []);
			Assert.Multiple(() =>
			{
				Assert.That(file3.TypeName, Is.EqualTo("txt"));
				Assert.That(file3.DirectoryName, Is.EqualTo(" "));
				Assert.That(file3.FileName, Is.EqualTo("hello"));
			});

			var file4 = package.AddFile("folder/hello", []);
			Assert.Multiple(() =>
			{
				Assert.That(file4.TypeName, Is.EqualTo(" "));
				Assert.That(file4.DirectoryName, Is.EqualTo("folder"));
				Assert.That(file4.FileName, Is.EqualTo("hello"));
			});
		}

		[Test]
		public void NormalizesSlashes()
		{
			using var package = new Package();
			var file = package.AddFile("a/b\\c\\d.txt", []);
			Assert.Multiple(() =>
			{
				Assert.That(file.TypeName, Is.EqualTo("txt"));
				Assert.That(file.DirectoryName, Is.EqualTo("a/b/c"));
				Assert.That(file.FileName, Is.EqualTo("d"));
			});
		}

		[Test]
		public void WriteMultiChunkPackage()
		{
			var tempDir = Path.Combine(Path.GetTempPath(), $"vpk_test_{System.Guid.NewGuid()}");
			Directory.CreateDirectory(tempDir);

			try
			{
				var basePath = Path.Combine(tempDir, "test_multipart");

				// Create a package with files that exceed a small chunk size
				using (var package = new Package())
				{
					// Create files totaling ~15 MB with 10 MB chunk size
					// This should create 2 chunk files
					var largeData1 = new byte[6 * 1024 * 1024]; // 6 MB
					var largeData2 = new byte[5 * 1024 * 1024]; // 5 MB
					var largeData3 = new byte[4 * 1024 * 1024]; // 4 MB

					// Fill with recognizable patterns
					for (var i = 0; i < largeData1.Length; i++)
					{
						largeData1[i] = (byte)(i % 256);
					}
					for (var i = 0; i < largeData2.Length; i++)
					{
						largeData2[i] = (byte)((i + 1) % 256);
					}
					for (var i = 0; i < largeData3.Length; i++)
					{
						largeData3[i] = (byte)((i + 2) % 256);
					}

					package.AddFile("large_file_1.dat", largeData1);
					package.AddFile("large_file_2.dat", largeData2);
					package.AddFile("large_file_3.dat", largeData3);
					package.AddFile("small_file.txt", Encoding.UTF8.GetBytes("Small file content"));

					// Write with 10 MB chunk size
					package.Write(basePath, 10 * 1024 * 1024);
				}

				// Verify the multi-chunk VPK was created
				Assert.That(File.Exists($"{basePath}_dir.vpk"), Is.True, "Directory file should exist");
				Assert.That(File.Exists($"{basePath}_000.vpk"), Is.True, "Chunk 0 should exist");
				Assert.That(File.Exists($"{basePath}_001.vpk"), Is.True, "Chunk 1 should exist");
				Assert.That(File.Exists($"{basePath}_002.vpk"), Is.False, "Chunk 2 should not exist");

				// Read and verify the package
				using var packageRead = new Package();
				packageRead.Read($"{basePath}_dir.vpk");
				packageRead.VerifyHashes();

				Assert.That(packageRead.IsDirVPK, Is.True);
				Assert.That(packageRead.Entries, Has.Count.EqualTo(2)); // "dat" and "txt" extensions

				// Verify large files
				var entry1 = packageRead.FindEntry("large_file_1.dat");
				Assert.That(entry1, Is.Not.Null);
				Assert.That(entry1.ArchiveIndex, Is.LessThan((ushort)0x7FFF));
				packageRead.ReadEntry(entry1, out var readData1);
				Assert.That(readData1.Length, Is.EqualTo(6 * 1024 * 1024));

				// Verify data integrity
				for (var i = 0; i < readData1.Length; i++)
				{
					if (readData1[i] != (byte)(i % 256))
					{
						Assert.Fail($"Data mismatch at byte {i}");
					}
				}

				var entry2 = packageRead.FindEntry("large_file_2.dat");
				Assert.That(entry2, Is.Not.Null);
				packageRead.ReadEntry(entry2, out var readData2);
				Assert.That(readData2.Length, Is.EqualTo(5 * 1024 * 1024));

				var entry3 = packageRead.FindEntry("large_file_3.dat");
				Assert.That(entry3, Is.Not.Null);
				packageRead.ReadEntry(entry3, out var readData3);
				Assert.That(readData3.Length, Is.EqualTo(4 * 1024 * 1024));

				// Verify small file
				var entrySmall = packageRead.FindEntry("small_file.txt");
				Assert.That(entrySmall, Is.Not.Null);
				packageRead.ReadEntry(entrySmall, out var readSmallData);
				Assert.That(Encoding.UTF8.GetString(readSmallData), Is.EqualTo("Small file content"));

				// Verify archive MD5 entries exist for chunks
				Assert.That(packageRead.ArchiveMD5Entries, Is.Not.Empty, "Archive MD5 entries should exist for multi-chunk VPK");
			}
			finally
			{
				// Clean up temp directory
				if (Directory.Exists(tempDir))
				{
					Directory.Delete(tempDir, true);
				}
			}
		}

		[Test]
		public void WriteSingleFileVsMultiChunk()
		{
			var tempDir = Path.Combine(Path.GetTempPath(), $"vpk_test_{System.Guid.NewGuid()}");
			Directory.CreateDirectory(tempDir);

			try
			{
				var testData = Encoding.UTF8.GetBytes("Test content for comparison");

				// Write as single file using stream
				var singleFilePath = Path.Combine(tempDir, "single.vpk");
				using (var package = new Package())
				{
					package.AddFile("test.txt", testData);
					package.Write(singleFilePath);
				}

				// Write as multi-chunk with large chunk size
				var multiChunkPath = Path.Combine(tempDir, "multi");
				using (var package = new Package())
				{
					package.AddFile("test.txt", testData);
					package.Write(multiChunkPath, 100 * 1024 * 1024); // 100 MB chunk - larger than our data
				}

				// Both should be valid and readable
				using var singlePackage = new Package();
				singlePackage.Read(singleFilePath);
				singlePackage.VerifyHashes();

				using var multiPackage = new Package();
				multiPackage.Read($"{multiChunkPath}_dir.vpk");
				multiPackage.VerifyHashes();

				// Both should have the same content
				var singleEntry = singlePackage.FindEntry("test.txt");
				var multiEntry = multiPackage.FindEntry("test.txt");

				Assert.That(singleEntry, Is.Not.Null);
				Assert.That(multiEntry, Is.Not.Null);

				singlePackage.ReadEntry(singleEntry, out var singleData);
				multiPackage.ReadEntry(multiEntry, out var multiData);

				Assert.That(singleData, Is.EqualTo(multiData));
				Assert.That(singleData, Is.EqualTo(testData));
			}
			finally
			{
				if (Directory.Exists(tempDir))
				{
					Directory.Delete(tempDir, true);
				}
			}
		}

		[Test]
		public void WriteMultiChunkVerifyChunkHashes()
		{
			var tempDir = Path.Combine(Path.GetTempPath(), $"vpk_test_{System.Guid.NewGuid()}");
			Directory.CreateDirectory(tempDir);

			try
			{
				var basePath = Path.Combine(tempDir, "test_hashes");

				// Create files that will be split across chunks
				using (var package = new Package())
				{
					// Create 3 MB files - with 2 MB chunk size we'll get multiple chunks
					var data1 = new byte[1500 * 1024]; // 1.5 MB
					var data2 = new byte[1500 * 1024]; // 1.5 MB
					var data3 = new byte[1500 * 1024]; // 1.5 MB

					for (var i = 0; i < data1.Length; i++) data1[i] = (byte)0xAA;
					for (var i = 0; i < data2.Length; i++) data2[i] = (byte)0xBB;
					for (var i = 0; i < data3.Length; i++) data3[i] = (byte)0xCC;

					package.AddFile("file1.bin", data1);
					package.AddFile("file2.bin", data2);
					package.AddFile("file3.bin", data3);

					package.Write(basePath, 2 * 1024 * 1024); // 2 MB chunks
				}

				// Read and verify
				using var packageRead = new Package();
				packageRead.Read($"{basePath}_dir.vpk");

				// Verify chunk hashes were calculated
				Assert.That(packageRead.ArchiveMD5Entries, Is.Not.Empty, "Should have archive MD5 entries");
				Assert.That(packageRead.ArchiveMD5Entries.Count, Is.GreaterThanOrEqualTo(2), "Should have entries for multiple 1MB fractions");

				// Verify all archive indices are valid
				foreach (var entry in packageRead.ArchiveMD5Entries)
				{
					Assert.That(entry.ArchiveIndex, Is.LessThan((ushort)0x7FFF), "Archive index should be valid chunk index");
					Assert.That(entry.Length, Is.GreaterThan(0u), "Hash entry length should be positive");
					Assert.That(entry.Checksum, Is.Not.Null, "Checksum should not be null");
					Assert.That(entry.Checksum.Length, Is.EqualTo(16), "MD5 checksum should be 16 bytes");
				}

				// Verify hashes
				packageRead.VerifyHashes();
				packageRead.VerifyChunkHashes();
			}
			finally
			{
				if (Directory.Exists(tempDir))
				{
					Directory.Delete(tempDir, true);
				}
			}
		}

		[Test]
		public void WriteMultiChunkWithManySmallFiles()
		{
			var tempDir = Path.Combine(Path.GetTempPath(), $"vpk_test_{System.Guid.NewGuid()}");
			Directory.CreateDirectory(tempDir);

			try
			{
				var basePath = Path.Combine(tempDir, "test_many_files");

				using (var package = new Package())
				{
					// Add 100 files of 50 KB each = 5 MB total
					for (var i = 0; i < 100; i++)
					{
						var data = new byte[50 * 1024];
						for (var j = 0; j < data.Length; j++)
						{
							data[j] = (byte)(i + j);
						}
						package.AddFile($"folder{i % 5}/file_{i}.dat", data);
					}

					// Write with 2 MB chunk size - should create 3 chunks
					package.Write(basePath, 2 * 1024 * 1024);
				}

				// Verify chunks exist
				Assert.That(File.Exists($"{basePath}_dir.vpk"), Is.True);
				Assert.That(File.Exists($"{basePath}_000.vpk"), Is.True);
				Assert.That(File.Exists($"{basePath}_001.vpk"), Is.True);
				Assert.That(File.Exists($"{basePath}_002.vpk"), Is.True);

				// Read and verify all files
				using var packageRead = new Package();
				packageRead.Read($"{basePath}_dir.vpk");
				packageRead.VerifyHashes();

				Assert.That(packageRead.Entries!["dat"], Has.Count.EqualTo(100));

				// Spot check a few files
				var entry0 = packageRead.FindEntry("folder0/file_0.dat");
				Assert.That(entry0, Is.Not.Null);
				packageRead.ReadEntry(entry0, out var data0);
				Assert.That(data0.Length, Is.EqualTo(50 * 1024));

				var entry99 = packageRead.FindEntry("folder4/file_99.dat");
				Assert.That(entry99, Is.Not.Null);
				packageRead.ReadEntry(entry99, out var data99);
				Assert.That(data99.Length, Is.EqualTo(50 * 1024));
			}
			finally
			{
				if (Directory.Exists(tempDir))
				{
					Directory.Delete(tempDir, true);
				}
			}
		}

		[Test]
		public void WriteMultiChunkWithOversizedFile()
		{
			var tempDir = Path.Combine(Path.GetTempPath(), $"vpk_test_{System.Guid.NewGuid()}");
			Directory.CreateDirectory(tempDir);

			try
			{
				var basePath = Path.Combine(tempDir, "test_oversized");

				using (var package = new Package())
				{
					var largeData = new byte[15 * 1024 * 1024]; // 15 MB
					package.AddFile("large.dat", largeData);

					package.Write(basePath, 10 * 1024 * 1024); // 10 MB chunk size
				}

				Assert.That(File.Exists($"{basePath}_dir.vpk"), Is.True);
				Assert.That(File.Exists($"{basePath}_000.vpk"), Is.True);

				using var packageRead = new Package();
				packageRead.Read($"{basePath}_dir.vpk");
				packageRead.VerifyHashes();

				var entry = packageRead.FindEntry("large.dat");
				Assert.That(entry, Is.Not.Null);
				Assert.That(entry!.TotalLength, Is.EqualTo(15 * 1024 * 1024));
			}
			finally
			{
				if (Directory.Exists(tempDir))
				{
					Directory.Delete(tempDir, true);
				}
			}
		}

		[Test]
		public void WriteMultiChunkWithChunkBoundaryEdgeCases()
		{
			var tempDir = Path.Combine(Path.GetTempPath(), $"vpk_test_{System.Guid.NewGuid()}");
			Directory.CreateDirectory(tempDir);

			try
			{
				var basePath = Path.Combine(tempDir, "test_boundary");
				var chunkSize = 1 * 1024 * 1024; // 1 MB

				using (var package = new Package())
				{
					var file1 = new byte[900 * 1024]; // 900 KB
					var file2 = new byte[200 * 1024]; // 200 KB - will fit in chunk 0, exceeding 1 MB (Valve behavior)
					var file3 = new byte[500 * 1024]; // 500 KB - should go to chunk 1
					var file4 = new byte[600 * 1024]; // 600 KB - will fit in chunk 1, exceeding 1 MB

					for (var i = 0; i < file1.Length; i++) file1[i] = 1;
					for (var i = 0; i < file2.Length; i++) file2[i] = 2;
					for (var i = 0; i < file3.Length; i++) file3[i] = 3;
					for (var i = 0; i < file4.Length; i++) file4[i] = 4;

					package.AddFile("file1.dat", file1);
					package.AddFile("file2.dat", file2);
					package.AddFile("file3.dat", file3);
					package.AddFile("file4.dat", file4);

					package.Write(basePath, chunkSize);
				}

				Assert.That(File.Exists($"{basePath}_dir.vpk"), Is.True);
				Assert.That(File.Exists($"{basePath}_000.vpk"), Is.True);
				Assert.That(File.Exists($"{basePath}_001.vpk"), Is.True);
				Assert.That(File.Exists($"{basePath}_002.vpk"), Is.False);

				var chunk0Size = new FileInfo($"{basePath}_000.vpk").Length;
				var chunk1Size = new FileInfo($"{basePath}_001.vpk").Length;

				Assert.That(chunk0Size, Is.EqualTo(900 * 1024 + 200 * 1024));
				Assert.That(chunk1Size, Is.EqualTo(500 * 1024 + 600 * 1024));

				using var packageRead = new Package();
				packageRead.Read($"{basePath}_dir.vpk");
				packageRead.VerifyHashes();

				var entry1 = packageRead.FindEntry("file1.dat");
				var entry2 = packageRead.FindEntry("file2.dat");
				var entry3 = packageRead.FindEntry("file3.dat");
				var entry4 = packageRead.FindEntry("file4.dat");

				Assert.That(entry1!.ArchiveIndex, Is.EqualTo(0));
				Assert.That(entry2!.ArchiveIndex, Is.EqualTo(0));
				Assert.That(entry3!.ArchiveIndex, Is.EqualTo(1));
				Assert.That(entry4!.ArchiveIndex, Is.EqualTo(1));

				packageRead.ReadEntry(entry1, out var data1);
				packageRead.ReadEntry(entry2, out var data2);
				Assert.That(data1[0], Is.EqualTo(1));
				Assert.That(data2[0], Is.EqualTo(2));
			}
			finally
			{
				if (Directory.Exists(tempDir))
				{
					Directory.Delete(tempDir, true);
				}
			}
		}

		[Test]
		public void WriteMultiChunkWhenBelowChunkSize()
		{
			var tempDir = Path.Combine(Path.GetTempPath(), $"vpk_test_{System.Guid.NewGuid()}");
			Directory.CreateDirectory(tempDir);

			try
			{
				var basePath = Path.Combine(tempDir, "test_small");

				using (var package = new Package())
				{
					var data1 = new byte[500 * 1024]; // 500 KB
					var data2 = new byte[400 * 1024]; // 400 KB
					package.AddFile("file1.dat", data1);
					package.AddFile("file2.dat", data2);

					package.Write(basePath, 2 * 1024 * 1024); // 2 MB chunk - files total < 2 MB
				}

				Assert.That(File.Exists($"{basePath}_dir.vpk"), Is.True);
				Assert.That(File.Exists($"{basePath}_000.vpk"), Is.True);

				using var packageRead = new Package();
				packageRead.Read($"{basePath}_dir.vpk");
				packageRead.VerifyHashes();

				var entry1 = packageRead.FindEntry("file1.dat");
				var entry2 = packageRead.FindEntry("file2.dat");

				Assert.That(entry1!.ArchiveIndex, Is.EqualTo(0));
				Assert.That(entry2!.ArchiveIndex, Is.EqualTo(0));
			}
			finally
			{
				if (Directory.Exists(tempDir))
				{
					Directory.Delete(tempDir, true);
				}
			}
		}

		[Test]
		public void WriteHandlesFileNamingVariations()
		{
			var tempDir = Path.Combine(Path.GetTempPath(), $"vpk_test_{System.Guid.NewGuid()}");
			Directory.CreateDirectory(tempDir);

			try
			{
				using (var package = new Package())
				{
					var data1 = new byte[2 * 1024 * 1024]; // 2 MB
					var data2 = new byte[500 * 1024]; // 500 KB
					package.AddFile("file1.dat", data1);
					package.AddFile("file2.dat", data2);

					package.Write(Path.Combine(tempDir, "test.vpk"), 2 * 1024 * 1024);
					Assert.That(File.Exists(Path.Combine(tempDir, "test_dir.vpk")), Is.True);

					package.Write(Path.Combine(tempDir, "test2_dir.vpk"), 2 * 1024 * 1024);
					Assert.That(File.Exists(Path.Combine(tempDir, "test2_dir.vpk")), Is.True);

					package.Write(Path.Combine(tempDir, "test3_dir"), 2 * 1024 * 1024);
					Assert.That(File.Exists(Path.Combine(tempDir, "test3_dir.vpk")), Is.True);
				}
			}
			finally
			{
				if (Directory.Exists(tempDir))
				{
					Directory.Delete(tempDir, true);
				}
			}
		}

		[Test]
		public void WriteSingleFileFromMultiChunkPackage()
		{
			var tempDir = Path.Combine(Path.GetTempPath(), $"vpk_test_{System.Guid.NewGuid()}");
			Directory.CreateDirectory(tempDir);

			try
			{
				var basePath = Path.Combine(tempDir, "test");

				using (var package = new Package())
				{
					var data1 = new byte[800 * 1024]; // 800 KB
					var data2 = new byte[600 * 1024]; // 600 KB
					package.AddFile("file1.dat", data1);
					package.AddFile("file2.dat", data2);

					package.Write(basePath, 1 * 1024 * 1024);

					Assert.That(File.Exists($"{basePath}_dir.vpk"), Is.True);
					Assert.That(File.Exists($"{basePath}_000.vpk"), Is.True);
				}

				using (var package = new Package())
				{
					package.Read($"{basePath}_dir.vpk");

					Assert.That(package.IsDirVPK, Is.True);

					var entry1 = package.FindEntry("file1.dat");
					Assert.That(entry1!.ArchiveIndex, Is.EqualTo(0));

					var singleFilePath = Path.Combine(tempDir, "test_single.vpk");

					package.Write(singleFilePath);

					Assert.That(File.Exists(singleFilePath), Is.True);
				}
			}
			finally
			{
				if (Directory.Exists(tempDir))
				{
					Directory.Delete(tempDir, true);
				}
			}
		}

		[Test]
		public void WriteThrowsOnInvalidChunkSize()
		{
			using var package = new Package();
			var data = new byte[1024];
			package.AddFile("file.dat", data);

			var tempPath = Path.Combine(Path.GetTempPath(), $"test_{System.Guid.NewGuid()}");

			Assert.Throws<ArgumentOutOfRangeException>(() =>
			{
				package.Write(tempPath, 0);
			});

			Assert.Throws<ArgumentOutOfRangeException>(() =>
			{
				package.Write(tempPath, -1);
			});
		}

		[Test]
		public void WriteThrowsOnEmptyPackage()
		{
			using var package = new Package();
			var tempPath = Path.Combine(Path.GetTempPath(), $"test_{System.Guid.NewGuid()}");

			Assert.Throws<InvalidOperationException>(() =>
			{
				package.Write(tempPath, 1024 * 1024);
			});
		}

		[Test]
		public void WriteMultiChunkVerifiesArchiveMD5Entries()
		{
			var tempDir = Path.Combine(Path.GetTempPath(), $"vpk_test_{System.Guid.NewGuid()}");
			Directory.CreateDirectory(tempDir);

			try
			{
				var basePath = Path.Combine(tempDir, "test_hashes");

				using (var package = new Package())
				{
					var data1 = new byte[900 * 1024]; // 900 KB
					var data2 = new byte[800 * 1024]; // 800 KB
					for (var i = 0; i < data1.Length; i++) data1[i] = (byte)(i % 256);
					for (var i = 0; i < data2.Length; i++) data2[i] = (byte)((i + 1) % 256);

					package.AddFile("file1.dat", data1);
					package.AddFile("file2.dat", data2);

					package.Write(basePath, 1 * 1024 * 1024); // 1 MB chunks
				}

				using (var package = new Package())
				{
					package.Read($"{basePath}_dir.vpk");

					Assert.That(package.ArchiveMD5Entries, Is.Not.Empty);
					Assert.That(package.ArchiveMD5Entries.Count, Is.GreaterThan(0));

					foreach (var entry in package.ArchiveMD5Entries)
					{
						Assert.That(entry.Checksum, Is.Not.Null);
						Assert.That(entry.Checksum.Length, Is.EqualTo(16));
						Assert.That(entry.Length, Is.EqualTo(1024 * 1024).Or.LessThan(1024 * 1024));
					}

					package.VerifyHashes();
				}
			}
			finally
			{
				if (Directory.Exists(tempDir))
				{
					Directory.Delete(tempDir, true);
				}
			}
		}

		[Test]
		public void WriteMultiChunkExactlyAtChunkSize()
		{
			var tempDir = Path.Combine(Path.GetTempPath(), $"vpk_test_{System.Guid.NewGuid()}");
			Directory.CreateDirectory(tempDir);

			try
			{
				var basePath = Path.Combine(tempDir, "test_exact");
				var chunkSize = 1 * 1024 * 1024; // 1 MB

				using (var package = new Package())
				{
					var data = new byte[chunkSize]; // Exactly 1 MB
					for (var i = 0; i < data.Length; i++) data[i] = (byte)(i % 256);

					package.AddFile("exact.dat", data);

					package.Write(basePath, chunkSize);
				}

				Assert.That(File.Exists($"{basePath}_dir.vpk"), Is.True);
				Assert.That(File.Exists($"{basePath}_000.vpk"), Is.True);

				using (var packageRead = new Package())
				{
					packageRead.Read($"{basePath}_dir.vpk");
					packageRead.VerifyHashes();

					var entry = packageRead.FindEntry("exact.dat");
					Assert.That(entry!.ArchiveIndex, Is.EqualTo(0));
				}
			}
			finally
			{
				if (Directory.Exists(tempDir))
				{
					Directory.Delete(tempDir, true);
				}
			}
		}

		[Test]
		public void WriteMultiChunkPreservesFileOrder()
		{
			var tempDir = Path.Combine(Path.GetTempPath(), $"vpk_test_{System.Guid.NewGuid()}");
			Directory.CreateDirectory(tempDir);

			try
			{
				var basePath = Path.Combine(tempDir, "test_order");

				using (var package = new Package())
				{
					var data = new byte[400 * 1024]; // 400 KB each

					package.AddFile("zebra.dat", data);
					package.AddFile("apple.dat", data);
					package.AddFile("monkey.dat", data);
					package.AddFile("banana.dat", data);

					package.Write(basePath, 700 * 1024); // 700 KB chunks
				}

				using (var packageRead = new Package())
				{
					packageRead.Read($"{basePath}_dir.vpk");

					var zebra = packageRead.FindEntry("zebra.dat");
					var apple = packageRead.FindEntry("apple.dat");
					var monkey = packageRead.FindEntry("monkey.dat");
					var banana = packageRead.FindEntry("banana.dat");

					Assert.That(zebra!.ArchiveIndex, Is.EqualTo(0));
					Assert.That(zebra.Offset, Is.EqualTo(0));

					Assert.That(apple!.ArchiveIndex, Is.EqualTo(0));
					Assert.That(apple.Offset, Is.EqualTo(400 * 1024));

					Assert.That(monkey!.ArchiveIndex, Is.EqualTo(1));
					Assert.That(monkey.Offset, Is.EqualTo(0));

					Assert.That(banana!.ArchiveIndex, Is.EqualTo(1));
					Assert.That(banana.Offset, Is.EqualTo(400 * 1024));
				}
			}
			finally
			{
				if (Directory.Exists(tempDir))
				{
					Directory.Delete(tempDir, true);
				}
			}
		}

		[Test]
		public void WriteMultiChunkWithVerySmallChunkSize()
		{
			var tempDir = Path.Combine(Path.GetTempPath(), $"vpk_test_{System.Guid.NewGuid()}");
			Directory.CreateDirectory(tempDir);

			try
			{
				var basePath = Path.Combine(tempDir, "test_small_chunks");

				using (var package = new Package())
				{
					var data = new byte[5000]; // 5 KB
					for (var i = 0; i < 10; i++)
					{
						package.AddFile($"file{i}.dat", data);
					}

					package.Write(basePath, 12000); // 12 KB chunks - will create many chunks
				}

				Assert.That(File.Exists($"{basePath}_dir.vpk"), Is.True);
				Assert.That(File.Exists($"{basePath}_000.vpk"), Is.True);
				Assert.That(File.Exists($"{basePath}_001.vpk"), Is.True);
				Assert.That(File.Exists($"{basePath}_002.vpk"), Is.True);
				Assert.That(File.Exists($"{basePath}_003.vpk"), Is.True);

				using (var packageRead = new Package())
				{
					packageRead.Read($"{basePath}_dir.vpk");
					packageRead.VerifyHashes();

					for (var i = 0; i < 10; i++)
					{
						var entry = packageRead.FindEntry($"file{i}.dat");
						Assert.That(entry, Is.Not.Null);
						Assert.That(entry!.TotalLength, Is.EqualTo(5000));
					}
				}
			}
			finally
			{
				if (Directory.Exists(tempDir))
				{
					Directory.Delete(tempDir, true);
				}
			}
		}
	}
}
