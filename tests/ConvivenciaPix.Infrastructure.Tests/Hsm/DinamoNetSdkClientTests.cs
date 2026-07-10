using ConvivenciaPix.Infrastructure.Hsm;
using Dinamo.Hsm;
using FluentAssertions;
using System.Linq;
using System.Reflection;
using System.Text;
using Xunit;

namespace ConvivenciaPix.Infrastructure.Tests.Hsm;

/// <summary>
/// Unit coverage for the managed contract of the real Dinamo SDK wrapper (guards + lifecycle),
/// which runs on any platform. Actual SignPIX/VerifyPIX delegation additionally requires the
/// native tacndlib (shipped for linux-x64/musl and win-x64/x86 only) AND a reachable HSM
/// appliance, so it is exercised by the platform-gated smoke test below and by HSM integration
/// tests — never fully offline.
/// </summary>
public sealed class DinamoNetSdkClientTests
{
    [Fact]
    public void SignPIX_BeforeConnect_ThrowsWithoutTouchingNativeLib()
    {
        using var sut = new DinamoNetSdkClient();
        var act = () => sut.SignPIX("key", "cert", Encoding.UTF8.GetBytes("<x/>"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*Connect*");
    }

    [Fact]
    public void VerifyPIX_BeforeConnect_ThrowsWithoutTouchingNativeLib()
    {
        using var sut = new DinamoNetSdkClient();
        var act = () => sut.VerifyPIX("chain", "", "<x/>");
        act.Should().Throw<InvalidOperationException>().WithMessage("*Connect*");
    }

    [Fact]
    public void Disconnect_WhenNeverConnected_IsNoOp()
    {
        using var sut = new DinamoNetSdkClient();
        sut.Invoking(s => s.Disconnect()).Should().NotThrow();
    }

    [Fact]
    public void Dispose_WhenNeverConnected_IsNoOp()
    {
        var sut = new DinamoNetSdkClient();
        sut.Invoking(s => s.Dispose()).Should().NotThrow();
    }

    // Contract guard against the real Dinamo.Hsm assembly: reflection on managed metadata needs
    // neither the native tacndlib nor an HSM, so it runs on any platform. It fails if a future
    // package version drifts from the exact SignPIX/VerifyPIX/Connect signatures the wrapper binds
    // to (which would otherwise surface only at runtime in Production).
    [Fact]
    public void Wrapper_BindsToTheRealDinamoClientApi()
    {
        var t = typeof(DinamoClient);

        HasMethod(t, "SignPIX", typeof(byte[]), typeof(string), typeof(string), typeof(byte[]))
            .Should().BeTrue("DinamoNetSdkClient calls SignPIX(keyId, certId, byte[]) -> byte[]");
        HasMethod(t, "VerifyPIX", typeof(bool), typeof(string), typeof(string), typeof(string))
            .Should().BeTrue("DinamoNetSdkClient calls VerifyPIX(chainId, crl, string) -> bool");
        HasMethod(t, "Connect", typeof(void), typeof(string), typeof(string), typeof(string), typeof(bool), typeof(bool))
            .Should().BeTrue("DinamoNetSdkClient calls Connect(address, user, pass, encrypted, useLoadBalance)");
        HasMethod(t, "Disconnect", typeof(void))
            .Should().BeTrue("DinamoNetSdkClient calls parameterless Disconnect()");
    }

    private static bool HasMethod(Type type, string name, Type returnType, params Type[] parameters) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Any(m => m.Name == name
                && m.ReturnType == returnType
                && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameters));
}
