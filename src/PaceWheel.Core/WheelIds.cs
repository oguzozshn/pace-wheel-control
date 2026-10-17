namespace PaceWheel.Core;

/// <summary>USB identifiers of the Rampage Pace V987 wheel.</summary>
public static class WheelIds
{
    public const int VendorId = 0x11FF;
    public const int ProductId = 0x2370;

    /// <summary>
    /// DirectInput product GUIDs encode the USB IDs in their first 32 bits as (PID &lt;&lt; 16) | VID.
    /// </summary>
    public static bool MatchesProductGuid(Guid productGuid, int vendorId = VendorId, int productId = ProductId)
    {
        uint data1 = BitConverter.ToUInt32(productGuid.ToByteArray(), 0);
        return data1 == (uint)((productId << 16) | vendorId);
    }
}
