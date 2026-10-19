using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ValvePak.Test
{
	internal sealed class EntryPathTest
	{
		[Test]
		[Arguments("A\\B//./c.TXT", "a/b/c.txt")]
		[Arguments("a/x/../b.txt", "a/b.txt")]
		[Arguments("/a/b.txt", "a/b.txt")]
		[Arguments("README", "readme")]
		[Arguments("", "")]
		[Arguments("../b.txt", "")]
		[Arguments("a/b/", "")]
		[Arguments(".gitignore", "")]
		public async Task NormalizesEntryPaths(string path, string expected)
		{
			await Assert.That(Package.NormalizeEntryPath(path)).IsEqualTo(expected);
		}

		[Test]
		public async Task RejectsNamesThatAreTooLong()
		{
			using (Assert.Multiple())
			{
				await Assert.That(Package.NormalizeEntryPath($"a.{new string('y', 129)}")).IsEmpty();
				await Assert.That(Package.NormalizeEntryPath($"{new string('é', 131)}.txt")).IsEmpty();
			}
		}

		[Test]
		[Arguments("Materials\\Models/", "materials/models")]
		[Arguments(" ", "")]
		public async Task NormalizesDirectoryPaths(string path, string expected)
		{
			await Assert.That(Package.NormalizeDirectoryPath(path)).IsEqualTo(expected);
		}

		[Test]
		[Arguments(" ")]
		[Arguments("")]
		public async Task GetsDirectoryPathWithRootAsEmptyString(string directoryName)
		{
			var entry = new PackageEntry
			{
				DirectoryName = directoryName,
				FileName = "file",
				TypeName = "txt",
			};

			using (Assert.Multiple())
			{
				await Assert.That(entry.GetDirectoryPath()).IsEmpty();
				await Assert.That(entry.GetFullPath()).IsEqualTo("file.txt");
			}
		}

		[Test]
		public async Task PathComparerIgnoresOnlyTheCaseOfAsciiLetters()
		{
			var folders = new Dictionary<string, int>(Package.PathComparer) { ["Models"] = 1 };

			using (Assert.Multiple())
			{
				await Assert.That(Package.PathComparer.Equals("MODELS/A.txt", "models/a.TXT")).IsTrue();
				await Assert.That(Package.PathComparer.Equals("Ä", "ä")).IsFalse();
				await Assert.That(folders.GetAlternateLookup<ReadOnlySpan<char>>().ContainsKey("mODELS")).IsTrue();
			}
		}
	}
}
