using ConvivenciaPix.Infrastructure.Hsm;
using Dinamo.Hsm;
using FluentAssertions;
using Microsoft.Extensions.Options;
using System.Linq;
using System.Reflection;
using Xunit;

namespace ConvivenciaPix.Infrastructure.Tests.Hsm;

/// <summary>
/// Unit coverage for the managed contract of the real Dinamo SDK wrapper (construction + lifecycle),
/// which runs on any platform. Actual SignPIX/postPIX delegation additionally requires the native
/// tacndlib (shipped for linux-x64/musl and win-x64/x86 only) AND a reachable HSM appliance, so
/// signing/HTTP is exercised by HSM integration tests — never fully offline.
/// </summary>
public sealed class DinamoNetSdkClientTests
{
    private static DinamoNetSdkClient New() =>
        new(Options.Create(new DinamoOptions { Host = "hsm.local", UserId = "u", Password = "p" }));

    [Fact]
    public void Dispose_WithEmptyPool_IsNoOp()
    {
        var sut = New();
        sut.Invoking(s => s.Dispose()).Should().NotThrow();
    }

    // Contract guard against the real Dinamo.Hsm assembly: reflection on managed metadata needs
    // neither the native tacndlib nor an HSM, so it runs on any platform. It fails if a future
    // package version drifts from the exact signatures the wrapper binds to (which would otherwise
    // surface only at runtime in Production).
    [Fact]
    public void Wrapper_BindsToTheRealDinamoClientApi()
    {
        var t = typeof(DinamoClient);

        HasMethod(t, "SignPIX", typeof(byte[]), typeof(string), typeof(string), typeof(byte[]))
            .Should().BeTrue("wrapper calls SignPIX(keyId, certId, byte[]) -> byte[]");
        HasMethod(t, "VerifyPIX", typeof(bool), typeof(string), typeof(string), typeof(string))
            .Should().BeTrue("wrapper calls VerifyPIX(chainId, crl, string) -> bool");
        HasMethod(t, "SignPIXDict", typeof(byte[]), typeof(string), typeof(string), typeof(byte[]))
            .Should().BeTrue("wrapper calls SignPIXDict(keyId, certId, byte[]) -> byte[]");
        HasMethod(t, "VerifyPIXDict", typeof(bool), typeof(string), typeof(string), typeof(byte[]))
            .Should().BeTrue("wrapper calls VerifyPIXDict(chainId, crl, byte[]) -> bool");
        HasMethod(t, "Connect", typeof(void), typeof(string), typeof(string), typeof(string), typeof(bool), typeof(bool))
            .Should().BeTrue("wrapper calls Connect(address, user, pass, encrypted, useLoadBalance)");
        HasMethod(t, "Disconnect", typeof(void))
            .Should().BeTrue("wrapper calls parameterless Disconnect()");
        HasMethod(t, "getPIXHTTPReqCode", typeof(long))
            .Should().BeTrue("wrapper reads the HTTP status via getPIXHTTPReqCode() -> long");

        // PIX HTTP verbs: post/put carry a byte[] body; get/delete do not.
        HasMethod(t, "postPIX", typeof(PIXResponse), typeof(string), typeof(string), typeof(string),
            typeof(string), typeof(string[]), typeof(byte[]), typeof(int), typeof(bool), typeof(bool))
            .Should().BeTrue("wrapper calls postPIX(keyId, certId, chainId, url, headers, body, timeout, gzip, verifyHost)");
        HasMethod(t, "getPIX", typeof(PIXResponse), typeof(string), typeof(string), typeof(string),
            typeof(string), typeof(string[]), typeof(int), typeof(bool), typeof(bool))
            .Should().BeTrue("wrapper calls getPIX(keyId, certId, chainId, url, headers, timeout, gzip, verifyHost)");

        // PIXResponse must expose the raw header block and body the wrapper maps from.
        typeof(PIXResponse).GetProperty("Header").Should().NotBeNull();
        typeof(PIXResponse).GetProperty("Body").Should().NotBeNull();
    }

    private static bool HasMethod(Type type, string name, Type returnType, params Type[] parameters) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Any(m => m.Name == name
                && m.ReturnType == returnType
                && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameters));
}
