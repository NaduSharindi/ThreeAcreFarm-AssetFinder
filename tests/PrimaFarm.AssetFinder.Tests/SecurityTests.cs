using Microsoft.Extensions.Options;
using PrimaFarm.AssetFinder.Web.Infrastructure;
using PrimaFarm.AssetFinder.Web.Models;
using PrimaFarm.AssetFinder.Web.Services.Security;

namespace PrimaFarm.AssetFinder.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void Hash_verifies_and_uses_unique_salt()
    {
        var a = PasswordHasher.Hash("Correct Horse 1!");
        var b = PasswordHasher.Hash("Correct Horse 1!");

        Assert.NotEqual(a, b);
        Assert.StartsWith("PBKDF2-SHA256$600000$", a);
        Assert.True(PasswordHasher.Verify("Correct Horse 1!", a));
        Assert.False(PasswordHasher.Verify("correct horse 1!", a));
    }

    [Theory]
    [InlineData("")]
    [InlineData("plaintext")]
    [InlineData("PBKDF2-SHA256$abc$salt$hash")]
    [InlineData("PBKDF2-SHA256$1000$!!notbase64$!!")]
    public void Malformed_stored_hash_never_verifies(string stored) =>
        Assert.False(PasswordHasher.Verify("anything", stored));
}

public class ApiKeyValidatorTests
{
    private static ApiKeyValidator Validator(Dictionary<string, string> clients)
    {
        var monitor = new StaticMonitor(new ApiClientOptions { Clients = clients });
        return new ApiKeyValidator(monitor);
    }

    [Fact]
    public void Only_the_hash_is_configured_and_the_key_maps_to_its_client()
    {
        var v = Validator(new() { ["ErpGateway"] = ApiKeyValidator.HashKey("secret-key-1") });

        Assert.Equal("ErpGateway", v.Validate("secret-key-1"));
        Assert.Null(v.Validate("secret-key-2"));
        Assert.Null(v.Validate(""));
        Assert.Null(v.Validate(new string('x', 500)));
    }

    private sealed class StaticMonitor(ApiClientOptions value) : IOptionsMonitor<ApiClientOptions>
    {
        public ApiClientOptions CurrentValue => value;
        public ApiClientOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<ApiClientOptions, string?> listener) => null;
    }
}

public class FileUploadValidatorTests
{
    [Theory]
    [InlineData(".jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 })]
    [InlineData(".png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })]
    [InlineData(".xlsx", new byte[] { 0x50, 0x4B, 0x03, 0x04 })]
    [InlineData(".txt", new byte[] { 0x48, 0x46, 0x41 })]
    public void Accepts_content_matching_extension(string ext, byte[] content) =>
        FileUploadValidator.Validate(content, ext);

    [Theory]
    [InlineData(".jpg", new byte[] { 0x3C, 0x73, 0x76, 0x67 })]          // "<svg" renamed to .jpg
    [InlineData(".png", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 })]          // jpeg renamed to .png
    [InlineData(".xlsx", new byte[] { 0x4D, 0x5A, 0x90, 0x00 })]         // Windows executable renamed to .xlsx
    [InlineData(".txt", new byte[] { 0x48, 0x00, 0x41 })]                // binary data in a text extract
    [InlineData(".exe", new byte[] { 0x4D, 0x5A })]
    public void Rejects_spoofed_content(string ext, byte[] content) =>
        Assert.Throws<BusinessRuleException>(() => FileUploadValidator.Validate(content, ext));
}
