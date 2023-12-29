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
				Assert.That(packageWritten.FindEntry("valvepak").CRC32, Is.EqualTo(0xF14F273C));
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

			Assert.That(packageWritten.Entries["txt"], Has.Count.EqualTo(1000));
		}

		[Test]
		public void AddAndRemoveFiles()
		{
			using var package = new Package();
			package.AddFile("test1.txt", Array.Empty<byte>());
			package.AddFile("test2.txt", Array.Empty<byte>());
			package.AddFile("test3.txt", Array.Empty<byte>());
			package.AddFile("test4.txt", Array.Empty<byte>());
#pragma warning disable NUnit2045 // Use Assert.Multiple
			Assert.That(package.Entries.ContainsKey("txt"), Is.True);
			Assert.That(package.Entries["txt"], Has.Count.EqualTo(4));
			Assert.That(package.RemoveFile(package.FindEntry("test2.txt")), Is.True);
			Assert.That(package.FindEntry("test2.txt"), Is.Null);
			Assert.That(package.FindEntry("test1.txt"), Is.Not.Null);
			Assert.That(package.RemoveFile(new PackageEntry
			{
				FileName = "test5",
				TypeName = "txt",
			}), Is.False);
			Assert.That(package.Entries["txt"], Has.Count.EqualTo(3));
			Assert.That(package.RemoveFile(package.FindEntry("test4.txt")), Is.True);
			Assert.That(package.RemoveFile(package.FindEntry("test3.txt")), Is.True);
			Assert.That(package.RemoveFile(package.FindEntry("test1.txt")), Is.True);
			Assert.That(package.Entries, Is.Empty);
#pragma warning restore NUnit2045
		}

		[Test]
		public void SetsSpaces()
		{
			using var package = new Package();
			var file = package.AddFile("", Array.Empty<byte>());
			Assert.Multiple(() =>
			{
				Assert.That(file.TypeName, Is.EqualTo(" "));
				Assert.That(file.DirectoryName, Is.EqualTo(" "));
				Assert.That(file.FileName, Is.EqualTo(""));
				Assert.That(package.Entries.ContainsKey(" "), Is.True);
				Assert.That(package.Entries[" "][0], Is.EqualTo(file));
			});

			var file2 = package.AddFile("hello", Array.Empty<byte>());
			Assert.Multiple(() =>
			{
				Assert.That(file2.TypeName, Is.EqualTo(" "));
				Assert.That(file2.DirectoryName, Is.EqualTo(" "));
				Assert.That(file2.FileName, Is.EqualTo("hello"));
			});

			var file3 = package.AddFile("hello.txt", Array.Empty<byte>());
			Assert.Multiple(() =>
			{
				Assert.That(file3.TypeName, Is.EqualTo("txt"));
				Assert.That(file3.DirectoryName, Is.EqualTo(" "));
				Assert.That(file3.FileName, Is.EqualTo("hello"));
			});

			var file4 = package.AddFile("folder/hello", Array.Empty<byte>());
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
			var file = package.AddFile("a/b\\c\\d.txt", Array.Empty<byte>());
			Assert.Multiple(() =>
			{
				Assert.That(file.TypeName, Is.EqualTo("txt"));
				Assert.That(file.DirectoryName, Is.EqualTo("a/b/c"));
				Assert.That(file.FileName, Is.EqualTo("d"));
			});
		}
	}
}
