using System.IO;
using System.Runtime.Intrinsics.Arm;
using System.Text;
using NuGet.Frameworks;
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
	}
}
