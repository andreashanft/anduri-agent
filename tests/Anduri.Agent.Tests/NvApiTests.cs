using System.Runtime.InteropServices;
using Anduri.Agent.Sensors.Nvidia;

namespace Anduri.Agent.Tests;

/// <summary>NvAPI can't run here, but the driver rejects any request whose layout is off, so pin the layouts.</summary>
public unsafe class NvApiTests
{
    [Fact]
    public void RequestsMatchTheDriverLayout()
    {
        Assert.Equal(8 + 40 * 4, sizeof(NvApi.Thermals));
        Assert.Equal(24, sizeof(NvApi.RegisterOp));
        Assert.Equal(8, (int)Marshal.OffsetOf<NvApi.RegisterOp>(nameof(NvApi.RegisterOp.WriteMask)));
        Assert.Equal(16, (int)Marshal.OffsetOf<NvApi.RegisterOp>(nameof(NvApi.RegisterOp.Value)));
        Assert.Equal(0x1808, NvApi.RegisterRequestSize);

        var thermals = new NvApi.Thermals(0x3FF);
        Assert.Equal(0x0002_00A8u, thermals.Version);
        Assert.Equal(0x3FF, thermals.Mask);
        Assert.Equal(0, thermals.Values[NvApi.HotspotIndex]);
    }

    [Fact]
    public void RegisterRequestHoldsOneGlobal32BitRead()
    {
        var buffer = new byte[NvApi.RegisterRequestSize];
        buffer.AsSpan().Fill(0xAB);
        fixed (byte* request = buffer)
        {
            NvApi.WriteRegisterRequest(request, NvApi.HotspotRegister);

            Assert.Equal(0x0001_1808u, *(uint*)request);
            Assert.Equal(1u, *(uint*)(request + 4));
            var op = (NvApi.RegisterOp*)(request + NvApi.RegisterHeaderSize);
            Assert.Equal(21, op->Flags);
            Assert.Equal(0x00AD_0AA0u, op->Offset);
            Assert.Equal(0ul, op->Value);
        }
        Assert.All(buffer[(NvApi.RegisterHeaderSize + 24)..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void ReadingsAreQ8Dot8Celsius()
    {
        Assert.Equal(45, NvApi.Celsius(45 * 256));
        Assert.Equal(45, NvApi.Celsius(45 * 256 + 200));
        Assert.Equal(254, NvApi.Celsius(254 * 256));
        // No sensor, saturated and negative readings aren't temperatures.
        Assert.Null(NvApi.Celsius(0));
        Assert.Null(NvApi.Celsius(255));
        Assert.Null(NvApi.Celsius(255 * 256));
        Assert.Null(NvApi.Celsius(-256));
        // The register's upper bits carry other state; only the low word is the temperature.
        Assert.Equal(34, NvApi.Celsius((int)(0x4000_2230u & 0xFFFF)));
    }
}
