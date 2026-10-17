using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using TUnit.Assertions.Enums;

namespace ValvePak.Test
{
	internal sealed class WriteTest
	{
		[Test]
		public async Task CreateNewPackage()
		{
			var oldPath = Path.Combine(AppContext.BaseDirectory, "Files", "test_single.vpk");

			using var packageOld = new Package();
			packageOld.Read(oldPath);

			var fileEntry = packageOld.FindEntry("kitten.jpg");
			await Assert.That(fileEntry).IsNotNull();
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

			await Assert.That(newEntry).IsNotNull();
			using (Assert.Multiple())
			{
				await Assert.That(newEntry.CRC32).IsEqualTo(0x9C800116u);
				await Assert.That(newEntry.ArchiveIndex).IsEqualTo((ushort)0x7FFF);
				await Assert.That(newEntry.DirectoryName).IsEqualTo("path/to");
				await Assert.That(newEntry.TypeName).IsEqualTo("jpg");
				await Assert.That(newEntry.FileName).IsEqualTo("cool kitty");
			}

			packageWritten.ReadEntry(newEntry, out var newFileData);
			using (Assert.Multiple())
			{
				await Assert.That(newFileData).IsEquivalentTo(fileData, CollectionOrdering.Matching);
				await Assert.That(packageWritten.FindEntry("valvepak")!.CRC32).IsEqualTo(0xF14F273Cu);
			}
		}

		[Test]
		public async Task WriteManyFiles()
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

			await Assert.That(packageWritten.Entries!["txt"]).Count().IsEqualTo(1000);
		}

		[Test]
		public async Task AddAndRemoveFiles()
		{
			using var package = new Package();
			package.AddFile("test1.txt", []);
			package.AddFile("test2.txt", []);
			package.AddFile("test3.txt", []);
			package.AddFile("test4.txt", []);

			using (Assert.Multiple())
			{
				await Assert.That(package.Entries!.ContainsKey("txt")).IsTrue();
				await Assert.That(package.Entries["txt"]).Count().IsEqualTo(4);
				await Assert.That(package.RemoveFile(package.FindEntry("test2.txt")!)).IsTrue();
				await Assert.That(package.FindEntry("test2.txt")).IsNull();
				await Assert.That(package.FindEntry("test1.txt")).IsNotNull();
				await Assert.That(package.RemoveFile(new PackageEntry
				{
					FileName = "test5",
					TypeName = "txt",
					DirectoryName = " ",
				})).IsFalse();
				await Assert.That(package.Entries["txt"]).Count().IsEqualTo(3);
				await Assert.That(package.RemoveFile(package.FindEntry("test4.txt")!)).IsTrue();
				await Assert.That(package.RemoveFile(package.FindEntry("test3.txt")!)).IsTrue();
				await Assert.That(package.RemoveFile(package.FindEntry("test1.txt")!)).IsTrue();
				await Assert.That(package.Entries).IsEmpty();
			}
		}

		[Test]
		public async Task SetsSpaces()
		{
			using var package = new Package();
			var file = package.AddFile("", []);
			using (Assert.Multiple())
			{
				await Assert.That(file.TypeName).IsEqualTo(" ");
				await Assert.That(file.DirectoryName).IsEqualTo(" ");
				await Assert.That(file.FileName).IsEqualTo("");
				await Assert.That(package.Entries!.ContainsKey(" ")).IsTrue();
				await Assert.That(package.Entries[" "][0]).IsEqualTo(file);
			}

			var file2 = package.AddFile("hello", []);
			using (Assert.Multiple())
			{
				await Assert.That(file2.TypeName).IsEqualTo(" ");
				await Assert.That(file2.DirectoryName).IsEqualTo(" ");
				await Assert.That(file2.FileName).IsEqualTo("hello");
			}

			var file3 = package.AddFile("hello.txt", []);
			using (Assert.Multiple())
			{
				await Assert.That(file3.TypeName).IsEqualTo("txt");
				await Assert.That(file3.DirectoryName).IsEqualTo(" ");
				await Assert.That(file3.FileName).IsEqualTo("hello");
			}

			var file4 = package.AddFile("folder/hello", []);
			using (Assert.Multiple())
			{
				await Assert.That(file4.TypeName).IsEqualTo(" ");
				await Assert.That(file4.DirectoryName).IsEqualTo("folder");
				await Assert.That(file4.FileName).IsEqualTo("hello");
			}
		}

		[Test]
		public async Task NormalizesSlashes()
		{
			using var package = new Package();
			var file = package.AddFile("a/b\\c\\d.txt", []);
			using (Assert.Multiple())
			{
				await Assert.That(file.TypeName).IsEqualTo("txt");
				await Assert.That(file.DirectoryName).IsEqualTo("a/b/c");
				await Assert.That(file.FileName).IsEqualTo("d");
			}
		}

		[Test]
		public async Task WriteThrowsWhenIsDirVPK()
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "test_dir.vpk");

			using var package = new Package();
			package.Read(path);

			using var output = new MemoryStream();
			await Assert.That(() => package.Write(output)).ThrowsExactly<InvalidOperationException>()
				.WithMessage("This package was opened from a _dir.vpk, writing back is currently unsupported.", StringComparison.Ordinal);
		}

		[Test]
		public async Task WriteThrowsOnNonSeekableStream()
		{
			using var package = new Package();
			package.AddFile("test.txt", Encoding.UTF8.GetBytes("hello"));

			using var nonSeekable = new NonSeekableStream();
			await Assert.That(() => package.Write(nonSeekable)).ThrowsExactly<InvalidOperationException>()
				.WithMessage("Stream must be seekable and readable.", StringComparison.Ordinal);
		}

		[Test]
		public async Task AddFileThrowsOnNullFilePath()
		{
			using var package = new Package();
			await Assert.That(() => package.AddFile(null!, [])).ThrowsExactly<ArgumentNullException>();
		}

		[Test]
		public async Task AddFileThrowsOnNullData()
		{
			using var package = new Package();
			await Assert.That(() => package.AddFile("test.txt", null!)).ThrowsExactly<ArgumentNullException>();
		}

		[Test]
		public async Task RemoveFileThrowsOnNullEntry()
		{
			using var package = new Package();
			await Assert.That(() => package.RemoveFile(null!)).ThrowsExactly<ArgumentNullException>();
		}

		[Test]
		public async Task RemoveFileReturnsFalseOnEmptyPackage()
		{
			using var package = new Package();
			var result = package.RemoveFile(new PackageEntry
			{
				FileName = "test",
				TypeName = "txt",
				DirectoryName = " ",
			});
			await Assert.That(result).IsFalse();
		}

		[Test]
		public async Task WriteAndVerifyRoundTrip()
		{
			using var output = new MemoryStream();

			using (var package = new Package())
			{
				package.AddFile("hello.txt", Encoding.UTF8.GetBytes("world"));
				package.AddFile("folder/image.jpg", Encoding.UTF8.GetBytes("not really a jpg"));
				package.Write(output);
			}

			output.Position = 0;

			using var readBack = new Package();
			readBack.SetFileName("test.vpk");
			readBack.Read(output);

			using (Assert.Multiple())
			{
				await Assert.That(readBack.Version).IsEqualTo(2u);
				await Assert.That(readBack.HeaderSize).IsGreaterThan(0u);
				await Assert.That(readBack.TreeSize).IsGreaterThan(0u);
				await Assert.That(readBack.FileDataSectionSize).IsGreaterThan(0u);
				await Assert.That(readBack.OtherMD5SectionSize).IsEqualTo(48u);
			}

			await Assert.That(() => readBack.VerifyHashes()).ThrowsNothing();
		}

		[Test]
		public async Task WriteVersion1Package()
		{
			using var output = new MemoryStream();

			using (var package = new Package())
			{
				await Assert.That(package.Version).IsEqualTo(2u);

				package.Version = 1;
				package.AddFile("hello.txt", Encoding.UTF8.GetBytes("world"));
				package.AddFile("folder/image.jpg", Encoding.UTF8.GetBytes("not really a jpg"));
				package.Write(output);
			}

			output.Position = 0;

			using var readBack = new Package();
			readBack.SetFileName("test.vpk");
			readBack.Read(output);

			using (Assert.Multiple())
			{
				await Assert.That(readBack.Version).IsEqualTo(1u);
				await Assert.That(readBack.HeaderSize).IsEqualTo(12u);
				await Assert.That(readBack.TreeSize).IsGreaterThan(0u);
				await Assert.That(readBack.OtherMD5SectionSize).IsZero();
			}

			var entry = readBack.FindEntry("hello.txt");
			await Assert.That(entry).IsNotNull();

			readBack.ReadEntry(entry, out var data);
			await Assert.That(Encoding.UTF8.GetString(data)).IsEqualTo("world");

			await Assert.That(() => readBack.VerifyFileChecksums()).ThrowsNothing();
		}

		[Test]
		public async Task WritePreservesVersionOfReadPackage()
		{
			using var output = new MemoryStream();

			using (var package = new Package())
			{
				package.Version = 1;
				package.AddFile("hello.txt", Encoding.UTF8.GetBytes("world"));
				package.Write(output);
			}

			output.Position = 0;

			using var readBack = new Package();
			readBack.SetFileName("test.vpk");
			readBack.Read(output);

			using var output2 = new MemoryStream();
			readBack.Write(output2);

			output2.Position = 0;

			using var readBack2 = new Package();
			readBack2.SetFileName("test.vpk");
			readBack2.Read(output2);

			await Assert.That(readBack2.Version).IsEqualTo(1u);

			var entry = readBack2.FindEntry("hello.txt");
			await Assert.That(entry).IsNotNull();

			readBack2.ReadEntry(entry, out var data);
			await Assert.That(Encoding.UTF8.GetString(data)).IsEqualTo("world");
		}

		[Test]
		public async Task SetVersionThrowsOnUnsupportedVersion()
		{
			using var package = new Package();
			await Assert.That(() => package.Version = 0).ThrowsExactly<ArgumentOutOfRangeException>();
			await Assert.That(() => package.Version = 3).ThrowsExactly<ArgumentOutOfRangeException>();
		}

		[Test]
		public async Task WriteToFile()
		{
			var tempFile = Path.GetTempFileName();

			try
			{
				using (var package = new Package())
				{
					package.AddFile("test.txt", Encoding.UTF8.GetBytes("hello from file"));
					package.Write(tempFile);
				}

				using var readBack = new Package();
				readBack.Read(tempFile);
				readBack.VerifyHashes();

				var entry = readBack.FindEntry("test.txt");
				await Assert.That(entry).IsNotNull();

				readBack.ReadEntry(entry, out var data);
				await Assert.That(Encoding.UTF8.GetString(data)).IsEqualTo("hello from file");
			}
			finally
			{
				File.Delete(tempFile);
			}
		}

		[Test]
		public async Task RemoveFileReturnsFalseForWrongType()
		{
			using var package = new Package();
			package.AddFile("test.txt", []);

			var result = package.RemoveFile(new PackageEntry
			{
				FileName = "test",
				TypeName = "jpg",
				DirectoryName = " ",
			});

			await Assert.That(result).IsFalse();
		}

		[Test]
		[Arguments(0, 2u)]
		[Arguments(100, 2u)]
		[Arguments(100, 1u)]
		public async Task WriteIntoLongerStream(int position, uint version)
		{
			const int StreamLength = 10000;

			using var output = new MemoryStream();
			output.SetLength(StreamLength);
			Array.Fill(output.GetBuffer(), (byte)0xCD, 0, StreamLength);
			output.Position = position;

			using (var package = new Package())
			{
				package.Version = version;
				package.AddFile("hello.txt", Encoding.UTF8.GetBytes("world"));
				package.AddFile("folder/image.jpg", Encoding.UTF8.GetBytes("not really a jpg"));
				package.Write(output);
			}

			var firstChangedPrefixByte = output.GetBuffer().AsSpan(0, position).IndexOfAnyExcept((byte)0xCD);
			await Assert.That(firstChangedPrefixByte).IsEqualTo(-1);

			output.Position = position;

			using var readBack = new Package();
			readBack.SetFileName("test.vpk");
			readBack.Read(output);

			await Assert.That(readBack.Version).IsEqualTo(version);

			if (version >= 2)
			{
				await Assert.That(() => readBack.VerifyHashes()).ThrowsNothing();
			}

			await Assert.That(() => readBack.VerifyFileChecksums()).ThrowsNothing();

			var hello = readBack.FindEntry("hello.txt");
			var image = readBack.FindEntry("folder/image.jpg");

			await Assert.That(hello).IsNotNull();
			await Assert.That(image).IsNotNull();

			readBack.ReadEntry(hello, out var helloData);
			readBack.ReadEntry(image, out var imageData);

			using (Assert.Multiple())
			{
				await Assert.That(Encoding.UTF8.GetString(helloData)).IsEqualTo("world");
				await Assert.That(Encoding.UTF8.GetString(imageData)).IsEqualTo("not really a jpg");
			}
		}

		[Test]
		[Arguments(StringComparison.Ordinal)]
		[Arguments(StringComparison.OrdinalIgnoreCase)]
		public async Task AddedFilesCanBeFoundAfterOptimizingForBinarySearch(StringComparison comparison)
		{
			var path = Path.Combine(AppContext.BaseDirectory, "Files", "platform_misc_dir.vpk");

			using var package = new Package();
			package.OptimizeEntriesForBinarySearch(comparison);
			package.Read(path);

			var existingEntries = new List<PackageEntry>();

			foreach (var entries in package.Entries!.Values)
			{
				existingEntries.AddRange(entries);
			}

			string[] addedPaths =
			[
				"addons/chess/zzz.vdf",
				"addons/chess/a.vdf",
				"addons/chess/chess2.vdf",
				"new/folder/file.vdf",
				"b.vdf",
				"addons/chess/c.vdf",
				"added.txt",
			];

			var addedEntries = new List<PackageEntry>();

			foreach (var addedPath in addedPaths)
			{
				addedEntries.Add(package.AddFile(addedPath, Encoding.UTF8.GetBytes(addedPath)));
			}

			foreach (var entry in existingEntries)
			{
				await Assert.That(package.FindEntry(entry.GetFullPath())).IsEqualTo(entry);
			}

			foreach (var entry in addedEntries)
			{
				await Assert.That(package.FindEntry(entry.GetFullPath())).IsEqualTo(entry);
			}

			var upperCaseEntry = package.FindEntry("ADDONS/CHESS/ZZZ.VDF");

			if (comparison == StringComparison.OrdinalIgnoreCase)
			{
				await Assert.That(upperCaseEntry).IsEqualTo(addedEntries[0]);
			}
			else
			{
				await Assert.That(upperCaseEntry).IsNull();
			}

			await Assert.That(package.FindEntry("addons/chess/missing.vdf")).IsNull();
		}

		private sealed class NonSeekableStream : Stream
		{
			public override bool CanRead => true;
			public override bool CanSeek => false;
			public override bool CanWrite => true;
			public override long Length => 0;
			public override long Position { get => 0; set { } }
			public override void Flush() { }
			public override int Read(byte[] buffer, int offset, int count) => 0;
			public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
			public override void SetLength(long value) { }
			public override void Write(byte[] buffer, int offset, int count) { }
		}
	}
}
