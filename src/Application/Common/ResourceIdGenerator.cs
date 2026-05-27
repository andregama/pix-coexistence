using System.Security.Cryptography;

namespace ConvivenciaPix.Application.Common;

public static class ResourceIdGenerator
{
    // 24 random bytes → 32-char Base64 (matches the Bacen manual's PI-ResourceId examples).
    public static string Generate() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
}
