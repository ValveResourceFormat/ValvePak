using System.IO;
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
			var old = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "steamdb_test_single.vpk");

			using var packageOld = new Package();
			packageOld.Read(old);

			var fileEntry = packageOld.FindEntry("kitten.jpg");
			packageOld.ReadEntry(fileEntry, out var fileData);

			var newName = "path/to/cool kitty.jpg";

			using var packageNew = new Package();
			packageNew.AddFile(newName, fileData);

			var newEntry = packageNew.FindEntry(newName);

			Assert.IsNotNull(newEntry);
			Assert.AreEqual(0x9C800116, newEntry.CRC32);
			Assert.AreEqual("path/to", newEntry.DirectoryName);
			Assert.AreEqual("jpg", newEntry.TypeName);
			Assert.AreEqual("cool kitty", newEntry.FileName);

			packageNew.ReadEntry(newEntry, out var newFileData);

			Assert.AreEqual(fileData, newFileData);
		}
	}
}
