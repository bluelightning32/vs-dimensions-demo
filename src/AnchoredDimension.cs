using Vintagestory.API.MathTools;
using Vintagestory.Common;

namespace Dimensions;

public class AnchoredDimension : BlockAccessorMovable {
  public AnchoredDimension(BlockAccessorBase parent, Vec3d pos)
      : base(parent, pos) {}

  public override FastVec3d GetRenderOffset(float dt) {
    if (!TrackSelection) {
      // This dimension is not the active tracking dimension, which means it
      // would not be rendered transparently. So return a bogus offset so that
      // it is rendered opaquely outside of the camera's view.
      return new FastVec3d();
    }
    // Return the same value that `BlockAccessorMovable.GetRenderOffset` would
    // if `TrackSelection` was false.
    FastVec3d result = new FastVec3d(-(subDimensionId % 4096) * 16384, 0.0,
                                     -(subDimensionId / 4096) * 16384)
                           .Add(-8192.0);
    return result.Add(CurrentPos.X, CurrentPos.Y, CurrentPos.Z);
  }
}
