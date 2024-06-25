using System.Collections.Generic;
using System.Reflection;

using Dimensions.BlockBehaviors;

using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace Dimensions.BlockEntityBehaviors;

public static class MultiblockStructureExtension {
  public static Dictionary<int, AssetLocation>
  GetBlockCodes(this MultiblockStructure ms) {
    FieldInfo field = typeof(MultiblockStructure)
                          .GetField("BlockCodes", BindingFlags.Public |
                                                      BindingFlags.NonPublic |
                                                      BindingFlags.Instance);
    return (Dictionary<int, AssetLocation>)field.GetValue(ms);
  }

  public static List<BlockOffsetAndNumber>
  GetTransformedOffsets(this MultiblockStructure ms) {
    FieldInfo field =
        typeof(MultiblockStructure)
            .GetField("TransformedOffsets", BindingFlags.Public |
                                                BindingFlags.NonPublic |
                                                BindingFlags.Instance);
    return (List<BlockOffsetAndNumber>)field.GetValue(ms);
  }
}

public class SchematicPreview : BlockEntityBehavior, IBlockEntityForward {
  private bool _setPreview;
  private MultiblockStructure _multiblockStructure;

  // This is only set on the server side.
  private IMiniDimension _previewDimension = null;
  // This is set to `_previewDimension.subDimensionId`, or to -1 if
  // `_previewDimension` is null. The reason this is a separate field is so that
  // it can be set in `FromTreeAttributes` before `Api` is available to create
  // the full minidimension object.
  private int _previewDimensionId = -1;

  public SchematicPreview(BlockEntity blockentity) : base(blockentity) {}

  public override void Initialize(ICoreAPI api, JsonObject properties) {
    base.Initialize(api, properties);
    _setPreview = properties["setPreview"].AsBool();

    // An instance of this BlockEntityBehavior is created every time a block
    // with this behavior is placed or loaded. If the same block type is placed
    // multiple times, the same `properties` instance will be given to
    // `Initialize` each time. So this code shares the same multiblock structure
    // instance with all behaviors that are created from the same properties
    // instance, to save memory and the CPU cost of deserializing the json.
    Dictionary<JsonObject, MultiblockStructure> structureCache =
        ObjectCacheUtil
            .GetOrCreate<Dictionary<JsonObject, MultiblockStructure>>(
                api, "SchematicPreview.structureCache", () => new());
    if (!structureCache.TryGetValue(properties, out _multiblockStructure)) {
      _multiblockStructure =
          properties["multiblockStructure"].AsObject<MultiblockStructure>();
      _multiblockStructure.InitForUse(0);
      structureCache.Add(properties, _multiblockStructure);
    }
    if (Api is ICoreClientAPI capi) {
      if (_previewDimensionId > 0) {
        // The minidimension origin isn't transferred from the server, or even
        // saved to disk on the server. As part of loading and initializing the
        // block on the client, set everything on the minidimension except the
        // chunk contents.
        IMiniDimension dim =
            capi.World.GetOrCreateDimension(_previewDimensionId, Pos.ToVec3d());
        dim.CurrentPos.SetFrom(Pos.ToVec3d());
      }
      if (_setPreview) {
        capi.World.SetBlocksPreviewDimension(_previewDimensionId);
      }
    } else if (Api is ICoreServerAPI sapi) {
      if (_previewDimensionId != -1) {
        // This code is called when the server loads a SchematicPreview block
        // from disk that had a minidimension attached. The following code will
        // recreate the minidimension. However, it doesn't entirely work,
        // because minidimension chunks are only sent to logged in players who
        // are in range whenever the minidimension chunk is modified. The
        // minidimension chunks are not sent to new players when they first
        // join, and they are not sent to player when they enter into the range.
        // The chunks are always loaded on the server before they are loaded on
        // the client. So effectively the chunks are not sent to any players.

        _previewDimension =
            api.World.BlockAccessor.CreateMiniDimension(Pos.ToVec3d());
        // Register the minidimension in the loaded dimensions.
        sapi.Server.SetMiniDimension(_previewDimension, _previewDimensionId);
        // Set the subdimension id inside of the dimension object, because
        // `SetMiniDimension` doesn't do it.
        _previewDimension.SetSubDimensionId(_previewDimensionId);
        sapi.Logger.Notification("Recreated minidimension {0}",
                                 _previewDimensionId);
        // The old chunks are still in the saved game, but there is no way to
        // link them into the new MiniDimension object. So recreate them
        // instead. The new chunks will replace the old ones in the saved game
        // file.
        FillMiniDimension();
        sapi.Logger.Notification("Filled minidimension {0}",
                                 _previewDimensionId);
      }
    }
  }

  public override void
  FromTreeAttributes(ITreeAttribute tree,
                     IWorldAccessor worldAccessForResolve) {
    base.FromTreeAttributes(tree, worldAccessForResolve);

    _previewDimensionId = tree.GetAsInt("previewDimensionId");
    if (Api is ICoreClientAPI capi) {
      if (_previewDimensionId > 0) {
        IMiniDimension dim =
            capi.World.GetOrCreateDimension(_previewDimensionId, Pos.ToVec3d());
        // `GetOrCreateDimension` only sets the dimension origin if it creates
        // the dimension. The dimension may have been implicitly created
        // beforehand if the chunks for the minidimension were transferred
        // before this block update. So always update the current position in
        // case the dimension already existed.
        dim.CurrentPos.SetFrom(Pos.ToVec3d());
      }
      if (_setPreview) {
        capi.World.SetBlocksPreviewDimension(_previewDimensionId);
      }
    }
  }

  public override void ToTreeAttributes(ITreeAttribute tree) {
    base.ToTreeAttributes(tree);

    tree.SetInt("previewDimensionId", _previewDimensionId);
  }

  public override void OnBlockPlaced(ItemStack byItemStack) {
    base.OnBlockPlaced(byItemStack);
  }

  public override void OnBlockRemoved() {
    base.OnBlockRemoved();

    if (Api is ICoreServerAPI sapi) {
      DimensionsSystem sys = DimensionsSystem.GetInstance(Api);
      if (_previewDimension != null) {
        sys.FreeMiniDimension(_previewDimension);
        _previewDimension = null;
        _previewDimensionId = -1;
      }
    }
  }

  public bool OnBlockInteractStart(IPlayer byPlayer, BlockSelection blockSel,
                                   ref EnumHandling handled) {
    if (Api is not ICoreServerAPI sapi) {
      // Return true to indicate that the action is allowed on the client. This
      // way the action will be sent to the server next.
      handled = EnumHandling.Handled;
      return true;
    }
    DimensionsSystem sys = DimensionsSystem.GetInstance(Api);
    if (_previewDimensionId == -1) {
      _previewDimension =
          sapi.World.BlockAccessor.CreateMiniDimension(Pos.ToVec3d());
      sys.AllocateMiniDimension(sapi, _previewDimension);
      _previewDimensionId = _previewDimension.subDimensionId;
      FillMiniDimension();
    } else {
      sys.FreeMiniDimension(_previewDimension);
      _previewDimension = null;
      _previewDimensionId = -1;
    }
    // Send `_previewDimensionId` to the client so that it can change the
    // preview dimension.
    Blockentity.MarkDirty();

    handled = EnumHandling.Handled;
    // Return true to indicate that the interaction was allowed.
    return true;
  }

  private void FillMiniDimension() {
    // `AdjustPosForSubDimension` only adjusts the X, Y, and Z coordinates; it
    // does not set the dimension. The `SetBlock` function of
    // `_previewDimension` will set the dimension of the incoming
    // position. However, for safety (and to avoid a warning about an obsolete
    // function with the default constructor), initialize the dimension
    // here.
    BlockPos minidimCenter =
        new(Vintagestory.API.Config.Dimensions.MiniDimensions);
    // Adjust the X, Y, and Z coordinates to the center of the minidimension.
    _previewDimension.AdjustPosForSubDimension(minidimCenter);
    PlaceMismatchedBlocks(_previewDimension, minidimCenter);
  }

  private void PlaceMismatchedBlocks(IBlockAccessor placeAccessor,
                                     BlockPos placeCenterPos) {
    Dictionary<int, AssetLocation> blockCodes =
        _multiblockStructure.GetBlockCodes();
    List<BlockOffsetAndNumber> transformedOffsets =
        _multiblockStructure.GetTransformedOffsets();
    Dictionary<int, Block[]> idLookup = new();

    BlockPos readPos = new(Pos.dimension);
    BlockPos writePos = new(placeCenterPos.dimension);

    for (int i = 0; i < transformedOffsets.Count; i++) {
      // X, Y, and Z are offsets. W is a block type index.
      Vec4i offset = transformedOffsets[i];
      readPos.Set(Pos);
      readPos.Add(offset.X, offset.Y, offset.Z);
      Block hasBlock = Api.World.BlockAccessor.GetBlock(readPos);
      AssetLocation asset = blockCodes[offset.W];
      if (!WildcardUtil.Match(asset, hasBlock.Code)) {
        if (!idLookup.TryGetValue(offset.W, out Block[] wantBlocks)) {
          wantBlocks = Api.World.SearchBlocks(asset);
          idLookup.Add(offset.W, wantBlocks);
        }
        writePos.Set(placeCenterPos);
        writePos.Add(offset.X, offset.Y, offset.Z);
        PlaceMismatchedBlock(placeAccessor, writePos, hasBlock, wantBlocks);
      }
    }
  }

  private static void PlaceMismatchedBlock(IBlockAccessor placeAccessor,
                                           BlockPos writePos, Block hasBlock,
                                           Block[] wantBlocks) {
    if (wantBlocks.Length == 0) {
      return;
    }
    placeAccessor.SetBlock(wantBlocks[0].Id, writePos);
  }
}
