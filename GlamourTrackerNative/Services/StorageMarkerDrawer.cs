using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;

namespace GlamourTracker.Services;

/// <summary>
/// UV helpers for dresser/armoire atlas debugging (Dev builds).
/// </summary>
internal static class StorageMarkerDrawer
{
    public static bool TryComputeUv(
        ISharedImmediateTexture? texture,
        StorageUiIconSlice slice,
        bool flipV,
        out Vector2 uv0,
        out Vector2 uv1)
    {
        uv0 = Vector2.Zero;
        uv1 = Vector2.One;

        if (texture == null || !slice.IsValid)
            return false;

        if (!texture.TryGetWrap(out var wrap, out _))
            return false;

        var texWidth = wrap.Width;
        var texHeight = wrap.Height;
        if (texWidth <= 0 || texHeight <= 0)
            return false;

        // Cast before divide: ushort/int is integer division (54/180 == 0).
        uv0 = new Vector2((float)slice.U / texWidth, (float)slice.V / texHeight);
        uv1 = new Vector2((float)(slice.U + slice.Width) / texWidth, (float)(slice.V + slice.Height) / texHeight);

        if (flipV)
        {
            var flipped0 = 1f - uv1.Y;
            var flipped1 = 1f - uv0.Y;
            uv0.Y = flipped0;
            uv1.Y = flipped1;
        }

        return uv0.X >= 0f && uv0.Y >= 0f && uv1.X <= 1.001f && uv1.Y <= 1.001f
            && uv1.X > uv0.X + 0.0001f && uv1.Y > uv0.Y + 0.0001f;
    }

    public static string DescribeUv(ISharedImmediateTexture? texture, StorageUiIconSlice slice, bool flipV)
    {
        if (!slice.IsValid)
            return "slice invalid";

        if (texture is null)
            return "no texture wrap (missing)";

        if (!texture.TryGetWrap(out var wrap, out var ex))
            return $"no texture wrap ({ex?.Message ?? "missing"})";

        if (!TryComputeUv(texture, slice, flipV, out var uv0, out var uv1))
            return $"tex {wrap.Width}x{wrap.Height} | atlas U={slice.U} V={slice.V} W={slice.Width} H={slice.Height} | UV invalid";

        return $"tex {wrap.Width}x{wrap.Height} | atlas U={slice.U} V={slice.V} W={slice.Width} H={slice.Height} | UV ({uv0.X:F4},{uv0.Y:F4})-({uv1.X:F4},{uv1.Y:F4})";
    }
}
