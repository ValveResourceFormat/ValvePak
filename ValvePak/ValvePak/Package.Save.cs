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
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace SteamDatabase.ValvePak
{
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

			var _trees = CreatePacketsGroup(Entries, maxFileBytes);

			if (_trees.Count >= 0x7FFF)
				throw new InvalidOperationException("The number of packages exceeds 32766");


			// Header
			writer.Write(MAGIC);
			writer.Write(2); // Version
			writer.Write(0); // TreeSize, to be updated later
			writer.Write(0); // FileDataSectionSize, to be updated later
			writer.Write(0); // ArchiveMD5SectionSize
			writer.Write(48); //OtherMD5SectionSize
			writer.Write(0); // SignatureSectionSize
			var headerSize = (int)(stream.Position - streamOffset);
			uint fileOffset = 0;
			const byte NullByte = 0;
			long fileTreeSize;

			if (_trees.Count == 1)
			{
				var tree = _trees[0];
				ushort archiveIndex = 0x7FFF;
				// File tree data
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

							writer.Write(Encoding.UTF8.GetBytes(entry.FileName));
							writer.Write(NullByte);
							writer.Write(entry.CRC32);
							writer.Write((short)0); // SmallData, we will put it into data instead
							writer.Write(archiveIndex);
							writer.Write(fileOffset);
							writer.Write(fileLength);
							writer.Write(ushort.MaxValue); // terminator, 0xFFFF

							fileOffset += fileLength;
						}

						writer.Write(NullByte);
					}

					writer.Write(NullByte);
				}

				writer.Write(NullByte);


				// File data
				foreach (var typeEntries in tree)
				{
					foreach (var directoryEntries in typeEntries.Value)
					{
						foreach (var entry in directoryEntries.Value)
						{
							ReadEntry(entry, out var fileData, validateCrc: false);
							writer.Write(fileData);
						}
					}
				}
			}
			else
			{
				for (ushort i = 0; i < 999; i++)
				{
					string sub_FilePath = GetSubFilePath(stream.Name, i);
					if (File.Exists(sub_FilePath))
						File.Delete(sub_FilePath);
				}
				for (ushort i = 0; i < _trees.Count; i++)
				{
					var tree = _trees[i];

					string sub_FilePath = GetSubFilePath(stream.Name,i);
					
					using var fs = new FileStream(sub_FilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
					using var writer_sub = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);

					fileOffset = 0;
					foreach (var fileType in tree.Keys)
					{
						//如果是一种类型的开头
						if (IsTypeHead(fileType, i, _trees))
						{
							writer.Write(Encoding.UTF8.GetBytes(fileType));
							writer.Write(NullByte);
						}

						var dics = tree[fileType];
						foreach (var dicName in dics.Keys)
						{
							//如果是一类地址的开头
							if (IsDirectoryHead(fileType, dicName, i, _trees))
							{
								writer.Write(Encoding.UTF8.GetBytes(dicName));
								writer.Write(NullByte);
							}

							var entries = dics[dicName];
							foreach (var entry in entries)
							{
								if (entry.FileName == "odest_spawn_02")
								{
									Debug.Write(1);
								}
								var fileLength = entry.TotalLength;
								writer.Write(Encoding.UTF8.GetBytes(entry.FileName));
								writer.Write(NullByte);
								writer.Write(entry.CRC32);
								writer.Write((short)0); // SmallData, we will put it into data instead
								writer.Write(i);
								writer.Write(fileOffset);
								writer.Write(fileLength);
								writer.Write(ushort.MaxValue); // terminator, 0xFFFF

								fileOffset += fileLength;

								ReadEntry(entry, out var fileData, validateCrc: false);
								writer_sub.Write(fileData);
							}

							//如果是一类地址的结尾
							if (IsDirectoryTail(fileType, dicName, i, _trees))
								writer.Write(NullByte);
						}

						//如果是一种类型的结尾
						if (IsTypeTail(fileType, i, _trees))
							writer.Write(NullByte);

					}
				}

				writer.Write(NullByte);
				fileTreeSize = stream.Position - headerSize;
			}

			fileTreeSize = stream.Position - headerSize;


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

		static string GetSubFilePath(string indexFilePath,ushort indexNumber)
		{
			FileInfo sub_FileInfo = new FileInfo(indexFilePath);

			string sub_FileName = Path.GetFileNameWithoutExtension(sub_FileInfo.FullName);
			if (sub_FileName.EndsWith("_dir", StringComparison.OrdinalIgnoreCase))
				sub_FileName = $"{sub_FileName[..^4]}";

			sub_FileName = $"{sub_FileName}_{indexNumber:D3}";
			return $"{sub_FileInfo.Directory}\\{sub_FileName}{sub_FileInfo.Extension}";
		}

		/// <summary>
		/// Determine if it is located at the beginning of a set of file types
		/// </summary>
		/// <param name="fileType">Current file type</param>
		/// <param name="treeIndex">Current tree index</param>
		/// <param name="trees">Tree list</param>
		/// <returns>Is located at the beginning of a set of files</returns>
		static bool IsTypeHead(string fileType, ushort treeIndex, List<Dictionary<string, Dictionary<string, List<PackageEntry>>>> trees)
		{
			var tree = trees[treeIndex];
			if (treeIndex == 0)
				return true;

			if (fileType == tree.Keys.First())
				return trees[treeIndex - 1].Keys.Last() != fileType;
			else
				return true;

		}
		/// <summary>
		/// Determine if it is located at the end of a set of file types
		/// </summary>
		/// <param name="fileType">Current file type</param>
		/// <param name="treeIndex">Current tree index</param>
		/// <param name="trees">Tree list</param>
		/// <returns>Is located at the end of a set of files</returns>
		static bool IsTypeTail(string fileType, ushort treeIndex, List<Dictionary<string, Dictionary<string, List<PackageEntry>>>> trees)
		{
			if (treeIndex == trees.Count - 1)
				return true;

			var tree = trees[treeIndex];
			if (fileType == tree.Keys.Last())
				return trees[treeIndex + 1].Keys.First() != fileType;
			else
				return true;

		}

		/// <summary>
		/// Determine if it is located at the beginning of a set of folder addresses
		/// </summary>
		/// <param name="fileType">Current file type</param>
		/// <param name="dicName">Current directory path</param>
		/// <param name="treeIndex">Current tree index</param>
		/// <param name="trees">Tree list</param>
		/// <returns>Is located at the beginning of a set of folder addresses</returns>
		static bool IsDirectoryHead(string fileType, string dicName, ushort treeIndex, List<Dictionary<string, Dictionary<string, List<PackageEntry>>>> trees)
		{
			if (treeIndex == 0)
				return true;

			var tree = trees[treeIndex];

			if (dicName == tree.Values.First().Keys.First() && fileType == tree.Keys.First())
				return trees[treeIndex - 1].Keys.Last() != fileType && trees[treeIndex - 1].Values.Last().Keys.Last() != dicName;
			else
				return true;
		}

		/// <summary>
		/// Determine if it is located at the end of a set of folder addresses
		/// </summary>
		/// <param name="fileType">Current file type</param>
		/// <param name="dicName">Current directory path</param>
		/// <param name="treeIndex">Current tree index</param>
		/// <param name="trees">Tree list</param>
		/// <returns>Is located at the end of a set of folder addresses</returns>
		static bool IsDirectoryTail(string fileType, string dicName, ushort treeIndex, List<Dictionary<string, Dictionary<string, List<PackageEntry>>>> trees)
		{
			if (treeIndex == trees.Count - 1)
				return true;

			var tree = trees[treeIndex];
			if (fileType == tree.Keys.Last() && dicName == tree.Values.Last().Keys.Last())
				return trees[treeIndex + 1].Keys.First() != fileType && trees[treeIndex - 1].Values.First().Keys.First() != dicName;
			else
				return true;
		}

		/// <summary>
		/// Split the current tree into multiple trees based on packet size
		/// </summary>
		/// <param name="mainTypeTree">Tree of data sources</param>
		/// <param name="maxFileBytes">Maximum file byte count</param>
		/// <returns>List of Trees</returns>
		/// <exception cref="InvalidOperationException">If the size of a single file exceeds the size of the package,throw this exception</exception>
		static List<Dictionary<string, Dictionary<string, List<PackageEntry>>>> CreatePacketsGroup(Dictionary<string, List<PackageEntry>> mainTypeTree, int maxFileBytes)
		{

			List<Dictionary<string, Dictionary<string, List<PackageEntry>>>> packets = new List<Dictionary<string, Dictionary<string, List<PackageEntry>>>>();
			//Create a new tree
			Dictionary<string, Dictionary<string, List<PackageEntry>>> currentTree = new Dictionary<string, Dictionary<string, List<PackageEntry>>>();
			packets.Add(currentTree);
			uint totalLength = 0;
			foreach (var typeEntries in mainTypeTree)
			{
				//Create a new type tree and add the current one to the current tree
				Dictionary<string, List<PackageEntry>> currentTypeTree = new Dictionary<string, List<PackageEntry>>();
				currentTree[typeEntries.Key] = currentTypeTree;

				//Group the entries under the current type tree according to their folder location
				IEnumerable<IGrouping<string, PackageEntry>> dicGroups = typeEntries.Value.GroupBy(s => s.DirectoryName.Length == 0 ? Space : s.DirectoryName);
				foreach (var dicGroup in dicGroups)
				{
					List<PackageEntry> entries = new List<PackageEntry>();
					currentTypeTree[dicGroup.Key] = entries;

					foreach (var entry in dicGroup)
					{
						var fileLength = entry.TotalLength;
						//If the size of a single file exceeds the size of the package
						if (fileLength >= maxFileBytes)
							throw new InvalidOperationException("There are files exceeding max file bytes");

						// TODO: Search for smaller files to fill in the empty space
						if (totalLength + fileLength >= maxFileBytes)
						{
							if (entries.Count == 0)
							{
								currentTypeTree.Remove(dicGroup.Key);
								if (currentTypeTree.Count == 0)
									currentTree.Remove(typeEntries.Key);

								if (currentTree.Count == 0)
									packets.Remove(currentTree);
							}

							currentTree = new Dictionary<string, Dictionary<string, List<PackageEntry>>>();
							packets.Add(currentTree);
							currentTypeTree = new Dictionary<string, List<PackageEntry>>();
							currentTree[typeEntries.Key] = currentTypeTree;
							entries = new List<PackageEntry>();
							currentTypeTree[dicGroup.Key] = entries;
							entries.Add(entry);
							totalLength = fileLength;
						}
						else
						{
							totalLength += fileLength;
							entries.Add(entry);
						}

					}

				}
			}
			return packets;
		}
	}
}
