using PrimaFarm.AssetFinder.Web.Models;

namespace PrimaFarm.AssetFinder.Web.Infrastructure;

public enum UploadKind { Register, Photo }

public sealed record UploadedFile(string FileName, string Extension, MemoryStream Content);

/// <summary>
/// OWASP File Upload checks: allow-listed extension, size limit, and content signature
/// (an .xlsx must really be a ZIP container, a .jpg must start with the JPEG marker, a text
/// extract must not contain binary data). The client-supplied name is never used on disk.
/// </summary>
public static class FileUploadValidator
{
    public static readonly string[] RegisterExtensions = [".xlsx", ".xlsm", ".txt"];
    public static readonly string[] PhotoExtensions = [".jpg", ".jpeg", ".png"];

    private static readonly byte[] ZipSignature = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static async Task<UploadedFile> ReadAsync(IFormFile? file, UploadKind kind, long maxBytes, CancellationToken ct)
    {
        if (file is null) throw new BusinessRuleException("No file was selected.");

        var name = Path.GetFileName(file.FileName);   // strip any client-supplied path
        var ext = Path.GetExtension(name).ToLowerInvariant();
        var allowed = kind == UploadKind.Photo ? PhotoExtensions : RegisterExtensions;

        if (!allowed.Contains(ext))
            throw new BusinessRuleException($"Only {string.Join(", ", allowed)} files are accepted.");
        if (file.Length == 0)
            throw new BusinessRuleException("The file is empty.");
        if (file.Length > maxBytes)
            throw new BusinessRuleException($"The file is larger than the {maxBytes / (1024 * 1024)} MB limit.");

        var ms = new MemoryStream((int)file.Length);
        await file.CopyToAsync(ms, ct);
        Validate(ms.GetBuffer().AsSpan(0, (int)ms.Length), ext);

        ms.Position = 0;
        return new UploadedFile(name.Length > 200 ? name[..200] : name, ext, ms);
    }

    internal static void Validate(ReadOnlySpan<byte> bytes, string ext)
    {
        var ok = ext switch
        {
            ".xlsx" or ".xlsm" => bytes.StartsWith(ZipSignature),
            ".jpg" or ".jpeg" => bytes.StartsWith(JpegSignature),
            ".png" => bytes.StartsWith(PngSignature),
            ".txt" => !bytes[..Math.Min(bytes.Length, 8192)].Contains((byte)0),
            _ => false
        };
        if (!ok) throw new BusinessRuleException("The file content does not match its extension.");
    }
}
