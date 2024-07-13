using Microsoft.VisualBasic.FileIO;
using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace SteamDatabase.ValvePak
{
	internal sealed class WriteEntry(ushort archiveIndex, uint fileOffset, PackageEntry entry)
	{
		internal ushort ArchiveIndex { get; set; } = archiveIndex;
		internal uint FileOffset { get; set; } = fileOffset;
		internal PackageEntry Entry { get; set; } = entry;
	}
	public partial class Package
	{
		/// <summary>
		/// Remove file from current package.
		/// </summary>
		/// <param name="entry">The package entry to remove.</param>
		/// <returns>Returns true if entry was removed, false otherwise.</returns>
		public bool RemoveFile(PackageEntry entry)
		{
			ArgumentNullException.ThrowIfNull(entry);

			if (!Entries.TryGetValue(entry.TypeName, out var typeEntries))
			{
				return false;
			}

			var removed = typeEntries.Remove(entry);

			if (typeEntries.Count == 0)
			{
				Entries.Remove(entry.TypeName);
			}

			return removed;
		}

		/// <summary>
		/// Add file to current package. Be careful to not add duplicate entries, because this does not check for duplicates.
		/// </summary>
		/// <param name="filePath">Full file path for this entry.</param>
		/// <param name="fileData">File data for this entry.</param>
		/// <returns>The added entry.</returns>
		public PackageEntry AddFile(string filePath, byte[] fileData)
		{
			ArgumentNullException.ThrowIfNull(filePath);

			filePath = filePath.Replace(WindowsDirectorySeparator, DirectorySeparatorChar);

			var lastSeparator = filePath.LastIndexOf(DirectorySeparatorChar);
			var directory = lastSeparator > -1 ? filePath[..lastSeparator] : string.Empty;
			var fileName = filePath[(lastSeparator + 1)..];

			var dot = fileName.LastIndexOf('.');
			string extension;

			if (dot > -1)
			{
				extension = fileName[(dot + 1)..];
				fileName = fileName[..dot];
			}
			else
			{
				// Valve uses a space for missing extensions
				extension = Space;
			}

			if (directory.Length == 0)
			{
				directory = Space;
			}

			// Putting file data into SmallData is kind of a hack
			var entry = new PackageEntry
			{
				FileName = fileName,
				DirectoryName = directory,
				TypeName = extension,
				SmallData = fileData,
				CRC32 = Crc32.HashToUInt32(fileData),
				ArchiveIndex = 0x7FFF,
			};

			if (Entries == null)
			{
				var stringComparer = Comparer == null ? null : StringComparer.FromComparison(Comparer.Comparison);
				Entries = new Dictionary<string, List<PackageEntry>>(stringComparer);
			}

			if (!Entries.TryGetValue(extension, out var typeEntries))
			{
				typeEntries = [];
				Entries[extension] = typeEntries;
			}

			typeEntries.Add(entry);

			return entry;
		}

		/// <summary>
		/// Opens and writes the given filename.
		/// </summary>
		/// <param name="filename">The file to open and write.</param>
		public void Write(string filename, int maxFileBytes = int.MaxValue)
		{
			ArgumentOutOfRangeException.ThrowIfNegative(maxFileBytes);

			using var fs = new FileStream(filename, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
			fs.SetLength(0);

			Write(fs, maxFileBytes);
		}

		/// <summary>
		/// Writes to the given <see cref="Stream"/>.
		/// </summary>
		/// <param name="stream">The input <see cref="Stream"/> to write to.</param>
		public void Write(FileStream stream, int maxFileBytes)
		{

			ArgumentNullException.ThrowIfNull(stream);

			if (!stream.CanSeek || !stream.CanRead)
				throw new InvalidOperationException("Stream must be seekable and readable.");

			using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

			// TODO: input.SetLength()
			var streamOffset = stream.Position;

			List<PackageEntry> entries = Entries.SelectMany(e => e.Value).ToList();

			if (entries.Any(e => e.TotalLength > maxFileBytes))
				throw new InvalidOperationException("There are files exceeding max file bytes");


			var tree = new Dictionary<string, Dictionary<string, List<PackageEntry>>>();

			// Precalculate the file tree and set data offsets
			foreach (var typeEntries in Entries)
			{
				var typeTree = new Dictionary<string, List<PackageEntry>>();
				tree[typeEntries.Key] = typeTree;

				foreach (var entry in typeEntries.Value)
				{
					var directoryName = entry.DirectoryName.Length == 0 ? Space : entry.DirectoryName;

					if (!typeTree.TryGetValue(directoryName, out var directoryEntries))
					{
						directoryEntries = [];
						typeTree[directoryName] = directoryEntries;
					}

					directoryEntries.Add(entry);
				}
			}

			// Header
			writer.Write(MAGIC);
			writer.Write(2); // Version
			writer.Write(0); // TreeSize, to be updated later
			writer.Write(0); // FileDataSectionSize, to be updated later
			writer.Write(0); // ArchiveMD5SectionSize
			writer.Write(48); //OtherMD5SectionSize
			writer.Write(0); // SignatureSectionSize
			var headerSize = (int)(stream.Position - streamOffset);
			const byte NullByte = 0;

			bool isSingleFile = entries.Sum(s => s.TotalLength) + headerSize + 64 <= maxFileBytes;

			var groups = CreatePacketsGroup(entries, maxFileBytes, isSingleFile);

			if (groups.Count >= 0x7FFF)
				throw new InvalidOperationException("The number of packages exceeds 32766");


			uint fileOffset = 0;
			foreach (var typeEntries in tree)
			{
				writer.Write(Encoding.UTF8.GetBytes(typeEntries.Key));
				writer.Write(NullByte);

				foreach (var directoryEntries in typeEntries.Value)
				{
					writer.Write(Encoding.UTF8.GetBytes(directoryEntries.Key));
					writer.Write(NullByte);

					foreach (var entry in directoryEntries.Value)
					{
						var fileLength = entry.TotalLength;

						var fullPath = entry.GetFullPath();
						WriteEntry writeEntry = null;

						foreach (var group in groups)
						{
							if (group.TryGetValue(fullPath, out writeEntry))
								break;
						}
						if (writeEntry is null)
							throw new InvalidOperationException("No need write entry found");


						writer.Write(Encoding.UTF8.GetBytes(entry.FileName));
						writer.Write(NullByte);
						writer.Write(entry.CRC32);
						writer.Write((short)0); // SmallData, we will put it into data instead
						writer.Write(writeEntry.ArchiveIndex);
						writer.Write(writeEntry.FileOffset);
						writer.Write(fileLength);
						writer.Write(ushort.MaxValue); // terminator, 0xFFFF

						fileOffset += fileLength;
					}

					writer.Write(NullByte);
				}

				writer.Write(NullByte);
			}

			writer.Write(NullByte);

			//clear sub file
			for (ushort i = 0; i < 999; i++)
			{
				string sub_FilePath = GetSubFilePath(stream.Name, i);
				if (File.Exists(sub_FilePath))
					File.Delete(sub_FilePath);
			}

			if (isSingleFile)
			{
				//Write file data
				foreach (var writeEntry in groups[0].Values)
				{
					ReadEntry(writeEntry.Entry, out var fileData, validateCrc: false);
					writer.Write(fileData);
				}
			}
			else
			{
				//Create and write sub file data
				for (ushort i = 0; i < groups.Count; i++)
				{
					string sub_FilePath = GetSubFilePath(stream.Name, i);

					using var fs = new FileStream(sub_FilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
					using var writer_sub = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);

					var group = groups[i];
					foreach (var writeEntry in group.Values)
					{
						ReadEntry(writeEntry.Entry, out var fileData, validateCrc: false);
						writer_sub.Write(fileData);
					}
				}

			}


			long fileTreeSize = stream.Position - headerSize;


			var afterFileData = stream.Position;
			var fileDataSize = afterFileData - fileTreeSize - headerSize;

			// Set tree size
			// TODO: It is possible to precalculate these sizes to remove seeking
			stream.Seek(streamOffset + (2 * sizeof(int)), SeekOrigin.Begin);
			writer.Write((int)fileTreeSize);
			writer.Write((int)fileDataSize);

			// Calculate file hashes
			stream.Seek(streamOffset, SeekOrigin.Begin);

			var buffer = ArrayPool<byte>.Shared.Rent(4096);

			try
			{
				// TODO: It is possible to transform these hashes while writing the file to remove seeking and stream reading
				using var fileTreeMD5 = MD5.Create();
				using var fullFileMD5 = MD5.Create();

				stream.Read(buffer, 0, headerSize);
				fullFileMD5.TransformBlock(buffer, 0, headerSize, null, 0);

				int bytesRead;
				var fileTreeRead = 0;

				// Calculate file tree size hash
				while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
				{
					fullFileMD5.TransformBlock(buffer, 0, bytesRead, null, 0);

					fileTreeRead += bytesRead;

					if (fileTreeRead >= fileTreeSize)
					{
						fileTreeMD5.TransformFinalBlock(buffer, 0, (int)(fileTreeSize - (fileTreeRead - bytesRead)));
						break;
					}

					fileTreeMD5.TransformBlock(buffer, 0, bytesRead, null, 0);
				}

				// Calculate remaining file data hash
				while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
				{
					fullFileMD5.TransformBlock(buffer, 0, bytesRead, null, 0);
				}

				// File tree hash
				writer.Write(fileTreeMD5.Hash);

				fullFileMD5.TransformBlock(fileTreeMD5.Hash, 0, fileTreeMD5.Hash.Length, null, 0);

				// File hashes hash
				var fileHashesMD5 = MD5.HashData([]); // We did not write any file hashes
				writer.Write(fileHashesMD5);

				// Full file hash
				fullFileMD5.TransformFinalBlock(fileHashesMD5, 0, fileHashesMD5.Length);
				writer.Write(fullFileMD5.Hash);
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(buffer);
			}
		}

		/// <summary>
		/// Get the sub file name
		/// </summary>
		/// <param name="indexFilePath">Index file path</param>
		/// <param name="indexNumber">Index number</param>
		/// <returns></returns>
		static string GetSubFilePath(string indexFilePath, ushort indexNumber)
		{
			FileInfo sub_FileInfo = new FileInfo(indexFilePath);

			string sub_FileName = Path.GetFileNameWithoutExtension(sub_FileInfo.FullName);
			if (sub_FileName.EndsWith("_dir", StringComparison.OrdinalIgnoreCase))
				sub_FileName = $"{sub_FileName[..^4]}";

			sub_FileName = $"{sub_FileName}_{indexNumber:D3}";
			return $"{sub_FileInfo.Directory}\\{sub_FileName}{sub_FileInfo.Extension}";
		}

		/// <summary>
		/// Split the current tree into multiple trees based on packet size
		/// </summary>
		/// <param name="mainTypeTree">Tree of data sources</param>
		/// <param name="maxFileBytes">Maximum file byte count</param>
		/// <returns>List of Trees</returns>

		static List<Dictionary<string, WriteEntry>> CreatePacketsGroup(List<PackageEntry> entries, int maxFileBytes, bool isSingleFile)
		{
			List<Dictionary<string, WriteEntry>> groups = new List<Dictionary<string, WriteEntry>>();
			uint totalLength = 0;
			ushort archiveIndex = 0;
			Dictionary<string, WriteEntry> group = new Dictionary<string, WriteEntry>();
			groups.Add(group);

			if (isSingleFile)
			{
				foreach (var entry in entries)
				{
					group.Add(entry.GetFullPath(), new(0x7FFF, totalLength, entry));
					totalLength += entry.TotalLength;
				}
			}
			else
			{
				group.Add(entries[0].GetFullPath(), new(archiveIndex, totalLength, entries[0]));
				totalLength += entries[0].TotalLength;

				entries.RemoveAt(0);
				do
				{
					PackageEntry entry = entries.Find(e => e.TotalLength < (ulong)maxFileBytes - totalLength);
					if (entry is not null)
					{
						group.Add(entry.GetFullPath(), new(archiveIndex, totalLength, entry));
						totalLength += entry.TotalLength;
						entries.Remove(entry);
					}
					else
					{
						group = new Dictionary<string, WriteEntry>();
						groups.Add(group);
						totalLength = 0;
						archiveIndex++;
					}

				} while (entries.Count != 0);
			}


			return groups;
		}
	}
}
