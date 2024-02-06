using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using Frosty.Controls;
using Frosty.Core;
using Frosty.Core.Controls;
using Frosty.Core.Windows;
using FrostySdk;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostySdk.Resources;

namespace ProjectMerger
{
    public class ProjectMergerMenuExtension : MenuExtension
    {
        public override string TopLevelMenuName { get; } = "File";
        public override string MenuItemName { get; } = "Import from Project";
        
        public override RelayCommand MenuItemClicked => new RelayCommand((o) =>
        {
            FrostyOpenFileDialog openFileDialog = new FrostyOpenFileDialog("Select a project file", "Project File (*.fbproject)|*.fbproject", "FrostyProject");
            if (!openFileDialog.ShowDialog()) return;
            
            FrostyTaskWindow.Show("Importing project...", "", task =>
            {
                using (NativeReader reader = new NativeReader(new FileStream(openFileDialog.FileName, FileMode.Open, FileAccess.Read)))
                {
                    ulong magic = reader.ReadULong();
                    if (magic != 0x00005954534F5246)
                    {
                        MessageBoxResult result = FrostyMessageBox.Show(
                            "This project file appears to be a legacy project, and importing it could corrupt the current project file. Are you sure you wish to continue?",
                            "Project Merger", MessageBoxButton.YesNo);
                        if (result == MessageBoxResult.No) return;
                    }

                    try
                    {
                        #region Useless shit

                        uint version = reader.ReadUInt();
                        if (version < 9) return;
                        
                        //Bunch of stuff we don't care about we just want to get to the bundles and assets
                        reader.ReadNullTerminatedString();
                        reader.ReadLong();
                        reader.ReadLong();
                        reader.ReadUInt();
                        reader.ReadNullTerminatedString();
                        reader.ReadNullTerminatedString();
                        reader.ReadNullTerminatedString();
                        reader.ReadNullTerminatedString();
                        reader.ReadNullTerminatedString();
                        
                        //Read the images the mod has(pointless but idk how to skip these other then just reading them outright)
                        int size = reader.ReadInt();
                        if (size > 0)
                        {
                            reader.ReadBytes(size);
                        }
                        
                        for (int i = 0; i < 4; i++)
                        {
                            size = reader.ReadInt();
                            if (size > 0)
                            {
                                reader.ReadBytes(size);
                            }
                        }
                        
                        reader.ReadInt();

                        #endregion
                        
                        //FINALLY! We get to the good part; reading the contents of the project

                        #region Adding stuff(mostly dummies)

                        //All of this is just copy pasted from FrostyProject.LoadInternal()

                        #region Added Bundles

                        // bundles
                        int numItems = reader.ReadInt();
                        for (int i = 0; i < numItems; i++)
                        {
                            string name = reader.ReadNullTerminatedString();
                            string sbName = reader.ReadNullTerminatedString();
                            BundleType type = (BundleType)reader.ReadInt();

                            App.AssetManager.AddBundle(name, type, App.AssetManager.GetSuperBundleId(sbName));
                        }

                        #endregion

                        //this doesn't ACTUALLY load the Ebx, this just sets up dummy files for us to later write to
                        #region EBX
                        
                        numItems = reader.ReadInt();
                        for (int i = 0; i < numItems; i++)
                        {
                            EbxAssetEntry entry = new EbxAssetEntry
                            {
                                Name = reader.ReadNullTerminatedString(),
                                Guid = reader.ReadGuid()
                            };
                            App.AssetManager.AddEbx(entry);
                            entry.IsDirty = true;
                        }

                        #endregion

                        //Same applies here
                        #region Res & Chunks

                        // res
                        numItems = reader.ReadInt();
                        for (int i = 0; i < numItems; i++)
                        {
                            ResAssetEntry entry = new ResAssetEntry
                            {
                                Name = reader.ReadNullTerminatedString(),
                                ResRid = reader.ReadULong(),
                                ResType = reader.ReadUInt(),
                                ResMeta = reader.ReadBytes(0x10)
                            };
                            App.AssetManager.AddRes(entry);
                        }

                        // chunks
                        numItems = reader.ReadInt();
                        for (int i = 0; i < numItems; i++)
                        {
                            ChunkAssetEntry newEntry = new ChunkAssetEntry
                            {
                                Id = reader.ReadGuid(),
                                H32 = reader.ReadInt()
                            };
                            App.AssetManager.AddChunk(newEntry);
                        }

                        #endregion

                        #endregion

                        #region Writing data(for ebx and stuff)
                        Dictionary<int, AssetEntry> h32map = new Dictionary<int, AssetEntry>();
                        bool allowOverwrite = false;
                        bool userDecision = false;

                        #region Ebx

                        numItems = reader.ReadInt();
                        for (int i = 0; i < numItems; i++)
                        {
                            #region Getting original data

                            string name = reader.ReadNullTerminatedString();
                            List<AssetEntry> linkedEntries = FrostyProject.LoadLinkedAssets(reader);
                            List<int> bundles = new List<int>();

                            if (version >= 13)
                            {
                                int length = reader.ReadInt();
                                for (int j = 0; j < length; j++)
                                {
                                    string bundleName = reader.ReadNullTerminatedString();
                                    int bid = App.AssetManager.GetBundleId(bundleName);
                                    if (bid != -1)
                                        bundles.Add(bid);
                                }
                            }

                            bool isModified = reader.ReadBoolean();

                            bool isTransientModified = false;
                            string userData = "";
                            byte[] data = null;
                            bool modifiedResource = false;

                            if (isModified)
                            {
                                isTransientModified = reader.ReadBoolean();
                                if (version >= 12)
                                    userData = reader.ReadNullTerminatedString();

                                if (version < 13)
                                {
                                    int length = reader.ReadInt();
                                    for (int j = 0; j < length; j++)
                                    {
                                        string bundleName = reader.ReadNullTerminatedString();
                                        int bid = App.AssetManager.GetBundleId(bundleName);
                                        if (bid != -1)
                                            bundles.Add(bid);
                                    }
                                }

                                if (version >= 13)
                                    modifiedResource = reader.ReadBoolean();
                                data = reader.ReadBytes(reader.ReadInt());
                            }

                            #endregion

                            #region Writing data to project

                            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(name);

                            if (!userDecision && !entry.IsDirty && entry.IsModified)
                            {
                                MessageBoxResult result = FrostyMessageBox.Show(
                                    "Would you like me to overwrite modified files in this project with those from the imported one?",
                                    "Project Merger", MessageBoxButton.YesNo);
                                if (result == MessageBoxResult.Yes)
                                {
                                    allowOverwrite = true;
                                }

                                userDecision = true;
                            }

                            if (entry != null && (allowOverwrite || (entry.IsDirty || !entry.IsModified)))
                            {
                                entry.LinkedAssets.AddRange(linkedEntries);
                                entry.AddedBundles.AddRange(bundles);

                                if (isModified)
                                {
                                    entry.ModifiedEntry = new ModifiedAssetEntry
                                    {
                                        IsTransientModified = isTransientModified,
                                        UserData = userData
                                    };

                                    if (modifiedResource)
                                    {
                                        // store as modified resource data object
                                        entry.ModifiedEntry.DataObject = ModifiedResource.Read(data);
                                    }
                                    else
                                    {
                                        if (!entry.IsAdded && App.PluginManager.GetCustomHandler(entry.Type) != null)
                                        {
                                            // @todo: throw some kind of error
                                        }

                                        // store as a regular ebx
                                        using (EbxReader ebxReader = EbxReader.CreateProjectReader(new MemoryStream(data)))
                                        {
                                            EbxAsset asset = ebxReader.ReadAsset<EbxAsset>();
                                            entry.ModifiedEntry.DataObject = asset;

                                            if (entry.IsAdded)
                                                entry.Type = asset.RootObject.GetType().Name;
                                            entry.ModifiedEntry.DependentAssets.AddRange(asset.Dependencies);
                                        }
                                    }

                                    entry.OnModified();
                                    entry.IsDirty = true;
                                }

                                int hash = Utils.HashString(entry.Name);
                                if (!h32map.ContainsKey(hash))
                                    h32map.Add(hash, entry);
                            }

                            #endregion
                        }

                        #endregion

                        #region Res

                        // res
                        numItems = reader.ReadInt();
                        for (int i = 0; i < numItems; i++)
                        {
                            string name = reader.ReadNullTerminatedString();
                            List<AssetEntry> linkedEntries = FrostyProject.LoadLinkedAssets(reader);
                            List<int> bundles = new List<int>();

                            if (version >= 13)
                            {
                                int length = reader.ReadInt();
                                for (int j = 0; j < length; j++)
                                {
                                    string bundleName = reader.ReadNullTerminatedString();
                                    int bid = App.AssetManager.GetBundleId(bundleName);
                                    if (bid != -1)
                                        bundles.Add(bid);
                                }
                            }

                            bool isModified = reader.ReadBoolean();

                            Sha1 sha1 = Sha1.Zero;
                            long originalSize = 0;
                            byte[] resMeta = null;
                            byte[] data = null;
                            string userData = "";

                            if (isModified)
                            {
                                sha1 = reader.ReadSha1();
                                originalSize = reader.ReadLong();

                                int length = reader.ReadInt();
                                if (length > 0)
                                    resMeta = reader.ReadBytes(length);

                                if (version >= 12)
                                    userData = reader.ReadNullTerminatedString();

                                if (version < 13)
                                {
                                    length = reader.ReadInt();
                                    for (int j = 0; j < length; j++)
                                    {
                                        string bundleName = reader.ReadNullTerminatedString();
                                        int bid = App.AssetManager.GetBundleId(bundleName);
                                        if (bid != -1)
                                            bundles.Add(bid);
                                    }
                                }

                                data = reader.ReadBytes(reader.ReadInt());
                            }

                            ResAssetEntry entry = App.AssetManager.GetResEntry(name);
                            if (entry != null && (allowOverwrite || (entry.IsDirty || !entry.IsModified)))
                            {
                                entry.LinkedAssets.AddRange(linkedEntries);
                                entry.AddedBundles.AddRange(bundles);

                                if (isModified)
                                {
                                    entry.ModifiedEntry = new ModifiedAssetEntry
                                    {
                                        Sha1 = sha1,
                                        OriginalSize = originalSize,
                                        ResMeta = resMeta,
                                        UserData = userData
                                    };

                                    if (sha1 == Sha1.Zero)
                                    {
                                        // store as modified resource data object
                                        entry.ModifiedEntry.DataObject = ModifiedResource.Read(data);
                                    }
                                    else
                                    {
                                        if (!entry.IsAdded && App.PluginManager.GetCustomHandler((ResourceType)entry.ResType) != null)
                                        {
                                            // @todo: throw some kind of error here
                                        }

                                        // store as normal data
                                        entry.ModifiedEntry.Data = data;
                                    }

                                    entry.OnModified();
                                }

                                int hash = Utils.HashString(entry.Name);
                                if (!h32map.ContainsKey(hash))
                                    h32map.Add(hash, entry);
                            }
                        }

                        #endregion

                        #region Chunk

                        // chunks
                        numItems = reader.ReadInt();
                        for (int i = 0; i < numItems; i++)
                        {
                            Guid id = reader.ReadGuid();
                            List<int> bundles = new List<int>();

                            if (version >= 13)
                            {
                                int length = reader.ReadInt();
                                for (int j = 0; j < length; j++)
                                {
                                    string bundleName = reader.ReadNullTerminatedString();
                                    int bid = App.AssetManager.GetBundleId(bundleName);
                                    if (bid != -1)
                                        bundles.Add(bid);
                                }
                            }

                            Sha1 sha1 = Sha1.Zero;
                            uint logicalOffset = 0;
                            uint logicalSize = 0;
                            uint rangeStart = 0;
                            uint rangeEnd = 0;
                            int firstMip = -1;
                            int h32 = 0;
                            bool addToChunkBundles = false;
                            string userData = "";
                            byte[] data = null;

                            if (version > 13)
                            {
                                firstMip = reader.ReadInt();
                                h32 = reader.ReadInt();
                            }

                            bool isModified = true;
                            if (version >= 13)
                                isModified = reader.ReadBoolean();

                            if (isModified)
                            {
                                sha1 = reader.ReadSha1();
                                logicalOffset = reader.ReadUInt();
                                logicalSize = reader.ReadUInt();
                                rangeStart = reader.ReadUInt();
                                rangeEnd = reader.ReadUInt();

                                if (version < 14)
                                {
                                    firstMip = reader.ReadInt();
                                    h32 = reader.ReadInt();
                                }

                                addToChunkBundles = reader.ReadBoolean();
                                if (version >= 12)
                                    userData = reader.ReadNullTerminatedString();

                                if (version < 13)
                                {
                                    int length = reader.ReadInt();
                                    for (int j = 0; j < length; j++)
                                    {
                                        string bundleName = reader.ReadNullTerminatedString();
                                        int bid = App.AssetManager.GetBundleId(bundleName);
                                        if (bid != -1)
                                            bundles.Add(bid);
                                    }
                                }

                                data = reader.ReadBytes(reader.ReadInt());
                            }

                            ChunkAssetEntry entry = App.AssetManager.GetChunkEntry(id);

                            if (entry == null && isModified && (allowOverwrite || (entry.IsDirty || !entry.IsModified)))
                            {
                                // hack: since chunks are not modified by FrostEd patches, instead a new one
                                // is added when something that uses a chunk is modified. If an existing chunk
                                // from a project is missing, a new one is created, and its linked resource
                                // is used to fill in the bundles (this may fail if a chunk is not meant to be
                                // in any bundles)

                                ChunkAssetEntry newEntry = new ChunkAssetEntry
                                {
                                    Id = id,
                                    H32 = h32
                                };
                                App.AssetManager.AddChunk(newEntry);

                                if (h32map.ContainsKey(newEntry.H32))
                                {
                                    foreach (int bundleId in h32map[newEntry.H32].Bundles)
                                        newEntry.AddToBundle(bundleId);
                                }
                                entry = newEntry;
                            }

                            if (entry != null)
                            {
                                entry.AddedBundles.AddRange(bundles);
                                if (isModified)
                                {
                                    entry.ModifiedEntry = new ModifiedAssetEntry
                                    {
                                        Sha1 = sha1,
                                        LogicalOffset = logicalOffset,
                                        LogicalSize = logicalSize,
                                        RangeStart = rangeStart,
                                        RangeEnd = rangeEnd,
                                        FirstMip = firstMip,
                                        H32 = h32,
                                        AddToChunkBundle = addToChunkBundles,
                                        UserData = userData,
                                        Data = data
                                    };
                                    entry.OnModified();
                                }
                                else
                                {
                                    entry.H32 = h32;
                                    entry.FirstMip = firstMip;
                                }
                            }
                        }

                        #endregion

                        #endregion
                    }
                    catch (Exception e)
                    {
                        App.Logger.LogError("Project merging has failed! As a result of this, saving may lead to the current project corrupting. Please be careful!");
                    }

                    if (reader != null)
                    {
                        ((IDisposable)reader).Dispose();
                    }
                }
            });
        });
    }
}