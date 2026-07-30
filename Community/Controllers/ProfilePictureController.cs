using System.Security.Claims;
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
    public async Task<IActionResult> GetProfilePicture(string userId)
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
    public async Task<IActionResult> UploadProfilePicture([FromBody] UploadProfilePictureRequest request)
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

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var user = await _userManager.FindByIdAsync(userId);
        if (user is null)
            return Unauthorized();

        // Verzeichnis anlegen
        var profilePicturesDir = Path.Combine(GetWebRootPath(), "profile-pictures");
        Directory.CreateDirectory(profilePicturesDir);

        // Altes Bild löschen
        if (!string.IsNullOrEmpty(user.ProfilePicturePath))
        {
            var oldPath = ResolveProfilePicturePath(user.ProfilePicturePath);
            if (oldPath is not null && System.IO.File.Exists(oldPath))
            {
                try { System.IO.File.Delete(oldPath); } catch { /* ignorieren */ }
            }
        }

        // Neues Bild speichern
        var fileName = $"{Guid.NewGuid():N}{extension}";
        var relativePath = $"profile-pictures/{fileName}";
        var fullPath = Path.Combine(GetWebRootPath(), relativePath);

        await System.IO.File.WriteAllBytesAsync(fullPath, imageBytes);

        // Pfad in der Datenbank speichern
        user.ProfilePicturePath = relativePath;
        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            try { System.IO.File.Delete(fullPath); } catch { }
            return StatusCode(500, new { Error = _localizer["Err_Profile_SaveFailed"].Value });
        }

        return Ok(new { Url = $"/api/profile-picture/{userId}" });
    }

    /// <summary>
    /// Löscht das Profilbild des angemeldeten Benutzers.
    /// </summary>
    [HttpDelete]
    [Authorize(Policy = "MobileApi")]
    public async Task<IActionResult> DeleteProfilePicture()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var user = await _userManager.FindByIdAsync(userId);
        if (user is null)
            return Unauthorized();

        if (!string.IsNullOrEmpty(user.ProfilePicturePath))
        {
            var fullPath = ResolveProfilePicturePath(user.ProfilePicturePath);
            if (fullPath is not null && System.IO.File.Exists(fullPath))
            {
                try { System.IO.File.Delete(fullPath); } catch { }
            }

            user.ProfilePicturePath = null;
            await _userManager.UpdateAsync(user);
        }

        return NoContent();
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
