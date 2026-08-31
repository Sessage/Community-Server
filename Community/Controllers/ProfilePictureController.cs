using System.Security.Claims;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Klassenbibliothek.Data;
using Klassenbibliothek.Localization;

namespace TodoSuite.Server.Controllers;

[ApiController]
[Route("api/profile-picture")]
public class ProfilePictureController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IWebHostEnvironment _env;
    private readonly IStringLocalizer<SharedResource> _localizer;

    // Maximale Dateigröße: 2 MB (nach Base64-Dekodierung)
    private const int MaxFileSizeBytes = 2 * 1024 * 1024;
    private const int MaxBase64ImageChars = ((MaxFileSizeBytes + 2) / 3) * 4;
    private const int MaxProfilePictureRequestBytes = 4 * 1024 * 1024;
    private const int ExpectedImageSize = 128;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> UserLocks =
        new(StringComparer.Ordinal);

    public ProfilePictureController(UserManager<ApplicationUser> userManager, IWebHostEnvironment env, IStringLocalizer<SharedResource> localizer)
    {
        _userManager = userManager;
        _env = env;
        _localizer = localizer;
    }

    /// <summary>
    /// Gibt das Profilbild eines Benutzers zurück.
    /// Das Bild ist öffentlich zugänglich (kein Auth erforderlich).
    /// </summary>
    [HttpGet("{userId}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetProfilePicture(string userId, CancellationToken cancellationToken)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user?.ProfilePicturePath is null)
            return NotFound();

        var fullPath = ResolveProfilePicturePath(user.ProfilePicturePath);
        if (fullPath is null)
            return NotFound();

        if (!System.IO.File.Exists(fullPath))
            return NotFound();

        Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        Response.Headers.Pragma = "no-cache";
        Response.Headers.XContentTypeOptions = "nosniff";
        return PhysicalFile(fullPath, GetContentType(fullPath));
    }

    /// <summary>
    /// Lädt ein neues Profilbild hoch.
    /// Erwartet einen JSON-Body mit dem Feld "imageData" als Data-URL (base64).
    /// Das Bild muss bereits auf 128×128 px zugeschnitten sein (vom Client).
    /// </summary>
    [HttpPost]
    [Authorize(Policy = "MobileApi")]
    [RequestSizeLimit(MaxProfilePictureRequestBytes)]
    public async Task<IActionResult> UploadProfilePicture(
        [FromBody] UploadProfilePictureRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ImageData))
            return BadRequest(new { Error = _localizer["Err_Profile_NoImageProvided"].Value });

        // Data-URL parsen: data:image/jpeg;base64,<data>
        var match = Regex.Match(request.ImageData, @"^data:image/(jpeg|jpg|png|webp);base64,(.+)$");
        if (!match.Success)
            return BadRequest(new { Error = _localizer["Err_Profile_InvalidImageFormat"].Value });

        if (match.Groups[2].Value.Length > MaxBase64ImageChars)
            return BadRequest(new { Error = _localizer["Err_Profile_ImageTooLarge"].Value });

        byte[] imageBytes;
        try
        {
            imageBytes = Convert.FromBase64String(match.Groups[2].Value);
        }
        catch
        {
            return BadRequest(new { Error = _localizer["Err_Profile_DecodeFailed"].Value });
        }

        if (imageBytes.Length > MaxFileSizeBytes)
            return BadRequest(new { Error = _localizer["Err_Profile_ImageTooLarge"].Value });

        var extension = NormalizeImageExtension(match.Groups[1].Value);
        if (!HasExpectedImageSignature(imageBytes, extension))
            return BadRequest(new { Error = _localizer["Err_Profile_InvalidImageFormat"].Value });

        if (!TryGetImageDimensions(imageBytes, extension, out var width, out var height)
            || width != ExpectedImageSize
            || height != ExpectedImageSize)
        {
            return BadRequest(new { Error = _localizer["Err_Profile_InvalidDimensions"].Value });
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var userLock = UserLocks.GetOrAdd(userId, static _ => new SemaphoreSlim(1, 1));
        await userLock.WaitAsync(cancellationToken);
        try
        {
            var user = await _userManager.FindByIdAsync(userId);
            if (user is null)
                return Unauthorized();

            var profilePicturesDir = Path.Combine(GetWebRootPath(), "profile-pictures");
            Directory.CreateDirectory(profilePicturesDir);

            var oldPath = ResolveProfilePicturePath(user.ProfilePicturePath ?? string.Empty);
            var fileName = $"{Guid.NewGuid():N}{extension}";
            var relativePath = $"profile-pictures/{fileName}";
            var fullPath = Path.Combine(profilePicturesDir, fileName);

            try
            {
                await System.IO.File.WriteAllBytesAsync(fullPath, imageBytes, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return StatusCode(500, new { Error = _localizer["Err_Profile_SaveFailed"].Value });
            }

            user.ProfilePicturePath = relativePath;
            var result = await _userManager.UpdateAsync(user);
            if (!result.Succeeded)
            {
                TryDeleteFile(fullPath);
                return StatusCode(500, new { Error = _localizer["Err_Profile_SaveFailed"].Value });
            }

            if (oldPath is not null && !string.Equals(oldPath, fullPath, StringComparison.OrdinalIgnoreCase))
                TryDeleteFile(oldPath);

            return Ok(new { Url = $"/api/profile-picture/{Uri.EscapeDataString(userId)}" });
        }
        finally
        {
            userLock.Release();
        }
    }

    /// <summary>
    /// Löscht das Profilbild des angemeldeten Benutzers.
    /// </summary>
    [HttpDelete]
    [Authorize(Policy = "MobileApi")]
    public async Task<IActionResult> DeleteProfilePicture(CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var userLock = UserLocks.GetOrAdd(userId, static _ => new SemaphoreSlim(1, 1));
        await userLock.WaitAsync(cancellationToken);
        try
        {
            var user = await _userManager.FindByIdAsync(userId);
            if (user is null)
                return Unauthorized();

            if (string.IsNullOrEmpty(user.ProfilePicturePath))
                return NoContent();

            var fullPath = ResolveProfilePicturePath(user.ProfilePicturePath);
            user.ProfilePicturePath = null;
            var result = await _userManager.UpdateAsync(user);
            if (!result.Succeeded)
                return StatusCode(500, new { Error = _localizer["Err_Profile_SaveFailed"].Value });

            if (fullPath is not null)
                TryDeleteFile(fullPath);
            return NoContent();
        }
        finally
        {
            userLock.Release();
        }
    }

    public record UploadProfilePictureRequest(string ImageData);

    private static string NormalizeImageExtension(string imageType)
        => imageType.Equals("jpeg", StringComparison.OrdinalIgnoreCase) || imageType.Equals("jpg", StringComparison.OrdinalIgnoreCase)
            ? ".jpg"
            : $".{imageType.ToLowerInvariant()}";

    private static string GetContentType(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "image/jpeg"
        };

    private static bool HasExpectedImageSignature(byte[] bytes, string extension)
        => extension switch
        {
            ".jpg" => bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,
            ".png" => bytes.Length > 8
                && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
                && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A,
            ".webp" => bytes.Length > 12
                && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
                && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50,
            _ => false
        };

    internal static bool TryGetImageDimensions(byte[] bytes, string extension, out int width, out int height)
    {
        width = 0;
        height = 0;
        return extension switch
        {
            ".png" => TryGetPngDimensions(bytes, out width, out height),
            ".jpg" => TryGetJpegDimensions(bytes, out width, out height),
            ".webp" => TryGetWebpDimensions(bytes, out width, out height),
            _ => false
        };
    }

    private static bool TryGetPngDimensions(byte[] bytes, out int width, out int height)
    {
        width = height = 0;
        if (bytes.Length < 24 || !HasExpectedImageSignature(bytes, ".png")) return false;
        width = ReadBigEndianInt32(bytes, 16);
        height = ReadBigEndianInt32(bytes, 20);
        return width > 0 && height > 0;
    }

    private static bool TryGetJpegDimensions(byte[] bytes, out int width, out int height)
    {
        width = height = 0;
        if (!HasExpectedImageSignature(bytes, ".jpg")) return false;

        var offset = 2;
        while (offset + 3 < bytes.Length)
        {
            if (bytes[offset++] != 0xFF) continue;
            while (offset < bytes.Length && bytes[offset] == 0xFF) offset++;
            if (offset >= bytes.Length) return false;
            var marker = bytes[offset++];
            if (marker is 0xD8 or 0xD9 or 0x01 || marker is >= 0xD0 and <= 0xD7)
                continue;
            if (offset + 1 >= bytes.Length) return false;

            var segmentLength = (bytes[offset] << 8) | bytes[offset + 1];
            if (segmentLength < 2 || offset + segmentLength > bytes.Length) return false;
            if (IsStartOfFrameMarker(marker))
            {
                if (segmentLength < 7) return false;
                height = (bytes[offset + 3] << 8) | bytes[offset + 4];
                width = (bytes[offset + 5] << 8) | bytes[offset + 6];
                return width > 0 && height > 0;
            }

            offset += segmentLength;
        }

        return false;
    }

    private static bool TryGetWebpDimensions(byte[] bytes, out int width, out int height)
    {
        width = height = 0;
        if (bytes.Length < 30 || !HasExpectedImageSignature(bytes, ".webp")) return false;
        var chunk = System.Text.Encoding.ASCII.GetString(bytes, 12, 4);
        switch (chunk)
        {
            case "VP8X" when bytes.Length >= 30:
                width = 1 + ReadLittleEndian24(bytes, 24);
                height = 1 + ReadLittleEndian24(bytes, 27);
                break;
            case "VP8L" when bytes.Length >= 25 && bytes[20] == 0x2F:
                var bits = (uint)(bytes[21] | (bytes[22] << 8) | (bytes[23] << 16) | (bytes[24] << 24));
                width = (int)(bits & 0x3FFF) + 1;
                height = (int)((bits >> 14) & 0x3FFF) + 1;
                break;
            case "VP8 " when bytes.Length >= 30
                              && bytes[23] == 0x9D && bytes[24] == 0x01 && bytes[25] == 0x2A:
                width = (bytes[26] | (bytes[27] << 8)) & 0x3FFF;
                height = (bytes[28] | (bytes[29] << 8)) & 0x3FFF;
                break;
        }

        return width > 0 && height > 0;
    }

    private static bool IsStartOfFrameMarker(byte marker)
        => marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7
            or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF;

    private static int ReadBigEndianInt32(byte[] bytes, int offset)
        => (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];

    private static int ReadLittleEndian24(byte[] bytes, int offset)
        => bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16);

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
                System.IO.File.Delete(path);
        }
        catch
        {
            // The database state is authoritative. A locked orphan can be cleaned up later.
        }
    }

    private string? ResolveProfilePicturePath(string relativePath)
    {
        var webRoot = GetWebRootPath();
        var root = Path.GetFullPath(Path.Combine(webRoot, "profile-pictures"));
        var fullPath = Path.GetFullPath(Path.Combine(webRoot, relativePath));
        return fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : null;
    }

    private string GetWebRootPath()
        => string.IsNullOrWhiteSpace(_env.WebRootPath)
            ? Path.Combine(_env.ContentRootPath, "wwwroot")
            : _env.WebRootPath;
}
