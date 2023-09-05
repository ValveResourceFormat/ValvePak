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

			Assert.IsNotNull(newEntry);
			Assert.AreEqual(0x9C800116, newEntry.CRC32);
			Assert.AreEqual(0x7FFF, newEntry.ArchiveIndex);
			Assert.AreEqual("path/to", newEntry.DirectoryName);
			Assert.AreEqual("jpg", newEntry.TypeName);
			Assert.AreEqual("cool kitty", newEntry.FileName);

			packageWritten.ReadEntry(newEntry, out var newFileData);
			Assert.AreEqual(fileData, newFileData);

			Assert.AreEqual(0xF14F273C, packageWritten.FindEntry("valvepak").CRC32);
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

			Assert.AreEqual(1000, packageWritten.Entries["txt"].Count);
		}

		[Test]
		public void AddAndRemoveFiles()
		{
			using var package = new Package();
			package.AddFile("test1.txt", Array.Empty<byte>());
			package.AddFile("test2.txt", Array.Empty<byte>());
			package.AddFile("test3.txt", Array.Empty<byte>());
			package.AddFile("test4.txt", Array.Empty<byte>());
			Assert.IsTrue(package.Entries.ContainsKey("txt"));
			Assert.AreEqual(4, package.Entries["txt"].Count);
			Assert.IsTrue(package.RemoveFile(package.FindEntry("test2.txt")));
			Assert.IsNull(package.FindEntry("test2.txt"));
			Assert.IsNotNull(package.FindEntry("test1.txt"));
			Assert.IsFalse(package.RemoveFile(new PackageEntry
			{
				FileName = "test5",
				TypeName = "txt",
			}));
			Assert.AreEqual(3, package.Entries["txt"].Count);
			Assert.IsTrue(package.RemoveFile(package.FindEntry("test4.txt")));
			Assert.IsTrue(package.RemoveFile(package.FindEntry("test3.txt")));
			Assert.IsTrue(package.RemoveFile(package.FindEntry("test1.txt")));
			Assert.IsEmpty(package.Entries);
		}

		[Test]
		public void SetsSpaces()
		{
			using var package = new Package();
			var file = package.AddFile("", Array.Empty<byte>());
			Assert.AreEqual(" ", file.TypeName);
			Assert.AreEqual(" ", file.DirectoryName);
			Assert.AreEqual("", file.FileName);
			Assert.IsTrue(package.Entries.ContainsKey(" "));
			Assert.AreEqual(file, package.Entries[" "][0]);

			var file2 = package.AddFile("hello", Array.Empty<byte>());
			Assert.AreEqual(" ", file2.TypeName);
			Assert.AreEqual(" ", file2.DirectoryName);
			Assert.AreEqual("hello", file2.FileName);

			var file3 = package.AddFile("hello.txt", Array.Empty<byte>());
			Assert.AreEqual("txt", file3.TypeName);
			Assert.AreEqual(" ", file3.DirectoryName);
			Assert.AreEqual("hello", file3.FileName);

			var file4 = package.AddFile("folder/hello", Array.Empty<byte>());
			Assert.AreEqual(" ", file4.TypeName);
			Assert.AreEqual("folder", file4.DirectoryName);
			Assert.AreEqual("hello", file4.FileName);
		}

		[Test]
		public void NormalizesSlashes()
		{
			using var package = new Package();
			var file = package.AddFile("a/b\\c\\d.txt", Array.Empty<byte>());
			Assert.AreEqual("txt", file.TypeName);
			Assert.AreEqual("a/b/c", file.DirectoryName);
			Assert.AreEqual("d", file.FileName);
		}
	}
}
