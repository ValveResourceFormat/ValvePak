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
		[Arguments("", null)]
		[Arguments("../b.txt", null)]
		[Arguments("a/b/", null)]
		[Arguments(".gitignore", null)]
		[Arguments("x.. ", "x.. ")]
		[Arguments("a/.. ", null)]
		[Arguments("a/..x", "a/..x")]
		[Arguments("dir/a.b. ", "dir/a.b. ")]
		[Arguments("a/b\0c.txt", null)]
		[Arguments("a/\ud800.txt", null)]
		public async Task NormalizesEntryPaths(string path, string? expected)
		{
			using (Assert.Multiple())
			{
				await Assert.That(Package.TryParseEntryPath(path, out var normalizedPath)).IsEqualTo(expected != null);
				await Assert.That(normalizedPath).IsEqualTo(expected);
			}
		}

		[Test]
		public async Task RejectsNamesThatAreTooLong()
		{
			using (Assert.Multiple())
			{
				await Assert.That(Package.TryParseEntryPath($"a.{new string('y', 129)}", out _)).IsFalse();
				await Assert.That(Package.TryParseEntryPath($"{new string('é', 131)}.txt", out _)).IsFalse();
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
