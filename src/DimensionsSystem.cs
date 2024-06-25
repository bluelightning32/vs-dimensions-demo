using System.Collections.Generic;
using System.Linq;

using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Dimensions;

public class DimensionsSystem : ModSystem {
  private ICoreAPI _api;
  // True if `_freeMiniDimensionIndexes` should be serialized again the next
  // time a save game is created.
  private bool _freeMiniDimensionIndexesDirty = false;
  // A list of mini dimension indices that were previously allocated with
  // `IServerAPI.LoadMiniDimension`, but were later freed with no way to return
  // them back to `IServerAPI`.
  private Queue<int> _freeMiniDimensionIndexes = new();

  public static DimensionsSystem GetInstance(ICoreAPI api) {
    return api.ModLoader.GetModSystem<DimensionsSystem>();
  }

  public override void Start(ICoreAPI api) {
    _api = api;
    api.RegisterBlockBehaviorClass(nameof(BlockBehaviors.BlockEntityForward),
                                   typeof(BlockBehaviors.BlockEntityForward));
    api.RegisterBlockEntityBehaviorClass(
        nameof(BlockEntityBehaviors.SchematicPreview),
        typeof(BlockEntityBehaviors.SchematicPreview));
  }

  public override void StartClientSide(ICoreClientAPI api) {}

  public override void StartServerSide(ICoreServerAPI api) {
    api.Event.SaveGameLoaded += OnSaveGameLoaded;
    api.Event.GameWorldSave += OnGameWorldSave;
  }

  private void OnGameWorldSave() {
    if (_freeMiniDimensionIndexesDirty) {
      ((ICoreServerAPI)_api)
          .WorldManager.SaveGame.StoreData("dimensions.freeDimensions",
                                           _freeMiniDimensionIndexes.ToList());
      _freeMiniDimensionIndexesDirty = false;
    }
  }

  private void OnSaveGameLoaded() {
    _freeMiniDimensionIndexes =
        new(((ICoreServerAPI)_api)
                .WorldManager.SaveGame.GetData<List<int>>(
                    "dimensions.freeDimensions"));
    _freeMiniDimensionIndexes ??= new();
    _freeMiniDimensionIndexesDirty = false;
  }

  public void AllocateMiniDimension(ICoreServerAPI sapi,
                                    IMiniDimension blocks) {
    if (_freeMiniDimensionIndexes.TryDequeue(out int index)) {
      sapi.Server.SetMiniDimension(blocks, index);
      _freeMiniDimensionIndexesDirty = true;
      sapi.Logger.Notification("Recycled subdimension id {0}", index);
    } else {
      index = sapi.Server.LoadMiniDimension(blocks);
      sapi.Logger.Notification("Allocated new subdimension id {0}", index);
    }
    // The minidimension index is its index in the `LoadedMiniDimensions`
    // dictionary. Additionally minidimensions can be identified by their
    // subdimension id (their dimension id is always 1). Since there is no
    // system function to allocate a subdimension id, this code follows the
    // `WorldEdit`'s convention of setting the subdimension id equal to the
    // minidimension index.
    //
    // Note that neither `LoadMiniDimension` nor `SetMiniDimension` set the
    // subdimension id.
    blocks.SetSubDimensionId(index);
  }

  public void FreeMiniDimension(IMiniDimension blocks) {
    // `LoadMiniDimension` and `SetMiniDimension` can add minidimensions
    // to the `LoadedMiniDimensions` dictionary, but there is no public API for
    // removing entries from that dictionary. Trying to store null in the
    // dictionary entry through `SetMiniDimension` will cause a
    // NullReferenceException on the next server tick.
    //
    // So instead, clear as much data as possible from the minidimension, and
    // leave it in the array. If the index is eventually recycled by
    // `AllocateMiniDimension`, then the new `MiniDimension` will replace the
    // old one in the dictionary.
    blocks.ClearChunks();
    blocks.UnloadUnusedServerChunks();
    _freeMiniDimensionIndexes.Enqueue(blocks.subDimensionId);
    _freeMiniDimensionIndexesDirty = true;
    _api.Logger.Notification("Freed subdimension id {0}",
                             blocks.subDimensionId);
  }

  public override void Dispose() { base.Dispose(); }
}
