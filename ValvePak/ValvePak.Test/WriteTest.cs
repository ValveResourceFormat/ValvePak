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
			using (Assert.EnterMultipleScope())
			{
				Assert.That(newEntry.CRC32, Is.EqualTo(0x9C800116));
				Assert.That(newEntry.ArchiveIndex, Is.EqualTo(0x7FFF));
				Assert.That(newEntry.DirectoryName, Is.EqualTo("path/to"));
				Assert.That(newEntry.TypeName, Is.EqualTo("jpg"));
				Assert.That(newEntry.FileName, Is.EqualTo("cool kitty"));
			}

			packageWritten.ReadEntry(newEntry, out var newFileData);
			using (Assert.EnterMultipleScope())
			{
				Assert.That(newFileData, Is.EqualTo(fileData));
				Assert.That(packageWritten.FindEntry("valvepak")!.CRC32, Is.EqualTo(0xF14F273C));
			}
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

			using (Assert.EnterMultipleScope())
			{
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
		}

		[Test]
		public void SetsSpaces()
		{
			using var package = new Package();
			var file = package.AddFile("", []);
			using (Assert.EnterMultipleScope())
			{
				Assert.That(file.TypeName, Is.EqualTo(" "));
				Assert.That(file.DirectoryName, Is.EqualTo(" "));
				Assert.That(file.FileName, Is.EqualTo(""));
				Assert.That(package.Entries!.ContainsKey(" "), Is.True);
				Assert.That(package.Entries[" "][0], Is.EqualTo(file));
			}

			var file2 = package.AddFile("hello", []);
			using (Assert.EnterMultipleScope())
			{
				Assert.That(file2.TypeName, Is.EqualTo(" "));
				Assert.That(file2.DirectoryName, Is.EqualTo(" "));
				Assert.That(file2.FileName, Is.EqualTo("hello"));
			}

			var file3 = package.AddFile("hello.txt", []);
			using (Assert.EnterMultipleScope())
			{
				Assert.That(file3.TypeName, Is.EqualTo("txt"));
				Assert.That(file3.DirectoryName, Is.EqualTo(" "));
				Assert.That(file3.FileName, Is.EqualTo("hello"));
			}

			var file4 = package.AddFile("folder/hello", []);
			using (Assert.EnterMultipleScope())
			{
				Assert.That(file4.TypeName, Is.EqualTo(" "));
				Assert.That(file4.DirectoryName, Is.EqualTo("folder"));
				Assert.That(file4.FileName, Is.EqualTo("hello"));
			}
		}

		[Test]
		public void NormalizesSlashes()
		{
			using var package = new Package();
			var file = package.AddFile("a/b\\c\\d.txt", []);
			using (Assert.EnterMultipleScope())
			{
				Assert.That(file.TypeName, Is.EqualTo("txt"));
				Assert.That(file.DirectoryName, Is.EqualTo("a/b/c"));
				Assert.That(file.FileName, Is.EqualTo("d"));
			}
		}

		[Test]
		public void WriteThrowsWhenIsDirVPK()
		{
			var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "steamdb_test_dir.vpk");

			using var package = new Package();
			package.Read(path);

			using var output = new MemoryStream();
			var ex = Assert.Throws<InvalidOperationException>(() => package.Write(output));
			Assert.That(ex.Message, Is.EqualTo("This package was opened from a _dir.vpk, writing back is currently unsupported."));
		}

		[Test]
		public void WriteThrowsOnNonSeekableStream()
		{
			using var package = new Package();
			package.AddFile("test.txt", Encoding.UTF8.GetBytes("hello"));

			using var nonSeekable = new NonSeekableStream();
			var ex = Assert.Throws<InvalidOperationException>(() => package.Write(nonSeekable));
			Assert.That(ex.Message, Is.EqualTo("Stream must be seekable and readable."));
		}

		[Test]
		public void AddFileThrowsOnNullFilePath()
		{
			using var package = new Package();
			Assert.Throws<ArgumentNullException>(() => package.AddFile(null!, []));
		}

		[Test]
		public void RemoveFileThrowsOnNullEntry()
		{
			using var package = new Package();
			Assert.Throws<ArgumentNullException>(() => package.RemoveFile(null!));
		}

		[Test]
		public void RemoveFileReturnsFalseOnEmptyPackage()
		{
			using var package = new Package();
			var result = package.RemoveFile(new PackageEntry
			{
				FileName = "test",
				TypeName = "txt",
				DirectoryName = " ",
			});
			Assert.That(result, Is.False);
		}

		[Test]
		public void WriteAndVerifyRoundTrip()
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

			using (Assert.EnterMultipleScope())
			{
				Assert.That(readBack.Version, Is.EqualTo(2));
				Assert.That(readBack.HeaderSize, Is.GreaterThan(0u));
				Assert.That(readBack.TreeSize, Is.GreaterThan(0u));
				Assert.That(readBack.FileDataSectionSize, Is.GreaterThan(0u));
				Assert.That(readBack.OtherMD5SectionSize, Is.EqualTo(48u));
			}

			Assert.DoesNotThrow(() => readBack.VerifyHashes());
		}

		[Test]
		public void WriteToFile()
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
				Assert.That(entry, Is.Not.Null);

				readBack.ReadEntry(entry, out var data);
				Assert.That(Encoding.UTF8.GetString(data), Is.EqualTo("hello from file"));
			}
			finally
			{
				File.Delete(tempFile);
			}
		}

		[Test]
		public void RemoveFileReturnsFalseForWrongType()
		{
			using var package = new Package();
			package.AddFile("test.txt", []);

			var result = package.RemoveFile(new PackageEntry
			{
				FileName = "test",
				TypeName = "jpg",
				DirectoryName = " ",
			});

			Assert.That(result, Is.False);
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
